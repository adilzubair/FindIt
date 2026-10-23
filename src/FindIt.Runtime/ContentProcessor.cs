using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using FindIt.Shared;
using OpenCvSharp;

namespace FindIt.Runtime;

public sealed partial class ContentProcessor : IContentProcessor
{
    private readonly RuntimePaths paths;
    private readonly NativeEmbeddingProvider media;
    private readonly NativeEmbeddingProvider text;
    private readonly FaceProcessor faces;
    private readonly SemaphoreSlim gate=new(1,1);
    public string FaceFingerprint => "yunet-8f2383e4-sface-0ba9fbfa-align112-l2-v1";
    public string ExtractionFingerprint => "thorough-playback-pts-time-windows-conservative-eof-face-instants-v7";
    public ContentProcessor(RuntimePaths paths){this.paths=paths;media=new(paths,true);text=new(paths);faces=new(paths);}
    public IReadOnlyList<RuntimeCapability> Capabilities=>[
        new("semantic","Semantic search",text.Available,text.Available?"Gemma 2 Q8 text encoder, 512 dimensions.":"Embedding model or native runtime missing; run Fetch-Dependencies.ps1."),
        new("media","Photo and video meaning",media.Available&&paths.Model(NativeEmbeddingProvider.MediaModel)is not null,"Gemma media encoder loads only during preparation."),
        new("video","Video extraction",paths.Tool("ffmpeg.exe")is not null,"One scene frame/second plus detected cuts, two face frames/second; no frame cap."),
        new("faces","Named people",faces.Available,"YuNet and SFace; uncertain identities need confirmation."),
        new("pdf","PDF pages",paths.Native("pdfium.dll")is not null,"PDFium text, rendered pages and OCR fallback."),
        new("ocr","OCR",paths.Tessdata is not null,"Tesseract English/Arabic; selected installed languages only."),
        new("speech","Transcription",paths.Tool("whisper-cli.exe")is not null&&paths.Model("ggml-small.bin")is not null,"Local multilingual Whisper small, timestamped text."),
        new("gpu","Graphics acceleration",media.DeviceDescription.StartsWith("Vulkan selected",StringComparison.Ordinal),media.DeviceDescription),
        new("ann","Fast vector search",NativeVectorIndex.Available,"USearch HNSW with file-mapped immutable generations.")];
    public async IAsyncEnumerable<ExtractedSegment> ExtractAsync(string absolutePath,FileKind kind,PreparationRequest options,[EnumeratorCancellation]CancellationToken cancellationToken=default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var scratch=Path.Combine(paths.RootPath,"FindIt","archive","work","runtime",Guid.NewGuid().ToString("N"));
        FileStream? scratchLease=null;
        try
        {
            scratchLease=CreateScratch(scratch);
            if(kind is FileKind.Image or FileKind.Video || (kind==FileKind.Document&&Path.GetExtension(absolutePath).Equals(".pdf",StringComparison.OrdinalIgnoreCase)))NativeMethods.EnsureVisualDependencies(paths);
            switch(kind)
            {
                case FileKind.Image:
                    using(var image=await ReadImageAsync(absolutePath,scratch,cancellationToken))
                    {
                        var jpeg=Jpeg(image,1024);var file=Path.Combine(scratch,"image.jpg");await File.WriteAllBytesAsync(file,jpeg,cancellationToken);
                        var words=ReadOcr(jpeg,options.OcrLanguages);
                        yield return new(){Kind=SegmentKind.Image,Text=words,ThumbnailJpeg=Jpeg(image,384),Embedding=await media.EmbedMediaAsync(file,true,cancellationToken),Faces=faces.Detect(image)};
                    }
                    break;
                case FileKind.Video:
                    await foreach(var segment in VideoAsync(absolutePath,scratch,cancellationToken))yield return segment;
                    if(await HasAudioAsync(absolutePath,cancellationToken))await foreach(var segment in AudioAsync(absolutePath,scratch,cancellationToken))yield return segment;
                    break;
                case FileKind.Audio:
                    await foreach(var segment in AudioAsync(absolutePath,scratch,cancellationToken))yield return segment;
                    break;
                case FileKind.Document:
                    await foreach(var segment in DocumentsAsync(absolutePath,scratch,options,cancellationToken))yield return segment;
                    break;
            }
        }
        finally {scratchLease?.Dispose();try{Directory.Delete(scratch,true);}catch(IOException){}catch(UnauthorizedAccessException){}gate.Release();}
    }
    public async Task<IReadOnlyList<DetectedFace>> DetectReferenceFacesAsync(string absolutePath,CancellationToken cancellationToken=default)
    {
        await gate.WaitAsync(cancellationToken);var scratch=Path.Combine(paths.RootPath,"FindIt","archive","work","runtime",Guid.NewGuid().ToString("N"));
        FileStream? scratchLease=null;
        try{scratchLease=CreateScratch(scratch);NativeMethods.EnsureVisualDependencies(paths);using var image=await ReadImageAsync(absolutePath,scratch,cancellationToken);return faces.Detect(image);}finally{scratchLease?.Dispose();try{if(Directory.Exists(scratch))Directory.Delete(scratch,true);}catch(IOException){}catch(UnauthorizedAccessException){}gate.Release();}
    }
    private FileStream CreateScratch(string scratch)
    {
        var root=Path.GetFullPath(paths.RootPath).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
        var absolute=Path.GetFullPath(scratch);
        if(!absolute.StartsWith(root,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Scratch path escapes the selected drive.");
        var current=Path.GetFullPath(paths.RootPath);
        foreach(var component in Path.GetRelativePath(current,absolute).Split(Path.DirectorySeparatorChar))
        {
            current=Path.Combine(current,component);
            if(Directory.Exists(current)&&(File.GetAttributes(current)&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException("Archive scratch directories cannot be junctions or symbolic links.");
            Directory.CreateDirectory(current);
        }
        ReclaimAbandonedScratch(Path.GetDirectoryName(absolute)!);
        // The OS releases this exclusive lease if a worker is killed or the drive is disconnected.
        return new FileStream(Path.Combine(absolute,"active.lock"),FileMode.CreateNew,FileAccess.ReadWrite,FileShare.None);
    }
    private void ReclaimAbandonedScratch(string parent)
    {
        foreach(var candidate in Directory.EnumerateDirectories(parent).Take(128))
        {
            try
            {
                if(!Guid.TryParseExact(Path.GetFileName(candidate),"N",out _)||(File.GetAttributes(candidate)&FileAttributes.ReparsePoint)!=0)continue;
                var marker=Path.Combine(candidate,"active.lock");if(!File.Exists(marker))continue;
                if((File.GetAttributes(marker)&FileAttributes.ReparsePoint)!=0)continue;
                // Active processors deny this open. Never reclaim unmarked folders from other versions.
                using(var abandoned=new FileStream(marker,FileMode.Open,FileAccess.ReadWrite,FileShare.None)){}
                var resolved=Path.GetFullPath(candidate);
                if(!resolved.StartsWith(Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))continue;
                Directory.Delete(resolved,true);
            }
            catch(IOException){}catch(UnauthorizedAccessException){}
        }
    }
    private async Task<Mat> ReadImageAsync(string path,string scratch,CancellationToken token)
    {
        // imdecode avoids ANSI path handling inside native decoders.
        var image=Cv2.ImDecode(await File.ReadAllBytesAsync(path,token),ImreadModes.Color);
        if(!image.Empty())return image;
        image.Dispose();var ffmpeg=paths.Tool("ffmpeg.exe")??throw new InvalidDataException("Unsupported image; FFmpeg fallback is missing.");
        var output=Path.Combine(scratch,"decoded.png");await ProcessRunner.CaptureAsync(ffmpeg,["-nostdin","-hide_banner","-loglevel","error","-i",path,"-frames:v","1","-y",output],token);
        image=Cv2.ImDecode(await File.ReadAllBytesAsync(output,token),ImreadModes.Color);
        if(image.Empty()){image.Dispose();throw new InvalidDataException("Unsupported or corrupt image.");}return image;
    }
    private static byte[] Jpeg(Mat image,int maxSide)
    {
        using var thumb=new Mat();var scale=Math.Min(1d,(double)maxSide/Math.Max(image.Width,image.Height));Cv2.Resize(image,thumb,new Size(Math.Max(1,(int)(image.Width*scale)),Math.Max(1,(int)(image.Height*scale))));
        Cv2.ImEncode(".jpg",thumb,out var bytes,[new ImageEncodingParam(ImwriteFlags.JpegQuality,82)]);return bytes;
    }
    private async IAsyncEnumerable<ExtractedSegment> VideoAsync(string path,string scratch,[EnumeratorCancellation]CancellationToken token)
    {
        var ffmpeg=paths.Tool("ffmpeg.exe")??throw new FileNotFoundException("FFmpeg is required to prepare videos.");
        const int width=640,height=360;
        var timeline=await MediaTimeline.ProbeAsync(paths.Tool("ffprobe.exe"),path,"v:0",token);
        long visualIndex=0,nextVisual=0,lastFrameTime=0;
        var windows=new VideoWindowScheduler();
        var cuts=new SceneChangeDetector();var cutPixels=new byte[32*18*3];using var cutThumbnail=new Mat();
        await foreach(var decoded in VideoFrameReader.ReadAsync(ffmpeg,path,width,height,token))
        {
                var time=lastFrameTime=decoded.StartMs;
                using var frame=Mat.FromPixelData(height,width,MatType.CV_8UC3,decoded.Pixels);var observations=faces.Detect(frame);
                Cv2.Resize(frame,cutThumbnail,new Size(32,18),0,0,InterpolationFlags.Area);
                System.Runtime.InteropServices.Marshal.Copy(cutThumbnail.Data,cutPixels,0,cutPixels.Length);
                bool sceneCut=cuts.Observe(cutPixels);
                // Finalize before adding this source frame, so half-open windows never
                // encode a later shot or a sparse VFR sample outside their declared time.
                foreach(var window in windows.Advance(time,sceneCut))
                    yield return new(){Kind=SegmentKind.VideoClip,StartMs=window.StartMs,EndMs=window.EndMs,Embedding=await media.EmbedFramesAsync(window.Frames.Select(f=>f.Path).ToArray(),token)};
                if(sceneCut)windows.Reset(time);
                if(time>=nextVisual)
                {
                    nextVisual=(time/1000+1)*1000;
                    var file=Path.Combine(scratch,$"frame-{visualIndex++%8}.jpg");await File.WriteAllBytesAsync(file,Jpeg(frame,640),token);var vector=await media.EmbedMediaAsync(file,true,token);
                    // A sampled image establishes presence at this instant; it does not
                    // establish a person's presence for the following second or across a cut.
                    yield return new(){Kind=SegmentKind.VideoFrame,StartMs=time,EndMs=time+1,ThumbnailJpeg=Jpeg(frame,384),Embedding=vector,Faces=observations};
                    if(vector is not null)
                    {
                        windows.Add(file,time);
                    }
                }
                else if(sceneCut)
                {
                    // Supplemental scene samples do not replace the regular temporal queue.
                    var file=Path.Combine(scratch,"scene-cut.jpg");await File.WriteAllBytesAsync(file,Jpeg(frame,640),token);
                    yield return new(){Kind=SegmentKind.VideoFrame,StartMs=time,EndMs=time+1,ThumbnailJpeg=Jpeg(frame,384),Embedding=await media.EmbedMediaAsync(file,true,token),Faces=observations};
                }
                else if(observations.Count>0)yield return new(){Kind=SegmentKind.VideoFrame,StartMs=time,EndMs=time+1,ThumbnailJpeg=Jpeg(frame,384),Faces=observations};
        }
            long end=timeline.ResolveEnd(lastFrameTime);
            foreach(var window in windows.Advance(end,true))
                yield return new(){Kind=SegmentKind.VideoClip,StartMs=window.StartMs,EndMs=window.EndMs,Embedding=await media.EmbedFramesAsync(window.Frames.Select(f=>f.Path).ToArray(),token)};
    }
    private async Task<bool> HasAudioAsync(string path,CancellationToken token)
    {
        var ffprobe=paths.Tool("ffprobe.exe");if(ffprobe is null)return true;
        var result=await ProcessRunner.CaptureAsync(ffprobe,["-v","error","-select_streams","a:0","-show_entries","stream=index","-of","csv=p=0",path],token);return !string.IsNullOrWhiteSpace(result);
    }
    private async IAsyncEnumerable<ExtractedSegment> AudioAsync(string path,string scratch,[EnumeratorCancellation]CancellationToken token)
    {
        var ffmpeg=paths.Tool("ffmpeg.exe")??throw new FileNotFoundException("FFmpeg is required to prepare audio.");
        var timeline=await MediaTimeline.ProbeAsync(paths.Tool("ffprobe.exe"),path,"a:0",token);
        const int rate=16000,window=30*rate,hop=25*rate;using var process=ProcessRunner.Start(ffmpeg,["-nostdin","-hide_banner","-loglevel","error","-i",path,"-vn","-ac","1","-ar","16000","-f","s16le","pipe:1"],true);
        using var registration=token.Register(()=>ProcessRunner.Kill(process));var diagnostic=ProcessRunner.ReadTailAsync(process.StandardError,token);var samples=new byte[window*2];var occupied=0;long offset=0;
        // Whisper loads its model per run, so speech is transcribed in long parts rather than per
        // embedding window. Each decoded sample is written to the part exactly once, so the
        // embedding windows' overlap is never transcribed twice.
        bool transcribe=paths.Tool("whisper-cli.exe") is not null&&paths.Model("ggml-small.bin") is not null;
        var speechPath=Path.Combine(scratch,"speech.wav");FileStream? speech=null;long speechBytes=0,speechStart=0;
        try
        {
            while(true)
            {
                var read=await process.StandardOutput.BaseStream.ReadAsync(samples.AsMemory(occupied),token);occupied+=read;
                for(var pending=samples.AsMemory(occupied-read,transcribe?read:0);!pending.IsEmpty;)
                {
                    speech??=BeginWave(speechPath,rate);
                    var take=(int)Math.Min(pending.Length,SpeechPartBytes-speechBytes);
                    await speech.WriteAsync(pending[..take],token);speechBytes+=take;pending=pending[take..];
                    if(speechBytes<SpeechPartBytes)continue;
                    foreach(var segment in await TranscribePartAsync(speech,speechPath,scratch,timeline.StartMs+speechStart*1000/(rate*2),speechBytes,rate,token))yield return segment;
                    speech=null;speechStart+=speechBytes;speechBytes=0;
                }
                if(occupied<samples.Length&&read>0)continue;
                if(occupied>rate*2 && (offset==0||occupied>(window-hop)*2))
                {
                    var wave=Path.Combine(scratch,"audio.wav");WriteWave(wave,samples,occupied,rate);var start=timeline.StartMs+offset*1000/rate;var end=start+occupied*1000/(rate*2);
                    yield return new(){Kind=SegmentKind.Audio,StartMs=start,EndMs=end,Embedding=await media.EmbedMediaAsync(wave,false,token)};
                }
                if(read==0)break;
                Buffer.BlockCopy(samples,hop*2,samples,0,(window-hop)*2);occupied=(window-hop)*2;offset+=hop;
            }
            await process.WaitForExitAsync(token);if(process.ExitCode!=0)throw new InvalidDataException("Audio extraction failed: "+await diagnostic);
            if(speech is not null)
            {
                foreach(var segment in await TranscribePartAsync(speech,speechPath,scratch,timeline.StartMs+speechStart*1000/(rate*2),speechBytes,rate,token))yield return segment;
                speech=null;
            }
        }
        finally {ProcessRunner.Kill(process);speech?.Dispose();}
    }
    // Two hours of 16 kHz mono PCM per Whisper run keeps each WAV far below its 4 GiB limit.
    private const long SpeechPartBytes=2L*60*60*16000*2;
    private async Task<IReadOnlyList<ExtractedSegment>> TranscribePartAsync(FileStream speech,string wave,string scratch,long startMs,long bytes,int rate,CancellationToken token)
    {
        try{FinishWave(speech,bytes,rate);}finally{await speech.DisposeAsync();}
        // Parts of a second or less carry no transcribable speech, as before.
        if(bytes<=rate*2)return [];
        var segments=new List<ExtractedSegment>();
        foreach(var passage in await TranscribeAsync(wave,scratch,startMs,bytes*1000/(rate*2),token))
            segments.Add(new(){Kind=SegmentKind.Transcript,StartMs=passage.StartMs,EndMs=passage.EndMs,Text=passage.Text,Embedding=await text.EmbedTextAsync(passage.Text,false,token)});
        return segments;
    }
    private async Task<IReadOnlyList<SpeechPassage>> TranscribeAsync(string wave,string scratch,long startMs,long durationMs,CancellationToken token)
    {
        var whisper=paths.Tool("whisper-cli.exe");var model=paths.Model("ggml-small.bin");if(whisper is null||model is null)return [];
        await ModelIntegrity.VerifyAsync(model,"ggml-small.bin",token);
        var output=Path.Combine(scratch,"speech");
        var arguments=new List<string>{"-m",model,"-f",wave,"-l","auto","-oj","-of",output,"-t",Math.Clamp(Environment.ProcessorCount/2,1,8).ToString(CultureInfo.InvariantCulture),"-np"};
        if(paths.Model("ggml-silero-v6.2.0.bin") is { } vad)
        {
            await ModelIntegrity.VerifyAsync(vad,"ggml-silero-v6.2.0.bin",token);
            arguments.AddRange(["--vad","--vad-model",vad]);
        }
        await ProcessRunner.CaptureAsync(whisper,arguments,token);
        using var json=JsonDocument.Parse(await File.ReadAllTextAsync(output+".json",token));
        return SpeechTimestamps.Parse(json.RootElement,startMs,durationMs);
    }
    private static void WriteWave(string path,byte[] samples,int count,int rate)
    {
        using var stream=File.Create(path);WriteWaveHeader(stream,(uint)count,rate);stream.Write(samples,0,count);
    }
    private static FileStream BeginWave(string path,int rate)
    {
        var stream=new FileStream(path,FileMode.Create,FileAccess.Write,FileShare.None,1024*1024);
        try{WriteWaveHeader(stream,0,rate);return stream;}catch{stream.Dispose();throw;}
    }
    private static void FinishWave(FileStream stream,long count,int rate){stream.Position=0;WriteWaveHeader(stream,checked((uint)count),rate);}
    private static void WriteWaveHeader(Stream stream,uint count,int rate)
    {
        using var writer=new BinaryWriter(stream,System.Text.Encoding.UTF8,leaveOpen:true);writer.Write("RIFF"u8);writer.Write(checked(36+count));writer.Write("WAVEfmt "u8);writer.Write(16);writer.Write((short)1);writer.Write((short)1);writer.Write(rate);writer.Write(rate*2);writer.Write((short)2);writer.Write((short)16);writer.Write("data"u8);writer.Write(count);
    }
    public async ValueTask DisposeAsync(){await media.DisposeAsync();await text.DisposeAsync();faces.Dispose();ocr?.Dispose();}
}
