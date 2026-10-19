using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace clony;

/// <summary>Partitions page: Disk Management-style layout bars plus shrink/extend.</summary>
public partial class MainWindow
{
    private const double GiBytes = 1024.0 * 1024 * 1024;
    private const long OneMiB = 1024 * 1024;

    private static readonly Brush PartitionStripe = Frozen(Color.FromRgb(0x3C, 0x78, 0xD8));
    private static readonly Brush FreeStripe = Frozen(Color.FromRgb(0x0A, 0x0A, 0x0A));

    private Dictionary<int, List<VolumePartition>> _partitions = new();
    private (string DiskIdentity, long Offset)? _selectedSegment;
    private VolumePartition? _selectedPartition;
    private ResizeRange? _range;
    private bool _syncingSize;
    private int _rangeRequest;

    private static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private async Task LoadPartitionsAsync()
    {
        try
        {
            _partitions = await Task.Run(PartitionManager.EnumerateAll);
        }
        catch (Exception ex)
        {
            _partitions = new();
            Status("Could not read partitions: " + ex.Message);
        }
        RenderPartitionDisks(restoreSelection: true);
        UpdateClonePreview();
    }

    /// <summary>Redraws the bars. restoreSelection re-queries the selected partition (after a refresh).</summary>
    private void RenderPartitionDisks(bool restoreSelection = false)
    {
        PartitionDisksPanel.Children.Clear();
        VolumePartition? reselect = null;
        foreach (var disk in _disks)
        {
            var parts = _partitions.TryGetValue(disk.Index, out var p) ? p : new List<VolumePartition>();
            PartitionDisksPanel.Children.Add(BuildDiskRow(disk, parts, ref reselect));
        }
        if (_disks.Count == 0)
            PartitionDisksPanel.Children.Add(new TextBlock { Text = "No disks found.", Style = (Style)FindResource("Caption") });

        // Keep the selection on the same physical disk + offset after a refresh (numbers can change).
        if (!restoreSelection) return;
        if (reselect != null) _ = SelectPartitionAsync(reselect);
        else ClearPartitionSelection();
    }

    private UIElement BuildDiskRow(PhysicalDisk disk, List<VolumePartition> parts, ref VolumePartition? reselect)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 10), Height = 74 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // Left header, like Disk Management's "Disk 0 / Basic / 931.51 GB / Online".
        var header = new Border
        {
            Background = (Brush)FindResource("SurfaceAltBrush"),
            BorderBrush = (Brush)FindResource("LineBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6, 0, 0, 6),
            Padding = new Thickness(10, 6, 10, 6),
        };
        var hs = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        hs.Children.Add(new TextBlock { Text = $"Disk {disk.Index}", FontWeight = FontWeights.SemiBold });
        hs.Children.Add(Caption($"{disk.PartitionStyle} · {disk.SizeDisplay}"));
        hs.Children.Add(Caption(disk.IsOffline ? "Offline" : disk.IsSystemDisk ? "Online · Windows" : "Online",
            disk.IsOffline ? (Brush)FindResource("WarnBrush") : null));
        hs.Children.Add(Caption(disk.Model));
        header.Child = hs;
        header.ToolTip = disk.Details;
        row.Children.Add(header);

        var bar = new Grid();
        Grid.SetColumn(bar, 1);
        row.Children.Add(bar);

        var segments = PartitionMap.Build(disk.SizeBytes, parts);
        if (segments.Count == 0)
        {
            bar.Children.Add(SegmentBorder(Caption(disk.IsOffline ? "Offline - partitions can't be read" : "No partitions"), FreeStripe, false));
            return row;
        }

        var weights = PartitionMap.Weights(segments);
        for (int i = 0; i < segments.Count; i++)
        {
            var seg = segments[i];
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(weights[i], GridUnitType.Star) });
            bool selected = _selectedSegment is { } s && s.DiskIdentity == disk.Identity && s.Offset == seg.Offset;
            if (selected && seg.Partition != null) reselect = seg.Partition;

            var element = SegmentBorder(SegmentContent(seg), seg.IsFree ? FreeStripe : PartitionStripe, selected);
            element.ToolTip = SegmentTooltip(seg);
            element.Cursor = Cursors.Hand;
            element.MouseLeftButtonUp += (_, _) => Segment_Clicked(disk, seg);
            Grid.SetColumn(element, i);
            bar.Children.Add(element);
        }
        return row;
    }

    private Border SegmentBorder(UIElement content, Brush stripe, bool selected)
    {
        var dock = new DockPanel();
        var top = new Border { Height = 6, Background = stripe, CornerRadius = new CornerRadius(3, 3, 0, 0) };
        DockPanel.SetDock(top, Dock.Top);
        dock.Children.Add(top);
        dock.Children.Add(new Border { Padding = new Thickness(8, 4, 8, 4), Child = content });
        return new Border
        {
            Margin = new Thickness(2, 0, 0, 0),
            Background = (Brush)FindResource("SurfaceBrush"),
            BorderBrush = selected ? (Brush)FindResource("AccentBrush") : (Brush)FindResource("LineBrush"),
            BorderThickness = new Thickness(selected ? 2 : 1),
            CornerRadius = new CornerRadius(4),
            Child = dock,
            ClipToBounds = true,
        };
    }

    private UIElement SegmentContent(DiskSegment seg)
    {
        var sp = new StackPanel();
        if (seg.Partition is { } p)
        {
            sp.Children.Add(new TextBlock { Text = p.Name, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            sp.Children.Add(Caption(p.SizeLine));
            sp.Children.Add(Caption(p.StatusLine));
        }
        else
        {
            sp.Children.Add(new TextBlock { Text = PhysicalDisk.FormatSize(seg.Size), FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            sp.Children.Add(Caption("Unallocated"));
        }
        return sp;
    }

    private static string SegmentTooltip(DiskSegment seg) => seg.Partition is { } p
        ? $"{p.Name}\n{p.SizeLine} · {p.StatusLine}\n{p.UsageLine}\nPartition {p.PartitionNumber} at byte {p.Offset:N0}"
        : $"Unallocated {PhysicalDisk.FormatSize(seg.Size)}\nat byte {seg.Offset:N0}";

    private TextBlock Caption(string text, Brush? foreground = null)
    {
        var t = new TextBlock { Text = text, Style = (Style)FindResource("Caption"), TextTrimming = TextTrimming.CharacterEllipsis };
        if (foreground != null) t.Foreground = foreground;
        return t;
    }

    private void Segment_Clicked(PhysicalDisk disk, DiskSegment seg)
    {
        if (_busy) return;
        _selectedSegment = (disk.Identity, seg.Offset);
        if (seg.Partition is { } p)
        {
            _ = SelectPartitionAsync(p);
            RenderPartitionDisks();
            return;
        }
        ClearPartitionSelection(keepSegment: true);
        PartTitle.Text = $"Unallocated · {PhysicalDisk.FormatSize(seg.Size)} on Disk {disk.Index}";
        PartText.Text = "Free space. To use it, select the partition directly to its left and extend it. " +
                        "Windows can only extend a partition into free space that comes right after it.";
        RenderPartitionDisks();
    }

    private async Task SelectPartitionAsync(VolumePartition p)
    {
        _selectedPartition = p;
        _range = null;
        PartResizePanel.IsEnabled = false;
        PartResizeButton.IsEnabled = false;
        var disk = _disks.FirstOrDefault(d => d.Index == p.DiskNumber);
        PartTitle.Text = $"{p.Name} · Disk {p.DiskNumber}, partition {p.PartitionNumber}";
        PartText.Text = $"{p.SizeLine} · {p.StatusLine}\n{p.UsageLine}";

        if (disk?.IsOffline == true)
        {
            PartRangeText.Text = "This disk is offline. Bring it online in Disk Management to resize. " +
                                 "A clony copy is kept offline on purpose: don't bring it online while the original is plugged in.";
            return;
        }
        if (p.FileSystem is not ("NTFS" or "ReFS"))
        {
            PartRangeText.Text = p.FileSystem.Length == 0
                ? "Only partitions with an NTFS or ReFS file system can be resized here."
                : $"{p.FileSystem} partitions can't be resized by Windows. Only NTFS and ReFS can.";
            return;
        }

        int request = ++_rangeRequest;
        PartRangeText.Text = "Checking how far it can shrink and extend... (Windows scans the file system, this can take a moment)";
        try
        {
            var range = await Task.Run(() => PartitionManager.GetSupportedSize(p.DiskNumber, p.PartitionNumber));
            if (request != _rangeRequest || _selectedPartition != p) return; // a newer click won
            _range = range;
            ShowRange(p, range);
        }
        catch (Exception ex)
        {
            if (request != _rangeRequest) return;
            PartRangeText.Text = ex.Message;
        }
    }

    private void ShowRange(VolumePartition p, ResizeRange r)
    {
        PartRangeText.Text =
            $"Shrink down to {PhysicalDisk.FormatSize(r.Min)} · extend up to {PhysicalDisk.FormatSize(r.Max)}" +
            (r.Max <= p.Size + OneMiB ? "\nNo free space directly after this partition, so it can't be extended." : "");
        _syncingSize = true;
        PartSlider.Minimum = r.Min / GiBytes;
        PartSlider.Maximum = r.Max / GiBytes;
        PartSlider.SmallChange = 1;
        PartSlider.LargeChange = Math.Max(1, (r.Max - r.Min) / GiBytes / 10);
        PartSlider.Value = p.Size / GiBytes;
        PartSizeBox.Text = (p.Size / GiBytes).ToString("0.##");
        _syncingSize = false;
        PartResizePanel.IsEnabled = !_busy && r.Max > r.Min;
        UpdateResizeButton();
    }

    private void ClearPartitionSelection(bool keepSegment = false)
    {
        if (!keepSegment) _selectedSegment = null;
        _selectedPartition = null;
        _range = null;
        _rangeRequest++;
        PartResizePanel.IsEnabled = false;
        PartResizeButton.IsEnabled = false;
        PartTitle.Text = "Nothing selected";
        PartText.Text = "Like Disk Management: a partition can be shrunk, or extended into unallocated space directly after it.";
        PartRangeText.Text = "";
    }

    private void PartSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncingSize || PartSizeBox == null) return;
        _syncingSize = true;
        PartSizeBox.Text = e.NewValue.ToString("0.##");
        _syncingSize = false;
        UpdateResizeButton();
    }

    private void PartSizeBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncingSize || PartSlider == null) return;
        if (double.TryParse(PartSizeBox.Text, out double gb))
        {
            _syncingSize = true;
            PartSlider.Value = Math.Clamp(gb, PartSlider.Minimum, PartSlider.Maximum);
            _syncingSize = false;
        }
        UpdateResizeButton();
    }

    private void PartMin_Click(object sender, RoutedEventArgs e) { if (_range != null) SetRequested(_range.Min); }

    private void PartMax_Click(object sender, RoutedEventArgs e) { if (_range != null) SetRequested(_range.Max); }

    private void SetRequested(long bytes)
    {
        _syncingSize = true;
        PartSlider.Value = bytes / GiBytes;
        PartSizeBox.Text = (bytes / GiBytes).ToString("0.##");
        _syncingSize = false;
        UpdateResizeButton();
    }

    /// <summary>GB box -> bytes, snapped to the exact min/max when within a MB (the box shows 2 decimals).</summary>
    private bool TryReadRequestedSize(out long bytes)
    {
        bytes = 0;
        if (_range == null || !double.TryParse(PartSizeBox.Text, out double gb) || gb <= 0 || double.IsNaN(gb)) return false;
        double raw = gb * GiBytes;
        const double snap = 6 * OneMiB; // 0.01 GB rounding in the box is ~10 MB; snap if within ~6 MB
        if (Math.Abs(raw - _range.Max) < snap) { bytes = _range.Max; return true; }
        if (Math.Abs(raw - _range.Min) < snap) { bytes = _range.Min; return true; }
        bytes = (long)(raw / OneMiB) * OneMiB; // whole MB, like Disk Management
        return true;
    }

    /// <summary>Re-enables the resize controls after a job, only if the current selection allows it.</summary>
    private void RestorePartitionControls()
    {
        PartResizePanel.IsEnabled = !_busy && _range is { } r && r.Max > r.Min;
        UpdateResizeButton();
    }

    private void UpdateResizeButton()
    {
        if (PartResizeButton == null) return;
        bool ok = !_busy && _selectedPartition is { } p && _range is { } r && TryReadRequestedSize(out long req)
                  && PartitionMap.ValidateNewSize(p.Size, r.Min, r.Max, req) == null;
        PartResizeButton.IsEnabled = ok;
        if (_selectedPartition is { } sel && TryReadRequestedSize(out long n))
            PartResizeButton.Content = n < sel.Size ? $"Shrink by {PhysicalDisk.FormatSize(sel.Size - n)}"
                                     : n > sel.Size ? $"Extend by {PhysicalDisk.FormatSize(n - sel.Size)}"
                                     : "Resize";
    }

    private async void PartResize_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedPartition is not { } p || _range is not { } r || !TryReadRequestedSize(out long newSize)) return;
        if (PartitionMap.ValidateNewSize(p.Size, r.Min, r.Max, newSize) is { } problem)
        {
            MessageBox.Show(this, problem, "Can't resize", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var disk = _disks.FirstOrDefault(d => d.Index == p.DiskNumber);
        if (!ConfirmResize(p, disk, newSize)) return;

        SetBusy(true);
        CancelButton.IsEnabled = false; // a resize can't be stopped safely part-way
        Status($"{(newSize < p.Size ? "Shrinking" : "Extending")} {p.Name} to {PhysicalDisk.FormatSize(newSize)}...");
        bool ok = false;
        string message;
        try
        {
            await Task.Run(() => PartitionManager.Resize(p.DiskNumber, p.PartitionNumber, newSize));
            ok = true;
            message = $"{p.Name} on Disk {p.DiskNumber} is now {PhysicalDisk.FormatSize(newSize)}.";
        }
        catch (Exception ex)
        {
            message = ex.Message;
        }
        finally
        {
            SetBusy(false);
        }
        Status(message);
        MessageBox.Show(this, message, ok ? "Resize complete" : "Resize did not complete",
            MessageBoxButton.OK, ok ? MessageBoxImage.Information : MessageBoxImage.Error);
        await LoadDisksAsync(null);
    }

    private bool ConfirmResize(VolumePartition p, PhysicalDisk? disk, long newSize)
    {
        bool shrink = newSize < p.Size;
        var notes = new List<string>();
        if (disk?.IsSystemDisk == true)
            notes.Add("Windows is running from this disk. Resizing works while Windows runs, but have a backup first.");
        if (disk != null && (SourceCombo.SelectedItem as PhysicalDisk)?.Identity == disk.Identity)
            notes.Add("This disk is the clone SOURCE on the Clone page. Changing it means any copy already made no longer matches.");
        if (shrink)
            notes.Add($"Shrinking frees {PhysicalDisk.FormatSize(p.Size - newSize)} as unallocated space after the partition. Files are kept.");
        else
            notes.Add($"Extending uses {PhysicalDisk.FormatSize(newSize - p.Size)} of the free space after the partition. Files are kept.");
        notes.Add("A power cut or unplugged USB cable during the resize can damage the file system.");

        return MessageBox.Show(this,
                   $"{(shrink ? "Shrink" : "Extend")} {p.Name} on Disk {p.DiskNumber}" +
                   (disk != null ? $" ({disk.Model})" : "") +
                   $"\nfrom {PhysicalDisk.FormatSize(p.Size)} to {PhysicalDisk.FormatSize(newSize)}?\n\n" +
                   string.Join("\n\n", notes),
                   "Confirm resize", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No)
               == MessageBoxResult.Yes;
    }
}
