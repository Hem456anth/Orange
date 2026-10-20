using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using SS = DocumentFormat.OpenXml.Spreadsheet;

namespace Orange.Core.Office;

public sealed class Sheet
{
    public string Name { get; set; } = "Sheet1";
    public List<List<string>> Rows { get; } = new();
}

/// <summary>
/// Spreadsheet readers and writers: XLSX (OpenXml), XLS (ExcelDataReader, read only), ODS (plain XML, hardened reader),
/// CSV/TSV (RFC 4180). Values only: formulas come through as their cached results, formatting is not kept.
/// </summary>
public static class Sheets
{
    public const int MaxCells = 5_000_000;
    public const int MaxColumns = 16_384;

    public static List<Sheet> Read(string path)
    {
        var ext = FileTypeDetector.Ext(path);
        return ext switch
        {
            "xlsx" => ReadXlsx(path),
            "xls" => ReadXls(path),
            "ods" => ReadOds(path),
            "csv" => [ReadDelimited(path, ',')],
            "tsv" => [ReadDelimited(path, '\t')],
            _ => throw new OrangeException($"Orange can't read .{ext} spreadsheets."),
        };
    }

    public static void Write(List<Sheet> sheets, string path)
    {
        switch (FileTypeDetector.Ext(path))
        {
            case "xlsx": WriteXlsx(sheets, path); break;
            case "ods": WriteOds(sheets, path); break;
            case "csv": WriteDelimited(sheets[0], path, ','); break;
            case "tsv": WriteDelimited(sheets[0], path, '\t'); break;
            case "pdf": WritePdf(sheets, path); break;
            default: throw new OrangeException("Unsupported spreadsheet output.");
        }
    }

    private sealed class CellBudget { public long Used; public void Take(int n) { Used += n; if (Used > MaxCells) throw new OrangeException("The spreadsheet has more than 5 million cells; Orange stops there to stay responsive."); } }

    // ---------------- CSV / TSV

    public static Sheet ReadDelimited(string path, char sep)
    {
        var sheet = new Sheet { Name = Path.GetFileNameWithoutExtension(path) };
        var budget = new CellBudget();
        using var r = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var row = new List<string>(); var cell = new StringBuilder(); bool quoted = false; int c;
        while ((c = r.Read()) >= 0)
        {
            char ch = (char)c;
            if (quoted)
            {
                if (ch == '"') { if (r.Peek() == '"') { cell.Append('"'); r.Read(); } else quoted = false; }
                else cell.Append(ch);
                if (cell.Length > 1_000_000) throw new OrangeException("A cell is larger than 1 MB; the file looks malformed.");
            }
            else if (ch == '"' && cell.Length == 0) quoted = true;
            else if (ch == sep) { row.Add(cell.ToString()); cell.Clear(); }
            else if (ch == '\n' || ch == '\r')
            {
                if (ch == '\r' && r.Peek() == '\n') r.Read();
                row.Add(cell.ToString()); cell.Clear();
                budget.Take(row.Count);
                sheet.Rows.Add(row); row = new();
            }
            else cell.Append(ch);
        }
        if (cell.Length > 0 || row.Count > 0) { row.Add(cell.ToString()); sheet.Rows.Add(row); }
        return sheet;
    }

    public static void WriteDelimited(Sheet s, string path, char sep)
    {
        using var w = new StreamWriter(path, false, new UTF8Encoding(true));   // BOM so Excel reads UTF-8
        foreach (var row in s.Rows)
            w.Write(string.Join(sep, row.Select(v => Quote(v, sep))) + "\r\n");
        static string Quote(string v, char sep)
        {
            // Neutralise spreadsheet formula injection in exported text (=, +, -, @ at the start).
            if (v.Length > 0 && "=+-@".Contains(v[0]) && !double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out _)) v = "'" + v;
            return v.IndexOfAny([sep, '"', '\n', '\r']) >= 0 ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
        }
    }

    // ---------------- XLSX

    public static List<Sheet> ReadXlsx(string path)
    {
        OfficeGuard.CheckZip(path);
        var result = new List<Sheet>(); var budget = new CellBudget();
        SpreadsheetDocument doc;
        try { doc = SpreadsheetDocument.Open(path, false); }
        catch (Exception e) when (e is OpenXmlPackageException or InvalidDataException or XmlException) { throw new OrangeException("This XLSX looks damaged and can't be opened.", e.Message); }
        using (doc)
        {
            var wb = doc.WorkbookPart ?? throw new OrangeException("This XLSX has no workbook.");
            var shared = wb.SharedStringTablePart?.SharedStringTable?.Elements<SharedStringItem>().Select(i => i.InnerText).ToList() ?? new();
            foreach (var s in wb.Workbook.Sheets?.Elements<DocumentFormat.OpenXml.Spreadsheet.Sheet>() ?? [])
            {
                if (s.Id?.Value is not { } id || wb.GetPartById(id) is not WorksheetPart wsp) continue;
                var sheet = new Sheet { Name = s.Name?.Value ?? $"Sheet{result.Count + 1}" };
                using var reader = OpenXmlReader.Create(wsp);
                List<string>? row = null; int rowIndex = 0;
                while (reader.Read())
                {
                    if (reader.ElementType == typeof(Row) && reader.IsStartElement)
                    {
                        var r = (Row)reader.LoadCurrentElement()!;
                        int target = (int)(r.RowIndex?.Value ?? (uint)(rowIndex + 1));
                        if (target - rowIndex > 100_000) throw new OrangeException("The sheet has a huge gap of empty rows; refusing to expand it.");
                        while (rowIndex < target - 1) { sheet.Rows.Add(new()); rowIndex++; }
                        row = new();
                        foreach (var c in r.Elements<Cell>())
                        {
                            int col = ColumnIndex(c.CellReference?.Value) ?? row.Count;
                            if (col >= MaxColumns) continue;
                            while (row.Count < col) row.Add("");
                            row.Add(CellText(c, shared));
                        }
                        budget.Take(row.Count);
                        sheet.Rows.Add(row); rowIndex++;
                    }
                }
                result.Add(sheet);
            }
        }
        return result;
    }

    private static string CellText(Cell c, List<string> shared)
    {
        var v = c.CellValue?.Text ?? c.InlineString?.InnerText ?? "";
        if (c.DataType?.Value == CellValues.SharedString && int.TryParse(v, out var i)) return i >= 0 && i < shared.Count ? shared[i] : "";
        if (c.DataType?.Value == CellValues.Boolean) return v == "1" ? "TRUE" : "FALSE";
        return v;
    }

    private static int? ColumnIndex(string? reference)
    {
        if (string.IsNullOrEmpty(reference)) return null;
        int n = 0; int i = 0;
        for (; i < reference.Length && char.IsLetter(reference[i]); i++) n = n * 26 + (char.ToUpperInvariant(reference[i]) - 'A' + 1);
        return i == 0 ? null : n - 1;
    }

    private static string ColumnName(int index) { var s = ""; index++; while (index > 0) { int m = (index - 1) % 26; s = (char)('A' + m) + s; index = (index - 1) / 26; } return s; }

    public static void WriteXlsx(List<Sheet> sheets, string path)
    {
        using var doc = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook);
        var wb = doc.AddWorkbookPart();
        wb.Workbook = new Workbook(new SS.Sheets());
        var sheetsEl = wb.Workbook.GetFirstChild<SS.Sheets>()!;
        uint id = 1;
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in sheets)
        {
            var wsp = wb.AddNewPart<WorksheetPart>();
            using (var w = OpenXmlWriter.Create(wsp))
            {
                w.WriteStartElement(new Worksheet()); w.WriteStartElement(new SheetData());
                for (int r = 0; r < s.Rows.Count; r++)
                {
                    w.WriteStartElement(new Row { RowIndex = (uint)(r + 1) });
                    for (int c = 0; c < s.Rows[r].Count; c++)
                    {
                        var v = s.Rows[r][c];
                        if (v.Length == 0) continue;
                        var cell = double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out _) && v.Trim() == v
                            ? new Cell { CellReference = ColumnName(c) + (r + 1), CellValue = new CellValue(v), DataType = CellValues.Number }
                            : new Cell { CellReference = ColumnName(c) + (r + 1), DataType = CellValues.InlineString, InlineString = new InlineString(new Text(v) { Space = SpaceProcessingModeValues.Preserve }) };
                        w.WriteElement(cell);
                    }
                    w.WriteEndElement();
                }
                w.WriteEndElement(); w.WriteEndElement();
            }
            var name = SafeSheetName(s.Name, used);
            sheetsEl.Append(new DocumentFormat.OpenXml.Spreadsheet.Sheet { Id = wb.GetIdOfPart(wsp), SheetId = id++, Name = name });
        }
        if (sheets.Count == 0) throw new OrangeException("There's nothing to write.");
    }

    private static string SafeSheetName(string name, HashSet<string> used)
    {
        var n = new string(name.Where(ch => ch is not ('[' or ']' or ':' or '*' or '?' or '/' or '\\')).ToArray()).Trim('\'');
        if (n.Length == 0) n = "Sheet";
        if (n.Length > 31) n = n[..31];
        var baseName = n; int k = 2;
        while (!used.Add(n)) n = (baseName.Length > 27 ? baseName[..27] : baseName) + " " + k++;
        return n;
    }

    // ---------------- XLS (legacy, read only)

    public static List<Sheet> ReadXls(string path)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var result = new List<Sheet>(); var budget = new CellBudget();
        try
        {
            using var fs = File.OpenRead(path);
            using var reader = ExcelDataReader.ExcelReaderFactory.CreateBinaryReader(fs);
            do
            {
                var sheet = new Sheet { Name = reader.Name ?? $"Sheet{result.Count + 1}" };
                while (reader.Read())
                {
                    var row = new List<string>(Math.Min(reader.FieldCount, MaxColumns));
                    for (int i = 0; i < reader.FieldCount && i < MaxColumns; i++)
                        row.Add(Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture) ?? "");
                    budget.Take(row.Count);
                    sheet.Rows.Add(row);
                }
                result.Add(sheet);
            } while (reader.NextResult());
        }
        catch (OrangeException) { throw; }
        catch (Exception e) { throw new OrangeException("This XLS file can't be read. Install LibreOffice for unusual legacy files.", e.Message); }
        return result;
    }

    // ---------------- ODS

    private const string NsTable = "urn:oasis:names:tc:opendocument:xmlns:table:1.0";
    private const string NsText = "urn:oasis:names:tc:opendocument:xmlns:text:1.0";
    private const string NsOffice = "urn:oasis:names:tc:opendocument:xmlns:office:1.0";

    public static List<Sheet> ReadOds(string path)
    {
        OfficeGuard.CheckZip(path);
        var result = new List<Sheet>(); var budget = new CellBudget();
        using var z = ZipFile.OpenRead(path);
        var entry = z.GetEntry("content.xml") ?? throw new OrangeException("This ODS has no content.");
        using var s = entry.Open();
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreWhitespace = false, MaxCharactersInDocument = 1_000_000_000 };
        using var x = XmlReader.Create(s, settings);
        Sheet? sheet = null; List<string>? row = null; int rowRepeat = 1; int colRepeat = 1; StringBuilder? cell = null; int pDepth = 0;
        try
        {
            while (x.Read())
            {
                if (x.NodeType == XmlNodeType.Element)
                {
                    if (x.LocalName == "table" && x.NamespaceURI == NsTable) { sheet = new Sheet { Name = x.GetAttribute("name", NsTable) ?? $"Sheet{result.Count + 1}" }; if (x.IsEmptyElement) { result.Add(sheet); sheet = null; } }
                    else if (x.LocalName == "table-row" && x.NamespaceURI == NsTable && sheet != null)
                    {
                        row = new(); rowRepeat = Math.Clamp(int.TryParse(x.GetAttribute("number-rows-repeated", NsTable), out var rr) ? rr : 1, 1, 1_048_576);
                        if (x.IsEmptyElement) { EndRow(); }
                    }
                    else if ((x.LocalName is "table-cell" or "covered-table-cell") && x.NamespaceURI == NsTable && row != null)
                    {
                        colRepeat = Math.Clamp(int.TryParse(x.GetAttribute("number-columns-repeated", NsTable), out var cr) ? cr : 1, 1, MaxColumns);
                        var value = x.GetAttribute("value", NsOffice) ?? x.GetAttribute("date-value", NsOffice) ?? x.GetAttribute("boolean-value", NsOffice);
                        cell = new StringBuilder(); pDepth = 0;
                        if (value != null) { cell.Append(value); cell.Insert(0, '\u0001'); }   // marker: typed value wins over display text
                        if (x.IsEmptyElement) EndCell();
                    }
                    else if (x.LocalName == "p" && x.NamespaceURI == NsText && cell != null) { if (pDepth++ > 0 && cell.Length > 0 && cell[0] != '\u0001') cell.Append('\n'); }
                    else if (x.LocalName == "s" && x.NamespaceURI == NsText && cell != null && (cell.Length == 0 || cell[0] != '\u0001')) cell.Append(' ', Math.Clamp(int.TryParse(x.GetAttribute("c", NsText), out var sc) ? sc : 1, 1, 1000));
                }
                else if ((x.NodeType == XmlNodeType.Text || x.NodeType == XmlNodeType.SignificantWhitespace) && cell != null && (cell.Length == 0 || cell[0] != '\u0001')) cell.Append(x.Value);
                else if (x.NodeType == XmlNodeType.EndElement)
                {
                    if ((x.LocalName is "table-cell" or "covered-table-cell") && x.NamespaceURI == NsTable) EndCell();
                    else if (x.LocalName == "table-row" && x.NamespaceURI == NsTable) EndRow();
                    else if (x.LocalName == "table" && x.NamespaceURI == NsTable && sheet != null)
                    {
                        while (sheet.Rows.Count > 0 && sheet.Rows[^1].All(v => v.Length == 0)) sheet.Rows.RemoveAt(sheet.Rows.Count - 1);
                        result.Add(sheet); sheet = null;
                    }
                }
            }
        }
        catch (XmlException e) { throw new OrangeException("This ODS looks damaged.", e.Message); }
        return result;

        void EndCell()
        {
            if (row == null || cell == null) return;
            var v = cell.ToString(); if (v.StartsWith('\u0001')) v = v[1..];
            // Trailing repeated empty cells pad rows to 16k columns; only expand non-empty repeats.
            int times = v.Length == 0 && colRepeat > 50 ? 0 : colRepeat;
            for (int i = 0; i < times && row.Count < MaxColumns; i++) row.Add(v);
            cell = null;
        }
        void EndRow()
        {
            if (sheet == null || row == null) return;
            while (row.Count > 0 && row[^1].Length == 0) row.RemoveAt(row.Count - 1);
            int times = row.Count == 0 && rowRepeat > 100 ? 1 : rowRepeat;
            for (int i = 0; i < times; i++) { budget.Take(Math.Max(1, row.Count)); sheet.Rows.Add(i == 0 ? row : row.ToList()); }
            row = null;
        }
    }

    public static void WriteOds(List<Sheet> sheets, string path)
    {
        using var fs = new FileStream(path, FileMode.CreateNew);
        using var z = new ZipArchive(fs, ZipArchiveMode.Create);
        var mt = z.CreateEntry("mimetype", CompressionLevel.NoCompression);
        using (var w = new StreamWriter(mt.Open(), new UTF8Encoding(false))) w.Write("application/vnd.oasis.opendocument.spreadsheet");
        using (var w = new StreamWriter(z.CreateEntry("META-INF/manifest.xml").Open(), new UTF8Encoding(false)))
            w.Write("<?xml version=\"1.0\" encoding=\"UTF-8\"?><manifest:manifest xmlns:manifest=\"urn:oasis:names:tc:opendocument:xmlns:manifest:1.0\" manifest:version=\"1.2\"><manifest:file-entry manifest:full-path=\"/\" manifest:media-type=\"application/vnd.oasis.opendocument.spreadsheet\"/><manifest:file-entry manifest:full-path=\"content.xml\" manifest:media-type=\"text/xml\"/></manifest:manifest>");
        using var cs = z.CreateEntry("content.xml").Open();
        using var x = XmlWriter.Create(cs, new XmlWriterSettings { Encoding = new UTF8Encoding(false) });
        x.WriteStartDocument();
        x.WriteStartElement("office", "document-content", NsOffice);
        x.WriteAttributeString("xmlns", "table", null, NsTable); x.WriteAttributeString("xmlns", "text", null, NsText);
        x.WriteAttributeString("office", "version", NsOffice, "1.2");
        x.WriteStartElement("office", "body", NsOffice); x.WriteStartElement("office", "spreadsheet", NsOffice);
        foreach (var s in sheets)
        {
            x.WriteStartElement("table", "table", NsTable); x.WriteAttributeString("table", "name", NsTable, s.Name);
            foreach (var row in s.Rows)
            {
                x.WriteStartElement("table", "table-row", NsTable);
                foreach (var v in row)
                {
                    x.WriteStartElement("table", "table-cell", NsTable);
                    if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out _) && v.Trim() == v && v.Length > 0)
                    { x.WriteAttributeString("office", "value-type", NsOffice, "float"); x.WriteAttributeString("office", "value", NsOffice, v); }
                    else if (v.Length > 0) x.WriteAttributeString("office", "value-type", NsOffice, "string");
                    if (v.Length > 0) { x.WriteStartElement("text", "p", NsText); x.WriteString(Clean(v)); x.WriteEndElement(); }
                    x.WriteEndElement();
                }
                x.WriteEndElement();
            }
            x.WriteEndElement();
        }
        x.WriteEndElement(); x.WriteEndElement(); x.WriteEndElement(); x.WriteEndDocument();
        static string Clean(string s) => new(s.Where(ch => ch is '\t' or '\n' or '\r' || ch >= 0x20).ToArray());
    }

    // ---------------- PDF

    public static void WritePdf(List<Sheet> sheets, string path)
    {
        var d = new DocModel { Title = Path.GetFileNameWithoutExtension(path) };
        foreach (var s in sheets)
        {
            if (d.Blocks.Count > 0) d.Add(new PageBreak());
            if (sheets.Count > 1) d.Add(new Heading(2, s.Name));
            var rows = s.Rows.Where(r => r.Any(v => v.Length > 0)).Select(r => (IReadOnlyList<string>)r).ToList();
            if (rows.Count > 0) d.Add(new Table(rows)); else d.Add(Para.Of("(empty sheet)"));
        }
        PdfIO.Write(d, path);
    }
}
