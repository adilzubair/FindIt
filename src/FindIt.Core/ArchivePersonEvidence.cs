using System.Globalization;
using FindIt.Shared;
using Microsoft.Data.Sqlite;

namespace FindIt.Core;

public sealed partial class ArchiveService
{
    private static string AcceptedAssignment(string alias) => $"{alias}.rejected=0 AND ({alias}.confirmed=1 OR {alias}.score>=0.55)";

    // Even older catalogs attached observations to broad visual intervals. A detector
    // observed one image at start_ms; that is the only identity timestamp we can assert.
    private static string SampleWithin(string observation, string moment) =>
        $"({observation}.id={moment}.id OR ({observation}.start_ms IS NOT NULL AND {moment}.start_ms IS NOT NULL AND {observation}.start_ms>={moment}.start_ms AND {observation}.start_ms<COALESCE({moment}.end_ms,{moment}.start_ms+1)))";

    private static string CooccurringPeople(string anchor, int count, int skip = -1)
    {
        var clauses = new List<string>();
        for (int i = 0; i < count; i++)
        {
            if (i == skip) continue;
            clauses.Add($"EXISTS(SELECT 1 FROM usermeta.assignments pa JOIN observations po ON po.id=pa.observation_id JOIN segments ps ON ps.id=po.segment_id WHERE pa.person_id=$person{i} AND {AcceptedAssignment("pa")} AND ps.file_id={anchor}.file_id AND (ps.id={anchor}.id OR (ps.start_ms IS NOT NULL AND ps.start_ms={anchor}.start_ms)))");
        }
        return clauses.Count == 0 ? "1=1" : string.Join(" AND ", clauses);
    }

    private static string PeoplePredicate(IReadOnlyList<Guid> people, List<(string Name, object? Value)> values)
    {
        for (int i = 0; i < people.Count; i++) values.Add(("$person" + i, people[i].ToString("D")));
        // Every selected person must occur in the same sampled image. Independent
        // EXISTS clauses would incorrectly join faces from different shots of a clip.
        return $"s.id IN (SELECT ms.id FROM usermeta.assignments a JOIN observations o ON o.id=a.observation_id JOIN segments os ON os.id=o.segment_id JOIN segments ms ON ms.file_id=os.file_id WHERE a.person_id=$person0 AND {AcceptedAssignment("a")} AND {SampleWithin("os", "ms")} AND {CooccurringPeople("os", people.Count, 0)})";
    }

    private SearchResult DecoratePersonResult(SqliteConnection db, SearchResult result, IReadOnlyList<Guid> people)
    {
        if (people.Count == 0) return result;
        var moments = result.Moments.Select(m => DecoratePersonMoment(db, result.FileId, m, people)).Where(m => m.PersonEvidence.Count > 0).ToArray();
        return result with { Moments = moments, Thumbnail = moments.FirstOrDefault()?.Thumbnail ?? result.Thumbnail };
    }

    private SearchMoment DecoratePersonMoment(SqliteConnection db, Guid fileId, SearchMoment moment, IReadOnlyList<Guid> people)
    {
        var evidence = ReadPersonEvidence(db, fileId, moment, people);
        var first = evidence.FirstOrDefault();
        if (first is null) return moment;
        string names = string.Join(", ", evidence.Select(e => e.Name).Distinct());
        string label = first.StartMs is long time ? $"{names} · {EvidenceTime(time)}" : names;
        return moment with { PersonEvidence = evidence, Thumbnail = first.Thumbnail ?? first.Portrait ?? moment.Thumbnail, Label = label };
    }

    private static string EvidenceTime(long time) => TimeSpan.FromMilliseconds(time).ToString(@"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture);

    private static IReadOnlyList<PersonEvidence> ReadPersonEvidence(SqliteConnection db, Guid fileId, SearchMoment moment, IReadOnlyList<Guid> people, long? selectedObservation = null)
    {
        if (people.Count == 0) return [];
        string personList = string.Join(',', Enumerable.Range(0, people.Count).Select(i => "$person" + i));
        long? sampleTime = null; long? sampleSegment = null;
        if (selectedObservation.HasValue)
        {
            using var selected = db.CreateCommand();
            selected.CommandText = $"SELECT os.id,os.start_ms FROM observations o JOIN segments os ON os.id=o.segment_id JOIN usermeta.assignments a ON a.observation_id=o.id WHERE o.id=$observation AND os.file_id=$file AND a.person_id IN ({personList}) AND {AcceptedAssignment("a")};";
            selected.Parameters.AddWithValue("$observation", selectedObservation.Value); selected.Parameters.AddWithValue("$file", fileId.ToString("D"));
            for (int i = 0; i < people.Count; i++) selected.Parameters.AddWithValue("$person" + i, people[i].ToString("D"));
            using var anchor = selected.ExecuteReader();
            if (!anchor.Read()) return [];
            sampleSegment = anchor.GetInt64(0); sampleTime = anchor.IsDBNull(1) ? null : anchor.GetInt64(1);
        }
        using var command = db.CreateCommand();
        command.CommandText = $"""
            SELECT o.id,os.id,a.person_id,p.name,os.start_ms,os.thumbnail,o.portrait,o.quality,a.confirmed
            FROM observations o JOIN segments os ON os.id=o.segment_id
            JOIN usermeta.assignments a ON a.observation_id=o.id JOIN usermeta.people p ON p.id=a.person_id
            JOIN segments ms ON ms.id=$segment
            WHERE os.file_id=$file AND a.person_id IN ({personList}) AND {AcceptedAssignment("a")}
            AND {SampleWithin("os", "ms")} AND {CooccurringPeople("os", people.Count)}
            {(sampleSegment is null ? "" : "AND (os.id=$sampleSegment OR (os.start_ms IS NOT NULL AND os.start_ms=$sampleTime))")}
            ORDER BY {(selectedObservation.HasValue ? "CASE WHEN o.id=$observation THEN 0 ELSE 1 END," : "")}os.start_ms,o.quality DESC,o.id LIMIT 128;
            """;
        command.Parameters.AddWithValue("$file", fileId.ToString("D")); command.Parameters.AddWithValue("$segment", moment.SegmentId);
        for (int i = 0; i < people.Count; i++) command.Parameters.AddWithValue("$person" + i, people[i].ToString("D"));
        if (sampleSegment.HasValue) { command.Parameters.AddWithValue("$sampleSegment", sampleSegment.Value); command.Parameters.AddWithValue("$sampleTime", (object?)sampleTime ?? DBNull.Value); }
        if (selectedObservation.HasValue) command.Parameters.AddWithValue("$observation", selectedObservation.Value);
        using var reader = command.ExecuteReader();
        var evidence = new List<PersonEvidence>();
        while (reader.Read())
        {
            long? time = reader.IsDBNull(4) ? null : reader.GetInt64(4);
            if (evidence.Count > 0 && evidence[0].StartMs != time) break;
            Guid person = Guid.Parse(reader.GetString(2));
            if (evidence.Any(e => e.PersonId == person)) continue;
            evidence.Add(new PersonEvidence
            {
                ObservationId = reader.GetInt64(0), SegmentId = reader.GetInt64(1), PersonId = person, Name = reader.GetString(3), StartMs = time,
                Thumbnail = reader.IsDBNull(5) ? null : Blob(reader.GetString(5)), Portrait = reader.IsDBNull(6) ? null : Blob(reader.GetString(6)),
                Quality = reader.GetDouble(7), Confirmed = reader.GetBoolean(8)
            });
        }
        return people.All(id => evidence.Any(e => e.PersonId == id)) ? evidence : [];
    }
}
