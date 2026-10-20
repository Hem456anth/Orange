using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Orange.App.Services;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace Orange.App.Views;

/// <summary>
/// Float shelf (Actions wheel → Shelf): a small always-on-top panel that holds files so you can collect them from
/// several folders and drag them out again later. It holds paths only; files are never copied or moved by the shelf,
/// and dragging out offers Copy (Explorer moves only if you hold Shift yourself).
/// </summary>
public sealed class ShelfWindow : Window
{
    public static ShelfWindow? Current { get; private set; }
    private readonly ListView _list = new() { SelectionMode = ListViewSelectionMode.Extended, CanDragItems = true, AllowDrop = true, MinHeight = 120 };
    private readonly TextBlock _empty = new() { Text = L.T("Drop files here to keep them handy. Drag them out when you need them."), TextWrapping = TextWrapping.Wrap, Opacity = 0.7, Margin = new Thickness(8) };
    private readonly List<string> _paths = new();

    public IReadOnlyList<string> Paths => _paths;

    public static ShelfWindow Open(IEnumerable<string>? files = null)
    {
        if (Current is null) { Current = new ShelfWindow(); Current.Closed += (_, _) => Current = null; }
        if (files != null) Current.Add(files);
        Current.Activate();
        return Current;
    }

    private ShelfWindow()
    {
        var root = new Grid { Padding = new Thickness(10), RowSpacing = 8, AllowDrop = true, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var host = new Grid();
        host.Children.Add(_empty); host.Children.Add(_list);
        root.Children.Add(host);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        var remove = new Button { Content = L.T("Remove") };
        remove.Click += (_, _) => { foreach (var i in _list.SelectedItems.Cast<ListViewItem>().Select(x => (string)x.Tag).ToList()) _paths.Remove(i); Render(); };
        var clear = new Button { Content = L.T("Clear") };
        clear.Click += (_, _) => { _paths.Clear(); Render(); };
        var wheel = new Button { Content = L.T("Open wheel") };
        wheel.Click += (_, _) => { if (_paths.Count > 0) { Interop.Native.GetCursorPos(out var p); WheelWindow.Open(WheelKind.Convert, _paths.ToList(), p, dragMode: false, App.Watcher); } };
        buttons.Children.Add(wheel); buttons.Children.Add(remove); buttons.Children.Add(clear);
        Grid.SetRow(buttons, 1); root.Children.Add(buttons);
        Content = root;
        AutomationProperties.SetName(_list, L.T("Files on the shelf"));

        foreach (var target in new UIElement[] { root, _list })
        {
            target.DragOver += (_, e) => { if (e.DataView.Contains(StandardDataFormats.StorageItems)) e.AcceptedOperation = DataPackageOperation.Copy; };
            target.Drop += async (_, e) =>
            {
                if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
                var def = e.GetDeferral();
                try { Add((await e.DataView.GetStorageItemsAsync()).Select(i => i.Path).Where(p => !string.IsNullOrEmpty(p))); }
                finally { e.AcceptedOperation = DataPackageOperation.Copy; def.Complete(); }
            };
        }
        _list.DragItemsStarting += (_, e) =>
        {
            var paths = e.Items.Cast<ListViewItem>().Select(i => (string)i.Tag).ToList();
            e.Data.RequestedOperation = DataPackageOperation.Copy;
            // Storage items are produced on demand, when the drop target asks for them.
            e.Data.SetDataProvider(StandardDataFormats.StorageItems, async request =>
            {
                var def = request.GetDeferral();
                try
                {
                    var items = new List<IStorageItem>();
                    foreach (var p in paths)
                    {
                        if (File.Exists(p)) items.Add(await StorageFile.GetFileFromPathAsync(p));
                        else if (Directory.Exists(p)) items.Add(await StorageFolder.GetFolderFromPathAsync(p));
                    }
                    request.SetData(items);
                }
                finally { def.Complete(); }
            });
        };
        WindowHelpers.Compact(this, L.T("Shelf"), topmost: true);
        WindowHelpers.Place(this, 320, 360, bottomRight: true);
        Render();
    }

    public void Add(IEnumerable<string> files)
    {
        foreach (var f in files)
            if ((File.Exists(f) || Directory.Exists(f)) && !_paths.Contains(f, StringComparer.OrdinalIgnoreCase) && _paths.Count < 500) _paths.Add(Path.GetFullPath(f));
        Render();
        Diagnostics.Event($"shelf {_paths.Count}");
    }

    private void Render()
    {
        _list.Items.Clear();
        foreach (var p in _paths)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            row.Children.Add(new FontIcon { Glyph = Directory.Exists(p) ? "\uE8B7" : "\uE8A5", FontSize = 16 });
            row.Children.Add(new TextBlock { Text = Path.GetFileName(p.TrimEnd('\\')), TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 230 });
            var item = new ListViewItem { Content = row, Tag = p };
            ToolTipService.SetToolTip(item, p);
            AutomationProperties.SetName(item, Path.GetFileName(p.TrimEnd('\\')));
            _list.Items.Add(item);
        }
        _empty.Visibility = _paths.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Title = _paths.Count == 0 ? L.T("Shelf") : L.F("Shelf · {0}", _paths.Count);
    }
}
