using System.Formats.Tar;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace Orange.Core.Providers;

/// <summary>SRT ⇄ VTT, and either → plain-text transcript. Managed code, no external tools.</summary>
public sealed partial class SubtitleProvider : IOperationProvider
{
    public string Name => "Subtitles";
    public IEnumerable<OperationDescriptor> Describe()
    {
        foreach (var (ext, title) in new[] { ("srt", "SRT"), ("vtt", "VTT"), ("txt", "TXT") })
            yield return new OperationDescriptor
            {
                Id = $"sub.to.{ext}", Title = title, Category = FileKind.Subtitle, Group = OperationGroup.Convert,
                InputExtensions = new HashSet<string> { "srt", "vtt" }, OutputExtension = ext, Keywords = [ext, "subtitles", "captions", ext == "txt" ? "transcript" : ext],
            };
        yield return new OperationDescriptor
        {
            Id = "sub.shift", Title = "Shift timing", Category = FileKind.Subtitle, Group = OperationGroup.Tool, InputExtensions = new HashSet<string> { "srt", "vtt" },
            Options = [new("seconds", "Shift by (seconds, negative = earlier)", OptionType.Number, "1", -36000, 36000)], Icon = "clock",
            Keywords = ["sync", "delay", "offset", "suffix:Shifted"],
        };
    }
    public bool IsAvailable(OperationDescriptor op) => true;

    public sealed record Cue(TimeSpan Start, TimeSpan End, string Text);

    [GeneratedRegex(@"(?<s>(\d+:)?\d{1,2}:\d{2}[\.,]\d{1,3})\s*-->\s*(?<e>(\d+:)?\d{1,2}:\d{2}[\.,]\d{1,3})")]
    private static partial Regex TimingLine();

    public static List<Cue> Parse(string text)
    {
        var cues = new List<Cue>();
        var blocks = text.Replace("\r\n", "\n").Replace('\r', '\n').Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
        foreach (var block in blocks)
        {
            var lines = block.Split('\n').ToList();
            int idx = lines.FindIndex(l => TimingLine().IsMatch(l));
            if (idx < 0) continue;                          // WEBVTT header, NOTE, STYLE blocks
            var m = TimingLine().Match(lines[idx]);
            cues.Add(new Cue(ParseTime(m.Groups["s"].Value), ParseTime(m.Groups["e"].Value), string.Join("\n", lines.Skip(idx + 1)).Trim()));
        }
        return cues;
    }

    private static TimeSpan ParseTime(string s)
    {
        var parts = s.Replace(',', '.').Split(':');
        double sec = double.Parse(parts[^1], CultureInfo.InvariantCulture);
        int min = int.Parse(parts[^2]); int hr = parts.Length == 3 ? int.Parse(parts[0]) : 0;
        return TimeSpan.FromHours(hr) + TimeSpan.FromMinutes(min) + TimeSpan.FromSeconds(sec);
    }

    private static string Fmt(TimeSpan t, char sep) =>
        $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}{sep}{t.Milliseconds:000}";

    public static string ToSrt(IEnumerable<Cue> cues) =>
        string.Join("\n", cues.Select((c, i) => $"{i + 1}\n{Fmt(c.Start, ',')} --> {Fmt(c.End, ',')}\n{c.Text}\n"));
    public static string ToVtt(IEnumerable<Cue> cues) =>
        "WEBVTT\n\n" + string.Join("\n", cues.Select(c => $"{Fmt(c.Start, '.')} --> {Fmt(c.End, '.')}\n{c.Text}\n"));
    public static string ToTxt(IEnumerable<Cue> cues) =>
        string.Join("\n", cues.Select(c => Regex.Replace(c.Text, "<[^>]+>", "").Replace('\n', ' ')).Where(s => s.Length > 0)) + "\n";

    public async Task<OperationResult> ExecuteAsync(OperationRequest r, IProgress<double>? progress, CancellationToken ct)
    {
        var input = r.Inputs[0];
        var cues = Parse(await File.ReadAllTextAsync(input, ct));
        if (cues.Count == 0) throw new OrangeException($"No subtitle cues were found in \"{Path.GetFileName(input)}\".");
        var srcExt = FileTypeDetector.Ext(input);
        string ext, body; string? suffix = null;
        if (r.Operation.Id == "sub.shift")
        {
            var by = TimeSpan.FromSeconds(r.OptDouble("seconds"));
            cues = cues.Select(c => c with { Start = Max0(c.Start + by), End = Max0(c.End + by) }).ToList();
            ext = srcExt; suffix = "Shifted";
            body = ext == "vtt" ? ToVtt(cues) : ToSrt(cues);
        }
        else
        {
            ext = r.Operation.OutputExtension!;
            body = ext switch { "srt" => ToSrt(cues), "vtt" => ToVtt(cues), _ => ToTxt(cues) };
        }
        using var tmp = new SafePaths.TempWorkspace();
        var o = tmp.File(ext);
        await File.WriteAllTextAsync(o, body, new UTF8Encoding(false), ct);
        var final = SafePaths.PlanOutput(input, r.OutputDirectory, Path.GetFileNameWithoutExtension(input), suffix, ext, r.Overwrite, r.Inputs);
        progress?.Report(1);
        return OperationResult.Ok(SafePaths.Commit(o, final, r.Overwrite));
        static TimeSpan Max0(TimeSpan t) => t < TimeSpan.Zero ? TimeSpan.Zero : t;
    }
}

/// <summary>
/// ZIP / TAR / GZIP extraction with zip-slip, link, entry-count and expansion-size guards; ZIP creation from several files.
/// RAR (and 7z) extraction is offered only when 7-Zip is installed; Orange lists the archive first and applies the
/// same path, link, count and size guards before letting 7-Zip write anything.
/// </summary>
public sealed class ArchiveProvider(ToolLocator? tools = null) : IOperationProvider
{
    private readonly ToolLocator _tools = tools ?? ToolLocator.Default;
    public string Name => "Archives";
    public IEnumerable<OperationDescriptor> Describe()
    {
        yield return new OperationDescriptor
        {
            Id = "archive.extract", Title = "Extract", Category = FileKind.Archive, Group = OperationGroup.Tool, InputExtensions = new HashSet<string> { "zip", "tar", "gz" },
            ProducesFolder = true, Icon = "extract", Keywords = ["unzip", "decompress", "open", "untar", "suffix:"],
        };
        yield return new OperationDescriptor
        {
            Id = "archive.extract-rar", Title = "Extract", Category = FileKind.Archive, Group = OperationGroup.Tool, InputExtensions = new HashSet<string> { "rar", "7z" },
            ProducesFolder = true, Icon = "extract", RequiresTool = "7zip", Keywords = ["unrar", "rar", "7z", "decompress", "open", "suffix:"],
        };
        yield return new OperationDescriptor
        {
            Id = "archive.to.7z", Title = "7Z", Category = FileKind.Archive, Group = OperationGroup.Convert, InputExtensions = new HashSet<string> { "zip", "tar", "gz", "rar" },
            OutputExtension = "7z", RequiresTool = "7zip", Keywords = ["7z", "7-zip", "repack", "smaller archive"],
        };
        foreach (var (ext, title) in new[] { ("zip", "ZIP"), ("tar", "TAR") })
            yield return new OperationDescriptor
            {
                Id = $"archive.to.{ext}", Title = title, Category = FileKind.Archive, Group = OperationGroup.Convert, InputExtensions = new HashSet<string> { "zip", "tar", "gz" },
                OutputExtension = ext, Keywords = [ext, "repack", "archive"],
            };
    }
    public bool IsAvailable(OperationDescriptor op) => op.RequiresTool is null || _tools.Has(op.RequiresTool);

    public async Task<OperationResult> ExecuteAsync(OperationRequest r, IProgress<double>? progress, CancellationToken ct)
    {
        var input = r.Inputs[0];
        if (r.Operation.Id == "archive.extract-rar")
        {
            using var t = new SafePaths.TempWorkspace();
            var d = t.Dir("x");
            await SevenZipExtractAsync(_tools.Require("7zip"), input, d, ct, progress);
            var fin = SafePaths.PlanOutput(input, r.OutputDirectory, Path.GetFileNameWithoutExtension(input), null, "", r.Overwrite, r.Inputs, isFolder: true);
            return OperationResult.Ok(SafePaths.CommitFolder(d, fin));
        }
        var stem = Path.GetFileNameWithoutExtension(input);
        if (stem.EndsWith(".tar", StringComparison.OrdinalIgnoreCase)) stem = stem[..^4];
        using var tmp = new SafePaths.TempWorkspace();
        var dir = tmp.Dir("x");
        if (FileTypeDetector.Ext(input) == "rar") await SevenZipExtractAsync(_tools.Require("7zip"), input, dir, ct, progress);
        else await ExtractAsync(input, dir, ct, progress);
        if (r.Operation.Id == "archive.to.7z")
        {
            var o7 = tmp.File("7z");
            await SevenZipCreateAsync(_tools.Require("7zip"), Directory.EnumerateFileSystemEntries(dir).ToList(), o7, ct);
            var f7 = SafePaths.PlanOutput(input, r.OutputDirectory, stem, null, "7z", r.Overwrite, r.Inputs);
            return OperationResult.Ok(SafePaths.Commit(o7, f7, r.Overwrite));
        }
        if (r.Operation.Id == "archive.extract")
        {
            var final = SafePaths.PlanOutput(input, r.OutputDirectory, stem, null, "", r.Overwrite, r.Inputs, isFolder: true);
            return OperationResult.Ok(SafePaths.CommitFolder(dir, final));
        }
        var ext = r.Operation.OutputExtension!;
        var o = tmp.File(ext);
        if (ext == "zip") ZipFile.CreateFromDirectory(dir, o, CompressionLevel.Optimal, includeBaseDirectory: false);
        else await TarFile.CreateFromDirectoryAsync(dir, o, includeBaseDirectory: false, ct);
        var f = SafePaths.PlanOutput(input, r.OutputDirectory, stem, null, ext, r.Overwrite, r.Inputs);
        return OperationResult.Ok(SafePaths.Commit(o, f, r.Overwrite));
    }

    public static async Task ExtractAsync(string archive, string dest, CancellationToken ct, IProgress<double>? progress = null)
    {
        var limits = EngineLimits.Current;
        long written = 0; int entries = 0;
        void Guard(string entryName, long size)
        {
            if (++entries > limits.MaxArchiveEntries) throw new OrangeException("This archive has too many files to extract safely.");
            written += Math.Max(0, size);
            if (written > limits.MaxArchiveExpandedBytes) throw new OrangeException("This archive expands to more than the size limit. It may be a zip bomb.");
        }
        string Target(string entryName)
        {
            var clean = entryName.Replace('\\', '/').TrimStart('/');
            var raw = clean.Split('/', StringSplitOptions.RemoveEmptyEntries).Where(p => p != ".").ToArray();
            if (raw.Any(p => p == "..") || clean.Contains(':'))
                throw new OrangeException($"The archive tried to write outside its folder (\"{entryName}\"), so Orange stopped.");
            var parts = raw.Select(SafePaths.SanitizeFileName).ToArray();
            if (parts.Length == 0) throw new OrangeException("Bad entry");
            var path = Path.GetFullPath(Path.Combine(new[] { dest }.Concat(parts).ToArray()));
            if (!SafePaths.IsInside(dest, path)) throw new OrangeException($"The archive tried to write outside its folder (\"{entryName}\"), so Orange stopped.");
            return path;
        }

        var head = new byte[512];
        int n; using (var fs = File.OpenRead(archive)) n = fs.ReadAtLeast(head, 512, false);
        bool isZip = n >= 4 && head[0] == 0x50 && head[1] == 0x4B;
        bool isGz = n >= 2 && head[0] == 0x1F && head[1] == 0x8B;

        if (isZip)
        {
            using var zip = ZipFile.OpenRead(archive);
            int i = 0;
            foreach (var e in zip.Entries)
            {
                ct.ThrowIfCancellationRequested();
                if (e.FullName.EndsWith('/') || e.FullName.EndsWith('\\')) { if (e.FullName.Trim('/', '.', '\\').Length > 0) Directory.CreateDirectory(Target(e.FullName)); continue; }
                Guard(e.FullName, e.Length);
                if (e.CompressedLength > 0 && e.Length / Math.Max(1, e.CompressedLength) > 1000) throw new OrangeException("This archive has a suspicious compression ratio (possible zip bomb).");
                var t = Target(e.FullName);
                Directory.CreateDirectory(Path.GetDirectoryName(t)!);
                await using var src = e.Open();
                await using var dst = new FileStream(t, FileMode.CreateNew);
                await CopyCapped(src, dst, e.Length, ct);
                progress?.Report(++i / (double)zip.Entries.Count);
            }
            return;
        }

        Stream stream = File.OpenRead(archive);
        try
        {
            if (isGz) stream = new GZipStream(stream, CompressionMode.Decompress);
            // A .gz may be a gzipped tar or a single gzipped file. Peek for the ustar magic.
            var buffered = new BufferedStream(stream, 1 << 16);
            var peek = new byte[512];
            var got = buffered.ReadAtLeast(peek, 512, false);
            var rest = new ConcatStream(peek.AsMemory(0, got), buffered);
            bool isTar = got >= 262 && Encoding.ASCII.GetString(peek, 257, 5) == "ustar";
            if (!isTar)
            {
                if (!isGz) throw new OrangeException("This doesn't look like a ZIP, TAR or GZIP archive.");
                var name = Path.GetFileNameWithoutExtension(archive);
                var t = Target(name);
                await using var dst = new FileStream(t, FileMode.CreateNew);
                await CopyCapped(rest, dst, limits.MaxArchiveExpandedBytes, ct);
                return;
            }
            using var tar = new TarReader(rest);
            TarEntry? entry;
            while ((entry = await tar.GetNextEntryAsync(cancellationToken: ct)) != null)
            {
                switch (entry.EntryType)
                {
                    case TarEntryType.Directory: if (entry.Name.Trim('/', '.').Length > 0) Directory.CreateDirectory(Target(entry.Name)); break;
                    case TarEntryType.RegularFile: case TarEntryType.V7RegularFile: case TarEntryType.ContiguousFile:
                        Guard(entry.Name, entry.Length);
                        var t = Target(entry.Name);
                        Directory.CreateDirectory(Path.GetDirectoryName(t)!);
                        await using (var dst = new FileStream(t, FileMode.CreateNew))
                            if (entry.DataStream is { } ds) await CopyCapped(ds, dst, entry.Length, ct);
                        break;
                    default: break;   // symlinks, hardlinks, devices: skipped on purpose
                }
            }
        }
        catch (InvalidDataException e) { throw new OrangeException("This archive is damaged.", e.Message, e); }
        finally { await stream.DisposeAsync(); }
    }

    /// <summary>Lists with -slt, refuses absolute/../link entries and oversize totals, then extracts with -snl- (no links) into an empty folder.</summary>
    public static async Task SevenZipExtractAsync(string sevenZip, string archive, string dest, CancellationToken ct, IProgress<double>? progress = null)
    {
        var limits = EngineLimits.Current;
        var list = await ProcessRunner.RunAsync(sevenZip, ["l", "-slt", "-ba", "-p", "--", archive], ct, timeout: TimeSpan.FromMinutes(2));
        if (list.ExitCode != 0)
            throw new OrangeException(list.StdErr.Contains("password", StringComparison.OrdinalIgnoreCase) || list.StdOut.Contains("Wrong password", StringComparison.OrdinalIgnoreCase)
                ? "This archive is password-protected." : "7-Zip couldn't read this archive. It may be damaged.", ProcessRunner.Tail(list.StdErr));
        long total = 0; int entries = 0;
        foreach (var block in list.StdOut.Replace("\r", "").Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = block.Split('\n').Select(l => l.Split(" = ", 2)).Where(x => x.Length == 2).ToDictionary(x => x[0].Trim(), x => x[1], StringComparer.OrdinalIgnoreCase);
            if (!kv.TryGetValue("Path", out var path)) continue;
            var norm = path.Replace('\\', '/');
            if (norm.StartsWith('/') || norm.Contains(':') || norm.Split('/').Any(p => p == ".."))
                throw new OrangeException($"The archive tried to write outside its folder (\"{path}\"), so Orange stopped.");
            if (kv.ContainsKey("Symbolic Link") && kv["Symbolic Link"].Length > 0 || kv.ContainsKey("Hard Link") && kv["Hard Link"].Length > 0)
                throw new OrangeException("This archive contains links, which Orange doesn't extract.");
            if (kv.TryGetValue("Size", out var sz) && long.TryParse(sz, out var n)) total += n;
            if (++entries > limits.MaxArchiveEntries) throw new OrangeException("This archive has too many files to extract safely.");
            if (total > limits.MaxArchiveExpandedBytes) throw new OrangeException("This archive expands to more than the size limit. It may be a zip bomb.");
        }
        var res = await ProcessRunner.RunAsync(sevenZip, ["x", "-y", "-snl-", "-p", "-bso0", "-bsp0", "-o" + dest, "--", archive], ct, timeout: TimeSpan.FromHours(1));
        if (res.ExitCode != 0) throw new OrangeException("7-Zip couldn't extract this archive.", ProcessRunner.Tail(res.StdErr));
        // Belt and braces: nothing may have landed outside, and no reparse points survive.
        foreach (var f in Directory.EnumerateFileSystemEntries(dest, "*", SearchOption.AllDirectories))
        {
            var info = new FileInfo(f);
            if (!SafePaths.IsInside(dest, Path.GetFullPath(f)) || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            { try { File.Delete(f); } catch { } throw new OrangeException("This archive contained a link or an unsafe path, so Orange stopped."); }
        }
        progress?.Report(1);
    }

    /// <summary>"Compress to ZIP" for any files and folders (Actions wheel). Folders keep their names inside the zip.</summary>
    /// <summary>7z archive of files/folders (bundled 7-Zip, no shell, links not followed). Paths are stored relative to their parent.</summary>
    public static async Task SevenZipCreateAsync(string sevenZip, IReadOnlyList<string> paths, string output, CancellationToken ct)
    {
        if (paths.Count == 0) throw new OrangeException("Nothing to compress.");
        var args = new List<string> { "a", "-t7z", "-mx=7", "-snl-", "-bso0", "-bsp0", "-y", "--", output };
        args.AddRange(paths.Select(Path.GetFullPath));
        var res = await ProcessRunner.RunAsync(sevenZip, args, ct, timeout: TimeSpan.FromHours(2));
        if (res.ExitCode is not (0 or 1) || !File.Exists(output)) throw new OrangeException("7-Zip couldn't create the archive.", ProcessRunner.Tail(res.StdErr));
    }

    /// <summary>The Actions wheel's "7z": next to the first item, named after it (or "Archive" for several).</summary>
    public static async Task<string> Create7zFromSelectionAsync(IReadOnlyList<string> paths, CancellationToken ct)
    {
        var tool = ToolLocator.Default.Require("7zip");
        var full = paths.Select(Path.GetFullPath).ToList();
        var first = full[0].TrimEnd(Path.DirectorySeparatorChar);
        var dir = Path.GetDirectoryName(first)!;
        var name = full.Count == 1 ? Path.GetFileNameWithoutExtension(first) : "Archive";
        using var tmp = new SafePaths.TempWorkspace();
        var o = tmp.File("7z");
        await SevenZipCreateAsync(tool, full, o, ct);
        return SafePaths.Commit(o, SafePaths.PlanOutput(first, dir, name, null, "7z", OverwritePolicy.KeepBoth, full), OverwritePolicy.KeepBoth);
    }

    public static async Task<string> CreateZipAsync(IReadOnlyList<string> paths, string? outputDir, CancellationToken ct, IProgress<double>? progress = null)
    {
        if (paths.Count == 0) throw new OrangeException("Nothing to compress.");
        var full = paths.Select(Path.GetFullPath).ToList();
        foreach (var p in full) if (!File.Exists(p) && !Directory.Exists(p)) throw new OrangeException($"\"{Path.GetFileName(p)}\" no longer exists.");
        var files = new List<(string Src, string Entry)>();
        foreach (var p in full)
        {
            if (File.Exists(p)) { files.Add((p, Path.GetFileName(p))); continue; }
            var root = Path.GetDirectoryName(p.TrimEnd(Path.DirectorySeparatorChar))!;
            foreach (var f in Directory.EnumerateFiles(p, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System, IgnoreInaccessible = true }))
                files.Add((f, Path.GetRelativePath(root, f).Replace('\\', '/')));
        }
        using var tmp = new SafePaths.TempWorkspace();
        var o = tmp.File("zip");
        await using (var fs = new FileStream(o, FileMode.CreateNew))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            int i = 0;
            foreach (var (src, entry) in files)
            {
                ct.ThrowIfCancellationRequested();
                var e = zip.CreateEntry(entry, CompressionLevel.Optimal);
                e.LastWriteTime = File.GetLastWriteTime(src);
                await using var es = e.Open();
                await using var ss = File.OpenRead(src);
                await ss.CopyToAsync(es, ct);
                progress?.Report(++i / (double)Math.Max(1, files.Count));
            }
        }
        var first = full[0];
        var name = full.Count == 1 ? Path.GetFileName(first.TrimEnd(Path.DirectorySeparatorChar)) : $"{Path.GetFileNameWithoutExtension(first)} and {full.Count - 1} more";
        var final = SafePaths.PlanOutput(first, outputDir, name, null, "zip", OverwritePolicy.KeepBoth, full);
        return SafePaths.Commit(o, final, OverwritePolicy.KeepBoth);
    }

    private static async Task CopyCapped(Stream src, Stream dst, long declared, CancellationToken ct)
    {
        var buf = new byte[81920]; long total = 0; int read;
        long cap = Math.Min(EngineLimits.Current.MaxArchiveExpandedBytes, declared <= 0 ? long.MaxValue : declared + 1024);
        while ((read = await src.ReadAsync(buf, ct)) > 0)
        {
            total += read;
            if (total > cap) throw new OrangeException("An archive entry is larger than it claims. Orange stopped extracting.");
            await dst.WriteAsync(buf.AsMemory(0, read), ct);
        }
    }

    /// <summary>Replays already-read bytes before continuing with the rest of a stream.</summary>
    private sealed class ConcatStream(ReadOnlyMemory<byte> head, Stream tail) : Stream
    {
        private int _pos;
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_pos < head.Length)
            {
                int n = Math.Min(count, head.Length - _pos);
                head.Span.Slice(_pos, n).CopyTo(buffer.AsSpan(offset, n)); _pos += n; return n;
            }
            return tail.Read(buffer, offset, count);
        }
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
    }
}
