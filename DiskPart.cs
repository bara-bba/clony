using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace clony;

/// <summary>Disk attribute changes via diskpart (online, read-only flag). Never touches data.</summary>
internal static class DiskPart
{
    private const int SettleMs = 3000;

    /// <summary>Raw I/O is refused on an offline disk, so bring it online first (and wait for its volumes).</summary>
    public static void EnsureOnline(int disk, Action<string> log)
    {
        if (DiskEnumerator.IsOffline(disk) != true) return;
        log($"Disk {disk} is offline - bringing it online (offline disks refuse all reads and writes).");
        Run($"select disk {disk}\r\nonline disk\r\n", $"bring Disk {disk} online", log);
        Thread.Sleep(SettleMs);
    }

    public static void ClearReadOnly(int disk, Action<string> log) =>
        Run($"select disk {disk}\r\nattributes disk clear readonly\r\n", $"clear read-only on Disk {disk}", log);

    public static void BringOnline(int disk, Action<string> log) =>
        Run($"select disk {disk}\r\nonline disk\r\n", $"bring Disk {disk} online", log);

    private static void Run(string script, string what, Action<string> log)
    {
        string path = Path.Combine(Path.GetTempPath(), $"clony_diskpart_{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, script + "exit\r\n");
        try
        {
            var psi = new ProcessStartInfo("diskpart.exe", $"/s \"{path}\"")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = OemEncoding(),
            };
            using var p = Process.Start(psi) ?? throw new InvalidOperationException("Could not start diskpart.");
            string output = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            log($"diskpart: {what} -> exit code {p.ExitCode}");
            if (p.ExitCode != 0)
                throw new InvalidOperationException($"diskpart could not {what} (exit code {p.ExitCode}).\n{LastLines(output, 6)}");
        }
        finally
        {
            try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    internal static Encoding OemEncoding()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
        }
        catch (ArgumentException) { return Encoding.Default; }
        catch (NotSupportedException) { return Encoding.Default; }
    }

    internal static string LastLines(string text, int count) =>
        string.Join(Environment.NewLine, text.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).TakeLast(count));
}
