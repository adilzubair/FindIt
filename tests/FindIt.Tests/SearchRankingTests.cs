using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using FindIt.Core;
using FindIt.Shared;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FindIt.Tests;

// A description of a picture or footage must find it first, while codes, names, phrases and documents holding most of a
// query's words still win by their words. The fixture mirrors the demo drive these searches were measured on.
[TestClass]
public sealed class SearchRankingTests
{
    private string root = "";
    [TestInitialize] public void Initialize() => root = Path.Combine(Path.GetTempPath(), "FindIt-search-ranking", Guid.NewGuid().ToString("N"));
    [TestCleanup] public void Cleanup() { if (Directory.Exists(root)) Directory.Delete(root, true); }

    private static readonly Dictionary<string, string> Drive = new()
    {
        // Pictures and footage: what they show is embedded, but they hold no words.
        ["Photos/Cappadocia/IMG_6025.jpg"] = "hot air balloons over the valley at sunrise",
        ["Photos/Algarve/IMG_6040.jpg"] = "a beach with umbrellas",
        ["Photos/Lisbon/IMG_6061.jpg"] = "a ferry timetable showing each departure time",
        ["Videos/GX010274.mp4"] = "dolphins swimming beside the boat",
        // Documents and recordings sharing a word or two with those descriptions.
        ["Documents/Kaya Cave Suites - booking.txt"] = "Balloon flights. Balloons launch at sunrise; pick-up from the hotel is around 05:00. Airport transfer from Kayseri.",
        ["Documents/Boarding pass.txt"] = "Atlantica Air. Boarding 07:10, gate 14.",
        ["Documents/Tenancy agreement.txt"] = "Hot water is included in the rent. The Tenant pays a deposit on signing, and the Landlord will return the Deposit within fourteen days of the end of the tenancy.",
        ["Documents/Invoice INV-2026-031.txt"] = "Invoice INV-2026-031 for design work, due November 2026.",
        ["Documents/Electricity bill.txt"] = "Bill date 6 October 2026. Reference 031 4410.",
        ["Voice memos/Recording 14.m4a"] = "The ferry to the island leaves at 9.15, and the last ferry back leaves at 7.40. Bring cash.",
        ["Voice memos/Recording 17.m4a"] = "They don't take cards on the boat.",
        ["Voice memos/Recording 23.m4a"] = "Restaurant tip from the hotel. Get there before seven.",
    };

    [TestMethod]
    public async Task DescriptionsFindTheClosestPictureAboveDocumentsSharingAWord()
    {
        await using var service = await PrepareAsync();
        foreach (var (query, expected) in new[] { ("hot air balloons", "IMG_6025.jpg"), ("hot air balloons at sunrise", "IMG_6025.jpg"), ("hot air ball", "IMG_6025.jpg"),
            ("dolphins swimming beside the boat", "GX010274.mp4"), ("dolphins swimming next to our boat", "GX010274.mp4") })
        {
            var response = await service.SearchAsync(new() { Query = query });
            Assert.IsTrue(response.SemanticUsed); Assert.AreEqual(expected, response.Results[0].Name, query);
            Assert.AreEqual(MatchCategory.Semantic, response.Results[0].MatchCategory, query);
        }
        // A document that shares a word is still found by meaning, and says so rather than claiming exact words.
        var balloons = await service.SearchAsync(new() { Query = "hot air balloons at sunrise" });
        Assert.AreEqual(MatchCategory.Semantic, balloons.Results.Single(r => r.Name.StartsWith("Kaya", StringComparison.Ordinal)).MatchCategory);
    }

    [TestMethod]
    public async Task DocumentsHoldingMostOfTheMeaningfulWordsStillRankFirst()
    {
        await using var service = await PrepareAsync();
        // The memos sit closer to a "when" question by meaning; "get" and "back" alone are not what is asked about.
        await AssertFirst(service, "when do I get my deposit back", "Tenancy agreement.txt", MatchCategory.Exact);
        // A picture of a timetable is closer by meaning, but the memo holds "last ferry back".
        await AssertFirst(service, "what time is the last ferry back", "Recording 14.m4a", MatchCategory.Exact);
        await AssertFirst(service, "deposit refund", "Tenancy agreement.txt", MatchCategory.Exact);
        await AssertFirst(service, "\"last ferry back\"", "Recording 14.m4a", MatchCategory.Exact);
        await AssertFirst(service, "ferry", "Recording 14.m4a", MatchCategory.Exact);
    }

    [TestMethod]
    public async Task CodesAndFilenamesStillWinByTheirWords()
    {
        await using var service = await PrepareAsync();
        await AssertFirst(service, "INV-2026-031", "Invoice INV-2026-031.txt", MatchCategory.Exact);
        await AssertFirst(service, "IMG_6025", "IMG_6025.jpg", MatchCategory.Exact);
        var partial = await service.SearchAsync(new() { Query = "IMG_60" });
        CollectionAssert.AreEquivalent(new[] { "IMG_6025.jpg", "IMG_6040.jpg", "IMG_6061.jpg" }, partial.Results.Take(3).Select(r => r.Name).ToArray());
        Assert.IsTrue(partial.Results.Take(3).All(r => r.MatchCategory == MatchCategory.Exact));
    }

    [TestMethod]
    public async Task ExactWordsRequiresWholeShortWordsExceptTheOneBeingTyped()
    {
        await using var service = await PrepareAsync(("Documents/Brochure.txt", "Hot air balloons fly over the valley every morning."));
        foreach (var query in new[] { "hot air balloons", "hot air ball" })
        {
            // "hot" and "air" no longer match "hotel" and "Airport" in the booking.
            var exact = await service.SearchAsync(new() { Query = query, Filters = new() { ExactOnly = true } });
            CollectionAssert.AreEqual(new[] { "Brochure.txt" }, exact.Results.Select(r => r.Name).ToArray(), query);
        }
        await AssertFirst(service, "hot air balloons", "Brochure.txt", MatchCategory.Exact);
    }

    [TestMethod]
    public async Task OtherScriptsKeepPrefixMatchesAndCoverage()
    {
        await using var service = await PrepareAsync(("Documents/ملاحظات.txt", "بيتي جميل كبير"));
        await AssertFirst(service, "بيت جميل كبير", "ملاحظات.txt", MatchCategory.Exact);
        var exact = await service.SearchAsync(new() { Query = "بيت جميل", Filters = new() { ExactOnly = true } });
        CollectionAssert.AreEqual(new[] { "ملاحظات.txt" }, exact.Results.Select(r => r.Name).ToArray());
    }

    [TestMethod]
    public async Task WeakKeywordHitsRemainWhenMeaningIsUnavailable()
    {
        await (await PrepareAsync()).DisposeAsync();
        await using var keywords = new ArchiveService(root, new IdeaEmbeddings("another-encoder"), new FixtureProcessor(new IdeaEmbeddings("another-encoder")));
        var response = await keywords.SearchAsync(new() { Query = "hot air balloons" });
        Assert.IsFalse(response.SemanticUsed);
        CollectionAssert.IsSubsetOf(new[] { "Kaya Cave Suites - booking.txt", "Boarding pass.txt", "Tenancy agreement.txt" }, response.Results.Select(r => r.Name).ToArray());
        Assert.IsTrue(response.Results.All(r => r.MatchCategory == MatchCategory.Exact));
    }

    private static async Task AssertFirst(ArchiveService service, string query, string name, MatchCategory category)
    {
        var response = await service.SearchAsync(new() { Query = query });
        Assert.AreEqual(name, response.Results[0].Name, query); Assert.AreEqual(category, response.Results[0].MatchCategory, query);
    }

    private async Task<ArchiveService> PrepareAsync(params (string Path, string Text)[] extra)
    {
        foreach (var (path, text) in Drive.Select(p => (p.Key, p.Value)).Concat(extra))
        {
            string full = Path.Combine(root, path); Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            await File.WriteAllTextAsync(full, text);
        }
        var embeddings = new IdeaEmbeddings("ranking-ideas-v1");
        var service = new ArchiveService(root, embeddings, new FixtureProcessor(embeddings));
        Assert.IsTrue((await service.PrepareAsync(new())).SemanticAvailable);
        return service;
    }

    // One dimension per idea, present when one of its cues appears. A picture of a description shares all its ideas;
    // a document sharing a word shares one, and the short memos sit closest to "when" questions, as on the demo drive.
    private sealed class IdeaEmbeddings(string fingerprint) : IEmbeddingProvider
    {
        private static readonly Regex[] Ideas = [.. new[] { "balloon", "sunrise", "hot air", "dolphin", "swim", "boat", "deposit", "tenan|landlord", "rent|water", "signing", @"\bwhen\b|\btime\b|leaves at|before seven", "ferry|island", "cash|cards" }
            .Select(cue => new Regex(cue, RegexOptions.IgnoreCase))];
        public bool Available => true; public string Fingerprint => fingerprint;
        public Task<float[]?> EmbedTextAsync(string text, bool query, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); var vector = new float[512]; vector[0] = .05f;
            for (int i = 0; i < Ideas.Length; i++) if (Ideas[i].IsMatch(text)) vector[i + 1] = 1;
            return Task.FromResult<float[]?>(vector);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FixtureProcessor(IEmbeddingProvider embeddings) : IContentProcessor
    {
        public IReadOnlyList<RuntimeCapability> Capabilities => []; public string FaceFingerprint => "ranking-no-faces-v1";
        public async IAsyncEnumerable<ExtractedSegment> ExtractAsync(string absolutePath, FileKind kind, PreparationRequest options, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            string text = await File.ReadAllTextAsync(absolutePath, cancellationToken);
            if (kind is FileKind.Image or FileKind.Video)
                yield return new() { Kind = kind == FileKind.Image ? SegmentKind.Image : SegmentKind.VideoFrame, StartMs = kind == FileKind.Video ? 0 : null, EndMs = kind == FileKind.Video ? 1000 : null, Embedding = await embeddings.EmbedTextAsync(text, false, cancellationToken) };
            else yield return new() { Kind = kind == FileKind.Audio ? SegmentKind.Transcript : SegmentKind.Text, StartMs = kind == FileKind.Audio ? 0 : null, EndMs = kind == FileKind.Audio ? 9000 : null, Text = text };
        }
        public Task<IReadOnlyList<DetectedFace>> DetectReferenceFacesAsync(string absolutePath, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<DetectedFace>>([]);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
