using ImageMagick;

namespace Orange.Core.Providers;

/// <summary>
/// Images through Magick.NET (ImageMagick 7, in-process, no shell). Every read names its decoder explicitly
/// from the detected signature, so script-like coders (MVG, MSL, SVG, URL, ephemeral, text) can't be reached
/// through a renamed file. Resource limits cap memory, pixel area and time.
/// </summary>
public sealed class ImageProvider : IOperationProvider
{
    public string Name => "Images (Magick.NET)";
    private static readonly string[] Inputs = ["png", "jpg", "webp", "heic", "tiff", "bmp", "gif", "avif", "ico", "svg"];
    public static readonly HashSet<string> InputExtensionsSet = Inputs.ToHashSet();
    private static readonly (string Ext, string Title, MagickFormat Fmt, bool Lossy)[] Targets =
    [
        ("png", "PNG", MagickFormat.Png, false), ("jpg", "JPG", MagickFormat.Jpeg, true), ("webp", "WebP", MagickFormat.WebP, true),
        ("heic", "HEIC", MagickFormat.Heic, true), ("tiff", "TIFF", MagickFormat.Tiff, false), ("bmp", "BMP", MagickFormat.Bmp, false),
        ("gif", "GIF", MagickFormat.Gif, false), ("avif", "AVIF", MagickFormat.Avif, true), ("ico", "ICO", MagickFormat.Ico, false),
    ];

    static ImageProvider()
    {
        var l = EngineLimits.Current;
        ResourceLimits.Memory = 2UL * 1024 * 1024 * 1024;
        ResourceLimits.Area = (ulong)l.MaxImagePixels * 4;
        ResourceLimits.Width = 65_000; ResourceLimits.Height = 65_000;
        ResourceLimits.Time = 600;
    }

    private static readonly OptionSpec Quality = new("quality", "Quality", OptionType.Integer, "85", 1, 100, Help: "Higher keeps more detail and makes a bigger file.");

    public IEnumerable<OperationDescriptor> Describe()
    {
        foreach (var t in Targets)
            yield return new OperationDescriptor
            {
                Id = $"image.to.{t.Ext}", Title = t.Title, Category = FileKind.Image, Group = OperationGroup.Convert,
                InputExtensions = Inputs.ToHashSet(), OutputExtension = t.Ext,
                Options = t.Lossy ? [Quality] : t.Ext == "ico" ? [new OptionSpec("size", "Icon size", OptionType.Choice, "256", Choices: ["16", "32", "48", "64", "128", "256"])] : [],
                Keywords = [t.Ext, "convert", "image", "photo", t.Ext == "jpg" ? "jpeg" : t.Ext],
            };
        yield return new OperationDescriptor
        {
            Id = "image.to.pdf", Title = "PDF", Category = FileKind.Image, Group = OperationGroup.Convert, InputExtensions = Inputs.ToHashSet(),
            OutputExtension = "pdf", Keywords = ["pdf", "convert", "document"],
        };

        yield return Tool("image.compress", "Compress", "Compressed", "compress",
            [Quality with { Default = "70" }, new("maxSide", "Longest side (px, 0 = keep)", OptionType.Integer, "0", 0, 20000),
             new("targetKB", "Target size (KB, 0 = off)", OptionType.Integer, "0", 0, 1_000_000, Help: "Orange searches quality, then size, to land at or just under this.")], ["smaller", "reduce", "optimize", "size"]);
        yield return Tool("image.resize", "Resize", "Resized", "resize",
            [new("mode", "Resize by", OptionType.Choice, "percent", Choices: ["percent", "width", "height", "longest"]),
             new("value", "Amount", OptionType.Number, "50", 1, 20000, Help: "Percent, or pixels for width/height/longest side.")], ["scale", "dimensions", "smaller", "bigger"]);
        yield return Tool("image.crop", "Crop", "Cropped", "crop",
            [new("aspect", "Aspect", OptionType.Choice, "1:1", Choices: ["1:1", "4:3", "3:2", "16:9", "9:16", "4:5", "custom"]),
             new("x", "Left (px)", OptionType.Integer, "0", 0, 65000), new("y", "Top (px)", OptionType.Integer, "0", 0, 65000),
             new("width", "Width (px)", OptionType.Integer, "0", 0, 65000), new("height", "Height (px)", OptionType.Integer, "0", 0, 65000)],
            ["trim", "cut", "square", "aspect"]);
        yield return Tool("image.rotate", "Rotate", "Rotated", "rotate",
            [new("angle", "Rotate", OptionType.Choice, "90", Choices: ["90", "180", "270", "flip-horizontal", "flip-vertical"])], ["turn", "flip", "mirror", "orientation"]);
        yield return Tool("image.strip-metadata", "Remove metadata", "Clean", "metadata",
            [], ["exif", "gps", "location", "privacy", "strip"]);
        yield return Tool("image.grayscale", "Grayscale", "Grayscale", "grayscale", [], ["black and white", "monochrome", "bw"]);
        yield return new OperationDescriptor
        {
            Id = "image.create-pdf", Title = "Create PDF", Category = FileKind.Image, Group = OperationGroup.Tool, InputExtensions = Inputs.ToHashSet(),
            OutputExtension = "pdf", CombinesInputs = true, MinInputs = 2, Icon = "pdf",
            Options = [new("pageSize", "Page size", OptionType.Choice, "fit", Choices: ["fit", "a4", "letter"])],
            Keywords = ["pdf", "combine", "merge", "images to pdf", "scan"],
        };
    }

    private static OperationDescriptor Tool(string id, string title, string suffix, string icon, OptionSpec[] options, string[] keywords) => new()
    {
        Id = id, Title = title, Category = FileKind.Image, Group = OperationGroup.Tool, InputExtensions = Inputs.ToHashSet(),
        OutputExtension = null, Options = options, Icon = icon, Keywords = keywords.Append("suffix:" + suffix).ToArray(),
    };

    public static string SuffixOf(OperationDescriptor op) =>
        op.Keywords.FirstOrDefault(k => k.StartsWith("suffix:"))?[7..] ?? op.Title;

    public bool IsAvailable(OperationDescriptor op) => op.Id switch
    {
        "image.to.heic" => CanWrite(MagickFormat.Heic),
        "image.to.avif" => CanWrite(MagickFormat.Avif),
        _ => true,
    };

    private static bool CanWrite(MagickFormat f)
    {
        try { return MagickFormatInfo.Create(f)?.SupportsWriting == true; } catch { return false; }
    }

    public static MagickFormat DecoderFor(string path)
    {
        var h = new byte[32];
        using (var fs = File.OpenRead(path)) fs.ReadAtLeast(h, h.Length, false);
        string A(int o, int n) => System.Text.Encoding.ASCII.GetString(h, o, n);
        if (h[0] == 0x89 && A(1, 3) == "PNG") return MagickFormat.Png;
        if (h[0] == 0xFF && h[1] == 0xD8) return MagickFormat.Jpeg;
        if (A(0, 3) == "GIF") return MagickFormat.Gif;
        if (A(0, 2) == "BM") return MagickFormat.Bmp;
        if ((h[0] == 0x49 && h[1] == 0x49) || (h[0] == 0x4D && h[1] == 0x4D)) return MagickFormat.Tiff;
        if (A(0, 4) == "RIFF" && A(8, 4) == "WEBP") return MagickFormat.WebP;
        if (h[0] == 0 && h[1] == 0 && h[2] == 1 && h[3] == 0) return MagickFormat.Ico;
        if (A(4, 4) == "ftyp") return A(8, 4).StartsWith("avi") ? MagickFormat.Avif : MagickFormat.Heic;
        if (SvgSafety.LooksLikeSvg(path)) { SvgSafety.EnsureSafe(path); return MagickFormat.Svg; }
        throw new OrangeException($"\"{Path.GetFileName(path)}\" isn't an image Orange can read.", "Its contents don't match a supported image format.");
    }

    public static MagickImageCollection Load(string path)
    {
        var fmt = DecoderFor(path);
        var settings = new MagickReadSettings { Format = fmt };
        if (fmt == MagickFormat.Svg) { settings.BackgroundColor = MagickColors.Transparent; settings.Density = new Density(144); }
        var col = new MagickImageCollection();
        try { col.Read(path, settings); }
        catch (MagickException e) { col.Dispose(); throw new OrangeException($"\"{Path.GetFileName(path)}\" couldn't be opened. It may be damaged.", e.Message, e); }
        if (col.Count == 0) { col.Dispose(); throw new OrangeException($"\"{Path.GetFileName(path)}\" has no image data."); }
        long px = (long)col[0].Width * col[0].Height;
        if (px > EngineLimits.Current.MaxImagePixels) { col.Dispose(); throw new OrangeException($"\"{Path.GetFileName(path)}\" is too large ({col[0].Width}×{col[0].Height})."); }
        return col;
    }

    public Task<OperationResult> ExecuteAsync(OperationRequest r, IProgress<double>? progress, CancellationToken ct) =>
        Task.Run(() => Execute(r, progress, ct), ct);

    private OperationResult Execute(OperationRequest r, IProgress<double>? progress, CancellationToken ct)
    {
        using var tmp = new SafePaths.TempWorkspace();
        var op = r.Operation;
        var input = r.Inputs[0];
        var stem = Path.GetFileNameWithoutExtension(input);
        progress?.Report(0.05);

        if (op.Id == "image.create-pdf" || op.Id == "image.to.pdf")
        {
            var outTmp = tmp.File("pdf");
            PdfProvider.ImagesToPdf(r.Inputs, outTmp, op.Id == "image.create-pdf" ? r.Opt("pageSize") : "fit", ct, progress);
            var name = op.CombinesInputs ? $"{stem} and {r.Inputs.Count - 1} more" : stem;
            var final = SafePaths.PlanOutput(input, r.OutputDirectory, name, null, "pdf", r.Overwrite, r.Inputs);
            return OperationResult.Ok(SafePaths.Commit(outTmp, final, r.Overwrite));
        }

        using var col = Load(input);
        ct.ThrowIfCancellationRequested();
        progress?.Report(0.3);
        var srcExt = OperationRegistry.Normalize(FileTypeDetector.Ext(input));
        string outExt; string? suffix = null;

        if (op.Group == OperationGroup.Convert)
        {
            var t = Targets.First(x => $"image.to.{x.Ext}" == op.Id);
            outExt = t.Ext;
            bool animatedOut = t.Fmt is MagickFormat.Gif or MagickFormat.WebP;
            if (!animatedOut && col.Count > 1 && t.Fmt != MagickFormat.Tiff && t.Fmt != MagickFormat.Ico)
                while (col.Count > 1) col.RemoveAt(col.Count - 1);   // first frame only for still formats
            foreach (var img in col)
            {
                img.AutoOrient();
                if (t.Lossy) img.Quality = (uint)r.OptInt("quality");
                if (t.Fmt == MagickFormat.Jpeg && img.HasAlpha) { img.BackgroundColor = MagickColors.White; img.Alpha(AlphaAction.Remove); }
                if (t.Fmt == MagickFormat.Ico)
                {
                    var s = uint.Parse(r.Opt("size"));
                    img.Resize(new MagickGeometry(s, s) { IgnoreAspectRatio = false });
                }
                if (t.Fmt == MagickFormat.Gif && col.Count == 1) img.Quantize(new QuantizeSettings { Colors = 256, DitherMethod = DitherMethod.FloydSteinberg });
            }
            if (t.Fmt == MagickFormat.Gif && col.Count > 1) col.Optimize();
            var outTmp = tmp.File(outExt);
            if (col.Count > 1 && animatedOut || t.Fmt == MagickFormat.Tiff && col.Count > 1) col.Write(outTmp, t.Fmt);
            else col[0].Write(outTmp, t.Fmt);
            return Finish(outTmp);
        }

        // Tools keep the input format (HEIC/AVIF fall back to JPG when this build can't write them).
        (outExt, var fmt) = WritableFormatFor(srcExt);
        suffix = SuffixOf(op);
        foreach (var img in col)
        {
            ct.ThrowIfCancellationRequested();
            if (op.Id != "image.rotate") img.AutoOrient();
            switch (op.Id)
            {
                case "image.compress":
                    var side = r.OptInt("maxSide");
                    if (side > 0 && Math.Max(img.Width, img.Height) > side) img.Resize(new MagickGeometry((uint)side, (uint)side));
                    img.Strip();
                    if (fmt is MagickFormat.Png) { img.Settings.SetDefine(MagickFormat.Png, "compression-level", "9"); if (img.TotalColors > 256) { } }
                    else img.Quality = (uint)r.OptInt("quality");
                    break;
                case "image.resize":
                    var v = r.OptDouble("value");
                    MagickGeometry g = r.Opt("mode") switch
                    {
                        "percent" => new MagickGeometry(new Percentage(v), new Percentage(v)),
                        "width" => new MagickGeometry((uint)v, 0),
                        "height" => new MagickGeometry(0, (uint)v),
                        _ => new MagickGeometry((uint)v, (uint)v),
                    };
                    if (r.Opt("mode") == "percent" && v > 1000) throw new OrangeException("Resize is limited to 1000%.");
                    img.Resize(g);
                    break;
                case "image.crop":
                    img.Crop(CropGeometry(img.Width, img.Height, r));
                    img.ResetPage();
                    break;
                case "image.rotate":
                    img.AutoOrient();
                    switch (r.Opt("angle"))
                    {
                        case "flip-horizontal": img.Flop(); break;
                        case "flip-vertical": img.Flip(); break;
                        default: img.Rotate(double.Parse(r.Opt("angle"), System.Globalization.CultureInfo.InvariantCulture)); break;
                    }
                    break;
                case "image.strip-metadata":
                    img.Strip();
                    break;
                case "image.grayscale":
                    img.Grayscale();
                    break;
                default: throw new OrangeException($"Unsupported image tool {op.Id}.");
            }
        }
        progress?.Report(0.8);
        var o = tmp.File(outExt);
        if (op.Id == "image.compress" && r.OptInt("targetKB") > 0)
        {
            var (bytes, note) = HitTargetSize((MagickImage)col[0], fmt, r.OptInt("targetKB") * 1024L, ct);
            File.WriteAllBytes(o, bytes);
            ValidateOutput(o);
            var tf = SafePaths.PlanOutput(input, r.OutputDirectory, stem, suffix, outExt, r.Overwrite, r.Inputs);
            progress?.Report(1);
            return new OperationResult(true, [SafePaths.Commit(o, tf, r.Overwrite)], Detail: note);
        }
        if (col.Count > 1 && fmt is MagickFormat.Gif or MagickFormat.WebP or MagickFormat.Tiff) col.Write(o, fmt); else col[0].Write(o, fmt);
        return Finish(o);

        OperationResult Finish(string produced)
        {
            ValidateOutput(produced);
            var final = SafePaths.PlanOutput(input, r.OutputDirectory, stem, suffix, outExt, r.Overwrite, r.Inputs);
            progress?.Report(1);
            return OperationResult.Ok(SafePaths.Commit(produced, final, r.Overwrite));
        }
    }

    /// <summary>
    /// Binary-searches encoder quality (lossy formats), then shrinks the pixel size in 10% steps, until the encoded
    /// file is at or under the target. Lossless formats only have the size step. Returns the bytes and a plain note.
    /// </summary>
    public static (byte[] Bytes, string Note) HitTargetSize(MagickImage src, MagickFormat fmt, long target, CancellationToken ct)
    {
        bool lossy = fmt is MagickFormat.Jpeg or MagickFormat.WebP or MagickFormat.Avif or MagickFormat.Heic;
        byte[] Encode(MagickImage im, uint q) { using var c = (MagickImage)im.Clone(); c.Strip(); if (lossy) c.Quality = q; else if (fmt == MagickFormat.Png) c.Settings.SetDefine(MagickFormat.Png, "compression-level", "9"); return c.ToByteArray(fmt); }
        double scale = 1.0;
        byte[]? best = null; uint bestQ = 0;
        for (int step = 0; step < 25; step++)
        {
            ct.ThrowIfCancellationRequested();
            using var scaled = (MagickImage)src.Clone();
            if (scale < 1) scaled.Resize(new MagickGeometry((uint)Math.Max(1, src.Width * scale), (uint)Math.Max(1, src.Height * scale)));
            if (lossy)
            {
                uint lo = 10, hi = 95; byte[]? fit = null; uint fitQ = 0;
                while (lo <= hi)
                {
                    uint mid = (lo + hi) / 2; var b = Encode(scaled, mid);
                    if (b.Length <= target) { fit = b; fitQ = mid; lo = mid + 1; } else hi = mid - 1;
                }
                if (fit != null) { best = fit; bestQ = fitQ; break; }
            }
            else { var b = Encode(scaled, 0); if (b.Length <= target) { best = b; break; } }
            scale *= 0.9;
            if (src.Width * scale < 16 || src.Height * scale < 16) break;
        }
        if (best is null) throw new OrangeException($"Orange couldn't get this image down to {target / 1024} KB.", "Even at the smallest size the file stays bigger. Try a lossy format like JPG or WebP.");
        var w = (uint)Math.Round(src.Width * scale); var h = (uint)Math.Round(src.Height * scale);
        var note = $"{best.Length / 1024.0:0.#} KB (target {target / 1024} KB)" + (lossy ? $", quality {bestQ}" : "") + (scale < 1 ? $", resized to {w}×{h}" : "");
        return (best, note);
    }

    public static MagickGeometry CropGeometry(uint w, uint h, OperationRequest r)
    {
        var aspect = r.Opt("aspect");
        if (aspect == "custom")
        {
            int x = r.OptInt("x"), y = r.OptInt("y"), cw = r.OptInt("width"), ch = r.OptInt("height");
            if (cw <= 0 || ch <= 0) throw new OrangeException("Enter a crop width and height.");
            if (x + cw > w || y + ch > h) throw new OrangeException($"The crop area is outside the image ({w}×{h}).");
            return new MagickGeometry(x, y, (uint)cw, (uint)ch);
        }
        var parts = aspect.Split(':').Select(double.Parse).ToArray();
        double target = parts[0] / parts[1];
        uint nw = w, nh = h;
        if ((double)w / h > target) nw = (uint)Math.Round(h * target); else nh = (uint)Math.Round(w / target);
        return new MagickGeometry((int)((w - nw) / 2), (int)((h - nh) / 2), nw, nh);   // centred
    }

    /// <summary>Tools keep the input format; HEIC/AVIF fall back to JPG when unwritable, SVG (vector) becomes PNG.</summary>
    public static (string Ext, MagickFormat Fmt) WritableFormatFor(string srcExt)
    {
        if (srcExt == "svg") return ("png", MagickFormat.Png);
        var f = FormatForExt(srcExt);
        return CanWrite(f) ? (srcExt, f) : ("jpg", MagickFormat.Jpeg);
    }

    private static MagickFormat FormatForExt(string ext) => ext switch
    {
        "png" => MagickFormat.Png, "jpg" => MagickFormat.Jpeg, "webp" => MagickFormat.WebP, "heic" => MagickFormat.Heic,
        "tiff" => MagickFormat.Tiff, "bmp" => MagickFormat.Bmp, "gif" => MagickFormat.Gif, "avif" => MagickFormat.Avif, "ico" => MagickFormat.Ico,
        _ => MagickFormat.Png,
    };

    /// <summary>Re-opens the result with its own decoder to prove it's a real, readable image.</summary>
    public static void ValidateOutput(string path)
    {
        try { using var img = new MagickImage(); img.Ping(path, new MagickReadSettings { Format = DecoderFor(path) }); if (img.Width == 0) throw new Exception("zero width"); }
        catch (Exception e) when (e is not OrangeException) { throw new OrangeException("The converted image failed its check, so it wasn't saved.", e.Message, e); }
    }
}
