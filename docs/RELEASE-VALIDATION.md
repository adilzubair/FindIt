# Release validation

The deliverable is a development preview. Passing the checks below does not establish the one-million-record, 8 GB laptop public-release promise.

## Verified in this workspace

- The solution builds with .NET 10 and real pinned native libraries.
- The actual published native launcher opened the self-contained WPF app through its verified host cache, without creating an archive. The published worker prepared two originals with the bundled on-drive model and returned both exact and semantic matches.
- Native Gemma text, image, audio, and joint multi-frame encoding; finite normalized vectors; CPU/GPU agreement and measured GPU fallback selection.
- Actual worker preparation and search on text, images, short video with sound, a PDF, a spreadsheet, and speech. Combined video produced visual frames, overlapping temporal embeddings, audio embeddings, and transcript content without per-file errors.
- The final automated release suite passed all **75 tests**: unchanged-file reuse, modification/deletion, relative-path relocation, Unicode, cancellation/resume, pack-tail rollback, incompatible encoders, people splitting/timestamp constraints, exact strings, corrupted publication recovery, worker protocol/termination, incremental indexes/tombstones/collection, and signed update validation. The report is `artifacts/tests/release-final.trx`.
- Real isolated LibVLC playback passed advancing-clock, pause/resume, seek and stop checks for both audio and video.
- Light/dark WPF captures from the application renderer; archive-backed media captures are stored under `artifacts/ui` when generated.
- A 100,000-vector synthetic USearch run produced a 117,257,344-byte index, query P50 2.60 ms and P95 4.61 ms on this development PC. The reported self-recall is a basic smoke check, not ANN Recall@50. These numbers exclude query encoding and do not represent the target laptop or external HDD.

Reports and TRX files are written under `artifacts/bench`, `artifacts/tests`, and `.tools/runtime-smoke`. Do not substitute this PC's timing for the release targets.

## Required before a public release

| Gate | Evidence still required |
|---|---|
| Portable round trip | Clean offline Windows 10 22H2/11 x64 machines; no installed runtimes/codecs; actual changed drive letters; NTFS and exFAT SSD/HDD |
| Scale | 100k, 500k, 1m records and graceful 2m; uncached queries; aggregate UI/worker resident mapped memory; 8 GB target laptop |
| Latency | Warm semantic P95 ≤2 s, exact P95 ≤200 ms; startup SSD ≤10 s / HDD ≤30 s; first model initialization separately |
| Relevance | Representative held-out queries; Hit@10 ≥85%; 512d within two points of 768d; ANN Recall@50 ≥95% against exact |
| People | Held-out identity precision ≥99% with coverage/unknown rejection; passenger/driver and unrelated-video-moment cases |
| Interaction | Physical scrolling frame timing, repeated seeks, Narrator, high contrast, reduced motion, keyboard-only, 200% DPI, 1366×768 |
| Faults | Physical surprise removal/reconnect, low space/full disk at every publication boundary, read-only media, locked originals, large/corrupt decoders |
| Distribution | Publisher code-signing certificate and trusted update key/channel; complete corresponding third-party redistribution/source materials and notices review |

## Implementation limits to account for

- Resume is durable at completed-file boundaries. Interrupting a long video or document currently restarts that file, while retaining completed files and the last published results.
- Preparation processes originals in a bounded serial pipeline. It does not yet provide a calibrated up-front space/ETA forecast from a full archive content inventory, or coarse whole-archive inference phases. Throughput should be measured on representative media before estimating completion.
- Text passages currently use bounded character windows rather than exact tokenizer-based 512-token boundaries. Office content uses dedicated managed Open XML parsing instead of the proposed Open XML SDK.
- People review has bounded first-page limits (1,000 groups and 2,000 observations per person). Very large identity catalogs need additional backend paging before certification.
- Sampling is finite. One-frame-per-second scenes and two-frame-per-second face analysis cannot guarantee capture of every brief appearance. Face thresholds are implementation defaults and require held-out calibration.
- The preview contains no publisher signing credentials. Offline update verification is implemented, but no public update service is configured. A manual public release must not be described as signed unless the package is actually signed and verified.

Camera RAW, legacy Office binary formats, original-file editing, cloud connectors and cross-platform binaries remain outside this release scope. Unsupported and decoder-failed files stay searchable by filename and report their preparation issues.
