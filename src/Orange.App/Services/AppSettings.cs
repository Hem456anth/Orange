using System.Text.Json;
using System.Text.Json.Serialization;
using Orange.Core;

namespace Orange.App.Services;

/// <summary>AltShift is Windows' Option+Shift: hold Alt and tap Shift (or the reverse) while dragging.</summary>
public enum WheelKey { Shift, Ctrl, Alt, AltShift }
public enum DragSourceFilter { ExplorerAndDesktop, AnyApp }
public enum ThemeChoice { System, Light, Dark }

/// <summary>Everything the user can change, stored as JSON in %LOCALAPPDATA%\Orange\settings.json. No registry, no cloud.</summary>
public sealed class AppSettings
{
    public bool DragActivation { get; set; } = true;
    public WheelKey ConvertKey { get; set; } = WheelKey.Shift;
    public WheelKey ToolsKey { get; set; } = WheelKey.AltShift;     // Tangerine: Option + Shift
    public WheelKey ActionsKey { get; set; } = WheelKey.Ctrl;
    /// <summary>2 = Alt+Shift tools wheel defaults. Older files are migrated once on load.</summary>
    public int SettingsVersion { get; set; } = 2;
    public DragSourceFilter DragSources { get; set; } = DragSourceFilter.ExplorerAndDesktop;
    /// <summary>Global shortcut that opens the wheel for the files selected in the active Explorer window.</summary>
    public string Hotkey { get; set; } = "Ctrl+Alt+O";
    public bool HotkeyEnabled { get; set; } = true;
    public string? OutputFolder { get; set; }              // null = next to the original (Tangerine behaviour)
    public OverwritePolicy Overwrite { get; set; } = OverwritePolicy.KeepBoth;
    public bool RevealWhenDone { get; set; } = true;
    public bool NotifyWhenDone { get; set; } = true;
    public bool AskOptionsForTools { get; set; } = true;   // tools with settings (trim, crop…) open a settings sheet first
    public bool PlaySounds { get; set; } = true;
    public ThemeChoice Theme { get; set; } = ThemeChoice.System;
    public bool StartWithWindows { get; set; } = true;
    public bool WelcomeShown { get; set; }
    public int MaxParallelJobs { get; set; } = Math.Max(1, Environment.ProcessorCount / 2);
    public double MaxInputGigabytes { get; set; } = 8;
    public Dictionary<string, string> ToolPaths { get; set; } = new();
    /// <summary>Last options used per operation, so "WebP at 70" sticks.</summary>
    public Dictionary<string, Dictionary<string, string>> LastOptions { get; set; } = new();
    public List<string> Favorites { get; set; } = new();
    public List<RecentEntry> Recent { get; set; } = new();

    public sealed record RecentEntry(string OperationId, string Title, string Category, DateTimeOffset When, int Files, IReadOnlyList<string> Outputs);

    [JsonIgnore] public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Orange");
    [JsonIgnore] private static string FilePath => Path.Combine(Folder, "settings.json");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    private static readonly object Gate = new();

    public static AppSettings Current { get; private set; } = Load();

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json, Json) ?? new AppSettings();
                if (!json.Contains("\"SettingsVersion\"") && loaded.ToolsKey == WheelKey.Ctrl && loaded.ActionsKey == WheelKey.Alt)
                {   // 0.1 defaults (Ctrl = tools, Alt = actions) → Tangerine's Alt+Shift for tools, Ctrl for actions.
                    loaded.ToolsKey = WheelKey.AltShift; loaded.ActionsKey = WheelKey.Ctrl;
                }
                loaded.SettingsVersion = 2;
                return loaded;
            }
        }
        catch { /* corrupt file: keep a copy and start fresh */ try { File.Copy(FilePath, FilePath + ".bad", true); } catch { } }
        return new AppSettings();
    }

    public void Save()
    {
        lock (Gate)
        {
            Directory.CreateDirectory(Folder);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
            File.Move(tmp, FilePath, overwrite: true);
        }
        Apply();
    }

    /// <summary>Pushes settings into the engine.</summary>
    public void Apply()
    {
        EngineLimits.Current = EngineLimits.Current with
        {
            MaxParallelJobs = Math.Clamp(MaxParallelJobs, 1, 16),
            MaxInputBytes = (long)(Math.Clamp(MaxInputGigabytes, 0.1, 200) * 1024 * 1024 * 1024),
        };
        foreach (var (tool, path) in ToolPaths) if (!string.IsNullOrWhiteSpace(path)) ToolLocator.Default.Override(tool, path);
    }

    public void AddRecent(OperationDescriptor op, int files, IReadOnlyList<string> outputs)
    {
        Recent.Insert(0, new RecentEntry(op.Id, op.Title, op.Category.ToString(), DateTimeOffset.Now, files, outputs.Take(5).ToList()));
        if (Recent.Count > 50) Recent.RemoveRange(50, Recent.Count - 50);
        Save();
    }

    public void ToggleFavorite(string opId)
    {
        if (!Favorites.Remove(opId)) Favorites.Add(opId);
        Save();
    }
}
