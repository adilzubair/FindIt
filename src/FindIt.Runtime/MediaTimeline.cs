using System.Globalization;
using System.Text.Json;

namespace FindIt.Runtime;

internal sealed record MediaTimeline(long StartMs,long? EndMs,long? AlternateEndMs=null)
{
    // Some containers report fallback format.duration as an ending timestamp,
    // others as a length. Prefer the smallest candidate beyond the last decoded
    // sample; missing/ambiguous EOF metadata must never exclude an encoded frame.
    internal long ResolveEnd(long lastDecodedMs)
    {
        var candidates=new[]{EndMs,AlternateEndMs}.Where(value=>value>lastDecodedMs).Select(value=>value!.Value).ToArray();
        return candidates.Length>0?candidates.Min():checked(lastDecodedMs+1);
    }
    internal static async Task<MediaTimeline> ProbeAsync(string? ffprobe,string path,string stream,CancellationToken token)
    {
        if(ffprobe is null)return new(0,null);
        string output=await ProcessRunner.CaptureAsync(ffprobe,["-v","error","-select_streams",stream,"-show_entries","stream=start_time,duration:format=start_time,duration","-of","json",path],token);
        using var json=JsonDocument.Parse(output);return Parse(json.RootElement);
    }

    internal static MediaTimeline Parse(JsonElement root)
    {
        static long? Value(JsonElement element,string name)
        {
            if(!element.TryGetProperty(name,out var value)||!double.TryParse(value.ToString(),NumberStyles.Float,CultureInfo.InvariantCulture,out double seconds)||!double.IsFinite(seconds)||Math.Abs(seconds)>=long.MaxValue/1000d)return null;
            return checked((long)Math.Round(seconds*1000,MidpointRounding.AwayFromZero));
        }
        long formatStart=0;long? formatDuration=null;
        if(root.TryGetProperty("format",out var format)){formatStart=Value(format,"start_time")??0;formatDuration=Value(format,"duration");}
        long start=0;long? end=formatDuration,alternate=null;bool streamEnd=false;
        if(root.TryGetProperty("streams",out var streams)&&streams.GetArrayLength()>0)
        {
            var stream=streams[0];start=Math.Max(0,checked((Value(stream,"start_time")??formatStart)-formatStart));
            if(Value(stream,"duration") is long duration&&duration>0){end=checked(start+duration);streamEnd=true;}
        }
        if(!streamEnd&&formatDuration is >0&&formatStart!=0)
        {
            long candidate=checked(formatDuration.Value-formatStart);
            if(candidate>0&&candidate!=end)alternate=candidate;
        }
        return new(start,end is >0?end:null,alternate);
    }
}
