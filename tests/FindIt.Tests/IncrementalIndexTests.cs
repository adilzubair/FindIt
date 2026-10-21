using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using FindIt.Core;
using FindIt.Runtime;
using FindIt.Shared;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FindIt.Tests;

[TestClass]
public sealed class IncrementalIndexTests
{
    private string _root = "";
    private string Home => Path.Combine(_root, "FindIt", "archive");
    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Combine(Path.GetTempPath(), "FindIt-incremental-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "Documents"));
        if (!NativeVectorIndex.Available) Assert.Inconclusive("The native USearch runtime is required for base/delta integration tests.");
    }
    [TestCleanup] public void Cleanup() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    private ArchiveService Service(ArchiveTests.TestProcessor? processor = null) => new(_root, new FixtureEmbeddings(), processor ?? new());
    private string Original(int id) => Path.Combine(_root, "Documents", $"item-{id:D3}.txt");
    private async Task SeedAsync(int count) { for (var id = 0; id < count; id++) await File.WriteAllTextAsync(Original(id), $"vector {id}"); }
    private static Task<ArchiveStatus> PrepareAsync(ArchiveService service) => service.PrepareAsync(new() { Folders = ["Documents"] });
    private string Absolute(string relative) => Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
    private string GenerationDirectory(long generation) => Path.Combine(Home, "generations", generation.ToString("D20"));
    private async Task<ManifestView> ManifestAsync(long generation) => JsonSerializer.Deserialize<ManifestView>(await File.ReadAllTextAsync(Path.Combine(GenerationDirectory(generation), "manifest.json")), WireJson.Options)!;
    private static async Task<string> DigestAsync(string path) => Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path)));

    [TestMethod]
    public async Task FirstBaseAndSmallAdditionReuseTheBaseAndSearchTheDelta()
    {
        await SeedAsync(30);
        await using var service = Service();
        var first = await PrepareAsync(service); var initial = await ManifestAsync(first.GenerationId);
        Assert.AreEqual(30, initial.VectorCount); Assert.AreEqual(30, initial.BaseVectorCount);
        Assert.IsNotNull(initial.SemanticBasePath); Assert.IsNull(initial.SemanticDeltaPath);
        StringAssert.Contains(initial.SemanticBasePath, $"{first.GenerationId:D20}/semantic.usearch");
        var basePath = Absolute(initial.SemanticBasePath); var baseDigest = await DigestAsync(basePath); var baseModified = File.GetLastWriteTimeUtc(basePath);
        await File.WriteAllTextAsync(Original(100), "vector 100");
        var second = await PrepareAsync(service); var added = await ManifestAsync(second.GenerationId);
        Assert.AreEqual(initial.SemanticBasePath, added.SemanticBasePath);
        Assert.IsNotNull(added.SemanticDeltaPath); Assert.AreEqual(31, added.VectorCount); Assert.AreEqual(30, added.BaseVectorCount);
        CollectionAssert.AreEquivalent(initial.BaseVectorPacks, added.BaseVectorPacks);
        Assert.AreEqual(baseDigest, await DigestAsync(basePath)); Assert.AreEqual(baseModified, File.GetLastWriteTimeUtc(basePath), "Small updates rewrote the immutable base.");
        Assert.IsTrue(added.Artifacts.Any(a => a.Path == initial.SemanticBasePath), "The borrowed base was not retained in the generation's integrity manifest.");
        using (var delta = NativeVectorIndex.Open(Absolute(added.SemanticDeltaPath))) Assert.AreEqual(1, delta.Search(FixtureEmbeddings.Vector(100), 100).Count);
        var query = await service.SearchAsync(new() { Query = "lookup100" });
        Assert.IsTrue(query.SemanticUsed); Assert.AreEqual("item-100.txt", query.Results[0].Name);
        Assert.AreEqual(MatchCategory.Semantic, query.Results[0].MatchCategory, "Fixture query must have no lexical overlap with originals.");
        Assert.IsFalse(query.Notice.Contains("exact scan", StringComparison.OrdinalIgnoreCase), "This test must exercise the native base/delta search path.");
    }

    [TestMethod]
    public async Task DeletedBaseVectorRemainsInTheImmutableFileButCannotLeakIntoLiveResults()
    {
        await SeedAsync(30);
        await using var service = Service();
        var first = await PrepareAsync(service); var initial = await ManifestAsync(first.GenerationId);
        var beforeDelete = await service.SearchAsync(new() { Query = "lookup7" });
        var deleted = beforeDelete.Results[0]; Assert.AreEqual("item-007.txt", deleted.Name);
        var deletedSegment = deleted.Moments.Single().SegmentId;
        File.Delete(Original(7));
        var second = await PrepareAsync(service); var removed = await ManifestAsync(second.GenerationId);
        Assert.AreEqual(initial.SemanticBasePath, removed.SemanticBasePath); Assert.IsNull(removed.SemanticDeltaPath);
        Assert.AreEqual(29, removed.VectorCount); Assert.AreEqual(30, removed.BaseVectorCount);
        using (var rawBase = NativeVectorIndex.Open(Absolute(removed.SemanticBasePath!)))
        {
            Assert.IsTrue(rawBase.Search(FixtureEmbeddings.Vector(7), 100).Any(n => n.Key == (ulong)deletedSegment), "Fixture did not retain the deleted vector in its original base.");
            var liveIds = await ReadLiveVectorIdsAsync(second.GenerationId);
            Assert.IsFalse(liveIds.Contains((ulong)deletedSegment));
            Assert.IsFalse(rawBase.Search(FixtureEmbeddings.Vector(7), 100, liveIds).Any(n => n.Key == (ulong)deletedSegment));
        }
        var results = await service.SearchAsync(new() { Query = "lookup7", Limit = 1000 });
        Assert.IsTrue(results.SemanticUsed); Assert.AreEqual(29, results.Results.Count);
        Assert.IsFalse(results.Results.Any(r => r.FileId == deleted.FileId || r.Moments.Any(m => m.SegmentId == deletedSegment)));
    }

    [TestMethod]
    public async Task AccumulatedDeltaCompactsWhenItReachesTenPercentOfTheBase()
    {
        await SeedAsync(30);
        await using var service = Service();
        var first = await PrepareAsync(service); var initial = await ManifestAsync(first.GenerationId);
        await File.WriteAllTextAsync(Original(100), "vector 100");
        var small = await PrepareAsync(service); var delta = await ManifestAsync(small.GenerationId);
        Assert.AreEqual(initial.SemanticBasePath, delta.SemanticBasePath); Assert.IsNotNull(delta.SemanticDeltaPath);
        await File.WriteAllTextAsync(Original(101), "vector 101"); await File.WriteAllTextAsync(Original(102), "vector 102");
        var compacted = await PrepareAsync(service); var complete = await ManifestAsync(compacted.GenerationId);
        Assert.AreNotEqual(initial.SemanticBasePath, complete.SemanticBasePath, "Three new vectors are exactly 10% of a 30-vector base.");
        Assert.IsNull(complete.SemanticDeltaPath); Assert.AreEqual(33, complete.BaseVectorCount); Assert.AreEqual(33, complete.VectorCount);
        CollectionAssert.AreEquivalent(complete.VectorPacks, complete.BaseVectorPacks);
        foreach (var id in new[] { 0, 100, 101, 102 }) Assert.AreEqual($"item-{id:D3}.txt", (await service.SearchAsync(new() { Query = $"lookup{id}" })).Results[0].Name);
    }

    [TestMethod]
    public async Task ReplacingBaseVectorsUsesADeltaUntilAdditionsAndTombstonesReachThreshold()
    {
        await SeedAsync(40);
        await using var service = Service();
        var first = await PrepareAsync(service); var initial = await ManifestAsync(first.GenerationId);
        var oldSegment = (await service.SearchAsync(new() { Query = "lookup7" })).Results[0].Moments.Single().SegmentId;
        await File.WriteAllTextAsync(Original(7), "vector 200");
        var update = await PrepareAsync(service); var small = await ManifestAsync(update.GenerationId);
        Assert.AreEqual(initial.SemanticBasePath, small.SemanticBasePath); Assert.IsNotNull(small.SemanticDeltaPath);
        var changed = await service.SearchAsync(new() { Query = "lookup200" });
        Assert.AreEqual("item-007.txt", changed.Results[0].Name);
        Assert.AreNotEqual(oldSegment, changed.Results[0].Moments.Single().SegmentId);
        await File.WriteAllTextAsync(Original(8), "vector 201");
        var secondUpdate = await PrepareAsync(service); var compacted = await ManifestAsync(secondUpdate.GenerationId);
        Assert.AreNotEqual(initial.SemanticBasePath, compacted.SemanticBasePath, "Two additions plus two tombstones are 10% of 40 base vectors.");
        Assert.IsNull(compacted.SemanticDeltaPath); Assert.AreEqual(40, compacted.VectorCount);
        Assert.AreEqual("item-008.txt", (await service.SearchAsync(new() { Query = "lookup201" })).Results[0].Name);
    }

    [TestMethod]
    public async Task GarbageCollectionPreservesBorrowedBasePreviousRecoveryAndOriginals()
    {
        await SeedAsync(30);
        var processor = new ArchiveTests.TestProcessor();
        long previousId, currentId; string borrowedBase;
        Directory.CreateDirectory(Path.Combine(_root, "FindIt", "models"));
        var modelPath = Path.Combine(_root, "FindIt", "models", "keep-model.pack"); await File.WriteAllTextAsync(modelPath, "model fixture never collected");
        var originalPath = Path.Combine(_root, "keep-original.pack"); await File.WriteAllTextAsync(originalPath, "original fixture never collected");
        await using (var service = Service(processor))
        {
            var first = await PrepareAsync(service); var initial = await ManifestAsync(first.GenerationId); borrowedBase = initial.SemanticBasePath!;
            await File.WriteAllTextAsync(Original(100), "vector 100");
            var second = await PrepareAsync(service); var extracted = processor.Extracted;
            var third = await PrepareAsync(service); var unchanged = await ManifestAsync(third.GenerationId);
            Assert.AreEqual(extracted, processor.Extracted); Assert.AreEqual(borrowedBase, unchanged.SemanticBasePath);
            var orphan = Path.Combine(Home, "packs", "unreferenced-fixture.pack"); await File.WriteAllTextAsync(orphan, "orphan");
            var fourth = await PrepareAsync(service); var current = await ManifestAsync(fourth.GenerationId);
            previousId = third.GenerationId; currentId = fourth.GenerationId;
            Assert.AreEqual(borrowedBase, current.SemanticBasePath); Assert.AreEqual(extracted, processor.Extracted);
            Assert.IsFalse(Directory.Exists(GenerationDirectory(second.GenerationId)), "An obsolete, unreferenced generation was not collected.");
            Assert.IsTrue(Directory.Exists(GenerationDirectory(first.GenerationId)), "GC removed the old generation containing the current borrowed base.");
            Assert.IsTrue(File.Exists(Absolute(borrowedBase))); Assert.IsFalse(File.Exists(orphan));
            Assert.IsTrue(File.Exists(modelPath)); Assert.IsTrue(File.Exists(originalPath));
            foreach (var pack in current.VectorPacks) Assert.IsTrue(File.Exists(Absolute(pack)), "GC removed a live canonical vector pack.");
        }
        await File.WriteAllBytesAsync(Path.Combine(GenerationDirectory(currentId), "catalog.sqlite"), [1, 2, 3]);
        await using (var recovered = Service())
        {
            Assert.AreEqual(previousId, (await recovered.GetStatusAsync()).GenerationId);
            var result = await recovered.SearchAsync(new() { Query = "lookup100" });
            Assert.IsTrue(result.SemanticUsed); Assert.AreEqual("item-100.txt", result.Results[0].Name);
        }
        Assert.AreEqual("model fixture never collected", await File.ReadAllTextAsync(modelPath));
        Assert.AreEqual("original fixture never collected", await File.ReadAllTextAsync(originalPath));
        Assert.IsTrue(File.Exists(Absolute(borrowedBase)));
    }

    private async Task<HashSet<ulong>> ReadLiveVectorIdsAsync(long generation)
    {
        var bytes = await File.ReadAllBytesAsync(Path.Combine(GenerationDirectory(generation), "vector-index.bin"));
        Assert.AreEqual(0, bytes.Length % 20);
        var ids = new HashSet<ulong>(); for (var offset = 0; offset < bytes.Length; offset += 20) ids.Add((ulong)BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(offset, 8)));
        return ids;
    }
    private sealed record ManifestView(long GenerationId, long VectorCount, string? SemanticBasePath, string? SemanticDeltaPath, long BaseVectorCount, string[] BaseVectorPacks, string[] VectorPacks, ArtifactView[] Artifacts);
    private sealed record ArtifactView(string Path, long Length, string Sha256);
    private sealed class FixtureEmbeddings : IEmbeddingProvider
    {
        public bool Available => true;
        public string Fingerprint => "incremental-orthogonal-fixture-v1";
        public static float[] Vector(int id) { var vector = new float[512]; vector[id % 512] = 1; return vector; }
        public Task<float[]?> EmbedTextAsync(string text, bool query, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var number = Regex.Match(text, @"\d+").Value;
            return Task.FromResult<float[]?>(Vector(int.Parse(number)));
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
