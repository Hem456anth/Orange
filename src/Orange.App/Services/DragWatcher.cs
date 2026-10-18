using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Orange.App.Interop;

namespace Orange.App.Services;

public enum WheelKind { Convert, Tools, Actions }

/// <summary>
/// Notices "a drag is in progress and the user tapped (or is holding) a wheel key" anywhere on the desktop.
///
/// How: low-level mouse and keyboard hooks (WH_MOUSE_LL / WH_KEYBOARD_LL). They observe input only; every event is
/// passed on unchanged, so Explorer's own drag (and its Shift = move / Ctrl = copy / Alt = link feedback) keeps
/// working. The hooks never read keystrokes beyond the three modifier keys and Escape, and nothing is logged.
/// A drag is "a left button press that moved past the system drag threshold"; by default it only counts when the
/// press started on a File Explorer window or the desktop (Settings → Drag sources).
///
/// What this can't know: whether the drag carries files. The wheel window learns that when the cursor is over it
/// (DragEnter gives the real data), and pre-fills from the Explorer selection, which is what Explorer drags.
/// </summary>
public sealed class DragWatcher : IDisposable
{
    private readonly DispatcherQueue _ui;
    private readonly Native.HookProc _mouseProc, _keyProc;   // kept alive: the OS holds raw pointers to these
    private IntPtr _mouseHook, _keyHook;
    private Native.POINT _downAt;
    private bool _buttonDown, _dragging, _eligible, _wheelOpen;
    private IntPtr _sourceWindow;
    private WheelKind _current;

    public event Action<WheelKind, Native.POINT, IntPtr>? WheelRequested;   // kind, cursor (physical px), drag source window
    public event Action<WheelKind>? WheelKindChanged;
    public event Action? DragEnded;
    public event Action? CancelRequested;

    public bool IsDragging => _dragging;
    public bool Enabled { get; set; } = true;

    public DragWatcher(DispatcherQueue ui)
    {
        _ui = ui;
        _mouseProc = MouseHook;
        _keyProc = KeyHook;
    }

    public void Start()
    {
        var mod = Native.GetModuleHandle(null);
        _mouseHook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, _mouseProc, mod, 0);
        _keyHook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, _keyProc, mod, 0);
        if (_mouseHook == IntPtr.Zero || _keyHook == IntPtr.Zero)
            throw new InvalidOperationException($"Couldn't watch for drags (error {Marshal.GetLastWin32Error()}). Use the shortcut or the context menu instead.");
        Diagnostics.Event("hooks-installed");
    }

    /// <summary>The wheel window tells the watcher whether it's open so key taps switch wheels instead of reopening.</summary>
    public void SetWheelOpen(bool open, WheelKind kind) { _wheelOpen = open; _current = kind; }

    private IntPtr MouseHook(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && Enabled)
        {
            var ms = Marshal.PtrToStructure<Native.MSLLHOOKSTRUCT>(lParam);
            switch ((int)wParam)
            {
                case Native.WM_LBUTTONDOWN:
                    _buttonDown = true; _dragging = false; _downAt = ms.pt;
                    _sourceWindow = Native.WindowFromPoint(ms.pt);
                    _eligible = AppSettings.Current.DragSources == DragSourceFilter.AnyApp || IsExplorerFast(_sourceWindow);
                    break;
                case Native.WM_MOUSEMOVE when _buttonDown && !_dragging:
                    if (Math.Abs(ms.pt.X - _downAt.X) > Native.GetSystemMetrics(Native.SM_CXDRAG) ||
                        Math.Abs(ms.pt.Y - _downAt.Y) > Native.GetSystemMetrics(Native.SM_CYDRAG))
                    {
                        _dragging = true;
                        // Key already held when the drag starts ("hold Shift while dragging").
                        if (_eligible && !_wheelOpen && HeldWheelKey() is { } held) Raise(held, ms.pt);
                    }
                    break;
                case Native.WM_LBUTTONUP:
                    var was = _dragging;
                    _buttonDown = false; _dragging = false;
                    if (was) _ui.TryEnqueue(() => DragEnded?.Invoke());
                    break;
            }
        }
        return Native.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    private IntPtr KeyHook(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && Enabled && ((int)wParam is Native.WM_KEYDOWN or Native.WM_SYSKEYDOWN))
        {
            var kb = Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam);
            bool repeat = (kb.flags & 0x80) == 0 && IsAutoRepeat(kb.vkCode);
            if (kb.vkCode == Native.VK_ESCAPE && _wheelOpen) _ui.TryEnqueue(() => CancelRequested?.Invoke());
            else if (!repeat && KindFor((int)kb.vkCode) is { } kind)
            {
                if (_wheelOpen) { if (kind != _current) { _current = kind; _ui.TryEnqueue(() => WheelKindChanged?.Invoke(kind)); } }
                else if (_dragging && _eligible) { Native.GetCursorPos(out var p); Raise(kind, p); }
            }
            _lastDown = kb.vkCode;
        }
        else if (nCode >= 0 && ((int)wParam is Native.WM_KEYUP or Native.WM_SYSKEYUP)) _lastDown = 0;
        return Native.CallNextHookEx(_keyHook, nCode, wParam, lParam);
    }

    private uint _lastDown;
    private bool IsAutoRepeat(uint vk) => vk == _lastDown;

    private void Raise(WheelKind kind, Native.POINT p)
    {
        _wheelOpen = true; _current = kind;
        var src = _sourceWindow;
        _ui.TryEnqueue(() => WheelRequested?.Invoke(kind, p, src));
    }

    private static WheelKind? KindFor(int vk)
    {
        var s = AppSettings.Current;
        WheelKey? key = vk switch
        {
            Native.VK_SHIFT or Native.VK_LSHIFT or Native.VK_RSHIFT => WheelKey.Shift,
            Native.VK_CONTROL or Native.VK_LCONTROL or Native.VK_RCONTROL => WheelKey.Ctrl,
            Native.VK_MENU or Native.VK_LMENU or Native.VK_RMENU => WheelKey.Alt,
            _ => null,
        };
        if (key is null) return null;
        if (key == s.ConvertKey) return WheelKind.Convert;
        if (key == s.ToolsKey) return WheelKind.Tools;
        if (key == s.ActionsKey) return WheelKind.Actions;
        return null;
    }

    private static WheelKind? HeldWheelKey()
    {
        if (Native.IsDown(Native.VK_SHIFT)) return KindFor(Native.VK_SHIFT);
        if (Native.IsDown(Native.VK_CONTROL)) return KindFor(Native.VK_CONTROL);
        if (Native.IsDown(Native.VK_MENU)) return KindFor(Native.VK_MENU);
        return null;
    }

    private readonly Dictionary<uint, bool> _pidIsExplorer = new();
    /// <summary>Hook callbacks must return quickly; class-name check first, process lookup cached by pid.</summary>
    private bool IsExplorerFast(IntPtr hwnd)
    {
        var root = Native.GetAncestor(hwnd, 2);
        var cls = Native.ClassName(root);
        if (cls is "CabinetWClass" or "ExploreWClass" or "Progman" or "WorkerW") return true;
        Native.GetWindowThreadProcessId(root, out var pid);
        if (_pidIsExplorer.TryGetValue(pid, out var v)) return v;
        v = string.Equals(Native.ProcessName(root), "explorer", StringComparison.OrdinalIgnoreCase);
        if (_pidIsExplorer.Count > 256) _pidIsExplorer.Clear();
        _pidIsExplorer[pid] = v;
        return v;
    }

    public void Dispose()
    {
        if (_mouseHook != IntPtr.Zero) Native.UnhookWindowsHookEx(_mouseHook);
        if (_keyHook != IntPtr.Zero) Native.UnhookWindowsHookEx(_keyHook);
        _mouseHook = _keyHook = IntPtr.Zero;
    }
}

/// <summary>Opt-in event trace for automated UI tests (ORANGE_TEST_EVENTS=path). Off by default; never contains file contents.</summary>
public static class Diagnostics
{
    private static readonly string? PathOut = Environment.GetEnvironmentVariable("ORANGE_TEST_EVENTS");
    private static readonly object Gate = new();
    public static bool Enabled => PathOut is not null;
    public static void Event(string what)
    {
        if (PathOut is null) return;
        lock (Gate) File.AppendAllText(PathOut, $"{DateTime.UtcNow:O} {what}{Environment.NewLine}");
    }
}
