using Orange.Core;
using Orange.Core.Providers;

namespace Orange.Core.Tests;

public class DetectionTests
{
    [Theory]
    [InlineData("sample.png", FileKind.Image)]
    [InlineData("sample.jpg", FileKind.Image)]
    [InlineData("animated.gif", FileKind.Image)]
    [InlineData("sample.tiff", FileKind.Image)]
    [InlineData("sample.bmp", FileKind.Image)]
    [InlineData("sample.mp4", FileKind.Video)]
    [InlineData("sample.wav", FileKind.Audio)]
    [InlineData("sample.mp3", FileKind.Audio)]
    [InlineData("sample.pdf", FileKind.Pdf)]
    [InlineData("sample.docx", FileKind.Document)]
    [InlineData("sample.rtf", FileKind.Document)]
    [InlineData("sample.md", FileKind.Document)]
    [InlineData("sample.html", FileKind.Document)]
    [InlineData("sample.txt", FileKind.Document)]
    [InlineData("sample.srt", FileKind.Subtitle)]
    [InlineData("sample.vtt", FileKind.Subtitle)]
    [InlineData("sample.zip", FileKind.Archive)]
    [InlineData("sample.tar.gz", FileKind.Archive)]
    public void Detects_fixtures(string name, FileKind kind) => Assert.Equal(kind, FileTypeDetector.Detect(EngineTest.F(name)));

    [Theory]
    [InlineData("hostile/mvg-disguised.png")]   // ImageMagick vector script renamed .png (ImageTragick-style)
    [InlineData("hostile/text-renamed.jpg")]
    public void Content_that_contradicts_extension_is_refused(string name) =>
        Assert.Equal(FileKind.Unknown, FileTypeDetector.Detect(EngineTest.F(name)));
}

public class PathTests
{
    [Theory]
    [InlineData("CON", "_CON")]
    [InlineData("nul.txt", "_nul.txt")]
    [InlineData("a<b>c:d|e?f*g", "a_b_c_d_e_f_g")]
    [InlineData("trailing. ", "trailing")]
    [InlineData("", "file")]
    public void Sanitizes_windows_names(string input, string expected) => Assert.Equal(expected, SafePaths.SanitizeFileName(input));

    [Fact]
    public void Plans_tangerine_style_names_and_never_overwrites()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var input = Path.Combine(dir, "photo.png"); File.WriteAllText(input, "x");
        Assert.Equal(Path.Combine(dir, "photo.webp"), SafePaths.PlanOutput(input, null, "photo", null, "webp", OverwritePolicy.KeepBoth));
        Assert.Equal(Path.Combine(dir, "photo Cropped.png"), SafePaths.PlanOutput(input, null, "photo", "Cropped", "png", OverwritePolicy.KeepBoth));
        // Same name as the input → numbered, even with Replace.
        Assert.Equal(Path.Combine(dir, "photo 2.png"), SafePaths.PlanOutput(input, null, "photo", null, "png", OverwritePolicy.Replace));
        File.WriteAllText(Path.Combine(dir, "photo.webp"), "x");
        Assert.Equal(Path.Combine(dir, "photo 2.webp"), SafePaths.PlanOutput(input, null, "photo", null, "webp", OverwritePolicy.KeepBoth));
        Assert.Equal(Path.Combine(dir, "photo.webp"), SafePaths.PlanOutput(input, null, "photo", null, "webp", OverwritePolicy.Replace));
        Assert.Throws<OrangeException>(() => SafePaths.PlanOutput(input, null, "photo", null, "webp", OverwritePolicy.Fail));
        Directory.Delete(dir, true);
    }

    [Fact]
    public void Inside_check_blocks_traversal()
    {
        var root = Path.Combine(Path.GetTempPath(), "root");
        Assert.True(SafePaths.IsInside(root, Path.Combine(root, "a", "b.txt")));
        Assert.False(SafePaths.IsInside(root, Path.Combine(root, "..", "evil.txt")));
        Assert.False(SafePaths.IsInside(root, root + "sibling" + Path.DirectorySeparatorChar + "x"));
    }

    [Fact]
    public void Rejects_missing_empty_and_folder_inputs()
    {
        Assert.Throws<OrangeException>(() => SafePaths.ValidateInput(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".png")));
        var empty = Path.GetTempFileName();
        Assert.Throws<OrangeException>(() => SafePaths.ValidateInput(empty));
        Assert.Throws<OrangeException>(() => SafePaths.ValidateInput(Path.GetTempPath()));
        File.Delete(empty);
    }

    [Theory]
    [InlineData("all", 5, new[] { 1, 2, 3, 4, 5 })]
    [InlineData("1-3,5", 5, new[] { 1, 2, 3, 5 })]
    [InlineData("4-", 5, new[] { 4, 5 })]
    [InlineData("2,2,1", 5, new[] { 1, 2 })]
    public void Parses_page_lists(string spec, int count, int[] expected) => Assert.Equal(expected, PageSelection.Parse(spec, count));

    [Theory]
    [InlineData("0")] [InlineData("6")] [InlineData("3-1")] [InlineData("a")] [InlineData("1-2-3")]
    public void Rejects_bad_page_lists(string spec) => Assert.Throws<OrangeException>(() => PageSelection.Parse(spec, 5));

    [Theory]
    [InlineData("90", 90)] [InlineData("1:30", 90)] [InlineData("01:00:02.5", 3602.5)]
    public void Parses_times(string s, double seconds) => Assert.Equal(seconds, OptionValidator.ParseTime(s)!.Value.TotalSeconds, 3);
}

public class RegistryTests : EngineTest
{
    [Fact]
    public void Ids_are_unique_and_every_category_has_operations()
    {
        Assert.Equal(Registry.All.Count, Registry.All.Select(o => o.Id).Distinct().Count());
        foreach (var k in new[] { FileKind.Image, FileKind.Video, FileKind.Audio, FileKind.Pdf, FileKind.Document, FileKind.Subtitle, FileKind.Archive })
            Assert.Contains(Registry.All, o => o.Category == k);
    }

    [Fact]
    public void Wheel_is_context_aware()
    {
        var png = Registry.ForFiles([F("sample.png")], OperationGroup.Convert).Select(o => o.Id).ToList();
        Assert.Contains("image.to.webp", png);
        Assert.DoesNotContain("image.to.png", png);                 // never offer the format it already is
        Assert.DoesNotContain(png, id => id.StartsWith("audio."));

        var two = Registry.ForFiles([F("sample.png"), F("sample.jpg")], OperationGroup.Tool).Select(o => o.Id).ToList();
        Assert.Equal("image.create-pdf", two[0]);                     // combining tool first for a multi selection
        Assert.Empty(Registry.ForFiles([F("sample.png"), F("sample.pdf")], OperationGroup.Convert)); // mixed kinds
        Assert.DoesNotContain("pdf.merge", Registry.ForFiles([F("sample.pdf")], OperationGroup.Tool).Select(o => o.Id)); // merge needs 2
    }

    [Fact]
    public void Search_understands_plain_words()
    {
        var r = Registry.Search("to mp3").Select(o => o.Id).ToList();
        Assert.Contains("audio.to.mp3", r);
        Assert.Contains(Registry.Search("compress").Select(o => o.Category), c => c == FileKind.Image);
        Assert.Empty(Registry.Search("definitely-not-a-format"));
    }

    [Fact]
    public void Option_validation_rejects_out_of_range_values()
    {
        var op = Registry.Get("image.compress");
        Assert.Throws<OrangeException>(() => Runner.Plan(op, [F("sample.jpg")], O(("quality", "500"))));
        Assert.Throws<OrangeException>(() => Runner.Plan(op, [F("sample.jpg")], O(("bogus", "1"))));
    }

    [Fact]
    public void Plan_rejects_files_of_the_wrong_kind()
    {
        var ex = Assert.Throws<OrangeException>(() => Runner.Plan(Registry.Get("image.to.webp"), [F("hostile/mvg-disguised.png")]));
        Assert.Contains("doesn't look like", ex.Message);
    }
}
