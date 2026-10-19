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
        body.Children.Add(Step("1", L.T("Drag files"), L.T("Start dragging one or more files in File Explorer or on the desktop. Keep holding the mouse button.")));
        body.Children.Add(Step("2", $"While dragging, tap {Key(s.ConvertKey)}", "The conversion wheel opens around the pointer. Drop on a format; the new file appears next to the original."));
        body.Children.Add(Step("3", $"Or tap {Key(s.ToolsKey)} for tools, {Key(s.ActionsKey)} for actions", "Compress, edit, trim, bleep, metadata and more. No dragging? Select files and press " + s.Hotkey + "."));
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
        WindowHelpers.Place(this, 560, 600, bottomRight: false);
    }

    private static string Key(WheelKey k) => WheelKeyInterpreter.Label(k);

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
