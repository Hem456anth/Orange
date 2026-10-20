using System.Diagnostics;

namespace Orange.Core.Testing;

public sealed record SweepResult(string Op, string Status, string? Input, string? Output, double Seconds, string? Error);

/// <summary>
/// End-to-end check of every registered operation: picks a real input for it (starting from the fixtures folder and
/// then from files earlier operations produced, so MP4 → MKV feeds the MKV tools), runs it with default options
/// through the same JobRunner the app uses, and checks that every output exists and isn't empty.
/// Status: pass, fail, skip (a helper program isn't installed), no-input (no file of that kind could be made).
/// </summary>
public static class ConverterSweep
{
    /// <summary>Options an operation can't run without (the UI asks for them); the sweep supplies sensible ones.</summary>
    public static readonly Dictionary<string, Dictionary<string, string>> RequiredOptions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["image.redact"] = new() { ["regions"] = "10,10,40,40" },
        ["image.annotate"] = new() { ["text"] = "Orange" },
        ["image.metadata"] = new() { ["artist"] = "Orange test" },
        ["pdf.reorder"] = new() { ["order"] = "1" },
        ["audio.metadata"] = new() { ["title"] = "Orange test" },
        ["video.metadata"] = new() { ["title"] = "Orange test" },
        ["audio.bleep"] = new() { ["ranges"] = "0:00-0:01" },
        ["audio.trim"] = new() { ["end"] = "1" },
        ["video.trim"] = new() { ["end"] = "1" },
        ["pdf.compress"] = new() { ["level"] = "screen" },
    };

    /// <summary>Content-dependent operations get a fixture that has what they look for (a QR code, embedded photos).</summary>
    public static readonly Dictionary<string, string> PreferredInput = new(StringComparer.OrdinalIgnoreCase)
    {
        ["image.qr-read"] = "qr.png",
        ["image.compress"] = "sample.png",
        ["pdf.compress"] = "photo-heavy.pdf",
        ["pdf.extract-images"] = "with-image.pdf",
    };

    public static async Task<List<SweepResult>> RunAsync(OperationRegistry reg, string fixturesDir, string workDir,
        Action<SweepResult>? onResult = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(workDir);
        var pool = new List<string>();
        // Plain "sample.*" files first: special fixtures (no audio track, animated, QR, deliberately hostile) only
        // stand in when nothing else fits.
        foreach (var f in Directory.EnumerateFiles(fixturesDir)
                     .Where(f => !Path.GetFileName(f).StartsWith("hostile", StringComparison.OrdinalIgnoreCase) && !Path.GetFileName(f).Equals("sample.epub", StringComparison.OrdinalIgnoreCase))
                     .OrderBy(f => Path.GetFileNameWithoutExtension(f).Equals("sample", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                     .ThenBy(f => f, StringComparer.OrdinalIgnoreCase))
            pool.Add(f);
        var results = new Dictionary<string, SweepResult>(StringComparer.OrdinalIgnoreCase);
        var runner = new JobRunner(reg);

        // Several passes: each pass may produce new kinds of input (MKV, ODT, FLAC…) for operations still waiting.
        for (int pass = 0; pass < 3; pass++)
        {
            bool progress = false;
            foreach (var op in reg.All.OrderBy(o => o.Category).ThenBy(o => o.Id))
            {
                ct.ThrowIfCancellationRequested();
                if (results.TryGetValue(op.Id, out var prev) && prev.Status != "no-input") continue;
                if (!reg.IsAvailable(op))
                {
                    results[op.Id] = new(op.Id, "skip", null, null, 0, $"needs {ToolNames.Friendly(op.RequiresTool ?? "a helper")}");
                    continue;
                }
                var input = (PreferredInput.TryGetValue(op.Id, out var pref) ? pool.FirstOrDefault(f => Path.GetFileName(f).Equals(pref, StringComparison.OrdinalIgnoreCase)) : null)
                            ?? pool.FirstOrDefault(f => Applies(reg, op, f));
                if (input is null) { results[op.Id] = new(op.Id, "no-input", null, null, 0, null); continue; }

                var dir = Path.Combine(workDir, op.Id);
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
                Directory.CreateDirectory(dir);
                var inputs = new List<string>();
                for (int i = 0; i < Math.Max(1, op.MinInputs); i++)
                {
                    var copy = Path.Combine(dir, (i == 0 ? "" : $"{i + 1}-") + Path.GetFileName(input));
                    File.Copy(input, copy);
                    inputs.Add(copy);
                }
                var sw = Stopwatch.StartNew();
                SweepResult r;
                try
                {
                    var opts = RequiredOptions.TryGetValue(op.Id, out var o) ? o : null;
                    var jobs = runner.Plan(op, inputs, opts);
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    cts.CancelAfter(TimeSpan.FromMinutes(5));
                    await runner.RunAsync(jobs, null, cts.Token);
                    var bad = jobs.FirstOrDefault(j => j.State != JobState.Succeeded);
                    if (bad is not null) r = new(op.Id, "fail", Path.GetFileName(input), null, sw.Elapsed.TotalSeconds, $"{bad.Result?.Error} {bad.Result?.Detail}".Trim());
                    else
                    {
                        var outs = jobs.SelectMany(j => j.Result!.Outputs).ToList();
                        var empty = outs.FirstOrDefault(x => !(File.Exists(x) && new FileInfo(x).Length > 0) && !(Directory.Exists(x) && Directory.EnumerateFileSystemEntries(x).Any()));
                        var detail = string.Join(" ", jobs.Select(j => j.Result?.Detail).Where(d => !string.IsNullOrEmpty(d)));
                        if (outs.Count == 0 && detail.Length > 0)   // read-only tools (QR read) answer in the message
                            r = new(op.Id, "pass", Path.GetFileName(input), "“" + detail[..Math.Min(80, detail.Length)] + "”", sw.Elapsed.TotalSeconds, null);
                        else r = outs.Count == 0 || empty is not null
                            ? new(op.Id, "fail", Path.GetFileName(input), empty, sw.Elapsed.TotalSeconds, "no output or empty output")
                            : new(op.Id, "pass", Path.GetFileName(input), string.Join(" | ", outs.Select(Path.GetFileName)), sw.Elapsed.TotalSeconds, null);
                        if (r.Status == "pass")
                            foreach (var x in outs.Where(File.Exists))
                                if (!pool.Any(p => string.Equals(Path.GetExtension(p), Path.GetExtension(x), StringComparison.OrdinalIgnoreCase)))
                                {   // keep one of each new kind for later operations
                                    var keep = Path.Combine(workDir, "_pool", Path.GetFileName(x));
                                    Directory.CreateDirectory(Path.GetDirectoryName(keep)!);
                                    if (!File.Exists(keep)) File.Copy(x, keep);
                                    pool.Add(keep); progress = true;
                                }
                    }
                }
                catch (Exception e) { r = new(op.Id, "fail", Path.GetFileName(input), null, sw.Elapsed.TotalSeconds, e.Message); }
                results[op.Id] = r;
                onResult?.Invoke(r);
            }
            if (!progress) break;
        }
        foreach (var r in results.Values.Where(r => r.Status is "skip" or "no-input")) onResult?.Invoke(r);
        return results.Values.OrderBy(r => r.Op).ToList();
    }

    private static bool Applies(OperationRegistry reg, OperationDescriptor op, string file)
    {
        // Direct match (not ForFiles, which hides a provider's op when another provider offers the same format):
        // every registered operation gets exercised, including the Pandoc/LibreOffice routes behind the built-in ones.
        try
        {
            if (FileTypeDetector.Detect(file) != op.Category) return false;
            var ext = OperationRegistry.Normalize(FileTypeDetector.Ext(file));
            if (op.InputExtensions.Count > 0 && !op.InputExtensions.Contains(ext)) return false;
            if (op.Group == OperationGroup.Convert && op.OutputExtension is { } x && OperationRegistry.Normalize(x) == ext) return false;
            return true;
        }
        catch { return false; }
    }
}
