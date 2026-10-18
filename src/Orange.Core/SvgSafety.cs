using System.Text;
using System.Text.RegularExpressions;

namespace Orange.Core;

/// <summary>
/// SVG is XML that can point at other files and URLs. Orange only renders SVGs that are self-contained:
/// no DOCTYPE/ENTITY (XXE, billion laughs), no external href/src/url() other than in-document "#id" or
/// embedded "data:image/…", and no XInclude. Anything else is refused before ImageMagick sees it.
/// </summary>
public static partial class SvgSafety
{
    public const long MaxBytes = 20L * 1024 * 1024;

    public static bool LooksLikeSvg(string path)
    {
        if (!path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)) return false;
        var head = new byte[4096];
        int n; using (var fs = File.OpenRead(path)) n = fs.ReadAtLeast(head, head.Length, false);
        var text = Encoding.UTF8.GetString(head, 0, n).TrimStart('\uFEFF', ' ', '\r', '\n', '\t');
        return text.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase) && text.Contains("<svg", StringComparison.OrdinalIgnoreCase)
               || text.StartsWith("<svg", StringComparison.OrdinalIgnoreCase)
               || text.StartsWith("<!--") && text.Contains("<svg", StringComparison.OrdinalIgnoreCase);
    }

    public static void EnsureSafe(string path)
    {
        var fi = new FileInfo(path);
        if (fi.Length > MaxBytes) throw new OrangeException("This SVG is too large to open safely.");
        var text = File.ReadAllText(path);
        string? why = null;
        if (text.Contains("<!DOCTYPE", StringComparison.OrdinalIgnoreCase) || text.Contains("<!ENTITY", StringComparison.OrdinalIgnoreCase)) why = "it declares a DOCTYPE or entities";
        else if (text.Contains("xi:include", StringComparison.OrdinalIgnoreCase) || text.Contains("XInclude", StringComparison.OrdinalIgnoreCase)) why = "it includes other files";
        else if (text.Contains("<script", StringComparison.OrdinalIgnoreCase) || text.Contains("<foreignObject", StringComparison.OrdinalIgnoreCase)) why = "it contains scripts or embedded HTML";
        else
        {
            foreach (Match m in HrefRx().Matches(text))
            {
                var v = m.Groups["v"].Value.Trim();
                if (v.StartsWith('#') || v.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase)) continue;
                why = "it links to another file or address"; break;
            }
            if (why == null)
                foreach (Match m in UrlRx().Matches(text))
                {
                    var v = m.Groups["v"].Value.Trim().Trim('\'', '"');
                    if (v.StartsWith('#') || v.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase)) continue;
                    why = "it links to another file or address"; break;
                }
            if (why == null && ImportRx().IsMatch(text)) why = "its styles import another file";
        }
        if (why != null) throw new OrangeException($"Orange won't open \"{Path.GetFileName(path)}\" because {why}.", "Only self-contained SVGs are rendered, so a file can't make Orange read other files or reach the network.");
    }

    [GeneratedRegex(@"(?:href|src)\s*=\s*([""'])(?<v>.*?)\1", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex HrefRx();
    [GeneratedRegex(@"url\(\s*(?<v>[^)]*)\)", RegexOptions.IgnoreCase)]
    private static partial Regex UrlRx();
    [GeneratedRegex(@"@import", RegexOptions.IgnoreCase)]
    private static partial Regex ImportRx();
}
