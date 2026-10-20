using System.Runtime.InteropServices;
using Orange.App.Interop;

namespace Orange.App.Services;

/// <summary>
/// A hidden message-only window that owns the notification-area icon and the global shortcut.
/// Runs on the UI thread, whose message loop WinUI already pumps.
/// </summary>
public sealed class TrayAndHotkey : IDisposable
{
    private const int TrayMsg = Native.WM_USER + 1, HotkeyId = 0x4F52;
    private readonly Native.WndProc _proc;
    private readonly IntPtr _hwnd;
    private IntPtr _icon;
    private bool _trayAdded, _hotkeyRegistered;

    public event Action? OpenRequested;
    public event Action? HotkeyPressed;
    public event Action? QuitRequested;
    public event Action? SettingsRequested;

    public string? HotkeyError { get; private set; }

    public TrayAndHotkey()
    {
        _proc = WndProc;
        var cls = new Native.WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<Native.WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc),
            hInstance = Native.GetModuleHandle(null),
            lpszClassName = "OrangeMessageWindow",
        };
        Native.RegisterClassEx(ref cls);
        _hwnd = Native.CreateWindowEx(0, cls.lpszClassName, "Orange", 0, 0, 0, 0, 0, Native.HWND_MESSAGE, IntPtr.Zero, cls.hInstance, IntPtr.Zero);
    }

    public void ShowTray()
    {
        var ico = Path.Combine(AppContext.BaseDirectory, "Assets", "orange.ico");
        _icon = Native.LoadImage(IntPtr.Zero, ico, 1 /*IMAGE_ICON*/, 0, 0, 0x10 /*LR_LOADFROMFILE*/ | 0x40 /*LR_DEFAULTSIZE*/);
        var d = Data();
        d.uFlags = Native.NIF_MESSAGE | Native.NIF_ICON | Native.NIF_TIP | Native.NIF_SHOWTIP;
        d.uCallbackMessage = TrayMsg; d.hIcon = _icon; d.szTip = "Orange — drag files, tap Shift";
        _trayAdded = Native.Shell_NotifyIcon(Native.NIM_ADD, ref d);
        d.uVersion = 4; Native.Shell_NotifyIcon(Native.NIM_SETVERSION, ref d);
    }

    /// <summary>Registers "Ctrl+Alt+O"-style text. Returns false with <see cref="HotkeyError"/> set when taken.</summary>
    public bool RegisterHotkey(string spec)
    {
        if (_hotkeyRegistered) { Native.UnregisterHotKey(_hwnd, HotkeyId); _hotkeyRegistered = false; }
        if (!TryParse(spec, out var mods, out var vk)) { HotkeyError = $"\"{spec}\" isn't a valid shortcut."; return false; }
        _hotkeyRegistered = Native.RegisterHotKey(_hwnd, HotkeyId, mods | Native.MOD_NOREPEAT, vk);
        HotkeyError = _hotkeyRegistered ? null : $"{spec} is already used by another app. Pick another shortcut in Settings.";
        return _hotkeyRegistered;
    }

    public static bool TryParse(string spec, out uint mods, out uint vk)
    {
        mods = 0; vk = 0;
        foreach (var raw in spec.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl" or "control": mods |= Native.MOD_CONTROL; break;
                case "alt": mods |= Native.MOD_ALT; break;
                case "shift": mods |= Native.MOD_SHIFT; break;
                case "win": mods |= Native.MOD_WIN; break;
                case var k when k.Length == 1 && char.IsLetterOrDigit(k[0]): vk = char.ToUpperInvariant(k[0]); break;
                case var k when k.StartsWith('f') && int.TryParse(k[1..], out var f) && f is >= 1 and <= 24: vk = (uint)(0x70 + f - 1); break;
                case "space": vk = 0x20; break;
                default: return false;
            }
        }
        return vk != 0 && mods != 0;
    }

    private Native.NOTIFYICONDATA Data() => new()
    {
        cbSize = Marshal.SizeOf<Native.NOTIFYICONDATA>(), hWnd = _hwnd, uID = 1, szTip = "", szInfo = "", szInfoTitle = "",
    };

    private IntPtr WndProc(IntPtr h, uint msg, IntPtr w, IntPtr l)
    {
        if (msg == Native.WM_HOTKEY && (int)w == HotkeyId) { HotkeyPressed?.Invoke(); return IntPtr.Zero; }
        if (msg == TrayMsg)
        {
            int ev = (int)((long)l & 0xFFFF);
            if (ev is Native.WM_LBUTTONUP or Native.WM_LBUTTONDBLCLK) OpenRequested?.Invoke();
            else if (ev is Native.WM_RBUTTONUP or 0x7B /*WM_CONTEXTMENU*/) ShowMenu();
            return IntPtr.Zero;
        }
        return Native.DefWindowProc(h, msg, w, l);
    }

    public IntPtr MessageWindow => _hwnd;
    /// <summary>Self-test: opens the tray menu at the cursor (blocks until it closes).</summary>
    public void ShowMenuForTest() => ShowMenu();

    private void ShowMenu()
    {
        var m = Native.CreatePopupMenu();
        Native.AppendMenu(m, Native.MF_STRING, 1, "Open Orange");
        Native.AppendMenu(m, Native.MF_STRING, 2, "Settings");
        if (WatchService.Count > 0)
            Native.AppendMenu(m, Native.MF_STRING, 4, WatchService.Paused ? $"Resume watch folders ({WatchService.Count})" : $"Pause watch folders ({WatchService.Count})");
        Native.AppendMenu(m, Native.MF_SEPARATOR, 0, null);
        Native.AppendMenu(m, Native.MF_STRING, 3, "Quit Orange");
        Native.GetCursorPos(out var p);
        Native.SetForegroundWindow(_hwnd);
        var cmd = Native.TrackPopupMenuEx(m, Native.TPM_RETURNCMD | Native.TPM_RIGHTBUTTON, p.X, p.Y, _hwnd, IntPtr.Zero);
        Native.DestroyMenu(m);
        switch (cmd) { case 1: OpenRequested?.Invoke(); break; case 2: SettingsRequested?.Invoke(); break; case 3: QuitRequested?.Invoke(); break; case 4: WatchService.SetPaused(!WatchService.Paused); break; }
    }

    public void Dispose()
    {
        if (_trayAdded) { var d = Data(); Native.Shell_NotifyIcon(Native.NIM_DELETE, ref d); }
        if (_hotkeyRegistered) Native.UnregisterHotKey(_hwnd, HotkeyId);
        if (_icon != IntPtr.Zero) Native.DestroyIcon(_icon);
        Native.DestroyWindow(_hwnd);
    }
}
