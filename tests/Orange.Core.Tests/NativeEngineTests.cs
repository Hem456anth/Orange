using Orange.Core.Office;
using Orange.Core.Testing;

namespace Orange.Core.Tests;

/// <summary>Orange's own document engine (ODT, RTF, legacy DOC/PPT) and the end-to-end converter sweep.</summary>
public class NativeEngineTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "orange-native-" + Guid.NewGuid().ToString("N"));
    public NativeEngineTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static string Fixture(string n)
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !Directory.Exists(Path.Combine(d.FullName, "tests", "fixtures"))) d = d.Parent;
        return Path.Combine(d!.FullName, "tests", "fixtures", n);
    }

    private static DocModel Sample()
    {
        var m = new DocModel { Title = "Round trip" };
        m.Add(new Heading(1, "Chapter one"));
        m.Add(new Para([new Run("Plain, "), new Run("bold", Bold: true), new Run(" and "), new Run("italic", Italic: true), new Run(" — ünïcødé ✓ {braces} \\slash")]));
        m.Add(new ListItem([new Run("first bullet")], false));
        m.Add(new ListItem([new Run("second bullet")], false));
        m.Add(new Table([["A", "B"], ["1", "2"]]));
        m.Add(new PageBreak());
        m.Add(new Heading(2, "Chapter two"));
        m.Add(Para.Of("Last  line\twith tab"));
        return m;
    }

    [Fact]
    public void Odt_round_trips_structure()
    {
        var p = Path.Combine(_dir, "a.odt");
        OdtIO.Write(Sample(), p);
        var back = OdtIO.Read(p);
        Assert.Contains(back.Blocks, b => b is Heading { Level: 1, Text: "Chapter one" });
        Assert.Contains(back.Blocks, b => b is Heading { Level: 2, Text: "Chapter two" });
        var para = back.Blocks.OfType<Para>().First();
        Assert.Contains(para.Runs, r => r.Bold && r.Text == "bold");
        Assert.Contains(para.Runs, r => r.Italic && r.Text == "italic");
        Assert.Contains("ünïcødé ✓ {braces} \\slash", para.Text);
        Assert.Equal(2, back.Blocks.OfType<ListItem>().Count());
        Assert.Equal("2", back.Blocks.OfType<Table>().Single().Rows[1][1]);
        Assert.Contains(back.Blocks, b => b is PageBreak);
        Assert.Contains(back.Blocks.OfType<Para>(), x => x.Text == "Last  line\twith tab");
        using var z = System.IO.Compression.ZipFile.OpenRead(p);
        Assert.Equal("mimetype", z.Entries[0].FullName);   // ODF: first, uncompressed
    }

    [Fact]
    public void Rtf_round_trips_structure()
    {
        var p = Path.Combine(_dir, "a.rtf");
        RtfIO.Write(Sample(), p);
        Assert.True(File.ReadAllBytes(p).All(b => b < 128));   // 7-bit clean
        var back = RtfIO.Read(p);
        Assert.Contains(back.Blocks, b => b is Heading { Level: 1, Text: "Chapter one" });
        var para = back.Blocks.OfType<Para>().First();
        Assert.Contains(para.Runs, r => r.Bold && r.Text == "bold");
        Assert.Contains("ünïcødé ✓ {braces} \\slash", para.Text);
        Assert.Equal(2, back.Blocks.OfType<ListItem>().Count());
        Assert.Equal("2", back.Blocks.OfType<Table>().Single().Rows[1][1]);
        Assert.Contains(back.Blocks, b => b is PageBreak);
    }

    [Fact]
    public void Rtf_reader_handles_hex_escapes_and_skips_destinations()
    {
        var p = Path.Combine(_dir, "b.rtf");
        File.WriteAllText(p, @"{\rtf1\ansi\ansicpg1252{\fonttbl{\f0 Times;}}{\*\unknowndest secret}{\info{\title T}}caf\'e9 {\b strong}\par second\line line\par}");
        var m = RtfIO.Read(p);
        var paras = m.Blocks.OfType<Para>().ToList();
        Assert.Equal("café strong", paras[0].Text);
        Assert.DoesNotContain("secret", string.Concat(paras.Select(x => x.Text)));
        Assert.Equal("second\nline", paras[1].Text);
    }

    [Fact]
    public void Legacy_doc_text_is_read()
    {
        var m = LegacyDoc.Read(Fixture("sample.doc"));
        var text = TextFormats.ToText(m);
        Assert.Contains("I am a test document", text);
        Assert.Contains("This is page two", text);
        Assert.Contains(m.Blocks, b => b is PageBreak);
    }

    [Fact]
    public void Legacy_ppt_slides_are_read()
    {
        var slides = LegacyPpt.Read(Fixture("sample.ppt"));
        Assert.Equal(2, slides.Count);
        Assert.Equal("Title of the first slide", slides[0].Title);
        Assert.Contains("It has bullet points on it", slides[1].Lines);
    }

    [Fact]
    public void Compound_file_rejects_non_ole_input()
    {
        Assert.Throws<OrangeException>(() => new CompoundFile(new byte[1024]));
        var p = Path.Combine(_dir, "fake.doc"); File.WriteAllText(p, "not a doc");
        Assert.Throws<OrangeException>(() => LegacyDoc.Read(p));
    }

    [Fact]
    public async Task Every_built_in_converter_passes_the_end_to_end_sweep()
    {
        // A clean PC: no FFmpeg, Pandoc, Ghostscript, LibreOffice or 7-Zip. Every operation must pass or say what it needs.
        var reg = OperationRegistry.CreateDefault(new ToolLocator { BundledRoot = Path.Combine(_dir, "none"), SearchSystem = false });
        var results = await ConverterSweep.RunAsync(reg, Path.GetDirectoryName(Fixture("sample.png"))!, Path.Combine(_dir, "sweep"));
        var failed = results.Where(r => r.Status is "fail" or "no-input").Select(r => $"{r.Op}: {r.Error}").ToList();
        Assert.True(failed.Count == 0, string.Join("\n", failed));
        Assert.True(results.Count(r => r.Status == "pass") >= 70);
    }
}
