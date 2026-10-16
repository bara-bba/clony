using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;

namespace clony;

public partial class MainWindow : Window
{
    private const int WM_DEVICECHANGE = 0x0219;
    private const int DBT_DEVICEARRIVAL = 0x8000;
    private const int DBT_DEVICEREMOVECOMPLETE = 0x8004;
    private const int DBT_DEVNODES_CHANGED = 0x0007;

    private List<PhysicalDisk> _disks = new();
    private CancellationTokenSource? _cts;
    private bool _busy;
    private readonly DispatcherTimer _deviceDebounce;

    public MainWindow()
    {
        InitializeComponent();
        RefreshButton.Content = GlyphIcon.Create("\u27F3");
        InitTitleBar();
        InitPreferences();
        InitScanPage();
        InitFormatPage();
        _deviceDebounce = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        _deviceDebounce.Tick += (_, _) => { _deviceDebounce.Stop(); if (!_busy) _ = LoadDisksAsync("Disk change detected - list refreshed."); };
        Loaded += async (_, _) => await LoadDisksAsync(null);
        SourceInitialized += (_, _) => HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(WndProc);
        Closing += Window_Closing;
    }

    // ---------- disks ----------

    private async Task LoadDisksAsync(string? reason)
    {
        string? src = (SourceCombo.SelectedItem as PhysicalDisk)?.Identity;
        string? dst = (DestCombo.SelectedItem as PhysicalDisk)?.Identity;
        string? vsrc = (VerifySourceCombo.SelectedItem as PhysicalDisk)?.Identity;
        string? vdst = (VerifyDestCombo.SelectedItem as PhysicalDisk)?.Identity;
        string? scan = (ScanDiskCombo.SelectedItem as PhysicalDisk)?.Identity;
        string? fmt = (FormatDiskCombo.SelectedItem as PhysicalDisk)?.Identity;
        RefreshButton.IsEnabled = false;
        Status("Scanning disks...");
        try
        {
            _disks = await Task.Run(DiskEnumerator.Enumerate);
            DiskList.ItemsSource = _disks;
            foreach (var combo in new[] { SourceCombo, DestCombo, VerifySourceCombo, VerifyDestCombo, ScanDiskCombo, FormatDiskCombo })
                combo.ItemsSource = _disks;

            // Re-select by physical identity, never by number: numbers change when drives are replugged.
            SourceCombo.SelectedItem = Find(src) ?? DefaultSource();
            DestCombo.SelectedItem = Find(dst) ?? DefaultDestination(SourceCombo.SelectedItem as PhysicalDisk);
            VerifySourceCombo.SelectedItem = Find(vsrc) ?? SourceCombo.SelectedItem;
            VerifyDestCombo.SelectedItem = Find(vdst) ?? DestCombo.SelectedItem;
            ScanDiskCombo.SelectedItem = Find(scan) ?? SourceCombo.SelectedItem;
            FormatDiskCombo.SelectedItem = Find(fmt) ?? DefaultFormatDisk();
            await LoadPartitionsAsync();

            Status(reason ?? $"Found {_disks.Count} disk(s).");
        }
        catch (Exception ex)
        {
            Status("Could not read the disk list: " + ex.Message);
        }
        finally
        {
            RefreshButton.IsEnabled = !_busy;
            UpdateCloneButton();
        }
    }

    private PhysicalDisk? Find(string? identity) =>
        identity == null ? null : _disks.FirstOrDefault(d => d.Identity == identity);

    // Default pick: the 512 GB-ish external disk as source, the larger external one as destination.
    private PhysicalDisk? DefaultSource() =>
        _disks.Where(d => !d.IsSystemDisk).OrderBy(d => d.SizeBytes).FirstOrDefault();

    private PhysicalDisk? DefaultDestination(PhysicalDisk? source) =>
        _disks.Where(d => !d.IsSystemDisk && d != source && (source == null || d.SizeBytes >= source.SizeBytes))
              .OrderBy(d => d.SizeBytes).FirstOrDefault();

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadDisksAsync(null);

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_DEVICECHANGE && (int)wParam is DBT_DEVICEARRIVAL or DBT_DEVICEREMOVECOMPLETE or DBT_DEVNODES_CHANGED)
        {
            _deviceDebounce.Stop();
            _deviceDebounce.Start();
        }
        return IntPtr.Zero;
    }

    private async void Eject_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { CommandParameter: PhysicalDisk disk } || _busy) return;

        Status($"Ejecting Disk {disk.Index} ({disk.Model})...");
        var (ok, message) = await Task.Run(() => DeviceEject.Eject(disk.PnpDeviceId));
        Status(ok ? $"Disk {disk.Index} ({disk.Model}): safe to remove." : $"Disk {disk.Index} not ejected: {message}");
        if (!ok)
            MessageBox.Show(this, message, $"Could not eject Disk {disk.Index}", MessageBoxButton.OK, MessageBoxImage.Warning);
        await LoadDisksAsync(ok ? $"Disk {disk.Index} ejected - you can unplug it." : null);
    }

    // ---------- pages / shared UI ----------

    private void Tab_Checked(object sender, RoutedEventArgs e)
    {
        if (ClonePage == null || VerifyPage == null || ScanPage == null || FormatPage == null || PartitionsPage == null || PrefsPage == null || DiskCard == null) return;
        bool clone = CloneTab.IsChecked == true, verify = VerifyTab.IsChecked == true, prefs = PrefsTab.IsChecked == true;
        ClonePage.Visibility = clone ? Visibility.Visible : Visibility.Collapsed;
        VerifyPage.Visibility = verify ? Visibility.Visible : Visibility.Collapsed;
        ScanPage.Visibility = ScanTab.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        FormatPage.Visibility = FormatTab.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        bool parts = PartitionsTab.IsChecked == true;
        PartitionsPage.Visibility = parts ? Visibility.Visible : Visibility.Collapsed;
        if (sender is RadioButton { ToolTip: string name } && PageTitle != null) PageTitle.Text = name;
        PrefsPage.Visibility = prefs ? Visibility.Visible : Visibility.Collapsed;
        // Preferences and Partitions use the full height; Partitions shows every disk itself.
        DiskCard.Visibility = prefs || parts ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        Status("Cancelling after the current block...");
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        foreach (var c in new Control[] { CloneButton, VerifyButton, RefreshButton, SourceCombo, DestCombo,
                     VerifySourceCombo, VerifyDestCombo, DryRunCheck, VerifyCheck, GptCheck, GrowCheck, BlockCombo, ConfirmBox,
                     ScanButton, ScanDiskCombo, ScanStartBox, ScanSlowCombo,
                     FormatButton, FormatDiskCombo, FormatStyleCombo, FormatFsCombo, FormatLabelBox, FormatFullCheck, FormatConfirmBox, DiskList })
            c.IsEnabled = !busy;
        // Page tabs stay clickable while busy: every action button is disabled anyway, and a locked tab
        // strip could strand you on a page (e.g. Preferences) until the job finished.
        CancelButton.IsEnabled = busy;
        if (busy) { PartResizePanel.IsEnabled = false; PartResizeButton.IsEnabled = false; }
        else RestorePartitionControls();
        if (!busy) Progress.Value = 0;
        if (!busy && _closeRequested) Dispatcher.BeginInvoke(Close);
    }

    private bool _closeRequested;

    private void ShowProgress(CopyProgress p)
    {
        Progress.Value = p.Percent;
        StatusText.Text = p.Text;
    }

    private void Status(string text) => StatusText.Text = text;

    private void Log(string line) => Dispatcher.BeginInvoke(() =>
    {
        LogBox.AppendText(line + Environment.NewLine);
        LogBox.ScrollToEnd();
    });

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_busy) return;
        var answer = MessageBox.Show(this,
            "A copy, compare or scan is running. Closing now stops it part-way (a copy leaves the destination incomplete).\n\nCancel it and close?",
            "clony", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer == MessageBoxResult.Yes) { _closeRequested = true; _cts?.Cancel(); }
        e.Cancel = true; // close only after the work has stopped cleanly
    }
}
