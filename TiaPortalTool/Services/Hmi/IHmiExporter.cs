using System.Reflection;

namespace TiaPortalTool.Services.Hmi;

/// <summary>
/// Exports one kind of HMI. Classic WinCC and WinCC Unified have different Openness object models, so each gets
/// its own implementation; <see cref="HmiExportService"/> picks the one that handles the device.
/// </summary>
public interface IHmiExporter
{
    HmiKind Kind { get; }

    /// <summary>Exports everything the exporter supports into <paramref name="hmiDirectory"/> and writes hmi_manifest.json there.</summary>
    void Export(HmiDevice hmi, Assembly assembly, string hmiDirectory, HmiExportResult result, IProgress<string> progress, CancellationToken cancellationToken);
}
