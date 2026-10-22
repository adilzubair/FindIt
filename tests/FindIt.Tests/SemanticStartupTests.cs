using System.Security.Cryptography;
using FindIt.Core;
using FindIt.Runtime;
using FindIt.Shared;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FindIt.Tests;

[TestClass]
public sealed class SemanticStartupTests
{
    private string root = "";
    private string? previousRecords;
    private string Records => Path.Combine(root, "host-verification");
    private string Model => Path.Combine(root, "model.gguf");
    [TestInitialize] public void Initialize() { root = Path.Combine(Path.GetTempPath(), "FindIt-semantic-startup", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(Path.Combine(root, "Documents")); previousRecords = ModelIntegrity.RecordDirectory; ModelIntegrity.RecordDirectory = Records; ModelIntegrity.ForgetSession(); }
    [TestCleanup] public void Cleanup() { ModelIntegrity.RecordDirectory = previousRecords; ModelIntegrity.ForgetSession(); if (Directory.Exists(root)) Directory.Delete(root, true); }

    [TestMethod]
    public async Task VerifiedModelIsNotHashedAgainByTheNextProcessOnThisPc()
    {
        var expected = await WriteModelAsync();
        int before = ModelIntegrity.HashesComputed;
        await ModelIntegrity.VerifyAsync(Model, "fixture", expected, CancellationToken.None);
        Assert.AreEqual(before + 1, ModelIntegrity.HashesComputed);
        ModelIntegrity.ForgetSession(); // a new worker process has no in-memory verification
        await ModelIntegrity.VerifyAsync(Model, "fixture", expected, CancellationToken.None);
        Assert.AreEqual(before + 1, ModelIntegrity.HashesComputed, "The recorded identity must skip the full-file hash.");
    }

    [TestMethod]
    public async Task ChangedModelIdentityIsHashedAgainAndRejected()
    {
        var expected = await WriteModelAsync();
        await ModelIntegrity.VerifyAsync(Model, "fixture", expected, CancellationToken.None);
        ModelIntegrity.ForgetSession();
        byte[] contents = await File.ReadAllBytesAsync(Model); contents[^1] ^= 1; await File.WriteAllBytesAsync(Model, contents);
        File.SetLastWriteTimeUtc(Model, File.GetLastWriteTimeUtc(Model).AddMinutes(1));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => ModelIntegrity.VerifyAsync(Model, "fixture", expected, CancellationToken.None));
    }

    [TestMethod]
    public async Task UnreadableOrMissingRecordsFallBackToHashing()
    {
        var expected = await WriteModelAsync();
        await ModelIntegrity.VerifyAsync(Model, "fixture", expected, CancellationToken.None);
        foreach (var record in Directory.EnumerateFiles(Records)) await File.WriteAllTextAsync(record, "not this identity");
        ModelIntegrity.ForgetSession(); int before = ModelIntegrity.HashesComputed;
        await ModelIntegrity.VerifyAsync(Model, "fixture", expected, CancellationToken.None);
        Assert.AreEqual(before + 1, ModelIntegrity.HashesComputed);
        ModelIntegrity.RecordDirectory = null; ModelIntegrity.ForgetSession();
        await ModelIntegrity.VerifyAsync(Model, "fixture", expected, CancellationToken.None);
        Assert.AreEqual(before + 2, ModelIntegrity.HashesComputed);
    }

    [TestMethod]
    public async Task ReaderWarmUpEncodesOneQueryForASemanticLibrary()
    {
        await PrepareAsync("warmup-v1");
        var encoder = new WarmUpEncoder("warmup-v1");
        await using var reader = new ArchiveService(root, encoder, new ArchiveTests.TestProcessor()) { WarmUpStartDelay = TimeSpan.Zero };
        reader.StartSemanticWarmUp(); await reader.SemanticWarmUp;
        Assert.AreEqual(1, encoder.QueryCalls);
    }

    [TestMethod]
    public async Task WarmUpWaitsForTheWindowsFirstPageBeforeReadingTheModel()
    {
        await PrepareAsync("warmup-v1");
        var encoder = new WarmUpEncoder("warmup-v1");
        await using var reader = new ArchiveService(root, encoder, new ArchiveTests.TestProcessor()) { WarmUpStartDelay = Timeout.InfiniteTimeSpan };
        reader.StartSemanticWarmUp(); await Task.Delay(200);
        Assert.AreEqual(0, encoder.QueryCalls, "The model load must not compete with the first page's catalog reads.");
        Assert.AreEqual(1, (await reader.SearchAsync(new() { Query = "" })).Results.Count);
        await reader.SemanticWarmUp.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual(1, encoder.QueryCalls);
    }

    [TestMethod]
    public async Task WarmUpSkipsLibrariesThatCannotBeSearchedByMeaning()
    {
        var empty = new WarmUpEncoder("warmup-v1");
        await using (var reader = new ArchiveService(root, empty, new ArchiveTests.TestProcessor())) { reader.StartSemanticWarmUp(); await reader.SemanticWarmUp; }
        Assert.AreEqual(0, empty.QueryCalls, "No prepared generation.");
        await PrepareAsync("warmup-v1");
        var incompatible = new WarmUpEncoder("another-encoder");
        await using (var reader = new ArchiveService(root, incompatible, new ArchiveTests.TestProcessor())) { reader.StartSemanticWarmUp(); await reader.SemanticWarmUp; }
        Assert.AreEqual(0, incompatible.QueryCalls, "Vectors from another encoder.");
    }

    [TestMethod]
    public async Task ClosingTheReaderCancelsAWarmUpInProgress()
    {
        await PrepareAsync("warmup-v1");
        var encoder = new WarmUpEncoder("warmup-v1") { Block = true };
        var reader = new ArchiveService(root, encoder, new ArchiveTests.TestProcessor()) { WarmUpStartDelay = TimeSpan.Zero };
        reader.StartSemanticWarmUp(); await encoder.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await reader.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsTrue(reader.SemanticWarmUp.IsCompletedSuccessfully);
    }

    [TestMethod]
    public async Task SearchStillReportsAnEncoderFailureAfterAFailedWarmUp()
    {
        await PrepareAsync("warmup-v1");
        var encoder = new WarmUpEncoder("warmup-v1") { Fail = true };
        await using var reader = new ArchiveService(root, encoder, new ArchiveTests.TestProcessor()) { WarmUpStartDelay = TimeSpan.Zero };
        reader.StartSemanticWarmUp(); await reader.SemanticWarmUp;
        var response = await reader.SearchAsync(new() { Query = "a bicycle in a forest" });
        Assert.IsFalse(response.SemanticUsed);
        StringAssert.Contains(response.Notice, "semantic runtime could not complete");
    }

    private async Task<(long Bytes, string Sha)> WriteModelAsync()
    {
        byte[] contents = RandomNumberGenerator.GetBytes(64 * 1024); await File.WriteAllBytesAsync(Model, contents);
        return (contents.LongLength, Convert.ToHexStringLower(SHA256.HashData(contents)));
    }
    private async Task PrepareAsync(string fingerprint)
    {
        await File.WriteAllTextAsync(Path.Combine(root, "Documents", "note.txt"), "a bicycle in a forest");
        await using var service = new ArchiveService(root, new ArchiveTests.TestEmbeddings(fingerprint), new ArchiveTests.TestProcessor());
        Assert.IsTrue((await service.PrepareAsync(new() { Folders = ["Documents"] })).SemanticAvailable);
    }

    private sealed class WarmUpEncoder(string fingerprint) : IEmbeddingProvider
    {
        public bool Available => true; public string Fingerprint => fingerprint;
        public bool Block { get; init; } public bool Fail { get; init; }
        public int QueryCalls { get; private set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<float[]?> EmbedTextAsync(string text, bool query, CancellationToken cancellationToken = default)
        {
            if (query) QueryCalls++;
            Started.TrySetResult();
            if (Block) await Task.Delay(Timeout.Infinite, cancellationToken);
            if (Fail) throw new InvalidOperationException("The embedding runtime could not load the model.");
            var vector = new float[512]; vector[0] = 1; return vector;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
