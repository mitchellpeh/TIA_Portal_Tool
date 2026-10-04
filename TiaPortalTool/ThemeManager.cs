using System.Windows;
using System.Windows.Media;
using TiaPortalTool.Services;

namespace TiaPortalTool;

/// <summary>
/// The app's look: Modern (dark slate and blue) or Windows 95. Theme.xaml merges one palette (colours) and one shape
/// set (corners, fonts, control templates); this swaps them. Everything in XAML uses DynamicResource, so open windows
/// change at once. Code that sets colours uses <see cref="Brush"/>/<see cref="Color"/> or SetResourceReference with the
/// same keys.
/// </summary>
public static class ThemeManager
{
    public static bool Win95 { get; private set; }

    /// <summary>Applies the style saved in the settings file; called once at start-up.</summary>
    public static void ApplySaved() => Apply(new AppSettingsService().Load().Win95Style, save: false);

    public static void Apply(bool win95, bool save = true)
    {
        Win95 = win95;
        var name = win95 ? "Win95" : "Modern";
        // App.xaml merges [0] palette, [1] shapes, [2] Theme.xaml straight into the application's resources; changes
        // to that collection reach every open window's DynamicResources. (Merged into a dictionary loaded by Source,
        // as before, the change never reached open windows.)
        var merged = Application.Current.Resources.MergedDictionaries;
        merged[0] = Load($"Themes/Palette.{name}.xaml");
        merged[1] = Load($"Themes/Shapes.{name}.xaml");

        if (save)
        {
            var service = new AppSettingsService();
            var settings = service.Load();
            settings.Win95Style = win95;
            service.Save(settings);
        }
    }

    public static Brush Brush(string key) => (Brush)Application.Current.FindResource(key);

    public static Color Color(string key) => ((SolidColorBrush)Brush(key)).Color;

    private static ResourceDictionary Load(string path) =>
        new() { Source = new Uri($"pack://application:,,,/{path}", UriKind.Absolute) };
}
