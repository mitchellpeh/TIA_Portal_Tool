using System.Globalization;
using System.Xml.Linq;

namespace TiaPortalTool.Conversion.Slc;

/// <summary>An operand as TIA Portal sees it: a symbol path, a local (temp) variable, a constant or an absolute address.</summary>
public sealed class TiaOperand
{
    private enum Kind { Global, Local, Literal, Typed, Addressed }

    private Kind _kind;
    private IReadOnlyList<string> _components = Array.Empty<string>();
    private string? _slice;
    private string _value = string.Empty;
    private TiaOperand? _file;
    private TiaOperand? _element;
    private TiaOperand? _bit;

    private TiaOperand(string dataType) => DataType = dataType;

    /// <summary>Bool, Int, DInt, Real, Word, Time, IEC_TIMER, ...</summary>
    public string DataType { get; private set; }

    /// <summary>Human-readable form for comments and the report, e.g. "B3".IP_ESTOP_ACTV.</summary>
    public string Display { get; private set; } = string.Empty;

    public IReadOnlyList<string> Components => _components;

    public static TiaOperand Global(string dataType, params string[] components) => new(dataType)
    {
        _kind = Kind.Global,
        _components = components,
        Display = $"\"{components[0]}\"" + string.Concat(components.Skip(1).Select(c => "." + c))
    };

    /// <summary>A bit of a word reached with a slice: "N7".N7_5.%X3.</summary>
    public static TiaOperand Slice(string block, string member, int bit) => new("Bool")
    {
        _kind = Kind.Global,
        _components = new[] { block, member },
        _slice = "x" + bit.ToString(CultureInfo.InvariantCulture),
        Display = $"\"{block}\".{member}.%X{bit}"
    };

    public static TiaOperand Local(string dataType, string name) => new(dataType)
    {
        _kind = Kind.Local, _components = new[] { name }, Display = "#" + name
    };

    public static TiaOperand Literal(string dataType, string value) => new(dataType)
    {
        _kind = Kind.Literal, _value = value, Display = value
    };

    /// <summary>A typed literal such as T#5S.</summary>
    public static TiaOperand Typed(string dataType, string value) => new(dataType)
    {
        _kind = Kind.Typed, _value = value, Display = value
    };

    /// <summary>
    /// A data file element reached by file and element number rather than by name: a whole word of a bit file
    /// (B3:16), or an indirect address (N91:[N7:56], N[N43:61]:[N43:63], B3:70/[N7:200]). File, element and bit
    /// are constants or variables. TIA's LAD import doesn't take addresses like these, so the translator reads and
    /// writes them through the SLC_WORD/FLOAT/BIT helpers. DataType is Word (B/N), Real (F) or Bool (a bit).
    /// </summary>
    public static TiaOperand Addressed(string dataType, TiaOperand file, TiaOperand element, TiaOperand? bit, string display) => new(dataType)
    {
        _kind = Kind.Addressed,
        _file = file,
        _element = element,
        _bit = bit,
        Display = display
    };

    public bool IsAddressed => _kind == Kind.Addressed;

    public TiaOperand AddressFile => _file!;

    public TiaOperand AddressElement => _element!;

    public TiaOperand? AddressBit => _bit;

    public bool IsLiteral => _kind == Kind.Literal;


    public TiaOperand WithType(string dataType)
    {
        var copy = (TiaOperand)MemberwiseClone();
        copy.DataType = dataType;
        return copy;
    }

    public XElement ToAccess(XNamespace ns, int uid)
    {
        switch (_kind)
        {
            case Kind.Global:
            case Kind.Local:
                var symbol = new XElement(ns + "Symbol");
                for (var i = 0; i < _components.Count; i++)
                {
                    var component = new XElement(ns + "Component", new XAttribute("Name", _components[i]));
                    if (_slice is not null && i == _components.Count - 1)
                    {
                        component.Add(new XAttribute("SliceAccessModifier", _slice));
                    }

                    symbol.Add(component);
                }

                return new XElement(ns + "Access", new XAttribute("Scope", _kind == Kind.Global ? "GlobalVariable" : "LocalVariable"),
                    new XAttribute("UId", uid), symbol);

            case Kind.Literal:
                return new XElement(ns + "Access", new XAttribute("Scope", "LiteralConstant"), new XAttribute("UId", uid),
                    new XElement(ns + "Constant",
                        new XElement(ns + "ConstantType", DataType),
                        new XElement(ns + "ConstantValue", _value)));

            case Kind.Typed:
                return new XElement(ns + "Access", new XAttribute("Scope", "TypedConstant"), new XAttribute("UId", uid),
                    new XElement(ns + "Constant", new XElement(ns + "ConstantValue", _value)));

            default:
                throw new InvalidOperationException($"{Display} must be read or written through the SLC helper functions");
        }
    }
}
