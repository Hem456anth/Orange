using System.Globalization;
using System.Text.Json;

namespace Orange.Core.Providers;

/// <summary>
/// Video and audio through FFmpeg as a separate process.
/// Hardening: every path is absolute and passed as "file:&lt;path&gt;" so a name like "concat:…" or "http:…" is never
/// read as a protocol; nested demuxers (HLS/concat playlists inside a hostile file) are limited to the file protocol;
/// no shell is involved; stdin is closed; runs at below-normal priority with a time limit.
/// </summary>
public sealed class MediaProvider(ToolLocator tools) : IOperationProvider
{
    public string Name => "Video & audio (FFmpeg)";
    private HashSet<string>? _encoders;

    private static readonly string[] VideoIn = ["mp4", "m4v", "mov", "mkv", "webm", "avi", "wmv"];
    private static readonly string[] AudioIn = ["mp3", "wav", "flac", "aac", "m4a", "ogg", "opus", "aiff", "wma"];

    private static OptionSpec Start => new("start", "Start (mm:ss)", OptionType.TimeSpan, "", Help: "Leave empty to start at the beginning.");
    private static OptionSpec End => new("end", "End (mm:ss)", OptionType.TimeSpan, "", Help: "Leave empty to go to the end.");

    public IEnumerable<OperationDescriptor> Describe()
    {
        // ---- video conversions
        foreach (var (ext, title) in new[] { ("mp4", "MP4"), ("mov", "MOV"), ("mkv", "MKV"), ("webm", "WebM"), ("avi", "AVI"), ("gif", "GIF") })
            yield return new OperationDescriptor
            {
                Id = $"video.to.{ext}", Title = title, Category = FileKind.Video, Group = OperationGroup.Convert, InputExtensions = VideoIn.ToHashSet(),
                OutputExtension = ext, RequiresTool = "ffmpeg", Keywords = [ext, "convert", "video", "transcode"],
                Options = ext == "gif"
                    ? [new("fps", "Frames per second", OptionType.Integer, "12", 1, 30), new("width", "Width (px)", OptionType.Integer, "480", 64, 1920)]
                    : [new("quality", "Quality", OptionType.Choice, "high", Choices: ["high", "balanced", "small"])],
            };
        foreach (var (ext, title) in new[] { ("mp3", "MP3"), ("m4a", "M4A"), ("wav", "WAV") })
            yield return new OperationDescriptor
            {
                Id = $"video.to.{ext}", Title = title, Category = FileKind.Video, Group = OperationGroup.Convert, InputExtensions = VideoIn.ToHashSet(),
                OutputExtension = ext, RequiresTool = "ffmpeg", Keywords = [ext, "extract audio", "audio", "soundtrack"],
            };

        // ---- video tools
        yield return VTool("video.compress", "Compress", "Compressed", "compress",
            [new("level", "Compression", OptionType.Choice, "medium", Choices: ["light", "medium", "strong"]),
             new("maxHeight", "Max height", OptionType.Choice, "keep", Choices: ["keep", "2160", "1080", "720", "480"])], ["smaller", "reduce", "size"]);
        yield return VTool("video.trim", "Trim", "Trimmed", "trim", [Start, End], ["cut", "clip", "shorten"], single: true);
        yield return VTool("video.mute", "Mute", "Muted", "mute", [], ["remove audio", "silent", "no sound"]);
        yield return VTool("video.resize", "Resize", "Resized", "resize",
            [new("height", "Height", OptionType.Choice, "720", Choices: ["2160", "1440", "1080", "720", "480", "360"])], ["scale", "resolution", "720p", "1080p"]);
        yield return VTool("video.crop", "Crop", "Cropped", "crop",
            [new("aspect", "Aspect", OptionType.Choice, "1:1", Choices: ["1:1", "4:3", "16:9", "9:16", "4:5"])], ["square", "vertical", "aspect"], single: true);
        yield return VTool("video.speed", "Speed", "Speed", "speed",
            [new("factor", "Speed ×", OptionType.Number, "2", 0.25, 4)], ["fast", "slow", "timelapse", "slow motion"], single: true);
        yield return VTool("video.snapshot", "Snapshot", "Snapshot", "snapshot",
            [new("at", "Time (mm:ss)", OptionType.TimeSpan, "0")], ["frame", "screenshot", "thumbnail", "still"], single: true, outExt: "png");
        yield return VTool("video.split", "Split", "Clips", "split",
            [new("every", "Clip length (seconds)", OptionType.Number, "60", 1, 36000)], ["segments", "chunks", "parts"], single: true, folder: true);
        yield return VTool("video.strip-metadata", "Remove metadata", "Clean", "metadata", [], ["privacy", "gps", "location", "exif"]);
        yield return new OperationDescriptor
        {
            Id = "video.join", Title = "Join videos", Category = FileKind.Video, Group = OperationGroup.Tool, InputExtensions = VideoIn.ToHashSet(),
            OutputExtension = "mp4", CombinesInputs = true, MinInputs = 2, RequiresTool = "ffmpeg", Icon = "join", Keywords = ["merge", "combine", "concatenate", "suffix:Joined"],
        };

        // ---- audio conversions
        foreach (var (ext, title) in new[] { ("mp3", "MP3"), ("wav", "WAV"), ("flac", "FLAC"), ("aac", "AAC"), ("m4a", "M4A"), ("ogg", "OGG"), ("opus", "Opus"), ("aiff", "AIFF"), ("wma", "WMA") })
            yield return new OperationDescriptor
            {
                Id = $"audio.to.{ext}", Title = title, Category = FileKind.Audio, Group = OperationGroup.Convert, InputExtensions = AudioIn.ToHashSet(),
                OutputExtension = ext, RequiresTool = "ffmpeg", Keywords = [ext, "convert", "audio", "music"],
                Options = ext is "wav" or "flac" or "aiff" ? [] : [new("bitrate", "Bitrate (kbps)", OptionType.Choice, ext == "opus" ? "128" : "192", Choices: ["96", "128", "160", "192", "256", "320"])],
            };

        // ---- audio tools
        yield return ATool("audio.trim", "Trim", "Trimmed", "trim", [Start, End], ["cut", "clip", "shorten", "ringtone"], single: true);
        yield return ATool("audio.normalize", "Normalize", "Normalized", "normalize", [], ["volume", "loudness", "louder", "level"]);
        yield return ATool("audio.channels", "Channels", "Mono", "channels",
            [new("channels", "Channels", OptionType.Choice, "mono", Choices: ["mono", "stereo"])], ["mono", "stereo", "downmix"]);
        yield return ATool("audio.compress", "Compress", "Compressed", "compress",
            [new("bitrate", "Bitrate (kbps)", OptionType.Choice, "96", Choices: ["64", "96", "128"])], ["smaller", "reduce", "size"]);
        yield return ATool("audio.strip-metadata", "Remove metadata", "Clean", "metadata", [], ["tags", "id3", "privacy"]);
        yield return new OperationDescriptor
        {
            Id = "audio.merge", Title = "Merge audio", Category = FileKind.Audio, Group = OperationGroup.Tool, InputExtensions = AudioIn.ToHashSet(),
            OutputExtension = null, CombinesInputs = true, MinInputs = 2, RequiresTool = "ffmpeg", Icon = "join", Keywords = ["join", "combine", "concatenate", "suffix:Merged"],
        };
    }

    private static OperationDescriptor VTool(string id, string title, string suffix, string icon, OptionSpec[] opts, string[] kw, bool single = false, string? outExt = null, bool folder = false) => new()
    {
        Id = id, Title = title, Category = FileKind.Video, Group = OperationGroup.Tool, InputExtensions = VideoIn.ToHashSet(), OutputExtension = outExt,
        Options = opts, RequiresTool = "ffmpeg", Icon = icon, ProducesFolder = folder,
        Keywords = kw.Append("suffix:" + suffix).Concat(single ? ["single-only"] : Array.Empty<string>()).ToArray(),
    };
    private static OperationDescriptor ATool(string id, string title, string suffix, string icon, OptionSpec[] opts, string[] kw, bool single = false) => new()
    {
        Id = id, Title = title, Category = FileKind.Audio, Group = OperationGroup.Tool, InputExtensions = AudioIn.ToHashSet(), Options = opts,
        RequiresTool = "ffmpeg", Icon = icon, Keywords = kw.Append("suffix:" + suffix).Concat(single ? ["single-only"] : Array.Empty<string>()).ToArray(),
    };

    public bool IsAvailable(OperationDescriptor op)
    {
        if (!tools.Has("ffmpeg") || !tools.Has("ffprobe")) return false;
        var x = op.OutputExtension;
        return x switch
        {
            "webm" => Has("libvpx-vp9") || Has("libvpx"),
            "ogg" => Has("libvorbis") || Has("vorbis"),
            "opus" => Has("libopus") || Has("opus"),
            "mp3" => Has("libmp3lame") || Has("mp3_mf"),
            "wma" => Has("wmav2"),
            _ => true,
        };
    }

    private readonly object _encoderLock = new();

    private bool Has(string encoder)
    {
        // Built into a local set and published only when complete: concurrent callers must never
        // observe a half-filled (or empty) set while the first probe is still reading ffmpeg's output.
        var set = Volatile.Read(ref _encoders);
        if (set is null)
        {
            lock (_encoderLock)
            {
                set = _encoders;
                if (set is null)
                {
                    set = new HashSet<string>();
                    try
                    {
                        var o = ProcessRunner.RunAsync(tools.Require("ffmpeg"), ["-hide_banner", "-encoders"], CancellationToken.None, timeout: TimeSpan.FromSeconds(20)).GetAwaiter().GetResult();
                        foreach (var line in o.StdOut.Split('\n'))
                        {
                            var p = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                            if (p.Length >= 2 && p[0].Length == 6 && (p[0][0] is 'V' or 'A' or 'S')) set.Add(p[1]);
                        }
                    }
                    catch { /* treated as nothing available */ }
                    Volatile.Write(ref _encoders, set);
                }
            }
        }
        return set.Contains(encoder);
    }

    private string H264() => Has("libx264") ? "libx264" : Has("h264_mf") ? "h264_mf" : Has("libopenh264") ? "libopenh264" : "mpeg4";
    private string Mp3() => Has("libmp3lame") ? "libmp3lame" : "mp3_mf";

    public sealed record ProbeInfo(double DurationSeconds, int Width, int Height, bool HasVideo, bool HasAudio);

    public async Task<ProbeInfo> ProbeAsync(string path, CancellationToken ct)
    {
        var o = await ProcessRunner.RunAsync(tools.Require("ffprobe"),
            ["-v", "error", "-protocol_whitelist", "file", "-print_format", "json", "-show_format", "-show_streams", "file:" + path], ct, timeout: TimeSpan.FromMinutes(1));
        if (o.ExitCode != 0) throw new OrangeException($"\"{Path.GetFileName(path)}\" couldn't be read. It may be damaged or use an unsupported codec.", ProcessRunner.Tail(o.StdErr));
        using var doc = JsonDocument.Parse(o.StdOut);
        double dur = 0; int w = 0, h = 0; bool v = false, a = false;
        if (doc.RootElement.TryGetProperty("format", out var f) && f.TryGetProperty("duration", out var d))
            double.TryParse(d.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out dur);
        if (doc.RootElement.TryGetProperty("streams", out var ss))
            foreach (var s in ss.EnumerateArray())
            {
                var type = s.GetProperty("codec_type").GetString();
                bool attached = s.TryGetProperty("disposition", out var disp) && disp.TryGetProperty("attached_pic", out var ap) && ap.GetInt32() == 1;
                if (type == "video" && !attached && !v) { v = true; w = s.TryGetProperty("width", out var ww) ? ww.GetInt32() : 0; h = s.TryGetProperty("height", out var hh) ? hh.GetInt32() : 0; }
                if (type == "audio") a = true;
            }
        return new ProbeInfo(dur, w, h, v, a);
    }

    public async Task<OperationResult> ExecuteAsync(OperationRequest r, IProgress<double>? progress, CancellationToken ct)
    {
        var op = r.Operation;
        var input = r.Inputs[0];
        var stem = Path.GetFileNameWithoutExtension(input);
        var srcExt = OperationRegistry.Normalize(FileTypeDetector.Ext(input));
        using var tmp = new SafePaths.TempWorkspace();
        var infos = new List<ProbeInfo>();
        foreach (var i in r.Inputs) infos.Add(await ProbeAsync(i, ct));
        var info = infos[0];
        if (op.Category == FileKind.Video && !info.HasVideo && op.Id != "video.to.mp3" && op.Id != "video.to.m4a" && op.Id != "video.to.wav")
            throw new OrangeException($"\"{Path.GetFileName(input)}\" has no video track.");
        if (op.Category == FileKind.Audio && !info.HasAudio || op.Id.StartsWith("video.to.") && op.OutputExtension is "mp3" or "m4a" or "wav" && !info.HasAudio)
            throw new OrangeException($"\"{Path.GetFileName(input)}\" has no audio track.");

        string outExt = op.OutputExtension ?? srcExt;
        string? suffix = op.Group == OperationGroup.Tool ? ImageProvider.SuffixOf(op) : null;
        var a = new List<string> { "-hide_banner", "-nostdin", "-y", "-protocol_whitelist", "file,pipe", "-progress", "pipe:1", "-nostats", "-loglevel", "error" };
        double expected = info.DurationSeconds;

        void In(string path, TimeSpan? ss = null, TimeSpan? to = null)
        {
            if (ss is { } s) a.AddRange(["-ss", Sec(s)]);
            if (to is { } t) a.AddRange(["-to", Sec(t)]);
            a.AddRange(["-i", "file:" + path]);
        }

        string outFile = tmp.File(outExt);
        string? outDir = null;

        switch (op.Id)
        {
            case var id when id.StartsWith("video.to."):
                In(input);
                VideoTarget(a, outExt, r, info);
                break;
            case var id when id.StartsWith("audio.to."):
                In(input);
                a.Add("-vn");
                AudioTarget(a, outExt, r.Options.ContainsKey("bitrate") || op.Options.Any(o => o.Name == "bitrate") ? r.OptInt("bitrate") : 0);
                a.AddRange(["-map_metadata", "0"]);
                break;
            case "video.compress":
            {
                In(input);
                outExt = "mp4"; outFile = tmp.File(outExt);
                int crf = r.Opt("level") switch { "light" => 23, "strong" => 32, _ => 28 };
                var vf = r.Opt("maxHeight") is var mh && mh != "keep" && info.Height > int.Parse(mh) ? $"scale=-2:{mh}" : null;
                if (vf != null) a.AddRange(["-vf", vf]);
                H264Args(a, crf);
                a.AddRange(["-c:a", "aac", "-b:a", r.Opt("level") == "strong" ? "96k" : "128k", "-movflags", "+faststart"]);
                break;
            }
            case "video.trim":
            case "audio.trim":
            {
                var s = r.OptTime("start"); var e = r.OptTime("end");
                if (s is null && e is null) throw new OrangeException("Set a start or end time to trim.");
                if (s is { } ss && e is { } ee && ee <= ss) throw new OrangeException("The end time must be after the start time.");
                if (s is { } s2 && s2.TotalSeconds >= info.DurationSeconds && info.DurationSeconds > 0) throw new OrangeException($"The start time is past the end ({Sec(TimeSpan.FromSeconds(info.DurationSeconds))} s).");
                In(input, s, e);
                expected = ((e?.TotalSeconds ?? info.DurationSeconds) - (s?.TotalSeconds ?? 0));
                if (op.Category == FileKind.Video) { H264Args(a, 20); a.AddRange(["-c:a", "aac", "-b:a", "192k"]); outExt = srcExt is "mp4" or "mov" or "mkv" ? srcExt : "mp4"; outFile = tmp.File(outExt); }
                else AudioTarget(a, outExt, 192, keepQuality: true);
                break;
            }
            case "video.mute":
                In(input); a.AddRange(["-c:v", "copy", "-an"]); break;
            case "video.resize":
                In(input);
                a.AddRange(["-vf", $"scale=-2:{int.Parse(r.Opt("height"))}"]); H264Args(a, 22); a.AddRange(["-c:a", "copy"]);
                outExt = "mp4"; outFile = tmp.File(outExt); break;
            case "video.crop":
            {
                In(input);
                var p = r.Opt("aspect").Split(':').Select(double.Parse).ToArray();
                double target = p[0] / p[1];
                int w = info.Width, h = info.Height;
                if ((double)w / h > target) w = (int)(h * target) / 2 * 2; else h = (int)(w / target) / 2 * 2;
                a.AddRange(["-vf", $"crop={w}:{h}"]); H264Args(a, 20); a.AddRange(["-c:a", "copy"]);
                outExt = "mp4"; outFile = tmp.File(outExt); break;
            }
            case "video.speed":
            {
                var f = r.OptDouble("factor");
                In(input);
                a.AddRange(["-filter:v", $"setpts=PTS/{Inv.F(f)}"]);
                if (info.HasAudio) a.AddRange(["-filter:a", Atempo(f)]); else a.Add("-an");
                H264Args(a, 20); if (info.HasAudio) a.AddRange(["-c:a", "aac"]);
                expected = info.DurationSeconds / f; outExt = "mp4"; outFile = tmp.File(outExt); break;
            }
            case "video.snapshot":
            {
                var at = r.OptTime("at") ?? TimeSpan.Zero;
                if (info.DurationSeconds > 0 && at.TotalSeconds > info.DurationSeconds) throw new OrangeException("That time is past the end of the video.");
                In(input, at); a.AddRange(["-frames:v", "1", "-update", "1"]); expected = 0; break;
            }
            case "video.split":
            {
                In(input);
                var every = r.OptDouble("every");
                if (info.DurationSeconds / every > 500) throw new OrangeException("That would make more than 500 clips. Use a longer clip length.");
                outDir = tmp.Dir("clips");
                var ext = srcExt is "mp4" or "mov" or "mkv" ? srcExt : "mp4";
                H264Args(a, 20); a.AddRange(["-c:a", "aac", "-f", "segment", "-segment_time", Inv.F(every), "-reset_timestamps", "1",
                    "-force_key_frames", $"expr:gte(t,n_forced*{Inv.F(every)})"]);
                outFile = Path.Combine(outDir, $"{SafePaths.SanitizeFileName(stem).Replace("%", "")} %03d.{ext}");
                break;
            }
            case "video.strip-metadata":
            case "audio.strip-metadata":
                In(input); a.AddRange(["-map", "0", "-map_metadata", "-1", "-map_chapters", "-1", "-c", "copy"]);
                if (srcExt is "mp4" or "mov" or "m4a") a.AddRange(["-movflags", "+faststart"]);
                break;
            case "audio.normalize":
                In(input); a.AddRange(["-vn", "-af", "loudnorm=I=-16:TP=-1.5:LRA=11"]); AudioTarget(a, outExt, 192, keepQuality: true); break;
            case "audio.channels":
                In(input); a.AddRange(["-vn", "-ac", r.Opt("channels") == "mono" ? "1" : "2"]); AudioTarget(a, outExt, 192, keepQuality: true);
                suffix = r.Opt("channels") == "mono" ? "Mono" : "Stereo"; break;
            case "audio.compress":
                In(input); a.Add("-vn");
                outExt = srcExt is "wav" or "aiff" ? "flac" : srcExt is "flac" ? "flac" : srcExt;   // lossless sources compress losslessly (observed: WAV → FLAC)
                outFile = tmp.File(outExt);
                AudioTarget(a, outExt, r.OptInt("bitrate"));
                break;
            case "audio.merge":
            {
                foreach (var i in r.Inputs) In(i);
                var filter = string.Concat(Enumerable.Range(0, r.Inputs.Count).Select(i => $"[{i}:a:0]")) + $"concat=n={r.Inputs.Count}:v=0:a=1[a]";
                a.AddRange(["-filter_complex", filter, "-map", "[a]"]);
                AudioTarget(a, outExt, 192, keepQuality: true);
                expected = infos.Sum(x => x.DurationSeconds);
                stem = $"{stem} and {r.Inputs.Count - 1} more";
                break;
            }
            case "video.join":
            {
                foreach (var i in r.Inputs) In(i);
                int W = infos[0].Width / 2 * 2, H = infos[0].Height / 2 * 2;
                bool audio = infos.All(x => x.HasAudio);
                var parts = new List<string>();
                for (int i = 0; i < r.Inputs.Count; i++)
                {
                    parts.Add($"[{i}:v:0]scale={W}:{H}:force_original_aspect_ratio=decrease,pad={W}:{H}:(ow-iw)/2:(oh-ih)/2,setsar=1,fps=30,format=yuv420p[v{i}]");
                    if (audio) parts.Add($"[{i}:a:0]aformat=sample_rates=48000:channel_layouts=stereo[a{i}]");
                }
                var chain = string.Concat(Enumerable.Range(0, r.Inputs.Count).Select(i => audio ? $"[v{i}][a{i}]" : $"[v{i}]"));
                parts.Add($"{chain}concat=n={r.Inputs.Count}:v=1:a={(audio ? 1 : 0)}[v]" + (audio ? "[a]" : ""));
                a.AddRange(["-filter_complex", string.Join(";", parts), "-map", "[v]"]);
                if (audio) a.AddRange(["-map", "[a]", "-c:a", "aac", "-b:a", "192k"]);
                H264Args(a, 20); a.AddRange(["-movflags", "+faststart"]);
                expected = infos.Sum(x => x.DurationSeconds);
                stem = $"{stem} and {r.Inputs.Count - 1} more";
                break;
            }
            default: throw new OrangeException($"Unsupported media operation {op.Id}.");
        }

        a.Add(outDir != null ? outFile : "file:" + outFile);
        var res = await ProcessRunner.RunAsync(tools.Require("ffmpeg"), a, ct, onStdOutLine: line =>
        {
            if (expected <= 0 || progress is null) return;
            if (line.StartsWith("out_time_us=") && long.TryParse(line.AsSpan(12), out var us) && us > 0)
                progress.Report(Math.Min(0.99, us / 1e6 / expected));
        });
        if (res.ExitCode != 0)
            throw new OrangeException(FriendlyError(res.StdErr, input), ProcessRunner.Tail(res.StdErr));

        if (outDir != null)
        {
            var finalDir = SafePaths.PlanOutput(input, r.OutputDirectory, stem, suffix, "", r.Overwrite, r.Inputs, isFolder: true);
            return OperationResult.Ok(SafePaths.CommitFolder(outDir, finalDir));
        }
        if (outExt != "png") await ValidateMediaAsync(outFile, ct);
        else ImageProvider.ValidateOutput(outFile);
        var final = SafePaths.PlanOutput(input, r.OutputDirectory, stem, suffix, outExt, r.Overwrite, r.Inputs);
        progress?.Report(1);
        return OperationResult.Ok(SafePaths.Commit(outFile, final, r.Overwrite));
    }

    private void VideoTarget(List<string> a, string ext, OperationRequest r, ProbeInfo info)
    {
        int crf = r.Operation.Options.Any(o => o.Name == "quality") ? r.Opt("quality") switch { "balanced" => 26, "small" => 30, _ => 20 } : 20;
        switch (ext)
        {
            case "mp4": case "mov": H264Args(a, crf); a.AddRange(["-c:a", "aac", "-b:a", "192k", "-movflags", "+faststart"]); break;
            case "mkv": H264Args(a, crf); a.AddRange(["-c:a", "aac", "-b:a", "192k"]); break;
            case "avi": H264Args(a, crf); a.AddRange(["-c:a", Mp3(), "-b:a", "192k"]); break;
            case "webm":
                if (Has("libvpx-vp9")) a.AddRange(["-c:v", "libvpx-vp9", "-crf", (crf + 12).ToString(), "-b:v", "0", "-row-mt", "1", "-deadline", "good", "-cpu-used", "4"]);
                else a.AddRange(["-c:v", "libvpx", "-crf", "10", "-b:v", "2M"]);
                a.AddRange(["-c:a", Has("libopus") ? "libopus" : "libvorbis", "-b:a", "128k"]); break;
            case "gif":
                var fps = r.OptInt("fps"); var w = Math.Min(r.OptInt("width"), info.Width > 0 ? info.Width : 4096);
                a.AddRange(["-vf", $"fps={fps},scale={w}:-1:flags=lanczos,split[s0][s1];[s0]palettegen=max_colors=192[p];[s1][p]paletteuse=dither=bayer", "-loop", "0", "-an"]);
                if (info.DurationSeconds > 120) throw new OrangeException("GIFs are limited to 2 minutes. Trim the video first.");
                break;
            case "mp3": a.AddRange(["-vn", "-c:a", Mp3(), "-b:a", "192k"]); break;
            case "m4a": a.AddRange(["-vn", "-c:a", "aac", "-b:a", "192k"]); break;
            case "wav": a.AddRange(["-vn", "-c:a", "pcm_s16le"]); break;
        }
    }

    private void H264Args(List<string> a, int crf)
    {
        var enc = H264();
        a.AddRange(["-c:v", enc]);
        if (enc == "libx264") a.AddRange(["-preset", "medium", "-crf", crf.ToString()]);
        else if (enc == "h264_mf") a.AddRange(["-rate_control", "quality", "-quality", Math.Clamp(100 - crf * 2, 1, 100).ToString()]);
        else if (enc == "mpeg4") a.AddRange(["-q:v", Math.Clamp(crf / 6, 2, 31).ToString()]);
        a.AddRange(["-pix_fmt", "yuv420p"]);
    }

    private void AudioTarget(List<string> a, string ext, int kbps, bool keepQuality = false)
    {
        if (kbps <= 0) kbps = 192;
        string B() => kbps + "k";
        switch (ext)
        {
            case "mp3": a.AddRange(["-c:a", Mp3(), "-b:a", B()]); break;
            case "wav": a.AddRange(["-c:a", "pcm_s16le"]); break;
            case "aiff": a.AddRange(["-c:a", "pcm_s16be"]); break;
            case "flac": a.AddRange(["-c:a", "flac", "-compression_level", "8"]); break;
            case "aac": a.AddRange(["-c:a", "aac", "-b:a", B(), "-f", "adts"]); break;
            case "m4a": a.AddRange(["-c:a", "aac", "-b:a", B(), "-movflags", "+faststart"]); break;
            case "ogg": if (Has("libvorbis")) a.AddRange(["-c:a", "libvorbis", "-b:a", B()]); else a.AddRange(["-c:a", "vorbis", "-strict", "-2", "-ac", "2"]); break;
            case "opus": if (Has("libopus")) a.AddRange(["-c:a", "libopus", "-b:a", B()]); else a.AddRange(["-c:a", "opus", "-strict", "-2", "-ar", "48000"]); break;
            case "wma": a.AddRange(["-c:a", "wmav2", "-b:a", B()]); break;
            default: throw new OrangeException($"Orange can't write .{ext} audio.");
        }
        a.Add("-vn");
    }

    private static string Atempo(double f)
    {
        var parts = new List<string>();
        while (f > 2.0) { parts.Add("atempo=2.0"); f /= 2.0; }
        while (f < 0.5) { parts.Add("atempo=0.5"); f /= 0.5; }
        parts.Add($"atempo={Inv.F(f)}");
        return string.Join(",", parts);
    }

    private async Task ValidateMediaAsync(string path, CancellationToken ct)
    {
        var p = await ProbeAsync(path, ct);
        if (!p.HasAudio && !p.HasVideo) throw new OrangeException("The converted file failed its check, so it wasn't saved.", "No audio or video streams in output.");
    }

    private static string Sec(TimeSpan t) => t.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);

    private static string FriendlyError(string stderr, string input)
    {
        var s = stderr.ToLowerInvariant();
        var n = Path.GetFileName(input);
        if (s.Contains("invalid data found")) return $"\"{n}\" looks damaged or isn't a media file FFmpeg can read.";
        if (s.Contains("no space left")) return "The disk is full. Free some space and try again.";
        if (s.Contains("permission denied")) return "Orange isn't allowed to write to that folder. Pick another destination.";
        if (s.Contains("unknown encoder") || s.Contains("encoder not found")) return "This FFmpeg build is missing the encoder for that format.";
        if (s.Contains("height not divisible") || s.Contains("width not divisible")) return "The video size isn't supported by the encoder.";
        return $"FFmpeg couldn't process \"{n}\".";
    }
}
