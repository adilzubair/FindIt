using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FindIt.Client;
using FindIt.Shared;

namespace FindIt.DriveCheck;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        string? Option(string name) { int index=Array.IndexOf(args,name);return index>=0&&index+1<args.Length?args[index+1]:null; }
        if(Option("--root") is not { } root||Option("--worker") is not { } worker||Option("--report") is not { } report)
        {
            Console.Error.WriteLine("Usage: FindIt.DriveCheck --root D:\\ --worker D:\\FindIt\\app\\0.1.0-preview.3\\FindIt.Worker.exe --report <workspace-report.json> [--reader-only | --extras | --eject]");
            return 2;
        }
        try
        {
            if(new[]{"--reader-only","--extras","--eject"}.Count(mode=>args.Contains(mode,StringComparer.Ordinal))>1)
                throw new ArgumentException("Select only one validation mode per command.");
            if(args.Contains("--reader-only",StringComparer.Ordinal))return await new ReaderValidation(root,worker,report).RunAsync();
            if(args.Contains("--eject",StringComparer.Ordinal))
            {
                if(args.Contains("--extras",StringComparer.Ordinal))throw new ArgumentException("Run extras and final eject as separate commands.");
                return await RequestFinalEject(root,worker,report);
            }
            if(args.Contains("--extras",StringComparer.Ordinal))return await new ExtrasValidation(root,worker,report).RunAsync();
            return await new DriveValidation(root,worker,report).RunAsync();
        }
        catch(Exception error) { Console.Error.WriteLine(error);return 1; }
    }

    private static async Task<int> RequestFinalEject(string root,string worker,string reportPath)
    {
        root=Path.GetFullPath(root);reportPath=Path.GetFullPath(reportPath);
        if(reportPath.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The final eject report must be outside the removable drive.");
        Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
        var started=DateTimeOffset.UtcNow;
        // Prove the host-side report can be persisted before requesting removal. This branch starts no workers.
        await Persist(new{kind="final-windows-safe-eject-request",rootPath=root,workerPath=Path.GetFullPath(worker),startedUtc=started,state="requesting",note="All validation workers and drive writes must be closed before invoking this explicit final command."});
        var result=await DriveEjector.RequestAsync(root);
        await Persist(new{kind="final-windows-safe-eject-request",rootPath=root,workerPath=Path.GetFullPath(worker),startedUtc=started,completedUtc=DateTimeOffset.UtcNow,result});
        Console.WriteLine(result.Message);Console.Out.Flush();return result.Requested?0:1;

        async Task Persist(object value)
        {
            var bytes=JsonSerializer.SerializeToUtf8Bytes(value,new JsonSerializerOptions(WireJson.Options){WriteIndented=true});
            await using var stream=new FileStream(reportPath,FileMode.Create,FileAccess.Write,FileShare.Read);
            await stream.WriteAsync(bytes);stream.Flush(true);
        }
    }
}

internal sealed class DriveValidation
{
    private static readonly string[] Originals=
    [
        "Library/Photos/golden-gate-bridge.png","Library/Photos/white-dog-on-grass.jpg",
        "Library/Photos/citrus-fruit-bowl.jpg","Library/Photos/reference-portrait.jpg",
        "Library/Videos/bridge-dog-portrait-with-speech.mp4","Library/Audio/jfk-inaugural-speech.wav",
        "Library/Documents/lease-cats-AB-0091.txt","Library/Documents/رحلة-الصيف.txt",
        "Library/Documents/invoice-AB-0091.pdf","Library/Documents/lease-agreement.docx",
        "Library/Documents/travel-budget.xlsx","Library/Documents/inspection-notes.rtf","Library/Documents/trip-summary.html"
    ];
    private const string Video="Library/Videos/bridge-dog-portrait-with-speech.mp4";
    private const string Portrait="Library/Photos/reference-portrait.jpg";
    private const string Update="Library/Validation/update-check.txt";
    private const string Resume="Library/Validation/resume-check.mp4";
    private const string Broken="Library/Validation/decoder-failure.jpg";
    private readonly string root,worker,reportPath;
    private readonly ValidationReport report=new();
    private readonly HashSet<string> ownedFiles=new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource budget=new(TimeSpan.FromMinutes(15));
    private readonly JsonSerializerOptions json=new(WireJson.Options){WriteIndented=true};
    private WorkerArchiveClient? client;
    private Guid? testPerson;
    private Guid? splitPerson;
    private long selectedObservation;
    private bool prepared;
    private int failed;
    private CancellationToken Token=>budget.Token;
    private WorkerArchiveClient Client=>client??throw new InvalidOperationException("Worker has not started.");

    public DriveValidation(string root,string worker,string reportPath)
    {
        this.root=Path.GetFullPath(root);this.worker=Path.GetFullPath(worker);this.reportPath=Path.GetFullPath(reportPath);
        if(!Directory.Exists(this.root)||!File.Exists(this.worker))throw new FileNotFoundException("The selected drive or packaged worker is unavailable.");
        if(!Within(this.root,this.worker))throw new ArgumentException("Use the actual packaged worker on the selected drive.");
        if(Within(this.root,this.reportPath))throw new ArgumentException("Write the validation report to the workspace, outside the archive drive.");
        RejectLinks(this.worker);RejectLinks(Path.Combine(this.root,"Library"));
        report.RootPath=this.root;report.WorkerPath=this.worker;
    }

    public async Task<int> RunAsync()
    {
        string? previousWorker=Environment.GetEnvironmentVariable("FINDIT_WORKER_PATH");
        ConsoleCancelEventHandler cancel=(_,e)=>{e.Cancel=true;budget.Cancel();};
        Console.CancelKeyPress+=cancel;
        Environment.SetEnvironmentVariable("FINDIT_WORKER_PATH",worker);
        try
        {
            if(!await Step("original-inventory",async()=>
            {
                foreach(var relative in Originals) { RejectLinks(Resolve(relative));Check(File.Exists(Resolve(relative)),"Missing fixture original: "+relative); }
                string[] actual=Directory.EnumerateFiles(Resolve("Library"),"*",SearchOption.AllDirectories).Select(Relative).Order(StringComparer.OrdinalIgnoreCase).ToArray();
                Check(actual.SequenceEqual(Originals.Order(StringComparer.OrdinalIgnoreCase),StringComparer.OrdinalIgnoreCase),"Library must contain exactly the 13 fixture originals before validation. Unexpected files are never overwritten.");
                foreach(var relative in new[]{Update,Resume,Broken})Check(!File.Exists(Resolve(relative)),"Validation filename already exists: "+relative);
                report.OriginalsBefore=await HashOriginals(Token);
                return new{count=actual.Length,totalBytes=report.OriginalsBefore.Sum(f=>f.Bytes),files=actual};
            }))return 1;
            if(!await Step("packaged-worker-start",async()=>
            {
                client=(WorkerArchiveClient)await WorkerArchiveClient.CreateAsync(root,Token);
                report.StatusBefore=await Client.GetStatusAsync(Token);
                return new{status=report.StatusBefore,diagnostics=await Client.GetDiagnosticsAsync(Token)};
            }))return 1;
            prepared=await Step("initial-preparation",async()=>
            {
                var trace=new ProgressTrace("initial");
                var status=await Client.PrepareAsync(Request(verify:true),trace,Token);
                Check(status.FileCount==13,$"Expected 13 prepared originals; found {status.FileCount}.");
                Check(status.Issues.Count==0,"Initial preparation reported issues: "+JsonSerializer.Serialize(status.Issues,WireJson.Options));
                Check(status.SemanticAvailable,"The actual packaged model must make semantic search available.");
                report.InitialStatus=status;return new{status,progress=trace.Snapshot()};
            });
            if(!prepared)return 1;

            await Step("exact-identifiers-documents-and-unicode",CheckDocuments);
            await Step("speech-audio-and-video",CheckSpeech);
            await Step("semantic-top-three-quality-observations",RecordSemanticQuality);
            await Step("packaged-runtime-module-paths",CheckProcessModules);
            await Step("video-moments-and-preview",CheckVideo);
            await Step("folder-type-and-date-filters",CheckFilters);
            bool enrolled=await Step("person-enrollment-and-timed-observations",EnrollPerson);
            if(enrolled)
            {
                await Step("person-alias-and-negative-cooccurrence",CheckPersonQueries);
                await Step("person-confirm-reject-restore",CorrectPerson);
                await Step("person-split-and-merge",SplitAndMergePerson);
            }
            await Step("incremental-add-verify-same-size-edit-delete",CheckIncremental);
            await Step("unchanged-preparation-reuses-all-originals",CheckUnchanged);
            await Step("cancel-media-keep-search-resume-and-delete",CheckCancellation);
            await Step("decoder-failure-is-visible-and-recoverable",CheckDecoderFailure);
        }
        catch(OperationCanceledException error)
        {
            failed++;report.FatalError="Validation cancelled or reached the 15-minute budget: "+error.Message;
        }
        catch(Exception error) { failed++;report.FatalError=error.ToString(); }
        finally
        {
            // Cleanup is limited to exact files this process successfully created. No folder is removed.
            foreach(var path in ownedFiles.ToArray())
            {
                try { DeleteOwned(path); }
                catch(Exception error) { failed++;report.CleanupErrors.Add(error.ToString()); }
            }
            if(client is not null&&prepared&&!Token.IsCancellationRequested)
            {
                await Step("final-clean-reconciliation",async()=>
                {
                    var status=await Client.PrepareAsync(Request(),new ProgressTrace("final"),Token);
                    Check(status.FileCount==13&&status.Issues.Count==0,$"Final archive should contain 13 originals and zero issues; found {status.FileCount} files and {status.Issues.Count} issues.");
                    report.FinalStatus=status;return status;
                });
            }
            if(report.OriginalsBefore.Count==13)
            {
                // Only 2.7 MiB: finish this read-only integrity check even when the operation budget expired.
                await Step("original-sha256-unchanged",async()=>
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
            if(client is not null)
            {
                try { await client.DisposeAsync(); }
                catch(Exception error) { failed++;report.CleanupErrors.Add("Worker shutdown: "+error); }
            }
            report.RemainingValidationFiles=ownedFiles.Where(File.Exists).Select(Relative).ToArray();
            report.TestPersonId=testPerson;report.RemainingSplitPersonId=splitPerson;
            report.CompletedUtc=DateTimeOffset.UtcNow;report.Success=failed==0;
            report.TotalElapsedMilliseconds=(report.CompletedUtc.Value-report.StartedUtc).TotalMilliseconds;
            Environment.SetEnvironmentVariable("FINDIT_WORKER_PATH",previousWorker);
            Console.CancelKeyPress-=cancel;
            await SaveReport();budget.Dispose();
            Console.WriteLine($"Validation {(report.Success?"passed":"finished with failures")}: {report.Steps.Count} steps. Report: {reportPath}");
        }
        return failed==0?0:1;
    }

    private async Task<object?> CheckDocuments()
    {
        var exact=await Search("AB-0091",new(){ExactOnly=true});
        string[] expected=Originals.Where(p=>p.StartsWith("Library/Documents/",StringComparison.Ordinal)).ToArray();
        foreach(var path in expected)Check(exact.Results.Any(r=>PathEquals(r.RelativePath,path)),"AB-0091 did not find document: "+path);
        var details=new List<object>();
        foreach(var path in expected)
        {
            var result=exact.Results.Single(r=>PathEquals(r.RelativePath,path));
            var preview=await Client.GetPreviewAsync(new(result.FileId,result.Moments.FirstOrDefault()?.SegmentId),Token);
            Check(PathEquals(preview.OriginalPath,Resolve(path))&&File.Exists(preview.OriginalPath),"Document preview did not resolve its real original.");
            Check(preview.Text.Contains("AB-0091",StringComparison.OrdinalIgnoreCase),"Document body extraction lost identifier: "+path);
            if(path.EndsWith(".xlsx",StringComparison.OrdinalIgnoreCase))
            {
                string text=string.Join('\n',preview.Moments.Select(m=>m.Text));
                Check(text.Contains("A2: AB-0091",StringComparison.Ordinal)&&text.Contains("C2: 129.95",StringComparison.Ordinal),"Workbook did not preserve exact values and cell locators.");
            }
            details.Add(new{path,preview.Page,preview.Text,momentCount=preview.Moments.Count});
        }
        var unicode=await Search("رحلة-الصيف",new(){ExactOnly=true});
        Check(unicode.Results.Any(r=>r.Name=="رحلة-الصيف.txt"),"Unicode filename search failed.");
        foreach(string hidden in new[]{"HIDDEN_SCRIPT_SENTINEL","HIDDEN_HTML_COMMENT_SENTINEL","HIDDEN_RTF_SENTINEL"})
            Check((await Search(hidden,new(){ExactOnly=true})).Results.Count==0,"Non-body content was indexed: "+hidden);
        return new{exact=Summary(exact),documents=details,unicode=Summary(unicode)};
    }

    private async Task<object?> CheckSpeech()
    {
        var response=await Search("country",new(){ExactOnly=true});
        foreach(var path in new[]{Video,"Library/Audio/jfk-inaugural-speech.wav"})
        {
            var result=response.Results.SingleOrDefault(r=>PathEquals(r.RelativePath,path));
            Check(result is not null,"Spoken word country was not transcribed in "+path);
            Check(result!.Moments.Any(m=>m.Kind==SegmentKind.Transcript&&m.StartMs>=0&&m.EndMs>m.StartMs&&m.EndMs<=14000),"Speech result lacks valid timestamped transcript passages.");
        }
        return Summary(response);
    }

    private async Task<object?> RecordSemanticQuality()
    {
        var observations=new List<object>();
        foreach(var pair in new[]{("a fluffy white dog sitting on green grass","Library/Photos/white-dog-on-grass.jpg"),("a red suspension bridge over water","Library/Photos/golden-gate-bridge.png"),("sliced citrus fruit and kiwi","Library/Photos/citrus-fruit-bowl.jpg"),("pale canine outdoors","Library/Photos/white-dog-on-grass.jpg"),("fresh lemons beside oranges","Library/Photos/citrus-fruit-bowl.jpg"),("suspension crossing spanning bay","Library/Photos/golden-gate-bridge.png")})
        {
            var response=await Search(pair.Item1,new(){Kind=FileKind.Image});
            Check(response.SemanticUsed,"Query encoder did not run: "+response.Notice);
            int rank=Array.FindIndex(response.Results.ToArray(),r=>PathEquals(r.RelativePath,pair.Item2))+1;
            observations.Add(new{query=pair.Item1,expectedPhoto=pair.Item2,expectedPhotoRank=rank>0?(int?)rank:null,expectedPhotoInTopThree=rank is >0 and <=3,topThree=response.Results.Take(3).Select(ResultSummary),response.ElapsedMilliseconds,response.Notice});
        }
        return new{scope="Observed quality on six image-only fixture queries, including three paraphrases without the original filename terms; no recall certification or top-three pass threshold.",observations};
    }

    private async Task<object?> CheckProcessModules()
    {
        var diagnostics=await Client.GetDiagnosticsAsync(Token);
        using var process=Process.GetProcessById(diagnostics.ProcessId);
        process.Refresh();
        var modules=process.Modules.Cast<ProcessModule>().Select(m=>new ModuleRecord(m.ModuleName,m.FileName)).OrderBy(m=>m.Name).ToArray();
        foreach(string name in new[]{"coreclr.dll","findit_native.dll","libllama.dll","libusearch_c.dll"})
        {
            var module=modules.FirstOrDefault(m=>m.Name.Equals(name,StringComparison.OrdinalIgnoreCase));
            Check(module is not null,"Packaged reader did not load expected module: "+name);
            Check(Within(root,module!.Path),"Module loaded outside the portable drive: "+module.Path);
        }
        Check(PathEquals(process.MainModule!.FileName,worker),"The running process is not the explicitly selected packaged worker.");
        return new{diagnostics,mainModule=process.MainModule.FileName,modules,scope="Live reader process; external decoder/preparer modules require separate process tracing."};
    }

    private async Task<object?> CheckVideo()
    {
        var found=(await Search("bridge-dog-portrait-with-speech.mp4",new(){ExactOnly=true})).Results.Single(r=>PathEquals(r.RelativePath,Video));
        var preview=await Client.GetPreviewAsync(new(found.FileId),Token);
        Check(PathEquals(preview.OriginalPath,Resolve(Video))&&File.Exists(preview.OriginalPath),"Video preview did not resolve the actual drive original.");
        var frames=preview.Moments.Where(m=>m.Kind==SegmentKind.VideoFrame&&m.Thumbnail is not null).ToArray();
        var clips=preview.Moments.Where(m=>m.Kind==SegmentKind.VideoClip).ToArray();
        Check(frames.Length>=14,"Video lacks its ordinary one-frame-per-second visual coverage.");
        Check(clips.Length>=2&&clips.All(m=>m.EndMs-m.StartMs==8000),"Video lacks the two expected eight-second temporal clips.");
        var visual=preview.Moments.Where(m=>m.Kind is SegmentKind.VideoFrame or SegmentKind.VideoClip).ToArray();
        Check(visual.All(m=>m.StartMs>=0&&m.EndMs>=m.StartMs&&m.EndMs<=14000),"Visual moments lie outside the actual 14-second source.");
        // AAC uses 1,024-sample frames at 16 kHz (64 ms); decoded tail padding is not visual coverage.
        var audio=preview.Moments.Where(m=>m.Kind is SegmentKind.Audio or SegmentKind.Transcript).ToArray();
        Check(audio.All(m=>m.StartMs>=0&&m.EndMs>=m.StartMs&&m.EndMs<=14100),"Audio timestamps exceed the source plus one AAC frame and timestamp rounding.");
        var selected=frames.FirstOrDefault(m=>m.StartMs>=6500&&m.StartMs<10500)??frames[0];
        var atMoment=await Client.GetPreviewAsync(new(found.FileId,selected.SegmentId),Token);
        Check(atMoment.StartMs==selected.StartMs,"Selecting a timestamped moment did not select its preview position.");
        return new{preview.OriginalPath,frameCount=frames.Length,clipCount=clips.Length,audioTailAllowanceMs=100,supplementalHalfSecondFrames=frames.Where(m=>m.StartMs%1000==500).Select(m=>m.StartMs),selectedStartMs=atMoment.StartMs,moments=preview.Moments};
    }

    private async Task<object?> CheckFilters()
    {
        var images=await Search("",new(){Kind=FileKind.Image});
        Check(images.Results.Count==4&&images.Results.All(r=>r.Kind==FileKind.Image),"Image type filter did not select four photographs.");
        var documents=await Search("",new(){Folder="Library/Documents"});
        Check(documents.Results.Count==7&&documents.Results.All(r=>r.RelativePath.StartsWith("Library/Documents/",StringComparison.OrdinalIgnoreCase)),"Folder filter did not select seven documents.");
        var all=await Search("");Check(all.Results.Count==13,"Browse did not return all originals.");
        var min=all.Results.Min(r=>r.ModifiedUtc).AddSeconds(-2);var max=all.Results.Max(r=>r.ModifiedUtc).AddSeconds(2);
        var inclusive=await Search("",new(){AfterUtc=min,BeforeUtc=max});Check(inclusive.Results.Count==13,"Inclusive modified-date range lost originals.");
        var future=await Search("",new(){AfterUtc=max.AddDays(1)});Check(future.Results.Count==0,"Future date filter returned older originals.");
        return new{images=Summary(images),documents=Summary(documents),min,max,inclusiveCount=inclusive.Results.Count,futureCount=future.Results.Count};
    }

    private async Task<object?> EnrollPerson()
    {
        long generation=(await Client.GetStatusAsync(Token)).GenerationId;
        var existing=(await Client.GetPeopleAsync(Token)).FirstOrDefault(p=>p.Name=="Test Person"&&p.Confirmed);
        var saved=await Client.SavePersonAsync(new(){PersonId=existing?.Id,Name="Test Person",Aliases=["Demo Person"],ReferencePaths=[Resolve(Portrait)]},Token);
        testPerson=saved.Id;
        var observations=await Client.GetPersonObservationsAsync(saved.Id,Token);
        Check(observations.Any(o=>PathEquals(o.RelativePath,Portrait)&&!o.Rejected),"Reference portrait was not assigned to the enrolled person.");
        var video=observations.Where(o=>PathEquals(o.RelativePath,Video)&&!o.Rejected).ToArray();
        Check(video.Length>0,"Reference identity did not match the repeated video portrait.");
        Check(video.All(o=>o.StartMs>=6500&&o.StartMs<10500),"Named video observations extended outside the actual portrait shot.");
        selectedObservation=video[0].Id;
        Check((await Client.GetStatusAsync(Token)).GenerationId==generation,"People enrollment rebuilt the main archive generation.");
        return new{generation,person=saved,observations};
    }

    private async Task<object?> CheckPersonQueries()
    {
        var alias=await Search("Demo Person",new(){ExactOnly=true});
        Check(alias.People.Any(p=>p.PersonId==testPerson),"Alias was not resolved to the enrolled person.");
        Check(alias.Results.Any(r=>PathEquals(r.RelativePath,Portrait))&&alias.Results.Any(r=>PathEquals(r.RelativePath,Video)),"Named-person search omitted the photo or video.");
        var negative=await Search("Test Person AB-0091",new(){ExactOnly=true});
        Check(negative.Results.Count==0,"Identity evidence was incorrectly attached to unrelated invoice documents.");
        return new{alias=Summary(alias),negative=Summary(negative)};
    }

    private async Task<object?> CorrectPerson()
    {
        long generation=(await Client.GetStatusAsync(Token)).GenerationId;
        var id=testPerson??throw new InvalidOperationException("Enrollment required.");
        await Client.SavePersonAsync(new(){PersonId=id,Name="Test Person",Aliases=["Demo Person"],ConfirmObservationIds=[selectedObservation]},Token);
        var confirmed=(await Client.GetPersonObservationsAsync(id,Token)).Single(o=>o.Id==selectedObservation);
        Check(confirmed.Confirmed&&!confirmed.Rejected,"Confirm observation did not persist.");
        try
        {
            await Client.SavePersonAsync(new(){PersonId=id,Name="Test Person",Aliases=["Demo Person"],RejectObservationIds=[selectedObservation]},Token);
            var rejected=(await Client.GetPersonObservationsAsync(id,Token)).Single(o=>o.Id==selectedObservation);
            Check(rejected.Rejected,"Rejected observation remained accepted.");
        }
        finally { await Client.SavePersonAsync(new(){PersonId=id,Name="Test Person",Aliases=["Demo Person"],ConfirmObservationIds=[selectedObservation]},Token); }
        var restored=(await Client.GetPersonObservationsAsync(id,Token)).Single(o=>o.Id==selectedObservation);
        Check(restored.Confirmed&&!restored.Rejected,"Observation was not restored after rejection.");
        Check((await Client.GetStatusAsync(Token)).GenerationId==generation,"People corrections rebuilt the main generation.");
        return new{generation,confirmed,restored};
    }

    private async Task<object?> SplitAndMergePerson()
    {
        long generation=(await Client.GetStatusAsync(Token)).GenerationId;
        var id=testPerson??throw new InvalidOperationException("Enrollment required.");
        string splitName="Test Person Split "+report.RunId[..8];
        var split=await Client.SavePersonAsync(new(){Name=splitName,ConfirmObservationIds=[selectedObservation]},Token);splitPerson=split.Id;
        try
        {
            Check((await Client.GetPersonObservationsAsync(split.Id,Token)).Any(o=>o.Id==selectedObservation&&!o.Rejected),"Split did not assign the selected observation to the new person.");
            Check(!(await Client.GetPersonObservationsAsync(id,Token)).Any(o=>o.Id==selectedObservation),"Split left the same observation assigned to both people.");
        }
        finally { await Client.SavePersonAsync(new(){PersonId=split.Id,Name=splitName,MergeIntoPersonId=id},Token);splitPerson=null; }
        Check(!(await Client.GetPeopleAsync(Token)).Any(p=>p.Id==split.Id),"Merge left the temporary split person behind.");
        Check((await Client.GetPersonObservationsAsync(id,Token)).Any(o=>o.Id==selectedObservation&&!o.Rejected),"Merge did not restore the observation to Test Person.");
        Check((await Client.GetStatusAsync(Token)).GenerationId==generation,"Split or merge changed the main generation.");
        return new{generation,personId=id,splitPersonId=split.Id,observationId=selectedObservation};
    }

    private async Task<object?> CheckIncremental()
    {
        string path=Resolve(Update);await CreateOwnedText(path,"PORTABLE-UPDATE-7481\n");
        try
        {
            var added=await Client.PrepareAsync(Request(verify:true),new ProgressTrace("add"),Token);
            Check(added.FileCount==14&&(await Search("PORTABLE-UPDATE-7481",new(){ExactOnly=true})).Results.Any(r=>PathEquals(r.RelativePath,Update)),"Added original did not become searchable.");
            var modified=File.GetLastWriteTimeUtc(path);long bytes=new FileInfo(path).Length;
            RejectLinks(path);await File.WriteAllTextAsync(path,"PORTABLE-UPDATE-7482\n",new UTF8Encoding(false),Token);File.SetLastWriteTimeUtc(path,modified);
            Check(new FileInfo(path).Length==bytes&&File.GetLastWriteTimeUtc(path)==modified,"Same-size/mtime fixture edit did not preserve metadata.");
            var verified=await Client.PrepareAsync(Request(verify:true),new ProgressTrace("verify-edit"),Token);
            var newResult=await Search("PORTABLE-UPDATE-7482",new(){ExactOnly=true});var oldResult=await Search("PORTABLE-UPDATE-7481",new(){ExactOnly=true});
            Check(newResult.Results.Any(r=>PathEquals(r.RelativePath,Update))&&oldResult.Results.Count==0,"Explicit content verification did not detect the same-size, same-mtime edit.");
            DeleteOwned(path);
            var deleted=await Client.PrepareAsync(Request(),new ProgressTrace("delete"),Token);
            Check(deleted.FileCount==13&&(await Search("PORTABLE-UPDATE-7482",new(){ExactOnly=true})).Results.Count==0,"Deleted original remained in the reconciled archive.");
            return new{addedGeneration=added.GenerationId,verifiedGeneration=verified.GenerationId,deletedGeneration=deleted.GenerationId,bytes,preservedModifiedUtc=modified};
        }
        finally { DeleteOwned(path); }
    }

    private async Task<object?> CheckUnchanged()
    {
        var progress=new ProgressTrace("unchanged");var status=await Client.PrepareAsync(Request(),progress,Token);
        var final=progress.Snapshot().LastOrDefault(p=>p.Stage==PreparationStage.Completed);
        Check(status.FileCount==13&&final is not null&&final.Completed==13&&final.Unchanged==13,"Unchanged preparation did not reuse all 13 originals.");
        return new{status.GenerationId,progress=progress.Snapshot()};
    }

    private async Task<object?> CheckCancellation()
    {
        string path=Resolve(Resume);await CreateOwnedCopy(Resolve(Video),path);
        try
        {
            using var cancellation=CancellationTokenSource.CreateLinkedTokenSource(Token);
            bool requested=false;var observed=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var progress=new ProgressTrace("cancel",p=>
            {
                if(!requested&&p.Stage==PreparationStage.Extracting&&PathEquals(p.CurrentPath,Resume))
                {requested=true;observed.TrySetResult();cancellation.Cancel();}
            });
            var cancelTimer=Stopwatch.StartNew();
            bool cancelled=false;
            try { await Client.PrepareAsync(Request(),progress,cancellation.Token); }
            catch(OperationCanceledException) when(!Token.IsCancellationRequested) {cancelled=true;}
            cancelTimer.Stop();
            Check(requested&&cancelled,"Preparation did not checkpoint the requested media cancellation.");
            var stillSearchable=await Search("AB-0091",new(){ExactOnly=true});
            Check(stillSearchable.Results.Count>=7,"The prior searchable documents became unavailable after cancellation.");
            var resumeTimer=Stopwatch.StartNew();var resumed=await Client.PrepareAsync(Request(),new ProgressTrace("resume"),Token);resumeTimer.Stop();
            Check(resumed.FileCount==14&&resumed.Issues.Count==0,"Cancelled video did not prepare successfully on resume.");
            var copy=(await Search("resume-check.mp4",new(){ExactOnly=true})).Results.Single(r=>PathEquals(r.RelativePath,Resume));
            Check((await Client.GetPreviewAsync(new(copy.FileId),Token)).Moments.Any(m=>m.Kind==SegmentKind.VideoClip),"Resumed video lacks its temporal clip records.");
            DeleteOwned(path);var deleted=await Client.PrepareAsync(Request(),new ProgressTrace("remove-resume"),Token);
            Check(deleted.FileCount==13&&(await Search("resume-check.mp4",new(){ExactOnly=true})).Results.Count==0,"Deleted cancellation fixture remained searchable.");
            return new{cancelled,cancelAndCheckpointElapsedMilliseconds=cancelTimer.Elapsed.TotalMilliseconds,resumeElapsedMilliseconds=resumeTimer.Elapsed.TotalMilliseconds,progress=progress.Snapshot(),oldSearch=Summary(stillSearchable),resumedGeneration=resumed.GenerationId,finalGeneration=deleted.GenerationId};
        }
        finally { DeleteOwned(path); }
    }

    private async Task<object?> CheckDecoderFailure()
    {
        string path=Resolve(Broken);await CreateOwnedText(path,"This deliberately invalid JPEG is a local decoder-failure test.\n");
        try
        {
            var status=await Client.PrepareAsync(Request(),new ProgressTrace("decoder-failure"),Token);
            Check(status.Issues.Any(i=>PathEquals(i.RelativePath,Broken)),"Invalid image did not produce a visible issue.");
            var filename=await Search("decoder-failure.jpg",new(){ExactOnly=true});
            Check(filename.Results.Any(r=>PathEquals(r.RelativePath,Broken)),"A decoder failure hid the original filename.");
            DeleteOwned(path);var clean=await Client.PrepareAsync(Request(),new ProgressTrace("decoder-clean"),Token);
            Check(clean.FileCount==13&&clean.Issues.Count==0,"Removing the invalid image did not restore a clean archive.");
            return new{issues=status.Issues,filename=Summary(filename),cleanGeneration=clean.GenerationId};
        }
        finally { DeleteOwned(path); }
    }

    private async Task<bool> Step(string name,Func<Task<object?>> action,bool allowAfterBudget=false)
    {
        var step=new StepRecord{Name=name,StartedUtc=DateTimeOffset.UtcNow};var timer=Stopwatch.StartNew();report.Steps.Add(step);
        Console.WriteLine("START "+name);Console.Out.Flush();
        try
        {
            if(!allowAfterBudget)Token.ThrowIfCancellationRequested();
            step.Details=await action();step.Outcome="passed";
        }
        catch(Exception error)
        {
            failed++;step.Outcome=error is OperationCanceledException?"cancelled":"failed";step.Error=error.ToString();
            Console.Error.WriteLine($"FAIL {name}: {error.Message}");
        }
        finally
        {
            step.ElapsedMilliseconds=timer.Elapsed.TotalMilliseconds;
            try { await SaveReport(); }
            catch(Exception error) when(error is IOException or UnauthorizedAccessException)
            {failed++;report.CleanupErrors.Add("Report checkpoint: "+error.Message);Console.Error.WriteLine("Unable to save report checkpoint: "+error.Message);}
        }
        Console.WriteLine($"{step.Outcome.ToUpperInvariant()} {name} ({step.ElapsedMilliseconds:N0} ms)");
        Console.Out.Flush();Console.Error.Flush();
        return step.Outcome=="passed";
    }

    private Task<SearchResponse> Search(string query,SearchFilters? filters=null)=>Client.SearchAsync(new(){Query=query,Filters=filters??new(),Limit=1000},Token);
    private static PreparationRequest Request(bool verify=false)=>new(){Folders=["Library"],OcrLanguages=["eng"],VerifyContents=verify};
    private static void Check(bool condition,string message){if(!condition)throw new ValidationException(message);}
    private static object ResultSummary(SearchResult r)=>new{r.RelativePath,r.Kind,r.MatchCategory,r.Score,r.Snippet,moments=r.Moments.Select(m=>new{m.Kind,m.StartMs,m.EndMs,m.Page})};
    private static object Summary(SearchResponse response)=>new{response.GenerationId,response.SemanticUsed,response.ElapsedMilliseconds,response.Notice,response.People,results=response.Results.Select(ResultSummary)};
    private static bool PathEquals(string a,string b)=>a.Replace('\\','/').Equals(b.Replace('\\','/'),StringComparison.OrdinalIgnoreCase);
    private static bool Within(string parent,string child)=>Path.GetFullPath(child).StartsWith(Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase);
    private string Relative(string path)=>Path.GetRelativePath(root,path).Replace('\\','/');
    private string Resolve(string relative)
    {
        var path=Path.GetFullPath(Path.Combine(root,relative.Replace('/',Path.DirectorySeparatorChar)));
        if(!Within(root,path))throw new IOException("Path escapes selected drive.");return path;
    }
    private void RejectLinks(string path)
    {
        if(!Within(root,path)&&!PathEquals(root,path))throw new IOException("Path is outside selected drive.");
        var current=root;
        foreach(var part in Path.GetRelativePath(root,path).Split(Path.DirectorySeparatorChar))
        {
            if(part==".")continue;current=Path.Combine(current,part);
            if((File.Exists(current)||Directory.Exists(current))&&(File.GetAttributes(current)&FileAttributes.ReparsePoint)!=0)throw new IOException("Validation refuses linked files or directories: "+current);
        }
    }
    private void PrepareOwnedPath(string path)
    {
        Check(new[]{Resolve(Update),Resolve(Resume),Resolve(Broken)}.Contains(path,StringComparer.OrdinalIgnoreCase),"Unknown validation filename.");
        RejectLinks(path);Check(!File.Exists(path),"Refusing to overwrite existing file: "+path);Directory.CreateDirectory(Path.GetDirectoryName(path)!);RejectLinks(path);
    }
    private async Task CreateOwnedText(string path,string text)
    {
        PrepareOwnedPath(path);await using var output=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None);ownedFiles.Add(path);
        await output.WriteAsync(Encoding.UTF8.GetBytes(text),Token);await output.FlushAsync(Token);
    }
    private async Task CreateOwnedCopy(string source,string path)
    {
        var timer=Stopwatch.StartNew();Console.WriteLine($"COPY START {Relative(source)} -> {Relative(path)} ({new FileInfo(source).Length:N0} bytes)");Console.Out.Flush();
        PrepareOwnedPath(path);RejectLinks(source);
        await using var input=new FileStream(source,FileMode.Open,FileAccess.Read,FileShare.Read);
        await using var output=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None);ownedFiles.Add(path);
        await input.CopyToAsync(output,Token);await output.FlushAsync(Token);
        Console.WriteLine($"COPY COMPLETE {Relative(path)} ({timer.Elapsed.TotalMilliseconds:N0} ms)");Console.Out.Flush();
    }
    private void DeleteOwned(string path)
    {
        if(!ownedFiles.Contains(path))return;RejectLinks(path);if(File.Exists(path))File.Delete(path);
    }
    private async Task<List<OriginalHash>> HashOriginals(CancellationToken cancellationToken)
    {
        var result=new List<OriginalHash>();
        foreach(string relative in Originals)
        {
            string path=Resolve(relative);RejectLinks(path);
            await using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read,65536,FileOptions.Asynchronous|FileOptions.SequentialScan);
            result.Add(new(relative,stream.Length,Convert.ToHexString(await SHA256.HashDataAsync(stream,cancellationToken)).ToLowerInvariant(),File.GetLastWriteTimeUtc(path)));
        }
        return result;
    }
    private async Task SaveReport()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);string temp=reportPath+"."+report.RunId+".tmp";
        await File.WriteAllTextAsync(temp,JsonSerializer.Serialize(report,json),new UTF8Encoding(false));File.Move(temp,reportPath,true);
    }
}

internal sealed class ProgressTrace(string label,Action<PreparationProgress>? onProgress=null):IProgress<PreparationProgress>
{
    private readonly List<PreparationProgress> items=[];
    private readonly Stopwatch last=Stopwatch.StartNew();
    private PreparationStage? stage;
    public void Report(PreparationProgress value)
    {
        lock(items)
        {
            items.Add(value);
            if(stage!=value.Stage||last.Elapsed>TimeSpan.FromSeconds(5))
            {Console.WriteLine($"PROGRESS {label}: {value.Stage} {value.Completed}/{value.Discovered} {value.CurrentPath}");Console.Out.Flush();stage=value.Stage;last.Restart();}
        }
        onProgress?.Invoke(value);
    }
    public PreparationProgress[] Snapshot(){lock(items)return items.ToArray();}
}

internal sealed class ValidationException(string message):Exception(message);
internal sealed record OriginalHash(string Path,long Bytes,string Sha256,DateTime ModifiedUtc);
internal sealed record ModuleRecord(string Name,string Path);
internal sealed class StepRecord
{
    public string Name { get; init; }="";
    public DateTimeOffset StartedUtc { get; init; }
    public double ElapsedMilliseconds { get; set; }
    public string Outcome { get; set; }="running";
    public object? Details { get; set; }
    public string? Error { get; set; }
}
internal sealed class ValidationReport
{
    public string RunId { get; init; }=Guid.NewGuid().ToString("N");
    public string Kind { get; init; }="actual-portable-drive-functional-validation";
    public string Scope { get; init; }="Tiny public/synthetic fixture on the selected drive; no 1 TB throughput, independent recall, older-PC or cold-cache certification.";
    public DateTimeOffset StartedUtc { get; init; }=DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedUtc { get; set; }
    public double? TotalElapsedMilliseconds { get; set; }
    public int RuntimeBudgetMinutes { get; init; }=15;
    public string RootPath { get; set; }="";
    public string WorkerPath { get; set; }="";
    public bool Success { get; set; }
    public string? FatalError { get; set; }
    public ArchiveStatus? StatusBefore { get; set; }
    public ArchiveStatus? InitialStatus { get; set; }
    public ArchiveStatus? FinalStatus { get; set; }
    public List<OriginalHash> OriginalsBefore { get; set; }=[];
    public List<OriginalHash> OriginalsAfter { get; set; }=[];
    public List<StepRecord> Steps { get; init; }=[];
    public List<string> CleanupErrors { get; init; }=[];
    public string[] RemainingValidationFiles { get; set; }=[];
    public Guid? TestPersonId { get; set; }
    public Guid? RemainingSplitPersonId { get; set; }
}
