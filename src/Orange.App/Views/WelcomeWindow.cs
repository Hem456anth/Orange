using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Orange.App.Services;
using Orange.App.Interop;

namespace Orange.App.Views;

/// <summary>
/// First-run window: the two-step gesture (drag, then tap a key), the keys for each wheel, and the start-at-sign-in
/// choice. Mirrors the shape of Tangerine's onboarding (steps + launch-at-login), in Orange's own words and design.
/// </summary>
public sealed class WelcomeWindow : Window
{
    public static WelcomeWindow? Last { get; private set; }

    public static void ShowIfFirstRun()
    {
        if (AppSettings.Current.WelcomeShown) return;
        Show();
    }

    public static void Show() { var w = new WelcomeWindow(); Last = w; w.Activate(); }

    private WelcomeWindow()
    {
        var s = AppSettings.Current;
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(120) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var hero = new Grid { Background = Brand.Gradient(withIndigo: true) };
        var heroText = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(28, 0, 28, 0), Spacing = 4 };
        heroText.Children.Add(new TextBlock { Text = L.T("Welcome to Orange"), FontSize = 26, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = new SolidColorBrush(Colors.White) });
        heroText.Children.Add(new TextBlock { Text = L.T("Convert and edit files right where they are. Everything stays on this PC."), Foreground = new SolidColorBrush(Colors.White), Opacity = 0.9, TextWrapping = TextWrapping.Wrap });
        hero.Children.Add(heroText);
        root.Children.Add(hero);

        var body = new StackPanel { Spacing = 14, Padding = new Thickness(28, 20, 28, 20) };
        var cards = new Grid { ColumnSpacing = 10 };
        for (int c = 0; c < 3; c++) cards.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        (string Glyph, string Title, string Text)[] steps =
        [
            ("\uE7C9", L.T("Drag files"), L.T("Start dragging one or more files in File Explorer or on the desktop. Keep holding the mouse button.")),
            ("\uE7FC", L.F("Tap {0}", Key(s.ConvertKey)), L.T("The conversion wheel opens around the pointer. Drop on a format; the new file appears next to the original.")),
            ("\uE90F", L.F("{0} or {1}", Key(s.ToolsKey), Key(s.ActionsKey)), L.F("Tools and actions wheels. No dragging? Select files and press {0}, or right-click → Orange.", s.Hotkey)),
        ];
        for (int c = 0; c < 3; c++) { var card = Card(c + 1, steps[c].Glyph, steps[c].Title, steps[c].Text); Grid.SetColumn(card, c); cards.Children.Add(card); }
        body.Children.Add(cards);
        body.Children.Add(PracticeZone(s));
        var autostart = new ToggleSwitch { Header = L.T("Start Orange when I sign in"), IsOn = ExplorerIntegration.AutostartInstalled || s.StartWithWindows };
        AutomationProperties.SetName(autostart, "Start Orange when I sign in");
        body.Children.Add(autostart);
        body.Children.Add(new TextBlock { Text = L.T("You can change keys, shortcuts and folders any time in Settings."), FontSize = 12, Opacity = 0.7 });
        var go = new Button { Content = L.T("Get started"), HorizontalAlignment = HorizontalAlignment.Right, Style = (Style)Application.Current.Resources["AccentButtonStyle"], MinWidth = 120 };
        go.Click += (_, _) =>
        {
            try { ExplorerIntegration.SetAutostart(autostart.IsOn); } catch (Exception e) { Diagnostics.Event("autostart-failed " + e.Message); }
            s.StartWithWindows = autostart.IsOn; s.WelcomeShown = true; s.Save();
            Close();
        };
        body.Children.Add(go);
        Grid.SetRow(body, 1);
        var scroll = new ScrollViewer { Content = body }; Grid.SetRow(scroll, 1); root.Children.Add(scroll);
        Content = root;
        Closed += (_, _) => { if (!s.WelcomeShown) { s.WelcomeShown = true; s.Save(); } };
        WindowHelpers.Compact(this, "Welcome to Orange", topmost: false);
        WindowHelpers.Place(this, 720, 640, bottomRight: false);
    }

    private static string Key(WheelKey k) => WheelKeyInterpreter.Label(k);

    private static UIElement Card(int n, string glyph, string title, string text)
    {
        var sp = new StackPanel { Spacing = 6 };
        sp.Children.Add(new FontIcon { Glyph = glyph, FontSize = 26, Foreground = new SolidColorBrush(Brand.Accent), HorizontalAlignment = HorizontalAlignment.Left });
        sp.Children.Add(new TextBlock { Text = $"{n}. {title}", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontSize = 15, TextWrapping = TextWrapping.Wrap });
        sp.Children.Add(new TextBlock { Text = text, Opacity = 0.75, TextWrapping = TextWrapping.Wrap, FontSize = 13 });
        var b = new Border { Padding = new Thickness(14), CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), Child = sp,
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"], Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"] };
        AutomationProperties.SetName(b, $"Step {n}: {title}. {text}");
        return b;
    }

    /// <summary>
    /// Practice: a sample picture the user can drag (from here or from the folder it lives in) and a drop zone that
    /// confirms the drag reached Orange. Real conversions still happen through the wheel.
    /// </summary>
    private UIElement PracticeZone(AppSettings s)
    {
        var dir = Path.Combine(Path.GetTempPath(), "Orange practice");
        var sample = Path.Combine(dir, "try-me.png");
        try { Directory.CreateDirectory(dir); if (!File.Exists(sample)) File.Copy(Path.Combine(AppContext.BaseDirectory, "Assets", "orange.png"), sample); } catch { }
        var zone = new Border { Height = 120, CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(2), AllowDrop = true,
            BorderBrush = new SolidColorBrush(Brand.Accent), Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x18, 0xFF, 0x7A, 0x00)) };
        var msg = new TextBlock { Text = L.T("Practice: drag try-me.png from its folder, tap Shift and drop on WebP. Or drop any file here to check that dragging reaches Orange."),
            TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16) };
        zone.Child = msg;
        AutomationProperties.SetName(zone, L.T("Practice drop zone"));
        zone.DragOver += (_, e) => { e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy; };
        zone.Drop += async (_, e) =>
        {
            if (!e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems)) return;
            var items = await e.DataView.GetStorageItemsAsync();
            msg.Text = L.F("Orange received {0}. Now try it anywhere: drag a file, tap {1}, drop on a format.", items.FirstOrDefault()?.Name ?? "", Key(s.ConvertKey));
            Diagnostics.Event("practice-drop " + items.Count);
        };
        var open = new HyperlinkButton { Content = L.T("Open the folder with try-me.png") };
        open.Click += (_, _) => Native.RevealInExplorer([sample]);
        var sp = new StackPanel { Spacing = 6 };
        sp.Children.Add(zone); sp.Children.Add(open);
        return sp;
    }

    private static UIElement Step(string n, string title, string text)
    {
        var g = new Grid { ColumnSpacing = 14 };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var badge = new Border { Width = 32, Height = 32, CornerRadius = new CornerRadius(16), Background = new SolidColorBrush(Brand.Accent), VerticalAlignment = VerticalAlignment.Top,
            Child = new TextBlock { Text = n, Foreground = new SolidColorBrush(Colors.White), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold } };
        g.Children.Add(badge);
        var t = new StackPanel { Spacing = 2 };
        t.Children.Add(new TextBlock { Text = title, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontSize = 15 });
        t.Children.Add(new TextBlock { Text = text, Opacity = 0.75, TextWrapping = TextWrapping.Wrap });
        Grid.SetColumn(t, 1); g.Children.Add(t);
        return g;
    }
}
