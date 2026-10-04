namespace TiaPortalTool.Conversion.Slc;

/// <summary>Everything read from an RSLogix 500 ASCII export (.SLC) plus its symbol/description file.</summary>
public sealed class SlcProgram
{
    public string SourcePath { get; set; } = string.Empty;

    /// <summary>Catalog number from the START line, e.g. "1747-L552C,D" or "Bul.1763".</summary>
    public string Processor { get; set; } = string.Empty;

    public string ProcessorDescription { get; set; } = string.Empty;

    public string ProjectName { get; set; } = string.Empty;

    public List<SlcSlot> Slots { get; } = new();

    /// <summary>Ladder (program) files by number, in file order.</summary>
    public SortedDictionary<int, SlcProgramFile> ProgramFiles { get; } = new();

    /// <summary>Data files by name ("N7", "T4"), excluding I/O and function files.</summary>
    public SortedDictionary<string, SlcDataFile> DataFiles { get; } = new(StringComparer.Ordinal);

    /// <summary>I/O image words read from the DATA I:/O: sections, keyed by "I:slot" / "O:slot".</summary>
    public Dictionary<string, List<int>> IoData { get; } = new(StringComparer.Ordinal);

    /// <summary>Function files (MicroLogix HSC0, RTC0, ...) present in the export. Not converted.</summary>
    public List<string> FunctionFiles { get; } = new();

    public SlcSymbolTable Symbols { get; set; } = new();

    /// <summary>True for MicroLogix controllers (Bul.17xx), which differ from the SLC 500 in a few instructions.</summary>
    public bool IsMicroLogix => Processor.StartsWith("Bul.17", StringComparison.OrdinalIgnoreCase);

    public List<string> ParseWarnings { get; } = new();
}

public sealed class SlcSlot
{
    public int Slot { get; set; }
    public string Catalog { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public int InputWords { get; set; }
    public int OutputWords { get; set; }
}

public sealed class SlcProgramFile
{
    public int Number { get; set; }
    public string Name { get; set; } = string.Empty;
    public List<SlcRung> Rungs { get; } = new();
}

public sealed class SlcRung
{
    public int Number { get; set; }

    /// <summary>The mnemonic text from SOR to EOR inclusive.</summary>
    public string Text { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public int SourceLine { get; set; }
}

public enum SlcFileKind
{
    Bit,       // B
    Integer,   // N
    Float,     // F
    Long,      // L
    Timer,     // T
    Counter,   // C
    Control,   // R
    String,    // ST
    Status,    // S
    Other      // A, MG, PD, ... (reported, not converted yet)
}

public sealed class SlcDataFile
{
    public string Type { get; set; } = string.Empty;
    public int Number { get; set; }
    public string Name => Type + Number;

    public SlcFileKind Kind => Type switch
    {
        "B" => SlcFileKind.Bit,
        "N" => SlcFileKind.Integer,
        "F" => SlcFileKind.Float,
        "L" => SlcFileKind.Long,
        "T" => SlcFileKind.Timer,
        "C" => SlcFileKind.Counter,
        "R" => SlcFileKind.Control,
        "ST" => SlcFileKind.String,
        "S" => SlcFileKind.Status,
        _ => SlcFileKind.Other
    };

    /// <summary>B/N/L/S values (one per word or long).</summary>
    public List<long> Words { get; } = new();

    /// <summary>F values, as written in the export.</summary>
    public List<string> Reals { get; } = new();

    /// <summary>T/C/R values: control word, PRE/LEN, ACC/POS.</summary>
    public List<(int Control, int Preset, int Accum)> Structures { get; } = new();

    /// <summary>ST values.</summary>
    public List<string> Strings { get; } = new();

    public int Length => Kind switch
    {
        SlcFileKind.Float => Reals.Count,
        SlcFileKind.Timer or SlcFileKind.Counter or SlcFileKind.Control => Structures.Count,
        SlcFileKind.String => Strings.Count,
        _ => Words.Count
    };
}
