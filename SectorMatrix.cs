using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace clony;

/// <summary>
/// Draws a ScanMap as a grid of square cells, left to right and top to bottom from the start of the disk.
/// Cell size adapts to the space available so the whole disk is always visible at once.
/// </summary>
public sealed class SectorMatrix : FrameworkElement
{
    private const double Gap = 2;
    private static readonly Brush VerySlowBrush = Frozen(Color.FromRgb(0xF0, 0x80, 0x3C));

    private ScanMap? _map;
    private double _cell;
    private int _cols;
    private int _hover = -1;

    /// <summary>Raised with the cell under the mouse, or -1 when the mouse leaves.</summary>
    public event Action<int>? CellHovered;

    public ScanMap? Map
    {
        get => _map;
        set { _map = value; InvalidateVisual(); }
    }

    public void Refresh() => InvalidateVisual();

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h)); // whole area hit-testable
        Fit(w, h);
        if (_cols == 0) return;

        var brushes = new[]
        {
            Res("SurfaceAltBrush"), Res("OkBrush"), Res("WarnBrush"), VerySlowBrush, Res("DangerBrush"),
        };
        var current = new Pen(Res("AccentBrush"), 2);
        var hover = new Pen(Res("TextBrush"), 1.5);
        double radius = _cell >= 8 ? 2 : 0;

        for (int i = 0; i < ScanMap.CellCount; i++)
        {
            var state = _map?.States[i] ?? CellState.Unscanned;
            var r = CellRect(i);
            Pen? pen = i == _hover ? hover : _map != null && i == _map.CurrentCell ? current : null;
            dc.DrawRoundedRectangle(brushes[(int)state], pen, r, radius, radius);
        }
    }

    /// <summary>Largest square cell that fits all cells in the available area.</summary>
    private void Fit(double w, double h)
    {
        _cols = 0;
        if (w <= Gap || h <= Gap) return;
        for (double size = Math.Floor(Math.Sqrt(w * h / ScanMap.CellCount)); size >= 2; size--)
        {
            int cols = (int)((w + Gap) / (size + Gap));
            if (cols <= 0) continue;
            int rows = (ScanMap.CellCount + cols - 1) / cols;
            if (rows * (size + Gap) - Gap <= h) { _cell = size; _cols = cols; return; }
        }
        _cell = 2;
        _cols = Math.Max(1, (int)((w + Gap) / (2 + Gap)));
    }

    private Rect CellRect(int i) =>
        new(i % _cols * (_cell + Gap), i / _cols * (_cell + Gap), _cell, _cell);

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int cell = -1;
        if (_cols > 0)
        {
            var p = e.GetPosition(this);
            int col = (int)(p.X / (_cell + Gap)), row = (int)(p.Y / (_cell + Gap));
            int i = row * _cols + col;
            if (col < _cols && i >= 0 && i < ScanMap.CellCount && CellRect(i).Contains(p)) cell = i;
        }
        SetHover(cell);
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        SetHover(-1);
    }

    private void SetHover(int cell)
    {
        if (cell == _hover) return;
        _hover = cell;
        InvalidateVisual();
        CellHovered?.Invoke(cell);
    }

    private Brush Res(string key) => TryFindResource(key) as Brush ?? Brushes.Gray;

    private static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}
