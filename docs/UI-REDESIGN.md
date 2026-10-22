# UI redesign validation

The desktop UI is a search-first, Windows 11 native design. It remains a native WPF application; backend interfaces, archive storage, the worker protocol, native inference, cursor limits, page-cache bounds and the 64 MB thumbnail budget are unchanged.

## Design

- **Shell.** A custom title bar holds the app identity, the search box (Ctrl+K), the drive status pill and caption buttons. A 76-DIP navigation rail offers Search, Browse, People, Drive, Settings and Eject. Content sits on a rounded layer over the window base, in the Windows 11 manner.
- **Type and color.** System Segoe UI Variable (Segoe UI on Windows 10), Segoe Fluent Icons or MDL2 Assets glyphs, 4 DIP control corners and 8 DIP cards. Light and dark palettes follow the Windows 11 base/layer/card tones with a single accent; high contrast maps every token to system colors. The previously bundled IBM Plex Sans has been removed.
- **Home.** A large search box with starter searches (recent searches, named people, then neutral examples), recent files from the newest-first browse window, named people, and per-type shortcuts with counts. An unprepared or disconnected drive shows a setup card instead.
- **Results and Browse** share one gallery. An empty query is newest-first browsing with a folder pane; a query shows "Results for …" and hides the pane. Tiles are a uniform 4:3 grid with a name, kind and date, and a chip that says why a file matched (person and time, speech, scene, exact words, page). File-type tabs, a Filters popover (date, folder, person) with removable chips, and an inline Exact words switch sit in one row.
- **Preview.** Docked at 1200 DIPs and wider; below that it slides over the results as a sheet with a scrim, so the gallery keeps its scroll position. Video and audio show a timeline with matching-moment markers that jump to each moment, clock-style times (0:06), sampled faces and a single possible-match note. Documents show extracted text on a page with the searched words highlighted. Previous/next result, Open, Show in folder and Copy path are always available.
- **No results** lists concrete fixes, for example turning off Exact words, showing all types, clearing other filters or searching deeper.
- **People.** Circular portraits, with named people first and unnamed face groups ("Add a name") after them. A person opens as a full page with name and alias fields, reference photos, Find, Merge, and inline yes/no answers for up to twelve uncertain faces. Review all matches… opens the existing full review dialog, including moving faces to a new person.
- **Drive.** Replaces Prepare drive and Folders. It shows status, first-run steps, staged preparation progress with pause/resume, folders that are already searchable, folders to prepare, OCR and verification options, capabilities on this PC, files needing attention, and Close and eject.
- **Settings.** Theme cards (Windows setting, Light, Dark), app updates and keyboard shortcuts.
- **Eject** asks for confirmation before closing and removing the drive.

Deliberate scope limits relative to the design mockup: paging remains explicit (Previous, Next and Search deeper over the existing 256-file pages) rather than infinite scroll; Browse is not grouped by month; date, folder and person filters share one popover; and the theme choice remains per session.

## Verification results

- Release build of `FindIt.App`: **0 warnings, 0 errors**.
- WPF interaction smoke: **109 checks passed** against the deterministic in-memory backend (`artifacts/redesign-ui/interaction.json`). It now also covers the Home landing and recent files, Browse reusing the loaded window, the results/browse mode switch, the person-filter chip and count, previous/next in the preview, the compact preview sheet and scrim, the uniform grid geometry, Settings theme cards, the Drive page, and the no-results fixes.
- Real-archive evidence smoke and paused playback at the matched moment passed against `people-media-fixture` (`evidence-smoke.json`, `paused-playback.json`).
- Unit tests covering the app project (`ContactSheetLayout`, `LibraryPresentation`, `PersonEvidence`, `Update`): 60 passed. `PublisherScriptProducesAnAcceptedSignedPackageWithoutBundlingItsKey` needs PowerShell 7 (`pwsh`), which was not installed on the validation machine; it is unrelated to the UI.
- Captures in light and dark at 1440×900, 1024×720 and 880×520 cover Home, Browse, photo, video and document results, no results, People, a person, Drive and Settings (`artifacts/redesign-ui/*.png`).

## Reproduce

```powershell
dotnet build src/FindIt.App/FindIt.App.csproj -c Release
$app = Resolve-Path src/FindIt.App/bin/Release/net10.0-windows/FindIt.App.exe
& $app --interaction-smoke "$PWD/artifacts/redesign-ui/interaction.json"
```

The renderer accepts `--render-ui`, `--render-open-archive`, `--render-page home|search|browse|people|drive|settings` (`folders` and `prepare` remain aliases), `--render-query`, `--render-kind`, `--render-select-first`, `--render-person-first`, `--render-exact`, `--width`, `--height` and `--dark`. Use an explicitly selected prepared test archive for archive-backed captures.

## Remaining manual checks

- Narrator and a complete physical keyboard-only walkthrough, including Ctrl+K, Ctrl+Enter and the timeline markers.
- Windows 11 snap layouts on the custom maximize button, title-bar dragging, and maximized insets on multiple monitors at mixed DPI.
- Windows 10 fallback rendering of Segoe UI and MDL2 glyphs, actual high contrast, and reduced motion.
- Physical drive removal/ejection and target-machine performance remain part of the existing release validation gates.

Video screenshots substitute an actual indexed frame for the native child HWND. Screenshots establish layout, and the separate playback smoke establishes playback behavior.
