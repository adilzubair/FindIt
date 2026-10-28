using System.Diagnostics;
using System.Globalization;
using FindIt.Shared;
using Microsoft.Data.Sqlite;

namespace FindIt.Core;

public sealed partial class ArchiveService
{
    private string ReadSearchRevision()
    {
        if (!File.Exists(storage.OverlayPath)) return "missing";
        PortablePaths.RejectLinks(RootPath, storage.OverlayPath);
        using var overlay = ArchiveDatabase.Open(storage.OverlayPath, true);
        return Convert.ToString(ArchiveDatabase.Scalar(overlay, "SELECT value FROM settings WHERE key='revision';"), CultureInfo.InvariantCulture) ?? "missing";
    }

    private static SearchResponse RestartSearch(long generation, string notice = "This search session expired or changed. Run the search again.") =>
        new() { GenerationId = generation, RestartRequired = true, Notice = notice };

    private static string SessionCursor(Guid id, int offset) => $"s1.{id:N}.{offset.ToString(CultureInfo.InvariantCulture)}";
    private static bool TrySessionCursor(string cursor, out Guid id, out int offset)
    {
        id = default; offset = 0;
        if (cursor.Length > 96) return false;
        var parts = cursor.Split('.');
        return parts.Length == 3 && parts[0] == "s1" && Guid.TryParseExact(parts[1], "N", out id)
            && int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out offset) && offset >= 0;
    }

    private static string BrowseCursor(int offset, string binding) => $"b1.{binding}.{offset.ToString(CultureInfo.InvariantCulture)}";
    private static bool TryBrowseCursor(string cursor, string binding, out int offset)
    {
        offset = 0;
        if (cursor.Length > 96) return false;
        var parts = cursor.Split('.');
        return parts.Length == 3 && parts[0] == "b1" && parts[1] == binding
            && int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out offset) && offset <= int.MaxValue - 1000;
    }

    private static FrozenSearchFile[] FreezeSearchFiles(SqliteConnection db, IEnumerable<RankedSegment> ranks, CancellationToken token)
    {
        var grouped = new Dictionary<Guid, (string Path, RankedSegment Best, List<long> Segments)>();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT f.id,f.path FROM segments s JOIN files f ON f.id=s.file_id WHERE s.id=$id;";
        var parameter = command.Parameters.Add("$id", SqliteType.Integer);
        foreach (var item in ranks.OrderByDescending(r => r.Score).ThenBy(r => r.Id))
        {
            token.ThrowIfCancellationRequested(); parameter.Value = item.Id;
            using var reader = command.ExecuteReader();
            if (!reader.Read()) throw new InvalidDataException("A ranked segment is missing from the archive catalog.");
            var id = Guid.Parse(reader.GetString(0));
            if (!grouped.TryGetValue(id, out var file)) grouped.Add(id, file = (reader.GetString(1), item, []));
            if (file.Segments.Count < 12) file.Segments.Add(item.Id);
        }
        return grouped.OrderByDescending(p => p.Value.Best.Score).ThenBy(p => p.Value.Path, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Key)
            .Select(p => new FrozenSearchFile(p.Key, p.Value.Best.Score, p.Value.Best.Exact, p.Value.Best.Semantic, p.Value.Segments.ToArray())).ToArray();
    }

    private SearchResult[] HydrateSearchFiles(SqliteConnection db, IEnumerable<FrozenSearchFile> files, QueryTerm[] tokens,
        Guid[] personIds, IReadOnlyList<PersonRecord> people, bool ranked, CancellationToken token)
    {
        var results = new List<SearchResult>();
        using var frames = new NearestFrames(db);
        foreach (var file in files)
        {
            token.ThrowIfCancellationRequested();
            using var command = db.CreateCommand();
            command.CommandText = $"SELECT f.id,f.path,f.kind,f.bytes,f.modified,s.id,s.kind,s.text,s.start_ms,s.end_ms,s.page,s.thumbnail FROM segments s JOIN files f ON f.id=s.file_id WHERE f.id=$file AND s.id IN ({string.Join(',', file.Segments.Select((_, i) => "$id" + i))});";
            command.Parameters.AddWithValue("$file", file.Id.ToString("D"));
            for (int i = 0; i < file.Segments.Length; i++) command.Parameters.AddWithValue("$id" + i, file.Segments[i]);
            SearchResult? result = null;
            var moments = new Dictionary<long, SearchMoment>();
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    var moment = ReadMoment(reader, 5); moments.Add(moment.SegmentId, moment);
                    if (result is not null) continue;
                    string path = reader.GetString(1); var kind = (FileKind)reader.GetInt32(2);
                    var category = personIds.Length > 0 && ranked && kind is FileKind.Image or FileKind.Video ? MatchCategory.PossiblePersonAction
                        : file.Exact ? MatchCategory.Exact : file.Semantic ? MatchCategory.Semantic : MatchCategory.Browse;
                    result = new SearchResult { FileId = file.Id, RelativePath = path, Name = Path.GetFileName(path), Kind = kind,
                        Bytes = reader.GetInt64(3), ModifiedUtc = DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture),
                        Score = file.Score, MatchCategory = category, MatchedPersonIds = personIds,
                        MatchingPeople = people.Where(p => personIds.Contains(p.Id)).Select(p => p.Name).ToArray() };
                }
            }
            if (result is null || moments.Count != file.Segments.Length) throw new InvalidDataException("A frozen search result is missing from the archive catalog.");
            var strongest = file.Segments.Select((id, rank) => frames.Fill(result.Kind, file.Id, moments[id] with { Rank = rank })).ToArray();
            var first = strongest[0];
            results.Add(DecoratePersonResult(db, result with { Thumbnail = first.Thumbnail, Snippet = Snippet(first.Text, tokens),
                Moments = strongest.OrderBy(m => m.StartMs ?? 0).ThenBy(m => m.Page ?? 0).ToArray() }, personIds));
        }
        return results.ToArray();
    }

    private SearchResponse SessionPage(SqliteConnection db, SearchSession session, long generation,
        IReadOnlyList<PersonRecord> people, int offset, int limit, Stopwatch timer, CancellationToken token)
    {
        var results = HydrateSearchFiles(db, session.Files.Skip(offset).Take(limit), session.Tokens, session.PersonIds, people, true, token);
        string notice = session.Notice;
        if (results.Any(r => r.MatchCategory == MatchCategory.PossiblePersonAction))
            notice = string.Join(" ", new[] { notice, "A sampled face falls within this scene or passage. Check the face and surrounding footage: action, role and speaker are not verified." }.Where(s => s.Length > 0));
        return new SearchResponse { GenerationId = generation, Results = results, SemanticUsed = session.SemanticUsed,
            PageCursor = SessionCursor(session.Id, offset), NextCursor = offset + limit < session.Files.Length ? SessionCursor(session.Id, offset + limit) : null,
            People = session.People, Notice = notice, ElapsedMilliseconds = timer.Elapsed.TotalMilliseconds,
            IsBoundedWindow = true, CanSearchDeeper = session.CanSearchDeeper, RetrievalBudget = session.RetrievalBudget };
    }
}
