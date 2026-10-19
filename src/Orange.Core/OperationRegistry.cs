using Orange.Core.Providers;

namespace Orange.Core;

/// <summary>
/// Holds every operation from every provider and answers "what can I do with these files?".
/// The UI never shows the whole list: it asks for the context-aware subset and searches the rest.
/// </summary>
public sealed class OperationRegistry
{
    private readonly List<(OperationDescriptor Op, IOperationProvider Provider)> _ops = new();

    public static OperationRegistry CreateDefault(ToolLocator? tools = null)
    {
        tools ??= ToolLocator.Default;
        var r = new OperationRegistry();
        r.Add(new ImageProvider());
        r.Add(new ImageStudioProvider());
        r.Add(new MediaProvider(tools));
        r.Add(new PdfProvider(tools));
        r.Add(new OfficeProvider());          // before Documents: the built-in route wins for the same format
        r.Add(new DocumentProvider(tools));
        r.Add(new SubtitleProvider());
        r.Add(new ArchiveProvider(tools));
        return r;
    }

    public void Add(IOperationProvider provider)
    {
        foreach (var op in provider.Describe())
        {
            if (_ops.Any(o => o.Op.Id == op.Id)) throw new InvalidOperationException($"Duplicate operation id {op.Id}");
            _ops.Add((op, provider));
        }
    }

    public IReadOnlyList<OperationDescriptor> All => _ops.Select(o => o.Op).ToList();
    public IEnumerable<OperationDescriptor> Available => _ops.Where(o => o.Provider.IsAvailable(o.Op)).Select(o => o.Op);
    public OperationDescriptor Get(string id) => _ops.FirstOrDefault(o => o.Op.Id == id).Op ?? throw new OrangeException($"Unknown operation \"{id}\".");
    public IOperationProvider ProviderFor(OperationDescriptor op) => _ops.First(o => o.Op.Id == op.Id).Provider;
    public bool IsAvailable(OperationDescriptor op) => ProviderFor(op).IsAvailable(op);

    /// <summary>Operations that apply to every file in the selection (all must share one kind), for the given wheel.</summary>
    public IReadOnlyList<OperationDescriptor> ForFiles(IReadOnlyList<string> files, OperationGroup group, bool includeUnavailable = false)
    {
        if (files.Count == 0) return Array.Empty<OperationDescriptor>();
        var kinds = files.Select(FileTypeDetector.Detect).Distinct().ToList();
        if (kinds.Count != 1 || kinds[0] == FileKind.Unknown) return Array.Empty<OperationDescriptor>();
        var kind = kinds[0];
        var exts = files.Select(FileTypeDetector.Ext).Select(Normalize).ToHashSet();
        return _ops
            .Where(o => o.Op.Group == group && o.Op.Category == kind)
            .Where(o => includeUnavailable || o.Provider.IsAvailable(o.Op))
            .Where(o => files.Count >= o.Op.MinInputs)
            .Where(o => files.Count == 1 || o.Op.CombinesInputs || o.Op.Group == OperationGroup.Convert || IsBatchable(o.Op))
            .Where(o => o.Op.InputExtensions.Count == 0 || exts.All(e => o.Op.InputExtensions.Contains(e)))
            // A conversion to the format the files already are is pointless; hide it (unless mixed formats).
            .Where(o => !(o.Op.Group == OperationGroup.Convert && o.Op.OutputExtension is { } x && exts.Count == 1 && exts.Contains(Normalize(x))))
            .Select(o => o.Op)
            // Two providers offering the same conversion (built-in DOCX→PDF and LibreOffice's): keep the first registered.
            .GroupBy(o => o.Group == OperationGroup.Convert ? "convert:" + Normalize(o.OutputExtension ?? o.Id) + (o.ProducesFolder ? "/" : "") : o.Id)
            .Select(g => g.First())
            .OrderBy(o => o.MinInputs > 1 && files.Count > 1 ? 0 : 1)   // multi-file selection: merge-type tools first
            .ToList();
    }

    /// <summary>Free-text search across titles, ids, categories and keywords. "webp", "to mp3", "compress pdf".</summary>
    public IReadOnlyList<OperationDescriptor> Search(string query, IReadOnlyList<string>? files = null)
    {
        var terms = query.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(t => t is not ("to" or "into" or "as")).ToArray();
        IEnumerable<OperationDescriptor> pool = files is { Count: > 0 }
            ? ForFiles(files, OperationGroup.Convert).Concat(ForFiles(files, OperationGroup.Tool))
            : Available;
        return pool
            .Select(op => (op, score: Score(op, terms)))
            .Where(x => x.score > 0)
            .OrderByDescending(x => x.score).ThenBy(x => x.op.Title)
            .Select(x => x.op).ToList();
    }

    private static int Score(OperationDescriptor op, string[] terms)
    {
        if (terms.Length == 0) return 1;
        var hay = new[] { op.Title, op.Id, op.Category.ToString(), op.OutputExtension ?? "" }.Concat(op.Keywords).Select(s => s.ToLowerInvariant()).ToArray();
        int score = 0;
        foreach (var t in terms)
        {
            if (hay.Any(h => h == t)) score += 10;
            else if (hay.Any(h => h.StartsWith(t))) score += 5;
            else if (hay.Any(h => h.Contains(t))) score += 2;
            else return 0;               // every term must match somewhere
        }
        return score;
    }

    private static bool IsBatchable(OperationDescriptor op) => !op.Keywords.Contains("single-only");
    public static string Normalize(string ext) => ext switch { "jpeg" => "jpg", "tif" => "tiff", "htm" => "html", "markdown" => "md", "aif" => "aiff", "heif" => "heic", "tgz" => "gz", _ => ext };
}
