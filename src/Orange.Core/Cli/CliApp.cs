using System.Text.Json;
using Orange.Core;
using Orange.Core.Presets;
using Orange.Core.Watch;

namespace Orange.Cli;

/// <summary>
/// orange — the same engine the Windows app uses, for scripts and terminals. See docs/CLI.md.
///   orange convert &lt;files|globs&gt; --to &lt;fmt&gt; | --preset &lt;name&gt; [-o dir] [--quality n] [--overwrite keep|replace|fail] [--json]
///   orange tools list
///   orange presets list|import &lt;file&gt;|export &lt;file&gt;
///   orange watch &lt;dir&gt; --preset &lt;name&gt; [--move-originals]
///   (also: ops, search, run, matrix — kept from 0.1)
/// Exit codes: 0 all done · 1 some files failed · 2 usage error · 3 nothing to do (no files matched / unsupported) · 4 internal error · 130 cancelled.
/// </summary>
public static class CliApp
{
    public const int Ok = 0, SomeFailed = 1, Usage = 2, NothingToDo = 3, Internal = 4, Cancelled = 130;

    public static string? PresetsPathOverride { get; set; }   // tests
    private static PresetStore Store => PresetsPathOverride is { } p ? new PresetStore(p) : PresetStore.Default;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error, CancellationToken ct = default)
    {
        Orange.Core.Engines.EngineManager.ForCurrentUser().Register();   // engines installed from Settings work from the command line too
        var registry = OperationRegistry.CreateDefault();
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help") { PrintUsage(output); return args.Length == 0 ? Usage : Ok; }
        try
        {
            return args[0] switch
            {
                "convert" => await ConvertAsync(args[1..], registry, output, error, ct),
                "tools" => Tools(args[1..], registry, output),
                "presets" => Presets(args[1..], registry, output, error),
                "watch" => await WatchAsync(args[1..], registry, output, error, ct),
                "ops" => Ops(args[1..], registry, output),
                "search" => Search(args[1..], registry, output),
                "matrix" => Matrix(registry, output),
                "run" => await RunOpAsync(args[1..], registry, output, error, ct),
                "sweep" => await SweepAsync(args[1..], registry, output, ct),
                "engines" => await EnginesAsync(args[1..], output, error, ct),
                "--version" or "version" => Version(output),
                _ => Bad(error, $"Unknown command \"{args[0]}\"."),
            };
        }
        catch (UsageException e) { return Bad(error, e.Message); }
        catch (OperationCanceledException) { error.WriteLine("Cancelled."); return Cancelled; }
        catch (OrangeException e) { error.WriteLine(e.Message); if (e.Detail is not null) error.WriteLine("  " + e.Detail); return SomeFailed; }
        catch (Exception e) { error.WriteLine("Internal error: " + e.Message); return Internal; }
    }

    private sealed class UsageException(string m) : Exception(m);

    private static int Bad(TextWriter error, string message) { error.WriteLine(message); error.WriteLine("Run \"orange --help\" for usage."); return Usage; }

    private static int Version(TextWriter o) { o.WriteLine($"orange {typeof(CliApp).Assembly.GetName().Version?.ToString(3)}"); return Ok; }

    private static void PrintUsage(TextWriter o) => o.WriteLine("""
        orange convert <files|globs> --to <format> | --preset <name> [-o <dir>] [--quality <1-100>] [--overwrite keep|replace|fail] [--json]
        orange tools list
        orange presets list | import <file.json> | export <file.json>
        orange watch <dir> --preset <name> [--move-originals]
        orange ops <files>        operations offered for these files
        orange search <words>     search every operation
        orange matrix             every operation and whether it's available here (markdown)
        orange run <op-id> <files> [--opt name=value]... [-o <dir>]
        Exit codes: 0 done, 1 some failed, 2 usage, 3 nothing to do, 4 internal error, 130 cancelled.
        Everything runs on this PC. Nothing is uploaded.
        """);

    // ---------------- argument helpers

    private sealed class Args
    {
        public List<string> Positional { get; } = new();
        public Dictionary<string, string?> Named { get; } = new(StringComparer.Ordinal);
        public List<string> Opts { get; } = new();
        public string? Get(string name, string? alias = null) => Named.TryGetValue(name, out var v) ? v : alias != null && Named.TryGetValue(alias, out var a) ? a : null;
        public bool Has(string name) => Named.ContainsKey(name);
    }

    private static Args Parse(string[] a, params string[] flags)
    {
        var r = new Args();
        for (int i = 0; i < a.Length; i++)
        {
            var s = a[i];
            if (s == "--") { r.Positional.AddRange(a[(i + 1)..]); break; }
            if (s.StartsWith('-') && s.Length > 1)
            {
                if (flags.Contains(s)) { r.Named[s] = null; continue; }
                if (i + 1 >= a.Length) throw new UsageException($"{s} needs a value.");
                if (s == "--opt") r.Opts.Add(a[++i]); else r.Named[s] = a[++i];
            }
            else r.Positional.Add(s);
        }
        return r;
    }

    /// <summary>Expands * and ? in the file-name part (not in folders). Literal paths pass through.</summary>
    public static List<string> ExpandGlobs(IEnumerable<string> patterns)
    {
        var result = new List<string>();
        foreach (var p in patterns)
        {
            var name = Path.GetFileName(p);
            if (name.IndexOfAny(['*', '?']) < 0) { result.Add(Path.GetFullPath(p)); continue; }
            var dir = Path.GetDirectoryName(p);
            dir = string.IsNullOrEmpty(dir) ? Directory.GetCurrentDirectory() : Path.GetFullPath(dir);
            if (!Directory.Exists(dir)) continue;
            result.AddRange(Directory.EnumerateFiles(dir, name).OrderBy(f => f, StringComparer.OrdinalIgnoreCase));
        }
        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private sealed record FileReport(string input, string status, string? output, string? error);

    // ---------------- convert

    private static async Task<int> ConvertAsync(string[] a, OperationRegistry reg, TextWriter o, TextWriter e, CancellationToken ct)
    {
        var p = Parse(a, "--json");
        var to = p.Get("--to"); var presetName = p.Get("--preset");
        if (to is null == (presetName is null)) throw new UsageException("Give exactly one of --to <format> or --preset <name>.");
        if (p.Positional.Count == 0) throw new UsageException("No input files.");
        var files = ExpandGlobs(p.Positional).Where(File.Exists).ToList();
        bool json = p.Has("--json");
        if (files.Count == 0) { e.WriteLine("No files matched."); return NothingToDo; }
        var outDir = p.Get("-o", "--out") is { } od ? Path.GetFullPath(od) : null;
        if (outDir != null) Directory.CreateDirectory(outDir);
        var reports = new List<FileReport>();

        if (presetName != null)
        {
            var preset = PresetStore.Find(Store.Load(reg), presetName) ?? throw new UsageException($"There's no preset called \"{presetName}\". Try \"orange presets list\".");
            if (outDir != null) preset = preset with { Output = preset.Output with { Location = OutputLocation.BesideOriginal } };
            var runner = new PresetRunner(reg);
            var fits = files.Where(f => runner.ForFiles([preset], [f]).Count > 0).ToList();
            foreach (var f in files.Except(fits)) reports.Add(new(f, "skipped", null, $"\"{preset.Name}\" doesn't apply to this file."));
            if (fits.Count > 0)
                foreach (var r in await runner.RunAsync(preset, fits, null, ct))
                {
                    var outPath = r.Output;
                    if (r.State == PresetFileState.Done && outDir != null && outPath != null)
                    {
                        var dest = SafePaths.PlanOutput(outPath, outDir, Path.GetFileNameWithoutExtension(outPath), null, FileTypeDetector.Ext(outPath), OverwritePolicy.KeepBoth);
                        File.Move(outPath, dest); outPath = dest;
                    }
                    reports.Add(new(r.Input, r.State.ToString().ToLowerInvariant(), outPath, r.Error));
                }
        }
        else
        {
            var fmt = OperationRegistry.Normalize(to!.TrimStart('.').ToLowerInvariant());
            var policy = p.Get("--overwrite") switch { null or "keep" => OverwritePolicy.KeepBoth, "replace" => OverwritePolicy.Replace, "fail" => OverwritePolicy.Fail, var x => throw new UsageException($"--overwrite must be keep, replace or fail (got {x}).") };
            var jobsByOp = new List<(OperationDescriptor Op, string File)>();
            foreach (var f in files)
            {
                var op = reg.ForFiles([f], OperationGroup.Convert).FirstOrDefault(x => x.OutputExtension is { } ext && OperationRegistry.Normalize(ext) == fmt);
                if (op is null) reports.Add(new(f, "skipped", null, $"Can't convert {Path.GetFileName(f)} to {fmt} on this PC."));
                else jobsByOp.Add((op, f));
            }
            var runner = new JobRunner(reg);
            foreach (var g in jobsByOp.GroupBy(x => x.Op.Id))
            {
                var op = g.First().Op;
                var opts = new Dictionary<string, string>();
                if (p.Get("--quality") is { } q)
                {
                    if (op.Options.Any(s => s.Name == "quality" && s.Type == OptionType.Integer)) opts["quality"] = q;
                    else if (op.Options.Any(s => s.Name == "quality")) e.WriteLine($"note: --quality is a choice for {op.Title}; using its default.");
                }
                foreach (var kv in p.Opts.Select(x => x.Split('=', 2)))
                {
                    if (kv.Length != 2) throw new UsageException("--opt expects name=value.");
                    opts[kv[0]] = kv[1];
                }
                var jobs = runner.Plan(op, g.Select(x => x.File).ToList(), opts, outDir, policy);
                await runner.RunAsync(jobs, null, ct);
                foreach (var j in jobs)
                    reports.Add(new(j.Request.Inputs[0], j.State switch { JobState.Succeeded => "done", JobState.Cancelled => "cancelled", _ => "failed" },
                        j.Result?.Outputs.FirstOrDefault(), j.Result?.Error));
            }
        }

        if (json) o.WriteLine(JsonSerializer.Serialize(new { files = reports }, Json));
        else foreach (var r in reports)
            if (r.status == "done") o.WriteLine(r.output);
            else e.WriteLine($"{r.status.ToUpperInvariant()} {Path.GetFileName(r.input)}: {r.error}");

        if (ct.IsCancellationRequested || reports.Any(r => r.status == "cancelled")) return Cancelled;
        if (reports.All(r => r.status == "skipped")) return NothingToDo;
        return reports.Any(r => r.status is "failed" or "skipped") ? SomeFailed : Ok;
    }

    // ---------------- tools / presets / watch

    private static int Tools(string[] a, OperationRegistry reg, TextWriter o)
    {
        if (a.Length > 0 && a[0] != "list") throw new UsageException("Usage: orange tools list");
        foreach (var t in new[] { "ffmpeg", "ffprobe", "pandoc", "ghostscript", "libreoffice", "7zip" })
            o.WriteLine($"{t,-12} {ToolLocator.Default.Find(t) ?? "not found"}");
        o.WriteLine();
        foreach (var g in reg.Available.Where(x => x.Group == OperationGroup.Tool).GroupBy(x => x.Category))
            o.WriteLine($"{g.Key,-9} {string.Join(", ", g.Select(x => x.Title))}");
        return Ok;
    }

    private static int Presets(string[] a, OperationRegistry reg, TextWriter o, TextWriter e)
    {
        var sub = a.Length > 0 ? a[0] : "list";
        switch (sub)
        {
            case "list":
                foreach (var p in Store.Load(reg))
                {
                    var op = reg.All.FirstOrDefault(x => x.Id == p.OperationId);
                    o.WriteLine($"{p.Name,-20} {op?.Category} → {op?.Title ?? p.OperationId}  {string.Join(" ", p.Options.Select(kv => $"{kv.Key}={kv.Value}"))}");
                }
                return Ok;
            case "import":
                if (a.Length < 2) throw new UsageException("Usage: orange presets import <file.json>");
                var incoming = PresetJson.Import(File.ReadAllText(a[1]), reg);
                var merged = Store.Load(reg).Where(x => !incoming.Any(i => i.Name.Equals(x.Name, StringComparison.OrdinalIgnoreCase))).Concat(incoming).ToList();
                Store.Save(merged, reg);
                o.WriteLine($"Imported {incoming.Count} preset{(incoming.Count == 1 ? "" : "s")}.");
                return Ok;
            case "export":
                if (a.Length < 2) throw new UsageException("Usage: orange presets export <file.json>");
                File.WriteAllText(a[1], PresetJson.Export(Store.Load(reg)));
                o.WriteLine(Path.GetFullPath(a[1]));
                return Ok;
            default: throw new UsageException("Usage: orange presets list|import <file>|export <file>");
        }
    }

    private static async Task<int> WatchAsync(string[] a, OperationRegistry reg, TextWriter o, TextWriter e, CancellationToken ct)
    {
        var p = Parse(a, "--move-originals");
        if (p.Positional.Count != 1 || p.Get("--preset") is not { } name) throw new UsageException("Usage: orange watch <dir> --preset <name> [--move-originals]");
        var dir = Path.GetFullPath(p.Positional[0]);
        using var w = PresetWatch.Create(new WatchSpec { Folder = dir, Preset = name, MoveOriginals = p.Has("--move-originals") }, reg, Store.Load(reg));
        w.Activity += ev => o.WriteLine($"{DateTime.Now:HH:mm:ss} {ev.Status,-10} {Path.GetFileName(ev.File)}{(ev.Output is { } x ? " → " + Path.GetFileName(x) : "")}{(ev.Error is { } er ? "  (" + er + ")" : "")}");
        o.WriteLine($"Watching {dir} with \"{name}\". Press Ctrl+C to stop.");
        try { await Task.Delay(Timeout.Infinite, ct); } catch (OperationCanceledException) { }
        return Ok;
    }

    // ---------------- 0.1 commands

    private static int Ops(string[] files, OperationRegistry reg, TextWriter o)
    {
        foreach (var g in new[] { OperationGroup.Convert, OperationGroup.Tool })
        {
            o.WriteLine($"[{g}]");
            foreach (var op in reg.ForFiles(ExpandGlobs(files), g)) o.WriteLine($"  {op.Id,-28} {op.Title}");
        }
        return Ok;
    }

    private static int Search(string[] words, OperationRegistry reg, TextWriter o)
    {
        foreach (var op in reg.Search(string.Join(' ', words))) o.WriteLine($"{op.Id,-28} {op.Title}  ({op.Category})");
        return Ok;
    }

    private static int Matrix(OperationRegistry reg, TextWriter o)
    {
        o.WriteLine("| Id | Category | Wheel | Title | Output | Needs | Available here |");
        o.WriteLine("|---|---|---|---|---|---|---|");
        foreach (var op in reg.All.OrderBy(x => x.Category).ThenBy(x => x.Group).ThenBy(x => x.Id))
            o.WriteLine($"| `{op.Id}` | {op.Category} | {op.Group} | {op.Title} | {(op.ProducesFolder ? "folder" : op.OutputExtension ?? "same as input")} | {(op.RequiresTool is null ? "built in" : ToolNames.Friendly(op.RequiresTool))} | {(reg.IsAvailable(op) ? "yes" : "no")} |");
        return Ok;
    }

    /// <summary>"orange sweep <fixtures> [--json report.json] [--work dir]": runs every operation end to end.</summary>
    private static async Task<int> SweepAsync(string[] a, OperationRegistry reg, TextWriter o, CancellationToken ct)
    {
        var p = Parse(a);
        if (p.Positional.Count != 1) throw new UsageException("Usage: orange sweep <fixtures-folder> [--json report.json] [--work dir]");
        var work = p.Get("--work") is { } w ? Path.GetFullPath(w) : Path.Combine(Path.GetTempPath(), "orange-sweep-" + Guid.NewGuid().ToString("N")[..8]);
        var results = await Orange.Core.Testing.ConverterSweep.RunAsync(reg, Path.GetFullPath(p.Positional[0]), work,
            r => o.WriteLine($"{r.Status,-8} {r.Op,-28} {r.Seconds,6:0.0}s {r.Input} {(r.Error is null ? "→ " + r.Output : "· " + r.Error)}"), ct);
        var summary = results.GroupBy(r => r.Status).ToDictionary(g => g.Key, g => g.Count());
        o.WriteLine("summary: " + string.Join(", ", summary.Select(kv => $"{kv.Key}={kv.Value}")));
        if (p.Get("--json") is { } json) File.WriteAllText(json, JsonSerializer.Serialize(new { summary, results }, Json));
        return results.Any(r => r.Status == "fail") ? SomeFailed : Ok;
    }

    /// <summary>"orange engines list | install <id>|all | uninstall <id>" (same verified downloads as Settings → Engines).</summary>
    private static async Task<int> EnginesAsync(string[] a, TextWriter o, TextWriter e, CancellationToken ct)
    {
        var mgr = Orange.Core.Engines.EngineManager.ForCurrentUser();
        switch (a.FirstOrDefault())
        {
            case null or "list":
                var inst = mgr.Installed();
                foreach (var s in Orange.Core.Engines.EngineManager.Catalog)
                    o.WriteLine($"{s.Id,-12} {s.Version,-8} {(inst.FirstOrDefault(i => i.Id == s.Id) is { } i ? "installed " + i.ExePath : "not installed")}  ({s.SizeBytes / 1048576} MB, {s.License})");
                return Ok;
            case "install" when a.Length == 2:
                var specs = a[1] == "all" ? Orange.Core.Engines.EngineManager.Catalog.ToList() : Orange.Core.Engines.EngineManager.Catalog.Where(s => s.Id == a[1]).ToList();
                if (specs.Count == 0) throw new UsageException($"No engine called \"{a[1]}\". Try \"orange engines list\".");
                foreach (var s in specs)
                {
                    o.WriteLine($"Installing {s.Name} {s.Version}…");
                    var r = await mgr.InstallAsync(s, null, ct);
                    o.WriteLine($"  {r.ExePath}");
                }
                return Ok;
            case "uninstall" when a.Length == 2:
                return mgr.Uninstall(a[1]) ? Ok : NothingToDo;
            default: throw new UsageException("Usage: orange engines list | install <id>|all | uninstall <id>");
        }
    }

    private static async Task<int> RunOpAsync(string[] a, OperationRegistry reg, TextWriter o, TextWriter e, CancellationToken ct)
    {
        if (a.Length < 2) throw new UsageException("Usage: orange run <op-id> <files> [--opt name=value]... [-o dir]");
        var op = reg.Get(a[0]);
        var p = Parse(a[1..]);
        var opts = new Dictionary<string, string>();
        foreach (var kv in p.Opts.Select(x => x.Split('=', 2)))
        {
            if (kv.Length != 2) throw new UsageException("--opt expects name=value.");
            opts[kv[0]] = kv[1];
        }
        var outDir = p.Get("-o", "--out") is { } od ? Path.GetFullPath(od) : null;
        var runner = new JobRunner(reg);
        var jobs = runner.Plan(op, ExpandGlobs(p.Positional), opts, outDir);
        await runner.RunAsync(jobs, null, ct);
        int failed = 0;
        foreach (var j in jobs)
        {
            if (j.State == JobState.Succeeded) foreach (var x in j.Result!.Outputs) o.WriteLine(x);
            else { failed++; e.WriteLine($"FAILED {j.DisplayName}: {j.Result?.Error}"); }
        }
        return failed == 0 ? Ok : SomeFailed;
    }
}
