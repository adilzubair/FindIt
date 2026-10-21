#requires -Version 7.2
[CmdletBinding()]
param([Parameter(Mandatory)][string]$PackageRoot,[string]$ReportPath)
$ErrorActionPreference='Stop'
$taskPackage=[IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($PackageRoot))
$taskArchive=Join-Path $taskPackage 'FindIt/archive'
if(Test-Path -LiteralPath $taskArchive){throw 'Use a fresh generated package. This check will not touch an existing archive.'}
$taskVersion=([IO.File]::ReadAllText((Join-Path $taskPackage 'FindIt/app/current.txt'))).Trim()
if($taskVersion -notmatch '^[A-Za-z0-9._-]+$' -or $taskVersion.Contains('..')){throw 'Invalid application version.'}
$taskWorker=Join-Path $taskPackage "FindIt/app/$taskVersion/FindIt.Worker.exe"
if(!(Test-Path -LiteralPath $taskWorker)){throw 'Published self-contained worker is missing.'}
$taskFixture=Join-Path $taskPackage ('findit-validation-'+[Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($taskFixture)|Out-Null
[IO.File]::WriteAllText((Join-Path $taskFixture 'invoice.txt'),'Invoice AB-0091 permits two cats.')
[IO.File]::WriteAllText((Join-Path $taskFixture 'travel.txt'),'A red bicycle rides through a forest.')
$taskToken=[Threading.CancellationTokenSource]::new([TimeSpan]::FromSeconds(90))
function Start-Worker([string]$Role){
    $info=[Diagnostics.ProcessStartInfo]::new($taskWorker)
    $info.UseShellExecute=$false;$info.CreateNoWindow=$true
    $info.RedirectStandardInput=$true;$info.RedirectStandardOutput=$true;$info.RedirectStandardError=$true
    foreach($argument in @('--root',$taskPackage,'--role',$Role)){$info.ArgumentList.Add($argument)}
    $process=[Diagnostics.Process]::Start($info)
    # Drain diagnostics without allowing native logs to block redirected pipes.
    $diagnostics=$process.StandardError.ReadToEndAsync()
    return @{ Process=$process; Diagnostics=$diagnostics }
}
function Read-Exact($Stream,[byte[]]$Buffer){
    $offset=0
    while($offset -lt $Buffer.Length){
        $read=$Stream.ReadAsync($Buffer,$offset,$Buffer.Length-$offset,$taskToken.Token).GetAwaiter().GetResult()
        if($read -eq 0){throw 'Published worker terminated before its response.'}
        $offset+=$read
    }
}
function Invoke-Rpc($Connection,[string]$Id,[string]$Method,$Parameters){
    $json=@{jsonrpc='2.0';id=$Id;method=$Method;params=$Parameters}|ConvertTo-Json -Depth 12 -Compress
    $bytes=[Text.Encoding]::UTF8.GetBytes($json)
    $inputStream=$Connection.Process.StandardInput.BaseStream
    $inputStream.Write([BitConverter]::GetBytes([int]$bytes.Length));$inputStream.Write($bytes);$inputStream.Flush()
    while($true){
        $header=[byte[]]::new(4);Read-Exact $Connection.Process.StandardOutput.BaseStream $header
        $length=[BitConverter]::ToInt32($header,0)
        if($length -lt 1 -or $length -gt 8MB){throw 'Invalid worker frame.'}
        $payload=[byte[]]::new($length);Read-Exact $Connection.Process.StandardOutput.BaseStream $payload
        $message=[Text.Encoding]::UTF8.GetString($payload)|ConvertFrom-Json
        if($message.id -ne $Id){continue}
        if($message.error){throw $message.error.message}
        return $message.result
    }
}
function Close-Worker($Connection){
    if($null -eq $Connection){return}
    $Connection.Process.StandardInput.Close()
    if(!$Connection.Process.WaitForExit(15000)){$Connection.Process.Kill($true);$Connection.Process.WaitForExit()}
    $Connection.Process.Dispose()
}
$taskPreparer=$null;$taskReader=$null
try{
    $taskPreparer=Start-Worker 'preparer'
    $taskPrepared=Invoke-Rpc $taskPreparer 'prepare' 'archive.prepare' @{folders=@([IO.Path]::GetFileName($taskFixture));ocrLanguages=@('eng')}
    Close-Worker $taskPreparer;$taskPreparer=$null
    if($taskPrepared.fileCount -ne 2 -or !$taskPrepared.semanticAvailable){throw 'Published preparation did not create two semantic files.'}
    $taskReader=Start-Worker 'reader'
    $taskExact=Invoke-Rpc $taskReader 'exact' 'archive.search' @{query='AB-0091';filters=@{exactOnly=$true};limit=10}
    $taskSemantic=Invoke-Rpc $taskReader 'semantic' 'archive.search' @{query='cycling among trees';filters=@{};limit=10}
    if(@($taskExact.results).Count -ne 1 -or $taskExact.results[0].name -ne 'invoice.txt'){throw 'Published exact search failed.'}
    if(!$taskSemantic.semanticUsed -or @($taskSemantic.results).Count -lt 1){throw 'Published local embedding search failed.'}
    $taskReport=[ordered]@{passed=$true;version=$taskVersion;worker=$taskWorker;files=$taskPrepared.fileCount;records=$taskPrepared.segmentCount;exactTop=$taskExact.results[0].name;semanticUsed=$taskSemantic.semanticUsed;semanticTop=$taskSemantic.results[0].name;scope='Actual self-contained published worker with on-drive models. This is not a physical-drive or clean-machine certification.'}
    $taskReport|ConvertTo-Json -Depth 8
    if($ReportPath){[IO.File]::WriteAllText([IO.Path]::GetFullPath($ReportPath),($taskReport|ConvertTo-Json -Depth 8))}
}
finally{
    Close-Worker $taskPreparer;Close-Worker $taskReader;$taskToken.Dispose()
    # Only these two fresh, explicitly checked app-owned test paths are eligible for cleanup.
    foreach($taskOwned in @($taskFixture,$taskArchive)){
        $taskAbsolute=[IO.Path]::GetFullPath($taskOwned)
        if(!$taskAbsolute.StartsWith($taskPackage+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Refusing test cleanup outside package.'}
        if(Test-Path -LiteralPath $taskAbsolute){Remove-Item -LiteralPath $taskAbsolute -Recurse -Force}
    }
}
