# Release validation

The deliverable is a development preview. Passing the checks below does not establish the one-million-record, 8 GB laptop public-release promise.

## Verified in this workspace

- The solution builds with .NET 10 and real pinned native libraries.
- The actual published native launcher opened the self-contained WPF app through its verified host cache, without creating an archive. The published worker prepared two originals with the bundled on-drive model and returned both exact and semantic matches.
- Native Gemma text, image, audio, and joint multi-frame encoding; finite normalized vectors; CPU/GPU agreement and measured GPU fallback selection.
- Actual worker preparation and search on text, images, short video with sound, a PDF, a spreadsheet, and speech. Combined video produced visual frames, overlapping temporal embeddings, audio embeddings, and transcript content without per-file errors.
- The preview.3 USB baseline passed **114 regression tests**, 19 main physical-drive checks, 7 supplemental checks and a final 10-check packaged-reader pass. The user subsequently reported successful operation on a second PC with 16 GB RAM and less capable graphics. Its CPU, Windows version, offline/runtime conditions and timings were not recorded; this is a reported round trip, not the planned 8 GB CPU-only certification. Previous reports remain under `artifacts/usb-e2e`.
- The integrated development suite passed **156 tests**, zero skipped, including deep Unicode archive paths exceeding 260 characters, conservative sampled-face joins, source-time video windows, and explicit extraction-policy refresh. The integration TRX is `artifacts/tests/findit.trx`.
- **25 real-worker recovery scenarios passed**, including kills around file/checkpoint/publication boundaries, sealed-pack immutability, deletion while interrupted, a valid post-pointer switch, simulated full disk, real Windows replacement denial/sharing, corruption fallback and previous-verified-generation retention. A normal-worker check also proved fault settings have no effect. Reports: `artifacts/recovery-next-final/recovery-report.json` and `build-guard.json`. See [method and limitations](recovery-validation.md).
- Real native preparation of an 18-file generated gallery passed eight stages: short-shot and long-shot clips, half-second shot origins, sparse/VFR footage, delayed video/audio, timestamped speech, enrollment and exact face-evidence preview requests. Report: `artifacts/people-native-check.json`. This small regression corpus does not establish identity precision or real-world relevance.
- Real isolated LibVLC playback passed advancing-clock, pause/resume, seek and stop checks for both audio and video.
- Updated native playback passed paused montage/VFR/delayed-video/delayed-audio/audio checks, plus a real-person poster/crop/Play/Pause check. The WPF interaction harness passed **56 checks** and application-rendered light/dark captures were reviewed at laptop and desktop sizes. Reports and captures: `artifacts/next-ui-final`. Initial failed diagnostics remain separately retained; the physical accessibility/hardware gates remain open.
- Light/dark WPF captures from the application renderer; archive-backed media captures are stored under `artifacts/ui` when generated.
- The replacement synthetic benchmark compares independent queries against exhaustive FP16 cosine top-50 references. Structured 100k and 500k datasets passed all unfiltered/10%/1% eligibility scenarios in mapped and loaded processes. The hard 100k isotropic dataset **failed**: 30.06% unfiltered and 90.16% at 10% eligibility, below the 95% gate. Both outcomes are retained. See [scale measurements and scope](scale-benchmark.md); ANN certification remains open. These measurements exclude query encoding, catalog/UI work and physical-drive/older-PC behavior.

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
- Scene cuts are detected heuristically. New joint video encodings stay within detected shots and source-time windows; undetected edits remain possible. Duration metadata can be ambiguous in offset containers, so metadata-only EOF bounds are estimates. Existing libraries are read without rewriting; explicit preparation refreshes extraction policies.
- The preview contains no publisher signing credentials. Offline update verification is implemented, but no public update service is configured. A manual public release must not be described as signed unless the package is actually signed and verified.

Camera RAW, legacy Office binary formats, original-file editing, cloud connectors and cross-platform binaries remain outside this release scope. Unsupported and decoder-failed files stay searchable by filename and report their preparation issues.
