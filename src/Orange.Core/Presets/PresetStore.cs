namespace Orange.Core.Presets;

/// <summary>presets.json beside settings.json (%LOCALAPPDATA%\Orange). Shared by the app, the CLI and watch folders.</summary>
public sealed class PresetStore(string path)
{
    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Orange", "presets.json");
    public static PresetStore Default { get; } = new(DefaultPath);

    public string FilePath { get; } = path;
    private static readonly object Gate = new();

    /// <summary>The saved presets, or the six defaults when nothing has been saved yet. A broken file is kept as .bad.</summary>
    public List<Preset> Load(OperationRegistry registry)
    {
        lock (Gate)
        {
            if (!File.Exists(FilePath)) return DefaultPresets.All.ToList();
            try { return PresetJson.Import(File.ReadAllText(FilePath), registry); }
            catch
            {
                try { File.Copy(FilePath, FilePath + ".bad", true); } catch { }
                return DefaultPresets.All.ToList();
            }
        }
    }

    public void Save(IEnumerable<Preset> presets, OperationRegistry registry)
    {
        var list = presets.ToList();
        foreach (var p in list) PresetJson.Validate(p, registry);
        if (list.Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != list.Count)
            throw new OrangeException("Two presets have the same name.");
        lock (Gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, PresetJson.Export(list));
            File.Move(tmp, FilePath, overwrite: true);
        }
    }

    public static Preset? Find(IEnumerable<Preset> presets, string name) =>
        presets.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
}
