using System.Xml.Linq;

namespace TiaPortalTool.Services;

public sealed class RungEdit
{
    public int Number { get; set; }
    public Dictionary<string, string> Title { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> Comment { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Splices edited block/rung comment text back into a previously exported block XML file
/// in place. Only updates MultilingualTextItem entries for languages that already exist in
/// the source — it does not add new languages. LAD/FBD only; SCL/STL networks are not
/// editable through this path (see BlockXmlReader's StructuredText notes).
/// </summary>
internal static class BlockXmlEditor
{
    /// <summary>
    /// Returns true if any text actually changed (and the file was rewritten). A no-op edit
    /// leaves the file untouched so the caller can skip a needless Import()+Compile() round
    /// trip through Openness — reimporting every block on every run, whether or not it changed,
    /// is what made full-project reimports look like a hang.
    /// </summary>
    public static bool ApplyEdits(string xmlPath, Dictionary<string, string> blockComment, List<RungEdit> rungEdits)
    {
        var doc = XDocument.Load(xmlPath);
        var blockElement = doc.Root?.Elements().FirstOrDefault(e => e.Name.LocalName.StartsWith("SW.Blocks."))
            ?? throw new InvalidOperationException("Could not locate block element in XML.");

        var topObjectList = blockElement.Elements().FirstOrDefault(e => e.Name.LocalName == "ObjectList");
        bool anyChanged = ApplyLanguageMap(topObjectList, "Comment", blockComment);

        if (topObjectList is not null)
        {
            var compileUnits = topObjectList.Elements().Where(e => e.Name.LocalName == "SW.Blocks.CompileUnit").ToList();
            foreach (var edit in rungEdits)
            {
                if (edit.Number < 1 || edit.Number > compileUnits.Count)
                {
                    continue;
                }

                var cuObjectList = compileUnits[edit.Number - 1].Elements().FirstOrDefault(e => e.Name.LocalName == "ObjectList");
                anyChanged |= ApplyLanguageMap(cuObjectList, "Title", edit.Title);
                anyChanged |= ApplyLanguageMap(cuObjectList, "Comment", edit.Comment);
            }
        }

        if (anyChanged)
        {
            doc.Save(xmlPath);
        }

        return anyChanged;
    }

    private static bool ApplyLanguageMap(XElement? objectList, string compositionName, Dictionary<string, string> values)
    {
        var multilingualText = objectList?.Elements()
            .FirstOrDefault(e => e.Name.LocalName == "MultilingualText"
                && (string?)e.Attribute("CompositionName") == compositionName);

        var items = multilingualText?.Elements().FirstOrDefault(e => e.Name.LocalName == "ObjectList")
            ?.Elements().Where(e => e.Name.LocalName == "MultilingualTextItem") ?? Enumerable.Empty<XElement>();

        bool anyChanged = false;
        foreach (var item in items)
        {
            var attributeList = item.Elements().FirstOrDefault(e => e.Name.LocalName == "AttributeList");
            var culture = attributeList?.Elements().FirstOrDefault(e => e.Name.LocalName == "Culture")?.Value;
            if (culture is null || !values.TryGetValue(culture, out var newText))
            {
                continue;
            }

            var textElement = attributeList?.Elements().FirstOrDefault(e => e.Name.LocalName == "Text");
            if (textElement is not null && textElement.Value != newText)
            {
                textElement.SetValue(newText);
                anyChanged = true;
            }
        }

        return anyChanged;
    }
}
