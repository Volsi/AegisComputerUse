using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using AegisComputerUse.Core.Security;

namespace AegisComputerUse.Core.Files;

public sealed record CreatedFileResult(
    string FullPath,
    string RelativePath,
    string Extension,
    long SizeBytes);

public sealed class UniversalFileCreator
{
    private readonly SafeFileSandbox _sandbox;

    public UniversalFileCreator(SafeFileSandbox sandbox)
    {
        _sandbox = sandbox ?? throw new ArgumentNullException(nameof(sandbox));
    }

    public CreatedFileResult CreateFile(string relativePath, string content, string? encoding = null)
    {
        string resolvedCheck = _sandbox.ResolveSafePath(relativePath);
        string ext = Path.GetExtension(resolvedCheck).ToLowerInvariant();
        string safeContent = content ?? string.Empty;

        byte[] bytes = ext switch
        {
            ".docx" => BuildDocx(safeContent),
            ".xlsx" => BuildXlsx(safeContent),
            ".pdf" => BuildPdf(safeContent),
            ".rtf" => Encoding.ASCII.GetBytes(BuildRtf(safeContent)),
            ".png" or ".bmp" => BuildRasterImage(safeContent, ext, encoding),
            _ => BuildTextOrDataBytes(safeContent, ext, encoding)
        };

        string fullPath = _sandbox.WriteBytesSafe(relativePath, bytes);
        return new CreatedFileResult(fullPath, relativePath, ext, bytes.LongLength);
    }

    private static byte[] BuildTextOrDataBytes(string content, string ext, string? encoding)
    {
        if (string.Equals(encoding, "base64", StringComparison.OrdinalIgnoreCase))
        {
            return Convert.FromBase64String(content.Trim());
        }

        if (ext == ".json" && !string.IsNullOrWhiteSpace(content))
        {
            using var doc = JsonDocument.Parse(content, new JsonDocumentOptions { MaxDepth = 32 });
            using var ms = new MemoryStream();
            using var writer = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true });
            doc.WriteTo(writer);
            writer.Flush();
            return ms.ToArray();
        }

        return Encoding.UTF8.GetBytes(content);
    }

    private static byte[] BuildDocx(string content)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            AddXmlEntry(zip, "[Content_Types].xml",
                new XDocument(
                    new XElement(XName.Get("Types", "http://schemas.openxmlformats.org/package/2006/content-types"),
                        new XElement(XName.Get("Default", "http://schemas.openxmlformats.org/package/2006/content-types"),
                            new XAttribute("Extension", "rels"),
                            new XAttribute("ContentType", "application/vnd.openxmlformats-package.relationships+xml")),
                        new XElement(XName.Get("Default", "http://schemas.openxmlformats.org/package/2006/content-types"),
                            new XAttribute("Extension", "xml"),
                            new XAttribute("ContentType", "application/xml")),
                        new XElement(XName.Get("Override", "http://schemas.openxmlformats.org/package/2006/content-types"),
                            new XAttribute("PartName", "/word/document.xml"),
                            new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"))
                    )));

            AddXmlEntry(zip, "_rels/.rels",
                new XDocument(
                    new XElement(XName.Get("Relationships", "http://schemas.openxmlformats.org/package/2006/relationships"),
                        new XElement(XName.Get("Relationship", "http://schemas.openxmlformats.org/package/2006/relationships"),
                            new XAttribute("Id", "rId1"),
                            new XAttribute("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument"),
                            new XAttribute("Target", "word/document.xml"))
                    )));

            XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
            var body = new XElement(w + "body");
            foreach (string rawLine in content.Replace("\r\n", "\n").Split('\n'))
            {
                string line = StripInvalidXmlChars(rawLine);
                bool isHeading = line.StartsWith("# ", StringComparison.Ordinal) || line.StartsWith("## ", StringComparison.Ordinal);
                string text = isHeading ? line.TrimStart('#', ' ') : line;

                var rPr = isHeading
                    ? new XElement(w + "rPr", new XElement(w + "b"), new XElement(w + "sz", new XAttribute(w + "val", "28")))
                    : new XElement(w + "rPr", new XElement(w + "sz", new XAttribute(w + "val", "22")));

                body.Add(new XElement(w + "p",
                    new XElement(w + "r",
                        rPr,
                        new XElement(w + "t", new XAttribute(XNamespace.Xml + "space", "preserve"), text))));
            }
            body.Add(new XElement(w + "sectPr"));

            AddXmlEntry(zip, "word/document.xml",
                new XDocument(new XElement(w + "document", new XAttribute(XNamespace.Xmlns + "w", w), body)));
        }

        return ms.ToArray();
    }

    private static byte[] BuildXlsx(string content)
    {
        List<List<string>> rows = ParseTableData(content);

        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            XNamespace ct = "http://schemas.openxmlformats.org/package/2006/content-types";
            AddXmlEntry(zip, "[Content_Types].xml",
                new XDocument(new XElement(ct + "Types",
                    new XElement(ct + "Default", new XAttribute("Extension", "rels"),
                        new XAttribute("ContentType", "application/vnd.openxmlformats-package.relationships+xml")),
                    new XElement(ct + "Default", new XAttribute("Extension", "xml"),
                        new XAttribute("ContentType", "application/xml")),
                    new XElement(ct + "Override", new XAttribute("PartName", "/xl/workbook.xml"),
                        new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml")),
                    new XElement(ct + "Override", new XAttribute("PartName", "/xl/worksheets/sheet1.xml"),
                        new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml")))));

            XNamespace rel = "http://schemas.openxmlformats.org/package/2006/relationships";
            AddXmlEntry(zip, "_rels/.rels",
                new XDocument(new XElement(rel + "Relationships",
                    new XElement(rel + "Relationship",
                        new XAttribute("Id", "rId1"),
                        new XAttribute("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument"),
                        new XAttribute("Target", "xl/workbook.xml")))));

            AddXmlEntry(zip, "xl/_rels/workbook.xml.rels",
                new XDocument(new XElement(rel + "Relationships",
                    new XElement(rel + "Relationship",
                        new XAttribute("Id", "rId1"),
                        new XAttribute("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet"),
                        new XAttribute("Target", "worksheets/sheet1.xml")))));

            XNamespace main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            XNamespace r = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
            AddXmlEntry(zip, "xl/workbook.xml",
                new XDocument(new XElement(main + "workbook",
                    new XAttribute(XNamespace.Xmlns + "r", r),
                    new XElement(main + "sheets",
                        new XElement(main + "sheet",
                            new XAttribute("name", "Sheet1"),
                            new XAttribute("sheetId", "1"),
                            new XAttribute(r + "id", "rId1"))))));

            var sheetData = new XElement(main + "sheetData");
            for (int rIdx = 0; rIdx < rows.Count; rIdx++)
            {
                var rowEl = new XElement(main + "row", new XAttribute("r", rIdx + 1));
                for (int cIdx = 0; cIdx < rows[rIdx].Count; cIdx++)
                {
                    string colRef = ToExcelColumnName(cIdx + 1) + (rIdx + 1);
                    string cellText = StripInvalidXmlChars(rows[rIdx][cIdx]);

                    // Neutralize Excel formula cells: never emit <f>; keep safe numeric or inline string
                    if (double.TryParse(cellText, NumberStyles.Float, CultureInfo.InvariantCulture, out double num) &&
                        !double.IsNaN(num) && !double.IsInfinity(num) &&
                        !cellText.StartsWith('+') && !cellText.StartsWith('0'))
                    {
                        rowEl.Add(new XElement(main + "c",
                            new XAttribute("r", colRef),
                            new XAttribute("t", "n"),
                            new XElement(main + "v", num.ToString(CultureInfo.InvariantCulture))));
                    }
                    else
                    {
                        string safeText = cellText.Length > 0 && cellText[0] is '=' or '+' or '@'
                            ? "'" + cellText
                            : cellText;
                        rowEl.Add(new XElement(main + "c",
                            new XAttribute("r", colRef),
                            new XAttribute("t", "inlineStr"),
                            new XElement(main + "is", new XElement(main + "t", safeText))));
                    }
                }
                sheetData.Add(rowEl);
            }

            AddXmlEntry(zip, "xl/worksheets/sheet1.xml",
                new XDocument(new XElement(main + "worksheet", sheetData)));
        }

        return ms.ToArray();
    }

    private static byte[] BuildPdf(string content)
    {
        List<byte[]> pageJpegs = RunOnStaThread(() => RenderTextPagesAsJpeg(content));
        return AssemblePdfFromJpegPages(pageJpegs, 794, 1123);
    }

    private static List<byte[]> RenderTextPagesAsJpeg(string content)
    {
        const int pageW = 794;
        const int pageH = 1123;
        const int margin = 52;
        const double maxTextWidth = pageW - margin * 2;
        const double maxTextHeight = pageH - margin * 2;

        var typeface = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var pages = new List<byte[]>();

        string[] paragraphs = (string.IsNullOrEmpty(content) ? " " : content.Replace("\r\n", "\n")).Split('\n');
        var currentLines = new List<string>();
        double currentHeight = 0;

        void FlushPage()
        {
            var visual = new DrawingVisual();
            using (DrawingContext dc = visual.RenderOpen())
            {
                dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, pageW, pageH));
                double y = margin;
                foreach (string line in currentLines)
                {
                    var ft = new FormattedText(
                        line.Length == 0 ? " " : line,
                        CultureInfo.CurrentCulture,
                        FlowDirection.LeftToRight,
                        typeface,
                        13,
                        Brushes.Black,
                        1.0)
                    {
                        MaxTextWidth = maxTextWidth
                    };
                    dc.DrawText(ft, new Point(margin, y));
                    y += Math.Max(18, ft.Height + 4);
                }
            }

            var rtb = new RenderTargetBitmap(pageW, pageH, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(visual);
            var encoder = new JpegBitmapEncoder { QualityLevel = 88 };
            encoder.Frames.Add(BitmapFrame.Create(rtb));
            using var ms = new MemoryStream();
            encoder.Save(ms);
            pages.Add(ms.ToArray());
            currentLines.Clear();
            currentHeight = 0;
        }

        foreach (string p in paragraphs)
        {
            var ft = new FormattedText(
                p.Length == 0 ? " " : p,
                CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                typeface,
                13,
                Brushes.Black,
                1.0)
            {
                MaxTextWidth = maxTextWidth
            };
            double h = Math.Max(18, ft.Height + 4);
            if (currentHeight + h > maxTextHeight && currentLines.Count > 0)
            {
                FlushPage();
            }
            currentLines.Add(p);
            currentHeight += h;
        }

        if (currentLines.Count > 0 || pages.Count == 0)
        {
            FlushPage();
        }

        return pages;
    }

    private static byte[] AssemblePdfFromJpegPages(List<byte[]> pageJpegs, int widthPx, int heightPx)
    {
        // Convert pixel canvas to standard A4 points (595 x 842)
        const int ptW = 595;
        const int ptH = 842;

        using var ms = new MemoryStream();
        var offsets = new List<long> { 0 };

        void WriteAscii(string s)
        {
            byte[] b = Encoding.ASCII.GetBytes(s);
            ms.Write(b, 0, b.Length);
        }

        WriteAscii("%PDF-1.4\n%\xE2\xE3\xCF\xD3\n");

        int pageCount = pageJpegs.Count;
        // Object layout:
        // 1: Catalog
        // 2: Pages
        // For each page i (0..pageCount-1):
        //   base = 3 + i * 3
        //   base + 0: Page object
        //   base + 1: Content stream
        //   base + 2: Image XObject

        offsets.Add(ms.Position);
        WriteAscii("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

        var kids = string.Join(" ", Enumerable.Range(0, pageCount).Select(i => $"{3 + i * 3} 0 R"));
        offsets.Add(ms.Position);
        WriteAscii($"2 0 obj\n<< /Type /Pages /Kids [ {kids} ] /Count {pageCount} >>\nendobj\n");

        for (int i = 0; i < pageCount; i++)
        {
            int pageObj = 3 + i * 3;
            int contentObj = pageObj + 1;
            int imgObj = pageObj + 2;
            byte[] jpg = pageJpegs[i];
            string contentStream = $"q\n{ptW} 0 0 {ptH} 0 0 cm\n/Im{i} Do\nQ\n";
            byte[] contentBytes = Encoding.ASCII.GetBytes(contentStream);

            offsets.Add(ms.Position);
            WriteAscii($"{pageObj} 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {ptW} {ptH}] /Resources << /XObject << /Im{i} {imgObj} 0 R >> >> /Contents {contentObj} 0 R >>\nendobj\n");

            offsets.Add(ms.Position);
            WriteAscii($"{contentObj} 0 obj\n<< /Length {contentBytes.Length} >>\nstream\n");
            ms.Write(contentBytes, 0, contentBytes.Length);
            WriteAscii("\nendstream\nendobj\n");

            offsets.Add(ms.Position);
            WriteAscii($"{imgObj} 0 obj\n<< /Type /XObject /Subtype /Image /Width {widthPx} /Height {heightPx} /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /Length {jpg.Length} >>\nstream\n");
            ms.Write(jpg, 0, jpg.Length);
            WriteAscii("\nendstream\nendobj\n");
        }

        long xrefPos = ms.Position;
        WriteAscii($"xref\n0 {offsets.Count}\n");
        WriteAscii("0000000000 65535 f \n");
        for (int i = 1; i < offsets.Count; i++)
        {
            WriteAscii($"{offsets[i]:D10} 00000 n \n");
        }

        WriteAscii($"trailer\n<< /Size {offsets.Count} /Root 1 0 R >>\nstartxref\n{xrefPos}\n%%EOF\n");
        return ms.ToArray();
    }

    private static byte[] BuildRasterImage(string content, string ext, string? encoding)
    {
        if (string.Equals(encoding, "base64", StringComparison.OrdinalIgnoreCase))
        {
            byte[] raw = Convert.FromBase64String(content.Trim());
            return RunOnStaThread(() =>
            {
                using var inStream = new MemoryStream(raw);
                var decoder = BitmapDecoder.Create(inStream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                BitmapEncoder encoder = ext == ".bmp" ? new BmpBitmapEncoder() : new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(decoder.Frames[0]));
                using var outStream = new MemoryStream();
                encoder.Save(outStream);
                return outStream.ToArray();
            });
        }

        return RunOnStaThread(() =>
        {
            const int w = 960;
            const int h = 540;
            var visual = new DrawingVisual();
            using (DrawingContext dc = visual.RenderOpen())
            {
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(18, 24, 34)), null, new Rect(0, 0, w, h));
                var ft = new FormattedText(
                    string.IsNullOrEmpty(content) ? "Aegis Generated Image" : content,
                    CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight,
                    new Typeface("Segoe UI"),
                    18,
                    Brushes.White,
                    1.0)
                {
                    MaxTextWidth = w - 64,
                    MaxTextHeight = h - 64
                };
                dc.DrawText(ft, new Point(32, 32));
            }

            var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(visual);
            BitmapEncoder encoder = ext == ".bmp" ? new BmpBitmapEncoder() : new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(rtb));
            using var ms = new MemoryStream();
            encoder.Save(ms);
            return ms.ToArray();
        });
    }

    private static string BuildRtf(string content)
    {
        var sb = new StringBuilder();
        sb.Append(@"{\rtf1\ansi\ansicpg1251\deff0{\fonttbl{\f0 Segoe UI;}}\uc1\pard\fs22 ");
        foreach (char ch in content)
        {
            if (ch == '\r') continue;
            if (ch == '\n') { sb.Append(@"\par "); continue; }
            if (ch is '\\' or '{' or '}') { sb.Append('\\').Append(ch); continue; }
            if (ch < 128) sb.Append(ch);
            else sb.Append(@"\u").Append((short)ch).Append('?');
        }
        sb.Append('}');
        return sb.ToString();
    }

    private static List<List<string>> ParseTableData(string content)
    {
        string trimmed = content.Trim();
        if (trimmed.StartsWith('['))
        {
            try
            {
                using var doc = JsonDocument.Parse(trimmed, new JsonDocumentOptions { MaxDepth = 16 });
                if (doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0)
                {
                    var headers = new List<string>();
                    var rows = new List<List<string>>();
                    foreach (var el in doc.RootElement.EnumerateArray())
                    {
                        if (el.ValueKind == JsonValueKind.Object)
                        {
                            foreach (var prop in el.EnumerateObject())
                            {
                                if (!headers.Contains(prop.Name)) headers.Add(prop.Name);
                            }
                        }
                    }

                    if (headers.Count > 0)
                    {
                        rows.Add(headers);
                        foreach (var el in doc.RootElement.EnumerateArray())
                        {
                            if (el.ValueKind != JsonValueKind.Object) continue;
                            var row = new List<string>(headers.Count);
                            foreach (string h in headers)
                            {
                                row.Add(el.TryGetProperty(h, out var v) ? v.ToString() : string.Empty);
                            }
                            rows.Add(row);
                        }
                        return rows;
                    }
                }
            }
            catch
            {
                // Fall back to delimited parsing
            }
        }

        var result = new List<List<string>>();
        foreach (string line in content.Replace("\r\n", "\n").Split('\n'))
        {
            if (string.IsNullOrEmpty(line)) continue;
            char delim = line.Contains('\t') ? '\t' : (line.Contains(';') && !line.Contains(',') ? ';' : ',');
            result.Add(ParseDelimitedLine(line, delim));
        }

        if (result.Count == 0) result.Add([string.Empty]);
        return result;
    }

    private static List<string> ParseDelimitedLine(string line, char delimiter)
    {
        var cells = new List<string>();
        var current = new StringBuilder();
        bool inQuotes = false;

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }
            }
            else if (c == delimiter && !inQuotes)
            {
                cells.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }
        cells.Add(current.ToString());
        return cells;
    }

    private static void AddXmlEntry(ZipArchive zip, string entryName, XDocument doc)
    {
        var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        doc.Save(writer, SaveOptions.DisableFormatting);
    }

    private static string StripInvalidXmlChars(string input)
    {
        if (string.IsNullOrEmpty(input)) return string.Empty;
        var sb = new StringBuilder(input.Length);
        foreach (char c in input)
        {
            if (c == '\t' || c == '\n' || c == '\r' || (c >= 0x20 && c <= 0xD7FF) || (c >= 0xE000 && c <= 0xFFFD))
                sb.Append(c);
        }
        return sb.ToString();
    }

    private static string ToExcelColumnName(int colNumber)
    {
        var sb = new StringBuilder();
        while (colNumber > 0)
        {
            int mod = (colNumber - 1) % 26;
            sb.Insert(0, (char)('A' + mod));
            colNumber = (colNumber - mod) / 26;
        }
        return sb.ToString();
    }

    private static T RunOnStaThread<T>(Func<T> func)
    {
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
            return func();

        T? result = default;
        Exception? captured = null;
        var t = new Thread(() =>
        {
            try { result = func(); }
            catch (Exception ex) { captured = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (captured is not null) throw new InvalidOperationException("STA rendering failed.", captured);
        return result!;
    }
}
