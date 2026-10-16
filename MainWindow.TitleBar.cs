using System.Windows;
using System.Windows.Input;

namespace clony;

/// <summary>Custom title bar (no Windows title bar), same approach as Picky.</summary>
public partial class MainWindow
{
    private static readonly Thickness NormalMargin = new(20, 12, 12, 20);
    // A maximized borderless window extends past the screen by the resize border; pad it back in.
    private static readonly Thickness MaximizedMargin = new(28, 20, 20, 28);

    private void InitTitleBar()
    {
        DwmHelper.ApplyPowerToysChrome(this);
        StateChanged += (_, _) =>
        {
            bool max = WindowState == WindowState.Maximized;
            RootGrid.Margin = max ? MaximizedMargin : NormalMargin;
            MaximizeButton.Content = max ? "\uE923" : "\uE922";
            MaximizeButton.ToolTip = max ? "Restore" : "Maximize";
        };
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) { ToggleMaximize(); return; }
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e) => ToggleMaximize();

    private void CloseWindow_Click(object sender, RoutedEventArgs e) => Close();

    private void ToggleMaximize() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
}
