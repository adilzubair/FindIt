using FindIt.Core;
using FindIt.Shared;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FindIt.Tests;

[TestClass]
public sealed class QueryTermsTests
{
    [TestMethod]
    public void ShortEnglishWordsMatchWholeUnlessStillBeingTyped()
    {
        CollectionAssert.AreEqual(new[] { new QueryTerm("hot", false, false), new QueryTerm("air", false, false), new QueryTerm("balloons", false, true) }, QueryTerms.Parse("hot air balloons"));
        CollectionAssert.AreEqual(new[] { new QueryTerm("hot", false, false), new QueryTerm("air", false, true) }, QueryTerms.Parse("hot air"));
        CollectionAssert.AreEqual(new[] { new QueryTerm("get", false, false), new QueryTerm("deposit", false, true), new QueryTerm("back", false, true) }, QueryTerms.Parse("When do I get my deposit back"));
        CollectionAssert.AreEqual(new[] { new QueryTerm("last ferry back", true, false), new QueryTerm("cash", false, true) }, QueryTerms.Parse("\"last ferry back\" the cash"));
        CollectionAssert.AreEqual(new[] { new QueryTerm("بيت", false, true), new QueryTerm("جميل", false, true) }, QueryTerms.Parse("بيت جميل"), "Other scripts keep prefixes for suffixed forms.");
        CollectionAssert.AreEqual(new[] { new QueryTerm("boat", false, true), new QueryTerm("dog", false, true) }, QueryTerms.Parse("boat Boat dog"), "Repeated words are one term.");
    }

    [TestMethod]
    public void PreviewHighlightsSearchedWordsWhereWordsStart()
    {
        const string text = "Signed together with the Deposit, when the keys come back. Get the backup.";
        var highlighter = QueryTerms.Highlighter("when do I get my deposit back")!;
        CollectionAssert.AreEqual(new[] { "Deposit", "back", "Get", "back" }, highlighter.Matches(text).Select(m => m.Value).ToArray(), "Not inside \"together\", not the ignored \"when\".");
        Assert.AreEqual("IMG_6025", QueryTerms.Highlighter("IMG_6025")!.Match("Photos/IMG_6025.JPG").Value);
        Assert.AreEqual("last ferry back", QueryTerms.Highlighter("\"last ferry back\"")!.Match("and the last ferry back leaves at 7.40").Value);
        Assert.IsFalse(QueryTerms.Highlighter("\"last ferry back\"")!.IsMatch("the last ferry backs up"));
        Assert.IsNull(QueryTerms.Highlighter("what is the"));
    }

    [TestMethod]
    public void SnippetCentresOnTheMostInformativePassage()
    {
        string filler = string.Concat(Enumerable.Repeat("The Landlord lets the apartment with its fixtures and furniture. ", 8));
        string tenancy = "This agreement is made together with the attached inventory. " + filler + "5. DEPOSIT On signing the Tenant pays a deposit. The Landlord will return the Deposit within fourteen (14) days. " + filler;
        string snippet = ArchiveService.Snippet(tenancy, QueryTerms.Parse("when do I get my deposit back"));
        StringAssert.Contains(snippet, "return the Deposit"); Assert.IsFalse(snippet.Contains("together", StringComparison.Ordinal));

        string memo = "The ferry to the island leaves at 9.15. " + filler + "And the last ferry back leaves at 7.40. " + filler;
        StringAssert.Contains(ArchiveService.Snippet(memo, QueryTerms.Parse("what time is the last ferry back")), "last ferry back leaves at 7.40");
        StringAssert.Contains(ArchiveService.Snippet(filler + "Signed together. " + filler, QueryTerms.Parse("gether")), "together", "Without a word-start match the earliest substring is kept.");
    }

    [TestMethod]
    public void CoverageReadsWordsAsTheCatalogIndexDoes()
    {
        CollectionAssert.AreEqual(new[] { "goreme", "nevsehir", "img", "6025", "jpg", "بيتي" }, ArchiveService.Words("Göreme, Nevşehir — IMG_6025.JPG بيتي"));
        (string[] Words, bool Prefix)[] terms = [(["hot"], false), (["air"], false), (["balloons"], true), (["img", "60"], true)];
        Assert.AreEqual(1, ArchiveService.Covered(terms, "Documents/Kaya booking.txt", "Balloons launch from the hotel; Airport transfer."));
        Assert.AreEqual(4, ArchiveService.Covered(terms, "Photos/IMG_6025.JPG", "Hot air balloons"));
    }
}
