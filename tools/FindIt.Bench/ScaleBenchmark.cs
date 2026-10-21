using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using FindIt.Runtime;

namespace FindIt.Bench;

public static class ScaleBenchmark
{
    internal static string? Option(string[] args, string name) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }

    public static async Task<int> RunAsync(string[] args)
    {
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += cancel;
        string? directory = null;
        try
        {
            int count = int.Parse(Option(args, "--ann-records") ?? throw new ArgumentException("Missing --ann-records."));
            int seed = int.Parse(Option(args, "--seed") ?? "42");
            int queries = int.Parse(Option(args, "--query-count") ?? "64");
            string distribution = Option(args, "--distribution") ?? "isotropic-512";
            string mode = Option(args, "--index-mode") ?? "both";
            int candidateCount = int.Parse(Option(args, "--candidate-count") ?? "512");
            int buildConcurrency = int.Parse(Option(args,"--build-concurrency")??Math.Min(8,Environment.ProcessorCount).ToString());
            if (count < 100 || count > 2_000_000 || queries < 4 || queries > 1024 || !ScaleAlgorithms.Distributions.Contains(distribution) || !new[] { "mapped", "loaded", "both" }.Contains(mode))
                throw new ArgumentException("Use 100–2,000,000 records, 4–1024 queries, distribution isotropic-512/clustered-512, mode mapped/loaded/both.");
            if (candidateCount < 50 || candidateCount > 16384) throw new ArgumentException("Use 50–16384 --candidate-count; default 512 matches the reader's minimum semantic candidate request.");
            if(buildConcurrency<1||buildConcurrency>8)throw new ArgumentException("Use 1–8 --build-concurrency; default is min(8,logical processors).");
            directory = ScaleArtifacts.DatasetDirectory(Option(args, "--root") ?? "artifacts/scale", count, seed, distribution);
            string marker = Path.Combine(directory, "dataset.json");
            if (!File.Exists(marker)) ScaleArtifacts.AtomicWrite(marker, new ScaleDataset(1, ScaleArtifacts.Owner, count, seed, distribution, ScaleAlgorithms.Dimensions));
            var state = ScaleArtifacts.Validate(directory);
            if (state.Count != count || state.Seed != seed || state.Distribution != distribution) throw new InvalidDataException("Dataset directory identity mismatch.");
            ScaleArtifacts.RejectLinkedFile(Path.Combine(directory, ".benchmark.lock"));
            await using var lease = new FileStream(Path.Combine(directory, ".benchmark.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            state = EnsureDataset(directory, state, cancellation.Token);
            var index = ScaleNativeBuilder.Build(directory,state,cancellation.Token,buildConcurrency:buildConcurrency);
            int querySeed = seed;
            var truth = EnsureTruth(directory, state, queries, querySeed, cancellation.Token);
            var measurements = new List<JsonElement>();
            foreach (string access in mode == "both" ? new[] { "mapped", "loaded" } : [mode])
                measurements.Add(await RunMeasurementAsync(directory, access, queries, candidateCount, cancellation.Token));
            bool passed = measurements.All(x => x.GetProperty("recallGatePassed").GetBoolean());
            var report = new
            {
                kind = "synthetic-ann-scale-validation", schema = 1, measuredUtc = DateTimeOffset.UtcNow,
                dataset = state, directory, queryCount = queries, querySeed, k = ScaleAlgorithms.TopK, candidateCount,
                independentQueries = true, productionSettings = new { metric = "cosine", storedScalar = "fp16", connectivity = 16, expansionAdd = 128, expansionSearch = 128 },
                reference = new { method = "Exhaustive cosine over every eligible canonical FP16 row; independent FP16-rounded queries; SIMD float products with double accumulation.", scannedRows = truth.ScannedRows, elapsedMs = truth.ElapsedMs },
                index,
                construction = new { requestedConcurrency=buildConcurrency, actualIndexConcurrency=index.BuildConcurrency, actualIndexBatchRows=index.BuildBatchRows,
                    scope="Benchmark construction only. Production preparation still inserts serially. Parallel graph insertion order is nondeterministic, so recall must be measured for each built graph; older completed serial graphs remain reusable and retain concurrency=1." },
                storage = new { canonicalBytes = new FileInfo(Path.Combine(directory, "vectors.f16")).Length, indexBytes = index.Bytes, truthBytes = new FileInfo(TruthPath(directory, queries)).Length },
                recallGate = new { minimumMeanRecallAt50 = .95, passed, appliesTo = "Each unfiltered, 10% filter, and 1% filter scenario in each access mode." },
                scope = "Synthetic USearch retrieval only. This does not certify text encoding, semantic usefulness, full reader/UI memory, physical drive performance, 8 GB hardware, or a clean offline Windows machine.",
                osCache = "Uncontrolled. Separate fresh child processes exclude managed build/truth memory, but dataset hashing/building and previous runs can warm the Windows filesystem cache. These are process-cold measurements, not disk-cold certification.",
                resumption = "Canonical vectors, native graphs (every 50,000 inserted rows), and exact-reference progress resume from flushed checkpoints. Native resumption verifies graph SHA-256, count, dimensions, dataset, DLL, and build settings; it loads a private writable copy and preserves current/previous snapshots. Completed native indexes remain checksum-verified and reusable.",
                measurements
            };
            string output = Path.GetFullPath(Option(args, "--output") ?? Path.Combine(directory, $"report-q{queries}-{DateTime.UtcNow:yyyyMMddTHHmmssfff}.json"));
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            ScaleArtifacts.AtomicWrite(output, report);
            Console.WriteLine(JsonSerializer.Serialize(report, ScaleArtifacts.Json));
            Console.Error.WriteLine($"Completed. Recall@50 gate {(passed ? "PASS" : "FAIL")}; report: {output}");
            return passed ? 0 : 3;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine($"Cancelled safely. Resume with the same arguments; artifact directory: {directory}");
            return 130;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { Console.CancelKeyPress -= cancel; }
    }

    public static ScaleDataset EnsureDataset(string directory, ScaleDataset state, CancellationToken token)
    {
        string path = Path.Combine(directory, "vectors.f16");
        ScaleArtifacts.RejectLinkedFile(path);
        long committedBytes = (long)state.CommittedRows * ScaleAlgorithms.BytesPerVector;
        if (!File.Exists(path) && committedBytes != 0) throw new InvalidDataException("A committed canonical dataset is missing.");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var file = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
        if (file.Length < committedBytes) throw new InvalidDataException("Canonical dataset is shorter than its durable checkpoint.");
        var block = new byte[1 << 20];
        long remaining = committedBytes;
        while (remaining > 0)
        {
            token.ThrowIfCancellationRequested();
            int take = (int)Math.Min(block.Length, remaining); file.ReadExactly(block.AsSpan(0, take)); hash.AppendData(block.AsSpan(0, take)); remaining -= take;
        }
        string prefixHash = Convert.ToHexString(hash.GetCurrentHash()).ToLowerInvariant();
        if (state.CommittedRows > 0 && prefixHash != state.Sha256) throw new InvalidDataException("Canonical dataset checksum mismatch; refusing to reuse corrupt artifacts.");
        if (state.CommittedRows == state.Count)
        {
            if (file.Length != committedBytes) throw new InvalidDataException("A completed canonical dataset has unexpected trailing bytes.");
            Console.Error.WriteLine($"Reusing verified canonical dataset ({state.Count:N0} rows)."); return state;
        }
        file.SetLength(committedBytes); // Only an owned uncommitted tail may be discarded.
        file.Position = committedBytes;
        const int rowsPerCheckpoint = 8192;
        var bytes = new byte[rowsPerCheckpoint * ScaleAlgorithms.BytesPerVector];
        for (int row = state.CommittedRows; row < state.Count;)
        {
            token.ThrowIfCancellationRequested();
            int rows = Math.Min(rowsPerCheckpoint, state.Count - row);
            for (int i = 0; i < rows; i++)
            {
                if ((i & 255) == 0) token.ThrowIfCancellationRequested();
                ScaleAlgorithms.Encode(ScaleAlgorithms.Vector(state.Seed, (ulong)(row + i), state.Distribution), bytes.AsSpan(i * ScaleAlgorithms.BytesPerVector, ScaleAlgorithms.BytesPerVector));
            }
            int length = rows * ScaleAlgorithms.BytesPerVector;
            file.Write(bytes, 0, length); file.Flush(true); hash.AppendData(bytes.AsSpan(0, length)); row += rows;
            state = state with { CommittedRows = row, Sha256 = Convert.ToHexString(hash.GetCurrentHash()).ToLowerInvariant() };
            ScaleArtifacts.AtomicWrite(Path.Combine(directory, "dataset.json"), state);
            Console.Error.WriteLine($"Canonical vectors {row:N0}/{state.Count:N0}");
        }
        return state;
    }


    internal static string TruthPath(string directory, int queryCount) => Path.Combine(directory, $"truth-q{queryCount}.json");
    public static ScaleTruth EnsureTruth(string directory, ScaleDataset dataset, int queryCount, int querySeed, CancellationToken token)
    {
        string path = TruthPath(directory, queryCount);
        string checkpoint = path + ".partial";
        ScaleTruth? state = File.Exists(path) ? ScaleArtifacts.Read<ScaleTruth>(path) : File.Exists(checkpoint) ? ScaleArtifacts.Read<ScaleTruth>(checkpoint) : null;
        if (state is not null && (state.DatasetSha256 != dataset.Sha256 || state.QueryCount != queryCount || state.QuerySeed != querySeed || state.ScannedRows < 0 || state.ScannedRows > dataset.Count))
            throw new InvalidDataException("Exact-reference checkpoint does not match the verified dataset/query set.");
        if (state?.ScannedRows == dataset.Count) { Console.Error.WriteLine("Reusing completed exhaustive reference."); return state; }
        var queries = Enumerable.Range(0, queryCount).Select(i => ScaleAlgorithms.Vector(querySeed, (ulong)i, dataset.Distribution, query: true)).ToArray();
        var norms = queries.Select(ScaleAlgorithms.Norm).ToArray();
        var queues = queries.Select(_ => ScaleAlgorithms.FilterModuli.Select(_ => new ExactTopK()).ToArray()).ToArray();
        if (state is not null)
            for (int q = 0; q < queryCount; q++)
                for (int f = 0; f < 3; f++)
                    foreach (var neighbor in state.Neighbors[q][f]) queues[q][f].Add(neighbor.Key, neighbor.Score);
        var timer = Stopwatch.StartNew();
        double previousElapsed = state?.ElapsedMs ?? 0;
        using var file = new FileStream(Path.Combine(directory, "vectors.f16"), FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
        long next = state?.ScannedRows ?? 0; file.Position = next * ScaleAlgorithms.BytesPerVector;
        var bytes = new byte[ScaleAlgorithms.BytesPerVector]; var vector = new float[ScaleAlgorithms.Dimensions];
        ScaleTruth Snapshot(long rows) => new(dataset.Sha256!, queryCount, querySeed, rows, previousElapsed + timer.Elapsed.TotalMilliseconds, queues.Select(q => q.Select(f => f.Results()).ToArray()).ToArray());
        try
        {
            for (long row = next; row < dataset.Count; row++)
            {
                if ((row & 255) == 0) token.ThrowIfCancellationRequested();
                file.ReadExactly(bytes); ScaleAlgorithms.Decode(bytes, vector); double norm = ScaleAlgorithms.Norm(vector);
                for (int q = 0; q < queries.Length; q++)
                {
                    double score = ScaleAlgorithms.Cosine(vector, queries[q], norm, norms[q]); queues[q][0].Add((ulong)row, score);
                    if (ScaleAlgorithms.Accept((ulong)row, 10)) queues[q][1].Add((ulong)row, score);
                    if (ScaleAlgorithms.Accept((ulong)row, 100)) queues[q][2].Add((ulong)row, score);
                }
                next = row + 1;
                if (next % 65536 == 0)
                {
                    ScaleArtifacts.AtomicWrite(checkpoint, Snapshot(next));
                    Console.Error.WriteLine($"Exact top-50 reference {next:N0}/{dataset.Count:N0} × {queryCount} queries; {timer.Elapsed.TotalSeconds:F1}s this run");
                }
            }
        }
        catch (OperationCanceledException) { ScaleArtifacts.AtomicWrite(checkpoint, Snapshot(next)); throw; }
        state = Snapshot(dataset.Count); ScaleArtifacts.AtomicWrite(path, state);
        return state;
    }

    private static async Task<JsonElement> RunMeasurementAsync(string directory, string mode, int queryCount, int candidateCount, CancellationToken token)
    {
        string output = Path.Combine(directory, $"measurement-{mode}-q{queryCount}-{Guid.NewGuid():N}.json");
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        foreach (string argument in new[] { "--scale-measure", directory, "--index-mode", mode, "--query-count", queryCount.ToString(), "--candidate-count", candidateCount.ToString(), "--output", output }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Cannot start isolated measurement process.");
        Task forward = Task.Run(async () => { while (await process.StandardError.ReadLineAsync(token) is { } line) Console.Error.WriteLine(line); }, token);
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(token);
        try { await process.WaitForExitAsync(token); await forward; _ = await stdout; }
        catch { try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { } throw; }
        if (process.ExitCode != 0) throw new InvalidOperationException($"Isolated {mode} measurement failed with exit {process.ExitCode}.");
        return ScaleArtifacts.Read<JsonElement>(output);
    }

    public static async Task<int> MeasureChildAsync(string[] args)
    {
        try
        {
            string directory = Path.GetFullPath(Option(args, "--scale-measure")!);
            var dataset = ScaleArtifacts.Validate(directory);
            int count = int.Parse(Option(args, "--query-count")!);
            int candidateCount = int.Parse(Option(args, "--candidate-count")!);
            string mode = Option(args, "--index-mode")!;
            if (mode is not ("mapped" or "loaded") || dataset.Count != dataset.CommittedRows) throw new InvalidDataException("Invalid measurement arguments.");
            var truth = ScaleArtifacts.Read<ScaleTruth>(TruthPath(directory, count));
            if (truth.DatasetSha256 != dataset.Sha256 || truth.ScannedRows != dataset.Count || truth.QueryCount != count) throw new InvalidDataException("Measurement exact-reference mismatch.");
            var queries = Enumerable.Range(0, count).Select(q => ScaleAlgorithms.Vector(truth.QuerySeed, (ulong)q, dataset.Distribution, query: true)).ToArray();
            _ = new RuntimePaths(AppContext.BaseDirectory);
            using var memory = new ScaleMemoryMonitor();
            var baseline = memory.Snapshot(); var timer = Stopwatch.StartNew();
            using var index = NativeVectorIndex.Open(Path.Combine(directory, "index.usearch"), mode == "mapped");
            double openMs = timer.Elapsed.TotalMilliseconds;
            var afterOpen = memory.Snapshot();
            var scenarios = new List<object>(); bool pass = true;
            for (int f = 0; f < 3; f++)
            {
                int modulus = ScaleAlgorithms.FilterModuli[f];
                HashSet<ulong>? allowed = modulus == 1 ? null : Enumerable.Range(0, dataset.Count).Where(id => ScaleAlgorithms.Accept((ulong)id, modulus)).Select(id => (ulong)id).ToHashSet();
                var perQuery = new List<object>(); var durations = new List<double>(); var recalls = new List<double>();
                double? firstMs = null;
                for (int q = 0; q < queries.Length; q++)
                {
                    timer.Restart(); var candidates = index.Search(queries[q], candidateCount, allowed); var actual = candidates.Take(ScaleAlgorithms.TopK).ToArray(); double elapsed = timer.Elapsed.TotalMilliseconds;
                    var expected = truth.Neighbors[q][f].Select(n => n.Key).ToHashSet();
                    int hits = actual.Select(n => n.Key).Distinct().Count(expected.Contains);
                    double recall = expected.Count == 0 ? 1 : (double)hits / expected.Count;
                    bool valid = candidates.All(n => n.Key < (ulong)dataset.Count && ScaleAlgorithms.Accept(n.Key, modulus)) && candidates.Select(n => n.Key).Distinct().Count() == candidates.Count;
                    if (!valid) throw new InvalidDataException("Native retrieval returned invalid, duplicate, or disallowed keys.");
                    if (q == 0) firstMs = elapsed; else durations.Add(elapsed);
                    recalls.Add(recall);
                    perQuery.Add(new { queryId = q, elapsedMs = elapsed, recallAt50 = recall, expectedCount = expected.Count, actualCount = actual.Length, candidateCount = candidates.Count, correctCount = hits });
                }
                double mean = recalls.Average(); pass &= mean >= .95;
                var memoryAfter = memory.Snapshot();
                scenarios.Add(new { filter = modulus == 1 ? "all" : $"key-mod-{modulus}", eligibleRecords = ScaleAlgorithms.EligibleCount(dataset.Count, modulus), independentQueries = count,
                    firstQueryMs = firstMs, warmUniqueQueryCount = durations.Count, warmP50Ms = ScaleAlgorithms.Percentile(durations, .5), warmP95Ms = ScaleAlgorithms.Percentile(durations, .95),
                    meanRecallAt50 = mean, minimumQueryRecallAt50 = recalls.Min(), recallGatePassed = mean >= .95, memoryWhileIndexOpen = memoryAfter, perQuery });
                Console.Error.WriteLine($"{mode}, {modulus}: Recall@50 {mean:P2}; warm P95 {ScaleAlgorithms.Percentile(durations, .95):F2}ms");
            }
            var finalMemory = memory.Snapshot();
            var report = new { mode, processId = Environment.ProcessId, hardware = ScaleHardware.Read(), nativeIndexOpenMs = openMs, beforeIndexOpen = baseline, afterIndexOpen = afterOpen,
                finalWhileIndexOpen = finalMemory, sampledPeakWhileIndexOpen = memory.Peak(), memoryScope = "Fresh benchmark child only, including canonical query arrays, expected top-50 sets, native index and allowed-key HashSets. Working set includes resident mapped pages. Excludes encoder, catalog, reader, UI and preview processes. Native index is still open for every memory snapshot.",
                queryCache = "No result or embedding cache; each warm timing uses a distinct held-out vector once per scenario. Filter scenarios run after unfiltered access and reuse vectors; graph/filesystem pages can be warm.", recallGatePassed = pass, scenarios };
            ScaleArtifacts.AtomicWrite(Option(args, "--output")!, report);
            await Task.CompletedTask;
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
