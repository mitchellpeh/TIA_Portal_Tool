using System.IO;
using System.Text.Json;

namespace TiaPortalTool.Services;

public sealed class AppSettings
{
    public string ProjectPath { get; set; } = string.Empty;
    /// <summary>Pre-per-project setting; only read to migrate it onto <see cref="ProjectPath"/>.</summary>
    public string OutputDirectory { get; set; } = string.Empty;

    /// <summary>Export folder override per project. Key: lower-cased full project path.</summary>
    public Dictionary<string, string> ExportFolders { get; set; } = new();

    /// <summary>Last folder used for Import Folder per project. Key: lower-cased full project path.</summary>
    public Dictionary<string, string> ImportFolders { get; set; } = new();

    public bool ExportTags { get; set; } = true;
    public bool ExportBlocks { get; set; } = true;
    public bool CompileBeforeExport { get; set; }
    public bool ExportHmi { get; set; }

    /// <summary>Settings window: the Windows 95 style instead of the modern one.</summary>
    public bool Win95Style { get; set; }
}

public sealed class AppSettingsService
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "TiaPortalTool",
        "settings.json");

    public AppSettings Load()
    {
        if (!File.Exists(SettingsPath))
        {
            return new AppSettings();
        }

        try
        {
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath)) ?? new AppSettings();
        }
        catch (JsonException)
        {
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        var directory = Path.GetDirectoryName(SettingsPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
    }
}
