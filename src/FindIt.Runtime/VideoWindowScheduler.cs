namespace FindIt.Runtime;

internal sealed record VideoWindowFrame(string Path,long StartMs,byte[]? Jpeg=null);
internal sealed record VideoWindow(long StartMs,long EndMs,IReadOnlyList<VideoWindowFrame> Frames);

/// <summary>Schedules bounded visual windows by presentation time, never by frame count.</summary>
internal sealed class VideoWindowScheduler
{
    private readonly Queue<VideoWindowFrame> frames=[];
    private long start,lastPublishedEnd;
    private bool initialized;

    internal void Reset(long shotStartMs){frames.Clear();start=lastPublishedEnd=shotStartMs;initialized=true;}

    internal void Add(string path,long time,byte[]? jpeg=null)
    {
        if(!initialized)Reset(time);
        frames.Enqueue(new(path,time,jpeg));while(frames.Count>8)frames.Dequeue();
    }

    internal IEnumerable<VideoWindow> Advance(long time,bool endShot=false)
    {
        if(!initialized)yield break;
        while(start+8000<=time)
        {
            long end=start+8000;
            var selected=frames.Where(f=>f.StartMs>=start&&f.StartMs<end).ToArray();
            if(selected.Length>=2){lastPublishedEnd=end;yield return new(start,end,selected);}
            start+=4000;
            while(frames.TryPeek(out var first)&&first.StartMs<start)frames.Dequeue();
            if(frames.Count==0&&start+8000<=time)
                start+=((time-start-8000)/4000+1)*4000;
        }
        if(endShot&&time>lastPublishedEnd)
        {
            var selected=frames.Where(f=>f.StartMs>=start&&f.StartMs<time).ToArray();
            if(selected.Length>=2){lastPublishedEnd=time;yield return new(start,time,selected);}
        }
    }
}
