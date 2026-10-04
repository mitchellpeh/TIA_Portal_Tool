namespace TiaPortalTool.Services.Hmi;

/// <summary>Which Openness object model an HMI uses. They are separate APIs, so each needs its own exporter.</summary>
public enum HmiKind
{
    /// <summary>WinCC Basic / Comfort / RT Advanced (panels and PC stations such as an IPC): Siemens.Engineering.Hmi.HmiTarget.</summary>
    ClassicWinCC,

    /// <summary>WinCC Unified (panels and PC RT): Siemens.Engineering.HmiUnified.HmiSoftware.</summary>
    Unified,
}

/// <summary>An HMI runtime found in the project.</summary>
public sealed class HmiDevice
{
    public HmiDevice(string deviceName, string runtimeName, HmiKind kind, object software)
    {
        DeviceName = deviceName;
        RuntimeName = runtimeName;
        Kind = kind;
        Software = software;
    }

    /// <summary>The station in the device tree, e.g. "PC-System_1" for an IPC or "HMI_1" for a panel.</summary>
    public string DeviceName { get; }

    /// <summary>The runtime device item that carries the HMI software, e.g. "HMI_RT_1".</summary>
    public string RuntimeName { get; }

    public HmiKind Kind { get; }
    public object Software { get; }

    public string Describe() => $"{DeviceName} / {RuntimeName} ({(Kind == HmiKind.ClassicWinCC ? "WinCC Basic/Comfort/Advanced" : "WinCC Unified")})";
}

/// <summary>One exported HMI object, as listed in hmi_manifest.json (the import reads it to know what goes where).</summary>
public sealed class HmiManifestEntry
{
    /// <summary>Screen, ScreenTemplate, ScreenPopup, ScreenSlidein, ScreenGlobalElements, ScreenOverview, TagTable, Connection, TextList, GraphicList, Cycle, VBScript.</summary>
    public string Category { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;

    /// <summary>Folder path inside the HMI ("" = top level), with "/" between folders.</summary>
    public string FolderPath { get; set; } = string.Empty;

    /// <summary>Path of the XML file, relative to the HMI's export folder.</summary>
    public string File { get; set; } = string.Empty;
}

public sealed class HmiManifest
{
    public string DeviceName { get; set; } = string.Empty;
    public string RuntimeName { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public List<HmiManifestEntry> Entries { get; set; } = new();
}

public sealed class HmiExportResult
{
    public List<string> Messages { get; } = new();
    public List<string> Warnings { get; } = new();
    public List<string> ExportFolders { get; } = new();
    public int ExportedCount { get; set; }
}
