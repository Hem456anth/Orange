using Microsoft.UI.Dispatching;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using Orange.App.Interop;
using Orange.App.Views;
using Orange.Core;

namespace Orange.App.Services;

/// <summary>Runs a chosen operation on dropped files: plans jobs, shows progress, notifies, records recents.</summary>
public sealed class ConversionService
{
    public static ConversionService Instance { get; } = new();
    public OperationRegistry Registry { get; } = OperationRegistry.CreateDefault();
    private JobRunner Runner => new(Registry);
    private static bool _notificationsReady;

    public static void InitNotifications()
    {
        try
        {
            AppNotificationManager.Default.NotificationInvoked += (_, args) =>
            {
                if (args.Arguments.TryGetValue("reveal", out var p) && !string.IsNullOrEmpty(p))
                    Native.RevealInExplorer(p.Split('|'));
            };
            AppNotificationManager.Default.Register();
            _notificationsReady = true;
        }
        catch { _notificationsReady = false; }   // e.g. notifications disabled by policy: the progress window still reports
    }

    /// <summary>Returns the options to use: the user's last choice for this operation, else defaults.</summary>
    public IReadOnlyDictionary<string, string> RememberedOptions(OperationDescriptor op) =>
        AppSettings.Current.LastOptions.TryGetValue(op.Id, out var o) ? o.Where(kv => op.Options.Any(s => s.Name == kv.Key)).ToDictionary() : new();

    public async Task<IReadOnlyList<Job>> RunAsync(OperationDescriptor op, IReadOnlyList<string> files, IReadOnlyDictionary<string, string>? options, DispatcherQueue ui)
    {
        var s = AppSettings.Current;
        options ??= RememberedOptions(op);
        IReadOnlyList<Job> jobs;
        try
        {
            jobs = Runner.Plan(op, files, options, s.OutputFolder is { Length: > 0 } f && Directory.Exists(f) ? f : null, s.Overwrite);
        }
        catch (OrangeException e)
        {
            ProgressWindow.ShowError(op.Title, e.Message, e.Detail);
            Sounds.Error();
            return Array.Empty<Job>();
        }
        if (options.Count > 0) { s.LastOptions[op.Id] = options.ToDictionary(); s.Save(); }

        Diagnostics.Event($"run-start {op.Id} {jobs.Count}");
        using var cts = new CancellationTokenSource();
        ProgressWindow? win = null;
        try { win = ProgressWindow.Show(op, jobs); win.CancelRequested += cts.Cancel; }
        catch (Exception e) { Diagnostics.Event("progress-window-failed " + Flat(e)); }
        var progress = new Progress<BatchProgress>(p => { try { win?.Update(p); } catch (Exception e) { Diagnostics.Event("progress-update-failed " + Flat(e)); } });
        Diagnostics.Event("jobs-running");
        await Runner.RunAsync(jobs, progress, cts.Token);
        Diagnostics.Event("jobs-finished " + string.Join(',', jobs.Select(j => j.State)));
        try { win?.Complete(jobs); } catch (Exception e) { Diagnostics.Event("progress-complete-failed " + Flat(e)); }

        var outputs = jobs.Where(j => j.State == JobState.Succeeded).SelectMany(j => j.Result!.Outputs).ToList();
        var failed = jobs.Where(j => j.State == JobState.Failed).ToList();
        Diagnostics.Event($"run-done {op.Id} ok={outputs.Count} failed={failed.Count} outputs={string.Join('|', outputs)}");
        if (outputs.Count > 0)
        {
            s.AddRecent(op, files.Count, outputs);
            if (s.RevealWhenDone) Native.RevealInExplorer(outputs);
            if (failed.Count == 0) Sounds.Success(); else Sounds.Error();
        }
        else if (failed.Count > 0) Sounds.Error();
        if (s.NotifyWhenDone) Toast(op, outputs, failed);
        return jobs;
    }

    private static string Flat(Exception e) => e.ToString().Replace(Environment.NewLine, " | ");

    private static void Toast(OperationDescriptor op, List<string> outputs, List<Job> failed)
    {
        if (!_notificationsReady) return;
        try
        {
            var b = new AppNotificationBuilder();
            if (failed.Count == 0)
                b.AddText(outputs.Count == 1 ? $"Saved {Path.GetFileName(outputs[0])}" : $"{outputs.Count} files saved")
                 .AddText($"{op.Title} finished on this PC. Nothing was uploaded.");
            else
                b.AddText($"{failed.Count} of {failed.Count + outputs.Count} couldn't be converted")
                 .AddText(failed[0].Result?.Error ?? "Unknown error");
            if (outputs.Count > 0) b.AddButton(new AppNotificationButton("Show in folder").AddArgument("reveal", string.Join('|', outputs.Take(20))));
            AppNotificationManager.Default.Show(b.BuildNotification());
        }
        catch { /* best effort */ }
    }
}

/// <summary>Short system sounds (respects the Settings switch and Windows' own sound scheme).</summary>
public static class Sounds
{
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool MessageBeep(uint type);
    public static void Tick() { }   // a per-segment tick would need a bundled WAV; kept silent by design
    public static void Success() { if (AppSettings.Current.PlaySounds) MessageBeep(0x40); }
    public static void Error() { if (AppSettings.Current.PlaySounds) MessageBeep(0x10); }
}
