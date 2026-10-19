using System.Text.Json;
using System.Text.Json.Serialization;
using Orange.Core;

namespace Orange.App.Services;

/// <remarks>WheelKey lives in Orange.Core.Input (shared with the unit-tested key interpreter).</remarks>
public enum DragSourceFilter { ExplorerAndDesktop, AnyApp }
public enum ThemeChoice { System, Light, Dark }

/// <summary>Everything the user can change, stored as JSON in %LOCALAPPDATA%\Orange\settings.json. No registry, no cloud.</summary>
public sealed class AppSettings
{
    public bool DragActivation { get; set; } = true;
    public WheelKey ConvertKey { get; set; } = WheelKey.Shift;
    public WheelKey ToolsKey { get; set; } = WheelKey.ShiftDouble;  // tap Shift twice
    public WheelKey ActionsKey { get; set; } = WheelKey.ShiftCtrl;
    /// <summary>
    /// 3 = 0.2 keys (Shift / Shift twice / Shift+Ctrl), chosen because Explorer gives plain Ctrl and Alt meanings
    /// (copy / link) during a drag. Older files are migrated once on load, only if the user kept the old defaults.
    /// </summary>
    public int SettingsVersion { get; set; } = 3;
    /// <summary>How many times the wheel has shown the key hint in its hub (shown for the first few drags).</summary>
    public int KeyHintsShown { get; set; }
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
    /// <summary>Local troubleshooting log in %LOCALAPPDATA%\Orange\logs. Off by default; never uploaded.</summary>
    public bool DebugLog { get; set; }
    [JsonIgnore] public static string LogFolder => Path.Combine(Folder, "logs");
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
                int version = 1;
                using (var doc = JsonDocument.Parse(json))
                    if (doc.RootElement.TryGetProperty(nameof(SettingsVersion), out var v) && v.TryGetInt32(out var n)) version = n;
                if (version < 2 && loaded.ToolsKey == WheelKey.Ctrl && loaded.ActionsKey == WheelKey.Alt)
                {   // 0.1.0 defaults (Ctrl = tools, Alt = actions) → 0.1 late defaults.
                    loaded.ToolsKey = WheelKey.AltShift; loaded.ActionsKey = WheelKey.Ctrl;
                }
                if (version < 3 && loaded.ConvertKey == WheelKey.Shift && loaded.ToolsKey == WheelKey.AltShift && loaded.ActionsKey == WheelKey.Ctrl)
                {   // Still on the 0.1 defaults → 0.2 defaults. Anyone who picked their own keys keeps them.
                    loaded.ToolsKey = WheelKey.ShiftDouble; loaded.ActionsKey = WheelKey.ShiftCtrl; loaded.KeyHintsShown = 0;
                }
                loaded.SettingsVersion = 3;
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
        if (DebugLog || Environment.GetEnvironmentVariable("ORANGE_DEBUG_LOG") == "1") { if (!Orange.Core.Logging.LocalLog.Enabled) Orange.Core.Logging.LocalLog.Enable(LogFolder); }
        else Orange.Core.Logging.LocalLog.Disable();
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
