using System.Reflection;

namespace TiaPortalTool;

/// <summary>
/// Version numbers shown on the launcher (a badge per tool), in each window (bottom left) and in reports.
/// The app version is the release of the whole exe (set in TiaPortalTool.csproj, tagged in git as vX.Y.Z); each tool
/// has its own version, bumped when that tool's behaviour or output changes.
/// </summary>
public static class AppVersions
{
    /// <summary>The whole app, from the assembly version (TiaPortalTool.csproj &lt;Version&gt;).</summary>
    public static string App
    {
        get
        {
            var version = Assembly.GetExecutingAssembly().GetName().Version;
            return version is null ? "?" : $"{version.Major}.{version.Minor}.{version.Build}";
        }
    }

    /// <summary>The TIA Portal Import / Export tool (export to Excel, reimport, Import Folder, diagnostics).</summary>
    public const string ImportExport = "1.1.1";

    /// <summary>The SLC 500 to TIA Portal converter.</summary>
    public const string SlcConverter = Conversion.Slc.SlcConverter.ToolVersion;

    /// <summary>The Logix 5000 to TIA Portal converter (not started; on hold).</summary>
    public const string LogixConverter = "0.0.0";

    public static string Footer(string toolName, string toolVersion) => $"TIA Portal Tool v{App}   ·   {toolName} v{toolVersion}";
}
