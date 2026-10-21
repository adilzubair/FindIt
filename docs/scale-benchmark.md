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

The reported gate requires mean Recall@50 ≥95% separately for unfiltered, 10%, and 1% scenarios in each selected access mode. A native filtered-search failure is useful evidence even where the full reader would choose its exact-search path for a small allowed set. The standalone benchmark does not exercise that reader fallback.

## Timing and memory boundaries

Dataset generation, native building, and exhaustive scoring happen in the parent. Each access mode starts a **fresh child process**, preventing earlier graph-building allocations or exact-reference scans from contaminating the child's memory totals.

For each mode, the report separates index-open time, the first query, and unique warm-query P50/P95. Query 0 is excluded from the warm latency percentiles. Every other query runs once per scenario; there is no query-result or embedding cache. Filtered scenarios run after the unfiltered scenario, so graph pages and queries can already be warm. Their first-query measurement is not a new process-cold launch.

The tool **does not evict the Windows filesystem cache**. Parent integrity checks, index construction, or previous runs can warm storage caches. “Fresh process” therefore does not mean “cold disk.” Run a rebooted-machine/drive-specific procedure separately before making cold-start or HDD claims.

Working set, private bytes, process peak working set, and 25 ms sampled peaks are measured while the native mapping or loaded index is still open. Working set includes resident mapped pages. The child includes query arrays, truth sets, result metadata, and the largest allowed-key HashSet. It excludes the encoder, SQLite catalog, complete reader, interface, video playback, and other processes. Sampling can miss very brief peaks; the operating system process peak is reported as an additional check. Hardware metadata includes CPU, logical processors, physical RAM, OS, runtime, and SIMD width.

## Storage, interruption, and reuse

At one million records the canonical FP16 file is 1.024 GB. The graph index adds further storage. Each distribution/size has its own canonical data and index; sizes do not share files. Native construction can require substantially more transient memory than a mapped read. Reserve several GB of free disk and run large cases sequentially. Consult actual report sizes rather than assuming graph overhead.

The runner creates `scale-v1-<distribution>-<count>-<seed>` under the chosen artifacts root. It refuses an existing child without its ownership marker, wrong markers, directory/file links, mismatched dataset identities, or checksum corruption. It does not open or clear an archive. An exclusive lease prevents two parent runs from modifying the same dataset.

Canonical data is flushed every 8,192 rows before publishing its checkpoint and prefix hash. On resume, the committed prefix is checksum-verified; only an owned uncommitted tail can be truncated. Exact-reference queues checkpoint every 65,536 rows and on cooperative cancellation. Completed reference data is reused for the same dataset/query set.

A native build interrupted before index publication restarts from the canonical vectors; native graph insertion is not checkpoint-resumable. A completed native index is reused only after validating its dataset identity, native DLL hash, byte length, and checksum. A mismatched completed artifact is retained for diagnosis and rejected; select a fresh artifacts root to rebuild. Writes use flushed temporary files and replacement. An unexpected power failure may lose the latest checkpoint, but does not require touching original media or a prepared archive.

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
