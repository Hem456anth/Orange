using System.Text.Json;
using Orange.Cli;
using Xunit;

namespace Orange.Core.Tests;

[Collection("Cli")]
public class CliTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "orange-cli-" + Guid.NewGuid().ToString("N"));
    public CliTests() { Directory.CreateDirectory(_dir); CliApp.PresetsPathOverride = Path.Combine(_dir, "presets.json"); }
    public void Dispose() { CliApp.PresetsPathOverride = null; try { Directory.Delete(_dir, true); } catch { } }

    private static string Fixture(string n)
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !Directory.Exists(Path.Combine(d.FullName, "tests", "fixtures"))) d = d.Parent;
        return Path.Combine(d!.FullName, "tests", "fixtures", n);
    }

    private static async Task<(int Code, string Out, string Err)> Run(params string[] args)
    {
        var o = new StringWriter(); var e = new StringWriter();
        var code = await CliApp.RunAsync(args, o, e);
        return (code, o.ToString(), e.ToString());
    }

    [Fact]
    public async Task Usage_errors_exit_2()
    {
        Assert.Equal(CliApp.Usage, (await Run()).Code);
        Assert.Equal(CliApp.Usage, (await Run("frobnicate")).Code);
        Assert.Equal(CliApp.Usage, (await Run("convert", "x.png")).Code);                       // neither --to nor --preset
        Assert.Equal(CliApp.Usage, (await Run("convert", "x.png", "--to", "webp", "--preset", "Web image")).Code);
    }

    [Fact]
    public async Task Glob_convert_with_json_report()
    {
        File.Copy(Fixture("sample.png"), Path.Combine(_dir, "a.png"));
        File.Copy(Fixture("sample.png"), Path.Combine(_dir, "b.png"));
        var outDir = Path.Combine(_dir, "out");
        var (code, stdout, _) = await Run("convert", Path.Combine(_dir, "*.png"), "--to", "webp", "-o", outDir, "--quality", "60", "--json");
        Assert.Equal(CliApp.Ok, code);
        using var doc = JsonDocument.Parse(stdout);
        var files = doc.RootElement.GetProperty("files");
        Assert.Equal(2, files.GetArrayLength());
        foreach (var f in files.EnumerateArray())
        {
            Assert.Equal("done", f.GetProperty("status").GetString());
            Assert.StartsWith(outDir, f.GetProperty("output").GetString());
        }
    }

    [Fact]
    public async Task No_match_exits_3_and_unsupported_exits_3()
    {
        Assert.Equal(CliApp.NothingToDo, (await Run("convert", Path.Combine(_dir, "*.nothing"), "--to", "webp")).Code);
        File.Copy(Fixture("sample.png"), Path.Combine(_dir, "a.png"));
        Assert.Equal(CliApp.NothingToDo, (await Run("convert", Path.Combine(_dir, "a.png"), "--to", "mp3")).Code);
    }

    [Fact]
    public async Task Preset_convert_and_presets_export_import()
    {
        File.Copy(Fixture("sample.png"), Path.Combine(_dir, "pic.png"));
        var (code, stdout, _) = await Run("convert", Path.Combine(_dir, "pic.png"), "--preset", "Web image");
        Assert.Equal(CliApp.Ok, code);
        Assert.Contains("pic web.png", stdout);

        var export = Path.Combine(_dir, "p.json");
        Assert.Equal(CliApp.Ok, (await Run("presets", "export", export)).Code);
        Assert.Contains("WhatsApp video", File.ReadAllText(export));
        File.WriteAllText(export, "{\"Version\":1,\"Presets\":[{\"Name\":\"Tiny WebP\",\"OperationId\":\"image.to.webp\",\"Options\":{\"quality\":\"30\"}}]}");
        Assert.Equal(CliApp.Ok, (await Run("presets", "import", export)).Code);
        var list = await Run("presets", "list");
        Assert.Contains("Tiny WebP", list.Out);
        Assert.Contains("Email PDF", list.Out);
    }

    [Fact]
    public async Task Unknown_preset_is_a_usage_error() =>
        Assert.Equal(CliApp.Usage, (await Run("convert", Fixture("sample.png"), "--preset", "Nope")).Code);

    [Fact]
    public async Task Tools_list_works() => Assert.Equal(CliApp.Ok, (await Run("tools", "list")).Code);
}
