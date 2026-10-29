using System.IO;

namespace TiaPortalTool.Services.Hmi;

/// <summary>
/// Catches HMI objects an import deletes without anyone asking. Openness exports leave some screen objects out (alarm
/// views and trend views at least), and an import replaces the whole screen or template from its file, so importing
/// an exported screen deletes them, with no error. The guard compares the names in the compiled runtime download
/// (<see cref="HmiRuntimeSource.Download"/>) before the import with those after the import's compile. A name the
/// runtime has fewer of, and that neither TIA's version of an imported object nor its file holds, is an object the
/// files couldn't show: a lost object, and the import must not be saved unless the user overrides it. What the files
/// do show (items deleted or renamed on purpose, edited texts) is left to <see cref="HmiItemDiff"/>.
/// </summary>
public static class HmiRuntimeGuard
{
    private static readonly (string FolderProperty, string ItemsProperty)[] ContainerTrees =
    {
        ("ScreenFolder", "Screens"),
        ("ScreenTemplateFolder", "ScreenTemplates"),
        ("ScreenPopupFolder", "ScreenPopups"),
    };

    public const string HeuristicNote =
        "The check reads TIA's compiled runtime (IM\\HMI\\C\\<n>\\Generates\\pdata.fwc in the project folder), whose format isn't documented, "
        + "so it's a heuristic: it can miss a loss, or list a name the import changed on purpose.";

    /// <summary>The "before" reading: the runtime's names right before the first object is replaced.</summary>
    public sealed class Snapshot
    {
        public string? File { get; set; }
        public HmiRuntimeNames? Names { get; set; }

        /// <summary>Why there's nothing to compare with. Not empty = the check can't run.</summary>
        public List<string> Problems { get; } = new();
    }

    /// <summary>
    /// Compiles the HMI (about a minute when nothing changed; every compile rewrites the download file) and reads its
    /// runtime names. A problem never throws: it's recorded, and the import then can't be saved unforced.
    /// </summary>
    public static Snapshot TakeBefore(dynamic project, HmiDevice hmi, System.Reflection.Assembly assembly, HmiImportOptions options, List<string> messages, IProgress<string> progress)
    {
        var snapshot = new Snapshot();
        if (!options.Compile)
        {
            snapshot.Problems.Add("the import isn't compiled (no-compile option), so there's no runtime to compare");
            return snapshot;
        }

        try
        {
            progress.Report($"HMI {hmi.RuntimeName}: compiling before the import, to read which objects the runtime has (lost-object check)...");

            // Only the outcome matters here; the compile after the import lists the warnings.
            var compile = PlcCompiler.Compile(hmi.Software, assembly, new List<string>(), progress, $"HMI {hmi.RuntimeName} (before the import)");
            if (!compile.Clean)
            {
                messages.Add($"  The HMI already had compile errors before the import; the lost-object check uses the runtime file of the last compile that wrote one.");
            }

            snapshot.File = FindRuntimeFile(project, hmi, HmiRuntimeSource.Download);
            if (snapshot.File is null)
            {
                snapshot.Problems.Add($"no compiled runtime (IM\\HMI\\C\\<n>\\{HmiRuntimeNames.RelativePath(HmiRuntimeSource.Download)}) for this HMI in the project folder");
                return snapshot;
            }

            snapshot.Names = HmiRuntimeNames.Read(snapshot.File);
            if (snapshot.Names.Counts.Count == 0)
            {
                snapshot.Problems.Add($"no object names could be read from {snapshot.File} (unknown format?)");
                snapshot.Names = null;
            }
            else
            {
                messages.Add($"  Lost-object check: {snapshot.Names.Counts.Count} name(s) in the runtime before the import ({snapshot.File}, written {snapshot.Names.LastWriteUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}).");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var message = ex is System.Reflection.TargetInvocationException { InnerException: { } inner } ? inner.Message : ex.Message;
            snapshot.Problems.Add($"the HMI couldn't be compiled or its runtime file read before the import: {message}");
            snapshot.Names = null;
        }

        return snapshot;
    }

    /// <summary>
    /// The "after" reading, once the import's compile ran: names the runtime has fewer of than before and that no
    /// imported file or TIA version of it holds. <paramref name="compiled"/> = the HMI was compiled after the import.
    /// </summary>
    public static HmiGuardFindings Check(Snapshot before, HmiDevice hmi, IReadOnlyList<HmiImportedObject> imported, bool compiled)
    {
        var problems = before.Problems.ToList();
        var lost = new List<HmiLostObject>();
        if (before.Names is null || problems.Count > 0)
        {
            return new HmiGuardFindings(hmi.RuntimeName, lost, problems);
        }

        if (!compiled)
        {
            problems.Add("the HMI wasn't compiled after the import");
            return new HmiGuardFindings(hmi.RuntimeName, lost, problems);
        }

        HmiRuntimeNames after;
        try
        {
            after = HmiRuntimeNames.Read(before.File!);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problems.Add($"the runtime file couldn't be read after the import: {ex.Message}");
            return new HmiGuardFindings(hmi.RuntimeName, lost, problems);
        }

        // A compile with errors (or nothing to do) leaves the file as it was, and then it shows nothing about the import.
        if (after.LastWriteUtc <= before.Names.LastWriteUtc)
        {
            problems.Add($"the compile after the import didn't rewrite {before.File}, so it doesn't show what the import did");
            return new HmiGuardFindings(hmi.RuntimeName, lost, problems);
        }

        if (after.Counts.Count == 0)
        {
            problems.Add($"no object names could be read from {before.File} after the import");
            return new HmiGuardFindings(hmi.RuntimeName, lost, problems);
        }

        // Every value the imported files and TIA's versions of them show. A name among them was visible: removed or
        // renamed on purpose, or an edited text; one TIA dropped from a file is reported by HmiItemDiff. A runtime
        // name can be "tag.member", so its tag part counts too.
        var shown = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in imported)
        {
            shown.UnionWith(item.FileValues);
            if (item.TiaValuesBefore is not null)
            {
                shown.UnionWith(item.TiaValuesBefore);
            }
        }

        bool Shown(string name) =>
            shown.Contains(name) || (name.IndexOf('.') is var dot and > 0 && shown.Contains(name.Substring(0, dot)));

        // Imported objects that existed before and hold screen items: the lost objects were on one of them.
        var holders = imported.Where(i => i.HoldsScreenItems && i.TiaValuesBefore is not null).ToList();
        foreach (var pair in before.Names.Counts.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            after.Counts.TryGetValue(pair.Key, out var count);
            if (count >= pair.Value || Shown(pair.Key))
            {
                continue;
            }

            lost.Add(new HmiLostObject(pair.Key, pair.Value, count, holders.Select(h => h.Label).ToList()));
        }

        return new HmiGuardFindings(hmi.RuntimeName, lost, problems);
    }

    /// <summary>That compiled file of this HMI, or null when the HMI was never compiled in this project folder.</summary>
    public static string? FindRuntimeFile(dynamic project, HmiDevice hmi, HmiRuntimeSource source)
    {
        string? projectDirectory = ProjectDirectory(project);
        return projectDirectory is null ? null : HmiRuntimeNames.FindFileFor(projectDirectory, source, ContainerNames(hmi.Software));
    }

    private static string? ProjectDirectory(dynamic project)
    {
        try
        {
            FileInfo path = project.Path;
            return path.DirectoryName;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Names of the HMI's screens, templates and pop-ups, to recognise its runtime file.</summary>
    private static IEnumerable<string> ContainerNames(object target)
    {
        var names = new List<string>();
        foreach (var (folderProperty, itemsProperty) in ContainerTrees)
        {
            var root = target.GetType().GetProperty(folderProperty)?.GetValue(target);
            if (root is not null)
            {
                CollectNames(root, itemsProperty, names);
            }
        }

        return names;
    }

    private static void CollectNames(dynamic folder, string itemsProperty, List<string> names)
    {
        object owner = folder;
        if (owner.GetType().GetProperty(itemsProperty)?.GetValue(owner) is System.Collections.IEnumerable items)
        {
            foreach (dynamic item in items)
            {
                names.Add((string)item.Name);
            }
        }

        foreach (dynamic sub in folder.Folders)
        {
            CollectNames(sub, itemsProperty, names);
        }
    }
}
