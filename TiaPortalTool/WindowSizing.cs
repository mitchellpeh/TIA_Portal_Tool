using System.Windows;

namespace TiaPortalTool;

public static class WindowSizing
{
    /// <summary>
    /// Shrinks a window that's bigger than the screen's working area (laptops, high display scaling), so its
    /// bottom edge and buttons stay on screen. The scrolling log areas take up the difference.
    /// </summary>
    public static void FitToScreen(Window window)
    {
        var area = SystemParameters.WorkArea;
        if (window.Height > area.Height)
        {
            window.Height = area.Height;
        }

        if (window.MinHeight > area.Height)
        {
            window.MinHeight = area.Height;
        }

        if (window.Width > area.Width)
        {
            window.Width = area.Width;
        }

        if (window.MinWidth > area.Width)
        {
            window.MinWidth = area.Width;
        }
    }
}
