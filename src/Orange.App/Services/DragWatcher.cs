using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Orange.App.Interop;

namespace Orange.App.Services;

/// <summary>Same order as <see cref="WheelTarget"/> (cast between them).</summary>
public enum WheelKind { Convert, Tools, Actions }

/// <summary>
/// Notices "a drag is in progress and the user tapped (or is holding) a wheel key" anywhere on the desktop.
///
/// How: low-level mouse and keyboard hooks (WH_MOUSE_LL / WH_KEYBOARD_LL). They observe input only; every event is
/// passed on unchanged, so Explorer's own drag keeps working. Explorer reads Shift = move, Ctrl = copy, Alt = link;
/// Orange's drop target always answers Copy, so a wheel key can never make Explorer move the source. Gestures
/// (Shift, Shift twice, Shift+Ctrl by default) are decoded by the unit-tested <see cref="WheelKeyInterpreter"/>. The hooks never read keystrokes beyond the three modifier keys and Escape, and nothing is logged.
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
    private volatile bool _buttonDown, _dragging, _eligible, _wheelOpen;
    private IntPtr _sourceWindow;
    private volatile WheelKind _current;
    private readonly WheelKeyInterpreter _keys = new();
    private static long NowMs => Environment.TickCount64;

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

    private Thread? _thread;
    private uint _threadId;

    /// <summary>
    /// Installs the hooks on a dedicated thread that does nothing but pump messages, so hook callbacks are
    /// answered immediately even while the UI thread is busy (Windows silently drops hooks that answer slowly).
    /// </summary>
    public void Start()
    {
        using var ready = new ManualResetEventSlim();
        Exception? error = null;
        _thread = new Thread(() =>
        {
            _threadId = GetCurrentThreadId();
            var mod = Native.GetModuleHandle(null);
            _mouseHook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, _mouseProc, mod, 0);
            _keyHook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, _keyProc, mod, 0);
            if (_mouseHook == IntPtr.Zero || _keyHook == IntPtr.Zero)
                error = new InvalidOperationException($"Couldn't watch for drags (error {Marshal.GetLastWin32Error()}). Use the shortcut or the context menu instead.");
            ready.Set();
            if (error != null) return;
            while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0) { TranslateMessage(ref msg); DispatchMessage(ref msg); }
            Native.UnhookWindowsHookEx(_mouseHook); Native.UnhookWindowsHookEx(_keyHook);
            _mouseHook = _keyHook = IntPtr.Zero;
        }) { IsBackground = true, Name = "orange-input-hooks", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
        ready.Wait(TimeSpan.FromSeconds(5));
        if (error != null) throw error;
        Diagnostics.Event("hooks-installed");
    }

    [StructLayout(LayoutKind.Sequential)] private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public Native.POINT pt; }
    [DllImport("user32.dll")] private static extern int GetMessage(out MSG m, IntPtr h, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG m);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref MSG m);
    [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint id, uint msg, IntPtr w, IntPtr l);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();

    /// <summary>The wheel window tells the watcher whether it's open so key taps switch wheels instead of reopening.</summary>
    public void SetWheelOpen(bool open, WheelKind kind) { _wheelOpen = open; _current = kind; }

    private IntPtr MouseHook(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try { MouseCore(nCode, wParam, lParam); } catch { /* never let an exception escape into the OS input chain */ }
        return Native.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    public int EventsSeen;
    private void MouseCore(int nCode, IntPtr wParam, IntPtr lParam)
    {
        Interlocked.Increment(ref EventsSeen);
        if (nCode >= 0 && Enabled)
        {
            var ms = Marshal.PtrToStructure<Native.MSLLHOOKSTRUCT>(lParam);
            switch ((int)wParam)
            {
                case Native.WM_LBUTTONDOWN:
                    if (Diagnostics.Enabled) Diagnostics.Event($"lbutton-down {ms.pt.X},{ms.pt.Y} injected={(ms.flags & 1) != 0}");
                    _buttonDown = true; _dragging = false; _downAt = ms.pt;
                    _sourceWindow = Native.WindowFromPoint(ms.pt);
                    _eligible = AppSettings.Current.DragSources == DragSourceFilter.AnyApp || IsExplorerFast(_sourceWindow);
                    break;
                case Native.WM_MOUSEMOVE when _buttonDown && !_dragging:
                    if (Math.Abs(ms.pt.X - _downAt.X) > Native.GetSystemMetrics(Native.SM_CXDRAG) ||
                        Math.Abs(ms.pt.Y - _downAt.Y) > Native.GetSystemMetrics(Native.SM_CYDRAG))
                    {
                        _dragging = true;
                        if (Diagnostics.Enabled) Diagnostics.Event($"drag-start eligible={_eligible} source={Native.ClassName(Native.GetAncestor(_sourceWindow, 2))}");
                        // Key already held when the drag starts ("hold Shift while dragging").
                        if (_eligible && !_wheelOpen && HeldWheelKey() is { } held) Raise(held, ms.pt);
                    }
                    break;
                case Native.WM_LBUTTONUP:
                    var was = _dragging;
                    _buttonDown = false; _dragging = false; _keys.Reset();
                    if (was) { if (Diagnostics.Enabled) Diagnostics.Event($"lbutton-up {ms.pt.X},{ms.pt.Y} wheelOpen={_wheelOpen}"); _ui.TryEnqueue(() => DragEnded?.Invoke()); }
                    break;
            }
        }
    }

    private IntPtr KeyHook(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try { KeyCore(nCode, wParam, lParam); } catch { }
        return Native.CallNextHookEx(_keyHook, nCode, wParam, lParam);
    }

    private void KeyCore(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && Enabled && ((int)wParam is Native.WM_KEYDOWN or Native.WM_SYSKEYDOWN))
        {
            var kb = Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam);
            bool repeat = (kb.flags & 0x80) == 0 && IsAutoRepeat(kb.vkCode);
            if (Diagnostics.Enabled && kb.vkCode is 0x10 or 0xA0 or 0xA1 or 0x11 or 0xA2 or 0xA3 or 0x12 or 0xA4 or 0xA5)
                Diagnostics.Event($"key {kb.vkCode} dragging={_dragging} eligible={_eligible} open={_wheelOpen}");
            if (kb.vkCode == Native.VK_ESCAPE && _wheelOpen) _ui.TryEnqueue(() => CancelRequested?.Invoke());
            else if (!repeat && Modifier((int)kb.vkCode) is { } mod && (_dragging || _wheelOpen))
            {
                SyncKeys();
                var target = _keys.OnKeyDown(mod, mod == ModifierKey.Shift || Native.IsDown(Native.VK_SHIFT),
                    mod == ModifierKey.Ctrl || Native.IsDown(Native.VK_CONTROL), mod == ModifierKey.Alt || Native.IsDown(Native.VK_MENU), NowMs);
                if (Diagnostics.Enabled) Diagnostics.Event($"key-gesture {mod} -> {target?.ToString() ?? "none"}");
                if (target is { } t)
                {
                    var kind = (WheelKind)(int)t;
                    if (_wheelOpen) { if (kind != _current) { _current = kind; _ui.TryEnqueue(() => WheelKindChanged?.Invoke(kind)); } }
                    else if (_dragging && _eligible) { Native.GetCursorPos(out var p); Raise(kind, p); }
                }
            }
            _lastDown = kb.vkCode;
        }
        else if (nCode >= 0 && ((int)wParam is Native.WM_KEYUP or Native.WM_SYSKEYUP)) _lastDown = 0;
    }

    private uint _lastDown;
    private bool IsAutoRepeat(uint vk) => vk == _lastDown;

    private void Raise(WheelKind kind, Native.POINT p)
    {
        _wheelOpen = true; _current = kind;
        var src = _sourceWindow;
        Diagnostics.Event($"wheel-request {kind} at {p.X},{p.Y}");
        _ui.TryEnqueue(() => WheelRequested?.Invoke(kind, p, src));
    }

    private static ModifierKey? Modifier(int vk) => vk switch
    {
        Native.VK_SHIFT or Native.VK_LSHIFT or Native.VK_RSHIFT => ModifierKey.Shift,
        Native.VK_CONTROL or Native.VK_LCONTROL or Native.VK_RCONTROL => ModifierKey.Ctrl,
        Native.VK_MENU or Native.VK_LMENU or Native.VK_RMENU => ModifierKey.Alt,
        _ => null,
    };

    /// <summary>Settings can change at any time; copy the bindings in before interpreting a key.</summary>
    private void SyncKeys()
    {
        var s = AppSettings.Current;
        _keys.ConvertKey = s.ConvertKey; _keys.ToolsKey = s.ToolsKey; _keys.ActionsKey = s.ActionsKey;
    }

    private WheelKind? HeldWheelKey()
    {
        SyncKeys();
        return _keys.ForHeld(Native.IsDown(Native.VK_SHIFT), Native.IsDown(Native.VK_CONTROL), Native.IsDown(Native.VK_MENU)) is { } t ? (WheelKind)(int)t : null;
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
        if (_threadId != 0) PostThreadMessage(_threadId, 0x0012 /*WM_QUIT*/, IntPtr.Zero, IntPtr.Zero);
        _thread?.Join(TimeSpan.FromSeconds(2));
        _threadId = 0;
    }
}

/// <summary>
/// Event trace. Two independent, opt-in sinks: the automated-test trace (ORANGE_TEST_EVENTS=path) and the user's
/// local debug log (Settings → Troubleshooting, %LOCALAPPDATA%\Orange\logs). Neither ever contains file contents,
/// neither touches the network.
/// </summary>
public static class Diagnostics
{
    private static readonly string? PathOut = Environment.GetEnvironmentVariable("ORANGE_TEST_EVENTS");
    private static readonly object Gate = new();
    public static bool Enabled => PathOut is not null || Orange.Core.Logging.LocalLog.Enabled;

    /// <summary>Synchronous (survives a crash right after), shared-read friendly, never throws.</summary>
    public static void Event(string what)
    {
        Orange.Core.Logging.LocalLog.Write(what);
        if (PathOut is null) return;
        lock (Gate)
            for (int attempt = 0; attempt < 20; attempt++)
            {
                try
                {
                    using var fs = new FileStream(PathOut, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                    var b = System.Text.Encoding.UTF8.GetBytes($"{DateTime.UtcNow:O} {what}{Environment.NewLine}");
                    fs.Write(b); fs.Flush(true);
                    return;
                }
                catch (IOException) { Thread.Sleep(5); }
                catch { return; }
            }
    }
}
