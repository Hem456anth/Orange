using System.Collections.ObjectModel;
using System.Globalization;
using ImageMagick;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Orange.App.Services;
using Orange.Core;
using Orange.Core.Providers;
using Windows.Foundation;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.UI;
using Orientation = Microsoft.UI.Xaml.Controls.Orientation;

namespace Orange.App.Views;

/// <summary>
/// The editing panels behind the tools wheel (Edit Photo, Add BG, Redact, Annotate, Crop, Trim, Bleep, Channels,
/// Metadata, Reorder pages). Every panel previews with the same engine code that writes the file, then returns
/// plain options; the conversion itself still runs through <see cref="ConversionService"/> like any other tool.
/// </summary>
public static class Editors
{
    private static readonly HashSet<string> Panels =
    [
        "image.edit", "image.add-background", "image.redact", "image.annotate", "image.crop",
        "audio.trim", "video.trim", "audio.bleep", "audio.channels",
        "image.metadata", "audio.metadata", "video.metadata", "pdf.reorder",
    ];

    public static bool HasPanel(OperationDescriptor op) => Panels.Contains(op.Id);
    /// <summary>The most recently opened panel (self-test screenshots).</summary>
    public static EditorWindow? Last { get; private set; }

    public static async Task<IReadOnlyDictionary<string, string>?> AskAsync(OperationDescriptor op, IReadOnlyList<string> files, IReadOnlyDictionary<string, string> remembered)
    {
        Last = null;
        try
        {
            EditorWindow w = op.Id switch
            {
                "image.edit" => new PhotoEditor(op, files[0]),
                "image.add-background" => new BackgroundEditor(op, files[0], remembered),
                "image.redact" or "image.annotate" or "image.crop" => new RegionEditor(op, files[0], remembered),
                "audio.trim" or "video.trim" or "audio.bleep" or "audio.channels" => new AudioEditor(op, files[0], remembered),
                "image.metadata" or "audio.metadata" or "video.metadata" => new MetadataEditor(op, files[0]),
                "pdf.reorder" => new ReorderEditor(op, files[0]),
                _ => throw new InvalidOperationException(op.Id),
            };
            Last = w;
            w.Activate();
            return await w.Result;
        }
        catch (Exception e)
        {
            Diagnostics.Event("editor-failed " + e.ToString().Replace(Environment.NewLine, " | "));
            // A panel that can't open (e.g. no audio track to draw) falls back to the plain settings sheet.
            return await OptionsWindow.AskAsync(op, files, remembered);
        }
    }

    internal static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";
    internal static string Inv(double d) => d.ToString("0.###", CultureInfo.InvariantCulture);
    internal static string Time(double s) => TimeSpan.FromSeconds(Math.Max(0, s)).ToString(s >= 3600 ? @"h\:mm\:ss\.ff" : @"m\:ss\.ff", CultureInfo.InvariantCulture);
}

/// <summary>Shared chrome: gradient title bar with a close button, content area, error line, Cancel / Apply.</summary>
public abstract class EditorWindow : Window
{
    private readonly TaskCompletionSource<IReadOnlyDictionary<string, string>?> _tcs = new();
    public Task<IReadOnlyDictionary<string, string>?> Result => _tcs.Task;
    protected readonly OperationDescriptor Op;
    protected readonly string File;
    protected readonly Grid Body = new();
    protected readonly TextBlock Status = new() { FontSize = 12, Opacity = 0.8, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    protected readonly Button ApplyButton = new() { Content = L.T("Apply"), MinWidth = 96 };
    private readonly string _previewDir = System.IO.Path.Combine(AppSettings.Folder, "preview", Guid.NewGuid().ToString("N"));
    private int _previewN;

    protected EditorWindow(OperationDescriptor op, string file, string title, double width, double height)
    {
        Op = op; File = file;
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(48) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new Grid { Background = WheelControl.BrandGradient(withViolet: false, angle: 1) };
        var close = new Button { Content = new FontIcon { Glyph = "\uE711", FontSize = 12 }, Background = new SolidColorBrush(Colors.Transparent), BorderThickness = new Thickness(0), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Foreground = new SolidColorBrush(Colors.White) };
        AutomationProperties.SetName(close, "Close");
        close.Click += (_, _) => Cancel();
        header.Children.Add(close);
        header.Children.Add(new TextBlock { Text = title, FontSize = 15, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = new SolidColorBrush(Colors.White), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
        root.Children.Add(header);

        Body.Margin = new Thickness(18, 14, 18, 6);
        Grid.SetRow(Body, 1);
        root.Children.Add(Body);

        var footer = new Grid { Padding = new Thickness(18, 8, 18, 14), ColumnSpacing = 8 };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var cancel = new Button { Content = L.T("Cancel"), MinWidth = 80 };
        cancel.Click += (_, _) => Cancel();
        ApplyButton.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        ApplyButton.Click += (_, _) => TryApply();
        footer.Children.Add(Status);
        Grid.SetColumn(cancel, 1); footer.Children.Add(cancel);
        Grid.SetColumn(ApplyButton, 2); footer.Children.Add(ApplyButton);
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);

        root.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Escape) { Cancel(); e.Handled = true; }
            else if (e.Key == Windows.System.VirtualKey.Enter && (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0) { TryApply(); e.Handled = true; }
        };
        Content = root;
        Closed += (_, _) => { _tcs.TrySetResult(null); OnClosedCleanup(); try { Directory.Delete(_previewDir, true); } catch { } };
        WindowHelpers.Compact(this, $"Orange — {title}", topmost: true);
        WindowHelpers.Place(this, width, height, bottomRight: false);
    }

    /// <summary>Gathers the options; throws <see cref="OrangeException"/> with a message to show when something's missing.</summary>
    protected abstract Dictionary<string, string> Collect();
    protected virtual void OnClosedCleanup() { }

    private void TryApply()
    {
        try
        {
            var o = Collect();
            OptionValidator.Validate(new OperationRequest { Operation = Op, Inputs = [File], Options = o });
            _tcs.TrySetResult(o);
            Close();
        }
        catch (OrangeException ex) { ShowError(ex.Message); }
    }

    private void Cancel() { _tcs.TrySetResult(null); Close(); }

    public string StatusText => Status.Text;
    protected void ShowError(string message) { Status.Text = message; Status.Foreground = new SolidColorBrush(Colors.IndianRed); }
    protected void ShowNote(string message) { Status.Text = message; Status.ClearValue(TextBlock.ForegroundProperty); }

    /// <summary>Writes a preview PNG under %LOCALAPPDATA%\Orange\preview (removed on close) and returns it as an image source.</summary>
    protected async Task<BitmapImage> ToBitmapAsync(MagickImage img)
    {
        Directory.CreateDirectory(_previewDir);
        var path = System.IO.Path.Combine(_previewDir, $"p{Interlocked.Increment(ref _previewN)}.png");
        await Task.Run(() => img.Write(path, MagickFormat.Png));
        var f = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
        using var stream = await f.OpenReadAsync();
        var bmp = new BitmapImage();
        await bmp.SetSourceAsync(stream);
        return bmp;
    }

    protected string PreviewPath(string ext) { Directory.CreateDirectory(_previewDir); return System.IO.Path.Combine(_previewDir, $"r{Interlocked.Increment(ref _previewN)}.{ext}"); }
    protected string PreviewDir => _previewDir;

    /// <summary>Loads the first frame, upright, at most <paramref name="maxSide"/> px: the working copy for previews.</summary>
    protected static Task<(MagickImage Image, uint FullW, uint FullH)> LoadWorkingCopyAsync(string file, uint maxSide) => Task.Run(() =>
    {
        using var col = ImageProvider.Load(file);
        var img = (MagickImage)col[0].Clone();
        img.AutoOrient();
        uint w = img.Width, h = img.Height;
        if (Math.Max(w, h) > maxSide) img.Resize(new MagickGeometry(maxSide, maxSide));
        return (img, w, h);
    });

    protected static TextBlock Label(string text, double size = 12, double opacity = 0.75) => new() { Text = text, FontSize = size, Opacity = opacity, VerticalAlignment = VerticalAlignment.Center };

    protected static Border Canvasy(UIElement child) => new()
    {
        Background = new SolidColorBrush(Color.FromArgb(0xFF, 0x16, 0x12, 0x10)), CornerRadius = new CornerRadius(12), Padding = new Thickness(10), Child = child,
    };

    /// <summary>Debounces preview renders so dragging a slider doesn't queue a render per pixel.</summary>
    protected DispatcherQueueTimer Debouncer(Func<Task> work, int ms = 120)
    {
        var t = DispatcherQueue.CreateTimer();
        t.Interval = TimeSpan.FromMilliseconds(ms);
        t.IsRepeating = false;
        bool busy = false, again = false;
        t.Tick += async (_, _) =>
        {
            if (busy) { again = true; return; }
            busy = true;
            try { do { again = false; await work(); } while (again); }
            catch (OrangeException e) { ShowError(e.Message); }
            catch (Exception e) { ShowError("Preview failed: " + e.Message); }
            finally { busy = false; }
        };
        return t;
    }
}

// ============================================================================ Edit Photo

public sealed class PhotoEditor : EditorWindow
{
    private readonly Image _preview = new() { Stretch = Stretch.Uniform };
    private readonly Dictionary<string, Slider> _sliders = new();
    private readonly ToggleButton _compare = new() { Content = L.T("Show original") };
    private MagickImage? _base;
    private BitmapImage? _original, _edited;
    private int _rotate;
    private readonly DispatcherQueueTimer _render;

    public PhotoEditor(OperationDescriptor op, string file) : base(op, file, "Edit Photo", 760, 700)
    {
        Body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Body.Children.Add(Canvasy(_preview));

        var tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 10, 0, 6) };
        var rot = new Button { Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { new FontIcon { Glyph = "\uE7AD", FontSize = 14 }, new TextBlock { Text = L.T("Rotate") } } } };
        rot.Click += (_, _) => { _rotate = (_rotate + 90) % 360; _render.Start(); };
        var resetAll = new Button { Content = L.T("Reset all") };
        resetAll.Click += (_, _) => { foreach (var s in _sliders.Values) s.Value = 0; _rotate = 0; _render.Start(); };
        _compare.Checked += (_, _) => _preview.Source = _original;
        _compare.Unchecked += (_, _) => _preview.Source = _edited ?? _original;
        AutomationProperties.SetName(_compare, "Show the original photo for comparison");
        tools.Children.Add(rot); tools.Children.Add(resetAll); tools.Children.Add(_compare);
        Grid.SetRow(tools, 1); Body.Children.Add(tools);

        var light = new Grid { ColumnSpacing = 10, RowSpacing = 2 };
        light.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
        light.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        light.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
        light.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
        var heading = new TextBlock { Text = L.T("Light"), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = new SolidColorBrush(WheelControl.Coral), Margin = new Thickness(0, 4, 0, 4) };
        light.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        light.Children.Add(heading);
        int row = 1;
        foreach (var (name, label) in new[] { ("exposure", "Exposure"), ("brightness", "Brightness"), ("contrast", "Contrast"), ("highlights", "Highlights"), ("shadows", "Shadows"), ("saturation", "Saturation") })
        {
            var spec = op.Options.First(o => o.Name == name);
            light.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var l = Label(label, 13, 0.9); Grid.SetRow(l, row); light.Children.Add(l);
            var slider = new Slider { Minimum = spec.Min ?? -100, Maximum = spec.Max ?? 100, StepFrequency = name == "exposure" ? 0.05 : 1, Value = 0, VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetName(slider, label);
            var val = Label(name == "exposure" ? "0.00 EV" : "+0", 12, 0.8); val.HorizontalAlignment = HorizontalAlignment.Right;
            slider.ValueChanged += (_, e) => { val.Text = name == "exposure" ? $"{e.NewValue:+0.00;-0.00;0.00} EV" : $"{e.NewValue:+0;-0;+0}"; _render.Start(); };
            var reset = new Button { Content = new FontIcon { Glyph = "\uE777", FontSize = 12 }, Padding = new Thickness(6), Background = new SolidColorBrush(Colors.Transparent), BorderThickness = new Thickness(0) };
            AutomationProperties.SetName(reset, $"Reset {label}");
            reset.Click += (_, _) => slider.Value = 0;
            Grid.SetRow(slider, row); Grid.SetColumn(slider, 1); light.Children.Add(slider);
            Grid.SetRow(val, row); Grid.SetColumn(val, 2); light.Children.Add(val);
            Grid.SetRow(reset, row); Grid.SetColumn(reset, 3); light.Children.Add(reset);
            _sliders[name] = slider;
            row++;
        }
        Grid.SetRow(light, 2); Body.Children.Add(light);
        _render = Debouncer(RenderAsync);
        ShowNote("Loading…");
        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        try
        {
            (_base, var w, var h) = await LoadWorkingCopyAsync(File, 1000);
            _original = await ToBitmapAsync(_base);
            _preview.Source = _original;
            ShowNote($"{w} × {h}. Changes are previewed here and saved as a copy.");
        }
        catch (OrangeException e) { ShowError(e.Message); ApplyButton.IsEnabled = false; }
    }

    private async Task RenderAsync()
    {
        if (_base is null) return;
        var opts = Collect();
        var req = new OperationRequest { Operation = Op, Inputs = [File], Options = opts };
        using var copy = (MagickImage)_base.Clone();
        await Task.Run(() => ImageStudioProvider.Adjust(copy, req));
        _edited = await ToBitmapAsync(copy);
        if (_compare.IsChecked != true) _preview.Source = _edited;
    }

    protected override Dictionary<string, string> Collect()
    {
        var d = _sliders.ToDictionary(kv => kv.Key, kv => Editors.Inv(kv.Value.Value));
        d["rotate"] = _rotate.ToString(CultureInfo.InvariantCulture);
        return d;
    }

    protected override void OnClosedCleanup() => _base?.Dispose();
}

// ============================================================================ Add BG

public sealed class BackgroundEditor : EditorWindow
{
    private readonly Image _preview = new() { Stretch = Stretch.Uniform };
    private readonly Slider _padding = new() { Minimum = 0, Maximum = 50, StepFrequency = 1, Value = 8 };
    private readonly Slider _radius = new() { Minimum = 0, Maximum = 50, StepFrequency = 1, Value = 0 };
    private string _background = "white";
    private MagickImage? _base;
    private readonly DispatcherQueueTimer _render;
    private readonly List<Button> _swatches = new();

    private static readonly string[] Colours = ["#FFFFFF", "#000000", "#1C2A48", "#AF52DE", "#C3B1E1", "#FF2D55", "#FF3B30", "#FF6B5E", "#FF8A00", "#8B5A2B", "#8E8E93", "#0A84FF", "#34C759"];

    public BackgroundEditor(OperationDescriptor op, string file, IReadOnlyDictionary<string, string> remembered) : base(op, file, "Add BG", 640, 680)
    {
        if (remembered.TryGetValue("background", out var b) && !b.StartsWith("image:", StringComparison.OrdinalIgnoreCase)) _background = b;
        if (remembered.TryGetValue("padding", out var p) && double.TryParse(p, CultureInfo.InvariantCulture, out var pv)) _padding.Value = pv;
        if (remembered.TryGetValue("radius", out var r) && double.TryParse(r, CultureInfo.InvariantCulture, out var rv)) _radius.Value = rv;
        Body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Body.Children.Add(Canvasy(_preview));

        var controls = new StackPanel { Spacing = 8, Margin = new Thickness(0, 12, 0, 0) };
        controls.Children.Add(new TextBlock { Text = L.T("Background"), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = new SolidColorBrush(WheelControl.Coral) });
        var gradRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        foreach (var (name, (a, bb)) in ImageStudioProvider.Gradients)
        {
            var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
            brush.GradientStops.Add(new GradientStop { Color = ParseColor(a), Offset = 0 });
            brush.GradientStops.Add(new GradientStop { Color = ParseColor(bb), Offset = 1 });
            gradRow.Children.Add(Swatch(brush, name, name));
        }
        var imgBtn = new Button { Content = new FontIcon { Glyph = "\uEB9F", FontSize = 16 }, Width = 40, Height = 32, Padding = new Thickness(0) };
        AutomationProperties.SetName(imgBtn, "Use an image as the background");
        imgBtn.Click += async (_, _) => await PickImageAsync();
        gradRow.Children.Add(imgBtn);
        controls.Children.Add(gradRow);
        var colRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        foreach (var c in Colours) colRow.Children.Add(Swatch(new SolidColorBrush(ParseColor(c)), c, c));
        var custom = new Button { Content = L.T("Custom…"), Height = 32 };
        var picker = new ColorPicker { IsAlphaEnabled = false, IsMoreButtonVisible = false, ColorSpectrumShape = ColorSpectrumShape.Ring };
        var flyoutPanel = new StackPanel { Spacing = 8 };
        var useColour = new Button { Content = L.T("Use colour"), HorizontalAlignment = HorizontalAlignment.Right };
        flyoutPanel.Children.Add(picker); flyoutPanel.Children.Add(useColour);
        var flyout = new Flyout { Content = flyoutPanel };
        useColour.Click += (_, _) => { Choose(Editors.Hex(picker.Color)); flyout.Hide(); };
        custom.Flyout = flyout;
        colRow.Children.Add(custom);
        controls.Children.Add(new ScrollViewer { Content = colRow, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled });

        controls.Children.Add(SliderRow("Padding", _padding, "%"));
        controls.Children.Add(SliderRow("Corner radius", _radius, "%"));
        Grid.SetRow(controls, 1); Body.Children.Add(controls);

        _render = Debouncer(RenderAsync);
        _padding.ValueChanged += (_, _) => _render.Start();
        _radius.ValueChanged += (_, _) => _render.Start();
        ShowNote("Transparent areas take the background. JPG and BMP have no transparency, so the whole picture sits on it.");
        _ = LoadAsync();
    }

    private UIElement SliderRow(string label, Slider s, string unit)
    {
        var g = new Grid { ColumnSpacing = 10 };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
        var val = Label($"{s.Value:0}{unit}");
        s.ValueChanged += (_, e) => val.Text = $"{e.NewValue:0}{unit}";
        AutomationProperties.SetName(s, label);
        g.Children.Add(Label(label, 13, 0.9));
        Grid.SetColumn(s, 1); g.Children.Add(s);
        Grid.SetColumn(val, 2); g.Children.Add(val);
        return g;
    }

    private Button Swatch(Brush brush, string value, string name)
    {
        var b = new Button { Width = 32, Height = 32, Padding = new Thickness(0), Background = brush, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(2), BorderBrush = new SolidColorBrush(Colors.Transparent), Tag = value };
        AutomationProperties.SetName(b, $"Background {name}");
        b.Click += (_, _) => Choose(value);
        _swatches.Add(b);
        return b;
    }

    private void Choose(string value)
    {
        _background = value;
        foreach (var s in _swatches) s.BorderBrush = new SolidColorBrush(string.Equals(s.Tag as string, value, StringComparison.OrdinalIgnoreCase) ? WheelControl.OrangeAccent : Colors.Transparent);
        _render.Start();
    }

    private async Task PickImageAsync()
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker { SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.PicturesLibrary };
        foreach (var e in new[] { ".png", ".jpg", ".jpeg", ".webp", ".heic", ".bmp", ".tif", ".tiff" }) picker.FileTypeFilter.Add(e);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var f = await picker.PickSingleFileAsync();
        if (f is null) return;
        Choose("image:" + f.Path);
    }

    private async Task LoadAsync()
    {
        try { (_base, _, _) = await LoadWorkingCopyAsync(File, 900); Choose(_background); }
        catch (OrangeException e) { ShowError(e.Message); ApplyButton.IsEnabled = false; }
    }

    private async Task RenderAsync()
    {
        if (_base is null) return;
        string bg = _background; int pad = (int)_padding.Value, rad = (int)_radius.Value;
        using var copy = (MagickImage)_base.Clone();
        using var result = await Task.Run(() => ImageStudioProvider.AddBackground(copy, bg, pad, rad));
        _preview.Source = await ToBitmapAsync(result);
        ShowNote("");
    }

    private static Color ParseColor(string hex)
    {
        hex = hex.TrimStart('#');
        return Color.FromArgb(255, Convert.ToByte(hex[..2], 16), Convert.ToByte(hex[2..4], 16), Convert.ToByte(hex[4..6], 16));
    }

    protected override Dictionary<string, string> Collect() => new()
    {
        ["background"] = _background, ["padding"] = Editors.Inv(Math.Round(_padding.Value)), ["radius"] = Editors.Inv(Math.Round(_radius.Value)),
    };

    protected override void OnClosedCleanup() => _base?.Dispose();
}

// ============================================================================ Redact / Annotate / Crop

public sealed class RegionEditor : EditorWindow
{
    private enum Tool { Box, Arrow, Text }
    private readonly Image _image = new() { Stretch = Stretch.Fill };
    private readonly Canvas _overlay = new() { Background = new SolidColorBrush(Colors.Transparent) };
    private readonly Grid _stage = new();
    private readonly List<(Rect R, Shape S)> _rects = new();
    private readonly List<(Point A, Point B, Shape S)> _arrows = new();
    private (Point P, TextBlock T)? _text;
    private uint _w, _h;
    private double _scale = 1;
    private Point _start; private Shape? _live; private bool _dragging;
    private Tool _tool = Tool.Box;
    private Color _colour = Color.FromArgb(255, 0xFF, 0x6A, 0x00);
    private readonly ComboBox _style = new() { ItemsSource = new[] { "black", "pixelate", "blur" }, SelectedIndex = 0, MinWidth = 120 };
    private readonly ComboBox _aspect = new() { ItemsSource = new[] { "free", "1:1", "4:3", "3:2", "16:9", "9:16", "4:5" }, SelectedIndex = 0, MinWidth = 100 };
    private readonly TextBox _textBox = new() { PlaceholderText = L.T("Text to add"), MinWidth = 220 };
    private readonly Slider _size = new() { Minimum = 12, Maximum = 200, Value = 48, Width = 140 };
    private bool IsCrop => Op.Id == "image.crop";
    private bool IsAnnotate => Op.Id == "image.annotate";

    public RegionEditor(OperationDescriptor op, string file, IReadOnlyDictionary<string, string> remembered)
        : base(op, file, op.Id switch { "image.redact" => "Redact", "image.annotate" => "Annotate", _ => "Crop" }, 820, 680)
    {
        Body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 0, 0, 10) };
        if (op.Id == "image.redact")
        {
            if (remembered.TryGetValue("style", out var st) && Array.IndexOf(new[] { "black", "pixelate", "blur" }, st) is var i && i >= 0) _style.SelectedIndex = i;
            bar.Children.Add(Label("Drag over what to hide.", 13, 0.9)); bar.Children.Add(_style);
            AutomationProperties.SetName(_style, "Redaction style");
        }
        else if (IsAnnotate)
        {
            foreach (var (t, g, n) in new[] { (Tool.Box, "\uE7FB", "Box"), (Tool.Arrow, "\uE72A", "Arrow"), (Tool.Text, "\uE8D2", "Text") })
            {
                var tb = new ToggleButton { Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { new FontIcon { Glyph = g, FontSize = 13 }, new TextBlock { Text = n } } }, IsChecked = t == Tool.Box, Tag = t };
                tb.Click += (s, _) => { _tool = (Tool)((ToggleButton)s).Tag; foreach (var o in bar.Children.OfType<ToggleButton>()) o.IsChecked = (Tool)o.Tag == _tool; };
                bar.Children.Add(tb);
            }
            foreach (var hex in new[] { "#FF6A00", "#FF3B30", "#FFD60A", "#34C759", "#0A84FF", "#FFFFFF", "#000000" })
            {
                var c = Color.FromArgb(255, Convert.ToByte(hex[1..3], 16), Convert.ToByte(hex[3..5], 16), Convert.ToByte(hex[5..7], 16));
                var b = new Button { Width = 26, Height = 26, Padding = new Thickness(0), Background = new SolidColorBrush(c), CornerRadius = new CornerRadius(13) };
                AutomationProperties.SetName(b, $"Colour {hex}");
                b.Click += (_, _) => _colour = c;
                bar.Children.Add(b);
            }
            bar.Children.Add(_textBox); bar.Children.Add(_size);
            AutomationProperties.SetName(_size, "Text size");
        }
        else
        {
            bar.Children.Add(Label("Drag the area to keep.", 13, 0.9)); bar.Children.Add(Label("Aspect")); bar.Children.Add(_aspect);
        }
        var undo = new Button { Content = L.T("Undo") }; undo.Click += (_, _) => Undo();
        var clear = new Button { Content = L.T("Clear") }; clear.Click += (_, _) => { while (_rects.Count + _arrows.Count > 0 || _text != null) Undo(); };
        bar.Children.Add(undo); bar.Children.Add(clear);
        Body.Children.Add(new ScrollViewer { Content = bar, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled });

        _stage.Children.Add(_image); _stage.Children.Add(_overlay);
        _stage.HorizontalAlignment = HorizontalAlignment.Center; _stage.VerticalAlignment = VerticalAlignment.Center;
        var holder = Canvasy(_stage);
        Grid.SetRow(holder, 1); Body.Children.Add(holder);
        _overlay.PointerPressed += OnDown; _overlay.PointerMoved += OnMove; _overlay.PointerReleased += OnUp;
        ShowNote("Loading…");
        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        try
        {
            var (img, w, h) = await LoadWorkingCopyAsync(File, 1400);
            _w = w; _h = h;
            using (img) _image.Source = await ToBitmapAsync(img);
            // Fit inside ~760×500 DIPs.
            _scale = Math.Min(760.0 / w, 480.0 / h);
            _stage.Width = _image.Width = _overlay.Width = w * _scale;
            _stage.Height = _image.Height = _overlay.Height = h * _scale;
            ShowNote($"{w} × {h} px");
        }
        catch (OrangeException e) { ShowError(e.Message); ApplyButton.IsEnabled = false; }
    }

    private Brush Stroke => new SolidColorBrush(IsAnnotate ? _colour : WheelControl.OrangeAccent);

    private void OnDown(object sender, PointerRoutedEventArgs e)
    {
        _start = Clamp(e.GetCurrentPoint(_overlay).Position);
        if (IsAnnotate && _tool == Tool.Text)
        {
            if (_text is { } old) _overlay.Children.Remove(old.T);
            var tb = new TextBlock { Text = string.IsNullOrWhiteSpace(_textBox.Text) ? "Text" : _textBox.Text, Foreground = new SolidColorBrush(_colour), FontSize = Math.Max(8, _size.Value * _scale) };
            Canvas.SetLeft(tb, _start.X); Canvas.SetTop(tb, _start.Y);
            _overlay.Children.Add(tb); _text = (_start, tb);
            return;
        }
        if (IsCrop) foreach (var r in _rects.ToList()) { _overlay.Children.Remove(r.S); _rects.Remove(r); }
        _dragging = true;
        _overlay.CapturePointer(e.Pointer);
        _live = IsAnnotate && _tool == Tool.Arrow
            ? new Line { Stroke = Stroke, StrokeThickness = 4, X1 = _start.X, Y1 = _start.Y, X2 = _start.X, Y2 = _start.Y, StrokeEndLineCap = PenLineCap.Triangle }
            : new Rectangle { Stroke = Stroke, StrokeThickness = 2, Fill = new SolidColorBrush(Op.Id == "image.redact" ? Color.FromArgb(0x99, 0, 0, 0) : Colors.Transparent) };
        _overlay.Children.Add(_live);
        Update(_start);
    }

    private void OnMove(object sender, PointerRoutedEventArgs e) { if (_dragging) Update(Clamp(e.GetCurrentPoint(_overlay).Position)); }

    private void OnUp(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging || _live is null) return;
        _dragging = false;
        _overlay.ReleasePointerCapture(e.Pointer);
        var end = Clamp(e.GetCurrentPoint(_overlay).Position);
        if (_live is Line l)
        {
            if (Math.Abs(l.X2 - l.X1) + Math.Abs(l.Y2 - l.Y1) < 6) { _overlay.Children.Remove(l); return; }
            _arrows.Add((new Point(l.X1, l.Y1), new Point(l.X2, l.Y2), l));
        }
        else
        {
            var r = RectOf(_start, end);
            if (r.Width < 4 || r.Height < 4) { _overlay.Children.Remove(_live); return; }
            _rects.Add((r, _live));
        }
        _live = null;
        ShowNote(IsCrop && _rects.Count == 1 ? $"{(int)(_rects[0].R.Width / _scale)} × {(int)(_rects[0].R.Height / _scale)} px" : "");
    }

    private void Update(Point p)
    {
        if (_live is Line l) { l.X2 = p.X; l.Y2 = p.Y; return; }
        if (_live is not Rectangle rect) return;
        var r = RectOf(_start, p);
        rect.Width = r.Width; rect.Height = r.Height;
        Canvas.SetLeft(rect, r.X); Canvas.SetTop(rect, r.Y);
    }

    private Rect RectOf(Point a, Point b)
    {
        double x = Math.Min(a.X, b.X), y = Math.Min(a.Y, b.Y), w = Math.Abs(b.X - a.X), h = Math.Abs(b.Y - a.Y);
        if (IsCrop && _aspect.SelectedItem is string asp && asp != "free")
        {
            var p = asp.Split(':').Select(double.Parse).ToArray();
            double ratio = p[0] / p[1];
            h = w / ratio;
            if (b.Y < a.Y) y = a.Y - h;
            if (y < 0) { y = 0; h = a.Y; w = h * ratio; if (b.X < a.X) x = a.X - w; }
            if (y + h > _overlay.Height) { h = _overlay.Height - y; w = h * ratio; if (b.X < a.X) x = a.X - w; }
        }
        return new Rect(x, y, w, h);
    }

    private Point Clamp(Point p) => new(Math.Clamp(p.X, 0, _overlay.Width), Math.Clamp(p.Y, 0, _overlay.Height));

    private void Undo()
    {
        if (_text is { } t && (_rects.Count + _arrows.Count == 0 || IsAnnotate && _tool == Tool.Text)) { _overlay.Children.Remove(t.T); _text = null; return; }
        if (_arrows.Count > 0 && (_rects.Count == 0 || _overlay.Children.IndexOf(_arrows[^1].S) > _overlay.Children.IndexOf(_rects[^1].S)))
        { _overlay.Children.Remove(_arrows[^1].S); _arrows.RemoveAt(_arrows.Count - 1); return; }
        if (_rects.Count > 0) { _overlay.Children.Remove(_rects[^1].S); _rects.RemoveAt(_rects.Count - 1); return; }
        if (_text is { } t2) { _overlay.Children.Remove(t2.T); _text = null; }
    }

    private string Px(double v) => ((int)Math.Round(v / _scale)).ToString(CultureInfo.InvariantCulture);
    private string RectPx(Rect r) => $"{Px(r.X)},{Px(r.Y)},{Px(r.Width)},{Px(r.Height)}";

    protected override Dictionary<string, string> Collect()
    {
        switch (Op.Id)
        {
            case "image.redact":
                if (_rects.Count == 0) throw new OrangeException("Drag over at least one area to redact.");
                return new() { ["regions"] = string.Join(";", _rects.Select(r => RectPx(r.R))), ["style"] = (string)_style.SelectedItem };
            case "image.annotate":
                if (_rects.Count + _arrows.Count == 0 && _text is null) throw new OrangeException("Draw a box or an arrow, or click to place text.");
                var d = new Dictionary<string, string>
                {
                    ["boxes"] = string.Join(";", _rects.Select(r => RectPx(r.R))),
                    ["arrows"] = string.Join(";", _arrows.Select(a => $"{Px(a.A.X)},{Px(a.A.Y)},{Px(a.B.X)},{Px(a.B.Y)}")),
                    ["color"] = Editors.Hex(_colour), ["size"] = Px(_size.Value * _scale),
                    ["stroke"] = Math.Max(2, (int)Math.Round(Math.Max(_w, _h) / 250.0)).ToString(CultureInfo.InvariantCulture),
                    ["text"] = "",
                };
                if (_text is { } t) { d["text"] = string.IsNullOrWhiteSpace(_textBox.Text) ? "Text" : _textBox.Text; d["x"] = Px(t.P.X); d["y"] = Px(t.P.Y); }
                return d;
            default:
                if (_rects.Count == 0)
                {
                    var asp = _aspect.SelectedItem as string ?? "free";
                    if (asp == "free") throw new OrangeException("Drag the area to keep, or pick an aspect for a centred crop.");
                    return new() { ["aspect"] = asp };
                }
                var rr = _rects[0].R;
                return new() { ["aspect"] = "custom", ["x"] = Px(rr.X), ["y"] = Px(rr.Y), ["width"] = Px(rr.Width), ["height"] = Px(rr.Height) };
        }
    }
}

// ============================================================================ Trim / Bleep / Channels

public sealed class AudioEditor : EditorWindow
{
    private const double W = 640, H = 120;
    private readonly Canvas _wave = new() { Width = W, Height = H, Background = new SolidColorBrush(Color.FromArgb(0x20, 0xFF, 0x7A, 0x00)) };
    private readonly Rectangle _sel = new() { Fill = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0x3B, 0x30)), Stroke = new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0x3B, 0x30)), StrokeThickness = 2, IsHitTestVisible = false };
    private readonly Rectangle _left = Handle(), _right = Handle();
    private readonly Line _playhead = new() { Stroke = new SolidColorBrush(Colors.White), StrokeThickness = 1.5, Y1 = 0, Y2 = H, Visibility = Visibility.Collapsed, IsHitTestVisible = false };
    private readonly TextBlock _startText = Label("Start 0:00.00", 12, 0.85), _endText = Label("End 0:00.00", 12, 0.85), _selText = Label("", 12, 0.85);
    private readonly List<(double S, double E, Rectangle R)> _bleeps = new();
    private readonly TextBlock _bleepCount = Label("Bleeps 0", 13, 0.9);
    private readonly ComboBox _sound = new() { ItemsSource = new[] { "beep", "silence" }, SelectedIndex = 0 };
    private readonly ToggleButton _mono = new() { Content = L.T("Mono") }, _stereo = new() { Content = L.T("Stereo") };
    private readonly CheckBox _autoSilence = new() { Content = L.T("Trim silent ends automatically") };
    private float[] _peaks = [];
    private double _duration, _start, _end;
    private int _drag;   // 0 none, 1 left handle, 2 right handle, 3 bleep range
    private double _bleepStart; private Rectangle? _liveBleep;
    private MediaPlayer? _player;
    private DispatcherQueueTimer? _tick;
    private double _stopAt;
    private readonly MediaProvider _media = new(ToolLocator.Default);
    private bool IsTrim => Op.Id is "audio.trim" or "video.trim";

    private static Rectangle Handle() => new() { Width = 8, Height = H, Fill = new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0x3B, 0x30)), RadiusX = 3, RadiusY = 3 };

    public AudioEditor(OperationDescriptor op, string file, IReadOnlyDictionary<string, string> remembered)
        : base(op, file, op.Id switch { "audio.bleep" => "Bleeps", "audio.channels" => "Convert Audio Channels", _ => op.Category == FileKind.Video ? "Trim Video" : "Trim Audio" }, 700, op.Id == "audio.channels" ? 420 : 470)
    {
        var panel = new StackPanel { Spacing = 10 };
        var top = new Grid();
        top.Children.Add(Label(op.Id switch { "audio.bleep" => "Drag across the waveform to mark each bleep.", "audio.channels" => "Output channel preview", _ => "Drag the ends to trim" }, 12, 0.75));
        _selText.HorizontalAlignment = HorizontalAlignment.Right; top.Children.Add(_selText);
        panel.Children.Add(top);
        panel.Children.Add(new Border { CornerRadius = new CornerRadius(10), Child = _wave, HorizontalAlignment = HorizontalAlignment.Center });

        if (IsTrim)
        {
            var times = new Grid(); _endText.HorizontalAlignment = HorizontalAlignment.Right;
            times.Children.Add(_startText); times.Children.Add(_endText);
            panel.Children.Add(times);
            _autoSilence.Checked += (_, _) => AutoSilence();
            panel.Children.Add(_autoSilence);
        }
        else if (op.Id == "audio.bleep")
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            var undo = new Button { Content = L.T("Undo") };
            undo.Click += (_, _) => { if (_bleeps.Count > 0) { _wave.Children.Remove(_bleeps[^1].R); _bleeps.RemoveAt(_bleeps.Count - 1); Count(); } };
            row.Children.Add(_bleepCount); row.Children.Add(Label("Replace with")); row.Children.Add(_sound); row.Children.Add(undo);
            AutomationProperties.SetName(_sound, "Replace bleeped sections with");
            panel.Children.Add(row);
            if (remembered.TryGetValue("sound", out var snd) && snd == "silence") _sound.SelectedIndex = 1;
        }
        else
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 0 };
            _mono.Click += (_, _) => { _mono.IsChecked = true; _stereo.IsChecked = false; };
            _stereo.Click += (_, _) => { _stereo.IsChecked = true; _mono.IsChecked = false; };
            bool mono = !remembered.TryGetValue("channels", out var ch) || ch == "mono";
            _mono.IsChecked = mono; _stereo.IsChecked = !mono;
            row.Children.Add(_mono); row.Children.Add(_stereo);
            panel.Children.Add(row);
        }

        var play = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        if (IsTrim)
        {
            var b = PlayButton("Play Selected Range"); b.Click += (_, _) => Play(File, _start, _end); play.Children.Add(b);
        }
        else
        {
            var orig = PlayButton("Original"); orig.Click += (_, _) => Play(File, 0, _duration);
            var after = PlayButton(op.Id == "audio.bleep" ? "With Bleeps" : "Play Output"); after.Click += async (_, _) => await PlayRenderedAsync();
            play.Children.Add(orig); play.Children.Add(after);
        }
        var stop = new Button { Content = new FontIcon { Glyph = "\uE71A", FontSize = 12 } }; AutomationProperties.SetName(stop, "Stop");
        stop.Click += (_, _) => StopPlayback();
        play.Children.Add(stop);
        panel.Children.Add(play);
        Body.Children.Add(panel);

        _wave.PointerPressed += OnDown; _wave.PointerMoved += OnMove; _wave.PointerReleased += OnUp;
        ShowNote("Reading the waveform…");
        _ = LoadAsync();
    }

    private static Button PlayButton(string text) => new() { Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { new FontIcon { Glyph = "\uE768", FontSize = 12 }, new TextBlock { Text = text } } } };

    private async Task LoadAsync()
    {
        try { await LoadCoreAsync(); }
        catch (OrangeException e) { ShowError(e.Message); ApplyButton.IsEnabled = false; }
        catch (Exception e) { ShowError("Couldn't read this file: " + e.Message); ApplyButton.IsEnabled = false; }
    }

    private async Task LoadCoreAsync()
    {
        var info = await _media.ProbeAsync(File, CancellationToken.None);
        _duration = info.DurationSeconds; _start = 0; _end = _duration;
        if (info.HasAudio) _peaks = await _media.WaveformAsync(File, (int)W / 2, CancellationToken.None);
        else _peaks = new float[(int)W / 2];
        DrawWave();
        if (IsTrim) { _wave.Children.Add(_sel); _wave.Children.Add(_left); _wave.Children.Add(_right); Layout(); }
        _wave.Children.Add(_playhead);
        ShowNote(info.HasAudio ? $"{Editors.Time(_duration)} long" : "No audio track. You can still trim the video by its length.");
    }

    private void DrawWave()
    {
        var poly = new Polygon { Fill = new SolidColorBrush(WheelControl.OrangeAccent), IsHitTestVisible = false };
        var pts = new PointCollection();
        double step = W / Math.Max(1, _peaks.Length), mid = H / 2;
        for (int i = 0; i < _peaks.Length; i++) pts.Add(new Point(i * step, mid - Math.Max(0.5, _peaks[i] * (mid - 4))));
        for (int i = _peaks.Length - 1; i >= 0; i--) pts.Add(new Point(i * step, mid + Math.Max(0.5, _peaks[i] * (mid - 4))));
        poly.Points = pts;
        _wave.Children.Insert(0, poly);
    }

    private double X(double t) => _duration <= 0 ? 0 : t / _duration * W;
    private double T(double x) => Math.Clamp(x / W, 0, 1) * _duration;

    private void Layout()
    {
        Canvas.SetLeft(_left, X(_start) - 4); Canvas.SetLeft(_right, X(_end) - 4);
        Canvas.SetLeft(_sel, X(_start)); _sel.Width = Math.Max(0, X(_end) - X(_start)); _sel.Height = H;
        _startText.Text = $"Start {Editors.Time(_start)}"; _endText.Text = $"End {Editors.Time(_end)}";
        _selText.Text = $"{Editors.Time(_end - _start)} selected";
    }

    private void OnDown(object sender, PointerRoutedEventArgs e)
    {
        var x = e.GetCurrentPoint(_wave).Position.X;
        _wave.CapturePointer(e.Pointer);
        if (IsTrim) { _drag = Math.Abs(x - X(_start)) < Math.Abs(x - X(_end)) ? 1 : 2; Drag(x); return; }
        if (Op.Id != "audio.bleep") return;
        _drag = 3; _bleepStart = T(x);
        _liveBleep = new Rectangle { Height = H, Fill = new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0x3B, 0x30)), IsHitTestVisible = false };
        Canvas.SetLeft(_liveBleep, x); _wave.Children.Add(_liveBleep);
    }

    private void OnMove(object sender, PointerRoutedEventArgs e) { if (_drag != 0) Drag(e.GetCurrentPoint(_wave).Position.X); }

    private void Drag(double x)
    {
        var t = T(x);
        if (_drag == 1) _start = Math.Min(t, _end - 0.05);
        else if (_drag == 2) _end = Math.Max(t, _start + 0.05);
        else if (_drag == 3 && _liveBleep != null)
        {
            double a = Math.Min(_bleepStart, t), b = Math.Max(_bleepStart, t);
            Canvas.SetLeft(_liveBleep, X(a)); _liveBleep.Width = Math.Max(1, X(b) - X(a));
            _selText.Text = $"{Editors.Time(a)} – {Editors.Time(b)}";
            return;
        }
        Layout();
    }

    private void OnUp(object sender, PointerRoutedEventArgs e)
    {
        _wave.ReleasePointerCapture(e.Pointer);
        if (_drag == 3 && _liveBleep != null)
        {
            var t = T(e.GetCurrentPoint(_wave).Position.X);
            double a = Math.Min(_bleepStart, t), b = Math.Max(_bleepStart, t);
            if (b - a < 0.05) _wave.Children.Remove(_liveBleep); else _bleeps.Add((a, b, _liveBleep));
            _liveBleep = null; Count();
        }
        _drag = 0;
    }

    private void Count() => _bleepCount.Text = $"Bleeps {_bleeps.Count}";

    /// <summary>Moves the handles to the first and last moments louder than about −34 dBFS.</summary>
    private void AutoSilence()
    {
        if (_peaks.Length == 0) return;
        int first = Array.FindIndex(_peaks, p => p > 0.02f), last = Array.FindLastIndex(_peaks, p => p > 0.02f);
        if (first < 0) { ShowError("This file sounds silent from start to end."); return; }
        double per = _duration / _peaks.Length;
        _start = Math.Max(0, first * per - 0.05); _end = Math.Min(_duration, (last + 1) * per + 0.05);
        Layout();
    }

    private void Play(string path, double from, double to)
    {
        StopPlayback();
        _player = new MediaPlayer { Source = MediaSource.CreateFromUri(new Uri(path)), AutoPlay = false };
        _stopAt = to;
        _player.MediaOpened += (p, _) => { p.PlaybackSession.Position = TimeSpan.FromSeconds(from); p.Play(); };
        _player.MediaFailed += (_, a) => DispatcherQueue.TryEnqueue(() => ShowError("Windows can't play this format here: " + a.ErrorMessage));
        _tick = DispatcherQueue.CreateTimer();
        _tick.Interval = TimeSpan.FromMilliseconds(40);
        _tick.Tick += (_, _) =>
        {
            if (_player is null) return;
            var pos = _player.PlaybackSession.Position.TotalSeconds;
            _playhead.Visibility = Visibility.Visible; _playhead.X1 = _playhead.X2 = X(Math.Min(pos, _duration));
            if (pos >= _stopAt - 0.02 || _player.PlaybackSession.PlaybackState == MediaPlaybackState.Paused && pos > from + 0.1) StopPlayback();
        };
        _tick.Start();
    }

    private void StopPlayback()
    {
        _tick?.Stop(); _tick = null;
        try { _player?.Pause(); _player?.Dispose(); } catch { }
        _player = null;
        _playhead.Visibility = Visibility.Collapsed;
    }

    /// <summary>Renders the real output with the engine into the preview folder and plays it, so the preview is exactly what Apply will save.</summary>
    private async Task PlayRenderedAsync()
    {
        try
        {
            var opts = Collect();
            ShowNote("Rendering preview…");
            var req = new OperationRequest { Operation = Op, Inputs = [File], Options = opts, OutputDirectory = PreviewDir, Overwrite = OverwritePolicy.Replace };
            Directory.CreateDirectory(PreviewDir);
            var res = await ConversionService.Instance.Registry.ProviderFor(Op).ExecuteAsync(req, null, CancellationToken.None);
            ShowNote("");
            if (res.Outputs.Count > 0) Play(res.Outputs[0], 0, double.MaxValue);
        }
        catch (OrangeException e) { ShowError(e.Message); }
    }

    protected override Dictionary<string, string> Collect()
    {
        switch (Op.Id)
        {
            case "audio.bleep":
                if (_bleeps.Count == 0) throw new OrangeException("Drag across the waveform to mark a section to bleep.");
                return new()
                {
                    ["ranges"] = string.Join(", ", _bleeps.OrderBy(b => b.S).Select(b => $"{Editors.Inv(b.S)}-{Editors.Inv(b.E)}")),
                    ["sound"] = (string)_sound.SelectedItem, ["frequency"] = "1000",
                };
            case "audio.channels":
                return new() { ["channels"] = _stereo.IsChecked == true ? "stereo" : "mono" };
            default:
                if (_start <= 0.01 && _end >= _duration - 0.01) throw new OrangeException("Drag an end of the waveform to choose what to keep.");
                return new() { ["start"] = Editors.Inv(_start), ["end"] = _end >= _duration - 0.01 ? "" : Editors.Inv(_end) };
        }
    }

    protected override void OnClosedCleanup() => StopPlayback();
}

// ============================================================================ Metadata

public sealed class MetadataEditor : EditorWindow
{
    private readonly ListView _list = new() { SelectionMode = ListViewSelectionMode.None, MaxHeight = 260 };
    private readonly ComboBox _remove = new() { MinWidth = 200 };
    private readonly Dictionary<string, TextBox> _fields = new();
    private readonly TextBlock _effect = Label("", 12, 0.85);
    private IReadOnlyList<MetadataEntry> _entries = [];

    public MetadataEditor(OperationDescriptor op, string file) : base(op, file, "Metadata", 620, 660)
    {
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(new TextBlock { Text = System.IO.Path.GetFileName(file), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        panel.Children.Add(Label("What this file says about itself. Items marked ● can identify you, your device or a place.", 12, 0.75));
        panel.Children.Add(new Border { CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0x80, 0x80, 0x80)), Child = _list });

        var removeSpec = op.Options.First(o => o.Name == "remove");
        _remove.ItemsSource = removeSpec.Choices!.Select(Friendly).ToList();
        _remove.SelectedIndex = 0;
        _remove.SelectionChanged += (_, _) => Explain();
        AutomationProperties.SetName(_remove, "Remove");
        var rr = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        rr.Children.Add(Label("Remove", 13, 0.9)); rr.Children.Add(_remove);
        panel.Children.Add(rr);
        panel.Children.Add(_effect);

        var grid = new Grid { ColumnSpacing = 10, RowSpacing = 6 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        int r = 0;
        foreach (var spec in op.Options.Where(o => o.Name != "remove"))
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var l = Label(spec.Label, 13, 0.9); Grid.SetRow(l, r); grid.Children.Add(l);
            var tb = new TextBox { PlaceholderText = L.T("unchanged") }; AutomationProperties.SetName(tb, spec.Label);
            Grid.SetRow(tb, r); Grid.SetColumn(tb, 1); grid.Children.Add(tb);
            _fields[spec.Name] = tb; r++;
        }
        panel.Children.Add(Label("Set or change", 13, 0.9));
        panel.Children.Add(grid);
        Body.Children.Add(new ScrollViewer { Content = panel });
        ShowNote("Reading…");
        _ = LoadAsync();
    }

    private static string Friendly(string c) => c switch
    {
        "none" => "Nothing", "location" => "Location (GPS) only", "camera" => "Camera and device details", "all-but-color" => "Everything except colour profile", "all" => "Everything", _ => c,
    };

    private async Task LoadAsync()
    {
        try
        {
            _entries = await MetadataReader.ReadAsync(File);
            foreach (var e in _entries)
            {
                var row = new Grid { ColumnSpacing = 8 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                if (e.Sensitive) row.Children.Add(new TextBlock { Text = L.T("●"), Foreground = new SolidColorBrush(WheelControl.OrangeAccent), FontSize = 10, VerticalAlignment = VerticalAlignment.Center });
                var k = Label($"{e.Group} · {e.Key}", 12, 0.75); Grid.SetColumn(k, 1); row.Children.Add(k);
                var v = new TextBlock { Text = e.Value, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, IsTextSelectionEnabled = true }; Grid.SetColumn(v, 2); row.Children.Add(v);
                AutomationProperties.SetName(row, $"{(e.Sensitive ? "Sensitive. " : "")}{e.Key}: {e.Value}");
                _list.Items.Add(row);
            }
            foreach (var (name, tb) in _fields)
            {
                var match = _entries.FirstOrDefault(e => e.Key.Equals(name, StringComparison.OrdinalIgnoreCase)
                    || name == "description" && e.Key == "ImageDescription" || name == "date" && e.Key is "DateTimeOriginal" or "date");
                if (match != null) tb.PlaceholderText = match.Value;
            }
            ShowNote($"{_entries.Count} entries, {_entries.Count(e => e.Sensitive)} sensitive.");
            Explain();
        }
        catch (OrangeException e) { ShowError(e.Message); }
    }

    private string RemoveChoice => Op.Options.First(o => o.Name == "remove").Choices![Math.Max(0, _remove.SelectedIndex)];

    /// <summary>Spells out what the chosen removal clears and keeps, from the entries actually in this file.</summary>
    private void Explain()
    {
        var choice = RemoveChoice;
        int Count(Func<MetadataEntry, bool> f) => _entries.Count(f);
        _effect.Text = choice switch
        {
            "none" => "Nothing is removed. Only the fields you fill in below change.",
            "location" => $"Removes {Count(e => e.Group == "Location" || e.Key.Contains("location", StringComparison.OrdinalIgnoreCase))} location entries. Everything else is kept.",
            "camera" => $"Removes {Count(e => e.Key is "Make" or "Model" or "LensMake" or "LensModel" or "LensSerialNumber" or "SerialNumber" or "OwnerName" or "Software")} camera and device entries. Location and dates are kept.",
            "all-but-color" => "Removes all EXIF, IPTC and XMP data. Keeps the colour profile so colours look the same.",
            _ => Op.Category == FileKind.Image ? "Removes every tag, including the colour profile." : "Removes every tag and chapter. Audio and video streams are copied unchanged.",
        };
    }

    protected override Dictionary<string, string> Collect()
    {
        var d = _fields.ToDictionary(kv => kv.Key, kv => kv.Value.Text.Trim());
        d["remove"] = RemoveChoice;
        if (d["remove"] == "none" && d.Where(kv => kv.Key != "remove").All(kv => kv.Value.Length == 0))
            throw new OrangeException("Choose what to remove or fill in a field to change.");
        return d;
    }
}

// ============================================================================ Reorder PDF pages

public sealed class ReorderEditor : EditorWindow
{
    private readonly ObservableCollection<string> _pages = new();
    private readonly ListView _list = new() { CanReorderItems = true, AllowDrop = true, CanDragItems = true, SelectionMode = ListViewSelectionMode.Single, Height = 380 };

    public ReorderEditor(OperationDescriptor op, string file) : base(op, file, "Reorder Pages", 560, 560)
    {
        _list.ItemsSource = _pages;
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(Label("Drag pages into the new order, or use the buttons. Delete a page to leave it out.", 12, 0.75));
        panel.Children.Add(new Border { CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0x80, 0x80, 0x80)), Child = _list });
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        Button B(string text, Action a) { var b = new Button { Content = text }; b.Click += (_, _) => a(); row.Children.Add(b); return b; }
        B("Move up", () => Move(-1)); B("Move down", () => Move(1));
        B("Reverse", () => { var r = _pages.Reverse().ToList(); _pages.Clear(); foreach (var p in r) _pages.Add(p); });
        B("Delete", () => { if (_list.SelectedIndex >= 0 && _pages.Count > 1) _pages.RemoveAt(_list.SelectedIndex); });
        panel.Children.Add(row);
        Body.Children.Add(panel);
        _ = LoadAsync();
    }

    private void Move(int d)
    {
        int i = _list.SelectedIndex, j = i + d;
        if (i < 0 || j < 0 || j >= _pages.Count) return;
        _pages.Move(i, j); _list.SelectedIndex = j;
    }

    private async Task LoadAsync()
    {
        try
        {
            var items = await Task.Run(() =>
            {
                using var doc = UglyToad.PdfPig.PdfDocument.Open(File);
                return Enumerable.Range(1, doc.NumberOfPages).Select(n =>
                {
                    string text;
                    try { text = string.Join(' ', doc.GetPage(n).GetWords().Take(12).Select(w => w.Text)); } catch { text = ""; }
                    return $"Page {n} · {(text.Length == 0 ? "(no text: images or drawing)" : text)}";
                }).ToList();
            });
            foreach (var p in items) _pages.Add(p);
            ShowNote($"{items.Count} pages");
        }
        catch (Exception e) { ShowError("This PDF couldn't be read: " + e.Message); ApplyButton.IsEnabled = false; }
    }

    protected override Dictionary<string, string> Collect()
    {
        var order = _pages.Select(p => p.Split(' ', 3)[1]).ToList();
        if (order.Count == 0) throw new OrangeException("No pages to save.");
        return new() { ["order"] = string.Join(",", order) };
    }
}

// ============================================================================ QR result

/// <summary>Shows what a QR code says. The text is data: shown and copyable, never opened or followed.</summary>
public sealed class QrResultWindow : Window
{
    public static void Show(string file, string text)
    {
        var w = new QrResultWindow(file, text);
        w.Activate();
    }

    private QrResultWindow(string file, string text)
    {
        var panel = new StackPanel { Spacing = 12, Padding = new Thickness(20) };
        panel.Children.Add(new TextBlock { Text = L.T("QR code"), FontSize = 18, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = System.IO.Path.GetFileName(file), Opacity = 0.7, TextTrimming = TextTrimming.CharacterEllipsis });
        var box = new TextBox { Text = text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true, MaxHeight = 220, FontFamily = new FontFamily("Cascadia Mono,Consolas") };
        AutomationProperties.SetName(box, "Decoded QR content");
        panel.Children.Add(box);
        bool looksLikeLink = text.Contains("://", StringComparison.Ordinal) || text.StartsWith("www.", StringComparison.OrdinalIgnoreCase);
        panel.Children.Add(new TextBlock
        {
            Text = looksLikeLink ? "This is a web address. Orange never opens links from QR codes. Check where it goes before you paste it into a browser."
                                 : "Orange shows the text only. Nothing in it is opened or run.",
            TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.8,
        });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        var copy = new Button { Content = L.T("Copy"), Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        copy.Click += (_, _) =>
        {
            var p = new Windows.ApplicationModel.DataTransfer.DataPackage(); p.SetText(text);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(p); copy.Content = L.T("Copied");
        };
        var close = new Button { Content = L.T("Close") }; close.Click += (_, _) => Close();
        buttons.Children.Add(close); buttons.Children.Add(copy);
        panel.Children.Add(buttons);
        Content = panel;
        WindowHelpers.Compact(this, "Orange — QR code", topmost: true);
        WindowHelpers.Place(this, 460, 400, bottomRight: false);
    }
}
