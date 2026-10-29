using System.IO;
using System.Reflection;
using System.Text.Json;

namespace TiaPortalTool.Services.Hmi;

/// <summary>
/// Imports HMI folders written by the HMI export back into the project. The folder can be one HMI's folder
/// (with hmi_manifest.json), the export's HMI folder, or the export folder itself. Each HMI folder goes into the HMI
/// with the same station and runtime name (or the only HMI of that kind, if the names don't match). Compiles every
/// HMI that got something imported; saving is left to the caller (<see cref="HmiImportResult.CanSave"/>).
/// </summary>
public sealed class HmiImportService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly IReadOnlyList<IHmiImporter> _importers = new IHmiImporter[]
    {
        new ClassicHmiImporter(),
    };

    /// <summary>The HMI folders (each with a manifest) at or under <paramref name="folder"/>.</summary>
    public static IReadOnlyList<string> FindHmiFolders(string folder)
    {
        if (File.Exists(Path.Combine(folder, ClassicHmiExporter.ManifestFileName)))
        {
            return new[] { folder };
        }

        foreach (var candidate in new[] { folder, Path.Combine(folder, HmiExportService.HmiFolderName) })
        {
            if (!Directory.Exists(candidate))
            {
                continue;
            }

            var found = Directory.GetDirectories(candidate)
                .Where(d => File.Exists(Path.Combine(d, ClassicHmiExporter.ManifestFileName)))
                .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (found.Count > 0)
            {
                return found;
            }
        }

        return Array.Empty<string>();
    }

    public HmiImportResult ImportAll(dynamic project, Assembly assembly, string folder, HmiImportOptions options, IProgress<string> progress, CancellationToken cancellationToken)
    {
        var result = new HmiImportResult();
        var hmiFolders = FindHmiFolders(folder);
        if (hmiFolders.Count == 0)
        {
            result.FailedCount++;
            result.Warnings.Add($"No HMI export found in {folder} (expected {ClassicHmiExporter.ManifestFileName} there, or in HMI\\<device> below it).");
            return result;
        }

        progress.Report("Looking for HMI devices...");
        IReadOnlyList<HmiDevice> hmis = HmiSoftwareLocator.FindAll(project, assembly);
        foreach (var hmiFolder in hmiFolders)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var manifestPath = Path.Combine(hmiFolder, ClassicHmiExporter.ManifestFileName);
            HmiManifest manifest;
            try
            {
                manifest = JsonSerializer.Deserialize<HmiManifest>(File.ReadAllText(manifestPath)) ?? new HmiManifest();
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                result.FailedCount++;
                result.Warnings.Add($"Couldn't read {manifestPath}: {ex.Message}");
                continue;
            }

            var hmi = MatchHmi(hmis, manifest, result);
            if (hmi is null)
            {
                result.FailedCount++;
                continue;
            }

            var importer = _importers.FirstOrDefault(i => i.Kind == hmi.Kind);
            if (importer is null)
            {
                result.FailedCount++;
                result.Warnings.Add($"HMI {hmi.Describe()}: importing this kind of HMI isn't supported yet.");
                continue;
            }

            var importedBefore = result.ImportedCount;
            var failedBefore = result.FailedCount;
            var objectsBefore = result.ImportedObjects.Count;
            var changesBefore = result.ItemChanges.Count;
            result.Messages.Add($"HMI {hmi.Describe()}: importing from {hmiFolder}");
            if (manifest.RuntimeOnlyObjects.Count > 0)
            {
                result.Messages.Add($"  At export, the compiled runtime had {manifest.RuntimeOnlyObjects.Count} object(s) the files can't hold: {string.Join(", ", manifest.RuntimeOnlyObjects)}. "
                                    + "Importing the screen or template that has them deletes them; the lost-object check will stop the save if that happens.");
            }

            // The guard's "before" reading is taken only once something is actually about to be replaced.
            HmiRuntimeGuard.Snapshot? snapshot = null;
            object projectObject = project;
            importer.Import(hmi, assembly, hmiFolder, manifest, options, result,
                () => snapshot = HmiRuntimeGuard.TakeBefore(projectObject, hmi, assembly, options, result.Messages, progress), progress, cancellationToken);

            if (result.ManifestUpdates.Count > 0)
            {
                foreach (var update in result.ManifestUpdates)
                {
                    manifest.Entries.RemoveAll(e => e.Category == update.Category && string.Equals(e.File, update.File, StringComparison.OrdinalIgnoreCase));
                    manifest.Entries.Add(update);
                }

                result.ManifestUpdates.Clear();
                result.PendingManifests.Add((manifestPath, manifest));
            }

            var imported = result.ImportedCount - importedBefore;
            if (imported == 0)
            {
                continue;
            }

            if (result.FailedCount > failedBefore)
            {
                result.Messages.Add($"HMI {hmi.RuntimeName}: not compiled because of the import failures.");
                continue;
            }

            if (options.Compile)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var compile = PlcCompiler.Compile(hmi.Software, assembly, result.Messages, progress, $"HMI {hmi.RuntimeName}");
                if (!compile.Clean)
                {
                    result.CompileFailed = true;
                    result.FailedCompiles.Add(compile);
                }
            }

            if (snapshot is not null)
            {
                progress.Report($"HMI {hmi.RuntimeName}: checking the compiled runtime for lost objects...");
                var findings = HmiRuntimeGuard.Check(snapshot, hmi, result.ImportedObjects.Skip(objectsBefore).ToList(), options.Compile);
                result.GuardFindings.Add(findings);
                DecideLostObjects(findings, options, result);
            }

            if (!result.SaveRefused)
            {
                DecideLayoutChanges(hmi, result.ItemChanges.Skip(changesBefore).Where(c => c.IsLayout).ToList(), options, result);
            }
        }

        result.Messages.Add($"HMI import: {result.ImportedCount} imported, {result.UnchangedCount} unchanged, {result.ConflictCount} conflict(s), {result.FailedCount} failed.");
        return result;
    }

    /// <summary>
    /// Writes the new hashes of the imported objects into the manifests, so the next import from the same folder sees
    /// them as unchanged. Call only after the project was saved.
    /// </summary>
    public static void WriteManifests(HmiImportResult result)
    {
        foreach (var (path, manifest) in result.PendingManifests)
        {
            File.WriteAllText(path, JsonSerializer.Serialize(manifest, JsonOptions));
        }
    }

    /// <summary>Reports the lost-object guard's findings, and refuses the save unless forced or confirmed.</summary>
    private static void DecideLostObjects(HmiGuardFindings findings, HmiImportOptions options, HmiImportResult result)
    {
        var name = findings.RuntimeName;
        if (findings.Lost.Count == 0 && findings.Problems.Count == 0)
        {
            result.Messages.Add($"HMI {name}: lost-object check passed: every object name of the compiled runtime is still there after the import (heuristic, see the notes).");
            return;
        }

        if (findings.Problems.Count > 0)
        {
            result.Warnings.Add($"HMI {name}: CAN'T VERIFY that the import deleted no objects: {string.Join("; ", findings.Problems)}.");
        }

        if (findings.Lost.Count > 0)
        {
            result.Warnings.Add($"HMI {name}: LOST OBJECTS: {findings.Lost.Count} object(s) of the compiled runtime are gone after the import. Openness exports leave some "
                                + "screen objects out of the files (alarm views and trend views at least), and an import replaces the whole screen or template, so it deletes them:");
            result.Warnings.AddRange(findings.Lost.Select(l => "    " + l));
        }

        result.Warnings.Add("    " + HmiRuntimeGuard.HeuristicNote);
        if (options.ForceLostObjects)
        {
            result.Warnings.Add("    Saving anyway, as asked (force lost objects).");
            return;
        }

        if (options.ConfirmLostObjects?.Invoke(findings) == true)
        {
            result.Warnings.Add("    You chose to save anyway.");
            return;
        }

        result.SaveRefused = true;
        result.Warnings.Add("    Not saving. Add the objects back in TIA after the import, or override: headless --force-lost-objects, or Yes in the window's prompt.");
    }

    /// <summary>Reports position/size changes TIA made on import, and refuses the save unless accepted or confirmed.</summary>
    private static void DecideLayoutChanges(HmiDevice hmi, IReadOnlyList<HmiItemChange> layout, HmiImportOptions options, HmiImportResult result)
    {
        if (layout.Count == 0)
        {
            return;
        }

        const int MaxListed = 20;
        result.Warnings.Add($"HMI {hmi.RuntimeName}: TIA changed the position or size of {layout.Count} screen item value(s) while importing (for example, a faceplate "
                            + "with Resizing = KeepRatio is re-fitted to its ratio):");
        result.Warnings.AddRange(layout.Take(MaxListed).Select(c => "    " + c));
        if (layout.Count > MaxListed)
        {
            result.Warnings.Add($"    ... and {layout.Count - MaxListed} more (all of them are listed with the import messages).");
        }

        if (options.AcceptLayoutChanges)
        {
            result.Warnings.Add("    Saving anyway, as asked (accept layout changes).");
            return;
        }

        if (options.ConfirmLayoutChanges?.Invoke(layout) == true)
        {
            result.Warnings.Add("    You chose to save anyway.");
            return;
        }

        result.SaveRefused = true;
        result.Warnings.Add("    Not saving. Fix the files (e.g. Resizing = None, or sizes that fit the ratio), or override: headless --accept-layout-changes, or Yes in the window's prompt.");
    }

    private static HmiDevice? MatchHmi(IReadOnlyList<HmiDevice> hmis, HmiManifest manifest, HmiImportResult result)
    {
        var exact = hmis.FirstOrDefault(h => h.DeviceName == manifest.DeviceName && h.RuntimeName == manifest.RuntimeName)
                    ?? hmis.FirstOrDefault(h => h.DeviceName == manifest.DeviceName);
        if (exact is not null)
        {
            return exact;
        }

        var sameKind = hmis.Where(h => h.Kind.ToString() == manifest.Kind).ToList();
        if (sameKind.Count == 1)
        {
            result.Warnings.Add($"No HMI named {manifest.DeviceName} / {manifest.RuntimeName} in this project; importing into {sameKind[0].Describe()}, the only one of that kind.");
            return sameKind[0];
        }

        result.Warnings.Add(sameKind.Count == 0
            ? $"No {manifest.Kind} HMI in this project to import {manifest.DeviceName} / {manifest.RuntimeName} into."
            : $"No HMI named {manifest.DeviceName} / {manifest.RuntimeName} in this project, and there are {sameKind.Count} of that kind to choose from. Rename the folder's manifest DeviceName to the target station.");
        return null;
    }
}
