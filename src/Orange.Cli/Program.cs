using Orange.Core;

// orange-cli: the same engine the Windows app uses, for scripting, CI and the Explorer "Send to" fallback.
//   orange-cli ops <files...>                 operations offered for these files (convert + tools)
//   orange-cli search <words...>              search all operations
//   orange-cli run <op-id> <files...> [--opt name=value]... [--out <dir>] [--overwrite keep|replace|fail]
//   orange-cli matrix                         every operation with its availability on this machine (markdown)
//   orange-cli tools                          which external converters were found

var registry = OperationRegistry.CreateDefault();
if (args.Length == 0) { Usage(); return 2; }

try
{
    switch (args[0])
    {
        case "ops":
        {
            var files = args.Skip(1).ToList();
            foreach (var g in new[] { OperationGroup.Convert, OperationGroup.Tool })
            {
                Console.WriteLine($"[{g}]");
                foreach (var op in registry.ForFiles(files, g)) Console.WriteLine($"  {op.Id,-28} {op.Title}");
            }
            return 0;
        }
        case "search":
            foreach (var op in registry.Search(string.Join(' ', args.Skip(1)))) Console.WriteLine($"{op.Id,-28} {op.Title}  ({op.Category})");
            return 0;
        case "tools":
            foreach (var t in new[] { "ffmpeg", "ffprobe", "ghostscript", "pandoc", "libreoffice" })
                Console.WriteLine($"{t,-12} {ToolLocator.Default.Find(t) ?? "not found"}");
            return 0;
        case "matrix":
            Console.WriteLine("| Id | Category | Wheel | Title | Output | Needs | Available here |");
            Console.WriteLine("|---|---|---|---|---|---|---|");
            foreach (var op in registry.All.OrderBy(o => o.Category).ThenBy(o => o.Group).ThenBy(o => o.Id))
                Console.WriteLine($"| `{op.Id}` | {op.Category} | {op.Group} | {op.Title} | {(op.ProducesFolder ? "folder" : op.OutputExtension ?? "same as input")} | {(op.RequiresTool is null ? "built in" : ToolNames.Friendly(op.RequiresTool))} | {(registry.IsAvailable(op) ? "yes" : "no")} |");
            return 0;
        case "run":
        {
            if (args.Length < 3) { Usage(); return 2; }
            var op = registry.Get(args[1]);
            var files = new List<string>(); var opts = new Dictionary<string, string>(); string? outDir = null;
            var policy = OverwritePolicy.KeepBoth;
            for (int i = 2; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--opt":
                        var kv = args[++i].Split('=', 2);
                        if (kv.Length != 2) throw new OrangeException($"--opt expects name=value, got \"{args[i]}\".");
                        opts[kv[0]] = kv[1]; break;
                    case "--out": outDir = Path.GetFullPath(args[++i]); break;
                    case "--overwrite":
                        policy = args[++i] switch { "replace" => OverwritePolicy.Replace, "fail" => OverwritePolicy.Fail, _ => OverwritePolicy.KeepBoth }; break;
                    default: files.Add(args[i]); break;
                }
            }
            var runner = new JobRunner(registry);
            var jobs = runner.Plan(op, files, opts, outDir, policy);
            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
            int lastPct = -1;
            var progress = new Progress<BatchProgress>(p =>
            {
                var pct = (int)(p.Fraction * 100);
                if (pct != lastPct && pct % 10 == 0) { lastPct = pct; Console.Error.WriteLine($"{pct,3}%  {p.Done}/{p.Total}"); }
            });
            await runner.RunAsync(jobs, progress, cts.Token);
            int failed = 0;
            foreach (var j in jobs)
            {
                if (j.State == JobState.Succeeded) foreach (var o in j.Result!.Outputs) Console.WriteLine(o);
                else { failed++; Console.Error.WriteLine($"FAILED {j.DisplayName}: {j.Result?.Error}{(j.Result?.Detail is { } d ? Environment.NewLine + "  " + d : "")}"); }
            }
            return failed == 0 ? 0 : 1;
        }
        default: Usage(); return 2;
    }
}
catch (OrangeException e)
{
    Console.Error.WriteLine(e.Message);
    if (e.Detail is not null) Console.Error.WriteLine("  " + e.Detail);
    return 1;
}

static void Usage() => Console.Error.WriteLine("usage: orange-cli ops|search|run|matrix|tools …  (see Program.cs header)");
