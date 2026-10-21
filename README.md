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

The native launcher copies **public application/runtime files only** to `%LOCALAPPDATA%\FindIt\runtime`. This disposable cache lets the UI survive drive removal and release executable handles before ejection. Models, originals, search indexes, extracted content, people, and preferences remain on the drive. The runtime cache can be removed while FindIt is closed.

## Build

Build prerequisites: Windows x64, PowerShell 7.2+, .NET SDK 10.0.400, and network access for the initial dependency download. A compiler, CMake, Ninja, pinned native sources, models, codecs, and licenses are fetched into the workspace; Visual Studio and administrator access are unnecessary.

```powershell
./scripts/Build.ps1
./scripts/Publish-Portable.ps1 -SkipBuild -Zip
./scripts/Test-Portable.ps1 -PackageRoot artifacts/portable -ReportPath artifacts/published-check.json
```

The first command verifies artifact checksums, builds the baseline CPU libraries, launcher and managed solution, then runs integration tests. The second publishes self-contained Windows applications, includes the models/codecs, creates per-file runtime checksums, and writes `artifacts/portable` and `artifacts/portable.zip`. The third tests real model inference through the published worker against temporary originals; it refuses existing archives and cleans its test catalog afterward. Allow several GB of workspace/free package space. Subsequent builds can use `-SkipDependencies` after a complete first build.

To run source builds against a selected archive root:

```powershell
dotnet run --project src/FindIt.App -- --root X:\
```

Never point preparation at a folder you do not intend to catalog. Files are opened for reading; preparation writes only beneath `FindIt/archive`. Symlinks, junctions, recursive application folders, and placeholders requiring cloud downloads are excluded.

## What is implemented

- A light/dark WPF gallery, paged metadata, recycled contact-sheet rows, bounded thumbnail cache, keyboard navigation, people review, preparation controls, and disconnected states.
- Isolated reader, preparer, and LibVLC playback workers over cancellable framed JSON-RPC.
- Real EmbeddingGemma 2 Q8 text/image/video/audio encoding, 512-dimensional normalized FP16 canonical vectors, USearch mapped indexes, filtered exact retrieval, SQLite FTS5, and reciprocal rank fusion.
- Streaming media extraction, frame/face sampling, overlapping temporal/audio windows, Whisper small with offline voice activity detection, PDF rendering/OCR, and text/Open XML extraction.
- YuNet/SFace face observations, reference enrollment, named groups, aliases, membership confirmations/rejections, and timestamp-constrained people search.
- Portable relative paths and library IDs, resumable file transactions, immutable generations, checksummed publication, retained previous results, bounded incremental indexes, and collection of unused application artifacts.
- Explicit signed update checks and offline update installation. There is no telemetry or background network check. Publisher configuration and signing credentials are intentionally absent from the development preview.

Read [architecture](docs/ARCHITECTURE.md), [release validation](docs/RELEASE-VALIDATION.md), and [third-party notices](THIRD-PARTY-NOTICES.md) before distributing a build. Model and dependency revisions/checksums are recorded in `models/manifest.json` and `scripts/Fetch-Dependencies.ps1`.

## Measurements and updates

```powershell
dotnet run --project tools/FindIt.Bench -- --root X:\ --queries queries.txt --repeat 5 --output artifacts/reader.json
dotnet run --project tools/FindIt.Bench -- --root artifacts/scale --ann-records 100000 --output artifacts/ann.json
```

These commands produce measurements, not a certification. The synthetic self-match metric is not Recall@50 against an exact baseline. The reader report excludes UI and playback memory. Uncached query latency, real relevance, identity precision, physical drive behavior, and accessibility need their own acceptance runs.

For publisher-managed updates, place a trusted ECDSA **public** key and an HTTPS manifest URL in `FindIt/update-source.json` using properties `publicKeyPem` and `manifestUrl`. Keep the private key outside the drive and source tree. `scripts/New-OfflineUpdate.ps1` creates a signed update ZIP from a published application version. The updater verifies the signature, file hashes, launcher manifest, version, and safe paths before activation; it cannot replace models or archive data. Without publisher configuration, the UI reports update checks as unavailable and makes no network request.
