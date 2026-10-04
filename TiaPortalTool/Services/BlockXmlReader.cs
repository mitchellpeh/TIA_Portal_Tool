using System.Text;
using System.Xml.Linq;

namespace TiaPortalTool.Services;

public sealed class RungData
{
    public int Number { get; set; }
    public Dictionary<string, string> Title { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> Comment { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Best-effort detokenized SCL/STL source, for read-only preview only.</summary>
    public string? SclPreview { get; set; }
}

public sealed class BlockXmlData
{
    public string BlockName { get; set; } = string.Empty;
    public string ProgrammingLanguage { get; set; } = string.Empty;

    /// <summary>True for SCL/STL blocks, where networks are tokenized StructuredText rather than LAD/FBD graphics.</summary>
    public bool IsTextual { get; set; }

    public Dictionary<string, string> BlockComment { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<RungData> Rungs { get; set; } = new();

    public List<string> AllLanguages()
    {
        var set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var lang in BlockComment.Keys) set.Add(lang);
        foreach (var rung in Rungs)
        {
            foreach (var lang in rung.Title.Keys) set.Add(lang);
            foreach (var lang in rung.Comment.Keys) set.Add(lang);
        }
        return set.ToList();
    }
}

internal static class BlockXmlReader
{
    public static BlockXmlData Read(string xmlPath, string blockName, string language)
    {
        var doc = XDocument.Load(xmlPath);
        var data = new BlockXmlData { BlockName = blockName, ProgrammingLanguage = language };

        var blockElement = doc.Root?.Elements().FirstOrDefault(e => e.Name.LocalName.StartsWith("SW.Blocks."));
        if (blockElement is null)
        {
            return data;
        }

        var topObjectList = blockElement.Elements().FirstOrDefault(e => e.Name.LocalName == "ObjectList");
        data.BlockComment = ReadLanguageMap(FindMultilingualText(topObjectList, "Comment"));

        if (topObjectList is null)
        {
            return data;
        }

        int rungNumber = 1;
        foreach (var compileUnit in topObjectList.Elements().Where(e => e.Name.LocalName == "SW.Blocks.CompileUnit"))
        {
            var cuObjectList = compileUnit.Elements().FirstOrDefault(e => e.Name.LocalName == "ObjectList");

            var rung = new RungData
            {
                Number = rungNumber,
                Title = ReadLanguageMap(FindMultilingualText(cuObjectList, "Title")),
                Comment = ReadLanguageMap(FindMultilingualText(cuObjectList, "Comment"))
            };

            var networkSource = compileUnit.Elements().FirstOrDefault(e => e.Name.LocalName == "AttributeList")
                ?.Elements().FirstOrDefault(e => e.Name.LocalName == "NetworkSource");
            var structuredText = networkSource?.Elements().FirstOrDefault(e => e.Name.LocalName == "StructuredText");
            if (structuredText is not null)
            {
                data.IsTextual = true;
                rung.SclPreview = Detokenize(structuredText);
            }

            data.Rungs.Add(rung);
            rungNumber++;
        }

        return data;
    }

    private static XElement? FindMultilingualText(XElement? objectList, string compositionName) =>
        objectList?.Elements()
            .FirstOrDefault(e => e.Name.LocalName == "MultilingualText"
                && (string?)e.Attribute("CompositionName") == compositionName);

    private static Dictionary<string, string> ReadLanguageMap(XElement? multilingualText)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (multilingualText is null)
        {
            return result;
        }

        var items = multilingualText.Elements().FirstOrDefault(e => e.Name.LocalName == "ObjectList")
            ?.Elements().Where(e => e.Name.LocalName == "MultilingualTextItem") ?? Enumerable.Empty<XElement>();

        foreach (var item in items)
        {
            var attributeList = item.Elements().FirstOrDefault(e => e.Name.LocalName == "AttributeList");
            var culture = attributeList?.Elements().FirstOrDefault(e => e.Name.LocalName == "Culture")?.Value;
            var text = attributeList?.Elements().FirstOrDefault(e => e.Name.LocalName == "Text")?.Value ?? string.Empty;
            if (!string.IsNullOrEmpty(culture))
            {
                result[culture] = text;
            }
        }

        return result;
    }

    // TIA Portal stores SCL/STL source as a tokenized XML tree (Token/Blank/NewLine/Text/
    // LineComment elements), not plain text. This walks the tree to reconstruct readable
    // source for display only — there is no writer for this format, so SCL edits cannot be
    // round-tripped through it. Unrecognized element types fall back to recursing into their
    // children (or emitting their text value), so the preview degrades gracefully rather than
    // dropping content silently.
    private static string Detokenize(XElement structuredText)
    {
        var sb = new StringBuilder();
        foreach (var child in structuredText.Elements())
        {
            AppendToken(sb, child);
        }
        return sb.ToString();
    }

    private static void AppendToken(StringBuilder sb, XElement element)
    {
        switch (element.Name.LocalName)
        {
            case "Token":
                sb.Append((string?)element.Attribute("Text") ?? string.Empty);
                break;
            case "Blank":
                sb.Append(' ', ParseNum(element));
                break;
            case "NewLine":
                sb.Append('\n', ParseNum(element));
                break;
            case "Text":
                sb.Append(element.Value);
                break;
            case "LineComment":
                sb.Append("//");
                foreach (var child in element.Elements()) AppendToken(sb, child);
                break;
            case "BlockComment":
                sb.Append("(*");
                foreach (var child in element.Elements()) AppendToken(sb, child);
                sb.Append("*)");
                break;
            default:
                if (element.HasElements)
                {
                    foreach (var child in element.Elements()) AppendToken(sb, child);
                }
                else
                {
                    sb.Append(element.Value);
                }
                break;
        }
    }

    private static int ParseNum(XElement element) =>
        int.TryParse((string?)element.Attribute("Num"), out var n) ? n : 1;
}
