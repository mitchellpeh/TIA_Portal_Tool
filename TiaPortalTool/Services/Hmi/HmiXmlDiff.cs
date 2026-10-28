using System.Xml.Linq;

namespace TiaPortalTool.Services.Hmi;

/// <summary>
/// Short, readable list of what differs between an HMI object's file and the same object exported from TIA, used in
/// conflict messages. Differences are grouped, so one value TIA changed on many tags is one line, not one per tag.
/// Values the hash ignores (see <see cref="HmiXmlHash.Canonical"/>) are ignored here too.
/// </summary>
public static class HmiXmlDiff
{
    public static IReadOnlyList<string> Summarize(string filePath, string tiaPath, int hashVersion, int maxLines = 12)
    {
        var file = Flatten(HmiXmlHash.Canonical(XDocument.Load(filePath), hashVersion));
        var tia = Flatten(HmiXmlHash.Canonical(XDocument.Load(tiaPath), hashVersion));

        // Difference text -> objects that have it.
        var groups = new Dictionary<string, List<string>>();
        void Add(string difference, string objectKey)
        {
            if (!groups.TryGetValue(difference, out var list))
            {
                groups[difference] = list = new List<string>();
            }

            list.Add(objectKey.Substring(objectKey.LastIndexOf('/') + 1));
        }

        foreach (var key in file.Keys.Union(tia.Keys))
        {
            if (!tia.TryGetValue(key, out var tiaValues))
            {
                Add("only in the file", key);
                continue;
            }

            if (!file.TryGetValue(key, out var fileValues))
            {
                Add("only in TIA", key);
                continue;
            }

            foreach (var path in fileValues.Keys.Union(tiaValues.Keys))
            {
                fileValues.TryGetValue(path, out var a);
                tiaValues.TryGetValue(path, out var b);
                if (!string.Equals(a, b, StringComparison.Ordinal))
                {
                    Add($"{path}: file {Show(a)}, TIA {Show(b)}", key);
                }
            }
        }

        // A path whose values differ object by object (e.g. re-pointed tags) is one line with an example, not one per object.
        var lines = groups
            .GroupBy(g => g.Key.Split(':')[0])
            .SelectMany(byPath => byPath.Count() > 3
                ? new[] { (Count: byPath.Sum(g => g.Value.Count), Text: $"{byPath.Key}: differs on {byPath.Sum(g => g.Value.Count)} objects, e.g. {byPath.First().Value[0]}: {byPath.First().Key.Substring(byPath.Key.Length + 2)}") }
                : byPath.Select(g => (Count: g.Value.Count, Text: $"{g.Key}  ({Objects(g.Value)})")))
            .OrderByDescending(l => l.Count)
            .Select(l => l.Text)
            .ToList();
        if (lines.Count > maxLines)
        {
            var more = lines.Count - maxLines;
            lines = lines.Take(maxLines).ToList();
            lines.Add($"... and {more} more kind(s) of difference.");
        }

        return lines;
    }

    private static string Objects(List<string> names) =>
        names.Count == 1 ? names[0] : $"{names.Count} objects: {string.Join(", ", names.Take(3))}{(names.Count > 3 ? ", ..." : string.Empty)}";

    private static string Show(string? value) =>
        value is null ? "(none)" : value.Length > 40 ? $"'{value.Substring(0, 37)}...'" : $"'{value}'";

    /// <summary>
    /// Object key ("Table/Tag") -> its values by path. An object is an element whose AttributeList has a Name; values
    /// of nested unnamed elements (texts, links, events) belong to the nearest named object above them.
    /// </summary>
    private static Dictionary<string, Dictionary<string, string>> Flatten(XDocument document)
    {
        var result = new Dictionary<string, Dictionary<string, string>>();
        foreach (var top in document.Root?.Elements().Where(e => e.Name.LocalName != "Engineering") ?? Enumerable.Empty<XElement>())
        {
            Walk(top, string.Empty, null, string.Empty, result);
        }

        return result;
    }

    private static void Walk(XElement element, string objectKey, Dictionary<string, string>? values, string path, Dictionary<string, Dictionary<string, string>> result)
    {
        var name = element.Element("AttributeList")?.Element("Name")?.Value;
        if (name is not null)
        {
            objectKey = objectKey.Length == 0 ? name : objectKey + "/" + name;
            if (!result.TryGetValue(objectKey, out values))
            {
                result[objectKey] = values = new Dictionary<string, string>();
            }

            path = string.Empty;
        }

        if (!element.HasElements)
        {
            if (values is not null && path.Length > 0)
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
            Walk(child, objectKey, values, Join(path, Segment(child)), result);
        }
    }

    // AttributeList and LinkList are just containers: "Length" reads better than "AttributeList/Length".
    private static string Segment(XElement element)
    {
        var local = element.Name.LocalName;
        if (local is "AttributeList" or "LinkList" or "ObjectList")
        {
            return string.Empty;
        }

        var composition = (string?)element.Attribute("CompositionName");
        var culture = element.Element("AttributeList")?.Element("Culture")?.Value;
        var segment = composition is not null && composition != "Items" ? composition : local;
        return culture is null ? segment : $"{segment}[{culture}]";
    }

    private static string Join(string path, string segment) =>
        segment.Length == 0 ? path : path.Length == 0 ? segment : path + "/" + segment;
}
