using System.Text.Json;
using FindIt.Runtime;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FindIt.Tests;

[TestClass]
public class SpeechTests
{
    [TestMethod]
    public void TranscriptOffsetsRemainRelativeToOriginalAndCannotEscapeWindow()
    {
        using var json=JsonDocument.Parse("""{"transcription":[{"text":" first words ","offsets":{"from":1200,"to":3200}},{"text":"last words","offsets":{"from":27000,"to":34000}},{"text":" ","offsets":{"from":0,"to":500}},{"text":"invalid interval","offsets":{"from":29000,"to":28000}}]}""");
        var result=SpeechTimestamps.Parse(json.RootElement,25000,30000);
        Assert.AreEqual(2,result.Count); Assert.AreEqual(new SpeechPassage(26200,28200,"first words"),result[0]);
        Assert.AreEqual(new SpeechPassage(52000,55000,"last words"),result[1]);
    }
}
