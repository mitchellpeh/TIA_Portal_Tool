using System.Globalization;
using System.Xml.Linq;

namespace TiaPortalTool.Conversion.Slc;

/// <summary>A point in a LAD network that carries power: the left rail, or an output pin of a part.</summary>
public sealed class PowerPin
{
    private PowerPin(int partId, string pin)
    {
        PartId = partId;
        Pin = pin;
    }

    public static readonly PowerPin Rail = new(-1, string.Empty);

    public int PartId { get; }
    public string Pin { get; }
    public bool IsRail => PartId < 0;

    public static PowerPin Of(int partId, string pin) => new(partId, pin);

    public override bool Equals(object? obj) => obj is PowerPin other && other.PartId == PartId && other.Pin == Pin;
    public override int GetHashCode() => PartId * 31 + Pin.GetHashCode();
}

/// <summary>
/// Builds one LAD network as a SimaticML FlgNet: operands (Access), instructions (Part/Call) and the wires between
/// them. A pin that feeds several inputs becomes one wire with several targets, which TIA Portal draws as parallel
/// branches; parallel branches are joined with an "O" part.
/// </summary>
public sealed class LadNetworkBuilder
{
    private static readonly XNamespace Ns = "http://www.siemens.com/automation/Openness/SW/NetworkSource/FlgNet/v5";

    private readonly List<XElement> _accesses = new();
    private readonly List<XElement> _parts = new();

    // Power wires: source pin -> input pins it feeds, in creation order.
    private readonly List<PowerPin> _powerSources = new();
    private readonly Dictionary<PowerPin, List<(int Part, string Pin)>> _powerTargets = new();

    // Operand wires: (access, part, pin, access is the target).
    private readonly List<(int Access, int Part, string Pin, bool Output)> _operandWires = new();

    // Pins left unconnected on purpose (unused timer outputs, an unused reset input).
    private readonly List<(int Part, string Pin, bool Output)> _openPins = new();

    // Temporary ids: accesses and parts share one counter; they're renumbered when the network is written.
    private int _nextId = 1;

    public bool IsEmpty => _parts.Count == 0;

    // ---- Operands ----

    public int Access(TiaOperand operand)
    {
        var id = _nextId++;
        _accesses.Add(operand.ToAccess(Ns, id));
        return id;
    }

    // ---- Parts ----

    private int AddPart(XElement part)
    {
        var id = _nextId++;
        part.SetAttributeValue("UId", id);
        _parts.Add(part);
        return id;
    }

    public int Part(string name, params (string Name, string Type, string Value)[] templates) =>
        Part(name, version: null, disabledEno: null, negated: false, templates);

    public int Part(string name, string? version, bool? disabledEno, bool negated, params (string Name, string Type, string Value)[] templates)
    {
        var part = new XElement(Ns + "Part", new XAttribute("Name", name));
        if (version is not null)
        {
            part.Add(new XAttribute("Version", version));
        }

        if (disabledEno is not null)
        {
            part.Add(new XAttribute("DisabledENO", disabledEno.Value ? "true" : "false"));
        }

        if (negated)
        {
            part.Add(new XElement(Ns + "Negated", new XAttribute("Name", "operand")));
        }

        foreach (var (templateName, type, value) in templates)
        {
            part.Add(new XElement(Ns + "TemplateValue", new XAttribute("Name", templateName), new XAttribute("Type", type), value));
        }

        return AddPart(part);
    }

    /// <summary>An instruction with an instance (TON, TONR, CTU) whose instance is a DB member, e.g. "T4".T4_1.</summary>
    public int InstancePart(string name, string version, IReadOnlyList<string> instance, params (string Name, string Type, string Value)[] templates)
    {
        var part = new XElement(Ns + "Part", new XAttribute("Name", name), new XAttribute("Version", version));
        var instanceElement = new XElement(Ns + "Instance", new XAttribute("Scope", "GlobalVariable"), new XAttribute("UId", 0));
        foreach (var component in instance)
        {
            instanceElement.Add(new XElement(Ns + "Component", new XAttribute("Name", component)));
        }

        part.Add(instanceElement);
        foreach (var (templateName, type, value) in templates)
        {
            part.Add(new XElement(Ns + "TemplateValue", new XAttribute("Name", templateName), new XAttribute("Type", type), value));
        }

        return AddPart(part);
    }

    /// <summary>A call of an FC; parameters are (name, section, type).</summary>
    public int Call(string blockName, params (string Name, string Section, string Type)[] parameters)
    {
        var info = new XElement(Ns + "CallInfo", new XAttribute("Name", blockName), new XAttribute("BlockType", "FC"));
        foreach (var (name, section, type) in parameters)
        {
            info.Add(new XElement(Ns + "Parameter", new XAttribute("Name", name), new XAttribute("Section", section), new XAttribute("Type", type)));
        }

        return AddPart(new XElement(Ns + "Call", info));
    }

    // ---- Wires ----

    public void Power(PowerPin source, int part, string pin)
    {
        if (!_powerTargets.TryGetValue(source, out var targets))
        {
            targets = new List<(int, string)>();
            _powerTargets[source] = targets;
            _powerSources.Add(source);
        }

        targets.Add((part, pin));
    }

    public void Input(int access, int part, string pin) => _operandWires.Add((access, part, pin, false));

    public void Output(int part, string pin, int access) => _operandWires.Add((access, part, pin, true));

    public void Open(int part, string pin, bool output) => _openPins.Add((part, pin, output));

    // ---- Writing ----

    public XElement ToFlgNet()
    {
        // TIA's own exports number accesses first, then parts, then wires; do the same.
        var map = new Dictionary<int, int>();
        var uid = 21;
        foreach (var access in _accesses)
        {
            var old = (int)access.Attribute("UId")!;
            map[old] = uid;
            access.SetAttributeValue("UId", uid++);
        }

        foreach (var part in _parts)
        {
            var old = (int)part.Attribute("UId")!;
            map[old] = uid;
            part.SetAttributeValue("UId", uid++);
            var instance = part.Element(Ns + "Instance");
            instance?.SetAttributeValue("UId", uid++);
        }

        var wires = new XElement(Ns + "Wires");
        foreach (var source in _powerSources)
        {
            var wire = new XElement(Ns + "Wire", new XAttribute("UId", uid++));
            wire.Add(source.IsRail
                ? new XElement(Ns + "Powerrail")
                : NameCon(map[source.PartId], source.Pin));
            foreach (var (part, pin) in _powerTargets[source])
            {
                wire.Add(NameCon(map[part], pin));
            }

            wires.Add(wire);
        }

        foreach (var (access, part, pin, output) in _operandWires)
        {
            var ident = new XElement(Ns + "IdentCon", new XAttribute("UId", map[access]));
            var name = NameCon(map[part], pin);
            wires.Add(new XElement(Ns + "Wire", new XAttribute("UId", uid++), output ? name : ident, output ? ident : name));
        }

        foreach (var (part, pin, output) in _openPins)
        {
            var open = new XElement(Ns + "OpenCon", new XAttribute("UId", uid++));
            var name = NameCon(map[part], pin);
            wires.Add(new XElement(Ns + "Wire", new XAttribute("UId", uid++), output ? name : open, output ? open : name));
        }

        return new XElement(Ns + "FlgNet",
            new XElement(Ns + "Parts", _accesses, _parts),
            wires);
    }

    private static XElement NameCon(int uid, string pin) =>
        new(Ns + "NameCon", new XAttribute("UId", uid.ToString(CultureInfo.InvariantCulture)), new XAttribute("Name", pin));
}
