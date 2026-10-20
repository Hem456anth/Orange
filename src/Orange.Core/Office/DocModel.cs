using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Orange.Core.Office;

/// <summary>A run of text with simple styling.</summary>
public sealed record Run(string Text, bool Bold = false, bool Italic = false);

public abstract record Block;
public sealed record Heading(int Level, string Text) : Block;
public sealed record Para(IReadOnlyList<Run> Runs) : Block
{
    public string Text => string.Concat(Runs.Select(r => r.Text));
    public static Para Of(string text) => new([new Run(text)]);
}
public sealed record ListItem(IReadOnlyList<Run> Runs, bool Ordered, int Level = 0) : Block
{
    public string Text => string.Concat(Runs.Select(r => r.Text));
}
public sealed record Table(IReadOnlyList<IReadOnlyList<string>> Rows) : Block;
public sealed record Picture(byte[] Data, string Extension, double WidthPt = 0, double HeightPt = 0) : Block;
public sealed record PageBreak : Block;

/// <summary>
/// The neutral document every native Office conversion goes through: readers build it, writers render it.
/// It keeps structure (headings, paragraphs with bold/italic, lists, tables, pictures, page breaks), not layout.
/// </summary>
public sealed class DocModel
{
    public string Title { get; set; } = "";
    public List<Block> Blocks { get; } = new();
    public int MaxBlocks { get; init; } = 500_000;

    public void Add(Block b)
    {
        if (Blocks.Count >= MaxBlocks) throw new OrangeException("The document is too large to convert.");
        Blocks.Add(b);
    }
}

/// <summary>Limits for Office containers (all are ZIPs): refuse bombs before any XML is parsed.</summary>
public static class OfficeGuard
{
    public const long MaxExpanded = 2L * 1024 * 1024 * 1024;
    public const int MaxEntries = 20_000;

    public static void CheckZip(string path)
    {
        try
        {
            using var z = ZipFile.OpenRead(path);
            if (z.Entries.Count > MaxEntries) throw new OrangeException("The file has too many parts to open safely.");
            long total = 0;
            foreach (var e in z.Entries)
            {
                total += e.Length;
                if (e.CompressedLength > 0 && e.Length > 10_000_000 && e.Length / e.CompressedLength > 1000)
                    throw new OrangeException("The file looks like a compression bomb and was refused.");
            }
            if (total > MaxExpanded) throw new OrangeException("The file expands to more than 2 GB and was refused.");
        }
        catch (InvalidDataException) { throw new OrangeException("The file looks damaged (its ZIP container can't be read)."); }
    }
}

/// <summary>Plain text, Markdown and HTML readers and writers for <see cref="DocModel"/>.</summary>
public static class TextFormats
{
    // ---------------- writers

    public static string ToText(DocModel d)
    {
        var sb = new StringBuilder();
        foreach (var b in d.Blocks)
        {
            switch (b)
            {
                case Heading h: sb.AppendLine(h.Text).AppendLine(); break;
                case Para p: sb.AppendLine(p.Text).AppendLine(); break;
                case ListItem li: sb.Append(new string(' ', li.Level * 2)).Append(li.Ordered ? "1. " : "• ").AppendLine(li.Text); break;
                case Table t: foreach (var r in t.Rows) sb.AppendLine(string.Join('\t', r)); sb.AppendLine(); break;
                case PageBreak: sb.AppendLine().AppendLine(); break;
            }
        }
        return sb.ToString().TrimEnd() + Environment.NewLine;
    }

    public static string ToMarkdown(DocModel d, Func<Picture, string?>? savePicture = null)
    {
        var sb = new StringBuilder();
        Block? prev = null;
        foreach (var b in d.Blocks)
        {
            if (prev is ListItem && b is not ListItem) sb.AppendLine();
            switch (b)
            {
                case Heading h: sb.Append(new string('#', Math.Clamp(h.Level, 1, 6))).Append(' ').AppendLine(EscMd(h.Text)).AppendLine(); break;
                case Para p: sb.AppendLine(MdRuns(p.Runs)).AppendLine(); break;
                case ListItem li: sb.Append(new string(' ', li.Level * 2)).Append(li.Ordered ? "1. " : "- ").AppendLine(MdRuns(li.Runs)); break;
                case Table t when t.Rows.Count > 0:
                    int cols = t.Rows.Max(r => r.Count);
                    string Row(IReadOnlyList<string> r) => "| " + string.Join(" | ", Enumerable.Range(0, cols).Select(i => i < r.Count ? EscMd(r[i]).Replace("|", "\\|").Replace("\n", " ") : "")) + " |";
                    sb.AppendLine(Row(t.Rows[0]));
                    sb.AppendLine("|" + string.Concat(Enumerable.Repeat("---|", cols)));
                    foreach (var r in t.Rows.Skip(1)) sb.AppendLine(Row(r));
                    sb.AppendLine();
                    break;
                case Picture pic when savePicture?.Invoke(pic) is { } name: sb.AppendLine($"![]({name})").AppendLine(); break;
                case PageBreak: sb.AppendLine("---").AppendLine(); break;
            }
            prev = b;
        }
        return sb.ToString().TrimEnd() + Environment.NewLine;
    }

    private static string MdRuns(IEnumerable<Run> runs) => string.Concat(runs.Select(r =>
    {
        var t = EscMd(r.Text);
        if (t.Trim().Length == 0) return t;
        if (r.Bold && r.Italic) return $"***{t}***";
        if (r.Bold) return $"**{t}**";
        if (r.Italic) return $"*{t}*";
        return t;
    }));

    private static string EscMd(string s) => Regex.Replace(s, @"([\\`*_\[\]<>])", @"\$1");

    public static string ToHtml(DocModel d, bool embedPictures = true)
    {
        var sb = new StringBuilder("<!DOCTYPE html>\n<html><head><meta charset=\"utf-8\"><title>")
            .Append(WebUtility.HtmlEncode(d.Title)).Append("</title>\n<style>body{font-family:Segoe UI,Arial,sans-serif;max-width:50em;margin:2em auto;line-height:1.5}table{border-collapse:collapse}td,th{border:1px solid #ccc;padding:4px 8px}img{max-width:100%}</style></head><body>\n");
        string? openList = null;
        foreach (var b in d.Blocks)
        {
            var want = b is ListItem l ? (l.Ordered ? "ol" : "ul") : null;
            if (openList != null && openList != want) { sb.Append("</").Append(openList).AppendLine(">"); openList = null; }
            if (want != null && openList == null) { sb.Append('<').Append(want).AppendLine(">"); openList = want; }
            switch (b)
            {
                case Heading h: var n = Math.Clamp(h.Level, 1, 6); sb.AppendLine($"<h{n}>{WebUtility.HtmlEncode(h.Text)}</h{n}>"); break;
                case Para p: sb.Append("<p>").Append(HtmlRuns(p.Runs)).AppendLine("</p>"); break;
                case ListItem li: sb.Append("<li>").Append(HtmlRuns(li.Runs)).AppendLine("</li>"); break;
                case Table t:
                    sb.AppendLine("<table>");
                    for (int i = 0; i < t.Rows.Count; i++)
                        sb.Append("<tr>").Append(string.Concat(t.Rows[i].Select(c => (i == 0 ? "<th>" : "<td>") + WebUtility.HtmlEncode(c) + (i == 0 ? "</th>" : "</td>")))).AppendLine("</tr>");
                    sb.AppendLine("</table>");
                    break;
                case Picture pic when embedPictures:
                    sb.AppendLine($"<p><img alt=\"\" src=\"data:image/{(pic.Extension == "jpg" ? "jpeg" : pic.Extension)};base64,{Convert.ToBase64String(pic.Data)}\"></p>");
                    break;
                case PageBreak: sb.AppendLine("<hr>"); break;
            }
        }
        if (openList != null) sb.Append("</").Append(openList).AppendLine(">");
        return sb.Append("</body></html>\n").ToString();
    }

    private static string HtmlRuns(IEnumerable<Run> runs) => string.Concat(runs.Select(r =>
    {
        var t = WebUtility.HtmlEncode(r.Text);
        if (r.Bold) t = $"<strong>{t}</strong>";
        if (r.Italic) t = $"<em>{t}</em>";
        return t;
    }));

    // ---------------- readers

    public static DocModel FromText(string text, string title)
    {
        var d = new DocModel { Title = title };
        foreach (var para in text.Replace("\r\n", "\n").Split("\n\n"))
            if (para.Trim().Length > 0) d.Add(Para.Of(para.Trim('\n')));
        return d;
    }

    private static readonly Regex MdInline = new(@"(\*\*\*(.+?)\*\*\*|\*\*(.+?)\*\*|__(.+?)__|\*(.+?)\*|_(.+?)_)", RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    public static List<Run> ParseMdInline(string s)
    {
        var runs = new List<Run>(); int at = 0;
        s = Regex.Replace(s, @"!\[[^\]]*\]\([^)]*\)", "", RegexOptions.None, TimeSpan.FromSeconds(1));
        s = Regex.Replace(s, @"\[([^\]]+)\]\([^)]*\)", "$1", RegexOptions.None, TimeSpan.FromSeconds(1));
        foreach (Match m in MdInline.Matches(s))
        {
            if (m.Index > at) runs.Add(new Run(Unesc(s[at..m.Index])));
            if (m.Groups[2].Success) runs.Add(new Run(Unesc(m.Groups[2].Value), true, true));
            else if (m.Groups[3].Success || m.Groups[4].Success) runs.Add(new Run(Unesc(m.Groups[3].Success ? m.Groups[3].Value : m.Groups[4].Value), Bold: true));
            else runs.Add(new Run(Unesc(m.Groups[5].Success ? m.Groups[5].Value : m.Groups[6].Value), Italic: true));
            at = m.Index + m.Length;
        }
        if (at < s.Length) runs.Add(new Run(Unesc(s[at..])));
        return runs;
        static string Unesc(string x) => Regex.Replace(x, @"\\([\\`*_\[\]<>|#-])", "$1").Replace("`", "");
    }

    public static DocModel FromMarkdown(string md, string title)
    {
        var d = new DocModel { Title = title };
        var lines = md.Replace("\r\n", "\n").Split('\n');
        var para = new StringBuilder();
        void Flush() { if (para.Length > 0) { d.Add(new Para(ParseMdInline(para.ToString().Trim()))); para.Clear(); } }
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var t = line.Trim();
            if (t.Length == 0) { Flush(); continue; }
            var h = Regex.Match(t, @"^(#{1,6})\s+(.*?)\s*#*$");
            if (h.Success) { Flush(); d.Add(new Heading(h.Groups[1].Length, string.Concat(ParseMdInline(h.Groups[2].Value).Select(r => r.Text)))); continue; }
            if (Regex.IsMatch(t, @"^(-{3,}|\*{3,}|_{3,})$")) { Flush(); d.Add(new PageBreak()); continue; }
            var li = Regex.Match(line, @"^(\s*)([-*+]|\d+[.)])\s+(.*)$");
            if (li.Success) { Flush(); d.Add(new ListItem(ParseMdInline(li.Groups[3].Value), char.IsDigit(li.Groups[2].Value[0]), li.Groups[1].Value.Length / 2)); continue; }
            if (t.StartsWith('|') && i + 1 < lines.Length && Regex.IsMatch(lines[i + 1].Trim(), @"^\|?\s*:?-{2,}"))
            {
                Flush();
                var rows = new List<IReadOnlyList<string>>();
                for (; i < lines.Length && lines[i].Trim().StartsWith('|'); i++)
                {
                    if (Regex.IsMatch(lines[i].Trim(), @"^\|[\s:|-]+\|?$")) continue;
                    rows.Add(lines[i].Trim().Trim('|').Split('|').Select(c => string.Concat(ParseMdInline(c.Trim()).Select(r => r.Text))).ToList());
                }
                i--;
                d.Add(new Table(rows));
                continue;
            }
            if (t.StartsWith("```")) { Flush(); var code = new StringBuilder(); for (i++; i < lines.Length && !lines[i].Trim().StartsWith("```"); i++) code.AppendLine(lines[i]); d.Add(Para.Of(code.ToString().TrimEnd())); continue; }
            para.Append(t.TrimStart('>', ' ')).Append(' ');
        }
        Flush();
        return d;
    }

    /// <summary>Minimal, script-free HTML reader: headings, paragraphs, lists, tables, bold/italic. No network, no CSS.</summary>
    public static DocModel FromHtml(string html, string title)
    {
        var d = new DocModel { Title = title };
        html = Regex.Replace(html, @"<(script|style|head|template|noscript)[\s\S]*?</\1\s*>", "", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2));
        html = Regex.Replace(html, @"<!--[\s\S]*?-->", "", RegexOptions.None, TimeSpan.FromSeconds(2));
        var tokens = Regex.Split(html, @"(<[^>]+>)", RegexOptions.None, TimeSpan.FromSeconds(2));
        var runs = new List<Run>(); bool bold = false, italic = false; int? heading = null; bool? listOrdered = null; var listStack = new Stack<bool>();
        List<List<string>>? table = null; List<string>? row = null; StringBuilder? cell = null;
        void Flush()
        {
            var text = string.Concat(runs.Select(r => r.Text));
            if (text.Trim().Length > 0)
            {
                var clean = runs.Select(r => r with { Text = Regex.Replace(r.Text, @"\s+", " ") }).ToList();
                if (heading is { } lv) d.Add(new Heading(lv, Regex.Replace(text, @"\s+", " ").Trim()));
                else if (listOrdered is { } o) d.Add(new ListItem(clean, o, Math.Max(0, listStack.Count - 1)));
                else d.Add(new Para(clean));
            }
            runs.Clear();
        }
        foreach (var tok in tokens)
        {
            if (tok.Length == 0) continue;
            if (tok[0] == '<')
            {
                var m = Regex.Match(tok, @"^<\s*(/?)\s*([a-zA-Z0-9]+)");
                if (!m.Success) continue;
                bool close = m.Groups[1].Value == "/"; var tag = m.Groups[2].Value.ToLowerInvariant();
                switch (tag)
                {
                    case "b" or "strong": bold = !close; break;
                    case "i" or "em": italic = !close; break;
                    case "h1" or "h2" or "h3" or "h4" or "h5" or "h6": Flush(); heading = close ? null : tag[1] - '0'; break;
                    case "p" or "div" or "section" or "article" or "blockquote": Flush(); break;
                    case "br": if (cell != null) cell.Append(' '); else runs.Add(new Run("\n")); break;
                    case "ul" or "ol": Flush(); if (close) { if (listStack.Count > 0) listStack.Pop(); listOrdered = listStack.Count > 0 ? listStack.Peek() : null; } else { listStack.Push(tag == "ol"); listOrdered = tag == "ol"; } break;
                    case "li": Flush(); break;
                    case "hr": Flush(); d.Add(new PageBreak()); break;
                    case "table": if (close && table != null) { d.Add(new Table(table.Select(r => (IReadOnlyList<string>)r).ToList())); table = null; } else if (!close) { Flush(); table = new(); } break;
                    case "tr": if (table != null) { if (close && row != null) { table.Add(row); row = null; } else if (!close) row = new(); } break;
                    case "td" or "th": if (row != null) { if (close && cell != null) { row.Add(Regex.Replace(cell.ToString(), @"\s+", " ").Trim()); cell = null; } else if (!close) cell = new(); } break;
                }
            }
            else
            {
                var text = WebUtility.HtmlDecode(tok);
                if (cell != null) cell.Append(text);
                else if (table == null) runs.Add(new Run(text, bold, italic));
            }
        }
        Flush();
        return d;
    }
}
