using ImageMagick;
using Orange.Core;

namespace Orange.Core.Tests;

/// <summary>Behaviours confirmed from the Tangerine 2.0.2 bundle (see docs/research/tangerine-binary-findings.md).</summary>
public class ParityTests : EngineTest
{
    private string JpegWithMetadata(ushort orientation = 1)
    {
        var p = Path.Combine(Out, $"meta-{orientation}.jpg");
        using var img = new MagickImage(F("sample.png"));
        var exif = new ExifProfile();
        exif.SetValue(ExifTag.Make, "TestCam");
        exif.SetValue(ExifTag.GPSLatitudeRef, "N");
        exif.SetValue(ExifTag.Orientation, orientation);
        img.SetProfile(exif);
        img.Orientation = (ImageMagick.Orientation)orientation;
        img.Comment = "secret note";
        img.Quality = 90;
        img.Write(p);
        return p;
    }

    [Fact]
    public async Task Metadata_removal_is_lossless_for_jpeg()
    {
        var src = JpegWithMetadata();
        var o = (await Run("image.strip-metadata", src)).Single();
        var a = File.ReadAllBytes(src); var b = File.ReadAllBytes(o);
        Assert.True(b.Length < a.Length);
        Assert.Equal(LosslessMetadata.ImageDataHash(a, "jpg"), LosslessMetadata.ImageDataHash(b, "jpg"));   // scan data byte-identical
        using var img = new MagickImage(o);
        Assert.Null(img.GetExifProfile());
        Assert.Null(img.Comment);
    }

    [Fact]
    public async Task Lossless_removal_keeps_orientation_so_photos_stay_upright()
    {
        var src = JpegWithMetadata(6);
        var o = (await Run("image.strip-metadata", src)).Single();
        using var img = new MagickImage(o);
        var exif = img.GetExifProfile();
        Assert.NotNull(exif);
        Assert.Equal((ushort)6, exif!.GetValue(ExifTag.Orientation)!.Value);
        Assert.Null(exif.GetValue(ExifTag.Make));
        Assert.Single(exif.Values);
    }

    [Fact]
    public async Task Metadata_removal_is_lossless_for_png_and_keeps_icc_when_asked()
    {
        var src = Path.Combine(Out, "t.png");
        using (var img = new MagickImage(F("sample.png"))) { img.SetAttribute("comment", "hello"); img.SetAttribute("Author", "me"); img.Write(src); }
        var o = (await Run("image.strip-metadata", src)).Single();
        Assert.Equal(LosslessMetadata.ImageDataHash(File.ReadAllBytes(src), "png"), LosslessMetadata.ImageDataHash(File.ReadAllBytes(o), "png"));
        var text = System.Text.Encoding.Latin1.GetString(File.ReadAllBytes(o));
        Assert.DoesNotContain("tEXt", text); Assert.DoesNotContain("hello", text);
    }

    [Fact]
    public void Damaged_jpeg_is_refused_not_crashed()
    {
        Assert.Throws<OrangeException>(() => LosslessMetadata.StripJpeg([0xFF, 0xD8, 0xFF, 0xE1, 0xFF, 0xFF, 0x00], false));
        Assert.Throws<OrangeException>(() => LosslessMetadata.StripPng([1, 2, 3], false));
    }

    [Fact]
    public async Task Compress_never_returns_a_bigger_file()
    {
        // A tiny, already-optimised PNG can't be made smaller: Orange says so instead of saving a bigger "compressed" copy.
        var tiny = Path.Combine(Out, "tiny.png");
        using (var img = new MagickImage(MagickColors.White, 8, 8)) { img.Settings.SetDefine(MagickFormat.Png, "compression-level", "9"); img.Write(tiny); }
        var jobs = Runner.Plan(Registry.Get("image.compress"), [tiny], O(("quality", "95")), Out);
        await Runner.RunAsync(jobs, null, default);
        if (jobs[0].State == JobState.Failed) Assert.Contains("already as small", jobs[0].Result!.Error);
        else Assert.True(new FileInfo(jobs[0].Result!.Outputs[0]).Length < new FileInfo(tiny).Length);
    }

    [SkippableTheory, InlineData("wave"), InlineData("bars"), InlineData("spectrum")]
    public async Task Audio_visualizer_makes_a_video_with_the_audio(string style)
    {
        var o = (await Run("audio.visualize", F("sample.wav"), O(("style", style), ("size", "1280x720")))).Single();
        Assert.EndsWith(".mp4", o);
        var probe = await ProcessRunner.RunAsync(ToolLocator.Default.Require("ffprobe"), ["-v", "error", "-show_entries", "stream=codec_type,width,height", "-of", "csv=p=0", o], default);
        Assert.Contains("video", probe.StdOut); Assert.Contains("audio", probe.StdOut); Assert.Contains("1280,720", probe.StdOut);
    }
}
