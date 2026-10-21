# Recovery validation

This runner creates fresh archives below this checkout's `artifacts` directory. It never accepts an existing archive root and never touches the USB test drive. It runs real framed-IPC workers, extraction, SQLite transactions, packed artifacts, and generation publication.

Build the normal and fault-enabled workers into separate outputs. The fault code is excluded unless `FindItEnableFaultInjection=true` is explicitly set when compiling. Portable releases must use the normal worker.

```powershell
$env:MSBuildEnableWorkloadResolver = 'false'
$env:DOTNET_CLI_HOME = Join-Path $PWD '.tools/dotnet-home'
dotnet build tools/FindIt.RecoveryCheck/FindIt.RecoveryCheck.csproj -c Release --artifacts-path artifacts/recovery-next-build -m:1 --disable-build-servers
dotnet build src/FindIt.Worker/FindIt.Worker.csproj -c Release --artifacts-path artifacts/recovery-next-fault-build -p:FindItEnableFaultInjection=true -m:1 --disable-build-servers
dotnet build src/FindIt.Worker/FindIt.Worker.csproj -c Release --artifacts-path artifacts/recovery-next-normal-build -m:1 --disable-build-servers
& artifacts/recovery-next-build/bin/FindIt.RecoveryCheck/release/FindIt.RecoveryCheck.exe `
  --worker artifacts/recovery-next-fault-build/bin/FindIt.Worker/release/FindIt.Worker.exe `
  --production-worker artifacts/recovery-next-normal-build/bin/FindIt.Worker/release/FindIt.Worker.exe `
  --output artifacts/recovery-next-results
```

The optional production worker enables a negative check: even with a valid fault environment and sandbox nonce, a normal build must prepare successfully without activating a fault. Every scenario reports its assertions and fails the executable on an error. `recovery-report.json` records the results; generated archives remain available for inspection.

Coverage includes:

- Killing only the runner's owned, blocked preparer inside a file transaction, after a file commit, before moving a generation, after moving it, and before switching its pointer.
- Killing intermediate checkpoint publications, removing runner-created originals before resume, and accepting a complete new generation after its pointer has already switched.
- Injecting disk-full exceptions during space checks and publication without filling any disk.
- A real Windows sharing conflict that denies pointer replacement, plus a short conflict that must succeed after bounded retries.
- Recovering from a damaged catalog, missing vector records, truncated manifest, and torn pointer without reader repairs.
- Publishing after a corrupted generation has been skipped, then damaging the new catalog and proving collection kept the previous verified generation.
- Strongly verifying an equal-size/equal-time source change, deleting an original, moving the generated archive directory, and reconnecting it.
- Comparing originals and the previous complete generation by SHA-256/length/mtime and confirming abandoned work is reclaimed after resume.
- Appending a newly discovered text embedding after an interrupted checkpoint while checking the complete checkpoint's shared vector packs remain unchanged. This scenario requires the verified local embedding package.

Fault activation needs both explicit build-time opt-in and runner-only process environment with a generated sandbox nonce. Originals, manifests, queries, and user preferences cannot activate it. Markers are published only after their file handles close; the runner kills a worker only after the marker identifies its exact owned PID, root, and fault boundary.

These checks do not certify real disk exhaustion, physical USB removal, power loss, whole-volume read-only operation, or NTFS/exFAT hardware behavior. Relocation is a generated-directory move, not a physical drive-letter change. Model relevance and large-index performance are separate checks.

For a targeted regression, `--filter <scenario name substring>` selects matching scenarios and records that filter in the report. A filter selecting no checks fails. Omit the filter for the full matrix; supply a production worker to include the negative activation check.
