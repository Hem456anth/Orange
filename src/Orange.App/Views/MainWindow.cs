using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Orange.App.Interop;
using Orange.App.Services;
using Orange.Core;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;

namespace Orange.App.Views;

/// <summary>
/// The full window: a drop zone plus the searchable list of every operation (the wheel only shows the top few),
/// Recent, Favorites, Settings and About. Built in code so the whole UI is plain C#.
/// </summary>
public sealed class MainWindow : Window
{
    private readonly NavigationView _nav = new() { IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed, IsSettingsVisible = true, PaneDisplayMode = NavigationViewPaneDisplayMode.Left, OpenPaneLength = 200 };
    private readonly Frame _host = new();
    private readonly List<string> _files = new();
    private readonly TextBox _search = new() { PlaceholderText = L.T("Search: webp, compress, to mp3, merge pdf…") };
    private readonly ComboBox _category = new() { MinWidth = 150 };
    private readonly ListView _ops = new() { SelectionMode = ListViewSelectionMode.None };
    private readonly TextBlock _filesText = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.8 };
    private readonly bool _ready;
    public IntPtr Hwnd { get; }

    public MainWindow()
    {
        Hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        Title = "Orange";
        ExtendsContentIntoTitleBar = false;
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "orange.ico"));
        SystemBackdrop = new MicaBackdrop();
        _nav.MenuItems.Add(Item(L.T("Convert"), "\uE8AB", "convert"));
        _nav.MenuItems.Add(Item(L.T("Favorites"), "\uE734", "favorites"));
        _nav.MenuItems.Add(Item(L.T("Recent"), "\uE81C", "recent"));
        _nav.FooterMenuItems.Add(Item(L.T("About & privacy"), "\uE946", "about"));
        _nav.Content = _host;
        _nav.SelectionChanged += (_, e) =>
        {
            if (e.IsSettingsSelected) { Show("settings"); return; }
            if (e.SelectedItem is NavigationViewItem i) Show((string)i.Tag);
        };
        var root = new Grid();
        root.Children.Add(_nav);
        root.RequestedTheme = App.ThemeFor(AppSettings.Current.Theme);
        Content = root;
        _category.Items.Add("All");
        foreach (var k in Enum.GetValues<FileKind>().Where(k => k != FileKind.Unknown)) _category.Items.Add(k.ToString());
        _category.SelectedIndex = 0;
        _category.SelectionChanged += (_, _) => RefreshOps();
        _search.TextChanged += (_, _) => RefreshOps();
        AutomationProperties.SetName(_search, "Search operations");
        AutomationProperties.SetName(_category, "Category");
        AppWindow.Closing += (_, e) => { e.Cancel = true; AppWindow.Hide(); };   // closing hides; Quit is in the tray menu
        WindowHelpers.Place(this, 980, 680, bottomRight: false);
        _ready = true;
        // NavigationView rejects SelectedItem before its template is applied.
        _nav.Loaded += (_, _) => { if (_nav.SelectedItem is null) _nav.SelectedItem = _nav.MenuItems[0]; };
    }

    private static NavigationViewItem Item(string text, string glyph, string tag) =>
        new() { Content = text, Tag = tag, Icon = new FontIcon { Glyph = glyph } };

    public void ShowWindow()
    {
        AppWindow.Show();
        Activate();
        Native.SetForegroundWindow(Hwnd);
    }

    /// <summary>Opens the searchable list for these files (wheel "More…", or typing while the wheel is open).</summary>
    public void ShowConvert(IReadOnlyList<string> files, string query)
    {
        _files.Clear(); _files.AddRange(files);
        _search.Text = query;
        _nav.SelectedItem = _nav.MenuItems[0];
        Show("convert");
        ShowWindow();
        _search.Focus(FocusState.Programmatic);
        _search.SelectionStart = _search.Text.Length;
    }

    public void ShowPage(string page) => Show(page);

    private void Show(string page)
    {
        if (!_ready) return;
        _host.Content = page switch
        {
            "convert" => ConvertPage(),
            "favorites" => FavoritesPage(),
            "recent" => RecentPage(),
            "about" => AboutPage(),
            _ => SettingsPage(),
        };
    }

    // ---------------- Convert
    private UIElement ConvertPage()
    {
        foreach (var e in new FrameworkElement[] { _filesText, _ops, _search, _category }) Detach(e);
        var drop = new Border
        {
            AllowDrop = true, MinHeight = 120, CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(2),
            BorderBrush = new SolidColorBrush(WheelControl.OrangeAccent), Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x18, 0xFF, 0x7A, 0x00)),
        };
        var dropText = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Spacing = 6 };
        dropText.Children.Add(new TextBlock { Text = L.T("Drop files here"), Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"], HorizontalAlignment = HorizontalAlignment.Center });
        dropText.Children.Add(new TextBlock { Text = L.T("Or anywhere: drag files in File Explorer and tap Shift."), Opacity = 0.7, HorizontalAlignment = HorizontalAlignment.Center });
        var browse = new Button { Content = L.T("Choose files…"), HorizontalAlignment = HorizontalAlignment.Center };
        browse.Click += async (_, _) =>
        {
            var p = new FileOpenPicker(); p.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(p, Hwnd);
            var picked = await p.PickMultipleFilesAsync();
            if (picked.Count > 0) { _files.Clear(); _files.AddRange(picked.Select(f => f.Path)); RefreshOps(); }
        };
        dropText.Children.Add(browse);
        drop.Child = dropText;
        drop.DragOver += (_, e) => { e.AcceptedOperation = e.DataView.Contains(StandardDataFormats.StorageItems) ? DataPackageOperation.Copy : DataPackageOperation.None; };
        drop.Drop += async (_, e) =>
        {
            if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
            var items = await e.DataView.GetStorageItemsAsync();
            _files.Clear(); _files.AddRange(items.Select(i => i.Path).Where(p => !string.IsNullOrEmpty(p)));
            RefreshOps();
        };

        var filters = new Grid { ColumnSpacing = 8 };
        filters.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        filters.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        filters.Children.Add(_search);
        Grid.SetColumn(_category, 1); filters.Children.Add(_category);

        var page = new Grid { Padding = new Thickness(24), RowSpacing = 12 };
        page.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        page.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        page.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        page.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        page.Children.Add(drop);
        Grid.SetRow(_filesText, 1); page.Children.Add(_filesText);
        Grid.SetRow(filters, 2); page.Children.Add(filters);
        Grid.SetRow(_ops, 3); page.Children.Add(_ops);
        RefreshOps();
        return page;
    }

    /// <summary>The page is rebuilt on each visit; long-lived controls are moved from the old page to the new one.</summary>
    private static void Detach(FrameworkElement e) { if (e.Parent is Panel p) p.Children.Remove(e); }

    private void RefreshOps()
    {
        var reg = ConversionService.Instance.Registry;
        _filesText.Text = _files.Count == 0 ? "No files chosen. Operations below show what Orange can do on this PC."
            : _files.Count == 1 ? Path.GetFileName(_files[0]) : $"{_files.Count} files: {string.Join(", ", _files.Take(4).Select(Path.GetFileName))}{(_files.Count > 4 ? "…" : "")}";
        IEnumerable<OperationDescriptor> pool = _files.Count > 0
            ? reg.ForFiles(_files, OperationGroup.Convert, includeUnavailable: true).Concat(reg.ForFiles(_files, OperationGroup.Tool, includeUnavailable: true))
            : reg.All;
        if (!string.IsNullOrWhiteSpace(_search.Text))
        {
            var hits = reg.Search(_search.Text).Select(o => o.Id).ToHashSet();
            var allHits = reg.All.Where(o => hits.Contains(o.Id) || Matches(o, _search.Text)).Select(o => o.Id).ToHashSet();
            pool = pool.Where(o => allHits.Contains(o.Id));
        }
        if (_category.SelectedIndex > 0 && Enum.TryParse<FileKind>((string)_category.SelectedItem, out var k)) pool = pool.Where(o => o.Category == k);
        _ops.Items.Clear();
        foreach (var op in pool.OrderBy(o => o.Category).ThenBy(o => o.Group).ThenBy(o => o.Title).Take(400)) _ops.Items.Add(Row(op));
        if (_ops.Items.Count == 0) _ops.Items.Add(new TextBlock { Text = _files.Count > 0 ? "Nothing matches for these files." : "Nothing matches.", Opacity = 0.7, Margin = new Thickness(8) });
    }

    private static bool Matches(OperationDescriptor o, string q) =>
        q.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(t => (o.Title + " " + o.Id + " " + string.Join(' ', o.Keywords)).Contains(t, StringComparison.OrdinalIgnoreCase));

    private UIElement Row(OperationDescriptor op)
    {
        var reg = ConversionService.Instance.Registry;
        bool available = reg.IsAvailable(op);
        var g = new Grid { ColumnSpacing = 12, Padding = new Thickness(4, 6, 4, 6) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bool fav = AppSettings.Current.Favorites.Contains(op.Id);
        var star = new Button { Content = new FontIcon { Glyph = fav ? "\uE735" : "\uE734", FontSize = 14 }, Background = new SolidColorBrush(Colors.Transparent), BorderThickness = new Thickness(0) };
        AutomationProperties.SetName(star, fav ? $"Remove {op.Title} from favorites" : $"Add {op.Title} to favorites");
        star.Click += (_, _) => { AppSettings.Current.ToggleFavorite(op.Id); RefreshOps(); };
        g.Children.Add(star);
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = op.Group == OperationGroup.Convert ? $"{op.Category} → {op.Title}" : $"{op.Category}: {op.Title}", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        var sub = op.Group == OperationGroup.Convert ? $"Save as .{op.OutputExtension}" : op.ProducesFolder ? "Saves into a new folder" : op.CombinesInputs ? $"Needs {op.MinInputs}+ files" : "Saves a new copy";
        if (!available) sub = op.RequiresTool is { } t && !ToolLocator.Default.Has(t) ? $"Needs {ToolNames.Friendly(t)} (not installed)" : "Not supported on this PC";
        text.Children.Add(new TextBlock { Text = sub, FontSize = 12, Opacity = available ? 0.65 : 0.9, Foreground = available ? null : new SolidColorBrush(Colors.IndianRed) });
        Grid.SetColumn(text, 1); g.Children.Add(text);
        var applicable = _files.Count > 0 && available && reg.ForFiles(_files, op.Group).Any(o => o.Id == op.Id);
        var run = new Button { Content = op.Group == OperationGroup.Convert ? "Convert" : "Run", IsEnabled = applicable };
        AutomationProperties.SetName(run, $"{op.Title} for the chosen files");
        run.Click += async (_, _) =>
        {
            IReadOnlyDictionary<string, string>? opts = null;
            if (op.Options.Count > 0)
            {
                opts = Editors.HasPanel(op)
                    ? await Editors.AskAsync(op, _files.ToList(), ConversionService.Instance.RememberedOptions(op))
                    : await OptionsWindow.AskAsync(op, _files.ToList(), ConversionService.Instance.RememberedOptions(op));
                if (opts is null) return;
            }
            await ConversionService.Instance.RunAsync(op, _files.ToList(), opts, DispatcherQueue);
        };
        Grid.SetColumn(run, 3); g.Children.Add(run);
        return g;
    }

    private UIElement FavoritesPage()
    {
        var reg = ConversionService.Instance.Registry;
        var list = new StackPanel { Spacing = 4, Padding = new Thickness(24) };
        list.Children.Add(new TextBlock { Text = L.T("Favorites"), Style = (Style)Application.Current.Resources["TitleTextBlockStyle"] });
        list.Children.Add(new TextBlock { Text = L.T("Favorites appear first on the wheel. Star operations on the Convert page."), Opacity = 0.7 });
        foreach (var id in AppSettings.Current.Favorites)
        {
            try { list.Children.Add(Row(reg.Get(id))); } catch (OrangeException) { }
        }
        return new ScrollViewer { Content = list };
    }

    private UIElement RecentPage()
    {
        var list = new StackPanel { Spacing = 8, Padding = new Thickness(24) };
        list.Children.Add(new TextBlock { Text = L.T("Recent"), Style = (Style)Application.Current.Resources["TitleTextBlockStyle"] });
        if (AppSettings.Current.Recent.Count == 0) list.Children.Add(new TextBlock { Text = L.T("Nothing yet. Your last 50 conversions appear here."), Opacity = 0.7 });
        foreach (var r in AppSettings.Current.Recent)
        {
            var row = new Grid { ColumnSpacing = 12 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var t = new StackPanel();
            t.Children.Add(new TextBlock { Text = $"{r.Category} → {r.Title} · {r.Files} file{(r.Files == 1 ? "" : "s")}", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            t.Children.Add(new TextBlock { Text = $"{r.When.LocalDateTime:g} · {string.Join(", ", r.Outputs.Select(Path.GetFileName))}", FontSize = 12, Opacity = 0.7, TextTrimming = TextTrimming.CharacterEllipsis });
            row.Children.Add(t);
            var show = new Button { Content = L.T("Show"), IsEnabled = r.Outputs.Any(o => File.Exists(o) || Directory.Exists(o)) };
            show.Click += (_, _) => Native.RevealInExplorer(r.Outputs.Where(o => File.Exists(o) || Directory.Exists(o)).ToList());
            Grid.SetColumn(show, 1); row.Children.Add(show);
            list.Children.Add(row);
        }
        var clear = new Button { Content = L.T("Clear history"), Margin = new Thickness(0, 12, 0, 0) };
        clear.Click += (_, _) => { AppSettings.Current.Recent.Clear(); AppSettings.Current.Save(); Show("recent"); };
        list.Children.Add(clear);
        return new ScrollViewer { Content = list };
    }

    // ---------------- Settings
    private UIElement SettingsPage()
    {
        var s = AppSettings.Current;
        var groups = new Dictionary<string, StackPanel>();
        StackPanel Section(string group, string header)
        {
            if (!groups.TryGetValue(group, out var g))
                groups[group] = g = new StackPanel { Spacing = 14, Padding = new Thickness(24), MaxWidth = 760, HorizontalAlignment = HorizontalAlignment.Left };
            g.Children.Add(Header(L.T(header)));
            return g;
        }
        StackPanel p;

        p = Section("Activation", "Activation");
        p.Children.Add(Toggle(L.T("Open the wheel while dragging files"), s.DragActivation, v => { s.DragActivation = v; if (App.Watcher != null) App.Watcher.Enabled = v; }));
        p.Children.Add(Choice(L.T("Convert wheel key"), s.ConvertKey, v => s.ConvertKey = v));
        p.Children.Add(Choice(L.T("Tools wheel key"), s.ToolsKey, v => s.ToolsKey = v));
        p.Children.Add(Choice(L.T("Actions wheel key"), s.ActionsKey, v => s.ActionsKey = v));
        p.Children.Add(Choice(L.T("Drags that open the wheel"), s.DragSources, v => s.DragSources = v, "Explorer and the desktop only, or drags from any app"));
        var hk = new TextBox { Header = L.T("Shortcut for the selected files in File Explorer"), Text = s.Hotkey, Width = 260, HorizontalAlignment = HorizontalAlignment.Left };
        var hkStatus = new TextBlock { FontSize = 12, Opacity = 0.75, Text = App.Tray?.HotkeyError ?? "Select files in Explorer, press the shortcut, then use the arrow keys and Enter." };
        hk.LostFocus += (_, _) =>
        {
            s.Hotkey = hk.Text.Trim(); s.Save();
            var ok = App.Tray?.RegisterHotkey(s.Hotkey) ?? false;
            hkStatus.Text = ok ? $"{s.Hotkey} is ready." : App.Tray?.HotkeyError ?? "";
        };
        p.Children.Add(hk); p.Children.Add(hkStatus);

        p = Section("Activation", "File Explorer");
        p.Children.Add(Toggle("\"Convert with Orange\" in the right-click menu", ExplorerIntegration.ContextMenuInstalled, ExplorerIntegration.SetContextMenu,
            "On Windows 11 it's under \"Show more options\" (Shift+F10)."));
        p.Children.Add(Toggle(L.T("Orange in the Send to menu"), ExplorerIntegration.SendToInstalled, ExplorerIntegration.SetSendTo));
        p.Children.Add(Toggle(L.T("Start Orange when I sign in"), ExplorerIntegration.AutostartInstalled, v => { s.StartWithWindows = v; ExplorerIntegration.SetAutostart(v); }));

        p = Section("Output", "Output");
        var outBox = new TextBox { Header = L.T("Save results to"), Text = s.OutputFolder ?? "", PlaceholderText = L.T("Next to the original file"), IsReadOnly = true, Width = 420, HorizontalAlignment = HorizontalAlignment.Left };
        var outButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var pick = new Button { Content = L.T("Choose folder…") };
        pick.Click += async (_, _) =>
        {
            var fp = new FolderPicker(); fp.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(fp, Hwnd);
            var f = await fp.PickSingleFolderAsync();
            if (f != null) { s.OutputFolder = f.Path; outBox.Text = f.Path; s.Save(); }
        };
        var reset = new Button { Content = L.T("Next to the original") };
        reset.Click += (_, _) => { s.OutputFolder = null; outBox.Text = ""; s.Save(); };
        outButtons.Children.Add(pick); outButtons.Children.Add(reset);
        p.Children.Add(outBox); p.Children.Add(outButtons);
        p.Children.Add(Choice(L.T("When a file with that name exists"), s.Overwrite, v => s.Overwrite = v, "Keep both adds \" 2\". Orange never overwrites the original."));
        p.Children.Add(Toggle(L.T("Show results in File Explorer"), s.RevealWhenDone, v => s.RevealWhenDone = v));
        p.Children.Add(Toggle(L.T("Notify when done"), s.NotifyWhenDone, v => s.NotifyWhenDone = v));
        p.Children.Add(Toggle(L.T("Ask for settings before running tools (trim, crop…)"), s.AskOptionsForTools, v => s.AskOptionsForTools = v));
        p.Children.Add(Toggle(L.T("Sounds"), s.PlaySounds, v => s.PlaySounds = v));

        p = Section("Output", "Appearance");
        p.Children.Add(Choice(L.T("Theme"), s.Theme, v => { s.Theme = v; if (Content is FrameworkElement fe) fe.RequestedTheme = App.ThemeFor(v); }));

        p = Section("Output", "Performance and limits");
        p.Children.Add(Number(L.T("Files converted at the same time"), s.MaxParallelJobs, 1, 16, v => s.MaxParallelJobs = (int)v));
        p.Children.Add(Number(L.T("Largest input file (GB)"), s.MaxInputGigabytes, 0.1, 200, v => s.MaxInputGigabytes = v));

        p = Section("Presets", "Presets");
        p.Children.Add(PresetsSettings.Build(Hwnd, () => Show("settings")));
        p = Section("Presets", "Watch folders");
        p.Children.Add(WatchFoldersSection());
        p = Section("Presets", "Wheel editor");
        p.Children.Add(PresetsSettings.WheelEditor(() => Show("settings")));

        p = Section("Privacy", "Troubleshooting");
        p.Children.Add(Toggle(L.T("Write a debug log"), s.DebugLog, v => s.DebugLog = v,
            "Records drag, key and drop events and conversion results in a text file on this PC. Nothing is sent anywhere. Turn it off when you're done."));
        var openLogs = new Button { Content = L.T("Open logs folder") };
        openLogs.Click += (_, _) => { Directory.CreateDirectory(AppSettings.LogFolder); System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe") { ArgumentList = { AppSettings.LogFolder }, UseShellExecute = false }); };
        p.Children.Add(openLogs);

        p = Section("Engines", "Converters");
        p.Children.Add(new TextBlock { Text = L.T("Orange uses these programs on your PC. Missing ones only hide the operations that need them."), Opacity = 0.7, TextWrapping = TextWrapping.Wrap });
        foreach (var tool in new[] { "ffmpeg", "ffprobe", "pandoc", "ghostscript", "libreoffice" })
        {
            var path = ToolLocator.Default.Find(tool);
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            row.Children.Add(new FontIcon { Glyph = path is null ? "\uE783" : "\uE73E", Foreground = new SolidColorBrush(path is null ? Colors.IndianRed : Colors.SeaGreen) });
            row.Children.Add(new TextBlock { Text = $"{ToolNames.Friendly(tool)} ({tool})", Width = 200, VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(new TextBlock { Text = path ?? "Not found", Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 340 });
            var b = new Button { Content = L.T("Locate…") };
            var t = tool;
            b.Click += async (_, _) =>
            {
                var fo = new FileOpenPicker(); fo.FileTypeFilter.Add(".exe");
                WinRT.Interop.InitializeWithWindow.Initialize(fo, Hwnd);
                var f = await fo.PickSingleFileAsync();
                if (f != null) { s.ToolPaths[t] = f.Path; s.Save(); Show("settings"); }
            };
            row.Children.Add(b);
            p.Children.Add(row);
        }

        p = Section("Engines", "Engines");
        p.Children.Add(new TextBlock { Text = L.T("Optional helpers Orange can download for you. Downloads happen only when you press Install, come from the project's official release page, and are checked against a fingerprint built into Orange before anything is installed."), Opacity = 0.7, TextWrapping = TextWrapping.Wrap });
        var installed = AppSettings.Engines.Installed();
        foreach (var spec in Orange.Core.Engines.EngineManager.Catalog)
        {
            var have = installed.FirstOrDefault(e => e.Id == spec.Id);
            var card = new StackPanel { Spacing = 4 };
            card.Children.Add(new TextBlock { Text = $"{spec.Name} {spec.Version}", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            card.Children.Add(new TextBlock { Text = $"{spec.Purpose}. {spec.License}. {spec.SizeBytes / 1048576.0:0} MB download.", FontSize = 12, Opacity = 0.7, TextWrapping = TextWrapping.Wrap });
            var bar = new ProgressBar { Minimum = 0, Maximum = 1, Width = 320, HorizontalAlignment = HorizontalAlignment.Left, Visibility = Visibility.Collapsed };
            var status = new TextBlock { FontSize = 12, Text = have is null ? "Not installed" : $"Installed in {Path.GetDirectoryName(have.ExePath)}" };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            var install = new Button { Content = have is null ? "Install" : "Reinstall" };
            var remove = new Button { Content = L.T("Uninstall"), IsEnabled = have is not null };
            var cancel = new Button { Content = L.T("Cancel"), Visibility = Visibility.Collapsed };
            CancellationTokenSource? cts = null;
            var sp = spec;
            install.Click += async (_, _) =>
            {
                cts = new CancellationTokenSource();
                install.IsEnabled = remove.IsEnabled = false; cancel.Visibility = bar.Visibility = Visibility.Visible;
                var prog = new Progress<Orange.Core.Engines.EngineProgress>(pr =>
                {
                    status.Text = pr.Total > 0 ? $"{pr.Stage} {pr.Done / 1048576.0:0.0} of {pr.Total / 1048576.0:0.0} MB" : pr.Stage;
                    bar.IsIndeterminate = pr.Total <= 0; if (pr.Total > 0) bar.Value = (double)pr.Done / pr.Total;
                });
                try { await Task.Run(() => AppSettings.Engines.InstallAsync(sp, prog, cts.Token)); Diagnostics.Event($"engine-installed {sp.Id}"); Show("settings"); }
                catch (OperationCanceledException) { status.Text = L.T("Cancelled. Nothing was installed."); }
                catch (Exception e) { status.Text = e.Message; Diagnostics.Event($"engine-failed {sp.Id} {e.Message}"); }
                finally { install.IsEnabled = true; cancel.Visibility = bar.Visibility = Visibility.Collapsed; }
            };
            cancel.Click += (_, _) => cts?.Cancel();
            remove.Click += (_, _) => { try { AppSettings.Engines.Uninstall(sp.Id); } catch (Exception e) { status.Text = e.Message; return; } Show("settings"); };
            buttons.Children.Add(install); buttons.Children.Add(remove); buttons.Children.Add(cancel);
            card.Children.Add(status); card.Children.Add(bar); card.Children.Add(buttons);
            p.Children.Add(card);
        }
        foreach (var (name, url, why) in new[] {
            ("LibreOffice", "https://www.libreoffice.org/download/", "legacy DOC, XLS and PPT files"),
            ("Ghostscript", "https://ghostscript.com/releases/gsdnld.html", "stronger PDF compression"),
            ("7-Zip", "https://www.7-zip.org/", "RAR and 7z extraction") })
            p.Children.Add(new TextBlock { Text = $"{name} ({why}): install it from {url} and Orange finds it automatically.", FontSize = 12, Opacity = 0.7, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
        p = Section("Language", "Language");
        p.Children.Add(LanguageSection());
        p = Section("Updates", "Updates");
        p.Children.Add(UpdatesSection());
        p = Section("Privacy", "Your data");
        p.Children.Add(new TextBlock { TextWrapping = TextWrapping.Wrap, Text = L.T("Your files stay on this PC. Orange converts everything locally, never uploads files, has no account, no analytics and no telemetry. It only goes online when you install an engine or turn on update checks.") });
        var wipe = new Button { Content = L.T("Delete settings and history") };
        wipe.Click += (_, _) => { try { Directory.Delete(AppSettings.Folder, true); } catch { } ProgressWindow.ShowInfo(L.T("Settings and history deleted. They'll be recreated with defaults.")); };
        p.Children.Add(wipe);
        var about = AboutPage();
        var viewers = groups.ToDictionary(kv => kv.Key, kv => (UIElement)new ScrollViewer { Content = kv.Value });
        viewers["About"] = about;

        // Left: the sections; right: the chosen one. The choice survives refreshes (Show("settings")).
        string[] order = ["Activation", "Output", "Presets", "Engines", "Language", "Updates", "Privacy", "About"];
        var list = new ListView { SelectionMode = ListViewSelectionMode.Single, Width = 190, Margin = new Thickness(12, 24, 0, 0) };
        foreach (var o in order) list.Items.Add(new ListViewItem { Content = L.T(o), Tag = o });
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(list, L.T("Settings sections"));
        var host = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
        void Pick(string key) { _settingsSection = key; host.Content = viewers.TryGetValue(key, out var v) ? v : viewers["Activation"]; }
        list.SelectionChanged += (_, _) => { if (list.SelectedItem is ListViewItem li) Pick((string)li.Tag); };
        list.SelectedIndex = Math.Max(0, Array.IndexOf(order, _settingsSection));
        Pick(order[Math.Max(0, Array.IndexOf(order, _settingsSection))]);
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var title = new StackPanel();
        title.Children.Add(new TextBlock { Text = L.T("Settings"), Style = (Style)Application.Current.Resources["TitleTextBlockStyle"], Margin = new Thickness(16, 16, 0, 0) });
        title.Children.Add(list);
        grid.Children.Add(title);
        Grid.SetColumn(host, 1); grid.Children.Add(host);
        return grid;
    }

    private string _settingsSection = "Activation";

    /// <summary>Opens Settings at one section (used by the self-test and the tray).</summary>
    public void ShowSettings(string section) { _settingsSection = section; _host.Content = SettingsPage(); }

    private UIElement LanguageSection()
    {
        var s = AppSettings.Current;
        var p = new StackPanel { Spacing = 8 };
        (string Code, string Name)[] langs = [("", L.T("Same as Windows")), ("en-US", "English"), ("hi-IN", "हिन्दी (Hindi)"), ("te-IN", "తెలుగు (Telugu)")];
        var cb = new ComboBox { Header = L.T("Language"), ItemsSource = langs.Select(x => x.Name).ToList(), SelectedIndex = Math.Max(0, Array.FindIndex(langs, x => x.Code == (s.Language ?? ""))), MinWidth = 260 };
        var note = new TextBlock { FontSize = 12, Opacity = 0.7, TextWrapping = TextWrapping.Wrap, Text = L.T("Orange uses the new language the next time it starts. Hindi and Telugu translations are new; corrections are welcome.") };
        cb.SelectionChanged += (_, _) => { s.Language = langs[cb.SelectedIndex].Code; s.Save(); note.Text = L.T("Restart Orange to switch language."); };
        p.Children.Add(cb); p.Children.Add(note);
        return p;
    }

    private UIElement UpdatesSection()
    {
        var s = AppSettings.Current;
        var p = new StackPanel { Spacing = 8 };
        p.Children.Add(new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.75, Text = L.T("Off by default. When on, Orange asks GitHub once a day whether a newer version exists. Nothing about you or your files is sent, and nothing is downloaded automatically.") });
        var notes = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, FontSize = 12 };
        var link = new HyperlinkButton { Visibility = Visibility.Collapsed };
        async Task Check()
        {
            notes.Text = L.T("Checking…");
            try
            {
                var info = await new Orange.Core.Updates.UpdateChecker().CheckAsync(typeof(App).Assembly.GetName().Version ?? new Version(0, 0));
                s.LastUpdateCheck = DateTimeOffset.Now; s.Save();
                notes.Text = info.IsNewer ? L.F("Orange {0} is available.", info.Version) + "\n\n" + info.Notes : L.T("You have the latest version.");
                if (info.IsNewer) { link.Content = L.T("Open the download page"); link.NavigateUri = new Uri(info.Url); link.Visibility = Visibility.Visible; }
            }
            catch (Exception e) { notes.Text = e.Message; }
        }
        p.Children.Add(Toggle(L.T("Check for updates"), s.CheckForUpdates, v => s.CheckForUpdates = v));
        var now = new Button { Content = L.T("Check now") };
        now.Click += async (_, _) => await Check();
        p.Children.Add(now); p.Children.Add(notes); p.Children.Add(link);
        return p;
    }


    private UIElement WatchFoldersSection()
    {
        var p = new StackPanel { Spacing = 8 };
        p.Children.Add(new TextBlock { Text = L.T("New files dropped into a watched folder are converted with a preset while Orange runs in the tray. Orange waits until a file has finished copying or downloading, and never re-converts its own results."), Opacity = 0.7, TextWrapping = TextWrapping.Wrap });
        if (WatchService.LastError is { } err) p.Children.Add(new TextBlock { Text = err, Foreground = new SolidColorBrush(Colors.IndianRed), TextWrapping = TextWrapping.Wrap });
        foreach (var spec in WatchService.Specs)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            row.Children.Add(new TextBlock { Text = $"{spec.Folder}  →  {spec.Preset}{(spec.MoveOriginals ? " (originals moved to Originals)" : "")}", VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 520 });
            var remove = new Button { Content = L.T("Remove") };
            var folder = spec.Folder;
            remove.Click += (_, _) => { WatchService.Remove(folder); Show("settings"); };
            row.Children.Add(remove);
            p.Children.Add(row);
        }
        var presets = ConversionService.Instance.Presets.Select(x => x.Name).ToList();
        var add = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var presetBox = new ComboBox { ItemsSource = presets, SelectedIndex = presets.Count > 0 ? 0 : -1, MinWidth = 200, PlaceholderText = L.T("Preset") };
        var move = new CheckBox { Content = L.T("Move originals to an Originals folder") };
        var pick = new Button { Content = L.T("Add folder…") };
        pick.Click += async (_, _) =>
        {
            if (presetBox.SelectedIndex < 0) return;
            var fp = new FolderPicker(); fp.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(fp, Hwnd);
            var f = await fp.PickSingleFolderAsync(); if (f is null) return;
            try { WatchService.Add(new Orange.Core.Watch.WatchSpec { Folder = f.Path, Preset = presets[presetBox.SelectedIndex], MoveOriginals = move.IsChecked == true }); }
            catch (Exception e) { ProgressWindow.ShowError("Watch folders", e.Message, null); }
            Show("settings");
        };
        add.Children.Add(presetBox); add.Children.Add(move); add.Children.Add(pick);
        p.Children.Add(add);
        if (WatchService.Count > 0)
            p.Children.Add(Toggle(L.T("Pause all watch folders"), WatchService.Paused, WatchService.SetPaused));
        return p;
    }

    private static TextBlock Header(string t) => new() { Text = t, Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"], Margin = new Thickness(0, 12, 0, 0) };

    private static UIElement Toggle(string label, bool value, Action<bool> set, string? help = null)
    {
        var ts = new ToggleSwitch { Header = label, IsOn = value };
        ts.Toggled += (_, _) =>
        {
            try { set(ts.IsOn); AppSettings.Current.Save(); }
            catch (Exception e) { ProgressWindow.ShowError("Settings", "That setting couldn't be changed.", e.Message); }
        };
        if (help is null) return ts;
        var sp = new StackPanel(); sp.Children.Add(ts); sp.Children.Add(new TextBlock { Text = help, FontSize = 12, Opacity = 0.65 }); return sp;
    }

    private static UIElement Choice<T>(string label, T value, Action<T> set, string? help = null) where T : struct, Enum
    {
        var cb = new ComboBox { Header = label, ItemsSource = Enum.GetNames<T>().Select(Humanize).ToList(), SelectedIndex = Array.IndexOf(Enum.GetValues<T>(), value), MinWidth = 260 };
        cb.SelectionChanged += (_, _) => { set(Enum.GetValues<T>()[cb.SelectedIndex]); AppSettings.Current.Save(); };
        if (help is null) return cb;
        var sp = new StackPanel(); sp.Children.Add(cb); sp.Children.Add(new TextBlock { Text = help, FontSize = 12, Opacity = 0.65 }); return sp;
    }

    private static string Humanize(string s) => s switch
    {
        "ExplorerAndDesktop" => "File Explorer and desktop", "AnyApp" => "Any app", "KeepBoth" => "Keep both", "Replace" => "Replace the existing file", "Fail" => "Stop and tell me", "AltShift" => "Alt + Shift", "Ctrl" => "Ctrl", "ShiftDouble" => "Shift twice", "ShiftCtrl" => "Shift + Ctrl",
        _ => s,
    };

    private static UIElement Number(string label, double value, double min, double max, Action<double> set)
    {
        var nb = new NumberBox { Header = label, Value = value, Minimum = min, Maximum = max, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline, Width = 260, HorizontalAlignment = HorizontalAlignment.Left };
        nb.ValueChanged += (_, _) => { if (!double.IsNaN(nb.Value)) { set(nb.Value); AppSettings.Current.Save(); } };
        return nb;
    }

    // ---------------- About
    private UIElement AboutPage()
    {
        var p = new StackPanel { Spacing = 10, Padding = new Thickness(24), MaxWidth = 760, HorizontalAlignment = HorizontalAlignment.Left };
        p.Children.Add(new TextBlock { Text = "Orange", Style = (Style)Application.Current.Resources["TitleTextBlockStyle"] });
        p.Children.Add(new TextBlock { Text = $"Version {typeof(App).Assembly.GetName().Version}", Opacity = 0.7 });
        p.Children.Add(new TextBlock { TextWrapping = TextWrapping.Wrap, Text =
            "Your files stay on this PC. Orange converts everything locally, never uploads files, has no account, no analytics and no telemetry, " +
            "and makes no network requests. Settings and history are stored in %LOCALAPPDATA%\\Orange and can be deleted at any time." });
        p.Children.Add(new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.8, Text =
            "Orange is an independent Windows app inspired by the workflow of Tangerine for Mac. It is not affiliated with Tangerine or Leap Studio." });
        var clear = new Button { Content = L.T("Delete settings and history") };
        clear.Click += (_, _) =>
        {
            try { Directory.Delete(AppSettings.Folder, true); } catch { }
            ProgressWindow.ShowInfo("Settings and history deleted. They'll be recreated with defaults.");
        };
        p.Children.Add(clear);
        p.Children.Add(Header("Open-source components"));
        var notices = Path.Combine(AppContext.BaseDirectory, "THIRD-PARTY-NOTICES.md");
        p.Children.Add(new TextBlock { Text = File.Exists(notices) ? File.ReadAllText(notices) : "THIRD-PARTY-NOTICES.md is missing from this build.", TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, FontSize = 12 });
        return new ScrollViewer { Content = p };
    }
}
