using FindIt.Runtime;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FindIt.Tests;

[TestClass]
public sealed class SceneChangeTests
{
    [TestMethod]
    public void AbruptNewShotAddsACutButMovementWithinTheSamePaletteDoesNot()
    {
        var detector=new SceneChangeDetector();var shot=new byte[32*18*3];
        for(int i=0;i<shot.Length;i+=3){shot[i]=i%6==0?(byte)220:(byte)30;shot[i+1]=50;shot[i+2]=30;}
        Assert.IsFalse(detector.Observe(shot));
        Assert.IsFalse(detector.Observe(shot));
        var moved=(byte[])shot.Clone();
        for(int i=0;i<moved.Length;i+=3)moved[i]=shot[i]==220?(byte)30:(byte)220;
        Assert.IsFalse(detector.Observe(moved),"A moving object or pan that retains the shot's palette must not add a scene sample.");
        var next=new byte[shot.Length];for(int i=0;i<next.Length;i+=3){next[i]=20;next[i+1]=40;next[i+2]=230;}
        Assert.IsTrue(detector.Observe(next),"A new frame with a broad visual and palette change should add a supplemental sample.");
        Assert.IsFalse(detector.Observe(next));
    }

    [TestMethod]
    public void BlackLetterboxingCannotHideACutInsideTheActivePicture()
    {
        var detector=new SceneChangeDetector();var first=new byte[32*18*3];var second=new byte[first.Length];
        for(int y=5;y<13;y++)for(int x=0;x<32;x++)
        {
            int pixel=(y*32+x)*3;first[pixel]=220;first[pixel+1]=50;first[pixel+2]=30;
            second[pixel]=20;second[pixel+1]=40;second[pixel+2]=230;
        }
        Assert.IsFalse(detector.Observe(first));
        Assert.IsTrue(detector.Observe(second),"Unchanged black borders must not dilute a broad change within the actual picture.");
        Assert.IsFalse(detector.Observe(second));
    }

    [TestMethod]
    public void NearBlackCompressionNoiseDoesNotCreateASceneCut()
    {
        var detector=new SceneChangeDetector();var dark=new byte[32*18*3];
        Assert.IsFalse(detector.Observe(dark));Array.Fill(dark,(byte)19);
        Assert.IsFalse(detector.Observe(dark));Array.Fill(dark,(byte)3);
        Assert.IsFalse(detector.Observe(dark));
    }
}
