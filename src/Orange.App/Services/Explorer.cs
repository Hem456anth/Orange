using System.Runtime.InteropServices;
using Microsoft.Win32;
using Orange.App.Interop;

namespace Orange.App.Services;

/// <summary>Reads the current selection of File Explorer windows through the documented Shell.Application automation object.</summary>
public static class ExplorerSelection
{
    /// <summary>Selected file paths in the Explorer window <paramref name="hwnd"/> (or the foreground one). Empty when none.</summary>
    public static IReadOnlyList<string> Get(IntPtr hwnd = default)
    {
        if (hwnd == IntPtr.Zero) hwnd = Native.GetForegroundWindow();
        var top = Native.GetAncestor(hwnd, 2 /*GA_ROOT*/);
        var result = new List<string>();
        object? shell = null;
        try
        {
            var t = Type.GetTypeFromProgID("Shell.Application");
            if (t is null) return result;
            shell = Activator.CreateInstance(t);
            dynamic windows = ((dynamic)shell!).Windows();
            int count = windows.Count;
            for (int i = 0; i < count; i++)
            {
                dynamic? w = null;
                try
                {
                    w = windows.Item(i);
                    if (w is null) continue;
                    if (new IntPtr((long)w.HWND) != top) continue;
                    dynamic items = w.Document.SelectedItems();
                    int n = items.Count;
                    for (int k = 0; k < n; k++)
                    {
                        string p = items.Item(k).Path;
                        if (!string.IsNullOrEmpty(p) && (File.Exists(p) || Directory.Exists(p))) result.Add(p);
                    }
                    break;
                }
                catch (COMException) { /* window closing, or not a folder view */ }
                finally { if (w is not null) Marshal.FinalReleaseComObject(w); }
            }
        }
        catch (Exception) { /* Explorer not running or automation blocked by policy */ }
        finally { if (shell is not null) Marshal.FinalReleaseComObject(shell); }
        return result;
    }

    public static bool IsExplorerWindow(IntPtr hwnd)
    {
        var root = Native.GetAncestor(hwnd, 2);
        var cls = Native.ClassName(root);
        if (cls is "CabinetWClass" or "ExploreWClass" or "Progman" or "WorkerW") return true;
        return string.Equals(Native.ProcessName(root), "explorer", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsDesktop(IntPtr hwnd) => Native.ClassName(Native.GetAncestor(hwnd, 2)) is "Progman" or "WorkerW";
}

/// <summary>
/// Optional Explorer entry points, all per-user (HKCU / %APPDATA%), no admin rights, removable from Settings:
///  - classic context-menu verb "Convert with Orange" (Windows 11 shows it under "Show more options");
///  - a Send To shortcut (passes the whole selection in one launch);
///  - start with Windows (HKCU Run key).
/// A Windows 11 top-level context-menu entry would need an IExplorerCommand in an MSIX/sparse package; see docs.
/// </summary>
public static class ExplorerIntegration
{
    private const string VerbKey = @"Software\Classes\*\shell\Orange";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private static string Exe => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Orange.exe");
    private static string SendToLink => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.SendTo), "Orange (convert).lnk");

    public static bool ContextMenuInstalled => Registry.CurrentUser.OpenSubKey(VerbKey) is not null;
    public static bool SendToInstalled => File.Exists(SendToLink);
    public static bool AutostartInstalled => Registry.CurrentUser.OpenSubKey(RunKey)?.GetValue("Orange") is not null;

    public static void SetContextMenu(bool on)
    {
        if (!on) { Registry.CurrentUser.DeleteSubKeyTree(VerbKey, false); Notify(); return; }
        using var k = Registry.CurrentUser.CreateSubKey(VerbKey);
        k.SetValue("", "Convert with Orange");
        k.SetValue("MUIVerb", "Convert with Orange");
        k.SetValue("Icon", $"\"{Exe}\",0");
        // Each selected file launches Orange once; the running instance gathers them into one wheel (see SingleInstance).
        k.SetValue("MultiSelectModel", "Player");
        using var c = k.CreateSubKey("command");
        c.SetValue("", $"\"{Exe}\" --wheel \"%1\"");
        Notify();
    }

    public static void SetSendTo(bool on)
    {
        if (!on) { if (File.Exists(SendToLink)) File.Delete(SendToLink); return; }
        var t = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("Windows Script Host is disabled on this PC.");
        dynamic sh = Activator.CreateInstance(t)!;
        try
        {
            dynamic lnk = sh.CreateShortcut(SendToLink);
            lnk.TargetPath = Exe;
            lnk.Arguments = "--wheel";
            lnk.IconLocation = Exe + ",0";
            lnk.Description = "Open the Orange wheel for these files";
            lnk.Save();
            Marshal.FinalReleaseComObject(lnk);
        }
        finally { Marshal.FinalReleaseComObject(sh); }
    }

    public static void SetAutostart(bool on)
    {
        using var k = Registry.CurrentUser.CreateSubKey(RunKey);
        if (on) k.SetValue("Orange", $"\"{Exe}\" --background"); else k.DeleteValue("Orange", false);
    }

    private static void Notify() => Native.SHChangeNotify(0x08000000 /*SHCNE_ASSOCCHANGED*/, 0, IntPtr.Zero, IntPtr.Zero);
}
