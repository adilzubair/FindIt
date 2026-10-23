# Development

## Prerequisites

- Windows 10 22H2 or Windows 11, x64
- [.NET SDK 10.0.400](https://dotnet.microsoft.com/download) (pinned in `global.json`)
- PowerShell 7.2 or later (`pwsh`)
- Network access for the first dependency download, and several GB of free space

Visual Studio and administrator rights are not needed. `scripts/Fetch-Dependencies.ps1` downloads a portable LLVM-MinGW compiler, CMake, Ninja, pinned native sources, FFmpeg, PDFium, the Microsoft C++ runtime, models and their licenses into `.tools/` and `runtime/`. Every download is checked against a SHA-256 pinned in the script or in `models/manifest.json`; a mismatch is left as `.partial` and never loaded.

## Build and test

```powershell
./scripts/Build.ps1                       # fetch, build native + managed + launcher, run tests
./scripts/Build.ps1 -SkipDependencies     # later builds, once runtime/ is complete
./scripts/Build.ps1 -SkipTests
```

`-BuildArtifactsPath artifacts/my-build` puts intermediate files and binaries in an isolated folder, so a build never overwrites an application you are running. Tests then run against that worker.

For quick managed-only iteration, no native toolchain is needed:

```powershell
dotnet build FindIt.slnx -c Release
dotnet test tests/FindIt.Tests/FindIt.Tests.csproj -c Release --no-build
```

Tests that need the native runtime, LibVLC or `pwsh` report *inconclusive* when it is missing. This is what CI runs.

Run the application from source against a chosen archive root:

```powershell
dotnet run --project src/FindIt.App -- --root X:\
```

Never point preparation at a folder you do not intend to catalog. Files are opened for reading only; preparation writes only beneath `FindIt/archive`.

## Portable package

```powershell
./scripts/Publish-Portable.ps1 -SkipBuild -Zip
./scripts/Test-Portable.ps1 -PackageRoot artifacts/portable -ReportPath artifacts/published-check.json
```

The publisher writes `artifacts/portable` and `artifacts/portable.zip`, using the version in `Directory.Build.props` unless `-Version` is given. It publishes self-contained applications, copies models and native tools, and writes a per-file `package.sha256` that the launcher verifies. `-SigningThumbprint` Authenticode-signs FindIt's own binaries with a certificate from the current user's store. Fault-injection builds are refused.

`Test-Portable.ps1` runs real model inference through the published worker on temporary originals. It refuses a package that already contains an archive and removes its test catalog afterwards.

`scripts/Test-Launcher.ps1` must run in an interactive session. It builds a delayed-window fixture and checks the native startup window, repeated clicks, cancellation, crash recovery, cold and warm caches, separate libraries and corrupt-package rejection in a fresh disposable cache.

## Releases

1. Update `<Version>` in `Directory.Build.props` and move the `Unreleased` notes in `CHANGELOG.md` under that version.
2. Merge to `main`, then push a tag `v<version>`, for example `v0.1.0-preview.9`.
3. The **Release** workflow builds everything, runs the tests, publishes the portable ZIP and creates a **draft** GitHub release with the ZIP, its SHA-256 and the changelog notes.
4. Review the draft and publish it.

The workflow fails if the tag does not match `Directory.Build.props`. GitHub limits release assets to 2 GiB; the portable ZIP is about 1.7 GiB because of the bundled models.

Signed in-app updates are optional. Place an ECDSA **public** key and HTTPS manifest URL in `FindIt/update-source.json` (`publicKeyPem`, `manifestUrl`) on the drive, then use `scripts/New-OfflineUpdate.ps1` to create a signed update ZIP from a published version folder. Keep the private key outside the drive and the source tree. Without that file the app reports update checks as unavailable and makes no network request.

## Diagnostic harnesses

`FindIt.App` accepts flags used for automated UI checks. They never start preparation.

| Flag | Purpose |
|---|---|
| `--interaction-smoke <report.json>` | Drives the UI against a deterministic in-memory archive; not evidence that a real drive works |
| `--render-ui <image.png>` | Renders a page to an image. Add `--render-page home\|search\|browse\|people\|drive\|settings`, `--width`, `--height`, `--dark`, `--render-dpi` |
| `--render-open-archive` | With `--render-ui`, renders a real prepared archive. Add `--render-query`, `--render-kind`, `--render-select-first`, `--render-person-first`, `--render-exact` |
| `--evidence-smoke <report.json>` | Opens a real archive, searches `--render-query` and checks person evidence and Play/Pause |
| `--playback-smoke <media>` / `--paused-playback-smoke <media>` | Checks the isolated LibVLC player's clock, pause, seek and stop; `--playback-report`, `--playback-start-ms` |

Example:

```powershell
dotnet build src/FindIt.App/FindIt.App.csproj -c Release
& src/FindIt.App/bin/Release/net10.0-windows/FindIt.App.exe --interaction-smoke "$PWD/artifacts/ui/interaction.json"
```

## Validation tools

| Tool | What it measures | Guide |
|---|---|---|
| `tools/FindIt.Bench` | Reader latency on a prepared drive, and synthetic ANN recall/latency/memory at 100k–2m records | [scale-benchmark.md](scale-benchmark.md) |
| `tools/FindIt.RecoveryCheck` | Crash, disk-full, sharing and corruption recovery with real workers on generated archives | [recovery-validation.md](recovery-validation.md) |
| `tools/FindIt.DriveCheck` | End-to-end checks on a physical drive with a fixed test corpus | [drive-validation.md](drive-validation.md) |

These tools write only beneath `artifacts/` or their own marked fixtures, and refuse personal archives. Their results are measurements, not certifications; see [validation status](validation.md).

## Code conventions

- Keep the UI free of inference; heavy or native work belongs in the worker.
- Pin every external download by URL and SHA-256, and prefer URLs that stay put: tagged releases, commit archives and, for FFmpeg, BtbN's month-end `autobuild-YYYY-MM-DD` releases (daily builds are deleted after a few weeks).
- Fault-injection code compiles only with `-p:FindItEnableFaultInjection=true` and must never reach a published package.
- Add a regression test with each fix. Tests that need optional native pieces should call `Assert.Inconclusive` when they are missing.
