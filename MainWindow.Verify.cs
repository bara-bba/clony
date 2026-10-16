using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;

namespace clony;

public partial class MainWindow
{
    private VerifyResult? _lastVerify;
    private string _lastVerifyPair = "";

    private async void Verify_Click(object sender, RoutedEventArgs e)
    {
        if (VerifySourceCombo.SelectedItem is not PhysicalDisk src || VerifyDestCombo.SelectedItem is not PhysicalDisk dst)
        {
            MessageBox.Show(this, "Select the original and the copy.", "clony", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (src == dst)
        {
            MessageBox.Show(this, "Pick two different disks.", "clony", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        bool offline = VerifyOfflineCheck.IsChecked == true;
        int block = SelectedBlockBytes();
        SetBusy(true);
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        SummaryTitle.Text = "Comparing...";
        SummaryText.Text = $"Disk {src.Index} ({src.Model}) vs Disk {dst.Index} ({dst.Model})";
        try
        {
            var progress = new Progress<CopyProgress>(ShowProgress);
            var result = await Task.Run(() => Verifier.Compare(src, dst, block, offline, Log, progress, token));
            ShowVerifyResult(result, src, dst, switchPage: false);
        }
        catch (Exception ex)
        {
            SummaryTitle.Text = "Comparison failed";
            SummaryTitle.Foreground = (System.Windows.Media.Brush)FindResource("DangerBrush");
            SummaryText.Text = ex.Message;
            Status("Compare failed: " + ex.Message);
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            SetBusy(false);
            await LoadDisksAsync(null);
        }
    }

    private void ShowVerifyResult(VerifyResult r, PhysicalDisk src, PhysicalDisk dst, bool switchPage)
    {
        _lastVerify = r;
        _lastVerifyPair = $"Original: {src.Caption}\nCopy:     {dst.Caption}";
        DiffList.ItemsSource = r.Differences;
        ExportButton.IsEnabled = true;

        string brush = r.Cancelled ? "WarnBrush" : r.Identical ? "OkBrush" : "DangerBrush";
        SummaryTitle.Foreground = (System.Windows.Media.Brush)FindResource(brush);
        SummaryTitle.Text = r.Cancelled ? "Comparison cancelled - incomplete"
            : r.Identical ? "✔  Identical - the copy matches the original bit for bit"
            : $"✖  {r.UnexpectedRanges:N0} difference(s) found ({PhysicalDisk.FormatSize(r.UnexpectedBytes)})";

        var sb = new StringBuilder();
        sb.Append($"Compared {PhysicalDisk.FormatSize(r.BytesCompared)} of {PhysicalDisk.FormatSize(r.SourceLength)} in {r.Duration:hh\\:mm\\:ss}. ");
        if (r.ExpectedRanges > 0)
            sb.Append($"{r.ExpectedRanges} expected difference(s): the partition table was updated for the larger disk. ");
        if (r.DestinationLength > r.SourceLength)
            sb.Append($"The extra {PhysicalDisk.FormatSize(r.DestinationLength - r.SourceLength)} on the copy is outside the original and not compared. ");
        if (r.TotalRanges > r.Differences.Count)
            sb.Append($"Only the first {r.Differences.Count:N0} ranges are listed.");
        SummaryText.Text = sb.ToString();

        if (switchPage) VerifyTab.IsChecked = true;
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_lastVerify is not { } r) return;
        var dialog = new SaveFileDialog
        {
            FileName = $"clony-verify-{DateTime.Now:yyyyMMdd-HHmm}.csv",
            Filter = "CSV report (*.csv)|*.csv",
        };
        if (dialog.ShowDialog(this) != true) return;

        var sb = new StringBuilder();
        foreach (string line in _lastVerifyPair.Split('\n')) sb.AppendLine("# " + line);
        sb.AppendLine($"# {CloneEngine.VerifySummary(r)}");
        sb.AppendLine("result,location,start_byte,length_bytes");
        foreach (var d in r.Differences)
            sb.AppendLine($"{d.Kind},\"{d.Location.Replace("\"", "\"\"")}\",{d.StartByte},{d.Length}");
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
