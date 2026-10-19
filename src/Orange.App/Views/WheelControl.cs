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
/// The circular menu: rounded "petal" wedges around a glass centre disc, first wedge at 12 o'clock, clockwise.
/// Petals are sectors stroked in their own fill with round joins, which rounds every corner without custom geometry.
/// The hot wedge takes the orange→coral gradient; the centre shows the file's thumbnail with a size pill under it.
/// Drawn with XAML shapes so it scales crisply at any DPI and follows the light/dark/high-contrast theme.
/// </summary>
public sealed class WheelControl : Grid
{
    public static readonly Color OrangeAccent = Color.FromArgb(255, 0xFF, 0x7A, 0x00);
    public static readonly Color Coral = Color.FromArgb(255, 0xFF, 0x4E, 0x50);
    public static readonly Color Violet = Color.FromArgb(255, 0x9B, 0x5D, 0xE5);
    /// <summary>The brand gradient (orange → coral → purple) used on headers and the hot wedge.</summary>
    public static LinearGradientBrush BrandGradient(bool withViolet = false, double angle = 0)
    {
        var b = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = angle == 0 ? new Point(1, 1) : new Point(1, 0) };
        b.GradientStops.Add(new GradientStop { Color = Color.FromArgb(255, 0xFF, 0xA0, 0x1E), Offset = 0 });
        b.GradientStops.Add(new GradientStop { Color = OrangeAccent, Offset = withViolet ? 0.35 : 0.45 });
        b.GradientStops.Add(new GradientStop { Color = Coral, Offset = withViolet ? 0.7 : 1 });
        if (withViolet) b.GradientStops.Add(new GradientStop { Color = Violet, Offset = 1 });
        return b;
    }
    private readonly Ellipse _ring = new();
    private readonly Canvas _canvas = new();
    private readonly List<(Microsoft.UI.Xaml.Shapes.Path Shape, StackPanel Label, TextBlock Text, FontIcon? Icon)> _segments = new();
    private readonly Ellipse _centerDisc = new();
    private readonly Image _thumb = new() { Stretch = Stretch.UniformToFill };
    private readonly Border _thumbClip = new() { CornerRadius = new CornerRadius(14) };
    private readonly TextBlock _pill = new() { FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextAlignment = TextAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly Border _pillBorder = new() { CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 3, 10, 3) };
    private readonly TextBlock _name = new() { FontSize = 10.5, TextAlignment = TextAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Opacity = 0.9 };
    private readonly TextBlock _hint = new() { FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, LineHeight = 14 };
    private bool _hintOn;
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

        _ring.Width = _ring.Height = diameter - 2;
        Canvas.SetLeft(_ring, 1); Canvas.SetTop(_ring, 1);
        _centerDisc.Width = _centerDisc.Height = InnerRadius * 2 - 10;
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
        _hint.Width = thumbSize + 16;
        Canvas.SetLeft(_hint, diameter / 2 - _hint.Width / 2);
        Canvas.SetTop(_hint, diameter / 2 - thumbSize / 2 - 6);
        _hint.Visibility = Visibility.Collapsed;
        _name.Width = InnerRadius * 1.6;
        Canvas.SetLeft(_name, diameter / 2 - _name.Width / 2);
        Canvas.SetTop(_name, diameter / 2 + thumbSize / 2 + 12);
    }

    public void SetItems(IReadOnlyList<WheelItem> items, string idleCaption)
    {
        _items = items; _idleCaption = idleCaption; _highlight = -1;
        _canvas.Children.Clear(); _segments.Clear();
        _canvas.Children.Add(_ring);
        _canvas.Children.Add(_centerDisc);
        int n = Math.Max(items.Count, 1);
        double step = 360.0 / n;
        // Petal inset: half the round stroke on each side, plus a visible gap between petals.
        const double round = 12;
        double r0 = InnerRadius + round / 2 + 3, r1 = OuterRadius - round / 2 - 4;
        double gapDeg(double r) => (round + 6) / r * 180 / Math.PI;
        for (int i = 0; i < items.Count; i++)
        {
            double a0 = -90 + i * step - step / 2, a1 = a0 + step;
            // Angular gap is wider at the inner edge so the visible gap is even along the petal.
            var geo = items.Count > 1 ? Petal(a0, a1, r0, r1, gapDeg(r0) / 2, gapDeg(r1) / 2) : Sector(a0, a1, r0, r1);
            var shape = new Microsoft.UI.Xaml.Shapes.Path { Data = geo, StrokeThickness = items.Count > 1 ? round : 0, StrokeLineJoin = PenLineJoin.Round };
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
                Text = items[i].Title.ToUpperInvariant(), FontSize = items[i].Glyph is null ? (items[i].Title.Length > 5 ? 13 : 16) : 10.5, FontWeight = Microsoft.UI.Text.FontWeights.Bold,
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
        _canvas.Children.Add(_hint);
        _canvas.Children.Add(_pillBorder);
        _canvas.Children.Add(_name);
        _pill.Text = idleCaption;
        Restyle();
    }

    /// <summary>The file name under the size pill (Tangerine shows the dragged file's name with the wheel).</summary>
    public void SetFileName(string name) { _name.Text = name; AutomationProperties.SetName(this, string.IsNullOrEmpty(name) ? "Orange wheel" : $"Orange wheel for {name}"); }

    public void SetThumbnail(ImageSource? src) { _thumb.Source = src; _thumbClip.Visibility = src is null || _hintOn ? Visibility.Collapsed : Visibility.Visible; }

    /// <summary>Key reminder shown in the hub, in place of the thumbnail, for the first few drags. Null hides it.</summary>
    public void SetHint(string? text)
    {
        _hintOn = !string.IsNullOrEmpty(text);
        _hint.Text = text ?? "";
        _hint.Visibility = _hintOn ? Visibility.Visible : Visibility.Collapsed;
        _thumbClip.Visibility = _hintOn || _thumb.Source is null ? Visibility.Collapsed : Visibility.Visible;
        AutomationProperties.SetHelpText(this, text ?? "");
    }

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
        // Warm, translucent glass petals (light) / smoked glass (dark); solid system colours in high contrast.
        var petal = hc ? (Brush)new SolidColorBrush(dark ? Colors.Black : Colors.White)
                       : new SolidColorBrush(dark ? Color.FromArgb(0xC8, 0x3A, 0x2E, 0x2A) : Color.FromArgb(0xD9, 0xFF, 0xE9, 0xDC));
        Brush hot = hc ? new SolidColorBrush(Res("SystemColorHighlightColor", Colors.Blue)) : BrandGradient();
        var fg = new SolidColorBrush(hc ? (dark ? Colors.White : Colors.Black) : dark ? Color.FromArgb(255, 0xFF, 0xEE, 0xE4) : Color.FromArgb(255, 0x5A, 0x2A, 0x12));
        var hotFg = new SolidColorBrush(hc ? Res("SystemColorHighlightTextColor", Colors.White) : Colors.White);
        for (int i = 0; i < _segments.Count; i++)
        {
            var (shape, label, text, icon) = _segments[i];
            bool on = i == _highlight;
            shape.Fill = on ? hot : petal;
            if (shape.StrokeThickness > 2 || !hc) shape.Stroke = on ? hot : petal;
            if (hc) { shape.Stroke = on ? hot : fg; }
            text.Foreground = on ? hotFg : fg;
            if (icon != null) icon.Foreground = on ? hotFg : fg;
            label.RenderTransformOrigin = new Point(0.5, 0.5);
            label.RenderTransform = new ScaleTransform { ScaleX = on ? 1.08 : 1, ScaleY = on ? 1.08 : 1 };
        }
        _ring.Fill = new SolidColorBrush(hc ? Colors.Transparent : dark ? Color.FromArgb(0x60, 0x20, 0x18, 0x16) : Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF));
        _ring.Stroke = new SolidColorBrush(hc ? fg.Color : dark ? Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x90, 0xFF, 0xFF, 0xFF));
        _ring.StrokeThickness = 1.5;
        _centerDisc.Fill = new SolidColorBrush(hc ? (dark ? Colors.Black : Colors.White) : dark ? Color.FromArgb(0xE6, 0x2A, 0x22, 0x20) : Color.FromArgb(0xE0, 0x5A, 0x4A, 0x44));
        _centerDisc.Stroke = new SolidColorBrush(dark ? Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x70, 0xFF, 0xFF, 0xFF));
        _centerDisc.StrokeThickness = 1;
        _pillBorder.Background = _highlight >= 0 && !hc ? BrandGradient() : new SolidColorBrush(hc ? Res("SystemColorHighlightColor", Colors.Blue) : Color.FromArgb(0xD0, 0x3A, 0x30, 0x2C));
        _pill.Foreground = new SolidColorBrush(Colors.White);
        _name.Foreground = new SolidColorBrush(hc ? fg.Color : Colors.White);
        _hint.Foreground = new SolidColorBrush(hc ? fg.Color : Colors.White);
    }

    /// <summary>A sector whose sides are pulled in by different angles at the inner and outer radius (even gap width).</summary>
    private Geometry Petal(double a0Deg, double a1Deg, double r0, double r1, double insetInner, double insetOuter)
    {
        double cx = Diameter / 2, cy = Diameter / 2;
        Point P(double deg, double r) => new(cx + r * Math.Cos(deg * Math.PI / 180), cy + r * Math.Sin(deg * Math.PI / 180));
        double i0 = a0Deg + insetInner, i1 = a1Deg - insetInner, o0 = a0Deg + insetOuter, o1 = a1Deg - insetOuter;
        var fig = new PathFigure { StartPoint = P(i0, r0), IsClosed = true };
        fig.Segments.Add(new LineSegment { Point = P(o0, r1) });
        fig.Segments.Add(new ArcSegment { Point = P(o1, r1), Size = new Size(r1, r1), IsLargeArc = o1 - o0 > 180, SweepDirection = SweepDirection.Clockwise });
        fig.Segments.Add(new LineSegment { Point = P(i1, r0) });
        fig.Segments.Add(new ArcSegment { Point = P(i0, r0), Size = new Size(r0, r0), IsLargeArc = i1 - i0 > 180, SweepDirection = SweepDirection.Counterclockwise });
        var geo = new PathGeometry();
        geo.Figures.Add(fig);
        return geo;
    }

    /// <summary>Opening animation: a quick scale-and-fade from the pointer (skipped when Windows animations are off).</summary>
    public void AnimateIn()
    {
        try
        {
            if (!new Windows.UI.ViewManagement.UISettings().AnimationsEnabled) return;
            var visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(this);
            var c = visual.Compositor;
            visual.CenterPoint = new System.Numerics.Vector3((float)Diameter / 2, (float)Diameter / 2, 0);
            var ease = c.CreateCubicBezierEasingFunction(new System.Numerics.Vector2(0.2f, 0.9f), new System.Numerics.Vector2(0.3f, 1f));
            var scale = c.CreateVector3KeyFrameAnimation();
            scale.InsertKeyFrame(0, new System.Numerics.Vector3(0.82f, 0.82f, 1)); scale.InsertKeyFrame(1, System.Numerics.Vector3.One, ease);
            scale.Duration = TimeSpan.FromMilliseconds(170);
            var fade = c.CreateScalarKeyFrameAnimation();
            fade.InsertKeyFrame(0, 0); fade.InsertKeyFrame(1, 1, ease); fade.Duration = TimeSpan.FromMilliseconds(140);
            visual.StartAnimation("Scale", scale); visual.StartAnimation("Opacity", fade);
        }
        catch { /* composition unavailable: show without animation */ }
    }

    private static Color Res(string key, Color fallback)
    {
        try { return Application.Current.Resources.TryGetValue(key, out var v) && v is Color c ? c : fallback; } catch { return fallback; }
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
