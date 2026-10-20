using System.Globalization;
using System.Text;

namespace Orange.Core.Office;

/// <summary>
/// Rich Text Format, written and read by Orange itself from Microsoft's published RTF 1.9.1 specification (clean
/// room). The reader is a small tokenizer: groups, control words, \'hh and \uN escapes, skipped destinations,
/// paragraphs, bold/italic, \outlinelevel headings, tables (\intbl/\cell/\row), PNG/JPEG pictures and page breaks.
/// </summary>
public static class RtfIO
{
    // ---------------------------------------------------------------- write

    public static void Write(DocModel d, string path)
    {
        var sb = new StringBuilder();
        sb.Append(@"{\rtf1\ansi\ansicpg1252\deff0\uc1{\fonttbl{\f0\fswiss Calibri;}{\f1\fmodern Consolas;}}");
        sb.Append(@"{\*\generator Orange;}\viewkind4\paperw11906\paperh16838\margl1134\margr1134\margt1134\margb1134");
        if (!string.IsNullOrEmpty(d.Title)) sb.Append(@"{\info{\title ").Append(Esc(d.Title)).Append("}}");
        sb.Append('\n');
        foreach (var b in d.Blocks)
        {
            switch (b)
            {
                case Heading h:
                    int lvl = Math.Clamp(h.Level, 1, 6);
                    sb.Append(@"\pard\plain\f0\sb240\sa120\keepn\outlinelevel").Append(lvl - 1).Append(@"\b\fs").Append(Math.Max(24, 44 - lvl * 4)).Append(' ')
                      .Append(Esc(h.Text)).Append(@"\b0\par").Append('\n');
                    break;
                case Para p:
                    sb.Append(@"\pard\plain\f0\fs22\sa160 "); Runs(sb, p.Runs); sb.Append(@"\par").Append('\n');
                    break;
                case ListItem li:
                    int indent = 360 * (li.Level + 1);
                    sb.Append(@"\pard\plain\f0\fs22\sa60\fi-360\li").Append(indent).Append(' ')
                      .Append(li.Ordered ? "" : @"\u8226?").Append(li.Ordered ? "" : @"\tab ");
                    Runs(sb, li.Runs); sb.Append(@"\par").Append('\n');
                    break;
                case Table t when t.Rows.Count > 0:
                {
                    int cols = Math.Max(1, t.Rows.Max(r => r.Count));
                    int width = 9638 / cols;   // text width of A4 with 2 cm margins, in twips
                    foreach (var row in t.Rows)
                    {
                        sb.Append(@"\trowd\trgaph108");
                        for (int c = 1; c <= cols; c++) sb.Append(@"\clbrdrt\brdrs\clbrdrl\brdrs\clbrdrb\brdrs\clbrdrr\brdrs\cellx").Append(width * c);
                        sb.Append('\n');
                        for (int c = 0; c < cols; c++) sb.Append(@"\pard\intbl\f0\fs20 ").Append(Esc(c < row.Count ? row[c] : "")).Append(@"\cell ");
                        sb.Append(@"\row").Append('\n');
                    }
                    sb.Append(@"\pard").Append('\n');
                    break;
                }
                case Picture pic:
                {
                    var ext = pic.Extension.TrimStart('.').ToLowerInvariant();
                    byte[] data = pic.Data; string blip;
                    if (ext is "jpg" or "jpeg") blip = @"\jpegblip";
                    else if (ext == "png") blip = @"\pngblip";
                    else { try { using var img = new ImageMagick.MagickImage(pic.Data); data = img.ToByteArray(ImageMagick.MagickFormat.Png); blip = @"\pngblip"; } catch { break; } }
                    int wpx = 400, hpx = 300;
                    try { using var info = new ImageMagick.MagickImage(data); wpx = (int)info.Width; hpx = (int)info.Height; } catch { }
                    double scale = Math.Min(1, 9638.0 / (wpx * 15));
                    sb.Append(@"\pard\plain\sa160{\pict").Append(blip).Append(@"\picw").Append(wpx).Append(@"\pich").Append(hpx)
                      .Append(@"\picwgoal").Append((int)(wpx * 15 * scale)).Append(@"\pichgoal").Append((int)(hpx * 15 * scale)).Append('\n');
                    for (int i = 0; i < data.Length; i++) { sb.Append(data[i].ToString("x2")); if (i % 64 == 63) sb.Append('\n'); }
                    sb.Append(@"}\par").Append('\n');
                    break;
                }
                case PageBreak:
                    sb.Append(@"\pard\page").Append('\n');
                    break;
            }
        }
        sb.Append('}');
        File.WriteAllText(path, sb.ToString(), Encoding.ASCII);
    }

    private static void Runs(StringBuilder sb, IReadOnlyList<Run> runs)
    {
        foreach (var r in runs)
        {
            if (r.Bold || r.Italic) sb.Append('{').Append(r.Bold ? @"\b" : "").Append(r.Italic ? @"\i" : "").Append(' ').Append(Esc(r.Text)).Append('}');
            else sb.Append(Esc(r.Text));
        }
    }

    /// <summary>RTF is 7-bit: specials escaped, everything else as \uN? (N signed 16-bit).</summary>
    public static string Esc(string s)
    {
        var sb = new StringBuilder(s.Length + 16);
        foreach (var c in s)
        {
            if (c is '\\' or '{' or '}') sb.Append('\\').Append(c);
            else if (c == '\n') sb.Append(@"\line ");
            else if (c == '\t') sb.Append(@"\tab ");
            else if (c == '\r') { }
            else if (c < 0x80 && c >= 0x20) sb.Append(c);
            else if (c >= 0x80) sb.Append(@"\u").Append(((short)c).ToString(CultureInfo.InvariantCulture)).Append('?');
        }
        return sb.ToString();
    }

    // ---------------------------------------------------------------- read

    private sealed class State
    {
        public bool Bold, Italic, Skip, InTable;
        public int Uc = 1;
        public int Outline = -1;
        public string? PictKind;
        public State Clone() => (State)MemberwiseClone();
    }

    private static readonly HashSet<string> SkipDestinations =
    [
        "fonttbl", "colortbl", "stylesheet", "info", "header", "footer", "headerl", "headerr", "headerf", "footerl", "footerr", "footerf",
        "footnote", "annotation", "fldinst", "xmlnstbl", "listtable", "listoverridetable", "rsidtbl", "generator", "themedata",
        "colorschememapping", "latentstyles", "datastore", "mmathPr", "pgdsctbl", "filetbl", "revtbl", "object", "objdata", "nonshppict",
    ];

    public static DocModel Read(string path)
    {
        if (new FileInfo(path).Length > 300_000_000) throw new OrangeException("This RTF file is larger than 300 MB.");
        var src = File.ReadAllText(path, Encoding.Latin1);
        if (!src.TrimStart().StartsWith(@"{\rtf", StringComparison.Ordinal)) throw new OrangeException("This isn't an RTF file.");
        var model = new DocModel { Title = Path.GetFileNameWithoutExtension(path) };
        Encoding ansi = Encoding.Latin1;
        try { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); ansi = Encoding.GetEncoding(1252); } catch { }

        var stack = new Stack<State>();
        var st = new State();
        var runs = new List<Run>();
        var text = new StringBuilder();
        var bytes = new List<byte>();            // \'hh bytes waiting to be decoded together (multi-byte code pages)
        var row = new List<string>(); var rows = new List<IReadOnlyList<string>>();
        var pict = new StringBuilder();
        int skipChars = 0;
        int depth = 0;
        bool expectDestination = false;

        void FlushBytes() { if (bytes.Count > 0) { text.Append(ansi.GetString(bytes.ToArray())); bytes.Clear(); } }
        void FlushRun()
        {
            FlushBytes();
            if (text.Length > 0) { runs.Add(new Run(text.ToString(), st.Bold, st.Italic)); text.Clear(); }
        }
        string RunsText() => string.Concat(runs.Select(r => r.Text));
        void EndParagraph()
        {
            FlushRun();
            if (st.InTable) return;   // table cell text collects until \cell
            if (rows.Count > 0) { model.Add(new Table(rows.ToList())); rows.Clear(); }
            var t = RunsText();
            if (t.Trim().Length > 0)
            {
                if (st.Outline >= 0) model.Add(new Heading(Math.Clamp(st.Outline + 1, 1, 6), t.Trim()));
                else if (t.StartsWith('•')) model.Add(new ListItem(Trim(runs, 1), false));
                else model.Add(new Para(runs.ToList()));
            }
            runs.Clear();
        }

        for (int i = 0; i < src.Length; i++)
        {
            char c = src[i];
            if (c == '{')
            {
                FlushRun();
                stack.Push(st); st = st.Clone(); depth++;
                if (stack.Count > 500) throw new OrangeException("This RTF file nests too deeply.");
                expectDestination = true;
                continue;
            }
            if (c == '}')
            {
                FlushRun();
                if (st.PictKind is { } kind && !stack.Peek().Skip && pict.Length > 0)
                {
                    try
                    {
                        var hex = new string(pict.ToString().Where(Uri.IsHexDigit).ToArray());
                        if (hex.Length % 2 == 1) hex = hex[..^1];
                        model.Add(new Picture(Convert.FromHexString(hex), kind));
                    }
                    catch { }
                    pict.Clear();
                }
                if (stack.Count == 0) break;
                st = stack.Pop(); depth--;
                expectDestination = false;
                continue;
            }
            if (c == '\\' && i + 1 < src.Length)
            {
                char n = src[i + 1];
                if (n is '\\' or '{' or '}') { if (!st.Skip) Emit(n); i++; expectDestination = false; continue; }
                if (n == '\'' && i + 3 < src.Length)
                {
                    if (skipChars > 0) { skipChars--; i += 3; continue; }
                    if (!st.Skip && byte.TryParse(src.AsSpan(i + 2, 2), NumberStyles.HexNumber, null, out var bv)) bytes.Add(bv);
                    i += 3; expectDestination = false; continue;
                }
                if (n == '*') { st.Skip = true; i++; continue; }   // unknown "ignorable" destination
                if (n == '~') { if (!st.Skip) Emit('\u00A0'); i++; continue; }
                if (n is '-' or '_') { if (n == '_' && !st.Skip) Emit('-'); i++; continue; }
                if (n is '\n' or '\r') { if (!st.Skip) EndParagraph(); i++; continue; }
                // control word
                int j = i + 1;
                while (j < src.Length && char.IsAsciiLetter(src[j])) j++;
                var word = src[(i + 1)..j];
                int k = j; bool neg = false;
                if (k < src.Length && src[k] == '-') { neg = true; k++; }
                int ds = k; while (k < src.Length && char.IsAsciiDigit(src[k]) && k - ds < 10) k++;
                int? param = k > ds ? int.Parse(src.AsSpan(ds, k - ds), CultureInfo.InvariantCulture) * (neg ? -1 : 1) : null;
                if (k < src.Length && src[k] == ' ') k++;
                i = k - 1;
                bool wasDest = expectDestination; expectDestination = false;
                if (word.Length == 0) continue;
                if (wasDest && SkipDestinations.Contains(word)) { st.Skip = true; continue; }
                if (word == "pict") { st.PictKind = "png"; st.Skip = false; continue; }
                if (st.PictKind is not null)
                {
                    if (word == "pngblip") st.PictKind = "png";
                    else if (word == "jpegblip") st.PictKind = "jpg";
                    else if (word is "wmetafile" or "emfblip" or "macpict" or "dibitmap" or "wbitmap") st.PictKind = null;   // not supported: drop
                    if (st.PictKind is null) st.Skip = true;
                    continue;
                }
                if (st.Skip) continue;
                switch (word)
                {
                    case "par": EndParagraph(); break;
                    case "line": Emit('\n'); break;
                    case "tab": Emit('\t'); break;
                    case "page": EndParagraph(); model.Add(new PageBreak()); break;
                    case "b": FlushRun(); st.Bold = param != 0; break;
                    case "i": FlushRun(); st.Italic = param != 0; break;
                    case "plain": FlushRun(); st.Bold = st.Italic = false; break;
                    case "pard": FlushRun(); st.Outline = -1; st.InTable = false; break;
                    case "outlinelevel": st.Outline = param ?? -1; break;
                    case "intbl": st.InTable = true; break;
                    case "cell": FlushRun(); row.Add(RunsText().Trim()); runs.Clear(); break;
                    case "row": FlushRun(); if (row.Count > 0) rows.Add(row.ToList()); row.Clear(); runs.Clear(); st.InTable = false; break;
                    case "uc": st.Uc = Math.Clamp(param ?? 1, 0, 10); break;
                    case "u":
                        if (param is { } u) { Emit((char)(u < 0 ? u + 65536 : u)); skipChars = st.Uc; }
                        break;
                    case "emdash": Emit('—'); break;
                    case "endash": Emit('–'); break;
                    case "bullet": Emit('•'); break;
                    case "lquote": Emit('‘'); break;
                    case "rquote": Emit('’'); break;
                    case "ldblquote": Emit('“'); break;
                    case "rdblquote": Emit('”'); break;
                    case "sect": EndParagraph(); break;
                }
                continue;
            }
            if (c is '\r' or '\n') continue;
            if (st.PictKind is not null) { pict.Append(c); continue; }
            if (st.Skip) continue;
            if (skipChars > 0) { skipChars--; continue; }
            expectDestination = false;
            Emit(c);
        }
        EndParagraph();
        if (rows.Count > 0) model.Add(new Table(rows.ToList()));
        return model;

        void Emit(char ch)
        {
            if (skipChars > 0) { skipChars--; return; }
            FlushBytes();
            text.Append(ch);
        }
    }

    private static List<Run> Trim(List<Run> runs, int chars)
    {
        var o = runs.ToList();
        if (o.Count > 0) o[0] = o[0] with { Text = o[0].Text[Math.Min(chars, o[0].Text.Length)..].TrimStart('\t', ' ') };
        return o;
    }
}
