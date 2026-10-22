# FindIt

Prepare an external drive on a powerful Windows PC, then search it offline on another PC. The application, models, previews, catalog, and people metadata travel with the drive. Originals stay in their existing folders.

This repository contains a working **0.1.0 preview**, including native inference and a portable package builder. It is not yet a certified public release. See [release validation](docs/RELEASE-VALIDATION.md) for measured results and remaining gates.

## Run the portable application

1. Copy **FindIt.exe and the entire FindIt folder** from `artifacts/portable` to the root of your external drive. Keep them together. Leave your existing Photos, Videos, and Documents folders in place.
2. Launch `FindIt.exe`. Choose **Prepare drive**, select folders and OCR languages, and explicitly start preparation.
3. Search by filename, exact document text, or meaning. Filter by type, folder, date, and saved people. Select a result for its preview and matching moments.
4. Name a prepared face group or enroll a reference photo from this drive. Review suggested memberships before relying on a person's identity. Person/action results are candidate matches with temporal evidence.
5. Use **Close and eject** before moving the drive. On the next PC, launch the same executable. No installed .NET runtime, Python, browser runtime, or host codecs are required by the package.

Windows 10 22H2 and Windows 11 x64 are the intended targets. This preview was built and exercised on Windows 11; the clean Windows 10 and older 8 GB laptop certification remains outstanding. Do not equate a successful developer-machine run with those compatibility gates.

The native launcher copies **public application/runtime files only** to `%LOCALAPPDATA%\FindIt\runtime`. This disposable cache lets the UI survive drive removal and release executable handles before ejection. Models, originals, search indexes, extracted content, people, and preferences remain on the drive. The runtime cache can be removed while FindIt is closed. FindIt also records which model files it has already checksum-verified in `%LOCALAPPDATA%\FindIt\verification`, so later launches skip re-reading them from the drive; deleting that folder only means the next launch verifies them again.

A native startup window appears before reading the drive's application files. First launch on a PC or after an update shows copy and verification progress and can be cancelled; later launches reuse the verified cache. Repeated clicks bring the startup or existing library window forward instead of opening another copy. Tiny disposable process records in `%LOCALAPPDATA%\FindIt\instances` support this behavior without keeping the USB launcher running. They contain no library content or filenames and can also be removed while FindIt is closed. Open the root `FindIt.exe` to use this behavior; launching the internal application directly bypasses the launcher.

## Build

Build prerequisites: Windows x64, PowerShell 7.2+, .NET SDK 10.0.400, and network access for the initial dependency download. A compiler, CMake, Ninja, pinned native sources, models, codecs, and licenses are fetched into the workspace; Visual Studio and administrator access are unnecessary.

```powershell
./scripts/Build.ps1
./scripts/Publish-Portable.ps1 -SkipBuild -Zip
./scripts/Test-Portable.ps1 -PackageRoot artifacts/portable -ReportPath artifacts/published-check.json
```

The first command verifies artifact checksums, builds the baseline CPU libraries, launcher and managed solution, then runs integration tests. The second publishes self-contained Windows applications, includes the models/codecs, creates per-file runtime checksums, and writes `artifacts/portable` and `artifacts/portable.zip`. The third tests real model inference through the published worker against temporary originals; it refuses existing archives and cleans its test catalog afterward. Allow several GB of workspace/free package space. Subsequent builds can use `-SkipDependencies` after a complete first build.

Use `-BuildArtifactsPath artifacts/my-build` on both build and publish scripts to isolate intermediate files and binaries from a running source application. Builds run the worker integration tests against that same isolated output. Normal builds and portable publication explicitly exclude the validation-only fault hooks.

Run `./scripts/Test-Launcher.ps1` separately in an interactive Windows session to verify the native startup UI, rapid repeated clicks, cancellation, crash recovery, cold/warm cache behavior, separate libraries and corrupt-file rejection. It builds a small delayed-window fixture and uses a fresh workspace cache; its startup timing does not certify physical USB or clean-machine performance.

To run source builds against a selected archive root:

```powershell
dotnet run --project src/FindIt.App -- --root X:\
```

Never point preparation at a folder you do not intend to catalog. Files are opened for reading; preparation writes only beneath `FindIt/archive`. Symlinks, junctions, recursive application folders, and placeholders requiring cloud downloads are excluded.

## What is implemented

- A light/dark WPF gallery, paged metadata, recycled contact-sheet rows, bounded thumbnail cache, keyboard navigation, people review, preparation controls, and disconnected states.
- Isolated reader, preparer, and LibVLC playback workers over cancellable framed JSON-RPC.
- Real EmbeddingGemma 2 Q8 text/image/video/audio encoding, 512-dimensional normalized FP16 canonical vectors, USearch mapped indexes, filtered exact retrieval, SQLite FTS5, and reciprocal rank fusion.
- Stable ranked search windows with page-only metadata loading, explicit session restart and **Search more**. ANN exploration is tuned separately from returned results; see [the 100k held-out measurements and remaining scope](docs/search-next-validation.md).
- Streaming media extraction, frame/face sampling, temporal windows bounded by detected scene cuts, overlapping audio windows, Whisper small with offline voice activity detection, PDF rendering/OCR, and text/Open XML extraction.
- YuNet/SFace face observations, reference enrollment, named groups, aliases, membership confirmations/rejections, and timestamp-constrained people search. Person results show the sampled context frame and face crop and open playback at that observation. Multiple selected people must share a sampled frame.
- Portable relative paths and library IDs, resumable file transactions, immutable generations, checksummed publication, retained previous results, bounded incremental indexes, and collection of unused application artifacts.
- Explicit signed update checks and offline update installation. There is no telemetry or background network check. Publisher configuration and signing credentials are intentionally absent from the development preview.

Read [architecture](docs/ARCHITECTURE.md), [release validation](docs/RELEASE-VALIDATION.md), and [third-party notices](THIRD-PARTY-NOTICES.md) before distributing a build. Model and dependency revisions/checksums are recorded in `models/manifest.json` and `scripts/Fetch-Dependencies.ps1`.

Existing prepared libraries remain readable. Explicitly preparing with this version refreshes extraction when its policy changes, including the new video scene boundaries. Sampling and cut detection can miss brief appearances or edits; person/action results remain possible matches for inspection.

If an original's size or modification time changed after preparation, FindIt blocks its stale preview and asks you to prepare again. Use **Verify contents** to detect replacements whose size and timestamp were deliberately preserved.

For an existing portable drive, close FindIt and merge the new `FindIt.exe` and `FindIt` application folder into the drive root. Preserve `FindIt/archive` and all original folders. The new package contains no prepared archive. Open and search first; start preparation explicitly when ready to refresh the media extraction policy.

## Measurements and updates

```powershell
dotnet run --project tools/FindIt.Bench -- --root X:\ --queries queries.txt --repeat 5 --output artifacts/reader.json
dotnet run --project tools/FindIt.Bench -- --root artifacts/scale --ann-records 100000 --distribution clustered-512 --query-count 64 --output artifacts/ann.json
```

These commands produce measurements, not a certification. The scale benchmark uses independent queries and exhaustive FP16 cosine top-50 references, with unfiltered, 10%, and 1% eligibility sets in mapped and loaded processes. Run both documented synthetic distributions and retain failures. See [scale methodology](docs/scale-benchmark.md) and [recovery validation](docs/recovery-validation.md). The reader report excludes UI and playback memory. Uncached query latency, real relevance, identity precision, physical drive behavior, and accessibility need their own acceptance runs.

For publisher-managed updates, place a trusted ECDSA **public** key and an HTTPS manifest URL in `FindIt/update-source.json` using properties `publicKeyPem` and `manifestUrl`. Keep the private key outside the drive and source tree. `scripts/New-OfflineUpdate.ps1` creates a signed update ZIP from a published application version. The updater verifies the signature, file hashes, launcher manifest, version, and safe paths before activation; it cannot replace models or archive data. Without publisher configuration, the UI reports update checks as unavailable and makes no network request.
