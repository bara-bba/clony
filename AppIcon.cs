using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace clony;

/// <summary>
/// Draws the app glyph — an accent rounded square with an "=" (identical copy) in the middle — at any
/// size, and assembles a multi-resolution .ico. Same geometry and frame sizes as Picky's icon.
/// Small sizes are pixel-snapped so the bars stay crisp at 16 px.
/// </summary>
internal static class AppIcon
{
    public static readonly int[] IcoSizes = { 16, 24, 32, 48, 256 };

    public static BitmapSource Render(int size, Color accent, Color glyph)
    {
        double s = size;
        double pad = s * 0.055;
        double radius = s * 0.24;

        // "=" geometry: two solid rectangles (square corners), snapped to whole pixels
        double barH = Math.Max(2, Math.Round(s * 0.11));
        double gap = Math.Max(2, Math.Round(s * 0.10));
        double inset = Math.Round(s * 0.27);
        double barW = s - 2 * inset;
        double top = Math.Round((s - (2 * barH + gap)) / 2);
        const double barRadius = 0;

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRoundedRectangle(Frozen(accent), null, new Rect(pad, pad, s - 2 * pad, s - 2 * pad), radius, radius);
            var bar = Frozen(glyph);
            dc.DrawRoundedRectangle(bar, null, new Rect(inset, top, barW, barH), barRadius, barRadius);
            dc.DrawRoundedRectangle(bar, null, new Rect(inset, top + barH + gap, barW, barH), barRadius, barRadius);
        }

        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>Window / header image in the current accent.</summary>
    public static BitmapSource CreateImageSource(Color accent, Color glyph, int size = 64) => Render(size, accent, glyph);

    /// <summary>PNG-framed .ico (Vista+ format) with all standard sizes.</summary>
    public static byte[] BuildIco(Color accent, Color glyph)
    {
        var frames = new List<(int Size, byte[] Png)>();
        foreach (int size in IcoSizes) frames.Add((size, ToPng(Render(size, accent, glyph))));

        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write((short)0);              // reserved
        w.Write((short)1);              // type = icon
        w.Write((short)frames.Count);

        int offset = 6 + 16 * frames.Count;
        foreach (var (size, png) in frames)
        {
            w.Write((byte)(size >= 256 ? 0 : size)); // width  (0 = 256)
            w.Write((byte)(size >= 256 ? 0 : size)); // height
            w.Write((byte)0);           // palette
            w.Write((byte)0);           // reserved
            w.Write((short)1);          // colour planes
            w.Write((short)32);         // bits per pixel
            w.Write(png.Length);
            w.Write(offset);
            offset += png.Length;
        }
        foreach (var (_, png) in frames) w.Write(png);
        w.Flush();
        return ms.ToArray();
    }

    public static byte[] ToPng(BitmapSource image)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    private static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}
