using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;

namespace Orange.App.Views;

public sealed record WheelItem(string Title, string? Glyph, object Tag, string AccessibleName);

/// <summary>
/// The circular menu: equal wedges around a centre disc, first wedge at 12 o'clock, clockwise.
/// Drawn with XAML shapes so it scales crisply at any DPI and follows the light/dark/high-contrast theme.
/// </summary>
public sealed class WheelControl : Grid
{
    public static readonly Color OrangeAccent = Color.FromArgb(255, 0xFF, 0x7A, 0x00);
    private readonly Canvas _canvas = new();
    private readonly List<(Microsoft.UI.Xaml.Shapes.Path Shape, StackPanel Label, TextBlock Text, FontIcon? Icon)> _segments = new();
    private readonly Ellipse _centerDisc = new();
    private readonly Image _thumb = new() { Stretch = Stretch.UniformToFill };
    private readonly Border _thumbClip = new() { CornerRadius = new CornerRadius(14) };
    private readonly TextBlock _pill = new() { FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextAlignment = TextAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly Border _pillBorder = new() { CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 3, 10, 3) };
    private IReadOnlyList<WheelItem> _items = Array.Empty<WheelItem>();
    private string _idleCaption = "";
    private int _highlight = -1;

    public double Diameter { get; }
    public double InnerRadius => Diameter * 0.22;
    public double OuterRadius => Diameter * 0.5 - 4;
    public IReadOnlyList<WheelItem> Items => _items;
    public int Highlighted => _highlight;
    public event Action<int>? HighlightChanged;

    public WheelControl(double diameter)
    {
        Diameter = diameter;
        Width = Height = diameter;
        _canvas.Width = _canvas.Height = diameter;
        Children.Add(_canvas);
        ActualThemeChanged += (_, _) => Restyle();

        _centerDisc.Width = _centerDisc.Height = InnerRadius * 2 - 8;
        Canvas.SetLeft(_centerDisc, diameter / 2 - _centerDisc.Width / 2);
        Canvas.SetTop(_centerDisc, diameter / 2 - _centerDisc.Height / 2);

        var thumbSize = InnerRadius * 1.05;
        _thumbClip.Width = _thumbClip.Height = thumbSize;
        _thumbClip.Child = _thumb;
        Canvas.SetLeft(_thumbClip, diameter / 2 - thumbSize / 2);
        Canvas.SetTop(_thumbClip, diameter / 2 - thumbSize / 2 - 10);
        _pillBorder.Child = _pill;
        _pillBorder.MaxWidth = InnerRadius * 2.2;
        _pillBorder.Width = InnerRadius * 1.8;
        Canvas.SetLeft(_pillBorder, diameter / 2 - _pillBorder.Width / 2);
        Canvas.SetTop(_pillBorder, diameter / 2 + thumbSize / 2 - 14);
    }

    public void SetItems(IReadOnlyList<WheelItem> items, string idleCaption)
    {
        _items = items; _idleCaption = idleCaption; _highlight = -1;
        _canvas.Children.Clear(); _segments.Clear();
        _canvas.Children.Add(_centerDisc);
        int n = Math.Max(items.Count, 1);
        double step = 360.0 / n, gap = items.Count > 1 ? 2.2 : 0;
        for (int i = 0; i < items.Count; i++)
        {
            double a0 = -90 + i * step - step / 2 + gap / 2, a1 = a0 + step - gap;
            var shape = new Microsoft.UI.Xaml.Shapes.Path { Data = Sector(a0, a1, InnerRadius, OuterRadius), StrokeThickness = 0 };
            AutomationProperties.SetName(shape, items[i].AccessibleName);
            _canvas.Children.Add(shape);

            var label = new StackPanel { Spacing = 2, HorizontalAlignment = HorizontalAlignment.Center, IsHitTestVisible = false };
            FontIcon? icon = null;
            if (items[i].Glyph is { } g)
            {
                icon = new FontIcon { Glyph = g, FontSize = 18, FontFamily = new FontFamily("Segoe Fluent Icons,Segoe MDL2 Assets"), HorizontalAlignment = HorizontalAlignment.Center };
                label.Children.Add(icon);
            }
            var text = new TextBlock
            {
                Text = items[i].Title.ToUpperInvariant(), FontSize = items[i].Glyph is null ? 15 : 10.5, FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                CharacterSpacing = 40, TextAlignment = TextAlignment.Center, MaxWidth = (OuterRadius - InnerRadius) * 1.1, TextWrapping = TextWrapping.Wrap,
            };
            label.Children.Add(text);
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double mid = (a0 + a1) / 2 * Math.PI / 180, rr = (InnerRadius + OuterRadius) / 2 + 2;
            Canvas.SetLeft(label, Diameter / 2 + rr * Math.Cos(mid) - label.DesiredSize.Width / 2);
            Canvas.SetTop(label, Diameter / 2 + rr * Math.Sin(mid) - label.DesiredSize.Height / 2);
            _canvas.Children.Add(label);
            _segments.Add((shape, label, text, icon));
        }
        _canvas.Children.Add(_thumbClip);
        _canvas.Children.Add(_pillBorder);
        _pill.Text = idleCaption;
        Restyle();
    }

    public void SetThumbnail(ImageSource? src) { _thumb.Source = src; _thumbClip.Visibility = src is null ? Visibility.Collapsed : Visibility.Visible; }

    /// <summary>Index of the wedge under a point in control coordinates, or -1 (centre or outside).</summary>
    public int HitTest(Point p)
    {
        double dx = p.X - Diameter / 2, dy = p.Y - Diameter / 2, r = Math.Sqrt(dx * dx + dy * dy);
        if (_items.Count == 0 || r < InnerRadius || r > OuterRadius + 24) return -1;
        double angle = (Math.Atan2(dy, dx) * 180 / Math.PI + 90 + 360) % 360;   // 0 at 12 o'clock, clockwise
        double step = 360.0 / _items.Count;
        return (int)((angle + step / 2) % 360 / step);
    }

    public void SetHighlight(int i)
    {
        if (i == _highlight) return;
        _highlight = i;
        Restyle();
        _pill.Text = i >= 0 && i < _items.Count ? _items[i].Title : _idleCaption;
        HighlightChanged?.Invoke(i);
    }

    private void Restyle()
    {
        bool dark = ActualTheme == ElementTheme.Dark;
        bool hc = new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast;
        var wedge = new SolidColorBrush(hc ? (dark ? Colors.Black : Colors.White) : dark ? Color.FromArgb(0xD0, 0x2B, 0x2B, 0x2B) : Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF));
        var hot = new SolidColorBrush(hc ? (Color)Application.Current.Resources["SystemColorHighlightColor"] : OrangeAccent);
        var fg = new SolidColorBrush(dark || hc && dark ? Colors.White : Color.FromArgb(255, 0x1A, 0x1A, 0x1A));
        var hotFg = new SolidColorBrush(hc ? (Color)Application.Current.Resources["SystemColorHighlightTextColor"] : Colors.White);
        for (int i = 0; i < _segments.Count; i++)
        {
            var (shape, _, text, icon) = _segments[i];
            bool on = i == _highlight;
            shape.Fill = on ? hot : wedge;
            shape.Stroke = hc ? fg : null; shape.StrokeThickness = hc ? 1 : 0;
            text.Foreground = on ? hotFg : fg;
            if (icon != null) icon.Foreground = on ? hotFg : fg;
        }
        _centerDisc.Fill = new SolidColorBrush(dark ? Color.FromArgb(0xF0, 0x20, 0x20, 0x20) : Color.FromArgb(0xF5, 0xFA, 0xFA, 0xFA));
        _pillBorder.Background = new SolidColorBrush(_highlight >= 0 ? OrangeAccent : dark ? Color.FromArgb(0xFF, 0x3A, 0x3A, 0x3A) : Color.FromArgb(0xFF, 0xEC, 0xEC, 0xEC));
        _pill.Foreground = _highlight >= 0 ? new SolidColorBrush(Colors.White) : fg;
    }

    private Geometry Sector(double a0Deg, double a1Deg, double r0, double r1)
    {
        double cx = Diameter / 2, cy = Diameter / 2;
        Point P(double deg, double r) => new(cx + r * Math.Cos(deg * Math.PI / 180), cy + r * Math.Sin(deg * Math.PI / 180));
        bool large = a1Deg - a0Deg > 180;
        if (a1Deg - a0Deg >= 359.9)   // single item: a ring
        {
            var g = new GeometryGroup { FillRule = FillRule.EvenOdd };
            g.Children.Add(new EllipseGeometry { Center = new Point(cx, cy), RadiusX = r1, RadiusY = r1 });
            g.Children.Add(new EllipseGeometry { Center = new Point(cx, cy), RadiusX = r0, RadiusY = r0 });
            return g;
        }
        var fig = new PathFigure { StartPoint = P(a0Deg, r0), IsClosed = true };
        fig.Segments.Add(new LineSegment { Point = P(a0Deg, r1) });
        fig.Segments.Add(new ArcSegment { Point = P(a1Deg, r1), Size = new Size(r1, r1), IsLargeArc = large, SweepDirection = SweepDirection.Clockwise });
        fig.Segments.Add(new LineSegment { Point = P(a1Deg, r0) });
        fig.Segments.Add(new ArcSegment { Point = P(a0Deg, r0), Size = new Size(r0, r0), IsLargeArc = large, SweepDirection = SweepDirection.Counterclockwise });
        var geo = new PathGeometry();
        geo.Figures.Add(fig);
        return geo;
    }
}
