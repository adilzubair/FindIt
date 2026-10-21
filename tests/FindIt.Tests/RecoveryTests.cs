using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using FindIt.Core;
using FindIt.Shared;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FindIt.Tests;

[TestClass]
public sealed class RecoveryTests
{
    private string _root = "";
    private string Home => Path.Combine(_root, "FindIt", "archive");
    [TestInitialize] public void Initialize() { _root = Path.Combine(Path.GetTempPath(), "FindIt-recovery-tests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(Path.Combine(_root, "Documents")); }
    [TestCleanup] public void Cleanup() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    private ArchiveService Service(IContentProcessor? processor = null) => new(_root, new ArchiveTests.TestEmbeddings("test-512-v1"), processor ?? new ArchiveTests.TestProcessor());
    private Task Write(string content) => File.WriteAllTextAsync(Path.Combine(_root, "Documents", "notes.txt"), content);
    private static SearchRequest Exact(string query) => new() { Query = query, Filters = new() { ExactOnly = true } };

    private async Task<(long Previous, long Current)> TwoGenerations()
    {
        await Write("Old cats live in the garden.");
        await using var archive = Service();
        var previous = await archive.PrepareAsync(new() { Folders = ["Documents"] });
        await Write("New bicycle rides in a forest, with no older material.");
        var current = await archive.PrepareAsync(new() { Folders = ["Documents"] });
        Assert.IsTrue(current.GenerationId > previous.GenerationId);
        return (previous.GenerationId, current.GenerationId);
    }

    [TestMethod]
    public async Task TornPointerRecoversLatestGenerationWithoutWritingRepairs()
    {
        var (_, current) = await TwoGenerations();
        await File.WriteAllTextAsync(Path.Combine(Home, "current.json"), "{\"generationId\":");
        var before = await SnapshotAsync(_root);
        await using (var reader = Service())
        {
            var status = await reader.GetStatusAsync();
            Assert.AreEqual(current, status.GenerationId);
            StringAssert.Contains(status.StatusDetail, "Recovered");
            Assert.AreEqual(1, (await reader.SearchAsync(Exact("bicycle"))).Results.Count);
        }
        AssertSnapshotsEqual(before, await SnapshotAsync(_root));
    }

    [TestMethod]
    public async Task DamagedLatestCatalogFallsBackToPreviousVerifiedGeneration()
    {
        var (previous, current) = await TwoGenerations();
        await File.WriteAllBytesAsync(Path.Combine(Home, "generations", current.ToString("D20"), "catalog.sqlite"), [0, 1, 2, 3]);
        var before = await SnapshotAsync(_root);
        await using (var reader = Service())
        {
            var status = await reader.GetStatusAsync();
            Assert.AreEqual(previous, status.GenerationId);
            Assert.AreEqual(1, (await reader.SearchAsync(Exact("cats"))).Results.Count);
            Assert.AreEqual(0, (await reader.SearchAsync(Exact("bicycle"))).Results.Count);
        }
        AssertSnapshotsEqual(before, await SnapshotAsync(_root));
    }

    [TestMethod]
    public async Task TruncatedManifestAndMissingLatestVectorPackRecoverOlderContent()
    {
        var (previous, current) = await TwoGenerations();
        var manifestPath = Path.Combine(Home, "generations", current.ToString("D20"), "manifest.json");
        using (var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath)))
        {
            var pack = manifest.RootElement.GetProperty("vectorPacks")[0].GetString()!;
            File.Delete(Path.Combine(_root, pack.Replace('/', Path.DirectorySeparatorChar)));
        }
        await using (var reader = Service()) Assert.AreEqual(previous, (await reader.GetStatusAsync()).GenerationId);
        await File.WriteAllTextAsync(manifestPath, "{broken");
        await using (var reader = Service())
        {
            Assert.AreEqual(previous, (await reader.GetStatusAsync()).GenerationId);
            Assert.AreEqual(1, (await reader.SearchAsync(Exact("cats"))).Results.Count);
        }
    }

    [TestMethod]
    public async Task HealthyStatusSearchPeopleAndPreviewDoNotWriteAnyDriveFiles()
    {
        await Write("A forest bicycle archive.");
        await using (var writer = Service()) await writer.PrepareAsync(new() { Folders = ["Documents"] });
        var before = await SnapshotAsync(_root);
        await using (var reader = Service())
        {
            await reader.GetStatusAsync();
            var result = await reader.SearchAsync(new() { Query = "forest" });
            await reader.GetPeopleAsync();
            await reader.GetPreviewAsync(new(result.Results.Single().FileId));
            await reader.SearchAsync(Exact("bicycle"));
        }
        AssertSnapshotsEqual(before, await SnapshotAsync(_root));
    }

    [TestMethod]
    public async Task BlobReadRejectsTraversalRootedStreamsNegativeAndTruncatedReferences()
    {
        Directory.CreateDirectory(Path.Combine(Home, "packs"));
        var relative = "FindIt/archive/packs/fixture.pack";
        await File.WriteAllBytesAsync(Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar)), [10, 20, 30, 40]);
        foreach (var reference in new[]
        {
            new BlobReference("../outside.bin", 0, 1), new BlobReference("Documents/../../outside.bin", 0, 1),
            new BlobReference(Path.Combine(_root, "Documents", "notes.txt"), 0, 1),
            new BlobReference("Documents/notes.txt:stream", 0, 1), new BlobReference(relative, -1, 1),
            new BlobReference(relative, 0, -1), new BlobReference(relative, long.MaxValue, 1),
            new BlobReference(relative, 2, 3), new BlobReference(relative, 0, 64 * 1024 * 1024 + 1)
        }) await Assert.ThrowsAsync<IOException>(() => BlobStore.ReadAsync(_root, reference));
        CollectionAssert.AreEqual(new byte[] { 20, 30 }, await BlobStore.ReadAsync(_root, new(relative, 1, 2)));
        Assert.AreEqual(0, (await BlobStore.ReadAsync(_root, new(relative, 4, 0))).Length);
    }

    [TestMethod]
    public async Task RootScanExcludesCaseInsensitiveApplicationAndSystemDirectories()
    {
        await Write("Original forest content.");
        Directory.CreateDirectory(Path.Combine(_root, "fInDiT", "private"));
        await File.WriteAllTextAsync(Path.Combine(_root, "fInDiT", "private", "secret.txt"), "excluded private marker");
        await File.WriteAllTextAsync(Path.Combine(_root, "fInDiT.ExE"), "excluded launcher marker");
        foreach (var name in new[] { "$RECYCLE.BIN", "System Volume Information" })
        {
            Directory.CreateDirectory(Path.Combine(_root, name));
            await File.WriteAllTextAsync(Path.Combine(_root, name, "hidden.txt"), "excluded system marker");
        }
        Directory.CreateDirectory(Path.Combine(_root, "FindIt-notes"));
        await File.WriteAllTextAsync(Path.Combine(_root, "FindIt-notes", "ordinary.txt"), "ordinary archive marker");
        await using var archive = Service();
        Assert.AreEqual(2, (await archive.PrepareAsync(new() { Folders = ["."] })).FileCount);
        Assert.AreEqual(0, (await archive.SearchAsync(Exact("excluded"))).Results.Count);
        Assert.AreEqual(1, (await archive.SearchAsync(Exact("ordinary"))).Results.Count);
        await Assert.ThrowsAsync<IOException>(() => archive.PrepareAsync(new() { Folders = ["FINDIT"] }));
    }

    [TestMethod]
    public async Task OverlappingCaseVariantScopesDoNotDuplicateOrReextractFiles()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Documents", "Sub"));
        await Write("forest");
        await File.WriteAllTextAsync(Path.Combine(_root, "Documents", "Sub", "other.txt"), "bicycle");
        var processor = new ArchiveTests.TestProcessor();
        await using var archive = Service(processor);
        var status = await archive.PrepareAsync(new() { Folders = ["Documents", "documents", "DOCUMENTS/Sub"] });
        Assert.AreEqual(2, status.FileCount);
        Assert.AreEqual(2, processor.Extracted);
        var results = await archive.SearchAsync(new());
        Assert.AreEqual(2, results.Results.Select(r => r.FileId).Distinct().Count());
    }

    [TestMethod]
    public async Task OriginalChangedDuringExtractionKeepsPreviousContentAndRetriesLater()
    {
        await Write("Old cats content.");
        await using (var writer = Service()) await writer.PrepareAsync(new() { Folders = ["Documents"] });
        await Write("New forest text which will change again during extraction.");
        await using (var changing = Service(new MutatingProcessor()))
        {
            var status = await changing.PrepareAsync(new() { Folders = ["Documents"] });
            Assert.IsTrue(status.Issues.Any(issue => issue.Message.Contains("changed", StringComparison.OrdinalIgnoreCase)));
            Assert.AreEqual(1, (await changing.SearchAsync(Exact("cats"))).Results.Count);
            Assert.AreEqual(0, (await changing.SearchAsync(Exact("forest"))).Results.Count);
        }
        await using var resumed = Service();
        var completed = await resumed.PrepareAsync(new() { Folders = ["Documents"] });
        Assert.AreEqual(0, completed.Issues.Count);
        Assert.AreEqual(1, (await resumed.SearchAsync(Exact("bicycle"))).Results.Count);
        Assert.AreEqual(0, (await resumed.SearchAsync(Exact("cats"))).Results.Count);
    }

    private sealed class MutatingProcessor : IContentProcessor
    {
        public IReadOnlyList<RuntimeCapability> Capabilities => [];
        public string FaceFingerprint => "test-face-v1";
        public async IAsyncEnumerable<ExtractedSegment> ExtractAsync(string path, FileKind kind, PreparationRequest options, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new() { Kind = SegmentKind.Text, Text = await File.ReadAllTextAsync(path, cancellationToken) };
            await File.WriteAllTextAsync(path, "The final stable bicycle version, changed during extraction.", cancellationToken);
        }
        public Task<IReadOnlyList<DetectedFace>> DetectReferenceFacesAsync(string path, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<DetectedFace>>([]);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    internal static async Task<Dictionary<string, string>> SnapshotAsync(string root)
    {
        var snapshot = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var info = new FileInfo(path);
            await using var stream = File.OpenRead(path);
            snapshot[Path.GetRelativePath(root, path)] = $"{info.Length}:{info.LastWriteTimeUtc.Ticks}:{Convert.ToHexString(await SHA256.HashDataAsync(stream))}";
        }
        return snapshot;
    }
    internal static void AssertSnapshotsEqual(Dictionary<string, string> before, Dictionary<string, string> after)
    {
        CollectionAssert.AreEquivalent(before.Keys.ToArray(), after.Keys.ToArray(), "Reader created or removed drive files.");
        foreach (var pair in before) Assert.AreEqual(pair.Value, after[pair.Key], $"Reader changed {pair.Key}.");
    }
}
