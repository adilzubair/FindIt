using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FindIt.Client;
using FindIt.Shared;

namespace FindIt.DriveCheck;

/// <summary>Explicit supplemental tests; never invoked by the ordinary phase-one validation.</summary>
internal sealed class ExtrasValidation
{
    private const string Probe="Library/Validation/window-hop-check.wav";
    private readonly string root,worker,reportPath;
    private readonly CancellationTokenSource budget=new(TimeSpan.FromMinutes(15));
    private readonly ValidationReport report=new()
    {
        Kind="actual-portable-drive-supplemental-validation",
        Scope="36-second audio window coverage, metadata-only pack timestamp change, read-only archive files, and an optional owned R: subst alias. A drive-letter alias is not a physical unplug/replug test. The operation budget is 15 minutes; safe metadata cleanup has an independent, bounded 120-second grace per attempt."
    };
    private readonly JsonSerializerOptions json=new(WireJson.Options){WriteIndented=true};
    private string[] originals=[];
    private bool ownProbe;
    private bool probeNeedsReconciliation;
    private bool aliasOwned;
    private string? ownedAliasTarget;
    private FileStream? readerChecksBarrier;
    private static readonly TimeSpan CleanupTimeout=TimeSpan.FromSeconds(120);
    private int failed;
    private CancellationToken Token=>budget.Token;

    public ExtrasValidation(string root,string worker,string reportPath)
    {
        this.root=Path.GetFullPath(root);this.worker=Path.GetFullPath(worker);this.reportPath=Path.GetFullPath(reportPath);
        if(!OperatingSystem.IsWindows())throw new PlatformNotSupportedException("Drive extras require Windows.");
        if(!Directory.Exists(this.root)||!File.Exists(this.worker)||!Within(this.root,this.worker))throw new IOException("Select the existing drive and its packaged worker.");
        if(Within(this.root,this.reportPath))throw new ArgumentException("The JSON report must be outside the archive drive.");
        RejectLinks(this.worker);RejectLinks(Resolve("Library"));report.RootPath=this.root;report.WorkerPath=this.worker;
    }

    public async Task<int> RunAsync()
    {
        string? oldWorker=Environment.GetEnvironmentVariable("FINDIT_WORKER_PATH");
        ConsoleCancelEventHandler cancel=(_,e)=>{e.Cancel=true;budget.Cancel();};Console.CancelKeyPress+=cancel;
        Environment.SetEnvironmentVariable("FINDIT_WORKER_PATH",worker);
        try
        {
            if(!await Step("extras-baseline-originals-and-generation",async()=>
            {
                originals=EnumerateRegularFiles(Resolve("Library")).Select(Relative).Order(StringComparer.OrdinalIgnoreCase).ToArray();
                Check(originals.Length==13,"Phase one must leave exactly 13 original files before extras start.");
                Check(!File.Exists(Resolve(Probe)),"Refusing to overwrite an existing window-hop probe.");
                report.OriginalsBefore=await HashOriginals(Token);
                await using var backend=await Reader(root);var status=await backend.GetStatusAsync(Token);report.StatusBefore=status;
                Check(status.FileCount==13&&status.Issues.Count==0,"Extras require a prepared archive containing 13 originals and zero issues.");
                return new{status,files=report.OriginalsBefore};
            }))return 1;
            await Step("audio-36-second-window-hop-and-cleanup",CheckAudioWindows);
            bool quiescent=await Step("extras-clean-publication-and-writer-quiescence",EnsureReaderChecksReady);
            if(quiescent)
            {
                await Step("pack-mtime-change-preserves-generation-and-content",CheckPackTimestamp);
                await Step("read-only-catalog-and-people-reader",CheckReadOnlyArchive);
                await Step("owned-r-drive-letter-alias",CheckDriveAlias);
            }
            else
            {
                foreach(string dependent in new[]{"pack-mtime-change-preserves-generation-and-content","read-only-catalog-and-people-reader","owned-r-drive-letter-alias"})
                    await Skip(dependent,"The 13-file reconciliation and writer-quiescence prerequisite did not complete; no dependent metadata or alias mutation was attempted.");
            }
        }
        catch(Exception error) {failed++;report.FatalError=error.ToString();}
        finally
        {
            readerChecksBarrier?.Dispose();readerChecksBarrier=null;
            if(ownProbe||probeNeedsReconciliation)
            {
                try
                {
                    using var cleanup=new CancellationTokenSource(CleanupTimeout);
                    if(probeNeedsReconciliation)
                    {
                        await using var backend=await Reader(root,cleanup.Token);
                        await CleanupAudioProbe(backend,cleanup.Token);
                    }
                    else DeleteProbe();
                }
                catch(Exception error){RecordCleanupFailure("Final probe cleanup/reconciliation",error);}
            }
            if(aliasOwned)
            {
                try { await RemoveOwnedAlias(CancellationToken.None); }
                catch(Exception error){failed++;report.CleanupErrors.Add("Alias cleanup: "+error);}
            }
            if(report.OriginalsBefore.Count==13)
            {
                await Step("extras-original-sha256-unchanged",async()=>
                {
                    report.OriginalsAfter=await HashOriginals(CancellationToken.None);
                    foreach(var before in report.OriginalsBefore)
                    {
                        var after=report.OriginalsAfter.Single(f=>f.Path==before.Path);
                        Check(before.Bytes==after.Bytes&&before.Sha256==after.Sha256,"Original content changed: "+before.Path);
                    }
                    return new{count=13,allSha256Unchanged=true};
                },allowAfterBudget:true);
            }
            report.RemainingValidationFiles=File.Exists(Resolve(Probe))?[Probe]:[];
            if(probeNeedsReconciliation)report.CleanupErrors.Add("Archive reconciliation is incomplete: the last catalog may still contain "+Probe+". The original 13 files are preserved; run normal preparation after recovering the drive.");
            report.CompletedUtc=DateTimeOffset.UtcNow;report.TotalElapsedMilliseconds=(report.CompletedUtc.Value-report.StartedUtc).TotalMilliseconds;report.Success=failed==0;
            Environment.SetEnvironmentVariable("FINDIT_WORKER_PATH",oldWorker);Console.CancelKeyPress-=cancel;
            await SaveReport();budget.Dispose();
            Log($"Extras {(report.Success?"passed":"finished with failures")}; report: {reportPath}");
        }
        return failed==0?0:1;
    }

    private async Task<object?> CheckAudioWindows()
    {
        string workspace=FindWorkspace();
        string source=Path.Combine(workspace,"artifacts","usb-e2e-media","Audio","jfk-inaugural-speech.wav");
        string ffmpeg=Path.Combine(workspace,"runtime","tools","ffmpeg.exe");
        Check(File.Exists(source)&&File.Exists(ffmpeg),"The existing public fixture WAV and workspace FFmpeg are required; no download or drive-side generation is attempted.");
        string outputDirectory=Path.Combine(workspace,"artifacts","usb-extras",report.RunId);
        Check(Within(workspace,outputDirectory),"Workspace output escapes the project.");Directory.CreateDirectory(outputDirectory);
        string generated=Path.Combine(outputDirectory,"window-hop-check.wav");
        var generationTimer=Stopwatch.StartNew();
        await RunProcess(ffmpeg,["-nostdin","-hide_banner","-loglevel","error","-stream_loop","-1","-i",source,"-t","36","-ac","1","-ar","16000","-c:a","pcm_s16le","-y",generated],Token);
        generationTimer.Stop();
        Check(File.Exists(generated)&&new FileInfo(generated).Length>36*16000*2,"The 36-second PCM fixture was not generated.");
        Log($"Generated 36-second window probe in workspace ({generationTimer.Elapsed.TotalMilliseconds:N0} ms).");
        string probe=Resolve(Probe);RejectLinks(probe);Check(!File.Exists(probe),"Refusing to overwrite an existing probe.");
        Directory.CreateDirectory(Path.GetDirectoryName(probe)!);RejectLinks(probe);
        var copyTimer=Stopwatch.StartNew();Log($"COPY START {generated} -> {Probe}");
        await using(var input=new FileStream(generated,FileMode.Open,FileAccess.Read,FileShare.Read))
        await using(var output=new FileStream(probe,FileMode.CreateNew,FileAccess.Write,FileShare.None))
        {ownProbe=true;await input.CopyToAsync(output,Token);await output.FlushAsync(Token);}
        copyTimer.Stop();
        Log($"COPY COMPLETE {Probe} ({copyTimer.Elapsed.TotalMilliseconds:N0} ms).");
        await using var backend=await Reader(root);
        object? result=null;
        Exception? operationError=null;
        try
        {
            probeNeedsReconciliation=true;
            var prepared=await backend.PrepareAsync(Request(),new ProgressTrace("audio-window"),Token);
            Check(prepared.FileCount==14&&prepared.Issues.Count==0,"Preparing the audio probe should produce 14 files and zero issues.");
            var filename=await backend.SearchAsync(new(){Query="window-hop-check.wav",Filters=new(){ExactOnly=true},Limit=100},Token);
            var item=filename.Results.Single(r=>PathEquals(r.RelativePath,Probe));
            var preview=await backend.GetPreviewAsync(new(item.FileId),Token);
            var audio=preview.Moments.Where(m=>m.Kind==SegmentKind.Audio).ToArray();
            var transcript=preview.Moments.Where(m=>m.Kind==SegmentKind.Transcript).ToArray();
            Check(audio.Any(m=>m.StartMs>=25000),"The second audio embedding window did not advance to at least 25 seconds.");
            Check(transcript.Any(m=>m.StartMs>=25000),"Transcript passages did not cover the final window at or after 25 seconds.");
            Check(audio.Concat(transcript).All(m=>m.StartMs>=0&&m.EndMs>m.StartMs&&m.EndMs<=36100),"Audio/transcript intervals exceed the 36-second source boundary.");
            var country=await backend.SearchAsync(new(){Query="country",Filters=new(){ExactOnly=true,Kind=FileKind.Audio,Folder="Library/Validation"},Limit=100},Token);
            var countryResult=country.Results.SingleOrDefault(r=>PathEquals(r.RelativePath,Probe));
            Check(countryResult is not null&&countryResult.Moments.Any(m=>m.Kind==SegmentKind.Transcript&&m.StartMs>=25000&&m.Text.Contains("country",StringComparison.OrdinalIgnoreCase)),"Exact country search did not return a transcript passage from the last audio window.");
            result=new{workspaceSource=source,generatedSource=generated,generationMilliseconds=generationTimer.Elapsed.TotalMilliseconds,copyMilliseconds=copyTimer.Elapsed.TotalMilliseconds,prepared.GenerationId,audioWindows=audio,transcriptPassages=transcript,country=countryResult};
        }
        catch(Exception error){operationError=error;throw;}
        finally
        {
            try
            {
                // Metadata-only cleanup must still run after Ctrl+C or the main 15-minute budget.
                using var cleanup=new CancellationTokenSource(CleanupTimeout);
                await CleanupAudioProbe(backend,cleanup.Token);
            }
            catch(Exception error)
            {
                RecordCleanupFailure("Audio probe cleanup/reconciliation",error);
                if(operationError is null)throw;
            }
        }
        return result;
    }

    private async Task CleanupAudioProbe(WorkerArchiveClient backend,CancellationToken cancellationToken)
    {
        DeleteProbe();
        if(!probeNeedsReconciliation)return;
        Log("CLEANUP reconciling the original 13 files with an independent 120-second budget.");
        var clean=await backend.PrepareAsync(Request(),new ProgressTrace("audio-window-clean"),cancellationToken);
        Check(clean.FileCount==13&&clean.Issues.Count==0,"Removing the probe did not restore 13 originals and zero issues.");
        report.FinalStatus=clean;probeNeedsReconciliation=false;
    }

    private async Task<object?> EnsureReaderChecksReady()
    {
        if(ownProbe||probeNeedsReconciliation)
        {
            using var cleanup=new CancellationTokenSource(CleanupTimeout);
            try
            {
                await using var recovery=await Reader(root,cleanup.Token);
                await CleanupAudioProbe(recovery,cleanup.Token);
            }
            catch(Exception error){RecordCleanupFailure("Prerequisite probe reconciliation",error);throw;}
        }
        Check(!ownProbe&&!probeNeedsReconciliation&&!File.Exists(Resolve(Probe)),"Probe deletion/reconciliation is incomplete.");
        string writerLock=Resolve("FindIt/archive/preparation.lock");RejectLinks(writerLock);
        Check(File.Exists(writerLock),"The prepared archive writer-lock file is missing.");
        // Open the existing file only. Holding this read handle excludes a new writer
        // throughout the dependent checks; it creates or modifies no drive artifact.
        readerChecksBarrier=new FileStream(writerLock,FileMode.Open,FileAccess.Read,FileShare.None);
        ArchiveStatus first;
        await using(var reader=await Reader(root))first=await reader.GetStatusAsync(Token);
        ArchiveStatus second;
        await using(var reader=await Reader(root))second=await reader.GetStatusAsync(Token);
        Log($"QUIESCENCE first generation={first.GenerationId} files={first.FileCount}; second generation={second.GenerationId} files={second.FileCount}.");
        Check(first.FileCount==13&&second.FileCount==13&&first.Issues.Count==0&&second.Issues.Count==0&&first.GenerationId==second.GenerationId,
            $"Reader checks require two stable 13-file statuses with zero issues. First={first.GenerationId}/{first.FileCount} ({first.StatusDetail}); second={second.GenerationId}/{second.FileCount} ({second.StatusDetail}).");
        report.FinalStatus=second;
        return new{writerLockHeld=true,probeReconciled=true,first,second};
    }

    private async Task<object?> CheckPackTimestamp()
    {
        await using var initial=await Reader(root);var status=await initial.GetStatusAsync(Token);
        Check(readerChecksBarrier is not null&&status.FileCount==13,$"Mtime check requires the quiescent 13-file archive; initial generation={status.GenerationId}, files={status.FileCount}.");
        string manifestPath=Resolve($"FindIt/archive/generations/{status.GenerationId:D20}/manifest.json");RejectLinks(manifestPath);
        using var manifest=JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath,Token));
        string relative=manifest.RootElement.GetProperty("vectorPacks").EnumerateArray().Select(v=>v.GetString()!).First(p=>p.EndsWith(".pack",StringComparison.OrdinalIgnoreCase));
        string pack=Resolve(relative);RejectLinks(pack);
        string beforeHash=await HashFile(pack,Token);var originalWrite=File.GetLastWriteTimeUtc(pack);var originalAccess=File.GetLastAccessTimeUtc(pack);var originalCreation=File.GetCreationTimeUtc(pack);
        DateTime changedWrite=originalWrite.AddHours(1);ArchiveStatus? reopened=null;
        try
        {
            File.SetLastWriteTimeUtc(pack,changedWrite);
            Check(File.GetLastWriteTimeUtc(pack)==changedWrite,"The filesystem did not apply the one-hour mtime-only change.");
            await using var fresh=await Reader(root);reopened=await fresh.GetStatusAsync(Token);
            Check(reopened.GenerationId==status.GenerationId&&reopened.FileCount==13,
                $"Mtime check expected generation={status.GenerationId}, files=13; reopened generation={reopened.GenerationId}, files={reopened.FileCount}, detail={reopened.StatusDetail}.");
            Check(await HashFile(pack,Token)==beforeHash,"Pack bytes changed during the metadata-only test.");
        }
        finally
        {
            RejectLinks(pack);File.SetLastWriteTimeUtc(pack,originalWrite);File.SetLastAccessTimeUtc(pack,originalAccess);
        }
        Check(File.GetLastWriteTimeUtc(pack)==originalWrite&&await HashFile(pack,Token)==beforeHash,"The pack's exact original mtime and SHA-256 were not restored.");
        return new{relative,sha256=beforeHash,originalWriteUtc=originalWrite,changedWriteUtc=changedWrite,originalAccessUtc=originalAccess,originalCreationUtc=originalCreation,status=reopened};
    }

    private async Task<object?> CheckReadOnlyArchive()
    {
        await using var initial=await Reader(root);var status=await initial.GetStatusAsync(Token);
        Check(readerChecksBarrier is not null&&status.FileCount==13,$"Read-only metadata check requires the quiescent 13-file archive; initial generation={status.GenerationId}, files={status.FileCount}.");
        string[] files=[Resolve($"FindIt/archive/generations/{status.GenerationId:D20}/catalog.sqlite"),Resolve("FindIt/archive/people.sqlite")];
        var saved=new List<ProtectedFile>();
        foreach(string file in files)
        {
            RejectLinks(file);Check(File.Exists(file),"Prepared archive metadata is missing: "+file);
            saved.Add(new(file,File.GetAttributes(file),await HashFile(file,Token)));
        }
        object? result=null;List<Exception> cleanupErrors=[];
        try
        {
            foreach(var file in saved)File.SetAttributes(file.Path,file.Attributes|FileAttributes.ReadOnly);
            await using var fresh=await Reader(root);
            var exact=await fresh.SearchAsync(new(){Query="AB-0091",Filters=new(){ExactOnly=true},Limit=100},Token);
            var semantic=await fresh.SearchAsync(new(){Query="pale canine outdoors",Filters=new(){Kind=FileKind.Image},Limit=3},Token);
            Check(exact.Results.Count>=7,"Exact search failed against read-only archive metadata.");
            Check(semantic.SemanticUsed&&semantic.Results.Count>0,"Semantic reader failed against read-only archive metadata.");
            var after=await fresh.GetStatusAsync(Token);Check(after.GenerationId==status.GenerationId&&after.FileCount==13,
                $"Read-only metadata check expected generation={status.GenerationId}, files=13; actual generation={after.GenerationId}, files={after.FileCount}, detail={after.StatusDetail}.");
            foreach(var file in saved)Check(await HashFile(file.Path,Token)==file.Sha256,"Reader changed read-only archive bytes: "+file.Path);
            result=new{generation=status.GenerationId,files=saved.Select(f=>new{path=Relative(f.Path),originalAttributes=f.Attributes.ToString(),f.Sha256}),exactCount=exact.Results.Count,semantic.SemanticUsed,semanticTop=semantic.Results.Select(r=>r.RelativePath)};
        }
        finally
        {
            foreach(var file in saved)
            {
                try {RejectLinks(file.Path);File.SetAttributes(file.Path,file.Attributes);}
                catch(Exception error){cleanupErrors.Add(error);}
            }
            if(cleanupErrors.Count>0)throw new AggregateException("Unable to restore exact archive file attributes.",cleanupErrors);
        }
        foreach(var file in saved)Check(File.GetAttributes(file.Path)==file.Attributes,"Original file attributes were not restored.");
        return result;
    }

    private async Task<object?> CheckDriveAlias()
    {
        Check(readerChecksBarrier is not null,"Alternate-letter check requires the quiescent archive prerequisite.");
        string? existing=DosTarget("R:");
        bool logicalPresent=(Native.GetLogicalDrives()&(1u<<('R'-'A')))!=0;
        if(existing is not null||logicalPresent)return new{attempted=false,reason="R: is already assigned; no alias was changed.",existingTarget=existing,physicalReplugTested=false};
        string subst=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"subst.exe");
        string expected="\\??\\"+root;
        object? result=null;
        try
        {
            Token.ThrowIfCancellationRequested();
            // subst refuses an occupied letter. Finish this short, bounded ownership transition
            // before observing caller cancellation, so a successful creation cannot go untracked.
            await RunProcess(subst,["R:",root],CancellationToken.None);
            aliasOwned=true;ownedAliasTarget=expected;
            string? target=DosTarget("R:");
            Check(target is not null&&target.TrimEnd('\\').Equals(expected.TrimEnd('\\'),StringComparison.OrdinalIgnoreCase),"subst returned an unexpected device target; refusing to remove a different mapping.");
            ownedAliasTarget=target;
            Token.ThrowIfCancellationRequested();
            await using var backend=await Reader("R:\\");
            var status=await backend.GetStatusAsync(Token);Check(status.FileCount==13,
                $"Alias check expected 13 files; actual generation={status.GenerationId}, files={status.FileCount}, detail={status.StatusDetail}.");
            var exact=await backend.SearchAsync(new(){Query="AB-0091",Filters=new(){ExactOnly=true},Limit=100},Token);
            var semantic=await backend.SearchAsync(new(){Query="pale canine outdoors",Filters=new(){Kind=FileKind.Image},Limit=3},Token);
            Check(exact.Results.Count>=7&&semantic.SemanticUsed&&semantic.Results.Count>0,"Exact or semantic search failed through the alternate letter.");
            var preview=await backend.GetPreviewAsync(new(exact.Results[0].FileId),Token);
            Check(preview.OriginalPath.StartsWith("R:\\",StringComparison.OrdinalIgnoreCase)&&File.Exists(preview.OriginalPath),"Preview did not resolve an existing original through R:.");
            result=new{attempted=true,alias="R:",target,physicalReplugTested=false,scope="Temporary subst alias only; no physical disconnect/reconnect.",status.GenerationId,exactCount=exact.Results.Count,semantic.SemanticUsed,previewOriginalPath=preview.OriginalPath};
        }
        finally
        {
            try {await RemoveOwnedAlias(CancellationToken.None);}
            catch(Exception error){RecordCleanupFailure("Alias cleanup",error);throw;}
        }
        Check(!string.Equals(DosTarget("R:"),ownedAliasTarget,StringComparison.OrdinalIgnoreCase),"The harness-owned R: alias remains assigned.");
        return result;
    }

    private Task RemoveOwnedAlias(CancellationToken cancellationToken)
    {
        if(!aliasOwned)return Task.CompletedTask;
        Check(!string.IsNullOrEmpty(ownedAliasTarget),"Alias ownership target is missing.");
        // Exact removal is atomic: a different mapping added after our last query is retained.
        // https://learn.microsoft.com/windows/win32/api/fileapi/nf-fileapi-definedosdevicew
        const uint rawTarget=1,remove=2,exactMatch=4;
        if(!Native.DefineDosDevice(rawTarget|remove|exactMatch,"R:",ownedAliasTarget!))
        {
            int error=Marshal.GetLastWin32Error();
            if(error is not (2 or 3))throw new Win32Exception(error,"Unable to remove the exact harness-owned R: mapping.");
            // subst may strip a root's final slash. If verification failed after creation,
            // try that exact raw spelling too; never use a prefix or unconditional removal.
            string trimmed=ownedAliasTarget!.TrimEnd('\\');
            if(trimmed!=ownedAliasTarget&&!Native.DefineDosDevice(rawTarget|remove|exactMatch,"R:",trimmed))
            {
                error=Marshal.GetLastWin32Error();
                if(error is not (2 or 3))throw new Win32Exception(error,"Unable to remove the exact harness-owned R: mapping.");
            }
            string? remaining=DosTarget("R:");
            Check(remaining is null||!remaining.TrimEnd('\\').Equals(trimmed,StringComparison.OrdinalIgnoreCase),"The expected R: mapping remains after exact removal; ownership cleanup requires attention.");
        }
        aliasOwned=false;return Task.CompletedTask;
    }

    private static string? DosTarget(string name)
    {
        var buffer=new StringBuilder(32768);
        if(Native.QueryDosDevice(name,buffer,buffer.Capacity)>0)return buffer.ToString().Split('\0')[0];
        int error=Marshal.GetLastWin32Error();
        if(error is 2 or 3)return null;
        throw new Win32Exception(error,"Unable to check whether R: is assigned.");
    }
    private async Task<WorkerArchiveClient> Reader(string readerRoot,CancellationToken? cancellationToken=null)=>(WorkerArchiveClient)await WorkerArchiveClient.CreateAsync(readerRoot,cancellationToken??Token);
    private void RecordCleanupFailure(string action,Exception error)
    {
        failed++;report.CleanupErrors.Add(action+": "+error);Log("CLEANUP FAILED "+action+": "+error.Message);
    }
    private static PreparationRequest Request()=>new(){Folders=["Library"],OcrLanguages=["eng"]};
    private async Task<bool> Step(string name,Func<Task<object?>> action,bool allowAfterBudget=false)
    {
        var step=new StepRecord{Name=name,StartedUtc=DateTimeOffset.UtcNow};report.Steps.Add(step);var timer=Stopwatch.StartNew();Log("START "+name);
        try {if(!allowAfterBudget)Token.ThrowIfCancellationRequested();step.Details=await action();step.Outcome="passed";}
        catch(Exception error){failed++;step.Outcome=error is OperationCanceledException?"cancelled":"failed";step.Error=error.ToString();Log($"FAIL {name}: {error.Message}");}
        finally
        {
            step.ElapsedMilliseconds=timer.Elapsed.TotalMilliseconds;
            try{await SaveReport();}catch(Exception error)when(error is IOException or UnauthorizedAccessException){failed++;report.CleanupErrors.Add("Report checkpoint: "+error);}
        }
        Log($"{step.Outcome.ToUpperInvariant()} {name} ({step.ElapsedMilliseconds:N0} ms)");return step.Outcome=="passed";
    }
    private async Task Skip(string name,string reason)
    {
        report.Steps.Add(new(){Name=name,StartedUtc=DateTimeOffset.UtcNow,Outcome="skipped",Details=new{reason}});
        Log("SKIPPED "+name+": "+reason);await SaveReport();
    }
    private async Task SaveReport()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);string temporary=reportPath+"."+report.RunId+".tmp";
        await File.WriteAllTextAsync(temporary,JsonSerializer.Serialize(report,json),new UTF8Encoding(false));File.Move(temporary,reportPath,true);
    }
    private async Task<List<OriginalHash>> HashOriginals(CancellationToken cancellationToken)
    {
        var result=new List<OriginalHash>();
        foreach(string relative in originals)
        {
            string path=Resolve(relative);RejectLinks(path);result.Add(new(relative,new FileInfo(path).Length,await HashFile(path,cancellationToken),File.GetLastWriteTimeUtc(path)));
        }
        return result;
    }
    private static async Task<string> HashFile(string path,CancellationToken cancellationToken)
    {
        await using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read,65536,FileOptions.Asynchronous|FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream,cancellationToken)).ToLowerInvariant();
    }
    private void DeleteProbe()
    {
        if(!ownProbe)return;string path=Resolve(Probe);RejectLinks(path);if(File.Exists(path))File.Delete(path);ownProbe=false;
    }
    private IEnumerable<string> EnumerateRegularFiles(string folder)
    {
        RejectLinks(folder);
        foreach(string path in Directory.EnumerateFileSystemEntries(folder))
        {
            RejectLinks(path);
            if(Directory.Exists(path)){foreach(string file in EnumerateRegularFiles(path))yield return file;}
            else yield return path;
        }
    }
    private void RejectLinks(string path)
    {
        Check(Within(root,path)||PathEquals(root,path),"Path escapes the selected drive.");string current=root;
        foreach(string part in Path.GetRelativePath(root,path).Split(Path.DirectorySeparatorChar))
        {
            if(part==".")continue;current=Path.Combine(current,part);
            if((File.Exists(current)||Directory.Exists(current))&&(File.GetAttributes(current)&FileAttributes.ReparsePoint)!=0)throw new IOException("Linked paths are not permitted: "+current);
        }
    }
    private string Resolve(string relative)
    {
        string path=Path.GetFullPath(Path.Combine(root,relative.Replace('/',Path.DirectorySeparatorChar)));Check(Within(root,path),"Path escapes selected drive.");return path;
    }
    private string Relative(string path)=>Path.GetRelativePath(root,path).Replace('\\','/');
    private static bool Within(string parent,string child)=>Path.GetFullPath(child).StartsWith(Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase);
    private static bool PathEquals(string a,string b)=>a.Replace('\\','/').Equals(b.Replace('\\','/'),StringComparison.OrdinalIgnoreCase);
    private static void Check(bool condition,string message){if(!condition)throw new ValidationException(message);}
    private static void Log(string message){Console.WriteLine(message);Console.Out.Flush();}
    private static string FindWorkspace()
    {
        foreach(string start in new[]{Directory.GetCurrentDirectory(),AppContext.BaseDirectory})
            for(var directory=new DirectoryInfo(start);directory is not null;directory=directory.Parent)
                if(File.Exists(Path.Combine(directory.FullName,"tools","FindIt.DriveCheck","FindIt.DriveCheck.csproj")))return directory.FullName;
        throw new DirectoryNotFoundException("Run the harness from its FindIt workspace to generate the public audio fixture locally.");
    }
    private static async Task RunProcess(string executable,IReadOnlyList<string> arguments,CancellationToken cancellationToken)
    {
        var start=new ProcessStartInfo(executable){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true};
        foreach(string argument in arguments)start.ArgumentList.Add(argument);
        using var process=Process.Start(start)??throw new IOException("Unable to start "+Path.GetFileName(executable));process.StandardInput.Close();
        var output=process.StandardOutput.ReadToEndAsync();var error=process.StandardError.ReadToEndAsync();
        using var bounded=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);bounded.CancelAfter(TimeSpan.FromSeconds(45));
        using var registration=bounded.Token.Register(()=>{try{if(!process.HasExited)process.Kill(true);}catch(InvalidOperationException){}catch(Win32Exception){}});
        await process.WaitForExitAsync(bounded.Token);await output;string diagnostic=await error;
        if(process.ExitCode!=0)throw new IOException(Path.GetFileName(executable)+" failed: "+diagnostic);
    }
    private sealed record ProtectedFile(string Path,FileAttributes Attributes,string Sha256);
    private static class Native
    {
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]internal static extern uint QueryDosDevice(string name,StringBuilder target,int capacity);
        [DllImport("kernel32.dll",SetLastError=true)]internal static extern uint GetLogicalDrives();
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
        [return:MarshalAs(UnmanagedType.Bool)]internal static extern bool DefineDosDevice(uint flags,string name,string target);
    }
}
