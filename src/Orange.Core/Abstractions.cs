namespace Orange.Core;

/// <summary>Broad kind of a file, decided from its content signature first and its extension second.</summary>
public enum FileKind { Unknown, Image, Video, Audio, Pdf, Document, Subtitle, Archive }

/// <summary>Which wheel an operation lives on. Mirrors the observed Convert / Tools split (actions are shell-side, not here).</summary>
public enum OperationGroup { Convert, Tool }

public enum OverwritePolicy { KeepBoth, Replace, Fail }

public enum OptionType { Integer, Number, Text, Choice, Boolean, TimeSpan }

/// <summary>A user-adjustable setting for an operation (shown in the settings flyout before running).</summary>
public sealed record OptionSpec(
    string Name,
    string Label,
    OptionType Type,
    string Default,
    double? Min = null,
    double? Max = null,
    IReadOnlyList<string>? Choices = null,
    string? Help = null);

/// <summary>Static description of one thing Orange can do. Registered by a provider.</summary>
public sealed record OperationDescriptor
{
    public required string Id { get; init; }                 // e.g. "image.to.webp", "pdf.merge"
    public required string Title { get; init; }              // "WebP", "Merge PDFs"
    public required FileKind Category { get; init; }
    public required OperationGroup Group { get; init; }
    /// <summary>Lower-case extensions (no dot) this operation accepts. Empty = any file of <see cref="Category"/>.</summary>
    public IReadOnlySet<string> InputExtensions { get; init; } = new HashSet<string>();
    /// <summary>Output extension, or null when the output keeps the input's extension (e.g. compress).</summary>
    public string? OutputExtension { get; init; }
    /// <summary>Many inputs produce one output (merge, images to PDF).</summary>
    public bool CombinesInputs { get; init; }
    /// <summary>Requires at least this many inputs (merge needs 2).</summary>
    public int MinInputs { get; init; } = 1;
    /// <summary>Output is a folder of files (split, extract pages/images/frames).</summary>
    public bool ProducesFolder { get; init; }
    public IReadOnlyList<OptionSpec> Options { get; init; } = Array.Empty<OptionSpec>();
    public IReadOnlyList<string> Keywords { get; init; } = Array.Empty<string>();
    /// <summary>Name of the external tool this needs ("ffmpeg", "ghostscript", "libreoffice"), or null when built in.</summary>
    public string? RequiresTool { get; init; }
    public string Icon { get; init; } = "";                  // Segoe Fluent Icons glyph name used by the UI
}

public sealed class OperationRequest
{
    public required OperationDescriptor Operation { get; init; }
    public required IReadOnlyList<string> Inputs { get; init; }
    /// <summary>Folder the result is written to. Defaults to the first input's folder.</summary>
    public string? OutputDirectory { get; init; }
    public IReadOnlyDictionary<string, string> Options { get; init; } = new Dictionary<string, string>();
    public OverwritePolicy Overwrite { get; init; } = OverwritePolicy.KeepBoth;

    public string Opt(string name)
    {
        if (Options.TryGetValue(name, out var v) && !string.IsNullOrWhiteSpace(v)) return v;
        var spec = Operation.Options.FirstOrDefault(o => o.Name == name)
                   ?? throw new ArgumentException($"Unknown option '{name}' for {Operation.Id}");
        return spec.Default;
    }
    public int OptInt(string name) => OptionValidator.ParseInt(Operation, name, Opt(name));
    public double OptDouble(string name) => OptionValidator.ParseDouble(Operation, name, Opt(name));
    public bool OptBool(string name) => OptionValidator.ParseBool(Opt(name));
    public TimeSpan? OptTime(string name) => OptionValidator.ParseTime(Opt(name));
}

public sealed record OperationResult(bool Success, IReadOnlyList<string> Outputs, string? Error = null, string? Detail = null)
{
    public static OperationResult Ok(params string[] outputs) => new(true, outputs);
    public static OperationResult Fail(string error, string? detail = null) => new(false, Array.Empty<string>(), error, detail);
}

/// <summary>Raised for problems the user can act on. The message is shown as-is.</summary>
public sealed class OrangeException(string message, string? detail = null, Exception? inner = null)
    : Exception(message, inner)
{
    public string? Detail { get; } = detail;
}

public interface IOperationProvider
{
    string Name { get; }
    IEnumerable<OperationDescriptor> Describe();
    /// <summary>False when a required external tool is missing; the registry hides the provider's operations.</summary>
    bool IsAvailable(OperationDescriptor op);
    Task<OperationResult> ExecuteAsync(OperationRequest request, IProgress<double>? progress, CancellationToken ct);
}
