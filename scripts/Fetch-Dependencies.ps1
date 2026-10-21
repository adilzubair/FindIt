[CmdletBinding()]
param([switch]$SkipModels,[switch]$SkipTools)
$ErrorActionPreference='Stop'
$projectRoot=Split-Path $PSScriptRoot -Parent
Set-Location -LiteralPath $projectRoot
New-Item -ItemType Directory -Path .tools/downloads,.tools/src,runtime/native,runtime/tools,runtime/models,runtime/tessdata,runtime/licenses -Force | Out-Null
function Get-Verified([string]$Url,[string]$Path,[string]$Sha256) {
    if((Test-Path -LiteralPath $Path) -and (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -eq $Sha256){return}
    Write-Host "Fetching $Path"
    Invoke-WebRequest -Uri $Url -OutFile ($Path+'.partial')
    if((Get-FileHash -LiteralPath ($Path+'.partial') -Algorithm SHA256).Hash -ne $Sha256){throw "Checksum mismatch: $Path. The untrusted download remains .partial and is not loaded."}
    Move-Item -LiteralPath ($Path+'.partial') -Destination $Path -Force
}
function Copy-Binary([string]$Source,[string]$Destination) {
    if((Test-Path -LiteralPath $Destination) -and (Get-FileHash -LiteralPath $Source).Hash -eq (Get-FileHash -LiteralPath $Destination).Hash){return}
    Copy-Item -LiteralPath $Source -Destination $Destination -Force
}
if(!$SkipTools) {
    $artifacts=@(
        @('https://github.com/mstorsjo/llvm-mingw/releases/download/20261006/llvm-mingw-20261006-ucrt-x86_64.zip','llvm.zip','317492c456aa27ee607a5919f1d2d38dcdc1112516a24d0bf4b00d078f52d17a'),
        @('https://github.com/Kitware/CMake/releases/download/v4.4.4/cmake-4.4.4-windows-x86_64.zip','cmake.zip','bace36e94b31c68ab6fa295f26dfa11219e0701cf7c94b0284a7d1cb13dac536'),
        @('https://github.com/ninja-build/ninja/releases/download/v1.13.2/ninja-win.zip','ninja.zip','07fc8261b42b20e71d1720b39068c2e14ffcee6396b76fb7a795fb460b78dc65'),
        @('https://github.com/ggml-org/llama.cpp/archive/4d756bc72bf00a4aacf410ae15a2d315f3db400d.zip','llama.zip','43b717fdaf7b5a0996028b7d8f79880b4ad918e9b15948d7ec2fc67d18860b19'),
        @('https://github.com/unum-cloud/USearch/archive/7a4abfb2033935498256044e01bc0049ec80d76c.zip','usearch.zip','0db638149781d916a6a14c7ab287d439e97bad0a4c30cbfce687f14e93c5e9ab'),
        @('https://github.com/ggml-org/whisper.cpp/archive/refs/tags/v1.9.5.zip','whisper.zip','7a7a73071939f16bc028ad2f713f16f26e37afb6cc155d0f19e3d65705d2b1d9'),
        @('https://github.com/BtbN/FFmpeg-Builds/releases/download/autobuild-2026-10-20-13-08/ffmpeg-n8.1.3-18-g46d899d7a0-win64-lgpl-8.1.zip','ffmpeg.zip','88bd75db73a3ddf2222cbfe302d64b3bc8126a144cccb24806bc9de78264499d'),
        @('https://github.com/bblanchon/pdfium-binaries/releases/download/chromium/8111/pdfium-win-x64.tgz','pdfium.tgz','3452d11031050ec65e57f180215b57b9e6d465e3fbedafa8a77ad90885c7d23e'),
        @('https://github.com/ggml-org/llama.cpp/releases/download/b11461/llama-b11461-bin-win-vulkan-x64.zip','llama-vulkan.zip','6b9ca8ad3e43ac3e166d1997875709e4547214bb5bf1c2ebfdd17a2350a8380c')
    )
    foreach($a in $artifacts){Get-Verified $a[0] ('.tools/downloads/'+$a[1]) $a[2]}
    $archives=@(@('llvm.zip','.tools','llvm-mingw-20261006-ucrt-x86_64'),@('cmake.zip','.tools','cmake-4.4.4-windows-x86_64'),@('ninja.zip','.tools/ninja','ninja.exe'),@('llama.zip','.tools/src','llama.cpp-4d756bc72bf00a4aacf410ae15a2d315f3db400d'),@('usearch.zip','.tools/src','USearch-7a4abfb2033935498256044e01bc0049ec80d76c'),@('whisper.zip','.tools/src','whisper.cpp-1.9.5'))
    foreach($a in $archives){if(!(Test-Path -LiteralPath (Join-Path $a[1] $a[2]))){Expand-Archive -LiteralPath ('.tools/downloads/'+$a[0]) -DestinationPath $a[1] -Force}}
    if(!(Test-Path .tools/ffmpeg)){Expand-Archive .tools/downloads/ffmpeg.zip .tools/ffmpeg -Force}
    if(!(Test-Path .tools/pdfium)){New-Item .tools/pdfium -ItemType Directory -Force | Out-Null;tar -xzf .tools/downloads/pdfium.tgz -C .tools/pdfium;if($LASTEXITCODE){throw 'PDFium archive extraction failed.'}}
    Get-ChildItem .tools/ffmpeg -Recurse -Filter ffmpeg.exe | ForEach-Object {Copy-Binary $_.FullName 'runtime/tools/ffmpeg.exe'}
    Get-ChildItem .tools/ffmpeg -Recurse -Filter ffprobe.exe | ForEach-Object {Copy-Binary $_.FullName 'runtime/tools/ffprobe.exe'}
    Copy-Binary '.tools/pdfium/bin/pdfium.dll' 'runtime/native/pdfium.dll'
    Get-ChildItem .tools/ffmpeg -Recurse -Filter LICENSE.txt | Copy-Item -Destination runtime/licenses/FFmpeg-LICENSE.txt -Force
    if(Test-Path .tools/pdfium/LICENSE){Copy-Item .tools/pdfium/LICENSE runtime/licenses/PDFium-LICENSE.txt -Force}
    if(Test-Path .tools/pdfium/licenses){Copy-Item .tools/pdfium/licenses runtime/licenses/PDFium-third-party -Recurse -Force}
    Copy-Item .tools/llvm-mingw-20261006-ucrt-x86_64/LICENSE.TXT runtime/licenses/LLVM-MinGW-LICENSE.txt -Force
    # Official desktop runtime package is extracted locally. No installation or administrator rights.
    Get-Verified 'https://aka.ms/Microsoft.VCLibs.x64.14.00.Desktop.appx' '.tools/downloads/vclibs.zip' 'b56a9101f706f9d95f815f5b7fa6efbac972e86573d378b96a07cff5540c5961'
    if(!(Test-Path .tools/vclibs)){Expand-Archive .tools/downloads/vclibs.zip .tools/vclibs -Force}
    foreach($name in @('vcruntime140.dll','vcruntime140_1.dll','msvcp140.dll','msvcp140_1.dll','msvcp140_2.dll','msvcp140_atomic_wait.dll','msvcp140_codecvt_ids.dll','concrt140.dll','vcomp140.dll')){Copy-Binary (Join-Path .tools/vclibs $name) (Join-Path runtime/native $name)}
    if(!(Test-Path .tools/vulkan)){Expand-Archive .tools/downloads/llama-vulkan.zip .tools/vulkan -Force}
    New-Item runtime/native/gpu -ItemType Directory -Force | Out-Null
    Copy-Binary '.tools/vulkan/ggml-vulkan.dll' 'runtime/native/gpu/ggml-vulkan.dll'
    Copy-Item .tools/src/llama.cpp-4d756bc72bf00a4aacf410ae15a2d315f3db400d/LICENSE runtime/licenses/llama-LICENSE.txt -Force
    Copy-Item .tools/src/USearch-7a4abfb2033935498256044e01bc0049ec80d76c/LICENSE runtime/licenses/USearch-LICENSE.txt -Force
    Copy-Item .tools/src/whisper.cpp-1.9.5/LICENSE runtime/licenses/whisper.cpp-LICENSE.txt -Force
    $notices=@(
        @('https://raw.githubusercontent.com/shimat/opencvsharp/b161e7e012f5101f6d5dc68a835c59db6cc88b18/LICENSE','OpenCvSharp-LICENSE.txt'),
        @('https://raw.githubusercontent.com/opencv/opencv/4.13.0/LICENSE','OpenCV-LICENSE.txt'),
        @('https://raw.githubusercontent.com/charlesw/tesseract/b5329d5be92fa670031d94c3875f879651a01f55/LICENSE.txt','Tesseract-dotnet-LICENSE.txt'),
        @('https://raw.githubusercontent.com/tesseract-ocr/tesseract/5.2.0/LICENSE','Tesseract-LICENSE.txt'),
        @('https://raw.githubusercontent.com/DanBloomberg/leptonica/1.82.0/leptonica-license.txt','Leptonica-LICENSE.txt')
    )
    foreach($notice in $notices){$target=Join-Path runtime/licenses $notice[1];if(!(Test-Path -LiteralPath $target)){Invoke-WebRequest $notice[0] -OutFile $target}}
    @'
FFmpeg build: https://github.com/BtbN/FFmpeg-Builds/releases/tag/autobuild-2026-10-20-13-08
Source/build scripts: https://github.com/BtbN/FFmpeg-Builds
FFmpeg source version: n8.1.3-18-g46d899d7a0. LGPL build; no GPL/nonfree configuration.
Exact binary SHA-256 is pinned in scripts/Fetch-Dependencies.ps1.
Microsoft C++ runtime: official Microsoft.VCLibs.x64.14.00.Desktop.appx from https://aka.ms/Microsoft.VCLibs.x64.14.00.Desktop.appx, extracted app-locally; no system installation.
Model provenance and weight licenses are recorded in models/manifest.json.
'@ | Set-Content runtime/licenses/SOURCES.txt
}
if(!$SkipModels) {
    $manifest=Get-Content models/manifest.json -Raw | ConvertFrom-Json
    foreach($model in $manifest.models){$folder=if($model.target -eq 'tessdata'){'runtime/tessdata'}else{'runtime/models'};Get-Verified $model.url (Join-Path $folder $model.file) $model.sha256
        $license=Join-Path runtime/licenses ($model.id+'-LICENSE.txt');if(!(Test-Path -LiteralPath $license)){Invoke-WebRequest $model.licenseUrl -OutFile $license}
    }
}
Write-Host 'Dependencies are ready in runtime/; downloaded artifacts are SHA-256 checked.'
