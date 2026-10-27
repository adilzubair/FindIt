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
    public void CoverageReadsWordsAsTheCatalogIndexDoes()
    {
        CollectionAssert.AreEqual(new[] { "goreme", "nevsehir", "img", "6025", "jpg", "بيتي" }, ArchiveService.Words("Göreme, Nevşehir — IMG_6025.JPG بيتي"));
        (string[] Words, bool Prefix)[] terms = [(["hot"], false), (["air"], false), (["balloons"], true), (["img", "60"], true)];
        Assert.AreEqual(1, ArchiveService.Covered(terms, "Documents/Kaya booking.txt", "Balloons launch from the hotel; Airport transfer."));
        Assert.AreEqual(4, ArchiveService.Covered(terms, "Photos/IMG_6025.JPG", "Hot air balloons"));
    }
}
