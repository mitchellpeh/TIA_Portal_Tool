using System.Globalization;
using System.Text.RegularExpressions;

namespace TiaPortalTool.Conversion.Slc;

/// <summary>One I/O signal the logic uses, and how the HAL connects it to hardware.</summary>
public sealed class HalSignal
{
    public string SlcAddress { get; set; } = string.Empty;
    public bool IsInput { get; set; }

    /// <summary>The zz tag the logic uses, e.g. "I1".zzIP_ESTOP.</summary>
    public string ZzOperand { get; set; } = string.Empty;

    /// <summary>The PLC tag standing for the real signal, e.g. hwIP_ESTOP.</summary>
    public string HardwareTag { get; set; } = string.Empty;

    /// <summary>Placeholder process-image address, e.g. %I64.0 or %IW80.</summary>
    public string Address { get; set; } = string.Empty;

    public string DataType { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string SourceKind { get; set; } = string.Empty;

    /// <summary>For DeviceNet signals: the device, from the symbol prefix (MTR01_FLT_IP → MTR01).</summary>
    public string Device { get; set; } = string.Empty;

    public string TagTable { get; set; } = string.Empty;
    public string MappingBlock { get; set; } = string.Empty;
    public string Symbol { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
}

public sealed class HalTagTable
{
    public string Name { get; set; } = string.Empty;
    public List<HalSignal> Signals { get; } = new();
}

public sealed class SlcHalConversion
{
    public List<HalSignal> Signals { get; } = new();
    public List<HalTagTable> TagTables { get; } = new();
    public List<LadBlock> Blocks { get; } = new();
    public bool HasInputs => Signals.Any(s => s.IsInput);
    public bool HasOutputs => Signals.Any(s => !s.IsInput);
}

/// <summary>
/// The hardware side of the HAL. The converted logic only uses the zz tags in I1/O0; this builds what ties them
/// to hardware, grouped by where each signal physically came from in the SLC rack:
/// <list type="bullet">
/// <item>a PLC tag table per slot ("HAL Slot 2 1746-IB16") with an hw tag for each signal the logic uses, at a
/// placeholder %I/%Q address laid out as a matching Siemens card would number it (16 inputs = two bytes, an analog
/// channel = one word). Mapping to real hardware is changing those addresses (Import / Export's Excel works);</item>
/// <item>an FC per slot and direction with one network per signal (hw tag → zz tag for inputs, zz tag → hw tag for
/// outputs), DeviceNet signals ordered by device; HAL_INPUTS and HAL_OUTPUTS call them, first and last in Main,
/// as the SLC reads inputs before the program and writes outputs after it.</item>
/// </list>
/// </summary>
public static class SlcHalBuilder
{
    public const string InputsFcName = "HAL_INPUTS";
    public const string OutputsFcName = "HAL_OUTPUTS";
    public const int InputsFcNumber = 1001;
    public const int OutputsFcNumber = 1002;
    public const int FirstSlotFcNumber = 1010;
    public const string HardwarePrefix = "hw";

    public static SlcHalConversion Build(SlcProgramAnalysis analysis, SlcDataConversion data)
    {
        var result = new SlcHalConversion();
        var program = analysis.Program;
        var referenced = new HashSet<SlcAddress>(analysis.References.Where(r => r.IsIo));
        var names = new MemberNamer();
        var number = FirstSlotFcNumber;

        foreach (var isInput in new[] { true, false })
        {
            var type = isInput ? "I" : "O";
            var block = isInput ? "I1" : "O0";
            var entries = data.AddressMap
                .Where(m => m.Block == block && m.Member.Length > 0)
                .Select(m => (Map: m, Address: SlcAddress.TryParse(m.SlcAddress)))
                .Where(e => e.Address is not null)
                .Select(e => (e.Map, Address: e.Address!))
                .ToList();

            // Placeholder addresses: slots packed in slot order, each the size of its SLC image.
            var baseByte = new Dictionary<int, int>();
            var next = 0;
            foreach (var slot in entries.Select(e => e.Address.Element).Distinct().OrderBy(s => s))
            {
                baseByte[slot] = next;
                var configured = program.Slots.FirstOrDefault(s => s.Slot == slot) is { } c ? (isInput ? c.InputWords : c.OutputWords) : 0;
                next += 2 * Math.Max(configured, entries.Where(e => e.Address.Element == slot).Max(e => e.Address.Word) + 1);
            }

            var calls = new List<string>();
            foreach (var slotGroup in entries.Where(e => IsUsed(e.Address, analysis, referenced)).GroupBy(e => e.Address.Element).OrderBy(g => g.Key))
            {
                var slot = program.Slots.FirstOrDefault(s => s.Slot == slotGroup.Key);
                var catalog = slot?.Catalog ?? "unknown";
                var kind = SourceKind(slotGroup.Key, catalog);
                var source = $"Slot {slotGroup.Key} {catalog}";
                var table = result.TagTables.FirstOrDefault(t => t.Name == "HAL " + source);
                if (table is null)
                {
                    table = new HalTagTable { Name = "HAL " + source };
                    result.TagTables.Add(table);
                }

                var fc = new LadBlock
                {
                    Name = $"HAL_{(isInput ? "IN" : "OUT")}_S{slotGroup.Key:00}_{Regex.Replace(catalog, "[^A-Za-z0-9]", "_")}",
                    Number = number++,
                    Group = $"{SlcDataBlockBuilder.HalGroup}/{(isInput ? "Inputs" : "Outputs")}",
                    Comment = $"{(isInput ? "Inputs from" : "Outputs to")} SLC {source}{(slot is null || slot.Description.Length == 0 ? string.Empty : " (" + slot.Description + ")")}, {kind}. "
                              + $"One network per signal the logic uses. Map each one by giving its hw tag the real address in tag table '{table.Name}', "
                              + "or by replacing the hw tag here with the real signal."
                };

                var signals = slotGroup
                    .Select(e => NewSignal(e.Map, e.Address, isInput, source, kind, table.Name, fc.Name, baseByte[e.Address.Element], names))
                    .OrderBy(s => s.Device, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(s => slotGroup.First(e => e.Map.SlcAddress == s.SlcAddress).Address.Word)
                    .ThenBy(s => slotGroup.First(e => e.Map.SlcAddress == s.SlcAddress).Address.Bit ?? -1)
                    .ToList();

                foreach (var signal in signals)
                {
                    fc.Networks.Add(MappingNetwork(signal));
                    table.Signals.Add(signal);
                    result.Signals.Add(signal);
                }

                result.Blocks.Add(fc);
                calls.Add(fc.Name);
            }

            if (calls.Count > 0)
            {
                var top = new LadBlock
                {
                    Name = isInput ? InputsFcName : OutputsFcName,
                    Number = isInput ? InputsFcNumber : OutputsFcNumber,
                    Group = SlcDataBlockBuilder.HalGroup,
                    Comment = isInput
                        ? "Hardware abstraction layer, inputs: copies every real input the logic uses into its zz tag in I1. Main calls it before the logic."
                        : "Hardware abstraction layer, outputs: copies every zz tag in O0 the logic drives out to its real output. Main calls it after the logic."
                };
                foreach (var call in calls)
                {
                    var fc = result.Blocks.First(b => b.Name == call);
                    var net = new LadNetworkBuilder();
                    var box = net.Call(call);
                    net.Power(PowerPin.Rail, box, "en");
                    top.Networks.Add(new LadNetwork { Title = call, Comment = fc.Comment.Split('.')[0] + ".", FlgNet = net.ToFlgNet() });
                }

                result.Blocks.Add(top);
            }
        }

        return result;
    }

    // A bit the logic uses, or every bit of a word the logic also uses whole; a word member when the logic uses the word.
    private static bool IsUsed(SlcAddress address, SlcProgramAnalysis analysis, HashSet<SlcAddress> referenced) =>
        address.Bit is null
            ? analysis.WordAccessedWords.Contains(address.WordAddress)
            : referenced.Contains(address) || analysis.WordAccessedWords.Contains(address.WordAddress);

    private static HalSignal NewSignal(AddressMapping map, SlcAddress address, bool isInput, string source, string kind,
        string table, string fc, int baseByte, MemberNamer names)
    {
        var area = isInput ? "I" : "Q";
        var hardwareAddress = address.Bit is { } bit
            ? $"%{area}{baseByte + 2 * address.Word + bit / 8}.{bit % 8}"       // as a 16-point Siemens card numbers its channels
            : $"%{area}W{baseByte + 2 * address.Word}";
        var stem = map.Member.StartsWith(SlcDataBlockBuilder.HardwarePrefix, StringComparison.Ordinal)
            ? map.Member.Substring(SlcDataBlockBuilder.HardwarePrefix.Length)
            : map.Member;
        return new HalSignal
        {
            SlcAddress = map.SlcAddress,
            IsInput = isInput,
            ZzOperand = map.TiaOperand,
            HardwareTag = names.Reserve(HardwarePrefix + stem, HardwarePrefix + stem),
            Address = hardwareAddress,
            DataType = map.DataType,
            Source = source,
            SourceKind = kind,
            Device = kind == DeviceNet ? DeviceOf(map.Symbol) : string.Empty,
            TagTable = table,
            MappingBlock = fc,
            Symbol = map.Symbol,
            Description = map.Description
        };
    }

    private static LadNetwork MappingNetwork(HalSignal signal)
    {
        var zz = Operand(signal.ZzOperand, signal.DataType);
        var hw = TiaOperand.Global(signal.DataType, signal.HardwareTag);
        var (from, to) = signal.IsInput ? (hw, zz) : (zz, hw);
        var net = new LadNetworkBuilder();
        if (signal.DataType == "Bool")
        {
            var contact = net.Part("Contact");
            net.Power(PowerPin.Rail, contact, "in");
            net.Input(net.Access(from), contact, "operand");
            var coil = net.Part("Coil");
            net.Power(PowerPin.Of(contact, "out"), coil, "in");
            net.Input(net.Access(to), coil, "operand");
        }
        else
        {
            var move = net.Part("Move", version: null, disabledEno: true, negated: false, ("Card", "Cardinality", "1"));
            net.Power(PowerPin.Rail, move, "en");
            net.Input(net.Access(from), move, "in");
            net.Output(move, "out1", net.Access(to));
        }

        var device = signal.Device.Length > 0 ? $"[{signal.Device}] " : string.Empty;
        var what = signal.Description.Length > 0 ? signal.Description : signal.Symbol;
        return new LadNetwork
        {
            Title = $"{device}{signal.ZzOperand.Split('.').Last()} ({signal.SlcAddress})",
            Comment = (what.Length > 0 ? what + ". " : string.Empty)
                      + (signal.IsInput
                          ? $"Real input: {signal.HardwareTag} (placeholder {signal.Address}) → zz tag {signal.ZzOperand}."
                          : $"zz tag {signal.ZzOperand} → real output: {signal.HardwareTag} (placeholder {signal.Address}).")
                      + $" Was SLC {signal.Source}, {signal.SourceKind}.",
            FlgNet = net.ToFlgNet()
        };
    }

    // "I1".zzIP_ESTOP → components I1, zzIP_ESTOP.
    private static TiaOperand Operand(string display, string dataType)
    {
        var dot = display.IndexOf("\".", StringComparison.Ordinal);
        return TiaOperand.Global(dataType, display.Substring(1, dot - 1), display.Substring(dot + 2));
    }

    private const string DeviceNet = "DeviceNet scanner (remote devices)";

    private static string SourceKind(int slot, string catalog)
    {
        var upper = catalog.ToUpperInvariant();
        if (upper.StartsWith("BUL.", StringComparison.Ordinal) || slot == 0)
        {
            return "controller's embedded I/O";
        }

        return upper switch
        {
            "1747-SDN" => DeviceNet,
            "1747-SN" or "1747-ASB" or "1747-BSN" => "remote I/O scanner",
            "OTHER" => "communication or specialty module",
            _ when upper.StartsWith("1747-", StringComparison.Ordinal) || upper.StartsWith("MVI", StringComparison.Ordinal) => "communication or specialty module",
            _ => "local I/O card"
        };
    }

    // DeviceNet signals carry the device in their symbol prefix in well-kept programs (MTR01_FLT_IP, VLV_MFLD_3_SOL).
    private static string DeviceOf(string symbol)
    {
        var underscore = symbol.IndexOf('_');
        return symbol.Length == 0 ? "no symbol" : underscore > 0 ? symbol.Substring(0, underscore) : symbol;
    }

    public static string TagFileLine(HalSignal signal) => string.Join("\t",
        signal.HardwareTag, signal.DataType, signal.Address,
        $"SLC {signal.SlcAddress} → {signal.ZzOperand}. {signal.Description}".Trim().Replace('\t', ' '));

    public static string DescribeCount(SlcHalConversion hal) => string.Format(CultureInfo.InvariantCulture,
        "HAL: {0} input and {1} output signal(s) in {2} tag table(s), mapped in {3} FC(s).",
        hal.Signals.Count(s => s.IsInput), hal.Signals.Count(s => !s.IsInput), hal.TagTables.Count, hal.Blocks.Count);
}
