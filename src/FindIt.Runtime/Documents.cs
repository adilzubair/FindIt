using System.Globalization;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using FindIt.Shared;
using Tesseract;

[assembly: InternalsVisibleTo("FindIt.Tests")]

namespace FindIt.Runtime;

public sealed partial class ContentProcessor
{
    private TesseractEngine? ocr;
    private string? ocrLanguages;
    private string ReadOcr(byte[] image, IReadOnlyList<string> languages)
    {
        if (paths.Tessdata is not { } folder) return "";
        var installed = languages.Where(x => Regex.IsMatch(x, "^[a-z]{3}(_[a-z]+)?$") && File.Exists(Path.Combine(folder, x + ".traineddata"))).Distinct().Order().ToArray();
        if (installed.Length == 0) return "";
        var joined = string.Join('+', installed);
        foreach (var language in installed) ModelIntegrity.Verify(Path.Combine(folder, language + ".traineddata"), language + ".traineddata");
        if (ocr is null || ocrLanguages != joined) { ocr?.Dispose(); ocr = new TesseractEngine(folder, joined, EngineMode.LstmOnly); ocrLanguages = joined; }
        using var pix = Pix.LoadFromMemory(image); using var page = ocr.Process(pix); return page.GetText().Trim();
    }

    private async IAsyncEnumerable<ExtractedSegment> DocumentsAsync(string path, string scratch, PreparationRequest options, [EnumeratorCancellation] CancellationToken token)
    {
        if (Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var page in PdfDocument.Read(path, token))
            {
                var words = page.Text;
                if (words.Count(char.IsLetterOrDigit) < 40) words = ReadOcr(page.Image, options.OcrLanguages);
                var imagePath = Path.Combine(scratch, "page.jpg"); await File.WriteAllBytesAsync(imagePath, page.Image, token);
                yield return new() { Kind = SegmentKind.DocumentPage, Page = page.Number, Text = words, ThumbnailJpeg = page.Thumbnail, Embedding = await media.EmbedMediaAsync(imagePath, true, token) };
                foreach (var chunk in DocumentTextExtraction.Chunk(words)) yield return new() { Kind = SegmentKind.Text, Page = page.Number, Text = chunk, Embedding = await text.EmbedTextAsync(chunk, false, token) };
            }
            yield break;
        }
        await foreach (var passage in DocumentTextExtraction.ReadAsync(path, token))
            yield return new() { Kind = SegmentKind.Text, Page = passage.Page, Text = passage.Text, SkipTextEmbedding = !passage.Embed,
                Embedding = passage.Embed ? await text.EmbedTextAsync(passage.Text, false, token) : null };
    }
}

internal sealed record DocumentPassage(string Text, int? Page = null, bool Embed = true);

/// <summary>Bounded managed extraction; Office XML parts are streamed without requiring Office or a native model.</summary>
internal static class DocumentTextExtraction
{
    private const int ChunkSize = 4000, MaximumXmlCharacters = 64 * 1024 * 1024, MaximumCellCharacters = 1024 * 1024, MaximumMetadataCharacters = 4 * 1024 * 1024;
    private static readonly HashSet<string> RtfDestinations = new(StringComparer.Ordinal)
    { "fonttbl", "colortbl", "stylesheet", "info", "pict", "object", "objdata", "filetbl", "listtable", "listoverridetable", "revtbl", "rsidtbl", "generator", "xmlnstbl", "datastore", "themedata", "colorschememapping", "fldinst", "nonshppict", "shppict" };
    private static readonly HashSet<string> HtmlBlocks = new(StringComparer.OrdinalIgnoreCase)
    { "p", "div", "br", "hr", "li", "tr", "td", "th", "table", "section", "article", "header", "footer", "blockquote", "pre", "h1", "h2", "h3", "h4", "h5", "h6" };
    static DocumentTextExtraction() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    internal static async IAsyncEnumerable<DocumentPassage> ReadAsync(string path, [EnumeratorCancellation] CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested(); string extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is ".doc" or ".xls" or ".ppt") throw new NotSupportedException("This binary Office format needs conversion to DOCX, XLSX, PPTX or PDF before content indexing.");
        if (extension is ".docx" or ".pptx" or ".xlsx" or ".odt" or ".ods" or ".odp")
        {
            using var zip = ZipFile.OpenRead(path); ValidateZip(zip, token);
            var passages = extension switch { ".xlsx" => ReadWorkbook(zip, token), ".pptx" => ReadPresentation(zip, token), ".docx" => ReadWord(zip, token), _ => ReadOpenDocument(zip, extension, token) };
            foreach (var passage in passages) { token.ThrowIfCancellationRequested(); yield return passage; } yield break;
        }
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (extension is ".rtf" or ".html" or ".htm" && stream.Length > 128L * 1024 * 1024) throw new InvalidDataException("This formatted-text file exceeds the 128 MiB extraction safety limit.");
        if (extension == ".rtf")
        {
            using var input = new StreamReader(stream, Encoding.Latin1, false, 65536, leaveOpen: true);
            foreach (var passage in ReadRtf(input, token)) yield return new(passage); yield break;
        }
        if (extension is ".html" or ".htm")
        {
            var encoding = await DetectHtmlEncodingAsync(stream, token);
            using var input = new StreamReader(stream, encoding, true, 65536, leaveOpen: true);
            foreach (var passage in ReadHtml(input, token)) yield return new(passage); yield break;
        }
        using var plain = new StreamReader(stream, Encoding.UTF8, true, 65536, leaveOpen: true);
        var chars = new char[ChunkSize]; string carry = ""; int read;
        while ((read = await plain.ReadAsync(chars, token)) > 0)
        {
            string value = carry + new string(chars, 0, read);
            if (!string.IsNullOrWhiteSpace(value)) yield return new(value); carry = value.Length > 300 ? value[^300..] : "";
        }
    }

    private static XmlReaderSettings XmlOptions(int limit = MaximumXmlCharacters) => new()
    { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreComments = true, IgnoreProcessingInstructions = true, CloseInput = false, MaxCharactersInDocument = limit, MaxCharactersFromEntities = 1024 };
    private static void ValidateZip(ZipArchive zip, CancellationToken token)
    {
        if (zip.Entries.Count > 100000) throw new InvalidDataException("This document contains too many package parts.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in zip.Entries)
        {
            token.ThrowIfCancellationRequested(); if (!seen.Add(entry.FullName)) throw new InvalidDataException("This document contains duplicate package parts.");
            if ((entry.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) || entry.FullName.EndsWith(".rels", StringComparison.OrdinalIgnoreCase)) &&
                (entry.Length > MaximumXmlCharacters || entry.Length > 1024 * 1024 && entry.Length / Math.Max(1, entry.CompressedLength) > 1000)) throw new InvalidDataException("A document XML part exceeds the 64 MiB or decompression safety limit.");
        }
    }
    private static ZipArchiveEntry RequiredPart(ZipArchive zip, string name) => zip.GetEntry(name) ?? throw new InvalidDataException("This document is missing package part " + name + ".");
    private static string Attribute(XmlReader reader, string localName)
    {
        if (!reader.HasAttributes) return ""; string value = "";
        while (reader.MoveToNextAttribute()) if (reader.LocalName == localName) { value = reader.Value; break; }
        reader.MoveToElement(); return value;
    }
    private static string ReadTextElement(XmlReader parent, CancellationToken token, int limit = MaximumCellCharacters)
    {
        // ReadSubtree leaves the parent on its end element so the next Read visits the adjacent sibling.
        using var reader = parent.ReadSubtree(); var value = new StringBuilder();
        while (reader.Read())
        {
            token.ThrowIfCancellationRequested();
            if (reader.NodeType is XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.SignificantWhitespace or XmlNodeType.Whitespace) AppendBounded(value, reader.Value, limit);
            else if (reader.NodeType == XmlNodeType.Element && reader.LocalName is "tab" or "br" or "line-break") AppendBounded(value, " ", limit);
        }
        return value.ToString();
    }
    private static void AppendBounded(StringBuilder target, string value, int limit)
    { if (target.Length > limit - value.Length) throw new InvalidDataException("A document text element exceeds the extraction safety limit."); target.Append(value); }
    private static IEnumerable<DocumentPassage> PackBlocks(IEnumerable<(string Text, string Locator, int? Page)> blocks, CancellationToken token)
    {
        var buffer = new StringBuilder(); string first = "", last = ""; int? page = null;
        foreach (var block in blocks)
        {
            token.ThrowIfCancellationRequested(); if (string.IsNullOrWhiteSpace(block.Text)) continue;
            foreach (string piece in Chunk(block.Text))
            {
                if (buffer.Length > 0 && (page != block.Page || buffer.Length + piece.Length > ChunkSize)) { yield return Located(buffer.ToString(), first, last, page); buffer.Clear(); }
                if (buffer.Length == 0) { first = block.Locator; page = block.Page; } last = block.Locator; buffer.Append(piece).Append('\n');
            }
        }
        if (buffer.Length > 0) yield return Located(buffer.ToString(), first, last, page);
    }
    private static DocumentPassage Located(string text, string first, string last, int? page) => new($"[{first}{(first == last ? "" : " – " + last)}]\n{text.TrimEnd()}", page);
    internal static IEnumerable<string> Chunk(string text)
    { for (int start = 0; start < text.Length; start += 3600) { int length = Math.Min(ChunkSize, text.Length - start); yield return text.Substring(start, length); if (start + length == text.Length) break; } }

    private static IEnumerable<DocumentPassage> ReadWord(ZipArchive zip, CancellationToken token)
    { using var stream = RequiredPart(zip, "word/document.xml").Open(); foreach (var passage in PackBlocks(WordBlocks(stream, token), token)) yield return passage; }
    private static IEnumerable<(string Text, string Locator, int? Page)> WordBlocks(Stream stream, CancellationToken token)
    {
        using var reader = XmlReader.Create(stream, XmlOptions()); int paragraph = 0;
        while (reader.Read())
        {
            token.ThrowIfCancellationRequested(); if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "p") continue;
            paragraph++; using var subtree = reader.ReadSubtree(); var value = new StringBuilder();
            while (subtree.Read())
            {
                token.ThrowIfCancellationRequested(); if (subtree.NodeType != XmlNodeType.Element) continue;
                if (subtree.LocalName == "t") AppendBounded(value, ReadTextElement(subtree, token), MaximumCellCharacters);
                else if (subtree.LocalName is "tab" or "br" or "cr") AppendBounded(value, " ", MaximumCellCharacters);
            }
            yield return (value.ToString(), $"Paragraph {paragraph}", null);
        }
    }
    private static Dictionary<string, string> Relationships(ZipArchive zip, string part, CancellationToken token)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        string folder = part[..(part.LastIndexOf('/') + 1)], name = part[(part.LastIndexOf('/') + 1)..];
        if (zip.GetEntry(folder + "_rels/" + name + ".rels") is not { } entry) return result;
        using var stream = entry.Open(); using var reader = XmlReader.Create(stream, XmlOptions(MaximumMetadataCharacters));
        while (reader.Read())
        {
            token.ThrowIfCancellationRequested(); if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "Relationship" || reader.GetAttribute("TargetMode") == "External") continue;
            string id = reader.GetAttribute("Id") ?? "", target = reader.GetAttribute("Target") ?? "";
            if (id.Length == 0 || target.Length == 0 || target.Contains('\\') || target.Contains(':') || target.Contains('#') || target.Contains('?')) continue;
            var uri = new Uri(new Uri("https://document.invalid/" + part), target); if (uri.Host != "document.invalid") continue;
            if (!result.TryAdd(id, Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/')))) throw new InvalidDataException("This document has duplicate relationship identifiers.");
        }
        return result;
    }
    private static IEnumerable<DocumentPassage> ReadPresentation(ZipArchive zip, CancellationToken token)
    {
        var slides = new List<string>();
        if (zip.GetEntry("ppt/presentation.xml") is { } presentation)
        {
            var relationships = Relationships(zip, presentation.FullName, token);
            using var stream = presentation.Open(); using var reader = XmlReader.Create(stream, XmlOptions(MaximumMetadataCharacters));
            while (reader.Read()) { token.ThrowIfCancellationRequested(); if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "sldId" && relationships.TryGetValue(RelationshipId(reader), out string? target)) slides.Add(target); }
        }
        if (slides.Count == 0) slides.AddRange(zip.Entries.Where(e => Regex.IsMatch(e.FullName, @"^ppt/slides/slide\d+\.xml$", RegexOptions.CultureInvariant)).OrderBy(e => NumericPart(e.FullName)).Select(e => e.FullName));
        for (int i = 0; i < slides.Count; i++) { using var stream = RequiredPart(zip, slides[i]).Open(); foreach (var passage in PackBlocks(DrawingBlocks(stream, i + 1, token), token)) yield return passage; }
    }
    private static IEnumerable<(string Text, string Locator, int? Page)> DrawingBlocks(Stream stream, int page, CancellationToken token)
    {
        using var reader = XmlReader.Create(stream, XmlOptions());
        while (reader.Read())
        {
            token.ThrowIfCancellationRequested(); if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "p") continue;
            using var paragraph = reader.ReadSubtree(); var value = new StringBuilder();
            while (paragraph.Read())
            {
                token.ThrowIfCancellationRequested();
                if (paragraph.NodeType == XmlNodeType.Element && paragraph.LocalName == "t") AppendBounded(value, ReadTextElement(paragraph, token), MaximumCellCharacters);
                else if (paragraph.NodeType == XmlNodeType.Element && paragraph.LocalName == "br") AppendBounded(value, " ", MaximumCellCharacters);
            }
            yield return (value.ToString(), $"Slide {page}", page);
        }
    }
    private static long NumericPart(string path) => long.TryParse(Regex.Match(Path.GetFileName(path), @"\d+").Value, NumberStyles.None, CultureInfo.InvariantCulture, out long value) ? value : long.MaxValue;
    private static string RelationshipId(XmlReader reader) => reader.GetAttribute("id", "http://schemas.openxmlformats.org/officeDocument/2006/relationships") ?? reader.GetAttribute("id", "http://purl.oclc.org/ooxml/officeDocument/relationships") ?? "";

    private static List<string> SharedStrings(ZipArchive zip, CancellationToken token)
    {
        var strings = new List<string>(); long total = 0;
        if (zip.GetEntry("xl/sharedStrings.xml") is not { } entry) return strings;
        using var stream = entry.Open(); using var reader = XmlReader.Create(stream, XmlOptions());
        while (reader.Read())
        {
            token.ThrowIfCancellationRequested(); if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "si") continue;
            using var item = reader.ReadSubtree(); var value = new StringBuilder();
            while (item.Read())
            {
                token.ThrowIfCancellationRequested();
                if (item.NodeType == XmlNodeType.Element && item.LocalName == "rPh") { using var phonetic = item.ReadSubtree(); while (phonetic.Read()) token.ThrowIfCancellationRequested(); }
                else if (item.NodeType == XmlNodeType.Element && item.LocalName == "t") AppendBounded(value, ReadTextElement(item, token), MaximumCellCharacters);
            }
            total += value.Length;
            if (total > MaximumXmlCharacters || strings.Count >= 2000000) throw new InvalidDataException("The spreadsheet shared-string table exceeds the extraction safety limit.");
            strings.Add(value.ToString());
        }
        return strings;
    }
    private static IEnumerable<DocumentPassage> ReadWorkbook(ZipArchive zip, CancellationToken token)
    {
        var shared = SharedStrings(zip, token); var sheets = new List<(string Path, string Name)>();
        if (zip.GetEntry("xl/workbook.xml") is { } workbook)
        {
            var relationships = Relationships(zip, workbook.FullName, token);
            using var stream = workbook.Open(); using var reader = XmlReader.Create(stream, XmlOptions(MaximumMetadataCharacters));
            while (reader.Read())
            {
                token.ThrowIfCancellationRequested();
                if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "sheet" && relationships.TryGetValue(RelationshipId(reader), out string? target)) sheets.Add((target, reader.GetAttribute("name") ?? "Sheet"));
            }
        }
        if (sheets.Count == 0) sheets.AddRange(zip.Entries.Where(e => Regex.IsMatch(e.FullName, @"^xl/worksheets/sheet\d+\.xml$", RegexOptions.CultureInvariant)).OrderBy(e => NumericPart(e.FullName)).Select((e, i) => (e.FullName, "Sheet " + (i + 1))));
        for (int i = 0; i < sheets.Count; i++)
        {
            using var stream = RequiredPart(zip, sheets[i].Path).Open(); using var reader = XmlReader.Create(stream, XmlOptions());
            var row = new StringBuilder(); string first = "", last = ""; bool meaningful = false; int rowNumber = 0, column = 0;
            while (reader.Read())
            {
                token.ThrowIfCancellationRequested();
                if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "row")
                {
                    string rowReference = reader.GetAttribute("r") ?? "";
                    if (rowReference.Length > 0 && (!int.TryParse(rowReference, NumberStyles.None, CultureInfo.InvariantCulture, out rowNumber) || rowNumber is < 1 or > 1048576)) throw new InvalidDataException("A spreadsheet row reference is invalid.");
                    if (rowReference.Length == 0) rowNumber++; column = 0;
                }
                if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "c")
                {
                    string reference = reader.GetAttribute("r") ?? "", type = reader.GetAttribute("t") ?? "n";
                    if (reference.Length == 0) reference = ColumnName(column) + Math.Max(1, rowNumber);
                    if (!Regex.IsMatch(reference, "^[A-Za-z]{1,3}[1-9][0-9]{0,6}$", RegexOptions.CultureInvariant)) throw new InvalidDataException("A spreadsheet cell reference is invalid.");
                    int letters = 0; column = 0;
                    while (letters < reference.Length && char.IsAsciiLetter(reference[letters])) { column = checked(column * 26 + char.ToUpperInvariant(reference[letters]) - 'A' + 1); letters++; }
                    if (column > 16384 || !int.TryParse(reference.AsSpan(letters), NumberStyles.None, CultureInfo.InvariantCulture, out int cellRow) || cellRow > 1048576) throw new InvalidDataException("A spreadsheet cell exceeds its format limits.");
                    string value = ReadCell(reader, type, shared, token); if (string.IsNullOrWhiteSpace(value)) continue;
                    foreach (string piece in Chunk(value))
                    {
                        if (row.Length > 0 && row.Length + piece.Length + reference.Length > ChunkSize)
                        { yield return SheetPassage(sheets[i].Name, first, last, row.ToString(), i + 1, meaningful); row.Clear(); meaningful = false; }
                        if (row.Length == 0) first = reference;
                        last = reference; row.Append(reference).Append(": ").Append(piece).Append('\n');
                        meaningful |= type is not ("n" or "b" or "e" or "d") && HasMeaningfulWords(piece);
                    }
                }
                // Numeric cells are retained for exact search in bounded groups, without one embedding per number.
                if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName == "row" && row.Length >= 2800)
                { yield return SheetPassage(sheets[i].Name, first, last, row.ToString(), i + 1, meaningful); row.Clear(); meaningful = false; }
            }
            if (row.Length > 0) yield return SheetPassage(sheets[i].Name, first, last, row.ToString(), i + 1, meaningful);
        }
    }
    private static string ReadCell(XmlReader parent, string type, IReadOnlyList<string> shared, CancellationToken token)
    {
        using var cell = parent.ReadSubtree(); var inline = new StringBuilder(); string value = "";
        while (cell.Read())
        {
            token.ThrowIfCancellationRequested(); if (cell.NodeType != XmlNodeType.Element) continue;
            if (cell.LocalName == "v") value = ReadTextElement(cell, token);
            else if (cell.LocalName == "t") AppendBounded(inline, ReadTextElement(cell, token), MaximumCellCharacters);
            else if (cell.LocalName == "rPh") { using var ignored = cell.ReadSubtree(); while (ignored.Read()) token.ThrowIfCancellationRequested(); }
        }
        if (type == "s")
        {
            if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int index) || index < 0 || index >= shared.Count) throw new InvalidDataException("A spreadsheet cell has an invalid shared-string reference.");
            return shared[index];
        }
        return type == "inlineStr" ? inline.ToString() : value;
    }
    private static DocumentPassage SheetPassage(string sheet, string first, string last, string value, int page, bool meaningful) =>
        new($"[Worksheet '{sheet.Replace("'", "''", StringComparison.Ordinal)}'!{first}{(first == last ? "" : ":" + last)}]\n{value.TrimEnd()}", page, meaningful);
    private static bool HasMeaningfulWords(string value)
    {
        int run = 0;
        foreach (char character in value) { if (char.IsLetter(character)) { if (++run >= 2) return true; } else run = 0; } return false;
    }

    private static IEnumerable<DocumentPassage> ReadOpenDocument(ZipArchive zip, string extension, CancellationToken token)
    {
        using var stream = RequiredPart(zip, "content.xml").Open();
        if (extension == ".ods") { foreach (var passage in OpenSpreadsheet(stream, token)) yield return passage; yield break; }
        foreach (var passage in PackBlocks(OpenBlocks(stream, extension, token), token)) yield return passage;
    }
    private static IEnumerable<(string Text, string Locator, int? Page)> OpenBlocks(Stream stream, string extension, CancellationToken token)
    {
        using var reader = XmlReader.Create(stream, XmlOptions()); int page = 0, paragraph = 0;
        while (reader.Read())
        {
            token.ThrowIfCancellationRequested(); if (reader.NodeType != XmlNodeType.Element) continue;
            if (reader.LocalName == "page" && extension == ".odp") page++;
            if (reader.LocalName is "p" or "h")
            { paragraph++; yield return (ReadOpenParagraph(reader, token), extension == ".odp" ? $"Slide {Math.Max(1, page)}" : $"Paragraph {paragraph}", extension == ".odp" ? Math.Max(1, page) : null); }
        }
    }
    private static string ReadOpenParagraph(XmlReader parent, CancellationToken token)
    {
        using var reader = parent.ReadSubtree(); var value = new StringBuilder();
        while (reader.Read())
        {
            token.ThrowIfCancellationRequested();
            if (reader.NodeType is XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.SignificantWhitespace or XmlNodeType.Whitespace) AppendBounded(value, reader.Value, MaximumCellCharacters);
            else if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "s")
            { int count = int.TryParse(Attribute(reader, "c"), out int n) ? Math.Clamp(n, 1, 1000) : 1; AppendBounded(value, new string(' ', count), MaximumCellCharacters); }
            else if (reader.NodeType == XmlNodeType.Element && reader.LocalName is "tab" or "line-break") AppendBounded(value, " ", MaximumCellCharacters);
        }
        return value.ToString();
    }
    private static IEnumerable<DocumentPassage> OpenSpreadsheet(Stream stream, CancellationToken token)
    {
        using var reader = XmlReader.Create(stream, XmlOptions()); int sheet = 0, rowNumber = 0; string sheetName = "Sheet";
        while (reader.Read())
        {
            token.ThrowIfCancellationRequested(); if (reader.NodeType != XmlNodeType.Element) continue;
            if (reader.LocalName == "table") { sheet++; rowNumber = 0; sheetName = Attribute(reader, "name"); }
            else if (reader.LocalName == "table-row")
            {
                int repeats = BoundedRepeat(Attribute(reader, "number-rows-repeated"), 1048576), thisRow = checked(rowNumber + 1); rowNumber = checked(rowNumber + repeats);
                if (rowNumber > 1048576) throw new InvalidDataException("A spreadsheet row exceeds its format limits.");
                using var row = reader.ReadSubtree(); int column = 0; var value = new StringBuilder(); string first = "", last = ""; bool meaningful = false;
                while (row.Read())
                {
                    token.ThrowIfCancellationRequested(); if (row.NodeType != XmlNodeType.Element || row.LocalName is not ("table-cell" or "covered-table-cell")) continue;
                    int columns = BoundedRepeat(Attribute(row, "number-columns-repeated"), 16384); string reference = ColumnName(column) + thisRow; column = checked(column + columns);
                    if (column > 16384) throw new InvalidDataException("A spreadsheet column exceeds its format limits.");
                    string valueType = Attribute(row, "value-type"), raw = Attribute(row, "string-value"); if (raw.Length == 0) raw = Attribute(row, "value"); if (raw.Length == 0) raw = Attribute(row, "date-value"); if (raw.Length == 0) raw = Attribute(row, "boolean-value");
                    using var cell = row.ReadSubtree(); var text = new StringBuilder();
                    while (cell.Read()) { token.ThrowIfCancellationRequested(); if (cell.NodeType == XmlNodeType.Element && cell.LocalName == "p") { if (text.Length > 0) text.Append(' '); AppendBounded(text, ReadOpenParagraph(cell, token), MaximumCellCharacters); } }
                    string contents = text.Length > 0 ? text.ToString() : raw; if (string.IsNullOrWhiteSpace(contents)) continue;
                    if (columns > 1 || repeats > 1) reference += ":" + ColumnName(column - 1) + (thisRow + repeats - 1);
                    foreach (string piece in Chunk(contents))
                    {
                        if (value.Length > 0 && value.Length + piece.Length > ChunkSize) { yield return SheetPassage(sheetName, first, last, value.ToString(), Math.Max(1, sheet), meaningful); value.Clear(); meaningful = false; }
                        if (value.Length == 0) first = reference; last = reference; value.Append(reference).Append(": ").Append(piece).Append('\n'); meaningful |= valueType is not ("float" or "currency" or "percentage" or "date" or "time" or "boolean") && HasMeaningfulWords(piece);
                    }
                }
                if (value.Length > 0) yield return SheetPassage(sheetName, first, last, value.ToString(), Math.Max(1, sheet), meaningful);
            }
        }
    }
    private static int BoundedRepeat(string text, int maximum) => int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int value) && value > 0 && value <= maximum ? value : text.Length == 0 ? 1 : throw new InvalidDataException("A spreadsheet repeat count exceeds its format limits.");
    private static string ColumnName(int column)
    {
        if (column is < 0 or >= 16384) throw new InvalidDataException("A spreadsheet column exceeds its format limits.");
        string result = ""; for (int value = column + 1; value > 0; value = (value - 1) / 26) result = (char)('A' + (value - 1) % 26) + result; return result;
    }

    private static async Task<Encoding> DetectHtmlEncodingAsync(FileStream stream, CancellationToken token)
    {
        var prefix = new byte[8192]; int read = await stream.ReadAsync(prefix, token); stream.Position = 0;
        string ascii = Encoding.Latin1.GetString(prefix, 0, read);
        var charset = Regex.Match(ascii, @"charset\s*=\s*['""\s]*([A-Za-z0-9._-]{1,40})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (charset.Success) try { return Encoding.GetEncoding(charset.Groups[1].Value); } catch (ArgumentException) { }
        return Encoding.UTF8;
    }
    private static IEnumerable<string> ReadHtml(TextReader reader, CancellationToken token)
    {
        var text = new StringBuilder(); long count = 0;
        while (reader.Read() is int value && value >= 0)
        {
            token.ThrowIfCancellationRequested(); if (++count > MaximumXmlCharacters) throw new InvalidDataException("HTML input exceeds the 64 MiB text extraction limit."); char character = (char)value;
            if (character == '<')
            {
                int lookahead = reader.Peek();
                if (lookahead < 0 || !char.IsAsciiLetter((char)lookahead) && lookahead is not ('/' or '!' or '?')) { text.Append('<'); continue; }
                if (reader.Peek() == '!') { reader.Read(); SkipDeclaration(reader, token); if (text.Length > 0) text.Append(' '); continue; }
                string trimmed = ReadHtmlTag(reader, token).Trim(); bool closing = trimmed.StartsWith('/');
                string name = new string(trimmed.TrimStart('/').TakeWhile(c => char.IsAsciiLetterOrDigit(c) || c == '-').ToArray()).ToLowerInvariant();
                if (!closing && name is "script" or "style" or "template" or "noscript") SkipRawHtml(reader, name, token);
                if (HtmlBlocks.Contains(name) && text.Length > 0) text.Append('\n');
            }
            else if (character == '&')
            {
                // Entity tokens stay whole before decoding, including across StreamReader buffers.
                text.Append(character);
                for (int i = 0; i < 64 && reader.Peek() >= 0; i++) { char next = (char)reader.Peek(); if (char.IsWhiteSpace(next) || next is '<' or '&') break; text.Append((char)reader.Read()); if (next == ';') break; }
            }
            else if (text.Length > 0 || !char.IsWhiteSpace(character)) text.Append(character);
            if (text.Length >= ChunkSize && (char.IsWhiteSpace(character) || text.Length >= ChunkSize + 128))
            {
                string raw = text.ToString(), decoded = System.Net.WebUtility.HtmlDecode(raw);
                if (!string.IsNullOrWhiteSpace(decoded)) yield return decoded;
                int carryStart = Math.Max(0, raw.Length - 300);
                while (carryStart > 0 && !char.IsWhiteSpace(raw[carryStart - 1]) && raw.Length - carryStart < 500) carryStart--;
                text.Clear(); if (!string.IsNullOrWhiteSpace(decoded)) text.Append(raw.AsSpan(carryStart));
            }
        }
        if (text.Length > 0) { string decoded = System.Net.WebUtility.HtmlDecode(text.ToString()); if (!string.IsNullOrWhiteSpace(decoded)) yield return decoded; }
    }
    private static string ReadHtmlTag(TextReader reader, CancellationToken token)
    {
        var tag = new StringBuilder(); char quote = '\0';
        while (reader.Read() is int value && value >= 0)
        {
            token.ThrowIfCancellationRequested(); char character = (char)value;
            if (quote == '\0' && character == '>') return tag.ToString();
            if (quote == '\0' && character is '\'' or '"') quote = character; else if (character == quote) quote = '\0';
            if (tag.Length >= 65536) throw new InvalidDataException("An HTML tag exceeds the extraction safety limit."); tag.Append(character);
        }
        return tag.ToString();
    }
    private static void SkipDeclaration(TextReader reader, CancellationToken token)
    {
        if (reader.Peek() != '-') { ReadHtmlTag(reader, token); return; }
        reader.Read(); if (reader.Peek() != '-') { ReadHtmlTag(reader, token); return; } reader.Read();
        int previous = 0, beforePrevious = 0; long length = 0;
        while (reader.Read() is int next && next >= 0)
        { token.ThrowIfCancellationRequested(); if (++length > MaximumXmlCharacters) throw new InvalidDataException("An HTML comment exceeds the extraction safety limit."); if (beforePrevious == '-' && previous == '-' && next == '>') return; beforePrevious = previous; previous = next; }
    }
    private static void SkipRawHtml(TextReader reader, string name, CancellationToken token)
    {
        string end = "</" + name; int matched = 0; long length = 0;
        while (reader.Read() is int value && value >= 0)
        {
            token.ThrowIfCancellationRequested(); if (++length > MaximumXmlCharacters) throw new InvalidDataException("An HTML hidden-text block exceeds the extraction safety limit."); char character = char.ToLowerInvariant((char)value);
            matched = character == end[matched] ? matched + 1 : character == '<' ? 1 : 0;
            if (matched != end.Length) continue;
            int next = reader.Peek(); if (next == '>' || next == '/' || next >= 0 && char.IsWhiteSpace((char)next)) { ReadHtmlTag(reader, token); return; } matched = 0;
        }
    }

    private sealed record RtfState(bool Skip = false, int UnicodeFallback = 1, int CodePage = 1252, bool Hidden = false);
    private static IEnumerable<string> ReadRtf(TextReader source, CancellationToken token)
    {
        char[] header = new char[5]; if (source.ReadBlock(header, 0, header.Length) != header.Length || new string(header) != "{\\rtf") throw new InvalidDataException("The RTF document header is invalid.");
        var reader = new PushbackTextReader(source); var stack = new Stack<RtfState>(); stack.Push(new()); var state = new RtfState(); var text = new StringBuilder(); int fallback = 0; long count = 5;
        if (reader.Read() != '1') throw new InvalidDataException("The RTF version is unsupported.");
        if (reader.Peek() == ' ') reader.Read();
        while (reader.Read() is int value && value >= 0)
        {
            token.ThrowIfCancellationRequested(); if (++count > MaximumXmlCharacters) throw new InvalidDataException("RTF input exceeds the 64 MiB extraction limit."); char character = (char)value;
            if (character == '{') { if (stack.Count >= 256) throw new InvalidDataException("RTF nesting exceeds the extraction safety limit."); stack.Push(state); fallback = 0; }
            else if (character == '}') { if (stack.Count == 0) throw new InvalidDataException("The RTF group structure is invalid."); state = stack.Pop(); fallback = 0; if (stack.Count == 0) break; }
            else if (character == '\\')
            {
                int next = reader.Read(); if (next < 0) break; count++;
                if (next is '\\' or '{' or '}') Emit((char)next);
                else if (next == '*') state = state with { Skip = true };
                else if (next == '\'')
                {
                    var bytes = new List<byte>();
                    while (true)
                    {
                        int a = reader.Read(), b = reader.Read(); count += 2;
                        if (a < 0 || b < 0 || !byte.TryParse(string.Concat((char)a, (char)b), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte decoded)) throw new InvalidDataException("An RTF hexadecimal escape is invalid.");
                        if (fallback > 0) fallback--; else if (!state.Skip && !state.Hidden) { if (bytes.Count >= MaximumCellCharacters) throw new InvalidDataException("An RTF text run exceeds the extraction safety limit."); bytes.Add(decoded); }
                        if (reader.Peek() != '\\') break;
                        reader.Read(); count++; if (reader.Peek() == '\'') { reader.Read(); count++; continue; }
                        reader.Push('\\'); break;
                    }
                    if (bytes.Count > 0) text.Append(RtfEncoding(state.CodePage).GetString(bytes.ToArray()));
                }
                else if (!char.IsAsciiLetter((char)next))
                { if (next == '~') Emit('\u00a0'); else if (next == '_') Emit('\u2011'); }
                else
                {
                    var word = new StringBuilder().Append((char)next);
                    while (reader.Peek() >= 0 && char.IsAsciiLetter((char)reader.Peek())) { if (word.Length >= 64) throw new InvalidDataException("An RTF control word is too long."); word.Append((char)reader.Read()); count++; }
                    bool negative = reader.Peek() == '-'; if (negative) { reader.Read(); count++; }
                    var digits = new StringBuilder();
                    while (reader.Peek() >= '0' && reader.Peek() <= '9') { if (digits.Length >= 10) throw new InvalidDataException("An RTF control parameter is too long."); digits.Append((char)reader.Read()); count++; }
                    int? parameter = digits.Length > 0 && int.TryParse((negative ? "-" : "") + digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number) ? number : null;
                    if (digits.Length > 0 && parameter is null) throw new InvalidDataException("An RTF control parameter is invalid.");
                    if (reader.Peek() == ' ') { reader.Read(); count++; }
                    string control = word.ToString();
                    if (RtfDestinations.Contains(control)) state = state with { Skip = true };
                    else if (control == "ansicpg" && parameter is > 0) state = state with { CodePage = parameter.Value };
                    else if (control == "uc") { if (parameter is not (>= 0 and <= 16)) throw new InvalidDataException("An RTF Unicode fallback count is invalid."); state = state with { UnicodeFallback = parameter.Value }; }
                    else if (control == "u") { if (parameter is not (>= -32768 and <= 65535)) throw new InvalidDataException("An RTF Unicode code unit is invalid."); if (!state.Skip && !state.Hidden) text.Append(unchecked((char)(short)parameter.Value)); fallback = state.UnicodeFallback; }
                    else if (control == "v") state = state with { Hidden = parameter != 0 };
                    else if (control == "plain") state = state with { Hidden = false };
                    else if (control == "bin" && parameter is not null)
                    { if (parameter is < 0 or > MaximumXmlCharacters) throw new InvalidDataException("An RTF binary block is too large."); for (int i = 0; i < parameter.Value; i++) { token.ThrowIfCancellationRequested(); if (reader.Read() < 0) throw new InvalidDataException("An RTF binary block is truncated."); } count += parameter.Value; }
                    else if (control is "par" or "line" or "row") Emit('\n'); else if (control is "tab" or "cell") Emit('\t');
                    else if (control == "emdash") Emit('\u2014'); else if (control == "endash") Emit('\u2013'); else if (control == "bullet") Emit('\u2022');
                    else if (control == "lquote") Emit('\u2018'); else if (control == "rquote") Emit('\u2019'); else if (control == "ldblquote") Emit('\u201c'); else if (control == "rdblquote") Emit('\u201d');
                }
            }
            else if (character is not ('\r' or '\n'))
            {
                if (character > 127) { if (fallback > 0) fallback--; else if (!state.Skip && !state.Hidden) text.Append(RtfEncoding(state.CodePage).GetString([(byte)character])); }
                else Emit(character);
            }
            if (text.Length >= ChunkSize) { if (!string.IsNullOrWhiteSpace(text.ToString())) yield return text.ToString(); text.Clear(); }
        }
        if (stack.Count != 0) throw new InvalidDataException("The RTF document has an unclosed group.");
        if (text.Length > 0 && !string.IsNullOrWhiteSpace(text.ToString())) yield return text.ToString();
        void Emit(char character) { if (fallback > 0) fallback--; else if (!state.Skip && !state.Hidden) text.Append(character); }
    }
    private static Encoding RtfEncoding(int codePage)
    { try { return Encoding.GetEncoding(codePage); } catch (ArgumentException error) { throw new InvalidDataException("The RTF code page is unsupported.", error); } }
    private sealed class PushbackTextReader(TextReader inner) : TextReader
    {
        private int pending = -1;
        public void Push(char character) { if (pending >= 0) throw new InvalidOperationException("A parser lookahead is already pending."); pending = character; }
        public override int Peek() => pending >= 0 ? pending : inner.Peek();
        public override int Read() { if (pending < 0) return inner.Read(); int result = pending; pending = -1; return result; }
    }
}
