namespace Orange.Core;

/// <summary>
/// Decides what a file is. The content signature wins over the extension so a renamed or hostile file
/// (e.g. an SVG/MVG script renamed to .png) is never handed to a decoder that expects something else.
/// </summary>
public static class FileTypeDetector
{
    public static readonly IReadOnlyDictionary<string, FileKind> ExtensionKinds = new Dictionary<string, FileKind>(StringComparer.OrdinalIgnoreCase)
    {
        // images
        ["png"] = FileKind.Image, ["jpg"] = FileKind.Image, ["jpeg"] = FileKind.Image, ["webp"] = FileKind.Image,
        ["heic"] = FileKind.Image, ["heif"] = FileKind.Image, ["tif"] = FileKind.Image, ["tiff"] = FileKind.Image,
        ["bmp"] = FileKind.Image, ["gif"] = FileKind.Image, ["avif"] = FileKind.Image, ["ico"] = FileKind.Image, ["svg"] = FileKind.Image,
        // video
        ["mp4"] = FileKind.Video, ["m4v"] = FileKind.Video, ["mov"] = FileKind.Video, ["mkv"] = FileKind.Video,
        ["webm"] = FileKind.Video, ["avi"] = FileKind.Video, ["wmv"] = FileKind.Video,
        // audio
        ["mp3"] = FileKind.Audio, ["wav"] = FileKind.Audio, ["flac"] = FileKind.Audio, ["aac"] = FileKind.Audio,
        ["m4a"] = FileKind.Audio, ["ogg"] = FileKind.Audio, ["opus"] = FileKind.Audio, ["aiff"] = FileKind.Audio,
        ["aif"] = FileKind.Audio, ["wma"] = FileKind.Audio,
        // pdf
        ["pdf"] = FileKind.Pdf,
        // documents
        ["docx"] = FileKind.Document, ["odt"] = FileKind.Document, ["rtf"] = FileKind.Document, ["txt"] = FileKind.Document,
        ["md"] = FileKind.Document, ["markdown"] = FileKind.Document, ["html"] = FileKind.Document, ["htm"] = FileKind.Document,
        ["epub"] = FileKind.Document, ["doc"] = FileKind.Document,
        ["mobi"] = FileKind.Document, ["azw"] = FileKind.Document, ["fb2"] = FileKind.Document, ["cbz"] = FileKind.Document,
        // camera RAW, JPEG XL
        ["dng"] = FileKind.Image, ["cr2"] = FileKind.Image, ["cr3"] = FileKind.Image, ["nef"] = FileKind.Image, ["arw"] = FileKind.Image,
        ["orf"] = FileKind.Image, ["rw2"] = FileKind.Image, ["raf"] = FileKind.Image, ["jxl"] = FileKind.Image,
        // fonts
        ["ttf"] = FileKind.Font, ["otf"] = FileKind.Font, ["woff"] = FileKind.Font, ["woff2"] = FileKind.Font,
        // spreadsheets and presentations
        ["xlsx"] = FileKind.Spreadsheet, ["xls"] = FileKind.Spreadsheet, ["ods"] = FileKind.Spreadsheet,
        ["csv"] = FileKind.Spreadsheet, ["tsv"] = FileKind.Spreadsheet,
        ["pptx"] = FileKind.Presentation, ["ppt"] = FileKind.Presentation,
        // subtitles
        ["srt"] = FileKind.Subtitle, ["vtt"] = FileKind.Subtitle,
        // archives
        ["zip"] = FileKind.Archive, ["tar"] = FileKind.Archive, ["gz"] = FileKind.Archive, ["tgz"] = FileKind.Archive,
        ["rar"] = FileKind.Archive, ["7z"] = FileKind.Archive,
    };

    public static string Ext(string path) => Path.GetExtension(path).TrimStart('.').ToLowerInvariant();

    public static FileKind KindFromExtension(string path) =>
        ExtensionKinds.TryGetValue(Ext(path), out var k) ? k : FileKind.Unknown;

    /// <summary>Signature-based kind. Returns Unknown when the header proves nothing (plain text formats).</summary>
    public static FileKind KindFromContent(ReadOnlySpan<byte> span)
    {
        var h = span.ToArray();
        bool At(int off, params byte[] sig) => h.Length >= off + sig.Length && h.AsSpan(off, sig.Length).SequenceEqual(sig);
        bool Ascii(int off, string s) => At(off, System.Text.Encoding.ASCII.GetBytes(s));

        if (At(0, 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A)) return FileKind.Image;      // PNG
        if (At(0, 0xFF, 0xD8, 0xFF)) return FileKind.Image;                                     // JPEG
        if (Ascii(0, "GIF87a") || Ascii(0, "GIF89a")) return FileKind.Image;
        if (Ascii(0, "BM")) return FileKind.Image;
        if (At(0, 0x49, 0x49, 0x2A, 0x00) || At(0, 0x4D, 0x4D, 0x00, 0x2A)) return FileKind.Image; // TIFF
        if (At(0, 0x00, 0x00, 0x01, 0x00)) return FileKind.Image;                               // ICO
        if (Ascii(0, "RIFF") && Ascii(8, "WEBP")) return FileKind.Image;
        if (Ascii(0, "RIFF") && Ascii(8, "WAVE")) return FileKind.Audio;
        if (Ascii(0, "RIFF") && Ascii(8, "AVI ")) return FileKind.Video;
        if (Ascii(4, "ftyp") && Ascii(8, "crx ")) return FileKind.Image;                       // Canon CR3
        if (Ascii(0, "FUJIFILMCCD-RAW")) return FileKind.Image;                                 // RAF
        if (Ascii(0, "IIRO") || Ascii(0, "IIRS") || Ascii(0, "MMOR") || At(0, 0x49, 0x49, 0x55, 0x00)) return FileKind.Image; // ORF, RW2
        if (At(0, 0xFF, 0x0A) || At(0, 0, 0, 0, 0x0C, 0x4A, 0x58, 0x4C, 0x20)) return FileKind.Image; // JPEG XL
        if (Ascii(0, "wOFF") || Ascii(0, "wOF2") || Ascii(0, "OTTO") || At(0, 0, 1, 0, 0) && h.Length >= 12 && h[4] == 0) return FileKind.Font;
        if (Ascii(60, "BOOKMOBI") || Ascii(60, "TEXtREAd")) return FileKind.Document;            // MOBI
        if (Ascii(4, "ftyp"))
        {
            var brand = h.Length >= 12 ? System.Text.Encoding.ASCII.GetString(h, 8, 4) : "";
            if (brand is "heic" or "heix" or "mif1" or "msf1" or "heim" or "heis" or "avif" or "avis") return FileKind.Image;
            if (brand.StartsWith("M4A") || brand.StartsWith("M4B")) return FileKind.Audio;
            return FileKind.Video;                                                              // mp4 / mov / m4v (may be audio-only; ffprobe decides)
        }
        if (At(4, 0x6D, 0x6F, 0x6F, 0x76) || Ascii(4, "mdat") || Ascii(4, "wide") || Ascii(4, "free")) return FileKind.Video; // old QuickTime
        if (At(0, 0x1A, 0x45, 0xDF, 0xA3)) return FileKind.Video;                               // Matroska / WebM
        if (At(0, 0x30, 0x26, 0xB2, 0x75, 0x8E, 0x66, 0xCF, 0x11)) return FileKind.Video;       // ASF (wmv/wma; ext disambiguates)
        if (Ascii(0, "ID3") || (h.Length > 1 && h[0] == 0xFF && (h[1] & 0xE0) == 0xE0)) return FileKind.Audio; // MP3 / ADTS
        if (Ascii(0, "fLaC") || Ascii(0, "OggS") || Ascii(0, "FORM")) return FileKind.Audio;
        if (Ascii(0, "%PDF-")) return FileKind.Pdf;
        if (At(0, 0x50, 0x4B, 0x03, 0x04) || At(0, 0x50, 0x4B, 0x05, 0x06)) return FileKind.Archive; // zip container (docx/epub/odt too)
        if (At(0, 0x1F, 0x8B)) return FileKind.Archive;
        if (Ascii(0, "Rar!\x1A\x07")) return FileKind.Archive;                                   // RAR 4/5
        if (At(0, 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C)) return FileKind.Archive;                  // 7z
        if (h.Length >= 262 && Ascii(257, "ustar")) return FileKind.Archive;
        if (Ascii(0, "{\\rtf")) return FileKind.Document;
        return FileKind.Unknown;
    }

    /// <summary>
    /// The kind Orange will treat the file as, or Unknown when the content contradicts the extension.
    /// Container formats (docx/epub/odt are zips, wma is ASF, m4a is MP4) are reconciled with the extension.
    /// </summary>
    public static FileKind Detect(string path)
    {
        var byExt = KindFromExtension(path);
        Span<byte> head = stackalloc byte[512];
        int n;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.None))
            n = fs.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        var byContent = KindFromContent(head[..n]);

        // Legacy Office (OLE compound file): only the extension can say which kind; LibreOffice/ExcelDataReader validate further.
        if (n >= 8 && head[..8].SequenceEqual(new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 }))
            return Ext(path) is "xls" or "ppt" or "doc" ? byExt : FileKind.Unknown;
        if (byContent == FileKind.Unknown)
        {
            // Text-based formats have no signature. Only trust the extension for those, and only if it looks like text.
            if ((byExt is FileKind.Document or FileKind.Subtitle || Ext(path) is "csv" or "tsv") && Ext(path) is not ("doc" or "xls" or "ppt") && LooksLikeText(head[..n]))
                return byExt;
            if (Ext(path) == "svg" && LooksLikeText(head[..n]) && SvgSafety.LooksLikeSvg(path)) return FileKind.Image;
            if (Ext(path) == "mp3" && n > 0) return FileKind.Audio;   // raw MPEG frames without ID3 sync quirks; ffprobe validates
            return FileKind.Unknown;
        }
        if (byContent == byExt) return byExt;
        // Reconcile containers.
        if (byContent == FileKind.Archive && byExt == FileKind.Document && Ext(path) is "docx" or "epub" or "odt") return FileKind.Document;
        if (byContent == FileKind.Archive && Ext(path) is "xlsx" or "ods") return FileKind.Spreadsheet;
        if (byContent == FileKind.Archive && Ext(path) is "cbz") return FileKind.Document;
        if (byContent == FileKind.Archive && Ext(path) is "pptx") return FileKind.Presentation;
        if (byContent == FileKind.Video && byExt == FileKind.Audio && Ext(path) is "m4a" or "wma" or "aac") return FileKind.Audio;
        if (byContent == FileKind.Audio && byExt == FileKind.Video) return FileKind.Audio; // e.g. audio-only file named .mp4 → treat as audio
        if (byContent == FileKind.Video && byExt == FileKind.Image && Ext(path) is "gif") return FileKind.Image;
        if (byExt == FileKind.Unknown) return byContent;               // no/odd extension: content decides
        return FileKind.Unknown;                                        // mismatch → refuse rather than guess
    }

    private static bool LooksLikeText(ReadOnlySpan<byte> h)
    {
        if (h.Length == 0) return true;
        int ctrl = 0;
        foreach (var b in h) if (b == 0) return false; else if (b < 0x09 || (b > 0x0D && b < 0x20)) ctrl++;
        return ctrl < h.Length / 20 + 1;
    }
}
