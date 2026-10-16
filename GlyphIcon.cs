using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Shapes;

namespace clony;

/// <summary>
/// Renders a Unicode symbol (e.g. ⟳) as a vector outline trimmed to its ink bounds, so it sits
/// exactly centred in a square icon button (a plain TextBlock carries the font's ascent/descent
/// padding and looks low). Same approach as Wally. Fill follows the host button's Foreground.
/// </summary>
internal static class GlyphIcon
{
    private static readonly FontFamily SymbolFont = new("Segoe UI Symbol, Segoe UI");

    public static Path Create(string glyph, double size = 16)
    {
        var text = new FormattedText(
            glyph,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface(SymbolFont, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
            size,
            Brushes.White,
            pixelsPerDip: 1.0);

        var geometry = text.BuildGeometry(new Point(0, 0));
        var bounds = geometry.Bounds;
        if (!bounds.IsEmpty)
            geometry.Transform = new TranslateTransform(-bounds.X, -bounds.Y);
        geometry.Freeze();

        var path = new Path
        {
            Data = geometry,
            Stretch = Stretch.None,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            SnapsToDevicePixels = true,
        };
        path.SetBinding(Shape.FillProperty, new Binding(nameof(Control.Foreground))
        {
            RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(Button), 1),
        });
        return path;
    }
}
