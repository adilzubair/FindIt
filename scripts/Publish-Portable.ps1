[CmdletBinding()]
param(
    [string]$Version = '0.1.0-preview.4',
    [string]$OutputDirectory,
    [string]$BuildArtifactsPath,
    [switch]$SkipBuild,
    [switch]$Zip,
    [string]$SigningThumbprint
)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path $PSScriptRoot -Parent
if ($env:FindItEnableFaultInjection -eq 'true') { throw 'Fault-injection builds are for workspace validation only. Clear FindItEnableFaultInjection before publishing.' }
if ($Version -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$' -or $Version.Contains('..')) { throw 'Invalid version.' }
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $taskRoot 'artifacts/portable' }
$taskOutput = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath (Join-Path $taskOutput 'FindIt/archive')) { throw 'Publish to a fresh folder; this destination already contains an archive.' }
Push-Location $taskRoot
try {
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'; $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'; $env:MSBuildEnableWorkloadResolver = 'false'
    $env:DOTNET_CLI_HOME = Join-Path $taskRoot '.tools/dotnet-home'
    if (-not $SkipBuild) {
        $taskBuildArguments = @{ Configuration = 'Release' }
        if ($BuildArtifactsPath) { $taskBuildArguments.BuildArtifactsPath = $BuildArtifactsPath }
        & "$PSScriptRoot/Build.ps1" @taskBuildArguments
    }
    $taskApp = Join-Path $taskOutput "FindIt/app/$Version"
    New-Item -ItemType Directory -Force $taskApp | Out-Null
    foreach ($taskProject in @('FindIt.App','FindIt.Worker')) {
        $taskPublishArguments = @('publish', "src/$taskProject/$taskProject.csproj", '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-p:PublishSingleFile=false', '-p:PublishTrimmed=false', '-p:RuntimeFrameworkVersion=10.0.13', '-p:FindItEnableFaultInjection=false', '-o', $taskApp, '-m:1', '--disable-build-servers')
        if ($BuildArtifactsPath) { $taskPublishArguments += @('--artifacts-path', [IO.Path]::GetFullPath($BuildArtifactsPath)) }
        & dotnet @taskPublishArguments
        if ($LASTEXITCODE) { throw "Publishing $taskProject failed." }
    }
    $taskRuntime = Join-Path $taskApp 'runtime'
    New-Item -ItemType Directory -Force $taskRuntime | Out-Null
    if (-not (Test-Path -LiteralPath (Join-Path $taskRoot 'runtime/native/findit_native.dll'))) { throw 'Native inference runtime missing. Run Build-Native.ps1.' }
    Copy-Item -LiteralPath (Join-Path $taskRoot 'runtime/native') -Destination $taskRuntime -Recurse -Force
    foreach ($taskFolder in @('tools','tessdata')) {
        if (Test-Path -LiteralPath (Join-Path $taskRoot "runtime/$taskFolder")) {
            Copy-Item -LiteralPath (Join-Path $taskRoot "runtime/$taskFolder") -Destination $taskRuntime -Recurse -Force
        }
    }
    $taskVlc = Join-Path $taskApp 'libvlc/win-x64'
    if (-not (Test-Path -LiteralPath (Join-Path $taskVlc 'libvlc.dll'))) { throw 'The bundled LibVLC package is missing.' }
    $taskModelSource = Join-Path $taskRoot 'runtime/models'
    if (-not (Test-Path -LiteralPath $taskModelSource)) { $taskModelSource = Join-Path $taskRoot 'FindIt/models' }
    if (-not (Test-Path -LiteralPath $taskModelSource)) { throw 'Verified models are missing. Run Fetch-Dependencies.ps1.' }
    $taskModels = Join-Path $taskOutput 'FindIt/models'
    New-Item -ItemType Directory -Force $taskModels | Out-Null
    $taskModelManifest = Get-Content -LiteralPath models/manifest.json -Raw | ConvertFrom-Json
    foreach ($taskModel in $taskModelManifest.models | Where-Object { $_.target -ne 'tessdata' }) {
        $taskModelFile = Join-Path $taskModelSource $taskModel.file
        if (-not (Test-Path -LiteralPath $taskModelFile) -or (Get-FileHash -LiteralPath $taskModelFile -Algorithm SHA256).Hash -ne $taskModel.sha256) { throw "Model integrity failed: $($taskModel.file)" }
        Copy-Item -LiteralPath $taskModelFile -Destination $taskModels -Force
    }
    foreach ($taskName in @('embeddinggemma-2-Q8_0.gguf','mmproj-embeddinggemma-2-Q8_0.gguf','face_detection_yunet_2023mar.onnx','face_recognition_sface_2021dec.onnx')) {
        if (-not (Test-Path -LiteralPath (Join-Path $taskModels $taskName))) { throw "Required model missing: $taskName" }
    }
    Copy-Item -LiteralPath models/manifest.json -Destination $taskModels -Force
    Copy-Item -LiteralPath artifacts/launcher/FindIt.exe -Destination (Join-Path $taskOutput 'FindIt.exe') -Force
    Copy-Item -LiteralPath README.md -Destination $taskOutput -Force
    Copy-Item -LiteralPath docs -Destination $taskOutput -Recurse -Force
    if (Test-Path -LiteralPath THIRD-PARTY-NOTICES.md) { Copy-Item -LiteralPath THIRD-PARTY-NOTICES.md -Destination $taskOutput -Force }
    if (Test-Path -LiteralPath licenses) { Copy-Item -LiteralPath licenses -Destination $taskOutput -Recurse -Force }
    if (Test-Path -LiteralPath runtime/licenses) {
        $taskLicenses = Join-Path $taskOutput 'licenses'
        New-Item -ItemType Directory -Force $taskLicenses | Out-Null
        Get-ChildItem runtime/licenses | Copy-Item -Destination $taskLicenses -Recurse -Force
    }
    Copy-Item -LiteralPath src/FindIt.App/Assets/Fonts/OFL-LICENSE.txt -Destination (Join-Path $taskLicenses 'IBM-Plex-Sans-OFL.txt') -Force
    if ($SigningThumbprint) {
        $taskCertificate = Get-Item "Cert:/CurrentUser/My/$SigningThumbprint"
        $taskOwnedBinaries = @((Join-Path $taskOutput 'FindIt.exe'), (Join-Path $taskApp 'runtime/native/findit_native.dll')) + @(Get-ChildItem -LiteralPath $taskApp -File | Where-Object { $_.Name -like 'FindIt.*' -and $_.Extension -in @('.exe','.dll') } | Select-Object -ExpandProperty FullName)
        foreach ($taskBinary in $taskOwnedBinaries) {
            $taskSignature = Set-AuthenticodeSignature -LiteralPath $taskBinary -Certificate $taskCertificate -HashAlgorithm SHA256 -TimestampServer 'http://timestamp.digicert.com'
            if ($taskSignature.Status -ne 'Valid') { throw "Signing failed: $taskBinary" }
        }
    }
    $taskLines = Get-ChildItem -LiteralPath $taskApp -Recurse -File | Where-Object { $_.Name -ne 'package.sha256' } | Sort-Object FullName | ForEach-Object {
        $taskRelative = [IO.Path]::GetRelativePath($taskApp, $_.FullName)
        "{0}  {1}" -f (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $taskRelative
    }
    [IO.File]::WriteAllLines((Join-Path $taskApp 'package.sha256'), $taskLines, [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText((Join-Path $taskOutput 'FindIt/app/current.txt'), $Version, [Text.UTF8Encoding]::new($false))
    if ($Zip) {
        $taskZip = "$taskOutput.zip"
        if (Test-Path -LiteralPath $taskZip) { throw 'ZIP output exists; choose a fresh output path.' }
        # Stream large GGUF files with ZIP64; Compress-Archive's 2 GiB per-file limit cannot carry this model.
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        [IO.Compression.ZipFile]::CreateFromDirectory($taskOutput, $taskZip, [IO.Compression.CompressionLevel]::Fastest, $false)
    }
    Write-Output "Portable package: $taskOutput"
} finally { Pop-Location }
