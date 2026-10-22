using FindIt.Core;
using FindIt.Shared;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FindIt.Tests;

[TestClass]
public sealed class SearchSessionTests
{
    private string root = "";
    [TestInitialize] public void Initialize()
    {
        root = Path.Combine(Path.GetTempPath(), "FindIt-search-sessions", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "Documents"));
    }
    [TestCleanup] public void Cleanup() { if (Directory.Exists(root)) Directory.Delete(root, true); }

    [TestMethod]
    public async Task ContinuationsFreezeFileOrderMomentsAndEncodeOnlyOnce()
    {
        await Originals(41);
        var encoder = new CountingEmbeddings();
        await using var service = new ArchiveService(root, encoder, new ArchiveTests.TestProcessor());
        await service.PrepareAsync(new());
        var request = new SearchRequest { Query = "needle", Limit = 7 };
        var first = await service.SearchAsync(request);
        Assert.IsTrue(first.SemanticUsed); Assert.IsTrue(first.IsBoundedWindow); Assert.IsNotNull(first.PageCursor);
        var seen = new List<Guid>(); string? cursor = first.PageCursor; int pages = 0;
        do
        {
            var page = await service.SearchAsync(request with { Cursor = cursor, GenerationId = first.GenerationId });
            Assert.IsFalse(page.RestartRequired); seen.AddRange(page.Results.Select(r => r.FileId));
            cursor = page.NextCursor; Assert.IsTrue(++pages <= 6);
        } while (cursor is not null);
        Assert.AreEqual(41, seen.Count); Assert.AreEqual(41, seen.Distinct().Count());
        Assert.AreEqual(1, encoder.QueryCalls, "Continuation encoded or retrieved the query again.");
        var revisited = await service.SearchAsync(request with { Cursor = first.PageCursor });
        CollectionAssert.AreEqual(first.Results.Select(r => r.FileId).ToArray(), revisited.Results.Select(r => r.FileId).ToArray());
        CollectionAssert.AreEqual(first.Results.SelectMany(r => r.Moments).Select(m => m.SegmentId).ToArray(), revisited.Results.SelectMany(r => r.Moments).Select(m => m.SegmentId).ToArray());
    }

    [TestMethod]
    public async Task PagingNeverExpandsWindowAndDeeperRetrievalRequiresFreshSearch()
    {
        await Originals(601);
        await using var service = new ArchiveService(root, new CountingEmbeddings(), new ArchiveTests.TestProcessor());
        await service.PrepareAsync(new());
        var request = new SearchRequest { Query = "needle", Limit = 19, Filters = new() { ExactOnly = true } };
        var first = await service.SearchAsync(request);
        Assert.AreEqual(512, first.RetrievalBudget); Assert.IsTrue(first.CanSearchDeeper); Assert.IsNull(first.TotalCount);
        var seen = new HashSet<Guid>(); string? cursor = first.PageCursor; int pages = 0;
        do
        {
            var page = await service.SearchAsync(request with { Cursor = cursor });
            foreach (var file in page.Results) Assert.IsTrue(seen.Add(file.FileId), "A frozen window repeated a file.");
            cursor = page.NextCursor; Assert.IsTrue(++pages <= 27);
        } while (cursor is not null);
        Assert.AreEqual(512, seen.Count, "Continuation silently enlarged its retrieval budget.");
        Assert.IsTrue((await service.SearchAsync(request with { Cursor = first.NextCursor, RetrievalBudget = 1024 })).RestartRequired);
        var deeper = await service.SearchAsync(request with { RetrievalBudget = 1024, Limit = 1000 });
        Assert.AreEqual(601, deeper.Results.Count); Assert.IsFalse(deeper.CanSearchDeeper);
        Assert.AreNotEqual(first.PageCursor, deeper.PageCursor);
    }

    [TestMethod]
    public async Task ChangedQueryFilterEncoderBudgetAndMalformedCursorsRequireRestart()
    {
        await Originals(12);
        var encoder = new CountingEmbeddings();
        await using var service = new ArchiveService(root, encoder, new ArchiveTests.TestProcessor());
        await service.PrepareAsync(new());
        var request = new SearchRequest { Query = "needle", Limit = 4 };
        foreach (var mutate in new Func<SearchRequest, SearchRequest>[] {
            r => r with { Query = "other" }, r => r with { Filters = new() { ExactOnly = true } },
            r => r with { Filters = new() { Folder = "Documents" } }, r => r with { RetrievalBudget = 1024 }, r => r with { Limit = 5 } })
        {
            var first = await service.SearchAsync(request);
            var invalid = await service.SearchAsync(mutate(request with { Cursor = first.NextCursor }));
            Assert.IsTrue(invalid.RestartRequired); Assert.AreEqual(0, invalid.Results.Count); Assert.IsNull(invalid.NextCursor);
        }
        var active = await service.SearchAsync(request);
        encoder.Version = "changed";
        Assert.IsTrue((await service.SearchAsync(request with { Cursor = active.NextCursor })).RestartRequired);
        foreach (string cursor in new[] { "", "garbage", "s2.123.0", $"s1.{Guid.NewGuid():N}.0", $"s1.{Guid.NewGuid():N}.-1", active.PageCursor + ".extra", new string('x', 5000) })
            Assert.IsTrue((await service.SearchAsync(request with { Cursor = cursor })).RestartRequired, cursor);
    }

    [TestMethod]
    public async Task ArchivePublicationAndPeopleCorrectionsInvalidateSessions()
    {
        await Originals(12);
        await using var service = new ArchiveService(root, new CountingEmbeddings(), new ArchiveTests.TestProcessor());
        await service.PrepareAsync(new());
        var request = new SearchRequest { Query = "needle", Limit = 4 };
        var first = await service.SearchAsync(request);
        var person = await service.SavePersonAsync(new() { Name = "Alex" });
        Assert.IsTrue((await service.SearchAsync(request with { Cursor = first.NextCursor })).RestartRequired);
        first = await service.SearchAsync(request);
        await service.SavePersonAsync(new() { PersonId = person.Id, Name = "Alex", Aliases = ["needle"] });
        Assert.IsTrue((await service.SearchAsync(request with { Cursor = first.NextCursor })).RestartRequired);
        first = await service.SearchAsync(request with { Query = "document" });
        await File.WriteAllTextAsync(Path.Combine(root, "Documents", "another.txt"), "needle document");
        await service.PrepareAsync(new());
        Assert.IsTrue((await service.SearchAsync(request with { Query = "document", Cursor = first.NextCursor, GenerationId = first.GenerationId })).RestartRequired);
        Assert.IsTrue((await service.SearchAsync(request with { Query = "document", Cursor = first.NextCursor })).RestartRequired);
    }

    [TestMethod]
    public async Task ExpiryEvictionAndReopenedServiceRequireRestartWithoutEncoding()
    {
        await Originals(12);
        var clock = new TestClock(); var encoder = new CountingEmbeddings();
        var request = new SearchRequest { Query = "needle", Limit = 4 };
        string? cursor;
        await using (var service = new ArchiveService(root, encoder, new ArchiveTests.TestProcessor(), clock))
        {
            await service.PrepareAsync(new());
            var first = await service.SearchAsync(request);
            clock.Advance(TimeSpan.FromMinutes(9));
            Assert.IsFalse((await service.SearchAsync(request with { Cursor = first.PageCursor })).RestartRequired);
            clock.Advance(TimeSpan.FromMinutes(10));
            Assert.IsTrue((await service.SearchAsync(request with { Cursor = first.NextCursor })).RestartRequired);
            Assert.AreEqual(1, encoder.QueryCalls);
            first = await service.SearchAsync(request);
            await service.SearchAsync(request with { Query = "document" });
            await service.SearchAsync(request with { Query = "third" });
            Assert.IsTrue((await service.SearchAsync(request with { Cursor = first.NextCursor })).RestartRequired);
            cursor = (await service.SearchAsync(request)).NextCursor;
        }
        await using var reopened = new ArchiveService(root, new CountingEmbeddings(), new ArchiveTests.TestProcessor());
        Assert.IsTrue((await reopened.SearchAsync(request with { Cursor = cursor })).RestartRequired);
    }

    [TestMethod]
    public async Task PeopleMutationDuringRetrievalDiscardsTheMixedRevisionResponse()
    {
        await Originals(12);
        var encoder = new CountingEmbeddings();
        await using var service = new ArchiveService(root, encoder, new ArchiveTests.TestProcessor());
        await service.PrepareAsync(new());
        encoder.OnQuery = () => service.SavePersonAsync(new() { Name = "New person" }).GetAwaiter().GetResult();
        var mixed = await service.SearchAsync(new() { Query = "needle", Limit = 4 });
        Assert.IsTrue(mixed.RestartRequired); Assert.AreEqual(0, mixed.Results.Count); Assert.IsNull(mixed.PageCursor);
        encoder.OnQuery = null;
        Assert.IsFalse((await service.SearchAsync(new() { Query = "needle", Limit = 4 })).RestartRequired);
    }

    [TestMethod]
    public async Task CancellationPublishesNoSessionAndDoesNotEvictExistingSession()
    {
        await Originals(12);
        var encoder = new CountingEmbeddings();
        await using var service = new ArchiveService(root, encoder, new ArchiveTests.TestProcessor());
        await service.PrepareAsync(new());
        var request = new SearchRequest { Query = "needle", Limit = 4 };
        var first = await service.SearchAsync(request);
        using var cancellation = new CancellationTokenSource(); encoder.OnQuery = cancellation.Cancel;
        await Assert.ThrowsAsync<OperationCanceledException>(() => service.SearchAsync(request with { Query = "cancel" }, cancellation.Token));
        encoder.OnQuery = null;
        Assert.IsFalse((await service.SearchAsync(request with { Cursor = first.NextCursor })).RestartRequired);
        Assert.AreEqual(2, encoder.QueryCalls);
    }

    [TestMethod]
    public void SessionCacheEnforcesTotalBytesAndLeastRecentlyUsedEviction()
    {
        var clock = new TestClock(); var cache = new SearchSessionCache(clock, byteBudget: 2500);
        SearchSession Session() => new() { Binding = "binding", Files = [new(Guid.NewGuid(), 1, false, true, [1])], Tokens = [], People = [], PersonIds = [], Notice = "" };
        var first = Session(); cache.Add(first); clock.Advance(TimeSpan.FromSeconds(1));
        var second = Session(); cache.Add(second); clock.Advance(TimeSpan.FromSeconds(1));
        Assert.AreSame(first, cache.Find(first.Id, "binding")); clock.Advance(TimeSpan.FromSeconds(1));
        var third = Session(); cache.Add(third);
        Assert.AreEqual(2, cache.Count); Assert.IsTrue(cache.RetainedBytes <= 2500);
        Assert.IsNull(cache.Find(second.Id, "binding")); Assert.IsNotNull(cache.Find(first.Id, "binding"));
        var bytesCache = new SearchSessionCache(clock, byteBudget: 1000);
        bytesCache.Add(first); bytesCache.Add(second);
        Assert.AreEqual(1, bytesCache.Count); Assert.IsTrue(bytesCache.RetainedBytes <= 1000);
        clock.Advance(TimeSpan.FromMinutes(10)); Assert.IsNull(bytesCache.Find(second.Id, "binding")); Assert.AreEqual(0, bytesCache.RetainedBytes);
    }

    private async Task Originals(int count)
    {
        for (int i = 0; i < count; i++) await File.WriteAllTextAsync(Path.Combine(root, "Documents", $"item-{i:D4}.txt"), $"needle document {i}");
    }
    private sealed class TestClock : TimeProvider
    {
        private long timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => timestamp;
        public void Advance(TimeSpan elapsed) => timestamp += elapsed.Ticks;
    }
    private sealed class CountingEmbeddings : IEmbeddingProvider
    {
        public bool Available => true;
        public string Version { get; set; } = "session-test-v1";
        public string Fingerprint => Version;
        public int QueryCalls { get; private set; }
        public Action? OnQuery { get; set; }
        public Task<float[]?> EmbedTextAsync(string text, bool query, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (query) { QueryCalls++; OnQuery?.Invoke(); }
            var vector = new float[512]; vector[(query ? QueryCalls : StringComparer.Ordinal.GetHashCode(text) & int.MaxValue) % 512] = 1;
            return Task.FromResult<float[]?>(vector);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
