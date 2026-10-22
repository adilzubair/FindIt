# Search follow-up: stable paging and ANN validation

Stable ranked paging and the bounded 100k ANN tuning/evaluation are implemented. Reader search expansion is now **8,192**, independent of the returned-candidate budget; graph construction remains unchanged. This closes the described 100k experiment, not the full scale, media relevance, or older-hardware release gates.

## Measured limits and confidence

The retained `artifacts/scale-validation/isotropic-100k.json` uses 64 independent synthetic queries, exact FP16 cosine references, and 512 ANN candidates. Mean Recall@50 was **30.06% unfiltered, 90.16% with 10% eligible records, and 100% with 1% eligible records**, in both access modes. Mapped warm P95 was 12.70, 66.77, and 131.74 ms respectively on the strong development PC with uncontrolled filesystem caches. These measurements exclude query encoding, SQLite, the interface, and physical HDD behavior.

The reader uses reciprocal-rank fusion; `SearchResult.Score` is a ranking value, and the interface does not display it as confidence. Person/action results remain possible matches. However, a successful low-recall ANN search currently returns `SemanticUsed=true` without detecting omitted neighbors. Runtime failure notices are not recall checks. Empty approximate results must not be presented as proof that content is absent.

## Stable, bounded pagination

Previously, `ArchiveSearch` increased candidates with `(offset + limit) * 4`, reranking each page and potentially skipping/repeating files. It now retrieves and groups once using `clamp(request.RetrievalBudget ?? max(512, limit * 4), 512, 16384)`. The interface retains its 256-file page and 1,024-candidate initial window. Continuation pages do not encode or traverse the graph again.

The worker query session:

- Freezes ranked file IDs, at most 12 matching segment IDs per file, score/category provenance, and bounded query/person metadata. It hydrates text, snippets, thumbnails and face evidence only for the requested page.
- Binds to library/generation, prepared/current encoder fingerprints and availability, normalized query, SQL filters, sorted resolved people, overlay `settings.revision`, page size, retrieval budget and exploration setting. Versioned `s1` cursors identify a random session ID and offset; `PageCursor` also permits revalidating the first page.
- Retains at most two sessions and an estimated 8 MiB total, using conservative per-entry accounting and LRU eviction. Oversized windows are truncated before retention. Ten-minute idle expiry uses monotonic time; expiry is checked on cache access. No session is written to the archive.
- Returns `SearchResponse.RestartRequired` for invalid, expired, evicted or unknown cursors and changed bindings, including worker restart. A revision change during retrieval discards the mixed response. The interface clears its pages, cursors and preview and offers a retry preserving the submitted query/filters. Previously viewed pages also validate their worker cursor.
- Exposes **Search more** as an explicit fresh, larger window, restarting at page one. `IsBoundedWindow` labels ranked selections; no exhaustive archive count is claimed. Ordinary empty-query/people-only browsing retains SQL paging with separately versioned, generation/revision-bound `b1` cursors.

Client transport remains generic. Regression tests cover duplicate/omitted page IDs, fixed windows versus explicit deeper retrieval, one encoding per session, revisited moment IDs, generation/people/encoder/filter/budget invalidation, concurrent people mutation, expiry/LRU/byte eviction, malformed cursors, cancellation and a real child-worker restart. The WPF harness exercises restart, query preservation and deeper retrieval. Query-cache-free certification must measure fresh first pages separately from continuation pages.

Verification: Release solution build passed with zero warnings/errors; **198 tests** passed with zero failed/skipped (`artifacts/search-next-tests/search-next-full.trx`); **99 WPF interaction checks** passed (`artifacts/search-next-ui/interaction.json`). Compact light/dark captures in that UI directory were visually reviewed. The first sandboxed test attempt could not connect to its child test process and was retained as an aborted TRX; the permitted rerun and full suite passed.

## Bounded ANN tuning experiment

The experiment reused the checksum-verified, **serially constructed** 100,000-vector graphs for both distributions with corpus seed 42. It retained all prior failures. Query IDs **256–511** were used for tuning, and **512–767** were evaluated only after freezing the policy, with `query=true`. Both sets cover every synthetic centroid. `--query-offset`, `--search-expansion`, and exact-reference fingerprints separate these sets without changing corpus centroids or rebuilding/embedding the graphs.

Compare `(returned candidates, configured search expansion)`:

| Candidates | Expansion | Purpose |
|---:|---:|---|
| 512 | 128 | Reproduce the retained baseline; effective expansion is 512 |
| 1,024 | 128 | Current interface first-page budget; effective expansion is 1,024 |
| 1,024 | 2,048 / 4,096 / 8,192 | Increase exploration without increasing page hydration |
| 512 | Selected expansion | Separate returned-metadata cost from exploration cost |

The 1,024-candidate isotropic unfiltered tuning means were 50.15%, 71.08%, 87.95% and **97.04%** at configured expansions 128, 2,048, 4,096 and 8,192. The 512-candidate/8,192 expansion diagnostic had the same 97.04% mean. Every filter/access-mode scenario for both distributions passed at 8,192, the smallest tested passing expansion. The frozen policy and SHA-256 inventory of tuning reports are in `artifacts/search-tuning/frozen-policy.json`; all experiment reports remain in that directory. Reader base and delta searches use this expansion while preserving their returned-candidate limits. Native mapped/loaded regression checks verify the setting is per-call and never edits the saved graph.

The untouched evaluation uses 256 queries per distribution, both fresh-process access modes, exhaustive canonical FP16 top-50 references and the unchanged 95% mean gate. It reports per-query recall, minimum recall, latency and resident memory. See the retained `*-evaluation.json` reports for results. Beam width does not cap distance calculations or storage reads. These serial 100k graphs do not certify the parallel million-record graph, real media, cold storage or the 8 GB laptop.

Evaluation results (mean/minimum recall are identical in both access modes):

| Distribution | Eligibility | Mean Recall@50 | Minimum query recall | Mapped warm P95 | Loaded warm P95 |
|---|---|---:|---:|---:|---:|
| Isotropic 100k | All | 97.17% | 90% | 101.26 ms | 90.13 ms |
| Isotropic 100k | 10% | 100% | 100% | 142.92 ms | 120.99 ms |
| Isotropic 100k | 1% | 100% | 100% | 125.49 ms | 120.70 ms |
| Clustered 100k | All | 100% | 100% | 32.55 ms | 31.38 ms |
| Clustered 100k | 10% | 100% | 100% | 119.68 ms | 122.77 ms |
| Clustered 100k | 1% | 100% | 100% | 120.84 ms | 135.46 ms |

The 1,024-candidate/128-expansion tuning baseline was 50.15% on the difficult dataset; its earlier retained 64-query run was 50.78%. The new evaluation uses different, untouched queries. No evaluation result was used to select or retune the policy. The 90% worst-query result is retained; a mean gate does not promise 95% on every query. These timings exclude model encoding, SQL, hydration and UI work and used uncontrolled OS caches.

## Filtered search and older hardware

The current correction selects exact retrieval before opening ANN for at most 4,096 eligible semantic records, counts only valid vector-bearing segments, and refreshes mappings on generation changes. A later broad query can still initialize ANN. This avoids unnecessary HDD graph preloading for a small folder/person filter; it does not tune the cutoff or make exact scans free.

Compare exact cutoffs of 4,096, 8,192, and 16,384 with contiguous and scattered eligible sets. The current exact implementation scans every 20-byte vector record, then reads eligible FP16 blocks. Scattered blocks can cause costly HDD seeks despite a small eligible count. Native selective traversal also becomes more expensive as filters narrow.

Finally measure full-reader P95, CPU, resident mapped pages, I/O/page faults, model initialization, and first visible metadata on the older CPU with SSD and HDD. HDD preloading is currently limited to a 1.5 GiB index and available-memory checks; larger exploration budgets can increase page traffic. Strong-PC warm-cache timings do not establish the 8 GB, CPU-only target. Real labeled media relevance and identity precision remain separate acceptance gates.
