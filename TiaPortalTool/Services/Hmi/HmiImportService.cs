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
            result.Messages.Add($"HMI {hmi.Describe()}: importing from {hmiFolder}");
            importer.Import(hmi, assembly, hmiFolder, manifest, options, result, progress, cancellationToken);

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
            }
            else if (options.Compile)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var compile = PlcCompiler.Compile(hmi.Software, assembly, result.Messages, progress, $"HMI {hmi.RuntimeName}");
                if (!compile.Clean)
                {
                    result.CompileFailed = true;
                    result.FailedCompiles.Add(compile);
                }
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
