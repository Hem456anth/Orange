using Orange.Core.Office;

namespace Orange.Core.Providers;

/// <summary>E-books: EPUB/MOBI/FB2/CBZ → EPUB, PDF, TXT, all built in. Registered before the Pandoc provider.</summary>
public sealed class EbookProvider : IOperationProvider
{
    public string Name => "E-books (built in)";
    private static readonly (string Ext, string Title, string[] From)[] Targets =
    [
        ("epub", "EPUB", ["mobi", "fb2", "cbz"]),
        ("pdf", "PDF", ["epub", "mobi", "fb2", "cbz"]),
        ("txt", "TXT", ["epub", "mobi", "fb2"]),
    ];

    public IEnumerable<OperationDescriptor> Describe() => Targets.Select(t => new OperationDescriptor
    {
        Id = $"ebook.to.{t.Ext}", Title = t.Title, Category = FileKind.Document, Group = OperationGroup.Convert,
        InputExtensions = t.From.ToHashSet(), OutputExtension = t.Ext, Keywords = ["ebook", "e-book", "kindle", "comic", t.Ext],
    });

    public bool IsAvailable(OperationDescriptor op) => true;

    public async Task<OperationResult> ExecuteAsync(OperationRequest r, IProgress<double>? progress, CancellationToken ct)
    {
        var input = r.Inputs[0]; var stem = Path.GetFileNameWithoutExtension(input);
        using var tmp = new SafePaths.TempWorkspace();
        progress?.Report(0.1);
        var model = await Task.Run(() => Ebooks.Read(input), ct);
        progress?.Report(0.5);
        var ext = r.Operation.OutputExtension!;
        var o = tmp.File(ext);
        switch (ext)
        {
            case "epub": Ebooks.WriteEpub(model, o); Ebooks.ReadEpub(o); break;
            case "pdf": PdfIO.Write(model, o); using (var d = UglyToad.PdfPig.PdfDocument.Open(o)) _ = d.NumberOfPages; break;
            case "txt": await File.WriteAllTextAsync(o, TextFormats.ToText(model), new System.Text.UTF8Encoding(false), ct); break;
        }
        var final = SafePaths.PlanOutput(input, r.OutputDirectory, stem, null, ext, r.Overwrite, r.Inputs);
        progress?.Report(1);
        return OperationResult.Ok(SafePaths.Commit(o, final, r.Overwrite));
    }
}

/// <summary>Fonts: TTF/OTF ↔ WOFF, → WOFF2, WOFF/WOFF2 → desktop font. Container changes only; outlines untouched.</summary>
public sealed class FontProvider : IOperationProvider
{
    public string Name => "Fonts (built in)";
    private static readonly HashSet<string> All = ["ttf", "otf", "woff", "woff2"];

    public IEnumerable<OperationDescriptor> Describe()
    {
        yield return new OperationDescriptor { Id = "font.to.woff", Title = "WOFF", Category = FileKind.Font, Group = OperationGroup.Convert, InputExtensions = All, OutputExtension = "woff", Keywords = ["web font", "woff"] };
        yield return new OperationDescriptor { Id = "font.to.woff2", Title = "WOFF2", Category = FileKind.Font, Group = OperationGroup.Convert, InputExtensions = All, OutputExtension = "woff2", Keywords = ["web font", "woff2"] };
        yield return new OperationDescriptor
        {
            Id = "font.to.desktop", Title = "TTF/OTF", Category = FileKind.Font, Group = OperationGroup.Convert, InputExtensions = new HashSet<string> { "woff", "woff2" },
            OutputExtension = "ttf", Keywords = ["install", "desktop font", "ttf", "otf", "truetype", "opentype"],
        };
    }

    public bool IsAvailable(OperationDescriptor op) => true;

    public async Task<OperationResult> ExecuteAsync(OperationRequest r, IProgress<double>? progress, CancellationToken ct)
    {
        var input = r.Inputs[0]; var stem = Path.GetFileNameWithoutExtension(input);
        if (new FileInfo(input).Length > 256L * 1024 * 1024) throw new OrangeException("The font file is larger than 256 MB.");
        var bytes = await File.ReadAllBytesAsync(input, ct);
        var font = Fonts.Read(bytes);
        var (ext, data) = r.Operation.Id switch
        {
            "font.to.woff" => ("woff", Fonts.WriteWoff(font)),
            "font.to.woff2" => ("woff2", Fonts.WriteWoff2(font)),
            _ => (font.DesktopExtension, Fonts.WriteSfnt(font)),
        };
        // Validate by reading back and comparing every table.
        var back = Fonts.Read(data);
        if (back.Tables.Count != font.Tables.Count || back.Tables.Any(t => font.Tables.First(x => x.Tag == t.Tag) is var src && (t.Tag != "head" && !src.Data.AsSpan().SequenceEqual(t.Data))))
            throw new OrangeException("The converted font didn't validate, so it wasn't saved.");
        using var tmp = new SafePaths.TempWorkspace();
        var o = tmp.File(ext); await File.WriteAllBytesAsync(o, data, ct);
        var final = SafePaths.PlanOutput(input, r.OutputDirectory, stem, null, ext, r.Overwrite, r.Inputs);
        progress?.Report(1);
        return OperationResult.Ok(SafePaths.Commit(o, final, r.Overwrite));
    }
}
