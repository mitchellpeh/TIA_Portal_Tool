using System.IO;
using System.Reflection;
using System.Text.Json;
using Microsoft.CSharp.RuntimeBinder;

namespace TiaPortalTool.Services.Hmi;

/// <summary>
/// Exports a classic WinCC HMI (Basic / Comfort / RT Advanced, including RT Advanced on an IPC) as SimaticML XML,
/// one file per object, keeping the folder structure of screens, templates, pop-ups, tag tables and scripts.
/// Layout under the HMI's folder: Screens, Templates, Popups, Slideins, Tags, Connections, TextLists,
/// GraphicLists, Cycles, Scripts, plus GlobalElements.xml / ScreenOverview.xml and hmi_manifest.json.
/// </summary>
public sealed class ClassicHmiExporter : IHmiExporter
{
    public const string ManifestFileName = "hmi_manifest.json";

    public HmiKind Kind => HmiKind.ClassicWinCC;

    public void Export(HmiDevice hmi, Assembly assembly, string hmiDirectory, HmiExportResult result, IProgress<string> progress, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(hmiDirectory);
        var exportOptionsType = assembly.GetType("Siemens.Engineering.ExportOptions")
            ?? throw new InvalidOperationException("Siemens.Engineering.ExportOptions type not found.");
        var context = new Context(hmiDirectory, Enum.Parse(exportOptionsType, "WithDefaults"), result, cancellationToken);
        dynamic target = hmi.Software;

        // (category, folder property on HmiTarget, item collection on each folder, sub-folder for the files)
        var folderTrees = new[]
        {
            ("Screen", "ScreenFolder", "Screens", "Screens"),
            ("ScreenTemplate", "ScreenTemplateFolder", "ScreenTemplates", "Templates"),
            ("ScreenPopup", "ScreenPopupFolder", "ScreenPopups", "Popups"),
            ("ScreenSlidein", "ScreenSlideinFolder", "ScreenSlideins", "Slideins"),
            ("TagTable", "TagFolder", "TagTables", "Tags"),
            ("VBScript", "VBScriptFolder", "VBScripts", "Scripts"),
        };
        foreach (var (category, folderProperty, itemsProperty, subDirectory) in folderTrees)
        {
            progress.Report($"HMI {hmi.RuntimeName}: exporting {subDirectory.ToLowerInvariant()}...");
            var root = TryGet(target, folderProperty, context, category);
            if (root is not null)
            {
                ExportFolderTree(root, itemsProperty, category, subDirectory, string.Empty, context);
            }
        }

        // Flat lists on the HmiTarget itself.
        var lists = new[]
        {
            ("Connection", "Connections", "Connections"),
            ("TextList", "TextLists", "TextLists"),
            ("GraphicList", "GraphicLists", "GraphicLists"),
            ("Cycle", "Cycles", "Cycles"),
        };
        foreach (var (category, property, subDirectory) in lists)
        {
            progress.Report($"HMI {hmi.RuntimeName}: exporting {subDirectory.ToLowerInvariant()}...");
            var items = TryGet(target, property, context, category);
            if (items is not null)
            {
                foreach (dynamic item in items)
                {
                    ExportItem(item, category, subDirectory, string.Empty, context);
                }
            }
        }

        // Single objects.
        foreach (var (category, property) in new[] { ("ScreenGlobalElements", "ScreenGlobalElements"), ("ScreenOverview", "ScreenOverview") })
        {
            var single = TryGet(target, property, context, category);
            if (single is not null)
            {
                ExportItem(single, category, string.Empty, string.Empty, context, fileName: property);
            }
        }

        var manifest = new HmiManifest
        {
            DeviceName = hmi.DeviceName,
            RuntimeName = hmi.RuntimeName,
            Kind = hmi.Kind.ToString(),
            Entries = context.Entries
        };
        File.WriteAllText(Path.Combine(hmiDirectory, ManifestFileName), JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));

        result.ExportedCount += context.Entries.Count;
        result.Messages.Add($"HMI {hmi.Describe()}: exported {context.Entries.Count} object(s) to {hmiDirectory}");
        foreach (var group in context.Entries.GroupBy(e => e.Category))
        {
            result.Messages.Add($"  {group.Key}: {group.Count()}");
        }
    }

    private static void ExportFolderTree(dynamic folder, string itemsProperty, string category, string subDirectory, string folderPath, Context context)
    {
        var items = TryGet(folder, itemsProperty, context, category);
        if (items is not null)
        {
            foreach (dynamic item in items)
            {
                ExportItem(item, category, subDirectory, folderPath, context);
            }
        }

        // Not every folder type has sub-folders (slide-ins, for one).
        var subFolders = TryGet(folder, "Folders", null, category);
        if (subFolders is null)
        {
            return;
        }

        foreach (dynamic subFolder in subFolders)
        {
            string name = subFolder.Name;
            ExportFolderTree(subFolder, itemsProperty, category, subDirectory, folderPath.Length == 0 ? name : $"{folderPath}/{name}", context);
        }
    }

    private static void ExportItem(dynamic item, string category, string subDirectory, string folderPath, Context context, string? fileName = null)
    {
        context.CancellationToken.ThrowIfCancellationRequested();
        string name = "(unknown)";
        try
        {
            name = fileName ?? ItemName(item);
            var relativeFolder = Path.Combine(subDirectory, SanitizePath(folderPath));
            var folder = Path.Combine(context.Root, relativeFolder);
            Directory.CreateDirectory(folder);
            var relativeFile = Path.Combine(relativeFolder, SanitizeFileName(name) + ".xml");
            var fullPath = Path.Combine(context.Root, relativeFile);
            if (File.Exists(fullPath))
            {
                // Export() won't overwrite, so a file from an earlier run would block it.
                File.Delete(fullPath);
            }

            // The options must go in as dynamic: a statically typed object argument won't bind to ExportOptions.
            item.Export(new FileInfo(fullPath), (dynamic)context.ExportOptions);
            context.Entries.Add(new HmiManifestEntry { Category = category, Name = name, FolderPath = folderPath, File = relativeFile.Replace('\\', '/') });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            context.Result.Warnings.Add($"Skipped {category} '{(folderPath.Length == 0 ? name : folderPath + "/" + name)}': {Describe(ex)}");
        }
    }

    /// <summary>Most HMI objects have a Name; slide-in screens don't, they're identified by their side (SlideinType: Top, Bottom, Left, Right).</summary>
    private static string ItemName(object item)
    {
        var type = item.GetType();
        var value = type.GetProperty("Name")?.GetValue(item) ?? type.GetProperty("SlideinType")?.GetValue(item);
        return value?.ToString() ?? throw new InvalidOperationException($"{type.Name} has neither a Name nor a SlideinType.");
    }

    /// <summary>Reads a property that may not exist in every TIA Portal version. Missing is reported once as a warning when a context is given.</summary>
    private static dynamic? TryGet(dynamic owner, string property, Context? context, string category)
    {
        try
        {
            object owned = owner;
            var info = owned.GetType().GetProperty(property);
            if (info is null)
            {
                context?.Result.Warnings.Add($"{category}: this TIA Portal version has no '{property}', skipped.");
                return null;
            }

            return info.GetValue(owned);
        }
        catch (Exception ex) when (ex is RuntimeBinderException or TargetInvocationException or NotSupportedException)
        {
            context?.Result.Warnings.Add($"{category}: couldn't read '{property}': {Describe(ex)}");
            return null;
        }
    }

    private static string Describe(Exception ex) =>
        ex is TargetInvocationException { InnerException: { } inner } ? inner.Message : ex.Message;

    private static string SanitizeFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(c, '_');
        }
        return name;
    }

    private static string SanitizePath(string path) =>
        path.Length == 0 ? string.Empty : Path.Combine(path.Split('/').Select(SanitizeFileName).ToArray());

    private sealed class Context
    {
        public Context(string root, object exportOptions, HmiExportResult result, CancellationToken cancellationToken)
        {
            Root = root;
            ExportOptions = exportOptions;
            Result = result;
            CancellationToken = cancellationToken;
        }

        public string Root { get; }
        public object ExportOptions { get; }
        public HmiExportResult Result { get; }
        public CancellationToken CancellationToken { get; }
        public List<HmiManifestEntry> Entries { get; } = new();
    }
}
