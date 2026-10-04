using System.Globalization;
using System.Xml.Linq;

namespace TiaPortalTool.Conversion.Slc;

/// <summary>One network of a generated LAD block.</summary>
public sealed class LadNetwork
{
    public string Title { get; set; } = string.Empty;
    public string Comment { get; set; } = string.Empty;
    public XElement? FlgNet { get; set; }

    /// <summary>True for a network that only reads values for the rung after it (not a rung of its own).</summary>
    public bool IsValueNetwork { get; set; }

    /// <summary>Why the rung needs manual conversion, or null if it was converted.</summary>
    public string? ManualReason { get; set; }
}

/// <summary>A generated LAD code block (FC or OB) with its networks and temp variables.</summary>
public sealed class LadBlock
{
    public string Name { get; set; } = string.Empty;
    public int Number { get; set; }
    public string BlockType { get; set; } = "FC";

    /// <summary>For OBs: ProgramCycle or Startup.</summary>
    public string SecondaryType { get; set; } = string.Empty;

    public string Group { get; set; } = string.Empty;
    public string Comment { get; set; } = string.Empty;
    public List<LadNetwork> Networks { get; } = new();
    public List<(string Name, string Type, string Comment)> Temps { get; } = new();
}

/// <summary>
/// Translates SLC ladder into TIA Portal LAD networks, one network per rung.
/// <para>
/// Power flow: input instructions (contacts, compares, one-shots) take the rung condition and produce a new one.
/// Output instructions (coils, boxes, timers) are hung off the current rung condition and don't change it, exactly
/// as in the SLC, so a rung can carry on after them. BST/NXB/BND become parallel branches joined with an OR.
/// </para>
/// </summary>
public sealed class SlcLadderTranslator
{
    public const string HelperToTime = "SLC_TO_TIME";
    public const string HelperFromTime = "SLC_FROM_TIME";
    public const string HelperCopWord = "SLC_COP_WORD";
    public const string HelperCopFloat = "SLC_COP_FLOAT";
    public const string HelperCopMixed = "SLC_COP_MIXED";
    public const string HelperFllWord = "SLC_FLL_WORD";
    public const string HelperFllFloat = "SLC_FLL_FLOAT";
    public const string HelperScp = "SLC_SCP";
    public const string HelperWordRead = "SLC_WORD_READ";
    public const string HelperWordWrite = "SLC_WORD_WRITE";
    public const string HelperFloatRead = "SLC_FLOAT_READ";
    public const string HelperFloatWrite = "SLC_FLOAT_WRITE";
    public const string HelperBitRead = "SLC_BIT_READ";
    public const string HelperBitSet = "SLC_BIT_SET";
    public const string HelperBitReset = "SLC_BIT_RESET";
    public const string HelperMvm = "SLC_MVM";

    private readonly SlcOperandResolver _resolver;
    private readonly Func<int, string> _programFileName;

    // Per-network state. A value that needs a box to read (a conversion, a helper call) is read in series right
    // before the instruction that uses it. Inside parallel branches that join again TIA doesn't allow such a box, so
    // there it's read in a separate network just before the rung instead (reading has no side effects).
    private LadNetworkBuilder _net = null!;
    private LadNetworkBuilder _prep = null!;
    private int _branchDepth;

    // Things worth telling the reader about this rung's conversion; added to the network comment.
    private List<string> _notes = new();

    // The value reads are chained in series (en/eno) so each runs after the one it may depend on.
    private PowerPin _prepTail = PowerPin.Rail;
    private Dictionary<string, int> _tempCounts = new(StringComparer.Ordinal);

    // Per-block state.
    private LadBlock _block = null!;
    private Dictionary<string, int> _tempDeclared = new(StringComparer.Ordinal);

    public SlcLadderTranslator(SlcOperandResolver resolver, Func<int, string> programFileName)
    {
        _resolver = resolver;
        _programFileName = programFileName;
    }

    private SlcProgramAnalysis Analysis => _resolver.Analysis;

    public LadBlock TranslateFile(SlcProgramFile file, string blockName, string group)
    {
        _block = new LadBlock
        {
            Name = blockName,
            Number = file.Number,
            Group = group,
            Comment = $"SLC program file {file.Number}" + (file.Name.Length > 0 ? $" ({file.Name})" : string.Empty)
                      + ". One network per SLC rung; network titles carry the rung numbers."
        };
        _tempDeclared = new Dictionary<string, int>(StringComparer.Ordinal);

        string? mcrZone = null;
        var mcrCount = 0;
        foreach (var rung in file.Rungs)
        {
            var network = new LadNetwork
            {
                Title = $"RUNG {rung.Number}" + (rung.Title.Length > 0 ? " - " + rung.Title : string.Empty),
                Comment = (rung.Description.Length > 0 ? rung.Description + Environment.NewLine + Environment.NewLine : string.Empty)
                          + "SLC: " + rung.Text
            };

            var tokenized = Analysis.Rungs[(file.Number, rung.Number)];
            try
            {
                if (tokenized.Error is not null)
                {
                    throw new ManualConversionException(tokenized.Error);
                }

                StartNetwork();
                var elements = tokenized.Elements;
                foreach (var note in elements.SelectMany(e => e.Operands).Select(o => SlcAddress.TryParse(o.TrimStart('#')))
                             .Where(a => a is not null).Select(a => SlcStatusFile.RungNote(a!)).Where(n => n is not null).Distinct())
                {
                    _notes.Add(note!);
                }

                if (elements.Any(e => e.Mnemonic is not ("COP" or "FLL" or "CPW") && e.Operands.Any(o => o.StartsWith("#", StringComparison.Ordinal))))
                {
                    _notes.Add("The SLC rung uses indexed addressing (#), which adds the index register S:24 to the address. The "
                               + "converted rung uses the address without that offset; check that S:24 was 0 here.");
                }

                var mcrIndex = elements.FindIndex(e => e.Mnemonic == "MCR");
                if (mcrIndex >= 0)
                {
                    if (mcrIndex != elements.Count - 1)
                    {
                        throw new ManualConversionException("MCR must be the last instruction on its rung");
                    }

                    if (mcrZone is not null && mcrIndex == 0)
                    {
                        network.Title += " - MCR zone end";
                        network.Comment = "End of MCR zone " + mcrZone + ". " + network.Comment;
                        mcrZone = null;
                    }
                    else
                    {
                        // Zone start: the zone condition goes into a temp bit that every rung in the zone starts with.
                        mcrZone = $"MCR_Zone{++mcrCount}";
                        DeclareTemp(mcrZone, "Bool", $"Condition of MCR zone {mcrCount} (rung {rung.Number}); ANDed into every rung of the zone");
                        var power = EvalSequence(BuildTree(elements.Take(mcrIndex).ToList()), Power.Rail);
                        var coil = _net.Part("Coil");
                        Connect(power, coil, "in");
                        _net.Input(_net.Access(TiaOperand.Local("Bool", mcrZone)), coil, "operand");
                        network.Title += " - MCR zone start";
                    }
                }
                else
                {
                    var start = Power.Rail;
                    if (mcrZone is not null)
                    {
                        var contact = _net.Part("Contact");
                        _net.Power(PowerPin.Rail, contact, "in");
                        _net.Input(_net.Access(TiaOperand.Local("Bool", mcrZone)), contact, "operand");
                        start = Power.Of(PowerPin.Of(contact, "out"));
                        network.Comment = $"Inside MCR zone ({mcrZone}). " + network.Comment;
                    }

                    EvalSequence(BuildTree(elements), start);
                }

                network.FlgNet = _net.IsEmpty ? null : _net.ToFlgNet();
                if (_notes.Count > 0)
                {
                    network.Comment = "NOTE: " + string.Join(" ", _notes.Distinct()) + Environment.NewLine + Environment.NewLine + network.Comment;
                }

                if (!_prep.IsEmpty)
                {
                    _block.Networks.Add(new LadNetwork
                    {
                        Title = $"RUNG {rung.Number} (values)",
                        Comment = $"Reads values that rung {rung.Number} uses as numbers (timer times in timebase counts, whole words of bit files) "
                                  + "into temps. The next network is the rung itself.",
                        FlgNet = _prep.ToFlgNet(),
                        IsValueNetwork = true
                    });
                }

                FinishNetworkTemps();
            }
            catch (ManualConversionException ex)
            {
                network.ManualReason = ex.Message;
                network.Title = $"RUNG {rung.Number} - MANUAL CONVERSION REQUIRED";
                network.Comment = $"Not converted: {ex.Message}." + Environment.NewLine + Environment.NewLine + network.Comment;
                network.FlgNet = null;
                FinishNetworkTemps();
            }

            _block.Networks.Add(network);
        }

        return _block;
    }

    // ---- Rung tree ----

    private abstract class Node
    {
    }

    private sealed class InstructionNode : Node
    {
        public InstructionNode(RungElement element) => Element = element;
        public RungElement Element { get; }
    }

    private sealed class BranchNode : Node
    {
        public List<List<Node>> Legs { get; } = new();
    }

    private static List<Node> BuildTree(List<RungElement> elements)
    {
        var index = 0;
        var result = ParseSequence(elements, ref index, topLevel: true);
        return result;
    }

    private static List<Node> ParseSequence(List<RungElement> elements, ref int index, bool topLevel)
    {
        var sequence = new List<Node>();
        while (index < elements.Count)
        {
            var element = elements[index];
            if (element.Mnemonic is "NXB" or "BND")
            {
                if (topLevel)
                {
                    throw new ManualConversionException($"unbalanced branch ({element.Mnemonic} without BST)");
                }

                return sequence;
            }

            index++;
            if (element.Mnemonic == "BST")
            {
                var branch = new BranchNode();
                while (true)
                {
                    branch.Legs.Add(ParseSequence(elements, ref index, topLevel: false));
                    if (index >= elements.Count)
                    {
                        throw new ManualConversionException("branch not closed (BST without BND)");
                    }

                    var marker = elements[index++].Mnemonic;
                    if (marker == "BND")
                    {
                        break;
                    }
                }

                sequence.Add(branch);
            }
            else
            {
                sequence.Add(new InstructionNode(element));
            }
        }

        return sequence;
    }

    // ---- Power ----

    /// <summary>
    /// The rung condition at some point: a pin, or the OR of parallel branch ends, which only becomes an "O" part
    /// once something actually uses it (branches of output instructions at the end of a rung don't need one).
    /// </summary>
    private sealed class Power
    {
        public static readonly Power Rail = new() { Pin = PowerPin.Rail };

        public PowerPin? Pin { get; private set; }
        public List<Power>? AnyOf { get; private set; }

        public static Power Of(PowerPin pin) => new() { Pin = pin };
        public static Power Or(List<Power> legs) => new() { AnyOf = legs };

        public void Materialize(PowerPin pin)
        {
            Pin = pin;
            AnyOf = null;
        }
    }

    private void Connect(Power power, int part, string pin) => _net.Power(Resolve(power), part, pin);

    private PowerPin Resolve(Power power)
    {
        if (power.Pin is not null)
        {
            return power.Pin;
        }

        var legs = power.AnyOf!;
        var or = _net.Part("O", ("Card", "Cardinality", legs.Count.ToString(CultureInfo.InvariantCulture)));
        for (var i = 0; i < legs.Count; i++)
        {
            _net.Power(Resolve(legs[i]), or, "in" + (i + 1).ToString(CultureInfo.InvariantCulture));
        }

        power.Materialize(PowerPin.Of(or, "out"));
        return power.Pin!;
    }

    private Power EvalSequence(List<Node> sequence, Power power)
    {
        foreach (var node in sequence)
        {
            power = node switch
            {
                InstructionNode instruction => Translate(instruction.Element, power),
                BranchNode branch => EvalBranch(branch, power),
                _ => power
            };
        }

        return power;
    }

    private Power EvalBranch(BranchNode branch, Power power)
    {
        _branchDepth++;
        var ends = branch.Legs.Select(leg => EvalSequence(leg, power)).ToList();
        _branchDepth--;

        // A leg of only output instructions passes the incoming condition straight through, and the OR of the
        // incoming condition with anything derived from it is just the incoming condition.
        if (ends.Any(e => ReferenceEquals(e, power)))
        {
            return power;
        }

        var distinct = ends.Distinct().ToList();
        return distinct.Count == 1 ? distinct[0] : Power.Or(distinct);
    }

    // ---- Instructions ----

    private Power Translate(RungElement element, Power power)
    {
        var ops = element.Operands;
        switch (element.Mnemonic)
        {
            case "XIC":
            case "XIO":
                return Contact(ops[0], negated: element.Mnemonic == "XIO", power);

            case "OTE":
                return Coil("Coil", ops[0], power);
            case "OTL":
                return Coil("SCoil", ops[0], power);
            case "OTU":
                return Coil("RCoil", ops[0], power);

            case "ONS":
            case "OSR" when ops.Count == 1:
            {
                var box = _net.Part("PBox");
                Connect(power, box, "in");
                _net.Input(_net.Access(Bool(ops[0])), box, "bit");
                return Power.Of(PowerPin.Of(box, "out"));
            }

            case "EQU": return Compare("Eq", ops[0], ops[1], power);
            case "NEQ": return Compare("Ne", ops[0], ops[1], power);
            case "LES": return Compare("Lt", ops[0], ops[1], power);
            case "LEQ": return Compare("Le", ops[0], ops[1], power);
            case "GRT": return Compare("Gt", ops[0], ops[1], power);
            case "GEQ": return Compare("Ge", ops[0], ops[1], power);
            case "LIM": return Limit(ops[0], ops[1], ops[2], power);

            case "MOV":
                Move(ops[0], ops[1], power);
                return power;
            case "CLR":
            {
                var destination = Destination(ops[0]);
                MoveValue(TiaOperand.Literal(destination.DataType == "Real" ? "Real" : "Int", destination.DataType == "Real" ? "0.0" : "0"), destination, power);
                return power;
            }

            case "ADD": Math("Add", ops, power); return power;
            case "SUB": Math("Sub", ops, power); return power;
            case "MUL": Math("Mul", ops, power); return power;
            case "DIV": Math("Div", ops, power); return power;
            case "AND": Logic("And", ops, power); return power;
            case "OR": Logic("Or", ops, power); return power;
            case "XOR": Logic("Xor", ops, power); return power;
            case "NOT":
                // NOT is XOR with all ones.
                Logic("Xor", new[] { ops[0], "0FFFFh", ops[1] }, power);
                return power;
            case "MVM": Mvm(ops, power); return power;
            case "SQR": Sqrt(ops, power); return power;
            case "NEG": Neg(ops, power); return power;
            case "SCP": Scp(ops, power); return power;
            case "COP": Cop(ops, power); return power;
            case "FLL": Fill(ops, power); return power;

            case "TON":
            case "TOF":
            case "RTO":
                Timer(element.Mnemonic, ops, power);
                return power;
            case "CTU":
            case "CTD":
                Counter(element.Mnemonic, ops, power);
                return power;
            case "RES":
                Reset(ops[0], power);
                return power;

            case "JSR":
            {
                var number = int.Parse(ops[0], CultureInfo.InvariantCulture);
                var call = _net.Call(_programFileName(number));
                Connect(power, call, "en");
                return power;
            }

            // Markers with nothing to do in TIA Portal.
            case "SBR":
            case "NOP":
            case "END":
                return power;

            default:
                throw new ManualConversionException($"{element} ({element.Info?.TiaEquivalent ?? "unknown instruction"})");
        }
    }

    private Power Contact(string operand, bool negated, Power power)
    {
        // A timer's TT bit is "enabled and not done": IN AND NOT Q, or for XIO: NOT IN OR Q.
        var address = SlcAddress.TryParse(operand);
        if (address is { FileType: "T", Member: "TT" })
        {
            var instance = _resolver.Instance(address);
            var running = TiaOperand.Global("Bool", instance.Components[0], instance.Components[1], "IN");
            var done = TiaOperand.Global("Bool", instance.Components[0], instance.Components[1], "Q");
            if (!negated)
            {
                return ContactOn(done, true, ContactOn(running, false, power));
            }

            return Power.Or(new List<Power> { ContactOn(running, true, power), ContactOn(done, false, power) });
        }

        var bit = Bool(operand);
        if (bit.IsAddressed)
        {
            (bit, power) = Readable(bit, power);
        }

        return ContactOn(bit, negated, power);
    }

    private Power ContactOn(TiaOperand operand, bool negated, Power power)
    {
        var contact = _net.Part("Contact", version: null, disabledEno: null, negated: negated);
        Connect(power, contact, "in");
        _net.Input(_net.Access(operand), contact, "operand");
        return Power.Of(PowerPin.Of(contact, "out"));
    }

    private Power Coil(string kind, string operand, Power power)
    {
        // Clearing or setting the math overflow trap is SLC housekeeping with nothing to do in TIA.
        if (SlcAddress.TryParse(operand) is { } trap && SlcStatusFile.IsOverflowTrap(trap))
        {
            return power;
        }

        var target = Bool(operand);
        if (target.IsAddressed)
        {
            // OTL/OTU on a bit reached by number: SLC_BIT_SET / SLC_BIT_RESET. An OTE would also need to clear the
            // bit when the rung is false, which a call can't do on its own.
            if (kind == "Coil")
            {
                throw new ManualConversionException($"OTE on the indirect bit {operand}");
            }

            var call = _net.Call(kind == "SCoil" ? HelperBitSet : HelperBitReset, ("File", "Input", "DInt"), ("Element", "Input", "DInt"), ("Bit", "Input", "DInt"));
            Connect(power, call, "en");
            AddressInputs(_net, call, target);
            return power;
        }

        var coil = _net.Part(kind);
        Connect(power, coil, "in");
        _net.Input(_net.Access(target), coil, "operand");
        return power;
    }

    private TiaOperand Bool(string operand)
    {
        var resolved = _resolver.Resolve(operand);
        if (resolved.DataType != "Bool")
        {
            throw new ManualConversionException($"{operand} is used as a bit but converts to {resolved.DataType}");
        }

        return resolved;
    }

    // ---- Values ----

    /// <summary>
    /// Resolves a value to read. Timer presets and accumulators are TIME in TIA; when the SLC logic uses them as
    /// numbers they're converted back to timebase counts with SLC_FROM_TIME, inline before the instruction.
    /// </summary>
    private (TiaOperand Value, Power Power) Value(string operand, Power power)
    {
        var address = SlcAddress.TryParse(operand);
        if (address is { FileType: "T", Member: "PRE" or "ACC" })
        {
            var time = _resolver.Resolve(operand);
            var temp = Temp("Int");
            var (net, call) = ValueBox(n => n.Call(HelperFromTime, ("Value", "Input", "Time"), ("BaseMs", "Input", "DInt"), ("Ret_Val", "Return", "Int")), ref power);
            net.Input(net.Access(time), call, "Value");
            net.Input(net.Access(TiaOperand.Literal("DInt", BaseMs(address).ToString(CultureInfo.InvariantCulture))), call, "BaseMs");
            net.Output(call, "Ret_Val", net.Access(temp));
            return (temp, power);
        }

        return Readable(_resolver.Resolve(operand), power);
    }

    /// <summary>
    /// Data reached by file/element number (whole words of bit files, indirect addresses) is read with
    /// SLC_WORD_READ / SLC_FLOAT_READ / SLC_BIT_READ into a temp before the instruction.
    /// </summary>
    private (TiaOperand Value, Power Power) Readable(TiaOperand operand, Power power)
    {
        if (!operand.IsAddressed)
        {
            return (operand, power);
        }

        var (helper, type) = operand.DataType switch
        {
            "Real" => (HelperFloatRead, "Real"),
            "Bool" => (HelperBitRead, "Bool"),
            _ => (HelperWordRead, "Int")
        };
        var temp = Temp(type);
        var parameters = new List<(string, string, string)> { ("File", "Input", "DInt"), ("Element", "Input", "DInt") };
        if (type == "Bool")
        {
            parameters.Add(("Bit", "Input", "DInt"));
        }

        parameters.Add(("Ret_Val", "Return", type));
        var (net, call) = ValueBox(n => n.Call(helper, parameters.ToArray()), ref power);
        AddressInputs(net, call, operand);
        net.Output(call, "Ret_Val", net.Access(temp));
        return (temp, power);
    }

    private static void AddressInputs(LadNetworkBuilder net, int call, TiaOperand operand)
    {
        net.Input(net.Access(operand.AddressFile), call, "File");
        net.Input(net.Access(operand.AddressElement), call, "Element");
        if (operand.AddressBit is not null)
        {
            net.Input(net.Access(operand.AddressBit), call, "Bit");
        }
    }

    /// <summary>
    /// Creates a box that reads a value: in series in the rung (the rung condition carries on from its ENO), or, inside
    /// parallel branches, chained in the values network before the rung.
    /// </summary>
    private (LadNetworkBuilder Net, int Box) ValueBox(Func<LadNetworkBuilder, int> create, ref Power power)
    {
        if (_branchDepth == 0)
        {
            var box = create(_net);
            Connect(power, box, "en");
            power = Power.Of(PowerPin.Of(box, "eno"));
            return (_net, box);
        }

        var prepBox = create(_prep);
        _prep.Power(_prepTail, prepBox, "en");
        _prepTail = PowerPin.Of(prepBox, "eno");
        return (_prep, prepBox);
    }

    /// <summary>
    /// TIA doesn't convert INT/DINT to REAL implicitly in box inputs or call parameters. Constants are rewritten as
    /// REAL constants; variables are converted into a REAL temp in the values network.
    /// </summary>
    private TiaOperand AsReal(TiaOperand operand, ref Power power)
    {
        if (operand.DataType == "Real")
        {
            return operand;
        }

        if (operand.IsLiteral)
        {
            return TiaOperand.Literal("Real", SlcDataBlockBuilder.FormatReal(operand.Display));
        }

        var temp = Temp("Real");
        var sourceType = operand.DataType == "Word" ? "Int" : operand.DataType;
        var (net, convert) = ValueBox(n => n.Part("Convert", version: null, disabledEno: true, negated: false,
            ("SrcType", "Type", sourceType), ("DestType", "Type", "Real")), ref power);
        net.Input(net.Access(operand), convert, "in");
        net.Output(convert, "out", net.Access(temp));
        return temp;
    }

    /// <summary>
    /// Connects a box output to its destination. A whole word of a bit file is written through a temp and
    /// SLC_POKE_WORD, chained after the box.
    /// </summary>
    private void Store(int box, string pin, TiaOperand destination)
    {
        if (!destination.IsAddressed)
        {
            _net.Output(box, pin, _net.Access(destination));
            return;
        }

        var type = destination.DataType == "Real" ? "Real" : "Int";
        var temp = Temp(type);
        _net.Output(box, pin, _net.Access(temp));
        WriteAddressed(destination, temp, Power.Of(PowerPin.Of(box, "eno")));
    }

    /// <summary>Writes a word or float reached by file/element number (SLC_WORD_WRITE / SLC_FLOAT_WRITE).</summary>
    private void WriteAddressed(TiaOperand destination, TiaOperand value, Power power)
    {
        if (destination.DataType == "Bool")
        {
            throw new ManualConversionException($"writing the bit {destination.Display} as a value");
        }

        var isFloat = destination.DataType == "Real";
        var call = _net.Call(isFloat ? HelperFloatWrite : HelperWordWrite,
            ("File", "Input", "DInt"), ("Element", "Input", "DInt"), ("Value", "Input", isFloat ? "Real" : "Int"));
        Connect(power, call, "en");
        AddressInputs(_net, call, destination);
        _net.Input(_net.Access(value), call, "Value");
    }

    private TiaOperand Destination(string operand)
    {
        if (SlcResolverConstant(operand))
        {
            throw new ManualConversionException($"constant {operand} used as a destination");
        }

        var address = SlcAddress.TryParse(operand);
        if (address is { FileType: "T", Member: "PRE" or "ACC" })
        {
            throw new ManualConversionException($"writing {operand} from math isn't converted yet");
        }

        return _resolver.Resolve(operand);
    }

    private static bool SlcResolverConstant(string operand) => SlcOperandResolver.TryConstant(operand) is not null;

    private static bool IsNumeric(string type) => type is "Int" or "DInt" or "Real" or "Word";

    private static string Wider(string a, string b)
    {
        if (a == "Real" || b == "Real")
        {
            return "Real";
        }

        if (a == "DInt" || b == "DInt")
        {
            return "DInt";
        }

        return "Int";
    }

    private int BaseMs(SlcAddress timer)
    {
        if (Analysis.Timers.TryGetValue(timer.WordAddress, out var usage) && usage.Timebase is { } seconds)
        {
            return (int)(seconds * 1000m);
        }

        throw new ManualConversionException($"the timebase of {timer.WordAddress} is unknown (no TON/TOF/RTO runs it)");
    }

    // ---- Compare ----

    private Power Compare(string kind, string left, string right, Power power)
    {
        // A timer's preset or accumulator against a constant compares natively as TIME.
        var timeCompare = TimeCompare(kind, left, right, power) ?? TimeCompare(Mirror(kind), right, left, power);
        if (timeCompare is not null)
        {
            return timeCompare;
        }

        (var a, power) = Value(left, power);
        (var b, power) = Value(right, power);
        if (!IsNumeric(a.DataType) || !IsNumeric(b.DataType))
        {
            throw new ManualConversionException($"compare of {a.DataType} with {b.DataType}");
        }

        var type = Wider(a.DataType, b.DataType);
        if (type == "Real")
        {
            a = AsReal(a, ref power);
            b = AsReal(b, ref power);
        }

        var part = _net.Part(kind, ("SrcType", "Type", type));
        Connect(power, part, "pre");
        _net.Input(_net.Access(a), part, "in1");
        _net.Input(_net.Access(b), part, "in2");
        return Power.Of(PowerPin.Of(part, "out"));
    }

    /// <summary>
    /// LIM with limits in words: like the SLC, inside the band when low &lt;= high, outside it (test &gt;= low OR
    /// test &lt;= high) when low &gt; high. Two branches, each starting with the compare of the limits.
    /// </summary>
    private Power VariableLimit(string lowText, string testText, string highText, Power power)
    {
        var normal = Compare("Le", lowText, highText, power);
        var inside = _net.Part("InRange", ("SrcType", "Type", ResultTypeForLimit(lowText, testText, highText)));
        Connect(normal, inside, "pre");
        _net.Input(_net.Access(_resolver.Resolve(lowText)), inside, "min");
        _net.Input(_net.Access(_resolver.Resolve(testText)), inside, "in");
        _net.Input(_net.Access(_resolver.Resolve(highText)), inside, "max");

        var reversed = Compare("Gt", lowText, highText, power);
        var outside = Power.Or(new List<Power> { Compare("Ge", testText, lowText, reversed), Compare("Le", testText, highText, reversed) });
        return Power.Or(new List<Power> { Power.Of(PowerPin.Of(inside, "out")), outside });
    }

    private string ResultTypeForLimit(params string[] operands)
    {
        var type = "Int";
        foreach (var operand in operands)
        {
            var resolved = _resolver.Resolve(operand);
            if (resolved.IsAddressed || !IsNumeric(resolved.DataType) || resolved.DataType == "Real")
            {
                throw new ManualConversionException("LIM with variable limits on floats or indirect addresses");
            }

            type = Wider(type, resolved.DataType);
        }

        return type;
    }

    private Power? TimeCompare(string kind, string timerText, string constantText, Power power)
    {
        var timer = SlcAddress.TryParse(timerText);
        var constant = SlcOperandResolver.TryConstant(constantText);
        if (timer is not { FileType: "T", Member: "PRE" or "ACC" } || constant is null || constant.DataType == "Real")
        {
            return null;
        }

        var counts = decimal.Parse(constant.Display.Replace("16#", string.Empty), CultureInfo.InvariantCulture);
        var time = TiaOperand.Typed("Time", SlcDataBlockBuilder.FormatTime(System.Math.Max(0, counts) * BaseMs(timer) / 1000m));
        var part = _net.Part(kind, ("SrcType", "Type", "Time"));
        Connect(power, part, "pre");
        _net.Input(_net.Access(_resolver.Resolve(timerText)), part, "in1");
        _net.Input(_net.Access(time), part, "in2");
        return Power.Of(PowerPin.Of(part, "out"));
    }

    // a > b is b < a, and so on.
    private static string Mirror(string kind) => kind switch
    {
        "Lt" => "Gt",
        "Gt" => "Lt",
        "Le" => "Ge",
        "Ge" => "Le",
        _ => kind
    };

    private Power Limit(string lowText, string testText, string highText, Power power)
    {
        var low = SlcOperandResolver.TryConstant(lowText);
        var high = SlcOperandResolver.TryConstant(highText);
        if (low is null || high is null)
        {
            return VariableLimit(lowText, testText, highText, power);
        }

        var lowValue = double.Parse(low.Display.Replace("16#", string.Empty), CultureInfo.InvariantCulture);
        var highValue = double.Parse(high.Display.Replace("16#", string.Empty), CultureInfo.InvariantCulture);
        (var test, power) = Value(testText, power);
        var type = Wider(test.DataType, Wider(low.DataType, high.DataType));
        if (type == "Real")
        {
            test = AsReal(test, ref power);
            low = AsReal(low, ref power);
            high = AsReal(high, ref power);
        }

        if (lowValue <= highValue)
        {
            var range = _net.Part("InRange", ("SrcType", "Type", type));
            Connect(power, range, "pre");
            _net.Input(_net.Access(low), range, "min");
            _net.Input(_net.Access(test), range, "in");
            _net.Input(_net.Access(high), range, "max");
            return Power.Of(PowerPin.Of(range, "out"));
        }

        // Low limit above the high limit: the SLC LIM is true outside the band, i.e. test >= low OR test <= high.
        var ge = _net.Part("Ge", ("SrcType", "Type", type));
        Connect(power, ge, "pre");
        _net.Input(_net.Access(test), ge, "in1");
        _net.Input(_net.Access(low), ge, "in2");
        var le = _net.Part("Le", ("SrcType", "Type", type));
        Connect(power, le, "pre");
        _net.Input(_net.Access(test), le, "in1");
        _net.Input(_net.Access(high), le, "in2");
        return Power.Or(new List<Power> { Power.Of(PowerPin.Of(ge, "out")), Power.Of(PowerPin.Of(le, "out")) });
    }

    // ---- Move and math ----

    private void Move(string sourceText, string destinationText, Power power)
    {
        var destinationAddress = SlcAddress.TryParse(destinationText);
        if (destinationAddress is { FileType: "T", Member: "PRE" })
        {
            MoveToPreset(sourceText, destinationAddress, power);
            return;
        }

        (var source, power) = Value(sourceText, power);
        MoveValue(source, Destination(destinationText), power);
    }

    /// <summary>MOVE, or CONVERT when a REAL goes into an integer (CONVERT rounds, like the SLC does).</summary>
    private void MoveValue(TiaOperand source, TiaOperand destination, Power power)
    {
        // Data reached by file/element number takes a value of its own type straight into the write helper.
        if (destination.IsAddressed && (destination.DataType == "Real" ? source.DataType == "Real" : source.DataType is "Int" or "Word"))
        {
            WriteAddressed(destination, source, power);
            return;
        }

        if (source.DataType != destination.DataType && (source.DataType == "Real" || destination.DataType == "Real")
            && IsNumeric(source.DataType) && IsNumeric(destination.DataType))
        {
            var convert = _net.Part("Convert", version: null, disabledEno: true, negated: false,
                ("SrcType", "Type", source.DataType == "Word" ? "Int" : source.DataType), ("DestType", "Type", destination.DataType == "Word" ? "Int" : destination.DataType));
            Connect(power, convert, "en");
            _net.Input(_net.Access(source), convert, "in");
            Store(convert, "out", destination);
            return;
        }

        var move = _net.Part("Move", version: null, disabledEno: true, negated: false, ("Card", "Cardinality", "1"));
        Connect(power, move, "en");
        _net.Input(_net.Access(source), move, "in");
        Store(move, "out1", destination);
    }

    /// <summary>
    /// Writes a timer preset. A constant becomes a TIME constant; an HMI setpoint word becomes its setpoint object's
    /// Output_Seconds; any other word is converted from timebase counts with SLC_TO_TIME.
    /// </summary>
    private void MoveToPreset(string sourceText, SlcAddress timer, Power power)
    {
        var preset = _resolver.Resolve(timer.ToString());
        var baseMs = BaseMs(timer);
        var constant = SlcOperandResolver.TryConstant(sourceText);
        if (constant is not null)
        {
            var counts = decimal.Parse(constant.Display, CultureInfo.InvariantCulture);
            MoveValue(TiaOperand.Typed("Time", SlcDataBlockBuilder.FormatTime(counts * baseMs / 1000m)), preset, power);
            return;
        }

        var sourceAddress = SlcAddress.TryParse(sourceText);
        var setpoint = sourceAddress is null ? null : _resolver.SetpointMember(sourceAddress.WordAddress);
        if (setpoint is not null)
        {
            MoveValue(TiaOperand.Global("Time", "TIME_SP", setpoint, "Output_Seconds"), preset, power);
            return;
        }

        (var source, power) = Value(sourceText, power);
        var call = _net.Call(HelperToTime, ("Counts", "Input", "Int"), ("BaseMs", "Input", "DInt"), ("Ret_Val", "Return", "Time"));
        Connect(power, call, "en");
        _net.Input(_net.Access(source), call, "Counts");
        _net.Input(_net.Access(TiaOperand.Literal("DInt", baseMs.ToString(CultureInfo.InvariantCulture))), call, "BaseMs");
        _net.Output(call, "Ret_Val", _net.Access(preset));
    }

    /// <summary>
    /// ADD/SUB/MUL/DIV. The box works in the widest type involved. Integer DIV is done in REAL and converted, because
    /// the SLC rounds integer division where TIA truncates. A REAL result going into an integer is converted (rounded).
    /// </summary>
    private void Math(string kind, IReadOnlyList<string> ops, Power power)
    {
        (var a, power) = Value(ops[0], power);
        (var b, power) = Value(ops[1], power);

        // Math straight into a timer preset (MUL N7:5 60 T4:3.PRE): work it out in timebase counts, then SLC_TO_TIME.
        if (SlcAddress.TryParse(ops[2]) is { FileType: "T", Member: "PRE" } presetTimer)
        {
            MathIntoPreset(kind, a, b, presetTimer, power);
            return;
        }

        var destination = Destination(ops[2]);
        var type = Wider(Wider(a.DataType, b.DataType), destination.DataType);
        if (kind == "Div" && type != "Real")
        {
            type = "Real";
        }

        if (type == "Real")
        {
            a = AsReal(a, ref power);
            b = AsReal(b, ref power);
        }

        // Only ADD and MUL can take more inputs, so only they carry a cardinality.
        var box = kind is "Add" or "Mul"
            ? _net.Part(kind, version: null, disabledEno: false, negated: false, ("Card", "Cardinality", "2"), ("SrcType", "Type", type))
            : _net.Part(kind, version: null, disabledEno: false, negated: false, ("SrcType", "Type", type));
        Connect(power, box, "en");
        _net.Input(_net.Access(a), box, "in1");
        _net.Input(_net.Access(b), box, "in2");
        if (type == "Real" && destination.DataType != "Real")
        {
            var temp = Temp("Real");
            _net.Output(box, "out", _net.Access(temp));
            MoveValue(temp, destination, Power.Of(PowerPin.Of(box, "eno")));
        }
        else
        {
            Store(box, "out", destination);
        }
    }

    private void MathIntoPreset(string kind, TiaOperand a, TiaOperand b, SlcAddress timer, Power power)
    {
        var type = Wider(a.DataType, b.DataType);
        if (kind == "Div")
        {
            type = "Real";
        }

        if (type == "Real")
        {
            a = AsReal(a, ref power);
            b = AsReal(b, ref power);
        }

        var box = kind is "Add" or "Mul"
            ? _net.Part(kind, version: null, disabledEno: false, negated: false, ("Card", "Cardinality", "2"), ("SrcType", "Type", type))
            : _net.Part(kind, version: null, disabledEno: false, negated: false, ("SrcType", "Type", type));
        Connect(power, box, "en");
        _net.Input(_net.Access(a), box, "in1");
        _net.Input(_net.Access(b), box, "in2");

        // Counts as an INT: a REAL result is rounded, as the SLC rounds a float written into an integer word.
        var counts = Temp("Int");
        var after = Power.Of(PowerPin.Of(box, "eno"));
        if (type == "Real")
        {
            var real = Temp("Real");
            _net.Output(box, "out", _net.Access(real));
            var convert = _net.Part("Convert", version: null, disabledEno: true, negated: false, ("SrcType", "Type", "Real"), ("DestType", "Type", "Int"));
            Connect(after, convert, "en");
            _net.Input(_net.Access(real), convert, "in");
            _net.Output(convert, "out", _net.Access(counts));
            after = Power.Of(PowerPin.Of(convert, "eno"));
        }
        else
        {
            _net.Output(box, "out", _net.Access(counts));
        }

        var call = _net.Call(HelperToTime, ("Counts", "Input", "Int"), ("BaseMs", "Input", "DInt"), ("Ret_Val", "Return", "Time"));
        Connect(after, call, "en");
        _net.Input(_net.Access(counts), call, "Counts");
        _net.Input(_net.Access(DInt(BaseMs(timer))), call, "BaseMs");
        _net.Output(call, "Ret_Val", _net.Access(_resolver.Resolve(timer.ToString())));
    }

    private void Logic(string kind, IReadOnlyList<string> ops, Power power)
    {
        (var a, power) = Value(ops[0], power);
        (var b, power) = Value(ops[1], power);
        var destination = Destination(ops[2]);
        var box = _net.Part(kind, version: null, disabledEno: true, negated: false, ("Card", "Cardinality", "2"), ("SrcType", "Type", "Word"));
        Connect(power, box, "en");
        _net.Input(_net.Access(a), box, "in1");
        _net.Input(_net.Access(b), box, "in2");
        Store(box, "out", destination);
    }

    /// <summary>MVM source mask destination: SLC_MVM works out (dest AND NOT mask) OR (source AND mask).</summary>
    private void Mvm(IReadOnlyList<string> ops, Power power)
    {
        (var source, power) = Value(ops[0], power);
        (var mask, power) = Value(ops[1], power);
        (var current, power) = Value(ops[2], power);
        var destination = Destination(ops[2]);
        var call = _net.Call(HelperMvm, ("Source", "Input", "Int"), ("Mask", "Input", "Int"), ("Dest", "Input", "Int"), ("Ret_Val", "Return", "Int"));
        Connect(power, call, "en");
        _net.Input(_net.Access(source), call, "Source");
        _net.Input(_net.Access(mask.DataType == "Word" && mask.IsLiteral ? TiaOperand.Literal("Int", ((short)Convert.ToInt32(mask.Display.Substring(3), 16)).ToString(CultureInfo.InvariantCulture)) : mask), call, "Mask");
        _net.Input(_net.Access(current), call, "Dest");
        Store(call, "Ret_Val", destination);
    }

    private void Sqrt(IReadOnlyList<string> ops, Power power)
    {
        (var source, power) = Value(ops[0], power);
        source = AsReal(source, ref power);
        var destination = Destination(ops[1]);
        var box = _net.Part("Sqrt", version: null, disabledEno: false, negated: false, ("SrcType", "Type", "Real"));
        Connect(power, box, "en");
        _net.Input(_net.Access(source), box, "in");
        WriteResult(box, "Real", destination);
    }

    private void Neg(IReadOnlyList<string> ops, Power power)
    {
        (var source, power) = Value(ops[0], power);
        var destination = Destination(ops[1]);
        var type = Wider(source.DataType, destination.DataType);
        if (type == "Real")
        {
            source = AsReal(source, ref power);
        }

        var box = _net.Part("Neg", version: null, disabledEno: true, negated: false, ("SrcType", "Type", type));
        Connect(power, box, "en");
        _net.Input(_net.Access(source), box, "in");
        WriteResult(box, type, destination);
    }

    /// <summary>Connects a box's "out" to the destination, through a REAL temp and CONVERT if the types need it.</summary>
    private void WriteResult(int box, string resultType, TiaOperand destination, string pin = "out")
    {
        if (resultType == "Real" && destination.DataType != "Real")
        {
            var temp = Temp("Real");
            _net.Output(box, pin, _net.Access(temp));
            MoveValue(temp, destination, Power.Of(PowerPin.Of(box, "eno")));
        }
        else
        {
            Store(box, pin, destination);
        }
    }

    /// <summary>SCP: linear scaling with REAL math in SLC_SCP.</summary>
    private void Scp(IReadOnlyList<string> ops, Power power)
    {
        var values = new TiaOperand[5];
        for (var i = 0; i < 5; i++)
        {
            (var value, power) = Value(ops[i], power);
            values[i] = AsReal(value, ref power);
        }

        var destination = Destination(ops[5]);
        var call = _net.Call(HelperScp,
            ("Value", "Input", "Real"), ("InMin", "Input", "Real"), ("InMax", "Input", "Real"),
            ("ScaledMin", "Input", "Real"), ("ScaledMax", "Input", "Real"), ("Ret_Val", "Return", "Real"));
        Connect(power, call, "en");
        var names = new[] { "Value", "InMin", "InMax", "ScaledMin", "ScaledMax" };
        for (var i = 0; i < 5; i++)
        {
            _net.Input(_net.Access(values[i]), call, names[i]);
        }

        WriteResult(call, "Real", destination, "Ret_Val");
    }

    // ---- File instructions ----

    /// <summary>One side of a COP/FLL: file and element (constants, or words for an indirect address) and its element type.</summary>
    private sealed class FileSide
    {
        public FileSide(TiaOperand file, TiaOperand element, bool isFloat)
        {
            File = file;
            Element = element;
            IsFloat = isFloat;
        }

        public TiaOperand File { get; }
        public TiaOperand Element { get; }
        public bool IsFloat { get; }
    }

    /// <summary>
    /// COP #source #destination length: SLC_COP_WORD / SLC_COP_FLOAT with the same file, element and length as the
    /// SLC instruction (the DBs are numbered like the files and keep the SLC layout). Indirect files or elements
    /// (#N[N43:61]:[N43:63]) pass the pointer words.
    /// </summary>
    private void Cop(IReadOnlyList<string> ops, Power power)
    {
        var source = Side(ops[0]);
        var destination = Side(ops[1]);
        var mixed = source.IsFloat != destination.IsFloat;
        var helper = mixed ? HelperCopMixed : destination.IsFloat ? HelperCopFloat : HelperCopWord;
        var parameters = mixed
            ? new[] { ("SrcFile", "Input", "DInt"), ("SrcElement", "Input", "DInt"), ("SrcIsFloat", "Input", "Bool"),
                      ("DstFile", "Input", "DInt"), ("DstElement", "Input", "DInt"), ("DstIsFloat", "Input", "Bool"), ("Length", "Input", "DInt") }
            : new[] { ("SrcFile", "Input", "DInt"), ("SrcElement", "Input", "DInt"), ("DstFile", "Input", "DInt"),
                      ("DstElement", "Input", "DInt"), ("Length", "Input", "DInt") };

        var call = _net.Call(helper, parameters);
        Connect(power, call, "en");
        _net.Input(_net.Access(source.File), call, "SrcFile");
        _net.Input(_net.Access(source.Element), call, "SrcElement");
        _net.Input(_net.Access(destination.File), call, "DstFile");
        _net.Input(_net.Access(destination.Element), call, "DstElement");
        _net.Input(_net.Access(DInt(int.Parse(ops[2], CultureInfo.InvariantCulture))), call, "Length");
        if (mixed)
        {
            _net.Input(_net.Access(TiaOperand.Literal("Bool", source.IsFloat ? "TRUE" : "FALSE")), call, "SrcIsFloat");
            _net.Input(_net.Access(TiaOperand.Literal("Bool", destination.IsFloat ? "TRUE" : "FALSE")), call, "DstIsFloat");
            _notes.Add("COP copies a float's raw words to/from integers. TIA stores the high word of a float first, which may differ "
                       + "from the SLC; check the word order the receiving system expects.");
        }
    }

    private static TiaOperand DInt(int value) => TiaOperand.Literal("DInt", value.ToString(CultureInfo.InvariantCulture));

    /// <summary>FLL value #destination length: SLC_FLL_WORD / SLC_FLL_FLOAT with the SLC instruction's operands.</summary>
    private void Fill(IReadOnlyList<string> ops, Power power)
    {
        (var value, power) = Value(ops[0], power);
        var destination = Side(ops[1]);
        if (destination.IsFloat)
        {
            value = AsReal(value, ref power);
        }

        var call = _net.Call(destination.IsFloat ? HelperFllFloat : HelperFllWord,
            ("Value", "Input", destination.IsFloat ? "Real" : "Int"), ("DstFile", "Input", "DInt"), ("DstElement", "Input", "DInt"), ("Length", "Input", "DInt"));
        Connect(power, call, "en");
        _net.Input(_net.Access(value), call, "Value");
        _net.Input(_net.Access(destination.File), call, "DstFile");
        _net.Input(_net.Access(destination.Element), call, "DstElement");
        _net.Input(_net.Access(DInt(int.Parse(ops[2], CultureInfo.InvariantCulture))), call, "Length");
    }

    private FileSide Side(string text)
    {
        if (text.IndexOf('[') >= 0)
        {
            var indirect = _resolver.Resolve(text);
            if (!indirect.IsAddressed || indirect.AddressBit is not null)
            {
                throw new ManualConversionException($"{text} isn't a file address");
            }

            return new FileSide(indirect.AddressFile, indirect.AddressElement, indirect.DataType == "Real");
        }

        var address = SlcAddress.TryParse(text);
        if (address is null || !address.IsFileReference || address.Bit is not null || address.Member is not null)
        {
            throw new ManualConversionException($"{text} isn't a plain file address");
        }

        if (address.FileType is not ("N" or "B" or "F"))
        {
            throw new ManualConversionException($"block copy of {address.FileType} files");
        }

        return new FileSide(DInt(address.FileNumber), DInt(address.Element), address.FileType == "F");
    }

    // ---- Timers and counters ----

    private void Timer(string mnemonic, IReadOnlyList<string> ops, Power power)
    {
        var timer = SlcAddress.TryParse(ops[0]) ?? throw new ManualConversionException($"bad timer {ops[0]}");
        var instance = _resolver.Instance(timer);
        var usage = Analysis.Timers[timer.WordAddress];

        // A preset that nothing in the logic changes is a constant; otherwise the TON reads the PT that the
        // converted preset moves write.
        TiaOperand preset;
        if (usage.Preset == PresetSource.Constant)
        {
            var counts = decimal.Parse(ops[2], CultureInfo.InvariantCulture);
            preset = TiaOperand.Typed("Time", SlcDataBlockBuilder.FormatTime(counts * BaseMs(timer) / 1000m));
        }
        else
        {
            preset = TiaOperand.Global("Time", instance.Components[0], instance.Components[1], "PT");
        }

        var name = mnemonic switch { "TOF" => "TOF", "RTO" => "TONR", _ => "TON" };
        power = NotBareRail(power);
        var part = _net.InstancePart(name, "1.0", instance.Components, ("time_type", "Type", "Time"));
        Connect(power, part, "IN");
        if (name == "TONR")
        {
            // RES resets it with a separate reset coil, like the SLC.
            _net.Open(part, "R", output: false);
        }

        _net.Input(_net.Access(preset), part, "PT");
        _net.Open(part, "Q", output: true);
        _net.Open(part, "ET", output: true);
    }

    private void Counter(string mnemonic, IReadOnlyList<string> ops, Power power)
    {
        var counter = SlcAddress.TryParse(ops[0]) ?? throw new ManualConversionException($"bad counter {ops[0]}");
        var instance = _resolver.Instance(counter);
        power = NotBareRail(power);
        var part = _net.InstancePart(mnemonic, "1.0", instance.Components, ("value_type", "Type", "Int"));
        Connect(power, part, mnemonic == "CTU" ? "CU" : "CD");
        _net.Open(part, mnemonic == "CTU" ? "R" : "LD", output: false);
        _net.Input(_net.Access(_resolver.Resolve(ops[1])), part, "PV");
        _net.Open(part, mnemonic == "CTU" ? "Q" : "Q", output: true);
        _net.Open(part, "CV", output: true);
    }

    /// <summary>
    /// TIA needs some logic in front of a timer or counter input; for one the SLC ran unconditionally, that's a
    /// contact on S2.AlwaysTrue.
    /// </summary>
    private Power NotBareRail(Power power) =>
        ReferenceEquals(power, Power.Rail) || power.Pin is { IsRail: true }
            ? ContactOn(TiaOperand.Global("Bool", "S2", SlcDataBlockBuilder.AlwaysTrueMember), false, power)
            : power;

    private void Reset(string operand, Power power)
    {
        var address = SlcAddress.TryParse(operand) ?? throw new ManualConversionException($"bad RES operand {operand}");
        switch (address.FileType)
        {
            case "T":
            {
                var coil = _net.Part("ResetIECTimerCoil");
                Connect(power, coil, "in");
                _net.Input(_net.Access(_resolver.Instance(address)), coil, "operand");
                break;
            }

            case "C":
            {
                // Counter reset: clear the count; the done bit follows on the next counter call.
                var instance = _resolver.Instance(address);
                MoveValue(TiaOperand.Literal("Int", "0"), TiaOperand.Global("Int", instance.Components[0], instance.Components[1], "CV"), power);
                break;
            }

            default:
                throw new ManualConversionException($"RES of {address.FileType} elements");
        }
    }

    // ---- Temps ----

    private void StartNetwork()
    {
        _net = new LadNetworkBuilder();
        _prep = new LadNetworkBuilder();
        _prepTail = PowerPin.Rail;
        _branchDepth = 0;
        _notes = new List<string>();
        _tempCounts = new Dictionary<string, int>(StringComparer.Ordinal);
    }

    private TiaOperand Temp(string type)
    {
        _tempCounts[type] = _tempCounts.TryGetValue(type, out var n) ? n + 1 : 1;
        var name = $"tmp{type}{_tempCounts[type]}";
        return TiaOperand.Local(type, name);
    }

    private void FinishNetworkTemps()
    {
        foreach (var pair in _tempCounts)
        {
            var declared = _tempDeclared.TryGetValue(pair.Key, out var n) ? n : 0;
            for (var i = declared + 1; i <= pair.Value; i++)
            {
                _block.Temps.Add(($"tmp{pair.Key}{i}", pair.Key, "Scratch value for type conversions within one network"));
            }

            _tempDeclared[pair.Key] = System.Math.Max(declared, pair.Value);
        }
    }

    private void DeclareTemp(string name, string type, string comment) => _block.Temps.Add((name, type, comment));
}
