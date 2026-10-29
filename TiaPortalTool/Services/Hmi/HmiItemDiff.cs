using System.Xml.Linq;

namespace TiaPortalTool.Services.Hmi;

/// <summary>One value TIA changed while importing a file, e.g. a faceplate's Width it re-fitted to keep its ratio.</summary>
public sealed class HmiItemChange
{
    public HmiItemChange(string label, string item, string attribute, string? fileValue, string? tiaValue)
    {
        Label = label;
        Item = item;
        Attribute = attribute;
        FileValue = fileValue;
        TiaValue = tiaValue;
    }

    /// <summary>The imported object, e.g. "Screen '1_Mode/Overview'".</summary>
    public string Label { get; }

    /// <summary>The screen item's ObjectName (or the object's own name for its own attributes).</summary>
    public string Item { get; }

    /// <summary>"Width", or a path such as "Events[Click]/FunctionList/..." for nested values.</summary>
    public string Attribute { get; }

    /// <summary>Null = the object isn't in that version at all.</summary>
    public string? FileValue { get; }
    public string? TiaValue { get; }

    /// <summary>Position or size: these change what the operator sees, so the save waits for a confirmation.</summary>
    public bool IsLayout => Attribute is "Left" or "Top" or "Width" or "Height" || Attribute == HmiItemDiff.MissingAfterImport;

    public override string ToString() => Attribute == HmiItemDiff.MissingAfterImport
        ? $"{Label}: {Item} is in the file but not in TIA after the import"
        : $"{Label}: {Item} {Attribute} {Show(FileValue)} -> {Show(TiaValue)}";

    private static string Show(string? value) =>
        value is null ? "(dropped)" : value.Length > 40 ? value.Substring(0, 37) + "..." : value;
}

/// <summary>
/// What TIA changed when it imported a file: the file against the same object exported again right after the import,
/// matched per screen item by ObjectName (by Name for objects without screen items, such as tag tables). Only values
/// the file has and TIA changed or dropped are reported. Values TIA added (defaults a hand-made file left out),
/// IDs and element order are not changes anyone made, so they're left out, as are the values the hash ignores
/// (<see cref="HmiXmlHash.Canonical"/>).
/// </summary>
public static class HmiItemDiff
{
    internal const string MissingAfterImport = "(missing after the import)";

    public static List<HmiItemChange> Compare(string label, string filePath, string tiaPath, int hashVersion)
    {
        var file = Flatten(HmiXmlHash.Canonical(XDocument.Load(filePath), hashVersion));
        var tia = Flatten(HmiXmlHash.Canonical(XDocument.Load(tiaPath), hashVersion));
        var changes = new List<HmiItemChange>();
        foreach (var pair in file)
        {
            if (!tia.TryGetValue(pair.Key, out var tiaValues))
            {
                changes.Add(new HmiItemChange(label, pair.Key, MissingAfterImport, null, null));
                continue;
            }

            foreach (var value in pair.Value)
            {
                tiaValues.TryGetValue(value.Key, out var tiaValue);
                if (!string.Equals(value.Value, tiaValue, StringComparison.Ordinal))
                {
                    changes.Add(new HmiItemChange(label, pair.Key, value.Key, value.Value, tiaValue));
                }
            }
        }

        return changes;
    }

    /// <summary>
    /// Every text value in the file (names, tag links, texts...). The lost-object guard uses them to tell a name an
    /// import removed on purpose (in TIA's version, not in the file) from one it lost.
    /// </summary>
    public static HashSet<string> Values(string path)
    {
        var values = new HashSet<string>(StringComparer.Ordinal);
        foreach (var element in XDocument.Load(path).Root?.Descendants() ?? Enumerable.Empty<XElement>())
        {
            if (!element.HasElements && element.Value.Trim() is { Length: > 0 } value)
            {
                values.Add(value);
            }
        }

        return values;
    }

    /// <summary>Object key -> its values by path. Objects are screen items (ObjectName); without any, named elements (Name).</summary>
    private static Dictionary<string, Dictionary<string, string>> Flatten(XDocument document)
    {
        var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        var tops = document.Root?.Elements().Where(e => e.Name.LocalName != "Engineering").ToList() ?? new List<XElement>();
        var byObjectName = tops.Any(t => t.Descendants("ObjectName").Any());
        foreach (var top in tops)
        {
            var key = top.Element("AttributeList")?.Element("Name")?.Value ?? top.Name.LocalName;
            Walk(top, key, Values(result, key), string.Empty, byObjectName, result, isTop: true);
        }

        return result;
    }

    private static void Walk(XElement element, string objectKey, Dictionary<string, string> values, string path, bool byObjectName, Dictionary<string, Dictionary<string, string>> result, bool isTop)
    {
        var attributes = element.Element("AttributeList");
        var name = byObjectName ? attributes?.Element("ObjectName")?.Value : attributes?.Element("Name")?.Value;
        if (!isTop && name is not null)
        {
            objectKey = name;
            for (var n = 2; result.ContainsKey(objectKey); n++)
            {
                objectKey = $"{name}#{n}";
            }

            values = Values(result, objectKey);
            path = string.Empty;
        }

        if (!element.HasElements)
        {
            if (path.Length > 0)
            {
                var key = path;
                for (var n = 2; values.ContainsKey(key); n++)
                {
                    key = $"{path}#{n}";
                }

                values[key] = element.Value;
            }

            return;
        }

        foreach (var child in element.Elements())
        {
            Walk(child, objectKey, values, Join(path, Segment(child)), byObjectName, result, isTop: false);
        }
    }

    private static Dictionary<string, string> Values(Dictionary<string, Dictionary<string, string>> result, string key)
    {
        if (!result.TryGetValue(key, out var values))
        {
            result[key] = values = new Dictionary<string, string>(StringComparer.Ordinal);
        }

        return values;
    }

    // AttributeList, LinkList and ObjectList are just containers: "Width" reads better than "AttributeList/Width".
    // A named child (event, interface property, list entry) keeps its name, so values don't depend on element order.
    private static string Segment(XElement element)
    {
        var local = element.Name.LocalName;
        if (local is "AttributeList" or "LinkList" or "ObjectList")
        {
            return string.Empty;
        }

        var composition = (string?)element.Attribute("CompositionName");
        var segment = composition is not null && composition != "Items" ? composition : local;
        var attributes = element.Element("AttributeList");
        var qualifier = attributes?.Element("Culture")?.Value ?? attributes?.Element("Name")?.Value;
        return qualifier is null ? segment : $"{segment}[{qualifier}]";
    }

    private static string Join(string path, string segment) =>
        segment.Length == 0 ? path : path.Length == 0 ? segment : path + "/" + segment;
}
