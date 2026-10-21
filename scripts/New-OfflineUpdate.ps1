#requires -Version 7.2
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$AppDirectory,
    [Parameter(Mandatory)][string]$PrivateKeyPath,
    [Parameter(Mandatory)][string]$OutputZip,
    [string]$DownloadUrl,
    [string]$ManifestOutputPath
)

$ErrorActionPreference = 'Stop'
if ($Version.Length -gt 64 -or $Version -notmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-[A-Za-z0-9]+(?:[.-][A-Za-z0-9]+)*)?$') {
    throw 'Version must be a bounded semantic version such as 1.2.0 or 1.2.0-preview.1.'
}
if ($DownloadUrl) {
    $taskDownloadUri = $null
    if (-not [Uri]::TryCreate($DownloadUrl, [UriKind]::Absolute, [ref]$taskDownloadUri) -or $taskDownloadUri.Scheme -ne 'https' -or $taskDownloadUri.UserInfo) {
        throw 'DownloadUrl must be an HTTPS URL without embedded credentials.'
    }
}

$taskAppRoot = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($AppDirectory))
$taskKeyFile = [IO.Path]::GetFullPath($PrivateKeyPath)
$taskZipFile = [IO.Path]::GetFullPath($OutputZip)
if (-not $ManifestOutputPath) { $ManifestOutputPath = "$taskZipFile.manifest.json" }
$taskManifestFile = [IO.Path]::GetFullPath($ManifestOutputPath)
if (-not [IO.Directory]::Exists($taskAppRoot)) { throw 'AppDirectory must be the published version folder, containing FindIt.App.exe and FindIt.Worker.exe.' }
if (-not [IO.File]::Exists($taskKeyFile)) { throw 'The provided publisher private key file does not exist.' }
if ([IO.File]::Exists($taskZipFile) -or [IO.File]::Exists($taskManifestFile)) { throw 'Output ZIP or manifest already exists. Choose fresh output paths.' }
if ($taskZipFile.Equals($taskManifestFile, [StringComparison]::OrdinalIgnoreCase)) { throw 'ZIP and manifest output paths must differ.' }
$taskAppPrefix = $taskAppRoot + [IO.Path]::DirectorySeparatorChar
foreach ($taskOutputPath in @($taskZipFile, $taskManifestFile)) {
    if ($taskOutputPath.StartsWith($taskAppPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Write update outputs outside the application payload directory.' }
}

function Assert-PlainPath([string]$Path) {
    if (([IO.File]::Exists($Path) -or [IO.Directory]::Exists($Path)) -and (([IO.File]::GetAttributes($Path) -band [IO.FileAttributes]::ReparsePoint) -ne 0)) {
        throw "Linked files and junctions cannot be packaged: $Path"
    }
}
Assert-PlainPath $taskAppRoot
Assert-PlainPath $taskKeyFile
foreach ($taskRequired in @('FindIt.App.exe', 'FindIt.Worker.exe')) {
    if (-not [IO.File]::Exists((Join-Path $taskAppRoot $taskRequired))) { throw "Published payload is missing $taskRequired." }
}

if (-not ('FindIt.OfflineUpdateSigning' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Security.Cryptography;
namespace FindIt {
    public static class OfflineUpdateSigning {
        public static byte[] Sign(string pem, byte[] payload) {
            using (var key = ECDsa.Create()) {
                key.ImportFromPem(pem);
                if (key.KeySize < 256 || key.KeySize > 521) throw new CryptographicException("Use an ECDSA private key of at least 256 bits.");
                return key.SignData(payload, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
            }
        }
    }
}
'@
}

$taskFiles = [Collections.Generic.List[object]]::new()
$taskSeen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$taskPending = [Collections.Generic.Stack[string]]::new()
$taskPending.Push($taskAppRoot)
[long]$taskExpandedSize = 0
while ($taskPending.Count -gt 0) {
    $taskFolder = $taskPending.Pop()
    Assert-PlainPath $taskFolder
    foreach ($taskEntry in [IO.Directory]::EnumerateFileSystemEntries($taskFolder)) {
        Assert-PlainPath $taskEntry
        if ([IO.Directory]::Exists($taskEntry)) { $taskPending.Push($taskEntry); continue }
        if ($taskEntry.Equals($taskKeyFile, [StringComparison]::OrdinalIgnoreCase)) { throw 'The private signing key is inside the payload. Move it out before packaging.' }
        $taskRelative = [IO.Path]::GetRelativePath($taskAppRoot, $taskEntry).Replace('\', '/')
        # Regenerate the launcher's integrity manifest inside the ZIP without changing published input.
        if ($taskRelative.Equals('package.sha256', [StringComparison]::OrdinalIgnoreCase)) { continue }
        if ($taskRelative.Contains('..')) { throw "The portable launcher cannot load this payload path: $taskRelative" }
        foreach ($taskPart in $taskRelative.Split('/')) {
            if (-not $taskPart -or $taskPart -in @('.', '..') -or $taskPart.Length -gt 255 -or $taskPart.EndsWith('.') -or $taskPart.EndsWith(' ') -or $taskPart -match '[<>:"|?*\x00-\x1f]' -or $taskPart.Split('.')[0] -match '^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$') {
                throw "Unsafe application payload path: $taskRelative"
            }
        }
        if (-not $taskSeen.Add($taskRelative)) { throw "Case-colliding payload paths: $taskRelative" }
        $taskInfo = [IO.FileInfo]::new($taskEntry)
        if ($taskInfo.Length -gt 1GB) { throw "A payload file exceeds the 1 GiB updater limit: $taskRelative" }
        $taskExpandedSize += $taskInfo.Length
        if ($taskExpandedSize -gt 4GB -or $taskFiles.Count -ge 20000) { throw 'The payload exceeds the portable updater safety limits.' }
        $taskFiles.Add([ordered]@{
            path = "FindIt/app/$Version/$taskRelative"
            sha256 = (Get-FileHash -LiteralPath $taskEntry -Algorithm SHA256).Hash.ToLowerInvariant()
            size = [long]$taskInfo.Length
        })
    }
}
$taskEncoding = [Text.UTF8Encoding]::new($false, $true)
$taskLauncherLines = @($taskFiles | Sort-Object { $_.path } | ForEach-Object { $_.sha256 + '  ' + $_.path.Substring("FindIt/app/$Version/".Length).Replace('/', '\') })
$taskLauncherBytes = $taskEncoding.GetBytes(($taskLauncherLines -join "`n") + "`n")
if ($taskLauncherBytes.Length -gt 4MB -or $taskFiles.Count -ge 20000 -or $taskExpandedSize + $taskLauncherBytes.Length -gt 4GB) { throw 'The launcher manifest exceeds updater safety limits.' }
$taskFiles.Add([ordered]@{ path = "FindIt/app/$Version/package.sha256"; sha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($taskLauncherBytes)).ToLowerInvariant(); size = [long]$taskLauncherBytes.Length })
$taskOrderedFiles = @($taskFiles | Sort-Object { $_.path })
$taskPayload = [ordered]@{ version = $Version; files = $taskOrderedFiles }
if ($DownloadUrl) { $taskPayload.downloadUrl = $DownloadUrl }
$taskPayloadBytes = $taskEncoding.GetBytes(($taskPayload | ConvertTo-Json -Depth 8 -Compress))
$taskPrivatePem = [IO.File]::ReadAllText($taskKeyFile)
try { $taskSignature = [FindIt.OfflineUpdateSigning]::Sign($taskPrivatePem, $taskPayloadBytes) }
finally { $taskPrivatePem = $null }
$taskEnvelopeBytes = $taskEncoding.GetBytes((([ordered]@{
    payload = [Convert]::ToBase64String($taskPayloadBytes)
    signature = [Convert]::ToBase64String($taskSignature)
}) | ConvertTo-Json -Compress))
if ($taskEnvelopeBytes.Length -gt 8MB) { throw 'The signed manifest exceeds the updater limit.' }

[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($taskZipFile)) | Out-Null
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($taskManifestFile)) | Out-Null
$taskTemporaryZip = "$taskZipFile.pending-$([Guid]::NewGuid().ToString('N'))"
$taskTemporaryManifest = "$taskManifestFile.pending-$([Guid]::NewGuid().ToString('N'))"
try {
    $taskZipStream = [IO.FileStream]::new($taskTemporaryZip, [IO.FileMode]::CreateNew, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    try {
        $taskZip = [IO.Compression.ZipArchive]::new($taskZipStream, [IO.Compression.ZipArchiveMode]::Create, $true)
        try {
            $taskEnvelopeEntry = $taskZip.CreateEntry('findit-update.json', [IO.Compression.CompressionLevel]::Fastest)
            $taskEnvelopeStream = $taskEnvelopeEntry.Open()
            try { $taskEnvelopeStream.Write($taskEnvelopeBytes, 0, $taskEnvelopeBytes.Length) } finally { $taskEnvelopeStream.Dispose() }
            foreach ($taskFile in $taskOrderedFiles) {
                $taskPayloadRelative = $taskFile.path.Substring("FindIt/app/$Version/".Length)
                if ($taskPayloadRelative -eq 'package.sha256') {
                    $taskGeneratedEntry = $taskZip.CreateEntry($taskFile.path, [IO.Compression.CompressionLevel]::Fastest)
                    $taskGeneratedStream = $taskGeneratedEntry.Open()
                    try { $taskGeneratedStream.Write($taskLauncherBytes, 0, $taskLauncherBytes.Length) } finally { $taskGeneratedStream.Dispose() }
                    continue
                }
                $taskSource = Join-Path $taskAppRoot $taskPayloadRelative
                Assert-PlainPath $taskSource
                # No directory entries, extra metadata or files outside this immutable version are emitted.
                $taskOutputEntry = $taskZip.CreateEntry($taskFile.path, [IO.Compression.CompressionLevel]::Fastest)
                $taskOutputStream = $taskOutputEntry.Open()
                $taskInputStream = [IO.FileStream]::new($taskSource, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
                try { $taskInputStream.CopyTo($taskOutputStream) } finally { $taskInputStream.Dispose(); $taskOutputStream.Dispose() }
            }
        } finally { $taskZip.Dispose() }
        $taskZipStream.Flush($true)
    } finally { $taskZipStream.Dispose() }
    if ([IO.FileInfo]::new($taskTemporaryZip).Length -gt 4GB) { throw 'The update ZIP exceeds the updater safety limit.' }
    # Re-read the finished ZIP to catch source changes between hashing and packaging.
    $taskVerificationZip = [IO.Compression.ZipFile]::OpenRead($taskTemporaryZip)
    try {
        foreach ($taskFile in $taskOrderedFiles) {
            $taskPacked = $taskVerificationZip.GetEntry($taskFile.path)
            if ($null -eq $taskPacked -or $taskPacked.Length -ne $taskFile.size) { throw "Packaged file size changed: $($taskFile.path)" }
            $taskPackedStream = $taskPacked.Open()
            $taskHasher = [Security.Cryptography.SHA256]::Create()
            try { $taskDigest = [BitConverter]::ToString($taskHasher.ComputeHash($taskPackedStream)).Replace('-', '').ToLowerInvariant() }
            finally { $taskPackedStream.Dispose(); $taskHasher.Dispose() }
            if ($taskDigest -ne $taskFile.sha256) { throw "Source changed while packaging: $($taskFile.path)" }
            if ($taskPacked.Length -gt 1MB -and ($taskPacked.CompressedLength -eq 0 -or $taskPacked.Length / [Math]::Max(1, $taskPacked.CompressedLength) -gt 1000)) {
                throw "Payload has an unsafe compression ratio: $($taskFile.path)"
            }
        }
    } finally { $taskVerificationZip.Dispose() }
    [IO.File]::WriteAllBytes($taskTemporaryManifest, $taskEnvelopeBytes)
    [IO.File]::Move($taskTemporaryZip, $taskZipFile, $false)
    [IO.File]::Move($taskTemporaryManifest, $taskManifestFile, $false)
    Write-Output "Signed offline update: $taskZipFile"
    Write-Output "Signed check manifest: $taskManifestFile"
    Write-Output 'Configure the publisher HTTPS manifest URL and public key separately. No files were uploaded and the private key was not packaged.'
} finally {
    foreach ($taskTemporary in @($taskTemporaryZip, $taskTemporaryManifest)) {
        if ([IO.File]::Exists($taskTemporary)) { [IO.File]::Delete($taskTemporary) }
    }
}
