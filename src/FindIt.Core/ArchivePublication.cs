using System.Buffers.Binary;
using System.Globalization;
using FindIt.Shared;
using Microsoft.Data.Sqlite;

namespace FindIt.Core;

public sealed partial class ArchiveService
{
    private async Task<GenerationManifest> PublishSnapshotAsync(SqliteConnection db, StageState state,
        GenerationManifest? previous, Action<PreparationStage, string, string> report,
        CancellationToken cancellationToken, string? publicationFolder = null)
    {
        publicationFolder ??= storage.StagePath;
        report(PreparationStage.RecognizingPeople, "", "Matching available face evidence to named people.");
        await AssignReferencesAsync(db, cancellationToken);
        report(PreparationStage.Publishing, "", "Validating a complete portable generation.");
        var vectorPacks = new List<string>(); long vectorCount = 0;
        var indexPath = Path.Combine(publicationFolder, "vector-index.bin");
        using (var index = new FileStream(indexPath, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024))
        using (var command = db.CreateCommand())
        {
            command.CommandText = "SELECT id,vector_path,vector_offset,vector_length FROM segments WHERE vector_path IS NOT NULL ORDER BY id;";
            using var reader = command.ExecuteReader(); var ids = new Dictionary<string, int>(StringComparer.Ordinal);
            var row = new byte[20];
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                string path = reader.GetString(1);
                if (reader.GetInt32(3) != Dimension * 2) throw new InvalidDataException("A vector has an incompatible dimension.");
                if (!ids.TryGetValue(path, out int pack)) { pack = vectorPacks.Count; ids[path] = pack; vectorPacks.Add(path); }
                BinaryPrimitives.WriteInt64LittleEndian(row.AsSpan(0, 8), reader.GetInt64(0));
                BinaryPrimitives.WriteInt32LittleEndian(row.AsSpan(8, 4), pack);
                BinaryPrimitives.WriteInt64LittleEndian(row.AsSpan(12, 8), reader.GetInt64(2));
                index.Write(row); vectorCount++;
            }
            index.Flush(true);
        }
        var semantic = PublishVectorIndexes(previous, state.GenerationId, indexPath, vectorPacks, vectorCount,
            message => report(PreparationStage.Publishing, "", message), cancellationToken, publicationFolder);
        ArchiveDatabase.Execute(db, "INSERT INTO search_fts(search_fts) VALUES('optimize');");
        if (ArchiveDatabase.Scalar(db, "PRAGMA integrity_check;")?.ToString() != "ok") throw new InvalidDataException("The prepared catalog failed integrity checks.");
        var referenced = new HashSet<string>(vectorPacks, StringComparer.Ordinal);
        var currentRelative = storage.Relative(storage.GenerationDirectory(state.GenerationId)) + "/";
        if (semantic.BasePath is not null && !semantic.BasePath.StartsWith(currentRelative, StringComparison.Ordinal)) referenced.Add(semantic.BasePath);
        if (semantic.DeltaPath is not null && !semantic.DeltaPath.StartsWith(currentRelative, StringComparison.Ordinal)) referenced.Add(semantic.DeltaPath);
        using (var command = db.CreateCommand())
        {
            command.CommandText = "SELECT DISTINCT json_extract(thumbnail,'$.relativePath') FROM segments WHERE thumbnail IS NOT NULL UNION SELECT DISTINCT json_extract(portrait,'$.relativePath') FROM observations WHERE portrait IS NOT NULL;";
            using var reader = command.ExecuteReader(); while (reader.Read()) if (!reader.IsDBNull(0)) referenced.Add(reader.GetString(0));
        }
        var kindCounts = new List<KindCount>(); var folderCounts = new List<FolderEntry>(); var issueList = new List<ArchiveIssue>();
        using (var command = db.CreateCommand())
        {
            command.CommandText = "SELECT kind,COUNT(*) FROM files GROUP BY kind;";
            using var reader = command.ExecuteReader(); while (reader.Read()) kindCounts.Add(new KindCount((FileKind)reader.GetInt32(0), reader.GetInt64(1)));
        }
        using (var command = db.CreateCommand())
        {
            command.CommandText = "SELECT CASE WHEN instr(path,'/')=0 THEN '.' ELSE substr(path,1,instr(path,'/')-1) END,COUNT(*) FROM files GROUP BY 1 ORDER BY 1;";
            using var reader = command.ExecuteReader(); while (reader.Read()) folderCounts.Add(new FolderEntry(reader.GetString(0), reader.GetInt64(1)));
        }
        using (var command = db.CreateCommand())
        {
            command.CommandText = "SELECT path,stage,message FROM issues LIMIT 100;";
            using var reader = command.ExecuteReader(); while (reader.Read()) issueList.Add(new ArchiveIssue(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        }
        long totalSegments = Convert.ToInt64(ArchiveDatabase.Scalar(db, "SELECT COUNT(*) FROM segments;"), CultureInfo.InvariantCulture);
        long totalBytes = Convert.ToInt64(ArchiveDatabase.Scalar(db, "SELECT COALESCE(SUM(bytes),0) FROM files;"), CultureInfo.InvariantCulture);
        db.Close();
        var artifacts = new List<ArtifactDigest>();
        foreach (var name in new[] { "catalog.sqlite", "vector-index.bin", "semantic.usearch", "semantic-delta.usearch" }.Where(name => File.Exists(Path.Combine(publicationFolder, name))))
        {
            var path = Path.Combine(publicationFolder, name);
            artifacts.Add(new ArtifactDigest(storage.Relative(Path.Combine(storage.GenerationDirectory(state.GenerationId), name)), new FileInfo(path).Length, await ArchiveStorage.HashFileAsync(path, cancellationToken), File.GetLastWriteTimeUtc(path).Ticks));
        }
        foreach (var relative in referenced)
        {
            var path = PortablePaths.Resolve(RootPath, relative, true);
            var info = new FileInfo(path);
            var prior = previous?.Artifacts.FirstOrDefault(a => a.Path == relative);
            artifacts.Add(prior is not null && prior.Length == info.Length && prior.WriteTimeMatches(info.LastWriteTimeUtc)
                ? prior : new ArtifactDigest(relative, info.Length, await ArchiveStorage.HashFileAsync(path, cancellationToken), info.LastWriteTimeUtc.Ticks));
        }
        var manifest = new GenerationManifest { LibraryId = state.LibraryId, GenerationId = state.GenerationId, PreparedUtc = DateTimeOffset.UtcNow, EmbeddingFingerprint = embeddings.Fingerprint, FaceFingerprint = FaceFingerprint, VectorCount = vectorCount, VectorPacks = vectorPacks, Artifacts = artifacts, FileCount = kindCounts.Sum(k => k.Count), SegmentCount = totalSegments, TotalBytes = totalBytes, Kinds = kindCounts, Folders = folderCounts, Issues = issueList, SemanticBasePath = semantic.BasePath, SemanticDeltaPath = semantic.DeltaPath, BaseVectorCount = semantic.BaseCount, BaseVectorPacks = semantic.BasePacks };
        await storage.PublishAsync(manifest, cancellationToken, publicationFolder);
        await CollectUnreferencedAsync(manifest, previous, cancellationToken);
        return manifest;
    }
}
