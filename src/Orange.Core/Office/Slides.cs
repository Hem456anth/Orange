using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Presentation;
using PdfSharp.Drawing;
using PdfSharp.Drawing.Layout;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;

namespace Orange.Core.Office;

/// <summary>
/// PPTX → PDF in a simple layout (each text box and picture at its position; no themes, effects or animations) and
/// PDF → PPTX (one full-page picture per slide). Slide images come from rendering that PDF with PDFium.
/// </summary>
public static class Slides
{
    private const double EmuPerPt = 12700;

    public static int PptxToPdf(string pptx, string pdfOut, int maxSlides = 1000)
    {
        OfficeGuard.CheckZip(pptx);
        OrangeFontResolver.Install();
        PresentationDocument doc;
        try { doc = PresentationDocument.Open(pptx, false); }
        catch (Exception e) when (e is OpenXmlPackageException or InvalidDataException or System.Xml.XmlException) { throw new OrangeException("This PPTX looks damaged and can't be opened.", e.Message); }
        using (doc)
        using (var pdf = new PdfSharp.Pdf.PdfDocument())
        {
            var pres = doc.PresentationPart ?? throw new OrangeException("This PPTX has no slides.");
            var size = pres.Presentation?.SlideSize;
            double w = (size?.Cx?.Value ?? 9144000) / EmuPerPt, h = (size?.Cy?.Value ?? 6858000) / EmuPerPt;
            var ids = pres.Presentation?.SlideIdList?.Elements<SlideId>().ToList() ?? new();
            int count = 0;
            foreach (var sid in ids.Take(maxSlides))
            {
                if (sid.RelationshipId?.Value is not { } rid || pres.GetPartById(rid) is not SlidePart sp) continue;
                var page = pdf.AddPage(); page.Width = XUnit.FromPoint(w); page.Height = XUnit.FromPoint(h);
                using var g = XGraphics.FromPdfPage(page);
                g.DrawRectangle(XBrushes.White, 0, 0, w, h);
                DrawTree(g, sp, sp.Slide?.CommonSlideData?.ShapeTree, w, h);
                count++;
            }
            if (count == 0) throw new OrangeException("This presentation has no slides.");
            pdf.Save(pdfOut);
            return count;
        }
    }

    private static void DrawTree(XGraphics g, SlidePart sp, OpenXmlElement? tree, double slideW, double slideH)
    {
        if (tree is null) return;
        foreach (var el in tree.ChildElements)
        {
            switch (el)
            {
                case P.Picture pic:
                {
                    var (x, y, cx, cy) = Box(pic.ShapeProperties?.Transform2D, slideW, slideH);
                    var rid = pic.BlipFill?.Blip?.Embed?.Value;
                    if (rid != null && sp.GetPartById(rid) is ImagePart ip)
                    {
                        try
                        {
                            using var ms = new MemoryStream(); using (var s = ip.GetStream()) s.CopyTo(ms);
                            ms.Position = 0;
                            using var img = XImage.FromStream(ms);
                            g.DrawImage(img, x, y, cx, cy);
                        }
                        catch { g.DrawRectangle(XPens.LightGray, x, y, cx, cy); }
                    }
                    break;
                }
                case P.Shape shape when shape.TextBody is { } body:
                {
                    var (x, y, cx, cy) = Box(shape.ShapeProperties?.Transform2D, slideW, slideH);
                    var isTitle = shape.NonVisualShapeProperties?.ApplicationNonVisualDrawingProperties?.PlaceholderShape?.Type?.Value is { } t && (t == PlaceholderValues.Title || t == PlaceholderValues.CenteredTitle);
                    if (cx <= 0) { x = slideW * 0.06; cx = slideW * 0.88; y = isTitle ? slideH * 0.05 : slideH * 0.25; cy = isTitle ? slideH * 0.15 : slideH * 0.7; }
                    double cursor = y;
                    foreach (var para in body.Elements<A.Paragraph>())
                    {
                        var text = string.Concat(para.Elements<A.Run>().Select(r => r.Text?.Text ?? ""));
                        if (text.Length == 0) { cursor += 8; continue; }
                        var firstRun = para.Elements<A.Run>().FirstOrDefault()?.RunProperties;
                        double pt = (firstRun?.FontSize?.Value ?? (isTitle ? 3600 : 1800)) / 100.0;
                        bool bold = firstRun?.Bold?.Value ?? isTitle;
                        var font = new XFont("Orange", pt, bold ? XFontStyleEx.Bold : XFontStyleEx.Regular);
                        var bullet = !isTitle && para.ParagraphProperties?.GetFirstChild<A.NoBullet>() is null && para.ParagraphProperties?.Level?.Value is not null ? "• " : "";
                        var lines = Math.Max(1, (int)Math.Ceiling(g.MeasureString(bullet + text, font).Width / Math.Max(1, cx)));
                        var height = lines * pt * 1.25;
                        if (cursor + height > slideH) break;
                        new XTextFormatter(g).DrawString(bullet + text, font, XBrushes.Black, new XRect(x, cursor, cx, height), XStringFormats.TopLeft);
                        cursor += height + pt * 0.3;
                    }
                    break;
                }
                case P.GroupShape grp: DrawTree(g, sp, grp, slideW, slideH); break;
            }
        }
    }

    private static (double X, double Y, double W, double H) Box(A.Transform2D? t, double slideW, double slideH)
    {
        if (t?.Offset is null || t.Extents is null) return (0, 0, 0, 0);
        double x = (t.Offset.X?.Value ?? 0) / EmuPerPt, y = (t.Offset.Y?.Value ?? 0) / EmuPerPt;
        double w = (t.Extents.Cx?.Value ?? 0) / EmuPerPt, h = (t.Extents.Cy?.Value ?? 0) / EmuPerPt;
        return (Math.Clamp(x, -slideW, slideW * 2), Math.Clamp(y, -slideH, slideH * 2), Math.Clamp(w, 0, slideW * 3), Math.Clamp(h, 0, slideH * 3));
    }

    /// <summary>Builds a PPTX with one picture per slide, each filling a 16:9 or 4:3 slide matched to the first page.</summary>
    public static void PicturesToPptx(IReadOnlyList<byte[]> pngs, string path)
    {
        if (pngs.Count == 0) throw new OrangeException("There are no pages to put on slides.");
        var first = new ImageMagick.MagickImageInfo(pngs[0]);
        long cx = 12192000, cy = (long)(cx * (double)first.Height / first.Width);
        cy = Math.Clamp(cy, 914400, 51206400);

        using var doc = PresentationDocument.Create(path, PresentationDocumentType.Presentation);
        var pres = doc.AddPresentationPart();
        pres.Presentation = new Presentation();
        var master = pres.AddNewPart<SlideMasterPart>("rIdMaster");
        var layout = master.AddNewPart<SlideLayoutPart>("rIdLayout");
        layout.SlideLayout = new SlideLayout(new CommonSlideData(EmptyTree()), new ColorMapOverride(new A.MasterColorMapping())) { Type = SlideLayoutValues.Blank };
        var theme = master.AddNewPart<ThemePart>("rIdTheme");
        theme.Theme = MinimalTheme();
        master.SlideMaster = new SlideMaster(new CommonSlideData(EmptyTree()),
            new P.ColorMap { Background1 = A.ColorSchemeIndexValues.Light1, Text1 = A.ColorSchemeIndexValues.Dark1, Background2 = A.ColorSchemeIndexValues.Light2, Text2 = A.ColorSchemeIndexValues.Dark2,
                Accent1 = A.ColorSchemeIndexValues.Accent1, Accent2 = A.ColorSchemeIndexValues.Accent2, Accent3 = A.ColorSchemeIndexValues.Accent3, Accent4 = A.ColorSchemeIndexValues.Accent4,
                Accent5 = A.ColorSchemeIndexValues.Accent5, Accent6 = A.ColorSchemeIndexValues.Accent6, Hyperlink = A.ColorSchemeIndexValues.Hyperlink, FollowedHyperlink = A.ColorSchemeIndexValues.FollowedHyperlink },
            new SlideLayoutIdList(new SlideLayoutId { Id = 2147483649U, RelationshipId = "rIdLayout" }));
        var slideIds = new SlideIdList();
        uint sid = 256;
        int n = 0;
        foreach (var png in pngs)
        {
            var slidePart = pres.AddNewPart<SlidePart>($"rIdSlide{++n}");
            slidePart.AddPart(layout);
            var img = slidePart.AddImagePart(ImagePartType.Png);
            using (var ms = new MemoryStream(png)) img.FeedData(ms);
            var rid = slidePart.GetIdOfPart(img);
            var tree = EmptyTree();
            tree.Append(new P.Picture(
                new P.NonVisualPictureProperties(new P.NonVisualDrawingProperties { Id = 2, Name = $"Page {n}" }, new P.NonVisualPictureDrawingProperties(new A.PictureLocks { NoChangeAspect = true }), new ApplicationNonVisualDrawingProperties()),
                new P.BlipFill(new A.Blip { Embed = rid }, new A.Stretch(new A.FillRectangle())),
                new P.ShapeProperties(new A.Transform2D(new A.Offset { X = 0, Y = 0 }, new A.Extents { Cx = cx, Cy = cy }), new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle })));
            slidePart.Slide = new Slide(new CommonSlideData(tree), new ColorMapOverride(new A.MasterColorMapping()));
            slideIds.Append(new SlideId { Id = sid++, RelationshipId = $"rIdSlide{n}" });
        }
        pres.Presentation.Append(new SlideMasterIdList(new SlideMasterId { Id = 2147483648U, RelationshipId = "rIdMaster" }), slideIds,
            new SlideSize { Cx = (int)cx, Cy = (int)cy }, new NotesSize { Cx = 6858000, Cy = 9144000 }, new DefaultTextStyle());
    }

    private static ShapeTree EmptyTree() => new(
        new P.NonVisualGroupShapeProperties(new P.NonVisualDrawingProperties { Id = 1, Name = "" }, new P.NonVisualGroupShapeDrawingProperties(), new ApplicationNonVisualDrawingProperties()),
        new GroupShapeProperties(new A.TransformGroup()));

    private static A.Theme MinimalTheme()
    {
        A.RgbColorModelHex Srgb(string hex) => new() { Val = hex };
        var scheme = new A.ColorScheme(
            new A.Dark1Color(Srgb("000000")), new A.Light1Color(Srgb("FFFFFF")), new A.Dark2Color(Srgb("44546A")), new A.Light2Color(Srgb("E7E6E6")),
            new A.Accent1Color(Srgb("FF7A00")), new A.Accent2Color(Srgb("FF4E50")), new A.Accent3Color(Srgb("9B5DE5")), new A.Accent4Color(Srgb("FFC000")),
            new A.Accent5Color(Srgb("5B9BD5")), new A.Accent6Color(Srgb("70AD47")), new A.Hyperlink(Srgb("0563C1")), new A.FollowedHyperlinkColor(Srgb("954F72"))) { Name = "Orange" };
        var fonts = new A.FontScheme(new A.MajorFont(new A.LatinFont { Typeface = "Calibri" }, new A.EastAsianFont { Typeface = "" }, new A.ComplexScriptFont { Typeface = "" }),
            new A.MinorFont(new A.LatinFont { Typeface = "Calibri" }, new A.EastAsianFont { Typeface = "" }, new A.ComplexScriptFont { Typeface = "" })) { Name = "Orange" };
        A.SolidFill Phc() => new(new A.SchemeColor { Val = A.SchemeColorValues.PhColor });
        var fmt = new A.FormatScheme(
            new A.FillStyleList(Phc(), Phc(), Phc()),
            new A.LineStyleList(new A.Outline(Phc()) { Width = 6350 }, new A.Outline(Phc()) { Width = 12700 }, new A.Outline(Phc()) { Width = 19050 }),
            new A.EffectStyleList(new A.EffectStyle(new A.EffectList()), new A.EffectStyle(new A.EffectList()), new A.EffectStyle(new A.EffectList())),
            new A.BackgroundFillStyleList(Phc(), Phc(), Phc())) { Name = "Orange" };
        return new A.Theme(new A.ThemeElements(scheme, fonts, fmt)) { Name = "Orange" };
    }
}
