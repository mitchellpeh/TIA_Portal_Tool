using System.Windows;
using System.Windows.Input;

namespace TiaPortalTool;

/// <summary>App settings, opened from the start-up screen: the Modern or Windows 95 style.</summary>
public partial class SettingsWindow : Window
{
    private readonly bool _loading = true;

    public SettingsWindow()
    {
        InitializeComponent();
        (ThemeManager.Win95 ? Win95Radio : ModernRadio).IsChecked = true;
        _loading = false;
        Loaded += (_, _) => CloseButton.Focus();
    }

    private void Style_Changed(object sender, RoutedEventArgs e)
    {
        if (!_loading)
        {
            ThemeManager.Apply(win95: Win95Radio.IsChecked == true);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
        }
    }
}
