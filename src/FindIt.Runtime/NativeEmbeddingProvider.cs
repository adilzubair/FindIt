using System.Runtime.InteropServices;
using System.Diagnostics;
using FindIt.Shared;

namespace FindIt.Runtime;

public sealed class NativeEmbeddingProvider : IEmbeddingProvider
{
    public const int Dimensions = 512;
    public const string TextModel = "embeddinggemma-2-Q8_0.gguf";
    public const string MediaModel = "mmproj-embeddinggemma-2-Q8_0.gguf";
    private readonly RuntimePaths paths;
    private readonly bool media;
    private readonly SemaphoreSlim gate = new(1,1);
    private IntPtr handle;
    private bool disposed;
    private bool probed;
    public string DeviceDescription { get; private set; } = "CPU; graphics acceleration is measured when preparation starts.";
    public bool Available => paths.Model(TextModel) is not null && NativeMethods.Present(paths,"findit_native");
    public string Fingerprint => "gemma2-bfcd2987-q8_0-llama4d756bc-mean-boseos-searchprompt-prefix512-l2-video8joint-v1";
    public NativeEmbeddingProvider(RuntimePaths paths, bool media=false) { this.paths=paths; this.media=media; }
    public Task<float[]?> EmbedTextAsync(string text,bool query,CancellationToken cancellationToken=default) => RunAsync((h,r,c) => NativeMethods.fi_embed_text(h,text,query?1:0,r,r.Length,c,out var e) is var n ? (n,e) : default,cancellationToken);
    internal Task<float[]?> EmbedMediaAsync(string path,bool image,CancellationToken cancellationToken) => RunAsync((h,r,c) => NativeMethods.fi_embed_media(h,path,image?1:0,r,r.Length,c,out var e) is var n ? (n,e) : default,cancellationToken);
    // Cleared when the loaded native runtime predates in-memory input (EntryPointNotFoundException).
    internal bool InMemoryInput { get; set; } = true;
    internal Task<float[]?> EmbedMediaAsync(byte[] data,bool image,CancellationToken cancellationToken) => RunAsync((h,r,c) => NativeMethods.fi_embed_media_buffer(h,data,(nuint)data.Length,image?1:0,r,r.Length,c,out var e) is var n ? (n,e) : default,cancellationToken);
    internal Task<float[]?> EmbedFramesAsync(IReadOnlyList<byte[]> frames,CancellationToken cancellationToken) => RunAsync((h,r,c) =>
    {
        var handles=frames.Select(f=>GCHandle.Alloc(f,GCHandleType.Pinned)).ToArray();
        try{return NativeMethods.fi_embed_frame_buffers(h,handles.Select(p=>p.AddrOfPinnedObject()).ToArray(),frames.Select(f=>(nuint)f.Length).ToArray(),frames.Count,r,r.Length,c,out var e) is var n ? (n,e) : default;}
        finally{foreach(var handle in handles)handle.Free();}
    },cancellationToken);
    internal Task<float[]?> EmbedFramesAsync(IReadOnlyList<string> paths,CancellationToken cancellationToken) => RunAsync((h,r,c) => NativeMethods.fi_embed_frames(h,string.Join('\n',paths),r,r.Length,c,out var e) is var n ? (n,e) : default,cancellationToken);
    private async Task<float[]?> RunAsync(Func<IntPtr,float[],IntPtr,(int Count,IntPtr Error)> action,CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(disposed,this);
        if(!Available) return null;
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            token.ThrowIfCancellationRequested();
            if(handle == IntPtr.Zero)
            {
                if(media && paths.Model(MediaModel) is null) throw new FileNotFoundException("The media encoder is missing. Run Fetch-Dependencies.ps1.");
                await ModelIntegrity.VerifyAsync(paths.Model(TextModel)!,TextModel,token);
                if(media)await ModelIntegrity.VerifyAsync(paths.Model(MediaModel)!,MediaModel,token);
                handle=NativeMethods.fi_create(paths.Model(TextModel)!,media?paths.Model(MediaModel):null,Path.GetDirectoryName(paths.Native("findit_native.dll"))!,Math.Clamp(Environment.ProcessorCount/2,1,8),0,out var error);
                NativeMethods.Check(error);
                if(handle==IntPtr.Zero) throw new InvalidOperationException("The embedding runtime could not load the model.");
            }
            var flag=Marshal.AllocHGlobal(sizeof(int));
            try
            {
                Marshal.WriteInt32(flag,0);
                using var registration=token.Register(() => Marshal.WriteInt32(flag,1));
                var raw = new float[768];
                var outcome=await Task.Run(() => action(handle,raw,flag),CancellationToken.None).ConfigureAwait(false);
                if(token.IsCancellationRequested){if(outcome.Error!=IntPtr.Zero)NativeMethods.fi_free_error(outcome.Error);token.ThrowIfCancellationRequested();}
                NativeMethods.Check(outcome.Error);
                if(media&&!probed&&paths.Native("gpu/ggml-vulkan.dll") is not null)
                {
                    probed=true;
                    await ProbeGpuAsync(action,flag,token).ConfigureAwait(false);
                }
                if(outcome.Count != raw.Length) throw new InvalidOperationException($"Expected 768 model dimensions, received {outcome.Count}.");
                var result=raw[..Dimensions];
                var norm=Math.Sqrt(result.Sum(x=>(double)x*x));
                if(!double.IsFinite(norm)||norm<=0) throw new InvalidOperationException("The model returned an invalid vector.");
                for(var i=0;i<result.Length;i++) result[i]/=(float)norm;
                return result;
            }
            finally { Marshal.FreeHGlobal(flag); }
        }
        finally { gate.Release(); }
    }
    private async Task ProbeGpuAsync(Func<IntPtr,float[],IntPtr,(int Count,IntPtr Error)> action,IntPtr flag,CancellationToken token)
    {
        IntPtr candidate=IntPtr.Zero;
        try
        {
            NativeMethods.EnsureVisualDependencies(paths);
            var backends=Path.GetDirectoryName(paths.Native("findit_native.dll"))!;
            if(NativeMethods.fi_gpu_available(backends)==0){DeviceDescription="CPU; no compatible Vulkan graphics device detected.";return;}
            candidate=NativeMethods.fi_create(paths.Model(TextModel)!,paths.Model(MediaModel),backends,Math.Clamp(Environment.ProcessorCount/2,1,8),1,out var createError);NativeMethods.Check(createError);
            if(candidate==IntPtr.Zero)return;
            var cpu=new float[768];var gpu=new float[768];
            var warm=await Task.Run(()=>action(candidate,gpu,flag));NativeMethods.Check(warm.Error);token.ThrowIfCancellationRequested();
            NativeMethods.fi_clear_media_cache(handle);
            var watch=Stopwatch.StartNew();var baseline=await Task.Run(()=>action(handle,cpu,flag));var cpuTime=watch.Elapsed.TotalMilliseconds;NativeMethods.Check(baseline.Error);
            NativeMethods.fi_clear_media_cache(candidate);
            watch.Restart();var measured=await Task.Run(()=>action(candidate,gpu,flag));var gpuTime=watch.Elapsed.TotalMilliseconds;NativeMethods.Check(measured.Error);token.ThrowIfCancellationRequested();
            var similarity=cpu.Zip(gpu,(a,b)=>(double)a*b).Sum();
            if(baseline.Count==768&&measured.Count==768&&similarity>=.999&&gpuTime<=cpuTime/1.2)
            {
                NativeMethods.fi_close(handle);handle=candidate;candidate=IntPtr.Zero;DeviceDescription=$"Vulkan selected after measured {cpuTime/Math.Max(gpuTime,1):0.0}× speedup and {similarity:0.0000} vector agreement.";
            }
            else DeviceDescription=$"CPU selected; Vulkan did not pass the speed/vector agreement gate ({similarity:0.0000}).";
        }
        catch(OperationCanceledException){throw;}
        catch(Exception ex){DeviceDescription="CPU fallback: "+ex.Message;}
        finally{if(candidate!=IntPtr.Zero)NativeMethods.fi_close(candidate);}
    }
    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try { disposed=true; if(handle!=IntPtr.Zero) NativeMethods.fi_close(handle); handle=IntPtr.Zero; }
        finally { gate.Release(); }
    }
}
