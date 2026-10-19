using Orange.Core.Presets;
using Xunit;

namespace Orange.Core.Tests;

public class PresetTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "orange-presets-" + Guid.NewGuid().ToString("N"));
    private readonly OperationRegistry _reg = OperationRegistry.CreateDefault();
    public PresetTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static string Fixtures => Path.Combine(AppContext.BaseDirectory, "fixtures");
    private string Copy(string name) { var d = Path.Combine(_dir, name); File.Copy(Path.Combine(FindFixtures(), name), d); return d; }
    private static string FindFixtures()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !Directory.Exists(Path.Combine(d.FullName, "tests", "fixtures"))) d = d.Parent;
        return Path.Combine(d!.FullName, "tests", "fixtures");
    }

    [Fact]
    public void Six_defaults_all_validate()
    {
        Assert.Equal(6, DefaultPresets.All.Count);
        foreach (var p in DefaultPresets.All) PresetJson.Validate(p, _reg);
        Assert.Contains(DefaultPresets.All, p => p.Name == "WhatsApp video" && p.Options["targetMB"] == "15");
        Assert.Contains(DefaultPresets.All, p => p.Name == "Email PDF");
        Assert.Contains(DefaultPresets.All, p => p.Name == "Discord video");
        Assert.Contains(DefaultPresets.All, p => p.Name == "Web image");
    }

    [Fact]
    public void Export_then_import_round_trips()
    {
        var json = PresetJson.Export(DefaultPresets.All);
        var back = PresetJson.Import(json, _reg);
        Assert.Equal(DefaultPresets.All.Select(p => p.Name), back.Select(p => p.Name));
        Assert.Equal("1.9", back.Single(p => p.Name == "Email PDF").Options["targetMB"]);
    }

    [Theory]
    [InlineData("{\"Version\":1,\"Presets\":[{\"Name\":\"x\",\"OperationId\":\"nope.op\"}]}", "unknown operation")]
    [InlineData("{\"Version\":1,\"Presets\":[{\"Name\":\"x\",\"OperationId\":\"image.to.webp\",\"Options\":{\"quality\":\"500\"}}]}", "")]
    [InlineData("{\"Version\":1,\"Presets\":[{\"Name\":\"x\",\"OperationId\":\"image.to.webp\",\"Output\":{\"NamePattern\":\"fixed.webp\"}}]}", "{name}")]
    [InlineData("{\"Version\":1,\"Presets\":[{\"Name\":\"x\",\"OperationId\":\"image.to.webp\",\"Output\":{\"Location\":\"Subfolder\",\"Subfolder\":\"..\\\\..\\\\Windows\"}}]}", "single folder")]
    [InlineData("not json", "isn't an Orange presets file")]
    public void Import_refuses_bad_presets(string json, string why)
    {
        var e = Assert.Throws<OrangeException>(() => PresetJson.Import(json, _reg));
        Assert.Contains(why, e.Message);
    }

    [Theory]
    [InlineData("{name} {preset}.{ext}", "holiday Web.webp")]
    [InlineData("{name}/../../evil {preset}", "holiday-..-..-evil Web.webp")]
    [InlineData("CON {name}", "CON holiday.webp")]
    public void Name_pattern_never_escapes_the_folder(string pattern, string expected)
    {
        var p = new Preset { Name = "Web", OperationId = "image.to.webp", Output = new() { NamePattern = pattern } };
        var n = p.FileNameFor(Path.Combine(_dir, "holiday.png"), "webp", "WebP");
        Assert.Equal(expected, n);
        Assert.DoesNotContain('/', n); Assert.DoesNotContain('\\', n);
    }

    [Fact]
    public async Task Runs_a_preset_names_output_and_keeps_both_on_conflict()
    {
        var png = Copy("sample.png");
        var preset = new Preset { Name = "Web", OperationId = "image.to.webp", Options = new() { ["quality"] = "70" } };
        var runner = new PresetRunner(_reg);
        var r1 = await runner.RunAsync(preset, [png]);
        Assert.Equal(PresetFileState.Done, r1[0].State);
        Assert.Equal(Path.Combine(_dir, "sample Web.webp"), r1[0].Output);
        var r2 = await runner.RunAsync(preset, [png]);
        Assert.Equal(Path.Combine(_dir, "sample Web 2.webp"), r2[0].Output);
        Assert.True(File.Exists(png));
    }

    [Fact]
    public async Task Skip_policy_leaves_existing_file_alone()
    {
        var png = Copy("sample.png");
        File.WriteAllText(Path.Combine(_dir, "sample Web.webp"), "mine");
        var preset = new Preset { Name = "Web", OperationId = "image.to.webp", Output = new() { Conflict = ConflictPolicy.Skip } };
        var r = await new PresetRunner(_reg).RunAsync(preset, [png]);
        Assert.Equal(PresetFileState.Skipped, r[0].State);
        Assert.Equal("mine", File.ReadAllText(Path.Combine(_dir, "sample Web.webp")));
    }

    [Fact]
    public async Task Pattern_equal_to_input_never_overwrites_the_original()
    {
        var png = Copy("sample.png");
        var before = File.ReadAllBytes(png);
        var preset = new Preset { Name = "Same", OperationId = "image.compress", Output = new() { NamePattern = "{name}.{ext}", Conflict = ConflictPolicy.Replace } };
        var r = await new PresetRunner(_reg).RunAsync(preset, [png]);
        Assert.Equal(before, File.ReadAllBytes(png));
        if (r[0].State == PresetFileState.Done) Assert.NotEqual(png, r[0].Output);
    }

    [Fact]
    public async Task Subfolder_and_metadata_strip()
    {
        var jpg = Copy("sample-meta.jpg");
        var preset = new Preset { Name = "Clean", OperationId = "image.to.png", Metadata = MetadataPolicy.Strip,
            Output = new() { Location = OutputLocation.Subfolder, Subfolder = "Out", NamePattern = "{name}.{ext}" } };
        var r = await new PresetRunner(_reg).RunAsync(preset, [jpg]);
        Assert.Equal(PresetFileState.Done, r[0].State);
        Assert.Equal(Path.Combine(_dir, "Out", "sample-meta.png"), r[0].Output);
    }

    [Fact]
    public void Store_returns_defaults_then_saved_presets()
    {
        var store = new PresetStore(Path.Combine(_dir, "presets.json"));
        Assert.Equal(6, store.Load(_reg).Count);
        store.Save([new Preset { Name = "Only", OperationId = "image.to.webp" }], _reg);
        Assert.Equal("Only", Assert.Single(store.Load(_reg)).Name);
        Assert.Throws<OrangeException>(() => store.Save([new Preset { Name = "A", OperationId = "image.to.webp" }, new Preset { Name = "a", OperationId = "image.to.png" }], _reg));
    }

    [Fact]
    public void Presets_for_files_match_kind()
    {
        var png = Copy("sample.png");
        var list = new PresetRunner(_reg).ForFiles(DefaultPresets.All, [png]).Select(p => p.Name).ToList();
        Assert.Contains("Web image", list);
        Assert.DoesNotContain("Email PDF", list);
    }
}

public class WheelLayoutTests
{
    private static OperationDescriptor Op(string id) => new() { Id = id, Title = id, Category = FileKind.Image, Group = OperationGroup.Convert };

    [Fact]
    public void Hide_and_reorder_petals_per_kind()
    {
        var ops = new[] { Op("a"), Op("b"), Op("c"), Op("d") };
        var l = new WheelLayout();
        Assert.Equal("abcd", string.Concat(l.Apply(FileKind.Image, OperationGroup.Convert, ops).Select(o => o.Id)));
        l.SetHidden(FileKind.Image, OperationGroup.Convert, "b", true);
        l.Move(FileKind.Image, OperationGroup.Convert, ops, "d", -1);
        Assert.Equal("adc", string.Concat(l.Apply(FileKind.Image, OperationGroup.Convert, ops).Select(o => o.Id)));
        // Another kind is untouched.
        Assert.Equal("abcd", string.Concat(l.Apply(FileKind.Video, OperationGroup.Convert, ops).Select(o => o.Id)));
        l.SetHidden(FileKind.Image, OperationGroup.Convert, "b", false);
        Assert.Contains("b", l.Apply(FileKind.Image, OperationGroup.Convert, ops).Select(o => o.Id));
    }
}
