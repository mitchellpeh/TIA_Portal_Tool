namespace TiaPortalTool.Services;

public sealed class TagRowData
{
    public string Name { get; set; } = string.Empty;
    public string DataType { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public Dictionary<string, string> Comments { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class TagTableData
{
    public string TableName { get; set; } = string.Empty;
    public string GroupPath { get; set; } = string.Empty;
    public List<string> Languages { get; set; } = new();
    public List<TagRowData> Rows { get; set; } = new();
}

public sealed class WorkbookManifestEntry
{
    public string WorkbookPath { get; set; } = string.Empty;
    public string SheetName { get; set; } = string.Empty;

    /// <summary>"Tag" | "BlockEditable" | "BlockReadOnly"</summary>
    public string Kind { get; set; } = string.Empty;

    public string? TagTableName { get; set; }
    public string? TagGroupPath { get; set; }

    public string? BlockName { get; set; }
    public string? BlockGroupPath { get; set; }
    public string? XmlPath { get; set; }
    public int RungCount { get; set; }

    public List<string> Languages { get; set; } = new();
}

public sealed class WorkbookExportResult
{
    public IReadOnlyList<string> WorkbookPaths { get; set; } = Array.Empty<string>();
    public IReadOnlyList<string> Messages { get; set; } = Array.Empty<string>();
    public IReadOnlyList<string> Warnings { get; set; } = Array.Empty<string>();
}

public sealed class WorkbookReimportResult
{
    public bool Success { get; set; }

    /// <summary>Everything applied and the only problem is the compile: the caller may still save if the user agrees.</summary>
    public bool OnlyCompileFailed => !Success && !ImportFailed && Compile is { Clean: false };

    public bool ImportFailed { get; set; }

    /// <summary>The PLC compile after the reimport; null when it didn't run.</summary>
    public CompileOutcome? Compile { get; set; }

    /// <summary>Names of the blocks reimported.</summary>
    public IReadOnlyCollection<string> TouchedBlocks { get; set; } = Array.Empty<string>();

    public IReadOnlyList<string> Messages { get; set; } = Array.Empty<string>();
}
