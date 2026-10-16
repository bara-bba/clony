using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace clony;

public sealed record FormatOptions(PhysicalDisk Disk, string PartitionStyle, string FileSystem, string Label, bool Full);

/// <summary>
/// Erases a whole disk and creates one partition with diskpart (clean, convert, create, format, assign).
/// Destructive by design: callers confirm first; this class re-checks the disk identity right before running.
/// </summary>
public static partial class FormatEngine
{
    public static readonly string[] Styles = { "GPT", "MBR" };
    public static readonly string[] FileSystems = { "NTFS", "exFAT", "FAT32" };
    private const long Fat32MaxBytes = 32L * 1024 * 1024 * 1024;
    private const long MbrMaxBytes = 2L * 1024 * 1024 * 1024 * 1024;

    [GeneratedRegex(@"^[^""*?/\\|.,;:+=\[\]<>]*$")]
    private static partial Regex SafeLabel();

    [GeneratedRegex(@"(\d{1,3})\s*%|(\d{1,3}) percent", RegexOptions.IgnoreCase)]
    private static partial Regex PercentLine();

    /// <summary>Returns a reason the format can't run, or null when the options are valid.</summary>
    public static string? Validate(FormatOptions o)
    {
        var d = o.Disk;
        if (d.IsSystemDisk) return $"Disk {d.Index} is the disk Windows is running from. clony never formats it.";
        if (!Styles.Contains(o.PartitionStyle)) return "Pick GPT or MBR.";
        if (!FileSystems.Contains(o.FileSystem)) return "Pick NTFS, exFAT or FAT32.";
        if (o.PartitionStyle == "MBR" && d.SizeBytes > MbrMaxBytes) return "MBR can't use more than 2 TB. Pick GPT.";
        if (o.FileSystem == "FAT32" && d.SizeBytes > Fat32MaxBytes)
            return "Windows only formats FAT32 up to 32 GB. Use exFAT for a large drive that has to work on Mac/TV/cameras, or NTFS for Windows.";
        int maxLabel = o.FileSystem == "NTFS" ? 32 : 11;
        if (o.Label.Length > maxLabel) return $"A {o.FileSystem} label can be at most {maxLabel} characters.";
        if (!SafeLabel().IsMatch(o.Label)) return "The label can't contain \" * ? / \\ | . , ; : + = [ ] < >";
        return null;
    }

    public static string BuildScript(FormatOptions o, int diskIndex)
    {
        var sb = new StringBuilder();
        sb.Append($"select disk {diskIndex}\r\n");
        sb.Append("online disk noerr\r\n");
        sb.Append("attributes disk clear readonly noerr\r\n");
        sb.Append("clean\r\n");
        sb.Append($"convert {o.PartitionStyle.ToLowerInvariant()}\r\n");
        sb.Append("create partition primary\r\n");
        string label = o.Label.Length > 0 ? $" label=\"{o.Label}\"" : "";
        sb.Append($"format fs={o.FileSystem.ToLowerInvariant()}{label}{(o.Full ? "" : " quick")}\r\n");
        sb.Append("assign\r\n");
        sb.Append("exit\r\n");
        return sb.ToString();
    }

    /// <summary>Runs the format. Blocks until diskpart finishes. Throws with diskpart's own message on failure.</summary>
    public static void Run(FormatOptions o, Action<string> log, IProgress<CopyProgress>? progress)
    {
        if (Validate(o) is { } problem) throw new InvalidOperationException(problem);

        // Disk numbers change when drives are replugged: find the same physical drive again right now.
        var now = DiskEnumerator.Enumerate().FirstOrDefault(x => x.Identity == o.Disk.Identity)
            ?? throw new InvalidOperationException($"{o.Disk.Model} is no longer connected. Nothing was changed.");
        if (now.Index != o.Disk.Index)
            throw new InvalidOperationException(
                $"{o.Disk.Model} is now Disk {now.Index}, not Disk {o.Disk.Index}. Nothing was changed - press Refresh and confirm again.");
        if (now.IsSystemDisk) throw new InvalidOperationException("That disk is now the Windows disk. Nothing was changed.");

        string script = BuildScript(o, now.Index);
        log($"Formatting Disk {now.Index} - {now.Model} ({now.SizeDisplay}): {o.PartitionStyle}, {o.FileSystem}, " +
            $"label \"{o.Label}\", {(o.Full ? "full (writes every sector)" : "quick")}.");
        foreach (string line in script.Split("\r\n", StringSplitOptions.RemoveEmptyEntries)) log("  diskpart> " + line);

        string path = Path.Combine(Path.GetTempPath(), $"clony_format_{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, script);
        var clock = Stopwatch.StartNew();
        try
        {
            var psi = new ProcessStartInfo("diskpart.exe", $"/s \"{path}\"")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = DiskPart.OemEncoding(),
            };
            using var p = Process.Start(psi) ?? throw new InvalidOperationException("Could not start diskpart.");
            var output = new StringBuilder();
            var line = new StringBuilder();
            int c;
            // diskpart rewrites its percent line with '\r', so split on both '\r' and '\n'.
            while ((c = p.StandardOutput.Read()) >= 0)
            {
                output.Append((char)c);
                if (c is '\r' or '\n') { Emit(line.ToString(), now.SizeBytes, clock, log, progress); line.Clear(); }
                else line.Append((char)c);
            }
            Emit(line.ToString(), now.SizeBytes, clock, log, progress);
            p.WaitForExit();
            if (p.ExitCode != 0)
                throw new InvalidOperationException(
                    $"diskpart stopped with exit code {p.ExitCode}. The disk may now be partly erased - format it again.\n" +
                    DiskPart.LastLines(output.ToString(), 6));
            log($"Format complete in {clock.Elapsed:hh\\:mm\\:ss}.");
        }
        finally
        {
            try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static void Emit(string raw, long size, Stopwatch clock, Action<string> log, IProgress<CopyProgress>? progress)
    {
        string text = raw.Trim();
        if (text.Length == 0) return;
        var m = PercentLine().Match(text);
        if (m.Success && int.TryParse(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value, out int pct) && pct <= 100)
        {
            long done = size * pct / 100;
            double secs = Math.Max(clock.Elapsed.TotalSeconds, 0.001);
            progress?.Report(new CopyProgress("Formatting", done, size, done / secs, clock.Elapsed));
            return; // percent lines repeat many times; keep them out of the log
        }
        if (text.StartsWith("Microsoft DiskPart", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("Copyright", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("On computer", StringComparison.OrdinalIgnoreCase)) return;
        log("  " + text);
    }
}
