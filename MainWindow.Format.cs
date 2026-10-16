using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace clony;

public partial class MainWindow
{
    private void InitFormatPage()
    {
        FormatStyleCombo.ItemsSource = FormatEngine.Styles;
        FormatStyleCombo.SelectedIndex = 0;
        FormatFsCombo.ItemsSource = FormatEngine.FileSystems;
        FormatFsCombo.SelectedIndex = 0;
    }

    private void FormatDisk_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (FormatDiskCombo.SelectedItem is not PhysicalDisk d)
        {
            FormatDiskInfo.Text = "";
            return;
        }
        FormatDiskInfo.Text = d.IsSystemDisk
            ? "Windows is running from this disk - clony won't format it."
            : $"{d.Details}" + (d.VolumeLetters.Count > 0 ? $"\nDrives {string.Join(" ", d.VolumeLetters)} will disappear." : "");
        FormatConfirmHint.Text = $"Type {d.Index} - everything on Disk {d.Index} ({d.Model}, {d.SizeDisplay}) will be erased.";
    }

    // Default: an external disk that isn't the clone source, never the Windows disk.
    private PhysicalDisk? DefaultFormatDisk() =>
        _disks.Where(d => !d.IsSystemDisk && d.CanEject && d.Identity != (SourceCombo.SelectedItem as PhysicalDisk)?.Identity).OrderByDescending(d => d.SizeBytes).FirstOrDefault()
        ?? _disks.FirstOrDefault(d => !d.IsSystemDisk && d.CanEject);

    private void FormatLog(string line) => Dispatcher.BeginInvoke(() =>
    {
        FormatLogBox.AppendText(line + Environment.NewLine);
        FormatLogBox.ScrollToEnd();
    });

    private async void Format_Click(object sender, RoutedEventArgs e)
    {
        if (FormatDiskCombo.SelectedItem is not PhysicalDisk disk)
        {
            MessageBox.Show(this, "Select a disk to format.", "clony", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var opt = new FormatOptions(disk, (string)FormatStyleCombo.SelectedItem, (string)FormatFsCombo.SelectedItem,
            FormatLabelBox.Text.Trim(), FormatFullCheck.IsChecked == true);
        if (FormatEngine.Validate(opt) is { } problem)
        {
            MessageBox.Show(this, problem, "Can't format", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!ConfirmFormat(opt)) return;

        FormatLogBox.Clear();
        SetBusy(true);
        CancelButton.IsEnabled = false; // diskpart can't be stopped safely mid-format
        bool ok = false;
        string message;
        try
        {
            var progress = new Progress<CopyProgress>(ShowProgress);
            await Task.Run(() => FormatEngine.Run(opt, FormatLog, progress));
            ok = true;
            message = $"Disk {disk.Index} ({disk.Model}) formatted as {opt.FileSystem}.";
        }
        catch (Exception ex)
        {
            message = ex.Message;
            FormatLog(">>> ERROR: " + ex.Message);
        }
        finally
        {
            FormatConfirmBox.Clear();
            SetBusy(false);
        }
        Status(message);
        MessageBox.Show(this, message, ok ? "Format complete" : "Format did not complete",
            MessageBoxButton.OK, ok ? MessageBoxImage.Information : MessageBoxImage.Error);
        await LoadDisksAsync(null);
    }

    private bool ConfirmFormat(FormatOptions o)
    {
        var d = o.Disk;
        if (FormatConfirmBox.Text.Trim() != d.Index.ToString())
        {
            MessageBox.Show(this, $"Type the disk number ({d.Index}) in the confirm box first.",
                "Confirmation required", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        string isSource = (SourceCombo.SelectedItem as PhysicalDisk)?.Identity == d.Identity
            ? "\n\nThis is the disk selected as the clone SOURCE on the Clone page.\n"
            : "";
        return MessageBox.Show(this,
                   $"ERASE Disk {d.Index} — {d.Model} ({d.SizeDisplay})?\n" +
                   (d.VolumeLetters.Count > 0 ? $"Drives {string.Join(" ", d.VolumeLetters)} and every file on them are deleted.\n" : "") +
                   $"New layout: {o.PartitionStyle}, one {o.FileSystem} partition, {(o.Full ? "full" : "quick")} format." +
                   isSource + "\n\nThis cannot be undone.",
                   "Confirm format", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No)
               == MessageBoxResult.Yes;
    }
}
