using System.Globalization;
using System.IO;
using System.Xml.Linq;

namespace TiaPortalTool.Conversion.Slc;

/// <summary>The result of checking one rung.</summary>
public sealed class RungCheck
{
    public string Block { get; set; } = string.Empty;
    public int File { get; set; }
    public int Rung { get; set; }

    /// <summary>OK, Mismatch, Manual (not converted), or Missing (no network found).</summary>
    public string Status { get; set; } = string.Empty;

    public string Detail { get; set; } = string.Empty;
    public string SlcText { get; set; } = string.Empty;
}

/// <summary>
/// Checks a conversion against what actually landed in TIA Portal. It reads the blocks exported back out of the
/// project, translates every network back into SLC terms (operands, instructions), and compares each rung with the
/// original: every output instruction must be there, and its rung condition must be logically the same, checked by
/// evaluating both conditions over the combinations of their contacts and compares.
/// </summary>
public sealed class SlcVerifier
{
    private static readonly XNamespace Flg = "http://www.siemens.com/automation/Openness/SW/NetworkSource/FlgNet/v5";

    private readonly SlcProgramAnalysis _analysis;
    private readonly Dictionary<string, string> _memberToAddress = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Block, int Byte), string> _wordAtOffset = new();
    private readonly Dictionary<int, string> _dbNames = new();
    private readonly Dictionary<string, int> _fileNumbers;

    public SlcVerifier(SlcProgramAnalysis analysis, SlcDataConversion data, IReadOnlyDictionary<int, string> programBlockNames)
    {
        _analysis = analysis;
        foreach (var mapping in data.AddressMap)
        {
            const string presetSuffix = " (as timer preset)";
            if (mapping.SlcAddress.EndsWith(presetSuffix, StringComparison.Ordinal))
            {
                // TIME_SP.<member>.Output_Seconds stands for the setpoint word.
                _memberToAddress[$"TIME_SP|{mapping.Member.Replace(".Output_Seconds", string.Empty)}"] =
                    mapping.SlcAddress.Substring(0, mapping.SlcAddress.Length - presetSuffix.Length);
                continue;
            }

            if (mapping.Member.Length > 0)
            {
                _memberToAddress[$"{mapping.Block}|{mapping.Member}"] = mapping.SlcAddress;
            }
            else if (mapping.ByteOffset >= 0)
            {
                _wordAtOffset[(mapping.Block, mapping.ByteOffset)] = mapping.SlcAddress;
            }
        }

        foreach (var block in data.DataBlocks)
        {
            _dbNames[block.Number] = block.Name;
        }

        _fileNumbers = programBlockNames.ToDictionary(p => p.Value, p => p.Key, StringComparer.OrdinalIgnoreCase);
    }

    // ---- Expressions ----

    /// <summary>A rung condition: TRUE, an atom (a contact or compare), NOT, AND or OR.</summary>
    private sealed class Expr
    {
        public static readonly Expr True = new() { Kind = 'T' };

        public char Kind { get; private set; }
        public string Atom { get; private set; } = string.Empty;
        public Expr[] Children { get; private set; } = Array.Empty<Expr>();

        public static Expr Of(string atom) => new() { Kind = 'A', Atom = atom };
        public static Expr Not(Expr e) => new() { Kind = '!', Children = new[] { e } };

        public static Expr And(Expr a, Expr b) =>
            a.Kind == 'T' ? b : b.Kind == 'T' ? a : new Expr { Kind = '&', Children = new[] { a, b } };

        public static Expr Or(IEnumerable<Expr> items)
        {
            var list = items.ToList();
            return list.Count == 1 ? list[0] : list.Any(i => i.Kind == 'T') ? True : new Expr { Kind = '|', Children = list.ToArray() };
        }

        public void CollectAtoms(HashSet<string> atoms)
        {
            if (Kind == 'A')
            {
                atoms.Add(Atom);
            }

            foreach (var child in Children)
            {
                child.CollectAtoms(atoms);
            }
        }

        public bool Eval(IReadOnlyDictionary<string, bool> values) => Kind switch
        {
            'T' => true,
            'A' => values[Atom],
            '!' => !Children[0].Eval(values),
            '&' => Children.All(c => c.Eval(values)),
            _ => Children.Any(c => c.Eval(values))
        };
    }

    /// <summary>An output instruction (by its normalized SLC form) and the rung condition that drives it.</summary>
    private sealed class Output
    {
        public Output(string signature, Expr condition)
        {
            Signature = signature;
            Condition = condition;
        }

        public string Signature { get; }
        public Expr Condition { get; }
    }

    // ---- Entry point ----

    public List<RungCheck> Verify(string exportFolder)
    {
        var checks = new List<RungCheck>();
        var exported = LoadExportedNetworks(exportFolder);

        foreach (var file in _analysis.Program.ProgramFiles.Values.Where(f => f.Number >= 2))
        {
            var blockName = _fileNumbers.FirstOrDefault(p => p.Value == file.Number).Key ?? $"file {file.Number}";
            exported.TryGetValue(blockName, out var networks);
            var mcrOpen = false;

            foreach (var rung in file.Rungs)
            {
                var check = new RungCheck { Block = blockName, File = file.Number, Rung = rung.Number, SlcText = rung.Text };
                checks.Add(check);
                var tokenized = _analysis.Rungs[(file.Number, rung.Number)];

                // The SLC side, including whether the rung sits in an MCR zone.
                List<Output>? expected = null;
                string? slcError = null;
                try
                {
                    expected = SlcOutputs(tokenized, ref mcrOpen);
                }
                catch (Exception ex) when (ex is ManualConversionException or InvalidOperationException or KeyNotFoundException or FormatException)
                {
                    slcError = ex.Message;
                }

                if (networks is null || !networks.TryGetValue(rung.Number, out var network))
                {
                    check.Status = "Missing";
                    check.Detail = $"no network titled RUNG {rung.Number} in {blockName}";
                    continue;
                }

                if (network.Title.Contains("MANUAL CONVERSION REQUIRED"))
                {
                    check.Status = "Manual";
                    check.Detail = "not converted; marked for manual conversion";
                    continue;
                }

                if (expected is null)
                {
                    check.Status = "Unchecked";
                    check.Detail = "the verifier can't read this SLC rung: " + slcError;
                    continue;
                }

                List<Output> actual;
                try
                {
                    actual = TiaOutputs(network);
                }
                catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException or FormatException)
                {
                    check.Status = "Unchecked";
                    check.Detail = "the verifier can't read this TIA network: " + ex.Message;
                    continue;
                }

                var problems = Compare(expected, actual);
                check.Status = problems.Count == 0 ? "OK" : "Mismatch";
                check.Detail = string.Join("; ", problems);
            }
        }

        return checks;
    }

    // ---- Exported networks ----

    private sealed class ExportedNetwork
    {
        public string Title { get; set; } = string.Empty;
        public XElement? FlgNet { get; set; }

        /// <summary>The "values" network just before this one, if any (it fills temps this network reads).</summary>
        public XElement? Values { get; set; }
    }

    /// <summary>Block name -> rung number -> network, from the exported block XML files.</summary>
    private static Dictionary<string, Dictionary<int, ExportedNetwork>> LoadExportedNetworks(string folder)
    {
        var result = new Dictionary<string, Dictionary<int, ExportedNetwork>>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.GetFiles(folder, "*.xml", SearchOption.AllDirectories))
        {
            var document = XDocument.Load(path);
            var block = document.Root?.Elements().FirstOrDefault(e => e.Name.LocalName is "SW.Blocks.FC");
            var name = block?.Element("AttributeList")?.Element("Name")?.Value;
            if (block is null || name is null)
            {
                continue;
            }

            var networks = new Dictionary<int, ExportedNetwork>();
            XElement? pendingValues = null;
            foreach (var unit in block.Descendants("SW.Blocks.CompileUnit"))
            {
                var title = unit.Descendants("MultilingualText").Where(m => (string?)m.Attribute("CompositionName") == "Title")
                    .SelectMany(m => m.Descendants("Text")).Select(t => t.Value).FirstOrDefault(t => t.Length > 0) ?? string.Empty;
                var flg = unit.Element("AttributeList")?.Element("NetworkSource")?.Element(Flg + "FlgNet");
                if (!title.StartsWith("RUNG ", StringComparison.Ordinal))
                {
                    continue;
                }

                var numberText = new string(title.Substring(5).TakeWhile(char.IsDigit).ToArray());
                if (!int.TryParse(numberText, out var number))
                {
                    continue;
                }

                if (title.EndsWith("(values)", StringComparison.Ordinal))
                {
                    pendingValues = flg;
                    continue;
                }

                networks[number] = new ExportedNetwork { Title = title, FlgNet = flg, Values = pendingValues };
                pendingValues = null;
            }

            result[name] = networks;
        }

        return result;
    }

    // ---- SLC side ----

    private List<Output> SlcOutputs(TokenizedRung tokenized, ref bool mcrOpen)
    {
        if (tokenized.Error is not null)
        {
            throw new InvalidOperationException(tokenized.Error);
        }

        var outputs = new List<Output>();
        var elements = tokenized.Elements;
        var mcrIndex = elements.FindIndex(e => e.Mnemonic == "MCR");
        if (mcrIndex >= 0)
        {
            if (mcrOpen && mcrIndex == 0)
            {
                mcrOpen = false;
                return outputs;   // zone end: nothing in TIA
            }

            mcrOpen = true;
            var index = 0;
            var condition = Sequence(elements.Take(mcrIndex).ToList(), ref index, Expr.True, outputs);
            outputs.Add(new Output("MCR", condition));
            return outputs;
        }

        var start = mcrOpen ? Expr.Of("MCR") : Expr.True;
        var i = 0;
        Sequence(elements, ref i, start, outputs);
        return outputs;
    }

    private Expr Sequence(List<RungElement> elements, ref int index, Expr power, List<Output> outputs)
    {
        while (index < elements.Count)
        {
            var element = elements[index];
            if (element.Mnemonic is "NXB" or "BND")
            {
                return power;
            }

            index++;
            if (element.Mnemonic == "BST")
            {
                var ends = new List<Expr>();
                while (true)
                {
                    ends.Add(Sequence(elements, ref index, power, outputs));
                    var marker = elements[index++].Mnemonic;
                    if (marker == "BND")
                    {
                        break;
                    }
                }

                power = ends.Any(e => ReferenceEquals(e, power)) ? power : Expr.Or(ends.Distinct());
                continue;
            }

            power = SlcInstruction(element, power, outputs);
        }

        return power;
    }

    private Expr SlcInstruction(RungElement element, Expr power, List<Output> outputs)
    {
        // "#" outside COP/FLL is indexed addressing; the conversion uses the address without the S:24 offset.
        var ops = element.Mnemonic is "COP" or "FLL"
            ? element.Operands
            : element.Operands.Select(o => o.TrimStart('#')).ToList();
        switch (element.Mnemonic)
        {
            case "XIC":
            case "XIO":
            {
                var atom = SlcBit(ops[0]);
                return Expr.And(power, element.Mnemonic == "XIC" ? atom : Expr.Not(atom));
            }

            case "OTE":
            case "OTL":
            case "OTU":
                if (SlcAddress.TryParse(ops[0]) is { } trap && SlcStatusFile.IsOverflowTrap(trap))
                {
                    return power;   // dropped on purpose: TIA doesn't fault on overflow
                }

                outputs.Add(new Output($"{element.Mnemonic} {Norm(ops[0])}", power));
                return power;

            case "ONS":
            case "OSR":
                outputs.Add(new Output($"ONS {Norm(ops[0])}", power));
                return Expr.Of($"ONS {Norm(ops[0])}");

            case "EQU": return Expr.And(power, Expr.Of(CompareAtom("EQU", ops[0], ops[1])));
            case "NEQ": return Expr.And(power, Expr.Of(CompareAtom("NEQ", ops[0], ops[1])));
            case "LES": return Expr.And(power, Expr.Of(CompareAtom("LES", ops[0], ops[1])));
            case "LEQ": return Expr.And(power, Expr.Of(CompareAtom("LEQ", ops[0], ops[1])));
            case "GRT": return Expr.And(power, Expr.Of(CompareAtom("GRT", ops[0], ops[1])));
            case "GEQ": return Expr.And(power, Expr.Of(CompareAtom("GEQ", ops[0], ops[1])));

            case "LIM" when SlcOperandResolver.TryConstant(ops[0]) is null || SlcOperandResolver.TryConstant(ops[2]) is null:
            {
                // Variable limits: inside the band when low <= high, outside it when low > high.
                var normal = Expr.And(Expr.Of(CompareAtom("LEQ", ops[0], ops[2])), Expr.Of($"LIM {Norm(ops[0])} {Norm(ops[1])} {Norm(ops[2])}"));
                var reversed = Expr.And(Expr.Of(CompareAtom("GRT", ops[0], ops[2])),
                    Expr.Or(new[] { Expr.Of(CompareAtom("GEQ", ops[1], ops[0])), Expr.Of(CompareAtom("LEQ", ops[1], ops[2])) }));
                return Expr.And(power, Expr.Or(new[] { normal, reversed }));
            }

            case "LIM":
            {
                var low = double.Parse(Norm(ops[0]), CultureInfo.InvariantCulture);
                var high = double.Parse(Norm(ops[2]), CultureInfo.InvariantCulture);
                return Expr.And(power, low <= high
                    ? Expr.Of($"LIM {Norm(ops[0])} {Norm(ops[1])} {Norm(ops[2])}")
                    : Expr.Or(new[] { Expr.Of(CompareAtom("GEQ", ops[1], ops[0])), Expr.Of(CompareAtom("LEQ", ops[1], ops[2])) }));
            }

            case "CLR":
                outputs.Add(new Output($"MOV 0 {Norm(ops[0])}", power));
                return power;

            case "NOT":
                outputs.Add(new Output($"XOR {Norm(ops[0])} -1 {Norm(ops[1])}", power));
                return power;

            case "RES":
            {
                var address = SlcAddress.TryParse(ops[0])!;
                outputs.Add(new Output(address.FileType == "C" ? $"MOV 0 {address}.ACC" : $"RES {Norm(ops[0])}", power));
                return power;
            }

            case "TON":
            case "TOF":
            case "RTO":
                outputs.Add(new Output($"{element.Mnemonic} {Norm(ops[0])} {SlcPreset(ops)}", power));
                return power;

            case "CTU":
            case "CTD":
                outputs.Add(new Output($"{element.Mnemonic} {Norm(ops[0])} {Norm(ops[1])}", power));
                return power;

            case "SBR":
            case "NOP":
            case "END":
                return power;

            default:
                if (element.Info is { } info && (info.Writes.Length > 0 || element.Mnemonic == "JSR"))
                {
                    outputs.Add(new Output($"{element.Mnemonic} {string.Join(" ", ops.Select(Norm))}", power));
                    return power;
                }

                throw new InvalidOperationException($"{element.Mnemonic} isn't checked yet");
        }
    }

    /// <summary>A timer's TT bit is IN AND NOT DN in the converted logic; everything else is an atom.</summary>
    private static Expr SlcBit(string operand)
    {
        var address = SlcAddress.TryParse(operand);
        if (address is { FileType: "T", Member: "TT" })
        {
            var timer = address.WordAddress;
            return Expr.And(Expr.Of($"{timer}/EN"), Expr.Not(Expr.Of($"{timer}/DN")));
        }

        return Expr.Of(Norm(operand));
    }

    private string SlcPreset(IReadOnlyList<string> ops)
    {
        var timer = SlcAddress.TryParse(ops[0])!.WordAddress;
        var usage = _analysis.Timers[timer];
        if (usage.Preset != PresetSource.Constant || usage.Timebase is null)
        {
            return "dynamic";
        }

        return ((long)(decimal.Parse(ops[2], CultureInfo.InvariantCulture) * usage.Timebase.Value * 1000m)).ToString(CultureInfo.InvariantCulture) + "ms";
    }

    // ---- TIA side ----

    private List<Output> TiaOutputs(ExportedNetwork network)
    {
        var temps = new Dictionary<string, string>(StringComparer.Ordinal);
        if (network.Values is not null)
        {
            new TiaNetwork(this, network.Values, temps).Evaluate();
        }

        return network.FlgNet is null ? new List<Output>() : new TiaNetwork(this, network.FlgNet, temps).Evaluate();
    }

    /// <summary>One exported FlgNet, evaluated into outputs and conditions.</summary>
    private sealed class TiaNetwork
    {
        private readonly SlcVerifier _owner;
        private readonly Dictionary<string, XElement> _accesses = new(StringComparer.Ordinal);
        private readonly Dictionary<string, XElement> _parts = new(StringComparer.Ordinal);
        private readonly Dictionary<(string Part, string Pin), string> _powerSource = new();   // input pin -> source "uid:pin" or "rail"
        private readonly Dictionary<(string Part, string Pin), string> _operandIn = new();     // input pin -> access uid
        private readonly Dictionary<(string Part, string Pin), string> _operandOut = new();    // output pin -> access uid
        private readonly Dictionary<string, Expr> _cache = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _temps;
        private readonly List<Output> _outputs = new();

        public TiaNetwork(SlcVerifier owner, XElement flgNet, Dictionary<string, string> temps)
        {
            _owner = owner;
            _temps = temps;
            foreach (var access in flgNet.Element(Flg + "Parts")!.Elements(Flg + "Access"))
            {
                _accesses[(string)access.Attribute("UId")!] = access;
            }

            foreach (var part in flgNet.Element(Flg + "Parts")!.Elements().Where(e => e.Name.LocalName is "Part" or "Call"))
            {
                _parts[(string)part.Attribute("UId")!] = part;
            }

            foreach (var wire in flgNet.Element(Flg + "Wires")!.Elements(Flg + "Wire"))
            {
                var ends = wire.Elements().ToList();
                var first = ends[0];
                foreach (var target in ends.Skip(1))
                {
                    if (target.Name.LocalName == "NameCon")
                    {
                        var key = ((string)target.Attribute("UId")!, (string)target.Attribute("Name")!);
                        switch (first.Name.LocalName)
                        {
                            case "Powerrail":
                                _powerSource[key] = "rail";
                                break;
                            case "NameCon":
                                _powerSource[key] = $"{(string)first.Attribute("UId")!}:{(string)first.Attribute("Name")!}";
                                break;
                            case "IdentCon":
                                _operandIn[key] = (string)first.Attribute("UId")!;
                                break;
                        }
                    }
                    else if (target.Name.LocalName == "IdentCon" && first.Name.LocalName == "NameCon")
                    {
                        _operandOut[((string)first.Attribute("UId")!, (string)first.Attribute("Name")!)] = (string)target.Attribute("UId")!;
                    }
                }
            }
        }

        public List<Output> Evaluate()
        {
            // Parts in document order; temps are filled by earlier parts and read by later ones.
            foreach (var pair in _parts)
            {
                Visit(pair.Key, pair.Value);
            }

            return _outputs;
        }

        private string Name(XElement part) =>
            part.Name.LocalName == "Call" ? (string)part.Element(Flg + "CallInfo")!.Attribute("Name")! : (string)part.Attribute("Name")!;

        /// <summary>The SLC address a helper's File/Element/Bit inputs stand for, e.g. N91:[N7:56] or B3:70/[N7:200].</summary>
        private string Addressed(string uid, string letterForIndirectFile) =>
            _owner.AddressText(In(uid, "File")!, In(uid, "Element")!, In(uid, "Bit"), letterForIndirectFile);

        private string? In(string part, string pin) => _operandIn.TryGetValue((part, pin), out var access) ? Operand(access) : null;

        private string? OutAccess(string part, string pin) => _operandOut.TryGetValue((part, pin), out var access) ? access : null;

        private void Visit(string uid, XElement part)
        {
            var name = Name(part);
            switch (name)
            {
                case "Coil":
                case "SCoil":
                case "RCoil":
                {
                    var operand = In(uid, "operand")!;
                    var condition = Power(uid, "in");
                    _outputs.Add(operand == "MCR"
                        ? new Output("MCR", condition)
                        : new Output($"{(name == "Coil" ? "OTE" : name == "SCoil" ? "OTL" : "OTU")} {operand}", condition));
                    break;
                }

                case "PBox":
                    _outputs.Add(new Output($"ONS {In(uid, "bit")}", Power(uid, "in")));
                    break;

                case "ResetIECTimerCoil":
                    _outputs.Add(new Output($"RES {In(uid, "operand")}", Power(uid, "in")));
                    break;

                case "TON":
                case "TOF":
                case "TONR":
                {
                    var timer = Instance(part);
                    var preset = In(uid, "PT")!;
                    var presetSig = preset.EndsWith(".PRE", StringComparison.Ordinal) ? "dynamic" : TimeMs(preset) + "ms";
                    _outputs.Add(new Output($"{(name == "TONR" ? "RTO" : name)} {timer} {presetSig}", Power(uid, "IN")));
                    break;
                }

                case "CTU":
                case "CTD":
                    _outputs.Add(new Output($"{name} {Instance(part)} {In(uid, "PV")}", Power(uid, name == "CTU" ? "CU" : "CD")));
                    break;

                case "Move":
                case "Convert":
                    Write(uid, name == "Move" ? "out1" : "out", "MOV", In(uid, "in")!, null, Power(uid, "en"));
                    break;

                case "Add":
                case "Sub":
                case "Mul":
                case "Div":
                case "And":
                case "Or":
                case "Xor":
                {
                    var mnemonic = name.ToUpperInvariant();
                    Write(uid, "out", mnemonic, In(uid, "in1")!, In(uid, "in2"), Power(uid, "en"));
                    break;
                }

                case "Sqrt":
                    Write(uid, "out", "SQR", In(uid, "in")!, null, Power(uid, "en"));
                    break;

                case "Neg":
                    Write(uid, "out", "NEG", In(uid, "in")!, null, Power(uid, "en"));
                    break;

                case SlcLadderTranslator.HelperFromTime:
                case SlcLadderTranslator.HelperWordRead:
                case SlcLadderTranslator.HelperFloatRead:
                case SlcLadderTranslator.HelperBitRead:
                {
                    // Reads a value into a temp: remember what the temp stands for.
                    var value = name == SlcLadderTranslator.HelperFromTime
                        ? In(uid, "Value")!
                        : Addressed(uid, name == SlcLadderTranslator.HelperFloatRead ? "F" : "N");
                    var temp = OutAccess(uid, "Ret_Val");
                    if (temp is not null)
                    {
                        _temps[LocalName(temp)] = value;
                    }

                    break;
                }

                case SlcLadderTranslator.HelperBitSet:
                case SlcLadderTranslator.HelperBitReset:
                    _outputs.Add(new Output($"{(name == SlcLadderTranslator.HelperBitSet ? "OTL" : "OTU")} {Addressed(uid, "B")}", Power(uid, "en")));
                    break;

                case SlcLadderTranslator.HelperMvm:
                    WriteText(uid, "Ret_Val", $"MVM {In(uid, "Source")} {In(uid, "Mask")}", Power(uid, "en"));
                    break;

                case SlcLadderTranslator.HelperWordWrite:
                case SlcLadderTranslator.HelperFloatWrite:
                {
                    var destination = Addressed(uid, name == SlcLadderTranslator.HelperFloatWrite ? "F" : "N");
                    var value = In(uid, "Value")!;
                    _outputs.Add(new Output(value.Contains(' ') ? $"{value} {destination}" : $"MOV {value} {destination}", Power(uid, "en")));
                    break;
                }

                case SlcLadderTranslator.HelperToTime:
                    Write(uid, "Ret_Val", "MOV", In(uid, "Counts")!, null, Power(uid, "en"));
                    break;

                case SlcLadderTranslator.HelperScp:
                {
                    var args = string.Join(" ", new[] { "Value", "InMin", "InMax", "ScaledMin", "ScaledMax" }.Select(p => In(uid, p)));
                    WriteText(uid, "Ret_Val", $"SCP {args}", Power(uid, "en"));
                    break;
                }

                case SlcLadderTranslator.HelperCopWord:
                case SlcLadderTranslator.HelperCopFloat:
                case SlcLadderTranslator.HelperCopMixed:
                {
                    var srcLetter = name == SlcLadderTranslator.HelperCopFloat || In(uid, "SrcIsFloat") == "TRUE" ? "F" : "N";
                    var dstLetter = name == SlcLadderTranslator.HelperCopFloat || In(uid, "DstIsFloat") == "TRUE" ? "F" : "N";
                    _outputs.Add(new Output(
                        $"COP #{_owner.AddressText(In(uid, "SrcFile")!, In(uid, "SrcElement")!, null, srcLetter)} "
                        + $"#{_owner.AddressText(In(uid, "DstFile")!, In(uid, "DstElement")!, null, dstLetter)} {In(uid, "Length")}",
                        Power(uid, "en")));
                    break;
                }

                case SlcLadderTranslator.HelperFllWord:
                case SlcLadderTranslator.HelperFllFloat:
                    _outputs.Add(new Output(
                        $"FLL {In(uid, "Value")} #{_owner.AddressText(In(uid, "DstFile")!, In(uid, "DstElement")!, null, name == SlcLadderTranslator.HelperFllFloat ? "F" : "N")} {In(uid, "Length")}",
                        Power(uid, "en")));
                    break;

                default:
                    if (part.Name.LocalName == "Call" && _owner._fileNumbers.TryGetValue(name, out var file))
                    {
                        _outputs.Add(new Output($"JSR {file}", Power(uid, "en")));
                    }

                    // Contacts, compares, OR and the like only matter through the conditions they produce.
                    break;
            }
        }

        /// <summary>
        /// A box result: into a temp it becomes what the temp stands for; into an operand it's an output. A REAL result
        /// converted into the destination (integer DIV, REAL math into an integer) reads as the original instruction.
        /// </summary>
        private void Write(string uid, string pin, string mnemonic, string a, string? b, Expr condition)
        {
            var text = mnemonic == "MOV" ? a : b is null ? $"{mnemonic} {a}" : $"{mnemonic} {a} {b}";
            WriteText(uid, pin, mnemonic == "MOV" && a.Contains(' ') ? a : text, condition, plainMove: mnemonic == "MOV" && !a.Contains(' '));
        }

        private void WriteText(string uid, string pin, string text, Expr condition, bool plainMove = false)
        {
            var access = OutAccess(uid, pin);
            if (access is null)
            {
                return;
            }

            if (IsLocal(access))
            {
                _temps[LocalName(access)] = text;
                return;
            }

            var destination = Operand(access);
            if (plainMove && text.StartsWith("T#", StringComparison.Ordinal) && destination.EndsWith(".PRE", StringComparison.Ordinal))
            {
                // A constant preset: back to timebase counts.
                text = _owner.TimeToCounts(destination.Substring(0, destination.Length - 4), text);
            }

            _outputs.Add(new Output(plainMove ? $"MOV {text} {destination}" : $"{text} {destination}", condition));
        }

        private Expr Power(string part, string pin)
        {
            if (!_powerSource.TryGetValue((part, pin), out var source))
            {
                return Expr.True;   // unconnected: treated as always on
            }

            if (source == "rail")
            {
                return Expr.True;
            }

            if (_cache.TryGetValue(source, out var cached))
            {
                return cached;
            }

            var split = source.LastIndexOf(':');
            var uid = source.Substring(0, split);
            var outPin = source.Substring(split + 1);
            var element = _parts[uid];
            var name = Name(element);
            Expr result;
            if (outPin == "eno")
            {
                result = Power(uid, "en");   // a box passes its enable through
            }
            else
            {
                result = name switch
                {
                    "Contact" => Contact(uid, element),
                    "O" => Expr.Or(element.Elements(Flg + "TemplateValue").Where(t => (string)t.Attribute("Name")! == "Card")
                        .Select(t => int.Parse(t.Value, CultureInfo.InvariantCulture))
                        .SelectMany(n => Enumerable.Range(1, n)).Select(i => Power(uid, "in" + i))),
                    "PBox" => Expr.Of($"ONS {In(uid, "bit")}"),
                    "Eq" or "Ne" or "Lt" or "Le" or "Gt" or "Ge" => Expr.And(Power(uid, "pre"), Expr.Of(_owner.TiaCompareAtom(name, In(uid, "in1")!, In(uid, "in2")!))),
                    "InRange" => Expr.And(Power(uid, "pre"), Expr.Of($"LIM {In(uid, "min")} {In(uid, "in")} {In(uid, "max")}")),
                    _ => throw new InvalidOperationException($"power from {name}.{outPin}")
                };
            }

            _cache[source] = result;
            return result;
        }

        private Expr Contact(string uid, XElement element)
        {
            var operand = In(uid, "operand")!;
            var input = Power(uid, "in");
            var atom = operand == "TRUE" ? Expr.True : Expr.Of(operand);
            return Expr.And(input, element.Element(Flg + "Negated") is null ? atom : Expr.Not(atom));
        }

        private string Instance(XElement part)
        {
            var components = part.Element(Flg + "Instance")!.Elements(Flg + "Component").Select(c => (string)c.Attribute("Name")!).ToList();
            return _owner.MemberAddress(components[0], components[1]);
        }

        private bool IsLocal(string access) => (string)_accesses[access].Attribute("Scope")! == "LocalVariable";

        private string LocalName(string access) => (string)_accesses[access].Descendants(Flg + "Component").First().Attribute("Name")!;

        /// <summary>An operand in SLC terms.</summary>
        private string Operand(string accessId)
        {
            var access = _accesses[accessId];
            var scope = (string)access.Attribute("Scope")!;
            if (scope == "LocalVariable")
            {
                var name = LocalName(accessId);
                if (name.StartsWith("MCR_Zone", StringComparison.Ordinal))
                {
                    return "MCR";
                }

                return _temps.TryGetValue(name, out var stands) ? stands : "#" + name;
            }

            if (scope is "LiteralConstant" or "TypedConstant")
            {
                var value = access.Descendants(Flg + "ConstantValue").First().Value;
                return value.StartsWith("T#", StringComparison.OrdinalIgnoreCase) ? value.ToUpperInvariant() : Norm(value);
            }

            var components = access.Descendants(Flg + "Component").ToList();
            var block = (string)components[0].Attribute("Name")!;
            var member = (string)components[1].Attribute("Name")!;
            if (block == "S2" && member == SlcDataBlockBuilder.AlwaysTrueMember)
            {
                return "TRUE";
            }

            if (block == "S2" && member == SlcDataBlockBuilder.FirstScanMember)
            {
                return "S:1/15";
            }

            var address = _owner.MemberAddress(block, member);
            var slice = (string?)components[1].Attribute("SliceAccessModifier");
            if (slice is not null)
            {
                return $"{address}/{slice.Substring(1)}";
            }

            if (components.Count > 2)
            {
                var sub = (string)components[2].Attribute("Name")!;
                return sub switch
                {
                    "Q" when address.StartsWith("T", StringComparison.Ordinal) => $"{address}/DN",
                    "IN" => $"{address}/EN",
                    "PT" => $"{address}.PRE",
                    "ET" => $"{address}.ACC",
                    "QU" => $"{address}/DN",
                    "CU" or "CD" => $"{address}/{sub}",
                    "PV" => $"{address}.PRE",
                    "CV" => $"{address}.ACC",
                    "Output_Seconds" => address,
                    _ => $"{address}.{sub}"
                };
            }

            return address;
        }

        private static long TimeMs(string literal)
        {
            // T#1H_2M_3S_400MS
            var text = literal.Substring(2);
            long total = 0;
            foreach (var part in text.Split('_'))
            {
                var digits = new string(part.TakeWhile(char.IsDigit).ToArray());
                var unit = part.Substring(digits.Length);
                var value = long.Parse(digits, CultureInfo.InvariantCulture);
                total += unit switch { "D" => value * 86_400_000, "H" => value * 3_600_000, "M" => value * 60_000, "S" => value * 1000, _ => value };
            }

            return total;
        }
    }

    // ---- Shared normalization ----

    private string MemberAddress(string block, string member) =>
        _memberToAddress.TryGetValue($"{block}|{member}", out var address) ? address : throw new KeyNotFoundException($"\"{block}\".{member} isn't a converted address");

    private string WordAddress(int db, int element)
    {
        var block = _dbNames.TryGetValue(db, out var name) ? name : throw new KeyNotFoundException($"DB{db}");
        return _wordAtOffset.TryGetValue((block, element * 2), out var address) ? address : $"{block}:{element}";
    }

    private string TimeToCounts(string timer, string literal)
    {
        var address = SlcAddress.TryParse(timer);
        if (address is null || !_analysis.Timers.TryGetValue(address, out var usage) || usage.Timebase is not { } seconds)
        {
            return literal;
        }

        return Norm((TiaNetworkTimeMs(literal) / (seconds * 1000m)).ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// SLC address text from file, element and bit arguments that are numbers or pointer words:
    /// (3, 16) -> B3:16, (91, N7:56) -> N91:[N7:56], (N43:61, N43:63) -> N[N43:61]:[N43:63].
    /// </summary>
    private string AddressText(string file, string element, string? bit, string letterForIndirectFile)
    {
        var fileIsNumber = int.TryParse(file, NumberStyles.Integer, CultureInfo.InvariantCulture, out var fileNumber);
        var elementIsNumber = int.TryParse(element, NumberStyles.Integer, CultureInfo.InvariantCulture, out var elementNumber);
        if (fileIsNumber && elementIsNumber && bit is null)
        {
            return WordAddress(fileNumber, elementNumber);
        }

        var fileText = fileIsNumber ? FileName(file) : $"{letterForIndirectFile}[{file}]";
        var text = $"{fileText}:{(elementIsNumber ? element : $"[{element}]")}";
        if (bit is not null)
        {
            text += "/" + (int.TryParse(bit, out _) ? bit : $"[{bit}]");
        }

        return text;
    }

    private string FileName(string number) =>
        _dbNames.TryGetValue(int.Parse(number, CultureInfo.InvariantCulture), out var name) ? name : "DB" + number;

    /// <summary>Compare atoms in one canonical form, so "GRT a b" and "LES b a" are the same atom.</summary>
    private string CompareAtom(string kind, string left, string right) => CanonicalCompare(kind, Norm(left), Norm(right), left);

    private string TiaCompareAtom(string kind, string left, string right)
    {
        var slc = kind switch { "Eq" => "EQU", "Ne" => "NEQ", "Lt" => "LES", "Le" => "LEQ", "Gt" => "GRT", _ => "GEQ" };

        // A timer compared with a TIME constant: back to timebase counts.
        if (right.StartsWith("T#", StringComparison.Ordinal) && SlcAddress.TryParse(left) is { FileType: "T" } timer
            && _analysis.Timers.TryGetValue(timer.WordAddress, out var usage) && usage.Timebase is { } seconds)
        {
            var ms = (decimal)TiaNetworkTimeMs(right);
            right = Norm((ms / (seconds * 1000m)).ToString(CultureInfo.InvariantCulture));
        }

        return CanonicalCompare(slc, left, right, left);
    }

    private static long TiaNetworkTimeMs(string literal)
    {
        long total = 0;
        foreach (var part in literal.Substring(2).Split('_'))
        {
            var digits = new string(part.TakeWhile(char.IsDigit).ToArray());
            var value = long.Parse(digits, CultureInfo.InvariantCulture);
            total += part.Substring(digits.Length) switch { "D" => value * 86_400_000, "H" => value * 3_600_000, "M" => value * 60_000, "S" => value * 1000, _ => value };
        }

        return total;
    }

    private static string CanonicalCompare(string kind, string a, string b, string _)
    {
        if (string.CompareOrdinal(a, b) <= 0)
        {
            return $"{kind} {a} {b}";
        }

        var mirrored = kind switch { "LES" => "GRT", "GRT" => "LES", "LEQ" => "GEQ", "GEQ" => "LEQ", _ => kind };
        return $"{mirrored} {b} {a}";
    }

    /// <summary>SLC operand text in one form: addresses canonical, numbers without formatting differences.</summary>
    private static string Norm(string text)
    {
        if (text.EndsWith("h", StringComparison.OrdinalIgnoreCase) && int.TryParse(text.Substring(0, text.Length - 1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex))
        {
            return ((short)hex).ToString(CultureInfo.InvariantCulture);
        }

        if (text.StartsWith("16#", StringComparison.Ordinal) && int.TryParse(text.Substring(3), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex2))
        {
            return ((short)hex2).ToString(CultureInfo.InvariantCulture);
        }

        if (decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            return number.ToString("0.############", CultureInfo.InvariantCulture);
        }

        var address = SlcAddress.TryParse(text);
        return address?.ToString() ?? text;
    }

    // ---- Comparing ----

    private static List<string> Compare(List<Output> expected, List<Output> actual)
    {
        var problems = new List<string>();
        var remaining = actual.ToList();
        foreach (var output in expected)
        {
            var match = remaining.FirstOrDefault(a => a.Signature == output.Signature);
            if (match is null)
            {
                problems.Add($"missing: {output.Signature}");
                continue;
            }

            remaining.Remove(match);
            if (!SameCondition(output.Condition, match.Condition))
            {
                problems.Add($"different condition for {output.Signature}");
            }
        }

        problems.AddRange(remaining.Select(a => $"extra: {a.Signature}"));
        return problems;
    }

    /// <summary>
    /// Logical equivalence of two conditions: every combination of their atoms when there are up to 14, otherwise
    /// 20,000 random combinations.
    /// </summary>
    private static bool SameCondition(Expr a, Expr b)
    {
        var atoms = new HashSet<string>(StringComparer.Ordinal);
        a.CollectAtoms(atoms);
        b.CollectAtoms(atoms);
        var list = atoms.ToList();
        var values = new Dictionary<string, bool>(StringComparer.Ordinal);

        if (list.Count <= 14)
        {
            for (var mask = 0; mask < 1 << list.Count; mask++)
            {
                for (var i = 0; i < list.Count; i++)
                {
                    values[list[i]] = (mask >> i & 1) == 1;
                }

                if (a.Eval(values) != b.Eval(values))
                {
                    return false;
                }
            }

            return true;
        }

        var random = new Random(12345);
        for (var trial = 0; trial < 20000; trial++)
        {
            foreach (var atom in list)
            {
                values[atom] = random.Next(2) == 1;
            }

            if (a.Eval(values) != b.Eval(values))
            {
                return false;
            }
        }

        return true;
    }
}
