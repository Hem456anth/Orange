using System.IO.Compression;
using System.Security;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Orange.Core.Office;

/// <summary>
/// OpenDocument Text (.odt), written and read by Orange itself from the OASIS ODF 1.3 specification (clean room:
/// no LibreOffice or Pandoc code). Headings, paragraphs with bold/italic, bulleted and numbered lists, tables,
/// pictures and page breaks — the same structure <see cref="DocModel"/> carries for DOCX.
/// </summary>
public static class OdtIO
{
    private static readonly XNamespace Office = "urn:oasis:names:tc:opendocument:xmlns:office:1.0";
    private static readonly XNamespace Text = "urn:oasis:names:tc:opendocument:xmlns:text:1.0";
    private static readonly XNamespace Table = "urn:oasis:names:tc:opendocument:xmlns:table:1.0";
    private static readonly XNamespace Style = "urn:oasis:names:tc:opendocument:xmlns:style:1.0";
    private static readonly XNamespace Fo = "urn:oasis:names:tc:opendocument:xmlns:xsl-fo-compatible:1.0";
    private static readonly XNamespace Draw = "urn:oasis:names:tc:opendocument:xmlns:drawing:1.0";
    private static readonly XNamespace XLink = "http://www.w3.org/1999/xlink";
    private static readonly XNamespace Svg = "urn:oasis:names:tc:opendocument:xmlns:svg-compatible:1.0";
    private static readonly XNamespace Manifest = "urn:oasis:names:tc:opendocument:xmlns:manifest:1.0";
    private static readonly XNamespace Meta = "urn:oasis:names:tc:opendocument:xmlns:meta:1.0";
    private static readonly XNamespace Dc = "http://purl.org/dc/elements/1.1/";

    // ---------------------------------------------------------------- write

    public static void Write(DocModel d, string path)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        // The spec requires "mimetype" first and uncompressed so tools can sniff the type.
        var mt = zip.CreateEntry("mimetype", CompressionLevel.NoCompression);
        using (var s = mt.Open()) s.Write(Encoding.ASCII.GetBytes("application/vnd.oasis.opendocument.text"));

        var pictures = new List<(string Name, byte[] Data, string Mime)>();
        var body = new XElement(Office + "text");
        XElement? list = null; bool listOrdered = false;
        int tableNo = 0, frameNo = 0;
        foreach (var b in d.Blocks)
        {
            if (b is not ListItem && list is not null) { body.Add(list); list = null; }
            switch (b)
            {
                case Heading h:
                    body.Add(new XElement(Text + "h", new XAttribute(Text + "style-name", $"Heading_20_{Math.Clamp(h.Level, 1, 6)}"),
                        new XAttribute(Text + "outline-level", Math.Clamp(h.Level, 1, 6)), Spaced(h.Text)));
                    break;
                case Para p:
                    body.Add(new XElement(Text + "p", new XAttribute(Text + "style-name", "Standard"), Runs(p.Runs)));
                    break;
                case ListItem li:
                    if (list is null || listOrdered != li.Ordered)
                    {
                        if (list is not null) body.Add(list);
                        list = new XElement(Text + "list", new XAttribute(Text + "style-name", li.Ordered ? "LNum" : "LBullet"));
                        listOrdered = li.Ordered;
                    }
                    XElement item = new(Text + "list-item", new XElement(Text + "p", new XAttribute(Text + "style-name", "Standard"), Runs(li.Runs)));
                    for (int i = 0; i < li.Level; i++) item = new XElement(Text + "list-item", new XElement(Text + "list", item));
                    list.Add(item);
                    break;
                case Table t when t.Rows.Count > 0:
                {
                    int cols = t.Rows.Max(r => r.Count);
                    var tbl = new XElement(Table + "table", new XAttribute(Table + "name", $"Table{++tableNo}"),
                        new XElement(Table + "table-column", new XAttribute(Table + "number-columns-repeated", cols)));
                    foreach (var row in t.Rows)
                    {
                        var tr = new XElement(Table + "table-row");
                        for (int c = 0; c < cols; c++)
                            tr.Add(new XElement(Table + "table-cell", new XAttribute(Office + "value-type", "string"),
                                new XElement(Text + "p", Spaced(c < row.Count ? row[c] : ""))));
                        tbl.Add(tr);
                    }
                    body.Add(tbl);
                    break;
                }
                case Picture pic:
                {
                    var ext = pic.Extension.TrimStart('.').ToLowerInvariant();
                    var name = $"Pictures/image{pictures.Count + 1}.{ext}";
                    pictures.Add((name, pic.Data, ext is "jpg" or "jpeg" ? "image/jpeg" : ext == "gif" ? "image/gif" : "image/png"));
                    var (w, h) = PictureSize(pic);
                    body.Add(new XElement(Text + "p", new XAttribute(Text + "style-name", "Standard"),
                        new XElement(Draw + "frame", new XAttribute(Draw + "name", $"Picture{++frameNo}"), new XAttribute(Text + "anchor-type", "as-char"),
                            new XAttribute(Svg + "width", $"{w:0.##}pt"), new XAttribute(Svg + "height", $"{h:0.##}pt"),
                            new XElement(Draw + "image", new XAttribute(XLink + "href", name), new XAttribute(XLink + "type", "simple"),
                                new XAttribute(XLink + "show", "embed"), new XAttribute(XLink + "actuate", "onLoad")))));
                    break;
                }
                case PageBreak:
                    body.Add(new XElement(Text + "p", new XAttribute(Text + "style-name", "PBreak")));
                    break;
            }
        }
        if (list is not null) body.Add(list);

        var content = new XDocument(new XElement(Office + "document-content", Ns(), new XAttribute(Office + "version", "1.3"),
            new XElement(Office + "automatic-styles",
                TextStyle("TB", bold: true, italic: false), TextStyle("TI", bold: false, italic: true), TextStyle("TBI", bold: true, italic: true),
                new XElement(Style + "style", new XAttribute(Style + "name", "PBreak"), new XAttribute(Style + "family", "paragraph"),
                    new XAttribute(Style + "parent-style-name", "Standard"),
                    new XElement(Style + "paragraph-properties", new XAttribute(Fo + "break-before", "page"))),
                ListStyle("LBullet", ordered: false), ListStyle("LNum", ordered: true)),
            new XElement(Office + "body", body)));

        var styles = new XDocument(new XElement(Office + "document-styles", Ns(), new XAttribute(Office + "version", "1.3"),
            new XElement(Office + "styles",
                new XElement(Style + "default-style", new XAttribute(Style + "family", "paragraph"),
                    new XElement(Style + "paragraph-properties", new XAttribute(Fo + "margin-bottom", "0.2cm")),
                    new XElement(Style + "text-properties", new XAttribute(Style + "font-name", "Calibri"), new XAttribute(Fo + "font-size", "11pt"))),
                new XElement(Style + "style", new XAttribute(Style + "name", "Standard"), new XAttribute(Style + "family", "paragraph")),
                Enumerable.Range(1, 6).Select(i => new XElement(Style + "style",
                    new XAttribute(Style + "name", $"Heading_20_{i}"), new XAttribute(Style + "display-name", $"Heading {i}"),
                    new XAttribute(Style + "family", "paragraph"), new XAttribute(Style + "parent-style-name", "Standard"),
                    new XAttribute(Style + "default-outline-level", i),
                    new XElement(Style + "paragraph-properties", new XAttribute(Fo + "margin-top", "0.4cm"), new XAttribute(Fo + "keep-with-next", "always")),
                    new XElement(Style + "text-properties", new XAttribute(Fo + "font-size", $"{Math.Max(12, 22 - i * 2)}pt"), new XAttribute(Fo + "font-weight", "bold")))))));

        var meta = new XDocument(new XElement(Office + "document-meta", Ns(), new XAttribute(Office + "version", "1.3"),
            new XElement(Office + "meta", new XElement(Meta + "generator", "Orange"), string.IsNullOrEmpty(d.Title) ? null : new XElement(Dc + "title", d.Title))));

        var manifest = new XDocument(new XElement(Manifest + "manifest", new XAttribute(XNamespace.Xmlns + "manifest", Manifest), new XAttribute(Manifest + "version", "1.3"),
            new XElement(Manifest + "file-entry", new XAttribute(Manifest + "full-path", "/"), new XAttribute(Manifest + "version", "1.3"), new XAttribute(Manifest + "media-type", "application/vnd.oasis.opendocument.text")),
            new[] { "content.xml", "styles.xml", "meta.xml" }.Select(f => new XElement(Manifest + "file-entry", new XAttribute(Manifest + "full-path", f), new XAttribute(Manifest + "media-type", "text/xml"))),
            pictures.Select(p => new XElement(Manifest + "file-entry", new XAttribute(Manifest + "full-path", p.Name), new XAttribute(Manifest + "media-type", p.Mime)))));

        Put(zip, "content.xml", content); Put(zip, "styles.xml", styles); Put(zip, "meta.xml", meta);
        foreach (var p in pictures) { var e = zip.CreateEntry(p.Name, CompressionLevel.NoCompression); using var s = e.Open(); s.Write(p.Data); }
        Put(zip, "META-INF/manifest.xml", manifest);
    }

    private static object[] Ns() =>
    [
        new XAttribute(XNamespace.Xmlns + "office", Office), new XAttribute(XNamespace.Xmlns + "text", Text),
        new XAttribute(XNamespace.Xmlns + "table", Table), new XAttribute(XNamespace.Xmlns + "style", Style),
        new XAttribute(XNamespace.Xmlns + "fo", Fo), new XAttribute(XNamespace.Xmlns + "draw", Draw),
        new XAttribute(XNamespace.Xmlns + "xlink", XLink), new XAttribute(XNamespace.Xmlns + "svg", Svg),
        new XAttribute(XNamespace.Xmlns + "meta", Meta), new XAttribute(XNamespace.Xmlns + "dc", Dc),
    ];

    private static XElement TextStyle(string name, bool bold, bool italic) =>
        new(Style + "style", new XAttribute(Style + "name", name), new XAttribute(Style + "family", "text"),
            new XElement(Style + "text-properties",
                bold ? new XAttribute(Fo + "font-weight", "bold") : null,
                italic ? new XAttribute(Fo + "font-style", "italic") : null));

    private static XElement ListStyle(string name, bool ordered) =>
        new(Text + "list-style", new XAttribute(Style + "name", name),
            Enumerable.Range(1, 6).Select(i => ordered
                ? new XElement(Text + "list-level-style-number", new XAttribute(Text + "level", i), new XAttribute(Style + "num-format", "1"), new XAttribute(Style + "num-suffix", "."),
                    LevelProps(i))
                : new XElement(Text + "list-level-style-bullet", new XAttribute(Text + "level", i), new XAttribute(Text + "bullet-char", i % 2 == 1 ? "•" : "◦"), LevelProps(i))));

    private static XElement LevelProps(int level) =>
        new(Style + "list-level-properties", new XAttribute(Text + "list-level-position-and-space-mode", "label-alignment"),
            new XElement(Style + "list-level-label-alignment", new XAttribute(Text + "label-followed-by", "listtab"),
                new XAttribute(Fo + "text-indent", "-0.635cm"), new XAttribute(Fo + "margin-left", $"{0.635 * (level + 1):0.###}cm")));

    private static IEnumerable<object> Runs(IReadOnlyList<Run> runs)
    {
        foreach (var r in runs)
        {
            var style = r.Bold && r.Italic ? "TBI" : r.Bold ? "TB" : r.Italic ? "TI" : null;
            if (style is null) foreach (var n in Spaced(r.Text)) yield return n;
            else yield return new XElement(Text + "span", new XAttribute(Text + "style-name", style), Spaced(r.Text));
        }
    }

    /// <summary>ODF collapses whitespace: runs of spaces become text:s, tabs text:tab, newlines text:line-break.</summary>
    private static IEnumerable<object> Spaced(string s)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '\t' || c == '\n' || (c == ' ' && (i == 0 || s[i - 1] == ' ')))
            {
                if (sb.Length > 0) { yield return Clean(sb.ToString()); sb.Clear(); }
                if (c == '\t') yield return new XElement(Text + "tab");
                else if (c == '\n') yield return new XElement(Text + "line-break");
                else { int n = 1; while (i + 1 < s.Length && s[i + 1] == ' ') { n++; i++; } yield return new XElement(Text + "s", new XAttribute(Text + "c", n)); }
            }
            else if (c != '\r') sb.Append(c);
        }
        if (sb.Length > 0) yield return Clean(sb.ToString());
    }

    private static string Clean(string s) => new(s.Where(XmlConvert.IsXmlChar).ToArray());

    private static (double W, double H) PictureSize(Picture p)
    {
        double w = p.WidthPt, h = p.HeightPt;
        if (w <= 0 || h <= 0)
            try { using var img = new ImageMagick.MagickImage(p.Data); w = img.Width * 0.75; h = img.Height * 0.75; } catch { w = 300; h = 200; }
        const double maxW = 450;   // fit the text width of an A4 page
        if (w > maxW) { h = h * maxW / w; w = maxW; }
        return (w, h);
    }

    private static void Put(ZipArchive zip, string name, XDocument doc)
    {
        var e = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var s = e.Open();
        using var w = XmlWriter.Create(s, new XmlWriterSettings { Encoding = new UTF8Encoding(false) });
        doc.Save(w);
    }

    // ---------------------------------------------------------------- read

    public static DocModel Read(string path)
    {
        OfficeGuard.CheckZip(path);
        using var zip = ZipFile.OpenRead(path);
        var entry = zip.GetEntry("content.xml") ?? throw new OrangeException("This isn't an OpenDocument text file (content.xml is missing).");
        XDocument doc;
        using (var s = entry.Open())
        using (var r = XmlReader.Create(s, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 500_000_000 }))
            doc = XDocument.Load(r);

        var styles = new Dictionary<string, (bool B, bool I, bool Break)>();
        var ordered = new HashSet<string>();
        foreach (var src in new[] { doc, LoadStyles(zip) })
        {
            if (src is null) continue;
            foreach (var st in src.Descendants(Style + "style"))
            {
                var name = (string?)st.Attribute(Style + "name"); if (name is null) continue;
                var tp = st.Element(Style + "text-properties"); var pp = st.Element(Style + "paragraph-properties");
                styles[name] = ((string?)tp?.Attribute(Fo + "font-weight") is "bold" or "600" or "700" or "800" or "900",
                                (string?)tp?.Attribute(Fo + "font-style") is "italic" or "oblique",
                                (string?)pp?.Attribute(Fo + "break-before") == "page");
            }
            foreach (var ls in src.Descendants(Text + "list-style"))
                if (ls.Elements(Text + "list-level-style-number").Any() && (string?)ls.Attribute(Style + "name") is { } n) ordered.Add(n);
        }

        var model = new DocModel { Title = (string?)LoadMeta(zip)?.Descendants(Dc + "title").FirstOrDefault() ?? Path.GetFileNameWithoutExtension(path) };
        var bodyText = doc.Root?.Element(Office + "body")?.Element(Office + "text") ?? throw new OrangeException("This OpenDocument file has no text body.");
        Walk(bodyText.Elements(), model, styles, ordered, zip, null, 0);
        return model;
    }

    private static XDocument? LoadStyles(ZipArchive zip) => LoadXml(zip, "styles.xml");
    private static XDocument? LoadMeta(ZipArchive zip) => LoadXml(zip, "meta.xml");
    private static XDocument? LoadXml(ZipArchive zip, string name)
    {
        var e = zip.GetEntry(name); if (e is null) return null;
        try { using var s = e.Open(); using var r = XmlReader.Create(s, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }); return XDocument.Load(r); }
        catch { return null; }
    }

    private static void Walk(IEnumerable<XElement> elements, DocModel m, Dictionary<string, (bool B, bool I, bool Break)> styles,
        HashSet<string> ordered, ZipArchive zip, bool? listOrdered, int level)
    {
        foreach (var e in elements)
        {
            if (e.Name == Text + "h")
            {
                int lvl = (int?)e.Attribute(Text + "outline-level") ?? 1;
                m.Add(new Heading(Math.Clamp(lvl, 1, 6), string.Concat(Collect(e, false, false, styles, m, zip).Select(r => r.Text)).Trim()));
            }
            else if (e.Name == Text + "p")
            {
                if ((string?)e.Attribute(Text + "style-name") is { } sn && styles.TryGetValue(sn, out var ps) && ps.Break && m.Blocks.Count > 0) m.Add(new PageBreak());
                var runs = Merge(Collect(e, false, false, styles, m, zip));
                if (listOrdered is { } o) { if (runs.Count > 0) m.Add(new ListItem(runs, o, level)); }
                else if (runs.Count > 0 && runs.Any(r => r.Text.Trim().Length > 0)) m.Add(new Para(runs));
            }
            else if (e.Name == Text + "list")
            {
                var o = (string?)e.Attribute(Text + "style-name") is { } ln ? ordered.Contains(ln) : listOrdered ?? false;
                foreach (var item in e.Elements().Where(x => x.Name == Text + "list-item" || x.Name == Text + "list-header"))
                    Walk(item.Elements(), m, styles, ordered, zip, o, listOrdered is null ? 0 : level + 1);
            }
            else if (e.Name == Table + "table")
            {
                var rows = new List<IReadOnlyList<string>>();
                foreach (var row in e.Descendants(Table + "table-row"))
                {
                    var cells = new List<string>();
                    foreach (var c in row.Elements(Table + "table-cell"))
                    {
                        var txt = string.Join("\n", c.Elements(Text + "p").Select(p => string.Concat(Collect(p, false, false, styles, null, zip).Select(r => r.Text))));
                        int rep = Math.Min((int?)c.Attribute(Table + "number-columns-repeated") ?? 1, 50);
                        for (int i = 0; i < rep; i++) cells.Add(txt);
                    }
                    while (cells.Count > 0 && cells[^1].Length == 0) cells.RemoveAt(cells.Count - 1);
                    if (cells.Count > 0) rows.Add(cells);
                    if (rows.Count > 20_000) break;
                }
                if (rows.Count > 0) m.Add(new Table(rows));
            }
            else if (e.Name == Text + "section" || e.Name == Text + "index-body" || e.Name.LocalName.EndsWith("-index") || e.Name == Text + "soft-page-break" && false)
                Walk(e.Elements(), m, styles, ordered, zip, listOrdered, level);
        }
    }

    private static List<Run> Merge(List<Run> runs)
    {
        var o = new List<Run>();
        foreach (var r in runs)
            if (o.Count > 0 && o[^1].Bold == r.Bold && o[^1].Italic == r.Italic) o[^1] = o[^1] with { Text = o[^1].Text + r.Text };
            else if (r.Text.Length > 0) o.Add(r);
        return o;
    }

    /// <summary>Text runs of one paragraph; pictures inside it are added to the model as their own blocks.</summary>
    private static List<Run> Collect(XElement p, bool bold, bool italic, Dictionary<string, (bool B, bool I, bool Break)> styles, DocModel? m, ZipArchive zip)
    {
        var list = new List<Run>();
        if ((string?)p.Attribute(Text + "style-name") is { } sn && styles.TryGetValue(sn, out var st)) { bold |= st.B; italic |= st.I; }
        foreach (var n in p.Nodes())
        {
            if (n is XText t) list.Add(new Run(System.Text.RegularExpressions.Regex.Replace(t.Value, @"\s+", " "), bold, italic));
            else if (n is XElement e)
            {
                if (e.Name == Text + "s") list.Add(new Run(new string(' ', Math.Clamp((int?)e.Attribute(Text + "c") ?? 1, 1, 1000)), bold, italic));
                else if (e.Name == Text + "tab") list.Add(new Run("\t", bold, italic));
                else if (e.Name == Text + "line-break") list.Add(new Run("\n", bold, italic));
                else if (e.Name == Text + "note") { }   // footnote bodies would land mid-sentence
                else if (e.Name == Draw + "frame")
                {
                    var href = (string?)e.Element(Draw + "image")?.Attribute(XLink + "href");
                    if (m is not null && href is not null && !href.Contains("..") && zip.GetEntry(href.TrimStart('.', '/')) is { } pe && pe.Length < 50_000_000)
                    {
                        using var s = pe.Open(); using var ms = new MemoryStream(); s.CopyTo(ms);
                        var ext = Path.GetExtension(href).TrimStart('.').ToLowerInvariant();
                        if (ext is "png" or "jpg" or "jpeg" or "gif" or "bmp")
                            m.Add(new Picture(ms.ToArray(), ext, ParsePt((string?)e.Attribute(Svg + "width")), ParsePt((string?)e.Attribute(Svg + "height"))));
                    }
                }
                else list.AddRange(Collect(e, bold, italic, styles, m, zip));
            }
        }
        return list;
    }

    private static double ParsePt(string? v)
    {
        if (string.IsNullOrEmpty(v)) return 0;
        var num = new string(v.TakeWhile(c => char.IsDigit(c) || c == '.').ToArray());
        if (!double.TryParse(num, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d)) return 0;
        return v.EndsWith("cm") ? d * 28.3465 : v.EndsWith("mm") ? d * 2.83465 : v.EndsWith("in") ? d * 72 : v.EndsWith("px") ? d * 0.75 : d;
    }
}
