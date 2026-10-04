using System.Globalization;
using System.Text.RegularExpressions;

namespace TiaPortalTool.Conversion.Slc;

public sealed class SlcProgramConversion
{
    public List<LadBlock> Blocks { get; } = new();

    /// <summary>The helper blocks the ladder uses, by name.</summary>
    public List<string> HelperBlocks { get; } = new();

    public List<string> Warnings { get; } = new();

    /// <summary>The hardware side of the HAL: hw tag tables and the mapping FCs (also in Blocks).</summary>
    public SlcHalConversion Hal { get; set; } = new();
}

/// <summary>
/// Builds the converted program: one LAD FC per SLC program file (numbered like the file), Main (OB1) calling the
/// SLC's main file the way the SLC runs it, a startup OB for the first-scan bit, the setpoint FC, and the SCL helper
/// functions the ladder uses.
/// </summary>
public static class SlcProgramBuilder
{
    public const string ProgramGroup = "Program Files";
    public const string SupportGroup = "SLC Support";
    public const string SetpointFcName = "SLC_SETPOINTS";
    public const int SetpointFcNumber = 1000;
    public const string TimeSetpointHelper = "SLC_TIME_SP";
    public const string StatusHelper = "SLC_STATUS";

    public static SlcProgramConversion Build(SlcProgramAnalysis analysis, SlcDataConversion data)
    {
        var result = new SlcProgramConversion();
        var program = analysis.Program;
        var resolver = new SlcOperandResolver(analysis, data);
        var names = BlockNames(program);
        var translator = new SlcLadderTranslator(resolver, number => names.TryGetValue(number, out var name)
            ? name
            : throw new ManualConversionException($"JSR to program file {number}, which isn't in the export"));

        foreach (var file in program.ProgramFiles.Values)
        {
            if (file.Number < 2)
            {
                continue;   // Files 0 and 1 are system/reserved in the SLC.
            }

            var block = translator.TranslateFile(file, names[file.Number], ProgramGroup);
            result.Blocks.Add(block);
            foreach (var network in block.Networks.Where(n => n.ManualReason is not null))
            {
                result.Warnings.Add($"{block.Name} {network.Title.Split(' ')[0]} {network.Title.Split(' ')[1]}: {network.ManualReason}");
            }
        }

        var usesFirstScan = analysis.References.Any(a => a.FileType == "S" && a.Element == 1 && a.Bit == 15);
        if (resolver.Setpoints.Any())
        {
            result.Blocks.Add(SetpointBlock(resolver));
        }

        result.Hal = SlcHalBuilder.Build(analysis, data);
        result.Blocks.AddRange(result.Hal.Blocks);

        var statusWords = StatusWords(analysis, data);
        result.Blocks.Add(MainBlock(names, resolver.Setpoints.Any(), usesFirstScan, statusWords, result.Hal));
        result.Blocks.Add(StartupBlock());

        // All helpers go in, so they're there for any rung someone converts by hand later; the time setpoint one
        // only when there are setpoints (it needs UDT_TIME_SP).
        result.HelperBlocks.AddRange(SlcHelperSources.Blocks
            .Select(b => b.Name)
            .Where(name => name != TimeSetpointHelper || resolver.Setpoints.Any()));

        return result;
    }

    /// <summary>
    /// FC names: the program file name from the export, else its RSLogix symbol, else "LAD n". A name that clashes
    /// with the Main OB gets an SLC_ prefix.
    /// </summary>
    public static Dictionary<int, string> BlockNames(SlcProgram program)
    {
        var names = new Dictionary<int, string>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Main", "Startup", SetpointFcName, SlcHalBuilder.InputsFcName, SlcHalBuilder.OutputsFcName };
        foreach (var file in program.ProgramFiles.Values)
        {
            var name = file.Name.Length > 0 ? file.Name : program.Symbols.FindProgramFile(file.Number)?.Symbol ?? string.Empty;
            name = Regex.Replace(name.Trim(), "[^A-Za-z0-9 _-]", "_");
            if (name.Length == 0)
            {
                name = $"LAD {file.Number}";   // as RSLogix labels an unnamed file
            }

            if (!used.Add(name))
            {
                name = "SLC_" + name;
                used.Add(name);
            }

            names[file.Number] = name;
        }

        return names;
    }

    private static LadBlock SetpointBlock(SlcOperandResolver resolver)
    {
        var block = new LadBlock
        {
            Name = SetpointFcName,
            Number = SetpointFcNumber,
            Group = SlcDataBlockBuilder.SetpointGroup,
            Comment = "Works out each HMI timer setpoint (hours + minutes + seconds) as a TIME for the timer presets, and writes "
                      + "the value back to the original SLC word in timebase counts so any other logic reading that word still works."
        };

        foreach (var pair in resolver.Setpoints.OrderBy(p => p.Value, StringComparer.Ordinal))
        {
            var (word, member) = (pair.Key, pair.Value);
            var setpoint = resolver.Analysis.Setpoints.First(s => s.Word.Equals(word));
            var baseMs = setpoint.SecondsPerCount is { } seconds ? (int)(seconds * 1000m) : 1000;
            var net = new LadNetworkBuilder();
            var call = net.Call(TimeSetpointHelper, ("BaseMs", "Input", "DInt"), ("SP", "InOut", $"\"{SlcDataBlockBuilder.TimeSetpointType}\""), ("Ret_Val", "Return", "Int"));
            net.Power(PowerPin.Rail, call, "en");
            net.Input(net.Access(TiaOperand.Literal("DInt", baseMs.ToString(CultureInfo.InvariantCulture))), call, "BaseMs");
            net.Input(net.Access(TiaOperand.Global($"\"{SlcDataBlockBuilder.TimeSetpointType}\"", "TIME_SP", member)), call, "SP");
            net.Output(call, "Ret_Val", net.Access(resolver.Resolve(word.ToString())));
            block.Networks.Add(new LadNetwork
            {
                Title = $"{member} (was {word})",
                Comment = $"Preset for {string.Join(", ", setpoint.Timers)}. {(setpoint.Note.Length > 0 ? setpoint.Note + "." : string.Empty)}",
                FlgNet = net.ToFlgNet()
            });
        }

        return block;
    }

    /// <summary>The SLC_STATUS outputs to wire, for the clock words the logic reads and DB S2 has.</summary>
    private static List<(string Output, string Member)> StatusWords(SlcProgramAnalysis analysis, SlcDataConversion data)
    {
        var outputs = new (int Word, string Output)[]
        {
            (4, "FreeRunningClock"), (37, "Year"), (38, "Month"), (39, "Day"), (40, "Hour"), (41, "Minute"), (42, "Second")
        };
        // Only when the logic reads one of them; then all are kept current, as the SLC does.
        if (!analysis.References.Any(a => a.FileType == "S" && SlcStatusFile.IsMaintainedWord(a.Element)))
        {
            return new List<(string, string)>();
        }

        var result = new List<(string, string)>();
        foreach (var (word, output) in outputs)
        {
            var mapping = data.AddressMap.FirstOrDefault(m => m.SlcAddress == $"S:{word}" && m.Block == "S2");
            if (mapping is not null)
            {
                result.Add((output, mapping.Member));
            }
        }

        return result;
    }

    private static LadBlock MainBlock(Dictionary<int, string> names, bool hasSetpoints, bool usesFirstScan, List<(string Output, string Member)> statusWords, SlcHalConversion hal)
    {
        var block = new LadBlock
        {
            Name = "Main",
            Number = 1,
            BlockType = "OB",
            SecondaryType = "ProgramCycle",
            Comment = "Program cycle. Reads the inputs through the HAL, calls the SLC main program file (file 2), which calls the other "
                      + "files as it did in the SLC, then writes the outputs through the HAL."
        };

        if (hal.HasInputs)
        {
            block.Networks.Add(CallNetwork(SlcHalBuilder.InputsFcName, "HAL inputs",
                "Copies the real inputs into the zz tags in I1 before the logic runs, as the SLC's input scan did."));
        }

        if (statusWords.Count > 0)
        {
            var net = new LadNetworkBuilder();
            var call = net.Call(StatusHelper, new[] { "FreeRunningClock", "Year", "Month", "Day", "Hour", "Minute", "Second" }
                .Select(p => (p, "Output", "Int")).ToArray());
            net.Power(PowerPin.Rail, call, "en");
            foreach (var output in new[] { "FreeRunningClock", "Year", "Month", "Day", "Hour", "Minute", "Second" })
            {
                // An FC call needs every output connected: each goes to its S2 word, or to a scratch temp if S2 has no such word.
                var member = statusWords.FirstOrDefault(w => w.Output == output).Member;
                net.Output(call, output, net.Access(member is null ? TiaOperand.Local("Int", "unusedClockWord") : TiaOperand.Global("Int", "S2", member)));
            }

            if (statusWords.Count < 7)
            {
                block.Temps.Add(("unusedClockWord", "Int", "Clock values the SLC status file of this processor has no word for"));
            }

            block.Networks.Add(new LadNetwork
            {
                Title = "SLC status clocks",
                Comment = "Updates the SLC status words the logic reads as clocks (S:4 free-running clock, S:37-S:42 date and time) "
                          + "from the CPU, so those rungs behave as in the SLC. No CPU settings needed.",
                FlgNet = net.ToFlgNet()
            });
        }

        if (hasSetpoints)
        {
            block.Networks.Add(CallNetwork(SetpointFcName, "Timer setpoints", "Converts the HMI timer setpoints before the logic uses them."));
        }

        if (names.TryGetValue(2, out var mainFile))
        {
            block.Networks.Add(CallNetwork(mainFile, "SLC program file 2", $"The SLC main program file ({mainFile}). It calls the other program files with JSR, now FC calls."));
        }

        if (hal.HasOutputs)
        {
            block.Networks.Add(CallNetwork(SlcHalBuilder.OutputsFcName, "HAL outputs",
                "Copies the zz tags in O0 out to the real outputs after the logic, as the SLC's output scan did."));
        }

        if (usesFirstScan)
        {
            var net = new LadNetworkBuilder();
            var coil = net.Part("RCoil");
            net.Power(PowerPin.Rail, coil, "in");
            net.Input(net.Access(TiaOperand.Global("Bool", "S2", SlcDataBlockBuilder.FirstScanMember)), coil, "operand");
            block.Networks.Add(new LadNetwork
            {
                Title = "End of first scan",
                Comment = "Clears the first-scan bit (S:1/15 in the SLC) after the first full program cycle. The startup OB sets it.",
                FlgNet = net.ToFlgNet()
            });
        }

        return block;
    }

    private static LadBlock StartupBlock()
    {
        var block = new LadBlock
        {
            Name = "Startup",
            Number = 100,
            BlockType = "OB",
            SecondaryType = "Startup",
            Comment = "Runs once when the CPU goes to RUN. Sets the first-scan bit that replaces the SLC's S:1/15, and the always-true bit."
        };

        foreach (var (member, title, comment) in new[]
                 {
                     (SlcDataBlockBuilder.FirstScanMember, "First scan", "Set the first-scan bit; Main clears it at the end of the first cycle."),
                     (SlcDataBlockBuilder.AlwaysTrueMember, "Always true", "Set the always-true bit that stands in front of timers and counters the SLC ran unconditionally.")
                 })
        {
            var net = new LadNetworkBuilder();
            var coil = net.Part("SCoil");
            net.Power(PowerPin.Rail, coil, "in");
            net.Input(net.Access(TiaOperand.Global("Bool", "S2", member)), coil, "operand");
            block.Networks.Add(new LadNetwork { Title = title, Comment = comment, FlgNet = net.ToFlgNet() });
        }

        return block;
    }

    private static LadNetwork CallNetwork(string blockName, string title, string comment)
    {
        var net = new LadNetworkBuilder();
        var call = net.Call(blockName);
        net.Power(PowerPin.Rail, call, "en");
        return new LadNetwork { Title = title, Comment = comment, FlgNet = net.ToFlgNet() };
    }
}
