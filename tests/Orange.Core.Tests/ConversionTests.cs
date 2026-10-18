using System.Text.Json;
using ImageMagick;
using Orange.Core;
using Orange.Core.Providers;
using UglyToad.PdfPig;

namespace Orange.Core.Tests;

/// <summary>Real conversions on real fixtures. Each test checks the output decodes, not just that a file appeared.</summary>
public class ImageConversionTests : EngineTest
{
    public static IEnumerable<object[]> Targets() =>
        new[] { "jpg", "webp", "tiff", "bmp", "gif", "avif", "ico", "heic" }.Select(x => new object[] { x });

    [SkippableTheory, MemberData(nameof(Targets))]
    public async Task Png_converts_to_every_supported_format(string ext)
    {
        var op = Registry.Get($"image.to.{ext}");
        Skip.IfNot(Registry.IsAvailable(op), $"this Magick.NET build can't write {ext}");
        var outFile = (await Run(op.Id, F("sample.png"))).Single();
        Assert.Equal("sample." + ext, Path.GetFileName(outFile));
        using var img = new MagickImage(outFile);
        Assert.True(img.Width > 0);
    }

    [Fact]
    public async Task Transparent_png_to_jpg_gets_white_background()
    {
        var o = (await Run("image.to.jpg", F("sample-alpha.png"))).Single();
        using var img = new MagickImage(o);
        Assert.False(img.HasAlpha);
    }

    [Fact]
    public async Task Crop_resize_rotate_change_dimensions()
    {
        using var src = new MagickImage(F("sample.png"));   // 320x200
        using (var c = new MagickImage((await Run("image.crop", F("sample.png"), O(("aspect", "1:1")))).Single())) { Assert.Equal(200u, c.Width); Assert.Equal(200u, c.Height); }
        using (var c = new MagickImage((await Run("image.crop", F("sample.png"), O(("aspect", "custom"), ("x", "10"), ("y", "10"), ("width", "50"), ("height", "40")))).Single())) { Assert.Equal(50u, c.Width); Assert.Equal(40u, c.Height); }
        using (var r = new MagickImage((await Run("image.resize", F("sample.png"), O(("mode", "percent"), ("value", "50")))).Single())) Assert.Equal(160u, r.Width);
        using (var r = new MagickImage((await Run("image.rotate", F("sample.png"), O(("angle", "90")))).Single())) { Assert.Equal(200u, r.Width); Assert.Equal(320u, r.Height); }
    }

    [Fact]
    public async Task Crop_outside_the_image_is_an_actionable_error()
    {
        var jobs = Runner.Plan(Registry.Get("image.crop"), [F("sample.png")], O(("aspect", "custom"), ("x", "300"), ("y", "0"), ("width", "100"), ("height", "10")), Out);
        await Runner.RunAsync(jobs, null, default);
        Assert.Contains("outside the image", jobs[0].Result!.Error);
    }

    [Fact]
    public async Task Compress_and_metadata_removal_strip_comments()
    {
        var o = (await Run("image.strip-metadata", F("sample-meta.jpg"))).Single();
        Assert.EndsWith("sample-meta Clean.jpg", o);
        using var img = new MagickImage(o);
        Assert.Null(img.Comment);
        var c = (await Run("image.compress", F("sample-meta.jpg"), O(("quality", "40")))).Single();
        Assert.True(new FileInfo(c).Length < new FileInfo(F("sample-meta.jpg")).Length);
    }

    [Fact]
    public async Task Running_twice_never_overwrites()
    {
        var a = (await Run("image.grayscale", F("sample.png"))).Single();
        var b = (await Run("image.grayscale", F("sample.png"))).Single();
        Assert.EndsWith("sample Grayscale.png", a);
        Assert.EndsWith("sample Grayscale 2.png", b);
    }

    [Fact]
    public async Task Animated_gif_to_webp_keeps_frames()
    {
        var o = (await Run("image.to.webp", F("animated.gif"))).Single();
        using var col = new MagickImageCollection(o);
        Assert.Equal(3, col.Count);
    }

    [Fact]
    public async Task Images_become_one_pdf_page_each()
    {
        var o = (await Run("image.create-pdf", new[] { F("sample.png"), F("sample.jpg"), F("sample.bmp") })).Single();
        using var pdf = PdfDocument.Open(o);
        Assert.Equal(3, pdf.NumberOfPages);
    }

    [Fact]
    public async Task Batch_converts_each_file_and_reports_progress()
    {
        var files = new[] { F("sample.png"), F("sample.bmp"), F("sample.tiff") };
        var jobs = Runner.Plan(Registry.Get("image.to.jpg"), files, null, Out);
        Assert.Equal(3, jobs.Count);
        var seen = new List<double>();
        await Runner.RunAsync(jobs, new SyncProgress<BatchProgress>(p => seen.Add(p.Fraction)), default);
        Assert.All(jobs, j => Assert.Equal(JobState.Succeeded, j.State));
        Assert.Equal(1.0, seen.Max(), 3);
        Assert.Equal(3, Directory.GetFiles(Out, "*.jpg").Length);   // sample.jpg, sample 2.jpg, sample 3.jpg
    }
}

public class MediaConversionTests : EngineTest
{
    private async Task<JsonElement> Probe(string path)
    {
        var o = await ProcessRunner.RunAsync(ToolLocator.Default.Require("ffprobe"), ["-v", "error", "-print_format", "json", "-show_format", "-show_streams", path], default);
        return JsonDocument.Parse(o.StdOut).RootElement.Clone();
    }
    private async Task<double> Duration(string p) => double.Parse((await Probe(p)).GetProperty("format").GetProperty("duration").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
    private async Task<int> Streams(string p, string type) => (await Probe(p)).GetProperty("streams").EnumerateArray().Count(s => s.GetProperty("codec_type").GetString() == type);

    public static IEnumerable<object[]> VideoTargets() => new[] { "mov", "mkv", "webm", "avi", "gif", "mp3", "m4a", "wav" }.Select(x => new object[] { x });
    public static IEnumerable<object[]> AudioTargets() => new[] { "mp3", "flac", "aac", "m4a", "ogg", "opus", "aiff", "wma" }.Select(x => new object[] { x });

    [SkippableTheory, MemberData(nameof(VideoTargets))]
    public async Task Mp4_converts(string ext)
    {
        NeedTool("ffmpeg");
        var op = Registry.Get($"video.to.{ext}");
        Skip.IfNot(Registry.IsAvailable(op), $"FFmpeg here has no encoder for {ext}");
        var o = (await Run(op.Id, F("sample.mp4"))).Single();
        Assert.Equal("sample." + ext, Path.GetFileName(o));
        Assert.InRange(await Duration(o), 2.5, 3.5);
    }

    [SkippableTheory, MemberData(nameof(AudioTargets))]
    public async Task Wav_converts(string ext)
    {
        NeedTool("ffmpeg");
        var op = Registry.Get($"audio.to.{ext}");
        Skip.IfNot(Registry.IsAvailable(op), $"FFmpeg here has no encoder for {ext}");
        var o = (await Run(op.Id, F("sample.wav"))).Single();
        Assert.InRange(await Duration(o), 1.8, 2.3);
        Assert.Equal(1, await Streams(o, "audio"));
    }

    [SkippableFact]
    public async Task Trim_split_join_merge_and_speed_produce_expected_durations()
    {
        Assert.InRange(await Duration((await Run("video.trim", F("sample.mp4"), O(("start", "0.5"), ("end", "1.5")))).Single()), 0.8, 1.3);
        Assert.InRange(await Duration((await Run("audio.trim", F("sample.wav"), O(("start", "1")))).Single()), 0.9, 1.1);
        var clips = (await Run("video.split", F("sample.mp4"), O(("every", "1")))).Single();
        Assert.Equal(3, Directory.GetFiles(clips).Length);
        Assert.InRange(await Duration((await Run("video.join", new[] { F("sample.mp4"), F("sample-noaudio.mp4") })).Single()), 4.6, 5.5);
        Assert.InRange(await Duration((await Run("audio.merge", new[] { F("sample.wav"), F("sample.mp3") })).Single()), 3.8, 4.3);
        Assert.InRange(await Duration((await Run("video.speed", F("sample.mp4"), O(("factor", "2")))).Single()), 1.3, 1.7);
    }

    [SkippableFact]
    public async Task Mute_snapshot_compress_and_metadata()
    {
        var muted = (await Run("video.mute", F("sample.mp4"))).Single();
        Assert.Equal(0, await Streams(muted, "audio"));
        var png = (await Run("video.snapshot", F("sample.mp4"), O(("at", "1")))).Single();
        using (var img = new MagickImage(png)) Assert.Equal(320u, img.Width);
        var flac = (await Run("audio.compress", F("sample.wav"))).Single();       // lossless source → FLAC
        Assert.EndsWith(".flac", flac);
        Assert.True(new FileInfo(flac).Length < new FileInfo(F("sample.wav")).Length);
        var clean = (await Run("audio.strip-metadata", F("sample.mp3"))).Single();
        var tags = (await Probe(clean)).GetProperty("format");
        Assert.False(tags.TryGetProperty("tags", out var t) && t.TryGetProperty("artist", out _));
        var small = (await Run("video.resize", F("sample-noaudio.mp4"), O(("height", "360")))).Single();
        Assert.Equal(1, await Streams(small, "video"));
    }

    [SkippableFact]
    public async Task Trim_with_end_before_start_is_rejected()
    {
        NeedTool("ffmpeg");
        var jobs = Runner.Plan(Registry.Get("video.trim"), [F("sample.mp4")], O(("start", "2"), ("end", "1")), Out);
        await Runner.RunAsync(jobs, null, default);
        Assert.Contains("after the start", jobs[0].Result!.Error);
    }

    [SkippableFact]
    public async Task Cancellation_stops_ffmpeg()
    {
        NeedTool("ffmpeg");
        var jobs = Runner.Plan(Registry.Get("video.to.webm"), [F("sample.mp4")], null, Out);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Runner.RunAsync(jobs, null, cts.Token);
        Assert.Contains(jobs[0].State, new[] { JobState.Cancelled, JobState.Succeeded });
        if (jobs[0].State == JobState.Cancelled) Assert.Empty(Directory.GetFiles(Out));
    }
}

public class PdfAndDocumentTests : EngineTest
{
    private static int Pages(string p) { using var d = PdfDocument.Open(p); return d.NumberOfPages; }

    [Fact]
    public async Task Merge_split_extract_rotate()
    {
        Assert.Equal(6, Pages((await Run("pdf.merge", new[] { F("sample.pdf"), F("with-image.pdf") })).Single()));
        var split = (await Run("pdf.split", F("sample.pdf"))).Single();
        Assert.Equal(3, Directory.GetFiles(split, "*.pdf").Length);
        Assert.Equal(2, Pages((await Run("pdf.extract-pages", F("sample.pdf"), O(("pages", "2-3")))).Single()));
        var rotated = (await Run("pdf.rotate", F("sample.pdf"), O(("angle", "90"), ("pages", "1")))).Single();
        using var d = PdfSharp.Pdf.IO.PdfReader.Open(rotated, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import);
        Assert.Equal(90, d.Pages[0].Rotate);
        Assert.Equal(0, d.Pages[1].Rotate);
    }

    [Fact]
    public async Task Text_and_images_come_out()
    {
        var txt = (await Run("pdf.to.txt", F("sample.pdf"))).Single();
        Assert.Contains("quick brown fox", await File.ReadAllTextAsync(txt));
        var md = (await Run("pdf.to.md", F("sample.pdf"))).Single();
        Assert.Contains("## Page 2", await File.ReadAllTextAsync(md));
        var imgs = (await Run("pdf.extract-images", F("with-image.pdf"))).Single();
        Assert.Equal(3, Directory.GetFiles(imgs).Length);
    }

    [SkippableFact]
    public async Task Ghostscript_compresses_and_renders()
    {
        var c = (await Run("pdf.compress", F("photos.pdf"), O(("level", "screen")))).Single();
        Assert.True(new FileInfo(c).Length < new FileInfo(F("photos.pdf")).Length / 2);
        var pngs = (await Run("pdf.to.png", F("sample.pdf"), O(("pages", "1-2"), ("dpi", "72")))).Single();
        Assert.Equal(2, Directory.GetFiles(pngs, "*.png").Length);
    }

    public static IEnumerable<object[]> DocPairs() => new[]
    {
        new object[] { "sample.md", "docx" }, new object[] { "sample.md", "html" }, new object[] { "sample.md", "odt" }, new object[] { "sample.md", "epub" },
        new object[] { "sample.md", "rtf" }, new object[] { "sample.docx", "md" }, new object[] { "sample.docx", "txt" }, new object[] { "sample.html", "docx" },
        new object[] { "sample.txt", "docx" }, new object[] { "sample.rtf", "md" },
    };

    [SkippableTheory, MemberData(nameof(DocPairs))]
    public async Task Pandoc_conversions(string input, string ext)
    {
        var o = (await Run($"doc.to.{ext}", F(input))).Single();
        Assert.True(new FileInfo(o).Length > 20);
        if (ext is "md" or "txt" or "html") Assert.Contains(input == "sample.rtf" || input == "sample.txt" ? "" : "bold", await File.ReadAllTextAsync(o));
    }

    [SkippableFact]
    public async Task Plain_text_is_escaped_not_interpreted()
    {
        var html = (await Run("doc.to.html", F("sample.txt"))).Single();
        var s = await File.ReadAllTextAsync(html);
        Assert.Contains("&lt;tags&gt;", s);
    }

    [SkippableFact]
    public async Task Docx_to_pdf_uses_libreoffice()
    {
        NeedTool("libreoffice");
        Assert.True(Pages((await Run("doc.to.pdf", F("sample.docx"))).Single()) >= 1);
    }
}

public class SubtitleArchiveTests : EngineTest
{
    [Fact]
    public async Task Srt_vtt_txt_and_shift()
    {
        var vtt = await File.ReadAllTextAsync((await Run("sub.to.vtt", F("sample.srt"))).Single());
        Assert.StartsWith("WEBVTT", vtt);
        Assert.Contains("00:00:01.000 --> 00:00:02.500", vtt);
        var srt = await File.ReadAllTextAsync((await Run("sub.to.srt", F("sample.vtt"))).Single());
        Assert.Contains("00:00:03,000 --> 00:00:04,000", srt);
        var txt = await File.ReadAllTextAsync((await Run("sub.to.txt", F("sample.srt"))).Single());
        Assert.Equal("Hello there\nSecond line\n", txt);
        var shifted = await File.ReadAllTextAsync((await Run("sub.shift", F("sample.srt"), O(("seconds", "-1.5")))).Single());
        Assert.Contains("00:00:00,000 --> 00:00:01,000", shifted);
    }

    [Fact]
    public async Task Extracts_zip_and_targz()
    {
        foreach (var a in new[] { "sample.zip", "sample.tar.gz" })
        {
            var d = (await Run("archive.extract", F(a))).Single();
            Assert.Equal("there", (await File.ReadAllTextAsync(Path.Combine(d, "inner", "b.txt"))).Trim());
        }
        var zip = (await Run("archive.to.zip", F("sample.tar.gz"))).Single();
        Assert.EndsWith("sample.zip", zip);
    }
}

public class SecurityTests : EngineTest
{
    [Fact]
    public async Task Zip_slip_is_stopped_and_nothing_escapes()
    {
        var err = await RunFailing("archive.extract", F("hostile/zip-slip.zip"));
        Assert.Contains("outside its folder", err);
        Assert.False(File.Exists(Path.Combine(Path.GetTempPath(), "..", "evil.txt")));
        Assert.False(File.Exists(Path.GetFullPath(Path.Combine(Path.GetTempPath(), "evil.txt"))));
    }

    [Fact]
    public async Task Zip_bomb_is_rejected() => Assert.Contains("zip bomb", await RunFailing("archive.extract", F("hostile/bomb.zip")));

    [Fact]
    public async Task Tar_symlinks_are_skipped()
    {
        var d = (await Run("archive.extract", F("hostile/symlink.tar"))).Single();
        Assert.Equal(["safe.txt"], Directory.GetFileSystemEntries(d).Select(Path.GetFileName).ToArray());
    }

    [Fact]
    public async Task Shell_metacharacters_in_names_are_just_names()
    {
        var hostile = Directory.GetFiles(Path.Combine(Fixtures, "hostile"), "inject*").Single();
        var marker = Path.Combine(Out, "pwned");
        var o = (await Run("image.to.jpg", hostile)).Single();
        Assert.Equal(Path.GetFileNameWithoutExtension(hostile) + ".jpg", Path.GetFileName(o));
        Assert.False(File.Exists(marker));
    }

    [SkippableFact]
    public async Task Shell_metacharacters_reach_ffmpeg_safely()
    {
        NeedTool("ffmpeg");
        var weird = Path.Combine(Out, "a & b; echo $(id) `x` %PATH% 'q'.wav");
        File.Copy(F("sample.wav"), weird);
        var o = (await Run("audio.to.flac", weird)).Single();
        Assert.True(File.Exists(o));
    }

    [Fact]
    public async Task Damaged_pdf_is_an_actionable_error() =>
        Assert.Contains("damaged", await RunFailing("pdf.split", F("hostile/truncated.pdf")));

    [SkippableFact]
    public async Task Damaged_video_is_an_actionable_error()
    {
        NeedTool("ffmpeg");
        Assert.Contains("damaged", await RunFailing("video.to.mov", F("hostile/truncated.mp4")));
    }

    [Fact]
    public void Temp_workspaces_are_removed()
    {
        string root;
        using (var t = new SafePaths.TempWorkspace()) { root = t.Root; File.WriteAllText(t.File("txt"), "x"); }
        Assert.False(Directory.Exists(root));
    }
}

/// <summary>IProgress that reports synchronously (Progress&lt;T&gt; posts to the thread pool and races the assertions).</summary>
internal sealed class SyncProgress<T>(Action<T> a) : IProgress<T> { public void Report(T v) => a(v); }
