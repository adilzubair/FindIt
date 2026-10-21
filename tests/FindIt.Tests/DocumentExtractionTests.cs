using System.IO.Compression;
using System.Text;
using System.Xml;
using FindIt.Runtime;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FindIt.Tests;

[TestClass]
public sealed class DocumentExtractionTests
{
    private string root = "";
    private const string R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    [TestInitialize] public void Initialize() { root = Path.Combine(Path.GetTempPath(), "FindIt-documents", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root); }
    [TestCleanup] public void Cleanup() { if (Directory.Exists(root)) Directory.Delete(root, true); }

    [TestMethod]
    public async Task WordKeepsAdjacentRunsSpacesAndTableCells()
    {
        string file = Package(".docx", ("word/document.xml", "<w:document xmlns:w='urn:word'><w:body><w:p><w:r><w:t>Hel</w:t><w:t>lo</w:t></w:r><w:r><w:t> </w:t><w:t>world</w:t><w:tab/><w:t>AB-0091</w:t></w:r></w:p><w:tbl><w:tr><w:tc><w:p><w:r><w:t>first cell</w:t></w:r></w:p></w:tc><w:tc><w:p><w:r><w:t>second cell</w:t></w:r></w:p></w:tc></w:tr></w:tbl></w:body></w:document>"));
        string text = Join(await Read(file));
        StringAssert.Contains(text, "Hello world AB-0091"); StringAssert.Contains(text, "first cell"); StringAssert.Contains(text, "second cell"); StringAssert.Contains(text, "Paragraph 1");
        Assert.IsFalse(text.Contains("Hel lo", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task PresentationUsesActualSlideOrderAndPreservesRunText()
    {
        string file = Package(".pptx",
            ("ppt/presentation.xml", $"<p:presentation xmlns:p='urn:ppt' xmlns:r='{R}'><p:sldIdLst><p:sldId id='256' r:id='r2'/><p:sldId id='257' r:id='r1'/></p:sldIdLst></p:presentation>"),
            ("ppt/_rels/presentation.xml.rels", "<Relationships><Relationship Id='r1' Target='slides/slide1.xml'/><Relationship Id='r2' Target='slides/slide2.xml'/></Relationships>"),
            ("ppt/slides/slide1.xml", "<s><p><r><t>second</t></r></p></s>"),
            ("ppt/slides/slide2.xml", "<s><p><r><t>first </t><t>slide</t></r></p></s>"));
        var passages = await Read(file); Assert.AreEqual(2, passages.Count);
        Assert.AreEqual(1, passages[0].Page); StringAssert.Contains(passages[0].Text, "first slide");
        Assert.AreEqual(2, passages[1].Page); StringAssert.Contains(passages[1].Text, "second");
    }

    [TestMethod]
    public async Task WorkbookPreservesExactSharedInlineAndCachedFormulaStringsWithCellLocators()
    {
        string file = Workbook("<row r='1'><c r='A1' t='s'><v>0</v></c><c r='B1' t='s'><v>1</v></c><c r='C1' t='inlineStr'><is><r><t>نص </t></r><r><t>عربي</t></r></is></c><c r='D1' t='str'><f>CONCAT(A1,B1)</f><v>00123X</v></c><c r='E1'><v>000123.4500</v></c></row>",
            "<sst><si><r><t>AB-</t></r><r><t>0091</t></r><rPh><t>ignore phonetic</t></rPh></si><si><t>second adjacent string</t></si></sst>");
        var passages = await Read(file); string text = Join(passages);
        StringAssert.Contains(text, "Worksheet 'Quarter ''One'''!A1:E1"); StringAssert.Contains(text, "A1: AB-0091"); StringAssert.Contains(text, "B1: second adjacent string");
        StringAssert.Contains(text, "C1: نص عربي"); StringAssert.Contains(text, "D1: 00123X"); StringAssert.Contains(text, "E1: 000123.4500"); Assert.IsFalse(text.Contains("ignore phonetic", StringComparison.Ordinal));
        Assert.AreEqual(1, passages[0].Page); Assert.IsTrue(passages[0].Embed);
    }

    [TestMethod]
    public async Task NumericSpreadsheetGroupsRetainExactValuesWithoutSemanticEmbeddingExplosion()
    {
        var rows = new StringBuilder();
        for (int i = 1; i <= 2000; i++) rows.Append($"<row r='{i}'><c r='A{i}'><v>{i}.0000</v></c><c r='B{i}' t='b'><v>1</v></c></row>");
        var passages = await Read(Workbook(rows.ToString()));
        Assert.IsTrue(passages.Count < 30, "Numeric cells should be grouped instead of yielding 4,000 segments.");
        Assert.IsTrue(passages.All(p => !p.Embed)); StringAssert.Contains(Join(passages), "A2000: 2000.0000"); StringAssert.Contains(Join(passages), "B1: 1");
    }

    [TestMethod]
    public async Task WorkbookInfersOmittedCoordinatesAndKeepsSparseExplicitCoordinates()
    {
        string file = Workbook("<row r='3'><c t='inlineStr'><is><t>first</t></is></c><c r='D3' t='inlineStr'><is><t>sparse</t></is></c><c t='inlineStr'><is><t>after sparse</t></is></c></row><row><c><v>123</v></c></row>");
        string text = Join(await Read(file)); StringAssert.Contains(text, "A3: first"); StringAssert.Contains(text, "D3: sparse"); StringAssert.Contains(text, "E3: after sparse"); StringAssert.Contains(text, "A4: 123");
    }

    [TestMethod]
    public async Task InvalidSharedStringReferenceIsReportedInsteadOfInventingText()
    { await Assert.ThrowsAsync<InvalidDataException>(async () => await Read(Workbook("<row><c r='A1' t='s'><v>8</v></c></row>", "<sst><si><t>only value</t></si></sst>"))); }

    [TestMethod]
    public async Task OpenDocumentKeepsAdjacentParagraphsSpansAndExplicitSpaces()
    {
        string file = Package(".odt", ("content.xml", "<document xmlns:text='urn:text'><text:p>Hello<text:s text:c='2'/><text:span>Arabic نص</text:span></text:p><text:p>adjacent paragraph</text:p></document>"));
        string text = Join(await Read(file)); StringAssert.Contains(text, "Hello  Arabic نص"); StringAssert.Contains(text, "adjacent paragraph");
    }

    [TestMethod]
    public async Task OpenSpreadsheetRepresentsRepeatedCellsAsRangesWithoutExpandingThem()
    {
        string file = Package(".ods", ("content.xml", "<document xmlns:table='urn:table' xmlns:office='urn:office' xmlns:text='urn:text'><table:table table:name='Amounts'><table:table-row table:number-rows-repeated='1000'><table:table-cell table:number-columns-repeated='10' office:value-type='float' office:value='100.25'><text:p>100.25</text:p></table:table-cell></table:table-row><table:table-row><table:table-cell office:value-type='string'><text:p>next item</text:p></table:table-cell></table:table-row></table:table></document>"));
        var passages = await Read(file); Assert.AreEqual(2, passages.Count); Assert.IsFalse(passages[0].Embed);
        StringAssert.Contains(passages[0].Text, "A1:J1000: 100.25"); StringAssert.Contains(passages[1].Text, "A1001: next item"); Assert.IsTrue(passages[1].Embed);
    }

    [TestMethod]
    public async Task HtmlKeepsInlineTextAndEntitiesAcrossBuffersButExcludesScriptsStylesAndComments()
    {
        string path = Path.Combine(root, "web.html");
        await File.WriteAllTextAsync(path, new string(' ', 3990) + "<div title='a > b'>AB-<b>0091</b> &amp; café &#x627;</div><SCRIPT>if(a<b){document.write('secret script');}</SCRIPT><!-- secret comment > --><style>.secret style{}</style><p>visible end</p>");
        string text = Join(await Read(path)); StringAssert.Contains(text, "AB-0091 & café ا"); StringAssert.Contains(text, "visible end");
        Assert.IsFalse(text.Contains("secret", StringComparison.Ordinal)); Assert.IsFalse(text.Contains("title=", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task HtmlUsesDeclaredLegacyCharsetAndSkipsBrokenLookingScriptOperators()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); string path = Path.Combine(root, "legacy.htm");
        await File.WriteAllBytesAsync(path, Encoding.GetEncoding(1252).GetBytes("<meta charset='windows-1252'><p>café</p><script>if (a < b && c > d) x++;</script><p>next</p>"));
        string text = Join(await Read(path)); StringAssert.Contains(text, "café"); StringAssert.Contains(text, "next"); Assert.IsFalse(text.Contains("x++", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task HtmlRetainsLiteralLessThanAndOverlappingContextAtChunkEdges()
    {
        string path = Path.Combine(root, "context.html");
        await File.WriteAllTextAsync(path, "<p>" + string.Join(' ', Enumerable.Repeat("filler", 569)) + " the bicycle in a beautiful forest &amp; visible river 3 < 5</p>");
        var passages = await Read(path); string text = Join(passages); StringAssert.Contains(text, "bicycle in a beautiful forest & visible river 3 < 5"); Assert.IsTrue(passages.Count > 1);
    }

    [TestMethod]
    public async Task RtfSupportsUnicodeFallbackEscapesCodePagesAndSkipsNonTextDestinations()
    {
        string path = Path.Combine(root, "rich.rtf");
        await File.WriteAllTextAsync(path, "{\\rtf1\\ansi\\ansicpg1252{\\fonttbl{\\f0 hidden font;}}\\uc1 Hello \\b bold\\b0 \\'e9\\par Arabic \\u1575?\\u1604?\\u1593?\\u1585?\\u1576?\\u1610? \\{brace\\} \\\\ {\\*\\unknown hidden destination}{\\pict hidden image}{\\field{\\*\\fldinst HYPERLINK hidden target}{\\fldrslt visible link}}}", Encoding.Latin1);
        string text = Join(await Read(path)); StringAssert.Contains(text, "Hello boldé"); StringAssert.Contains(text, "Arabic العربي"); StringAssert.Contains(text, "{brace} \\"); StringAssert.Contains(text, "visible link");
        Assert.IsFalse(text.Contains("hidden", StringComparison.Ordinal)); Assert.IsFalse(text.StartsWith("1", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task RtfBinaryBlocksDoNotChangeGroupStructureAndNestedUnicodeFallbackIsScoped()
    {
        string path = Path.Combine(root, "binary.rtf");
        await File.WriteAllTextAsync(path, "{\\rtf1\\uc1 before {\\uc0\\u233} \\u233? {\\pict\\bin4 {}\\x} after}", Encoding.Latin1);
        string text = Join(await Read(path)); StringAssert.Contains(text, "before é é"); StringAssert.Contains(text, "after"); Assert.IsFalse(text.Contains("{}", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task RtfDecodesMultibyteHexRunsAndHonorsHiddenText()
    {
        string path = Path.Combine(root, "utf8.rtf");
        await File.WriteAllTextAsync(path, "{\\rtf1\\ansicpg65001 UTF8 \\'d8\\'a7\\'d9\\'84\\par visible \\v hidden secret\\v0 restored}", Encoding.Latin1);
        string text = Join(await Read(path)); StringAssert.Contains(text, "UTF8 ال"); StringAssert.Contains(text, "restored"); Assert.IsFalse(text.Contains("secret", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task MalformedRtfAndDtdDocumentsFailWithClearErrors()
    {
        string path = Path.Combine(root, "bad.rtf"); await File.WriteAllTextAsync(path, "{\\rtf1 unclosed");
        await Assert.ThrowsAsync<InvalidDataException>(async () => await Read(path));
        string word = Package(".docx", ("word/document.xml", "<!DOCTYPE x [<!ENTITY leak SYSTEM 'file:///C:/not-readable'>]><document><p><t>&leak;</t></p></document>"));
        await Assert.ThrowsAsync<XmlException>(async () => await Read(word));
    }

    [TestMethod]
    public async Task HighlyCompressedXmlIsRejectedBeforeDecompression()
    {
        string path = Path.Combine(root, "bomb.docx"); using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        { using var writer = new StreamWriter(zip.CreateEntry("word/document.xml", CompressionLevel.SmallestSize).Open()); writer.Write("<document><p><t>"); writer.Write(new string('a', 4 * 1024 * 1024)); writer.Write("</t></p></document>"); }
        await Assert.ThrowsAsync<InvalidDataException>(async () => await Read(path));
    }

    [TestMethod]
    public async Task CancellationIsHonoredWithoutOpeningOrRequiringModels()
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => { await foreach (var unused in DocumentTextExtraction.ReadAsync(Path.Combine(root, "missing.docx"), cancellation.Token)) { } });
    }

    private string Workbook(string rows, string shared = "<sst/>") => Package(".xlsx",
        ("xl/workbook.xml", $"<workbook xmlns:r='{R}'><sheets><sheet name=\"Quarter 'One'\" sheetId='8' r:id='r8'/></sheets></workbook>"),
        ("xl/_rels/workbook.xml.rels", "<Relationships><Relationship Id='r8' Target='worksheets/sheet8.xml'/></Relationships>"),
        ("xl/sharedStrings.xml", shared), ("xl/worksheets/sheet8.xml", "<worksheet><sheetData>" + rows + "</sheetData></worksheet>"));
    private string Package(string extension, params (string Name, string Text)[] parts)
    {
        string path = Path.Combine(root, Guid.NewGuid().ToString("N") + extension); using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var part in parts) { using var writer = new StreamWriter(zip.CreateEntry(part.Name, CompressionLevel.NoCompression).Open(), new UTF8Encoding(false)); writer.Write(part.Text); } return path;
    }
    private static async Task<List<DocumentPassage>> Read(string path)
    { var passages = new List<DocumentPassage>(); await foreach (var passage in DocumentTextExtraction.ReadAsync(path)) passages.Add(passage); return passages; }
    private static string Join(IEnumerable<DocumentPassage> passages) => string.Join('\n', passages.Select(p => p.Text));
}
