using System.IO;
using System.Reflection;

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
        }

        return result;
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
