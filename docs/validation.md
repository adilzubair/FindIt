# Validation status

FindIt is a **development preview**. Everything below was measured on development hardware (Intel Core Ultra 9 275HX, 32 GB RAM, Windows 11) and an 8 GB FAT32 USB stick. It shows that the design works end to end; it does not certify the public-release targets — one million records on an 8 GB, CPU-only laptop — listed under [release gates](#release-gates).

Methods for each check are in [scale benchmark](scale-benchmark.md), [recovery validation](recovery-validation.md), [drive validation](drive-validation.md), [people evidence](people-evidence.md) and [development](development.md).

## Verified

**Build and tests**

- The solution builds with .NET 10 and the pinned native libraries with zero warnings.
- The integrated suite passes about 200 tests, including Unicode archive paths longer than 260 characters, conservative sampled-face joins, source-time video windows, extraction-policy refresh, stale-preview rejection, resumable native graph checkpoints, buffer ownership, filtered exact search and stable search sessions. Tests that need the native runtime or LibVLC report *inconclusive* when it is absent.
- The WPF interaction harness passes over 100 checks against an in-memory archive, covering Home, Browse, search results, filters, the preview sheet, People, Drive, Settings and the no-results guidance. Light and dark captures were reviewed at 1440×900, 1366×768, 1024×720 and 880×520.

**Inference and preparation**

- Native EmbeddingGemma text, image, audio and joint multi-frame encoding produce finite normalized vectors; CPU and GPU agree numerically and the GPU fallback is chosen by measurement.
- The published worker prepares text, images, short video with sound, PDFs, spreadsheets and speech, producing visual frames, overlapping temporal and audio embeddings and transcripts without per-file errors.
- An 18-file generated gallery passes native checks for short and long shots, half-second cuts, sparse/VFR footage, delayed video or audio, timestamped speech, reference enrollment and exact face-evidence preview.
- Isolated LibVLC playback passes clock, pause/resume, seek and stop checks for audio and video.

**Recovery**

- 25 real-worker recovery scenarios pass: kills around file, checkpoint and publication boundaries, sealed-pack immutability, deletion while interrupted, a valid post-pointer switch, simulated full disk, real Windows sharing/denial, corruption fallback and retention of the previous verified generation. A normal (non-fault) worker ignores valid fault settings.

**Portable drive round trips**

- On a physical FAT32 USB stick, the packaged reader passes exact and semantic retrieval, document locators, timestamped speech and video, named-person evidence, incremental updates, cancellation/resume and decoder-error checks. Updating the application in place preserved every pre-existing file (originals, archive, people data, models, older versions) by SHA-256, length and modification time.
- The prepared drive was also used successfully on a second PC (16 GB RAM, weaker GPU). Its timings were not recorded, so this is a reported round trip, not the planned 8 GB certification.

**Launcher**

- The native launcher passes 33 fixture checks: repeated clicks, clicks during a delayed start, restoring a minimized window, cancellation, abandoned-mutex recovery, incomplete-cache repair, warm-cache reuse, concurrent libraries sharing a cold cache, Unicode paths and corrupt-package rejection.
- The startup window appeared after about 270–315 ms on the development PC and the USB stick; a warm handoff to the application took about 730 ms. Five rapid launches produce one application window.

## Search quality and scale

ANN recall is measured against exhaustive FP16 cosine top-50 references with independent queries, in fresh mapped and loaded processes. See [scale benchmark](scale-benchmark.md) for method.

The reader's search expansion of 8,192 was chosen on a separate 256-query tuning split, then evaluated once on an untouched 256-query split of the 100k serial graphs:

| Distribution | Eligibility | Mean Recall@50 | Worst query | Warm P95 (mapped / loaded) |
|---|---|---:|---:|---:|
| Isotropic (hard) | All | 97.17% | 90% | 101 / 90 ms |
| Isotropic (hard) | 10% | 100% | 100% | 143 / 121 ms |
| Isotropic (hard) | 1% | 100% | 100% | 125 / 121 ms |
| Clustered | All / 10% / 1% | 100% | 100% | 33–135 ms |

Before tuning, the hard isotropic set failed the 95% gate (30.06% unfiltered at 512 candidates). The clustered 500k and one-million-record graphs passed every scenario at the earlier default expansion; they have not been re-measured with the tuned policy. Streaming the million-record index from short ASCII paths cut peak loaded memory from 2.48 GB to 1.41 GB with identical recall.

These numbers cover the native index only. They exclude query encoding, SQLite, the interface, cold storage and older hardware, and synthetic vectors say nothing about semantic relevance.

## Release gates

| Gate | Evidence still required |
|---|---|
| Portable round trip | Clean offline Windows 10 22H2/11 x64 machines with no installed runtimes or codecs; changed drive letters; NTFS and exFAT SSD/HDD |
| Scale | 100k, 500k, 1m records and graceful 2m; uncached queries; aggregate UI/worker resident memory on the 8 GB target laptop |
| Latency | Warm semantic P95 ≤ 2 s, exact P95 ≤ 200 ms; startup SSD ≤ 10 s / HDD ≤ 30 s; first model initialization measured separately |
| Relevance | Representative held-out queries; Hit@10 ≥ 85%; 512-d within two points of 768-d; ANN Recall@50 ≥ 95% against exact |
| People | Held-out identity precision ≥ 99% with coverage and unknown rejection; passenger/driver and unrelated-moment cases |
| Interaction | Physical scrolling frame timing, repeated seeks, Narrator, high contrast, reduced motion, keyboard-only, 200% DPI, 1366×768 |
| Faults | Physical surprise removal, low space at every publication boundary, read-only media, locked originals, large or corrupt decoders |
| Distribution | Code-signing certificate and trusted update key/channel; complete third-party redistribution and source-material review |

## Known issues

- An initial playback check after a fresh install once failed to open the player; later cached runs passed. The cause has not been established.
- A low-recall ANN search still reports semantic search as used. An empty semantic result is not proof that content is absent.

## Implementation limits

- Resume is durable at completed-file boundaries. Interrupting a long video or document restarts that file, while completed files and the last published results are kept.
- Preparation is a bounded serial pipeline without a calibrated up-front space/time forecast. Measure throughput on representative media before estimating completion.
- Text passages use bounded character windows rather than exact 512-token boundaries. Office files use a dedicated managed Open XML parser.
- People review pages are bounded (1,000 groups, 2,000 observations per person). Very large identity catalogs need backend paging.
- Ranked search returns a bounded selection, not an exhaustive count. Continuation pages keep their frozen ranking; **Search more** starts a fresh, larger window that may reorder files.
- Sampling is finite: one frame per second for scenes and two per second for faces cannot capture every brief appearance. Face thresholds are defaults that need held-out calibration.
- Scene cuts are detected heuristically; undetected edits remain possible. Container duration metadata can be ambiguous, so end-of-file bounds are estimates.
- Packages are unsigned and no public update service is configured. Do not describe a build as signed unless it was actually signed and verified.

Camera RAW, legacy binary Office formats, editing originals, cloud connectors and non-Windows builds are out of scope. Unsupported or undecodable files stay searchable by name and report their issue.
