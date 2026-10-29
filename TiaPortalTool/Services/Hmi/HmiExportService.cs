using System.IO;
using System.Reflection;
using System.Text.Json;

namespace TiaPortalTool.Services.Hmi;

/// <summary>Finds every HMI in the project and hands each to the exporter for its kind. Writes to &lt;export folder&gt;\HMI\&lt;device&gt;.</summary>
public sealed class HmiExportService
{
    public const string HmiFolderName = "HMI";

    private readonly IReadOnlyList<IHmiExporter> _exporters = new IHmiExporter[]
    {
        new ClassicHmiExporter(),
        // WinCC Unified (Siemens.Engineering.HmiUnified) goes here when a job needs it.
    };

    public HmiExportResult ExportAll(dynamic project, Assembly assembly, string exportDirectory, IProgress<string> progress, CancellationToken cancellationToken)
    {
        var result = new HmiExportResult();
        progress.Report("Looking for HMI devices...");
        IReadOnlyList<HmiDevice> hmis = HmiSoftwareLocator.FindAll(project, assembly);
        if (hmis.Count == 0)
        {
            result.Warnings.Add("No HMI found in this project, so there was nothing to export for HMI.");
            return result;
        }

        var usedFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var hmi in hmis)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var exporter = _exporters.FirstOrDefault(e => e.Kind == hmi.Kind);
            if (exporter is null)
            {
                result.Warnings.Add($"HMI {hmi.Describe()}: exporting this kind of HMI isn't supported yet, skipped.");
                continue;
            }

            // One folder per HMI, named after the station (the name people see in the device tree).
            var folderName = SanitizeFileName(hmi.DeviceName);
            if (!usedFolders.Add(folderName))
            {
                folderName = SanitizeFileName($"{hmi.DeviceName}_{hmi.RuntimeName}");
                usedFolders.Add(folderName);
            }

            var hmiDirectory = Path.Combine(exportDirectory, HmiFolderName, folderName);
            exporter.Export(hmi, assembly, hmiDirectory, result, progress, cancellationToken);
            result.ExportFolders.Add(hmiDirectory);
            if (hmi.Kind == HmiKind.ClassicWinCC)
            {
                progress.Report($"HMI {hmi.RuntimeName}: checking the compiled runtime for objects the export leaves out...");
                CheckRuntimeOnlyObjects((object)project, hmi, hmiDirectory, result);
            }
        }

        return result;
    }

    /// <summary>
    /// Lists the objects the compiled runtime has but no exported file holds, and records them in the manifest: an
    /// import of the screen or template they're on would delete them. Reads RtData.plf as it is (no compile), so it's
    /// as current as the last compile that was saved.
    /// </summary>
    private static void CheckRuntimeOnlyObjects(object project, HmiDevice hmi, string hmiDirectory, HmiExportResult result)
    {
        var manifestPath = Path.Combine(hmiDirectory, ClassicHmiExporter.ManifestFileName);
        if (!File.Exists(manifestPath))
        {
            return;
        }

        try
        {
            // The compiler's database: as of the last saved compile, and it holds screen objects as their own records.
            var file = HmiRuntimeGuard.FindRuntimeFile(project, hmi, HmiRuntimeSource.Database);
            if (file is null)
            {
                result.Messages.Add($"  HMI {hmi.RuntimeName} has no compiled runtime in this project folder, so the export can't tell which objects it leaves out "
                                    + "(Openness exports skip alarm views and trend views). Compile the HMI and export again to check.");
                return;
            }

            var names = HmiRuntimeNames.Read(file);
            var values = new HashSet<string>(StringComparer.Ordinal);
            foreach (var xml in Directory.GetFiles(hmiDirectory, "*.xml", SearchOption.AllDirectories))
            {
                values.UnionWith(HmiItemDiff.Values(xml));
            }

            var missing = names.Counts.Keys
                .Where(n => !values.Contains(n) && !(n.IndexOf('.') is var dot and > 0 && values.Contains(n.Substring(0, dot))))
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToList();
            var manifest = JsonSerializer.Deserialize<HmiManifest>(File.ReadAllText(manifestPath)) ?? new HmiManifest();
            manifest.RuntimeOnlyObjects = missing;
            manifest.RuntimeChecked = names.LastWriteUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
            File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
            if (missing.Count == 0)
            {
                result.Messages.Add($"  HMI {hmi.RuntimeName}: every object in the compiled runtime (compiled {manifest.RuntimeChecked}) is in the exported files.");
                return;
            }

            result.Warnings.Add($"HMI {hmi.RuntimeName}: the compiled runtime (compiled {manifest.RuntimeChecked}) has {missing.Count} object(s) that no exported file holds: "
                                + $"{string.Join(", ", missing)}. Openness leaves them out of the export (alarm views and trend views at least). Importing the screen or "
                                + "template they're on would delete them: the import's lost-object check stops that save. Listed in hmi_manifest.json (RuntimeOnlyObjects). "
                                + $"The runtime doesn't say which screen each is on. Read from {file}, whose format isn't documented, so the list is a heuristic.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            result.Warnings.Add($"HMI {hmi.RuntimeName}: couldn't check the compiled runtime for objects the export leaves out: {ex.Message}");
        }
    }

    private static string SanitizeFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(c, '_');
        }
        return name;
    }
}
