using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FindIt.Shared;

namespace FindIt.Core;

internal sealed record ArtifactDigest(string Path, long Length, string Sha256, long LastWriteUtcTicks = 0);
internal sealed record GenerationManifest
{
    public int FormatVersion { get; init; } = 1;
    public Guid LibraryId { get; init; }
    public long GenerationId { get; init; }
    public DateTimeOffset PreparedUtc { get; init; }
    public string EmbeddingFingerprint { get; init; } = "";
    public int VectorDimension { get; init; } = 512;
    public long VectorCount { get; init; }
    public long FileCount { get; init; }
    public long SegmentCount { get; init; }
    public long TotalBytes { get; init; }
    public IReadOnlyList<KindCount> Kinds { get; init; } = [];
    public IReadOnlyList<FolderEntry> Folders { get; init; } = [];
    public IReadOnlyList<ArchiveIssue> Issues { get; init; } = [];
    public string FaceFingerprint { get; init; } = "";
    public IReadOnlyList<string> VectorPacks { get; init; } = [];
    public string? SemanticBasePath { get; init; }
    public string? SemanticDeltaPath { get; init; }
    public long BaseVectorCount { get; init; }
    public IReadOnlyList<string> BaseVectorPacks { get; init; } = [];
    public IReadOnlyList<ArtifactDigest> Artifacts { get; init; } = [];
}
internal sealed record StageState(Guid LibraryId, long GenerationId, string SettingsFingerprint, string EmbeddingFingerprint, string ThumbnailPack, string VectorPack);
internal sealed record CurrentGeneration(long GenerationId, string ManifestSha256);

internal sealed class ArchiveStorage
{
    public string Root { get; }
    public string Home { get; }
    public string OverlayPath => Path.Combine(Home, "people.sqlite");
    public string StagePath => Path.Combine(Home, "staging");
    private GenerationManifest? cached;
    private Dictionary<string, (long Length, DateTime LastWrite)> verifiedFiles = [];
    private readonly SemaphoreSlim recoveryGate = new(1, 1);
    public string? RecoveryNotice { get; private set; }
    public ArchiveStorage(string root)
    {
        Root = PortablePaths.NormalizeRoot(root);
        if (!Directory.Exists(Root)) throw new DirectoryNotFoundException("The archive drive is not connected.");
        PortablePaths.RejectLinks(Root, Root);
        Home = PortablePaths.Resolve(Root, PortablePaths.MetadataDirectory, true, false);
    }

    public void EnsureWritable()
    {
        PortablePaths.RejectLinks(Root, Home);
        Directory.CreateDirectory(Home);
        Directory.CreateDirectory(Path.Combine(Home, "generations"));
        Directory.CreateDirectory(Path.Combine(Home, "packs"));
        using var overlay = ArchiveDatabase.Open(OverlayPath);
        ArchiveDatabase.InitializeOverlay(overlay);
    }

    public string GenerationDirectory(long generation) => PortablePaths.Resolve(Root, $"FindIt/archive/generations/{generation:D20}", true, false);
    public string CatalogPath(GenerationManifest generation) => Path.Combine(GenerationDirectory(generation.GenerationId), "catalog.sqlite");
    public string VectorPath(GenerationManifest generation) => Path.Combine(GenerationDirectory(generation.GenerationId), "vector-index.bin");
    public string Relative(string path) => PortablePaths.Relative(Root, path);

    public async Task<GenerationManifest?> RecoverAsync(CancellationToken cancellationToken)
    {
        await recoveryGate.WaitAsync(cancellationToken);
        try { return await RecoverCoreAsync(cancellationToken); }
        finally { recoveryGate.Release(); }
    }

    private async Task<GenerationManifest?> RecoverCoreAsync(CancellationToken cancellationToken)
    {
        RecoveryNotice = null;
        if (!Directory.Exists(Home)) return null;
        PortablePaths.RejectLinks(Root, Home);
        CurrentGeneration? pointer = null;
        try { pointer = JsonSerializer.Deserialize<CurrentGeneration>(await File.ReadAllTextAsync(Path.Combine(Home, "current.json"), cancellationToken), WireJson.Options); }
        catch (Exception error) when (error is IOException or JsonException) { }
        if (cached is not null && pointer?.GenerationId == cached.GenerationId && verifiedFiles.All(p => File.Exists(p.Key) && new FileInfo(p.Key).Length == p.Value.Length && File.GetLastWriteTimeUtc(p.Key) == p.Value.LastWrite)) return cached;
        cached = null; verifiedFiles = [];
        var generationsPath = Path.Combine(Home, "generations");
        if (!Directory.Exists(generationsPath)) return null;
        PortablePaths.RejectLinks(Root, generationsPath);
        var candidates = Directory.EnumerateDirectories(generationsPath).Select(p => long.TryParse(Path.GetFileName(p), out long id) ? id : 0).Where(id => id > 0).OrderByDescending(id => id).ToList();
        if (pointer is not null && candidates.Remove(pointer.GenerationId)) candidates.Insert(0, pointer.GenerationId);
        foreach (var id in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var manifestPath = Path.Combine(GenerationDirectory(id), "manifest.json");
                PortablePaths.RejectLinks(Root, manifestPath);
                if (pointer?.GenerationId == id && !string.Equals(await HashFileAsync(manifestPath, cancellationToken), pointer.ManifestSha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Manifest checksum failed.");
                var manifest = JsonSerializer.Deserialize<GenerationManifest>(await File.ReadAllTextAsync(manifestPath, cancellationToken), WireJson.Options) ?? throw new InvalidDataException("Empty manifest.");
                if (manifest.FormatVersion != 1 || manifest.GenerationId != id || manifest.LibraryId == Guid.Empty || manifest.VectorDimension != 512) throw new InvalidDataException("Unsupported archive generation.");
                var verification = new Dictionary<string, (long, DateTime)>();
                foreach (var artifact in manifest.Artifacts)
                {
                    var path = PortablePaths.Resolve(Root, artifact.Path, true);
                    var info = new FileInfo(path);
                    bool smallRequired = path.EndsWith("catalog.sqlite", StringComparison.OrdinalIgnoreCase) || path.EndsWith("vector-index.bin", StringComparison.OrdinalIgnoreCase);
                    if (info.Length != artifact.Length || artifact.LastWriteUtcTicks != 0 && info.LastWriteTimeUtc.Ticks != artifact.LastWriteUtcTicks || smallRequired && !string.Equals(await HashFileAsync(path, cancellationToken), artifact.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("An archive artifact failed validation.");
                    verification[path] = (artifact.Length, File.GetLastWriteTimeUtc(path));
                }
                // The catalog and vector records must always be covered, even for an empty semantic index.
                if (!verification.ContainsKey(CatalogPath(manifest)) || !verification.ContainsKey(VectorPath(manifest)) || manifest.VectorPacks.Any(p => !verification.ContainsKey(PortablePaths.Resolve(Root, p, true)))) throw new InvalidDataException("Generation is missing required artifacts.");
                verification[manifestPath] = (new FileInfo(manifestPath).Length, File.GetLastWriteTimeUtc(manifestPath));
                using (var db = ArchiveDatabase.Open(CatalogPath(manifest), true))
                    if (!string.Equals(ArchiveDatabase.Scalar(db, "PRAGMA quick_check;")?.ToString(), "ok", StringComparison.Ordinal)) throw new InvalidDataException("The catalog failed integrity validation.");
                cached = manifest; verifiedFiles = verification;
                if (pointer?.GenerationId != id) RecoveryNotice = "Recovered the most recent complete, verified archive generation.";
                return manifest;
            }
            catch (Exception error) when (error is IOException or InvalidDataException or JsonException or Microsoft.Data.Sqlite.SqliteException or UnauthorizedAccessException)
            { RecoveryNotice = "An incomplete or damaged generation was skipped. Previous complete results remain available."; }
        }
        return null;
    }

    public async Task PublishAsync(GenerationManifest manifest, CancellationToken cancellationToken, string? sourceFolder = null)
    {
        sourceFolder ??= StagePath;
        if (!PortablePaths.IsWithin(Home, sourceFolder) || string.Equals(Home, sourceFolder, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Publication must originate inside the archive work directory.");
        PortablePaths.RejectLinks(Root, sourceFolder);
        var target = GenerationDirectory(manifest.GenerationId);
        if (Directory.Exists(target)) throw new IOException("This generation already exists.");
        await WriteAtomicAsync(Path.Combine(sourceFolder, "manifest.json"), JsonSerializer.Serialize(manifest, WireJson.Options), cancellationToken);
        Directory.Move(sourceFolder, target);
        // Publication is a pointer to a complete generation. Recovery scans and validates generations if the pointer tears.
        var pointer = new CurrentGeneration(manifest.GenerationId, await HashFileAsync(Path.Combine(target, "manifest.json"), cancellationToken));
        await WriteAtomicAsync(Path.Combine(Home, "current.json"), JsonSerializer.Serialize(pointer, WireJson.Options), cancellationToken);
        await recoveryGate.WaitAsync(cancellationToken);
        try { cached = null; verifiedFiles = []; }
        finally { recoveryGate.Release(); }
    }

    public static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
    }
    public async Task VerifyAllAsync(GenerationManifest generation, CancellationToken cancellationToken)
    {
        foreach (var artifact in generation.Artifacts)
        {
            var path = PortablePaths.Resolve(Root, artifact.Path, true);
            if (new FileInfo(path).Length != artifact.Length || !string.Equals(await HashFileAsync(path, cancellationToken), artifact.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The archive failed a full artifact verification.");
        }
    }
    public static string HashText(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    public static async Task WriteAtomicAsync(string path, string text, CancellationToken cancellationToken)
    {
        var temporary = path + ".new-" + Guid.NewGuid().ToString("N");
        await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
        {
            await stream.WriteAsync(Encoding.UTF8.GetBytes(text), cancellationToken);
            stream.Flush(true);
        }
        File.Move(temporary, path, true);
    }
}
