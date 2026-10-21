# Physical drive validation

`tools/FindIt.DriveCheck` runs against a real prepared portable package. It uses
the worker, native libraries, and models on the selected drive. JSON reports
belong on the host, outside the archive. Its small fixture checks behavior;
it does not certify relevance, a million-record index, or low-end performance.

The fixture is intentionally separate from the source distribution. Stage the
13 files described by the test's `Originals` list under `Library/`, verify their
hashes against the fixture inventory, and copy a complete portable package to
the drive root. The validator refuses unexpected originals and never formats
a drive. Do not run it against a personal library: its fixture and count checks
are specific to this test corpus.

```powershell
$env:MSBuildEnableWorkloadResolver = 'false'
dotnet build tools/FindIt.DriveCheck -c Release --artifacts-path artifacts/drive-check-build
$check = 'artifacts/drive-check-build/bin/FindIt.DriveCheck/release/FindIt.DriveCheck.exe'
$worker = 'D:\FindIt\app\0.1.0-preview.3\FindIt.Worker.exe'
& $check --root D:\ --worker $worker --report "$PWD/artifacts/drive-check.json"
& $check --root D:\ --worker $worker --report "$PWD/artifacts/drive-extras.json" --extras
& $check --root D:\ --worker $worker --report "$PWD/artifacts/drive-reader.json" --reader-only
```

The main check exercises exact and semantic retrieval, document extraction and
locators, timestamped speech and video, named reference enrollment, person
corrections, incremental changes, cancellation/resume, and decoder errors. It
removes only its own temporary probe files, reconciles the final library, and
compares original SHA-256 hashes. Published generations remain on the drive.
Initial preparation requests strong source verification so the same-size,
same-time edit test has a known hash baseline.

The supplemental check generates a 36-second audio probe in the workspace,
then verifies overlapping audio/transcript windows on the drive. It also
restores a temporarily changed vector-pack timestamp, restores temporarily
read-only metadata attributes, and removes its own optional `R:` alias. The
alias is a `subst` mapping, not proof of physical reconnection on another PC.
Read-only file attributes are not a whole-volume write-protection test.

The final `--reader-only` pass checks the existing prepared library using the
exact packaged worker after updates. It verifies search, previews, named-person
evidence, runtime locations, and unchanged originals without preparing or
changing people. Temporal checks use the recorded segment intervals; intervals
can span a scene cut. They do not establish a person's action or speaking role.

Reports retain errors and cleanup failures. A failed run must be investigated
before handoff; a pass does not replace the clean offline second-PC test.
Keep the first failing report when retesting a fix.

The app's `--render-ui` and `--playback-smoke` modes can additionally exercise
the actual portable launcher, prepared interface, and isolated LibVLC player.
Use `--render-open-archive` for a real archive capture. The separate
`--interaction-smoke` mode uses an in-memory archive and must not be presented
as evidence that a physical drive works.

Only after all reports and instructions have been copied and every drive
worker has closed, the explicit `--eject` option requests Windows safe removal.
It never forces a dismount. If Windows declines, use Safely Remove Hardware;
do not infer that removal is safe from an application-close event alone.
