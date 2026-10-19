using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Orange.App.Services;
using Orange.Core;
using Orange.Core.Presets;
using Windows.Storage.Pickers;

namespace Orange.App.Views;

/// <summary>Settings → Presets (list, new preset form with live name preview, import/export) and the wheel editor.</summary>
public static class PresetsSettings
{
    private static OperationRegistry Reg => ConversionService.Instance.Registry;

    public static UIElement Build(IntPtr hwnd, Action refresh)
    {
        var p = new StackPanel { Spacing = 10 };
        var presets = ConversionService.Instance.Presets;
        p.Children.Add(new TextBlock { Text = "A preset is a format plus its settings and a naming rule. Pinned presets appear first on the wheel for files they fit.", Opacity = 0.7, TextWrapping = TextWrapping.Wrap });

        foreach (var pr in presets.ToList())
        {
            var op = Reg.All.FirstOrDefault(o => o.Id == pr.OperationId);
            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var t = new StackPanel();
            t.Children.Add(new TextBlock { Text = pr.Name, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            var opts = string.Join(", ", pr.Options.Select(kv => $"{kv.Key} {kv.Value}"));
            t.Children.Add(new TextBlock { Text = $"{op?.Category} → {op?.Title ?? pr.OperationId}{(opts.Length > 0 ? " · " + opts : "")} · {pr.Output.NamePattern}", FontSize = 12, Opacity = 0.7, TextTrimming = TextTrimming.CharacterEllipsis });
            row.Children.Add(t);
            var pin = new ToggleSwitch { IsOn = pr.PinnedOnWheel, OnContent = "On wheel", OffContent = "Hidden", MinWidth = 0 };
            var name = pr.Name;
            pin.Toggled += (_, _) => Replace(name, x => x with { PinnedOnWheel = pin.IsOn });
            Grid.SetColumn(pin, 1); row.Children.Add(pin);
            var del = new Button { Content = "Delete" };
            del.Click += (_, _) => { var l = ConversionService.Instance.Presets.Where(x => x.Name != name).ToList(); SaveAll(l); refresh(); };
            Grid.SetColumn(del, 2); row.Children.Add(del);
            p.Children.Add(row);
        }

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var import = new Button { Content = "Import…" };
        import.Click += async (_, _) =>
        {
            var fo = new FileOpenPicker(); fo.FileTypeFilter.Add(".json");
            WinRT.Interop.InitializeWithWindow.Initialize(fo, hwnd);
            var f = await fo.PickSingleFileAsync(); if (f is null) return;
            try
            {
                var incoming = PresetJson.Import(await File.ReadAllTextAsync(f.Path), Reg);
                var merged = ConversionService.Instance.Presets.Where(x => !incoming.Any(i => i.Name.Equals(x.Name, StringComparison.OrdinalIgnoreCase))).Concat(incoming).ToList();
                SaveAll(merged); refresh();
            }
            catch (OrangeException e) { ProgressWindow.ShowError("Presets", e.Message, e.Detail); }
        };
        var export = new Button { Content = "Export…" };
        export.Click += async (_, _) =>
        {
            var fs = new FileSavePicker { SuggestedFileName = "orange-presets" }; fs.FileTypeChoices.Add("JSON", new List<string> { ".json" });
            WinRT.Interop.InitializeWithWindow.Initialize(fs, hwnd);
            var f = await fs.PickSaveFileAsync(); if (f is null) return;
            await File.WriteAllTextAsync(f.Path, PresetJson.Export(ConversionService.Instance.Presets));
        };
        var reset = new Button { Content = "Restore defaults" };
        reset.Click += (_, _) => { SaveAll(DefaultPresets.All.ToList()); refresh(); };
        buttons.Children.Add(import); buttons.Children.Add(export); buttons.Children.Add(reset);
        p.Children.Add(buttons);
        p.Children.Add(NewPresetForm(refresh));
        return p;
    }

    private static void Replace(string name, Func<Preset, Preset> change)
    {
        var l = ConversionService.Instance.Presets.Select(x => x.Name == name ? change(x) : x).ToList();
        SaveAll(l);
    }

    private static void SaveAll(List<Preset> list)
    {
        try { ConversionService.Instance.SavePresets(list); }
        catch (OrangeException e) { ProgressWindow.ShowError("Presets", e.Message, e.Detail); }
    }

    private static UIElement NewPresetForm(Action refresh)
    {
        var box = new StackPanel { Spacing = 8, Padding = new Thickness(12), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
            BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"] };
        box.Children.Add(new TextBlock { Text = "New preset", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        var ops = Reg.All.Where(o => !o.CombinesInputs && !o.ProducesFolder).OrderBy(o => o.Category).ThenBy(o => o.Group).ThenBy(o => o.Title).ToList();
        var name = new TextBox { Header = "Name", PlaceholderText = "e.g. Instagram photo", Width = 300, HorizontalAlignment = HorizontalAlignment.Left };
        var opBox = new ComboBox { Header = "Operation", ItemsSource = ops.Select(o => $"{o.Category} → {o.Title}").ToList(), SelectedIndex = Math.Max(0, ops.FindIndex(o => o.Id == "image.to.webp")), MinWidth = 300 };
        var optPanel = new StackPanel { Spacing = 6 };
        var optBoxes = new Dictionary<string, TextBox>();
        void BuildOptions()
        {
            optPanel.Children.Clear(); optBoxes.Clear();
            if (opBox.SelectedIndex < 0) return;
            foreach (var spec in ops[opBox.SelectedIndex].Options.Where(s => s.Type != OptionType.TimeSpan).Take(6))
            {
                var tb = new TextBox { Header = spec.Label + (spec.Choices is { } c ? $" ({string.Join(" / ", c)})" : spec.Min is { } mn ? $" ({mn}–{spec.Max})" : ""), Text = spec.Default, Width = 300, HorizontalAlignment = HorizontalAlignment.Left };
                optBoxes[spec.Name] = tb; optPanel.Children.Add(tb);
            }
        }
        opBox.SelectionChanged += (_, _) => BuildOptions();
        BuildOptions();
        var pattern = new TextBox { Header = "File name", Text = "{name} {preset}.{ext}", Width = 300, HorizontalAlignment = HorizontalAlignment.Left };
        var where = new ComboBox { Header = "Save to", ItemsSource = new List<string> { "Beside the original", "A subfolder" }, SelectedIndex = 0, MinWidth = 300 };
        var sub = new TextBox { Header = "Subfolder name", Text = "Orange", Width = 300, HorizontalAlignment = HorizontalAlignment.Left, Visibility = Visibility.Collapsed };
        where.SelectionChanged += (_, _) => sub.Visibility = where.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        var conflict = new ComboBox { Header = "If the name is taken", ItemsSource = new List<string> { "Keep both", "Replace", "Skip" }, SelectedIndex = 0, MinWidth = 300 };
        var meta = new ComboBox { Header = "Metadata", ItemsSource = new List<string> { "Keep (as the operation does)", "Strip" }, SelectedIndex = 0, MinWidth = 300 };
        var preview = new TextBlock { FontSize = 12, Opacity = 0.8 };
        var error = new TextBlock { FontSize = 12, Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.IndianRed), TextWrapping = TextWrapping.Wrap };
        Preset Current() => new()
        {
            Name = name.Text.Trim(), OperationId = ops[Math.Max(0, opBox.SelectedIndex)].Id,
            Options = optBoxes.Where(kv => kv.Value.Text.Trim().Length > 0).ToDictionary(kv => kv.Key, kv => kv.Value.Text.Trim()),
            Metadata = meta.SelectedIndex == 1 ? MetadataPolicy.Strip : MetadataPolicy.AsOperation,
            Output = new OutputRule { NamePattern = pattern.Text, Location = where.SelectedIndex == 1 ? OutputLocation.Subfolder : OutputLocation.BesideOriginal,
                Subfolder = sub.Text.Trim(), Conflict = (ConflictPolicy)Math.Max(0, conflict.SelectedIndex) },
        };
        void Preview()
        {
            try
            {
                var c = Current();
                var op = ops[Math.Max(0, opBox.SelectedIndex)];
                var ext = op.OutputExtension ?? op.Category switch { FileKind.Image => "jpg", FileKind.Video => "mp4", FileKind.Audio => "mp3", FileKind.Pdf => "pdf", _ => "txt" };
                var n = (c with { Name = c.Name.Length > 0 ? c.Name : "Preset" }).FileNameFor(@"C:\Photos\holiday." + ext, ext, op.Title);
                preview.Text = "Example: " + (c.Output.Location == OutputLocation.Subfolder ? $@"{SafePaths.SanitizeFileName(c.Output.Subfolder)}\" : "") + n;
            }
            catch (Exception e) { preview.Text = e.Message; }
        }
        foreach (var tb in new[] { name, pattern, sub }) tb.TextChanged += (_, _) => Preview();
        foreach (var cb in new[] { opBox, where }) cb.SelectionChanged += (_, _) => Preview();
        Preview();
        var save = new Button { Content = "Save preset", Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        save.Click += (_, _) =>
        {
            try
            {
                var c = Current();
                PresetJson.Validate(c, Reg);
                var list = ConversionService.Instance.Presets.Where(x => !x.Name.Equals(c.Name, StringComparison.OrdinalIgnoreCase)).Append(c).ToList();
                ConversionService.Instance.SavePresets(list);
                refresh();
            }
            catch (OrangeException e) { error.Text = e.Message; }
        };
        foreach (var e in new UIElement[] { name, opBox, optPanel, pattern, preview, where, sub, conflict, meta, save, error }) box.Children.Add(e);
        return box;
    }

    // ---------------- wheel editor

    public static UIElement WheelEditor(Action refresh)
    {
        var p = new StackPanel { Spacing = 8 };
        p.Children.Add(new TextBlock { Text = "Choose which petals each kind of file shows, and their order. Favourites and pinned presets still come first.", Opacity = 0.7, TextWrapping = TextWrapping.Wrap });
        var kinds = new[] { FileKind.Image, FileKind.Video, FileKind.Audio, FileKind.Pdf, FileKind.Document, FileKind.Subtitle, FileKind.Archive };
        var kindBox = new ComboBox { Header = "File type", ItemsSource = kinds.Select(k => k.ToString()).ToList(), SelectedIndex = 0, MinWidth = 200 };
        var groupBox = new ComboBox { Header = "Wheel", ItemsSource = new List<string> { "Convert", "Tools" }, SelectedIndex = 0, MinWidth = 200 };
        var pickers = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        pickers.Children.Add(kindBox); pickers.Children.Add(groupBox);
        p.Children.Add(pickers);
        var body = new Grid { ColumnSpacing = 24 };
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var list = new StackPanel { Spacing = 4 };
        var preview = new WheelControl(240);
        Grid.SetColumn(preview, 1);
        body.Children.Add(list); body.Children.Add(preview);
        p.Children.Add(body);

        void Render()
        {
            var kind = kinds[Math.Max(0, kindBox.SelectedIndex)];
            var group = groupBox.SelectedIndex == 1 ? OperationGroup.Tool : OperationGroup.Convert;
            var layout = AppSettings.Current.Layout;
            var all = Reg.All.Where(o => o.Category == kind && o.Group == group).ToList();
            var ordered = layout.Apply(kind, group, all).Concat(all.Where(o => layout.IsHidden(kind, group, o.Id))).ToList();
            list.Children.Clear();
            foreach (var op in ordered)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                var show = new CheckBox { Content = op.Title, IsChecked = !layout.IsHidden(kind, group, op.Id), MinWidth = 200 };
                var id = op.Id;
                show.Click += (_, _) => { layout.SetHidden(kind, group, id, show.IsChecked != true); AppSettings.Current.Save(); Render(); };
                var up = new Button { Content = "\u25B2", Padding = new Thickness(8, 2, 8, 2) };
                var down = new Button { Content = "\u25BC", Padding = new Thickness(8, 2, 8, 2) };
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(up, $"Move {op.Title} up");
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(down, $"Move {op.Title} down");
                up.Click += (_, _) => { layout.Move(kind, group, all, id, -1); AppSettings.Current.Save(); Render(); };
                down.Click += (_, _) => { layout.Move(kind, group, all, id, +1); AppSettings.Current.Save(); Render(); };
                row.Children.Add(show); row.Children.Add(up); row.Children.Add(down);
                list.Children.Add(row);
            }
            var visible = layout.Apply(kind, group, all).Take(8).Select(o => new WheelItem(o.Title, group == OperationGroup.Tool ? Glyphs.For(o) : null, o, o.Title)).ToList();
            preview.SetItems(visible, "Preview");
        }
        kindBox.SelectionChanged += (_, _) => Render();
        groupBox.SelectionChanged += (_, _) => Render();
        Render();
        var resetLayout = new Button { Content = "Reset all petals" };
        resetLayout.Click += (_, _) => { AppSettings.Current.Layout = new WheelLayout(); AppSettings.Current.Save(); Render(); };
        p.Children.Add(resetLayout);
        return p;
    }
}
