using System.Globalization;
using System.Text.Json;
using ImageMagick;
using Orange.Core.Providers;

namespace Orange.Core;

public sealed record MetadataEntry(string Group, string Key, string Value, bool Sensitive);

/// <summary>
/// Reads what a file carries about itself, for the Metadata panel's preview. Sensitive entries (location, serial
/// numbers, owner names, software that names a device) are flagged so the panel can show what a removal will clear.
/// Values are display-only data and are truncated; nothing here is ever executed or followed.
/// </summary>
public static class MetadataReader
{
    private static readonly string[] SensitiveHints = ["gps", "location", "serial", "owner", "author", "artist", "creator", "make", "model", "lens", "software", "encoder", "comment", "producer"];

    public static async Task<IReadOnlyList<MetadataEntry>> ReadAsync(string path, CancellationToken ct = default)
    {
        path = SafePaths.ValidateInput(path);
        var kind = FileTypeDetector.Detect(path);
        var list = new List<MetadataEntry>();
        switch (kind)
        {
            case FileKind.Image:
            {
                using var col = ImageProvider.Load(path);
                var img = col[0];
                list.Add(new("File", "Dimensions", $"{img.Width} × {img.Height}", false));
                list.Add(new("File", "Frames", col.Count.ToString(CultureInfo.InvariantCulture), false));
                if (img.GetExifProfile() is { } exif)
                    foreach (var v in exif.Values)
                    {
                        var val = v.GetValue();
                        var text = val switch { Array a => string.Join(", ", a.Cast<object>().Take(16)), null => "", _ => val.ToString() ?? "" };
                        if (v.Tag.ToString() is "MakerNote" or "UserComment" && text.Length > 64) text = $"({text.Length} bytes)";
                        list.Add(new(v.Tag.ToString().StartsWith("GPS") ? "Location" : "EXIF", v.Tag.ToString(), Clip(text), IsSensitive(v.Tag.ToString())));
                    }
                if (img.GetIptcProfile() is { } iptc) foreach (var v in iptc.Values) list.Add(new("IPTC", v.Tag.ToString(), Clip(v.Value), IsSensitive(v.Tag.ToString())));
                if (img.GetXmpProfile() is { } xmp) list.Add(new("XMP", "XMP packet", $"{xmp.ToByteArray().Length} bytes", true));
                if (img.GetColorProfile() is { } icc) list.Add(new("Colour", "ICC profile", icc.Description ?? icc.Model ?? "embedded", false));
                break;
            }
            case FileKind.Video:
            case FileKind.Audio:
            {
                var ffprobe = ToolLocator.Default.Require("ffprobe");
                var o = await ProcessRunner.RunAsync(ffprobe, ["-v", "error", "-protocol_whitelist", "file", "-print_format", "json", "-show_format", "-show_streams", "-show_chapters", "file:" + path], ct, timeout: TimeSpan.FromMinutes(1));
                if (o.ExitCode != 0) throw new OrangeException($"\"{Path.GetFileName(path)}\" couldn't be read.", ProcessRunner.Tail(o.StdErr));
                using var doc = JsonDocument.Parse(o.StdOut);
                if (doc.RootElement.TryGetProperty("format", out var f))
                {
                    if (f.TryGetProperty("format_long_name", out var fn)) list.Add(new("File", "Container", fn.GetString() ?? "", false));
                    if (f.TryGetProperty("duration", out var d) && double.TryParse(d.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var secs))
                        list.Add(new("File", "Duration", TimeSpan.FromSeconds(secs).ToString(@"h\:mm\:ss\.f", CultureInfo.InvariantCulture), false));
                    if (f.TryGetProperty("bit_rate", out var br) && long.TryParse(br.GetString(), out var bps)) list.Add(new("File", "Bitrate", $"{bps / 1000} kbps", false));
                    if (f.TryGetProperty("tags", out var tags)) foreach (var t in tags.EnumerateObject()) list.Add(new("Tags", t.Name, Clip(t.Value.ToString()), IsSensitive(t.Name)));
                }
                if (doc.RootElement.TryGetProperty("streams", out var ss))
                    foreach (var s in ss.EnumerateArray())
                    {
                        var type = s.TryGetProperty("codec_type", out var ty) ? ty.GetString() : "stream";
                        var codec = s.TryGetProperty("codec_name", out var cn) ? cn.GetString() : "";
                        var desc = type == "video" && s.TryGetProperty("width", out var w) && s.TryGetProperty("height", out var h) ? $"{codec} {w.GetInt32()}×{h.GetInt32()}"
                                 : type == "audio" && s.TryGetProperty("channels", out var ch) ? $"{codec}, {ch.GetInt32()} ch" + (s.TryGetProperty("sample_rate", out var sr) ? $", {sr.GetString()} Hz" : "") : codec ?? "";
                        list.Add(new("Streams", $"{type} #{s.GetProperty("index").GetInt32()}", desc, false));
                    }
                if (doc.RootElement.TryGetProperty("chapters", out var chs) && chs.GetArrayLength() > 0) list.Add(new("File", "Chapters", chs.GetArrayLength().ToString(CultureInfo.InvariantCulture), false));
                break;
            }
            case FileKind.Pdf:
            {
                using var pdf = PdfProvider.Open(path, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import);
                list.Add(new("File", "Pages", pdf.PageCount.ToString(CultureInfo.InvariantCulture), false));
                foreach (var key in pdf.Info.Elements.Keys) list.Add(new("Document", key.TrimStart('/'), Clip(pdf.Info.Elements[key]?.ToString()?.Trim('(', ')') ?? ""), IsSensitive(key)));
                if (pdf.Internals.Catalog.Elements.ContainsKey("/Metadata")) list.Add(new("XMP", "XMP packet", "present", true));
                break;
            }
            default:
                list.Add(new("File", "Size", $"{new FileInfo(path).Length:N0} bytes", false));
                break;
        }
        return list;
    }

    private static bool IsSensitive(string key) { var k = key.ToLowerInvariant(); return SensitiveHints.Any(k.Contains); }
    private static string Clip(string s) { s = new string(s.Where(c => !char.IsControl(c)).ToArray()); return s.Length > 300 ? s[..300] + "…" : s; }
}
