using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace clony;

/// <summary>Clone page layout preview: source and resulting copy on one scale; pick who gets the extra space.</summary>
public partial class MainWindow
{
    private const int PreviewSectorBytes = 512; // preview only - the engine uses the real sector size

    // Chosen partition per source disk (physical identity + start byte); null = the largest.
    private (string Identity, long Offset)? _growChoice;

    private void GrowCheck_Click(object sender, RoutedEventArgs e) => UpdateClonePreview();

    /// <summary>Start byte of the partition the user picked on this source, or null for the default (largest).</summary>
    private long? SelectedGrowAt(PhysicalDisk src) =>
        _growChoice is { } c && c.Identity == src.Identity ? c.Offset : null;

    private void UpdateClonePreview()
    {
        if (CloneSourceBar == null) return;
        CloneSourceBar.Children.Clear();
        CloneSourceBar.ColumnDefinitions.Clear();
        CloneCopyBar.Children.Clear();
        CloneCopyBar.ColumnDefinitions.Clear();

        if (SourceCombo.SelectedItem is not PhysicalDisk src || DestCombo.SelectedItem is not PhysicalDisk dst)
        {
            ClonePreviewText.Text = "Select a source and a destination.";
            return;
        }
        var parts = _partitions.TryGetValue(src.Index, out var p) ? p : new List<VolumePartition>();
        if (parts.Count == 0)
        {
            ClonePreviewText.Text = src.IsOffline
                ? "The source is offline, so its partitions can't be read. Bring it online to see the layout."
                : "No partitions found on the source.";
            return;
        }

        var (plan, reason) = Plan(src, dst, parts);
        DrawSource(src, dst, parts, plan);
        DrawCopy(dst, parts, plan);
        ClonePreviewText.Text = Describe(src, dst, parts, plan, reason);
    }

    private (GrowPlan? Plan, string? Reason) Plan(PhysicalDisk src, PhysicalDisk dst, List<VolumePartition> parts)
    {
        if (GrowCheck.IsChecked != true) return (null, null);
        var layout = new DiskLayout(src.PartitionStyle,
            parts.Select(v => new PartitionInfo(v.PartitionNumber, v.Kind == "Extended" ? PartitionTable.ExtendedName : "", v.Offset, v.End))
                 .OrderBy(x => x.StartByte).ToList(), -1);
        return GrowPlanner.Build(layout, new DiskGeometry(src.SizeBytes, PreviewSectorBytes),
            new DiskGeometry(dst.SizeBytes, PreviewSectorBytes), SelectedGrowAt(src));
    }

    private void DrawSource(PhysicalDisk src, PhysicalDisk dst, List<VolumePartition> parts, GrowPlan? plan)
    {
        // Same scale as the copy: the source bar only spans its share of the destination's width.
        var bar = new Grid();
        CloneSourceBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(src.SizeBytes, GridUnitType.Star) });
        if (dst.SizeBytes > src.SizeBytes)
            CloneSourceBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(dst.SizeBytes - src.SizeBytes, GridUnitType.Star) });
        CloneSourceBar.Children.Add(bar);

        var segments = PartitionMap.Build(src.SizeBytes, parts);
        FillBar(bar, segments, seg =>
        {
            var element = SegmentBorder(SegmentContent(seg), StripeFor(seg, plan, isCopy: false), plan != null && seg.Partition?.Offset == plan.Grow.StartByte);
            if (seg.Partition is { } part)
            {
                element.Cursor = Cursors.Hand;
                element.ToolTip = $"{part.Name}\n{part.SizeLine}\nClick to give this partition the extra space";
                element.MouseLeftButtonUp += (_, _) => GrowTarget_Clicked(src, part, parts);
            }
            return element;
        });
    }

    private void DrawCopy(PhysicalDisk dst, List<VolumePartition> parts, GrowPlan? plan)
    {
        var copy = parts.Select(v =>
        {
            if (plan == null) return v;
            if (v.Offset == plan.Grow.StartByte)
                return v with { Size = v.Size + plan.DeltaBytes, Kind = $"+{PhysicalDisk.FormatSize(plan.DeltaBytes)}" };
            return v.Offset >= plan.Grow.EndByte ? v with { Offset = v.Offset + plan.DeltaBytes, Kind = "moved to the end" } : v;
        }).ToList();

        var bar = new Grid();
        CloneCopyBar.Children.Add(bar);
        FillBar(bar, PartitionMap.Build(dst.SizeBytes, copy), seg =>
        {
            var element = SegmentBorder(SegmentContent(seg), StripeFor(seg, plan, isCopy: true), false);
            element.ToolTip = SegmentTooltip(seg);
            return element;
        });
    }

    private static void FillBar(Grid bar, IReadOnlyList<DiskSegment> segments, System.Func<DiskSegment, Border> make)
    {
        var weights = PartitionMap.Weights(segments, minShare: 0.05);
        for (int i = 0; i < segments.Count; i++)
        {
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(weights[i], GridUnitType.Star) });
            var element = make(segments[i]);
            Grid.SetColumn(element, i);
            bar.Children.Add(element);
        }
    }

    private Brush StripeFor(DiskSegment seg, GrowPlan? plan, bool isCopy)
    {
        if (seg.Partition is not { } p) return FreeStripe;
        if (plan == null) return PartitionStripe;
        if (p.Offset == plan.Grow.StartByte) return (Brush)FindResource("OkBrush");
        long originalOffset = isCopy && p.Offset >= plan.Grow.EndByte + plan.DeltaBytes ? p.Offset - plan.DeltaBytes : p.Offset;
        return originalOffset >= plan.Grow.EndByte ? (Brush)FindResource("WarnBrush") : PartitionStripe;
    }

    private void GrowTarget_Clicked(PhysicalDisk src, VolumePartition part, List<VolumePartition> parts)
    {
        if (_busy) return;
        bool logical = parts.Any(x => x.Kind == "Extended" && part.Offset > x.Offset && part.End <= x.End);
        if (logical)
        {
            Status($"{part.Name} is a logical partition inside an MBR extended partition - clony can't give it the extra space.");
            return;
        }
        _growChoice = (src.Identity, part.Offset);
        GrowCheck.IsChecked = true;
        UpdateClonePreview();
    }

    private static string Describe(PhysicalDisk src, PhysicalDisk dst, List<VolumePartition> parts, GrowPlan? plan, string? reason)
    {
        long extra = dst.SizeBytes - src.SizeBytes;
        if (extra <= 0) return "The destination is the same size as the source: the copy is identical.";
        if (plan == null)
            return (reason != null ? $"Can't give the extra space to a partition: {reason}. " : "") +
                   $"The copy is identical; {PhysicalDisk.FormatSize(extra)} stays unallocated at the end.";

        var grow = parts.First(p => p.Offset == plan.Grow.StartByte);
        var moved = parts.Where(p => p.Offset >= plan.Grow.EndByte && p.Kind != "Extended").Select(p => p.Name).ToList();
        string text = $"{grow.Name} gets +{PhysicalDisk.FormatSize(plan.DeltaBytes)}. " +
                      (moved.Count > 0 ? $"{string.Join(", ", moved)} {(moved.Count == 1 ? "is" : "are")} written at the end of the copy. " : "") +
                      "Everything before it stays exactly where it is.";
        if (grow.FileSystem is not ("NTFS" or "ReFS"))
            text += $"\nWindows can only extend NTFS or ReFS{(grow.FileSystem.Length > 0 ? $", not {grow.FileSystem}" : "")}: " +
                    "the space will be left free right after this partition.";
        return text;
    }
}
