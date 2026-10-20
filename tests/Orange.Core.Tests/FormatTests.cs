using Orange.Core.Office;
using Xunit;

namespace Orange.Core.Tests;

public class FormatTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "orange-fmt-" + Guid.NewGuid().ToString("N"));
    private readonly OperationRegistry _reg = OperationRegistry.CreateDefault();
    public FormatTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static string Fixture(string n)
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !Directory.Exists(Path.Combine(d.FullName, "tests", "fixtures"))) d = d.Parent;
        return Path.Combine(d!.FullName, "tests", "fixtures", n);
    }
    private string Copy(string n, string? as_ = null) { var p = Path.Combine(_dir, as_ ?? n); File.Copy(Fixture(n), p, true); return p; }

    private async Task<string> Run(string opId, string input, Dictionary<string, string>? opts = null)
    {
        var runner = new JobRunner(_reg);
        var jobs = runner.Plan(_reg.Get(opId), [input], opts);
        await runner.RunAsync(jobs, null, CancellationToken.None);
        Assert.True(jobs[0].State == JobState.Succeeded, $"{opId}: {jobs[0].Result?.Error} {jobs[0].Result?.Detail}");
        return jobs[0].Result!.Outputs[0];
    }

    // ---- images

    [Fact]
    public async Task Jpeg_xl_write_and_read()
    {
        var jxl = await Run("image.to.jxl", Copy("sample.png"));
        Assert.Equal(FileKind.Image, FileTypeDetector.Detect(jxl));
        var png = await Run("image.to.png", jxl);
        Assert.True(new FileInfo(png).Length > 100);
    }

    [Fact]
    public async Task Multi_size_ico_create_and_extract()
    {
        var ico = await Run("image.to.ico", Copy("sample.png"), new() { ["size"] = "all" });
        using (var col = new ImageMagick.MagickImageCollection(ico)) Assert.Equal(6, col.Count);
        var folder = await Run("image.ico-extract", ico);
        var names = Directory.GetFiles(folder).Select(Path.GetFileName).ToList();
        Assert.Equal(6, names.Count);
        Assert.Contains(names, n => n!.Contains("256x256"));
        Assert.Contains(names, n => n!.Contains("16x16"));
    }

    [Theory]
    [InlineData("dng")] [InlineData("cr2")] [InlineData("nef")] [InlineData("arw")]
    public void Tiff_based_raw_files_route_to_libraw_not_the_tiff_reader(string ext)
    {
        var p = Copy("sample.tiff", "camera." + ext);
        Assert.Equal(FileKind.Image, FileTypeDetector.Detect(p));
        Assert.NotEqual(ImageMagick.MagickFormat.Tiff, Providers.ImageProvider.DecoderFor(p));
        Assert.Contains(_reg.ForFiles([p], OperationGroup.Convert), o => o.Id == "image.to.jpg");
    }

    [Fact]
    public void Heic_write_stays_hidden_when_unsupported()
    {
        var heic = _reg.Get("image.to.heic");
        if (!ImageMagick.MagickFormatInfo.Create(ImageMagick.MagickFormat.Heic)!.SupportsWriting) Assert.False(_reg.IsAvailable(heic));
    }

    // ---- e-books

    [Theory]
    [InlineData("sample.epub", "ebook.to.pdf")]
    [InlineData("sample.epub", "ebook.to.txt")]
    [InlineData("sample.fb2", "ebook.to.epub")]
    [InlineData("sample.fb2", "ebook.to.pdf")]
    [InlineData("sample.mobi", "ebook.to.epub")]
    [InlineData("sample.mobi", "ebook.to.txt")]
    [InlineData("sample.cbz", "ebook.to.pdf")]
    [InlineData("sample.cbz", "ebook.to.epub")]
    public async Task Ebook_conversions(string fixture, string op) => Assert.True(new FileInfo(await Run(op, Copy(fixture))).Length > 10);

    [Fact]
    public void Epub_reader_keeps_order_and_ignores_paths_outside_the_package()
    {
        var d = Ebooks.ReadEpub(Copy("sample.epub"));
        var text = TextFormats.ToText(d);
        Assert.True(text.IndexOf("Chapter One") < text.IndexOf("Chapter Two"));
        Assert.DoesNotContain("root:", text);
        Assert.Null(Ebooks.Resolve("OPS/", "../../../etc/passwd"));
        Assert.Equal("OPS/text/a.xhtml", Ebooks.Resolve("OPS/", "text/a.xhtml#p1"));
    }

    [Fact]
    public void Mobi_text_and_written_epub_round_trip()
    {
        var d = Ebooks.ReadMobi(Copy("sample.mobi"));
        Assert.Equal("Mobi Title", d.Blocks.OfType<Heading>().First().Text);
        var epub = Path.Combine(_dir, "x.epub");
        Ebooks.WriteEpub(d, epub);
        var back = TextFormats.ToText(Ebooks.ReadEpub(epub));
        Assert.Contains("Read on any Kindle.", back);
        Assert.Contains("Second page.", back);
    }

    [Fact]
    public void PalmDoc_decompression()
    {
        // literal "ab", then a back-reference (distance 2, length 3) → "ababa", then 0xC1 = " A"
        var ms = new MemoryStream();
        int pair = 0x8000 | (2 << 3) | (3 - 3);
        Ebooks.PalmDocDecompress(new byte[] { (byte)'a', (byte)'b', (byte)(pair >> 8), (byte)pair, 0xC1 }, ms);
        Assert.Equal("ababa A", System.Text.Encoding.ASCII.GetString(ms.ToArray()));
    }

    [Fact]
    public void Fb2_with_dtd_is_refused()
    {
        var p = Path.Combine(_dir, "x.fb2");
        File.WriteAllText(p, "<?xml version=\"1.0\"?><!DOCTYPE x [<!ENTITY e SYSTEM \"file:///etc/passwd\">]><FictionBook xmlns=\"http://www.gribuser.ru/xml/fictionbook/2.0\"><body><p>&e;</p></body></FictionBook>");
        Assert.ThrowsAny<Exception>(() => Ebooks.ReadFb2(p));
    }

    // ---- fonts

    private static string? SystemFont() => new[]
    {
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts", "arial.ttf"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts", "segoeui.ttf"),
        "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf",
    }.FirstOrDefault(File.Exists);

    [Fact]
    public async Task Font_ttf_to_woff_woff2_and_back_keeps_every_table()
    {
        var src = SystemFont();
        if (src is null) return;   // no font on this machine
        var ttf = Path.Combine(_dir, "font.ttf"); File.Copy(src, ttf);
        var original = Fonts.Read(File.ReadAllBytes(ttf));
        foreach (var op in new[] { "font.to.woff", "font.to.woff2" })
        {
            var web = await Run(op, ttf);
            Assert.Equal(FileKind.Font, FileTypeDetector.Detect(web));
            var back = await Run("font.to.desktop", web);
            Assert.EndsWith(".ttf", back);
            var round = Fonts.Read(File.ReadAllBytes(back));
            foreach (var t in original.Tables.Where(t => t.Tag != "head"))
                Assert.True(t.Data.AsSpan().SequenceEqual(round.Tables.Single(x => x.Tag == t.Tag).Data), t.Tag);
            File.Delete(back);
        }
        Assert.True(new FileInfo(Directory.GetFiles(_dir, "*.woff2").Single()).Length < new FileInfo(ttf).Length);
    }

    [Fact]
    public void Damaged_font_is_refused()
    {
        var bad = new byte[] { 0x77, 0x4F, 0x46, 0x46, 0, 1, 0, 0, 0, 0, 0, 100, 0x7F, 0xFF };
        Assert.Throws<OrangeException>(() => Fonts.Read(bad));
    }

    // ---- archives

    [Fact]
    public void Seven_zip_ops_are_registered_and_need_7zip()
    {
        var op = _reg.Get("archive.to.7z");
        Assert.Equal("7zip", op.RequiresTool);
        Assert.Equal(ToolLocator.Default.Has("7zip"), _reg.IsAvailable(op));
    }
}
