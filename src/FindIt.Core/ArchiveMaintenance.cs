using System.Buffers.Binary;
using System.Text.Json;
using FindIt.Runtime;
using FindIt.Shared;

namespace FindIt.Core;

public sealed partial class ArchiveService
{
    private sealed record IndexPublication(string? BasePath, string? DeltaPath, long BaseCount, IReadOnlyList<string> BasePacks);

    private IndexPublication PublishVectorIndexes(GenerationManifest? previous, long generationId, string recordsPath,
        IReadOnlyList<string> packs, long count, Action<string> report, CancellationToken cancellationToken, string? publicationFolder = null)
    {
        publicationFolder ??= storage.StagePath;
        if (count == 0 || !NativeVectorIndex.Available) return new(null, null, 0, []);
        var generationRelative = storage.Relative(storage.GenerationDirectory(generationId));
        string? priorBase = previous?.SemanticBasePath;
        if (priorBase is null && previous is not null && File.Exists(Path.Combine(storage.GenerationDirectory(previous.GenerationId), "semantic.usearch")))
            priorBase = storage.Relative(Path.Combine(storage.GenerationDirectory(previous.GenerationId), "semantic.usearch"));
        bool compatible = previous is not null && previous.EmbeddingFingerprint == embeddings.Fingerprint && priorBase is not null && File.Exists(PortablePaths.Resolve(RootPath, priorBase, true));
        var basePacks = compatible ? (previous!.BaseVectorPacks.Count > 0 ? previous.BaseVectorPacks : previous.VectorPacks).ToHashSet(StringComparer.Ordinal) : [];
        long baseCount = compatible ? (previous!.BaseVectorCount > 0 ? previous.BaseVectorCount : previous.VectorCount) : 0;
        long activeBase = 0;
        if (compatible)
        {
            using var records = new FileStream(recordsPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);
            var row = new byte[20];
            while (records.Position < records.Length)
            {
                cancellationToken.ThrowIfCancellationRequested(); records.ReadExactly(row);
                if (basePacks.Contains(packs[BinaryPrimitives.ReadInt32LittleEndian(row.AsSpan(8, 4))])) activeBase++;
            }
        }
        long deltaCount = count - activeBase;
        long changed = deltaCount + Math.Max(0, baseCount - activeBase);
        long threshold = Math.Min(100_000, Math.Max(1, (long)Math.Ceiling(baseCount * .1)));
        if (compatible && changed < threshold)
        {
            string? delta = null;
            if (deltaCount > 0)
            {
                report($"Updating {deltaCount:N0} vectors in the incremental search index.");
                ArchiveSpace.EnsureAvailable(RootPath, checked(deltaCount * 1344 + 16 * 1024 * 1024));
                BuildNativeIndex(recordsPath, packs, Path.Combine(publicationFolder, "semantic-delta.usearch"), cancellationToken, basePacks);
                delta = generationRelative + "/semantic-delta.usearch";
            }
            return new(priorBase, delta, baseCount, basePacks.ToArray());
        }
        report(compatible ? "Compacting the search index after archive changes." : "Building the optimized portable semantic index.");
        ArchiveSpace.EnsureAvailable(RootPath, checked(count * 1344 + 16 * 1024 * 1024));
        BuildNativeIndex(recordsPath, packs, Path.Combine(publicationFolder, "semantic.usearch"), cancellationToken);
        return new(generationRelative + "/semantic.usearch", null, count, packs.ToArray());
    }

    /// <summary>Only app-owned artifacts are collected. Originals and models are never candidates.</summary>
    private async Task CollectUnreferencedAsync(CancellationToken cancellationToken)
    {
        var generations = Path.Combine(storage.Home, "generations");
        var manifests = new List<(string Directory, GenerationManifest Manifest)>();
        foreach (var directory in Directory.EnumerateDirectories(generations).OrderByDescending(Path.GetFileName))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                PortablePaths.RejectLinks(RootPath, directory);
                var manifest = JsonSerializer.Deserialize<GenerationManifest>(await File.ReadAllTextAsync(Path.Combine(directory, "manifest.json"), cancellationToken), WireJson.Options);
                if (manifest is not null) manifests.Add((directory, manifest));
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        }
        var protectedFiles = manifests.Take(2).SelectMany(m => m.Manifest.Artifacts.Select(a => a.Path)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var current in manifests.Take(2)) protectedFiles.Add(storage.Relative(Path.Combine(current.Directory, "manifest.json")));
        var protectedDirectories = manifests.Take(2).Select(m => m.Directory).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var path in protectedFiles)
        {
            var absolute = PortablePaths.Resolve(RootPath, path, true, false);
            foreach (var candidate in manifests) if (PortablePaths.IsWithin(candidate.Directory, absolute)) protectedDirectories.Add(candidate.Directory);
        }
        if (File.Exists(storage.OverlayPath))
        {
            using var overlay = ArchiveDatabase.Open(storage.OverlayPath, true);
            using var command = overlay.CreateCommand(); command.CommandText = "SELECT portrait FROM people WHERE portrait IS NOT NULL;";
            using var reader = command.ExecuteReader(); while (reader.Read())
            {
                try { var blob = JsonSerializer.Deserialize<BlobReference>(reader.GetString(0), WireJson.Options); if (blob is not null) protectedFiles.Add(blob.RelativePath); }
                catch (JsonException) { }
            }
        }
        if (File.Exists(Path.Combine(storage.StagePath, "state.json")))
        {
            try
            {
                var state = JsonSerializer.Deserialize<StageState>(await File.ReadAllTextAsync(Path.Combine(storage.StagePath, "state.json"), cancellationToken), WireJson.Options);
                if (state is not null) { protectedFiles.Add(state.ThumbnailPack); protectedFiles.Add(state.VectorPack); }
            }
            catch (Exception ex) when (ex is IOException or JsonException) { }
        }
        foreach (var candidate in manifests.Where(m => !protectedDirectories.Contains(m.Directory)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var resolved = Path.GetFullPath(candidate.Directory);
                if (!PortablePaths.IsWithin(Path.GetFullPath(generations), resolved)) throw new IOException("Unsafe generation collection path.");
                PortablePaths.RejectLinks(RootPath, resolved);
                Directory.Delete(resolved, true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        foreach (var pack in Directory.EnumerateFiles(Path.Combine(storage.Home, "packs"), "*.pack"))
        {
            if (protectedFiles.Contains(storage.Relative(pack))) continue;
            try { PortablePaths.RejectLinks(RootPath, pack); File.Delete(pack); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        foreach (var abandoned in Directory.EnumerateDirectories(storage.Home, "interrupted-*"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var absolute = Path.GetFullPath(abandoned);
                if (!PortablePaths.IsWithin(Path.GetFullPath(storage.Home), absolute)) throw new IOException("Unsafe abandoned work path.");
                PortablePaths.RejectLinks(RootPath, absolute);
                Directory.Delete(absolute, true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
