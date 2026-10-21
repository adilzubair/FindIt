using System.Text.Json;
using FindIt.Runtime;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FindIt.Tests;

[TestClass]
public sealed class VideoWindowTests
{
    [TestMethod]
    public void SparseFramesNeverEscapeTheirDeclaredEightSecondWindow()
    {
        var windows=Run(Enumerable.Range(0,8).Select(i=>i*2000L),15000);
        CollectionAssert.AreEqual(new[]{(0L,8000L),(4000L,12000L),(8000L,15000L)},windows.Select(w=>(w.StartMs,w.EndMs)).ToArray());
        AssertInside(windows);
    }

    [TestMethod]
    public void LongSourceGapDoesNotCreateCatchUpClipsOrCarryAnEarlierFrameAcrossTheGap()
    {
        var windows=Run(new[]{0L}.Concat(Enumerable.Range(100,9).Select(i=>i*1000L)),109000);
        Assert.IsTrue(windows.Count<=3);Assert.IsTrue(windows.All(w=>w.Frames.All(f=>f.StartMs>=100000)));
        AssertInside(windows);
    }

    [TestMethod]
    public void HalfSecondShotOriginKeepsEightSecondWindowsFourSecondOverlapAndFinalTail()
    {
        var scheduler=new VideoWindowScheduler();scheduler.Add("blue",0);
        Assert.AreEqual(0,scheduler.Advance(500,true).Count());scheduler.Reset(500);
        var windows=new List<VideoWindow>();
        for(long time=1000;time<17500;time+=1000){windows.AddRange(scheduler.Advance(time));scheduler.Add("portrait"+time,time);}
        windows.AddRange(scheduler.Advance(17500,true));
        CollectionAssert.AreEqual(new[]{(500L,8500L),(4500L,12500L),(8500L,16500L),(12500L,17500L)},windows.Select(w=>(w.StartMs,w.EndMs)).ToArray());
        AssertInside(windows);
    }

    [TestMethod]
    public void CutBeforeTheNominalWindowEndProducesOnlyItsActualShot()
    {
        var windows=Run(Enumerable.Range(0,8).Select(i=>i*1000L),7500);
        Assert.AreEqual(1,windows.Count);Assert.AreEqual(0L,windows[0].StartMs);Assert.AreEqual(7500L,windows[0].EndMs);AssertInside(windows);
    }

    [TestMethod]
    public void DelayedVideoStartsItsFirstWindowOnTheCommonPlaybackTimeline()
    {
        var windows=Run(Enumerable.Range(3,8).Select(i=>i*1000L),11000);
        Assert.AreEqual(1,windows.Count);Assert.AreEqual(3000L,windows[0].StartMs);Assert.AreEqual(11000L,windows[0].EndMs);AssertInside(windows);
    }

    [TestMethod]
    [DataRow("3","3.2","3","3.2",0L,3200L)]
    [DataRow("3","3.2","0","7",3000L,6200L)]
    [DataRow("2.936","11.064","0","17",2936L,14000L)]
    [DataRow("N/A","N/A","N/A","11",0L,11000L)]
    public void StreamOffsetsAndEndsUseTheCommonContainerOrigin(string streamStart,string streamDuration,string containerStart,string containerDuration,long expectedStart,long expectedEnd)
    {
        using var json=JsonDocument.Parse(JsonSerializer.Serialize(new{streams=new[]{new{start_time=streamStart,duration=streamDuration}},format=new{start_time=containerStart,duration=containerDuration}}));
        Assert.AreEqual(new MediaTimeline(expectedStart,expectedEnd),MediaTimeline.Parse(json.RootElement));
    }

    [TestMethod]
    [DataRow("6.2",3200L)]
    [DataRow("3.2",3200L)]
    public void AmbiguousOffsetContainerDurationChoosesTheSmallestEndBeyondTheLastDecodedSample(string formatDuration,long expectedEnd)
    {
        using var json=JsonDocument.Parse(JsonSerializer.Serialize(new{streams=new[]{new{start_time="3",duration="N/A"}},format=new{start_time="3",duration=formatDuration}}));
        var timeline=MediaTimeline.Parse(json.RootElement);
        Assert.AreEqual(0L,timeline.StartMs);Assert.AreEqual(expectedEnd,timeline.ResolveEnd(3100),"Matroska's absolute format end and MP4's format length must resolve to the same normalized source interval.");
    }

    [TestMethod]
    public void MissingOrStaleEndMetadataStillIncludesTheLastDecodedPresentationSample()
    {
        Assert.AreEqual(3101L,new MediaTimeline(0,null).ResolveEnd(3100));
        Assert.AreEqual(3101L,new MediaTimeline(0,3000,100).ResolveEnd(3100));
    }

    private static List<VideoWindow> Run(IEnumerable<long> times,long end)
    {
        var scheduler=new VideoWindowScheduler();var windows=new List<VideoWindow>();
        foreach(long time in times){windows.AddRange(scheduler.Advance(time));scheduler.Add("frame"+time,time);}
        windows.AddRange(scheduler.Advance(end,true));return windows;
    }
    private static void AssertInside(IEnumerable<VideoWindow> windows)
    {
        foreach(var window in windows)
        {
            Assert.IsTrue(window.Frames.Count is >=2 and <=8);
            Assert.IsTrue(window.Frames.All(frame=>frame.StartMs>=window.StartMs&&frame.StartMs<window.EndMs),"Every encoded source image must lie inside the clip interval advertised to search.");
        }
    }
}
