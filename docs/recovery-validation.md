# Recovery validation

`FindIt.RecoveryCheck` exercises real child workers on generated disposable archives. It only accepts an output directory beneath this checkout's `artifacts` directory; it never accepts a user's archive root. No removable drive is needed or modified.

Build normal and validation workers separately. Keep validation output away from a portable package:

```powershell
$env:MSBuildEnableWorkloadResolver = 'false'
dotnet build src/FindIt.Worker -c Release --artifacts-path artifacts/recovery-normal -p:FindItEnableFaultInjection=false
dotnet build src/FindIt.Worker -c Release --artifacts-path artifacts/recovery-fault -p:FindItEnableFaultInjection=true
dotnet build tools/FindIt.RecoveryCheck -c Release --artifacts-path artifacts/recovery-runner
& artifacts/recovery-runner/bin/FindIt.RecoveryCheck/release/FindIt.RecoveryCheck.exe `
  --worker artifacts/recovery-fault/bin/FindIt.Worker/release/FindIt.Worker.exe `
  --production-worker artifacts/recovery-normal/bin/FindIt.Worker/release/FindIt.Worker.exe `
  --output artifacts/recovery-results
```

Fault activation code is compiled only when `FindItEnableFaultInjection=true`. The normal build and portable publisher explicitly set it to false. Activation additionally requires the runner's process environment and matching nonce in its generated sandbox. Source files, queries, manifests, or archive preferences cannot enable a fault. The production-worker check supplies valid fault settings and verifies ordinary preparation completes without hitting a hook.

The matrix covers termination after a segment write and file commit, before/after generation publication, intermediate checkpoints, and after a valid pointer switch. Before the switch, the previous complete library must remain searchable; after the switch, the complete new generation may be selected. Interrupted work resumes without rewriting originals. Retained generation bytes and source SHA-256/mtime are checked.

Further cases inject disk-full exceptions at real boundaries, briefly lock the pointer to exercise bounded retries, deny replacement through Windows access controls, corrupt or remove required artifacts, truncate the pointer, change bytes without changing size/mtime, delete sources, and relocate/disconnect/reconnect a generated root. The runner kills only its own blocked child processes. JSON reports include each result and failure.

This is local filesystem fault validation. Injected disk-full errors do not fill a disk or emulate device firmware. Renaming a generated root does not establish physical USB removal behavior. Power loss, full-volume write protection, real NTFS/exFAT removable media and the older offline PC still require separate acceptance runs. Preserve the first failing report when fixing and repeating a check.
