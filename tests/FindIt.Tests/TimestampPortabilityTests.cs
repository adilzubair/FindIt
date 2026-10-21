using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FindIt.Core;
using FindIt.Shared;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FindIt.Tests;

[TestClass]
public sealed class TimestampPortabilityTests
{
    private string root = "";
    private string Original => Path.Combine(root, "Documents", "invoice.txt");
    [TestInitialize] public void Initialize() { root = Path.Combine(Path.GetTempPath(), "FindIt-timestamp-tests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(Path.GetDirectoryName(Original)!); }
    [TestCleanup] public void Cleanup() { if (Directory.Exists(root)) Directory.Delete(root, true); }
    private ArchiveService Service() => new(root, new ArchiveTests.TestEmbeddings("timestamp-fixture-v1"), new ArchiveTests.TestProcessor());
    private string Generation(long id) => Path.Combine(root, "FindIt", "archive", "generations", id.ToString("D20"));
    private string Absolute(string relative) => Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));

    [TestMethod]
    public async Task CatalogTimestampOnlyChangeKeepsTheGenerationAndManifestImmutable()
    {
        var history = await PrepareHistoryAsync(); string manifest = Path.Combine(Generation(history.Current), "manifest.json"); byte[] before = await File.ReadAllBytesAsync(manifest);
        string catalog = Path.Combine(Generation(history.Current), "catalog.sqlite"); ShiftTimestamp(catalog);
        await using var reader = Service();
        Assert.AreEqual(history.Current, (await reader.GetStatusAsync()).GenerationId);
        Assert.AreEqual("invoice.txt", (await reader.SearchAsync(new() { Query = "newmarker7351", Filters = new() { ExactOnly = true } })).Results.Single().Name);
        CollectionAssert.AreEqual(before, await File.ReadAllBytesAsync(manifest), "Recovery must not rewrite the immutable manifest for this host's timestamp interpretation.");
    }

    [TestMethod]
    public async Task PackTimestampOnlyChangeIsHashedAcceptedAndCachedUsingObservedTime()
    {
        var history = await PrepareHistoryAsync(); string pack = await NewCurrentPackAsync(history.Previous, history.Current);
        string manifest = Path.Combine(Generation(history.Current), "manifest.json"); byte[] before = await File.ReadAllBytesAsync(manifest); ShiftTimestamp(pack);
        await using var reader = Service(); Assert.AreEqual(history.Current, (await reader.GetStatusAsync()).GenerationId);
        // Once verified, an unchanged observed timestamp must use the cached identity. An
        // exclusive handle makes an accidental repeat hash fail, without modifying contents.
        using (var locked = new FileStream(pack, FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.AreEqual(history.Current, (await reader.GetStatusAsync()).GenerationId);
        CollectionAssert.AreEqual(before, await File.ReadAllBytesAsync(manifest));
    }

    [TestMethod]
    public async Task SameSizeChangedPackAndTimestampFallBackToPreviousCompleteGeneration()
    {
        var history = await PrepareHistoryAsync(); string pack = await NewCurrentPackAsync(history.Previous, history.Current);
        byte[] contents = await File.ReadAllBytesAsync(pack); long length = contents.LongLength; contents[0] ^= 1; await File.WriteAllBytesAsync(pack, contents); ShiftTimestamp(pack);
        Assert.AreEqual(length, new FileInfo(pack).Length);
        await AssertPreviousGenerationAsync(history.Previous);
    }

    [TestMethod]
    public async Task SameSizeChangedCatalogAndTimestampFallBackToPreviousCompleteGeneration()
    {
        var history = await PrepareHistoryAsync(); string catalog = Path.Combine(Generation(history.Current), "catalog.sqlite");
        await ChangeCatalogTextWithoutChangingLengthAsync(catalog); ShiftTimestamp(catalog);
        await AssertPreviousGenerationAsync(history.Previous);
    }

    [TestMethod]
    public async Task RequiredCatalogChecksumStillDetectsSameSizeChangeWithPreservedTimestamp()
    {
        var history = await PrepareHistoryAsync(); string catalog = Path.Combine(Generation(history.Current), "catalog.sqlite"); DateTime originalTimestamp = File.GetLastWriteTimeUtc(catalog);
        await ChangeCatalogTextWithoutChangingLengthAsync(catalog); File.SetLastWriteTimeUtc(catalog, originalTimestamp);
        await AssertPreviousGenerationAsync(history.Previous);
    }

    [TestMethod]
    public async Task SizeMismatchStillRejectsAnArtifactEvenWhenItsTimestampIsPreserved()
    {
        var history = await PrepareHistoryAsync(); string pack = await NewCurrentPackAsync(history.Previous, history.Current); DateTime originalTimestamp = File.GetLastWriteTimeUtc(pack);
        await using (var stream = new FileStream(pack, FileMode.Append, FileAccess.Write, FileShare.None)) await stream.WriteAsync(new byte[] { 0 });
        File.SetLastWriteTimeUtc(pack, originalTimestamp); await AssertPreviousGenerationAsync(history.Previous);
    }

    private async Task<(long Previous, long Current)> PrepareHistoryAsync()
    {
        await File.WriteAllTextAsync(Original, "oldmarker4821 invoice for a bicycle in a forest.");
        await using var service = Service(); var previous = await service.PrepareAsync(new() { Folders = ["Documents"] });
        await File.WriteAllTextAsync(Original, "newmarker7351 updated invoice for citrus fruit and a dog.");
        var current = await service.PrepareAsync(new() { Folders = ["Documents"] });
        Assert.IsTrue(current.GenerationId > previous.GenerationId); return (previous.GenerationId, current.GenerationId);
    }
    private async Task<string> NewCurrentPackAsync(long previous, long current)
    {
        using var older = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(Generation(previous), "manifest.json")));
        using var newer = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(Generation(current), "manifest.json")));
        var oldPacks = older.RootElement.GetProperty("vectorPacks").EnumerateArray().Select(p => p.GetString()).ToHashSet(StringComparer.Ordinal);
        string? pack = newer.RootElement.GetProperty("vectorPacks").EnumerateArray().Select(p => p.GetString()).FirstOrDefault(p => !oldPacks.Contains(p));
        Assert.IsNotNull(pack, "The second generation must own a new vector pack so fallback does not reference the altered artifact."); return Absolute(pack);
    }
    private async Task AssertPreviousGenerationAsync(long previous)
    {
        await using var reader = Service(); Assert.AreEqual(previous, (await reader.GetStatusAsync()).GenerationId);
        var results = await reader.SearchAsync(new() { Query = "oldmarker4821", Filters = new() { ExactOnly = true } }); Assert.AreEqual(1, results.Results.Count);
    }
    private static void ShiftTimestamp(string path) => File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddHours(5));
    private static async Task ChangeCatalogTextWithoutChangingLengthAsync(string path)
    {
        byte[] contents = await File.ReadAllBytesAsync(path), marker = Encoding.UTF8.GetBytes("newmarker7351");
        int offset = contents.AsSpan().IndexOf(marker); Assert.IsTrue(offset >= 0, "The current catalog must contain the marker.");
        contents[offset] = (byte)'N'; long length = contents.LongLength; await File.WriteAllBytesAsync(path, contents); Assert.AreEqual(length, new FileInfo(path).Length);
    }
}
