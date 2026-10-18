using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Orange.App.Interop;
using Orange.App.Services;
using Orange.App.Views;
using Orange.Core;

namespace Orange.App;

public partial class App : Application
{
    public static MainWindow Main { get; private set; } = null!;
    public static DragWatcher? Watcher { get; private set; }
    public static TrayAndHotkey? Tray { get; private set; }
    public static int ExitCode { get; set; }
    private static DispatcherQueue? _ui;
    private static readonly List<string> Pending = new();
    private static DispatcherQueueTimer? _pendingTimer;

    public App()
    {
        InitializeComponent();
        // Orange lives in the tray: closing the wheel or a progress window must never quit the app.
        DispatcherShutdownMode = DispatcherShutdownMode.OnExplicitShutdown;
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Diagnostics.Event("domain-unhandled " + e.ExceptionObject.ToString()!.Replace(Environment.NewLine, " | "));
        TaskScheduler.UnobservedTaskException += (_, e) => { e.SetObserved(); Diagnostics.Event("task-unobserved " + e.Exception.ToString().Replace(Environment.NewLine, " | ")); };
        UnhandledException += (_, e) =>
        {
            e.Handled = true;
            try { File.AppendAllText(Path.Combine(AppSettings.Folder, "crash.log"), $"{DateTime.Now:O} {e.Exception}\n"); } catch { }
            Diagnostics.Event("unhandled " + e.Exception.ToString().Replace(Environment.NewLine, " | "));
        };
    }

    public static ElementTheme ThemeFor(ThemeChoice t) => t switch { ThemeChoice.Light => ElementTheme.Light, ThemeChoice.Dark => ElementTheme.Dark, _ => ElementTheme.Default };

    protected override async void OnLaunched(LaunchActivatedEventArgs launch)
    {
        _ui = DispatcherQueue.GetForCurrentThread();
        var args = Environment.GetCommandLineArgs().Skip(1).ToList();
        var settings = AppSettings.Current;
        settings.Apply();
        SafePaths.TempWorkspace.CleanupStale();

        if (args.Count >= 1 && args[0] == "--selftest")
        {
            ExitCode = 3;
            try { ExitCode = await SelfTest.RunAsync(args.Count > 1 ? args[1] : Path.Combine(Path.GetTempPath(), "orange-selftest")); }
            catch (Exception e) { Diagnostics.Event("selftest-crashed " + e); ExitCode = 4; }
            Exit();
            return;
        }

        Step("notifications", ConversionService.InitNotifications);
        Step("main-window", () => Main = new MainWindow());

        Step("tray", () => { Tray = new TrayAndHotkey(); Tray.ShowTray(); });
        Tray ??= new TrayAndHotkey();
        Tray.OpenRequested += () => Main.ShowWindow();
        Tray.SettingsRequested += () => { Main.ShowWindow(); };
        Tray.QuitRequested += Quit;
        Tray.HotkeyPressed += OnHotkey;
        if (settings.HotkeyEnabled) Step("hotkey", () => Tray.RegisterHotkey(settings.Hotkey));

        Watcher = new DragWatcher(_ui) { Enabled = settings.DragActivation };
        Watcher.WheelRequested += (kind, pt, src) =>
        {
            Diagnostics.Event($"wheel-requested {kind} at={pt.X},{pt.Y}");
            // Open at once; the Explorer selection (a cross-process COM call) is read off the UI thread so the
            // input hooks are never starved, and the real drag payload replaces it on DragEnter anyway.
            var w = WheelWindow.Open(kind, Array.Empty<string>(), pt, dragMode: true, Watcher);
            _ = ExplorerSelection.GetAsync(src, TimeSpan.FromMilliseconds(1500)).ContinueWith(t =>
            {
                var files = t.Result;
                Diagnostics.Event($"selection {files.Count}");
                if (files.Count > 0) _ui!.TryEnqueue(() => w.SuggestFiles(files));
            });
        };
        try { Watcher.Start(); }
        catch (Exception e) { Diagnostics.Event("hooks-failed " + e.Message); ProgressWindow.ShowError("Orange", "Drag activation is unavailable on this PC.", e.Message); }

        if (args.Count > 0 && args[0] == "--wheel") Queue(args.Skip(1));
        else if (!args.Contains("--background")) Main.ShowWindow();
        Diagnostics.Event("ready");
    }

    private static void Step(string name, Action a)
    {
        try { a(); Diagnostics.Event("step-ok " + name); }
        catch (Exception e) { Diagnostics.Event($"step-failed {name} {e.ToString().Replace(Environment.NewLine, " | ")}"); }
    }

    /// <summary>A second launch's arguments arrive here (on a background thread).</summary>
    public static void OnRedirected(AppActivationArguments e)
    {
        if (e.Kind != ExtendedActivationKind.Launch || e.Data is not Windows.ApplicationModel.Activation.ILaunchActivatedEventArgs la) return;
        var argv = SplitArgs(la.Arguments ?? "");
        // The first token can be the exe path.
        if (argv.Count > 0 && argv[0].EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) argv.RemoveAt(0);
        _ui?.TryEnqueue(() =>
        {
            if (argv.Count > 0 && argv[0] == "--wheel") Queue(argv.Skip(1));
            else if (!argv.Contains("--background")) Main.ShowWindow();
        });
    }

    private static void Queue(IEnumerable<string> files)
    {
        foreach (var f in files) if (File.Exists(f) || Directory.Exists(f)) Pending.Add(Path.GetFullPath(f));
        _pendingTimer ??= CreateTimer();
        _pendingTimer.Stop(); _pendingTimer.Start();   // gather one-launch-per-file context-menu invocations
    }

    private static DispatcherQueueTimer CreateTimer()
    {
        var t = _ui!.CreateTimer();
        t.Interval = TimeSpan.FromMilliseconds(350);
        t.IsRepeating = false;
        t.Tick += (_, _) =>
        {
            var files = Pending.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            Pending.Clear();
            if (files.Count == 0) { Main.ShowWindow(); return; }
            Native.GetCursorPos(out var p);
            WheelWindow.Open(WheelKind.Convert, files, p, dragMode: false, Watcher);
        };
        return t;
    }

    private static void OnHotkey()
    {
        var files = ExplorerSelection.Get();
        Diagnostics.Event($"hotkey selection={files.Count}");
        if (files.Count == 0) { Main.ShowConvert(Array.Empty<string>(), ""); return; }
        Native.GetCursorPos(out var p);
        WheelWindow.Open(WheelKind.Convert, files, p, dragMode: false, Watcher);
    }

    private static void Quit()
    {
        Watcher?.Dispose();
        Tray?.Dispose();
        Current.Exit();
    }

    [DllImport("shell32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CommandLineToArgvW(string cmdLine, out int numArgs);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr h);

    public static List<string> SplitArgs(string cmd)
    {
        var list = new List<string>();
        if (string.IsNullOrWhiteSpace(cmd)) return list;
        var p = CommandLineToArgvW(cmd, out var n);
        if (p == IntPtr.Zero) return list;
        try { for (int i = 0; i < n; i++) list.Add(Marshal.PtrToStringUni(Marshal.ReadIntPtr(p, i * IntPtr.Size))!); }
        finally { LocalFree(p); }
        return list;
    }
}
