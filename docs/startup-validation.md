# Startup feedback and repeated launches

The root native launcher now shows a small Windows startup window before reading the drive's version file or application manifest. A background thread counts and copies public runtime files and verifies their checksums. Copy progress measures bytes, verification progress measures files, and unknown-length phases use an indeterminate bar. Setup can be cancelled without publishing `.complete`; the next launch repairs an incomplete copy. A marker included in a source package is never copied or trusted. Native controls follow Windows system colors, DPI scaling and the client-area animation preference.

A per-library startup mutex serializes rapid clicks. A separate per-runtime mutex protects a cold cache shared by different libraries. Once the actual application window appears, the launcher exits so its executable does not keep the USB drive open. Subsequent clicks use a small disposable process record under `%LOCALAPPDATA%\FindIt\instances` to restore that window. The record contains the PID, process creation time and executable file identity; it contains no library paths, filenames or content. Creation time prevents PID reuse from targeting another process, and executable file identity works even when Windows redirects a packaged host's cache path. Closed/crashed processes do not prevent relaunching. These guarantees apply when opening the root launcher; direct internal app launches bypass it.

## Workspace evidence, 22 October 2026

- The final binary compiles with `-Wall -Wextra -Werror`, and the CMake/resource build also succeeds.
- `scripts/Test-Launcher.ps1` passed **33 checks**, including ten repeated startup clicks, five clicks during delayed app startup, restoring a minimized app, cancellation, abandoned-mutex recovery, incomplete-cache repair, warm-cache reuse, concurrent separate libraries sharing a cold cache, Unicode/spaces in paths, argument forwarding and corrupt-package rejection. The source fixture intentionally delays showing its window; the script uses real file copying and SHA-256 verification in a fresh disposable workspace cache.
- The final test observed the first native window after **268 ms** on this development PC. Report and UI capture: `artifacts/startup/native-final-identity/report.json` and `startup.png`. This is not physical USB cold-copy or clean-machine certification.
- The actual published preview.5 WPF application passed handoff, **five repeat launches producing one application**, and clean closing against an isolated copied gallery. Warm-cache handoff was approximately **729 ms**. Report: `artifacts/startup/actual-package.json`.
- An earlier actual-app repeat test exposed Windows package cache-path redirection. Literal path-prefix matching was replaced by executable file-identity matching, and both final fixture and actual-app tests passed afterward. Earlier failed diagnostics remain retained.
- A separate initial playback smoke attempt failed the existing `real-person-evidence-and-player-open` assertion. Its report remains `artifacts/startup/actual-evidence-initial-failed.json`; this launcher change does not claim to resolve or certify that previously recorded first-start playback issue.

The resulting launcher is approximately 291 KB and works with the existing preview.5 application manifest/cache. No application republish or archive preparation is needed. `artifacts/launcher/FindIt.exe` and the workspace preview.5 package have been updated.

## Physical pen-drive installation

After reconnection, the expected 8 GB FAT32 drive received the launcher-only update. **Fourteen physical-drive checks passed**, including visible native feedback, five rapid clicks during startup, five additional clicks with an existing minimized application, restoration of that same window, and clean application/worker shutdown. Feedback was observed after approximately **314 ms** on this PC with an existing runtime cache. This does not measure cold setup or establish a clean-machine startup guarantee.

Before and after installation, **9,000 pre-existing files other than the deliberately replaced root launcher** retained their SHA-256, length and modification time. The application remains preview.5; its package, models, archive, originals and old versions were preserved, and no preparation ran. Exact copies of the previous launcher and application pointer, the final report and rollback instructions are in `D:\Validation\startup-feedback-20261022`. Workspace evidence is `artifacts/startup/drive-before.json` and `drive-final.json`. FindIt was closed and the drive left connected.

Cold USB setup, clean-machine behavior, physical accessibility/high contrast and older-machine latency remain release gates. The earlier first-start playback issue remains open.
