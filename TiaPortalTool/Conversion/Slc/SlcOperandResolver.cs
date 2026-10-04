using System.Globalization;

namespace TiaPortalTool.Conversion.Slc;

/// <summary>Thrown when a rung uses something the converter can't translate; the rung becomes a manual-work network.</summary>
public sealed class ManualConversionException : Exception
{
    public ManualConversionException(string message) : base(message)
    {
    }
}

/// <summary>
/// Turns SLC operands into TIA operands using the stage 1 address map: symbols and members for named data,
/// slices for bits of integer words, absolute addresses for whole words of bit files, IEC timer/counter members
/// for T and C elements.
/// </summary>
public sealed class SlcOperandResolver
{
    private readonly Dictionary<string, AddressMapping> _map;
    private readonly Dictionary<string, int> _dbNumbers;
    private readonly Dictionary<SlcAddress, string> _setpointMembers = new();

    public SlcOperandResolver(SlcProgramAnalysis analysis, SlcDataConversion data)
    {
        Analysis = analysis;
        _map = new Dictionary<string, AddressMapping>(StringComparer.Ordinal);
        foreach (var mapping in data.AddressMap)
        {
            _map[mapping.SlcAddress] = mapping;
        }

        _dbNumbers = data.DataBlocks.ToDictionary(b => b.Name, b => b.Number, StringComparer.Ordinal);

        const string suffix = " (as timer preset)";
        foreach (var mapping in data.AddressMap.Where(m => m.SlcAddress.EndsWith(suffix, StringComparison.Ordinal)))
        {
            var word = SlcAddress.TryParse(mapping.SlcAddress.Substring(0, mapping.SlcAddress.Length - suffix.Length));
            if (word is not null)
            {
                _setpointMembers[word] = mapping.Member.Substring(0, mapping.Member.Length - ".Output_Seconds".Length);
            }
        }
    }

    public SlcProgramAnalysis Analysis { get; }

    /// <summary>The TIME_SP member that replaces this word as a timer preset, or null.</summary>
    public string? SetpointMember(SlcAddress word) => _setpointMembers.TryGetValue(word, out var member) ? member : null;

    public IEnumerable<KeyValuePair<SlcAddress, string>> Setpoints => _setpointMembers;

    public int DbNumber(string block) => _dbNumbers.TryGetValue(block, out var number) ? number : throw new ManualConversionException($"no data block {block}");

    /// <summary>Resolves an operand (address or constant) for reading or writing.</summary>
    public TiaOperand Resolve(string text)
    {
        var constant = TryConstant(text);
        if (constant is not null)
        {
            return constant;
        }

        // "#" outside COP/FLL is indexed addressing (offset by S:24); the translator notes it and uses the address as is.
        if (text.StartsWith("#", StringComparison.Ordinal))
        {
            text = text.Substring(1);
        }

        if (text.IndexOf('[') >= 0)
        {
            return Indirect(text);
        }

        var address = SlcAddress.TryParse(text) ?? throw new ManualConversionException($"operand {text} not understood");

        return address.FileType switch
        {
            "T" => Timer(address),
            "C" => Counter(address),
            "R" => Control(address),
            "S" when address.Element == 1 && address.Bit == 15 => TiaOperand.Global("Bool", "S2", SlcDataBlockBuilder.FirstScanMember),
            "M" or "G" => throw new ManualConversionException($"{text} is module (M0/M1/G file) data; map it by hand"),
            _ => DataWord(address, text)
        };
    }

    /// <summary>The timer or counter instance itself, e.g. "T4".T4_1.</summary>
    public TiaOperand Instance(SlcAddress element)
    {
        var mapping = Find(element.WordAddress.ToString());
        return TiaOperand.Global(mapping.DataType, mapping.Block, mapping.Member);
    }

    /// <summary>DB number and byte offset of a word for block moves (COP/FLL), with its element size.</summary>
    public (int Db, int Byte, int ElementBytes) Location(SlcAddress address)
    {
        var word = SlcAddress.TryParse($"{address.FileName}:{address.Element}")
                   ?? throw new ManualConversionException($"bad address {address}");
        var mapping = Find(word.ToString());
        if (mapping.ByteOffset < 0)
        {
            throw new ManualConversionException($"{address} has no fixed memory location (only word, integer and float files do)");
        }

        var size = mapping.DataType is "Real" or "DInt" ? 4 : 2;
        return (DbNumber(mapping.Block), mapping.ByteOffset, size);
    }

    private TiaOperand Timer(SlcAddress address)
    {
        var instance = Instance(address);
        var block = instance.Components[0];
        var member = instance.Components[1];
        return address.Member switch
        {
            null when address.Bit is null => instance,
            "DN" => TiaOperand.Global("Bool", block, member, "Q"),
            "EN" => TiaOperand.Global("Bool", block, member, "IN"),
            "PRE" => TiaOperand.Global("Time", block, member, "PT"),
            "ACC" => TiaOperand.Global("Time", block, member, "ET"),
            _ => throw new ManualConversionException($"timer bit {address} has no IEC equivalent")
        };
    }

    private TiaOperand Counter(SlcAddress address)
    {
        var instance = Instance(address);
        var block = instance.Components[0];
        var member = instance.Components[1];
        return address.Member switch
        {
            null when address.Bit is null => instance,
            "DN" => TiaOperand.Global("Bool", block, member, "QU"),
            "CU" => TiaOperand.Global("Bool", block, member, "CU"),
            "CD" => TiaOperand.Global("Bool", block, member, "CD"),
            "PRE" => TiaOperand.Global("Int", block, member, "PV"),
            "ACC" => TiaOperand.Global("Int", block, member, "CV"),
            _ => throw new ManualConversionException($"counter bit {address} has no IEC equivalent")
        };
    }

    private TiaOperand Control(SlcAddress address)
    {
        var instance = Instance(address);
        if (address.Member is null)
        {
            return instance;
        }

        var type = address.Member is "LEN" or "POS" ? "Int" : "Bool";
        return TiaOperand.Global(type, instance.Components[0], instance.Components[1], address.Member);
    }

    private TiaOperand DataWord(SlcAddress address, string text)
    {
        if (address.Bit is not null)
        {
            // B and I/O bits are members; bits of integer words are slices of the word member.
            if (_map.TryGetValue(address.ToString(), out var bitMapping) && bitMapping.Member.IndexOf(".%X", StringComparison.Ordinal) < 0)
            {
                return TiaOperand.Global("Bool", bitMapping.Block, bitMapping.Member);
            }

            var wordMapping = Find(address.WordAddress.ToString());
            if (wordMapping.Member.Length == 0)
            {
                throw new ManualConversionException($"no member for {text}");
            }

            return TiaOperand.Slice(wordMapping.Block, wordMapping.Member, address.Bit.Value);
        }

        var mapping = Find(address.ToString());
        if (mapping.Member.Length == 0)
        {
            // A whole word of a bit file (or of I/O declared as bits): address it absolutely.
            return TiaOperand.Addressed("Word", DInt(DbNumber(mapping.Block)), DInt(mapping.ByteOffset / 2), null, address.ToString());
        }

        return TiaOperand.Global(mapping.DataType, mapping.Block, mapping.Member);
    }

    private static TiaOperand DInt(int value) => TiaOperand.Literal("DInt", value.ToString(CultureInfo.InvariantCulture));

    // N91:[N7:56]   N[N43:61]:[N43:63]   F90:[N7:55]   B3:70/[N7:200]   N7:[N7:1]/3
    private static readonly System.Text.RegularExpressions.Regex IndirectForm = new(
        @"^(?<type>[A-Z])(?:(?<file>\d+)|\[(?<fileIx>[^\]]+)\]):(?:(?<elem>\d+)|\[(?<elemIx>[^\]]+)\])(?:/(?:(?<bit>\d+)|\[(?<bitIx>[^\]]+)\]))?$",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>An indirect address: the file, element and/or bit number comes from another word.</summary>
    private TiaOperand Indirect(string text)
    {
        var match = IndirectForm.Match(text);
        if (!match.Success)
        {
            throw new ManualConversionException($"indirect address {text} not understood");
        }

        var type = match.Groups["type"].Value;
        if (type is not ("N" or "B" or "F"))
        {
            throw new ManualConversionException($"indirect address into a {type} file");
        }

        TiaOperand Part(string constant, string indirect)
        {
            if (match.Groups[constant].Success)
            {
                return DInt(int.Parse(match.Groups[constant].Value, CultureInfo.InvariantCulture));
            }

            var pointer = Resolve(match.Groups[indirect].Value);
            if (pointer.DataType is not ("Int" or "DInt") || pointer.IsAddressed)
            {
                throw new ManualConversionException($"indirect address {text}: the pointer must be a plain integer word");
            }

            return pointer;
        }

        var file = Part("file", "fileIx");
        var element = Part("elem", "elemIx");
        var hasBit = match.Groups["bit"].Success || match.Groups["bitIx"].Success;
        var bit = hasBit ? Part("bit", "bitIx") : null;
        var dataType = hasBit ? "Bool" : type == "F" ? "Real" : "Word";
        return TiaOperand.Addressed(dataType, file, element, bit, text);
    }

    private AddressMapping Find(string address) =>
        _map.TryGetValue(address, out var mapping) ? mapping : throw new ManualConversionException($"{address} isn't in any converted data file");

    /// <summary>Integer, real and hex ("0FFFFh") constants.</summary>
    public static TiaOperand? TryConstant(string text)
    {
        if (text.EndsWith("h", StringComparison.OrdinalIgnoreCase) && text.Length > 1
            && int.TryParse(text.Substring(0, text.Length - 1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex))
        {
            return TiaOperand.Literal("Word", "16#" + (hex & 0xFFFF).ToString("X4", CultureInfo.InvariantCulture));
        }

        if (long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var integer))
        {
            return integer is >= short.MinValue and <= short.MaxValue
                ? TiaOperand.Literal("Int", integer.ToString(CultureInfo.InvariantCulture))
                : TiaOperand.Literal("DInt", integer.ToString(CultureInfo.InvariantCulture));
        }

        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
        {
            return TiaOperand.Literal("Real", SlcDataBlockBuilder.FormatReal(text));
        }

        return null;
    }
}
