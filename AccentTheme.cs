using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace clony;

/// <summary>
/// Single source of truth for the accent colour (same approach as Picky). Replacing the shared
/// brush resources updates every control that uses them via DynamicResource, live.
/// </summary>
internal static class AccentTheme
{
    public static readonly Color Default = Colors.White;

    public static Color Current { get; private set; } = Default;

    /// <summary>Readable foreground on the accent: near-black on light accents, white on dark ones.</summary>
    public static Color OnAccent { get; private set; } = Color.FromRgb(0x1C, 0x1C, 0x1C);

    public static event Action? Changed;

    /// <summary>Parses "#RRGGBB" or "RRGGBB"; returns false for anything else.</summary>
    public static bool TryParse(string? hex, out Color color)
    {
        color = Current;
        if (string.IsNullOrWhiteSpace(hex)) return false;
        hex = hex.Trim().TrimStart('#');
        if (hex.Length != 6 || !int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int rgb))
            return false;
        color = Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        return true;
    }

    public static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    public static void Apply(Color c)
    {
        Current = c;
        bool light = Luminance(c) > 0.5;
        OnAccent = light ? Color.FromRgb(0x1C, 0x1C, 0x1C) : Colors.White;

        SetBrush("AccentBrush", c);
        SetBrush("AccentPressedBrush", Lerp(c, light ? Colors.Black : Colors.White, light ? 0.15 : 0.12));
        SetBrush("AccentSoftBrush", Color.FromArgb(0x33, c.R, c.G, c.B));
        SetBrush("OnAccentBrush", OnAccent);
        Changed?.Invoke();
    }

    /// <summary>WCAG relative luminance (0 = black, 1 = white).</summary>
    private static double Luminance(Color c)
    {
        static double Channel(double v)
        {
            v /= 255.0;
            return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }

    private static void SetBrush(string key, Color c)
    {
        var brush = new SolidColorBrush(c);
        brush.Freeze();
        Application.Current.Resources[key] = brush;
    }

    private static Color Lerp(Color a, Color b, double t) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * t),
        (byte)(a.G + (b.G - a.G) * t),
        (byte)(a.B + (b.B - a.B) * t));
}
