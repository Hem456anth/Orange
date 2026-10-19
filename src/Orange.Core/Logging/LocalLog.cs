using System.Text;

namespace Orange.Core.Logging;

/// <summary>
/// Opt-in plain-text debug log on the local disk (the app uses %LOCALAPPDATA%\Orange\logs). Off by default.
/// No network, no file contents: callers log event names, key codes, data formats, file paths and results.
/// One file per day (orange-yyyyMMdd.log), each capped (rolls to .1), files older than <see cref="KeepDays"/> deleted.
/// Never throws: logging must not break a drag or a conversion.
/// </summary>
public static class LocalLog
{
    private static readonly object Gate = new();
    private static string? _folder;

    public static long MaxBytesPerFile { get; set; } = 2 * 1024 * 1024;
    public static int KeepDays { get; set; } = 7;
    public static bool Enabled => _folder is not null;
    public static string? Folder => _folder;

    /// <summary>Clock seam for tests.</summary>
    public static Func<DateTime> Now { get; set; } = () => DateTime.Now;

    public static void Enable(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            _folder = folder;
            Prune();
            Write("log-enabled");
        }
        catch { _folder = null; }
    }

    public static void Disable()
    {
        if (_folder is null) return;
        Write("log-disabled");
        _folder = null;
    }

    public static string CurrentFile(string folder) => Path.Combine(folder, $"orange-{Now():yyyyMMdd}.log");

    public static void Write(string line)
    {
        var folder = _folder;
        if (folder is null) return;
        try
        {
            var text = $"{Now():yyyy-MM-dd HH:mm:ss.fff} {Clean(line)}{Environment.NewLine}";
            var bytes = Encoding.UTF8.GetBytes(text);
            lock (Gate)
            {
                var file = CurrentFile(folder);
                var fi = new FileInfo(file);
                if (fi.Exists && fi.Length + bytes.Length > MaxBytesPerFile)
                {
                    var rolled = file + ".1";
                    File.Delete(rolled);
                    File.Move(file, rolled);
                }
                using var fs = new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                fs.Write(bytes);
            }
        }
        catch { /* best effort */ }
    }

    /// <summary>Deletes orange-*.log files older than KeepDays. Touches nothing else in the folder.</summary>
    public static void Prune()
    {
        var folder = _folder;
        if (folder is null) return;
        try
        {
            var cutoff = Now().Date.AddDays(-KeepDays);
            foreach (var f in Directory.EnumerateFiles(folder, "orange-*.log*"))
            {
                var name = Path.GetFileName(f);
                if (name.Length < 18 || !DateTime.TryParseExact(name.Substring(7, 8), "yyyyMMdd", null, System.Globalization.DateTimeStyles.None, out var day)) continue;
                if (day < cutoff) File.Delete(f);
            }
        }
        catch { }
    }

    /// <summary>One event per line: control characters (CR/LF/tab…) become spaces so a file name can't forge log lines.</summary>
    private static string Clean(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s) sb.Append(char.IsControl(c) ? ' ' : c);
        return sb.Length > 4000 ? sb.ToString(0, 4000) + "…" : sb.ToString();
    }
}
