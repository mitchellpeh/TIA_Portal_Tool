using System.Globalization;

namespace TiaPortalTool.Conversion.Slc;

/// <summary>How a timer's preset gets its value.</summary>
public enum PresetSource
{
    /// <summary>Only the constant in the timer instruction.</summary>
    Constant,

    /// <summary>Logic moves a constant into .PRE.</summary>
    ConstantMove,

    /// <summary>Logic moves a word into .PRE and nothing in the program writes that word: an HMI/SCADA setpoint.</summary>
    Setpoint,

    /// <summary>Logic moves a word that the program itself calculates, or calculates .PRE directly.</summary>
    Calculated
}

public sealed class TimerUsage
{
    public SlcAddress Timer { get; set; } = null!;
    public HashSet<string> Instructions { get; } = new(StringComparer.Ordinal);

    /// <summary>Timebases seen in TON/TOF/RTO instructions, in seconds ("1.0", "0.01", "0.001").</summary>
    public HashSet<string> Timebases { get; } = new(StringComparer.Ordinal);

    public HashSet<string> InstructionPresets { get; } = new(StringComparer.Ordinal);
    public PresetSource Preset { get; set; } = PresetSource.Constant;

    /// <summary>The words moved into .PRE, for setpoint and calculated presets.</summary>
    public HashSet<SlcAddress> PresetWords { get; } = new();

    /// <summary>The timebase if every instruction agrees on one; otherwise null.</summary>
    public decimal? Timebase => Timebases.Count == 1 ? decimal.Parse(Timebases.First(), CultureInfo.InvariantCulture) : null;
}

/// <summary>One HMI-entered timer preset word that becomes a time setpoint object.</summary>
public sealed class TimeSetpoint
{
    public SlcAddress Word { get; set; } = null!;
    public List<SlcAddress> Timers { get; } = new();

    /// <summary>Seconds per count of the word (the timers' timebase), or null if the timers disagree.</summary>
    public decimal? SecondsPerCount { get; set; }

    /// <summary>
    /// For a staged setpoint, the words the operator actually enters. Logic copies them into <see cref="Word"/>
    /// (usually after a LIM range check). Empty when the HMI writes <see cref="Word"/> directly.
    /// </summary>
    public List<SlcAddress> EnteredAt { get; } = new();

    /// <summary>Why it's a timer, or why it's ambiguous, for the report.</summary>
    public string Note { get; set; } = string.Empty;
}

/// <summary>
/// Cross-references the whole program: what every rung reads and writes, how timers are used, and which preset
/// words are entered from outside the PLC.
/// </summary>
public sealed class SlcProgramAnalysis
{
    public SlcProgram Program { get; }

    public Dictionary<(int File, int Rung), TokenizedRung> Rungs { get; } = new();

    public Dictionary<string, int> InstructionCounts { get; } = new(StringComparer.Ordinal);

    /// <summary>Every address any rung refers to, with bits and members.</summary>
    public HashSet<SlcAddress> References { get; } = new();

    /// <summary>Word addresses with at least one bit-level reference (in logic or in the symbol table).</summary>
    public HashSet<SlcAddress> BitAccessedWords { get; } = new();

    /// <summary>Word addresses referenced as a whole word by logic.</summary>
    public HashSet<SlcAddress> WordAccessedWords { get; } = new();

    /// <summary>Word addresses (or elements) written by logic, including COP/FLL destination ranges.</summary>
    public HashSet<SlcAddress> WrittenWords { get; } = new();

    /// <summary>Bits written one at a time (OTE/OTL/OTU and the like).</summary>
    public HashSet<SlcAddress> WrittenBits { get; } = new();

    /// <summary>Words and bits the logic reads (operands it doesn't write), including COP source ranges.</summary>
    public HashSet<SlcAddress> ReadAddresses { get; } = new();

    /// <summary>Data files read or written through indirect addresses ("N91"), or "N*" when the file number itself is indirect.</summary>
    public HashSet<string> IndirectFiles { get; } = new(StringComparer.Ordinal);

    /// <summary>Words filled from a module's M0/M1/G or I/O data (COP/MOV), with the slot and the source address.</summary>
    public Dictionary<SlcAddress, (int Slot, string Source)> CopiedFromModule { get; } = new();

    /// <summary>Words copied to a module's M0/M1/G or O data, with the slot and the destination address.</summary>
    public Dictionary<SlcAddress, (int Slot, string Destination)> CopiedToModule { get; } = new();

    /// <summary>For each word, the file:rung places the logic uses it (first few).</summary>
    public Dictionary<SlcAddress, List<string>> UsedAt { get; } = new();

    /// <summary>For each written word, the source operand of every plain MOV into it (null for any other kind of write).</summary>
    private readonly Dictionary<SlcAddress, List<string?>> _writers = new();

    public Dictionary<SlcAddress, TimerUsage> Timers { get; } = new();

    public List<TimeSetpoint> Setpoints { get; } = new();

    /// <summary>Operands with indirect addressing ("N7:[N7:10]"), with where they appear.</summary>
    public List<string> IndirectReferences { get; } = new();

    /// <summary>Rungs that couldn't be split into instructions.</summary>
    public List<string> RungErrors { get; } = new();

    public List<string> Warnings { get; } = new();

    private SlcProgramAnalysis(SlcProgram program) => Program = program;

    public static SlcProgramAnalysis Analyze(SlcProgram program)
    {
        var analysis = new SlcProgramAnalysis(program);
        analysis.Run();
        return analysis;
    }

    private void Run()
    {
        var presetMoves = new List<(SlcAddress Timer, string Source, string Mnemonic)>();

        foreach (var file in Program.ProgramFiles.Values)
        {
            foreach (var rung in file.Rungs)
            {
                var tokenized = RungTokenizer.Tokenize(rung.Text, Program.IsMicroLogix);
                Rungs[(file.Number, rung.Number)] = tokenized;
                if (tokenized.Error is not null)
                {
                    RungErrors.Add($"File {file.Number} rung {rung.Number}: {tokenized.Error}. Text: {rung.Text}");
                }

                foreach (var element in tokenized.Elements.Where(e => !e.IsBranch))
                {
                    InstructionCounts[element.Mnemonic] = InstructionCounts.TryGetValue(element.Mnemonic, out var n) ? n + 1 : 1;
                    AnalyzeInstruction(element, file.Number, rung.Number, presetMoves);
                }
            }
        }

        foreach (var entry in Program.Symbols.Entries)
        {
            if (entry.Key.Bit is not null)
            {
                BitAccessedWords.Add(entry.Key.WordAddress);
            }
        }

        ClassifyPresets(presetMoves);
    }

    private void AnalyzeInstruction(RungElement element, int file, int rung, List<(SlcAddress, string, string)> presetMoves)
    {
        var info = element.Info!;
        for (var i = 0; i < element.Operands.Count; i++)
        {
            var operand = element.Operands[i];
            if (operand.IndexOf('[') >= 0)
            {
                IndirectReferences.Add($"{operand} in {element.Mnemonic}, file {file} rung {rung}");
                var indirectFile = System.Text.RegularExpressions.Regex.Match(operand, @"^#?([A-Z]+)(\d*)[:\[]");
                if (indirectFile.Success)
                {
                    IndirectFiles.Add(indirectFile.Groups[1].Value + (indirectFile.Groups[2].Value.Length > 0 ? indirectFile.Groups[2].Value : "*"));
                }

                continue;
            }

            var address = SlcAddress.TryParse(operand);
            if (address is null)
            {
                continue;
            }

            References.Add(address);
            NoteUse(address.WordAddress, file, rung);
            if (address.Bit is not null)
            {
                BitAccessedWords.Add(address.WordAddress);
            }
            else if (address.Member is null)
            {
                WordAccessedWords.Add(address.WordAddress);
            }

            if (info.Writes.Contains(i))
            {
                MarkWritten(element, address);
                if (address.Bit is not null)
                {
                    WrittenBits.Add(address);
                }
            }
            else
            {
                MarkRead(element, address, i);
            }
        }

        NoteModuleCopies(element);

        if (element.Mnemonic is "TON" or "TOF" or "RTO" && SlcAddress.TryParse(element.Operands[0]) is { } timer)
        {
            var usage = GetTimer(timer.WordAddress);
            usage.Instructions.Add(element.Mnemonic);
            usage.Timebases.Add(NormalizeTimebase(element.Operands[1]));
            usage.InstructionPresets.Add(element.Operands[2]);
        }

        // Anything written into a timer's .PRE decides where its preset comes from.
        foreach (var index in info.Writes)
        {
            if (index < element.Operands.Count && SlcAddress.TryParse(element.Operands[index]) is { FileType: "T", Member: "PRE" } target)
            {
                var source = element.Mnemonic == "MOV" ? element.Operands[0] : string.Empty;
                presetMoves.Add((target.WordAddress, source, element.Mnemonic));
            }
        }
    }

    private void NoteUse(SlcAddress word, int file, int rung)
    {
        if (!UsedAt.TryGetValue(word, out var places))
        {
            places = new List<string>();
            UsedAt[word] = places;
        }

        var place = $"{file}:{rung}";
        if (places.Count < 8 && !places.Contains(place))
        {
            places.Add(place);
        }
    }

    /// <summary>Operands the instruction reads; a COP/CPW source file address reads its whole range.</summary>
    private void MarkRead(RungElement element, SlcAddress address, int index)
    {
        if (element.Mnemonic is "COP" or "CPW" && index == 0 && address.IsFileReference && !address.IsIo
            && int.TryParse(element.Operands[2], NumberStyles.None, CultureInfo.InvariantCulture, out var length))
        {
            for (var n = 0; n < length; n++)
            {
                ReadAddresses.Add(SlcAddress.TryParse($"{address.FileName}:{address.Element + n}")!);
            }

            return;
        }

        ReadAddresses.Add(address.Bit is null ? address.WordAddress : address);
    }

    /// <summary>COP/MOV between a module's data (M0/M1/G files, or the I/O image of a slot) and a data file.</summary>
    private void NoteModuleCopies(RungElement element)
    {
        if (element.Mnemonic is not ("COP" or "CPW" or "MOV") || element.Operands.Count < 2)
        {
            return;
        }

        var source = SlcAddress.TryParse(element.Operands[0]);
        var destination = SlcAddress.TryParse(element.Operands[1]);
        if (source is null || destination is null)
        {
            return;
        }

        var length = element.Mnemonic == "MOV" ? 1
            : int.TryParse(element.Operands[2], NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0;
        static bool IsModule(SlcAddress a) => a.FileType is "M" or "G" or "I" or "O";
        static string Offset(SlcAddress a, int i) => a.FileType is "M" or "G" or "I" or "O"
            ? $"{a.FileType}{(a.FileType is "I" or "O" ? string.Empty : a.FileNumber.ToString(CultureInfo.InvariantCulture))}:{a.Element}.{a.Word + i}"
            : $"{a.FileName}:{a.Element + i}";

        if (IsModule(source) && !IsModule(destination))
        {
            for (var i = 0; i < length; i++)
            {
                CopiedFromModule[SlcAddress.TryParse($"{destination.FileName}:{destination.Element + i}")!] = (source.Element, Offset(source, i));
            }
        }
        else if (IsModule(destination) && !IsModule(source))
        {
            for (var i = 0; i < length; i++)
            {
                CopiedToModule[SlcAddress.TryParse($"{source.FileName}:{source.Element + i}")!] = (destination.Element, Offset(destination, i));
            }
        }
    }

    private void MarkWritten(RungElement element, SlcAddress address)
    {
        var word = address.WordAddress;
        if (element.Mnemonic is "COP" or "FLL" or "CPW" && address.IsFileReference && !address.IsIo
            && int.TryParse(element.Operands[2], NumberStyles.None, CultureInfo.InvariantCulture, out var length))
        {
            // Remember each element's source for COP/CPW, so a block copy from module data (an HMI or SCADA link)
            // still counts as externally entered.
            var source = element.Mnemonic is "COP" or "CPW" ? SlcAddress.TryParse(element.Operands[0]) : null;
            for (var n = 0; n < length; n++)
            {
                string? sourceText = source is null ? null
                    : source.FileType is "I" or "O" or "M" or "G"
                        ? $"{source.FileType}{source.FileNumber}:{source.Element}.{source.Word + n}"
                        : $"{source.FileName}:{source.Element + n}";
                AddWriter(SlcAddress.TryParse($"{word.FileName}:{word.Element + n}")!, sourceText);
            }

            return;
        }

        AddWriter(word, element.Mnemonic == "MOV" && address.Bit is null && address.Member is null ? element.Operands[0] : null);
    }

    private void AddWriter(SlcAddress word, string? movSource)
    {
        WrittenWords.Add(word);
        if (!_writers.TryGetValue(word, out var list))
        {
            list = new List<string?>();
            _writers[word] = list;
        }

        list.Add(movSource);
    }

    /// <summary>
    /// The words an operator enters for <paramref name="word"/>: the word itself if nothing in the program writes
    /// it, or, if every write is a plain MOV from such entry words (a "staged" setpoint, e.g. LIM 0 N101:25 999
    /// MOV N101:25 N7:0), those words. Returns null if logic calculates the value.
    /// </summary>
    private List<SlcAddress>? FindEntryWords(SlcAddress word, int depth = 0)
    {
        if (word.Member is not null || word.Bit is not null || word.FileType is not ("N" or "L"))
        {
            return null;
        }

        if (!_writers.TryGetValue(word, out var writes))
        {
            return new List<SlcAddress> { word };
        }

        if (depth >= 3)
        {
            return null;
        }

        var entries = new List<SlcAddress>();
        foreach (var source in writes)
        {
            // Moving a constant in (clearing or defaulting the value) doesn't make it a calculated value.
            if (source is not null && IsConstant(source))
            {
                continue;
            }

            var sourceAddress = source is null ? null : SlcAddress.TryParse(source);

            // Copied in from module/input data: this word is where the HMI/SCADA value lands.
            if (sourceAddress?.FileType is "I" or "M" or "G")
            {
                if (!entries.Contains(word))
                {
                    entries.Add(word);
                }

                continue;
            }

            var sourceEntries = sourceAddress is null ? null : FindEntryWords(sourceAddress.WordAddress, depth + 1);
            if (sourceEntries is null)
            {
                return null;
            }

            entries.AddRange(sourceEntries.Where(e => !entries.Contains(e)));
        }

        return entries.Count > 0 ? entries : null;
    }

    private TimerUsage GetTimer(SlcAddress timer)
    {
        if (!Timers.TryGetValue(timer, out var usage))
        {
            usage = new TimerUsage { Timer = timer };
            Timers[timer] = usage;
        }

        return usage;
    }

    private void ClassifyPresets(List<(SlcAddress Timer, string Source, string Mnemonic)> presetMoves)
    {
        var setpoints = new Dictionary<SlcAddress, TimeSetpoint>();

        foreach (var (timer, source, mnemonic) in presetMoves)
        {
            var usage = GetTimer(timer);
            var sourceAddress = SlcAddress.TryParse(source);

            PresetSource kind;
            List<SlcAddress>? entryWords;
            if (mnemonic != "MOV")
            {
                kind = PresetSource.Calculated;
            }
            else if (sourceAddress is null)
            {
                kind = source.IndexOf('[') >= 0 ? PresetSource.Calculated : PresetSource.ConstantMove;
            }
            else if ((entryWords = FindEntryWords(sourceAddress.WordAddress)) is not null)
            {
                kind = PresetSource.Setpoint;
                var word = sourceAddress.WordAddress;
                usage.PresetWords.Add(word);
                if (!setpoints.TryGetValue(word, out var setpoint))
                {
                    setpoint = new TimeSetpoint { Word = word };
                    setpoint.EnteredAt.AddRange(entryWords.Where(e => !e.Equals(word)));
                    setpoints[word] = setpoint;
                }

                setpoint.Timers.Add(timer);
            }
            else
            {
                kind = PresetSource.Calculated;
                usage.PresetWords.Add(sourceAddress.WordAddress);
            }

            // The least "static" source wins: one calculated move makes the whole preset calculated.
            if (kind > usage.Preset)
            {
                usage.Preset = kind;
            }
        }

        foreach (var setpoint in setpoints.Values)
        {
            var timebases = setpoint.Timers.Select(t => Timers[t].Timebase).Distinct().ToList();
            setpoint.SecondsPerCount = timebases.Count == 1 ? timebases[0] : null;
            if (setpoint.SecondsPerCount is null)
            {
                var timers = string.Join(", ", setpoint.Timers);
                setpoint.Note = setpoint.Timers.All(t => Timers[t].Timebases.Count == 0)
                    ? $"{timers} gets this preset but no TON/TOF/RTO in the program runs it, so its timebase is unknown"
                    : $"{timers} run with different timebases";
                Warnings.Add($"{setpoint.Word}: {setpoint.Note}. Its setpoint object starts at 0; set it by hand.");
            }

            if (setpoint.EnteredAt.Count > 0)
            {
                var entered = string.Join(", ", setpoint.EnteredAt);
                setpoint.Note = (setpoint.Note.Length > 0 ? setpoint.Note + "; " : string.Empty)
                                + $"entered at {entered} and copied into {setpoint.Word} by logic (usually after a range check)";
            }

            // A timer that also gets calculated presets elsewhere isn't a pure setpoint timer; say so.
            foreach (var timer in setpoint.Timers.Where(t => Timers[t].Preset == PresetSource.Calculated))
            {
                Warnings.Add($"{timer} gets its preset from setpoint {setpoint.Word} and also from calculated values. Check the converted logic.");
            }

            Setpoints.Add(setpoint);
        }

        Setpoints.Sort((a, b) => string.CompareOrdinal(a.Word.ToString(), b.Word.ToString()));
    }

    private static bool IsConstant(string operand) =>
        double.TryParse(operand, NumberStyles.Float, CultureInfo.InvariantCulture, out _)
        || operand.EndsWith("h", StringComparison.OrdinalIgnoreCase) && operand.Length > 1 && Uri.IsHexDigit(operand[0]);

    private static string NormalizeTimebase(string text) => text switch
    {
        "1.0" or "1" => "1.0",
        "0.01" or ".01" => "0.01",
        "0.001" or ".001" => "0.001",
        _ => text
    };
}
