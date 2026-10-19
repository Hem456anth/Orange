using System.IO.Compression;
using Orange.Core.Office;
using Xunit;

namespace Orange.Core.Tests;

public class OfficeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "orange-office-" + Guid.NewGuid().ToString("N"));
    private readonly OperationRegistry _reg = OperationRegistry.CreateDefault();
    public OfficeTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static string Fixture(string n)
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !Directory.Exists(Path.Combine(d.FullName, "tests", "fixtures"))) d = d.Parent;
        return Path.Combine(d!.FullName, "tests", "fixtures", n);
    }
    private string Copy(string n) { var p = Path.Combine(_dir, n); File.Copy(Fixture(n), p, true); return p; }

    private async Task<string> Run(string opId, string input)
    {
        var runner = new JobRunner(_reg);
        var jobs = runner.Plan(_reg.Get(opId), [input]);
        await runner.RunAsync(jobs, null, CancellationToken.None);
        Assert.True(jobs[0].State == JobState.Succeeded, $"{opId}: {jobs[0].Result?.Error} {jobs[0].Result?.Detail}");
        return jobs[0].Result!.Outputs[0];
    }

    [Theory]
    [InlineData("sample.docx", "doc.native.pdf")]
    [InlineData("sample.docx", "doc.native.html")]
    [InlineData("sample.docx", "doc.native.md")]
    [InlineData("sample.docx", "doc.native.txt")]
    [InlineData("sample.md", "doc.native.docx")]
    [InlineData("sample.md", "doc.native.pdf")]
    [InlineData("sample.txt", "doc.native.docx")]
    [InlineData("sample.html", "doc.native.docx")]
    [InlineData("sample.html", "doc.native.md")]
    public async Task Native_document_conversions(string fixture, string op)
    {
        var o = await Run(op, Copy(fixture));
        Assert.True(new FileInfo(o).Length > 50);
    }

    [Fact]
    public async Task Docx_text_survives_a_round_trip_through_pdf_and_markdown()
    {
        var src = Copy("sample.docx");
        var original = TextFormats.ToText(DocxIO.Read(src));
        var md = await File.ReadAllTextAsync(await Run("doc.native.md", src));
        var firstWords = original.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Take(3).ToList();
        foreach (var w in firstWords) Assert.Contains(w.Trim('*', '_'), md);
        var pdf = await Run("doc.native.pdf", src);
        using var doc = UglyToad.PdfPig.PdfDocument.Open(pdf);
        var text = string.Join(" ", doc.GetPages().Select(p => p.Text));
        Assert.Contains(firstWords[0].Trim('*', '_', ':', '.'), text);
    }

    [Fact]
    public void Markdown_model_keeps_structure()
    {
        var d = TextFormats.FromMarkdown("# Title\n\nSome **bold** and *italic*.\n\n- one\n- two\n\n| a | b |\n|---|---|\n| 1 | 2 |\n", "t");
        Assert.IsType<Heading>(d.Blocks[0]);
        var p = Assert.IsType<Para>(d.Blocks[1]);
        Assert.Contains(p.Runs, r => r.Bold && r.Text == "bold");
        Assert.Equal(2, d.Blocks.OfType<ListItem>().Count());
        Assert.Equal("2", d.Blocks.OfType<Table>().Single().Rows[1][1]);
        var docx = Path.Combine(_dir, "m.docx");
        DocxIO.Write(d, docx);
        var back = DocxIO.Read(docx);
        Assert.Equal("Title", back.Blocks.OfType<Heading>().First().Text);
        Assert.Equal(2, back.Blocks.OfType<ListItem>().Count());
        Assert.Single(back.Blocks.OfType<Table>());
    }

    [Fact]
    public void Html_reader_drops_scripts()
    {
        var d = TextFormats.FromHtml("<html><head><script>alert(1)</script></head><body><h1>Hi</h1><p>x<script>evil()</script>y</p></body></html>", "t");
        var text = TextFormats.ToText(d);
        Assert.Contains("Hi", text);
        Assert.DoesNotContain("alert", text);
        Assert.DoesNotContain("evil", text);
    }

    [Theory]
    [InlineData("sample.xlsx", "sheet.to.csv")]
    [InlineData("sample.xlsx", "sheet.to.ods")]
    [InlineData("sample.xlsx", "sheet.to.pdf")]
    [InlineData("sample.xls", "sheet.to.xlsx")]
    [InlineData("sample.ods", "sheet.to.xlsx")]
    [InlineData("sample.csv", "sheet.to.xlsx")]
    [InlineData("sample.csv", "sheet.to.tsv")]
    public async Task Native_sheet_conversions(string fixture, string op) => Assert.True(new FileInfo(await Run(op, Copy(fixture))).Length > 20);

    [Fact]
    public void Sheet_values_round_trip_across_formats()
    {
        var x = Sheets.Read(Copy("sample.xlsx"));
        Assert.Equal(["Fruit", "Notes"], x.Select(s => s.Name));
        Assert.Equal("Mango", x[0].Rows[1][0]);
        Assert.Equal("12", x[0].Rows[2][1]);
        Assert.Equal("gap", x[1].Rows[2][2]);          // C3 lands in the right column and row
        var ods = Path.Combine(_dir, "o.ods"); Sheets.WriteOds(x, ods);
        var y = Sheets.ReadOds(ods);
        Assert.Equal("Orange", y[0].Rows[2][0]);
        var xls = Sheets.Read(Copy("sample.xls"));
        Assert.Equal("Mango", xls[0].Rows[1][0]);
        var odsIn = Sheets.Read(Copy("sample.ods"));
        Assert.Equal(3, odsIn[0].Rows.Count);           // 1M repeated empty rows are not expanded
        Assert.Equal("12", odsIn[0].Rows[2][1]);
    }

    [Fact]
    public void Csv_quoting_and_formula_injection()
    {
        var s = Sheets.Read(Copy("sample.csv"))[0];
        Assert.Equal("says \"hi\", twice", s.Rows[1][1]);
        Assert.Equal("line1\nline2", s.Rows[2][1]);
        var outCsv = Path.Combine(_dir, "out.csv");
        Sheets.WriteDelimited(s, outCsv, ',');
        var text = File.ReadAllText(outCsv);
        Assert.Contains("'=HYPERLINK", text);           // neutralised for spreadsheet apps
        Assert.Contains("\"says \"\"hi\"\", twice\"", text);
    }

    [Fact]
    public async Task Pptx_to_pdf_and_png_and_pdf_to_pptx_and_docx()
    {
        var pdf = await Run("slides.to.pdf", Copy("sample.pptx"));
        using (var d = UglyToad.PdfPig.PdfDocument.Open(pdf))
        {
            Assert.Equal(2, d.NumberOfPages);
            Assert.Contains("Orange", d.GetPage(1).Text);
        }
        var folder = await Run("slides.to.png", Copy("sample.pptx"));
        Assert.Equal(2, Directory.GetFiles(folder, "*.png").Length);
        var pptx = await Run("pdf.to.pptx", Copy("sample.pdf"));
        using (var p = DocumentFormat.OpenXml.Packaging.PresentationDocument.Open(pptx, false))
            Assert.True(p.PresentationPart!.SlideParts.Count() >= 1);
        var docx = await Run("pdf.to.docx", Copy("sample.pdf"));
        Assert.True(TextFormats.ToText(DocxIO.Read(docx)).Trim().Length > 0);
    }

    [Fact]
    public void Zip_bomb_office_file_is_refused()
    {
        var bomb = Path.Combine(_dir, "bomb.xlsx");
        using (var z = ZipFile.Open(bomb, ZipArchiveMode.Create))
        {
            var e = z.CreateEntry("xl/worksheets/sheet1.xml", CompressionLevel.SmallestSize);
            using var s = e.Open();
            var zeros = new byte[1 << 20];
            for (int i = 0; i < 64; i++) s.Write(zeros);
        }
        var ex = Assert.Throws<OrangeException>(() => Sheets.ReadXlsx(bomb));
        Assert.Contains("bomb", ex.Message);
    }

    [Fact]
    public void Native_route_wins_and_is_offered_for_docx()
    {
        var ops = _reg.ForFiles([Copy("sample.docx")], OperationGroup.Convert);
        var pdf = ops.Where(o => o.OutputExtension == "pdf").ToList();
        Assert.Single(pdf);
        Assert.Equal("doc.native.pdf", pdf[0].Id);
        Assert.Contains(_reg.ForFiles([Copy("sample.xlsx")], OperationGroup.Convert), o => o.Id == "sheet.to.csv");
        Assert.Contains(_reg.ForFiles([Copy("sample.pptx")], OperationGroup.Convert), o => o.Id == "slides.to.pdf");
    }
}
