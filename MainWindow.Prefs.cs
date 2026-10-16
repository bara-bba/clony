using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace clony;

public sealed record Swatch(string Name, string Hex, bool IsSelected)
{
    public Brush Brush => new SolidColorBrush((Color)ColorConverter.ConvertFromString(Hex));
}

public partial class MainWindow
{
    private static readonly (string Name, string Hex)[] Presets =
    {
        ("White (default)", "#FFFFFF"), ("Sky", "#4CC2FF"), ("Picky blue", "#0078D4"), ("Teal", "#2EC4B6"),
        ("Green", "#4CAF6E"), ("Yellow", "#FFD54C"), ("Orange", "#FF8C00"), ("Red", "#E04B4B"),
        ("Pink", "#FF6FB5"), ("Purple", "#B4A0FF"), ("Grey", "#A0A0A8"),
    };

    private void InitPreferences()
    {
        AccentTheme.Changed += OnAccentChanged;
        OnAccentChanged();
    }

    private void OnAccentChanged()
    {
        var accent = AccentTheme.Current;
        var glyph = AccentTheme.OnAccent;
        HeaderLogo.Source = AppIcon.CreateImageSource(accent, glyph, 80);
        PreviewLogo.Source = AppIcon.CreateImageSource(accent, glyph, 192);
        Icon = AppIcon.CreateImageSource(accent, glyph, 64);   // title bar; the exe icon stays white
        string hex = AccentTheme.ToHex(accent);
        AccentHexInput.Text = hex;
        SwatchList.ItemsSource = Presets.Select(p => new Swatch(p.Name, p.Hex, p.Hex == hex)).ToList();
    }

    private void Swatch_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: Swatch s } && AccentTheme.TryParse(s.Hex, out var c))
            ApplyAccent(c);
    }

    private void ApplyAccent_Click(object sender, RoutedEventArgs e) => TryApplyHex();

    private void ResetAccent_Click(object sender, RoutedEventArgs e) => ApplyAccent(AccentTheme.Default);

    private void AccentHex_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) TryApplyHex();
    }

    private void TryApplyHex()
    {
        if (AccentTheme.TryParse(AccentHexInput.Text, out var c))
        {
            ApplyAccent(c);
            return;
        }
        AccentError.Text = "Enter a colour as #RRGGBB, e.g. #4CC2FF.";
        AccentError.Visibility = Visibility.Visible;
        AccentHexInput.Text = AccentTheme.ToHex(AccentTheme.Current);
    }

    private void ApplyAccent(Color c)
    {
        string? error = App.ApplyAccent(c, persist: true);
        AccentError.Text = error == null ? "" : "Colour applied but could not be saved: " + error;
        AccentError.Visibility = error == null ? Visibility.Collapsed : Visibility.Visible;
    }
}
