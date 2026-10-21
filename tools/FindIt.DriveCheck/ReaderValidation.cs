using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FindIt.Client;
using FindIt.Shared;

namespace FindIt.DriveCheck;

/// <summary>Final packaged-reader proof. This mode never prepares files or writes people metadata.</summary>
internal sealed class ReaderValidation
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
    private readonly string root,worker,workerFolder,reportPath;
    private readonly CancellationTokenSource budget=new(TimeSpan.FromMinutes(15));
    private readonly ValidationReport report=new()
    {
        Kind="final-packaged-reader-only-validation",
        Scope="Read-only APIs against the existing 13-file portable fixture and selected final worker. Original and archive SHA-256 snapshots prove unchanged bytes. Stored face-interval overlap is separate from the external 6.5–10.5 second portrait shot; sampled intervals can span a cut. This does not certify action roles, independent recall, physical replug, 1 TB throughput or an older PC."
    };
    private readonly JsonSerializerOptions json=new(WireJson.Options){WriteIndented=true};
    private List<OriginalHash> archiveBefore=[];
    private WorkerArchiveClient? client;
    private PreviewResult? videoPreview;
    private int failed;
    private CancellationToken Token=>budget.Token;
    private WorkerArchiveClient Client=>client??throw new InvalidOperationException("Reader has not started.");

    public ReaderValidation(string root,string worker,string reportPath)
    {
        this.root=Path.GetFullPath(root);this.worker=Path.GetFullPath(worker);this.workerFolder=Path.GetDirectoryName(this.worker)!;this.reportPath=Path.GetFullPath(reportPath);
        if(!Directory.Exists(this.root)||!File.Exists(this.worker)||!Within(this.root,this.worker))throw new IOException("Select the prepared drive and its actual packaged worker.");
        if(Within(this.root,this.reportPath))throw new ArgumentException("The report must be outside the portable drive.");
        RejectLinks(this.worker);RejectLinks(Resolve("Library"));report.RootPath=this.root;report.WorkerPath=this.worker;
    }

    public async Task<int> RunAsync()
    {
        string? oldWorker=Environment.GetEnvironmentVariable("FINDIT_WORKER_PATH");
        ConsoleCancelEventHandler cancel=(_,e)=>{e.Cancel=true;budget.Cancel();};Console.CancelKeyPress+=cancel;
        Environment.SetEnvironmentVariable("FINDIT_WORKER_PATH",worker);
        try
        {
            if(!await Step("reader-baseline-original-and-archive-hashes",async()=>
            {
                var actual=EnumerateFiles(Resolve("Library")).Select(Relative).Order(StringComparer.OrdinalIgnoreCase).ToArray();
                Check(actual.SequenceEqual(Originals.Order(StringComparer.OrdinalIgnoreCase),StringComparer.OrdinalIgnoreCase),"The library must contain the exact 13 fixture originals.");
                report.OriginalsBefore=await HashFiles(Originals,Token);
                archiveBefore=await HashArchive(Token);
                Check(archiveBefore.Count>0,"The prepared archive is missing.");
                return new{originals=report.OriginalsBefore,archiveFiles=archiveBefore};
            }))return 1;
            if(!await Step("reader-existing-status",async()=>
            {
                client=(WorkerArchiveClient)await WorkerArchiveClient.CreateAsync(root,Token);
                var status=await Client.GetStatusAsync(Token);report.InitialStatus=status;report.StatusBefore=status;
                Check(status.FileCount==13&&status.SegmentCount>=37&&status.SemanticAvailable&&status.Issues.Count==0,$"Expected 13 files, at least 37 segments, semantic search and zero issues; found {status.FileCount}/{status.SegmentCount}, semantic={status.SemanticAvailable}, issues={status.Issues.Count}.");
                return new{status,expectedMinimumSegments=37};
            }))return 1;
            await Step("reader-exact-seven-documents-and-unicode",CheckDocuments);
            await Step("reader-semantic-canine-photo-rank-one",CheckSemantic);
            await Step("reader-spoken-country-in-audio-and-video",CheckSpeech);
            await Step("reader-video-original-and-timed-moments",CheckVideo);
            await Step("reader-existing-person-alias-and-strict-temporal-overlap",CheckPeople);
            await Step("reader-final-package-module-paths",CheckModules);
            await Step("reader-generation-preserved",async()=>
            {
                var status=await Client.GetStatusAsync(Token);report.FinalStatus=status;
                Check(status.GenerationId==report.InitialStatus!.GenerationId&&status.FileCount==13,"Read-only queries changed the prepared generation.");
                return status;
            });
        }
        catch(Exception error){failed++;report.FatalError=error.ToString();}
        finally
        {
            if(client is not null)
            {
                try{await client.DisposeAsync();}catch(Exception error){failed++;report.CleanupErrors.Add("Reader shutdown: "+error);}
            }
            if(report.OriginalsBefore.Count==13)
            {
                await Step("reader-original-and-archive-bytes-unchanged",async()=>
                {
                    report.OriginalsAfter=await HashFiles(Originals,CancellationToken.None);
                    var archiveAfter=await HashArchive(CancellationToken.None);
                    CompareSnapshots(report.OriginalsBefore,report.OriginalsAfter,"original");
                    CompareSnapshots(archiveBefore,archiveAfter,"archive");
                    return new{originalCount=13,allOriginalSha256Unchanged=true,allArchiveSha256Unchanged=true,archiveFiles=archiveAfter};
                },allowAfterBudget:true);
            }
            report.CompletedUtc=DateTimeOffset.UtcNow;report.TotalElapsedMilliseconds=(report.CompletedUtc.Value-report.StartedUtc).TotalMilliseconds;report.Success=failed==0;
            Environment.SetEnvironmentVariable("FINDIT_WORKER_PATH",oldWorker);Console.CancelKeyPress-=cancel;
            await SaveReport();budget.Dispose();Log($"Reader-only {(report.Success?"passed":"finished with failures")}; report: {reportPath}");
        }
        return failed==0?0:1;
    }

    private async Task<object?> CheckDocuments()
    {
        var found=await Search("AB-0091",new(){ExactOnly=true});
        string[] documents=Originals.Where(p=>p.StartsWith("Library/Documents/",StringComparison.Ordinal)).ToArray();
        Check(found.Results.Count==7&&documents.All(p=>found.Results.Any(r=>PathEquals(r.RelativePath,p))),"AB-0091 should return exactly the seven document originals.");
        var unicode=await Search("رحلة-الصيف",new(){ExactOnly=true});
        var arabic=unicode.Results.SingleOrDefault(r=>r.Name=="رحلة-الصيف.txt");Check(arabic is not null,"Unicode filename was not searchable.");
        var preview=await Client.GetPreviewAsync(new(arabic!.FileId),Token);
        Check(File.Exists(preview.OriginalPath)&&PathEquals(preview.OriginalPath,Resolve("Library/Documents/رحلة-الصيف.txt")),"Arabic filename preview did not resolve its actual drive original.");
        return new{identifier=Summary(found),unicode=Summary(unicode),unicodeOriginalPath=preview.OriginalPath};
    }

    private async Task<object?> CheckSemantic()
    {
        var response=await Search("pale canine outdoors",new(){Kind=FileKind.Image});
        Check(response.SemanticUsed,"The packaged query encoder did not run: "+response.Notice);
        Check(response.Results.Count>0&&PathEquals(response.Results[0].RelativePath,"Library/Photos/white-dog-on-grass.jpg"),"The expected dog photograph was not the first result for the single fixture paraphrase.");
        return new{query="pale canine outdoors",expectedPhotoRank=1,scope="One fixture-specific first-result assertion; no independent recall claim.",response=Summary(response)};
    }

    private async Task<object?> CheckSpeech()
    {
        var response=await Search("country",new(){ExactOnly=true});
        foreach(string path in new[]{Video,"Library/Audio/jfk-inaugural-speech.wav"})
        {
            var result=response.Results.SingleOrDefault(r=>PathEquals(r.RelativePath,path));Check(result is not null,"Speech query omitted "+path);
            Check(result!.Moments.Any(m=>m.Kind==SegmentKind.Transcript&&m.StartMs>=0&&m.EndMs>m.StartMs&&m.EndMs<=14100),"Speech query lacks a source-timed transcript in "+path);
        }
        return Summary(response);
    }

    private async Task<object?> CheckVideo()
    {
        var preview=await VideoPreview();
        var visuals=preview.Moments.Where(m=>m.Kind is SegmentKind.VideoFrame or SegmentKind.VideoClip).ToArray();
        var clips=visuals.Where(m=>m.Kind==SegmentKind.VideoClip).ToArray();
        Check(visuals.Count(m=>m.Kind==SegmentKind.VideoFrame&&m.Thumbnail is not null)>=14,"Video lacks its one-Hz visual coverage.");
        Check(clips.Length>=2&&clips.All(m=>m.EndMs-m.StartMs==8000),"Video lacks its expected eight-second clip windows.");
        Check(visuals.All(m=>m.StartMs>=0&&m.EndMs>=m.StartMs&&m.EndMs<=14000),"Visual moments exceed the actual 14-second source.");
        Check(PathEquals(preview.OriginalPath,Resolve(Video))&&File.Exists(preview.OriginalPath),"Video preview points outside the selected original.");
        var selected=visuals.First(m=>m.Kind==SegmentKind.VideoFrame&&m.StartMs>=6500&&m.StartMs<10500);
        var atMoment=await Client.GetPreviewAsync(new(preview.FileId,selected.SegmentId),Token);Check(atMoment.StartMs==selected.StartMs,"Selected moment did not preserve its source position.");
        return new{preview.OriginalPath,selectedStartMs=atMoment.StartMs,visuals,allMoments=preview.Moments};
    }

    private async Task<object?> CheckPeople()
    {
        var people=await Client.GetPeopleAsync(Token);var person=people.SingleOrDefault(p=>p.Confirmed&&p.Name=="Test Person");
        Check(person is not null,"The existing confirmed Test Person is missing; reader-only mode does not enroll or repair identities.");
        report.TestPersonId=person!.Id;
        string? alias=person.Aliases.FirstOrDefault(a=>a.Replace(" ","",StringComparison.Ordinal).Equals("DemoPerson",StringComparison.OrdinalIgnoreCase));
        Check(alias is not null,"The existing Demo Person alias is missing.");
        var named=await Search(person.Name,new(){ExactOnly=true});var aliased=await Search(alias!,new(){ExactOnly=true});
        foreach(var response in new[]{named,aliased})
        {
            Check(response.People.Any(p=>p.PersonId==person.Id),"Name or alias did not resolve to Test Person.");
            Check(response.Results.Any(r=>PathEquals(r.RelativePath,Portrait))&&response.Results.Any(r=>PathEquals(r.RelativePath,Video)),"Name or alias omitted the reference photo or repeated video portrait.");
        }
        var observations=await Client.GetPersonObservationsAsync(person.Id,Token);
        var accepted=observations.Where(o=>!o.Rejected&&PathEquals(o.RelativePath,Video)).ToArray();
        Check(accepted.Length>0&&accepted.All(o=>o.StartMs>=6500&&o.StartMs<10500),"Accepted video observations do not match the known portrait shot starts.");
        var source=await VideoPreview();var precise=new List<FaceInterval>();var missing=new List<long>();
        foreach(var observation in accepted)
        {
            var frames=source.Moments.Where(m=>m.Kind==SegmentKind.VideoFrame&&m.StartMs==observation.StartMs).ToArray();
            var intervals=frames.Where(m=>m.StartMs.HasValue).Select(m=>new FaceInterval(observation.Id,m.StartMs!.Value,EffectiveEnd(m.StartMs.Value,m.EndMs))).Distinct().ToArray();
            if(intervals.Length==1)precise.Add(intervals[0]);else missing.Add(observation.Id);
        }
        bool preciseComplete=missing.Count==0;
        FaceInterval[] evidence=preciseComplete?precise.ToArray():[new(0,6500,10500)];
        var searches=new List<object>();
        foreach(string query in new[]{person.Name,alias!,person.Name+" portrait"})
        {
            var response=await Search(query,new(){Kind=FileKind.Video,ExactOnly=query==person.Name||query==alias});
            Check(response.Results.Count>0&&response.Results.All(r=>PathEquals(r.RelativePath,Video)),"Person-video search did not return only the matching fixture video.");
            var moments=response.Results.SelectMany(r=>r.Moments).ToArray();Check(moments.Length>0,"Person video result lacks matching moments.");
            foreach(var moment in moments)
                Check(moment.StartMs is long start&&evidence.Any(face=>Overlaps(start,EffectiveEnd(start,moment.EndMs),face.StartMs,face.EndMs)),"A returned person moment has no strict half-open overlap with the available face evidence: "+JsonSerializer.Serialize(moment,WireJson.Options));
            searches.Add(new{query,response=Summary(response),everyMomentOverlapsStoredFaceIntervals=preciseComplete,intervalProof=preciseComplete?"exact source VideoFrame intervals, including face-only half-second segments":"external aggregate portrait-shot range only; some exact observation segment intervals were unavailable",everyMomentOverlapsExternalPortraitShot=moments.All(m=>m.StartMs is long start&&Overlaps(start,EffectiveEnd(start,m.EndMs),6500,10500)),momentsOutsideExternalShot=moments.Where(m=>m.StartMs is long start&&!Overlaps(start,EffectiveEnd(start,m.EndMs),6500,10500))});
        }
        var negative=await Search("Test Person AB-0091",new(){ExactOnly=true});Check(negative.Results.Count==0,"Unrelated invoice content was attached to the named face.");
        return new{personId=person.Id,person.Name,alias,observations,preciseEvidenceComplete=preciseComplete,missingSourceIntervalObservationIds=missing,storedFaceIntervals=precise,externalPortraitShot=new{startMs=6500,endMs=10500},samplingLimitation="Stored one-second frame intervals can span a shot cut; strict stored overlap does not verify a person's action or role.",searches,negativeCount=negative.Results.Count};
    }

    private async Task<object?> CheckModules()
    {
        var diagnostics=await Client.GetDiagnosticsAsync(Token);using var process=Process.GetProcessById(diagnostics.ProcessId);process.Refresh();
        var modules=process.Modules.Cast<ProcessModule>().Select(m=>new ModuleRecord(m.ModuleName,m.FileName)).OrderBy(m=>m.Name).ToArray();
        Check(PathEquals(process.MainModule!.FileName,worker),"Actual reader executable differs from the selected final package.");
        foreach(string name in new[]{"coreclr.dll","findit_native.dll","libllama.dll","libusearch_c.dll"})
        {
            var module=modules.FirstOrDefault(m=>m.Name.Equals(name,StringComparison.OrdinalIgnoreCase));Check(module is not null,"Missing loaded module: "+name);
            Check(Within(workerFolder,module!.Path),"Required module loaded outside the selected final package folder: "+module.Path);
        }
        return new{workerFolder,mainModule=process.MainModule.FileName,diagnostics,modules};
    }

    private async Task<PreviewResult> VideoPreview()
    {
        if(videoPreview is not null)return videoPreview;
        var result=(await Search("bridge-dog-portrait-with-speech.mp4",new(){ExactOnly=true})).Results.Single(r=>PathEquals(r.RelativePath,Video));
        videoPreview=await Client.GetPreviewAsync(new(result.FileId),Token);return videoPreview;
    }
    private Task<SearchResponse> Search(string query,SearchFilters? filters=null)=>Client.SearchAsync(new(){Query=query,Filters=filters??new(),Limit=1000},Token);
    private static long EffectiveEnd(long start,long? end)=>end is long value&&value>start?value:checked(start+1);
    private static bool Overlaps(long aStart,long aEnd,long bStart,long bEnd)=>aStart<bEnd&&aEnd>bStart;
    private static object Summary(SearchResponse response)=>new{response.GenerationId,response.SemanticUsed,response.Notice,response.ElapsedMilliseconds,response.People,results=response.Results.Select(r=>new{r.RelativePath,r.Kind,r.Score,r.MatchCategory,r.Moments})};
    private static void CompareSnapshots(IReadOnlyList<OriginalHash> before,IReadOnlyList<OriginalHash> after,string label)
    {
        Check(before.Count==after.Count,$"The {label} file count changed during read-only validation.");
        foreach(var file in before)
        {
            var match=after.SingleOrDefault(f=>PathEquals(f.Path,file.Path));
            Check(match is not null&&match.Bytes==file.Bytes&&match.Sha256==file.Sha256&&match.ModifiedUtc==file.ModifiedUtc,$"Read-only validation changed {label} bytes or modification time: {file.Path}");
        }
    }
    private Task<List<OriginalHash>> HashArchive(CancellationToken cancellationToken)=>HashFiles(EnumerateFiles(Resolve("FindIt/archive")).Select(Relative).Order(StringComparer.OrdinalIgnoreCase),cancellationToken);
    private async Task<List<OriginalHash>> HashFiles(IEnumerable<string> paths,CancellationToken cancellationToken)
    {
        var result=new List<OriginalHash>();
        foreach(string relative in paths)
        {
            string path=Resolve(relative);RejectLinks(path);await using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read,65536,FileOptions.Asynchronous|FileOptions.SequentialScan);
            result.Add(new(relative,stream.Length,Convert.ToHexString(await SHA256.HashDataAsync(stream,cancellationToken)).ToLowerInvariant(),File.GetLastWriteTimeUtc(path)));
        }
        return result;
    }
    private IEnumerable<string> EnumerateFiles(string folder)
    {
        RejectLinks(folder);
        foreach(string path in Directory.EnumerateFileSystemEntries(folder))
        {RejectLinks(path);if(Directory.Exists(path)){foreach(string file in EnumerateFiles(path))yield return file;}else yield return path;}
    }
    private async Task<bool> Step(string name,Func<Task<object?>> action,bool allowAfterBudget=false)
    {
        var step=new StepRecord{Name=name,StartedUtc=DateTimeOffset.UtcNow};report.Steps.Add(step);var timer=Stopwatch.StartNew();Log("START "+name);
        try{if(!allowAfterBudget)Token.ThrowIfCancellationRequested();step.Details=await action();step.Outcome="passed";}
        catch(Exception error){failed++;step.Outcome=error is OperationCanceledException?"cancelled":"failed";step.Error=error.ToString();Log($"FAIL {name}: {error.Message}");}
        finally
        {
            step.ElapsedMilliseconds=timer.Elapsed.TotalMilliseconds;
            try{await SaveReport();}catch(Exception error)when(error is IOException or UnauthorizedAccessException){failed++;report.CleanupErrors.Add("Report checkpoint: "+error);}
        }
        Log($"{step.Outcome.ToUpperInvariant()} {name} ({step.ElapsedMilliseconds:N0} ms)");return step.Outcome=="passed";
    }
    private async Task SaveReport()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);string temporary=reportPath+"."+report.RunId+".tmp";
        await File.WriteAllTextAsync(temporary,JsonSerializer.Serialize(report,json),new UTF8Encoding(false));File.Move(temporary,reportPath,true);
    }
    private void RejectLinks(string path)
    {
        Check(Within(root,path)||PathEquals(root,path),"Path escapes selected drive.");string current=root;
        foreach(string part in Path.GetRelativePath(root,path).Split(Path.DirectorySeparatorChar))
        {if(part==".")continue;current=Path.Combine(current,part);if((File.Exists(current)||Directory.Exists(current))&&(File.GetAttributes(current)&FileAttributes.ReparsePoint)!=0)throw new IOException("Linked paths are not permitted: "+current);}
    }
    private string Resolve(string relative)
    {string path=Path.GetFullPath(Path.Combine(root,relative.Replace('/',Path.DirectorySeparatorChar)));Check(Within(root,path),"Path escapes selected drive.");return path;}
    private string Relative(string path)=>Path.GetRelativePath(root,path).Replace('\\','/');
    private static bool Within(string parent,string child)=>Path.GetFullPath(child).StartsWith(Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase);
    private static bool PathEquals(string a,string b)=>a.Replace('\\','/').Equals(b.Replace('\\','/'),StringComparison.OrdinalIgnoreCase);
    private static void Check(bool condition,string message){if(!condition)throw new ValidationException(message);}
    private static void Log(string message){Console.WriteLine(message);Console.Out.Flush();}
    private sealed record FaceInterval(long ObservationId,long StartMs,long EndMs);
}
