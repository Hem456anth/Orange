namespace Orange.Core;

/// <summary>Input validation, output naming, temp workspaces and atomic commit of results.</summary>
public static class SafePaths
{
    /// <summary>Default per-file cap. Configurable through <see cref="EngineLimits"/>.</summary>
    public static long MaxInputBytes => EngineLimits.Current.MaxInputBytes;

    /// <summary>Validates an input path and returns its canonical full path.</summary>
    public static string ValidateInput(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new OrangeException("No file was given.");
        if (path.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || path.Contains('\0'))
            throw new OrangeException("The file path contains characters Windows does not allow.");
        string full;
        try { full = Path.GetFullPath(path); }
        catch (Exception e) { throw new OrangeException("That file path isn't valid.", e.Message, e); }

        if (full.StartsWith(@"\\?\GLOBALROOT", StringComparison.OrdinalIgnoreCase) || full.StartsWith(@"\\.\", StringComparison.Ordinal))
            throw new OrangeException("Device paths aren't supported.");
        var info = new FileInfo(full);
        if (!info.Exists)
            throw new OrangeException(Directory.Exists(full) ? $"\"{info.Name}\" is a folder. Pick files inside it." : $"\"{info.Name}\" no longer exists.");
        if (info.Length == 0) throw new OrangeException($"\"{info.Name}\" is empty.");
        if (info.Length > MaxInputBytes)
            throw new OrangeException($"\"{info.Name}\" is larger than the {MaxInputBytes / (1024 * 1024 * 1024.0):0.#} GB limit set in Settings.");
        // Cloud placeholders (OneDrive "online-only") would trigger a download on read; we allow it but callers can warn.
        return full;
    }

    /// <summary>Strips characters that are illegal or dangerous in a Windows file name and reserved device names.</summary>
    public static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat(new[] { '<', '>', ':', '"', '/', '\\', '|', '?', '*' }).ToHashSet();
        var chars = name.Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray();
        var s = new string(chars).Trim().TrimEnd('.', ' ');
        if (s.Length == 0) s = "file";
        var stem = s.Split('.')[0].ToUpperInvariant();
        string[] reserved = ["CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
                             "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"];
        if (reserved.Contains(stem)) s = "_" + s;
        return s.Length > 200 ? s[..200] : s;
    }

    /// <summary>
    /// Plans the output path: "name.ext" for conversions, "name Suffix.ext" for tools, " 2", " 3"… on a clash.
    /// Never returns an input path.
    /// </summary>
    public static string PlanOutput(string firstInput, string? outputDir, string baseName, string? suffix, string extension,
                                    OverwritePolicy policy, IEnumerable<string>? inputs = null, bool isFolder = false)
    {
        var dir = outputDir ?? Path.GetDirectoryName(Path.GetFullPath(firstInput))!;
        if (!Directory.Exists(dir)) throw new OrangeException($"The destination folder \"{dir}\" doesn't exist.");
        var stem = SanitizeFileName(suffix is null ? baseName : $"{baseName} {suffix}");
        var ext = isFolder ? "" : "." + SanitizeFileName(extension.TrimStart('.')).ToLowerInvariant();
        var inputSet = new HashSet<string>((inputs ?? new[] { firstInput }).Select(Path.GetFullPath), StringComparer.OrdinalIgnoreCase);

        string Candidate(int n) => Path.Combine(dir, n == 1 ? stem + ext : $"{stem} {n}{ext}");
        bool Exists(string p) => File.Exists(p) || Directory.Exists(p);

        var first = Candidate(1);
        if (inputSet.Contains(first) || (Exists(first) && policy == OverwritePolicy.KeepBoth))
        {
            for (int n = 2; n < 10_000; n++)
            {
                var c = Candidate(n);
                if (!Exists(c) && !inputSet.Contains(c)) return c;
            }
            throw new OrangeException("Couldn't find a free file name in the destination folder.");
        }
        if (Exists(first) && policy == OverwritePolicy.Fail)
            throw new OrangeException($"\"{Path.GetFileName(first)}\" already exists.", "Choose Keep both or Replace in Settings.");
        return first;
    }

    /// <summary>A private scratch folder under %TEMP%\Orange that is deleted on dispose.</summary>
    public sealed class TempWorkspace : IDisposable
    {
        public string Root { get; }
        public TempWorkspace()
        {
            Root = Path.Combine(Path.GetTempPath(), "Orange", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }
        /// <summary>A path inside the workspace with an ASCII-only, unique name (protects tools from odd characters).</summary>
        public string File(string extension) => Path.Combine(Root, Guid.NewGuid().ToString("N")[..12] + "." + extension.TrimStart('.'));
        public string Dir(string name = "out") { var d = Path.Combine(Root, name); Directory.CreateDirectory(d); return d; }
        public void Dispose()
        {
            for (int i = 0; i < 3; i++)
            {
                try { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); return; }
                catch (IOException) { Thread.Sleep(100); }
                catch (UnauthorizedAccessException) { Thread.Sleep(100); }
            }
        }

        /// <summary>Removes scratch folders older than a day left by crashes.</summary>
        public static void CleanupStale()
        {
            var root = Path.Combine(Path.GetTempPath(), "Orange");
            if (!Directory.Exists(root)) return;
            foreach (var d in Directory.EnumerateDirectories(root))
                try { if (Directory.GetLastWriteTimeUtc(d) < DateTime.UtcNow.AddDays(-1)) Directory.Delete(d, true); } catch { /* in use */ }
        }
    }

    /// <summary>Validates a produced file and moves it to its final location (replace only when the policy allows).</summary>
    public static string Commit(string tempFile, string finalPath, OverwritePolicy policy)
    {
        var fi = new FileInfo(tempFile);
        if (!fi.Exists || fi.Length == 0) throw new OrangeException("The conversion produced no output.", "The converter exited without writing a file.");
        if (System.IO.File.Exists(finalPath))
        {
            if (policy != OverwritePolicy.Replace) finalPath = PlanOutput(finalPath, null, Path.GetFileNameWithoutExtension(finalPath), null, FileTypeDetector.Ext(finalPath), OverwritePolicy.KeepBoth);
            else { System.IO.File.Move(tempFile, finalPath, overwrite: true); return finalPath; }
        }
        System.IO.File.Move(tempFile, finalPath, overwrite: false);
        return finalPath;
    }

    public static string CommitFolder(string tempDir, string finalDir)
    {
        if (!Directory.EnumerateFileSystemEntries(tempDir).Any()) throw new OrangeException("Nothing was extracted.");
        if (Directory.Exists(finalDir)) finalDir = PlanOutput(finalDir, Path.GetDirectoryName(finalDir), Path.GetFileName(finalDir), null, "", OverwritePolicy.KeepBoth, isFolder: true);
        try { Directory.Move(tempDir, finalDir); }
        catch (IOException)   // different volume: copy then delete
        {
            CopyDir(tempDir, finalDir);
        }
        return finalDir;
    }

    private static void CopyDir(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var f in Directory.EnumerateFiles(src)) System.IO.File.Copy(f, Path.Combine(dst, Path.GetFileName(f)));
        foreach (var d in Directory.EnumerateDirectories(src)) CopyDir(d, Path.Combine(dst, Path.GetFileName(d)));
    }

    /// <summary>True when <paramref name="candidate"/> resolves inside <paramref name="root"/> (zip-slip guard).</summary>
    public static bool IsInside(string root, string candidate)
    {
        var r = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var c = Path.GetFullPath(candidate);
        return c.StartsWith(r, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }
}

/// <summary>Resource limits applied to every job. Adjustable in Settings.</summary>
public sealed record EngineLimits
{
    public long MaxInputBytes { get; init; } = 8L * 1024 * 1024 * 1024;     // 8 GB
    public long MaxImagePixels { get; init; } = 300_000_000;                 // ~17k x 17k
    public long MaxPdfPages { get; init; } = 5_000;
    public long MaxArchiveExpandedBytes { get; init; } = 20L * 1024 * 1024 * 1024;
    public int MaxArchiveEntries { get; init; } = 100_000;
    public TimeSpan ProcessTimeout { get; init; } = TimeSpan.FromHours(4);
    public int MaxParallelJobs { get; init; } = Math.Max(1, Environment.ProcessorCount / 2);
    public static EngineLimits Current { get; set; } = new();
}
