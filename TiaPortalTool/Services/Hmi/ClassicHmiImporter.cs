using System.IO;
using System.Reflection;

namespace TiaPortalTool.Services.Hmi;

/// <summary>
/// Imports a classic WinCC HMI folder written by <see cref="ClassicHmiExporter"/>. Only objects whose file changed
/// since the export (or that are new) are imported, and an object that was also changed in TIA after the export is
/// a conflict: imported with <see cref="HmiImportOptions.Force"/>, otherwise decided by
/// <see cref="HmiImportOptions.ResolveConflict"/> (or left alone without one), so an edit made in TIA is never silently
/// reverted. Folders (screen groups, tag table groups, ...) are created as needed.
/// Order: cycles, lists and connections, then tags, scripts, templates, and screens last, so each import can bind
/// to what it references.
/// </summary>
public sealed class ClassicHmiImporter : IHmiImporter
{
    /// <summary>
    /// One kind of object: where its files are and where it goes in the HmiTarget. FolderProperty null = a flat
    /// collection on the HmiTarget; SingleImport set = one object imported by a method on the HmiTarget.
    /// </summary>
    private sealed class CategoryInfo
    {
        public CategoryInfo(string category, string subDirectory, string? folderProperty, string itemsProperty, bool hasSubFolders, string? singleImport = null)
        {
            Category = category;
            SubDirectory = subDirectory;
            FolderProperty = folderProperty;
            ItemsProperty = itemsProperty;
            HasSubFolders = hasSubFolders;
            SingleImport = singleImport;
        }

        public string Category { get; }
        public string SubDirectory { get; }
        public string? FolderProperty { get; }
        public string ItemsProperty { get; }
        public bool HasSubFolders { get; }
        public string? SingleImport { get; }
    }

    private static readonly CategoryInfo[] Categories =
    {
        new("Cycle", "Cycles", null, "Cycles", false),
        new("TextList", "TextLists", null, "TextLists", false),
        new("GraphicList", "GraphicLists", null, "GraphicLists", false),
        new("Connection", "Connections", null, "Connections", false),
        new("TagTable", "Tags", "TagFolder", "TagTables", true),
        new("VBScript", "Scripts", "VBScriptFolder", "VBScripts", true),
        new("ScreenTemplate", "Templates", "ScreenTemplateFolder", "ScreenTemplates", true),
        new("Screen", "Screens", "ScreenFolder", "Screens", true),
        new("ScreenPopup", "Popups", "ScreenPopupFolder", "ScreenPopups", true),
        new("ScreenSlidein", "Slideins", "ScreenSlideinFolder", "ScreenSlideins", false),
        new("ScreenGlobalElements", string.Empty, null, "ScreenGlobalElements", false, "ImportScreenGlobalElements"),
        new("ScreenOverview", string.Empty, null, "ScreenOverview", false, "ImportScreenOverview"),
    };

    public HmiKind Kind => HmiKind.ClassicWinCC;

    public void Import(HmiDevice hmi, Assembly assembly, string hmiDirectory, HmiManifest manifest, HmiImportOptions options, HmiImportResult result, IProgress<string> progress, CancellationToken cancellationToken)
    {
        var importOptionsType = assembly.GetType("Siemens.Engineering.ImportOptions")
            ?? throw new InvalidOperationException("Siemens.Engineering.ImportOptions type not found.");
        var exportOptionsType = assembly.GetType("Siemens.Engineering.ExportOptions")
            ?? throw new InvalidOperationException("Siemens.Engineering.ExportOptions type not found.");
        var context = new Context(hmi.Software, hmiDirectory, Enum.Parse(importOptionsType, "Override"), Enum.Parse(exportOptionsType, "WithDefaults"), options, result, manifest.HashVersion, progress, cancellationToken);

        try
        {
            // An empty manifest is a hand-made package of new objects, not an old export.
            if (manifest.Entries.Count > 0 && manifest.Entries.All(e => e.Hash.Length == 0))
            {
                result.Warnings.Add($"HMI {hmi.RuntimeName}: this export has no file hashes (made before the HMI import existed), so the import can't tell your edits "
                                    + "from changes made in TIA. Export again, edit, then import" + (options.Force ? "; importing anyway (force)." : ", or import with force."));
            }

            foreach (var info in Categories)
            {
                var items = CollectItems(info, hmiDirectory, manifest, result);
                if (items.Count == 0)
                {
                    continue;
                }

                progress.Report($"HMI {hmi.RuntimeName}: checking {items.Count} {info.Category} file(s)...");
                foreach (var item in items)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ImportItem(info, item, context);
                }
            }
        }
        finally
        {
            context.DeleteTempFolder();
        }
    }

    private sealed class WorkItem
    {
        public WorkItem(string file, string folderPath, HmiManifestEntry? entry)
        {
            File = file;
            FolderPath = folderPath;
            Entry = entry;
        }

        public string File { get; }
        public string FolderPath { get; }

        /// <summary>The manifest entry, or null for a file added after the export.</summary>
        public HmiManifestEntry? Entry { get; }

        public string Label(string category) => $"{category} '{(FolderPath.Length == 0 ? string.Empty : FolderPath + "/")}{Path.GetFileNameWithoutExtension(File)}'";
    }

    /// <summary>The manifest's files for this category, plus XML files added to its folder since the export.</summary>
    private static List<WorkItem> CollectItems(CategoryInfo info, string root, HmiManifest manifest, HmiImportResult result)
    {
        var items = new List<WorkItem>();
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in manifest.Entries.Where(e => e.Category == info.Category))
        {
            var file = Path.Combine(root, entry.File.Replace('/', '\\'));
            known.Add(Path.GetFullPath(file));
            if (!File.Exists(file))
            {
                result.Warnings.Add($"{info.Category} '{entry.Name}': its file {entry.File} is gone. Deleting objects isn't supported; it stays in TIA.");
                continue;
            }

            items.Add(new WorkItem(file, entry.FolderPath, entry));
        }

        // Single objects and flat lists without their own folder can't gain new files.
        if (info.SingleImport is not null || info.SubDirectory.Length == 0)
        {
            return items;
        }

        var directory = Path.Combine(root, info.SubDirectory);
        if (!Directory.Exists(directory))
        {
            return items;
        }

        foreach (var file in Directory.GetFiles(directory, "*.xml", info.HasSubFolders ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            if (known.Contains(Path.GetFullPath(file)))
            {
                continue;
            }

            var relativeDirectory = Path.GetDirectoryName(file)!.Substring(directory.TrimEnd('\\').Length).Trim('\\');
            items.Add(new WorkItem(file, relativeDirectory.Replace('\\', '/'), null));
        }

        return items;
    }

    private static void ImportItem(CategoryInfo info, WorkItem item, Context context)
    {
        var result = context.Result;
        var label = item.Label(info.Category);
        try
        {
            var fileHash = HmiXmlHash.Compute(item.File, context.HashVersion);
            var baseline = item.Entry?.Hash ?? string.Empty;
            var lastFileHash = item.Entry is null ? string.Empty : item.Entry.FileHash.Length > 0 ? item.Entry.FileHash : baseline;
            if (lastFileHash.Length > 0 && lastFileHash == fileHash)
            {
                // Not edited since the export: nothing to do, and no need to ask TIA.
                result.UnchangedCount++;
                return;
            }

            var name = HmiXmlHash.ReadObjectName(item.File)
                ?? throw new InvalidOperationException("no <Name> found in the file (not an HMI object export?).");
            if (item.Entry is not null && !string.Equals(name, item.Entry.Name, StringComparison.Ordinal))
            {
                result.Warnings.Add($"{label}: the object inside is now named '{name}' (exported as '{item.Entry.Name}'). It's imported as '{name}'; '{item.Entry.Name}' stays in TIA.");
            }

            // What's in TIA now, if anything.
            dynamic? container = FindContainer(info, item.FolderPath, context, create: false);
            dynamic? existing = info.SingleImport is not null ? SingleObject(info, context)
                : container is null ? null : FindItem(container, info.ItemsProperty, name);
            var overwritten = false;
            if (existing is not null)
            {
                var tiaFile = ExportCurrent(existing, context, label);
                try
                {
                    var currentHash = HmiXmlHash.Compute(tiaFile, context.HashVersion);
                    if (currentHash == fileHash)
                    {
                        result.UnchangedCount++;
                        return;
                    }

                    var changedInTia = baseline.Length == 0 || currentHash != baseline;
                    if (changedInTia && !context.Options.Force)
                    {
                        var conflict = new HmiConflict(label, HmiXmlDiff.Summarize(item.File, tiaFile, context.HashVersion), baseline.Length > 0);
                        var choice = context.Options.ResolveConflict?.Invoke(conflict) ?? HmiConflictChoice.Keep;
                        if (choice == HmiConflictChoice.Cancel)
                        {
                            throw new OperationCanceledException();
                        }

                        if (choice == HmiConflictChoice.Keep)
                        {
                            result.ConflictCount++;
                            result.Warnings.Add(baseline.Length == 0
                                ? $"{label}: '{name}' already exists in TIA and differs from the file, and there's no export hash to tell which is newer. Not imported; TIA's version is kept."
                                : $"{label}: changed in TIA since the export and in the file. Not imported, so TIA's version is kept.");
                            result.Warnings.Add("    What differs (file vs TIA):");
                            result.Warnings.AddRange(conflict.Differences.Select(d => "      " + d));
                            return;
                        }

                        overwritten = true;
                    }
                }
                finally
                {
                    File.Delete(tiaFile);
                }
            }

            using var heartbeat = ProgressHeartbeat.Start(context.Progress, $"importing {label}");
            if (info.SingleImport is not null)
            {
                // ImportScreenGlobalElements / ImportScreenOverview on the HmiTarget. Options as dynamic, like Export.
                object target = context.Target;
                target.GetType().GetMethod(info.SingleImport)!.Invoke(target, new[] { new FileInfo(item.File), context.ImportOptions });
            }
            else
            {
                container ??= FindContainer(info, item.FolderPath, context, create: true);
                dynamic composition = GetProperty(container!, info.ItemsProperty);
                composition.Import(new FileInfo(item.File), (dynamic)context.ImportOptions);
            }

            result.ImportedCount++;
            result.Messages.Add($"Imported {label}{(existing is null ? " (new)" : overwritten ? " (over TIA's changes, as chosen)" : string.Empty)}.");

            // New baseline: what TIA holds now. TIA may write the object back slightly differently from the file.
            var imported = info.SingleImport is not null ? SingleObject(info, context) : FindItem(FindContainer(info, item.FolderPath, context, create: false)!, info.ItemsProperty, name);
            if (imported is not null)
            {
                var newHash = ExportHash(imported, context, label);
                var relativeFile = item.File.Substring(context.Root.TrimEnd('\\').Length).Trim('\\').Replace('\\', '/');
                context.Result.ManifestUpdates.Add(new HmiManifestEntry { Category = info.Category, Name = name, FolderPath = item.FolderPath, File = relativeFile, Hash = newHash, FileHash = fileHash });
                if (newHash != fileHash)
                {
                    result.Messages.Add($"  note: TIA stores {label} differently from the file (it may have adjusted or dropped a value). Export it again to see what TIA kept.");
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            result.FailedCount++;
            result.Warnings.Add($"Import failed for {label}: {Describe(ex)}");
        }
    }

    /// <summary>The folder (or the HmiTarget itself for flat collections) that holds this category's items at <paramref name="folderPath"/>.</summary>
    private static dynamic? FindContainer(CategoryInfo info, string folderPath, Context context, bool create)
    {
        if (info.FolderProperty is null)
        {
            return context.Target;
        }

        dynamic folder = GetProperty(context.Target, info.FolderProperty);
        if (folderPath.Length == 0)
        {
            return folder;
        }

        foreach (var segment in folderPath.Split('/'))
        {
            dynamic folders = GetProperty(folder, "Folders");
            dynamic? next = null;
            foreach (dynamic candidate in folders)
            {
                if (string.Equals((string)candidate.Name, segment, StringComparison.Ordinal))
                {
                    next = candidate;
                    break;
                }
            }

            if (next is null)
            {
                if (!create)
                {
                    return null;
                }

                next = folders.Create(segment);
                context.Result.Messages.Add($"  created {info.Category} folder '{segment}'.");
            }

            folder = next;
        }

        return folder;
    }

    private static dynamic? FindItem(dynamic container, string itemsProperty, string name)
    {
        foreach (dynamic item in GetProperty(container, itemsProperty))
        {
            if (string.Equals(ItemName(item), name, StringComparison.Ordinal))
            {
                return item;
            }
        }

        return null;
    }

    private static dynamic? SingleObject(CategoryInfo info, Context context)
    {
        object target = context.Target;
        return target.GetType().GetProperty(info.ItemsProperty)?.GetValue(target);
    }

    /// <summary>Exports the object as it is in TIA now to a temp file; the caller deletes it.</summary>
    private static string ExportCurrent(dynamic item, Context context, string label)
    {
        var file = Path.Combine(context.TempFolder, Guid.NewGuid().ToString("N") + ".xml");
        using (ProgressHeartbeat.Start(context.Progress, $"reading {label} from TIA"))
        {
            item.Export(new FileInfo(file), (dynamic)context.ExportOptions);
        }

        return file;
    }

    /// <summary>Exports the object as it is in TIA now and hashes it the same way as the files.</summary>
    private static string ExportHash(dynamic item, Context context, string label)
    {
        string file = ExportCurrent(item, context, label);
        try
        {
            return HmiXmlHash.Compute(file, context.HashVersion);
        }
        finally
        {
            File.Delete(file);
        }
    }

    private static string ItemName(object item)
    {
        var type = item.GetType();
        var value = type.GetProperty("Name")?.GetValue(item) ?? type.GetProperty("SlideinType")?.GetValue(item);
        return value?.ToString() ?? string.Empty;
    }

    private static dynamic GetProperty(object owner, string property)
    {
        var info = owner.GetType().GetProperty(property)
            ?? throw new InvalidOperationException($"this TIA Portal version has no '{property}' on {owner.GetType().Name}.");
        return info.GetValue(owner)!;
    }

    private static string Describe(Exception ex) =>
        ex is TargetInvocationException { InnerException: { } inner } ? inner.Message : ex.Message;

    private sealed class Context
    {
        private string? _tempFolder;

        public Context(object target, string root, object importOptions, object exportOptions, HmiImportOptions options, HmiImportResult result, int hashVersion, IProgress<string> progress, CancellationToken cancellationToken)
        {
            HashVersion = hashVersion;
            Progress = progress;
            Target = target;
            Root = root;
            ImportOptions = importOptions;
            ExportOptions = exportOptions;
            Options = options;
            Result = result;
            CancellationToken = cancellationToken;
        }

        public object Target { get; }
        public string Root { get; }
        public object ImportOptions { get; }
        public object ExportOptions { get; }
        public HmiImportOptions Options { get; }
        public HmiImportResult Result { get; }

        /// <summary>The manifest's hash version; every hash in one import is made the same way.</summary>
        public int HashVersion { get; }

        public IProgress<string> Progress { get; }
        public CancellationToken CancellationToken { get; }

        public string TempFolder
        {
            get
            {
                if (_tempFolder is null)
                {
                    _tempFolder = Path.Combine(Path.GetTempPath(), "hmi-import-" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(_tempFolder);
                }

                return _tempFolder;
            }
        }

        public void DeleteTempFolder()
        {
            if (_tempFolder is not null && Directory.Exists(_tempFolder))
            {
                Directory.Delete(_tempFolder, recursive: true);
            }
        }
    }
}
