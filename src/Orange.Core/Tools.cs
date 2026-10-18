using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Orange.Core;

/// <summary>
/// Finds external tools. Search order: an explicit override (Settings), the "tools" folder next to Orange.exe
/// (the bundled copies), then PATH. Never downloads anything.
/// </summary>
public sealed class ToolLocator
{
    private readonly Dictionary<string, string?> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _overrides = new(StringComparer.OrdinalIgnoreCase);
    public static ToolLocator Default { get; set; } = new();

    private static readonly Dictionary<string, string[]> Candidates = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ffmpeg"] = ["ffmpeg"],
        ["ffprobe"] = ["ffprobe"],
        ["ghostscript"] = ["gswin64c", "gswin32c", "gs"],
        ["pandoc"] = ["pandoc"],
        ["libreoffice"] = ["soffice", "soffice.com"],
    };

    public void Override(string tool, string path) { _overrides[tool] = path; _cache.Remove(tool); }

    public string? Find(string tool)
    {
        if (_cache.TryGetValue(tool, out var hit)) return hit;
        string? found = null;
        if (_overrides.TryGetValue(tool, out var o) && File.Exists(o)) found = o;
        var names = Candidates.TryGetValue(tool, out var c) ? c : [tool];
        var exts = OperatingSystem.IsWindows() ? new[] { ".exe", ".com" } : new[] { "" };
        var bundled = Path.Combine(AppContext.BaseDirectory, "tools");
        var dirs = new List<string> { bundled, Path.Combine(bundled, tool), Path.Combine(bundled, tool, "bin") };
        if (OperatingSystem.IsWindows())
        {
            var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            if (tool.Equals("libreoffice", StringComparison.OrdinalIgnoreCase)) dirs.Add(Path.Combine(pf, "LibreOffice", "program"));
            if (tool.Equals("ghostscript", StringComparison.OrdinalIgnoreCase) && Directory.Exists(Path.Combine(pf, "gs")))
                dirs.AddRange(Directory.GetDirectories(Path.Combine(pf, "gs")).OrderDescending().Select(d => Path.Combine(d, "bin")));
        }
        dirs.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries));
        foreach (var d in dirs)
        {
            if (found != null) break;
            foreach (var n in names)
            foreach (var e in exts)
            {
                // Only absolute directories: a relative PATH entry (".") could plant a fake ffmpeg next to the user's file.
                if (!Path.IsPathRooted(d)) continue;
                var p = Path.Combine(d, n + e);
                if (File.Exists(p)) { found = p; goto done; }
            }
        }
        done:
        _cache[tool] = found;
        return found;
    }

    public bool Has(string tool) => Find(tool) != null;

    public string Require(string tool) => Find(tool) ?? throw new OrangeException(
        $"This needs {ToolNames.Friendly(tool)}, which isn't installed.",
        $"Install {ToolNames.Friendly(tool)} or point Orange to it in Settings → Converters.");
}

public static class ToolNames
{
    public static string Friendly(string tool) => tool.ToLowerInvariant() switch
    {
        "ffmpeg" or "ffprobe" => "FFmpeg",
        "ghostscript" => "Ghostscript",
        "pandoc" => "Pandoc",
        "libreoffice" => "LibreOffice",
        _ => tool,
    };
}

public sealed record ProcessOutcome(int ExitCode, string StdOut, string StdErr);

/// <summary>
/// Runs an external tool without a shell. Arguments go through ProcessStartInfo.ArgumentList, so no file name
/// can inject a command, and nothing is ever concatenated into a command line string.
/// </summary>
public static class ProcessRunner
{
    private const int MaxCapture = 1 << 20; // keep at most 1 MB of each stream

    public static async Task<ProcessOutcome> RunAsync(string exe, IEnumerable<string> args, CancellationToken ct,
        Action<string>? onStdOutLine = null, Action<string>? onStdErrLine = null, string? workingDir = null, TimeSpan? timeout = null)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = workingDir ?? Path.GetTempPath(),
        };
        foreach (var a in args)
        {
            if (a.Contains('\0')) throw new OrangeException("Invalid argument.");
            psi.ArgumentList.Add(a);
        }
        // Tools inherit a minimal, predictable environment.
        psi.Environment.Remove("FFREPORT");
        psi.Environment["AV_LOG_FORCE_NOCOLOR"] = "1";

        using var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var so = new StringBuilder(); var se = new StringBuilder();
        p.OutputDataReceived += (_, e) => { if (e.Data is null) return; onStdOutLine?.Invoke(e.Data); if (so.Length < MaxCapture) so.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data is null) return; onStdErrLine?.Invoke(e.Data); if (se.Length < MaxCapture) se.AppendLine(e.Data); };
        try { p.Start(); }
        catch (Exception e) { throw new OrangeException($"Couldn't start {Path.GetFileNameWithoutExtension(exe)}.", e.Message, e); }
        p.StandardInput.Close();
        p.BeginOutputReadLine(); p.BeginErrorReadLine();
        TryLowerPriority(p);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout ?? EngineLimits.Current.ProcessTimeout);
        try { await p.WaitForExitAsync(cts.Token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch { /* already gone */ }
            if (ct.IsCancellationRequested) throw;
            throw new OrangeException("The conversion took too long and was stopped.", "Raise the time limit in Settings for very large files.");
        }
        p.WaitForExit(); // flush async readers
        return new ProcessOutcome(p.ExitCode, so.ToString(), se.ToString());
    }

    private static void TryLowerPriority(Process p)
    {
        try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { /* not permitted */ }
    }

    /// <summary>The last meaningful lines of a tool's stderr, for the "Details" part of an error.</summary>
    public static string Tail(string text, int lines = 6) =>
        string.Join(Environment.NewLine, text.Split('\n').Select(l => l.TrimEnd()).Where(l => l.Length > 0).TakeLast(lines));
}

public static class OptionValidator
{
    public static int ParseInt(OperationDescriptor op, string name, string value)
    {
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
            throw new OrangeException($"\"{value}\" isn't a whole number for {Label(op, name)}.");
        CheckRange(op, name, v);
        return v;
    }
    public static double ParseDouble(OperationDescriptor op, string name, string value)
    {
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || double.IsNaN(v) || double.IsInfinity(v))
            throw new OrangeException($"\"{value}\" isn't a number for {Label(op, name)}.");
        CheckRange(op, name, v);
        return v;
    }
    public static bool ParseBool(string v) => v.Equals("true", StringComparison.OrdinalIgnoreCase) || v == "1" || v.Equals("yes", StringComparison.OrdinalIgnoreCase);

    /// <summary>Accepts "", "90", "1:30", "01:02:03.5". Empty means "not set".</summary>
    public static TimeSpan? ParseTime(string v)
    {
        v = v.Trim();
        if (v.Length == 0) return null;
        var parts = v.Split(':');
        if (parts.Length > 3) throw new OrangeException($"\"{v}\" isn't a time. Use seconds or mm:ss.");
        double total = 0;
        foreach (var part in parts)
        {
            if (!double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) || n < 0)
                throw new OrangeException($"\"{v}\" isn't a time. Use seconds or mm:ss.");
            total = total * 60 + n;
        }
        return TimeSpan.FromSeconds(total);
    }

    public static void Validate(OperationRequest r)
    {
        foreach (var (k, v) in r.Options)
        {
            var spec = r.Operation.Options.FirstOrDefault(o => o.Name == k) ?? throw new OrangeException($"Unknown setting \"{k}\".");
            switch (spec.Type)
            {
                case OptionType.Integer: ParseInt(r.Operation, k, v); break;
                case OptionType.Number: ParseDouble(r.Operation, k, v); break;
                case OptionType.TimeSpan: ParseTime(v); break;
                case OptionType.Choice when spec.Choices is { } c && !c.Contains(v):
                    throw new OrangeException($"\"{v}\" isn't a valid choice for {spec.Label}.");
                case OptionType.Text when v.Length > 500:
                    throw new OrangeException($"{spec.Label} is too long.");
            }
        }
    }

    private static void CheckRange(OperationDescriptor op, string name, double v)
    {
        var s = op.Options.FirstOrDefault(o => o.Name == name);
        if (s is null) return;
        if (s.Min is { } min && v < min || s.Max is { } max && v > max)
            throw new OrangeException($"{s.Label} must be between {s.Min} and {s.Max}.");
    }
    private static string Label(OperationDescriptor op, string name) => op.Options.FirstOrDefault(o => o.Name == name)?.Label ?? name;
}

internal static class Inv
{
    public static string F(double d) => d.ToString("0.###", CultureInfo.InvariantCulture);
}
