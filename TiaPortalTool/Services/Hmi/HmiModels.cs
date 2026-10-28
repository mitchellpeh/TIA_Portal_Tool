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

    /// <summary>
    /// Hash of the file as exported (see <see cref="HmiXmlHash"/>). The import compares it with the file and with
    /// the object in TIA to tell an edited file from an object that was changed in TIA after the export.
    /// Empty in manifests written before the import existed.
    /// </summary>
    public string Hash { get; set; } = string.Empty;

    /// <summary>
    /// Hash of the file when it was last in step with TIA: the export, or the last import of it. Empty = same as
    /// <see cref="Hash"/>. They differ when TIA stores an imported object differently from the file, and without
    /// this the file would be imported again on every run.
    /// </summary>
    public string FileHash { get; set; } = string.Empty;
}

public sealed class HmiManifest
{
    public string DeviceName { get; set; } = string.Empty;
    public string RuntimeName { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;

    /// <summary>The <see cref="HmiXmlHash"/> version the hashes were made with (manifests without it are version 1).</summary>
    public int HashVersion { get; set; } = 1;

    public List<HmiManifestEntry> Entries { get; set; } = new();
}

public sealed class HmiImportOptions
{
    /// <summary>
    /// Import files even when the object in TIA was changed after the export (or the manifest has no hashes to tell).
    /// Without it, those objects are reported as conflicts and left alone.
    /// </summary>
    public bool Force { get; set; }

    /// <summary>
    /// Asked for each conflict when <see cref="Force"/> is off (the GUI shows a prompt). Null = keep TIA's version and
    /// report the conflict. Called on the worker thread.
    /// </summary>
    public Func<HmiConflict, HmiConflictChoice>? ResolveConflict { get; set; }

    /// <summary>Compile the HMI after importing. The project is saved only if the import and the compile are clean.</summary>
    public bool Compile { get; set; } = true;
}

/// <summary>An object changed both in its file and in TIA since the export.</summary>
public sealed class HmiConflict
{
    public HmiConflict(string label, IReadOnlyList<string> differences, bool hasBaseline)
    {
        Label = label;
        Differences = differences;
        HasBaseline = hasBaseline;
    }

    /// <summary>E.g. "TagTable 'Folder/Table'".</summary>
    public string Label { get; }

    /// <summary>What differs between the file and TIA now (see <see cref="HmiXmlDiff"/>).</summary>
    public IReadOnlyList<string> Differences { get; }

    /// <summary>False when the manifest has no export hash, so there's no telling which side is newer.</summary>
    public bool HasBaseline { get; }
}

public enum HmiConflictChoice
{
    /// <summary>Import the file over TIA's version.</summary>
    Overwrite,

    /// <summary>Leave TIA's version and report the conflict.</summary>
    Keep,

    /// <summary>Stop the whole import; nothing is saved.</summary>
    Cancel,
}

public sealed class HmiImportResult
{
    public List<string> Messages { get; } = new();
    public List<string> Warnings { get; } = new();
    public int ImportedCount { get; set; }
    public int UnchangedCount { get; set; }
    public int ConflictCount { get; set; }
    public int FailedCount { get; set; }
    public bool CompileFailed { get; set; }

    /// <summary>The HMI compiles that had errors, one per HMI device.</summary>
    public List<CompileOutcome> FailedCompiles { get; } = new();

    /// <summary>Manifest entries for the objects imported, hashed as TIA holds them now. Written back only once the project is saved.</summary>
    public List<HmiManifestEntry> ManifestUpdates { get; } = new();

    /// <summary>Manifests to rewrite once the project is saved (see HmiImportService.WriteManifests).</summary>
    public List<(string Path, HmiManifest Manifest)> PendingManifests { get; } = new();

    /// <summary>True when something was imported and nothing failed: the caller may save.</summary>
    public bool CanSave => ImportedCount > 0 && FailedCount == 0 && !CompileFailed;

    /// <summary>Everything imported and the only problem is the compile: the caller may still save if the user agrees.</summary>
    public bool OnlyCompileFailed => ImportedCount > 0 && FailedCount == 0 && CompileFailed;
}

public sealed class HmiExportResult
{
    public List<string> Messages { get; } = new();
    public List<string> Warnings { get; } = new();
    public List<string> ExportFolders { get; } = new();
    public int ExportedCount { get; set; }
}
