using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;

namespace TiaPortalTool.Services;

public sealed class FolderImportResult
{
    public bool Success { get; set; }

    /// <summary>The folder holds only HMI exports: nothing was imported into the PLC and nothing was compiled.</summary>
    public bool NothingForPlc { get; set; }

    /// <summary>
    /// Everything imported and the only problem is the compile: the caller may still save if the user agrees
    /// (<see cref="Compile"/> lists the errors).
    /// </summary>
    public bool OnlyCompileFailed => !Success && !ImportFailed && Compile is { Clean: false };

    /// <summary>A file didn't import (nothing was compiled then).</summary>
    public bool ImportFailed { get; set; }

    /// <summary>The PLC compile after the import; null when it didn't run.</summary>
    public CompileOutcome? Compile { get; set; }

    /// <summary>Names of the blocks this import brought in (imported from XML or generated from SCL).</summary>
    public IReadOnlyCollection<string> TouchedBlocks { get; set; } = Array.Empty<string>();

    public IReadOnlyList<string> Messages { get; set; } = Array.Empty<string>();
}

/// <summary>
/// Imports new/changed content from a folder into the PLC software, then compiles the whole program:
///   &lt;folder&gt;\&lt;Table name&gt;.tags.tsv   -> PLC tag table (created if missing). Columns: Name, DataType, Address[, Comment].
///   &lt;folder&gt;\&lt;group\path&gt;\*.xml     -> SimaticML block, imported with Override into that block group
///                                    (subfolders = block groups, same layout as the export's _blocks_xml).
///                                    An OB that already exists in the project is refused, never replaced.
/// Tags go in first so imported blocks can bind to them by name. HMI exports inside the folder (a folder with an
/// hmi_manifest.json) are skipped here; the caller imports them with the HMI import.
/// </summary>
public sealed class FolderImportService
{
    /// <summary>
    /// Where the tool writes its run logs: a folder inside the folder being imported (or exported to). Every import skips
    /// it, so a log never gets imported and a package can be imported again as it is.
    /// </summary>
    public const string LogFolderName = "TIA_Portal_Tool_Logs";

    /// <summary>True for a file inside a <see cref="LogFolderName"/> folder (at any depth).</summary>
    public static bool InLogFolder(string path) =>
        path.Split('\\', '/').Any(s => string.Equals(s, LogFolderName, StringComparison.OrdinalIgnoreCase));

    // TIA's auto-created names for absolute operands with no tag ("Tag_12", "Tag__639264727201275679").
    private static readonly Regex AutoTagName = new(@"^Tag_+\d+$", RegexOptions.Compiled);

    private const string CommentLanguage = "en-US";

    public FolderImportResult ImportAndCompile(dynamic plcSoftware, Assembly assembly, string folder, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var messages = new List<string>();
        bool anyFailure = false;

        // HMI exports (HMI\<device> with an hmi_manifest.json) inside the folder are the HMI import's job, not blocks;
        // the tool's own log folder is never imported.
        var hmiFolders = Hmi.HmiImportService.FindHmiFolders(folder)
            .Select(d => Path.GetFullPath(d).TrimEnd('\\') + "\\").ToList();
        bool Skipped(string file) => InLogFolder(file) || hmiFolders.Any(d => Path.GetFullPath(file).StartsWith(d, StringComparison.OrdinalIgnoreCase));
        var touched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var tagFiles = Directory.GetFiles(folder, "*.tags.tsv", SearchOption.AllDirectories).Where(f => !Skipped(f)).OrderBy(f => f).ToList();
        var xmlFiles = Directory.GetFiles(folder, "*.xml", SearchOption.AllDirectories).Where(f => !Skipped(f)).OrderBy(f => f).ToList();
        var typeFiles = xmlFiles.Where(IsPlcTypeFile).ToList();
        // Global DBs go in before the SCL sources, since generated blocks may read them; other blocks come after.
        var globalDbFiles = xmlFiles.Except(typeFiles).Where(IsGlobalDbFile).ToList();
        var blockFiles = xmlFiles.Except(typeFiles).Except(globalDbFiles).ToList();
        var sourceFiles = Directory.GetFiles(folder, "*.scl", SearchOption.AllDirectories).Where(f => !Skipped(f)).OrderBy(f => f).ToList();
        if (tagFiles.Count == 0 && xmlFiles.Count == 0 && sourceFiles.Count == 0)
        {
            if (hmiFolders.Count > 0 || ProjectTextService.FindFiles(folder).Count > 0)
            {
                return new FolderImportResult { Success = true, NothingForPlc = true, Messages = messages };
            }

            messages.Add($"Nothing to import in {folder} (expected *.tags.tsv, *.xml, *.scl, {ProjectTextService.FilePattern} and/or an HMI export).");
            return new FolderImportResult { Success = false, Messages = messages };
        }

        // PLC data types first: tags and blocks can use them.
        if (typeFiles.Count > 0)
        {
            var importOptionsType = assembly.GetType("Siemens.Engineering.ImportOptions")
                ?? throw new InvalidOperationException("Siemens.Engineering.ImportOptions type not found.");
            dynamic overrideOption = Enum.Parse(importOptionsType, "Override");
            progress?.Report($"Importing {typeFiles.Count} PLC data type file(s)...");

            foreach (var file in typeFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!ImportTypeFile(plcSoftware, folder, file, overrideOption, messages))
                {
                    anyFailure = true;
                }
            }
        }

        if (globalDbFiles.Count > 0)
        {
            var importOptionsType = assembly.GetType("Siemens.Engineering.ImportOptions")
                ?? throw new InvalidOperationException("Siemens.Engineering.ImportOptions type not found.");
            dynamic overrideOption = Enum.Parse(importOptionsType, "Override");
            progress?.Report($"Importing {globalDbFiles.Count} global DB file(s)...");

            foreach (var file in globalDbFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!ImportBlockFile(plcSoftware, folder, file, overrideOption, messages, touched))
                {
                    anyFailure = true;
                }
            }
        }

        // SCL sources next: they compile into blocks that imported blocks may call.
        if (sourceFiles.Count > 0)
        {
            progress?.Report($"Generating blocks from {sourceFiles.Count} SCL source file(s)...");
            foreach (var file in sourceFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!ImportSourceFile(plcSoftware, file, messages, touched))
                {
                    anyFailure = true;
                }
            }
        }

        if (tagFiles.Count > 0)
        {
            progress?.Report($"Importing {tagFiles.Count} tag table file(s)...");
        }

        foreach (var file in tagFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                ImportTagFile(plcSoftware, file, messages);
            }
            catch (Exception ex)
            {
                messages.Add($"Tag import failed for {Path.GetFileName(file)}: {Describe(ex)}");
                anyFailure = true;
            }
        }

        if (blockFiles.Count > 0)
        {
            var importOptionsType = assembly.GetType("Siemens.Engineering.ImportOptions")
                ?? throw new InvalidOperationException("Siemens.Engineering.ImportOptions type not found.");
            dynamic overrideOption = Enum.Parse(importOptionsType, "Override");
            progress?.Report($"Importing {blockFiles.Count} block file(s)...");

            foreach (var file in blockFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!ImportBlockFile(plcSoftware, folder, file, overrideOption, messages, touched))
                {
                    anyFailure = true;
                }
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        var importFailed = anyFailure;
        CompileOutcome? compile = null;
        if (anyFailure)
        {
            messages.Add("Skipping compile because of the import failures above.");
        }
        else
        {
            compile = PlcCompiler.Compile(plcSoftware, assembly, messages, progress);
            anyFailure = !compile.Clean;
        }

        return new FolderImportResult
        {
            Success = !anyFailure, ImportFailed = importFailed, Compile = compile, TouchedBlocks = touched, Messages = messages,
        };
    }

    private static void ImportTagFile(dynamic plcSoftware, string file, List<string> messages)
    {
        string tableName = Path.GetFileName(file);
        tableName = tableName.Substring(0, tableName.Length - ".tags.tsv".Length);

        // Names and addresses are read from TIA once here: each property read is a slow cross-process call, and
        // reading them again for every new tag made big tag files (hundreds of HAL tags) take minutes.
        var allTags = new List<ExistingTag>();
        CollectTags(plcSoftware.TagTableGroup, allTags);

        dynamic? table = null;
        foreach (dynamic t in plcSoftware.TagTableGroup.TagTables)
        {
            if (string.Equals((string)t.Name, tableName, StringComparison.OrdinalIgnoreCase))
            {
                table = t;
                break;
            }
        }

        if (table is null)
        {
            table = plcSoftware.TagTableGroup.TagTables.Create(tableName);
            messages.Add($"Created tag table '{tableName}'.");
        }

        int created = 0, replaced = 0, autoRemoved = 0, skipped = 0;
        foreach (var raw in File.ReadAllLines(file))
        {
            var line = raw.TrimStart('﻿');
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#"))
            {
                continue;
            }

            var cols = line.Split('\t');
            if (cols.Length < 3)
            {
                messages.Add($"  {tableName}: skipped malformed line '{line}'.");
                skipped++;
                continue;
            }

            string name = cols[0].Trim(), dataType = cols[1].Trim(), address = cols[2].Trim();
            string comment = cols.Length > 3 ? cols[3].Trim() : string.Empty;

            // Same name elsewhere -> leave it alone (TIA tag names are unique per PLC).
            var sameName = allTags.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
            if (sameName is not null && !string.Equals(sameName.Table, tableName, StringComparison.OrdinalIgnoreCase))
            {
                messages.Add($"  {name}: already exists in tag table '{sameName.Table}' - skipped.");
                skipped++;
                continue;
            }

            // Auto-created "Tag_nnn" tags on the same address are leftovers from absolute addressing - remove them.
            foreach (var auto in allTags.Where(x => AutoTagName.IsMatch(x.Name)
                         && string.Equals(x.Address, address, StringComparison.OrdinalIgnoreCase)).ToList())
            {
                messages.Add($"  removed auto tag {auto.Name} ({address}) from '{auto.Table}'.");
                auto.Tag.Delete();
                allTags.Remove(auto);
                autoRemoved++;
            }

            if (sameName is not null)
            {
                sameName.Tag.Delete();
                allTags.Remove(sameName);
                replaced++;
            }
            else
            {
                created++;
            }

            dynamic tag = table.Tags.Create(name, dataType, address);
            if (comment.Length > 0)
            {
                foreach (dynamic item in tag.Comment.Items)
                {
                    if (string.Equals(item.Language.ToString(), CommentLanguage, StringComparison.OrdinalIgnoreCase))
                    {
                        item.Text = comment;
                        break;
                    }
                }
            }

            allTags.Add(new ExistingTag(tag, name, tableName, address));
        }

        messages.Add($"Tag table '{tableName}': {created} created, {replaced} replaced, {autoRemoved} auto tag(s) removed, {skipped} skipped.");
    }

    private static bool ImportBlockFile(dynamic plcSoftware, string root, string file, dynamic overrideOption, List<string> messages, HashSet<string> touched)
    {
        string relDir = Path.GetDirectoryName(file)!.Substring(root.TrimEnd('\\').Length).Trim('\\').Replace('\\', '/');
        string label = (relDir.Length > 0 ? relDir + "/" : string.Empty) + Path.GetFileName(file);

        string? blockName;
        try
        {
            blockName = ReadBlockName(file);
        }
        catch (System.Xml.XmlException ex)
        {
            messages.Add($"Import failed for {label}: not valid XML ({ex.Message}).");
            return false;
        }

        if (blockName is null)
        {
            messages.Add($"Skipped {label}: no SW.Blocks.* <Name> found (not a block export?).");
            return false;
        }

        if (IsObFile(file) && FindBlockAnywhere((object)plcSoftware.BlockGroup, blockName, string.Empty).Block is not null)
        {
            messages.Add($"Refused {label}: {ExistingObMessage(blockName)}");
            return false;
        }

        try
        {
            dynamic group = EnsureGroup(plcSoftware.BlockGroup, relDir, messages);

            // Override only replaces a block in the target group; a same-named block in another group would collide.
            var existing = FindBlockAnywhere((object)plcSoftware.BlockGroup, blockName, string.Empty);
            if (existing.Block is not null && !SameGroupPath(existing.Path, relDir))
            {
                messages.Add($"  removed existing {blockName} from group '{existing.Path}' (moving it to '{relDir}').");
                existing.Block.Delete();
            }

            group.Blocks.Import(new FileInfo(file), overrideOption);
            touched.Add(blockName);
            messages.Add($"Imported {blockName} into '{(relDir.Length > 0 ? relDir : "Program blocks")}'.");
            return true;
        }
        catch (Exception ex)
        {
            messages.Add($"Import failed for {label}: {Describe(ex)}");
            return false;
        }
    }

    /// <summary>
    /// Imports a PLC data type into the top level of "PLC data types". Folders don't map to type groups, so a
    /// converter can keep its types in any subfolder.
    /// </summary>
    private static bool ImportTypeFile(dynamic plcSoftware, string root, string file, dynamic overrideOption, List<string> messages)
    {
        string label = file.Substring(root.TrimEnd('\\').Length).Trim('\\').Replace('\\', '/');
        try
        {
            plcSoftware.TypeGroup.Types.Import(new FileInfo(file), overrideOption);
            messages.Add($"Imported PLC data type {Path.GetFileNameWithoutExtension(file)}.");
            return true;
        }
        catch (Exception ex)
        {
            messages.Add($"Import failed for {label}: {Describe(ex)}");
            return false;
        }
    }

    /// <summary>
    /// Adds an SCL source as an external source and generates its blocks. Blocks the source defines are deleted
    /// first so the new versions replace them, wherever they are.
    /// </summary>
    private static bool ImportSourceFile(dynamic plcSoftware, string file, List<string> messages, HashSet<string> touched)
    {
        var name = Path.GetFileName(file);
        try
        {
            var text = File.ReadAllText(file);
            var declarations = Regex.Matches(text, "^\\s*(FUNCTION_BLOCK|FUNCTION|DATA_BLOCK|ORGANIZATION_BLOCK)\\s+\"([^\"]+)\"", RegexOptions.Multiline)
                .Cast<Match>().ToList();
            var blockNames = declarations.Select(m => m.Groups[2].Value).ToList();

            // Checked before anything is deleted, so a refused source leaves the project as it was.
            var existingObs = declarations
                .Where(m => m.Groups[1].Value == "ORGANIZATION_BLOCK"
                            && FindBlockAnywhere((object)plcSoftware.BlockGroup, m.Groups[2].Value, string.Empty).Block is not null)
                .Select(m => m.Groups[2].Value).ToList();
            if (existingObs.Count > 0)
            {
                messages.Add($"Refused {name}: {string.Join(" ", existingObs.Select(ExistingObMessage))}");
                return false;
            }

            foreach (var blockName in blockNames)
            {
                var existing = FindBlockAnywhere((object)plcSoftware.BlockGroup, blockName, string.Empty);
                existing.Block?.Delete();
            }

            dynamic sources = plcSoftware.ExternalSourceGroup.ExternalSources;
            foreach (dynamic source in sources)
            {
                if (string.Equals((string)source.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    source.Delete();
                    break;
                }
            }

            dynamic created = sources.CreateFromFile(name, file);
            created.GenerateBlocksFromSource();
            touched.UnionWith(blockNames);
            messages.Add($"Generated {string.Join(", ", blockNames)} from {name}.");
            return true;
        }
        catch (Exception ex)
        {
            messages.Add($"Generating blocks from {name} failed: {Describe(ex)}");
            return false;
        }
    }

    private static bool IsGlobalDbFile(string file)
    {
        try
        {
            var doc = System.Xml.Linq.XDocument.Load(file);
            return doc.Root?.Elements().Any(e => e.Name.LocalName == "SW.Blocks.GlobalDB") == true;
        }
        catch (System.Xml.XmlException)
        {
            return false;
        }
    }

    private static bool IsObFile(string file)
    {
        try
        {
            var doc = System.Xml.Linq.XDocument.Load(file);
            return doc.Root?.Elements().Any(e => e.Name.LocalName == "SW.Blocks.OB") == true;
        }
        catch (System.Xml.XmlException)
        {
            return false;
        }
    }

    /// <summary>
    /// Why an existing OB is never replaced: the import deletes and recreates it, so the new OB loses what its XML doesn't
    /// carry (process image partition and event links in the device configuration, cycle time, networks added in TIA
    /// since the export). Seen in the field: a replaced Main stopped a high-speed counter.
    /// </summary>
    private static string ExistingObMessage(string obName) =>
        $"OB '{obName}' already exists and is not replaced (a replaced OB loses its process image / event links in the "
        + "device configuration and any networks added since the export). Add the network(s) to the OB in TIA by hand, "
        + "or delete the OB in TIA first if a full replacement is really meant.";

    private static bool IsPlcTypeFile(string file)
    {
        try
        {
            var doc = System.Xml.Linq.XDocument.Load(file);
            return doc.Root?.Elements().Any(e => e.Name.LocalName.StartsWith("SW.Types.", StringComparison.Ordinal)) == true;
        }
        catch (System.Xml.XmlException)
        {
            // Let the block import report the broken file.
            return false;
        }
    }

    private static string? ReadBlockName(string file)
    {
        var doc = System.Xml.Linq.XDocument.Load(file);
        var block = doc.Root?.Elements().FirstOrDefault(e => e.Name.LocalName.StartsWith("SW.Blocks.", StringComparison.Ordinal));
        return block?.Element("AttributeList")?.Element("Name")?.Value;
    }

    /// <summary>
    /// Group paths ("a/b") compared the way the export folders hold them: Windows drops trailing dots and spaces from
    /// folder names, so a group "vi - MISC." is exported to (and imported from) a folder "vi - MISC".
    /// </summary>
    private static bool SameGroupPath(string a, string b)
    {
        static string Normalize(string path) => string.Join("/", path.Split('/').Select(s => s.TrimEnd('.', ' ')));
        return string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);
    }

    private static dynamic EnsureGroup(dynamic rootGroup, string groupPath, List<string> messages)
    {
        dynamic group = rootGroup;
        if (string.IsNullOrEmpty(groupPath))
        {
            return group;
        }

        foreach (var segment in groupPath.Split('/'))
        {
            dynamic? next = null;
            foreach (dynamic candidate in group.Groups)
            {
                if (SameGroupPath((string)candidate.Name, segment))
                {
                    next = candidate;
                    break;
                }
            }

            if (next is null)
            {
                next = group.Groups.Create(segment);
                messages.Add($"  created block group '{segment}'.");
            }

            group = next;
        }

        return group;
    }

    // object parameter on purpose: a dynamic argument would make the tuple result dynamic, and tuple element
    // names (Block/Path) don't exist at runtime.
    private static (dynamic? Block, string Path) FindBlockAnywhere(object groupObj, string blockName, string path)
    {
        dynamic group = groupObj;
        dynamic? found = BlockReimportService.FindBlockByName(group.Blocks, blockName);
        if (found is not null)
        {
            return (found, path);
        }

        foreach (dynamic sub in group.Groups)
        {
            string subPath = path.Length > 0 ? path + "/" + (string)sub.Name : (string)sub.Name;
            var hit = FindBlockAnywhere((object)sub, blockName, subPath);
            if (hit.Block is not null)
            {
                return hit;
            }
        }

        return (null, string.Empty);
    }

    private sealed class ExistingTag
    {
        public ExistingTag(dynamic tag, string name, string table, string address)
        {
            Tag = tag;
            Name = name;
            Table = table;
            Address = address;
        }

        public dynamic Tag { get; }
        public string Name { get; }
        public string Table { get; }
        public string Address { get; }
    }

    private static void CollectTags(dynamic tagGroup, List<ExistingTag> result)
    {
        foreach (dynamic table in tagGroup.TagTables)
        {
            string tableName = table.Name;
            foreach (dynamic tag in table.Tags)
            {
                result.Add(new ExistingTag(tag, (string)tag.Name, tableName, (string?)tag.LogicalAddress ?? string.Empty));
            }
        }

        foreach (dynamic sub in tagGroup.Groups)
        {
            CollectTags(sub, result);
        }
    }

    private static string Describe(Exception ex) =>
        ex is TargetInvocationException { InnerException: { } inner } ? inner.Message : ex.Message;
}
