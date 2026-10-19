using MigraDoc.DocumentObjectModel;
using MigraDoc.Rendering;
using PdfSharp.Fonts;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using MD = MigraDoc.DocumentObjectModel;

namespace Orange.Core.Office;

/// <summary>
/// PDFsharp 6 needs fonts supplied explicitly. Uses the PC's own fonts (Segoe UI / Arial on Windows, DejaVu or
/// Liberation on Linux), never downloads any. Complex scripts (Devanagari, Telugu) render without shaping.
/// </summary>
public sealed class OrangeFontResolver : IFontResolver
{
    private static readonly object Gate = new();
    private static bool _installed;
    private readonly Dictionary<string, string> _files = new();

    public static void Install()
    {
        lock (Gate)
        {
            if (_installed) return;
            if (GlobalFontSettings.FontResolver is null) GlobalFontSettings.FontResolver = new OrangeFontResolver();
            _installed = true;
        }
    }

    private OrangeFontResolver()
    {
        var win = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts");
        (string, string[])[] faces =
        [
            ("R", [Path.Combine(win, "segoeui.ttf"), Path.Combine(win, "arial.ttf"), "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf", "/usr/share/fonts/truetype/liberation/LiberationSans-Regular.ttf"]),
            ("B", [Path.Combine(win, "segoeuib.ttf"), Path.Combine(win, "arialbd.ttf"), "/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf", "/usr/share/fonts/truetype/liberation/LiberationSans-Bold.ttf"]),
            ("I", [Path.Combine(win, "segoeuii.ttf"), Path.Combine(win, "ariali.ttf"), "/usr/share/fonts/truetype/dejavu/DejaVuSans-Oblique.ttf", "/usr/share/fonts/truetype/liberation/LiberationSans-Italic.ttf"]),
            ("BI", [Path.Combine(win, "segoeuiz.ttf"), Path.Combine(win, "arialbi.ttf"), "/usr/share/fonts/truetype/dejavu/DejaVuSans-BoldOblique.ttf", "/usr/share/fonts/truetype/liberation/LiberationSans-BoldItalic.ttf"]),
        ];
        foreach (var (key, candidates) in faces)
            if (candidates.FirstOrDefault(File.Exists) is { } f) _files[key] = f;
        if (!_files.ContainsKey("R"))
        {   // Any TTF at all beats failing.
            var any = new[] { win, "/usr/share/fonts" }.Where(Directory.Exists).SelectMany(d => Directory.EnumerateFiles(d, "*.ttf", SearchOption.AllDirectories)).FirstOrDefault();
            if (any != null) _files["R"] = any;
        }
    }

    public FontResolverInfo? ResolveTypeface(string familyName, bool bold, bool italic)
    {
        var key = bold && italic ? "BI" : bold ? "B" : italic ? "I" : "R";
        if (!_files.ContainsKey(key)) key = bold && _files.ContainsKey("B") ? "B" : "R";
        return new FontResolverInfo("orange-" + key, bold && key is "R" or "I", italic && key is "R" or "B");
    }

    public byte[]? GetFont(string faceName)
    {
        var key = faceName.Replace("orange-", "");
        if (!_files.TryGetValue(key, out var f) && !_files.TryGetValue("R", out f))
            throw new OrangeException("No usable font was found on this PC to write the PDF.");
        return File.ReadAllBytes(f);
    }
}

public static class PdfIO
{
    /// <summary>Renders a DocModel to an A4 PDF with MigraDoc (simple, readable layout).</summary>
    public static void Write(DocModel d, string path)
    {
        OrangeFontResolver.Install();
        var doc = new Document();
        doc.Info.Title = d.Title; doc.Info.Author = "Orange";
        var normal = doc.Styles[StyleNames.Normal]!;
        normal.Font.Name = "Orange"; normal.Font.Size = 11;
        normal.ParagraphFormat.SpaceAfter = 6;
        for (int i = 1; i <= 6; i++)
        {
            var h = doc.Styles[$"Heading{i}"]!;
            h.Font.Size = new[] { 20, 16, 14, 13, 12, 11 }[i - 1]; h.Font.Bold = true;
            h.ParagraphFormat.SpaceBefore = 10; h.ParagraphFormat.SpaceAfter = 4; h.ParagraphFormat.KeepWithNext = true;
        }
        var sec = doc.AddSection();
        sec.PageSetup = doc.DefaultPageSetup.Clone();
        sec.PageSetup.PageFormat = PageFormat.A4;
        sec.PageSetup.LeftMargin = sec.PageSetup.RightMargin = Unit.FromCentimeter(2);
        sec.PageSetup.TopMargin = sec.PageSetup.BottomMargin = Unit.FromCentimeter(2);
        double usable = 21 - 4;   // cm
        var tmpImages = new List<string>();
        try
        {
            foreach (var b in d.Blocks)
            {
                switch (b)
                {
                    case Heading h: sec.AddParagraph(h.Text, $"Heading{Math.Clamp(h.Level, 1, 6)}"); break;
                    case Para p: AddRuns(sec.AddParagraph(), p.Runs); break;
                    case ListItem li:
                        var lp = sec.AddParagraph();
                        lp.Format.LeftIndent = Unit.FromCentimeter(0.6 * (li.Level + 1));
                        lp.Format.FirstLineIndent = Unit.FromCentimeter(-0.4);
                        lp.AddText(li.Ordered ? "• " : "• ");
                        AddRuns(lp, li.Runs);
                        break;
                    case Table t when t.Rows.Count > 0:
                        int cols = Math.Min(t.Rows.Max(r => r.Count), 30);
                        var tbl = sec.AddTable();
                        tbl.Borders.Width = 0.5; tbl.Format.Font.Size = cols > 8 ? 7 : 9;
                        for (int c = 0; c < cols; c++) tbl.AddColumn(Unit.FromCentimeter(usable / cols));
                        foreach (var r in t.Rows.Take(20000))
                        {
                            var row = tbl.AddRow();
                            for (int c = 0; c < cols && c < r.Count; c++) row.Cells[c].AddParagraph(r[c]);
                        }
                        sec.AddParagraph();
                        break;
                    case Picture pic:
                        var f = Path.Combine(Path.GetTempPath(), "Orange", Guid.NewGuid().ToString("N") + "." + pic.Extension);
                        Directory.CreateDirectory(Path.GetDirectoryName(f)!);
                        File.WriteAllBytes(f, pic.Data); tmpImages.Add(f);
                        var img = sec.AddParagraph().AddImage(f);
                        img.LockAspectRatio = true;
                        try
                        {
                            var info = new ImageMagick.MagickImageInfo(pic.Data);
                            var wcm = info.Width / 96.0 * 2.54;
                            img.Width = Unit.FromCentimeter(Math.Min(usable, wcm));
                        }
                        catch { img.Width = Unit.FromCentimeter(usable); }
                        break;
                    case PageBreak: sec.AddPageBreak(); break;
                }
            }
            if (d.Blocks.Count == 0) sec.AddParagraph(" ");
            var renderer = new PdfDocumentRenderer { Document = doc };
            renderer.RenderDocument();
            renderer.PdfDocument.Save(path);
        }
        finally { foreach (var f in tmpImages) try { File.Delete(f); } catch { } }
    }

    private static void AddRuns(MD.Paragraph p, IEnumerable<Run> runs)
    {
        foreach (var r in runs)
        {
            var parts = r.Text.Split('\n');
            for (int i = 0; i < parts.Length; i++)
            {
                if (i > 0) p.AddLineBreak();
                if (parts[i].Length == 0) continue;
                var ft = p.AddFormattedText(parts[i]);
                ft.Bold = r.Bold; ft.Italic = r.Italic;
            }
        }
    }

    /// <summary>PDF → DocModel: text grouped into paragraphs by line gaps, big lines as headings, embedded pictures kept.</summary>
    public static DocModel Read(string path, int maxPages = 2000)
    {
        var d = new DocModel { Title = Path.GetFileNameWithoutExtension(path) };
        PdfDocument pdf;
        try { pdf = PdfDocument.Open(path); }
        catch (Exception e) { throw new OrangeException("This PDF looks damaged and can't be read.", e.Message); }
        using (pdf)
        {
            int count = Math.Min(pdf.NumberOfPages, maxPages);
            for (int n = 1; n <= count; n++)
            {
                var page = pdf.GetPage(n);
                var words = page.GetWords().ToList();
                var lines = words.GroupBy(w => Math.Round(w.BoundingBox.Bottom / 2.0)).OrderByDescending(g => g.Key)
                    .Select(g => (Y: g.Key * 2, Size: g.Average(w => w.Letters.Count > 0 ? w.Letters.Average(l => l.PointSize) : 10), Text: string.Join(' ', g.OrderBy(w => w.BoundingBox.Left).Select(w => w.Text))))
                    .ToList();
                double body = lines.Count > 0 ? lines.Select(l => l.Size).OrderBy(s => s).ElementAt(lines.Count / 2) : 10;
                var para = new System.Text.StringBuilder(); double lastY = double.NaN;
                void Flush() { if (para.Length > 0) { d.Add(Para.Of(para.ToString().Trim())); para.Clear(); } }
                foreach (var l in lines)
                {
                    if (l.Size > body * 1.3 && l.Text.Length < 120) { Flush(); d.Add(new Heading(l.Size > body * 1.7 ? 1 : 2, l.Text)); lastY = l.Y; continue; }
                    if (!double.IsNaN(lastY) && lastY - l.Y > body * 1.9) Flush();
                    para.Append(l.Text).Append(' ');
                    lastY = l.Y;
                }
                Flush();
                foreach (var img in page.GetImages().Take(50))
                {
                    try
                    {
                        if (img.TryGetPng(out var png)) d.Add(new Picture(png, "png"));
                        else if (img.RawBytes.ToArray() is { Length: > 2 } raw && raw[0] == 0xFF && raw[1] == 0xD8) d.Add(new Picture(raw, "jpg"));
                    }
                    catch { /* unsupported image encoding: skip */ }
                }
                if (n < count) d.Add(new PageBreak());
            }
        }
        return d;
    }

    /// <summary>Renders PDF pages to PNG bytes with PDFium (bundled, BSD); no Ghostscript needed.</summary>
    public static IEnumerable<byte[]> RenderPages(string path, int dpi = 110, int maxPages = 500)
    {
        var bytes = File.ReadAllBytes(path);
        int pages = PDFtoImage.Conversion.GetPageCount(bytes);
        for (int i = 0; i < Math.Min(pages, maxPages); i++)
        {
            using var ms = new MemoryStream();
            PDFtoImage.Conversion.SavePng(ms, bytes, page: i, options: new PDFtoImage.RenderOptions(Dpi: dpi, WithAnnotations: true, WithFormFill: true));
            yield return ms.ToArray();
        }
    }
}
