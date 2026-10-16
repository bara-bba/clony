using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;

namespace clony;

public partial class MainWindow
{
    private static readonly (string Brush, string Label)[] LegendItems =
    {
        ("SurfaceAltBrush", "Not scanned yet"),
        ("OkBrush", "Read OK"),
        ("WarnBrush", "Slow"),
        ("#F0803C", "Very slow (4x the slow limit, min 1 s)"),
        ("DangerBrush", "Unreadable"),
    };

    private readonly DispatcherTimer _matrixTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private ScanResult? _lastScan;
    private string _lastScanDisk = "";

    private void InitScanPage()
    {
        _matrixTimer.Tick += (_, _) => { Matrix.Refresh(); ShowCellInfo(_hoverCell); };
        Matrix.CellHovered += cell => { _hoverCell = cell; ShowCellInfo(cell); };
        foreach (var (brush, label) in LegendItems)
        {
            var swatch = new Rectangle { Width = 12, Height = 12, RadiusX = 2, RadiusY = 2, Margin = new Thickness(0, 0, 8, 0) };
            if (brush.StartsWith('#')) swatch.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(brush));
            else swatch.SetResourceReference(Shape.FillProperty, brush);
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
            row.Children.Add(swatch);
            row.Children.Add(new TextBlock { Text = label, FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
            Legend.Children.Add(row);
        }
    }

    private int _hoverCell = -1;

    private void ShowCellInfo(int cell)
    {
        var map = Matrix.Map;
        if (cell < 0 || map == null)
        {
            CellInfo.Text = map == null ? "Point at a square to see where it is on the disk." : "Point at a square for details.";
            return;
        }
        long start = map.CellStart(cell), end = map.CellEnd(cell);
        string state = map.States[cell] switch
        {
            CellState.Unscanned => "not scanned yet",
            CellState.Fast => "read OK",
            CellState.Slow => "slow",
            CellState.VerySlow => "very slow",
            _ => $"UNREADABLE ({map.UnreadablePieces[cell]} x 512 KB failed)",
        };
        CellInfo.Text =
            $"Square {cell + 1:N0} of {ScanMap.CellCount:N0}\n" +
            $"{FormatGb(start)} - {FormatGb(end)}\n" +
            $"Bytes {start:N0} - {end:N0}\n" +
            $"{map.LocationAt(start)}\n" +
            $"Result: {state}" + (map.WorstMs[cell] > 0 ? $"\nSlowest read: {map.WorstMs[cell]:N0} ms" : "");
    }

    private const double GiB = 1024.0 * 1024 * 1024;

    private static string FormatGb(long bytes) => $"{bytes / GiB:N2} GB";

    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        if (ScanDiskCombo.SelectedItem is not PhysicalDisk disk)
        {
            MessageBox.Show(this, "Select a disk to scan.", "clony", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!double.TryParse(ScanStartBox.Text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double startGb)
            || startGb < 0 || startGb * GiB >= disk.SizeBytes)
        {
            MessageBox.Show(this, $"Start must be a number of GB between 0 and {Math.Floor(disk.SizeBytes / GiB):N0}.", "clony",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        int slowMs = int.Parse((string)((ComboBoxItem)ScanSlowCombo.SelectedItem).Tag, CultureInfo.InvariantCulture);
        long startByte = (long)(startGb * GiB);

        SetBusy(true);
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _lastScanDisk = disk.Caption;
        FindingList.ItemsSource = null;
        ScanExportButton.IsEnabled = false;
        ScanTitle.Foreground = (Brush)FindResource("TextBrush");
        ScanTitle.Text = "Scanning...";
        ScanText.Text = $"Disk {disk.Index} ({disk.Model}) from {startGb:N0} GB. Read only.";
        _matrixTimer.Start();
        try
        {
            var progress = new Progress<CopyProgress>(ShowProgress);
            var result = await Task.Run(() => SectorScanner.Scan(disk.Index, startByte, slowMs,
                map => Dispatcher.Invoke(() => Matrix.Map = map), Log, progress, token));
            ShowScanResult(result);
        }
        catch (Exception ex)
        {
            ScanTitle.Text = "Scan failed";
            ScanTitle.Foreground = (Brush)FindResource("DangerBrush");
            ScanText.Text = ex.Message;
            Status("Scan failed: " + ex.Message);
        }
        finally
        {
            _matrixTimer.Stop();
            Matrix.Refresh();
            _cts.Dispose();
            _cts = null;
            SetBusy(false);
            await LoadDisksAsync(null);
        }
    }

    private void ShowScanResult(ScanResult r)
    {
        _lastScan = r;
        FindingList.ItemsSource = r.Findings;
        ScanExportButton.IsEnabled = true;

        string brush = r.UnreadableRanges > 0 || r.StoppedReason != null ? "DangerBrush"
            : r.Cancelled ? "WarnBrush" : "OkBrush";
        ScanTitle.Foreground = (Brush)FindResource(brush);
        ScanTitle.Text = r.UnreadableRanges > 0 ? $"✖  {r.UnreadableRanges:N0} unreadable area(s), {PhysicalDisk.FormatSize(r.UnreadableBytes)}"
            : r.StoppedReason != null ? "✖  The drive stopped responding"
            : r.Cancelled ? "Scan cancelled - incomplete"
            : "✔  Every sector read OK";

        var sb = new StringBuilder();
        sb.Append($"Scanned {FormatGb(r.StartByte)} to {FormatGb(r.ReachedByte)} of {FormatGb(r.DiskLength)} in {r.Duration:hh\\:mm\\:ss}. ");
        if (r.VerySlowRanges > 0) sb.Append($"{r.VerySlowRanges:N0} very slow area(s). ");
        if (r.StoppedReason != null) sb.Append(r.StoppedReason);
        ScanText.Text = sb.ToString();
        Status(ScanTitle.Text.TrimStart('✔', '✖', ' '));
    }

    private void ScanExport_Click(object sender, RoutedEventArgs e)
    {
        if (_lastScan is not { } r) return;
        var dialog = new SaveFileDialog
        {
            FileName = $"clony-scan-{DateTime.Now:yyyyMMdd-HHmm}.csv",
            Filter = "CSV report (*.csv)|*.csv",
        };
        if (dialog.ShowDialog(this) != true) return;

        var sb = new StringBuilder();
        sb.AppendLine("# Disk: " + _lastScanDisk);
        sb.AppendLine($"# Scanned bytes {r.StartByte} to {r.ReachedByte} of {r.DiskLength} in {r.Duration:hh\\:mm\\:ss}");
        sb.AppendLine($"# Unreadable areas: {r.UnreadableRanges} ({r.UnreadableBytes} bytes); very slow areas: {r.VerySlowRanges}");
        if (r.StoppedReason != null) sb.AppendLine("# Stopped: " + r.StoppedReason);
        if (r.Cancelled) sb.AppendLine("# Cancelled before the end");
        sb.AppendLine("result,location,start_byte,length_bytes,worst_ms");
        foreach (var f in r.Findings)
            sb.AppendLine($"{f.Kind},\"{f.Location.Replace("\"", "\"\"")}\",{f.StartByte},{f.Length},{f.WorstMs}");
        try
        {
            File.WriteAllText(dialog.FileName, sb.ToString(), new UTF8Encoding(true));
            Status("Report saved: " + dialog.FileName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, ex.Message, "Could not save report", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
