using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Orange.Core;

/// <summary>
/// Removes metadata from JPEG and PNG files without re-encoding: the compressed image data is copied byte for byte,
/// so quality and file size never get worse. The result is checked by hashing the image data before and after.
///
/// JPEG: drops APP1 (EXIF, XMP), APP13 (IPTC/Photoshop), APP3–APP12, APP15 and COM segments. APP0 (JFIF) and APP14
/// (Adobe colour transform) are kept because decoders need them; APP2 ICC_PROFILE is kept when asked. EXIF
/// orientation is preserved by writing a minimal EXIF block that holds only the Orientation tag, so the photo
/// doesn't turn sideways.
/// PNG: drops tEXt, zTXt, iTXt, eXIf and tIME chunks (and iCCP unless kept). Critical chunks are copied unchanged.
/// </summary>
public static class LosslessMetadata
{
    public sealed record Result(byte[] Bytes, int RemovedBlocks, long RemovedBytes);

    public static bool Supports(string ext) => ext is "jpg" or "png";

    public static Result Strip(string path, bool keepColorProfile)
    {
        var data = File.ReadAllBytes(path);
        var ext = OperationRegistry.Normalize(FileTypeDetector.Ext(path));
        var r = ext == "jpg" ? StripJpeg(data, keepColorProfile) : StripPng(data, keepColorProfile);
        if (!ImageDataHash(data, ext).AsSpan().SequenceEqual(ImageDataHash(r.Bytes, ext)))
            throw new OrangeException("The cleaned image failed its check, so it wasn't saved.", "Image data changed during metadata removal.");
        return r;
    }

    // ------------------------------------------------------------------ JPEG

    public static Result StripJpeg(byte[] d, bool keepIcc)
    {
        if (d.Length < 4 || d[0] != 0xFF || d[1] != 0xD8) throw new OrangeException("This JPEG is damaged (no start marker).");
        using var o = new MemoryStream(d.Length);
        o.Write(d, 0, 2);
        int p = 2, removed = 0; long removedBytes = 0;
        ushort orientation = 1;
        bool wroteOrientation = false;
        while (p + 4 <= d.Length)
        {
            if (d[p] != 0xFF) throw new OrangeException("This JPEG is damaged (bad marker).");
            byte m = d[p + 1];
            if (m == 0xFF) { p++; continue; }                          // fill byte
            if (m == 0xDA)                                             // start of scan: rest is image data, copied verbatim
            {
                if (orientation != 1 && !wroteOrientation) WriteOrientationApp1(o, orientation);
                o.Write(d, p, d.Length - p);
                break;
            }
            if (m is 0xD8 or 0x01 or (>= 0xD0 and <= 0xD7)) { o.Write(d, p, 2); p += 2; continue; }
            int len = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(p + 2));
            if (len < 2 || p + 2 + len > d.Length) throw new OrangeException("This JPEG is damaged (segment runs past the end).");
            var seg = d.AsSpan(p, 2 + len);
            bool drop = m switch
            {
                0xE0 or 0xEE => false,                                 // JFIF, Adobe
                0xE1 => true,                                          // EXIF / XMP
                0xE2 => !(keepIcc && seg.Length > 16 && seg.Slice(4, 12).SequenceEqual("ICC_PROFILE\0"u8)),
                >= 0xE3 and <= 0xEF => true,
                0xFE => true,                                          // comment
                _ => false,
            };
            if (m == 0xE1 && seg.Length > 10 && seg.Slice(4, 6).SequenceEqual("Exif\0\0"u8)) orientation = ReadOrientation(seg[10..].ToArray());
            if (drop) { removed++; removedBytes += seg.Length; }
            else
            {
                // Keep the orientation right after JFIF/Adobe headers, before the frame header.
                if (orientation != 1 && !wroteOrientation && m is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC) { WriteOrientationApp1(o, orientation); wroteOrientation = true; }
                o.Write(seg);
            }
            p += 2 + len;
        }
        return new Result(o.ToArray(), removed, removedBytes);
    }

    private static ushort ReadOrientation(byte[] tiff)
    {
        try
        {
            if (tiff.Length < 8) return 1;
            bool le = tiff[0] == 'I';
            ushort U16(int o) => le ? BinaryPrimitives.ReadUInt16LittleEndian(tiff.AsSpan(o)) : BinaryPrimitives.ReadUInt16BigEndian(tiff.AsSpan(o));
            uint U32(int o) => le ? BinaryPrimitives.ReadUInt32LittleEndian(tiff.AsSpan(o)) : BinaryPrimitives.ReadUInt32BigEndian(tiff.AsSpan(o));
            int ifd = (int)U32(4);
            if (ifd < 8 || ifd + 2 > tiff.Length) return 1;
            int n = U16(ifd);
            for (int i = 0; i < n && ifd + 2 + i * 12 + 12 <= tiff.Length; i++)
            {
                int e = ifd + 2 + i * 12;
                if (U16(e) == 0x0112) { var v = U16(e + 8); return v is >= 1 and <= 8 ? v : (ushort)1; }
            }
        }
        catch { }
        return 1;
    }

    /// <summary>APP1 "Exif" with a big-endian TIFF header and one IFD entry: Orientation.</summary>
    private static void WriteOrientationApp1(Stream o, ushort orientation)
    {
        var tiff = new byte[8 + 2 + 12 + 4];
        "MM"u8.CopyTo(tiff); BinaryPrimitives.WriteUInt16BigEndian(tiff.AsSpan(2), 42); BinaryPrimitives.WriteUInt32BigEndian(tiff.AsSpan(4), 8);
        BinaryPrimitives.WriteUInt16BigEndian(tiff.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt16BigEndian(tiff.AsSpan(10), 0x0112); BinaryPrimitives.WriteUInt16BigEndian(tiff.AsSpan(12), 3);
        BinaryPrimitives.WriteUInt32BigEndian(tiff.AsSpan(14), 1); BinaryPrimitives.WriteUInt16BigEndian(tiff.AsSpan(18), orientation);
        int len = 2 + 6 + tiff.Length;
        o.Write([0xFF, 0xE1, (byte)(len >> 8), (byte)len]);
        o.Write("Exif\0\0"u8);
        o.Write(tiff);
    }

    // ------------------------------------------------------------------ PNG

    private static readonly byte[] PngSig = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public static Result StripPng(byte[] d, bool keepIcc)
    {
        if (d.Length < 8 || !d.AsSpan(0, 8).SequenceEqual(PngSig)) throw new OrangeException("This PNG is damaged (bad signature).");
        using var o = new MemoryStream(d.Length);
        o.Write(PngSig);
        int p = 8, removed = 0; long removedBytes = 0;
        while (p + 12 <= d.Length)
        {
            int len = (int)BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(p));
            if (len < 0 || p + 12 + (long)len > d.Length) throw new OrangeException("This PNG is damaged (chunk runs past the end).");
            var type = System.Text.Encoding.ASCII.GetString(d, p + 4, 4);
            bool drop = type is "tEXt" or "zTXt" or "iTXt" or "eXIf" or "tIME" || type == "iCCP" && !keepIcc;
            if (drop) { removed++; removedBytes += 12 + len; } else o.Write(d, p, 12 + len);
            p += 12 + len;
            if (type == "IEND") break;
        }
        return new Result(o.ToArray(), removed, removedBytes);
    }

    // ------------------------------------------------------------------ verification

    /// <summary>Hash of only the compressed image data (JPEG frame/tables/scan; PNG IHDR/PLTE/tRNS/IDAT), ignoring metadata.</summary>
    public static byte[] ImageDataHash(byte[] d, string ext)
    {
        using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        if (ext == "jpg")
        {
            int p = 2;
            while (p + 4 <= d.Length)
            {
                byte m = d[p + 1];
                if (d[p] != 0xFF) break;
                if (m == 0xFF) { p++; continue; }
                if (m == 0xDA) { h.AppendData(d, p, d.Length - p); break; }
                if (m is 0xD8 or 0x01 or (>= 0xD0 and <= 0xD7)) { p += 2; continue; }
                int len = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(p + 2));
                if (m is not ((>= 0xE0 and <= 0xEF) or 0xFE)) h.AppendData(d, p, 2 + len);
                p += 2 + len;
            }
        }
        else
        {
            int p = 8;
            while (p + 12 <= d.Length)
            {
                int len = (int)BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(p));
                var type = System.Text.Encoding.ASCII.GetString(d, p + 4, 4);
                if (type is "IHDR" or "PLTE" or "tRNS" or "IDAT" or "IEND") h.AppendData(d, p, 12 + len);
                p += 12 + len;
            }
        }
        return h.GetHashAndReset();
    }
}
