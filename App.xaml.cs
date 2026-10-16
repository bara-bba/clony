using System;
using System.IO;
using System.Windows;
using System.Windows.Media;

namespace clony;

public partial class App : Application
{
    internal static AppSettings Settings { get; private set; } = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        // Build-time helper (same as Picky): `clony.exe --emit-icon <path>` writes the app .ico and exits.
        if (e.Args.Length == 2 && e.Args[0] == "--emit-icon")
        {
            File.WriteAllBytes(e.Args[1], AppIcon.BuildIco(Colors.White, Colors.Black));
            Shutdown(0);
            return;
        }

        Settings = AppSettings.Load();
        AccentTheme.Apply(AccentTheme.TryParse(Settings.AccentColor, out var accent) ? accent : AccentTheme.Default);
        base.OnStartup(e);
        new MainWindow().Show();
    }

    /// <summary>Applies an accent everywhere and optionally remembers it.</summary>
    internal static string? ApplyAccent(Color color, bool persist)
    {
        AccentTheme.Apply(color);
        if (!persist) return null;
        Settings.AccentColor = AccentTheme.ToHex(color);
        return Settings.Save();
    }
}
