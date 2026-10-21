# Search follow-up: stable paging and ANN validation

These are **unimplemented release tasks**, except for the small-filter routing correction described below. Production ANN parameters remain unchanged. Passing the portable regression suite does not close the ANN accuracy gate.

## Measured limits and confidence

The retained `artifacts/scale-validation/isotropic-100k.json` uses 64 independent synthetic queries, exact FP16 cosine references, and 512 ANN candidates. Mean Recall@50 was **30.06% unfiltered, 90.16% with 10% eligible records, and 100% with 1% eligible records**, in both access modes. Mapped warm P95 was 12.70, 66.77, and 131.74 ms respectively on the strong development PC with uncontrolled filesystem caches. These measurements exclude query encoding, SQLite, the interface, and physical HDD behavior.

The reader uses reciprocal-rank fusion; `SearchResult.Score` is a ranking value, and the interface does not display it as confidence. Person/action results remain possible matches. However, a successful low-recall ANN search currently returns `SemanticUsed=true` without detecting omitted neighbors. Runtime failure notices are not recall checks. Empty approximate results must not be presented as proof that content is absent.

## Stable, bounded pagination

`ArchiveSearch` currently requests `min(16384, max(512, (offset + limit) * 4))` candidates. The interface requests 256 files, so its first search asks for 1,024 candidates. Later pages increase that budget. The pinned native search uses `max(expansionSearch, requestedCandidates)`, so paging also changes graph exploration. Newly discovered neighbors can reorder earlier file positions; applying the next offset can skip or repeat files even when the archive is unchanged. Generation/query/filter checks in the current cursor do not freeze rankings.

The proposed worker query session should:

- Encode, retrieve, fuse, and group once at the current first-page budget. Freeze ranked file IDs, matching segment IDs, and bounded provenance; hydrate page metadata separately. Do not eagerly request 16,384 candidates or retain full passage text and image data.
- Bind the session to generation, encoder fingerprint, normalized query, filters, resolved people, overlay `settings.revision`, and retrieval budget. A versioned opaque cursor contains a random session ID and offset.
- Keep at most two sessions, with an 8 MiB total budget and a proposed ten-minute idle expiry. Sessions are disposable worker memory and never archive files.
- Return a structured `SearchResponse.RestartRequired` when the session expires, the worker restarts, or archive/people state changes. The interface clears its page/cursor cache and offers a fresh search, rather than silently reranking at an old offset.
- Treat deeper retrieval as an explicit fresh window that resets paging. Preserve ordinary empty-query SQL browsing. A bounded approximate window is not an exhaustive archive result count.

`SearchRequest.Cursor` can remain opaque. The affected components are Shared response contracts, Core session lifecycle and hydration, and App restart handling; Client transport can remain generic. This is a medium refactor, rather than a parameter change. Required tests cover duplicate/omitted page IDs, one encoding per session, generation/people invalidation, eviction/expiry, malformed cursors, cancellation, and worker restart. Query-cache-free certification must still measure fresh first-page searches separately from continuation pages.

## Bounded ANN tuning experiment

Reuse the verified immutable 100,000-vector graphs first; no re-embedding is needed. Preserve the existing failed report. For both distributions, use corpus seed 42 and independent query IDs **256–511 for tuning** and **512–767 for untouched evaluation**, with `query=true`. Both sets cover all 256 synthetic centroids. Changing the seed would also change those centroids, so query identity needs its own offset/domain and reference fingerprint.

Compare `(returned candidates, configured search expansion)`:

| Candidates | Expansion | Purpose |
|---:|---:|---|
| 512 | 128 | Reproduce the retained baseline; effective expansion is 512 |
| 1,024 | 128 | Current interface first-page budget; effective expansion is 1,024 |
| 1,024 | 2,048 / 4,096 / 8,192 | Increase exploration without increasing page hydration |
| 512 | Selected expansion | Separate returned-metadata cost from exploration cost |

Select a policy from tuning recall and latency, freeze it, then evaluate the untouched set once in loaded and mapped modes for every filter scenario. Report mean and minimum per-query Recall@50, not self-match or stored-key checks. Keep the 95% mean gate; a failed evaluation remains a failure. If the bounded exploration sweep cannot pass, investigate graph construction separately. Beam width is not a hard cap on distance calculations or storage reads. Serial and parallel graphs require separate quality measurements.

## Filtered search and older hardware

The current correction selects exact retrieval before opening ANN for at most 4,096 eligible semantic records, counts only valid vector-bearing segments, and refreshes mappings on generation changes. A later broad query can still initialize ANN. This avoids unnecessary HDD graph preloading for a small folder/person filter; it does not tune the cutoff or make exact scans free.

Compare exact cutoffs of 4,096, 8,192, and 16,384 with contiguous and scattered eligible sets. The current exact implementation scans every 20-byte vector record, then reads eligible FP16 blocks. Scattered blocks can cause costly HDD seeks despite a small eligible count. Native selective traversal also becomes more expensive as filters narrow.

Finally measure full-reader P95, CPU, resident mapped pages, I/O/page faults, model initialization, and first visible metadata on the older CPU with SSD and HDD. HDD preloading is currently limited to a 1.5 GiB index and available-memory checks; larger exploration budgets can increase page traffic. Strong-PC warm-cache timings do not establish the 8 GB, CPU-only target. Real labeled media relevance and identity precision remain separate acceptance gates.
