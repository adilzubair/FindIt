using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using FindIt.Core;
using FindIt.Runtime;
using FindIt.Shared;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FindIt.Tests;

[TestClass]
public sealed class SmallFilteredSearchTests
{
    private string root = "";

    [TestInitialize]
    public void Initialize()
    {
        root = Path.Combine(Path.GetTempPath(), "FindIt-small-filter", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "Selected"));
        Directory.CreateDirectory(Path.Combine(root, "Other"));
        if (!NativeVectorIndex.Available) Assert.Inconclusive("Native USearch is required to distinguish exact-only routing from native initialization.");
    }

    [TestCleanup] public void Cleanup() { if (Directory.Exists(root)) Directory.Delete(root, true); }

    [TestMethod]
    public async Task SmallFilterAvoidsAnnUntilBroadSearchAndRefreshesMappingsAfterPublication()
    {
        await Write("Selected/one.txt", "content1");
        await Write("Selected/two.txt", "content2");
        await Write("Other/three.txt", "content3");
        var encoder = new NumberEmbeddings();
        await using var service = new ArchiveService(root, encoder, new ArchiveTests.TestProcessor());
        var first = await service.PrepareAsync(new());

        var narrow = await service.SearchAsync(Query(2, "Selected"));
        Assert.IsTrue(narrow.SemanticUsed); Assert.AreEqual("two.txt", narrow.Results[0].Name);
        Assert.AreEqual(1, encoder.QueryCalls, "A filtered exact vector search must still encode the semantic query.");
        AssertNoNativeReaderIndex(service);
        var firstExact = Field(service, "exactIndex"); Assert.IsNotNull(firstExact);

        var broad = await service.SearchAsync(Query(2));
        Assert.IsTrue(broad.SemanticUsed); Assert.AreEqual("two.txt", broad.Results[0].Name);
        var firstNative = Field(service, "nativeIndex"); Assert.IsNotNull(firstNative, "An earlier exact-only lookup must not suppress later native initialization.");
        Assert.AreEqual(true, Field(service, "nativeIndexInitialized"));

        File.Delete(Path.Combine(root, "Selected", "two.txt"));
        await Write("Selected/replacement.txt", "content4");
        var second = await service.PrepareAsync(new());
        Assert.AreNotEqual(first.GenerationId, second.GenerationId);
        var refreshed = await service.SearchAsync(Query(4, "Selected"));
        Assert.IsTrue(refreshed.SemanticUsed); Assert.AreEqual("replacement.txt", refreshed.Results[0].Name);
        Assert.IsFalse(refreshed.Results.Any(r => r.Name == "two.txt"));
        AssertNoNativeReaderIndex(service);
        Assert.AreNotSame(firstExact, Field(service, "exactIndex"), "New-generation filtered search reused stale canonical mappings.");
        Assert.AreEqual(second.GenerationId, Field(service, "loadedVectorGeneration"));
        Assert.ThrowsExactly<ObjectDisposedException>(() => ((NativeVectorIndex)firstNative).Search(new float[512], 1));

        var nextBroad = await service.SearchAsync(Query(4));
        Assert.IsTrue(nextBroad.SemanticUsed); Assert.AreEqual("replacement.txt", nextBroad.Results[0].Name);
        Assert.IsNotNull(Field(service, "nativeIndex"));
        Assert.AreNotSame(firstNative, Field(service, "nativeIndex"));
    }

    [TestMethod]
    public async Task ThousandsOfNonSemanticSegmentsDoNotInflateTheExactSearchCutoff()
    {
        await Write("Selected/mixed.txt", "fixture");
        await using var service = new ArchiveService(root, new NumberEmbeddings(), new MixedProcessor());
        var prepared = await service.PrepareAsync(new());
        Assert.AreEqual(4098L, prepared.SegmentCount, "The fixture must exceed the 4096-segment cutoff while retaining only two semantic vectors.");

        var result = await service.SearchAsync(Query(2, "Selected"));
        Assert.IsTrue(result.SemanticUsed); Assert.AreEqual("mixed.txt", result.Results[0].Name);
        Assert.IsTrue(result.Results[0].Moments.Any(m => m.Text == "content2"));
        AssertNoNativeReaderIndex(service);
        Assert.IsNotNull(Field(service, "exactIndex"));
    }

    [TestMethod]
    public async Task CancellationAfterQueryEncodingStillCancelsFilteredRetrieval()
    {
        await Write("Selected/one.txt", "content1");
        var encoder = new NumberEmbeddings();
        await using var service = new ArchiveService(root, encoder, new ArchiveTests.TestProcessor());
        await service.PrepareAsync(new());
        using var cancellation = new CancellationTokenSource(); encoder.OnQuery = cancellation.Cancel;

        await Assert.ThrowsAsync<OperationCanceledException>(() => service.SearchAsync(Query(1, "Selected"), cancellation.Token));
        Assert.AreEqual(1, encoder.QueryCalls); AssertNoNativeReaderIndex(service);
    }

    private Task Write(string path, string text) => File.WriteAllTextAsync(Path.Combine(root, path), text);
    private static SearchRequest Query(int id, string? folder = null) => new() { Query = "lookup" + id, Filters = new() { Folder = folder } };

    // Resource lifecycle is the regression: result equality alone cannot detect an
    // unnecessary native load because native-open failures already fall back to exact.
    private static object? Field(ArchiveService service, string name) => typeof(ArchiveService).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(service);
    private static void AssertNoNativeReaderIndex(ArchiveService service)
    {
        Assert.IsNull(Field(service, "nativeIndex")); Assert.IsNull(Field(service, "nativeDeltaIndex"));
        Assert.IsNull(Field(service, "liveVectorIds")); Assert.AreEqual(false, Field(service, "nativeIndexInitialized"));
    }

    private sealed class NumberEmbeddings : IEmbeddingProvider
    {
        public bool Available => true;
        public string Fingerprint => "small-filter-numbers-v1";
        public int QueryCalls { get; private set; }
        public Action? OnQuery { get; set; }
        public Task<float[]?> EmbedTextAsync(string text, bool query, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (query) { QueryCalls++; OnQuery?.Invoke(); }
            int number = int.Parse(Regex.Match(text, @"\d+").Value);
            var vector = new float[512]; vector[number % vector.Length] = 1;
            return Task.FromResult<float[]?>(vector);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class MixedProcessor : IContentProcessor
    {
        public IReadOnlyList<RuntimeCapability> Capabilities => [];
        public string FaceFingerprint => "small-filter-no-faces-v1";
        public async IAsyncEnumerable<ExtractedSegment> ExtractAsync(string absolutePath, FileKind kind, PreparationRequest options, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield return new() { Kind = SegmentKind.Text, Text = "content1" };
            yield return new() { Kind = SegmentKind.Text, Text = "content2" };
            for (int i = 0; i < 4096; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return new() { Kind = SegmentKind.Text, Text = "metadata", SkipTextEmbedding = true };
            }
        }
        public Task<IReadOnlyList<DetectedFace>> DetectReferenceFacesAsync(string absolutePath, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<DetectedFace>>([]);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
