using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using FindIt.Client;
using FindIt.Shared;

namespace FindIt.DriveCheck;

/// <summary>Opt-in native integration run in an explicitly owned workspace artifact folder.</summary>
internal sealed class PeopleMediaValidation
{
    private const string Marker="FindIt owned native people/media validation v1";
    private const string Montage="Library/Videos/bridge-dog-portrait-with-speech.mp4";
    private const string LongShot="Library/Videos/long-portrait-shot.mp4";
    private const string HalfCut="Library/Videos/half-second-cut-long-shot.mp4";
    private const string VariableRate="Library/Videos/variable-rate-offset-face.mp4";
    private const string DelayedVideo="Library/Videos/delayed-video-early-audio.mp4";
    private const string DelayedAudio="Library/Videos/early-video-delayed-audio.mp4";
    private readonly string root,worker,reportPath,fixtures,workspace;
    private readonly List<object> steps=[];
    private readonly CancellationTokenSource budget=new(TimeSpan.FromMinutes(15));
    private readonly JsonSerializerOptions json=new(WireJson.Options){WriteIndented=true};
    private WorkerArchiveClient? client;
    private CancellationToken Token=>budget.Token;

    public PeopleMediaValidation(string root,string worker,string reportPath,string fixtures)
    {
        this.root=Path.GetFullPath(root);this.worker=Path.GetFullPath(worker);this.reportPath=Path.GetFullPath(reportPath);this.fixtures=Path.GetFullPath(fixtures);
        workspace=FindWorkspace();var artifacts=Path.Combine(workspace,"artifacts");
        if(!Within(artifacts,this.root)||!Within(artifacts,this.reportPath)||!Within(artifacts,this.fixtures)||Within(this.root,this.reportPath))
            throw new ArgumentException("Native media validation requires root, fixtures and separate report paths inside this workspace's artifacts folder. Removable-drive roots are never accepted.");
        if(!File.Exists(this.worker)||!Directory.Exists(this.fixtures))throw new FileNotFoundException("The selected worker or generated media fixture is unavailable.");
        RejectLinks(this.root);RejectLinks(this.fixtures);RejectLinks(this.worker);
    }

    public async Task<int> RunAsync()
    {
        string? prior=Environment.GetEnvironmentVariable("FINDIT_WORKER_PATH");var watch=Stopwatch.StartNew();string? failure=null;
        Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
        var marker=Path.Combine(root,".findit-people-media-validation");
        if(Directory.Exists(root)&&Directory.EnumerateFileSystemEntries(root).Any()&&(!File.Exists(marker)||await File.ReadAllTextAsync(marker,Token)!=Marker))
            throw new IOException("Refusing to change an existing folder without this harness's ownership marker.");
        Directory.CreateDirectory(root);await File.WriteAllTextAsync(marker,Marker,Token);
        await using var lease=new FileStream(Path.Combine(root,".findit-people-media.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
        try
        {
            Environment.SetEnvironmentVariable("FINDIT_WORKER_PATH",worker);
            await Step("copy-known-private-fixtures",async()=>
            {
                var files=new List<object>();
                foreach(var folder in new[]{"Photos","Videos","Audio","Documents"})
                foreach(var source in Directory.EnumerateFiles(Path.Combine(fixtures,folder),"*",SearchOption.AllDirectories))
                {
                    RejectLinks(source);string relative=Path.GetRelativePath(fixtures,source);var target=Path.Combine(root,"Library",relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);RejectLinks(target);
                    if(!File.Exists(target)){await using var input=File.OpenRead(source);await using var output=new FileStream(target,FileMode.CreateNew,FileAccess.Write,FileShare.Read);await input.CopyToAsync(output,Token);}
                    string expected=await Hash(source),actual=await Hash(target);Check(expected==actual,"An existing fixture differs: "+relative);files.Add(new{relative,sha256=actual});
                }
                Check(files.Count==13,"Use the original generated 13-file fixture folder.");return files;
            });
            await Step("generate-uncut-seventeen-second-video",async()=>
            {
                string path=Resolve(LongShot);
                if(!File.Exists(path))
                {
                    string ffmpeg=Path.Combine(workspace,"runtime","tools","ffmpeg.exe");Check(File.Exists(ffmpeg),"Bundled FFmpeg is unavailable.");
                    var info=new ProcessStartInfo(ffmpeg){UseShellExecute=false,CreateNoWindow=true,RedirectStandardError=true,RedirectStandardOutput=true};
                    foreach(var argument in new[]{"-nostdin","-hide_banner","-loglevel","error","-loop","1","-i",Resolve("Library/Photos/reference-portrait.jpg"),"-t","17","-r","2","-vf","scale=640:360:force_original_aspect_ratio=decrease,pad=640:360:(ow-iw)/2:(oh-ih)/2","-an","-c:v","mpeg4","-q:v","3","-n",path})info.ArgumentList.Add(argument);
                    using var process=Process.Start(info)??throw new IOException("FFmpeg did not start.");
                    using var registration=Token.Register(()=>{try{process.Kill(true);}catch(InvalidOperationException){}});
                    var error=process.StandardError.ReadToEndAsync(Token);var output=process.StandardOutput.ReadToEndAsync(Token);await process.WaitForExitAsync(Token);await output;
                    Check(process.ExitCode==0,"Fixture video encoding failed: "+await error);
                }
                return new{path,sha256=await Hash(path),seconds=17,groundTruth="One uncut still-portrait shot; no action ground truth."};
            });
            await Step("generate-half-second-cut-and-variable-rate-offset-video",async()=>
            {
                string image=Resolve("Library/Photos/reference-portrait.jpg"),half=Resolve(HalfCut),variable=Resolve(VariableRate);
                const string portrait="[1:v]scale=640:360:force_original_aspect_ratio=decrease,pad=640:360:(ow-iw)/2:(oh-ih)/2,setsar=1[p];[0:v][p]concat=n=2:v=1:a=0";
                if(!File.Exists(half))await Encode(["-f","lavfi","-i","color=c=blue:s=640x360:r=2:d=0.5","-loop","1","-framerate","2","-i",image,"-filter_complex",portrait+"[v]","-map","[v]","-t","17.5","-r","2","-an","-c:v","mpeg4","-q:v","3","-n",half]);
                if(!File.Exists(variable))await Encode(["-f","lavfi","-i","color=c=blue:s=640x360:r=10:d=0.7","-loop","1","-framerate","10","-i",image,"-filter_complex",portrait+",select='eq(n,0)+eq(n,3)+eq(n,7)+eq(n,11)+eq(n,16)+eq(n,24)+eq(n,27)+eq(n,31)',setpts=PTS+3/TB[v]","-map","[v]","-frames:v","8","-fps_mode","passthrough","-enc_time_base","1:1000","-an","-c:v","mpeg4","-q:v","3","-n",variable]);
                return new{halfSecondCut=new{path=half,sha256=await Hash(half),cutMs=500,endMs=17500},variableRate=new{path=variable,sha256=await Hash(variable),containerOffsetMs=3000,firstFacePresentationMs=700,groundTruth="Blue until700ms; portrait thereafter. Nonuniform source presentation times, no action ground truth."}};
            });
            await Step("generate-delayed-video-and-delayed-audio",async()=>
            {
                string delayedVideo=Resolve(DelayedVideo),delayedAudio=Resolve(DelayedAudio),speech=Resolve("Library/Audio/jfk-inaugural-speech.wav");
                if(!File.Exists(delayedVideo))await Encode(["-copyts","-i",Resolve(VariableRate),"-i",speech,"-map","0:v:0","-map","1:a:0","-c:v","copy","-c:a","aac","-t","7","-n",delayedVideo]);
                if(!File.Exists(delayedAudio))await Encode(["-i",Resolve(LongShot),"-itsoffset","3","-i",speech,"-map","0:v:0","-map","1:a:0","-c:v","copy","-c:a","aac","-t","17","-n",delayedAudio]);
                return new{delayedVideo=new{path=delayedVideo,sha256=await Hash(delayedVideo),firstVideoMs=3000,firstFaceMs=3700},delayedAudio=new{path=delayedAudio,sha256=await Hash(delayedAudio),firstAudioMs=2936,explanation="AAC adds one64ms priming frame before speech at3s; decoded PCM and transcript timestamps retain that offset."}};
            });
            client=(WorkerArchiveClient)await WorkerArchiveClient.CreateAsync(root,Token);
            await Step("actual-native-preparation",async()=>
            {
                var timer=Stopwatch.StartNew();var status=await client.PrepareAsync(new(){Folders=["Library"],OcrLanguages=["eng"]},new PreparationTrace(),Token);
                Check(status.FileCount==18&&status.Issues.Count==0,"Native preparation must finish 18 files with zero extraction issues.");Check(status.SemanticAvailable,"The actual query model is unavailable.");
                return new{elapsedMs=timer.Elapsed.TotalMilliseconds,status,diagnostics=await client.GetDiagnosticsAsync(Token)};
            });
            await Step("short-shot-and-full-window-timelines",async()=>
            {
                var montage=await Preview(Montage);var clips=montage.Moments.Where(m=>m.Kind==SegmentKind.VideoClip).OrderBy(m=>m.StartMs).ToArray();
                Check(clips.Length==4&&clips.All(m=>DriveValidation.WithinFixtureShot(m.StartMs!.Value,m.EndMs!.Value)),"The montage must have four joint clips that never cross its known cuts.");
                long[] boundaries=[0,3500,6500,10500,14000];
                for(int i=0;i<4;i++)Check(clips[i].StartMs==boundaries[i]&&clips[i].EndMs==boundaries[i+1],"Short clip does not cover its expected shot boundary.");
                Check(montage.Moments.Where(m=>m.Kind==SegmentKind.VideoFrame).All(m=>m.EndMs-m.StartMs==1),"A visual sample must be recorded as an instant.");
                var longPreview=await Preview(LongShot);var longClips=longPreview.Moments.Where(m=>m.Kind==SegmentKind.VideoClip).OrderBy(m=>m.StartMs).ToArray();
                var expected=new[]{(0L,8000L),(4000L,12000L),(8000L,16000L),(12000L,17000L)};
                Check(longClips.Length==expected.Length,"The uncut shot must retain overlapping full windows and a short tail.");
                for(int i=0;i<expected.Length;i++)Check(longClips[i].StartMs==expected[i].Item1&&longClips[i].EndMs==expected[i].Item2,"Uncut window cadence differs from eight seconds with four-second overlap.");
                var halfPreview=await Preview(HalfCut);var halfClips=halfPreview.Moments.Where(m=>m.Kind==SegmentKind.VideoClip).OrderBy(m=>m.StartMs).ToArray();
                var halfExpected=new[]{(500L,8500L),(4500L,12500L),(8500L,16500L),(12500L,17500L)};
                Check(halfClips.Length==halfExpected.Length,"Half-second scene start must retain all full windows and its tail.");
                for(int i=0;i<halfExpected.Length;i++)Check(halfClips[i].StartMs==halfExpected[i].Item1&&halfClips[i].EndMs==halfExpected[i].Item2,"Long-shot windows lost the half-second shot origin.");
                return new{montageClips=clips,longShotClips=longClips,halfSecondCutClips=halfClips,frameCount=montage.Moments.Count(m=>m.Kind==SegmentKind.VideoFrame)};
            });
            await Step("named-reference-and-real-face-evidence",async()=>
            {
                var existing=(await client.GetPeopleAsync(Token)).FirstOrDefault(p=>p.Name=="Test Person"&&p.Confirmed);
                var person=await client.SavePersonAsync(new(){PersonId=existing?.Id,Name="Test Person",Aliases=["Demo Person"],ReferencePaths=existing is null?[Resolve("Library/Photos/reference-portrait.jpg")]:[]},Token);
                var observations=(await client.GetPersonObservationsAsync(person.Id,Token)).Where(o=>o.RelativePath.Replace('\\','/')==Montage&&!o.Rejected).ToArray();
                Check(observations.Length>0&&observations.All(o=>o.StartMs>=6500&&o.StartMs<10500),"Native identity observations fall outside the known portrait shot.");
                var source=await Preview(Montage);var portraitClip=source.Moments.Single(m=>m.Kind==SegmentKind.VideoClip&&m.StartMs==6500);
                var preview=await client.GetPreviewAsync(new(source.FileId,portraitClip.SegmentId,observations[0].Id,[person.Id]),Token);
                Check(preview.StartMs==observations[0].StartMs&&preview.PersonEvidence.Single().ObservationId==observations[0].Id,"Native person preview did not seek to the requested accepted face sample.");
                Check(preview.PersonEvidence[0].Thumbnail is not null&&preview.PersonEvidence[0].Portrait is not null,"Native context frame or aligned face crop is missing.");
                var query=await client.SearchAsync(new(){Query="Test Person driving",Filters=new(){Kind=FileKind.Video}},Token);
                Check(query.SemanticUsed&&query.Results.Count>0,"Native person-constrained semantic search did not execute.");
                Check(query.Results.All(r=>r.MatchCategory==MatchCategory.PossiblePersonAction&&r.Moments.All(m=>m.PersonEvidence.Count>0)),"Candidate action results must retain inspectable face evidence.");
                return new{person,observations,preview,query,scope="Real model integration and evidence alignment. The still montage does not establish driving or speaker accuracy."};
            });
            await Step("source-presentation-time-and-offset-face-seek",async()=>
            {
                var person=(await client.GetPeopleAsync(Token)).Single(p=>p.Name=="Test Person"&&p.Confirmed);
                var source=await Preview(VariableRate);var frames=source.Moments.Where(m=>m.Kind==SegmentKind.VideoFrame).Select(m=>m.StartMs!.Value).Order().ToArray();
                long[] expected=[0,700,1100,1600,2400,2700,3100];
                Check(frames.SequenceEqual(expected),"Decoder retimed variable-rate frames instead of keeping their source presentation timestamps: "+JsonSerializer.Serialize(frames));
                var observations=(await client.GetPersonObservationsAsync(person.Id,Token)).Where(o=>o.RelativePath.Replace('\\','/')==VariableRate&&!o.Rejected).OrderBy(o=>o.StartMs).ToArray();
                Check(observations.Length>0&&observations[0].StartMs==700,"The first named face must be at its actual700ms presentation time, not the old synthetic500ms grid.");
                var first=source.Moments.Single(m=>m.Kind==SegmentKind.VideoFrame&&m.StartMs==700);
                var preview=await client.GetPreviewAsync(new(source.FileId,first.SegmentId,observations[0].Id,[person.Id]),Token);
                Check(preview.StartMs==700&&preview.PersonEvidence[0].Thumbnail is not null,"Face evidence preview did not keep the normalized source presentation time.");
                return new{sourcePresentationTimes=frames,containerOffsetMs=3000,firstAcceptedFace=observations[0],preview,scope="Decoded presentation timeline normalized to the common container origin; not camera capture time."};
            });
            await Step("shared-playback-origin-for-delayed-video-audio-and-speech",async()=>
            {
                var person=(await client.GetPeopleAsync(Token)).Single(p=>p.Name=="Test Person"&&p.Confirmed);
                var source=await Preview(DelayedVideo);var frames=source.Moments.Where(m=>m.Kind==SegmentKind.VideoFrame).Select(m=>m.StartMs!.Value).Order().ToArray();
                long[] expected=[3000,3700,4100,4600,5400,5700,6100];Check(frames.SequenceEqual(expected),"Delayed video lost its offset relative to early audio: "+JsonSerializer.Serialize(frames));
                var observations=(await client.GetPersonObservationsAsync(person.Id,Token)).Where(o=>o.RelativePath.Replace('\\','/')==DelayedVideo&&!o.Rejected).OrderBy(o=>o.StartMs).ToArray();
                Check(observations.Length>0&&observations[0].StartMs==3700,"A face beginning3.7s into original playback must not seek to700ms.");
                var frame=source.Moments.Single(m=>m.Kind==SegmentKind.VideoFrame&&m.StartMs==3700);
                var preview=await client.GetPreviewAsync(new(source.FileId,frame.SegmentId,observations[0].Id,[person.Id]),Token);Check(preview.StartMs==3700,"Delayed video face seek did not preserve the shared playback origin.");
                var audioSource=await Preview(DelayedAudio);var audio=audioSource.Moments.Where(m=>m.Kind==SegmentKind.Audio).OrderBy(m=>m.StartMs).First();
                Check(audio.StartMs==2936,"Delayed audio windows lost the AAC stream's2.936s decoded start.");
                Check(audioSource.Moments.Where(m=>m.Kind==SegmentKind.Transcript).All(m=>m.StartMs>=2936),"Transcript passages were retimed before their delayed audio.");
                var speech=await client.SearchAsync(new(){Query="country",Filters=new(){ExactOnly=true,Kind=FileKind.Video}},Token);
                var country=speech.Results.SingleOrDefault(r=>r.RelativePath.Replace('\\','/')==DelayedAudio);Check(country is not null&&country.Moments.Any(m=>m.Kind==SegmentKind.Transcript&&m.StartMs>=2936),"Timestamped country search omitted the delayed-audio transcript.");
                return new{sourcePresentationTimes=frames,firstAcceptedFace=observations[0],preview,delayedAudioWindow=audio,delayedSpeech=country,scope="Shared original playback timeline; sampled faces and overlapping speech do not verify the speaker."};
            });
        }
        catch(Exception error){failure=error.ToString();Console.Error.WriteLine(error);}
        finally
        {
            if(client is not null)await client.DisposeAsync();Environment.SetEnvironmentVariable("FINDIT_WORKER_PATH",prior);
            await File.WriteAllTextAsync(reportPath,JsonSerializer.Serialize(new{passed=failure is null,root,worker,elapsedMs=watch.Elapsed.TotalMilliseconds,steps,failure,retained=true,scope="Workspace native integration fixture; no external-drive writes, hardware latency certification, or actor/action verification."},json));
            budget.Dispose();
        }
        return failure is null?0:1;
    }

    private async Task Step(string name,Func<Task<object>> work)
    {
        Console.WriteLine("START "+name);Console.Out.Flush();var timer=Stopwatch.StartNew();
        try{var result=await work();steps.Add(new{name,passed=true,elapsedMs=timer.Elapsed.TotalMilliseconds,result});Console.WriteLine("PASS "+name);}
        catch(Exception error){steps.Add(new{name,passed=false,elapsedMs=timer.Elapsed.TotalMilliseconds,error=error.ToString()});throw;}
        finally{Console.Out.Flush();}
    }
    private async Task<PreviewResult> Preview(string relative)
    {
        var response=await client!.SearchAsync(new(){Query=Path.GetFileName(relative),Filters=new(){ExactOnly=true}},Token);
        var file=response.Results.Single(r=>r.RelativePath.Replace('\\','/')==relative);return await client.GetPreviewAsync(new(file.FileId),Token);
    }
    private async Task<string> Hash(string path){await using var stream=File.OpenRead(path);return Convert.ToHexString(await SHA256.HashDataAsync(stream,Token));}
    private async Task Encode(IReadOnlyList<string> arguments)
    {
        var info=new ProcessStartInfo(Path.Combine(workspace,"runtime","tools","ffmpeg.exe")){UseShellExecute=false,CreateNoWindow=true,RedirectStandardError=true,RedirectStandardOutput=true};
        foreach(var argument in new[]{"-nostdin","-hide_banner","-loglevel","error"}.Concat(arguments))info.ArgumentList.Add(argument);
        using var process=Process.Start(info)??throw new IOException("FFmpeg did not start.");using var registration=Token.Register(()=>{try{process.Kill(true);}catch(InvalidOperationException){}});
        var error=process.StandardError.ReadToEndAsync(Token);var output=process.StandardOutput.ReadToEndAsync(Token);await process.WaitForExitAsync(Token);await output;Check(process.ExitCode==0,"Fixture encoding failed: "+await error);
    }
    private string Resolve(string path)=>Path.Combine(root,path.Replace('/',Path.DirectorySeparatorChar));
    private static void Check(bool condition,string message){if(!condition)throw new InvalidDataException(message);}
    private static bool Within(string parent,string path)=>path.StartsWith(Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase);
    private static void RejectLinks(string path)
    {
        for(var current=Path.GetFullPath(path);current is not null;current=Path.GetDirectoryName(current))
            if((File.Exists(current)||Directory.Exists(current))&&(File.GetAttributes(current)&FileAttributes.ReparsePoint)!=0)throw new IOException("Native validation paths cannot contain symbolic links or junctions.");
    }
    private static string FindWorkspace()
    {
        foreach(var start in new[]{Environment.CurrentDirectory,AppContext.BaseDirectory})
            for(var directory=new DirectoryInfo(start);directory is not null;directory=directory.Parent)
                if(File.Exists(Path.Combine(directory.FullName,"FindIt.slnx")))return directory.FullName;
        throw new DirectoryNotFoundException("Run from the FindIt workspace.");
    }
    private sealed class PreparationTrace:IProgress<PreparationProgress>
    {
        private string previous="";
        public void Report(PreparationProgress progress){string key=progress.Stage+":"+progress.CurrentPath;if(key==previous)return;previous=key;Console.WriteLine($"{progress.Stage} {progress.Completed}/{progress.Discovered} {progress.CurrentPath}");Console.Out.Flush();}
    }
}
