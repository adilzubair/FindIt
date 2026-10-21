using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using FindIt.Shared;

namespace FindIt.RecoveryCheck;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        string? Option(string name) { var index = Array.IndexOf(args, name); return index >= 0 && index + 1 < args.Length ? args[index + 1] : null; }
        if (Option("--worker") is not { } worker || Option("--output") is not { } output)
        {
            Console.Error.WriteLine("Usage: FindIt.RecoveryCheck --worker <fault-enabled worker.exe> --output <workspace artifacts directory> [--production-worker <normal worker.exe>] [--filter <scenario name substring>]");
            return 2;
        }
        try { return await new RecoveryRun(worker, output, Option("--production-worker"), Option("--filter")).RunAsync(); }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}

internal sealed class RecoveryRun
{
    private readonly string worker, output, sandbox, nonce = Guid.NewGuid().ToString("N");
    private readonly string? productionWorker;
    private readonly string? filter;
    private readonly List<object> results = [];
    private readonly JsonSerializerOptions json = new(WireJson.Options) { WriteIndented = true };
    private int failures;
    private int caseSequence;
    private const string Old = "Amber fossil archive original alpha.";
    private const string New = "Violet forest archive updated alpha.";

    public RecoveryRun(string worker, string output, string? productionWorker, string? filter)
    {
        this.worker = Path.GetFullPath(worker); this.output = Path.GetFullPath(output);
        this.productionWorker = productionWorker is null ? null : Path.GetFullPath(productionWorker);
        this.filter = filter;
        if (!File.Exists(this.worker) || (this.productionWorker is not null && !File.Exists(this.productionWorker))) throw new FileNotFoundException("Build the validation and normal workers first.");
        // This tool creates its own disposable child only. It never accepts an archive root.
        var repository = FindRepository();
        if (!Within(Path.Combine(repository, "artifacts"), this.output)) throw new ArgumentException("Recovery output must be a child of this checkout's artifacts directory.");
        RejectLinks(repository, this.output);
        Directory.CreateDirectory(this.output);
        sandbox = Path.Combine(this.output, "FindIt-recovery-check-" + nonce);
        Directory.CreateDirectory(sandbox);
        File.WriteAllText(Path.Combine(sandbox, ".fault-test-nonce"), nonce);
    }

    public async Task<int> RunAsync()
    {
        var started = DateTimeOffset.UtcNow;
        foreach (var point in new[] { "file.segment-written", "file.committed", "publication.manifest-ready", "publication.generation-moved", "publication.pointer-ready" })
            await Check("kill-" + point, () => FaultAsync(point, "block", kill: true));
        foreach (var point in new[] { "space.preflight", "publication.manifest-ready", "publication.generation-moved", "publication.pointer-ready" })
            await Check("disk-full-" + point, () => FaultAsync(point, "disk-full", kill: false));
        await Check("disk-full-file.committed", () => FaultAsync("file.committed", "disk-full", kill: false));
        await Check("kill-intermediate-checkpoint-manifest", () => CheckpointKillAsync("publication.manifest-ready"));
        await Check("kill-intermediate-checkpoint-after-move", () => CheckpointKillAsync("publication.generation-moved"));
        await Check("kill-intermediate-checkpoint-shrink-before-resume", () => CheckpointKillAsync("publication.generation-moved", removeAdded: true));
        await Check("sealed-checkpoint-packs-immutable-on-resume", () => CheckpointKillAsync("publication.generation-moved", removeAdded: true, appendAfterKill: true));
        await Check("kill-after-valid-pointer-switch", AfterSwitchKillAsync);
        await Check("real-denied-pointer-replace", DeniedReplaceAsync);
        await Check("transient-pointer-sharing-retries", TransientReplaceAsync);
        await Check("corrupt-catalog-fallback", () => CorruptAsync("catalog.sqlite"));
        await Check("missing-vector-records-fallback", () => CorruptAsync("vector-index.bin"));
        await Check("truncated-manifest-fallback", () => CorruptAsync("manifest.json"));
        await Check("retain-previous-verified-generation-after-corruption", RetainValidPreviousAsync);
        await Check("torn-pointer-read-only-recovery", TornPointerAsync);
        await Check("same-size-same-time-change-and-delete", ChangedDeletedAsync);
        await Check("disconnect-reconnect-and-root-relocation", RelocationAsync);
        if (productionWorker is not null) await Check("production-build-cannot-activate-faults", ProductionGuardAsync);
        if (results.Count == 0) throw new ArgumentException("The scenario filter selected no recovery checks.");
        var report = new { startedUtc = started, completedUtc = DateTimeOffset.UtcNow, workerPath = worker, productionWorkerPath = productionWorker, sandboxPath = sandbox,
            scenarioFilter = filter, passed = results.Count - failures, failed = failures, results, limitations = new[] { "Generated local filesystem archives only; no removable drive was touched.", "Disk-full is a deterministic injected IOException at real preparation/publication boundaries; it does not fill a disk or emulate filesystem firmware.", "Hard kills target only this runner's blocked preparer process. This does not certify power-loss behavior, NTFS/exFAT physical removal, or full-volume read-only operation." } };
        await File.WriteAllTextAsync(Path.Combine(output, "recovery-report.json"), JsonSerializer.Serialize(report, json));
        Console.WriteLine($"Recovery checks: {results.Count - failures} passed, {failures} failed. Report: {Path.Combine(output, "recovery-report.json")}");
        return failures == 0 ? 0 : 1;
    }

    private async Task Check(string name, Func<Task<object>> test)
    {
        if (filter is not null && !name.Contains(filter, StringComparison.Ordinal)) return;
        var watch = Stopwatch.StartNew();
        Console.WriteLine($"RUN {name}"); Console.Out.Flush();
        try { var evidence = await test(); results.Add(new { name, passed = true, elapsedMs = watch.Elapsed.TotalMilliseconds, evidence }); Console.WriteLine($"PASS {name} ({watch.Elapsed.TotalSeconds:0.00}s)"); }
        catch (Exception error) { failures++; results.Add(new { name, passed = false, elapsedMs = watch.Elapsed.TotalMilliseconds, error = error.ToString() }); Console.WriteLine($"FAIL {name}: {error.Message}"); }
        await File.WriteAllTextAsync(Path.Combine(output, "recovery-progress.json"), JsonSerializer.Serialize(results, json));
        Console.Out.Flush();
    }

    private async Task<(string Root, ArchiveStatus Baseline)> SeedAsync(string name)
    {
        var root = Path.Combine(sandbox, $"case-{++caseSequence:D2}-{Guid.NewGuid():N}"[..18]);
        Directory.CreateDirectory(Path.Combine(root, "Documents"));
        await File.WriteAllTextAsync(Path.Combine(root, "Documents", "000-alpha.txt"), Old);
        await File.WriteAllTextAsync(Path.Combine(root, "Documents", "beta.txt"), "Silver bicycle archive stable beta.");
        await using var writer = Start(root, "preparer");
        var baseline = await writer.PrepareAsync();
        Require(baseline.FileCount == 2 && baseline.Issues.Count == 0, "Baseline must contain two fully prepared originals.");
        return (root, baseline);
    }

    private async Task<object> FaultAsync(string point, string mode, bool kill)
    {
        var (root, baseline) = await SeedAsync(point + "-" + mode);
        await File.WriteAllTextAsync(Path.Combine(root, "Documents", "000-alpha.txt"), New);
        var originals = await SnapshotAsync(Path.Combine(root, "Documents"));
        var pointer = await File.ReadAllBytesAsync(Path.Combine(Home(root), "current.json"));
        var oldGeneration = await SnapshotAsync(Generation(root, baseline.GenerationId));
        await using (var liveReader = Start(root, "reader"))
        await using (var broken = Start(root, "preparer", point, mode))
        {
            Require((await liveReader.StatusAsync()).GenerationId == baseline.GenerationId, "Live reader did not start on baseline.");
            await broken.SendPrepareAsync();
            await WaitForHitAsync(broken, point, root);
            Require((await liveReader.SearchAsync("Amber")).Results.Count == 1, "The already open reader lost its prior complete content while preparing.");
            if (kill) await broken.KillOwnedAsync();
            else
            {
                using var failed = await broken.ResponseAsync("prepare");
                Require(failed.RootElement.TryGetProperty("error", out var error) && error.GetProperty("message").GetString()!.Contains("disk-full", StringComparison.OrdinalIgnoreCase), "Injected disk-full did not return an actionable preparation failure.");
            }
        }
        var pointerAfterFault = await File.ReadAllBytesAsync(Path.Combine(Home(root), "current.json"));
        Require(pointer.SequenceEqual(pointerAfterFault), "Fault replaced the committed generation pointer.");
        Equal(oldGeneration, await SnapshotAsync(Generation(root, baseline.GenerationId)), "Previous generation changed.");
        await using (var reader = Start(root, "reader")) await AssertBaselineAsync(reader, baseline);
        ArchiveStatus resumed;
        List<PreparationProgress> progress;
        await using (var retry = Start(root, "preparer")) { resumed = await retry.PrepareAsync(); progress = retry.Progress.ToList(); }
        Require(resumed.FileCount == 2 && resumed.Issues.Count == 0, "Retry did not finish the complete library.");
        await using (var reader = Start(root, "reader"))
        {
            Require((await reader.SearchAsync("Violet")).Results.Count == 1 && (await reader.SearchAsync("Amber")).Results.Count == 0, "Retry served stale or missing updated contents.");
        }
        Equal(originals, await SnapshotAsync(Path.Combine(root, "Documents")), "Preparation modified an original.");
        AssertNoAbandonedWork(root);
        if (point == "file.committed") Require(progress.Any(p => p.Unchanged == 2), "Killed worker's committed staging files were not reused on resume.");
        return new { root, point, mode, baselineGeneration = baseline.GenerationId, resumedGeneration = resumed.GenerationId, originalsSha256Unchanged = true, maxReused = progress.Select(p => p.Unchanged).DefaultIfEmpty().Max(), committedPointerUnchangedDuringFault = true, previousGenerationBytesUnchanged = true };
    }

    private async Task<object> DeniedReplaceAsync()
    {
        var (root, baseline) = await SeedAsync("denied-pointer");
        await File.WriteAllTextAsync(Path.Combine(root, "Documents", "000-alpha.txt"), New);
        var originals = await SnapshotAsync(Path.Combine(root, "Documents"));
        var pointerPath = Path.Combine(Home(root), "current.json");
        var pointer = await File.ReadAllBytesAsync(pointerPath);
        await using (var writer = Start(root, "preparer"))
        using (var denyReplace = new FileStream(pointerPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            await writer.SendPrepareAsync();
            using var failed = await writer.ResponseAsync("prepare");
            Require(failed.RootElement.TryGetProperty("error", out _), "An OS-denied pointer replacement unexpectedly succeeded.");
        }
        var pointerAfterFailure = await File.ReadAllBytesAsync(pointerPath);
        Require(pointer.SequenceEqual(pointerAfterFailure), "Denied replacement altered the pointer.");
        Require(!Directory.EnumerateFiles(Home(root), "current.json.new-*").Any(), "Failed atomic replacement leaked its temporary file.");
        await using (var reader = Start(root, "reader")) await AssertBaselineAsync(reader, baseline);
        await using (var resumed = Start(root, "preparer")) Require((await resumed.PrepareAsync()).Issues.Count == 0, "Retry after denied replace failed.");
        Equal(originals, await SnapshotAsync(Path.Combine(root, "Documents")), "Denied replacement changed originals.");
        AssertNoAbandonedWork(root);
        return new { root, actualOsWriteFailure = true, pointerUnchanged = true, retrySucceeded = true, originalsSha256Unchanged = true };
    }

    private async Task<object> CheckpointKillAsync(string point, bool removeAdded = false, bool appendAfterKill = false)
    {
        var (root, baseline) = await SeedAsync("checkpoint-kill");
        if (appendAfterKill) await File.WriteAllTextAsync(Path.Combine(root, "Documents", "000-alpha.txt"), New);
        for (int i = 0; i < 100; i++) await File.WriteAllTextAsync(Path.Combine(root, "Documents", $"added-{i:D3}.bin"), $"Runner-owned filename-only original {i}.");
        var originals = await SnapshotAsync(Path.Combine(root, "Documents"));
        var pointer = await File.ReadAllBytesAsync(Path.Combine(Home(root), "current.json"));
        await using (var writer = Start(root, "preparer", point, "block"))
        {
            await writer.SendPrepareAsync();
            // Drain progress while waiting for the marker: a sufficiently large job
            // otherwise fills redirected stdout before reaching the checkpoint.
            var response = writer.ResponseAsync("prepare");
            try
            {
                await WaitForHitAsync(writer, point, root, response);
                bool checkpointExists = Directory.Exists(Path.Combine(Home(root), "checkpoints")) && Directory.EnumerateDirectories(Path.Combine(Home(root), "checkpoints"), "snapshot-*").Any();
                Require(point != "publication.manifest-ready" || checkpointExists, "Fault did not interrupt an intermediate checkpoint publication.");
                Require(Directory.Exists(Path.Combine(Home(root), "staging")), "Intermediate checkpoint unexpectedly moved resumable staging.");
                await writer.KillOwnedAsync();
            }
            finally
            {
                // The owned kill closes its IPC stream. Observe the pending read so
                // no background protocol task survives this test case.
                if (writer.HasExited)
                {
                    try { using var ignored = await response; }
                    catch (IOException) { }
                }
            }
        }
        var pointerAfterKill = await File.ReadAllBytesAsync(Path.Combine(Home(root), "current.json"));
        Require(pointer.SequenceEqual(pointerAfterKill), "Intermediate checkpoint fault changed the committed pointer.");
        await using (var reader = Start(root, "reader")) await AssertBaselineAsync(reader, baseline);
        Equal(originals, await SnapshotAsync(Path.Combine(root, "Documents")), "Killed preparer changed originals.");
        Dictionary<string, string>? sealedPacks = null;
        if (appendAfterKill)
        {
            sealedPacks = await SnapshotPublishedPacksAsync(root, baseline.GenerationId);
            Require(sealedPacks.Count > 0, "The pack immutability check requires local embedding models and a referenced prepared vector pack.");
            using (var state = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(Home(root), "staging", "state.json"))))
            {
                var sealedVectorPack = state.RootElement.GetProperty("vectorPack").GetString()!;
                Require(sealedPacks.ContainsKey(sealedVectorPack) && new FileInfo(Path.Combine(root, sealedVectorPack)).Length > 0,
                    "The interrupted checkpoint must already reference the staged writer's nonempty vector pack.");
            }
            await File.WriteAllTextAsync(Path.Combine(root, "Documents", "gamma.txt"), "Emerald mountain archive appended gamma.");
            originals = await SnapshotAsync(Path.Combine(root, "Documents"));
        }
        if (removeAdded)
        {
            // A user may remove originals before resuming an interrupted job. Delete
            // only the exact runner-created fixtures, never enumerated user files.
            for (int i = 0; i < 100; i++) File.Delete(Path.Combine(root, "Documents", $"added-{i:D3}.bin"));
            originals = await SnapshotAsync(Path.Combine(root, "Documents"));
        }
        ArchiveStatus status; long reused;
        await using (var resumed = Start(root, "preparer")) { status = await resumed.PrepareAsync(); reused = resumed.Progress.Select(p => p.Unchanged).DefaultIfEmpty().Max(); }
        Require(status.FileCount == 2 + (removeAdded ? 0 : 100) + (appendAfterKill ? 1 : 0) && status.Issues.Count == 0, "Intermediate checkpoint could not resume to the current complete library.");
        Require(reused >= (removeAdded ? 2 : 100), "Intermediate checkpoint discarded committed staging files.");
        await using (var reader = Start(root, "reader")) Require((await reader.SearchAsync("added")).Results.Count == (removeAdded ? 0 : 100), "Resumed checkpoint's searchable originals do not match current sources.");
        Equal(originals, await SnapshotAsync(Path.Combine(root, "Documents")), "Checkpoint recovery changed originals.");
        if (sealedPacks is not null)
        {
            var after = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var relative in sealedPacks.Keys) after[relative] = await FileFingerprintAsync(Path.Combine(root, relative));
            Equal(sealedPacks, after, "Resume appended to a pack already referenced by a complete immutable checkpoint generation.");
            await using var reader = Start(root, "reader");
            Require((await reader.SearchAsync("Violet")).Results.Count == 1 && (await reader.SearchAsync("Emerald")).Results.Count == 1, "Rotating sealed packs lost reused or newly appended text contents.");
        }
        AssertNoAbandonedWork(root);
        return new { root, point, removedRunnerFixturesBeforeResume = removeAdded, appendedRunnerFixtureAfterKill = appendAfterKill, sealedSharedPacksCompared = sealedPacks?.Count, baselineGeneration = baseline.GenerationId, resumedGeneration = status.GenerationId, status.FileCount, reused, originalsSha256Unchanged = true, abandonedCheckpointReclaimed = true };
    }

    private async Task<object> AfterSwitchKillAsync()
    {
        var (root, baseline) = await SeedAsync("after-pointer-switch");
        await File.WriteAllTextAsync(Path.Combine(root, "Documents", "000-alpha.txt"), New);
        var originals = await SnapshotAsync(Path.Combine(root, "Documents"));
        var previous = await SnapshotAsync(Generation(root, baseline.GenerationId));
        await using (var writer = Start(root, "preparer", "publication.pointer-switched", "block"))
        {
            await writer.SendPrepareAsync(); await WaitForHitAsync(writer, "publication.pointer-switched", root); await writer.KillOwnedAsync();
        }
        ArchiveStatus current;
        await using (var reader = Start(root, "reader"))
        {
            current = await reader.StatusAsync();
            Require(current.GenerationId > baseline.GenerationId && current.LibraryId == baseline.LibraryId && current.FileCount == 2, "A valid pointer switch did not recover the complete new generation.");
            Require((await reader.SearchAsync("Violet")).Results.Count == 1 && (await reader.SearchAsync("Amber")).Results.Count == 0, "Post-switch recovery returned mixed generations.");
        }
        Equal(previous, await SnapshotAsync(Generation(root, baseline.GenerationId)), "Post-switch kill damaged the previous complete generation.");
        await using (var resumed = Start(root, "preparer")) Require((await resumed.PrepareAsync()).Issues.Count == 0, "Preparation could not resume after an already valid publication.");
        Equal(originals, await SnapshotAsync(Path.Combine(root, "Documents")), "Post-switch kill changed originals.");
        AssertNoAbandonedWork(root);
        return new { root, previousGeneration = baseline.GenerationId, recoveredGeneration = current.GenerationId, completeNewGenerationAccepted = true, previousGenerationBytesUnchanged = true, originalsSha256Unchanged = true };
    }

    private async Task<object> CorruptAsync(string artifact)
    {
        var (root, baseline) = await SeedAsync("corrupt-" + artifact);
        await File.WriteAllTextAsync(Path.Combine(root, "Documents", "000-alpha.txt"), New);
        ArchiveStatus latest;
        await using (var writer = Start(root, "preparer")) latest = await writer.PrepareAsync();
        var path = Path.Combine(Generation(root, latest.GenerationId), artifact);
        if (artifact == "vector-index.bin") File.Delete(path); else await File.WriteAllBytesAsync(path, [0x7b, 0x22, 0x62]);
        var before = await SnapshotAsync(root);
        await using (var reader = Start(root, "reader")) await AssertBaselineAsync(reader, baseline);
        Equal(before, await SnapshotAsync(root), "Recovery reader modified archive/source files.");
        return new { root, artifact, fallbackGeneration = baseline.GenerationId, rejectedGeneration = latest.GenerationId, readerWroteNothing = true };
    }

    private async Task<object> TransientReplaceAsync()
    {
        var (root, baseline) = await SeedAsync("transient-sharing");
        await File.WriteAllTextAsync(Path.Combine(root, "Documents", "000-alpha.txt"), New);
        var originals = await SnapshotAsync(Path.Combine(root, "Documents"));
        await using var writer = Start(root, "preparer", "publication.pointer-ready", "block");
        await writer.SendPrepareAsync();
        await WaitForHitAsync(writer, "publication.pointer-ready", root);
        var pointerPath = Path.Combine(Home(root), "current.json");
        using (var denyReplace = new FileStream(pointerPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            await File.WriteAllTextAsync(Path.Combine(sandbox, $"release-{writer.Id}"), "continue");
            await Task.Delay(200);
            Require(Directory.EnumerateFiles(Home(root), "current.json.new-*").Any(), "Pointer operation did not remain pending during the sharing conflict.");
        }
        using var response = await writer.ResponseAsync("prepare");
        Require(response.RootElement.TryGetProperty("result", out _), "Publication did not retry a transient sharing conflict.");
        await using (var reader = Start(root, "reader")) Require((await reader.SearchAsync("Violet")).Results.Count == 1, "Retried publication did not publish complete new content.");
        Equal(originals, await SnapshotAsync(Path.Combine(root, "Documents")), "Transient sharing retry changed originals.");
        AssertNoAbandonedWork(root);
        return new { root, heldPointerReplaceConflictMs = 200, previousGeneration = baseline.GenerationId, retrySucceeded = true, originalsSha256Unchanged = true };
    }

    private async Task<object> RetainValidPreviousAsync()
    {
        var (root, baseline) = await SeedAsync("retain-valid-previous");
        var baselineBytes = await SnapshotAsync(Generation(root, baseline.GenerationId));
        await File.WriteAllTextAsync(Path.Combine(root, "Documents", "000-alpha.txt"), New);
        ArchiveStatus damaged;
        await using (var writer = Start(root, "preparer")) damaged = await writer.PrepareAsync();
        await File.WriteAllBytesAsync(Path.Combine(Generation(root, damaged.GenerationId), "catalog.sqlite"), [1, 2, 3]);
        await using (var fallback = Start(root, "reader")) await AssertBaselineAsync(fallback, baseline);
        await File.WriteAllTextAsync(Path.Combine(root, "Documents", "000-alpha.txt"), "Coral river archive replacement alpha.");
        var originals = await SnapshotAsync(Path.Combine(root, "Documents"));
        ArchiveStatus published;
        await using (var writer = Start(root, "preparer")) published = await writer.PrepareAsync();
        Require(published.GenerationId > damaged.GenerationId && published.Issues.Count == 0, "Preparation after catalog fallback did not publish a fresh complete generation.");
        Require(Directory.Exists(Generation(root, baseline.GenerationId)), "Collection deleted the previous verified generation in favor of a corrupt newer manifest.");
        Equal(baselineBytes, await SnapshotAsync(Generation(root, baseline.GenerationId)), "Collection changed the previous verified generation.");
        await File.WriteAllBytesAsync(Path.Combine(Generation(root, published.GenerationId), "catalog.sqlite"), [4, 5, 6]);
        var beforeRead = await SnapshotAsync(root);
        await using (var fallback = Start(root, "reader")) await AssertBaselineAsync(fallback, baseline);
        Equal(beforeRead, await SnapshotAsync(root), "Fallback recovery wrote archive repairs.");
        Equal(originals, await SnapshotAsync(Path.Combine(root, "Documents")), "Recovery/publication changed originals.");
        return new { root, previousVerifiedGeneration = baseline.GenerationId, rejectedGeneration = damaged.GenerationId, publishedGeneration = published.GenerationId, secondCorruptionFallbackSucceeded = true, previousGenerationBytesUnchanged = true, originalsSha256Unchanged = true };
    }

    private async Task<object> TornPointerAsync()
    {
        var (root, baseline) = await SeedAsync("torn-pointer");
        await File.WriteAllTextAsync(Path.Combine(Home(root), "current.json"), "{\"generationId\":");
        var before = await SnapshotAsync(root);
        await using (var reader = Start(root, "reader")) await AssertBaselineAsync(reader, baseline);
        Equal(before, await SnapshotAsync(root), "Torn-pointer recovery wrote repairs to the drive.");
        return new { root, recoveredGeneration = baseline.GenerationId, readerWroteNothing = true };
    }

    private async Task<object> ChangedDeletedAsync()
    {
        var (root, baseline) = await SeedAsync("changed-deleted");
        var alpha = Path.Combine(root, "Documents", "000-alpha.txt");
        var modified = File.GetLastWriteTimeUtc(alpha);
        // Equal-length replacement deliberately preserves mtime: Verify contents must detect bytes.
        var changed = Old.Replace("Amber", "Coral", StringComparison.Ordinal);
        await File.WriteAllTextAsync(alpha, changed); File.SetLastWriteTimeUtc(alpha, modified);
        File.Delete(Path.Combine(root, "Documents", "beta.txt"));
        var originals = await SnapshotAsync(Path.Combine(root, "Documents"));
        ArchiveStatus status;
        await using (var writer = Start(root, "preparer")) status = await writer.PrepareAsync();
        Require(status.FileCount == 1 && status.Issues.Count == 0, "Delete reconciliation kept a removed original.");
        await using (var reader = Start(root, "reader"))
        {
            Require((await reader.SearchAsync("Coral")).Results.Count == 1, "Strong verification missed the same-size same-time change.");
            Require((await reader.SearchAsync("Amber")).Results.Count == 0 && (await reader.SearchAsync("Silver")).Results.Count == 0, "Stale original contents remained searchable.");
        }
        Equal(originals, await SnapshotAsync(Path.Combine(root, "Documents")), "Reconciliation changed originals.");
        return new { root, baselineGeneration = baseline.GenerationId, updatedGeneration = status.GenerationId, fileCount = status.FileCount, sameSizeSameTimestampDetected = true, deletedContentRemoved = true };
    }

    private async Task<object> RelocationAsync()
    {
        var (root, baseline) = await SeedAsync("relocation");
        var relocated = root + "-relocated";
        var before = await SnapshotAsync(root);
        await using (var oldReader = Start(root, "reader"))
        {
            await oldReader.StatusAsync();
            // No physical drive action: move only this runner's generated directory.
            Directory.Move(root, relocated);
            try
            {
                using var unavailable = await oldReader.CallAsync("archive.status", null);
                Require(unavailable.RootElement.TryGetProperty("error", out _), "Disconnected root was presented as an available, unprepared archive.");
                await using var newReader = Start(relocated, "reader");
                var status = await newReader.StatusAsync();
                Require(status.LibraryId == baseline.LibraryId && status.GenerationId == baseline.GenerationId, "Relocation lost portable library identity.");
                var found = (await newReader.SearchAsync("Amber")).Results.Single();
                var preview = await newReader.PreviewAsync(found.FileId);
                Require(Within(relocated, preview.OriginalPath) && File.Exists(preview.OriginalPath), "Preview retained the old absolute source path.");
            }
            finally { Directory.Move(relocated, root); }
            await AssertBaselineAsync(oldReader, baseline);
        }
        Equal(before, await SnapshotAsync(root), "Relocation/reconnection reader changed drive files.");
        return new { root, sameLibraryId = true, previewUsesNewRoot = true, reconnectSucceeded = true, readerWroteNothing = true, physicalDriveLetterChangeTested = false };
    }

    private async Task<object> ProductionGuardAsync()
    {
        var (root, _) = await SeedAsync("production-guard");
        await File.WriteAllTextAsync(Path.Combine(root, "Documents", "000-alpha.txt"), New);
        await using var normal = new RawWorker(productionWorker!, root, "preparer", FaultEnvironment("publication.pointer-ready", "disk-full"));
        var status = await normal.PrepareAsync();
        Require(status.FileCount == 2 && !File.Exists(HitPath(normal.Id)), "Normal build contains active fault injection.");
        return new { root, normalExecutable = productionWorker, faultEnvironmentIgnored = true };
    }

    private RawWorker Start(string root, string role, string? point = null, string? mode = null) => new(worker, root, role, point is null ? null : FaultEnvironment(point, mode!));
    private Dictionary<string, string> FaultEnvironment(string point, string mode) => new() { ["FINDIT_FAULT_SANDBOX"] = sandbox, ["FINDIT_FAULT_NONCE"] = nonce, ["FINDIT_FAULT_POINT"] = point, ["FINDIT_FAULT_MODE"] = mode };
    private string HitPath(int id) => Path.Combine(sandbox, $"hit-{id}.json");
    private async Task WaitForHitAsync(RawWorker process, string point, string root, Task<JsonDocument>? response = null)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        while (true)
        {
            if (File.Exists(HitPath(process.Id)))
            {
                try
                {
                    await using var stream = new FileStream(HitPath(process.Id), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var hit = await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token);
                    Require(hit.RootElement.GetProperty("point").GetString() == point && hit.RootElement.GetProperty("processId").GetInt32() == process.Id && hit.RootElement.GetProperty("archiveRoot").GetString() == root, "Fault boundary marker did not identify the owned worker/root.");
                    return;
                }
                catch (IOException) when (!timeout.IsCancellationRequested) { }
            }
            else if (process.HasExited) throw new IOException("Validation worker exited before the requested fault boundary.");
            else if (response?.IsCompleted == true)
            {
                using var early = await response;
                throw new IOException("Preparation replied before the requested fault boundary: " + early.RootElement);
            }
            await Task.Delay(25, timeout.Token);
        }
    }
    private static async Task AssertBaselineAsync(RawWorker reader, ArchiveStatus baseline)
    {
        var status = await reader.StatusAsync();
        Require(status.LibraryId == baseline.LibraryId && status.GenerationId == baseline.GenerationId && status.FileCount == 2, "Previous complete generation is not available.");
        Require((await reader.SearchAsync("Amber")).Results.Count == 1 && (await reader.SearchAsync("Violet")).Results.Count == 0, "Previous complete search results changed.");
    }
    private static void AssertNoAbandonedWork(string root)
    {
        var checkpoints = Path.Combine(Home(root), "checkpoints");
        Require(!Directory.Exists(checkpoints) || !Directory.EnumerateDirectories(checkpoints, "snapshot-*").Any(), "Retry leaked abandoned checkpoint folders.");
        Require(!Directory.EnumerateFiles(Home(root), "current.json.new-*").Any(), "Retry leaked abandoned pointer temporaries.");
        Require(!Directory.EnumerateDirectories(Home(root), "interrupted-*").Any(), "Retry leaked abandoned staging folders.");
        var scratch = Path.Combine(Home(root), "work", "runtime");
        Require(!Directory.Exists(scratch) || !Directory.EnumerateDirectories(scratch).Any(), "Retry did not reclaim killed worker scratch.");
    }
    private static string Home(string root) => Path.Combine(root, "FindIt", "archive");
    private static string Generation(string root, long id) => Path.Combine(Home(root), "generations", id.ToString("D20"));
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
    private static bool Within(string parent, string child) => Path.GetFullPath(child).StartsWith(Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private static async Task<Dictionary<string, string>> SnapshotAsync(string root)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var info = new FileInfo(file); await using var stream = File.OpenRead(file);
            result[Path.GetRelativePath(root, file)] = $"{info.Length}:{info.LastWriteTimeUtc.Ticks}:{Convert.ToHexString(await SHA256.HashDataAsync(stream))}";
        }
        return result;
    }
    private static async Task<Dictionary<string, string>> SnapshotPublishedPacksAsync(string root, long afterGeneration)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var generation in Directory.EnumerateDirectories(Path.Combine(Home(root), "generations")))
        {
            if (!long.TryParse(Path.GetFileName(generation), out var id) || id <= afterGeneration) continue;
            using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(generation, "manifest.json")));
            foreach (var artifact in manifest.RootElement.GetProperty("artifacts").EnumerateArray())
            {
                var relative = artifact.GetProperty("path").GetString()!;
                if (!relative.EndsWith(".pack", StringComparison.OrdinalIgnoreCase)) continue;
                var path = Path.GetFullPath(Path.Combine(root, relative));
                Require(Within(root, path), "Generated manifest pack escaped its sandbox archive.");
                result[relative] = await FileFingerprintAsync(path);
            }
        }
        return result;
    }
    private static async Task<string> FileFingerprintAsync(string path)
    {
        var info = new FileInfo(path); await using var stream = File.OpenRead(path);
        return $"{info.Length}:{info.LastWriteTimeUtc.Ticks}:{Convert.ToHexString(await SHA256.HashDataAsync(stream))}";
    }
    private static void Equal(Dictionary<string, string> before, Dictionary<string, string> after, string message) => Require(before.Count == after.Count && before.All(p => after.TryGetValue(p.Key, out var value) && p.Value == value), message);
    private static string FindRepository()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "FindIt.slnx"))) return directory.FullName;
        throw new DirectoryNotFoundException("Run this tool from its FindIt checkout build outputs.");
    }
    private static void RejectLinks(string parent, string destination)
    {
        var current = Path.GetFullPath(parent);
        foreach (var component in Path.GetRelativePath(current, destination).Split(Path.DirectorySeparatorChar))
        {
            current = Path.Combine(current, component);
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("Validation output cannot traverse links.");
        }
    }
}

internal sealed class RawWorker : IAsyncDisposable
{
    private readonly Process process;
    private readonly SemaphoreSlim writes = new(1);
    private readonly CancellationTokenSource budget = new(TimeSpan.FromMinutes(3));
    private readonly Task<string> stderr;
    public int Id => process.Id;
    public bool HasExited => process.HasExited;
    public List<PreparationProgress> Progress { get; } = [];
    public RawWorker(string executable, string root, string role, Dictionary<string, string>? faultEnvironment)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("--root"); start.ArgumentList.Add(root); start.ArgumentList.Add("--role"); start.ArgumentList.Add(role);
        foreach (var key in new[] { "FINDIT_FAULT_SANDBOX", "FINDIT_FAULT_NONCE", "FINDIT_FAULT_POINT", "FINDIT_FAULT_MODE" }) start.Environment.Remove(key);
        if (faultEnvironment is not null) foreach (var pair in faultEnvironment) start.Environment[pair.Key] = pair.Value;
        process = Process.Start(start) ?? throw new IOException("Could not start validation worker.");
        stderr = process.StandardError.ReadToEndAsync();
    }
    public Task SendPrepareAsync() => SendAsync("prepare", "archive.prepare", new PreparationRequest { Folders = ["Documents"], VerifyContents = true });
    public async Task<ArchiveStatus> PrepareAsync() { await SendPrepareAsync(); using var response = await ResponseAsync("prepare"); return Result<ArchiveStatus>(response); }
    public async Task<ArchiveStatus> StatusAsync() { using var response = await CallAsync("archive.status", null); return Result<ArchiveStatus>(response); }
    public async Task<SearchResponse> SearchAsync(string query) { using var response = await CallAsync("archive.search", new SearchRequest { Query = query, Filters = new() { ExactOnly = true } }); return Result<SearchResponse>(response); }
    public async Task<PreviewResult> PreviewAsync(Guid fileId) { using var response = await CallAsync("archive.preview", new PreviewRequest(fileId)); return Result<PreviewResult>(response); }
    public async Task<JsonDocument> CallAsync(string method, object? parameters)
    {
        var id = Guid.NewGuid().ToString("N"); await SendAsync(id, method, parameters); return await ResponseAsync(id);
    }
    private Task SendAsync(string id, string method, object? parameters) => FramedTransport.WriteAsync(process.StandardInput.BaseStream, new RpcRequest("2.0", id, method, parameters), writes, budget.Token);
    public async Task<JsonDocument> ResponseAsync(string id)
    {
        while (true)
        {
            var frame = await FramedTransport.ReadAsync(process.StandardOutput.BaseStream, budget.Token) ?? throw new IOException("Validation worker exited: " + await stderr);
            if (frame.RootElement.TryGetProperty("method", out _))
            {
                Progress.Add(frame.RootElement.GetProperty("params").Deserialize<RpcProgress>(WireJson.Options)!.Progress); frame.Dispose(); continue;
            }
            if (frame.RootElement.GetProperty("id").GetString() != id) { frame.Dispose(); throw new IOException("Worker response request identity changed."); }
            return frame;
        }
    }
    private static T Result<T>(JsonDocument response)
    {
        if (response.RootElement.TryGetProperty("error", out var error)) throw new IOException(error.GetProperty("message").GetString());
        return response.RootElement.GetProperty("result").Deserialize<T>(WireJson.Options) ?? throw new InvalidDataException("Worker omitted result.");
    }
    public async Task KillOwnedAsync()
    {
        if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
    }
    public async ValueTask DisposeAsync()
    {
        if (!process.HasExited)
        {
            process.StandardInput.Close();
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (TimeoutException) { await KillOwnedAsync(); }
        }
        await stderr; process.Dispose(); writes.Dispose(); budget.Dispose();
    }
}
