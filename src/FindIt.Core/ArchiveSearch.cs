using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FindIt.Runtime;
using FindIt.Shared;
using Microsoft.Data.Sqlite;

namespace FindIt.Core;

public sealed partial class ArchiveService
{
    private readonly SemaphoreSlim searchGate = new(1, 1);
    private NativeVectorIndex? nativeIndex;
    private NativeVectorIndex? nativeDeltaIndex;
    private HashSet<ulong>? liveVectorIds;
    private ExactVectorIndex? exactIndex;
    private long loadedVectorGeneration;
    private bool nativeIndexInitialized;
    private string indexNotice = "";

    public async Task<SearchResponse> SearchAsync(SearchRequest request, CancellationToken cancellationToken = default)
    {
        await searchGate.WaitAsync(cancellationToken);
        try
        {
            var watch = Stopwatch.StartNew();
            var generation = await storage.RecoverAsync(cancellationToken);
            if (generation is null) return new SearchResponse { Notice = storage.RecoveryNotice ?? "Prepare this drive to start searching." };
            if (request.GenerationId is long expected && expected != generation.GenerationId)
                return new SearchResponse { GenerationId = generation.GenerationId, Notice = "The archive generation changed. Run the search again." };
            using var db = ArchiveDatabase.Open(storage.CatalogPath(generation), true);
            bool overlayExists = File.Exists(storage.OverlayPath);
            if (overlayExists) AttachOverlay(db);
            var people = await ReadPeopleAsync(false, cancellationToken);
            string query = request.Query.Trim();
            var detected = new List<PersonRecord>();
            var ambiguous = new List<string>();
            var names = people.SelectMany(person => person.Aliases.Prepend(person.Name).Where(a => !string.IsNullOrWhiteSpace(a)).Select(name => (Name: name, Person: person))).GroupBy(n => n.Name, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Key.Length);
            foreach (var group in names)
            {
                string pattern = @"(?<![\p{L}\p{N}])" + Regex.Escape(group.Key) + @"(?![\p{L}\p{N}])";
                if (!Regex.IsMatch(query, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) continue;
                var owners = group.Select(n => n.Person).DistinctBy(p => p.Id).ToArray();
                var selected = owners.Where(p => request.Filters.PersonIds.Contains(p.Id)).ToArray();
                if (owners.Length > 1 && selected.Length != 1) { ambiguous.Add(group.Key); continue; }
                var person = selected.Length == 1 ? selected[0] : owners[0];
                if (!detected.Any(p => p.Id == person.Id)) detected.Add(person);
                query = Regex.Replace(query, pattern, " ", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            }
            query = Regex.Replace(query, @"\s+", " ").Trim();
            var personIds = request.Filters.PersonIds.Concat(detected.Select(p => p.Id)).Distinct().ToArray();
            if (personIds.Length > 32) throw new ArgumentException("Choose at most 32 people in one search.");
            var predicates = BuildPredicates(request.Filters, personIds, overlayExists);
            int limit = Math.Clamp(request.Limit, 1, 1000);
            int offset = ParseCursor(request.Cursor, generation.GenerationId, query, request.Filters);
            int candidates = (int)Math.Min(16384, Math.Max(512, ((long)offset + limit) * 4));
            var ranks = new Dictionary<long, RankedSegment>();
            var lexical = LexicalTerms(query);
            var tokens = lexical.Select(t => t.Text).ToArray();
            string match = string.Join(request.Filters.ExactOnly ? " AND " : " OR ", lexical.Select(t => "\"" + t.Text.Replace("\"", "\"\"") + "\"" + (t.Phrase ? "" : "*")));
            if (!string.IsNullOrWhiteSpace(match))
            {
                using var command = db.CreateCommand();
                command.CommandText = $"SELECT s.id,bm25(search_fts,0,0,5,2,1) FROM search_fts JOIN segments s ON s.id=CAST(search_fts.segment_id AS INTEGER) JOIN files f ON f.id=s.file_id WHERE search_fts MATCH $match AND {predicates.Sql} ORDER BY 2 LIMIT $limit;";
                command.Parameters.AddWithValue("$match", match); command.Parameters.AddWithValue("$limit", candidates); AddParameters(command, predicates.Parameters);
                using var reader = command.ExecuteReader(); int rank = 0;
                while (reader.Read()) { var id = reader.GetInt64(0); ranks[id] = new RankedSegment(id) { Score = 1.0 / (60 + ++rank), Exact = true }; }
            }
            if (query.Length > 0)
            {
                // Substring filename/path lookup also works for partial filenames and punctuation.
                using var filename = db.CreateCommand();
                filename.CommandText = $"SELECT s.id FROM files f JOIN segments s ON s.file_id=f.id WHERE (f.path LIKE $path ESCAPE '\\') AND {predicates.Sql} GROUP BY f.id ORDER BY f.path LIMIT $limit;";
                filename.Parameters.AddWithValue("$path", "%" + EscapeLike(query) + "%"); filename.Parameters.AddWithValue("$limit", candidates); AddParameters(filename, predicates.Parameters);
                using var files = filename.ExecuteReader(); int rank = 0;
                while (files.Read()) { long id = files.GetInt64(0); if (!ranks.TryGetValue(id, out var item)) ranks[id] = item = new RankedSegment(id); item.Score += 2.0 / (60 + ++rank); item.Exact = true; }
            }
            bool semanticUsed = false; string notice = "";
            try
            {
            if (!request.Filters.ExactOnly && !string.IsNullOrWhiteSpace(query))
            {
                if (generation.VectorCount > 0 && embeddings.Available && generation.EmbeddingFingerprint == embeddings.Fingerprint)
                {
                    var encodedQuery = await embeddings.EmbedTextAsync(query, true, cancellationToken);
                    if (encodedQuery is not null)
                    {
                        var prepared = VectorEncoding.Encode(encodedQuery, Dimension) ?? throw new InvalidDataException("Incompatible query embedding.");
                        var vector = VectorEncoding.Decode(prepared);
                        HashSet<ulong>? allowed = null;
                        if (predicates.Parameters.Count > 0 || predicates.Sql != "1=1")
                        {
                            allowed = [];
                            // Filename/text-only segments are not semantic candidates and must
                            // not inflate the cutoff for the small-set exact retrieval path.
                            using var filter = db.CreateCommand(); filter.CommandText = $"SELECT s.id FROM segments s JOIN files f ON f.id=s.file_id WHERE {predicates.Sql} AND s.vector_path IS NOT NULL AND s.vector_offset>=0 AND s.vector_length=$vectorLength;"; AddParameters(filter, predicates.Parameters);
                            filter.Parameters.AddWithValue("$vectorLength", Dimension * 2);
                            using var selected = filter.ExecuteReader(); while (selected.Read()) { cancellationToken.ThrowIfCancellationRequested(); allowed.Add((ulong)selected.GetInt64(0)); }
                        }
                        IReadOnlyList<(long Id, double Score)> neighbors;
                        if (allowed is { Count: <= 4096 })
                        {
                            // A small filtered scan needs only canonical vectors. In particular,
                            // do not preload the entire HDD graph before choosing this path.
                            EnsureVectorGeneration(generation.GenerationId);
                            exactIndex ??= new ExactVectorIndex(RootPath, storage.VectorPath(generation), generation.VectorPacks, Dimension);
                            neighbors = exactIndex.Search(vector, candidates, id => allowed.Contains((ulong)id), cancellationToken);
                        }
                        else
                        {
                            EnsureVectorIndex(generation);
                            if (nativeIndex is not null)
                            {
                                var accepted = allowed ?? liveVectorIds;
                                neighbors = nativeIndex.Search(vector, candidates, accepted)
                                    .Concat(nativeDeltaIndex?.Search(vector, candidates, accepted) ?? [])
                                    .GroupBy(n => n.Key).Select(g => g.MinBy(n => n.Distance)!)
                                    .OrderBy(n => n.Distance).Take(candidates)
                                    .Select(n => ((long)n.Key, 1.0 - n.Distance)).ToArray();
                            }
                            else
                            {
                                neighbors = exactIndex!.Search(vector, candidates, allowed is null ? null : id => allowed.Contains((ulong)id), cancellationToken);
                                notice = indexNotice.Length > 0 ? indexNotice : "Semantic search uses an exact scan because the optimized ANN runtime is unavailable; large libraries may be slower.";
                            }
                        }
                        int rank = 0;
                        foreach (var neighbor in neighbors)
                        {
                            if (!ranks.TryGetValue(neighbor.Id, out var item)) ranks[neighbor.Id] = item = new RankedSegment(neighbor.Id);
                            item.Score += 1.0 / (60 + ++rank); item.Semantic = true;
                        }
                        semanticUsed = true;
                    }
                    else notice = "The local encoder could not embed this query. Keyword results are shown.";
                }
                else notice = generation.VectorCount == 0 ? "Keyword results are shown. This archive has no prepared semantic vectors yet." : "Keyword results are shown. The matching local query encoder is unavailable.";
            }
            }
            catch (Exception error) when (error is not OperationCanceledException && error is not OutOfMemoryException)
            {
                semanticUsed = false;
                notice = "The semantic runtime could not complete this search. Keyword results remain available. " + (error.Message.Length > 240 ? error.Message[..240] : error.Message);
            }
            if (query.Length == 0)
            {
                using var browse = db.CreateCommand();
                browse.CommandText = $"SELECT (SELECT s.id FROM segments s WHERE s.file_id=f.id AND {predicates.Sql} LIMIT 1) FROM files f WHERE EXISTS(SELECT 1 FROM segments s WHERE s.file_id=f.id AND {predicates.Sql}) ORDER BY f.modified DESC,f.path LIMIT $limit OFFSET $offset;";
                browse.Parameters.AddWithValue("$limit", limit + 1); browse.Parameters.AddWithValue("$offset", offset); AddParameters(browse, predicates.Parameters);
                using var reader = browse.ExecuteReader(); int rank = 0;
                while (reader.Read()) { var id = reader.GetInt64(0); ranks[id] = new RankedSegment(id) { Score = 1.0 / (60 + ++rank) }; }
            }
            var grouped = new Dictionary<Guid, MutableResult>();
            foreach (var item in ranks.Values.OrderByDescending(i => i.Score))
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var command = db.CreateCommand();
                command.CommandText = "SELECT f.id,f.path,f.kind,f.bytes,f.modified,s.id,s.kind,s.text,s.start_ms,s.end_ms,s.page,s.thumbnail FROM segments s JOIN files f ON f.id=s.file_id WHERE s.id=$id;"; command.Parameters.AddWithValue("$id", item.Id);
                using var reader = command.ExecuteReader(); if (!reader.Read()) continue;
                var fileId = Guid.Parse(reader.GetString(0));
                var moment = ReadMoment(reader, 5);
                if (!grouped.TryGetValue(fileId, out var result))
                {
                    string path = reader.GetString(1);
                    var kind = (FileKind)reader.GetInt32(2);
                    var category = personIds.Length > 0 && query.Length > 0 && (kind is FileKind.Image or FileKind.Video) ? MatchCategory.PossiblePersonAction : item.Exact ? MatchCategory.Exact : item.Semantic ? MatchCategory.Semantic : MatchCategory.Browse;
                    grouped[fileId] = result = new MutableResult(new SearchResult { FileId = fileId, RelativePath = path, Name = Path.GetFileName(path), Kind = kind, Bytes = reader.GetInt64(3), ModifiedUtc = DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture), Thumbnail = moment.Thumbnail, Snippet = Snippet(moment.Text, tokens), MatchCategory = category, Score = item.Score, MatchingPeople = people.Where(p => personIds.Contains(p.Id)).Select(p => p.Name).ToArray(), MatchedPersonIds = personIds });
                }
                if (result.Moments.Count < 12) result.Moments.Add(moment);
            }
            var ordered = grouped.Values.OrderByDescending(p => p.Result.Score).ThenBy(p => p.Result.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray();
            int localOffset = query.Length == 0 ? 0 : offset;
            var results = ordered.Skip(localOffset).Take(limit).Select(p => DecoratePersonResult(db, p.Result with { Moments = p.Moments.OrderBy(m => m.StartMs ?? 0).ThenBy(m => m.Page ?? 0).ToArray() }, personIds)).Where(r => r.Moments.Count > 0).ToArray();
            if (ambiguous.Count > 0) notice = $"Multiple people use the name {string.Join(", ", ambiguous.Distinct(StringComparer.OrdinalIgnoreCase))}. Select a person filter to resolve the name. " + notice;
            if (results.Any(r => r.MatchCategory == MatchCategory.PossiblePersonAction)) notice = string.Join(" ", new[] { notice, "A sampled face falls within this scene or passage. Check the face and surrounding footage: action, role and speaker are not verified." }.Where(s => s.Length > 0));
            return new SearchResponse { GenerationId = generation.GenerationId, Results = results, SemanticUsed = semanticUsed, NextCursor = ordered.Length > localOffset + limit ? MakeCursor(offset + limit, generation.GenerationId, query, request.Filters) : null, TotalCount = null, Notice = notice, ElapsedMilliseconds = watch.Elapsed.TotalMilliseconds, People = detected.Select(p => new PersonChip(p.Id, p.Name)).ToArray() };
        }
        finally { searchGate.Release(); }
    }

    private void EnsureVectorGeneration(long generationId)
    {
        if (loadedVectorGeneration == generationId) return;
        nativeIndex?.Dispose(); nativeIndex = null; nativeDeltaIndex?.Dispose(); nativeDeltaIndex = null; exactIndex?.Dispose(); exactIndex = null; liveVectorIds = null; indexNotice = "";
        nativeIndexInitialized = false;
        loadedVectorGeneration = generationId;
    }

    private void EnsureVectorIndex(GenerationManifest generation)
    {
        EnsureVectorGeneration(generation.GenerationId);
        // An earlier small filtered query may have opened the exact index only.
        // A later broader query must still be allowed to initialize native retrieval.
        if (nativeIndexInitialized) return;
        string path = generation.SemanticBasePath is { } basePath ? PortablePaths.Resolve(RootPath, basePath, true) : Path.Combine(storage.GenerationDirectory(generation.GenerationId), "semantic.usearch");
        if (NativeVectorIndex.Available && File.Exists(path))
        {
            // Query the drive's seek-penalty flag; low-memory systems retain a bounded mapped view.
            var deltaPath = generation.SemanticDeltaPath is { } delta ? PortablePaths.Resolve(RootPath, delta, true) : null;
            long baseBytes = new FileInfo(path).Length;
            long deltaBytes = deltaPath is null ? 0 : new FileInfo(deltaPath).Length;
            long estimatedPeak = checked(NativeVectorIndex.EstimateLoadPeakBytes(path, baseBytes)
                + (deltaPath is null ? 0 : NativeVectorIndex.EstimateLoadPeakBytes(deltaPath, deltaBytes)));
            bool load = StorageProfile.ShouldPreload(RootPath, checked(baseBytes + deltaBytes), estimatedPeak);
            try
            {
                nativeIndex = NativeVectorIndex.Open(path, !load);
                if (deltaPath is not null) nativeDeltaIndex = NativeVectorIndex.Open(deltaPath, !load);
                liveVectorIds = [];
                using var records = new FileStream(storage.VectorPath(generation), FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);
                var row = new byte[20];
                while (records.Position < records.Length) { records.ReadExactly(row); liveVectorIds.Add((ulong)System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(row)); }
            }
            catch (Exception error) when (error is not OperationCanceledException && error is not OutOfMemoryException)
            {
                nativeIndex?.Dispose(); nativeIndex = null; nativeDeltaIndex?.Dispose(); nativeDeltaIndex = null; liveVectorIds = null;
                indexNotice = "The optimized index could not be opened. Semantic search is using the canonical exact index and may be slower.";
                exactIndex ??= new ExactVectorIndex(RootPath, storage.VectorPath(generation), generation.VectorPacks, Dimension);
            }
        }
        else exactIndex ??= new ExactVectorIndex(RootPath, storage.VectorPath(generation), generation.VectorPacks, Dimension);
        nativeIndexInitialized = true;
    }

    private (string Sql, List<(string Name, object? Value)> Parameters) BuildPredicates(SearchFilters filters, IReadOnlyList<Guid> people, bool overlay)
    {
        var clauses = new List<string>(); var values = new List<(string, object?)>();
        if (filters.Kind is FileKind kind) { clauses.Add("f.kind=$kind"); values.Add(("$kind", (int)kind)); }
        if (!string.IsNullOrWhiteSpace(filters.Folder) && filters.Folder != ".")
        {
            var relative = PortablePaths.Relative(RootPath, PortablePaths.Resolve(RootPath, filters.Folder, false, false));
            clauses.Add("(f.path=$folder COLLATE NOCASE OR f.path LIKE $folderPrefix ESCAPE '\\')"); values.Add(("$folder", relative)); values.Add(("$folderPrefix", EscapeLike(relative) + "/%"));
        }
        if (filters.AfterUtc is DateTimeOffset after) { clauses.Add("f.modified >= $after"); values.Add(("$after", after.UtcDateTime.ToString("O", CultureInfo.InvariantCulture))); }
        if (filters.BeforeUtc is DateTimeOffset before) { clauses.Add("f.modified <= $before"); values.Add(("$before", before.UtcDateTime.ToString("O", CultureInfo.InvariantCulture))); }
        if (people.Count > 0) clauses.Add(overlay ? PeoplePredicate(people, values) : "0=1");
        return (clauses.Count == 0 ? "1=1" : string.Join(" AND ", clauses), values);
    }
    private void AttachOverlay(SqliteConnection db)
    {
        PortablePaths.RejectLinks(RootPath, storage.OverlayPath);
        using var attach = db.CreateCommand(); attach.CommandText = "ATTACH DATABASE $path AS usermeta;"; attach.Parameters.AddWithValue("$path", storage.OverlayPath); attach.ExecuteNonQuery();
    }
    private static void AddParameters(SqliteCommand command, IEnumerable<(string Name, object? Value)> parameters) { foreach (var p in parameters) command.Parameters.AddWithValue(p.Name, p.Value ?? DBNull.Value); }
    private static SearchMoment ReadMoment(SqliteDataReader reader, int start) => new()
    {
        SegmentId = reader.GetInt64(start), Kind = (SegmentKind)reader.GetInt32(start + 1), Text = reader.GetString(start + 2),
        StartMs = reader.IsDBNull(start + 3) ? null : reader.GetInt64(start + 3), EndMs = reader.IsDBNull(start + 4) ? null : reader.GetInt64(start + 4), Page = reader.IsDBNull(start + 5) ? null : reader.GetInt32(start + 5),
        Thumbnail = reader.IsDBNull(start + 6) ? null : Blob(reader.GetString(start + 6)),
        Label = !reader.IsDBNull(start + 5) ? "Page " + reader.GetInt32(start + 5) : !reader.IsDBNull(start + 3) ? TimeSpan.FromMilliseconds(reader.GetInt64(start + 3)).ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture) : ((SegmentKind)reader.GetInt32(start + 1)).ToString()
    };
    private static string Snippet(string text, string[] tokens)
    {
        if (text.Length <= 260) return text;
        int first = tokens.Select(t => text.IndexOf(t, StringComparison.OrdinalIgnoreCase)).Where(i => i >= 0).DefaultIfEmpty(0).Min(); int start = Math.Max(0, first - 50);
        return (start > 0 ? "…" : "") + text.Substring(start, Math.Min(260, text.Length - start)) + "…";
    }
    private static readonly HashSet<string> EnglishStopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a","an","and","are","as","at","be","been","being","but","by","can","could","did","do","does","for","from","had","has","have","how","i","if","in","into","is","it","its","me","my","of","on","or","our","over","s","she","some","someone","something","than","that","the","their","them","then","there","these","they","this","those","to","under","up","us","was","we","were","what","when","where","which","who","why","will","with","would","you","your"
    };
    private static (string Text, bool Phrase)[] LexicalTerms(string query)
    {
        var result = new List<(string Text, bool Phrase)>();
        foreach (Match token in Regex.Matches(query, "\"(?<phrase>[^\"]+)\"|(?<token>[\\p{L}\\p{N}_]+)", RegexOptions.CultureInvariant))
        {
            bool phrase = token.Groups["phrase"].Success;
            string value = phrase ? token.Groups["phrase"].Value.Trim() : token.Groups["token"].Value;
            if (value.Length == 0 || !phrase && (EnglishStopWords.Contains(value) || value.Length == 1 && char.IsLetter(value[0]))) continue;
            result.Add((value, phrase)); if (result.Count == 48) break;
        }
        return result.Distinct().ToArray();
    }
    private static string MakeCursor(int offset, long generation, string query, SearchFilters filters) => Convert.ToBase64String(Encoding.UTF8.GetBytes($"{offset}|{generation}|{ArchiveStorage.HashText(query + JsonSerializer.Serialize(filters, WireJson.Options))}"));
    private static int ParseCursor(string? cursor, long generation, string query, SearchFilters filters)
    {
        if (cursor is null) return 0;
        try { var fields = Encoding.UTF8.GetString(Convert.FromBase64String(cursor)).Split('|'); if (fields.Length == 3 && long.Parse(fields[1], CultureInfo.InvariantCulture) == generation && fields[2] == ArchiveStorage.HashText(query + JsonSerializer.Serialize(filters, WireJson.Options))) return Math.Clamp(int.Parse(fields[0], CultureInfo.InvariantCulture), 0, query.Length == 0 ? int.MaxValue - 1000 : 16000); }
        catch (Exception error) when (error is FormatException or OverflowException) { }
        return 0;
    }
    private sealed class RankedSegment(long id) { public long Id { get; } = id; public double Score; public bool Exact; public bool Semantic; }
    private sealed class MutableResult(SearchResult result) { public SearchResult Result { get; } = result; public List<SearchMoment> Moments { get; } = []; }

    public async Task<PreviewResult> GetPreviewAsync(PreviewRequest request, CancellationToken cancellationToken = default)
    {
        var generation = await storage.RecoverAsync(cancellationToken) ?? throw new InvalidOperationException("Prepare an archive first.");
        using var db = ArchiveDatabase.Open(storage.CatalogPath(generation), true);
        using var command = db.CreateCommand(); command.CommandText = "SELECT path,kind,bytes,modified FROM files WHERE id=$id;"; command.Parameters.AddWithValue("$id", request.FileId.ToString("D"));
        string path, modified; FileKind kind; long bytes;
        using (var reader = command.ExecuteReader()) { if (!reader.Read()) throw new FileNotFoundException("This item is not in the current archive generation."); path = reader.GetString(0); kind = (FileKind)reader.GetInt32(1); bytes = reader.GetInt64(2); modified = reader.GetString(3); }
        string original = PortablePaths.Resolve(RootPath, path);
        var originalInfo = new FileInfo(original);
        if (originalInfo.Length != bytes || originalInfo.LastWriteTimeUtc.ToString("O", CultureInfo.InvariantCulture) != modified)
            throw new IOException("The original changed since preparation. Explicitly prepare this folder again to refresh its search results and preview.");
        var personIds = request.PersonIds?.Distinct().ToArray() ?? [];
        if (personIds.Length > 32) throw new ArgumentException("Choose at most 32 people in one preview.");
        var moments = new List<SearchMoment>();
        using (var segments = db.CreateCommand())
        {
            // The search response already supplies its matching rail. Inspecting one face
            // must not issue hundreds of evidence lookups over an entire long recording.
            segments.CommandText = "SELECT id,kind,text,start_ms,end_ms,page,thumbnail FROM segments WHERE file_id=$id " + (personIds.Length > 0 && request.SegmentId.HasValue ? "AND id=$selected " : "") + "ORDER BY CASE WHEN id=$selected THEN 0 ELSE 1 END,start_ms,page,id LIMIT 500;";
            segments.Parameters.AddWithValue("$id", request.FileId.ToString("D")); segments.Parameters.AddWithValue("$selected", request.SegmentId ?? -1);
            using var reader = segments.ExecuteReader(); while (reader.Read()) moments.Add(ReadMoment(reader, 0));
        }
        if (personIds.Length > 0)
        {
            if (!File.Exists(storage.OverlayPath)) throw new FileNotFoundException("The selected person evidence is unavailable.");
            AttachOverlay(db);
            moments = moments.Select(m => DecoratePersonMoment(db, request.FileId, m, personIds)).Where(m => m.PersonEvidence.Count > 0).ToList();
        }
        var selectedMoment = request.SegmentId is long id ? moments.FirstOrDefault(m => m.SegmentId == id) : moments.FirstOrDefault();
        if (request.SegmentId.HasValue && selectedMoment is null) throw new FileNotFoundException("That moment is unavailable in this generation.");
        var evidence = selectedMoment?.PersonEvidence ?? [];
        if (request.EvidenceObservationId is long observation)
        {
            if (personIds.Length == 0 || selectedMoment is null) throw new ArgumentException("Select people to inspect face evidence.");
            evidence = ReadPersonEvidence(db, request.FileId, selectedMoment, personIds, observation);
            if (!evidence.Any(e => e.ObservationId == observation)) throw new FileNotFoundException("The selected face evidence is no longer accepted for this moment.");
        }
        return new PreviewResult { FileId = request.FileId, OriginalPath = original, Kind = kind, Text = selectedMoment?.Text ?? "", StartMs = evidence.FirstOrDefault()?.StartMs ?? selectedMoment?.StartMs, Page = selectedMoment?.Page, PersonEvidence = evidence, Moments = moments };
    }
}
