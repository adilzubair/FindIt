using System.Globalization;
using System.Text.Json;
using FindIt.Shared;
using FindIt.Runtime;
using Microsoft.Data.Sqlite;

namespace FindIt.Core;

public sealed partial class ArchiveService
{
    public async Task<IReadOnlyList<PersonRecord>> GetPeopleAsync(CancellationToken cancellationToken = default)
        => await ReadPeopleAsync(true, cancellationToken);

    private async Task<IReadOnlyList<PersonRecord>> ReadPeopleAsync(bool includeCounts, CancellationToken cancellationToken)
    {
        if (!File.Exists(storage.OverlayPath)) return [];
        PortablePaths.RejectLinks(RootPath, storage.OverlayPath);
        var generation = includeCounts ? await storage.RecoverAsync(cancellationToken) : null;
        using var db = ArchiveDatabase.Open(storage.OverlayPath, true);
        if (generation is not null)
            ArchiveDatabase.Execute(db, "ATTACH DATABASE $path AS archive;", ("$path", storage.CatalogPath(generation)));
        using var command = db.CreateCommand();
        command.CommandText = generation is null ? "SELECT p.id,p.name,p.aliases,p.portrait,p.confirmed,0 FROM people p WHERE p.confirmed=1 ORDER BY p.name COLLATE NOCASE;" : "SELECT p.id,p.name,p.aliases,p.portrait,p.confirmed,(SELECT COUNT(*) FROM assignments a JOIN archive.observations o ON o.id=a.observation_id WHERE a.person_id=p.id AND a.rejected=0 AND (a.confirmed=1 OR a.score>=0.55)) AS observations FROM people p WHERE p.confirmed=1 OR observations>0 ORDER BY p.confirmed DESC,p.name COLLATE NOCASE LIMIT 1000;";
        var result = new List<PersonRecord>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            result.Add(new PersonRecord { Id = Guid.Parse(reader.GetString(0)), Name = reader.GetString(1), Aliases = JsonSerializer.Deserialize<string[]>(reader.GetString(2), WireJson.Options) ?? [], Portrait = reader.IsDBNull(3) ? null : Blob(reader.GetString(3)), Confirmed = reader.GetBoolean(4), ObservationCount = reader.GetInt64(5) });
        }
        return result;
    }

    public async Task<IReadOnlyList<PersonObservation>> GetPersonObservationsAsync(Guid personId, CancellationToken cancellationToken = default)
    {
        var generation = await storage.RecoverAsync(cancellationToken);
        if (generation is null || !File.Exists(storage.OverlayPath)) return [];
        using var db = ArchiveDatabase.Open(storage.CatalogPath(generation), true); AttachOverlay(db);
        using var command = db.CreateCommand();
        command.CommandText = "SELECT o.id,f.id,f.path,COALESCE(o.portrait,s.thumbnail),s.start_ms,o.quality,a.confirmed,a.rejected FROM observations o JOIN segments s ON s.id=o.segment_id JOIN files f ON f.id=s.file_id JOIN usermeta.assignments a ON a.observation_id=o.id WHERE a.person_id=$person ORDER BY a.confirmed DESC,a.rejected,o.quality DESC LIMIT 2000;";
        command.Parameters.AddWithValue("$person", personId.ToString("D"));
        using var reader = command.ExecuteReader(); var results = new List<PersonObservation>();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(new PersonObservation { Id = reader.GetInt64(0), FileId = Guid.Parse(reader.GetString(1)), RelativePath = reader.GetString(2), Thumbnail = reader.IsDBNull(3) ? null : Blob(reader.GetString(3)), StartMs = reader.IsDBNull(4) ? null : reader.GetInt64(4), Quality = reader.GetDouble(5), Confirmed = reader.GetBoolean(6), Rejected = reader.GetBoolean(7) });
        }
        return results;
    }

    public async Task<PersonRecord> SavePersonAsync(PersonUpdateRequest request, CancellationToken cancellationToken = default)
    {
        if (request.Name.Trim().Length is 0 or > 160) throw new ArgumentException("Enter a name between 1 and 160 characters.");
        if (request.ReferencePaths.Count > 32) throw new ArgumentException("Choose at most 32 reference photos at a time.");
        await overlayGate.WaitAsync(cancellationToken);
        Guid id = request.PersonId ?? Guid.NewGuid();
        try
        {
            storage.EnsureWritable();
            var generation = await storage.RecoverAsync(cancellationToken);
            if (request.ReferencePaths.Count > 0 && generation is not null && generation.FaceFingerprint != FaceFingerprint)
                throw new InvalidOperationException("Prepare this archive with the current face encoder before adding new reference photos. Existing named-person searches remain available.");
            using var overlay = ArchiveDatabase.Open(storage.OverlayPath);
            ArchiveDatabase.InitializeOverlay(overlay);
            string? portrait = ArchiveDatabase.Scalar(overlay, "SELECT portrait FROM people WHERE id=$id;", ("$id", id.ToString("D")))?.ToString();
            var references = new List<(byte[] Embedding, string? Portrait)>();
            if (request.ReferencePaths.Count > 0)
            {
                using var pack = new PackWriter(RootPath, $"FindIt/archive/packs/references-{Guid.NewGuid():N}.pack");
                foreach (var referencePath in request.ReferencePaths)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string full = Path.GetFullPath(referencePath);
                    if (!File.Exists(full) || (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0) throw new IOException("Select a regular reference photo.");
                    var faces = await processor.DetectReferenceFacesAsync(full, cancellationToken);
                    if (faces.Count != 1) throw new InvalidOperationException(faces.Count == 0 ? "No usable face was detected in a reference photo. Check the local face model package and select a clear portrait." : "A reference photo contains multiple faces. Select a photo containing one person.");
                    var face = faces[0];
                    if (face.Quality < 0.25) throw new InvalidOperationException("A reference face is too unclear. Choose a sharper portrait.");
                    var encoded = VectorEncoding.Encode(face.Embedding, face.Embedding.Length) ?? throw new InvalidDataException("The face encoder returned no embedding.");
                    string? facePortrait = face.PortraitJpeg is { Length: > 0 } ? Json(pack.Append(face.PortraitJpeg)) : null;
                    references.Add((encoded, facePortrait)); portrait ??= facePortrait;
                }
                pack.Flush();
            }
            ArchiveDatabase.Execute(overlay, "BEGIN IMMEDIATE;");
            try
            {
                ArchiveDatabase.Execute(overlay, "INSERT INTO people(id,name,aliases,portrait,confirmed) VALUES($id,$name,$aliases,$portrait,1) ON CONFLICT(id) DO UPDATE SET name=excluded.name,aliases=excluded.aliases,portrait=COALESCE(excluded.portrait,people.portrait),confirmed=1;", ("$id", id.ToString("D")), ("$name", request.Name.Trim()), ("$aliases", JsonSerializer.Serialize(request.Aliases.Select(a => a.Trim()).Where(a => a.Length is > 0 and <= 160).Distinct(StringComparer.OrdinalIgnoreCase).Take(32).ToArray(), WireJson.Options)), ("$portrait", portrait));
                foreach (var reference in references)
                    ArchiveDatabase.Execute(overlay, "INSERT INTO person_references(person_id,embedding,fingerprint) VALUES($id,$embedding,$fingerprint);", ("$id", id.ToString("D")), ("$embedding", reference.Embedding), ("$fingerprint", FaceFingerprint));
                if (request.ConfirmObservationIds.Count > 0 || request.RejectObservationIds.Count > 0)
                {
                    if (generation is null) throw new InvalidOperationException("Prepare the archive before correcting observations.");
                    using var catalog = ArchiveDatabase.Open(storage.CatalogPath(generation), true);
                    foreach (long observation in request.ConfirmObservationIds.Concat(request.RejectObservationIds).Distinct())
                    {
                        if (ArchiveDatabase.Scalar(catalog, "SELECT 1 FROM observations WHERE id=$id;", ("$id", observation)) is null) throw new InvalidOperationException("A selected face observation is no longer available.");
                        bool rejected = request.RejectObservationIds.Contains(observation);
                        if (rejected) ArchiveDatabase.Execute(overlay, "INSERT OR IGNORE INTO rejections(observation_id,person_id) VALUES($observation,$person);", ("$observation", observation), ("$person", id.ToString("D")));
                        else ArchiveDatabase.Execute(overlay, "DELETE FROM rejections WHERE observation_id=$observation AND person_id=$person;", ("$observation", observation), ("$person", id.ToString("D")));
                        ArchiveDatabase.Execute(overlay, "INSERT INTO assignments(observation_id,person_id,confirmed,rejected,score) VALUES($observation,$person,1,$rejected,1) ON CONFLICT(observation_id) DO UPDATE SET person_id=excluded.person_id,confirmed=1,rejected=excluded.rejected,score=1;", ("$observation", observation), ("$person", id.ToString("D")), ("$rejected", rejected ? 1 : 0));
                    }
                }
                if (request.MergeIntoPersonId is Guid target && target != id)
                {
                    if (ArchiveDatabase.Scalar(overlay, "SELECT 1 FROM people WHERE id=$id;", ("$id", target.ToString("D"))) is null) throw new ArgumentException("The merge target is unavailable.");
                    ArchiveDatabase.Execute(overlay, "UPDATE assignments SET person_id=$target WHERE person_id=$source; UPDATE person_references SET person_id=$target WHERE person_id=$source; INSERT OR IGNORE INTO rejections(observation_id,person_id) SELECT observation_id,$target FROM rejections WHERE person_id=$source; DELETE FROM people WHERE id=$source;", ("$target", target.ToString("D")), ("$source", id.ToString("D")));
                    id = target;
                }
                ArchiveDatabase.Execute(overlay, "UPDATE settings SET value=CAST(CAST(value AS INTEGER)+1 AS TEXT) WHERE key='revision'; COMMIT;");
            }
            catch { ArchiveDatabase.Execute(overlay, "ROLLBACK;"); throw; }
            if (references.Count > 0 && generation is not null)
            {
                using var catalog = ArchiveDatabase.Open(storage.CatalogPath(generation), true);
                await AssignReferencesAsync(catalog, cancellationToken, alreadyLocked: true, reclassify: true);
            }
        }
        finally { overlayGate.Release(); }
        return (await GetPeopleAsync(cancellationToken)).First(p => p.Id == id);
    }

    private string FaceFingerprint => processor.FaceFingerprint;

    private async Task AssignReferencesAsync(SqliteConnection catalog, CancellationToken cancellationToken, bool alreadyLocked = false, bool reclassify = false)
    {
        if (!alreadyLocked) await overlayGate.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(storage.OverlayPath)) return;
            using var overlay = ArchiveDatabase.Open(storage.OverlayPath);
            ArchiveDatabase.InitializeOverlay(overlay);
            var references = new List<(string Person, float[] Vector, bool Confirmed)>();
            using (var command = overlay.CreateCommand())
            {
                command.CommandText = "SELECT r.person_id,r.embedding,p.confirmed FROM person_references r JOIN people p ON p.id=r.person_id WHERE r.fingerprint=$fingerprint;"; command.Parameters.AddWithValue("$fingerprint", FaceFingerprint);
                using var reader = command.ExecuteReader(); while (reader.Read()) references.Add((reader.GetString(0), VectorEncoding.Decode((byte[])reader[1]), reader.GetBoolean(2)));
            }
            // A discovered cluster is an organizational hypothesis, not a competing named
            // identity. Keep its prototypes out of the named-reference ambiguity gate.
            var namedReferences = references.Where(r => r.Confirmed).ToArray();
            NativeVectorIndex? faceIndex = null;
            if (NativeVectorIndex.Available && references.Any(r => !r.Confirmed))
            {
                faceIndex = new NativeVectorIndex(references[0].Vector.Length);
                for (int i = 0; i < references.Count; i++) if (!references[i].Confirmed) faceIndex.Add((ulong)i + 1, references[i].Vector);
            }
            using var faces = catalog.CreateCommand(); faces.CommandText = "SELECT id,embedding,quality,portrait FROM observations;";
            using var observations = faces.ExecuteReader();
            ArchiveDatabase.Execute(overlay, "BEGIN IMMEDIATE;");
            try
            {
                while (observations.Read())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    long observation = observations.GetInt64(0);
                    if (observations.GetDouble(2) < 0.4) continue;
                    if (ArchiveDatabase.Scalar(overlay, reclassify ? "SELECT 1 FROM assignments WHERE observation_id=$id AND confirmed=1 AND rejected=0;" : "SELECT 1 FROM assignments WHERE observation_id=$id;", ("$id", observation)) is not null) continue;
                    var vector = VectorEncoding.Decode((byte[])observations[1]);
                    var rejected = new HashSet<string>(StringComparer.Ordinal);
                    using (var command = overlay.CreateCommand())
                    {
                        command.CommandText = "SELECT person_id FROM rejections WHERE observation_id=$id;"; command.Parameters.AddWithValue("$id", observation);
                        using var rejectedReader = command.ExecuteReader(); while (rejectedReader.Read()) rejected.Add(rejectedReader.GetString(0));
                    }
                    // Score all named identities exactly. A bounded ANN list of reference
                    // photos can otherwise hide the runner-up identity behind many photos of
                    // the same person, accidentally treating an ambiguous match as certain.
                    var namedCandidates = namedReferences.Where(r => !rejected.Contains(r.Person)).GroupBy(r => r.Person)
                        .Select(g => (Person: g.Key, Score: g.Max(r => VectorEncoding.Cosine(vector, r.Vector))))
                        .OrderByDescending(r => r.Score).Take(2).ToArray();
                    string person; double score;
                    if (namedCandidates.Length > 0 && namedCandidates[0].Score >= 0.55 && (namedCandidates.Length == 1 || namedCandidates[0].Score - namedCandidates[1].Score >= 0.05))
                    { person = namedCandidates[0].Person; score = namedCandidates[0].Score; }
                    else
                    {
                        var pool = faceIndex is null ? references.Where(r => !r.Confirmed) : faceIndex.Search(vector, 12).Select(n => references[(int)n.Key - 1]);
                        var candidates = pool.Where(r => !rejected.Contains(r.Person)).GroupBy(r => r.Person)
                            .Select(g => (Person: g.Key, Score: g.Max(r => VectorEncoding.Cosine(vector, r.Vector))))
                            .OrderByDescending(r => r.Score).Take(2).ToArray();
                        if (candidates.Length > 0 && candidates[0].Score >= 0.65 && (candidates.Length == 1 || candidates[0].Score - candidates[1].Score >= 0.05))
                        { person = candidates[0].Person; score = candidates[0].Score; }
                        else if (observations.GetDouble(2) >= 0.6)
                        {
                            person = Guid.NewGuid().ToString("D"); score = 1;
                            long number = Convert.ToInt64(ArchiveDatabase.Scalar(overlay, "SELECT COUNT(*) FROM people;"), CultureInfo.InvariantCulture) + 1;
                            ArchiveDatabase.Execute(overlay, "INSERT INTO people(id,name,aliases,portrait,confirmed) VALUES($id,$name,'[]',$portrait,0); INSERT INTO person_references(person_id,embedding,fingerprint) VALUES($id,$embedding,$fingerprint);", ("$id", person), ("$name", $"Unnamed person {number}"), ("$portrait", observations.IsDBNull(3) ? null : observations.GetString(3)), ("$embedding", (byte[])observations[1]), ("$fingerprint", FaceFingerprint));
                            references.Add((person, vector, false));
                            if (faceIndex is null && NativeVectorIndex.Available) faceIndex = new NativeVectorIndex(vector.Length);
                            faceIndex?.Add((ulong)references.Count, vector);
                        }
                        else { ArchiveDatabase.Execute(overlay, "DELETE FROM assignments WHERE observation_id=$id AND confirmed=0 AND rejected=0;", ("$id", observation)); continue; }
                    }
                    ArchiveDatabase.Execute(overlay, "INSERT INTO assignments(observation_id,person_id,confirmed,rejected,score) VALUES($id,$person,0,0,$score) ON CONFLICT(observation_id) DO UPDATE SET person_id=excluded.person_id,confirmed=0,rejected=0,score=excluded.score;", ("$id", observation), ("$person", person), ("$score", score));
                }
                ArchiveDatabase.Execute(overlay, "UPDATE settings SET value=CAST(CAST(value AS INTEGER)+1 AS TEXT) WHERE key='revision'; COMMIT;");
            }
            catch { ArchiveDatabase.Execute(overlay, "ROLLBACK;"); throw; }
            finally { faceIndex?.Dispose(); }
        }
        finally { if (!alreadyLocked) overlayGate.Release(); }
    }
}
