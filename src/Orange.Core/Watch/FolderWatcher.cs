using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Orange.Core.Watch;

/// <summary>One watched folder: new files that fit the preset are converted beside them (or per the preset's rule).</summary>
public sealed record WatchSpec
{
    public required string Folder { get; init; }
    public required string Preset { get; init; }
    /// <summary>After a successful conversion, move the original into "Originals" inside the watched folder.</summary>
    public bool MoveOriginals { get; init; }
    public bool Paused { get; init; }
}

public sealed record WatchEvent(string File, string Status, string? Output = null, string? Error = null);

/// <summary>
/// Watches one folder (not subfolders). A new file is handled once it has stopped growing for
/// <see cref="SettleMs"/> and can be opened exclusively (so downloads and copies are finished). It ignores:
/// files Orange itself wrote (outputs of this watcher), the "Originals" folder, partial-download and temp names,
/// and files the preset can't handle. Processing is serial, one file at a time.
/// </summary>
public sealed class FolderWatcher : IDisposable
{
    public const string OriginalsFolder = "Originals";
    private static readonly string[] PartialSuffixes = [".tmp", ".part", ".partial", ".crdownload", ".download", ".opdownload", ".!ut"];

    private readonly WatchSpec _spec;
    private readonly Func<string, CancellationToken, Task<IReadOnlyList<string>>> _process;
    private readonly FileSystemWatcher _fsw;
    private readonly ConcurrentDictionary<string, byte> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _ownOutputs = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _serial = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private volatile bool _paused;

    public int SettleMs { get; init; } = 2000;
    public int PollMs { get; init; } = 500;
    /// <summary>Gives up on a file that never settles (still growing or locked) after this long.</summary>
    public TimeSpan MaxWait { get; init; } = TimeSpan.FromHours(2);
    public event Action<WatchEvent>? Activity;
    public WatchSpec Spec => _spec;
    public bool Paused => _paused;

    /// <param name="process">Converts one file and returns the paths it wrote.</param>
    public FolderWatcher(WatchSpec spec, Func<string, CancellationToken, Task<IReadOnlyList<string>>> process)
    {
        if (!Directory.Exists(spec.Folder)) throw new OrangeException($"The folder \"{spec.Folder}\" doesn't exist.");
        _spec = spec with { Folder = Path.GetFullPath(spec.Folder) };
        _process = process;
        _paused = spec.Paused;
        _fsw = new FileSystemWatcher(_spec.Folder) { IncludeSubdirectories = false, NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite };
        _fsw.Created += (_, e) => Enqueue(e.FullPath);
        _fsw.Renamed += (_, e) => Enqueue(e.FullPath);   // browsers rename x.crdownload → x.mp4 when done
        _fsw.EnableRaisingEvents = true;
    }

    public void Pause() => _paused = true;
    public void Resume() => _paused = false;

    /// <summary>True for names a watcher must never pick up.</summary>
    public static bool IsIgnoredName(string path)
    {
        var name = Path.GetFileName(path);
        if (name.StartsWith('.') || name.StartsWith("~$") || name.EndsWith('~')) return true;
        if (name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase) || name.Equals("thumbs.db", StringComparison.OrdinalIgnoreCase)) return true;
        return PartialSuffixes.Any(s => name.EndsWith(s, StringComparison.OrdinalIgnoreCase));
    }

    private void Enqueue(string path)
    {
        if (_cts.IsCancellationRequested || IsIgnoredName(path) || _ownOutputs.ContainsKey(path)) return;
        if (!string.Equals(Path.GetDirectoryName(path), _spec.Folder, StringComparison.OrdinalIgnoreCase)) return;
        if (!_pending.TryAdd(path, 0)) return;
        _ = Task.Run(() => HandleAsync(path));
    }

    private async Task HandleAsync(string path)
    {
        try
        {
            if (!await WaitUntilSettledAsync(path, _cts.Token).ConfigureAwait(false)) return;
            while (_paused && !_cts.IsCancellationRequested) await Task.Delay(PollMs, _cts.Token).ConfigureAwait(false);
            await _serial.WaitAsync(_cts.Token).ConfigureAwait(false);
            try
            {
                if (!File.Exists(path) || _ownOutputs.ContainsKey(path)) return;
                Activity?.Invoke(new WatchEvent(path, "converting"));
                IReadOnlyList<string> outputs;
                try { outputs = await _process(path, _cts.Token).ConfigureAwait(false); }
                catch (OrangeException e) { Activity?.Invoke(new WatchEvent(path, "skipped", Error: e.Message)); return; }
                foreach (var o in outputs) _ownOutputs[Path.GetFullPath(o)] = 0;
                if (outputs.Count == 0) { Activity?.Invoke(new WatchEvent(path, "failed", Error: "Nothing was written.")); return; }
                if (_spec.MoveOriginals) MoveOriginal(path);
                Activity?.Invoke(new WatchEvent(path, "done", outputs[0]));
            }
            finally { _serial.Release(); }
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { Activity?.Invoke(new WatchEvent(path, "failed", Error: e.Message)); }
        finally { _pending.TryRemove(path, out _); }
    }

    /// <summary>Size unchanged for SettleMs and the file opens with FileShare.None.</summary>
    private async Task<bool> WaitUntilSettledAsync(string path, CancellationToken ct)
    {
        long last = -1; var stableSince = DateTime.UtcNow; var start = DateTime.UtcNow;
        while (DateTime.UtcNow - start < MaxWait)
        {
            ct.ThrowIfCancellationRequested();
            if (!File.Exists(path)) return false;
            long size;
            try { size = new FileInfo(path).Length; } catch { size = -2; }
            if (size != last) { last = size; stableSince = DateTime.UtcNow; }
            else if ((DateTime.UtcNow - stableSince).TotalMilliseconds >= SettleMs && size >= 0)
            {
                try { using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None); return true; }
                catch (IOException) { stableSince = DateTime.UtcNow; }   // still locked by the writer
                catch (UnauthorizedAccessException) { return false; }
            }
            await Task.Delay(PollMs, ct).ConfigureAwait(false);
        }
        Activity?.Invoke(new WatchEvent(path, "skipped", Error: "The file never finished writing."));
        return false;
    }

    private void MoveOriginal(string path)
    {
        var dir = Path.Combine(_spec.Folder, OriginalsFolder);
        Directory.CreateDirectory(dir);
        var target = SafePaths.PlanOutput(path, dir, Path.GetFileNameWithoutExtension(path), null, FileTypeDetector.Ext(path), OverwritePolicy.KeepBoth, [path]);
        File.Move(path, target);
    }

    public void Dispose()
    {
        _cts.Cancel();
        _fsw.EnableRaisingEvents = false;
        _fsw.Dispose();
    }
}

/// <summary>watches.json beside settings.json; the tray app restores watchers from it at start-up.</summary>
public sealed class WatchStore(string path)
{
    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Orange", "watches.json");
    public static WatchStore Default { get; } = new(DefaultPath);
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    public List<WatchSpec> Load()
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<List<WatchSpec>>(File.ReadAllText(path), Json) ?? new() : new(); }
        catch { return new(); }
    }

    public void Save(IEnumerable<WatchSpec> specs)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(specs.ToList(), Json));
        File.Move(tmp, path, overwrite: true);
    }
}

/// <summary>Glue: a watcher whose processing is a preset run.</summary>
public static class PresetWatch
{
    public static FolderWatcher Create(WatchSpec spec, OperationRegistry registry, IReadOnlyList<Presets.Preset> presets)
    {
        var preset = Presets.PresetStore.Find(presets, spec.Preset) ?? throw new OrangeException($"There's no preset called \"{spec.Preset}\".");
        var runner = new Presets.PresetRunner(registry);
        return new FolderWatcher(spec, async (file, ct) =>
        {
            if (runner.ForFiles([preset], [file]).Count == 0) throw new OrangeException($"\"{preset.Name}\" doesn't apply to this file.");
            var r = await runner.RunAsync(preset, [file], null, ct).ConfigureAwait(false);
            if (r[0].State == Presets.PresetFileState.Failed) throw new OrangeException(r[0].Error ?? "Failed.");
            return r.Where(x => x.Output != null && x.State == Presets.PresetFileState.Done).Select(x => x.Output!).ToList();
        });
    }
}
