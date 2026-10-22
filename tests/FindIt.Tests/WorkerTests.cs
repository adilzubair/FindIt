using System.Diagnostics;
using System.Text.Json;
using FindIt.Client;
using FindIt.Shared;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FindIt.Tests;

[TestClass]
public sealed class WorkerTests
{
    private string _root = "";
    [TestInitialize] public void Initialize() { _root = Path.Combine(Path.GetTempPath(), "FindIt-worker-tests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(Path.Combine(_root, "Documents")); }
    [TestCleanup] public void Cleanup() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    [TestMethod]
    public async Task ReaderRestartReturnsStructuredSearchRestartInsteadOfRerankingOldOffset()
    {
        for (int i = 0; i < 5; i++) await File.WriteAllTextAsync(Path.Combine(_root, "Documents", $"item-{i}.bin"), "filename-only fixture");
        await using (var preparer = Start("preparer"))
        {
            await preparer.SendAsync("prepare", "archive.prepare", new PreparationRequest { Folders = ["Documents"] });
            using var prepared = await preparer.ResponseAsync("prepare");
            Assert.IsTrue(prepared.RootElement.TryGetProperty("result", out _), prepared.RootElement.ToString());
        }
        var request = new SearchRequest { Query = "item", Limit = 2, Filters = new() { ExactOnly = true } };
        string cursor;
        await using (var reader = Start("reader"))
        {
            await reader.SendAsync("first", "archive.search", request);
            using var first = await reader.ResponseAsync("first");
            cursor = first.RootElement.GetProperty("result").GetProperty("nextCursor").GetString()!;
            Assert.IsFalse(string.IsNullOrEmpty(cursor));
            await reader.SendAsync("next", "archive.search", request with { Cursor = cursor });
            using var next = await reader.ResponseAsync("next");
            Assert.IsFalse(next.RootElement.GetProperty("result").GetProperty("restartRequired").GetBoolean());
        }
        await using var restarted = Start("reader");
        await restarted.SendAsync("expired", "archive.search", request with { Cursor = cursor });
        using var expired = await restarted.ResponseAsync("expired");
        Assert.IsTrue(expired.RootElement.GetProperty("result").GetProperty("restartRequired").GetBoolean());
        Assert.AreEqual(0, expired.RootElement.GetProperty("result").GetProperty("results").GetArrayLength());
    }

    [TestMethod]
    public async Task ReaderRejectsPreparationAndKeepsTheDriveUnmodified()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "Documents", "plain.txt"), "forest document");
        var before = await RecoveryTests.SnapshotAsync(_root);
        await using (var worker = Start("reader"))
        {
            await worker.SendAsync("forbidden", "archive.prepare", new PreparationRequest { Folders = ["Documents"] });
            using var response = await worker.ResponseAsync("forbidden");
            Assert.IsTrue(response.RootElement.TryGetProperty("error", out var error));
            StringAssert.Contains(error.GetProperty("message").GetString()!, "explicit preparer");
            await worker.SendAsync("still-alive", "archive.status");
            using var next = await worker.ResponseAsync("still-alive");
            Assert.AreEqual(0, next.RootElement.GetProperty("result").GetProperty("fileCount").GetInt64());
        }
        RecoveryTests.AssertSnapshotsEqual(before, await RecoveryTests.SnapshotAsync(_root));
    }

    [TestMethod]
    public async Task PreparationProgressPrecedesItsFinalResultAndStreamRemainsUsable()
    {
        // Filename-only originals keep protocol tests independent of installed ML models.
        await File.WriteAllTextAsync(Path.Combine(_root, "Documents", "cats.bin"), "cats live here");
        await File.WriteAllTextAsync(Path.Combine(_root, "Documents", "forest.bin"), "forest bicycle route");
        await using var worker = Start("preparer");
        await worker.SendAsync("prepare", "archive.prepare", new PreparationRequest { Folders = ["Documents"] });
        var progress = new List<PreparationProgress>();
        using (var final = await worker.ResponseAsync("prepare", progress))
        {
            Assert.IsTrue(final.RootElement.TryGetProperty("result", out var result), final.RootElement.ToString());
            Assert.AreEqual(2, result.GetProperty("fileCount").GetInt64());
            Assert.IsTrue(progress.Count > 0);
            Assert.AreEqual(PreparationStage.Completed, progress[^1].Stage, "Final result overtook completed progress.");
            Assert.AreEqual(2, progress[^1].Completed);
        }
        await worker.SendAsync("following", "archive.search", new SearchRequest { Query = "forest", Filters = new() { ExactOnly = true } });
        using var following = await worker.ResponseAsync("following");
        Assert.AreEqual("forest.bin", following.RootElement.GetProperty("result").GetProperty("results")[0].GetProperty("name").GetString());
    }

    [TestMethod]
    public async Task CancellationCheckpointsBeforeReplyAndFollowingRequestsStayFramed()
    {
        for (var i = 0; i < 150; i++) await File.WriteAllTextAsync(Path.Combine(_root, "Documents", $"file-{i:D3}.bin"), $"Filename-only original {i}.");
        await using var worker = Start("preparer");
        await worker.SendAsync("cancelled", "archive.prepare", new PreparationRequest { Folders = ["Documents"] });
        using (var first = await worker.ReadAsync())
        {
            Assert.AreEqual("preparation.progress", first.RootElement.GetProperty("method").GetString());
            Assert.AreEqual("cancelled", first.RootElement.GetProperty("params").GetProperty("requestId").GetString());
        }
        await worker.CancelAsync("cancelled");
        var progress = new List<PreparationProgress>();
        using (var paused = await worker.ResponseAsync("cancelled", progress))
        {
            Assert.AreEqual(-32800, paused.RootElement.GetProperty("error").GetProperty("code").GetInt32(), paused.RootElement.ToString());
            Assert.IsTrue(progress.Any(p => p.Stage == PreparationStage.Paused), "Cancellation did not checkpoint before responding.");
        }
        await worker.SendAsync("after-cancel", "archive.status");
        using (var status = await worker.ResponseAsync("after-cancel"))
        {
            long published=status.RootElement.GetProperty("result").GetProperty("fileCount").GetInt64();
            Assert.IsTrue(published>=0&&published<150,"Cancellation may publish completed files, and must omit unfinished work.");
        }
        await worker.SendAsync("resume", "archive.prepare", new PreparationRequest { Folders = ["Documents"] });
        using var resumed = await worker.ResponseAsync("resume");
        Assert.AreEqual(150, resumed.RootElement.GetProperty("result").GetProperty("fileCount").GetInt64());
    }

    [TestMethod]
    public async Task ConcurrentSearchRepliesKeepTheirRequestIdentityAndReaderWritesNothing()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "Documents", "cats.bin"), "only cats indoors");
        await File.WriteAllTextAsync(Path.Combine(_root, "Documents", "forest.bin"), "only forest outdoors");
        await using (var preparer = Start("preparer"))
        {
            await preparer.SendAsync("seed", "archive.prepare", new PreparationRequest { Folders = ["Documents"] });
            using var seeded = await preparer.ResponseAsync("seed");
            Assert.IsTrue(seeded.RootElement.TryGetProperty("result", out _), seeded.RootElement.ToString());
        }
        var before = await RecoveryTests.SnapshotAsync(_root);
        await using (var reader = Start("reader"))
        {
            await Task.WhenAll(
                reader.SendAsync("cats-search", "archive.search", new SearchRequest { Query = "cats", Filters = new() { ExactOnly = true } }),
                reader.SendAsync("forest-search", "archive.search", new SearchRequest { Query = "forest", Filters = new() { ExactOnly = true } }),
                reader.SendAsync("unknown", "not.a.real.method"));
            var ids = new HashSet<string>();
            for (var i = 0; i < 3; i++)
            {
                using var frame = await reader.ReadAsync();
                var id = frame.RootElement.GetProperty("id").GetString()!;
                Assert.IsTrue(ids.Add(id), "A request received duplicate responses.");
                if (id == "unknown") Assert.AreEqual(-32601, frame.RootElement.GetProperty("error").GetProperty("code").GetInt32());
                else Assert.AreEqual(id == "cats-search" ? "cats.bin" : "forest.bin", frame.RootElement.GetProperty("result").GetProperty("results")[0].GetProperty("name").GetString());
            }
            CollectionAssert.AreEquivalent(new[] { "cats-search", "forest-search", "unknown" }, ids.ToArray());
        }
        RecoveryTests.AssertSnapshotsEqual(before, await RecoveryTests.SnapshotAsync(_root));
    }

    [TestMethod]
    public async Task KilledReaderFailsPromptlyWithoutTakingDownItsClient()
    {
        FindExecutable(); // Explicitly skip if the worker has not been built.
        await using var backend = await WorkerArchiveClient.CreateAsync(_root);
        var client = (WorkerArchiveClient)backend;
        var diagnostics = await client.GetDiagnosticsAsync();
        using var ownedWorker = Process.GetProcessById(diagnostics.ProcessId);
        ownedWorker.Kill(entireProcessTree: true);
        await ownedWorker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        var failure = await Assert.ThrowsAsync<IOException>(async () => await client.GetStatusAsync().WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.IsFalse(string.IsNullOrWhiteSpace(failure.Message));
        Assert.IsTrue(Directory.Exists(_root));
    }

    private RawWorker Start(string role) => new(FindExecutable(), _root, role);
    private static string FindExecutable()
    {
        var configured = Environment.GetEnvironmentVariable("FINDIT_WORKER_PATH");
        if (configured is not null && File.Exists(configured)) return configured;
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
            foreach (var configuration in new[] { "Debug", "Release" })
            {
                var candidate = Path.Combine(folder.FullName, "src", "FindIt.Worker", "bin", configuration, "net10.0", "FindIt.Worker.exe");
                if (File.Exists(candidate)) return candidate;
            }
        Assert.Inconclusive("Build FindIt.Worker before running process integration tests.");
        throw new InvalidOperationException();
    }

    private sealed class RawWorker : IAsyncDisposable
    {
        private readonly Process _process;
        private readonly SemaphoreSlim _writes = new(1);
        private readonly CancellationTokenSource _timeout = new(TimeSpan.FromSeconds(40));
        private readonly Task<string> _errors;
        public RawWorker(string executable, string root, string role)
        {
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add("--root"); start.ArgumentList.Add(root); start.ArgumentList.Add("--role"); start.ArgumentList.Add(role);
            _process = Process.Start(start)!;
            _errors = _process.StandardError.ReadToEndAsync();
        }
        public Task SendAsync(string id, string method, object? parameters = null) => FramedTransport.WriteAsync(_process.StandardInput.BaseStream, new RpcRequest("2.0", id, method, parameters), _writes, _timeout.Token);
        public Task CancelAsync(string id) => FramedTransport.WriteAsync(_process.StandardInput.BaseStream, new RpcNotification("2.0", "rpc.cancel", new RpcCancellation(id)), _writes, _timeout.Token);
        public async Task<JsonDocument> ReadAsync() => await FramedTransport.ReadAsync(_process.StandardOutput.BaseStream, _timeout.Token) ?? throw new IOException("Worker closed its protocol stream.");
        public async Task<JsonDocument> ResponseAsync(string expectedId, List<PreparationProgress>? progress = null)
        {
            while (true)
            {
                var frame = await ReadAsync();
                if (frame.RootElement.TryGetProperty("method", out var method))
                {
                    Assert.AreEqual("preparation.progress", method.GetString());
                    var update = frame.RootElement.GetProperty("params").Deserialize<RpcProgress>(WireJson.Options)!;
                    Assert.AreEqual(expectedId, update.RequestId);
                    progress?.Add(update.Progress); frame.Dispose(); continue;
                }
                Assert.AreEqual(expectedId, frame.RootElement.GetProperty("id").GetString());
                return frame;
            }
        }
        public async ValueTask DisposeAsync()
        {
            _process.StandardInput.Close();
            try { await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (TimeoutException) { _process.Kill(entireProcessTree: true); await _process.WaitForExitAsync(); }
            await _errors;
            _process.Dispose(); _writes.Dispose(); _timeout.Dispose();
        }
    }
}
