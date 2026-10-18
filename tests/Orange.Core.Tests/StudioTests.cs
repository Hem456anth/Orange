using System.Globalization;
using ImageMagick;
using Orange.Core;
using Orange.Core.Providers;
using UglyToad.PdfPig;

namespace Orange.Core.Tests;

/// <summary>Editing-panel operations. Each test measures the pixels/metadata/duration that changed, not just that a file exists.</summary>
public class StudioTests : EngineTest
{
    private static double Mean(MagickImage img) => img.Statistics().Composite().Mean / Quantum.Max;

    [Fact]
    public async Task Edit_exposure_and_shadows_change_real_pixels()
    {
        using var src = new MagickImage(F("sample.png"));
        using var brighter = new MagickImage((await Run("image.edit", F("sample.png"), O(("exposure", "1")))).Single());
        Assert.True(Mean(brighter) > Mean(src) + 0.02, $"exposure +1 should brighten ({Mean(src):0.000} → {Mean(brighter):0.000})");
        using var darker = new MagickImage((await Run("image.edit", F("sample.png"), O(("brightness", "-40"), ("contrast", "10")))).Single());
        Assert.True(Mean(darker) < Mean(src) - 0.02);
        using var lifted = new MagickImage((await Run("image.edit", F("sample.png"), O(("shadows", "80")))).Single());
        Assert.True(Mean(lifted) > Mean(src));
        using var gray = new MagickImage((await Run("image.edit", F("sample.png"), O(("saturation", "-100")))).Single());
        Assert.True(gray.ColorSpace == ColorSpace.Gray || gray.ChannelCount <= 2 || gray.Statistics().GetChannel(PixelChannel.Red)!.Mean == gray.Statistics().GetChannel(PixelChannel.Blue)!.Mean);
    }

    [Fact]
    public async Task Edit_with_no_changes_still_produces_identical_dimensions()
    {
        using var o = new MagickImage((await Run("image.edit", F("sample.png"), O(("rotate", "90")))).Single());
        Assert.Equal(200u, o.Width); Assert.Equal(320u, o.Height);
    }

    [Fact]
    public async Task Add_background_fills_transparency_and_pads()
    {
        var o = (await Run("image.add-background", F("sample-alpha.png"), O(("background", "#FF0000"), ("padding", "10")))).Single();
        using var src = new MagickImage(F("sample-alpha.png"));
        using var img = new MagickImage(o);
        Assert.False(img.HasAlpha);
        Assert.True(img.Width > src.Width);
        var corner = img.GetPixels().GetPixel(0, 0);
        Assert.Equal(255, corner[0]); Assert.Equal(0, corner[1]);
        var g = (await Run("image.add-background", F("sample-alpha.png"), O(("background", "sunset"), ("padding", "0"), ("radius", "10")))).Single();
        using var gi = new MagickImage(g);
        Assert.NotEqual(gi.GetPixels().GetPixel(0, 0)[0], gi.GetPixels().GetPixel((int)gi.Width - 1, 0)[0]);   // a real gradient, not flat
    }

    [Fact]
    public async Task Bad_background_colour_is_an_actionable_error()
    {
        var jobs = Runner.Plan(Registry.Get("image.add-background"), [F("sample.png")], O(("background", "not-a-colour")), Out);
        await Runner.RunAsync(jobs, null, default);
        Assert.Contains("isn't a colour", jobs[0].Result!.Error);
    }

    [Theory, InlineData("black"), InlineData("pixelate"), InlineData("blur")]
    public async Task Redact_changes_only_the_marked_area(string style)
    {
        var busy = Path.Combine(Out, "busy.png");
        using (var b = new MagickImage("plasma:fractal", new MagickReadSettings { Width = 320, Height = 200 })) { b.AddNoise(NoiseType.Gaussian); b.Write(busy); }
        using var src = new MagickImage(busy);
        using var o = new MagickImage((await Run("image.redact", busy, O(("regions", "10,10,80,60"), ("style", style)))).Single());
        using var srcArea = (MagickImage)src.Clone(); srcArea.Crop(new MagickGeometry(10, 10, 80, 60));
        using var outArea = (MagickImage)o.Clone(); outArea.Crop(new MagickGeometry(10, 10, 80, 60));
        Assert.True(srcArea.Compare(outArea, ErrorMetric.RootMeanSquared) > 0.01, "the redacted area must change");
        using var srcRest = (MagickImage)src.Clone(); srcRest.Crop(new MagickGeometry(200, 120, 100, 60));
        using var outRest = (MagickImage)o.Clone(); outRest.Crop(new MagickGeometry(200, 120, 100, 60));
        Assert.True(srcRest.Compare(outRest, ErrorMetric.RootMeanSquared) < 0.01, "the rest must be untouched");
        if (style == "black") Assert.Equal(0, o.GetPixels().GetPixel(40, 40)[0]);
    }

    [Fact]
    public async Task Annotate_draws_text_boxes_and_arrows()
    {
        using var src = new MagickImage(F("sample.png"));
        using var o = new MagickImage((await Run("image.annotate", F("sample.png"),
            O(("text", "Hello"), ("size", "30"), ("boxes", "5,5,100,50"), ("arrows", "200,150,280,60"), ("color", "#00FF00")))).Single());
        Assert.True(src.Compare(o, ErrorMetric.RootMeanSquared) > 0.01);
        var edge = o.GetPixels().GetPixel(5, 30);   // on the box's left edge
        Assert.True(edge[1] > 200 && edge[0] < 60);
    }

    [Fact]
    public async Task Metadata_removes_location_but_keeps_other_tags_and_edits_fields()
    {
        var gps = Path.Combine(Out, "gps.jpg");
        using (var img = new MagickImage(F("sample.jpg")))
        {
            var exif = new ExifProfile();
            exif.SetValue(ExifTag.GPSLatitude, [new Rational(16, 1), new Rational(30, 1), new Rational(0, 1)]);
            exif.SetValue(ExifTag.GPSLatitudeRef, "N");
            exif.SetValue(ExifTag.Make, "TestCam");
            exif.SetValue(ExifTag.Artist, "Old Name");
            img.SetProfile(exif); img.Write(gps);
        }
        var before = await MetadataReader.ReadAsync(gps);
        Assert.Contains(before, e => e.Group == "Location" && e.Sensitive);

        var o = (await Run("image.metadata", gps, O(("remove", "location"), ("artist", "Hemanth"), ("copyright", "© 2026")))).Single();
        var after = await MetadataReader.ReadAsync(o);
        Assert.DoesNotContain(after, e => e.Group == "Location");
        Assert.Contains(after, e => e.Key == "Make" && e.Value == "TestCam");
        Assert.Contains(after, e => e.Key == "Artist" && e.Value == "Hemanth");
        Assert.Contains(after, e => e.Key == "Copyright" && e.Value == "© 2026");

        var cam = (await Run("image.metadata", gps, O(("remove", "camera")))).Single();
        Assert.DoesNotContain(await MetadataReader.ReadAsync(cam), e => e.Key == "Make");
        var all = (await Run("image.metadata", gps, O(("remove", "all")))).Single();
        Assert.DoesNotContain(await MetadataReader.ReadAsync(all), e => e.Group is "EXIF" or "Location");
    }

    [Fact]
    public async Task Collage_lays_out_a_grid()
    {
        var o = (await Run("image.collage", new[] { F("sample.png"), F("sample.jpg"), F("sample.bmp") }, O(("cell", "200"), ("gap", "10"), ("columns", "3")))).Single();
        using var img = new MagickImage(o);
        Assert.Equal(3u * 200 + 4 * 10, img.Width);
        Assert.Equal(200u + 2 * 10, img.Height);
    }

    [Fact]
    public async Task Qr_reader_returns_the_text_without_acting_on_it()
    {
        var path = Path.Combine(Out, "qr.png");
        var url = "https://example.com/?q=orange&x=1";
        var matrix = new ZXing.QrCode.QRCodeWriter().encode(url, ZXing.BarcodeFormat.QR_CODE, 300, 300);
        using (var img = new MagickImage(MagickColors.White, 300, 300))
        {
            var d = new ImageMagick.Drawing.Drawables().FillColor(MagickColors.Black);
            for (int y = 0; y < 300; y++) for (int x = 0; x < 300; x++) if (matrix[x, y]) d.Rectangle(x, y, x, y);
            d.Draw(img); img.Write(path);
        }
        var op = Registry.Get("image.qr-read");
        var jobs = Runner.Plan(op, [path], null, Out);
        await Runner.RunAsync(jobs, null, default);
        Assert.Equal(JobState.Succeeded, jobs[0].State);
        Assert.Empty(jobs[0].Result!.Outputs);
        Assert.Equal(url, jobs[0].Result!.Detail);
        // No QR → a clear message, not a crash.
        var none = Runner.Plan(op, [F("sample.png")], null, Out);
        await Runner.RunAsync(none, null, default);
        Assert.Contains("No QR code", none[0].Result!.Error);
    }

    [Fact]
    public async Task Compress_to_target_size_lands_under_target()
    {
        var big = Path.Combine(Out, "big.jpg");
        using (var img = new MagickImage("plasma:fractal", new MagickReadSettings { Width = 1600, Height = 1200 })) { img.Quality = 98; img.Write(big); }
        var size = new FileInfo(big).Length;
        long target = size / 5;
        var jobs = Runner.Plan(Registry.Get("image.compress"), [big], O(("targetKB", (target / 1024).ToString(CultureInfo.InvariantCulture))), Out);
        await Runner.RunAsync(jobs, null, default);
        Assert.True(jobs[0].State == JobState.Succeeded, jobs[0].Result?.Error);
        var o = jobs[0].Result!.Outputs.Single();
        Assert.InRange(new FileInfo(o).Length, 1, target / 1024 * 1024);
        Assert.Contains("KB", jobs[0].Result!.Detail);
        using var check = new MagickImage(o); Assert.True(check.Width > 0);
    }

    [Fact]
    public async Task Svg_renders_but_hostile_svgs_are_refused()
    {
        var png = (await Run("image.to.png", F("sample.svg"))).Single();
        using (var img = new MagickImage(png)) Assert.True(img.Width >= 200);
        foreach (var bad in new[] { "xxe.svg", "external-href.svg" })
        {
            var jobs = Runner.Plan(Registry.Get("image.to.png"), [F("hostile/" + bad)], null, Out);
            await Runner.RunAsync(jobs, null, default);
            Assert.Equal(JobState.Failed, jobs[0].State);
            Assert.Contains("won't open", jobs[0].Result!.Error);
        }
    }

    [Fact]
    public async Task Pdf_reorder_keeps_the_order_given()
    {
        var o = (await Run("pdf.reorder", F("sample.pdf"), O(("order", "3,1,2")))).Single();
        using var src = PdfDocument.Open(F("sample.pdf"));
        using var doc = PdfDocument.Open(o);
        Assert.Equal(3, doc.NumberOfPages);
        Assert.Equal(src.GetPage(3).Text, doc.GetPage(1).Text);
        Assert.Equal(src.GetPage(1).Text, doc.GetPage(2).Text);
        Assert.Throws<OrangeException>(() => PageSelection.ParseOrdered("1,1", 3));
        Assert.Equal([3, 2, 1], PageSelection.ParseOrdered("3-1", 3));
    }

    [Fact]
    public async Task Zip_from_files_and_folders()
    {
        var folder = Path.Combine(Out, "My Folder"); Directory.CreateDirectory(Path.Combine(folder, "sub"));
        File.WriteAllText(Path.Combine(folder, "a.txt"), "a"); File.WriteAllText(Path.Combine(folder, "sub", "b.txt"), "b");
        var zip = await ArchiveProvider.CreateZipAsync([folder, F("sample.txt")], Out, default);
        Assert.EndsWith(".zip", zip);
        using var z = System.IO.Compression.ZipFile.OpenRead(zip);
        Assert.Contains(z.Entries, e => e.FullName == "My Folder/sub/b.txt");
        Assert.Contains(z.Entries, e => e.FullName == "sample.txt");
    }
}

public class AudioStudioTests : EngineTest
{
    private static async Task<double> Duration(string p)
    {
        var o = await ProcessRunner.RunAsync(ToolLocator.Default.Require("ffprobe"), ["-v", "error", "-show_entries", "format=duration", "-of", "csv=p=0", p], default);
        return double.Parse(o.StdOut.Trim(), CultureInfo.InvariantCulture);
    }

    /// <summary>RMS level of a section in dBFS, measured by FFmpeg's astats on the output.</summary>
    private static async Task<double> Rms(string p, double start, double end)
    {
        var o = await ProcessRunner.RunAsync(ToolLocator.Default.Require("ffmpeg"),
            ["-hide_banner", "-nostdin", "-ss", start.ToString(CultureInfo.InvariantCulture), "-to", end.ToString(CultureInfo.InvariantCulture), "-i", p, "-af", "astats=metadata=0", "-f", "null", "-"], default);
        var line = o.StdErr.Split('\n').Last(l => l.Contains("RMS level dB"));
        var v = line.Split(':').Last().Trim();
        return v.Contains("inf") ? -200 : double.Parse(v, CultureInfo.InvariantCulture);
    }

    [SkippableFact]
    public async Task Bleep_with_silence_mutes_only_the_section()
    {
        var o = (await Run("audio.bleep", F("sample.wav"), O(("ranges", "0.5-1.0"), ("sound", "silence")))).Single();
        Assert.True(await Rms(o, 0.6, 0.9) < -80);
        Assert.True(await Rms(o, 1.2, 1.6) > -40);
    }

    [SkippableFact]
    public async Task Bleep_with_tone_replaces_the_section()
    {
        var o = (await Run("audio.bleep", F("sample.wav"), O(("ranges", "0:00.5-0:01"), ("sound", "beep")))).Single();
        Assert.True(await Rms(o, 0.6, 0.9) > -35);
        Assert.InRange(await Duration(o), 1.8, 2.2);
    }

    [SkippableFact]
    public async Task Silence_trim_removes_padding()
    {
        NeedTool("ffmpeg");
        var padded = Path.Combine(Out, "padded.wav");
        var r = await ProcessRunner.RunAsync(ToolLocator.Default.Require("ffmpeg"), ["-hide_banner", "-y", "-f", "lavfi", "-i", "anullsrc=r=44100:cl=mono:d=1", "-i", F("sample.wav"),
            "-f", "lavfi", "-i", "anullsrc=r=44100:cl=mono:d=1", "-filter_complex", "[0][1][2]concat=n=3:v=0:a=1", padded], default);
        Assert.Equal(0, r.ExitCode);
        var src = await Duration(padded);
        var o = (await Run("audio.silence-trim", padded)).Single();
        Assert.True(await Duration(o) < src - 1.5, $"{src} → {await Duration(o)}");
    }

    [SkippableFact]
    public async Task Split_speed_and_metadata()
    {
        var parts = (await Run("audio.split", F("sample.wav"), O(("every", "1")))).Single();
        Assert.True(Directory.GetFiles(parts).Length >= 2);
        Assert.InRange(await Duration((await Run("audio.speed", F("sample.wav"), O(("factor", "2")))).Single()), 0.9, 1.1);
        var tagged = (await Run("audio.metadata", F("sample.mp3"), O(("title", "Orange Test"), ("artist", "Hemanth")))).Single();
        var meta = await MetadataReader.ReadAsync(tagged);
        Assert.Contains(meta, e => e.Key.Equals("title", StringComparison.OrdinalIgnoreCase) && e.Value == "Orange Test");
    }

    [SkippableFact]
    public async Task Waveform_has_the_requested_resolution_and_is_normalised()
    {
        NeedTool("ffmpeg");
        var peaks = await new MediaProvider(ToolLocator.Default).WaveformAsync(F("sample.wav"), 200, default);
        Assert.Equal(200, peaks.Length);
        Assert.Equal(1f, peaks.Max(), 3);
        Assert.True(peaks.Count(p => p > 0.05) > 100);
    }

    [SkippableFact]
    public async Task Video_and_audio_compress_to_target_size()
    {
        var info = new FileInfo(F("sample.mp4")).Length;
        double targetMB = Math.Max(0.05, info / 1048576.0 / 2);
        var jobs = Runner.Plan(Registry.Get("video.compress"), [F("sample.mp4")], O(("targetMB", targetMB.ToString(CultureInfo.InvariantCulture))), Out);
        NeedTool("ffmpeg");
        await Runner.RunAsync(jobs, null, default);
        if (jobs[0].State == JobState.Failed) Assert.Contains("too small", jobs[0].Result!.Error);   // a 2-second fixture can be below the floor
        else Assert.True(new FileInfo(jobs[0].Result!.Outputs[0]).Length <= targetMB * 1048576);

        var a = Runner.Plan(Registry.Get("audio.compress"), [F("sample.wav")], O(("targetMB", "0.02")), Out);
        await Runner.RunAsync(a, null, default);
        Assert.True(a[0].State == JobState.Succeeded, a[0].Result?.Error);
        Assert.True(new FileInfo(a[0].Result!.Outputs[0]).Length <= 0.02 * 1048576);
    }

    [Fact]
    public void Bleep_ranges_are_validated()
    {
        Assert.Throws<OrangeException>(() => MediaProvider.ParseRanges("2-1", 10));
        Assert.Throws<OrangeException>(() => MediaProvider.ParseRanges("20-21", 10));
        Assert.Equal(2, MediaProvider.ParseRanges("0:01-0:02, 3-4", 10).Count);
    }
}
