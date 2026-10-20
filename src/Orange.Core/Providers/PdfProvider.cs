using ImageMagick;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace Orange.Core.Providers;

/// <summary>
/// PDF work. Structure edits (merge, split, rotate, extract pages, images→PDF) use PDFsharp (MIT, managed).
/// Text and embedded-image extraction use PdfPig (Apache-2.0, managed). Rasterising and compression use
/// Ghostscript (AGPL, separate optional process, always with -dSAFER) when it's installed.
/// </summary>
public sealed class PdfProvider(ToolLocator tools) : IOperationProvider
{
    public string Name => "PDF (PDFsharp, PdfPig, Ghostscript)";
    private static readonly HashSet<string> Pdf = ["pdf"];
    private static OptionSpec Pages => new("pages", "Pages", OptionType.Text, "all", Help: "\"all\", or ranges like 1-3,5");

    public IEnumerable<OperationDescriptor> Describe()
    {
        yield return Conv("pdf.to.txt", "TXT", "txt", null, ["text", "extract text", "plain"]);
        yield return Conv("pdf.to.md", "Markdown", "md", null, ["markdown", "text"]);
        yield return Conv("pdf.to.png", "PNG", "png", null, ["image", "pages to images", "render"], folder: true,
            [new("dpi", "Resolution (DPI)", OptionType.Choice, "150", Choices: ["72", "150", "300"]), Pages]);
        yield return Conv("pdf.to.jpg", "JPG", "jpg", null, ["image", "jpeg", "pages to images"], folder: true,
            [new("dpi", "Resolution (DPI)", OptionType.Choice, "150", Choices: ["72", "150", "300"]), Pages]);

        yield return T("pdf.merge", "Merge PDFs", "Merged", "merge", [], ["combine", "join"], combine: true);
        yield return T("pdf.split", "Split", "Pages", "split", [], ["separate", "pages", "burst"], folder: true);
        yield return T("pdf.extract-pages", "Extract pages", "Extract", "pages", [Pages with { Default = "1" }], ["select", "pick pages", "subset"], single: true);
        yield return T("pdf.rotate", "Rotate", "Rotated", "rotate",
            [new("angle", "Rotate", OptionType.Choice, "90", Choices: ["90", "180", "270"]), Pages], ["turn", "orientation", "landscape"]);
        yield return T("pdf.reorder", "Reorder pages", "Reordered", "reorder",
            [new("order", "New page order", OptionType.Text, "", Help: "Every page you want, in the new order, e.g. 3,1,2 or 5-1 to reverse. Pages left out are dropped.")],
            ["rearrange", "sort pages", "move pages", "reverse"], single: true);
        yield return T("pdf.compress", "Compress", "Compressed", "compress",
            [new("level", "Compression", OptionType.Choice, "ebook", Choices: ["printer", "ebook", "screen"], Help: "screen is smallest; printer keeps the most quality."),
             new("targetMB", "Target size (MB, 0 = off)", OptionType.Number, "0", 0, 100000, Help: "Orange tries printer → ebook → screen → 72 dpi and keeps the best one that fits.")],
            ["smaller", "reduce", "optimize", "size"]);   // Ghostscript when installed, built-in picture re-encoding otherwise
        yield return T("pdf.extract-images", "Extract images", "Images", "images", [], ["pictures", "photos", "embedded"], folder: true);
        yield return T("pdf.strip-metadata", "Remove metadata", "Clean", "metadata", [], ["author", "privacy", "properties"]);
    }

    private static OperationDescriptor Conv(string id, string title, string ext, string? tool, string[] kw, bool folder = false, OptionSpec[]? opts = null) => new()
    {
        Id = id, Title = title, Category = FileKind.Pdf, Group = OperationGroup.Convert, InputExtensions = Pdf, OutputExtension = ext,
        RequiresTool = tool, Keywords = kw.Append(ext).ToArray(), ProducesFolder = folder, Options = opts ?? [],
    };
    private static OperationDescriptor T(string id, string title, string suffix, string icon, OptionSpec[] opts, string[] kw,
        bool combine = false, bool folder = false, bool single = false, string? tool = null) => new()
    {
        Id = id, Title = title, Category = FileKind.Pdf, Group = OperationGroup.Tool, InputExtensions = Pdf, Options = opts, Icon = icon,
        CombinesInputs = combine, MinInputs = combine ? 2 : 1, ProducesFolder = folder, RequiresTool = tool,
        Keywords = kw.Append("suffix:" + suffix).Concat(single ? ["single-only"] : Array.Empty<string>()).ToArray(),
    };

    public bool IsAvailable(OperationDescriptor op) => op.RequiresTool is null || tools.Has(op.RequiresTool);

    public Task<OperationResult> ExecuteAsync(OperationRequest r, IProgress<double>? progress, CancellationToken ct) => Task.Run(async () =>
    {
        var op = r.Operation;
        var input = r.Inputs[0];
        var stem = Path.GetFileNameWithoutExtension(input);
        var suffix = op.Group == OperationGroup.Tool ? ImageProvider.SuffixOf(op) : null;
        using var tmp = new SafePaths.TempWorkspace();
        progress?.Report(0.05);

        string CommitFile(string produced, string ext, string? name = null)
        {
            var final = SafePaths.PlanOutput(input, r.OutputDirectory, name ?? stem, suffix, ext, r.Overwrite, r.Inputs);
            progress?.Report(1);
            return SafePaths.Commit(produced, final, r.Overwrite);
        }
        string CommitDir(string dir, string? folderName = null)
        {
            var final = SafePaths.PlanOutput(input, r.OutputDirectory, folderName ?? stem, folderName is null ? suffix ?? op.Title : null, "", r.Overwrite, r.Inputs, isFolder: true);
            progress?.Report(1);
            return SafePaths.CommitFolder(dir, final);
        }

        switch (op.Id)
        {
            case "pdf.merge":
            {
                using var outDoc = new PdfDocument();
                for (int i = 0; i < r.Inputs.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    using var src = Open(r.Inputs[i], PdfDocumentOpenMode.Import);
                    foreach (var page in src.Pages) outDoc.AddPage(page);
                    progress?.Report(0.1 + 0.8 * (i + 1) / r.Inputs.Count);
                }
                var o = tmp.File("pdf"); outDoc.Save(o); ValidatePdf(o);
                return OperationResult.Ok(CommitFile(o, "pdf", $"{stem} and {r.Inputs.Count - 1} more"));
            }
            case "pdf.split":
            {
                using var src = Open(input, PdfDocumentOpenMode.Import);
                var dir = tmp.Dir("pages");
                for (int i = 0; i < src.PageCount; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    using var one = new PdfDocument();
                    one.AddPage(src.Pages[i]);
                    one.Save(Path.Combine(dir, $"{SafePaths.SanitizeFileName(stem)} page {(i + 1).ToString().PadLeft(src.PageCount.ToString().Length, '0')}.pdf"));
                    progress?.Report(0.1 + 0.85 * (i + 1) / src.PageCount);
                }
                return OperationResult.Ok(CommitDir(dir));
            }
            case "pdf.extract-pages":
            {
                using var src = Open(input, PdfDocumentOpenMode.Import);
                var pages = PageSelection.Parse(r.Opt("pages"), src.PageCount);
                using var outDoc = new PdfDocument();
                foreach (var p in pages) outDoc.AddPage(src.Pages[p - 1]);
                var o = tmp.File("pdf"); outDoc.Save(o); ValidatePdf(o);
                return OperationResult.Ok(CommitFile(o, "pdf"));
            }
            case "pdf.reorder":
            {
                using var src = Open(input, PdfDocumentOpenMode.Import);
                var spec = r.Opt("order");
                if (string.IsNullOrWhiteSpace(spec)) throw new OrangeException("Type the new page order, like 3,1,2.");
                var pages = PageSelection.ParseOrdered(spec, src.PageCount);
                using var outDoc = new PdfDocument();
                foreach (var p in pages) outDoc.AddPage(src.Pages[p - 1]);
                var o = tmp.File("pdf"); outDoc.Save(o); ValidatePdf(o);
                return OperationResult.Ok(CommitFile(o, "pdf"));
            }
            case "pdf.rotate":
            {
                using var doc = Open(input, PdfDocumentOpenMode.Modify);
                var pages = PageSelection.Parse(r.Opt("pages"), doc.PageCount);
                var angle = int.Parse(r.Opt("angle"));
                foreach (var p in pages) { var pg = doc.Pages[p - 1]; pg.Rotate = ((pg.Rotate + angle) % 360 + 360) % 360; }
                var o = tmp.File("pdf"); doc.Save(o); ValidatePdf(o);
                return OperationResult.Ok(CommitFile(o, "pdf"));
            }
            case "pdf.strip-metadata":
            {
                using var doc = Open(input, PdfDocumentOpenMode.Modify);
                var info = doc.Info;
                foreach (var key in info.Elements.Keys.ToList()) info.Elements.Remove(key);
                doc.Internals.Catalog.Elements.Remove("/Metadata");   // XMP packet
                var o = tmp.File("pdf"); doc.Save(o); ValidatePdf(o);
                return OperationResult.Ok(CommitFile(o, "pdf"));
            }
            case "pdf.to.txt":
            case "pdf.to.md":
            {
                var o = tmp.File(op.OutputExtension!);
                await using (var w = new StreamWriter(o, false, new System.Text.UTF8Encoding(false)))
                {
                    using var doc = OpenPig(input);
                    int n = 0, total = doc.NumberOfPages;
                    if (total > EngineLimits.Current.MaxPdfPages) throw new OrangeException($"This PDF has {total} pages, more than the {EngineLimits.Current.MaxPdfPages} page limit.");
                    foreach (var page in doc.GetPages())
                    {
                        ct.ThrowIfCancellationRequested();
                        var text = ContentOrderTextExtractor.GetText(page);
                        if (op.Id == "pdf.to.md") await w.WriteLineAsync($"## Page {page.Number}\n");
                        await w.WriteLineAsync(text.TrimEnd());
                        await w.WriteLineAsync(op.Id == "pdf.to.md" ? "" : "\f");
                        progress?.Report(0.1 + 0.85 * ++n / total);
                    }
                }
                if (new FileInfo(o).Length < 8)
                    throw new OrangeException("This PDF has no selectable text. It's probably scanned.", "Text recognition (OCR) isn't part of Orange yet.");
                return OperationResult.Ok(CommitFile(o, op.OutputExtension!));
            }
            case "pdf.extract-images":
            {
                var dir = tmp.Dir("images");
                int count = 0;
                using (var doc = OpenPig(input))
                    foreach (var page in doc.GetPages())
                    {
                        ct.ThrowIfCancellationRequested();
                        int k = 0;
                        foreach (var img in page.GetImages())
                        {
                            k++;
                            var baseName = Path.Combine(dir, $"page {page.Number} image {k}");
                            if (img.TryGetPng(out var png)) await File.WriteAllBytesAsync(baseName + ".png", png, ct);
                            else
                            {
                                var raw = img.RawBytes.ToArray();
                                if (raw.Length > 3 && raw[0] == 0xFF && raw[1] == 0xD8) await File.WriteAllBytesAsync(baseName + ".jpg", raw, ct);
                                else continue;   // JBIG2/CCITT/JPX without a decoder: skipped, counted in the message
                            }
                            count++;
                        }
                        progress?.Report(0.1 + 0.85 * page.Number / doc.NumberOfPages);
                    }
                if (count == 0) throw new OrangeException("No extractable images were found in this PDF.");
                return OperationResult.Ok(CommitDir(dir));
            }
            case "pdf.compress" when !tools.Has("ghostscript"):
            {
                // Built in: re-encode the pictures; for a target size, step down the levels until it fits.
                var targetMB = r.OptDouble("targetMB");
                string[] levels = targetMB > 0 ? ["printer", "ebook", "screen", "tiny"] : [r.Opt("level")];
                string? best = null; long bestLen = long.MaxValue; int i = 0;
                foreach (var lvl in levels)
                {
                    ct.ThrowIfCancellationRequested();
                    var t = tmp.File("pdf");
                    Office.PdfCompress.Compress(input, t, lvl, ct);
                    progress?.Report(0.1 + 0.2 * ++i);
                    var len = new FileInfo(t).Length;
                    if (len < bestLen) { best = t; bestLen = len; }
                    if (targetMB > 0 && len <= targetMB * 1048576) break;
                }
                ValidatePdf(best!);
                if (targetMB > 0 && bestLen > targetMB * 1048576)
                    throw new OrangeException($"Orange couldn't get this PDF under {targetMB:0.##} MB.", $"The smallest it got was {bestLen / 1048576.0:0.##} MB. Text and vector pages don't shrink much; installing Ghostscript can help.");
                if (bestLen >= new FileInfo(input).Length)
                    throw new OrangeException("This PDF is already as small as Orange can make it.", "Its pictures are already compressed. Installing Ghostscript can shrink fonts too.");
                var c0 = CommitFile(best!, "pdf");
                return new OperationResult(true, [c0], Detail: $"{new FileInfo(input).Length / 1048576.0:0.##} MB → {bestLen / 1048576.0:0.##} MB");
            }
            case "pdf.compress":
            {
                var o = tmp.File("pdf");
                var targetMB = r.OptDouble("targetMB");
                if (targetMB > 0)
                {
                    long target = (long)(targetMB * 1024 * 1024);
                    string? fit = null; long best = long.MaxValue; int i = 0;
                    foreach (var (lvl, dpi) in new[] { ("printer", 0), ("ebook", 0), ("screen", 0), ("screen", 72), ("screen", 50) })
                    {
                        ct.ThrowIfCancellationRequested();
                        var t = tmp.File("pdf");
                        var args = new List<string> { "-sDEVICE=pdfwrite", "-dCompatibilityLevel=1.6", $"-dPDFSETTINGS=/{lvl}", "-dDetectDuplicateImages=true", "-dCompressFonts=true" };
                        if (dpi > 0) args.AddRange(["-dDownsampleColorImages=true", "-dDownsampleGrayImages=true", "-dColorImageResolution=" + dpi, "-dGrayImageResolution=" + dpi, "-dColorImageDownsampleThreshold=1.0"]);
                        args.AddRange(["-o", t, input]);
                        await Gs(args, ct);
                        progress?.Report(0.1 + 0.18 * ++i);
                        var len = new FileInfo(t).Length;
                        best = Math.Min(best, len);
                        if (len <= target) { fit = t; break; }
                    }
                    if (fit is null) throw new OrangeException($"Orange couldn't get this PDF under {targetMB:0.##} MB.", $"The smallest it got was {best / 1048576.0:0.##} MB. Text and vector pages don't shrink much.");
                    ValidatePdf(fit);
                    var c = CommitFile(fit, "pdf");
                    return new OperationResult(true, [c], Detail: $"{new FileInfo(c).Length / 1048576.0:0.##} MB (target {targetMB:0.##} MB)");
                }
                await Gs(["-sDEVICE=pdfwrite", "-dCompatibilityLevel=1.6", $"-dPDFSETTINGS=/{r.Opt("level")}", "-dDetectDuplicateImages=true",
                          "-dCompressFonts=true", "-o", o, input], ct);
                ValidatePdf(o);
                // Never hand back a "compressed" file that's bigger. Ghostscript rewrites everything; Orange's own engine
                // only re-encodes pictures, which sometimes wins on already-optimised files, so try it before giving up.
                if (new FileInfo(o).Length >= new FileInfo(input).Length)
                {
                    var alt = tmp.File("pdf");
                    try { Office.PdfCompress.Compress(input, alt, r.Opt("level"), ct); } catch { }
                    if (File.Exists(alt) && new FileInfo(alt).Length > 0 && new FileInfo(alt).Length < new FileInfo(input).Length) { ValidatePdf(alt); o = alt; }
                }
                if (new FileInfo(o).Length >= new FileInfo(input).Length)
                    throw new OrangeException("This PDF is already as small as Orange can make it.", "Try the 'screen' level for a smaller, lower-quality file.");
                return OperationResult.Ok(CommitFile(o, "pdf"));
            }
            case "pdf.to.png":
            case "pdf.to.jpg":
            {
                int total;
                using (var check = OpenPig(input)) total = check.NumberOfPages;
                var pages = PageSelection.Parse(r.Opt("pages"), total);
                var dir = tmp.Dir("pages");
                var dev = op.Id == "pdf.to.png" ? "png16m" : "jpeg";
                var ext = op.OutputExtension!;
                int done = 0;
                if (!tools.Has("ghostscript"))
                {   // Built in: PDFium (bundled) renders the pages.
                    var bytes = await File.ReadAllBytesAsync(input, ct);
                    var opts = new PDFtoImage.RenderOptions(Dpi: int.Parse(r.Opt("dpi")), WithAnnotations: true, WithFormFill: true);
                    foreach (var p in pages)
                    {
                        ct.ThrowIfCancellationRequested();
                        var file = Path.Combine(dir, $"page {p.ToString().PadLeft(total.ToString().Length, '0')}.{ext}");
                        await using (var fs = File.Create(file))
                        {
                            if (ext == "png") PDFtoImage.Conversion.SavePng(fs, bytes, page: (Index)(p - 1), options: opts);
                            else PDFtoImage.Conversion.SaveJpeg(fs, bytes, page: (Index)(p - 1), options: opts);
                        }
                        progress?.Report(0.05 + 0.9 * ++done / pages.Count);
                    }
                    return OperationResult.Ok(CommitDir(dir, stem));
                }
                foreach (var p in pages)
                {
                    ct.ThrowIfCancellationRequested();
                    await Gs([$"-sDEVICE={dev}", $"-r{int.Parse(r.Opt("dpi"))}", "-dTextAlphaBits=4", "-dGraphicsAlphaBits=4", "-dJPEGQ=90",
                              $"-dFirstPage={p}", $"-dLastPage={p}", "-o", Path.Combine(dir, $"page {p.ToString().PadLeft(total.ToString().Length, '0')}.{ext}"), input], ct);
                    progress?.Report(0.05 + 0.9 * ++done / pages.Count);
                }
                return OperationResult.Ok(CommitDir(dir, stem));
            }
            default: throw new OrangeException($"Unsupported PDF operation {op.Id}.");
        }
    }, ct);

    private async Task Gs(List<string> args, CancellationToken ct)
    {
        // -dSAFER blocks file access outside the named input/output and PostScript operators that run commands.
        var a = new List<string> { "-dSAFER", "-dBATCH", "-dNOPAUSE", "-dQUIET", "-dNOPROMPT" };
        a.AddRange(args);
        var res = await ProcessRunner.RunAsync(tools.Require("ghostscript"), a, ct);
        if (res.ExitCode != 0)
            throw new OrangeException(res.StdErr.Contains("password", StringComparison.OrdinalIgnoreCase) || res.StdOut.Contains("password", StringComparison.OrdinalIgnoreCase)
                ? "This PDF is password-protected. Remove the password first." : "Ghostscript couldn't process this PDF. It may be damaged.", ProcessRunner.Tail(res.StdErr + res.StdOut));
    }

    public static PdfDocument Open(string path, PdfDocumentOpenMode mode)
    {
        try
        {
            var d = PdfReader.Open(path, mode);
            if (d.PageCount > EngineLimits.Current.MaxPdfPages) { d.Dispose(); throw new OrangeException($"This PDF has more than {EngineLimits.Current.MaxPdfPages} pages."); }
            return d;
        }
        catch (PdfReaderException e) when (e.Message.Contains("password", StringComparison.OrdinalIgnoreCase))
        { throw new OrangeException($"\"{Path.GetFileName(path)}\" is password-protected. Remove the password first.", e.Message, e); }
        catch (Exception e) when (e is not OrangeException)
        { throw new OrangeException($"\"{Path.GetFileName(path)}\" couldn't be opened as a PDF. It may be damaged.", e.Message, e); }
    }

    private static PdfDocument_Pig OpenPig(string path)
    {
        try { return new PdfDocument_Pig(UglyToad.PdfPig.PdfDocument.Open(path, new UglyToad.PdfPig.ParsingOptions { UseLenientParsing = true, SkipMissingFonts = true })); }
        catch (Exception e) when (e.Message.Contains("password", StringComparison.OrdinalIgnoreCase) || e.GetType().Name.Contains("Encrypt"))
        { throw new OrangeException($"\"{Path.GetFileName(path)}\" is password-protected. Remove the password first.", e.Message, e); }
        catch (Exception e) { throw new OrangeException($"\"{Path.GetFileName(path)}\" couldn't be read as a PDF. It may be damaged.", e.Message, e); }
    }

    /// <summary>Small wrapper so the two libraries' PdfDocument types don't collide.</summary>
    private sealed class PdfDocument_Pig(UglyToad.PdfPig.PdfDocument d) : IDisposable
    {
        public int NumberOfPages => d.NumberOfPages;
        public IEnumerable<UglyToad.PdfPig.Content.Page> GetPages() => d.GetPages();
        public void Dispose() => d.Dispose();
    }

    public static void ValidatePdf(string path)
    {
        try { using var d = PdfReader.Open(path, PdfDocumentOpenMode.Import); if (d.PageCount == 0) throw new Exception("no pages"); }
        catch (Exception e) { throw new OrangeException("The PDF Orange made failed its check, so it wasn't saved.", e.Message, e); }
    }

    /// <summary>One page per image. "fit" sizes each page to its image; a4/letter centre the image with margins.</summary>
    public static void ImagesToPdf(IReadOnlyList<string> images, string output, string pageSize, CancellationToken ct, IProgress<double>? progress)
    {
        using var doc = new PdfDocument();
        using var scratch = new SafePaths.TempWorkspace();
        for (int i = 0; i < images.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var src = images[i];
            string usable;
            double dpiX, dpiY;
            using (var img = new MagickImage())
            {
                try { img.Read(src, new MagickReadSettings { Format = ImageProvider.DecoderFor(src) }); }
                catch (MagickException e) { throw new OrangeException($"\"{Path.GetFileName(src)}\" couldn't be opened.", e.Message, e); }
                var orientation = img.Orientation;
                img.AutoOrient();
                dpiX = img.Density.X > 1 ? img.Density.X : 72; dpiY = img.Density.Y > 1 ? img.Density.Y : 72;
                if (img.Density.Units == DensityUnit.PixelsPerCentimeter) { dpiX *= 2.54; dpiY *= 2.54; }
                bool isJpeg = ImageProvider.DecoderFor(src) == MagickFormat.Jpeg && orientation is Orientation.TopLeft or Orientation.Undefined;
                if (isJpeg) usable = src;                                    // embed the original JPEG bytes, no re-encode
                else { usable = scratch.File("png"); img.Strip(); img.Write(usable, MagickFormat.Png); }
            }
            using var x = XImage.FromFile(usable);
            var page = doc.AddPage();
            double wPt = x.PixelWidth * 72.0 / dpiX, hPt = x.PixelHeight * 72.0 / dpiY;
            if (pageSize == "fit") { page.Width = XUnit.FromPoint(wPt); page.Height = XUnit.FromPoint(hPt); }
            else
            {
                page.Size = pageSize == "letter" ? PdfSharp.PageSize.Letter : PdfSharp.PageSize.A4;
                if (wPt > hPt) page.Orientation = PdfSharp.PageOrientation.Landscape;
            }
            using var gfx = XGraphics.FromPdfPage(page);
            double pw = page.Width.Point, ph = page.Height.Point, margin = pageSize == "fit" ? 0 : 36;
            double scale = Math.Min((pw - 2 * margin) / wPt, (ph - 2 * margin) / hPt);
            if (pageSize == "fit") scale = 1;
            double dw = wPt * scale, dh = hPt * scale;
            gfx.DrawImage(x, (pw - dw) / 2, (ph - dh) / 2, dw, dh);
            progress?.Report(0.1 + 0.85 * (i + 1) / images.Count);
        }
        doc.Info.Creator = "Orange";
        doc.Save(output);
        ValidatePdf(output);
    }
}

public static class PageSelection
{
    /// <summary>Parses "all", "3", "1-3,7,9-" into a sorted, distinct, 1-based page list within [1, count].</summary>
    public static List<int> Parse(string spec, int count)
    {
        spec = spec.Trim().ToLowerInvariant();
        if (spec is "" or "all") return Enumerable.Range(1, count).ToList();
        var set = new SortedSet<int>();
        foreach (var raw in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = raw.Split('-');
            if (parts.Length > 2) throw Bad(spec);
            int a = parts[0].Length == 0 ? 1 : ParseOne(parts[0], spec);
            int b = parts.Length == 1 ? a : parts[1].Length == 0 ? count : ParseOne(parts[1], spec);
            if (a < 1 || b > count || a > b) throw new OrangeException($"Pages \"{raw}\" are outside this document (1–{count}).");
            for (int i = a; i <= b; i++) set.Add(i);
        }
        if (set.Count == 0) throw Bad(spec);
        return set.ToList();
    }
    /// <summary>Like <see cref="Parse"/> but keeps the order given, allows descending ranges (5-1), and rejects repeats.</summary>
    public static List<int> ParseOrdered(string spec, int count)
    {
        var list = new List<int>();
        foreach (var part in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var ab = part.Split('-', 2, StringSplitOptions.TrimEntries);
            int a = ParseOne(ab[0], spec), b = ab.Length == 2 ? ParseOne(ab[1], spec) : a;
            if (a < 1 || b < 1 || a > count || b > count) throw new OrangeException($"Page {Math.Max(a, b)} doesn't exist. This PDF has {count} pages.");
            for (int p = a; a <= b ? p <= b : p >= b; p += a <= b ? 1 : -1)
            {
                if (list.Contains(p)) throw new OrangeException($"Page {p} is listed twice.");
                list.Add(p);
            }
        }
        if (list.Count == 0) throw new OrangeException("Type the new page order, like 3,1,2.");
        return list;
    }

    private static int ParseOne(string s, string spec) => int.TryParse(s, out var v) ? v : throw Bad(spec);
    private static OrangeException Bad(string spec) => new($"\"{spec}\" isn't a page list. Use something like 1-3,5.");
}
