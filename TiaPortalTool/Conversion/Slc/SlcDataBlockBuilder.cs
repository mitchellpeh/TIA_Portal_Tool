using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace TiaPortalTool.Conversion.Slc;

public sealed class SlcDataConversion
{
    public List<TiaDataBlock> DataBlocks { get; } = new();
    public List<TiaDataType> DataTypes { get; } = new();
    public List<AddressMapping> AddressMap { get; } = new();
    public List<string> Warnings { get; } = new();
}

/// <summary>
/// Builds the TIA data blocks that mirror the SLC data table:
/// <list type="bullet">
/// <item>one standard-access global DB per data file (B3 → DB3 "B3", N7 → DB7 "N7", ...), member per element,
/// in the SLC memory layout so word, range and indirect operations still line up with the named bits;</item>
/// <item>I1 (DB1) and O0 (DB1000) holding the I/O image as "zz" tags for the user to map to real hardware;</item>
/// <item>TIME_SP (DB1001) holding a time setpoint object for each HMI-entered timer preset.</item>
/// </list>
/// </summary>
public static class SlcDataBlockBuilder
{
    public const string TimeSetpointType = "UDT_TIME_SP";
    public const string ControlType = "SLC_CONTROL";
    public const string DataFilesGroup = "Data Files";
    public const string HalGroup = "HAL";
    public const string SetpointGroup = "Setpoints";
    public const int InputDbNumber = 1;
    public const int OutputDbNumber = 1000;
    public const int SetpointDbNumber = 1001;
    public const string HardwarePrefix = "zz";

    // Bits of a 16-bit SLC word in the order they're declared in a standard-access DB. Siemens stores a WORD
    // high byte first, so bits 8-15 come first; that way %DBn.DBWx reads the same value as the SLC word.
    private static readonly int[] BitDeclarationOrder = { 8, 9, 10, 11, 12, 13, 14, 15, 0, 1, 2, 3, 4, 5, 6, 7 };

    public static SlcDataConversion Build(SlcProgramAnalysis analysis)
    {
        var result = new SlcDataConversion();
        var program = analysis.Program;

        foreach (var file in program.DataFiles.Values)
        {
            switch (file.Kind)
            {
                case SlcFileKind.Bit:
                    result.DataBlocks.Add(BuildBitFile(file, program.Symbols, result.AddressMap));
                    break;
                case SlcFileKind.Integer:
                case SlcFileKind.Long:
                case SlcFileKind.Status:
                    result.DataBlocks.Add(BuildWordFile(file, program.Symbols, result.AddressMap));
                    break;
                case SlcFileKind.Float:
                    result.DataBlocks.Add(BuildFloatFile(file, program.Symbols, result.AddressMap));
                    break;
                case SlcFileKind.Timer:
                    result.DataBlocks.Add(BuildTimerFile(file, analysis, result.AddressMap));
                    break;
                case SlcFileKind.Counter:
                case SlcFileKind.Control:
                    result.DataBlocks.Add(BuildStructureFile(file, program.Symbols, result.AddressMap));
                    break;
                case SlcFileKind.String:
                    result.DataBlocks.Add(BuildStringFile(file, program.Symbols, result.AddressMap));
                    break;
                default:
                    result.Warnings.Add($"Data file {file.Name} ({file.Type} files) isn't converted yet. Logic that uses it will need manual work.");
                    break;
            }
        }

        if (program.DataFiles.Values.Any(f => f.Kind == SlcFileKind.Control))
        {
            result.DataTypes.Add(BuildControlType());
        }

        result.DataBlocks.Add(BuildIoBlock("I", analysis, result.AddressMap, result.Warnings));
        result.DataBlocks.Add(BuildIoBlock("O", analysis, result.AddressMap, result.Warnings));

        if (analysis.Setpoints.Count > 0)
        {
            result.DataTypes.Add(BuildTimeSetpointType());
            result.DataBlocks.Add(BuildSetpointBlock(analysis, result.AddressMap));
        }

        foreach (var reference in analysis.References.Where(r => r.FileType is "M" or "G").Select(r => r.ToString()).Distinct())
        {
            result.Warnings.Add($"{reference} is in a module's M0/M1/G file (DeviceNet/specialty module data). Map it to a zz tag by hand.");
        }

        return result;
    }

    // ---- Data files ----

    private static TiaDataBlock NewDataFileBlock(SlcDataFile file, string comment) => new()
    {
        Name = file.Name,
        Number = file.Number,
        Group = DataFilesGroup,
        Comment = comment,
        StandardAccess = true,
        Retain = file.Kind != SlcFileKind.Status
    };

    private static TiaDataBlock BuildBitFile(SlcDataFile file, SlcSymbolTable symbols, List<AddressMapping> map)
    {
        var block = NewDataFileBlock(file, $"SLC bit file {file.Name}. Bits are declared 8-15 then 0-7 in each word so that "
                                           + $"%DB{file.Number}.DBW(2 x word) equals the SLC word {file.Name}:word.");
        var names = new MemberNamer();
        for (var word = 0; word < file.Words.Count; word++)
        {
            var wordAddress = Address($"{file.Name}:{word}");
            var wordSymbol = symbols.Find(wordAddress);
            map.Add(new AddressMapping
            {
                SlcAddress = wordAddress.ToString(), Block = block.Name, DataType = "Word", ByteOffset = word * 2,
                Symbol = wordSymbol?.Symbol ?? string.Empty, Description = wordSymbol?.Description ?? string.Empty
            });

            foreach (var bit in BitDeclarationOrder)
            {
                var address = Address($"{file.Name}:{word}/{bit}");
                var symbol = symbols.Find(address);
                var name = names.Reserve(symbol?.Symbol, $"{word}/{bit}");
                var value = (file.Words[word] >> bit & 1) == 1;
                block.Members.Add(new TiaMember(name, "Bool", Comment(address, symbol, name), value ? "true" : "false"));
                map.Add(Mapping(address, block, name, "Bool", word * 2 + (bit >= 8 ? 0 : 1), bit % 8, symbol));
            }
        }

        block.SizeBytes = file.Words.Count * 2;
        return block;
    }

    private static TiaDataBlock BuildWordFile(SlcDataFile file, SlcSymbolTable symbols, List<AddressMapping> map)
    {
        var isLong = file.Kind == SlcFileKind.Long;
        var dataType = isLong ? "DInt" : "Int";
        var size = isLong ? 4 : 2;
        var block = NewDataFileBlock(file, file.Kind == SlcFileKind.Status
            ? "SLC status file S2, kept for reference. Status bits the logic uses are mapped to TIA equivalents by the converter."
            : $"SLC {(isLong ? "long integer" : "integer")} file {file.Name}. Bits of a word are reached with a slice, e.g. \"{file.Name}\".word.%X3.");
        if (file.Kind == SlcFileKind.Status)
        {
            block.Name = "S2";
            block.Number = 2;
        }

        var names = new MemberNamer();
        for (var word = 0; word < file.Words.Count; word++)
        {
            var address = Address($"{file.Name}:{word}");
            var symbol = symbols.Find(address);
            var name = names.Reserve(symbol?.Symbol, $"{word}");
            var comment = Comment(address, symbol, name) + BitSymbolsNote(file.Name, word, symbols);
            block.Members.Add(new TiaMember(name, dataType, comment, file.Words[word].ToString(CultureInfo.InvariantCulture)));
            map.Add(Mapping(address, block, name, dataType, word * size, -1, symbol));

            // Bits of a word are reached with a slice (.%Xn), which counts from the least significant bit like the SLC.
            if (!isLong)
            {
                for (var bit = 0; bit < 16; bit++)
                {
                    var bitAddress = Address($"{file.Name}:{word}/{bit}");
                    var bitSymbol = symbols.Find(bitAddress);
                    if (bitSymbol is not null)
                    {
                        map.Add(Mapping(bitAddress, block, $"{name}.%X{bit}", "Bool", word * 2 + (bit >= 8 ? 0 : 1), bit % 8, bitSymbol));
                    }
                }
            }
        }

        if (file.Kind == SlcFileKind.Status)
        {
            // After the status words so their layout is unchanged. Set by the startup OB, cleared at the end of Main.
            block.Members.Add(new TiaMember(FirstScanMember, "Bool", "Replaces S:1/15 (first pass): true during the first scan after startup", "false"));
            block.Members.Add(new TiaMember(AlwaysTrueMember, "Bool", "Always TRUE (also set by the startup OB). TIA needs a contact in front of a timer or counter "
                                                                         + "that the SLC ran unconditionally.", "true"));
        }

        block.SizeBytes = file.Words.Count * size;
        return block;
    }

    public const string FirstScanMember = "FirstScan";
    public const string AlwaysTrueMember = "AlwaysTrue";

    private static TiaDataBlock BuildFloatFile(SlcDataFile file, SlcSymbolTable symbols, List<AddressMapping> map)
    {
        var block = NewDataFileBlock(file, $"SLC floating point file {file.Name}.");
        var names = new MemberNamer();
        for (var element = 0; element < file.Reals.Count; element++)
        {
            var address = Address($"{file.Name}:{element}");
            var symbol = symbols.Find(address);
            var name = names.Reserve(symbol?.Symbol, $"{element}");
            block.Members.Add(new TiaMember(name, "Real", Comment(address, symbol, name), FormatReal(file.Reals[element])));
            map.Add(Mapping(address, block, name, "Real", element * 4, -1, symbol));
        }

        block.SizeBytes = file.Reals.Count * 4;
        return block;
    }

    private static TiaDataBlock BuildTimerFile(SlcDataFile file, SlcProgramAnalysis analysis, List<AddressMapping> map)
    {
        var block = NewDataFileBlock(file, $"SLC timer file {file.Name} as IEC timers. .DN = .Q, .EN = .IN, .TT = .IN AND NOT .Q, "
                                           + ".PRE/.ACC = .PT/.ET (TIME, converted from the SLC timebase).");
        var names = new MemberNamer();
        for (var element = 0; element < file.Structures.Count; element++)
        {
            var address = Address($"{file.Name}:{element}");
            var symbol = analysis.Program.Symbols.Find(address);
            var name = names.Reserve(symbol?.Symbol, $"{element}");
            var comment = Comment(address, symbol, name) + TimerNote(analysis, address, file.Structures[element].Preset);
            block.Members.Add(new TiaMember(name, "IEC_TIMER", comment) { Version = "1.0" });
            map.Add(Mapping(address, block, name, "IEC_TIMER", -1, -1, symbol));
        }

        block.SizeBytes = file.Structures.Count * 16;
        return block;
    }

    private static string TimerNote(SlcProgramAnalysis analysis, SlcAddress timer, int dataTablePreset)
    {
        if (!analysis.Timers.TryGetValue(timer, out var usage))
        {
            return " | not used in logic";
        }

        var timebase = usage.Timebase is { } tb ? $"{tb.ToString(CultureInfo.InvariantCulture)} s" : "mixed timebases";
        var preset = usage.Preset switch
        {
            PresetSource.Setpoint => $"preset from HMI setpoint {string.Join(", ", usage.PresetWords)}",
            PresetSource.Calculated => $"preset calculated by logic from {(usage.PresetWords.Count > 0 ? string.Join(", ", usage.PresetWords) : "an expression")}",
            PresetSource.ConstantMove => "preset set by logic to constants",
            _ => usage.Timebase is { } b && int.TryParse(usage.InstructionPresets.FirstOrDefault(), out var p)
                ? $"preset {p} = {FormatTime(p * b)}"
                : $"preset {string.Join("/", usage.InstructionPresets)}"
        };
        return $" | {string.Join("/", usage.Instructions)}, timebase {timebase}, {preset}";
    }

    private static TiaDataBlock BuildStructureFile(SlcDataFile file, SlcSymbolTable symbols, List<AddressMapping> map)
    {
        var isCounter = file.Kind == SlcFileKind.Counter;
        var dataType = isCounter ? "IEC_COUNTER" : $"\"{ControlType}\"";
        var block = NewDataFileBlock(file, isCounter
            ? $"SLC counter file {file.Name} as IEC counters (CTUD). .ACC = .CV, .PRE = .PV, .DN = .QU."
            : $"SLC control file {file.Name} (sequencers, shift registers, FIFO/LIFO).");
        var names = new MemberNamer();
        for (var element = 0; element < file.Structures.Count; element++)
        {
            var address = Address($"{file.Name}:{element}");
            var symbol = symbols.Find(address);
            var name = names.Reserve(symbol?.Symbol, $"{element}");
            var member = new TiaMember(name, dataType, Comment(address, symbol, name));
            if (isCounter)
            {
                member.Version = "1.0";
            }
            else
            {
                member.SubStartValues["LEN"] = file.Structures[element].Preset.ToString(CultureInfo.InvariantCulture);
                member.SubStartValues["POS"] = file.Structures[element].Accum.ToString(CultureInfo.InvariantCulture);
            }

            block.Members.Add(member);
            map.Add(Mapping(address, block, name, isCounter ? "IEC_COUNTER" : ControlType, -1, -1, symbol));
        }

        block.SizeBytes = file.Structures.Count * 6;
        return block;
    }

    private static TiaDataBlock BuildStringFile(SlcDataFile file, SlcSymbolTable symbols, List<AddressMapping> map)
    {
        var block = NewDataFileBlock(file, $"SLC string file {file.Name}.");
        var names = new MemberNamer();
        for (var element = 0; element < file.Strings.Count; element++)
        {
            var address = Address($"{file.Name}:{element}");
            var symbol = symbols.Find(address);
            var name = names.Reserve(symbol?.Symbol, $"{element}");
            block.Members.Add(new TiaMember(name, "String[82]", Comment(address, symbol, name), "'" + file.Strings[element].Replace("'", "''") + "'"));
            map.Add(Mapping(address, block, name, "String[82]", -1, -1, symbol));
        }

        block.SizeBytes = file.Strings.Count * 84;
        return block;
    }

    // ---- I/O (hardware abstraction layer) ----

    /// <summary>
    /// The I or O image as zz tags, slot by slot in SLC order. A word that logic or the symbol table uses bit by bit
    /// becomes 16 Bools; any other word becomes an Int.
    /// </summary>
    private static TiaDataBlock BuildIoBlock(string type, SlcProgramAnalysis analysis, List<AddressMapping> map, List<string> warnings)
    {
        var program = analysis.Program;
        var isInput = type == "I";
        var block = new TiaDataBlock
        {
            Name = isInput ? "I1" : "O0",
            Number = isInput ? InputDbNumber : OutputDbNumber,
            Group = HalGroup,
            StandardAccess = true,
            Retain = false,
            Comment = isInput
                ? "SLC input image (I1). Every zz tag here must be written from real inputs or fieldbus data in the HAL input mapping."
                : "SLC output image (O0). Every zz tag here must be copied to real outputs or fieldbus data in the HAL output mapping."
        };

        // Words per slot: the configured image size, stretched to cover anything the logic or symbols use beyond it.
        var wordsPerSlot = new SortedDictionary<int, int>();
        foreach (var slot in program.Slots)
        {
            var count = isInput ? slot.InputWords : slot.OutputWords;
            if (count > 0)
            {
                wordsPerSlot[slot.Slot] = count;
            }
        }

        var used = analysis.References.Concat(program.Symbols.Entries.Select(e => e.Key)).Where(a => a.FileType == type);
        foreach (var address in used)
        {
            var needed = address.Word + 1;
            if (!wordsPerSlot.TryGetValue(address.Element, out var count) || count < needed)
            {
                if (count == 0)
                {
                    warnings.Add($"{address} refers to slot {address.Element}, which has no {(isInput ? "input" : "output")} words in the rack configuration.");
                }

                wordsPerSlot[address.Element] = Math.Max(count, needed);
            }
        }

        var names = new MemberNamer();
        var offset = 0;
        foreach (var pair in wordsPerSlot)
        {
            var slot = program.Slots.FirstOrDefault(s => s.Slot == pair.Key);
            var slotNote = slot is null ? $"slot {pair.Key}" : $"slot {slot.Slot}: {slot.Catalog} {slot.Description}".TrimEnd();
            for (var word = 0; word < pair.Value; word++, offset += 2)
            {
                var wordAddress = Address($"{type}:{pair.Key}.{word}");
                var wordSymbol = program.Symbols.Find(wordAddress);
                if (analysis.BitAccessedWords.Contains(wordAddress))
                {
                    map.Add(new AddressMapping
                    {
                        SlcAddress = wordAddress.ToString(), Block = block.Name, DataType = "Word", ByteOffset = offset,
                        Symbol = wordSymbol?.Symbol ?? string.Empty, Description = wordSymbol?.Description ?? string.Empty
                    });

                    foreach (var bit in BitDeclarationOrder)
                    {
                        var address = Address($"{type}:{pair.Key}.{word}/{bit}");
                        var symbol = program.Symbols.Find(address);
                        var name = names.Reserve(HardwareName(symbol?.Symbol), $"{HardwarePrefix}{type}_{pair.Key}_{word}_{bit}");
                        block.Members.Add(new TiaMember(name, "Bool", Comment(address, symbol, name) + $" [{slotNote}]"));
                        map.Add(Mapping(address, block, name, "Bool", offset + (bit >= 8 ? 0 : 1), bit % 8, symbol));
                    }
                }
                else
                {
                    var name = names.Reserve(HardwareName(wordSymbol?.Symbol), $"{HardwarePrefix}{type}_{pair.Key}_{word}");
                    var comment = Comment(wordAddress, wordSymbol, name) + $" [{slotNote}]"
                                  + (analysis.WordAccessedWords.Contains(wordAddress) ? string.Empty : " (not used in logic)");
                    block.Members.Add(new TiaMember(name, "Int", comment));
                    map.Add(Mapping(wordAddress, block, name, "Int", offset, -1, wordSymbol));
                }
            }
        }

        block.SizeBytes = offset;
        return block;
    }

    private static string? HardwareName(string? symbol) => string.IsNullOrWhiteSpace(symbol) ? null : HardwarePrefix + symbol;

    // ---- Time setpoints ----

    private static TiaDataType BuildTimeSetpointType()
    {
        var type = new TiaDataType
        {
            Name = TimeSetpointType,
            Comment = "Timer setpoint entered from the HMI as hours, minutes and/or seconds. Use any combination; they are added up into Output_Seconds."
        };
        type.Members.Add(new TiaMember("Input_Hours", "Real", "Hours part of the setpoint (from the HMI)", "0.0"));
        type.Members.Add(new TiaMember("Input_Mins", "Real", "Minutes part of the setpoint (from the HMI)", "0.0"));
        type.Members.Add(new TiaMember("Input_Secs", "Real", "Seconds part of the setpoint (from the HMI)", "0.0"));
        type.Members.Add(new TiaMember("Output_Seconds", "Time", "Total setpoint as TIME, calculated by the PLC. Used as the timer preset.", "T#0MS"));
        return type;
    }

    private static TiaDataBlock BuildSetpointBlock(SlcProgramAnalysis analysis, List<AddressMapping> map)
    {
        var block = new TiaDataBlock
        {
            Name = "TIME_SP",
            Number = SetpointDbNumber,
            Group = SetpointGroup,
            StandardAccess = false,
            Retain = true,
            Comment = "Timer setpoints that the SLC program took from HMI-entered words. Enter hours/minutes/seconds; "
                      + "Output_Seconds is the timer preset."
        };

        var names = new MemberNamer();
        foreach (var setpoint in analysis.Setpoints)
        {
            var symbol = analysis.Program.Symbols.Find(setpoint.Word);
            var name = names.Reserve(symbol?.Symbol, $"{setpoint.Word.FileName}_{setpoint.Word.Element}");
            var timers = string.Join(", ", setpoint.Timers.Select(t => t.ToString()));
            var unit = setpoint.SecondsPerCount is { } spc ? $"{spc.ToString(CultureInfo.InvariantCulture)} s per count" : "mixed timebases";
            var comment = $"Replaces {setpoint.Word} ({unit}) as the preset of {timers}"
                          + (setpoint.EnteredAt.Count > 0 ? $"; the SLC HMI entered it at {string.Join(", ", setpoint.EnteredAt)}" : string.Empty)
                          + (symbol is null || symbol.Description.Length == 0 ? string.Empty : " - " + symbol.Description);

            var member = new TiaMember(name, $"\"{TimeSetpointType}\"", comment);
            var file = analysis.Program.DataFiles.TryGetValue(setpoint.Word.FileName, out var f) ? f : null;
            var counts = file is not null && setpoint.Word.Element < file.Words.Count ? file.Words[setpoint.Word.Element] : 0;
            var seconds = setpoint.SecondsPerCount is { } perCount ? counts * perCount : 0m;
            member.SubStartValues["Input_Hours"] = "0.0";
            member.SubStartValues["Input_Mins"] = "0.0";
            member.SubStartValues["Input_Secs"] = FormatReal(seconds.ToString(CultureInfo.InvariantCulture));
            member.SubStartValues["Output_Seconds"] = FormatTime(seconds);
            block.Members.Add(member);

            map.Add(new AddressMapping
            {
                SlcAddress = setpoint.Word + " (as timer preset)", Block = block.Name, Member = name + ".Output_Seconds",
                DataType = "Time", Symbol = symbol?.Symbol ?? string.Empty, Description = symbol?.Description ?? string.Empty
            });
        }

        block.SizeBytes = analysis.Setpoints.Count * 16;
        return block;
    }

    private static TiaDataType BuildControlType()
    {
        var type = new TiaDataType { Name = ControlType, Comment = "SLC 500 control element (R file) for sequencers, shift registers and FIFO/LIFO." };
        foreach (var (bit, text) in new[]
                 {
                     ("EN", "Enable"), ("EU", "Unload enable"), ("DN", "Done"), ("EM", "Empty"),
                     ("ER", "Error"), ("UL", "Unload"), ("IN", "Inhibit"), ("FD", "Found")
                 })
        {
            type.Members.Add(new TiaMember(bit, "Bool", text, "false"));
        }

        type.Members.Add(new TiaMember("LEN", "Int", "Length", "0"));
        type.Members.Add(new TiaMember("POS", "Int", "Position", "0"));
        return type;
    }

    // ---- Helpers ----

    private static SlcAddress Address(string text) => SlcAddress.TryParse(text) ?? throw new InvalidOperationException($"Bad address {text}");

    private static AddressMapping Mapping(SlcAddress address, TiaDataBlock block, string member, string dataType, int byteOffset, int bitOffset, SlcSymbol? symbol) => new()
    {
        SlcAddress = address.ToString(),
        Block = block.Name,
        Member = member,
        DataType = dataType,
        ByteOffset = block.StandardAccess ? byteOffset : -1,
        BitOffset = block.StandardAccess ? bitOffset : -1,
        Symbol = symbol?.Symbol ?? string.Empty,
        Description = symbol?.Description ?? string.Empty
    };

    /// <summary>"B3:0/4 - INPUT DOOR CLOSED", plus the original symbol if it had to be renamed.</summary>
    private static string Comment(SlcAddress address, SlcSymbol? symbol, string memberName)
    {
        var text = new StringBuilder(address.ToString());
        if (symbol is not null && symbol.Description.Length > 0)
        {
            text.Append(" - ").Append(symbol.Description);
        }

        if (symbol is not null && symbol.Symbol.Length > 0 && !memberName.EndsWith(MemberNamer.Sanitize(symbol.Symbol), StringComparison.OrdinalIgnoreCase))
        {
            text.Append(" (SLC symbol ").Append(symbol.Symbol).Append(')');
        }

        return text.ToString();
    }

    /// <summary>Lists bit symbols of an integer word in its comment, since only the word is a member.</summary>
    private static string BitSymbolsNote(string fileName, int word, SlcSymbolTable symbols)
    {
        var parts = new List<string>();
        for (var bit = 0; bit < 16; bit++)
        {
            var symbol = symbols.Find(Address($"{fileName}:{word}/{bit}"));
            if (symbol is not null)
            {
                parts.Add($"/{bit} {(symbol.Symbol.Length > 0 ? symbol.Symbol : symbol.Description)}");
            }
        }

        return parts.Count == 0 ? string.Empty : " | bits: " + string.Join("; ", parts);
    }

    public static string FormatReal(string text)
    {
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            return "0.0";
        }

        var formatted = value.ToString("R", CultureInfo.InvariantCulture);
        if (formatted.IndexOf('E') >= 0)
        {
            var parts = formatted.Split('E');
            return (parts[0].IndexOf('.') >= 0 ? parts[0] : parts[0] + ".0") + "E" + parts[1];
        }

        return formatted.IndexOf('.') >= 0 ? formatted : formatted + ".0";
    }

    /// <summary>Formats seconds as a TIA TIME literal, e.g. T#1H_2M_3S_400MS.</summary>
    public static string FormatTime(decimal seconds)
    {
        var ms = (long)Math.Round(seconds * 1000m);
        if (ms <= 0)
        {
            return "T#0MS";
        }

        var parts = new List<string>();
        void Part(long unitMs, string suffix)
        {
            if (ms >= unitMs)
            {
                parts.Add((ms / unitMs).ToString(CultureInfo.InvariantCulture) + suffix);
                ms %= unitMs;
            }
        }

        Part(86_400_000, "D");
        Part(3_600_000, "H");
        Part(60_000, "M");
        Part(1_000, "S");
        Part(1, "MS");
        return "T#" + string.Join("_", parts);
    }
}

/// <summary>Hands out member names that are valid in TIA Portal and unique (ignoring case) within one block.</summary>
public sealed class MemberNamer
{
    private readonly HashSet<string> _used = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Uses the symbol if there is one and it's free, otherwise the fallback (made unique with a suffix). Fallbacks are
    /// the SLC element address within the file ("0/5" for B3:0/5, "30" for N7:30), which TIA accepts as quoted member
    /// names, so unnamed data reads like the SLC address: "B3"."0/5".
    /// </summary>
    public string Reserve(string? symbol, string fallback)
    {
        if (!string.IsNullOrWhiteSpace(symbol))
        {
            var candidate = Sanitize(symbol!);
            if (_used.Add(candidate))
            {
                return candidate;
            }

            // Same symbol twice in one file: keep it recognisable and add the address.
            candidate = $"{candidate}_{fallback}";
            if (_used.Add(candidate))
            {
                return candidate;
            }
        }

        var name = fallback;
        var unique = name;
        for (var n = 2; !_used.Add(unique); n++)
        {
            unique = $"{name}_{n}";
        }

        return unique;
    }

    public static string Sanitize(string text)
    {
        var name = Regex.Replace(text.Trim(), "[^A-Za-z0-9_]", "_");
        if (name.Length == 0 || char.IsDigit(name[0]))
        {
            name = "_" + name;
        }

        return name.Length > 120 ? name.Substring(0, 120) : name;
    }
}
