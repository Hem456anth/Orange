using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Orange.App.Interop;
using Orange.App.Services;
using Orange.Core;
using Windows.Storage;

namespace Orange.App.Views;

internal static class WindowHelpers
{
    public static double Scale(Window w) => Native.GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(w)) / 96.0;

    /// <summary>Sizes a window in DIPs and puts it bottom-right of the work area (where Windows shows toasts) or centred.</summary>
    public static void Place(Window w, double widthDip, double heightDip, bool bottomRight)
    {
        var s = Scale(w);
        int cw = (int)(widthDip * s), ch = (int)(heightDip * s);
        var area = DisplayArea.GetFromWindowId(w.AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        int x = bottomRight ? area.X + area.Width - cw - (int)(16 * s) : area.X + (area.Width - cw) / 2;
        int y = bottomRight ? area.Y + area.Height - ch - (int)(16 * s) : area.Y + (area.Height - ch) / 2;
        w.AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(x, y, cw, ch));
    }

    public static void Compact(Window w, string title, bool topmost)
    {
        w.Title = title;
        var p = OverlappedPresenter.Create();
        p.IsMaximizable = false; p.IsMinimizable = false; p.IsResizable = false; p.IsAlwaysOnTop = topmost;
        w.AppWindow.SetPresenter(p);
        w.AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "orange.ico"));
        w.SystemBackdrop = new MicaBackdrop();
        if (w.Content is FrameworkElement fe) fe.RequestedTheme = App.ThemeFor(AppSettings.Current.Theme);
    }
}

/// <summary>The settings sheet shown before a tool runs (trim times, crop aspect…). Remembers the last values.</summary>
public sealed class OptionsWindow : Window
{
    private readonly TaskCompletionSource<IReadOnlyDictionary<string, string>?> _tcs = new();
    public static OptionsWindow? Last { get; private set; }
    private readonly Dictionary<string, Func<string>> _readers = new();

    public static Task<IReadOnlyDictionary<string, string>?> AskAsync(OperationDescriptor op, IReadOnlyList<string> files, IReadOnlyDictionary<string, string> remembered)
    {
        var w = new OptionsWindow(op, files, remembered);
        Last = w;
        w.Activate();
        return w._tcs.Task;
    }

    private OptionsWindow(OperationDescriptor op, IReadOnlyList<string> files, IReadOnlyDictionary<string, string> remembered)
    {
        var panel = new StackPanel { Spacing = 12, Padding = new Thickness(24) };
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        var thumb = new Image { Width = 56, Height = 56, Stretch = Stretch.UniformToFill };
        header.Children.Add(new Border { CornerRadius = new CornerRadius(8), Child = thumb, Background = new SolidColorBrush(Colors.Transparent) });
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(new TextBlock { Text = op.Title, Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"] });
        titles.Children.Add(new TextBlock { Text = files.Count == 1 ? Path.GetFileName(files[0]) : $"{files.Count} files", Opacity = 0.7, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 300 });
        var info = new TextBlock { Opacity = 0.7, FontSize = 12 };
        titles.Children.Add(info);
        header.Children.Add(titles);
        panel.Children.Add(header);
        _ = Preview(files, thumb, info);

        foreach (var spec in op.Options)
        {
            var value = remembered.TryGetValue(spec.Name, out var v) ? v : spec.Default;
            FrameworkElement editor;
            switch (spec.Type)
            {
                case OptionType.Choice:
                    var cb = new ComboBox { ItemsSource = spec.Choices, SelectedItem = value, MinWidth = 200 };
                    _readers[spec.Name] = () => cb.SelectedItem as string ?? spec.Default;
                    editor = cb; break;
                case OptionType.Integer or OptionType.Number:
                    var nb = new NumberBox
                    {
                        Value = double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 0,
                        Minimum = spec.Min ?? double.MinValue, Maximum = spec.Max ?? double.MaxValue, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
                        SmallChange = spec.Type == OptionType.Integer ? 1 : 0.25, MinWidth = 200,
                    };
                    _readers[spec.Name] = () => double.IsNaN(nb.Value) ? spec.Default : spec.Type == OptionType.Integer
                        ? ((int)Math.Round(nb.Value)).ToString(System.Globalization.CultureInfo.InvariantCulture)
                        : nb.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    editor = nb; break;
                case OptionType.Boolean:
                    var ts = new ToggleSwitch { IsOn = OptionValidator.ParseBool(value) };
                    _readers[spec.Name] = () => ts.IsOn ? "true" : "false";
                    editor = ts; break;
                default:
                    var tb = new TextBox { Text = value, PlaceholderText = spec.Type == OptionType.TimeSpan ? "mm:ss" : "", MinWidth = 200 };
                    _readers[spec.Name] = () => tb.Text.Trim();
                    editor = tb; break;
            }
            AutomationProperties.SetName(editor, spec.Label);
            var row = new StackPanel { Spacing = 4 };
            row.Children.Add(new TextBlock { Text = spec.Label });
            row.Children.Add(editor);
            if (spec.Help is { } h) row.Children.Add(new TextBlock { Text = h, FontSize = 12, Opacity = 0.65, TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(row);
        }

        var error = new InfoBar { Severity = InfoBarSeverity.Error, IsClosable = false, IsOpen = false };
        panel.Children.Add(error);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        var go = new Button { Content = op.Group == OperationGroup.Convert ? "Convert" : op.Title, Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        var cancel = new Button { Content = "Cancel" };
        go.Click += (_, _) =>
        {
            var result = _readers.ToDictionary(kv => kv.Key, kv => kv.Value());
            try
            {
                OptionValidator.Validate(new OperationRequest { Operation = op, Inputs = files, Options = result });
                _tcs.TrySetResult(result); Close();
            }
            catch (OrangeException ex) { error.Message = ex.Message; error.IsOpen = true; }
        };
        cancel.Click += (_, _) => { _tcs.TrySetResult(null); Close(); };
        buttons.Children.Add(cancel); buttons.Children.Add(go);
        panel.Children.Add(buttons);
        var root = new ScrollViewer { Content = panel };
        root.KeyDown += (_, e) => { if (e.Key == Windows.System.VirtualKey.Escape) { _tcs.TrySetResult(null); Close(); } };
        Content = root;
        Closed += (_, _) => _tcs.TrySetResult(null);
        WindowHelpers.Compact(this, $"Orange — {op.Title}", topmost: true);
        WindowHelpers.Place(this, 420, Math.Min(140 + op.Options.Count * 86 + 110, 700), bottomRight: false);
    }

    private static async Task Preview(IReadOnlyList<string> files, Image thumb, TextBlock info)
    {
        try
        {
            var f = await StorageFile.GetFileFromPathAsync(files[0]);
            using var t = await f.GetThumbnailAsync(Windows.Storage.FileProperties.ThumbnailMode.SingleItem, 96);
            var bmp = new BitmapImage(); await bmp.SetSourceAsync(t); thumb.Source = bmp;
            var kind = FileTypeDetector.Detect(files[0]);
            if (kind == FileKind.Image) { var p = await f.Properties.GetImagePropertiesAsync(); info.Text = $"{p.Width} × {p.Height}"; }
            else if (kind is FileKind.Video) { var p = await f.Properties.GetVideoPropertiesAsync(); info.Text = $"{p.Width} × {p.Height} · {p.Duration:mm\\:ss}"; }
            else if (kind is FileKind.Audio) { var p = await f.Properties.GetMusicPropertiesAsync(); info.Text = $"{p.Duration:mm\\:ss}"; }
        }
        catch { /* preview is a nicety */ }
    }
}

/// <summary>Small bottom-right window: "Converting to MP3 · 42%", then "1 of 1 saved" with Show in folder.</summary>
public sealed class ProgressWindow : Window
{
    private readonly TextBlock _title = new() { FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontSize = 15, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _status = new() { Opacity = 0.75, TextWrapping = TextWrapping.Wrap, MaxLines = 3 };
    private readonly SimpleProgressBar _bar = new() { IsIndeterminate = true };
    private readonly Button _primary = new() { Content = "Cancel" };
    private readonly Button _details = new() { Content = "Details", Visibility = Visibility.Collapsed };
    private IReadOnlyList<string> _outputs = Array.Empty<string>();
    private string _detailText = "";
    private bool _done;
    private readonly OperationDescriptor? _op;
    public event Action? CancelRequested;

    private ProgressWindow(string title, OperationDescriptor? op)
    {
        _op = op;
        _title.Text = title;
        AutomationProperties.SetLiveSetting(_status, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(_details); buttons.Children.Add(_primary);
        var panel = new StackPanel { Spacing = 10, Padding = new Thickness(20, 16, 20, 16) };
        panel.Children.Add(_title); panel.Children.Add(_bar); panel.Children.Add(_status); panel.Children.Add(buttons);
        Content = panel;
        _primary.Click += (_, _) =>
        {
            if (!_done) { CancelRequested?.Invoke(); _primary.IsEnabled = false; _status.Text = "Cancelling…"; }
            else { if (_outputs.Count > 0) Native.RevealInExplorer(_outputs); Close(); }
        };
        _details.Click += async (_, _) =>
        {
            var dlg = new ContentDialog { Title = "Details", Content = new ScrollViewer { Content = new TextBlock { Text = _detailText, IsTextSelectionEnabled = true, TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Cascadia Mono,Consolas") }, MaxHeight = 320 }, CloseButtonText = "Close", XamlRoot = Content.XamlRoot };
            await dlg.ShowAsync();
        };
        Diagnostics.Event("pw-compact");
        WindowHelpers.Compact(this, "Orange", topmost: true);
        Diagnostics.Event("pw-place");
        WindowHelpers.Place(this, 380, 170, bottomRight: true);
    }

    public static ProgressWindow Show(OperationDescriptor op, IReadOnlyList<Job> jobs)
    {
        Diagnostics.Event("pw-ctor");
        var w = new ProgressWindow(op.Group == OperationGroup.Convert ? $"Converting to {op.Title}" : op.Title, op);
        w._status.Text = jobs.Count == 1 ? jobs[0].DisplayName : $"0 of {jobs.Count} done";
        Diagnostics.Event("pw-activate");
        w.Activate();
        Diagnostics.Event("pw-shown");
        return w;
    }

    public void Update(BatchProgress p)
    {
        _bar.IsIndeterminate = p.Fraction <= 0.001;
        _bar.Value = p.Fraction;
        var pct = (int)(p.Fraction * 100);
        _title.Text = (_op?.Group == OperationGroup.Convert ? $"Converting to {_op.Title}" : _op?.Title ?? "Orange") + $" · {pct}%";
        if (p.Total > 1) _status.Text = $"{p.Done} of {p.Total} done";
    }

    public void Complete(IReadOnlyList<Job> jobs)
    {
        _done = true;
        _bar.IsIndeterminate = false; _bar.Value = 1;
        _outputs = jobs.Where(j => j.State == JobState.Succeeded).SelectMany(j => j.Result!.Outputs).ToList();
        int ok = jobs.Count(j => j.State == JobState.Succeeded), failed = jobs.Count(j => j.State == JobState.Failed);
        var cancelled = jobs.Count(j => j.State == JobState.Cancelled);
        _title.Text = failed == 0 && cancelled == 0 ? (ok == 1 ? $"Saved {Path.GetFileName(_outputs.FirstOrDefault() ?? "")}" : $"{ok} of {jobs.Count} saved") : $"{ok} of {jobs.Count} saved";
        if (failed > 0)
        {
            var first = jobs.First(j => j.State == JobState.Failed);
            _status.Text = first.Result?.Error ?? "Something went wrong.";
            _detailText = string.Join("\n\n", jobs.Where(j => j.State == JobState.Failed).Select(j => $"{j.DisplayName}\n{j.Result?.Error}\n{j.Result?.Detail}"));
            _details.Visibility = Visibility.Visible;
            _bar.ShowError = true;
        }
        else _status.Text = cancelled > 0 ? "Cancelled. Finished files were kept." : "Done on this PC. Nothing was uploaded.";
        _primary.IsEnabled = true;
        _primary.Content = _outputs.Count > 0 ? "Show in folder" : "Close";
        if (failed == 0 && cancelled == 0) _ = AutoClose();
    }

    private async Task AutoClose() { await Task.Delay(6000); try { Close(); } catch { } }

    public static void ShowError(string title, string message, string? detail)
    {
        var w = new ProgressWindow(title, null) { _done = true, _detailText = detail ?? "" };
        w._bar.Visibility = Visibility.Collapsed;
        w._status.Text = message;
        w._primary.Content = "Close";
        if (!string.IsNullOrEmpty(detail)) w._details.Visibility = Visibility.Visible;
        w.Activate();
    }

    public static void ShowInfo(string message)
    {
        var w = new ProgressWindow("Orange", null) { _done = true };
        w._bar.Visibility = Visibility.Collapsed;
        w._status.Text = message;
        w._primary.Content = "Close";
        w.Activate();
        _ = w.AutoClose();
    }
}

/// <summary>
/// A plain progress bar drawn with two rectangles. WinUI's ProgressBar crashed (0xC000027B in Microsoft.UI.Xaml.dll)
/// when rendered in this unpackaged, self-contained build on Windows Server 2025 in CI, so Orange draws its own.
/// </summary>
public sealed class SimpleProgressBar : Grid
{
    private readonly Border _fill = new() { HorizontalAlignment = HorizontalAlignment.Left, CornerRadius = new CornerRadius(2) };
    private double _value;
    private bool _indeterminate, _error;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private double _phase;

    public SimpleProgressBar()
    {
        Height = 4;
        CornerRadius = new CornerRadius(2);
        Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x33, 0x80, 0x80, 0x80));
        _fill.Background = new SolidColorBrush(WheelControl.OrangeAccent);
        Children.Add(_fill);
        SizeChanged += (_, _) => Layout();
        _timer.Tick += (_, _) => { _phase = (_phase + 0.025) % 1.3; Layout(); };
        Unloaded += (_, _) => _timer.Stop();
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(this, "Progress");
    }

    public double Value { get => _value; set { _value = double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0; Layout(); } }
    public bool IsIndeterminate { get => _indeterminate; set { _indeterminate = value; if (value) _timer.Start(); else _timer.Stop(); Layout(); } }
    public bool ShowError { get => _error; set { _error = value; _fill.Background = new SolidColorBrush(value ? Microsoft.UI.Colors.IndianRed : WheelControl.OrangeAccent); } }

    private void Layout()
    {
        double w = ActualWidth;
        if (w <= 0) return;
        if (_indeterminate)
        {
            _fill.Width = w * 0.3;
            _fill.Margin = new Thickness(Math.Max(0, (w * (_phase - 0.3))), 0, 0, 0);
        }
        else { _fill.Margin = new Thickness(0); _fill.Width = w * _value; }
    }
}
