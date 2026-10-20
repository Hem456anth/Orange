using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace Orange.App;

/// <summary>
/// Custom entry point: one Orange per user. A second launch (context-menu verb, Send To, shortcut) forwards its
/// arguments to the running instance and exits, so a 10-file selection becomes one wheel instead of ten windows.
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // "Orange.exe --cli convert …" is the command line (cli\orange.cmd calls it): attach to the caller's console,
        // run the engine without starting any UI, and exit with the CLI's code. One runtime, no second copy.
        if (args.Length > 0 && args[0] == "--cli")
        {
            AttachConsole(-1);
            var stdout = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
            var stderr = new StreamWriter(Console.OpenStandardError()) { AutoFlush = true };
            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
            return Orange.Cli.CliApp.RunAsync(args[1..], stdout, stderr, cts.Token).GetAwaiter().GetResult();
        }
        WinRT.ComWrappersSupport.InitializeComWrappers();
        if (args.Contains("--selftest"))
        {
            // Self-test runs as its own instance so CI can launch it next to a background Orange.
        }
        else
        {
            var main = AppInstance.FindOrRegisterForKey("Orange.Main." + Environment.UserName);
            if (!main.IsCurrent)
            {
                Redirect(main);
                return 0;
            }
            main.Activated += (_, e) => App.OnRedirected(e);
        }
        Application.Start(p =>
        {
            var ctx = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(ctx);
            _ = new App();
        });
        return App.ExitCode;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern bool AttachConsole(int pid);

    // RedirectActivationToAsync must not block the STA thread's COM calls: wait on a worker and pump nothing else.
    private static void Redirect(AppInstance target)
    {
        var args = AppInstance.GetCurrent().GetActivatedEventArgs();
        using var done = new ManualResetEvent(false);
        Task.Run(async () =>
        {
            try { await target.RedirectActivationToAsync(args); } finally { done.Set(); }
        });
        done.WaitOne(TimeSpan.FromSeconds(10));
    }
}
