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
        WinRT.ComWrappersSupport.InitializeComWrappers();
        if (args.Contains("--selftest") || args.Contains("--cli"))
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
        Application.Start(_ =>
        {
            var ctx = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(ctx);
            _ = new App();
        });
        return App.ExitCode;
    }

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
