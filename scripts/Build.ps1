[CmdletBinding()]
param(
    [ValidateSet('Debug','Release')][string]$Configuration = 'Release',
    [switch]$SkipDependencies,
    [switch]$SkipTests
)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path $PSScriptRoot -Parent
Push-Location $taskRoot
try {
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
    $env:MSBuildEnableWorkloadResolver = 'false'
    $env:DOTNET_CLI_HOME = Join-Path $taskRoot '.tools/dotnet-home'
    if (-not $SkipDependencies) {
        & "$PSScriptRoot/Fetch-Dependencies.ps1"
    }
    & dotnet restore FindIt.slnx --disable-parallel
    if ($LASTEXITCODE) { throw 'Package restore failed.' }
    if (-not $SkipDependencies) { & "$PSScriptRoot/Build-Native.ps1" -Configuration $Configuration }
    & dotnet build FindIt.slnx -c $Configuration --no-restore -m:1 --disable-build-servers
    if ($LASTEXITCODE) { throw 'Managed build failed.' }
    $taskCompiler = Get-ChildItem '.tools/llvm-mingw-*/bin/x86_64-w64-mingw32-clang.exe' | Select-Object -First 1 -ExpandProperty FullName
    if (-not $taskCompiler) { throw 'Portable C compiler is missing. Run Fetch-Dependencies.ps1.' }
    New-Item -ItemType Directory -Force artifacts/launcher | Out-Null
    & $taskCompiler src/FindIt.Launcher/launcher.c -o artifacts/launcher/FindIt.exe -O2 -municode -mwindows -static '-Wl,--stack,16777216' -ladvapi32 -lshell32 -luser32
    if ($LASTEXITCODE) { throw 'Native launcher build failed.' }
    if (-not $SkipTests) {
        & dotnet test tests/FindIt.Tests/FindIt.Tests.csproj -c $Configuration --no-build --no-restore -m:1 --disable-build-servers --logger 'trx;LogFileName=findit.trx' --results-directory artifacts/tests
        if ($LASTEXITCODE) { throw 'Tests failed.' }
    }
} finally { Pop-Location }
