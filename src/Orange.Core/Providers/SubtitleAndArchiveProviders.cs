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
/// RAR is not supported (no permissively licensed decoder is bundled).
/// </summary>
public sealed class ArchiveProvider : IOperationProvider
{
    public string Name => "Archives";
    public IEnumerable<OperationDescriptor> Describe()
    {
        yield return new OperationDescriptor
        {
            Id = "archive.extract", Title = "Extract", Category = FileKind.Archive, Group = OperationGroup.Tool, InputExtensions = new HashSet<string> { "zip", "tar", "gz" },
            ProducesFolder = true, Icon = "extract", Keywords = ["unzip", "decompress", "open", "untar", "suffix:"],
        };
        foreach (var (ext, title) in new[] { ("zip", "ZIP"), ("tar", "TAR") })
            yield return new OperationDescriptor
            {
                Id = $"archive.to.{ext}", Title = title, Category = FileKind.Archive, Group = OperationGroup.Convert, InputExtensions = new HashSet<string> { "zip", "tar", "gz" },
                OutputExtension = ext, Keywords = [ext, "repack", "archive"],
            };
    }
    public bool IsAvailable(OperationDescriptor op) => true;

    public async Task<OperationResult> ExecuteAsync(OperationRequest r, IProgress<double>? progress, CancellationToken ct)
    {
        var input = r.Inputs[0];
        var stem = Path.GetFileNameWithoutExtension(input);
        if (stem.EndsWith(".tar", StringComparison.OrdinalIgnoreCase)) stem = stem[..^4];
        using var tmp = new SafePaths.TempWorkspace();
        var dir = tmp.Dir("x");
        await ExtractAsync(input, dir, ct, progress);
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
