using ImageMagick;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;

namespace Orange.Core.Office;

/// <summary>
/// PDF compression without Ghostscript: re-encodes the pictures inside the PDF (JPEG at a quality and maximum size per
/// level) and saves with object compression. Text, fonts and vectors are untouched, so the result looks the same apart
/// from picture quality. Ghostscript, when installed, is still used because it also subsets fonts.
/// </summary>
public static class PdfCompress
{
    public static (int Quality, uint MaxSide) Level(string level) => level switch
    {
        "printer" => (85, 3000),
        "screen" => (50, 1100),
        "tiny" => (35, 800),
        _ => (70, 1700),   // ebook
    };

    /// <summary>Returns the number of pictures re-encoded.</summary>
    public static int Compress(string input, string output, string level, CancellationToken ct = default)
    {
        var (quality, maxSide) = Level(level);
        PdfDocument doc;
        try { doc = PdfReader.Open(input, PdfDocumentOpenMode.Modify); }
        catch (Exception e) { throw new OrangeException("This PDF can't be opened for compression (it may be encrypted or damaged).", e.Message); }
        int changed = 0;
        var seen = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        using (doc)
        {
            foreach (var page in doc.Pages)
            {
                ct.ThrowIfCancellationRequested();
                var xobjects = page.Resources?.Elements.GetDictionary("/XObject");
                if (xobjects is null) continue;
                foreach (var key in xobjects.Elements.Keys.ToList())
                {
                    if ((xobjects.Elements[key] as PdfReference)?.Value is not PdfDictionary img || !seen.Add(img)) continue;
                    if (img.Elements.GetName("/Subtype") != "/Image" || img.Stream is null) continue;
                    try { if (Recompress(img, quality, maxSide)) changed++; }
                    catch { /* leave a picture we can't re-encode as it was */ }
                }
            }
            doc.Options.CompressContentStreams = true;
            doc.Options.UseFlateDecoderForJpegImages = PdfUseFlateDecoderForJpegImages.Never;
            doc.Options.NoCompression = false;
            doc.Save(output);
        }
        return changed;
    }

    private static bool Recompress(PdfDictionary img, int quality, uint maxSide)
    {
        if (img.Elements.ContainsKey("/SMask") || img.Elements.ContainsKey("/Mask") || img.Elements.GetBoolean("/ImageMask")) return false;
        var filter = img.Elements["/Filter"] switch { PdfName n => n.Value, PdfArray a when a.Elements.Count == 1 => a.Elements[0].ToString(), _ => null };
        int w = img.Elements.GetInteger("/Width"), h = img.Elements.GetInteger("/Height");
        if (w < 64 || h < 64) return false;
        var raw = img.Stream.Value;
        MagickImage pic;
        if (filter == "/DCTDecode")
            pic = new MagickImage(raw, MagickFormat.Jpeg);
        else if (filter == "/FlateDecode" && img.Elements.GetInteger("/BitsPerComponent") == 8 && img.Elements["/DecodeParms"] is null)
        {
            var cs = img.Elements.GetName("/ColorSpace");
            int channels = cs switch { "/DeviceRGB" => 3, "/DeviceGray" => 1, _ => 0 };
            if (channels == 0) return false;
            if (!img.Stream.TryUncompress()) return false;
            var bytes = img.Stream.Value;
            if (bytes.Length < (long)w * h * channels) return false;
            pic = new MagickImage(bytes, new MagickReadSettings { Width = (uint)w, Height = (uint)h, Format = channels == 3 ? MagickFormat.Rgb : MagickFormat.Gray, Depth = 8 });
        }
        else return false;
        using (pic)
        {
            if (pic.ColorSpace == ColorSpace.CMYK) return false;   // keep print CMYK pictures exactly
            if (Math.Max(pic.Width, pic.Height) > maxSide) pic.Resize(new MagickGeometry(maxSide, maxSide));
            pic.Strip();
            pic.Quality = (uint)quality;
            var jpeg = pic.ToByteArray(MagickFormat.Jpeg);
            if (jpeg.Length >= raw.Length * 0.95) return false;   // not worth it
            img.Stream.Value = jpeg;
            img.Elements.SetName("/Filter", "/DCTDecode");
            img.Elements.Remove("/DecodeParms");
            img.Elements.SetInteger("/Width", (int)pic.Width);
            img.Elements.SetInteger("/Height", (int)pic.Height);
            img.Elements.SetInteger("/BitsPerComponent", 8);
            img.Elements.SetName("/ColorSpace", pic.ChannelCount >= 3 ? "/DeviceRGB" : "/DeviceGray");
            img.Elements.SetInteger("/Length", jpeg.Length);
            return true;
        }
    }
}
