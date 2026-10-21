# Synthetic ANN scale validation

`FindIt.Bench --ann-records` measures the pinned native USearch index against exhaustive reference results. It replaces the earlier self-match smoke test. A query finding its own indexed vector is not Recall@50.

This tool measures **synthetic ANN retrieval**, not semantic usefulness, text-model latency, a complete reader's memory, gallery performance, or performance on the eventual older PC. Reports must retain those qualifications. Real media relevance and the complete portable reader require separate acceptance runs.

## Build and run

Use a separate output directory so an open application or benchmark remains undisturbed:

```powershell
$env:MSBuildEnableWorkloadResolver = 'false'
$env:DOTNET_CLI_HOME = Join-Path $PWD '.tools/dotnet-home'
dotnet build tools/FindIt.Bench/FindIt.Bench.csproj -c Release `
  --artifacts-path artifacts/scale-next-build -m:1 --disable-build-servers

$bench = 'artifacts/scale-next-build/bin/FindIt.Bench/release/FindIt.Bench.exe'
& $bench --root artifacts/scale-validation --ann-records 100000 `
  --distribution clustered-512 --query-count 64 --candidate-count 512 `
  --output artifacts/scale-validation/clustered-100k.json
```

Run 100,000, 500,000, and 1,000,000 records sequentially. Run 2,000,000 only as an optional stress test. Repeat those commands with `--distribution isotropic-512` for the hard unstructured sanity set. The two distributions are reported separately; success on the clustered set must not hide a failure on the isotropic set.

Options:

| Option | Default | Meaning |
| --- | --- | --- |
| `--root` | `artifacts/scale` | Parent for owned benchmark datasets; must be beneath an `artifacts` directory |
| `--ann-records` | Required | Corpus size, 100–2,000,000 |
| `--distribution` | `isotropic-512` | `isotropic-512` or `clustered-512` |
| `--seed` | `42` | Deterministic generator seed |
| `--query-count` | `64` | Held-out queries, 4–1024; use at least 64 for acceptance runs |
| `--candidate-count` | `512` | Request this many ANN candidates, then evaluate the first 50; 512 matches the production reader's minimum request |
| `--build-concurrency` | Minimum of 8 and logical processors | Native graph insertion workers, 1–8; recorded in graph checkpoints and final index metadata |
| `--index-mode` | `both` | Fresh-process `mapped`, `loaded`, or both |
| `--output` | Timestamped dataset report | Final JSON report, also printed to standard output |

`--candidate-count 50` is available as a stricter raw native-search diagnostic. Do not compare it to a production request of 512 candidates without preserving that difference in the report.

Exit codes are `0` for completed runs that pass all reported recall gates, `3` for completed runs with a failed recall gate, `130` for cancellation, and `1` for an execution/integrity failure. A failed gate still produces the full report. Progress goes to standard error; redirect standard output if the complete per-query JSON is unwanted on the console.

The source build locates the bundled `runtime/native/libusearch_c.dll` through the normal runtime resolver. It does not need the embedding model or a worker process. Do not rebuild the executable's output folder while a run is active.

## Corpus and exact reference

- Vectors are 512-dimensional, normalized, rounded to FP16, and streamed to `vectors.f16`. There is no full managed `N × 512` float copy.
- SplitMix64 is specified in source, so generating or resuming a row does not depend on `System.Random`, process state, or the .NET version.
- `isotropic-512` uses independent uniform coordinate noise and normalization. It is deliberately difficult for graph search in high dimension.
- `clustered-512` uses 256 deterministic centroids and independent per-vector coordinate noise with amplitude 0.45. This is a structured stress dataset; it is not measured media embeddings and does not establish media relevance.
- Queries use a separate random domain from corpus vectors. Clustered queries share centroids with corpus members but never reuse corpus vectors or their noise.
- Exhaustive reference search scores **every eligible canonical corpus row** using cosine similarity. SIMD float products use double accumulation and double norms. FP16-rounded query values are passed to both reference and native searches.
- Exact top 50 results use descending cosine, with ascending key as a deterministic tie break. Reference queues retain only the strongest 50 per query/filter.
- Filters are explicit stable key subsets: all records, `key % 10 == 3`, and `key % 100 == 17`. Expected filtered results are computed from their complete eligible sets, not by filtering a short global result list. Key filters can correlate with synthetic clusters; the exact reference includes that effect.
- Recall@50 is the fraction of exact top-50 keys present in the first 50 ANN results. If fewer than 50 records are eligible, the denominator is the exact eligible result count. The report includes every query's expected/actual/hit counts, mean recall, and worst query recall.

The default graph configuration is the application's pinned cosine/FP16, connectivity 16, insertion expansion 128, and search expansion 128. USearch may increase effective expansion to satisfy a larger candidate request. The native DLL SHA-256 and candidate count are recorded. This runner does not tune parameters against the test queries.

New benchmark graphs use bounded parallel insertion, with at most eight workers and 1,024 vectors per batch. The native index reserves the complete corpus capacity before parallel insertion. An exclusive managed gate prevents search, save, reserve, or disposal while workers are active; every native insertion receives a separate error pointer. All workers join before the completed canonical prefix advances. Inputs are checked for finite values and duplicate keys before mutation. A native batch failure poisons its in-memory handle, preventing search, save, or continuation; resumption reloads the last durable checkpoint instead of saving a partial batch.

Cancellation is checked between successful complete batches. Batches stop at checkpoint boundaries, so the 50,000-row publication interval is preserved. Graph insertion order is nondeterministic under parallel construction, and recall is always measured against the exact reference for the resulting graph. **Production preparation still builds serially in this pass.** The report records both the requested concurrency and the actual saved index's concurrency/batch size; older completed serial indexes remain reusable with construction concurrency 1. Do not present parallel construction throughput as production preparation throughput or silently compare their recall as if graph construction were identical.

Native count, exact key membership, and exact stored-vector lookup validate insertion integrity independently of graph traversal. They do not prove that every node is reachable by ANN search. During a 2,048-row isotropic diagnostic, serial construction returned all 2,048 nodes with a full-size request while two parallel constructions returned 2,031 and 2,041, although all keys and canonical vectors were present. This is a measured construction-quality difference that must remain visible; do not replace held-out Recall@50 with self-match or stored-key checks.

The reported gate requires mean Recall@50 ≥95% separately for unfiltered, 10%, and 1% scenarios in each selected access mode. A native filtered-search failure is useful evidence even where the full reader would choose its exact-search path for a small allowed set. The standalone benchmark does not exercise that reader fallback.

## Timing and memory boundaries

Dataset generation, native building, and exhaustive scoring happen in the parent. Each access mode starts a **fresh child process**, preventing earlier graph-building allocations or exact-reference scans from contaminating the child's memory totals.

For each mode, the report separates index-open time, the first query, and unique warm-query P50/P95. Query 0 is excluded from the warm latency percentiles. Every other query runs once per scenario; there is no query-result or embedding cache. Filtered scenarios run after the unfiltered scenario, so graph pages and queries can already be warm. Their first-query measurement is not a new process-cold launch.

The tool **does not evict the Windows filesystem cache**. Parent integrity checks, index construction, or previous runs can warm storage caches. “Fresh process” therefore does not mean “cold disk.” Run a rebooted-machine/drive-specific procedure separately before making cold-start or HDD claims.

Working set, private bytes, process peak working set, and 25 ms sampled peaks are measured while the native mapping or loaded index is still open. Working set includes resident mapped pages. The child includes query arrays, truth sets, result metadata, and the largest allowed-key HashSet. It excludes the encoder, SQLite catalog, complete reader, interface, video playback, and other processes. Sampling can miss very brief peaks; the operating system process peak is reported as an additional check. Hardware metadata includes CPU, logical processors, physical RAM, OS, runtime, and SIMD width.

Loaded indexes now stream through the pinned native file API only when the resolved full path is ASCII and shorter than 240 characters. The Unicode-safe metadata mapping is closed before loading, and a read-sharing lease prevents file replacement or writes between metadata and load. This avoids simultaneously resident mapped-source and private-destination copies for the normal controlled portable index paths. Unicode, long, and device-prefixed paths retain the Unicode-safe buffer loader and its potential two-copy transition. `EstimateLoadPeakBytes` accounts for one versus two serialized index copies; callers must add allocator, model, catalog, and interface headroom. A 1.5 GiB serialized-index limit alone is not an aggregate reader peak-memory guarantee.

The reader's preload policy adds the larger of 128 MiB or **25% of serialized index bytes** as allocation headroom, applies its 1.5 GiB index-transition budget, and checks available physical memory before choosing a loaded index. A Unicode/long-path fallback whose two-copy transition exceeds that budget remains mapped. The native one-copy/two-copy estimate itself is unchanged; neither estimate is a full-application memory certification.

## Storage, interruption, and reuse

At one million records the canonical FP16 file is 1.024 GB. The graph index adds further storage. Each distribution/size has its own canonical data and index; sizes do not share files. Native construction can require substantially more transient memory than a mapped read. Reserve several GB of free disk and run large cases sequentially. Consult actual report sizes rather than assuming graph overhead.

The runner creates `scale-v1-<distribution>-<count>-<seed>` under the chosen artifacts root. It refuses an existing child without its ownership marker, wrong markers, directory/file links, mismatched dataset identities, or checksum corruption. It does not open or clear an archive. An exclusive lease prevents two parent runs from modifying the same dataset.

Canonical data is flushed every 8,192 rows before publishing its checkpoint and prefix hash. On resume, the committed prefix is checksum-verified; only an owned uncommitted tail can be truncated. Exact-reference queues checkpoint every 65,536 rows and on cooperative cancellation. Completed reference data is reused for the same dataset/query set.

A native build saves a flushed, token-named graph checkpoint every **50,000 inserted rows**. Its atomically replaced pointer records the current and previous graph, row counts, checksums, dataset/DLL identities, dimensions, insertion/search settings, and cumulative build time. The previous graph is retained until its replacement pointer has been published. Cleanup is restricted to the benchmark's exact token filename convention inside its owned dataset directory.

On resume, the builder verifies graph identity, byte length, SHA-256, native row count, dimensions, concurrency, and batch-size fingerprint, then seeks the canonical file to the next row. A corrupt current graph falls back to its separately verified previous graph. It loads a **private writable native copy**, through streaming for eligible short ASCII paths or a read-only source mapping for the Unicode/long-path fallback. Source files are closed before insertion resumes. Ordinary mapped and loaded reader indexes remain immutable. A cooperative cancellation also attempts to checkpoint additional completed batches; a hard process termination can lose at most the work since the last successful periodic checkpoint. A termination during checkpoint publication can leave an unreferenced token graph; the next successful publication safely removes that owned debris. Resume with the same build concurrency; changing it requires a separate build rather than relabeling an existing graph.

A completed native index is reused only after validating its dataset identity, native DLL hash, byte length, and checksum. Completed indexes from the earlier benchmark implementation remain compatible. A mismatched completed artifact is retained for diagnosis and rejected; select a fresh artifacts root to rebuild. The final complete graph is copied and flushed to the immutable index, and its manifest is published before build checkpoints are removed. Writes use flushed temporary files and atomic replacement. An unexpected power failure may lose the latest checkpoint, but does not require touching original media or a prepared archive.

Press Ctrl+C for cooperative cancellation. Reports and artifacts stay local. Neither drive contents, source media, nor generated scale data should be committed to Git.

## Development measurements, 21 October 2026

These runs used a Core Ultra 9 275HX, 32 GB RAM, Windows 11 build 26200, local storage, and the pinned native DLL. Other development preparation/build jobs ran concurrently. Filesystem cache was uncontrolled. Timings are diagnostic measurements, not isolated performance certification or evidence for the older 8 GB laptop.

At 100,000 records, the 512-candidate configuration produced these mean Recall@50 values against 64 independent exhaustive reference queries. Mapped and loaded modes agreed:

| Synthetic distribution | Unfiltered | 10% eligible | 1% eligible | Gate |
|---|---:|---:|---:|---|
| `clustered-512` | 100% | 99.97% | 100% | Pass |
| `isotropic-512` | 30.06% | 90.16% | 100% | **Fail** |

At 500,000 `clustered-512` records, both access modes passed: unfiltered 99.75%, 10% eligible 98.94%, and 1% eligible 99.81%. Unfiltered warm P95 was 7.21 ms mapped and 3.22 ms loaded; these exclude encoding and catalog/UI work. Index size was 586,282,600 bytes, in addition to 512,000,000 canonical vector bytes. The final benchmark-child working set while the index was open was approximately 681–704 MiB across the two modes. See `artifacts/scale-validation/clustered-500k.json` for scenario-level latency and memory.

Reports are `artifacts/scale-validation/clustered-100k.json` and `isotropic-100k.json`. The failed report is retained. The distributions contain synthetic vectors, not labeled media embeddings; neither outcome establishes semantic usefulness. Sparse filtered benchmark scenarios deliberately exercise native ANN even when the production reader would use its small-set exact path.

The hard isotropic result keeps ANN release certification open. Do not substitute the clustered result for it. A future tuning pass can use the pinned C API's `usearch_change_expansion_search` to compare larger exploration budgets on the existing graph without re-embedding. Choose settings with separate tuning queries, then evaluate untouched held-out queries and actual media, including old-PC and HDD latency. Production settings remain unchanged pending that evaluation.

Focused reference/safety tests:

```powershell
dotnet test tests/FindIt.Tests/FindIt.Tests.csproj -c Release `
  --artifacts-path artifacts/scale-tests-build -m:1 --disable-build-servers `
  --filter FullyQualifiedName~ScaleBenchmarkTests
```

## Recorded development run — 21 October 2026

Host: Intel Core Ultra 9 275HX, 24 logical processors, 33,752,997,888 bytes physical RAM, Windows build 26200, .NET 10.0.11 x64. Runs used the local workspace drive. Other development work ran concurrently and filesystem caches were uncontrolled. These results are development evidence, **not certification of the reference 8 GB laptop, an external HDD, semantic relevance, or complete reader memory**.

All rows use 512 dimensions, 64 independent queries, exact top-50 reference, and seed 42. Each recall value below is the mean across queries and was identical between mapped and loaded modes. Construction differs between the older serial graphs and the newer eight-worker graph; this is recorded rather than treated as a controlled construction comparison.

| Dataset | Records | Construction | Requested candidates | Recall@50: all / 10% / 1% | Mean ≥95% in all scenarios |
| --- | ---: | --- | ---: | --- | --- |
| Clustered | 100,000 | Serial | 512 | 100.00% / 99.97% / 100.00% | Pass |
| Clustered | 500,000 | Serial | 512 | 99.75% / 98.94% / 99.81% | Pass |
| Clustered | 1,000,000 | Eight workers, resumed after hard kill | 512 | 98.38% / 96.81% / 99.03% | Pass |
| Clustered, same million-record graph | 1,000,000 | Same saved graph | 1,024 | 99.03% / 98.53% / 99.31% | Pass |
| Isotropic | 100,000 | Serial | 512 | 30.06% / 90.16% / 100.00% | **Fail** |
| Isotropic, same graph | 100,000 | Same saved graph | 1,024 | 50.78% / 98.38% / 100.00% | **Fail** |

The WPF interface currently requests 256 results, resulting in 1,024 semantic candidates. The worker/API minimum remains 512. The 1,024-candidate runs therefore diagnose the existing interface policy; they are not parameter tuning or an untouched certification set. The isotropic failure remains open evidence of a hard-distribution limitation. No global search-expansion increase was applied to conceal it.

The million-record graph is 1,172,578,076 bytes and its canonical FP16 data is 1,024,000,000 bytes. Its checkpointed native construction took 478.85 seconds, excluding the earlier discarded serial attempt and some restart overhead. A deliberately terminated owned process had logged 320,480 inserted rows; the latest verified graph checkpoint held 300,000 with the previous 250,000 retained. Current/previous checksums and the entire canonical-file checksum matched before/after termination. Resumption loaded a private copy of the verified 300,000-row checkpoint and completed the run. This exercised real process death in addition to the small cooperative-cancellation and corrupt-current-checkpoint tests.

The before/after load optimization reused the exact same million-record graph, canonical data, 64-query reference, and 1,024-candidate policy:

| Loaded-index measurement | Mapped-source copy loader | Short-ASCII streaming loader |
| --- | ---: | ---: |
| Peak child working set | 2,483,486,720 bytes | 1,411,051,520 bytes |
| Final child working set | 1,410,973,696 bytes | 1,411,051,520 bytes |
| Index-open time | 1.033 seconds | 2.363 seconds |
| Mean Recall@50: all / 10% / 1% | 99.03% / 98.53% / 99.31% | 99.03% / 98.53% / 99.31% |

The measured peak reduction is about 43.2%, with a slower open in this run. Final memory and recall were unchanged. Streaming-loader warm P95 native retrieval was 8.57 / 75.58 / 421.27 ms for all / 10% / 1% respectively. Mapped timings varied substantially with cache/development activity and are retained in the raw reports; this is another reason to avoid treating this run as hardware latency certification. These memory values exclude model encoding, catalog, UI, preview, and other processes. Unicode/long-path fallback retains the two-copy loading behavior, which the reader's preload policy must budget or avoid.

Raw local reports are retained under `artifacts/scale-validation/`: `clustered-100k.json`, `clustered-500k.json`, `clustered-1m-parallel.json`, `clustered-1m-ui1024.json`, `clustered-1m-ui1024-stream.json`, `isotropic-100k.json`, and `isotropic-100k-ui1024.json`. The `native-hardkill-before.json` / `native-hardkill-after.json` evidence and distinct interrupted/restart logs preserve the recovery exercise. They contain synthetic data and machine diagnostics, remain local, and are not committed with generated datasets.
