[CmdletBinding()]
param([ValidateSet('Release','Debug')][string]$Configuration='Release',[int]$Jobs=8)
$ErrorActionPreference='Stop'
$projectRoot=Split-Path $PSScriptRoot -Parent
Set-Location -LiteralPath $projectRoot
$toolchain=Join-Path $projectRoot '.tools/llvm-mingw-20261006-ucrt-x86_64/bin'
$cmake=Join-Path $projectRoot '.tools/cmake-4.4.4-windows-x86_64/bin/cmake.exe'
$ninja=Join-Path $projectRoot '.tools/ninja/ninja.exe'
if(!(Test-Path $cmake)){throw 'Run scripts/Fetch-Dependencies.ps1 first.'}
$env:PATH=$toolchain+';'+$env:PATH
$llama=Join-Path $projectRoot '.tools/src/llama.cpp-4d756bc72bf00a4aacf410ae15a2d315f3db400d'
$usearch=Join-Path $projectRoot '.tools/src/USearch-7a4abfb2033935498256044e01bc0049ec80d76c'
$whisper=Join-Path $projectRoot '.tools/src/whisper.cpp-1.9.5'
# Embedding-only noncausal contexts have no logits. Avoid ~1 MiB/token of useless allocations.
$file=Join-Path $llama 'src/llama-context.cpp';$code=[IO.File]::ReadAllText($file);$code=$code.Replace('bool has_logits     = true;','bool has_logits     = !(cparams.embeddings && !cparams.causal_attn);');[IO.File]::WriteAllText($file,$code)
# libc++23 stopped transitively exposing these standard headers. No behavior change.
$file=Join-Path $usearch 'include/usearch/index.hpp';$code=[IO.File]::ReadAllText($file);if(!$code.Contains('#include <type_traits>')){$code='#include <type_traits>'+[Environment]::NewLine+$code};if(!$code.Contains('#include <exception>')){$code='#include <exception>'+[Environment]::NewLine+$code};[IO.File]::WriteAllText($file,$code)
$file=Join-Path $whisper 'examples/common.cpp';$code=[IO.File]::ReadAllText($file);if(!$code.Contains('#include <algorithm>')){$code='#include <algorithm>'+[Environment]::NewLine+$code};[IO.File]::WriteAllText($file,$code)
$common=@('-G','Ninja',"-DCMAKE_MAKE_PROGRAM=$ninja",'-DCMAKE_C_COMPILER=x86_64-w64-mingw32-clang.exe','-DCMAKE_CXX_COMPILER=x86_64-w64-mingw32-clang++.exe',"-DCMAKE_BUILD_TYPE=$Configuration")
& $cmake -S native -B .tools/native-build @common "-DLLAMA_SOURCE=$llama" "-DUSEARCH_SOURCE=$usearch"
if($LASTEXITCODE){throw 'Native configuration failed.'}
& $cmake --build .tools/native-build --config $Configuration --target findit_native usearch_c -j $Jobs
if($LASTEXITCODE){throw 'Native embedding/ANN build failed.'}
Get-ChildItem .tools/native-build -Recurse -Filter '*.dll' | Copy-Item -Destination runtime/native -Force
& $cmake -S $whisper -B .tools/whisper-build @common '-DBUILD_SHARED_LIBS=OFF' '-DGGML_NATIVE=OFF' '-DGGML_OPENMP=OFF' '-DWHISPER_BUILD_TESTS=OFF' '-DWHISPER_BUILD_SERVER=OFF'
if($LASTEXITCODE){throw 'Whisper configuration failed.'}
& $cmake --build .tools/whisper-build --config $Configuration --target whisper-cli -j $Jobs
if($LASTEXITCODE){throw 'Whisper build failed.'}
Copy-Item .tools/whisper-build/bin/whisper-cli.exe runtime/tools -Force
foreach($name in @('libc++.dll','libunwind.dll','libwinpthread-1.dll')){Copy-Item (Join-Path $toolchain $name) runtime/native -Force;Copy-Item (Join-Path $toolchain $name) runtime/tools -Force}
# NuGet dependencies are copied beside our native loader, avoiding a host VC++ installation requirement.
foreach($spec in @(@('opencvsharp4.runtime.win','OpenCvSharpExtern.dll'),@('tesseract','leptonica-1.82.0.dll'),@('tesseract','tesseract50.dll'))){$candidate=Get-ChildItem (Join-Path .packages $spec[0]) -Recurse -Filter $spec[1] -ErrorAction SilentlyContinue | Where-Object FullName -match 'win-x64|[\\/]x64[\\/]' | Select-Object -First 1;if($candidate){Copy-Item -LiteralPath $candidate.FullName -Destination runtime/native -Force}}
Write-Host 'Native binaries are ready in runtime/native and runtime/tools.'
