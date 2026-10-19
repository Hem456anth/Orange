using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI;

namespace Orange.App.Views;

/// <summary>
/// Orange's colour scheme in one place for newer views (the wheel keeps its own copies in WheelControl):
/// orange → coral, optionally into violet. App.xaml maps the orange ramp onto WinUI's SystemAccentColor.
/// </summary>
public static class Brand
{
    public static readonly Color Accent = Color.FromArgb(255, 0xFF, 0x7A, 0x00);      // primary orange (buttons, hot wedge)
    public static readonly Color AccentLight = Color.FromArgb(255, 0xFF, 0xA0, 0x1E); // gradient start / hover
    public static readonly Color AccentDeep = Color.FromArgb(255, 0xFF, 0x4E, 0x50);  // coral, gradient end
    public static readonly Color Indigo = Color.FromArgb(255, 0x9B, 0x5D, 0xE5);      // violet, optional third stop
    public static readonly Color Heading = Color.FromArgb(255, 0xFF, 0x4E, 0x50);     // section headings in panels
    public static readonly Color PetalLight = Color.FromArgb(0xD9, 0xFF, 0xE9, 0xDC);
    public static readonly Color PetalDark = Color.FromArgb(0xC8, 0x3A, 0x2E, 0x2A);
    public static readonly Color InkLight = Color.FromArgb(255, 0x5A, 0x2A, 0x12);    // wedge labels on light petals
    public static readonly Color InkDark = Color.FromArgb(255, 0xFF, 0xEE, 0xE4);
    public static readonly Color CentreLight = Color.FromArgb(0xE0, 0x5A, 0x4A, 0x44);
    public static readonly Color CentreDark = Color.FromArgb(0xE6, 0x2A, 0x22, 0x20);
    public static readonly Color Error = Color.FromArgb(255, 0xC4, 0x2B, 0x1C);

    public static Color WithAlpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);

    /// <summary>The brand gradient (amber → orange → coral, optionally into violet) for headers and the hot wedge.</summary>
    public static LinearGradientBrush Gradient(bool withIndigo = false, bool horizontal = false)
    {
        var b = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = horizontal ? new Point(1, 0) : new Point(1, 1) };
        b.GradientStops.Add(new GradientStop { Color = AccentLight, Offset = 0 });
        b.GradientStops.Add(new GradientStop { Color = Accent, Offset = withIndigo ? 0.4 : 0.5 });
        b.GradientStops.Add(new GradientStop { Color = AccentDeep, Offset = withIndigo ? 0.75 : 1 });
        if (withIndigo) b.GradientStops.Add(new GradientStop { Color = Indigo, Offset = 1 });
        return b;
    }
}
