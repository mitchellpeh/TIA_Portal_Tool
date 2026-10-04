using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace TiaPortalTool;

/// <summary>Start-up chooser between the TIA Portal tool and the (in-progress) Rockwell converters.</summary>
public partial class LauncherWindow : Window
{
    public LauncherWindow()
    {
        InitializeComponent();

        VersionText.Text = $"TIA Portal Tool v{AppVersions.App}";
        ShowVersion(ImportExportBadge, ImportExportVersionText, AppVersions.ImportExport);
        ShowVersion(SlcBadge, SlcVersionText, AppVersions.SlcConverter);
        ShowVersion(LogixBadge, LogixVersionText, AppVersions.LogixConverter);
        Loaded += (_, _) => ImportExportButton.Focus();
    }

    // Pre-release tools (below 1.0) keep the amber badge; released ones turn green.
    private static void ShowVersion(Border badge, TextBlock text, string version)
    {
        text.Text = "v" + version;
        if (!version.StartsWith("0.", System.StringComparison.Ordinal))
        {
            badge.SetResourceReference(Border.BackgroundProperty, "Badge.Done.Background");
            text.SetResourceReference(TextBlock.ForegroundProperty, "Badge.Done.Foreground");
        }
    }

    private SettingsWindow? _settings;

    // Not modal: the start-up screen stays live behind it, so a style change shows on both at once.
    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (_settings is { IsVisible: true })
        {
            _settings.Activate();
            return;
        }

        _settings = new SettingsWindow { Owner = this };
        _settings.Closed += (_, _) => _settings = null;
        _settings.Show();
    }

    private void ImportExport_Click(object sender, RoutedEventArgs e) => OpenTool(new MainWindow());

    private void SlcConversion_Click(object sender, RoutedEventArgs e) => OpenTool(new SlcConverterWindow());

    /// <summary>
    /// Back to the start-up screen from a tool window. The launcher opens first so the app never has zero windows;
    /// if the tool refuses to close (an operation is still running and the user keeps it), the launcher goes away again.
    /// </summary>
    public static void ReturnFrom(Window tool)
    {
        var launcher = new LauncherWindow();
        launcher.Show();
        tool.Close();
        if (tool.IsVisible)
        {
            launcher.Close();
            return;
        }

        Application.Current.MainWindow = launcher;
    }

    private void OpenTool(Window tool)
    {
        // Show the tool before closing this window so the app never has zero windows open.
        Application.Current.MainWindow = tool;
        tool.Show();
        Close();
    }

    private void LogixConversion_Click(object sender, RoutedEventArgs e) => ShowWorkInProgress("Logix 5000 (.L5X/.L5K) conversion");

    private void ShowWorkInProgress(string toolName) =>
        MessageBox.Show(this, $"{toolName} is a work in progress and isn't available yet.", "Work in progress",
            MessageBoxButton.OK, MessageBoxImage.Information);

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
        }
    }
}
