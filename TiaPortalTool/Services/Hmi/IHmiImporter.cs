using System.Reflection;

namespace TiaPortalTool.Services.Hmi;

/// <summary>Imports one kind of HMI from a folder written by the matching <see cref="IHmiExporter"/>.</summary>
public interface IHmiImporter
{
    HmiKind Kind { get; }

    /// <summary>Imports the edited and new files in <paramref name="hmiDirectory"/> into <paramref name="hmi"/>. Doesn't compile or save.</summary>
    void Import(HmiDevice hmi, Assembly assembly, string hmiDirectory, HmiManifest manifest, HmiImportOptions options, HmiImportResult result, IProgress<string> progress, CancellationToken cancellationToken);
}
