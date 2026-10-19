using System.Text.Json;
using System.Text.Json.Serialization;

namespace Orange.Core.Presets;

public enum OutputLocation { BesideOriginal, Subfolder }
public enum ConflictPolicy { KeepBoth, Replace, Skip }
public enum MetadataPolicy { AsOperation, Strip }

/// <summary>Where a preset's result goes and what it is called.</summary>
public sealed record OutputRule
{
    public OutputLocation Location { get; init; } = OutputLocation.BesideOriginal;
    /// <summary>Subfolder name next to the original when <see cref="Location"/> is Subfolder.</summary>
    public string Subfolder { get; init; } = "Orange";
    /// <summary>Tokens: {name} original name without extension, {preset}, {ext} output extension, {date} yyyy-MM-dd, {op} operation title.</summary>
    public string NamePattern { get; init; } = "{name} {preset}.{ext}";
    public ConflictPolicy Conflict { get; init; } = ConflictPolicy.KeepBoth;
}

/// <summary>
/// A saved recipe: one operation, its options, and an output rule. Shows pinned on the wheel for matching files,
/// runs from the main window, the CLI (--preset) and watch folders.
/// </summary>
public sealed record Preset
{
    public required string Name { get; init; }
    public required string OperationId { get; init; }
    public Dictionary<string, string> Options { get; init; } = new();
    public MetadataPolicy Metadata { get; init; } = MetadataPolicy.AsOperation;
    public OutputRule Output { get; init; } = new();
    public bool PinnedOnWheel { get; init; } = true;

    /// <summary>Expands the name pattern into a safe file name (no folders, no reserved names).</summary>
    public string FileNameFor(string input, string outputExtension, string opTitle, DateTime? now = null)
    {
        var name = Path.GetFileNameWithoutExtension(input);
        var ext = outputExtension.TrimStart('.');
        var s = Output.NamePattern
            .Replace("{name}", name).Replace("{preset}", Name).Replace("{ext}", ext)
            .Replace("{op}", opTitle).Replace("{date}", (now ?? DateTime.Now).ToString("yyyy-MM-dd"));
        // Anything the user typed that looks like a path becomes part of the name, never a folder.
        s = s.Replace('/', '-').Replace('\\', '-');
        if (!s.EndsWith("." + ext, StringComparison.OrdinalIgnoreCase) && ext.Length > 0) s += "." + ext;
        var stem = SafePaths.SanitizeFileName(Path.GetFileNameWithoutExtension(s));
        if (string.IsNullOrWhiteSpace(stem) || stem.Trim('.', ' ').Length == 0) stem = name;
        return ext.Length > 0 ? $"{stem}.{ext.ToLowerInvariant()}" : stem;
    }
}

public static class PresetJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        PropertyNameCaseInsensitive = true,
    };

    public sealed record File(int Version, List<Preset> Presets);

    public static string Export(IEnumerable<Preset> presets) => JsonSerializer.Serialize(new File(1, presets.ToList()), Options);

    /// <summary>Parses and validates presets against the registry: unknown operations or bad options are refused with a reason.</summary>
    public static List<Preset> Import(string json, OperationRegistry registry)
    {
        File? f;
        try { f = JsonSerializer.Deserialize<File>(json, Options); }
        catch (JsonException e) { throw new OrangeException("That isn't an Orange presets file.", e.Message); }
        if (f?.Presets is null || f.Presets.Count == 0) throw new OrangeException("The file has no presets.");
        if (f.Presets.Count > 200) throw new OrangeException("Too many presets in one file (200 max).");
        foreach (var p in f.Presets) Validate(p, registry);
        return f.Presets;
    }

    public static void Validate(Preset p, OperationRegistry registry)
    {
        if (string.IsNullOrWhiteSpace(p.Name) || p.Name.Length > 60) throw new OrangeException("A preset needs a name of 1-60 characters.");
        var op = registry.All.FirstOrDefault(o => o.Id == p.OperationId) ?? throw new OrangeException($"Preset \"{p.Name}\": unknown operation \"{p.OperationId}\".");
        if (op.CombinesInputs || op.ProducesFolder) throw new OrangeException($"Preset \"{p.Name}\": {op.Title} can't be used in a preset.");
        OptionValidator.Validate(new OperationRequest { Operation = op, Inputs = Array.Empty<string>(), Options = p.Options });
        if (p.Output.NamePattern.Length is 0 or > 120 || !p.Output.NamePattern.Contains("{name}"))
            throw new OrangeException($"Preset \"{p.Name}\": the name pattern must contain {{name}} (max 120 characters).");
        if (p.Output.Location == OutputLocation.Subfolder &&
            (string.IsNullOrWhiteSpace(p.Output.Subfolder) || p.Output.Subfolder.IndexOfAny(['/', '\\', ':']) >= 0 || p.Output.Subfolder.Trim('.', ' ').Length == 0))
            throw new OrangeException($"Preset \"{p.Name}\": the subfolder must be a single folder name.");
    }
}

public static class DefaultPresets
{
    public static IReadOnlyList<Preset> All { get; } =
    [
        new() { Name = "WhatsApp video", OperationId = "video.compress", Options = new() { ["targetMB"] = "15", ["maxHeight"] = "720" },
                Output = new() { NamePattern = "{name} WhatsApp.{ext}" } },
        new() { Name = "Discord video", OperationId = "video.compress", Options = new() { ["targetMB"] = "9.5" },
                Output = new() { NamePattern = "{name} Discord.{ext}" } },
        new() { Name = "Email PDF", OperationId = "pdf.compress", Options = new() { ["targetMB"] = "1.9" },
                Output = new() { NamePattern = "{name} email.{ext}" } },
        new() { Name = "Web image", OperationId = "image.compress", Options = new() { ["quality"] = "80", ["maxSide"] = "1920" },
                Metadata = MetadataPolicy.Strip, Output = new() { NamePattern = "{name} web.{ext}" } },
        new() { Name = "WebP for web", OperationId = "image.to.webp", Options = new() { ["quality"] = "80" },
                Metadata = MetadataPolicy.Strip, PinnedOnWheel = false, Output = new() { NamePattern = "{name}.{ext}" } },
        new() { Name = "Podcast MP3", OperationId = "audio.to.mp3", Options = new() { ["bitrate"] = "128" },
                Output = new() { Location = OutputLocation.Subfolder, Subfolder = "MP3", NamePattern = "{name}.{ext}" } },
    ];
}
