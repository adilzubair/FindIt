# UI redesign validation

The redesign uses Playbook-inspired content spacing, Eagle-inspired library/inspector organization, and Peakto-inspired matching moments. The application remains a native WPF desktop application.

## Implemented

- Shared IBM Plex Sans typography and light/dark palettes; System, Light, and Dark session appearance choices, including native titlebar updates.
- A 216-DIP sidebar, persistent search, media-type tabs, filter popover, removable chips, and accurate cursor-paged result ranges.
- Fitted 4:3 thumbnails with compact/comfortable/large gallery sizing, recycled virtualized rows, scroll anchors, and grid/list selection states.
- A resizable docked inspector at window widths of at least 1200 DIPs; a Back to results detail view below that width. Playback controls, moments, and details scroll when needed, with file actions available at the bottom.
- Timestamped playback, seeking, extracted document text, matching excerpts, and clear possible-person/action qualifications.
- Virtualized people portraits, person details, existing face-review/merge workflows, folder browsing actions, and reorganized preparation controls.
- Matching dialogs, menus, calendars, scrollbars, notices, empty states, and missing-preview fallbacks.
- Stable filter IDs/paths on refresh; retained submitted queries during paging; cancellation-safe thumbnail reloads; and reopening a previously selected list result after closing its preview.

Backend interfaces, archive storage, the worker protocol, native inference, cursor limits, page-cache bounds, and the 64 MB thumbnail budget are preserved.

## Verification results

- Release solution build: succeeded with **0 warnings and 0 errors**.
- Regression suite: **97 passed, 0 failed, 0 skipped**. This includes 22 new focused gallery/filter cases.
- WPF interaction smoke: **54 checks passed** against a deterministic in-memory backend. It exercises the actual controls, handlers, measured row extents, responsive layouts, search cancellation, and keyboard arrows/Escape.
- Actual isolated LibVLC video and audio playback both passed, including advancing clock, pause/resume, seek, and stop. Separate reports are recorded for each.
- 24 after-captures and 8 before-captures cover both themes, mixed media, inspectors, People, Folders, preparation, no results, and an unprepared drive.
- Window sizes checked: 1400×900, 1366×768, and 880×520. Captures also exercise 144/192-DPI bitmap output.

Reports and images are generated under:

- `artifacts/tests/ui-redesign.trx`
- `artifacts/ui/redesign-before/`
- `artifacts/ui/redesign-after/interaction-smoke.json`
- `artifacts/ui/redesign-after/playback.json` and `playback-audio.json`
- `artifacts/ui/redesign-after/`

These ignored artifacts are local verification outputs, not checked-in product assets.

The subsequent [search session update](search-next-validation.md) adds **Search more**, ranked-selection labeling, and a query-preserving restart path. Its separate verification passed 198 regression tests and 99 WPF interaction checks; compact light/dark captures and `interaction.json` are retained in `artifacts/search-next-ui`. These later results do not replace the remaining physical accessibility/display checks below.

## Reproduce

Build with the repository's .NET 10 SDK and existing dependencies:

```powershell
dotnet build FindIt.slnx -c Release --no-restore -m:1 --disable-build-servers
dotnet test tests/FindIt.Tests/FindIt.Tests.csproj -c Release --no-build --no-restore -m:1 --disable-build-servers --results-directory artifacts/tests
```

Run UI interaction checks without opening or preparing any real archive:

```powershell
$app = Resolve-Path src/FindIt.App/bin/Release/net10.0-windows/FindIt.App.exe
& $app --interaction-smoke "$PWD/artifacts/ui/redesign-after/interaction-smoke.json"
```

The existing renderer accepts `--render-ui`, `--render-open-archive`, `--render-page`, `--render-kind`, `--render-select-first`, `--width`, `--height`, and `--dark`. The new `--render-exact` flag makes exact-word no-results captures reproducible. Use an explicitly selected prepared test archive for archive-backed captures. `--playback-smoke` and `--playback-report` continue to exercise the real isolated player.

## Remaining manual checks

- Narrator and a complete physical keyboard-only walkthrough, including Ctrl+K and Ctrl+Enter. The automated harness routes arrow/Escape events but does not synthesize physical modifier keys.
- Actual Windows high contrast and reduced-motion settings. High-contrast system brushes and motion-free custom controls are implemented, but those OS configurations were not changed during validation.
- Physical monitor DPI transitions and mixed-DPI multi-monitor behavior. The renderer's 144/192-DPI bitmap output increases resolution while retaining layout in DIPs; it does not establish those physical display checks.
- Physical drive removal/ejection and target-machine performance remain part of the existing release validation gates.

Video screenshots substitute an actual indexed frame for the native child HWND. Screenshots establish layout, and the separate playback smoke establishes playback behavior.
