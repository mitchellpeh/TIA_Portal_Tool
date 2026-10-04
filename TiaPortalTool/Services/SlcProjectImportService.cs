using System.IO;
using System.Reflection;

namespace TiaPortalTool.Services;

public sealed class SlcImportTarget
{
    /// <summary>Folder that will hold the new project's own folder.</summary>
    public string NewProjectDirectory { get; set; } = string.Empty;

    public string NewProjectName { get; set; } = string.Empty;

    /// <summary>An existing .apNN project; when set, the converted blocks are added to it.</summary>
    public string? ExistingProjectPath { get; set; }

    public bool S71200 { get; set; }

    public bool IsNewProject => ExistingProjectPath is null;
}

public sealed class SlcImportResult
{
    public bool Success { get; set; }
    public bool Saved { get; set; }
    public string ProjectPath { get; set; } = string.Empty;
    public List<string> Messages { get; } = new();
}

/// <summary>
/// Puts a converted SLC folder into TIA Portal: either a new project with a basic CPU of the chosen family, or an
/// existing project. Blocks and types with the same name are overwritten; everything else in the project is left alone.
/// </summary>
public sealed class SlcProjectImportService
{
    private readonly TiaSessionService _sessionService = new();
    private readonly FolderImportService _folderImportService = new();

    /// <summary>The TIA Portal major version the import will use, so the converter can write matching SimaticML.</summary>
    public static int? TargetVersion(SlcImportTarget target) => target.IsNewProject
        ? OpennessProjectProbe.GetNewestInstalledVersion()
        : OpennessProjectProbe.TryParseTargetVersion(target.ExistingProjectPath!);

    public SlcImportResult Import(SlcImportTarget target, string convertedFolder, IProgress<string> progress, CancellationToken cancellationToken)
    {
        var result = new SlcImportResult();
        TiaSession? session = null;
        try
        {
            session = target.IsNewProject
                ? _sessionService.Create(target.NewProjectDirectory, target.NewProjectName, progress)
                : _sessionService.Open(target.ExistingProjectPath!, progress);
            cancellationToken.ThrowIfCancellationRequested();

            result.ProjectPath = target.IsNewProject ? ProjectFilePath(session) : target.ExistingProjectPath!;
            var assembly = Assembly.LoadFrom(session.AssemblyPath);

            if (target.IsNewProject)
            {
                PlcDeviceCreator.CreatePlc(session.Project, target.S71200, progress);
                result.Messages.Add("Use Change device in TIA Portal to switch the CPU to the real one.");
            }

            dynamic? plcSoftware = PlcSoftwareLocator.FindFirstPlcSoftware(session.Project, assembly);
            if (plcSoftware is null)
            {
                result.Messages.Add("No PLC found in the project. Add a CPU to it first.");
                return result;
            }

            FolderImportResult import = _folderImportService.ImportAndCompile((object)plcSoftware, assembly, convertedFolder, progress, cancellationToken);

            // Show the import results before the save step, in the order they happened.
            foreach (var message in import.Messages)
            {
                progress.Report(message);
            }

            result.Success = import.Success;
            cancellationToken.ThrowIfCancellationRequested();

            // A new project is worth keeping even with errors (there's nothing to protect); an existing one is only
            // saved after a clean compile, like Import Folder.
            if (import.Success || target.IsNewProject)
            {
                progress.Report("Saving the project...");
                session.Save();
                result.Saved = true;
                result.Messages.Add(import.Success ? "Project saved." : "Project saved with the errors above so you can fix them in TIA Portal.");
            }
            else
            {
                result.Messages.Add("Project NOT saved because of the errors above.");
            }

            return result;
        }
        catch (OperationCanceledException)
        {
            result.Messages.Add("Cancelled. The project was closed without saving.");
            return result;
        }
        catch (TiaSessionOpenException ex)
        {
            result.Messages.Add(ex.Message);
            return result;
        }
        catch (Exception ex)
        {
            result.Messages.Add("Import failed: " + ex);
            return result;
        }
        finally
        {
            if (session is not null)
            {
                progress.Report("Closing the project and TIA Portal...");
                session.Dispose();
            }
        }
    }

    private static string ProjectFilePath(TiaSession session)
    {
        try
        {
            return ((FileInfo)session.Project.Path).FullName;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }
}
