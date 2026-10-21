using FindIt.Runtime;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FindIt.Tests;

[TestClass]
public sealed class VideoFrameTimestampTests
{
    [TestMethod]
    [DataRow("0",0L)]
    [DataRow("0.7",700L)]
    [DataRow("580.123333",580123L)]
    [DataRow("5e-4",1L)]
    public void ReadsInvariantSourcePresentationTimesFromFrameDiagnostics(string seconds,long expected)
    {
        string line=$"[Parsed_showinfo_5 @ 000001] n:   17 pts: 34800 pts_time:{seconds} duration: 500 duration_time:0.5 fmt:bgr24";
        Assert.IsTrue(VideoFrameReader.TryReadTimestamp(line,out long milliseconds));Assert.AreEqual(expected,milliseconds);
    }

    [TestMethod]
    [DataRow("N/A")]
    [DataRow("NaN")]
    [DataRow("Infinity")]
    [DataRow("-0.5")]
    [DataRow("1e300")]
    [DataRow("")]
    public void RejectsMissingNonfiniteNegativeOrUnrepresentableTimes(string seconds)
    {
        Assert.IsFalse(VideoFrameReader.TryReadTimestamp($"[Parsed_showinfo_5 @ 000001] n: 0 pts:0 pts_time:{seconds} fmt:bgr24",out _));
    }

    [TestMethod]
    public void CodecAndFilterConfigurationMessagesCannotBeMistakenForFrameTimes()
    {
        Assert.IsFalse(VideoFrameReader.TryReadTimestamp("Input file name n:0 pts_time:0.7",out _));
        Assert.IsFalse(VideoFrameReader.TryReadTimestamp("[Parsed_showinfo_5 @ 000001] config in time_base: 1/1000, pts_time:0.7",out _));
        Assert.IsFalse(VideoFrameReader.TryReadTimestamp("[Parsed_showinfo_5 @ 000001] n:0 pts:700",out _));
    }
}
