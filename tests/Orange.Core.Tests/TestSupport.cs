using Orange.Core;

namespace Orange.Core.Tests;

/// <summary>Shared helpers: fixture lookup, a scratch output folder per test, and tool gating.</summary>
public abstract class EngineTest : IDisposable
{
    protected static readonly OperationRegistry Registry = OperationRegistry.CreateDefault();
    protected static readonly JobRunner Runner = new(Registry);
    protected readonly string Out;

    static EngineTest()
    {
        // Dev Linux boxes often have distro Pandoc builds that can't run DOCX/ODT/EPUB writers in --sandbox.
        if (!OperatingSystem.IsWindows()) Environment.SetEnvironmentVariable("ORANGE_ALLOW_UNSANDBOXED_PANDOC", "1");
    }

    protected EngineTest()
    {
        Out = Path.Combine(Path.GetTempPath(), "orange-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Out);
    }
    public void Dispose() { try { Directory.Delete(Out, true); } catch { } }

    public static string Fixtures
    {
        get
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !Directory.Exists(Path.Combine(d.FullName, "tests", "fixtures"))) d = d.Parent;
            return d is null ? throw new DirectoryNotFoundException("tests/fixtures not found") : Path.Combine(d.FullName, "tests", "fixtures");
        }
    }
    public static string F(string name) => Path.Combine(Fixtures, name);

    /// <summary>Skips when an external tool is missing, unless ORANGE_REQUIRE_TOOLS=1 (CI), where it fails instead.</summary>
    protected static void NeedTool(string tool)
    {
        if (ToolLocator.Default.Has(tool)) return;
        if (Environment.GetEnvironmentVariable("ORANGE_REQUIRE_TOOLS") == "1") Assert.Fail($"{tool} is required in this environment but wasn't found");
        Skip.If(true, $"{tool} not installed");
    }

    protected async Task<IReadOnlyList<string>> Run(string opId, IEnumerable<string> inputs, Dictionary<string, string>? opts = null, OverwritePolicy policy = OverwritePolicy.KeepBoth)
    {
        var op = Registry.Get(opId);
        if (op.RequiresTool is { } t) NeedTool(t);
        if (op.Category is FileKind.Video or FileKind.Audio) NeedTool("ffprobe");
        var jobs = Runner.Plan(op, inputs.ToList(), opts, Out, policy);
        await Runner.RunAsync(jobs, null, CancellationToken.None);
        foreach (var j in jobs)
            Assert.True(j.State == JobState.Succeeded, $"{opId} failed: {j.Result?.Error} | {j.Result?.Detail}");
        return jobs.SelectMany(j => j.Result!.Outputs).ToList();
    }
    protected Task<IReadOnlyList<string>> Run(string opId, string input, Dictionary<string, string>? opts = null) => Run(opId, new[] { input }, opts);

    protected static async Task<string> RunFailing(string opId, params string[] inputs)
    {
        var op = Registry.Get(opId);
        if (op.RequiresTool is { } t) NeedTool(t);
        try
        {
            var jobs = Runner.Plan(op, inputs, null, Path.GetTempPath());
            await Runner.RunAsync(jobs, null, CancellationToken.None);
            var j = jobs.Single();
            Assert.Equal(JobState.Failed, j.State);
            return j.Result!.Error!;
        }
        catch (OrangeException e) { return e.Message; }
    }

    protected static Dictionary<string, string> O(params (string k, string v)[] kv) => kv.ToDictionary(x => x.k, x => x.v);
}
