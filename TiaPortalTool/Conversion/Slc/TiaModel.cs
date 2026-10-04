namespace TiaPortalTool.Conversion.Slc;

/// <summary>A global DB to generate.</summary>
public sealed class TiaDataBlock
{
    public string Name { get; set; } = string.Empty;
    public int Number { get; set; }

    /// <summary>Block group path in the project ("Data Files", "HAL"), "/"-separated.</summary>
    public string Group { get; set; } = string.Empty;

    public string Comment { get; set; } = string.Empty;

    /// <summary>
    /// Standard (non-optimized) access keeps the SLC memory layout, so word, range and indirect operations can
    /// address the same memory as the named bits.
    /// </summary>
    public bool StandardAccess { get; set; } = true;

    public bool Retain { get; set; }

    public List<TiaMember> Members { get; } = new();

    /// <summary>Approximate size in bytes, for the retentive-memory check.</summary>
    public int SizeBytes { get; set; }
}

/// <summary>A PLC data type (UDT) to generate.</summary>
public sealed class TiaDataType
{
    public string Name { get; set; } = string.Empty;
    public string Comment { get; set; } = string.Empty;
    public List<TiaMember> Members { get; } = new();
}

public sealed class TiaMember
{
    public TiaMember(string name, string dataType, string? comment = null, string? startValue = null)
    {
        Name = name;
        DataType = dataType;
        Comment = comment;
        StartValue = startValue;
    }

    public string Name { get; }

    /// <summary>"Bool", "Int", "IEC_TIMER", or a quoted UDT name such as "\"UDT_TIME_SP\"".</summary>
    public string DataType { get; }

    public string? Comment { get; }
    public string? StartValue { get; }

    /// <summary>Start values for members of a UDT-typed member, by sub-member name.</summary>
    public Dictionary<string, string> SubStartValues { get; } = new(StringComparer.Ordinal);

    /// <summary>System types such as IEC_TIMER carry a version in the XML.</summary>
    public string? Version { get; set; }
}

/// <summary>Where one SLC address ended up in TIA Portal.</summary>
public sealed class AddressMapping
{
    public string SlcAddress { get; set; } = string.Empty;
    public string Block { get; set; } = string.Empty;
    public string Member { get; set; } = string.Empty;
    public string DataType { get; set; } = string.Empty;

    /// <summary>Byte offset in a standard-access DB (word files only), or -1.</summary>
    public int ByteOffset { get; set; } = -1;

    /// <summary>Bit within that byte for Bool members, or -1.</summary>
    public int BitOffset { get; set; } = -1;

    public string Symbol { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;

    /// <summary>The operand as written in TIA Portal, e.g. "B3".IP_ESTOP_ACTV or %DB3.DBW24.</summary>
    public string TiaOperand => Member.Length == 0 ? Block : $"\"{Block}\".{Member}";

    public string AbsoluteOperand(int dbNumber) => ByteOffset < 0
        ? string.Empty
        : BitOffset >= 0 ? $"%DB{dbNumber}.DBX{ByteOffset}.{BitOffset}" : $"%DB{dbNumber}.DBW{ByteOffset}";
}
