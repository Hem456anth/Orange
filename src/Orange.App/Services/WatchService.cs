using Orange.Core;
using Orange.Core.Watch;

namespace Orange.App.Services;

/// <summary>Runs the watch folders saved in watches.json while the tray app is running; pause/resume from the tray or Settings.</summary>
public static class WatchService
{
    private static readonly List<FolderWatcher> Active = new();
    public static bool Paused { get; private set; }
    public static IReadOnlyList<WatchSpec> Specs => WatchStore.Default.Load();
    public static int Count { get { lock (Active) return Active.Count; } }
    public static event Action<WatchEvent>? Activity;
    public static string? LastError { get; private set; }

    public static void Start()
    {
        lock (Active)
        {
            foreach (var w in Active) w.Dispose();
            Active.Clear();
            LastError = null;
            foreach (var spec in WatchStore.Default.Load())
            {
                try
                {
                    var w = PresetWatch.Create(spec, ConversionService.Instance.Registry, ConversionService.Instance.Presets);
                    if (Paused || spec.Paused) w.Pause();
                    w.Activity += e => { Diagnostics.Event($"watch {e.Status} {e.File} {e.Output} {e.Error}"); Activity?.Invoke(e); };
                    Active.Add(w);
                }
                catch (Exception e) { LastError = $"{spec.Folder}: {e.Message}"; Diagnostics.Event("watch-failed " + LastError); }
            }
        }
    }

    public static void SetPaused(bool paused)
    {
        Paused = paused;
        lock (Active) foreach (var w in Active) { if (paused) w.Pause(); else if (!w.Spec.Paused) w.Resume(); }
    }

    public static void Add(WatchSpec spec)
    {
        var list = WatchStore.Default.Load().Where(s => !string.Equals(s.Folder, spec.Folder, StringComparison.OrdinalIgnoreCase)).Append(spec).ToList();
        WatchStore.Default.Save(list);
        Start();
    }

    public static void Remove(string folder)
    {
        WatchStore.Default.Save(WatchStore.Default.Load().Where(s => !string.Equals(s.Folder, folder, StringComparison.OrdinalIgnoreCase)));
        Start();
    }

    public static void Stop() { lock (Active) { foreach (var w in Active) w.Dispose(); Active.Clear(); } }
}
