using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;

namespace FindIt.Runtime;

internal sealed record DecodedVideoFrame(byte[] Pixels,long StartMs);

/// <summary>Pairs each decoded image with its source presentation time without retiming it.</summary>
internal static class VideoFrameReader
{
    internal static bool TryReadTimestamp(string line,out long milliseconds)
    {
        milliseconds=0;
        if(!IsFrameDiagnostic(line))return false;
        const string key=" pts_time:";int start=line.IndexOf(key,StringComparison.Ordinal);if(start<0)return false;start+=key.Length;
        int end=start;while(end<line.Length&&!char.IsWhiteSpace(line[end]))end++;
        if(!double.TryParse(line.AsSpan(start,end-start),NumberStyles.Float,CultureInfo.InvariantCulture,out double seconds)||!double.IsFinite(seconds)||seconds<0||seconds>=long.MaxValue/1000d)return false;
        milliseconds=checked((long)Math.Round(seconds*1000,MidpointRounding.AwayFromZero));return true;
    }

    internal static bool IsFrameDiagnostic(string line)=>line.Contains("Parsed_showinfo_",StringComparison.Ordinal)&&line.Contains(" n:",StringComparison.Ordinal);

    internal static async IAsyncEnumerable<DecodedVideoFrame> ReadAsync(string ffmpeg,string path,int width,int height,[EnumeratorCancellation]CancellationToken token)
    {
        // select retains source timestamps. fps=2 would move a selected picture onto
        // a synthetic half-second grid, which can seek before a brief face appears.
        // Advance to the next half-second grid boundary after the last retained
        // source frame. This avoids both VFR drift and a catch-up burst after a gap.
        string filter=$"select='isnan(prev_selected_t)+gte(t,ceil((prev_selected_t+0.001)*2)/2)',scale={width}:{height}:force_original_aspect_ratio=decrease,pad={width}:{height}:(ow-iw)/2:(oh-ih)/2,showinfo";
        using var process=ProcessRunner.Start(ffmpeg,["-nostdin","-hide_banner","-loglevel","info","-i",path,"-an","-vf",filter,"-fps_mode","passthrough","-threads:v","1","-f","rawvideo","-pix_fmt","bgr24","pipe:1"],true);
        using var registration=token.Register(()=>ProcessRunner.Kill(process));
        using var drainCancellation=CancellationTokenSource.CreateLinkedTokenSource(token);
        var timestamps=Channel.CreateBounded<long>(new BoundedChannelOptions(4){SingleReader=true,SingleWriter=true,FullMode=BoundedChannelFullMode.Wait});
        var diagnostics=new StringBuilder();
        Exception? timestampFailure=null;
        var drain=DrainAsync();var pixels=new byte[checked(width*height*3)];long previous=-1;
        try
        {
            while(true)
            {
                int read=0;while(read<pixels.Length){int count=await process.StandardOutput.BaseStream.ReadAsync(pixels.AsMemory(read),token);if(count==0)break;read+=count;}
                if(read==0)break;if(read!=pixels.Length)throw new InvalidDataException("Video decoder returned an incomplete frame.");
                long time;
                try{time=await timestamps.Reader.ReadAsync(token);}catch(ChannelClosedException error){token.ThrowIfCancellationRequested();throw new InvalidDataException("Video decoder did not supply a presentation timestamp for a frame.",error);}
                if(time<=previous)throw new InvalidDataException("Video presentation timestamps must increase.");previous=time;
                yield return new(pixels,time);
            }
            await process.WaitForExitAsync(token);await drain;
            if(timestampFailure is not null)throw new InvalidDataException("Video frame presentation timestamps could not be read.",timestampFailure);
            if(process.ExitCode!=0)throw new InvalidDataException("Video extraction failed: "+diagnostics);
            if(timestamps.Reader.TryRead(out _))throw new InvalidDataException("Video decoder returned a timestamp without its frame.");
        }
        finally
        {
            ProcessRunner.Kill(process);drainCancellation.Cancel();timestamps.Writer.TryComplete();
            try{await drain;}catch(OperationCanceledException){}catch(ChannelClosedException){}
        }

        async Task DrainAsync()
        {
            Exception? failure=null;
            try
            {
                while(await process.StandardError.ReadLineAsync(drainCancellation.Token) is { } line)
                {
                    if(TryReadTimestamp(line,out long time))await timestamps.Writer.WriteAsync(time,drainCancellation.Token);
                    else if(IsFrameDiagnostic(line))throw new InvalidDataException("A decoded frame has a missing or invalid presentation timestamp.");
                    else{diagnostics.AppendLine(line);if(diagnostics.Length>4096)diagnostics.Remove(0,diagnostics.Length-4096);}
                }
            }
            catch(Exception error) when(error is OperationCanceledException or IOException or ChannelClosedException){failure=timestampFailure=error;if(error is InvalidDataException)ProcessRunner.Kill(process);}
            finally{timestamps.Writer.TryComplete(failure);}
        }
    }
}
