using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using A = DocumentFormat.OpenXml.Drawing;
using DW = DocumentFormat.OpenXml.Drawing.Wordprocessing;
using PIC = DocumentFormat.OpenXml.Drawing.Pictures;

namespace Orange.Core.Office;

/// <summary>DOCX reader and writer (DocumentFormat.OpenXml, MIT). No macros are ever run; external relationships are ignored.</summary>
public static class DocxIO
{
    public static DocModel Read(string path)
    {
        OfficeGuard.CheckZip(path);
        var d = new DocModel { Title = Path.GetFileNameWithoutExtension(path) };
        WordprocessingDocument doc;
        try { doc = WordprocessingDocument.Open(path, false, new OpenSettings { AutoSave = false }); }
        catch (Exception e) when (e is OpenXmlPackageException or InvalidDataException or System.Xml.XmlException)
        { throw new OrangeException("This DOCX looks damaged and can't be opened.", e.Message); }
        using (doc)
        {
            var main = doc.MainDocumentPart ?? throw new OrangeException("This DOCX has no document body.");
            if (doc.PackageProperties.Title is { Length: > 0 } t) d.Title = t;
            var body = main.Document?.Body;
            if (body is null) return d;
            foreach (var el in body.ChildElements)
            {
                switch (el)
                {
                    case Paragraph p: ReadParagraph(p, main, d); break;
                    case DocumentFormat.OpenXml.Wordprocessing.Table tbl:
                        var rows = tbl.Elements<TableRow>().Select(r => (IReadOnlyList<string>)r.Elements<TableCell>()
                            .Select(c => string.Join(" ", c.Elements<Paragraph>().Select(x => x.InnerText)).Trim()).ToList()).ToList();
                        if (rows.Count > 0) d.Add(new Table(rows));
                        break;
                }
            }
        }
        return d;
    }

    private static void ReadParagraph(Paragraph p, MainDocumentPart main, DocModel d)
    {
        var style = p.ParagraphProperties?.ParagraphStyleId?.Val?.Value ?? "";
        int? level = style.StartsWith("Heading", StringComparison.OrdinalIgnoreCase) && int.TryParse(style.AsSpan(7), out var lv) ? lv
                   : style.Equals("Title", StringComparison.OrdinalIgnoreCase) ? 1 : null;
        if (p.ParagraphProperties?.OutlineLevel?.Val?.Value is int ol && level is null && ol < 6) level = ol + 1;
        var runs = new List<Run>();
        foreach (var r in p.Descendants<DocumentFormat.OpenXml.Wordprocessing.Run>())
        {
            if (r.Descendants<Break>().Any(b => b.Type?.Value == BreakValues.Page)) { Emit(); d.Add(new PageBreak()); }
            foreach (var blip in r.Descendants<A.Blip>())
                if (blip.Embed?.Value is { } rid && main.GetPartById(rid) is ImagePart ip)
                {
                    Emit();
                    using var s = ip.GetStream();
                    using var ms = new MemoryStream();
                    s.CopyTo(ms);
                    var ext = ip.ContentType switch { "image/png" => "png", "image/jpeg" => "jpg", "image/gif" => "gif", "image/bmp" => "bmp", _ => "" };
                    if (ext.Length > 0 && ms.Length < 50_000_000) d.Add(new Picture(ms.ToArray(), ext));
                }
            var text = string.Concat(r.ChildElements.Select(c => c switch { Text t => t.Text, TabChar => "\t", Break b when b.Type?.Value != BreakValues.Page => "\n", _ => "" }));
            if (text.Length == 0) continue;
            var rp = r.RunProperties;
            runs.Add(new Run(text, rp?.Bold is { } b2 && (b2.Val?.Value ?? true), rp?.Italic is { } i && (i.Val?.Value ?? true)));
        }
        Emit();

        void Emit()
        {
            if (runs.Count == 0 || string.Concat(runs.Select(x => x.Text)).Trim().Length == 0) { runs.Clear(); return; }
            var copy = runs.ToList(); runs.Clear();
            if (level is { } l) d.Add(new Heading(Math.Clamp(l, 1, 6), string.Concat(copy.Select(x => x.Text)).Trim()));
            else if (p.ParagraphProperties?.NumberingProperties is { } np)
            {
                var ordered = IsOrdered(main, np);
                d.Add(new ListItem(copy, ordered, np.NumberingLevelReference?.Val?.Value ?? 0));
            }
            else d.Add(new Para(copy));
        }
    }

    private static bool IsOrdered(MainDocumentPart main, NumberingProperties np)
    {
        try
        {
            var numId = np.NumberingId?.Val?.Value; var ilvl = np.NumberingLevelReference?.Val?.Value ?? 0;
            var numbering = main.NumberingDefinitionsPart?.Numbering;
            if (numId is null || numbering is null) return false;
            var inst = numbering.Elements<NumberingInstance>().FirstOrDefault(n => n.NumberID?.Value == numId);
            var absId = inst?.AbstractNumId?.Val?.Value;
            var abs = numbering.Elements<AbstractNum>().FirstOrDefault(a => a.AbstractNumberId?.Value == absId);
            var fmt = abs?.Elements<Level>().FirstOrDefault(l => l.LevelIndex?.Value == ilvl)?.NumberingFormat?.Val?.Value;
            return fmt is not null && fmt != NumberFormatValues.Bullet;
        }
        catch { return false; }
    }

    // ---------------- writer

    public static void Write(DocModel d, string path)
    {
        using var doc = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var main = doc.AddMainDocumentPart();
        main.Document = new Document(new Body());
        var body = main.Document.Body!;
        AddStyles(main);
        int? bulletNum = null, orderedNum = null;
        uint picId = 1;
        foreach (var b in d.Blocks)
        {
            switch (b)
            {
                case Heading h:
                    body.Append(new Paragraph(new ParagraphProperties(new ParagraphStyleId { Val = $"Heading{Math.Clamp(h.Level, 1, 6)}" }), new DocumentFormat.OpenXml.Wordprocessing.Run(new Text(h.Text) { Space = SpaceProcessingModeValues.Preserve })));
                    break;
                case Para p: body.Append(new Paragraph(Runs(p.Runs))); break;
                case ListItem li:
                    var num = li.Ordered ? (orderedNum ??= AddNumbering(main, true)) : (bulletNum ??= AddNumbering(main, false));
                    var lpara = new Paragraph(new ParagraphProperties(new NumberingProperties(new NumberingLevelReference { Val = Math.Clamp(li.Level, 0, 8) }, new NumberingId { Val = num })));
                    lpara.Append(Runs(li.Runs));
                    body.Append(lpara);
                    break;
                case Table t:
                    var tbl = new DocumentFormat.OpenXml.Wordprocessing.Table(new TableProperties(
                        new TableBorders(new TopBorder { Val = BorderValues.Single, Size = 4 }, new BottomBorder { Val = BorderValues.Single, Size = 4 },
                            new LeftBorder { Val = BorderValues.Single, Size = 4 }, new RightBorder { Val = BorderValues.Single, Size = 4 },
                            new InsideHorizontalBorder { Val = BorderValues.Single, Size = 4 }, new InsideVerticalBorder { Val = BorderValues.Single, Size = 4 }),
                        new TableWidth { Type = TableWidthUnitValues.Pct, Width = "5000" }));
                    foreach (var r in t.Rows)
                        tbl.Append(new TableRow(r.Select(c => new TableCell(new Paragraph(new DocumentFormat.OpenXml.Wordprocessing.Run(new Text(c) { Space = SpaceProcessingModeValues.Preserve })))).ToArray()));
                    body.Append(tbl);
                    body.Append(new Paragraph());
                    break;
                case Picture pic: body.Append(new Paragraph(new DocumentFormat.OpenXml.Wordprocessing.Run(Image(main, pic, picId++)))); break;
                case PageBreak: body.Append(new Paragraph(new DocumentFormat.OpenXml.Wordprocessing.Run(new Break { Type = BreakValues.Page }))); break;
            }
        }
        body.Append(new SectionProperties(new PageSize { Width = 11906, Height = 16838 }, new PageMargin { Top = 1134, Bottom = 1134, Left = 1134, Right = 1134, Header = 708, Footer = 708, Gutter = 0 }));
        doc.PackageProperties.Title = d.Title;
        doc.PackageProperties.Creator = "Orange";
    }

    private static OpenXmlElement[] Runs(IEnumerable<Run> runs) => runs.Select(r =>
    {
        var rp = new RunProperties();
        if (r.Bold) rp.Append(new Bold());
        if (r.Italic) rp.Append(new Italic());
        var run = new DocumentFormat.OpenXml.Wordprocessing.Run();
        if (rp.HasChildren) run.Append(rp);
        var parts = r.Text.Split('\n');
        for (int i = 0; i < parts.Length; i++)
        {
            if (i > 0) run.Append(new Break());
            run.Append(new Text(parts[i]) { Space = SpaceProcessingModeValues.Preserve });
        }
        return (OpenXmlElement)run;
    }).ToArray();

    private static void AddStyles(MainDocumentPart main)
    {
        var styles = new Styles(new DocDefaults(new RunPropertiesDefault(new RunPropertiesBaseStyle(new RunFonts { Ascii = "Calibri", HighAnsi = "Calibri" }, new FontSize { Val = "22" }))));
        int[] sizes = [40, 32, 28, 26, 24, 22];
        for (int i = 1; i <= 6; i++)
            styles.Append(new Style(new StyleName { Val = $"heading {i}" }, new BasedOn { Val = "Normal" }, new PrimaryStyle(),
                new StyleParagraphProperties(new SpacingBetweenLines { Before = "240", After = "120" }, new KeepNext(), new OutlineLevel { Val = i - 1 }),
                new StyleRunProperties(new Bold(), new FontSize { Val = sizes[i - 1].ToString() })) { Type = StyleValues.Paragraph, StyleId = $"Heading{i}" });
        styles.Append(new Style(new StyleName { Val = "Normal" }, new PrimaryStyle(), new StyleParagraphProperties(new SpacingBetweenLines { After = "160" })) { Type = StyleValues.Paragraph, StyleId = "Normal", Default = true });
        main.AddNewPart<StyleDefinitionsPart>().Styles = styles;
    }

    private static int AddNumbering(MainDocumentPart main, bool ordered)
    {
        var part = main.NumberingDefinitionsPart ?? main.AddNewPart<NumberingDefinitionsPart>();
        part.Numbering ??= new Numbering();
        int id = part.Numbering.Elements<AbstractNum>().Count() + 1;
        var abs = new AbstractNum { AbstractNumberId = id };
        for (int l = 0; l < 9; l++)
            abs.Append(new Level(new StartNumberingValue { Val = 1 }, new NumberingFormat { Val = ordered ? NumberFormatValues.Decimal : NumberFormatValues.Bullet },
                new LevelText { Val = ordered ? $"%{l + 1}." : "•" }, new PreviousParagraphProperties(new Indentation { Left = (720 * (l + 1)).ToString(), Hanging = "360" })) { LevelIndex = l });
        part.Numbering.InsertAt(abs, part.Numbering.Elements<AbstractNum>().Count());
        part.Numbering.Append(new NumberingInstance(new AbstractNumId { Val = id }) { NumberID = id });
        return id;
    }

    private static Drawing Image(MainDocumentPart main, Picture pic, uint id)
    {
        var type = pic.Extension switch { "png" => ImagePartType.Png, "gif" => ImagePartType.Gif, "bmp" => ImagePartType.Bmp, _ => ImagePartType.Jpeg };
        var ip = main.AddImagePart(type);
        using (var ms = new MemoryStream(pic.Data)) ip.FeedData(ms);
        var rid = main.GetIdOfPart(ip);
        // Size: keep aspect, at most 16 cm wide.
        double wPx = 800, hPx = 600;
        try { var info = new ImageMagick.MagickImageInfo(pic.Data); wPx = info.Width; hPx = info.Height; } catch { }
        const long EmuPerPx = 9525; const long MaxW = 5760000;
        long cx = (long)(wPx * EmuPerPx), cy = (long)(hPx * EmuPerPx);
        if (cx > MaxW) { cy = cy * MaxW / cx; cx = MaxW; }
        return new Drawing(new DW.Inline(
            new DW.Extent { Cx = cx, Cy = cy }, new DW.DocProperties { Id = id, Name = $"Picture {id}" },
            new A.Graphic(new A.GraphicData(new PIC.Picture(
                new PIC.NonVisualPictureProperties(new PIC.NonVisualDrawingProperties { Id = id, Name = $"image{id}.{pic.Extension}" }, new PIC.NonVisualPictureDrawingProperties()),
                new PIC.BlipFill(new A.Blip { Embed = rid }, new A.Stretch(new A.FillRectangle())),
                new PIC.ShapeProperties(new A.Transform2D(new A.Offset { X = 0, Y = 0 }, new A.Extents { Cx = cx, Cy = cy }), new A.PresetGeometry { Preset = A.ShapeTypeValues.Rectangle }))
            ) { Uri = "http://schemas.openxmlformats.org/drawingml/2006/picture" }))
        { DistanceFromTop = 0, DistanceFromBottom = 0, DistanceFromLeft = 0, DistanceFromRight = 0 });
    }
}
