using System;
using System.Windows;
using System.Windows.Controls;

namespace clony;

public partial class MainWindow
{
    private const int Megabyte = 1024 * 1024;

    private void Selection_Changed(object sender, SelectionChangedEventArgs e)
    {
        UpdateCloneButton();
        UpdateClonePreview();
    }

    private void DryRun_Click(object sender, RoutedEventArgs e) => UpdateCloneButton();

    private void UpdateCloneButton()
    {
        if (CloneButton == null || DryRunCheck == null) return;
        bool dry = DryRunCheck.IsChecked == true;
        CloneButton.Content = dry ? "Run dry run" : "Start clone";
        CloneButton.Style = (Style)FindResource(dry ? "AccentButton" : "DangerButton");
        if (DestCombo.SelectedItem is PhysicalDisk d)
            ConfirmHint.Text = $"For a real clone, type {d.Index} - everything on Disk {d.Index} ({d.Model}, {d.SizeDisplay}) will be overwritten.";
    }

    private int SelectedBlockBytes() =>
        BlockCombo.SelectedItem is ComboBoxItem { Tag: string mb } && int.TryParse(mb, out int v) ? v * Megabyte : 16 * Megabyte;

    private async void Clone_Click(object sender, RoutedEventArgs e)
    {
        if (SourceCombo.SelectedItem is not PhysicalDisk src || DestCombo.SelectedItem is not PhysicalDisk dst)
        {
            MessageBox.Show(this, "Select a source and a destination disk.", "clony", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        bool dryRun = DryRunCheck.IsChecked == true;
        if (!dryRun && !ConfirmLive(src, dst)) return;

        var opt = new CloneOptions(src, dst, SelectedBlockBytes(), GptCheck.IsChecked == true, VerifyCheck.IsChecked == true, dryRun,
            GrowMain: GrowCheck.IsChecked == true, GrowAt: SelectedGrowAt(src));
        LogBox.Clear();
        SetBusy(true);
        _cts = new System.Threading.CancellationTokenSource();
        try
        {
            var engine = new CloneEngine(Log, new Progress<CopyProgress>(ShowProgress));
            var result = await engine.RunAsync(opt, _cts.Token);
            Status(result.Message);
            if (result.Verify != null) ShowVerifyResult(result.Verify, src, dst, switchPage: true);
            if (!dryRun)
                MessageBox.Show(this, result.Message, result.Success ? "Clone complete" : "Clone did not complete",
                    MessageBoxButton.OK, result.Success ? MessageBoxImage.Information : MessageBoxImage.Error);
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            ConfirmBox.Clear();
            SetBusy(false);
            await LoadDisksAsync(null);
        }
    }

    private bool ConfirmLive(PhysicalDisk src, PhysicalDisk dst)
    {
        if (ConfirmBox.Text.Trim() != dst.Index.ToString())
        {
            MessageBox.Show(this, $"Type the destination disk number ({dst.Index}) in the confirm box first.",
                "Confirmation required", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        return MessageBox.Show(this,
                   $"OVERWRITE Disk {dst.Index} — {dst.Model} ({dst.SizeDisplay})\n" +
                   $"with an exact copy of Disk {src.Index} — {src.Model} ({src.SizeDisplay})?\n\n" +
                   "Everything currently on the destination is lost. This cannot be undone.\n" +
                   "Both disks are locked while copying; the copy is left offline at the end.",
                   "Confirm clone", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No)
               == MessageBoxResult.Yes;
    }
}
