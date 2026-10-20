using System.Text;

namespace Orange.Core.Office;

/// <summary>
/// Microsoft Compound File Binary (the container of .doc/.ppt/.xls), read from the open [MS-CFB] specification.
/// Read-only, bounds-checked, with loop protection on every sector chain.
/// </summary>
public sealed class CompoundFile
{
    private readonly byte[] _d;
    private readonly int _sector, _mini;
    private readonly List<int> _fat = new(), _miniFat = new();
    private readonly List<(string Name, int Type, int Start, long Size)> _dir = new();
    private readonly byte[] _miniStream;
    private const int EndOfChain = -2, FreeSect = -1;

    public CompoundFile(byte[] data)
    {
        _d = data;
        if (data.Length < 512 || BitConverter.ToUInt64(data, 0) != 0xE11AB1A1E011CFD0) throw new OrangeException("This isn't an old-style Office file.");
        _sector = 1 << BitConverter.ToUInt16(data, 30);
        _mini = 1 << BitConverter.ToUInt16(data, 32);
        if (_sector is not (512 or 4096) || _mini != 64) throw new OrangeException("This Office file has an unsupported layout.");
        int fatSectors = BitConverter.ToInt32(data, 44);
        int dirStart = BitConverter.ToInt32(data, 48);
        int miniFatStart = BitConverter.ToInt32(data, 60);
        int difatStart = BitConverter.ToInt32(data, 68);

        var fatList = new List<int>();
        for (int i = 0; i < 109; i++) { int s = BitConverter.ToInt32(data, 76 + i * 4); if (s >= 0) fatList.Add(s); }
        int guard = 0;
        for (int s = difatStart; s >= 0 && guard++ < 100_000; )
        {
            int off = Offset(s);
            for (int i = 0; i < _sector / 4 - 1; i++) { int v = BitConverter.ToInt32(data, off + i * 4); if (v >= 0) fatList.Add(v); }
            s = BitConverter.ToInt32(data, off + _sector - 4);
        }
        if (fatList.Count > fatSectors + 1 && fatSectors > 0) fatList = fatList.Take(fatSectors).ToList();
        foreach (var fs in fatList) { int off = Offset(fs); for (int i = 0; i < _sector / 4; i++) _fat.Add(BitConverter.ToInt32(data, off + i * 4)); }

        var dirBytes = ReadChain(dirStart, long.MaxValue, _fat, _sector, big: true);
        for (int p = 0; p + 128 <= dirBytes.Length; p += 128)
        {
            int nameLen = BitConverter.ToUInt16(dirBytes, p + 64);
            var name = nameLen >= 2 && nameLen <= 64 ? Encoding.Unicode.GetString(dirBytes, p, nameLen - 2) : "";
            int type = dirBytes[p + 66];
            int start = BitConverter.ToInt32(dirBytes, p + 116);
            long size = _sector == 512 ? BitConverter.ToUInt32(dirBytes, p + 120) : BitConverter.ToInt64(dirBytes, p + 120);
            _dir.Add((name, type, start, size));
        }
        if (miniFatStart >= 0)
        {
            var mf = ReadChain(miniFatStart, long.MaxValue, _fat, _sector, big: true);
            for (int i = 0; i + 4 <= mf.Length; i += 4) _miniFat.Add(BitConverter.ToInt32(mf, i));
        }
        var root = _dir.FirstOrDefault(e => e.Type == 5);
        _miniStream = root.Type == 5 && root.Start >= 0 ? ReadChain(root.Start, root.Size, _fat, _sector, big: true) : [];
    }

    private int Offset(int sector)
    {
        long off = (long)(sector + 1) * _sector;
        if (sector < 0 || off + _sector > _d.Length) throw new OrangeException("This Office file is damaged (a sector points outside the file).");
        return (int)off;
    }

    private byte[] ReadChain(int start, long size, List<int> fat, int sectorSize, bool big)
    {
        using var ms = new MemoryStream();
        var seen = new HashSet<int>();
        for (int s = start; s >= 0; )
        {
            if (!seen.Add(s) || seen.Count > 2_000_000) throw new OrangeException("This Office file is damaged (a sector chain loops).");
            if (big) ms.Write(_d, Offset(s), sectorSize);
            else
            {
                long off = (long)s * sectorSize;
                if (off + sectorSize > _miniStream.Length) throw new OrangeException("This Office file is damaged (mini stream).");
                ms.Write(_miniStream, (int)off, sectorSize);
            }
            if (ms.Length >= size) break;
            s = s < fat.Count ? fat[s] : EndOfChain;
        }
        var bytes = ms.ToArray();
        return size < bytes.Length ? bytes[..(int)size] : bytes;
    }

    public bool Has(string name) => _dir.Any(e => e.Type == 2 && e.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    public byte[] Stream(string name)
    {
        var e = _dir.FirstOrDefault(x => x.Type == 2 && x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (e.Type != 2) throw new OrangeException($"This Office file has no \"{name}\" stream.");
        if (e.Size > 1_000_000_000) throw new OrangeException("This Office file is too large.");
        return e.Size < 4096 ? ReadChain(e.Start, e.Size, _miniFat, _mini, big: false) : ReadChain(e.Start, e.Size, _fat, _sector, big: true);
    }
}

/// <summary>
/// Word 97–2003 (.doc) text reader from the open [MS-DOC] specification: the piece table in the table stream
/// maps character positions to 8-bit or UTF-16 text in the WordDocument stream. Paragraphs, table rows (cell marks)
/// and field results are kept; formatting is not.
/// </summary>
public static class LegacyDoc
{
    public static DocModel Read(string path)
    {
        var cf = new CompoundFile(File.ReadAllBytes(path));
        var wd = cf.Stream("WordDocument");
        if (wd.Length < 0x01A2 || BitConverter.ToUInt16(wd, 0) != 0xA5EC) throw new OrangeException("This .doc file isn't a Word 97–2003 document (older Word 6/95 files aren't supported).");
        ushort flags = BitConverter.ToUInt16(wd, 0x0A);
        if ((flags & 0x0100) != 0) throw new OrangeException("This Word document is password-protected.");
        var table = cf.Stream((flags & 0x0200) != 0 ? "1Table" : "0Table");
        int ccpText = BitConverter.ToInt32(wd, 0x004C);
        // fcClx/lcbClx live in FibRgFcLcb97 at index 33 (0x01A2 from the start of the FIB).
        int csw = BitConverter.ToUInt16(wd, 32);
        int rgLwOff = 34 + csw * 2;
        int cslw = BitConverter.ToUInt16(wd, rgLwOff);
        int fcLcbOff = rgLwOff + 2 + cslw * 4 + 2;
        int fcClx = BitConverter.ToInt32(wd, fcLcbOff + 33 * 8);
        int lcbClx = BitConverter.ToInt32(wd, fcLcbOff + 33 * 8 + 4);
        if (fcClx < 0 || lcbClx <= 0 || fcClx + lcbClx > table.Length) throw new OrangeException("This Word document is damaged (no piece table).");

        // Clx: skip Prc entries (0x01), then Pcdt (0x02) holding the PlcPcd.
        int p = fcClx, end = fcClx + lcbClx;
        while (p < end && table[p] == 0x01) p += 3 + BitConverter.ToInt16(table, p + 1);
        if (p >= end || table[p] != 0x02) throw new OrangeException("This Word document is damaged (bad piece table).");
        int lcb = BitConverter.ToInt32(table, p + 1);
        int plc = p + 5;
        int n = (lcb - 4) / 12;
        var sb = new StringBuilder();
        Encoding cp1252;
        try { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); cp1252 = Encoding.GetEncoding(1252); } catch { cp1252 = Encoding.Latin1; }
        for (int i = 0; i < n; i++)
        {
            int cpStart = BitConverter.ToInt32(table, plc + i * 4), cpEnd = BitConverter.ToInt32(table, plc + (i + 1) * 4);
            if (cpStart >= ccpText) break;
            cpEnd = Math.Min(cpEnd, ccpText);
            int pcd = plc + (n + 1) * 4 + i * 8;
            uint fc = BitConverter.ToUInt32(table, pcd + 2);
            bool compressed = (fc & 0x40000000) != 0;
            long off = compressed ? (fc & 0x3FFFFFFF) / 2 : fc;
            int len = cpEnd - cpStart;
            if (len <= 0) continue;
            long bytes = compressed ? len : len * 2L;
            if (off < 0 || off + bytes > wd.Length) continue;
            sb.Append(compressed ? cp1252.GetString(wd, (int)off, len) : Encoding.Unicode.GetString(wd, (int)off, len * 2));
            if (sb.Length > 200_000_000) throw new OrangeException("This Word document is too large.");
        }
        return Build(sb.ToString(), Path.GetFileNameWithoutExtension(path));
    }

    /// <summary>Turns Word's special characters into the document model.</summary>
    internal static DocModel Build(string raw, string title)
    {
        var m = new DocModel { Title = title };
        var clean = new StringBuilder();
        // Fields: 0x13 instruction 0x14 result 0x15 → keep only the result.
        int depth = 0; var inInstr = new Stack<bool>();
        foreach (char c in raw)
        {
            if (c == '\u0013') { inInstr.Push(true); depth++; continue; }
            if (c == '\u0014') { if (inInstr.Count > 0) { inInstr.Pop(); inInstr.Push(false); } continue; }
            if (c == '\u0015') { if (inInstr.Count > 0) inInstr.Pop(); depth = Math.Max(0, depth - 1); continue; }
            if (inInstr.Count > 0 && inInstr.Peek()) continue;
            clean.Append(c);
        }
        var rows = new List<IReadOnlyList<string>>(); var cells = new List<string>(); var cell = new StringBuilder();
        foreach (var para in clean.ToString().Split('\r'))
        {
            if (para.Contains('\u0007'))
            {   // table text: cells end with 0x07, a row ends with an empty cell mark
                foreach (var part in para.Split('\u0007').SkipLast(1)) { cells.Add((cell + part).Trim()); cell.Clear(); }
                var tail = para[(para.LastIndexOf('\u0007') + 1)..];
                if (para.EndsWith('\u0007') && cells.Count > 0 && cells[^1].Length == 0 && cells.Count > 1) { cells.RemoveAt(cells.Count - 1); rows.Add(cells.ToList()); cells.Clear(); }
                else if (para.EndsWith('\u0007') && cells.Count > 0 && rows.Count > 0 && cells.Count >= rows[^1].Count) { rows.Add(cells.ToList()); cells.Clear(); }
                if (tail.Length > 0) cell.Append(tail).Append('\n');
                continue;
            }
            if (cells.Count > 0) { rows.Add(cells.ToList()); cells.Clear(); }
            if (rows.Count > 0) { m.Add(new Table(rows.ToList())); rows.Clear(); }
            if (para.Contains('\f')) foreach (var piece in para.Split('\f').Select((s, i) => (s, i))) { if (piece.i > 0) m.Add(new PageBreak()); AddPara(m, piece.s); }
            else AddPara(m, para);
        }
        if (cells.Count > 0) rows.Add(cells);
        if (rows.Count > 0) m.Add(new Table(rows));
        return m;
    }

    private static void AddPara(DocModel m, string s)
    {
        var t = new string(s.Select(c => c == '\u000B' ? '\n' : c == '\u001E' ? '-' : c == '\u00A0' ? ' ' : c).Where(c => c == '\n' || c == '\t' || c >= ' ').ToArray()).Trim();
        if (t.Length == 0) return;
        if (t.StartsWith('•') || t.StartsWith('·')) m.Add(new ListItem([new Run(t[1..].TrimStart())], false));
        else m.Add(Para.Of(t));
    }
}

/// <summary>
/// PowerPoint 97–2003 (.ppt) text reader from the open [MS-PPT] specification: walks the record tree of the
/// "PowerPoint Document" stream and collects each slide's text atoms (titles and bullets). Pictures, layout and
/// animations are not reproduced.
/// </summary>
public static class LegacyPpt
{
    public sealed record SlideText(string Title, List<string> Lines);

    public static List<SlideText> Read(string path)
    {
        var cf = new CompoundFile(File.ReadAllBytes(path));
        var s = cf.Stream("PowerPoint Document");
        // Text lives either in the SlideListWithText (older files) or in each slide's drawing (ClientTextbox inside
        // the SlideContainer). Collect both and use whichever has text.
        var listSlides = new List<List<(int Kind, string Text)>>();
        var drawnSlides = new List<List<(int Kind, string Text)>>();
        List<(int Kind, string Text)>? current = null, drawn = null;
        bool inSlideList = false;
        int lastTextType = 1;
        Walk(0, s.Length, 0);
        var slides = listSlides.Any(x => x.Count > 0) ? listSlides : drawnSlides;
        if (!slides.Any(x => x.Count > 0)) throw new OrangeException("No slide text was found in this presentation.");
        return slides.Where(x => x.Count > 0).Select(x =>
        {
            var title = x.FirstOrDefault(t => t.Kind is 0 or 6).Text ?? x[0].Text;
            var lines = x.Where(t => t.Text != title).SelectMany(t => t.Text.Split('\r', '\v')).Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
            return new SlideText(title.Replace('\r', ' ').Replace('\v', ' ').Trim(), lines);
        }).ToList();

        void Walk(int start, int end, int depth)
        {
            if (depth > 64) return;
            int p = start;
            while (p + 8 <= end)
            {
                ushort verInst = BitConverter.ToUInt16(s, p);
                ushort type = BitConverter.ToUInt16(s, p + 2);
                uint len = BitConverter.ToUInt32(s, p + 4);
                int body = p + 8;
                if (len > end - body) return;   // damaged: stop this level
                int ver = verInst & 0xF, inst = verInst >> 4;
                if (type == 0x0FF0)   // SlideListWithText
                {
                    bool was = inSlideList; inSlideList = inst == 0;   // 0 = slides, 1 = masters, 2 = notes
                    Walk(body, body + (int)len, depth + 1);
                    inSlideList = was;
                }
                else if (type == 0x03EE)   // SlideContainer
                {
                    var was = drawn; drawn = new(); drawnSlides.Add(drawn);
                    Walk(body, body + (int)len, depth + 1);
                    drawn = was;
                }
                else if (type is 0x03F8 or 0x0FF0 or 0x03F0) { /* MainMaster / notes: not slide content */ }
                else if (ver == 0xF) Walk(body, body + (int)len, depth + 1);
                else
                {
                    var target = inSlideList ? current : drawn;
                    switch (type)
                    {
                        case 0x03F3 when inSlideList: current = new(); listSlides.Add(current); break;   // SlidePersistAtom
                        case 0x0F9F: if (len >= 4) lastTextType = BitConverter.ToInt32(s, body); break;  // TextHeaderAtom
                        case 0x0FA0: target?.Add((lastTextType, Encoding.Unicode.GetString(s, body, (int)len & ~1))); break;   // TextCharsAtom
                        case 0x0FA8: target?.Add((lastTextType, Encoding.Latin1.GetString(s, body, (int)len))); break;          // TextBytesAtom
                    }
                }
                p = body + (int)len;
            }
        }
    }

    public static DocModel ToModel(List<SlideText> slides, string title)
    {
        var m = new DocModel { Title = title };
        for (int i = 0; i < slides.Count; i++)
        {
            if (i > 0) m.Add(new PageBreak());
            m.Add(new Heading(1, slides[i].Title.Length > 0 ? slides[i].Title : $"Slide {i + 1}"));
            foreach (var l in slides[i].Lines) m.Add(new ListItem([new Run(l)], false));
        }
        return m;
    }
}
