using Orange.Core.Office;

namespace Orange.Core.Providers;

/// <summary>
/// Office documents without LibreOffice: DOCX/Markdown/TXT/HTML ↔ each other and to PDF, spreadsheets
/// (XLSX/XLS/ODS/CSV/TSV) ↔ each other and to PDF, PPTX → PDF/PNG, PDF → DOCX/PPTX. All in-process, no network.
/// Layout is simplified ("structure, not pixels"); LibreOffice stays the route for legacy DOC/PPT and exact layout.
/// Registered before the Pandoc/LibreOffice provider so the native route wins when both offer the same format.
/// </summary>
public sealed class OfficeProvider : IOperationProvider
{
    public string Name => "Office (built in)";

    // Orange's own document engine (no Pandoc, no LibreOffice): every reader feeds DocModel, every writer reads it.
    private static readonly string[] DocIn = ["docx", "odt", "rtf", "md", "txt", "html", "epub", "doc"];
    private static readonly (string Ext, string Title, string[] From)[] DocTargets =
    [
        ("pdf", "PDF", DocIn),
        ("docx", "DOCX", DocIn),
        ("odt", "ODT", DocIn),
        ("rtf", "RTF", DocIn),
        ("html", "HTML", DocIn),
        ("md", "Markdown", DocIn),
        ("txt", "TXT", DocIn),
        ("epub", "EPUB", DocIn),
    ];
    private static readonly HashSet<string> SlidesIn = ["pptx", "ppt"];
    private static readonly string[] SheetIn = ["xlsx", "xls", "ods", "csv", "tsv"];
    private static readonly (string Ext, string Title)[] SheetOut = [("xlsx", "XLSX"), ("ods", "ODS"), ("csv", "CSV"), ("tsv", "TSV"), ("pdf", "PDF")];

    public IEnumerable<OperationDescriptor> Describe()
    {
        foreach (var t in DocTargets)
            yield return new OperationDescriptor
            {
                Id = $"doc.native.{t.Ext}", Title = t.Title, Category = FileKind.Document, Group = OperationGroup.Convert,
                InputExtensions = t.From.ToHashSet(), OutputExtension = t.Ext, Keywords = [t.Ext, "convert", "document", "word", "office"],
            };
        foreach (var t in SheetOut)
            yield return new OperationDescriptor
            {
                Id = $"sheet.to.{t.Ext}", Title = t.Title, Category = FileKind.Spreadsheet, Group = OperationGroup.Convert,
                InputExtensions = SheetIn.ToHashSet(), OutputExtension = t.Ext, Keywords = [t.Ext, "excel", "spreadsheet", "table", "convert"],
            };
        yield return new OperationDescriptor
        {
            Id = "slides.to.pdf", Title = "PDF", Category = FileKind.Presentation, Group = OperationGroup.Convert, InputExtensions = SlidesIn,
            OutputExtension = "pdf", Keywords = ["powerpoint", "slides", "pdf", "handout"],
        };
        yield return new OperationDescriptor
        {
            Id = "slides.to.png", Title = "PNG", Category = FileKind.Presentation, Group = OperationGroup.Convert, InputExtensions = SlidesIn,
            OutputExtension = "png", ProducesFolder = true, Keywords = ["powerpoint", "slides", "images", "pictures"],
        };
        yield return new OperationDescriptor
        {
            Id = "slides.to.pptx", Title = "PPTX", Category = FileKind.Presentation, Group = OperationGroup.Convert, InputExtensions = new HashSet<string> { "ppt" },
            OutputExtension = "pptx", Keywords = ["powerpoint", "upgrade", "legacy", "pptx"],
        };
        yield return new OperationDescriptor
        {
            Id = "pdf.to.docx", Title = "DOCX", Category = FileKind.Pdf, Group = OperationGroup.Convert, InputExtensions = new HashSet<string> { "pdf" },
            OutputExtension = "docx", Keywords = ["word", "editable", "docx"],
        };
        yield return new OperationDescriptor
        {
            Id = "pdf.to.pptx", Title = "PPTX", Category = FileKind.Pdf, Group = OperationGroup.Convert, InputExtensions = new HashSet<string> { "pdf" },
            OutputExtension = "pptx", Keywords = ["powerpoint", "slides", "presentation"],
        };
    }

    public bool IsAvailable(OperationDescriptor op) => true;

    public async Task<OperationResult> ExecuteAsync(OperationRequest r, IProgress<double>? progress, CancellationToken ct)
    {
        var op = r.Operation;
        var input = r.Inputs[0];
        var stem = Path.GetFileNameWithoutExtension(input);
        var src = OperationRegistry.Normalize(FileTypeDetector.Ext(input));
        using var tmp = new SafePaths.TempWorkspace();
        progress?.Report(0.05);
        await Task.Yield();
        ct.ThrowIfCancellationRequested();

        if (op.Id == "slides.to.png")
        {
            var pdf = tmp.File("pdf");
            SlidesToPdf(input, src, stem, pdf);
            var dir = tmp.Dir("slides");
            int n = 0;
            foreach (var png in PdfIO.RenderPages(pdf, dpi: 144))
            {
                ct.ThrowIfCancellationRequested();
                await File.WriteAllBytesAsync(Path.Combine(dir, $"{stem} slide {++n:000}.png"), png, ct);
                progress?.Report(0.3 + 0.6 * Math.Min(1, n / 50.0));
            }
            var folder = SafePaths.PlanOutput(input, r.OutputDirectory, stem, "slides", "", r.Overwrite, r.Inputs, isFolder: true);
            return OperationResult.Ok(SafePaths.CommitFolder(dir, folder));
        }

        var outExt = op.OutputExtension!;
        var o = tmp.File(outExt);
        switch (op.Id)
        {
            case var id when id.StartsWith("doc.native."):
            {
                var model = await ReadDocAsync(input, src, stem, ct);
                progress?.Report(0.5);
                await WriteDocAsync(model, o, outExt, tmp, ct);
                break;
            }
            case var id when id.StartsWith("sheet.to."):
            {
                var sheets = Sheets.Read(input);
                progress?.Report(0.5);
                if (sheets.Count == 0) throw new OrangeException("The spreadsheet has no sheets.");
                Sheets.Write(sheets, o);
                if (outExt is "csv" or "tsv" && sheets.Count > 1)
                {   // One file per extra sheet would surprise; keep the first and say so in the name.
                    var final1 = SafePaths.PlanOutput(input, r.OutputDirectory, stem, SafePaths.SanitizeFileName(sheets[0].Name), outExt, r.Overwrite, r.Inputs);
                    progress?.Report(1);
                    return OperationResult.Ok(SafePaths.Commit(o, final1, r.Overwrite));
                }
                break;
            }
            case "slides.to.pdf": SlidesToPdf(input, src, stem, o); break;
            case "slides.to.pptx":
            {
                var pdf = tmp.File("pdf");
                SlidesToPdf(input, src, stem, pdf);
                var pages = new List<byte[]>();
                foreach (var png in PdfIO.RenderPages(pdf, dpi: 150, maxPages: 1000)) { ct.ThrowIfCancellationRequested(); pages.Add(png); }
                Slides.PicturesToPptx(pages, o);
                break;
            }
            case "pdf.to.docx": DocxIO.Write(PdfIO.Read(input), o); break;
            case "pdf.to.pptx":
            {
                var pages = new List<byte[]>();
                foreach (var png in PdfIO.RenderPages(input, dpi: 150, maxPages: 300)) { ct.ThrowIfCancellationRequested(); pages.Add(png); progress?.Report(0.1 + 0.7 * Math.Min(1, pages.Count / 50.0)); }
                Slides.PicturesToPptx(pages, o);
                break;
            }
            default: throw new OrangeException($"Unknown operation {op.Id}.");
        }
        Validate(o, outExt);
        var final = SafePaths.PlanOutput(input, r.OutputDirectory, stem, null, outExt, r.Overwrite, r.Inputs);
        progress?.Report(1);
        return OperationResult.Ok(SafePaths.Commit(o, final, r.Overwrite));
    }

    private static async Task<DocModel> ReadDocAsync(string input, string src, string stem, CancellationToken ct) => src switch
    {
        "docx" => DocxIO.Read(input),
        "odt" => OdtIO.Read(input),
        "rtf" => RtfIO.Read(input),
        "doc" => LegacyDoc.Read(input),
        "epub" => Ebooks.ReadEpub(input),
        "md" => TextFormats.FromMarkdown(await ReadTextAsync(input, ct), stem),
        "html" => TextFormats.FromHtml(await ReadTextAsync(input, ct), stem),
        "txt" => TextFormats.FromText(await ReadTextAsync(input, ct), stem),
        _ => throw new OrangeException($"Orange can't read .{src} files natively."),
    };

    /// <summary>PPTX through the slide renderer; legacy PPT through Orange's own record reader (text and structure).</summary>
    private static void SlidesToPdf(string input, string src, string stem, string pdf)
    {
        if (src == "ppt") PdfIO.Write(LegacyPpt.ToModel(LegacyPpt.Read(input), stem), pdf);
        else Slides.PptxToPdf(input, pdf);
    }

    private static async Task<string> ReadTextAsync(string path, CancellationToken ct)
    {
        if (new FileInfo(path).Length > 200_000_000) throw new OrangeException("The text file is larger than 200 MB.");
        return await File.ReadAllTextAsync(path, ct);
    }

    private static async Task WriteDocAsync(DocModel m, string o, string ext, SafePaths.TempWorkspace tmp, CancellationToken ct)
    {
        var utf8 = new System.Text.UTF8Encoding(false);
        switch (ext)
        {
            case "pdf": PdfIO.Write(m, o); break;
            case "docx": DocxIO.Write(m, o); break;
            case "html": await File.WriteAllTextAsync(o, TextFormats.ToHtml(m), utf8, ct); break;
            case "md": await File.WriteAllTextAsync(o, TextFormats.ToMarkdown(m), utf8, ct); break;
            case "txt": await File.WriteAllTextAsync(o, TextFormats.ToText(m), utf8, ct); break;
            case "odt": OdtIO.Write(m, o); break;
            case "rtf": RtfIO.Write(m, o); break;
            case "epub": Ebooks.WriteEpub(m, o); break;
        }
    }

    /// <summary>Re-open what was written so a broken file never reaches the user.</summary>
    private static void Validate(string path, string ext)
    {
        try
        {
            switch (ext)
            {
                case "pdf": using (var d = UglyToad.PdfPig.PdfDocument.Open(path)) if (d.NumberOfPages < 1) throw new Exception("no pages"); break;
                case "docx": using (var d = DocumentFormat.OpenXml.Packaging.WordprocessingDocument.Open(path, false)) _ = d.MainDocumentPart!.Document.Body; break;
                case "xlsx": using (var d = DocumentFormat.OpenXml.Packaging.SpreadsheetDocument.Open(path, false)) _ = d.WorkbookPart!.Workbook; break;
                case "pptx": using (var d = DocumentFormat.OpenXml.Packaging.PresentationDocument.Open(path, false)) _ = d.PresentationPart!.Presentation; break;
                case "ods": Sheets.ReadOds(path); break;
                case "odt": OdtIO.Read(path); break;
                case "rtf": RtfIO.Read(path); break;
                case "epub": Ebooks.ReadEpub(path); break;
            }
        }
        catch (Exception e) { throw new OrangeException("The converted file didn't validate, so it wasn't saved.", e.Message); }
    }
}
