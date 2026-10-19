using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI;

namespace Orange.App.Views;

/// <summary>
/// Orange's colour scheme: one blue family used everywhere (wheel, panel headers, accent buttons, progress, waveform,
/// selection handles). Errors keep a red tint on purpose: it is a state, not a brand colour, and red/blue keeps them
/// distinguishable for colour-blind users. App.xaml maps the same blues onto WinUI's SystemAccentColor ramp.
/// Contrast: white text on <see cref="Accent"/> is 4.9:1 and on <see cref="AccentDeep"/> 8.0:1 (WCAG AA for body text).
/// </summary>
public static class Brand
{
    public static readonly Color Accent = Color.FromArgb(255, 0x0A, 0x64, 0xE0);      // primary blue (buttons, hot wedge)
    public static readonly Color AccentLight = Color.FromArgb(255, 0x3D, 0x9B, 0xFF); // gradient start / hover
    public static readonly Color AccentDeep = Color.FromArgb(255, 0x00, 0x3F, 0xA8);  // gradient end / pressed
    public static readonly Color Indigo = Color.FromArgb(255, 0x4F, 0x46, 0xE5);      // optional third stop
    public static readonly Color Heading = Color.FromArgb(255, 0x0A, 0x64, 0xE0);     // section headings in panels
    public static readonly Color PetalLight = Color.FromArgb(0xD9, 0xE3, 0xEF, 0xFF);
    public static readonly Color PetalDark = Color.FromArgb(0xC8, 0x1C, 0x26, 0x36);
    public static readonly Color InkLight = Color.FromArgb(255, 0x0B, 0x2A, 0x5A);    // wedge labels on light petals
    public static readonly Color InkDark = Color.FromArgb(255, 0xE6, 0xF0, 0xFF);
    public static readonly Color CentreLight = Color.FromArgb(0xE0, 0x2A, 0x3A, 0x52);
    public static readonly Color CentreDark = Color.FromArgb(0xE6, 0x16, 0x1E, 0x2B);
    public static readonly Color Error = Color.FromArgb(255, 0xC4, 0x2B, 0x1C);

    public static Color WithAlpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);

    /// <summary>The brand gradient (light blue → blue → deep blue, optionally into indigo) for headers and the hot wedge.</summary>
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
