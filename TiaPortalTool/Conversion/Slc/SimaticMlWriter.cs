using System.Globalization;
using System.Xml.Linq;

namespace TiaPortalTool.Conversion.Slc;

/// <summary>
/// Writes generated blocks and types as SimaticML documents that Openness can import (the same format as a block
/// export). Only the attributes that matter are written; TIA Portal fills in its defaults for the rest.
/// </summary>
public static class SimaticMlWriter
{
    private static readonly XNamespace InterfaceNs = "http://www.siemens.com/automation/Openness/SW/Interface/v5";

    public const string CommentLanguage = "en-US";

    public static XDocument GlobalDb(TiaDataBlock block, string engineeringVersion)
    {
        var section = new XElement(InterfaceNs + "Section", new XAttribute("Name", "Static"));
        foreach (var member in block.Members)
        {
            section.Add(MemberElement(member, block.Retain ? "Retain" : "NonRetain"));
        }

        var attributes = new XElement("AttributeList",
            new XElement("Interface", new XElement(InterfaceNs + "Sections", section)),
            new XElement("MemoryLayout", block.StandardAccess ? "Standard" : "Optimized"),
            new XElement("Name", block.Name),
            // V20 requires the (empty) namespace as part of a block's identity.
            new XElement("Namespace"),
            new XElement("Number", block.Number.ToString(CultureInfo.InvariantCulture)),
            new XElement("ProgrammingLanguage", "DB"));

        var ids = new IdCounter();
        var blockElement = new XElement("SW.Blocks.GlobalDB", new XAttribute("ID", ids.Next()),
            attributes,
            new XElement("ObjectList", MultilingualText("Comment", block.Comment, ids)));

        return Document(engineeringVersion, blockElement);
    }

    public static XDocument PlcType(TiaDataType type, string engineeringVersion)
    {
        var section = new XElement(InterfaceNs + "Section", new XAttribute("Name", "None"));
        foreach (var member in type.Members)
        {
            section.Add(MemberElement(member, remanence: null));
        }

        var ids = new IdCounter();
        var typeElement = new XElement("SW.Types.PlcStruct", new XAttribute("ID", ids.Next()),
            new XElement("AttributeList",
                new XElement("Interface", new XElement(InterfaceNs + "Sections", section)),
                new XElement("Name", type.Name),
                new XElement("Namespace")),
            new XElement("ObjectList", MultilingualText("Comment", type.Comment, ids)));

        return Document(engineeringVersion, typeElement);
    }

    /// <summary>A LAD FC or OB with its networks.</summary>
    public static XDocument CodeBlock(LadBlock block, string engineeringVersion)
    {
        var sections = new XElement(InterfaceNs + "Sections");
        XElement Section(string name) => new(InterfaceNs + "Section", new XAttribute("Name", name));
        XElement Member(string name, string type, string? comment = null, bool informative = false)
        {
            var member = new XElement(InterfaceNs + "Member", new XAttribute("Name", name), new XAttribute("Datatype", type));
            if (informative)
            {
                member.Add(new XAttribute("Informative", "true"));
            }

            if (comment is not null)
            {
                member.Add(new XElement(InterfaceNs + "Comment", new XElement(InterfaceNs + "MultiLanguageText", new XAttribute("Lang", CommentLanguage), comment)));
            }

            return member;
        }

        var temp = Section("Temp");
        foreach (var (name, type, comment) in block.Temps)
        {
            temp.Add(Member(name, type, comment));
        }

        if (block.BlockType == "OB")
        {
            var input = Section("Input");
            if (block.SecondaryType == "Startup")
            {
                input.Add(Member("LostRetentive", "Bool", "=True, if retentive data are lost", informative: true));
                input.Add(Member("LostRTC", "Bool", "=True, if date and time are lost", informative: true));
            }
            else
            {
                input.Add(Member("Initial_Call", "Bool", "Initial call of this OB", informative: true));
                input.Add(Member("Remanence", "Bool", "=True, if remanent data are available", informative: true));
            }

            sections.Add(input, temp, Section("Constant"));
        }
        else
        {
            sections.Add(Section("Input"), Section("Output"), Section("InOut"), temp, Section("Constant"),
                new XElement(InterfaceNs + "Section", new XAttribute("Name", "Return"), Member("Ret_Val", "Void")));
        }

        var attributes = new XElement("AttributeList",
            new XElement("Interface", sections));
        if (block.BlockType == "FC")
        {
            // Off, so TIA applies its usual implicit conversions (INT into a REAL box, INT/WORD), as the SLC did.
            attributes.Add(new XElement("IsIECCheckEnabled", "false"));
        }

        attributes.Add(
            new XElement("MemoryLayout", "Optimized"),
            new XElement("Name", block.Name),
            new XElement("Namespace"),
            new XElement("Number", block.Number.ToString(CultureInfo.InvariantCulture)),
            new XElement("ProgrammingLanguage", "LAD"));
        if (block.BlockType == "OB")
        {
            attributes.Add(new XElement("SecondaryType", block.SecondaryType));
        }

        var ids = new IdCounter();
        var element = new XElement($"SW.Blocks.{block.BlockType}", new XAttribute("ID", ids.Next()), attributes);
        var objects = new XElement("ObjectList", MultilingualText("Comment", block.Comment, ids));
        foreach (var network in block.Networks)
        {
            var source = new XElement("NetworkSource");
            if (network.FlgNet is not null)
            {
                source.Add(network.FlgNet);
            }

            objects.Add(new XElement("SW.Blocks.CompileUnit", new XAttribute("ID", ids.Next()), new XAttribute("CompositionName", "CompileUnits"),
                new XElement("AttributeList", source, new XElement("ProgrammingLanguage", "LAD")),
                new XElement("ObjectList",
                    MultilingualText("Comment", network.Comment, ids),
                    MultilingualText("Title", Truncate(network.Title, 120), ids))));
        }

        objects.Add(MultilingualText("Title", block.Name, ids));
        element.Add(objects);
        return Document(engineeringVersion, element);
    }

    private static string Truncate(string text, int length) => text.Length <= length ? text : text.Substring(0, length - 3) + "...";

    private static XDocument Document(string engineeringVersion, XElement content) =>
        new(new XDeclaration("1.0", "utf-8", null),
            new XElement("Document",
                new XElement("Engineering", new XAttribute("version", engineeringVersion)),
                content));

    private static XElement MemberElement(TiaMember member, string? remanence)
    {
        var element = new XElement(InterfaceNs + "Member",
            new XAttribute("Name", member.Name),
            new XAttribute("Datatype", member.DataType));
        if (member.Version is not null)
        {
            element.Add(new XAttribute("Version", member.Version));
        }

        if (remanence is not null)
        {
            element.Add(new XAttribute("Remanence", remanence));
            element.Add(new XAttribute("Accessibility", "Public"));
        }

        if (!string.IsNullOrEmpty(member.Comment))
        {
            element.Add(new XElement(InterfaceNs + "Comment",
                new XElement(InterfaceNs + "MultiLanguageText", new XAttribute("Lang", CommentLanguage), member.Comment)));
        }

        if (member.SubStartValues.Count > 0)
        {
            // Start values inside a UDT-typed member: list the sub-members with their values.
            var section = new XElement(InterfaceNs + "Section", new XAttribute("Name", "None"));
            foreach (var pair in member.SubStartValues)
            {
                section.Add(new XElement(InterfaceNs + "Member",
                    new XAttribute("Name", pair.Key),
                    new XAttribute("Datatype", SubMemberType(pair.Key, pair.Value)),
                    new XElement(InterfaceNs + "StartValue", pair.Value)));
            }

            element.Add(new XElement(InterfaceNs + "Sections", section));
        }
        else if (member.StartValue is not null)
        {
            element.Add(new XElement(InterfaceNs + "StartValue", member.StartValue));
        }

        return element;
    }

    // Sub-members only appear for the converter's own UDTs, so their types can be told from the value.
    private static string SubMemberType(string name, string value) =>
        value.StartsWith("T#", StringComparison.Ordinal) ? "Time"
        : value is "true" or "false" ? "Bool"
        : value.IndexOf('.') >= 0 || value.IndexOf('E') >= 0 ? "Real"
        : "Int";

    private static XElement MultilingualText(string compositionName, string text, IdCounter ids) =>
        new("MultilingualText", new XAttribute("ID", ids.Next()), new XAttribute("CompositionName", compositionName),
            new XElement("ObjectList",
                new XElement("MultilingualTextItem", new XAttribute("ID", ids.Next()), new XAttribute("CompositionName", "Items"),
                    new XElement("AttributeList",
                        new XElement("Culture", CommentLanguage),
                        new XElement("Text", text)))));

    private sealed class IdCounter
    {
        private int _next;

        public string Next() => (_next++).ToString("X", CultureInfo.InvariantCulture);
    }
}
