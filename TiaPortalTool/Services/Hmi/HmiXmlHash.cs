using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace TiaPortalTool.Services.Hmi;

/// <summary>
/// Fingerprint of an exported HMI object. Hashes the XML content, not the bytes, so re-saving a file in an editor
/// (line endings, indentation, BOM) doesn't count as a change. The DocumentInfo header (export time, TIA version,
/// installed products) is left out because it changes on every export.
/// Version 2 also leaves out what TIA works out by itself rather than what anyone edits: the ID attributes (TIA
/// renumbers them) and the Length and Coding of tags bound to a PLC tag (they follow the PLC data type, so
/// re-importing PLC types changes them in TIA). Manifests record the version they were hashed with.
/// </summary>
public static class HmiXmlHash
{
    /// <summary>The version new exports are hashed with. Version 1 = manifests without a HashVersion.</summary>
    public const int CurrentVersion = 2;

    public static string Compute(string path, int version = CurrentVersion)
    {
        var canonical = Canonical(XDocument.Load(path, LoadOptions.None), version).Root?.ToString(SaveOptions.DisableFormatting) ?? string.Empty;
        using var sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(canonical))).Replace("-", string.Empty);
    }

    /// <summary>The document with everything the hash ignores removed (changes <paramref name="document"/> in place).</summary>
    public static XDocument Canonical(XDocument document, int version)
    {
        document.Root?.Elements().Where(e => e.Name.LocalName == "DocumentInfo").Remove();
        if (version < 2 || document.Root is null)
        {
            return document;
        }

        document.Root.Descendants().Attributes("ID").Remove();
        foreach (var owner in document.Root.Descendants().Where(e => e.Element("LinkList")?.Element("ControllerTag") is not null).ToList())
        {
            owner.Element("AttributeList")?.Elements().Where(e => e.Name.LocalName is "Length" or "Coding").Remove();
        }

        return document;
    }

    /// <summary>The object's Name in a SimaticML HMI export (slide-ins have none; they're named by SlideinType).</summary>
    public static string? ReadObjectName(string path)
    {
        var document = XDocument.Load(path);
        var item = document.Root?.Elements().FirstOrDefault(e => e.Name.LocalName != "DocumentInfo" && e.Name.LocalName != "Engineering");
        var attributes = item?.Element("AttributeList");
        return attributes?.Element("Name")?.Value ?? attributes?.Element("SlideinType")?.Value;
    }
}
