using System.Globalization;
using System.IO;

namespace TiaPortalTool.Services;

/// <summary>
/// Project texts (Tools > Project texts in TIA): every multilingual text in the project, e.g. HMI alarm texts and
/// block comments, as one Excel sheet. Export gives the file to edit; import writes the edited texts back.
/// Import Folder picks up any *.projecttexts.xlsx in the folder.
/// </summary>
public sealed class ProjectTextService
{
    public const string FilePattern = "*.projecttexts.xlsx";

    public static List<string> FindFiles(string folder) =>
        Directory.GetFiles(folder, FilePattern, SearchOption.AllDirectories).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Imports one file; returns false when TIA reports an error. Source-language texts are updated too.</summary>
    public bool Import(dynamic project, string file, List<string> messages)
    {
        try
        {
            dynamic result = project.ImportProjectTexts(new FileInfo(file), true);
            var state = Describe(() => (string)result.State.ToString());
            messages.Add($"Project texts {Path.GetFileName(file)}: {state}");
            AddResultMessages(result, messages);
            return !state.StartsWith("Error", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            messages.Add($"Project texts {Path.GetFileName(file)}: import failed: {ex.InnerException?.Message ?? ex.Message}");
            return false;
        }
    }

    /// <summary>Exports all project texts for a source and a target language (e.g. en-US, de-DE).</summary>
    public void Export(dynamic project, string file, string sourceCulture, string targetCulture, List<string> messages)
    {
        if (File.Exists(file))
        {
            File.Delete(file);   // TIA refuses to overwrite
        }

        dynamic result = project.ExportProjectTexts(new FileInfo(file), new CultureInfo(sourceCulture), new CultureInfo(targetCulture));
        messages.Add($"Project texts exported ({sourceCulture} -> {targetCulture}) to {file}: {Describe(() => (string)result.State.ToString())}");
        AddResultMessages(result, messages);
    }

    private static void AddResultMessages(dynamic result, List<string> messages)
    {
        try
        {
            foreach (dynamic message in result.Messages)
            {
                messages.Add("  " + Describe(() => (string)message.Message) + " " + Describe(() => (string)message.State.ToString()));
            }
        }
        catch (Exception)
        {
            // older Openness versions return no message list
        }
    }

    private static string Describe(Func<string> get)
    {
        try
        {
            return get();
        }
        catch (Exception)
        {
            return "done";
        }
    }
}
