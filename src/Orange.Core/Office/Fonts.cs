using System.Buffers.Binary;
using System.IO.Compression;

namespace Orange.Core.Office;

/// <summary>
/// Font containers: TTF/OTF (sfnt) ↔ WOFF (zlib per table) and → WOFF2 (Brotli, null transforms, which the WOFF2
/// spec allows for every table). WOFF2 input is read when its glyf/loca use the null transform; transformed glyph
/// data is refused with a message. Outlines are never changed: a TTF stays TrueType, an OTF stays CFF.
/// </summary>
public static class Fonts
{
    public sealed record Table(string Tag, uint Checksum, byte[] Data);
    public sealed record Sfnt(uint Flavor, List<Table> Tables)
    {
        public bool IsCff => Flavor == 0x4F54544F;   // 'OTTO'
        public string DesktopExtension => IsCff ? "otf" : "ttf";
    }

    private const int MaxTables = 512;
    private const long MaxFontBytes = 256L * 1024 * 1024;

    public static string Kind(ReadOnlySpan<byte> head) =>
        head.Length < 4 ? "" :
        head[..4].SequenceEqual("wOFF"u8) ? "woff" :
        head[..4].SequenceEqual("wOF2"u8) ? "woff2" :
        head[..4].SequenceEqual("OTTO"u8) ? "otf" :
        head[..4].SequenceEqual(new byte[] { 0, 1, 0, 0 }) || head[..4].SequenceEqual("true"u8) ? "ttf" : "";

    public static Sfnt Read(byte[] data) => Kind(data) switch
    {
        "ttf" or "otf" => ReadSfnt(data),
        "woff" => ReadWoff(data),
        "woff2" => ReadWoff2(data),
        _ => throw new OrangeException("This isn't a TTF, OTF, WOFF or WOFF2 font."),
    };

    private static uint U32(byte[] b, int o) { if (o < 0 || o + 4 > b.Length) throw Bad(); return BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(o)); }
    private static ushort U16(byte[] b, int o) { if (o < 0 || o + 2 > b.Length) throw Bad(); return BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(o)); }
    private static OrangeException Bad() => new("The font file looks damaged.");
    private static byte[] Slice(byte[] b, long off, long len) { if (off < 0 || len < 0 || off + len > b.Length || len > MaxFontBytes) throw Bad(); return b.AsSpan((int)off, (int)len).ToArray(); }

    public static Sfnt ReadSfnt(byte[] d)
    {
        uint flavor = U32(d, 0); int n = U16(d, 4);
        if (n is 0 or > MaxTables) throw Bad();
        var tables = new List<Table>();
        for (int i = 0; i < n; i++)
        {
            int rec = 12 + i * 16;
            var tag = System.Text.Encoding.ASCII.GetString(d, rec, 4);
            tables.Add(new Table(tag, U32(d, rec + 4), Slice(d, U32(d, rec + 8), U32(d, rec + 12))));
        }
        return new Sfnt(flavor, tables);
    }

    public static byte[] WriteSfnt(Sfnt f)
    {
        var tables = f.Tables.OrderBy(t => t.Tag, StringComparer.Ordinal).ToList();
        int n = tables.Count;
        int pow = 1, log = 0; while (pow * 2 <= n) { pow *= 2; log++; }
        using var ms = new MemoryStream();
        Span<byte> hdr = stackalloc byte[12];
        BinaryPrimitives.WriteUInt32BigEndian(hdr, f.Flavor); BinaryPrimitives.WriteUInt16BigEndian(hdr[4..], (ushort)n);
        BinaryPrimitives.WriteUInt16BigEndian(hdr[6..], (ushort)(pow * 16)); BinaryPrimitives.WriteUInt16BigEndian(hdr[8..], (ushort)log);
        BinaryPrimitives.WriteUInt16BigEndian(hdr[10..], (ushort)(n * 16 - pow * 16));
        ms.Write(hdr);
        long offset = 12 + n * 16;
        var dir = new byte[n * 16];
        for (int i = 0; i < n; i++)
        {
            var t = tables[i];
            System.Text.Encoding.ASCII.GetBytes(t.Tag.PadRight(4)[..4], dir.AsSpan(i * 16));
            BinaryPrimitives.WriteUInt32BigEndian(dir.AsSpan(i * 16 + 4), Checksum(t.Data));
            BinaryPrimitives.WriteUInt32BigEndian(dir.AsSpan(i * 16 + 8), (uint)offset);
            BinaryPrimitives.WriteUInt32BigEndian(dir.AsSpan(i * 16 + 12), (uint)t.Data.Length);
            offset += (t.Data.Length + 3) & ~3;
        }
        ms.Write(dir);
        foreach (var t in tables) { ms.Write(t.Data); ms.Write(new byte[((t.Data.Length + 3) & ~3) - t.Data.Length]); }
        var font = ms.ToArray();
        // head.checkSumAdjustment = 0xB1B0AFBA − checksum(whole font with the field zeroed).
        int hi = tables.FindIndex(t => t.Tag == "head");
        if (hi >= 0 && tables[hi].Data.Length >= 12)
        {
            int headOff = (int)BinaryPrimitives.ReadUInt32BigEndian(font.AsSpan(12 + hi * 16 + 8));
            BinaryPrimitives.WriteUInt32BigEndian(font.AsSpan(headOff + 8), 0);
            BinaryPrimitives.WriteUInt32BigEndian(font.AsSpan(12 + hi * 16 + 4), Checksum(font.AsSpan(headOff, tables[hi].Data.Length).ToArray()));
            BinaryPrimitives.WriteUInt32BigEndian(font.AsSpan(headOff + 8), unchecked(0xB1B0AFBA - Checksum(font)));
        }
        return font;
    }

    public static uint Checksum(byte[] data)
    {
        uint sum = 0;
        for (int i = 0; i < data.Length; i += 4)
        {
            uint v = 0;
            for (int k = 0; k < 4; k++) v = v << 8 | (uint)(i + k < data.Length ? data[i + k] : 0);
            sum += v;
        }
        return sum;
    }

    // ---------------- WOFF 1

    public static Sfnt ReadWoff(byte[] d)
    {
        uint flavor = U32(d, 4); int n = U16(d, 12);
        if (n is 0 or > MaxTables) throw Bad();
        var tables = new List<Table>();
        for (int i = 0; i < n; i++)
        {
            int rec = 44 + i * 20;
            var tag = System.Text.Encoding.ASCII.GetString(d, rec, 4);
            uint off = U32(d, rec + 4), comp = U32(d, rec + 8), orig = U32(d, rec + 12), sum = U32(d, rec + 16);
            if (orig > MaxFontBytes || comp > orig) throw Bad();
            var raw = Slice(d, off, comp);
            byte[] data;
            if (comp == orig) data = raw;
            else
            {
                using var z = new ZLibStream(new MemoryStream(raw), CompressionMode.Decompress);
                data = new byte[orig];
                int read = 0, got;
                while (read < data.Length && (got = z.Read(data, read, data.Length - read)) > 0) read += got;
                if (read != orig || z.ReadByte() != -1) throw Bad();
            }
            tables.Add(new Table(tag, sum, data));
        }
        return new Sfnt(flavor, tables);
    }

    public static byte[] WriteWoff(Sfnt f)
    {
        var tables = f.Tables.OrderBy(t => t.Tag, StringComparer.Ordinal).ToList();
        int n = tables.Count;
        var blobs = tables.Select(t =>
        {
            using var ms = new MemoryStream();
            using (var z = new ZLibStream(ms, CompressionLevel.SmallestSize, leaveOpen: true)) z.Write(t.Data);
            var c = ms.ToArray();
            return c.Length < t.Data.Length ? c : t.Data;
        }).ToList();
        long offset = 44 + n * 20;
        using var outMs = new MemoryStream();
        var header = new byte[44 + n * 20];
        "wOFF"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), f.Flavor);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(12), (ushort)n);
        uint sfntSize = (uint)(12 + n * 16 + tables.Sum(t => (t.Data.Length + 3) & ~3));
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(16), sfntSize);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(20), 1);   // version 1.0
        for (int i = 0; i < n; i++)
        {
            int rec = 44 + i * 20;
            System.Text.Encoding.ASCII.GetBytes(tables[i].Tag.PadRight(4)[..4], header.AsSpan(rec));
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(rec + 4), (uint)offset);
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(rec + 8), (uint)blobs[i].Length);
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(rec + 12), (uint)tables[i].Data.Length);
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(rec + 16), Checksum(tables[i].Data));
            offset += (blobs[i].Length + 3) & ~3;
        }
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(8), (uint)offset);   // total length
        outMs.Write(header);
        foreach (var b in blobs) { outMs.Write(b); outMs.Write(new byte[((b.Length + 3) & ~3) - b.Length]); }
        return outMs.ToArray();
    }

    // ---------------- WOFF 2

    private static readonly string[] KnownTags =
    [
        "cmap", "head", "hhea", "hmtx", "maxp", "name", "OS/2", "post", "cvt ", "fpgm", "glyf", "loca", "prep", "CFF ", "VORG", "EBDT",
        "EBLC", "gasp", "hdmx", "kern", "LTSH", "PCLT", "VDMX", "vhea", "vmtx", "BASE", "GDEF", "GPOS", "GSUB", "EBSC", "JSTF", "MATH",
        "CBDT", "CBLC", "COLR", "CPAL", "SVG ", "sbix", "acnt", "avar", "bdat", "bloc", "bsln", "cvar", "fdsc", "feat", "fmtx", "fvar",
        "gvar", "hsty", "just", "lcar", "mort", "morx", "opbd", "prop", "trak", "Zapf", "Silf", "Glat", "Gloc", "Feat", "Sill",
    ];

    public static byte[] WriteWoff2(Sfnt f)
    {
        var tables = f.Tables.OrderBy(t => t.Tag, StringComparer.Ordinal).ToList();
        using var dir = new MemoryStream();
        foreach (var t in tables)
        {
            int idx = Array.IndexOf(KnownTags, t.Tag);
            // Transform version: glyf/loca use 3 (null transform), every other table uses 0 (null). Bits 6-7 hold it.
            int version = t.Tag is "glyf" or "loca" ? 3 : 0;
            dir.WriteByte((byte)((idx >= 0 ? idx : 63) | version << 6));
            if (idx < 0) dir.Write(System.Text.Encoding.ASCII.GetBytes(t.Tag.PadRight(4)[..4]));
            WriteBase128(dir, (uint)t.Data.Length);
        }
        using var raw = new MemoryStream();
        foreach (var t in tables) raw.Write(t.Data);
        byte[] compressed;
        using (var cms = new MemoryStream())
        {
            using (var b = new BrotliStream(cms, CompressionLevel.SmallestSize, leaveOpen: true)) b.Write(raw.ToArray());
            compressed = cms.ToArray();
        }
        var header = new byte[48];
        "wOF2"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), f.Flavor);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(12), (ushort)tables.Count);
        uint sfntSize = (uint)(12 + tables.Count * 16 + tables.Sum(t => (t.Data.Length + 3) & ~3));
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(16), sfntSize);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(20), (uint)compressed.Length);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(24), 1);
        long total = 48 + dir.Length + compressed.Length;
        total = (total + 3) & ~3;
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(8), (uint)total);
        using var o = new MemoryStream();
        o.Write(header); o.Write(dir.ToArray()); o.Write(compressed);
        while (o.Length % 4 != 0) o.WriteByte(0);
        return o.ToArray();
    }

    public static Sfnt ReadWoff2(byte[] d)
    {
        uint flavor = U32(d, 4); int n = U16(d, 12);
        if (n is 0 or > MaxTables) throw Bad();
        if (U16(d, 14) != 0 && false) throw Bad();
        uint compLen = U32(d, 20);
        int pos = 48;
        var entries = new List<(string Tag, int Version, uint Length)>();
        for (int i = 0; i < n; i++)
        {
            if (pos >= d.Length) throw Bad();
            byte flags = d[pos++];
            int idx = flags & 63, version = flags >> 6;
            string tag = idx == 63 ? System.Text.Encoding.ASCII.GetString(Slice(d, pos, 4)) : idx < KnownTags.Length ? KnownTags[idx] : throw Bad();
            if (idx == 63) pos += 4;
            uint origLen = ReadBase128(d, ref pos);
            bool transformed = tag is "glyf" or "loca" ? version != 3 : version != 0;
            if (transformed) throw new OrangeException("This WOFF2 uses compressed glyph tables that Orange can't unpack yet.", "Most web-font tools write this form; Orange reads WOFF2 files that use the plain (null-transform) layout.");
            entries.Add((tag, version, origLen));
        }
        if (flavor == 0x74746366) throw new OrangeException("WOFF2 font collections aren't supported.");
        var compressed = Slice(d, pos, compLen);
        long expected = entries.Sum(e => (long)e.Length);
        if (expected > MaxFontBytes) throw Bad();
        var all = new byte[expected];
        using (var b = new BrotliStream(new MemoryStream(compressed), CompressionMode.Decompress))
        {
            int read = 0, got;
            while (read < all.Length && (got = b.Read(all, read, all.Length - read)) > 0) read += got;
            if (read != expected) throw Bad();
        }
        var tables = new List<Table>(); long off = 0;
        foreach (var e in entries) { var data = all.AsSpan((int)off, (int)e.Length).ToArray(); off += e.Length; tables.Add(new Table(e.Tag, Checksum(data), data)); }
        return new Sfnt(flavor, tables);
    }

    private static void WriteBase128(Stream s, uint v)
    {
        Span<byte> buf = stackalloc byte[5]; int n = 0;
        do { buf[4 - n++] = (byte)(v & 0x7F); v >>= 7; } while (v != 0);
        for (int i = 5 - n; i < 5; i++) s.WriteByte((byte)(buf[i] | (i < 4 ? 0x80 : 0)));
    }

    private static uint ReadBase128(byte[] d, ref int pos)
    {
        uint acc = 0;
        for (int i = 0; i < 5; i++)
        {
            if (pos >= d.Length) throw Bad();
            byte b = d[pos++];
            if (i == 0 && b == 0x80) throw Bad();
            if ((acc & 0xFE000000) != 0) throw Bad();
            acc = acc << 7 | (uint)(b & 0x7F);
            if ((b & 0x80) == 0) return acc;
        }
        throw Bad();
    }
}
