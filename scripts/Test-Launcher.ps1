#requires -Version 7.2
[CmdletBinding()]
param([string]$LauncherPath, [string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path $PSScriptRoot -Parent
if (-not $LauncherPath) { $LauncherPath = Join-Path $taskRoot 'artifacts/launcher/FindIt.exe' }
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $taskRoot ('artifacts/launcher-test-' + [Guid]::NewGuid().ToString('N')) }
$taskOutput = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $taskOutput) { throw 'Use a fresh launcher-test output directory.' }
[IO.Directory]::CreateDirectory($taskOutput) | Out-Null
$taskCompiler = (Get-ChildItem (Join-Path $taskRoot '.tools/llvm-mingw-*/bin/x86_64-w64-mingw32-clang.exe') | Select-Object -First 1).FullName
& $taskCompiler (Join-Path $taskRoot 'tests/FindIt.Launcher.Tests/fixture.c') -o (Join-Path $taskOutput 'fixture.exe') -O2 -Wall -Wextra -Werror -municode -mwindows -static -lshell32 -luser32
if ($LASTEXITCODE) { throw 'Launcher fixture build failed.' }
Add-Type -TypeDefinition @'
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class LauncherWindows {
    public delegate bool Visitor(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")] public static extern bool EnumWindows(Visitor visitor, IntPtr parameter);
    [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr parent, Visitor visitor, IntPtr parameter);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr window, StringBuilder text, int capacity);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr window, StringBuilder text, int capacity);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr SendMessageTimeout(IntPtr window, uint message, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr window, IntPtr dc, uint flags);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr window, out Rect rectangle);
    [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] public static extern IntPtr GetDlgItem(IntPtr window, int id);
    [DllImport("user32.dll")] public static extern int GetDlgCtrlID(IntPtr window);
    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; }
    public static IntPtr Window(uint process, bool visibleOnly = true, string classPrefix = null) {
        IntPtr found = IntPtr.Zero;
        EnumWindows((window, ignored) => {
            uint pid; GetWindowThreadProcessId(window, out pid);
            var name = new StringBuilder(256); GetClassName(window, name, 256);
            if (pid == process && (!visibleOnly || IsWindowVisible(window)) && (classPrefix == null || name.ToString().StartsWith(classPrefix))) { found = window; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }
    public static string Text(IntPtr window) {
        var text = new StringBuilder(1024); GetWindowText(window, text, 1024); return text.ToString();
    }
    public static string Labels(IntPtr parent) {
        var labels = new StringBuilder();
        EnumChildWindows(parent, (window, ignored) => { labels.AppendLine(GetDlgCtrlID(window) + ": " + Text(window)); return true; }, IntPtr.Zero);
        return labels.ToString();
    }
}
'@
Add-Type -AssemblyName System.Drawing
$taskProcesses = [Collections.Generic.List[Diagnostics.Process]]::new()
$taskChecks = [Collections.Generic.List[string]]::new()
$taskLocal = Join-Path $taskOutput 'host'
$taskVersion = 'launcher-test-' + [Guid]::NewGuid().ToString('N')
function Check([bool]$Condition, [string]$Name) { if (-not $Condition) { throw $Name }; $taskChecks.Add($Name); Write-Output $Name }
function Wait-Until([scriptblock]$Predicate, [string]$Failure, [int]$Seconds = 15) {
    $clock = [Diagnostics.Stopwatch]::StartNew()
    while (-not (& $Predicate)) { if ($clock.Elapsed.TotalSeconds -gt $Seconds) { throw $Failure }; Start-Sleep -Milliseconds 20 }
}
function New-Package([string]$Name, [switch]$Corrupt) {
    $root = Join-Path $taskOutput $Name
    $app = Join-Path $root "FindIt/app/$taskVersion"
    [IO.Directory]::CreateDirectory($app) | Out-Null
    Copy-Item -LiteralPath $LauncherPath -Destination (Join-Path $root 'FindIt.exe')
    Copy-Item -LiteralPath (Join-Path $taskOutput 'fixture.exe') -Destination (Join-Path $app 'FindIt.App.exe')
    # A modest real payload tests byte progress and overwriting partial caches.
    $payload = [byte[]]::new(16MB); [Random]::new(42).NextBytes($payload)
    [IO.File]::WriteAllBytes((Join-Path $app 'payload.bin'), $payload)
    [IO.File]::WriteAllText((Join-Path $root 'FindIt/app/current.txt'), $taskVersion)
    $lines = foreach ($name in @('FindIt.App.exe','payload.bin')) {
        '{0}  {1}' -f (Get-FileHash -LiteralPath (Join-Path $app $name)).Hash.ToLowerInvariant(), $name
    }
    if ($Corrupt) { $lines[1] = ('0' * 64) + '  payload.bin' }
    [IO.File]::WriteAllLines((Join-Path $app 'package.sha256'), [string[]]$lines, [Text.UTF8Encoding]::new($false))
    $hash = (Get-FileHash -LiteralPath (Join-Path $app 'package.sha256')).Hash.ToLowerInvariant()
    return @{ Root=$root; App=$app; Hash=$hash; Cache=(Join-Path $taskLocal "FindIt/runtime/$taskVersion-$($hash.Substring(0,16))") }
}
function Start-Launcher($Package, [string[]]$Arguments = @()) {
    $info = [Diagnostics.ProcessStartInfo]::new((Join-Path $Package.Root 'FindIt.exe'))
    # This explicitly tests the interactive startup window seen by the user.
    $info.UseShellExecute = $false; $info.CreateNoWindow = $true; $info.WindowStyle = 'Normal'
    $info.Environment['LOCALAPPDATA'] = $taskLocal
    foreach ($argument in $Arguments) { $info.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($info); $taskProcesses.Add($process); return $process
}
function Child-Process($Package) {
    $matches = @(Get-Process -Name 'FindIt.App' -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq (Join-Path $Package.Cache 'FindIt.App.exe') })
    # Shared-cache tests use launch logs and window counts rather than this helper.
    if ($matches.Count -eq 1) { return $matches[0] }; return $null
}
function Close-App($Process) {
    if (-not $Process -or $Process.HasExited) { return }
    $window = [LauncherWindows]::Window($Process.Id, $false)
    [LauncherWindows]::PostMessage($window, 0x10, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
    if (-not $Process.WaitForExit(5000)) { throw 'Test application did not close.' }
}
function Capture([IntPtr]$Window, [string]$Name) {
    $awareness = [LauncherWindows]::SetThreadDpiAwarenessContext([IntPtr](-4))
    $rect = [LauncherWindows+Rect]::new(); [LauncherWindows]::GetWindowRect($Window, [ref]$rect) | Out-Null
    $bitmap = [Drawing.Bitmap]::new($rect.Right-$rect.Left, $rect.Bottom-$rect.Top)
    $graphics = [Drawing.Graphics]::FromImage($bitmap); $dc = $graphics.GetHdc()
    try { if (-not [LauncherWindows]::PrintWindow($Window, $dc, 2)) { throw 'Startup capture failed.' } }
    finally { $graphics.ReleaseHdc($dc); $graphics.Dispose() }
    try { $bitmap.Save((Join-Path $taskOutput $Name), [Drawing.Imaging.ImageFormat]::Png) } finally { $bitmap.Dispose(); [LauncherWindows]::SetThreadDpiAwarenessContext($awareness) | Out-Null }
}
$taskCacheGuard = $null
try {
    $package = New-Package 'portable USB 漢字'
    $taskCacheGuard = [Threading.Mutex]::new($true, "Local\FindIt.Cache.$taskVersion.$($package.Hash.Substring(0,16))")
    $clock = [Diagnostics.Stopwatch]::StartNew(); $first = Start-Launcher $package
    Wait-Until { [LauncherWindows]::Window($first.Id, $true, 'FindIt.Startup.') -ne [IntPtr]::Zero } 'No immediate startup window'
    $firstVisibleMs = $clock.Elapsed.TotalMilliseconds
    $startupWindow = [LauncherWindows]::Window($first.Id, $true, 'FindIt.Startup.')
    Wait-Until { [LauncherWindows]::Labels($startupWindow).Contains('already getting ready') } 'Setup-wait feedback missing'
    Check (-not $first.HasExited) 'Startup window appears before setup completes'
    $reply = [IntPtr]::Zero
    $responsive = [LauncherWindows]::SendMessageTimeout($startupWindow, 0, [IntPtr]::Zero, [IntPtr]::Zero, 2, 1000, [ref]$reply)
    Check ($responsive -ne [IntPtr]::Zero) 'Startup window responds while setup is blocked'
    Capture $startupWindow 'startup.png'
    $duplicates = @(1..10 | ForEach-Object { Start-Launcher $package })
    foreach ($duplicate in $duplicates) { Check ($duplicate.WaitForExit(3000) -and $duplicate.ExitCode -eq 0) 'Repeated startup click reuses the first launcher' }
    Check (-not (Test-Path -LiteralPath (Join-Path $package.Root 'launches.txt'))) 'Repeated clicks do not start applications during setup'
    [LauncherWindows]::PostMessage($startupWindow, 0x10, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
    Check ($first.WaitForExit(5000) -and $first.ExitCode -eq 0) 'Cancel closes a blocked setup cleanly'
    Check (-not (Test-Path -LiteralPath (Join-Path $package.Cache '.complete'))) 'Cancelled setup does not create a verified marker'
    $crashed = Start-Launcher $package
    Wait-Until { $handle = [LauncherWindows]::Window($crashed.Id); $handle -ne [IntPtr]::Zero -and [LauncherWindows]::Labels($handle).Contains('already getting ready') } 'Crash fixture did not reach setup wait'
    $crashed.Kill($true); $crashed.WaitForExit()
    $recovered = Start-Launcher $package
    Wait-Until { [LauncherWindows]::Window($recovered.Id) -ne [IntPtr]::Zero } 'Abandoned startup guard blocked recovery'
    Check (-not $recovered.HasExited) 'Killing a launcher does not leave the next launch locked out'
    [LauncherWindows]::PostMessage([LauncherWindows]::Window($recovered.Id), 0x10, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
    Check ($recovered.WaitForExit(5000)) 'Recovered launch remains cancellable'
    $taskCacheGuard.ReleaseMutex(); $taskCacheGuard.Dispose(); $taskCacheGuard = $null

    [IO.Directory]::CreateDirectory($package.Cache) | Out-Null
    [IO.File]::WriteAllText((Join-Path $package.Cache 'payload.bin'), 'incomplete previous copy')
    $first = Start-Launcher $package @('--probe', 'quote " and trailing slash\')
    Wait-Until { Test-Path -LiteralPath (Join-Path $package.Root 'launches.txt') } 'Cold setup did not launch the app'
    $app = Child-Process $package; $taskProcesses.Add($app)
    Check (Test-Path -LiteralPath (Join-Path $package.Cache '.complete')) 'Cold setup verifies its cache before launching'
    Check ((Get-FileHash -LiteralPath (Join-Path $package.Cache 'payload.bin')).Hash -eq (Get-FileHash -LiteralPath (Join-Path $package.App 'payload.bin')).Hash) 'Retry repairs an incomplete cache'
    Check (-not $first.HasExited) 'Startup feedback stays until an application window is visible'
    $duplicates = @(1..5 | ForEach-Object { Start-Launcher $package })
    foreach ($duplicate in $duplicates) { if (-not $duplicate.WaitForExit(3000)) { throw 'Handoff duplicate hung.' } }
    Check (@(Get-Content -LiteralPath (Join-Path $package.Root 'launches.txt')).Count -eq 1) 'Repeated clicks during application startup produce one app'
    [IO.File]::WriteAllText((Join-Path $package.Root 'show-window'), '')
    Wait-Until { [LauncherWindows]::Window($app.Id) -ne [IntPtr]::Zero } 'App did not show'
    Check ($first.WaitForExit(5000) -and $first.ExitCode -eq 0) 'USB launcher exits after handoff, releasing its executable'
    Check ([LauncherWindows]::Window($first.Id) -eq [IntPtr]::Zero) 'Startup window closes at handoff'
    $main = [LauncherWindows]::Window($app.Id)
    [LauncherWindows]::ShowWindow($main, 6) | Out-Null
    $repeat = Start-Launcher $package
    Check ($repeat.WaitForExit(5000) -and $repeat.ExitCode -eq 0) 'Repeat launch finds the existing application'
    Wait-Until { -not [LauncherWindows]::IsIconic($main) } 'Existing application was not restored'
    Check (@(Get-Content -LiteralPath (Join-Path $package.Root 'launches.txt')).Count -eq 1) 'Repeat launch restores the existing window without another app'
    $arguments = [Text.Encoding]::Unicode.GetString([IO.File]::ReadAllBytes((Join-Path $package.Root 'arguments.txt')))
    Check ($arguments.Contains($package.Root) -and $arguments.Contains('--probe')) 'Unicode and spaces in library paths and diagnostic arguments survive forwarding'
    Close-App $app
    $cachedFile = Get-Item -LiteralPath (Join-Path $package.Cache 'payload.bin'); $before = $cachedFile.LastWriteTimeUtc.Ticks
    $repeat = Start-Launcher $package
    Wait-Until { @(Get-Content -LiteralPath (Join-Path $package.Root 'launches.txt')).Count -eq 2 } 'Stale PID prevented relaunch'
    $app = Child-Process $package; $taskProcesses.Add($app)
    Check ($repeat.WaitForExit(5000) -and $repeat.ExitCode -eq 0) 'Closed application can launch again despite its stale PID record'
    Check ((Get-Item -LiteralPath $cachedFile.FullName).LastWriteTimeUtc.Ticks -eq $before) 'Warm launch reuses the verified cache without copying'
    Close-App $app

    $second = New-Package 'different library'
    [IO.File]::WriteAllText((Join-Path $second.Root 'show-window'), '')
    $taskLocal = Join-Path $taskOutput 'second-host'
    $package.Cache = Join-Path $taskLocal "FindIt/runtime/$taskVersion-$($package.Hash.Substring(0,16))"
    $second.Cache = $package.Cache
    $one = Start-Launcher $package; $two = Start-Launcher $second
    Check ($one.WaitForExit(5000) -and $two.WaitForExit(5000)) 'Separate library roots can open independently with a shared cache'
    $apps = @(Get-Process -Name 'FindIt.App' -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq (Join-Path $package.Cache 'FindIt.App.exe') })
    Check ($apps.Count -eq 2 -and (Test-Path -LiteralPath (Join-Path $second.Root 'launches.txt'))) 'Single-instance guard is scoped to a library, not the entire PC'
    Check ((Test-Path -LiteralPath (Join-Path $package.Cache '.complete')) -and (Get-FileHash -LiteralPath (Join-Path $package.Cache 'payload.bin')).Hash -eq (Get-FileHash -LiteralPath (Join-Path $package.App 'payload.bin')).Hash) 'Concurrent libraries populate one verified cold cache safely'
    foreach ($app in $apps) { $taskProcesses.Add($app); Close-App $app }

    $bad = New-Package 'corrupt application' -Corrupt
    [IO.File]::WriteAllText((Join-Path $bad.App '.complete'), 'untrusted marker')
    $badStart = Start-Launcher $bad
    Wait-Until { $handle = [LauncherWindows]::Window($badStart.Id, $true, '#32770'); $handle -ne [IntPtr]::Zero } 'Corrupt package did not show a recoverable error'
    Check (-not (Test-Path -LiteralPath (Join-Path $bad.Cache '.complete')) -and -not (Test-Path -LiteralPath (Join-Path $bad.Root 'launches.txt'))) 'Corrupt files never launch or mark the cache verified'
    $dialog = [LauncherWindows]::Window($badStart.Id, $true, '#32770')
    Capture $dialog 'failure-dialog.png'
    [LauncherWindows]::Labels($dialog) | Set-Content -LiteralPath (Join-Path $taskOutput 'dialog-labels.txt')
    [LauncherWindows]::PostMessage($dialog, 0x10, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
    Check ($badStart.WaitForExit(5000) -and $badStart.ExitCode -ne 0) 'Setup failure leaves the startup guard recoverable'

    $report = [ordered]@{passed=$true;firstVisibleMs=$firstVisibleMs;checks=$taskChecks.ToArray();launcher=(Get-FileHash -LiteralPath $LauncherPath).Hash;scope='Native launcher with real copy, SHA verification and a deterministic delayed-window fixture. Workspace storage and existing Windows installation; no physical USB cold-copy timing claim.'}
    $report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $taskOutput 'report.json') -Encoding utf8
} catch {
    [ordered]@{passed=$false;error=$_.Exception.Message;checks=$taskChecks.ToArray();processes=@($taskProcesses | ForEach-Object { @{id=$_.Id;exited=$_.HasExited;exitCode=$(if ($_.HasExited) { $_.ExitCode } else { $null })} })} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $taskOutput 'failed.json') -Encoding utf8
    throw
} finally {
    if ($taskCacheGuard) { $taskCacheGuard.ReleaseMutex(); $taskCacheGuard.Dispose() }
    foreach ($process in $taskProcesses) { if ($process -and -not $process.HasExited) { $process.Kill($true); $process.WaitForExit() }; if ($process) { $process.Dispose() } }
}
