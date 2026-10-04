using System.Windows;

namespace TiaPortalTool;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // Before the first window opens, so it starts in the saved theme.
        ThemeManager.ApplySaved();
        base.OnStartup(e);
    }
}
