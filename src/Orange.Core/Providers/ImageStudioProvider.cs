using System.Globalization;
using ImageMagick;
using ImageMagick.Drawing;
using ZXing;
using ZXing.Common;

namespace Orange.Core.Providers;

/// <summary>
/// The image tools behind Orange's editing panels: Edit (light adjustments), Add background, Redact, Annotate,
/// Collage, metadata editing and the QR reader. Each one changes real pixels/metadata and writes a new file
/// next to the original; settings come from the panel as plain options so the CLI and tests use the same path.
/// </summary>
public sealed class ImageStudioProvider : IOperationProvider
{
    public string Name => "Image studio (Magick.NET, ZXing.Net)";
    private static readonly HashSet<string> Inputs = ImageProvider.InputExtensionsSet;

    public static readonly IReadOnlyDictionary<string, (string A, string B)> Gradients = new Dictionary<string, (string, string)>
    {
        ["sunset"] = ("#FF9A3C", "#9B5DE5"), ["citrus"] = ("#FFB347", "#FF5E62"), ["ocean"] = ("#2BC0E4", "#EAECC6"),
        ["mint"] = ("#43E97B", "#38F9D7"), ["lavender"] = ("#C3B1E1", "#F7F0FF"), ["night"] = ("#0F2027", "#2C5364"),
        ["peach"] = ("#FFDDE1", "#EE9CA7"), ["mono"] = ("#FFFFFF", "#D9D9D9"),
    };

    public IEnumerable<OperationDescriptor> Describe()
    {
        OptionSpec Slider(string n, string l, double min = -100, double max = 100) => new(n, l, OptionType.Number, "0", min, max);
        yield return Tool("image.edit", "Edit", "Edited", "edit",
            [Slider("exposure", "Exposure (EV)", -3, 3), Slider("brightness", "Brightness"), Slider("contrast", "Contrast"),
             Slider("highlights", "Highlights"), Slider("shadows", "Shadows"), Slider("saturation", "Saturation"),
             new("rotate", "Rotate", OptionType.Choice, "0", Choices: ["0", "90", "180", "270"])],
            ["adjust", "exposure", "brightness", "contrast", "highlights", "shadows", "saturation", "light", "photo editor"]);
        yield return Tool("image.add-background", "Add BG", "Background", "background",
            [new("background", "Background", OptionType.Text, "white", Help: "A colour (white, #RRGGBB), a gradient (sunset, ocean, mint…, or gradient:#a-#b), or image:<path>."),
             new("padding", "Padding (%)", OptionType.Integer, "8", 0, 50),
             new("radius", "Corner radius (%)", OptionType.Integer, "0", 0, 50)],
            ["background", "fill", "transparent", "gradient", "backdrop", "frame"]);
        yield return Tool("image.redact", "Redact", "Redacted", "redact",
            [new("regions", "Areas", OptionType.Text, "", Help: "x,y,w,h in pixels; separate several with ';'."),
             new("style", "Style", OptionType.Choice, "black", Choices: ["black", "pixelate", "blur"])],
            ["hide", "censor", "blur", "pixelate", "privacy", "black out"]);
        yield return Tool("image.annotate", "Annotate", "Annotated", "annotate",
            [new("text", "Text", OptionType.Text, ""), new("x", "Text left (px)", OptionType.Integer, "20", 0, 65000), new("y", "Text top (px)", OptionType.Integer, "20", 0, 65000),
             new("size", "Text size (px)", OptionType.Integer, "48", 6, 1000), new("color", "Colour", OptionType.Text, "#FF6A00"),
             new("boxes", "Boxes", OptionType.Text, "", Help: "Outlined rectangles x,y,w,h; separate several with ';'."),
             new("arrows", "Arrows", OptionType.Text, "", Help: "x1,y1,x2,y2; separate several with ';'."),
             new("stroke", "Line width (px)", OptionType.Integer, "6", 1, 200)],
            ["markup", "text", "draw", "arrow", "highlight", "label"]);
        yield return Tool("image.metadata", "Metadata", "Metadata", "metadata",
            [new("remove", "Remove", OptionType.Choice, "none", Choices: ["none", "location", "camera", "all-but-color", "all"],
                Help: "location = GPS only; camera = make, model, lens, serial; all-but-color keeps the colour profile."),
             new("artist", "Artist", OptionType.Text, ""), new("copyright", "Copyright", OptionType.Text, ""),
             new("description", "Description", OptionType.Text, ""), new("date", "Date taken (yyyy-mm-dd hh:mm)", OptionType.Text, "")],
            ["exif", "gps", "location", "camera", "copyright", "author", "edit metadata", "tags", "privacy"]);
        yield return new OperationDescriptor
        {
            Id = "image.collage", Title = "Collage", Category = FileKind.Image, Group = OperationGroup.Tool, InputExtensions = Inputs,
            OutputExtension = "png", CombinesInputs = true, MinInputs = 2, Icon = "collage",
            Options = [new("columns", "Columns (0 = auto)", OptionType.Integer, "0", 0, 20), new("cell", "Cell size (px)", OptionType.Integer, "800", 100, 4000),
                       new("gap", "Gap (px)", OptionType.Integer, "16", 0, 400), new("background", "Background", OptionType.Text, "white"),
                       new("fit", "Fit", OptionType.Choice, "cover", Choices: ["cover", "contain"])],
            Keywords = ["grid", "combine", "mosaic", "montage", "suffix:Collage"],
        };
        yield return new OperationDescriptor
        {
            Id = "image.qr-read", Title = "Read QR", Category = FileKind.Image, Group = OperationGroup.Tool, InputExtensions = Inputs,
            Icon = "qr", Keywords = ["qr", "qr code", "barcode", "scan", "decode", "single-only", "suffix:QR"],
        };
    }

    private static OperationDescriptor Tool(string id, string title, string suffix, string icon, OptionSpec[] options, string[] keywords) => new()
    {
        Id = id, Title = title, Category = FileKind.Image, Group = OperationGroup.Tool, InputExtensions = Inputs,
        OutputExtension = null, Options = options, Icon = icon, Keywords = keywords.Append("suffix:" + suffix).ToArray(),
    };

    public bool IsAvailable(OperationDescriptor op) => true;

    public Task<OperationResult> ExecuteAsync(OperationRequest r, IProgress<double>? progress, CancellationToken ct) =>
        Task.Run(() => Execute(r, progress, ct), ct);

    private OperationResult Execute(OperationRequest r, IProgress<double>? progress, CancellationToken ct)
    {
        var op = r.Operation;
        var input = r.Inputs[0];
        var stem = Path.GetFileNameWithoutExtension(input);
        using var tmp = new SafePaths.TempWorkspace();
        progress?.Report(0.05);

        if (op.Id == "image.qr-read")
        {
            var found = ReadQr(input);
            progress?.Report(1);
            if (found.Count == 0) throw new OrangeException("No QR code found in this image.", "Try a sharper or larger picture of the code.");
            return new OperationResult(true, Array.Empty<string>(), Detail: string.Join("\n", found));
        }
        if (op.Id == "image.collage")
        {
            var o = tmp.File("png");
            using (var c = Collage(r.Inputs, r.OptInt("columns"), r.OptInt("cell"), r.OptInt("gap"), r.Opt("background"), r.Opt("fit") == "contain", ct)) c.Write(o, MagickFormat.Png);
            ImageProvider.ValidateOutput(o);
            var final = SafePaths.PlanOutput(input, r.OutputDirectory, $"{stem} and {r.Inputs.Count - 1} more", "Collage", "png", r.Overwrite, r.Inputs);
            progress?.Report(1);
            return OperationResult.Ok(SafePaths.Commit(o, final, r.Overwrite));
        }

        var srcExt = OperationRegistry.Normalize(FileTypeDetector.Ext(input));
        if (op.Id == "image.metadata" && r.Opt("remove") is "all" or "all-but-color" && LosslessMetadata.Supports(srcExt)
            && new[] { "artist", "copyright", "description", "date" }.All(k => r.Opt(k).Length == 0))
        {
            var res = LosslessMetadata.Strip(input, keepColorProfile: r.Opt("remove") == "all-but-color");
            var o0 = tmp.File(srcExt); File.WriteAllBytes(o0, res.Bytes);
            ImageProvider.ValidateOutput(o0);
            var f0 = SafePaths.PlanOutput(input, r.OutputDirectory, stem, ImageProvider.SuffixOf(op), srcExt, r.Overwrite, r.Inputs);
            progress?.Report(1);
            return new OperationResult(true, [SafePaths.Commit(o0, f0, r.Overwrite)], Detail: $"Removed {res.RemovedBlocks} metadata block(s) without re-encoding");
        }
        using var col = ImageProvider.Load(input);
        var (outExt, fmt) = ImageProvider.WritableFormatFor(srcExt);
        progress?.Report(0.3);

        if (op.Id == "image.add-background" && fmt is MagickFormat.Jpeg or MagickFormat.Bmp && r.OptInt("radius") > 0) { }   // flattening below handles it
        for (int i = 0; i < col.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var img = (MagickImage)col[i];
            if (op.Id != "image.metadata") img.AutoOrient();
            switch (op.Id)
            {
                case "image.edit": Adjust(img, r); break;
                case "image.add-background":
                {
                    var bg = AddBackground(img, r.Opt("background"), r.OptInt("padding"), r.OptInt("radius"));
                    col[i] = bg; img.Dispose();
                    break;
                }
                case "image.redact": Redact(img, ParseRects(r.Opt("regions"), img.Width, img.Height, "area"), r.Opt("style")); break;
                case "image.annotate": Annotate(img, r); break;
                case "image.metadata": EditMetadata(img, r); break;
                default: throw new OrangeException($"Unsupported image tool {op.Id}.");
            }
        }
        progress?.Report(0.8);
        var outFile = tmp.File(outExt);
        if (col.Count > 1 && fmt is MagickFormat.Gif or MagickFormat.WebP or MagickFormat.Tiff) col.Write(outFile, fmt); else col[0].Write(outFile, fmt);
        ImageProvider.ValidateOutput(outFile);
        var f = SafePaths.PlanOutput(input, r.OutputDirectory, stem, ImageProvider.SuffixOf(op), outExt, r.Overwrite, r.Inputs);
        progress?.Report(1);
        return OperationResult.Ok(SafePaths.Commit(outFile, f, r.Overwrite));
    }

    // ---------------------------------------------------------------- Edit (light)

    /// <summary>Exposure is a linear gain (2^EV); highlights/shadows bend only the top/bottom of the tone curve via a LUT.</summary>
    public static void Adjust(MagickImage img, OperationRequest r)
    {
        double ev = r.OptDouble("exposure"), br = r.OptDouble("brightness"), co = r.OptDouble("contrast");
        double hi = r.OptDouble("highlights") / 100, sh = r.OptDouble("shadows") / 100, sat = r.OptDouble("saturation");
        var rot = r.Opt("rotate");
        if (rot != "0") img.Rotate(double.Parse(rot, CultureInfo.InvariantCulture));
        if (ev != 0) img.Evaluate(Channels.RGB, EvaluateOperator.Multiply, Math.Pow(2, ev));
        if (br != 0 || co != 0) img.BrightnessContrast(new Percentage(br), new Percentage(co));
        if (hi != 0 || sh != 0)
        {
            using var lut = new MagickImage(MagickColors.Black, 256, 1);
            using (var px = lut.GetPixels())
            {
                var row = new byte[256 * lut.ChannelCount];
                for (int x = 0; x < 256; x++)
                {
                    double v = x / 255.0;
                    v += sh * 4 * v * Math.Pow(1 - v, 2) * 0.6;      // lifts/darkens the low tones, fades out by the midtones
                    v += hi * 4 * v * v * (1 - v) * 0.6;              // same for the high tones
                    var b = (byte)Math.Clamp(Math.Round(v * 255), 0, 255);
                    for (int c = 0; c < lut.ChannelCount; c++) row[x * lut.ChannelCount + c] = b;
                }
                px.SetArea(0, 0, 256, 1, row);
            }
            img.Clut(lut, PixelInterpolateMethod.Bilinear, Channels.RGB);
        }
        if (sat != 0) img.Modulate(new Percentage(100), new Percentage(Math.Max(0, 100 + sat)), new Percentage(100));
    }

    // ---------------------------------------------------------------- Background

    public static MagickImage AddBackground(MagickImage img, string background, int paddingPct, int radiusPct)
    {
        uint pad = (uint)Math.Round(Math.Max(img.Width, img.Height) * paddingPct / 100.0);
        uint W = img.Width + 2 * pad, H = img.Height + 2 * pad;
        var canvas = MakeBackground(background, W, H);
        using var fg = (MagickImage)img.Clone();
        if (radiusPct > 0)
        {
            var rad = Math.Min(fg.Width, fg.Height) * radiusPct / 100.0;
            using var mask = new MagickImage(MagickColors.Transparent, fg.Width, fg.Height);
            new Drawables().FillColor(MagickColors.White).RoundRectangle(0, 0, fg.Width - 1, fg.Height - 1, rad, rad).Draw(mask);
            fg.Alpha(AlphaAction.Set);
            fg.Composite(mask, CompositeOperator.DstIn);
        }
        canvas.Composite(fg, (int)pad, (int)pad, CompositeOperator.Over);
        canvas.Alpha(AlphaAction.Off);
        canvas.Quality = img.Quality;
        return canvas;
    }

    public static MagickImage MakeBackground(string spec, uint w, uint h)
    {
        spec = spec.Trim();
        if (spec.StartsWith("image:", StringComparison.OrdinalIgnoreCase))
        {
            var path = SafePaths.ValidateInput(spec[6..].Trim().Trim('"'));
            if (FileTypeDetector.Detect(path) != FileKind.Image) throw new OrangeException("The background image isn't an image Orange can read.");
            using var col = ImageProvider.Load(path);
            var bg = (MagickImage)col[0].Clone();
            bg.AutoOrient();
            bg.Resize(new MagickGeometry(w, h) { FillArea = true });
            bg.Crop(w, h, Gravity.Center);
            bg.ResetPage();
            return bg;
        }
        string? a = null, b = null;
        if (Gradients.TryGetValue(spec.ToLowerInvariant(), out var g)) (a, b) = g;
        else if (spec.StartsWith("gradient:", StringComparison.OrdinalIgnoreCase))
        {
            var p = spec[9..].Split('-', 2);
            if (p.Length != 2) throw new OrangeException("Write a gradient as gradient:#RRGGBB-#RRGGBB.");
            (a, b) = (p[0], p[1]);
        }
        if (a != null)
        {
            var rs = new MagickReadSettings { Width = w, Height = h };
            rs.SetDefine("gradient:direction", "SouthEast");   // diagonal, like the reference backgrounds
            return new MagickImage($"gradient:{Color(a).ToHexString()}-{Color(b!).ToHexString()}", rs);
        }
        return new MagickImage(Color(spec), w, h);
    }

    public static MagickColor Color(string s)
    {
        s = s.Trim();
        if (s.StartsWith('#') && s.Length is 7 or 9 && s[1..].All(Uri.IsHexDigit)) return new MagickColor(s);
        var named = s.ToLowerInvariant() switch
        {
            "white" => "#FFFFFF", "black" => "#000000", "orange" => "#FF8A00", "red" => "#FF3B30", "blue" => "#0A84FF", "green" => "#34C759",
            "yellow" => "#FFD60A", "purple" => "#AF52DE", "pink" => "#FF2D55", "gray" or "grey" => "#8E8E93", "navy" => "#1C2A48", "brown" => "#8B5A2B",
            "transparent" => "#00000000", _ => null,
        };
        return named != null ? new MagickColor(named) : throw new OrangeException($"\"{s}\" isn't a colour. Use a name like white or #RRGGBB.");
    }

    // ---------------------------------------------------------------- Redact / annotate

    public static List<(int X, int Y, int W, int H)> ParseRects(string spec, uint imgW, uint imgH, string what)
    {
        var list = new List<(int, int, int, int)>();
        foreach (var part in spec.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var n = part.Split(',').Select(x => int.TryParse(x.Trim(), out var v) ? v : -1).ToArray();
            if (n.Length != 4 || n.Any(v => v < 0) || n[2] == 0 || n[3] == 0) throw new OrangeException($"\"{part}\" isn't an {what}. Use x,y,width,height in pixels.");
            int x = Math.Min(n[0], (int)imgW - 1), y = Math.Min(n[1], (int)imgH - 1);
            list.Add((x, y, Math.Min(n[2], (int)imgW - x), Math.Min(n[3], (int)imgH - y)));
        }
        return list;
    }

    public static void Redact(MagickImage img, List<(int X, int Y, int W, int H)> rects, string style)
    {
        if (rects.Count == 0) throw new OrangeException("Mark at least one area to redact.");
        foreach (var (x, y, w, h) in rects)
        {
            if (style == "black") { new Drawables().FillColor(MagickColors.Black).StrokeWidth(0).Rectangle(x, y, x + w - 1, y + h - 1).Draw(img); continue; }
            using var patch = (MagickImage)img.Clone();
            patch.Crop(new MagickGeometry(x, y, (uint)w, (uint)h)); patch.ResetPage();
            if (style == "pixelate")
            {
                uint block = (uint)Math.Max(8, Math.Max(w, h) / 12);
                patch.Scale(new MagickGeometry(Math.Max(1, (uint)w / block), Math.Max(1, (uint)h / block)) { IgnoreAspectRatio = true });
                patch.Scale(new MagickGeometry((uint)w, (uint)h) { IgnoreAspectRatio = true });
            }
            else patch.Blur(0, Math.Max(8, Math.Max(w, h) / 6.0));   // heavy blur; can't be undone by sharpening
            img.Composite(patch, x, y, CompositeOperator.Over);
        }
    }

    private static void Annotate(MagickImage img, OperationRequest r)
    {
        var color = Color(r.Opt("color"));
        var stroke = r.OptInt("stroke");
        bool any = false;
        foreach (var (x, y, w, h) in ParseRects(r.Opt("boxes"), img.Width, img.Height, "box"))
        {
            new Drawables().StrokeColor(color).StrokeWidth(stroke).FillColor(MagickColors.Transparent).Rectangle(x, y, x + w - 1, y + h - 1).Draw(img);
            any = true;
        }
        foreach (var part in r.Opt("arrows").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var n = part.Split(',').Select(v => double.TryParse(v.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : double.NaN).ToArray();
            if (n.Length != 4 || n.Any(double.IsNaN)) throw new OrangeException($"\"{part}\" isn't an arrow. Use x1,y1,x2,y2.");
            double ang = Math.Atan2(n[3] - n[1], n[2] - n[0]), head = Math.Max(12, stroke * 4);
            var p1 = new PointD(n[2] - head * Math.Cos(ang - 0.45), n[3] - head * Math.Sin(ang - 0.45));
            var p2 = new PointD(n[2] - head * Math.Cos(ang + 0.45), n[3] - head * Math.Sin(ang + 0.45));
            new Drawables().StrokeColor(color).StrokeWidth(stroke).StrokeLineCap(LineCap.Round).Line(n[0], n[1], n[2], n[3])
                .FillColor(color).Polygon(new PointD(n[2], n[3]), p1, p2).Draw(img);
            any = true;
        }
        var text = r.Opt("text");
        if (!string.IsNullOrWhiteSpace(text))
        {
            if (text.Length > 500) throw new OrangeException("Annotation text is limited to 500 characters.");
            new Drawables().FontPointSize(r.OptInt("size")).FillColor(color).StrokeColor(MagickColors.Transparent)
                .TextAlignment(TextAlignment.Left).Text(r.OptInt("x"), r.OptInt("y") + r.OptInt("size"), text).Draw(img);
            any = true;
        }
        if (!any) throw new OrangeException("Add some text, a box or an arrow to annotate.");
    }

    // ---------------------------------------------------------------- Metadata

    private static readonly ExifTag[] CameraTags = [ExifTag.Make, ExifTag.Model, ExifTag.LensMake, ExifTag.LensModel, ExifTag.LensSerialNumber,
        ExifTag.SerialNumber, ExifTag.OwnerName, ExifTag.Software];

    public static void EditMetadata(MagickImage img, OperationRequest r)
    {
        var remove = r.Opt("remove");
        if (remove == "all") img.Strip();
        else if (remove == "all-but-color")
        {
            var icc = img.GetColorProfile();
            img.Strip();
            if (icc != null) img.SetProfile(icc);
        }
        var exif = img.GetExifProfile() ?? new ExifProfile();
        if (remove == "location")
            foreach (var v in exif.Values.Where(v => v.Tag.ToString().StartsWith("GPS", StringComparison.Ordinal)).ToList()) exif.RemoveValue(v.Tag);
        if (remove == "camera") foreach (var t in CameraTags) exif.RemoveValue(t);
        if (remove == "location") exif.RemoveValue(ExifTag.GPSIFDOffset);

        bool changed = remove != "none";
        string artist = r.Opt("artist"), copy = r.Opt("copyright"), desc = r.Opt("description"), date = r.Opt("date");
        if (artist.Length > 0) { exif.SetValue(ExifTag.Artist, artist); changed = true; }
        if (copy.Length > 0) { exif.SetValue(ExifTag.Copyright, copy); changed = true; }
        if (desc.Length > 0) { exif.SetValue(ExifTag.ImageDescription, desc); changed = true; }
        if (date.Length > 0)
        {
            if (!DateTime.TryParse(date, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) throw new OrangeException($"\"{date}\" isn't a date. Use yyyy-mm-dd hh:mm.");
            var s = d.ToString("yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture);
            exif.SetValue(ExifTag.DateTimeOriginal, s); exif.SetValue(ExifTag.DateTime, s); changed = true;
        }
        if (!changed) throw new OrangeException("Choose what to remove or fill in a field to change.");
        if (remove is not ("all" or "all-but-color") || exif.Values.Any()) img.SetProfile(exif);
    }

    // ---------------------------------------------------------------- Collage

    public static MagickImage Collage(IReadOnlyList<string> files, int columns, int cell, int gap, string background, bool contain, CancellationToken ct)
    {
        int n = files.Count;
        if (n > 100) throw new OrangeException("A collage can hold up to 100 images.");
        int cols = columns > 0 ? Math.Min(columns, n) : (int)Math.Ceiling(Math.Sqrt(n));
        int rows = (int)Math.Ceiling(n / (double)cols);
        long W = cols * cell + (cols + 1) * gap, H = rows * cell + (rows + 1) * gap;
        if (W * H > EngineLimits.Current.MaxImagePixels) throw new OrangeException("That collage would be too large. Use a smaller cell size.");
        var canvas = MakeBackground(background, (uint)W, (uint)H);
        for (int i = 0; i < n; i++)
        {
            ct.ThrowIfCancellationRequested();
            using var col = ImageProvider.Load(files[i]);
            using var tile = (MagickImage)col[0].Clone();
            tile.AutoOrient();
            if (contain) tile.Resize(new MagickGeometry((uint)cell, (uint)cell));
            else { tile.Resize(new MagickGeometry((uint)cell, (uint)cell) { FillArea = true }); tile.Crop((uint)cell, (uint)cell, Gravity.Center); tile.ResetPage(); }
            int cx = gap + (i % cols) * (cell + gap) + (cell - (int)tile.Width) / 2;
            int cy = gap + (i / cols) * (cell + gap) + (cell - (int)tile.Height) / 2;
            canvas.Composite(tile, cx, cy, CompositeOperator.Over);
        }
        return canvas;
    }

    // ---------------------------------------------------------------- QR

    /// <summary>Decodes every QR code it can find. The text is returned as data; nothing is opened or followed.</summary>
    public static List<string> ReadQr(string path)
    {
        using var col = ImageProvider.Load(path);
        using var img = (MagickImage)col[0].Clone();
        img.AutoOrient();
        if (Math.Max(img.Width, img.Height) > 3000) img.Resize(new MagickGeometry(3000, 3000));
        img.BackgroundColor = MagickColors.White;
        img.Alpha(AlphaAction.Remove);
        img.ColorSpace = ColorSpace.sRGB;
        using var pixels = img.GetPixels();
        var bytes = pixels.ToByteArray(PixelMapping.RGB) ?? throw new OrangeException("Couldn't read the image pixels.");
        var source = new RGBLuminanceSource(bytes, (int)img.Width, (int)img.Height, RGBLuminanceSource.BitmapFormat.RGB24);
        var reader = new MultiFormatReader();
        var hints = new Dictionary<DecodeHintType, object>
        {
            [DecodeHintType.TRY_HARDER] = true,
            [DecodeHintType.POSSIBLE_FORMATS] = new List<BarcodeFormat> { BarcodeFormat.QR_CODE },
        };
        reader.Hints = hints;
        var results = new List<string>();
        try
        {
            var multi = new ZXing.Multi.QrCode.QRCodeMultiReader();
            var many = multi.decodeMultiple(new BinaryBitmap(new HybridBinarizer(source)), hints);
            if (many != null) results.AddRange(many.Select(x => x.Text));
        }
        catch (ReaderException) { }
        if (results.Count == 0)
        {
            var one = reader.decode(new BinaryBitmap(new HybridBinarizer(source))) ?? reader.decode(new BinaryBitmap(new GlobalHistogramBinarizer(source)));
            if (one != null) results.Add(one.Text);
        }
        // Strip control characters so decoded text can't spoof the UI or a log line.
        return results.Distinct().Select(t => new string(t.Where(c => !char.IsControl(c) || c is '\n' or '\t').ToArray())).ToList();
    }
}
