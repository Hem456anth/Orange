using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Orange.App.Interop;
using Orange.App.Services;
using Orange.Core;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.System;

namespace Orange.App.Views;

/// <summary>
/// The pop-up wheel. Two ways in:
///  - Drag mode: appears under the cursor during a drag, never takes focus, is an OLE drop target; dropping on a wedge runs it.
///  - Keyboard mode: opened by the shortcut / context menu / Send To with known files; arrows + Enter, Esc to close.
/// Shift / Ctrl / Alt (configurable) switch between the Convert, Tools and Actions wheels while it's open; Tab cycles in keyboard mode.
/// </summary>
public sealed class WheelWindow : Window
{
    private const int MaxWedges = 8;
    private readonly WheelControl _wheel;
    private readonly ListView _a11yList = new() { Opacity = 0.01, Width = 1, Height = 1, IsTabStop = true, SelectionMode = ListViewSelectionMode.Single };
    private readonly Grid _root = new();
    private readonly double _diameterDip = 380;
    private IntPtr _hwnd;
    private IReadOnlyList<string> _files = Array.Empty<string>();
    private WheelKind _kind;
    private bool _dragMode, _closed, _dropped;
    private readonly DragWatcher? _watcher;
    private List<object> _tags = new();

    public static WheelWindow? Active { get; private set; }
    public IntPtr Hwnd => _hwnd;
    public WheelControl Wheel => _wheel;

    public WheelWindow(DragWatcher? watcher)
    {
        _watcher = watcher;
        _wheel = new WheelControl(_diameterDip);
        Title = "Orange";
        _root.Children.Add(_wheel);
        _root.Children.Add(_a11yList);
        _root.AllowDrop = true;
        _root.Background = new SolidColorBrush(Colors.Transparent);   // must be non-null to receive drag events
        _root.DragEnter += OnDragEnter;
        _root.DragOver += OnDragOver;
        _root.Drop += OnDrop;
        _root.PointerMoved += (_, e) => { if (!_dragMode) _wheel.SetHighlight(_wheel.HitTest(e.GetCurrentPoint(_wheel).Position)); };
        _root.PointerReleased += async (_, e) =>
        {
            if (_dragMode) return;
            var i = _wheel.HitTest(e.GetCurrentPoint(_wheel).Position);
            if (i >= 0) await InvokeAsync(i, _files); else if (IsOutside(e.GetCurrentPoint(_wheel).Position)) Close();
        };
        _a11yList.SelectionChanged += (_, _) => _wheel.SetHighlight(_a11yList.SelectedIndex);
        _a11yList.KeyDown += OnKeyDown;
        _wheel.HighlightChanged += i => { if (_a11yList.SelectedIndex != i && i < _a11yList.Items.Count) _a11yList.SelectedIndex = i; Diagnostics.Event($"highlight {i}"); };
        AutomationProperties.SetName(_a11yList, "Orange wheel");
        Content = _root;
        SystemBackdrop = new DesktopAcrylicBackdrop();

        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var presenter = OverlappedPresenter.Create();
        presenter.IsAlwaysOnTop = true; presenter.IsResizable = false; presenter.IsMaximizable = false; presenter.IsMinimizable = false;
        presenter.SetBorderAndTitleBar(false, false);
        AppWindow.SetPresenter(presenter);
        AppWindow.IsShownInSwitchers = false;
        var ex = (long)Native.GetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE);
        Native.SetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE, new IntPtr(ex | Native.WS_EX_TOOLWINDOW | Native.WS_EX_TOPMOST));
        Native.StripFrame(_hwnd);
        Closed += (_, _) => { _closed = true; _watcher?.SetWheelOpen(false, _kind); if (Active == this) Active = null; Diagnostics.Event("wheel-closed"); };
        if (_watcher != null)
        {
            _watcher.WheelKindChanged += OnKindChanged;
            _watcher.CancelRequested += OnCancel;
            _watcher.DragEnded += OnDragEnded;
            Closed += (_, _) => { _watcher.WheelKindChanged -= OnKindChanged; _watcher.CancelRequested -= OnCancel; _watcher.DragEnded -= OnDragEnded; };
        }
    }

    private void OnKindChanged(WheelKind k) => SetKind(k);
    private void OnCancel() => Close();
    private async void OnDragEnded()
    {
        // The drop event (if it landed on us) arrives around the same time as the button-up. Give it a moment.
        await Task.Delay(400);
        if (!_dropped && !_closed && _dragMode) Close();
    }

    /// <summary>Shows the wheel centred on a screen point (physical pixels), clamped to that monitor's work area.</summary>
    public static WheelWindow Open(WheelKind kind, IReadOnlyList<string> files, Native.POINT at, bool dragMode, DragWatcher? watcher)
    {
        Active?.Close();
        var w = new WheelWindow(watcher) { _dragMode = dragMode };
        Active = w;
        w._files = files;
        w.SetKind(kind);
        w.Place(at);
        if (dragMode)
        {
            // Show without stealing focus from Explorer, which owns the drag loop.
            Native.ShowWindow(w._hwnd, Native.SW_SHOWNOACTIVATE);
            Native.SetWindowPos(w._hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }
        else
        {
            w.Activate();
            w._a11yList.Focus(FocusState.Keyboard);
            if (w._a11yList.Items.Count > 0) w._a11yList.SelectedIndex = 0;
        }
        // Showing can re-apply the system frame and drop the region on Windows 11; clip again once visible.
        Native.StripFrame(w._hwnd);
        w.ApplyRegion();
        watcher?.SetWheelOpen(true, kind);
        _ = w.LoadThumbnailAsync();
        Native.GetWindowRect(w._hwnd, out var r);
        Diagnostics.Event($"wheel-shown kind={kind} drag={dragMode} files={files.Count} rect={r.Left},{r.Top},{r.Right},{r.Bottom} items={string.Join('|', w._wheel.Items.Select(i => i.Title))}");
        return w;
    }

    private void Place(Native.POINT at)
    {
        var mon = Native.MonitorFromPoint(at, Native.MONITOR_DEFAULTTONEAREST);
        Native.GetDpiForMonitor(mon, 0, out var dpi, out _);
        double scale = dpi / 96.0;
        int size = (int)Math.Round(_diameterDip * scale);
        var mi = new Native.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.MONITORINFO>() };
        Native.GetMonitorInfo(mon, ref mi);
        int x = Math.Clamp(at.X - size / 2, mi.rcWork.Left, mi.rcWork.Right - size);
        int y = Math.Clamp(at.Y - size / 2, mi.rcWork.Top, mi.rcWork.Bottom - size);
        AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(x, y, size, size));
        // Round window: everything outside the circle is not part of the window (no click/drop dead zones).
        ApplyRegion();
    }

    private void ApplyRegion()
    {
        Native.GetWindowRect(_hwnd, out var r);
        int w = r.Right - r.Left, h = r.Bottom - r.Top;
        if (w > 0 && h > 0) Native.SetWindowRgn(_hwnd, Native.CreateEllipticRgn(0, 0, w + 1, h + 1), true);
    }

    public void SetKind(WheelKind kind)
    {
        _kind = kind;
        _watcher?.SetWheelOpen(true, kind);
        var reg = ConversionService.Instance.Registry;
        var s = AppSettings.Current;
        List<WheelItem> items = new();
        _tags = new();
        string idle = Caption();
        if (kind == WheelKind.Actions)
        {
            foreach (var a in FileActions.All) { items.Add(new WheelItem(a.Title, a.Glyph, a, a.Title)); }
        }
        else
        {
            var group = kind == WheelKind.Convert ? OperationGroup.Convert : OperationGroup.Tool;
            var files = _files.Where(File.Exists).ToList();
            var ops = files.Count == _files.Count ? reg.ForFiles(files, group).ToList() : new List<OperationDescriptor>();
            int Rank(OperationDescriptor o) => s.Favorites.Contains(o.Id) ? 0 : s.Recent.Any(r => r.OperationId == o.Id) ? 1 : 2;
            var ordered = ops.Select((o, i) => (o, i)).OrderBy(x => x.o.CombinesInputs && files.Count > 1 ? -1 : Rank(x.o)).ThenBy(x => x.i).Select(x => x.o).ToList();
            var shown = ordered.Count > MaxWedges ? ordered.Take(MaxWedges - 1).ToList() : ordered;
            foreach (var o in shown)
                items.Add(new WheelItem(o.Title, group == OperationGroup.Tool ? Glyphs.For(o) : null, o,
                    group == OperationGroup.Convert ? $"Convert to {o.Title}" : o.Title));
            if (ordered.Count > MaxWedges) items.Add(new WheelItem("More", "\uE712", "more", $"{ordered.Count - shown.Count} more options"));
            if (ops.Count == 0)
                idle = _files.Count == 0 ? "No files" : files.Count != _files.Count ? "Folder: tap Alt for actions" :
                       FileTypeDetector.Detect(files[0]) == FileKind.Unknown ? "Unsupported file" :
                       files.Select(FileTypeDetector.Detect).Distinct().Count() > 1 ? "Mixed file types" : kind == WheelKind.Tools ? "No tools" : "No conversions";
        }
        _tags = items.Select(i => i.Tag).ToList();
        _wheel.SetItems(items, idle);
        _a11yList.Items.Clear();
        foreach (var i in items) _a11yList.Items.Add(new ListViewItem { Content = i.AccessibleName, Tag = i.Tag });
        Diagnostics.Event($"wheel-kind {kind} items={string.Join('|', items.Select(i => i.Title))}");
    }

    private string Caption()
    {
        if (_files.Count == 0) return "Drop here";
        if (_files.Count > 1) return $"{_files.Count} files";
        try { var fi = new FileInfo(_files[0]); return fi.Exists ? Size(fi.Length) : "Folder"; } catch { return ""; }
    }
    private static string Size(long b) => b < 1024 ? $"{b} B" : b < 1 << 20 ? $"{b / 1024.0:0} KB" : b < 1 << 30 ? $"{b / 1048576.0:0.0} MB" : $"{b / 1073741824.0:0.00} GB";

    /// <summary>Fills in files learned later (Explorer selection) unless the drag already told us.</summary>
    public void SuggestFiles(IReadOnlyList<string> files)
    {
        if (_closed || _files.Count > 0) return;
        _files = files; SetKind(_kind); _ = LoadThumbnailAsync();
    }

    private async Task LoadThumbnailAsync()
    {
        try
        {
            if (_files.Count == 0 || !File.Exists(_files[0])) return;
            var f = await StorageFile.GetFileFromPathAsync(_files[0]);
            using var t = await f.GetThumbnailAsync(Windows.Storage.FileProperties.ThumbnailMode.SingleItem, 128);
            if (t is null || _closed) return;
            var bmp = new BitmapImage();
            await bmp.SetSourceAsync(t);
            _wheel.SetThumbnail(bmp);
        }
        catch { /* no thumbnail (e.g. cloud placeholder offline) */ }
    }

    // ---------- drag & drop

    private async void OnDragEnter(object sender, DragEventArgs e)
    {
        Diagnostics.Event("drag-enter");
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        var def = e.GetDeferral();
        try
        {
            var items = await e.DataView.GetStorageItemsAsync();
            var paths = items.Select(i => i.Path).Where(p => !string.IsNullOrEmpty(p)).ToList();
            if (paths.Count > 0 && !paths.SequenceEqual(_files, StringComparer.OrdinalIgnoreCase))
            {
                _files = paths;           // the drag's real payload wins over the Explorer-selection guess
                SetKind(_kind);
                _ = LoadThumbnailAsync();
            }
        }
        catch { /* some sources only expose items on drop */ }
        finally { def.Complete(); }
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        var i = _wheel.HitTest(e.GetPosition(_wheel));
        _wheel.SetHighlight(i);
        if (i >= 0 && e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            // Copy, never Move: the source keeps its files whatever modifier keys are down.
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = _wheel.Items[i].AccessibleName;
            e.DragUIOverride.IsCaptionVisible = true;
            e.DragUIOverride.IsGlyphVisible = false;
        }
        else e.AcceptedOperation = DataPackageOperation.None;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        _dropped = true;
        var i = _wheel.HitTest(e.GetPosition(_wheel));
        Diagnostics.Event($"drop index={i}");
        if (i < 0 || !e.DataView.Contains(StandardDataFormats.StorageItems)) { Close(); return; }
        var def = e.GetDeferral();
        List<string> paths;
        try { paths = (await e.DataView.GetStorageItemsAsync()).Select(x => x.Path).Where(p => !string.IsNullOrEmpty(p)).ToList(); }
        finally { e.AcceptedOperation = DataPackageOperation.Copy; def.Complete(); }
        if (paths.Count == 0) { ProgressWindow.ShowError("Orange", "Those items aren't files on this PC.", "Virtual items (e.g. inside a ZIP or phone) need to be copied out first."); Close(); return; }
        await InvokeAsync(i, paths);
    }

    // ---------- choosing

    private async Task InvokeAsync(int index, IReadOnlyList<string> files)
    {
        if (index < 0 || index >= _tags.Count) return;
        var tag = _tags[index];
        var hwndForPickers = _hwnd;
        Diagnostics.Event($"invoke {index} {(tag as OperationDescriptor)?.Id ?? tag.ToString()}");
        switch (tag)
        {
            case "more":
                Close();
                App.Main.ShowConvert(files, "");
                return;
            case FileActions.ActionItem a:
                Close();
                try
                {
                    var msg = await FileActions.RunAsync(a.Id, files, App.Main.Hwnd);
                    if (!string.IsNullOrEmpty(msg)) ProgressWindow.ShowInfo(msg);
                }
                catch (Exception ex) { ProgressWindow.ShowError(a.Title, "That action didn't work.", ex.Message); }
                return;
            case OperationDescriptor op:
                Close();
                var settings = AppSettings.Current;
                IReadOnlyDictionary<string, string>? options = null;
                if (op.Options.Count > 0 && op.Group == OperationGroup.Tool && settings.AskOptionsForTools)
                {
                    options = await OptionsWindow.AskAsync(op, files, ConversionService.Instance.RememberedOptions(op));
                    if (options is null) return;   // cancelled
                }
                await ConversionService.Instance.RunAsync(op, files, options, DispatcherQueue);
                return;
        }
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        int n = _a11yList.Items.Count;
        switch (e.Key)
        {
            case VirtualKey.Right:
                if (n > 0) _a11yList.SelectedIndex = (_a11yList.SelectedIndex + 1 + n) % n; e.Handled = true; break;
            case VirtualKey.Left:
                if (n > 0) _a11yList.SelectedIndex = (_a11yList.SelectedIndex - 1 + n) % n; e.Handled = true; break;
            case VirtualKey.Home: if (n > 0) _a11yList.SelectedIndex = 0; e.Handled = true; break;
            case VirtualKey.End: if (n > 0) _a11yList.SelectedIndex = n - 1; e.Handled = true; break;
            case VirtualKey.Enter: case VirtualKey.Space:
                _ = InvokeAsync(_a11yList.SelectedIndex, _files); e.Handled = true; break;
            case VirtualKey.Escape: Close(); e.Handled = true; break;
            case VirtualKey.Tab:
                SetKind(_kind switch { WheelKind.Convert => WheelKind.Tools, WheelKind.Tools => WheelKind.Actions, _ => WheelKind.Convert });
                if (_a11yList.Items.Count > 0) _a11yList.SelectedIndex = 0;
                e.Handled = true; break;
            case >= VirtualKey.Number1 and <= VirtualKey.Number9:
                var k = e.Key - VirtualKey.Number1;
                if (k < n) _ = InvokeAsync(k, _files);
                e.Handled = true; break;
            case >= VirtualKey.A and <= VirtualKey.Z:
                // Type to search: jump to the full, searchable list with what was typed.
                Close();
                App.Main.ShowConvert(_files, ((char)('a' + (e.Key - VirtualKey.A))).ToString());
                e.Handled = true; break;
        }
    }

    private bool IsOutside(Windows.Foundation.Point p)
    {
        double dx = p.X - _wheel.Diameter / 2, dy = p.Y - _wheel.Diameter / 2;
        return Math.Sqrt(dx * dx + dy * dy) > _wheel.OuterRadius;
    }

    /// <summary>For UI self-tests: the screen point (physical px) at the middle of wedge <paramref name="i"/>.</summary>
    public Native.POINT WedgeCenterOnScreen(int i)
    {
        Native.GetWindowRect(_hwnd, out var r);
        double scale = Native.GetDpiForWindow(_hwnd) / 96.0;
        int n = Math.Max(1, _wheel.Items.Count);
        double ang = (-90 + i * 360.0 / n) * Math.PI / 180, rr = (_wheel.InnerRadius + _wheel.OuterRadius) / 2 * scale;
        return new Native.POINT { X = (r.Left + r.Right) / 2 + (int)(rr * Math.Cos(ang)), Y = (r.Top + r.Bottom) / 2 + (int)(rr * Math.Sin(ang)) };
    }
}

/// <summary>Segoe Fluent Icons glyphs for tool wedges.</summary>
public static class Glyphs
{
    public static string For(OperationDescriptor op) => op.Icon switch
    {
        "compress" => "\uE73F", "resize" => "\uE740", "crop" => "\uE7A8", "rotate" => "\uE7AD", "metadata" => "\uE8D7",
        "grayscale" => "\uE790", "pdf" => "\uEA90", "trim" => "\uE8C6", "mute" => "\uE74F", "speed" => "\uEC4A",
        "snapshot" => "\uE722", "split" => "\uE8B9", "join" or "merge" => "\uE71D", "normalize" => "\uE767", "channels" => "\uE995",
        "pages" => "\uE8A5", "images" => "\uE8B9", "extract" => "\uE8B7", "clock" => "\uE823", "count" => "\uE8EF",
        _ => "\uE713",
    };
}
