using System.IO.Compression;
using System.Net;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Orange.Core.Office;

/// <summary>
/// E-book readers (EPUB, FB2, MOBI without DRM, CBZ) into <see cref="DocModel"/> and an EPUB 3 writer.
/// XML is parsed with DTDs prohibited and no resolver; ZIP containers pass the Office bomb guard; paths inside an
/// EPUB are resolved against the package and never touch the disk.
/// </summary>
public static class Ebooks
{
    private static readonly XmlReaderSettings SafeXml = new() { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 500_000_000 };

    private static XDocument LoadXml(Stream s)
    {
        using var r = XmlReader.Create(s, SafeXml);
        return XDocument.Load(r);
    }

    public static DocModel Read(string path) => FileTypeDetector.Ext(path) switch
    {
        "epub" => ReadEpub(path),
        "fb2" => ReadFb2(path),
        "mobi" or "azw" => ReadMobi(path),
        "cbz" => ReadCbz(path),
        var e => throw new OrangeException($"Orange can't read .{e} e-books."),
    };

    // ---------------- EPUB

    public static DocModel ReadEpub(string path)
    {
        OfficeGuard.CheckZip(path);
        using var z = ZipFile.OpenRead(path);
        if (z.GetEntry("META-INF/encryption.xml") is not null) throw new OrangeException("This e-book is DRM-protected, so Orange can't convert it.");
        var container = z.GetEntry("META-INF/container.xml") ?? throw new OrangeException("This EPUB has no container file; it looks damaged.");
        XNamespace cn = "urn:oasis:names:tc:opendocument:xmlns:container";
        string opfPath;
        using (var s = container.Open()) opfPath = LoadXml(s).Descendants(cn + "rootfile").FirstOrDefault()?.Attribute("full-path")?.Value ?? throw new OrangeException("This EPUB doesn't say where its content is.");
        var opfEntry = z.GetEntry(opfPath) ?? throw new OrangeException("This EPUB's package file is missing.");
        XDocument opf; using (var s = opfEntry.Open()) opf = LoadXml(s);
        XNamespace o = "http://www.idpf.org/2007/opf"; XNamespace dc = "http://purl.org/dc/elements/1.1/";
        var baseDir = opfPath.Contains('/') ? opfPath[..(opfPath.LastIndexOf('/') + 1)] : "";
        var manifest = opf.Descendants(o + "item").Where(i => i.Attribute("id") != null && i.Attribute("href") != null)
            .GroupBy(i => i.Attribute("id")!.Value).ToDictionary(g => g.Key, g => (Href: g.First().Attribute("href")!.Value, Type: g.First().Attribute("media-type")?.Value ?? ""));
        var d = new DocModel { Title = opf.Descendants(dc + "title").FirstOrDefault()?.Value ?? Path.GetFileNameWithoutExtension(path) };
        bool first = true;
        foreach (var idref in opf.Descendants(o + "itemref").Select(i => i.Attribute("idref")?.Value).Where(v => v != null).Take(5000))
        {
            if (!manifest.TryGetValue(idref!, out var item) || !item.Type.Contains("html")) continue;
            var entryName = Resolve(baseDir, WebUtility.UrlDecode(item.Href));
            var entry = entryName is null ? null : z.GetEntry(entryName);
            if (entry is null) continue;
            string html; using (var sr = new StreamReader(entry.Open(), Encoding.UTF8)) html = sr.ReadToEnd();
            var chapter = TextFormats.FromHtml(html, d.Title);
            if (!first && chapter.Blocks.Count > 0) d.Add(new PageBreak());
            foreach (var b in chapter.Blocks) d.Add(b);
            if (chapter.Blocks.Count > 0) first = false;
        }
        return d;
    }

    /// <summary>Resolves "../x.html" against a folder inside the ZIP; null when it would leave the package.</summary>
    public static string? Resolve(string baseDir, string href)
    {
        href = href.Split('#')[0];
        var parts = new List<string>(baseDir.Split('/', StringSplitOptions.RemoveEmptyEntries));
        foreach (var p in href.Replace('\\', '/').Split('/'))
        {
            if (p is "" or ".") continue;
            if (p == "..") { if (parts.Count == 0) return null; parts.RemoveAt(parts.Count - 1); }
            else parts.Add(p);
        }
        return string.Join('/', parts);
    }

    // ---------------- FB2

    public static DocModel ReadFb2(string path)
    {
        XDocument x; using (var s = File.OpenRead(path)) x = LoadXml(s);
        XNamespace f = "http://www.gribuser.ru/xml/fictionbook/2.0";
        var d = new DocModel { Title = x.Descendants(f + "book-title").FirstOrDefault()?.Value ?? Path.GetFileNameWithoutExtension(path) };
        var binaries = x.Descendants(f + "binary").Where(b => b.Attribute("id") != null).ToDictionary(b => b.Attribute("id")!.Value, b => b);
        foreach (var body in x.Descendants(f + "body").Take(10)) Walk(body, 1);
        return d;

        void Walk(XElement el, int depth)
        {
            foreach (var c in el.Elements())
            {
                switch (c.Name.LocalName)
                {
                    case "title": d.Add(new Heading(Math.Min(depth, 6), string.Join(" ", c.Elements().Select(e => e.Value.Trim())))); break;
                    case "section": Walk(c, depth + 1); break;
                    case "p": d.Add(new Para(Runs(c))); break;
                    case "subtitle": d.Add(new Heading(Math.Min(depth + 1, 6), c.Value.Trim())); break;
                    case "empty-line": break;
                    case "poem" or "cite" or "epigraph": Walk(c, depth); break;
                    case "stanza": foreach (var v in c.Elements()) d.Add(Para.Of(v.Value)); break;
                    case "image":
                        var href = c.Attributes().FirstOrDefault(a => a.Name.LocalName == "href")?.Value?.TrimStart('#');
                        if (href != null && binaries.TryGetValue(href, out var bin))
                        {
                            try
                            {
                                var bytes = Convert.FromBase64String(bin.Value);
                                var ext = (bin.Attribute("content-type")?.Value ?? "") switch { "image/png" => "png", "image/jpeg" or "image/jpg" => "jpg", _ => "" };
                                if (ext.Length > 0 && bytes.Length < 30_000_000) d.Add(new Picture(bytes, ext));
                            }
                            catch (FormatException) { }
                        }
                        break;
                    case "table":
                        d.Add(new Table(c.Elements().Where(r => r.Name.LocalName == "tr").Select(r => (IReadOnlyList<string>)r.Elements().Select(td => td.Value.Trim()).ToList()).ToList()));
                        break;
                }
            }
        }
        static List<Run> Runs(XElement p) => p.Nodes().Select(n => n switch
        {
            XText t => new Run(t.Value),
            XElement e when e.Name.LocalName == "strong" => new Run(e.Value, Bold: true),
            XElement e when e.Name.LocalName == "emphasis" => new Run(e.Value, Italic: true),
            XElement e => new Run(e.Value),
            _ => new Run(""),
        }).ToList();
    }

    // ---------------- MOBI (PalmDOC, no DRM)

    public static DocModel ReadMobi(string path)
    {
        var data = File.ReadAllBytes(path);
        if (data.Length < 86 || Encoding.ASCII.GetString(data, 60, 8) is not ("BOOKMOBI" or "TEXtREAd"))
            throw new OrangeException("This doesn't look like a MOBI e-book.");
        int count = U16(data, 76);
        if (count < 2 || 78 + count * 8 > data.Length) throw new OrangeException("This MOBI file looks damaged.");
        int Rec(int i) => (int)U32(data, 78 + i * 8);
        int RecEnd(int i) => i + 1 < count ? Rec(i + 1) : data.Length;
        int r0 = Rec(0);
        int compression = U16(data, r0), textRecords = U16(data, r0 + 8), encryption = U16(data, r0 + 12);
        if (encryption != 0) throw new OrangeException("This e-book is DRM-protected, so Orange can't convert it.");
        if (compression is not (1 or 2)) throw new OrangeException("This MOBI uses HUFF/CDIC compression, which Orange can't read yet. Try the EPUB edition.");
        Encoding enc = Encoding.UTF8; int extraFlags = 0; string title = Path.GetFileNameWithoutExtension(path);
        if (r0 + 20 < data.Length && Encoding.ASCII.GetString(data, r0 + 16, 4) == "MOBI")
        {
            int headerLen = (int)U32(data, r0 + 20);
            var cp = U32(data, r0 + 28);
            if (cp == 1252) { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); enc = Encoding.GetEncoding(1252); }
            if (headerLen >= 0xE4 && r0 + 16 + 0xE4 <= data.Length) extraFlags = U16(data, r0 + 16 + 0xE2);
            int nameOff = (int)U32(data, r0 + 0x54), nameLen = (int)U32(data, r0 + 0x58);
            if (nameLen > 0 && nameLen < 1000 && r0 + nameOff + nameLen <= data.Length) title = enc.GetString(data, r0 + nameOff, nameLen);
        }
        var text = new MemoryStream();
        for (int i = 1; i <= textRecords && i < count; i++)
        {
            int start = Rec(i), end = RecEnd(i);
            if (start < 0 || end > data.Length || end < start) throw new OrangeException("This MOBI file looks damaged.");
            end -= TrailingSize(data, start, end, extraFlags);
            var rec = data.AsSpan(start, Math.Max(0, end - start));
            if (compression == 2) PalmDocDecompress(rec, text); else text.Write(rec);
            if (text.Length > 500_000_000) throw new OrangeException("The e-book's text is too large.");
        }
        var html = enc.GetString(text.ToArray()).Replace("<mbp:pagebreak/>", "<hr>").Replace("<mbp:pagebreak />", "<hr>");
        return TextFormats.FromHtml(html, title);
    }

    private static int U16(byte[] b, int o) => o + 2 <= b.Length ? b[o] << 8 | b[o + 1] : 0;
    private static uint U32(byte[] b, int o) => o + 4 <= b.Length ? (uint)(b[o] << 24 | b[o + 1] << 16 | b[o + 2] << 8 | b[o + 3]) : 0;

    /// <summary>Size of the trailing entries MOBI appends to each text record (multibyte and index data).</summary>
    private static int TrailingSize(byte[] d, int start, int end, int flags)
    {
        int size = 0;
        for (int bit = 15; bit > 0; bit--)
        {
            if ((flags & (1 << bit)) == 0) continue;
            // Backward-encoded variable-length integer at the end of the remaining record.
            int value = 0, shift = 0, pos = end - size - 1;
            for (int k = 0; k < 4 && pos >= start; k++, pos--)
            {
                byte v = d[pos];
                value |= (v & 0x7F) << shift; shift += 7;
                if ((v & 0x80) != 0) break;
            }
            size += value;
        }
        if ((flags & 1) != 0 && end - size - 1 >= start) size += (d[end - size - 1] & 3) + 1;
        return Math.Clamp(size, 0, end - start);
    }

    public static void PalmDocDecompress(ReadOnlySpan<byte> src, Stream dst)
    {
        var outBuf = new List<byte>(src.Length * 2);
        for (int i = 0; i < src.Length;)
        {
            byte c = src[i++];
            if (c == 0 || c is >= 9 and <= 0x7F) outBuf.Add(c);
            else if (c is >= 1 and <= 8) { for (int k = 0; k < c && i < src.Length; k++) outBuf.Add(src[i++]); }
            else if (c >= 0xC0) { outBuf.Add((byte)' '); outBuf.Add((byte)(c ^ 0x80)); }
            else if (i < src.Length)
            {
                int pair = (c << 8 | src[i++]) & 0x3FFF;
                int dist = pair >> 3, len = (pair & 7) + 3;
                if (dist == 0 || dist > outBuf.Count) continue;
                int from = outBuf.Count - dist;
                for (int k = 0; k < len; k++) outBuf.Add(outBuf[from + k]);
            }
        }
        dst.Write(outBuf.ToArray());
    }

    // ---------------- CBZ (comic archive: pictures in name order)

    public static DocModel ReadCbz(string path)
    {
        OfficeGuard.CheckZip(path);
        var d = new DocModel { Title = Path.GetFileNameWithoutExtension(path) };
        using var z = ZipFile.OpenRead(path);
        var pages = z.Entries.Where(e => e.Length > 0 && e.Length < 100_000_000 && Path.GetExtension(e.Name).ToLowerInvariant() is ".jpg" or ".jpeg" or ".png" or ".gif" or ".webp")
            .OrderBy(e => e.FullName, NaturalComparer.Instance).Take(5000).ToList();
        if (pages.Count == 0) throw new OrangeException("This comic archive has no pictures.");
        foreach (var e in pages)
        {
            using var s = e.Open(); using var ms = new MemoryStream(); s.CopyTo(ms);
            var bytes = ms.ToArray();
            var ext = Path.GetExtension(e.Name).TrimStart('.').ToLowerInvariant().Replace("jpeg", "jpg");
            if (ext == "webp") { using var img = new ImageMagick.MagickImage(bytes, ImageMagick.MagickFormat.WebP); bytes = img.ToByteArray(ImageMagick.MagickFormat.Png); ext = "png"; }
            if (d.Blocks.Count > 0) d.Add(new PageBreak());
            d.Add(new Picture(bytes, ext));
        }
        return d;
    }

    private sealed class NaturalComparer : IComparer<string>
    {
        public static readonly NaturalComparer Instance = new();
        public int Compare(string? a, string? b)
        {
            a ??= ""; b ??= "";
            int i = 0, j = 0;
            while (i < a.Length && j < b.Length)
            {
                if (char.IsDigit(a[i]) && char.IsDigit(b[j]))
                {
                    int si = i, sj = j;
                    while (i < a.Length && char.IsDigit(a[i])) i++;
                    while (j < b.Length && char.IsDigit(b[j])) j++;
                    var c = long.Parse(a.AsSpan(si, Math.Min(18, i - si))).CompareTo(long.Parse(b.AsSpan(sj, Math.Min(18, j - sj))));
                    if (c != 0) return c;
                }
                else { var c = char.ToLowerInvariant(a[i]).CompareTo(char.ToLowerInvariant(b[j])); if (c != 0) return c; i++; j++; }
            }
            return (a.Length - i).CompareTo(b.Length - j);
        }
    }

    // ---------------- EPUB writer

    public static void WriteEpub(DocModel d, string path)
    {
        using var fs = new FileStream(path, FileMode.CreateNew);
        using var z = new ZipArchive(fs, ZipArchiveMode.Create);
        void Put(string name, string text, CompressionLevel level = CompressionLevel.Optimal)
        {
            using var w = new StreamWriter(z.CreateEntry(name, level).Open(), new UTF8Encoding(false));
            w.Write(text);
        }
        Put("mimetype", "application/epub+zip", CompressionLevel.NoCompression);
        Put("META-INF/container.xml", "<?xml version=\"1.0\"?><container version=\"1.0\" xmlns=\"urn:oasis:names:tc:opendocument:xmlns:container\"><rootfiles><rootfile full-path=\"OEBPS/content.opf\" media-type=\"application/oebps-package+xml\"/></rootfiles></container>");

        // Chapters: split at page breaks and level-1 headings.
        var chapters = new List<List<Block>> { new() };
        foreach (var b in d.Blocks)
        {
            if ((b is PageBreak || b is Heading { Level: 1 }) && chapters[^1].Count > 0) chapters.Add(new());
            if (b is not PageBreak) chapters[^1].Add(b);
        }
        var manifest = new StringBuilder(); var spine = new StringBuilder(); var nav = new StringBuilder();
        int img = 0;
        for (int i = 0; i < chapters.Count; i++)
        {
            var body = new StringBuilder();
            foreach (var b in chapters[i])
            {
                if (b is Picture p)
                {
                    var name = $"img{++img}.{p.Extension}";
                    using (var s = z.CreateEntry("OEBPS/" + name, CompressionLevel.NoCompression).Open()) s.Write(p.Data);
                    manifest.Append($"<item id=\"i{img}\" href=\"{name}\" media-type=\"image/{(p.Extension == "jpg" ? "jpeg" : p.Extension)}\"/>");
                    body.Append($"<p><img src=\"{name}\" alt=\"\"/></p>\n");
                }
                else
                {
                    var one = new DocModel(); one.Add(b);
                    var html = TextFormats.ToHtml(one, embedPictures: false);
                    var start = html.IndexOf("<body>", StringComparison.Ordinal) + 6; var end = html.LastIndexOf("</body>", StringComparison.Ordinal);
                    body.Append(Xhtml(html[start..end]));
                }
            }
            var title = chapters[i].OfType<Heading>().FirstOrDefault()?.Text ?? (chapters.Count == 1 ? d.Title : $"Part {i + 1}");
            Put($"OEBPS/c{i + 1}.xhtml", $"<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<!DOCTYPE html>\n<html xmlns=\"http://www.w3.org/1999/xhtml\"><head><title>{WebUtility.HtmlEncode(title)}</title></head><body>\n{body}</body></html>");
            manifest.Append($"<item id=\"c{i + 1}\" href=\"c{i + 1}.xhtml\" media-type=\"application/xhtml+xml\"/>");
            spine.Append($"<itemref idref=\"c{i + 1}\"/>");
            nav.Append($"<li><a href=\"c{i + 1}.xhtml\">{WebUtility.HtmlEncode(title)}</a></li>");
        }
        Put("OEBPS/nav.xhtml", $"<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<!DOCTYPE html>\n<html xmlns=\"http://www.w3.org/1999/xhtml\" xmlns:epub=\"http://www.idpf.org/2007/ops\"><head><title>Contents</title></head><body><nav epub:type=\"toc\"><ol>{nav}</ol></nav></body></html>");
        var id = "urn:uuid:" + Guid.NewGuid();
        Put("OEBPS/content.opf", $"<?xml version=\"1.0\" encoding=\"utf-8\"?><package xmlns=\"http://www.idpf.org/2007/opf\" version=\"3.0\" unique-identifier=\"uid\"><metadata xmlns:dc=\"http://purl.org/dc/elements/1.1/\"><dc:identifier id=\"uid\">{id}</dc:identifier><dc:title>{WebUtility.HtmlEncode(d.Title.Length > 0 ? d.Title : "Untitled")}</dc:title><dc:language>en</dc:language><meta property=\"dcterms:modified\">{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ssZ}</meta></metadata><manifest><item id=\"nav\" href=\"nav.xhtml\" media-type=\"application/xhtml+xml\" properties=\"nav\"/>{manifest}</manifest><spine>{spine}</spine></package>");
    }

    /// <summary>HTML fragments from the writer are already escaped; make void tags XHTML-safe.</summary>
    private static string Xhtml(string html) => html.Replace("<hr>", "<hr/>").Replace("<br>", "<br/>");
}
