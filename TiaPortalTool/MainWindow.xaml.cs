using Microsoft.Win32;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using TiaPortalTool.Services;
using TiaPortalTool.Services.Hmi;

namespace TiaPortalTool;

public partial class MainWindow : Window
{
    private enum Outcome { Success, Warning, Error, Cancelled }


    private sealed class OperationResult
    {
        public OperationResult(Outcome outcome, string summary, IReadOnlyList<string> messages)
        {
            Outcome = outcome;
            Summary = summary;
            Messages = messages;
        }

        public Outcome Outcome { get; }
        public string Summary { get; }
        public IReadOnlyList<string> Messages { get; }
    }

    // Theme resource keys (ThemeManager); set with SetResourceReference so a theme change recolours them.
    private const string TextBrush = "Text.Body";
    private const string MutedBrush = "Text.Muted";
    private const string SuccessBrush = "Hint.Success";
    private const string WarningBrush = "Hint.Warning";
    private const string ErrorBrush = "Hint.Error";
    private const string InputBorderBrush = "Input.Border";

    private readonly OpennessProjectProbe _probe = new();
    private readonly TiaSessionService _sessionService = new();
    private readonly WorkbookExportService _workbookExportService = new();
    private readonly WorkbookReimportService _workbookReimportService = new();
    private readonly FolderImportService _folderImportService = new();
    private readonly HmiExportService _hmiExportService = new();
    private readonly HmiImportService _hmiImportService = new();
    private readonly AppSettingsService _appSettingsService = new();

    // The Output pane: colour-coded log that pulses when TIA Portal may be waiting for the user.
    private readonly OutputLog _log;

    // Log file written line by line during a run, so a hung or killed run still leaves a log.
    private StreamWriter? _logFile;
    private readonly Stopwatch _stopwatch = new();
    private readonly DispatcherTimer _elapsedTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _busy;
    private CancellationTokenSource? _cancellation;
    private string? _resultFolder;

    // Export folder overrides and last Import Folder choices, per project (see ProjectKey).
    private readonly Dictionary<string, string> _exportFolders = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _importFolders = new(StringComparer.OrdinalIgnoreCase);
    private string? _projectKey;
    private string _legacyOutputDirectory = string.Empty;

    public MainWindow()
    {
        InitializeComponent();
        WindowSizing.FitToScreen(this);

        Title = "TIA Portal Import / Export";
        VersionText.Text = AppVersions.Footer("Import / Export", AppVersions.ImportExport);

        _log = new OutputLog(this, LogBox, OutputCard);
        _log.LineWritten += WriteLogFile;
        _log.AttentionChanged += OnAttentionChanged;
        _elapsedTimer.Tick += (_, _) =>
        {
            UpdateElapsed();
            if (_busy)
            {
                _log.CheckForStall();
            }
        };

        var settings = _appSettingsService.Load();
        foreach (var pair in settings.ExportFolders)
        {
            _exportFolders[pair.Key] = pair.Value;
        }

        foreach (var pair in settings.ImportFolders)
        {
            _importFolders[pair.Key] = pair.Value;
        }

        // Older settings had one global output folder; attach it to the last-used project.
        var lastKey = ProjectKey(settings.ProjectPath);
        if (!string.IsNullOrWhiteSpace(settings.OutputDirectory) && lastKey is not null && !_exportFolders.ContainsKey(lastKey))
        {
            _exportFolders[lastKey] = settings.OutputDirectory;
        }
        else if (lastKey is null)
        {
            _legacyOutputDirectory = settings.OutputDirectory;
        }

        // Setting the path loads that project's export folder via ProjectPathTextBox_TextChanged.
        ProjectPathTextBox.Text = settings.ProjectPath;
        ExportTagsCheckBox.IsChecked = settings.ExportTags;
        ExportCommentsCheckBox.IsChecked = settings.ExportBlocks;
        CompileBeforeExportCheckBox.IsChecked = settings.CompileBeforeExport;
        ExportHmiCheckBox.IsChecked = settings.ExportHmi;
        OpenAfterImportCheckBox.IsChecked = settings.OpenProjectAfterImport;
        SaveWithErrorsCheckBox.IsChecked = settings.SaveWithCompileErrors;
        UpdateProjectHint();
        UpdateOutputHint();

        Closing += MainWindow_Closing;
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_busy)
        {
            var answer = MessageBox.Show(
                this,
                "An operation is still running. Closing now stops it part-way, and the project won't be saved.\n\nClose anyway?",
                "Operation in progress",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }
        }

        // Update the saved settings rather than replacing them, so settings this window doesn't own (the theme) survive.
        var settings = _appSettingsService.Load();
        settings.ProjectPath = ProjectPathTextBox.Text?.Trim() ?? string.Empty;
        settings.OutputDirectory = _legacyOutputDirectory;
        settings.ExportFolders = new Dictionary<string, string>(_exportFolders);
        settings.ImportFolders = new Dictionary<string, string>(_importFolders);
        settings.ExportTags = ExportTagsCheckBox.IsChecked == true;
        settings.ExportBlocks = ExportCommentsCheckBox.IsChecked == true;
        settings.CompileBeforeExport = CompileBeforeExportCheckBox.IsChecked == true;
        settings.ExportHmi = ExportHmiCheckBox.IsChecked == true;
        settings.OpenProjectAfterImport = OpenAfterImportCheckBox.IsChecked == true;
        settings.SaveWithCompileErrors = SaveWithErrorsCheckBox.IsChecked == true;
        _appSettingsService.Save(settings);
    }

    // ---- Project and output folder ----

    private void BrowseProject_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "TIA Portal project files (*.ap*)|*.ap*|All files (*.*)|*.*",
            Title = "Select a TIA Portal project"
        };

        if (dialog.ShowDialog(this) == true)
        {
            ProjectPathTextBox.Text = dialog.FileName;
        }
    }

    private void BrowseOutputDirectory_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Select the export folder for this project"
        };

        if (!string.IsNullOrWhiteSpace(OutputDirectoryTextBox.Text) && Directory.Exists(OutputDirectoryTextBox.Text))
        {
            dialog.SelectedPath = OutputDirectoryTextBox.Text;
        }

        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            OutputDirectoryTextBox.Text = dialog.SelectedPath;
        }
    }

    private void ProjectPathTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        // Only switch once the path names a real project file, so typing a path doesn't
        // wipe the export folder on every keystroke.
        var key = ProjectKey(ProjectPathTextBox.Text?.Trim());
        if (key != _projectKey)
        {
            _projectKey = key;
            if (key is not null)
            {
                OutputDirectoryTextBox.Text = _exportFolders.TryGetValue(key, out var folder) ? folder : string.Empty;
            }
        }

        UpdateProjectHint();
        UpdateOutputHint();
    }

    private void OutputDirectoryTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_projectKey is not null)
        {
            var folder = OutputDirectoryTextBox.Text?.Trim() ?? string.Empty;
            if (folder.Length == 0)
            {
                _exportFolders.Remove(_projectKey);
            }
            else
            {
                _exportFolders[_projectKey] = folder;
            }
        }

        UpdateOutputHint();
    }

    /// <summary>Settings key for a project: its lower-cased full path, or null if the file doesn't exist.</summary>
    private static string? ProjectKey(string? projectPath)
    {
        if (string.IsNullOrWhiteSpace(projectPath) || !File.Exists(projectPath))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(projectPath).ToLowerInvariant();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void OpenOutputDirectory_Click(object sender, RoutedEventArgs e)
    {
        var directory = TryGetExportDirectory();
        if (directory is not null)
        {
            OpenFolder(directory);
        }
    }

    private void OpenResultFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_resultFolder is not null)
        {
            OpenFolder(_resultFolder);
        }
    }

    private void UpdateProjectHint()
    {
        var path = ProjectPathTextBox.Text?.Trim() ?? string.Empty;
        if (path.Length == 0)
        {
            SetProjectHint("No project selected.", MutedBrush, InputBorderBrush);
        }
        else if (!File.Exists(path))
        {
            SetProjectHint("File not found.", ErrorBrush, ErrorBrush);
        }
        else if (OpennessProjectProbe.TryParseTargetVersion(path) is not int version)
        {
            SetProjectHint($"Not a TIA Portal project file (expected .apNN, got '{Path.GetExtension(path)}').", WarningBrush, WarningBrush);
        }
        else
        {
            SetProjectHint($"TIA Portal V{version} project: {Path.GetFileNameWithoutExtension(path)}", SuccessBrush, InputBorderBrush);
        }
    }

    private void SetProjectHint(string text, string foreground, string border)
    {
        ProjectHintText.Text = text;
        ProjectHintText.SetResourceReference(TextBlock.ForegroundProperty, foreground);
        ProjectPathTextBox.SetResourceReference(Control.BorderBrushProperty, border);
    }

    private void UpdateOutputHint()
    {
        var directory = TryGetExportDirectory();
        if (directory is null)
        {
            OutputHintText.Text = @"Leave blank to use Desktop\TIAExport\<project name>.";
            OpenOutputButton.IsEnabled = false;
            return;
        }

        var exists = Directory.Exists(directory);
        var usingDefault = string.IsNullOrWhiteSpace(OutputDirectoryTextBox.Text);
        OutputHintText.Text = (usingDefault ? "Blank, so the default is used: " + directory + "." : "Export and Reimport use this folder.")
            + (exists ? string.Empty : " It's created on the first export.")
            + (_projectKey is null ? string.Empty : " Remembered for this project.");
        OpenOutputButton.IsEnabled = exists;
    }

    private string? TryGetExportDirectory()
    {
        var projectPath = ProjectPathTextBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(projectPath) && string.IsNullOrWhiteSpace(OutputDirectoryTextBox.Text))
        {
            return null;
        }

        return GetExportDirectory(projectPath ?? string.Empty);
    }

    private string GetExportDirectory(string projectPath)
    {
        var overridePath = OutputDirectoryTextBox.Text?.Trim();
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            return overridePath!;
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            "TIAExport",
            Path.GetFileNameWithoutExtension(projectPath));
    }

    // ---- Actions ----

    private async void ExportSelected_Click(object sender, RoutedEventArgs e)
    {
        var projectPath = RequireProject("exporting");
        if (projectPath is null)
        {
            return;
        }

        var exportTags = ExportTagsCheckBox.IsChecked == true;
        var exportBlocks = ExportCommentsCheckBox.IsChecked == true;
        var compileBeforeExport = exportBlocks && CompileBeforeExportCheckBox.IsChecked == true;
        var exportHmi = ExportHmiCheckBox.IsChecked == true;
        if (!exportTags && !exportBlocks && !exportHmi)
        {
            ShowNotice("Select at least one export option.");
            return;
        }

        var exportDirectory = GetExportDirectory(projectPath);
        await RunOperationAsync(
            "Export",
            "Opening the project and exporting...",
            (progress, cancellationToken) => RunExport(projectPath, exportTags, exportBlocks, compileBeforeExport, exportHmi, exportDirectory, progress, cancellationToken),
            logDirectory: exportDirectory,
            resultFolder: exportDirectory);
    }

    private OperationResult RunExport(string projectPath, bool exportTags, bool exportBlocks, bool compileBeforeExport, bool exportHmi, string exportDirectory, IProgress<string> progress, CancellationToken cancellationToken) =>
        WithSession(projectPath, "Export", progress, cancellationToken, (session, assembly) =>
        {
            var messages = new List<string>();
            var warnings = new List<string>();

            // PLC: tags and blocks to Excel. Only these need a PLC in the project.
            if (exportTags || exportBlocks)
            {
                dynamic? plcSoftware = PlcSoftwareLocator.FindFirstPlcSoftware(session.Project, assembly);
                if (plcSoftware is null)
                {
                    warnings.Add("No PLC software found in this project, so tags and blocks weren't exported.");
                }
                else
                {
                    WorkbookExportResult result = _workbookExportService.ExportAll((object)plcSoftware, assembly, exportDirectory, exportTags, exportBlocks, compileBeforeExport, progress, cancellationToken);
                    messages.AddRange(result.Messages);
                    messages.Add($"Wrote {result.WorkbookPaths.Count} workbook(s) to {exportDirectory}:");
                    messages.AddRange(result.WorkbookPaths.Select(p => "  " + Path.GetFileName(p)));
                    warnings.AddRange(result.Warnings);
                }
            }

            // HMI: every HMI device, as XML.
            if (exportHmi)
            {
                cancellationToken.ThrowIfCancellationRequested();
                HmiExportResult hmiResult = _hmiExportService.ExportAll(session.Project, assembly, exportDirectory, progress, cancellationToken);
                messages.AddRange(hmiResult.Messages);
                warnings.AddRange(hmiResult.Warnings);
            }

            if (warnings.Count == 0)
            {
                return new OperationResult(Outcome.Success, "Export completed.", messages);
            }

            messages.Add($"{warnings.Count} warning(s):");
            messages.AddRange(warnings.Select(w => "  " + w));
            return new OperationResult(Outcome.Warning, $"Export completed with {warnings.Count} warning(s).", messages);
        });

    private async void Reimport_Click(object sender, RoutedEventArgs e)
    {
        var projectPath = RequireProject("reimporting");
        if (projectPath is null)
        {
            return;
        }

        var exportDirectory = GetExportDirectory(projectPath);
        if (!Directory.Exists(exportDirectory))
        {
            ShowNotice($"No export folder found at {exportDirectory}. Export first.");
            return;
        }

        if (!Confirm(
                "Confirm reimport",
                $"Apply the edited workbooks in\n{exportDirectory}\n\nto the project\n{projectPath}?\n\n"
                + $"The PLC program is compiled{SaveRule()} "
                + "Archive the project first if you want a backup."))
        {
            return;
        }

        var saveWithErrors = SaveWithErrorsCheckBox.IsChecked == true;
        await RunOperationAsync(
            "Reimport",
            "Opening the project and reimporting...",
            (progress, cancellationToken) => RunReimport(projectPath, exportDirectory, saveWithErrors, progress, cancellationToken),
            logDirectory: exportDirectory,
            resultFolder: null,
            openProjectAfter: OpenAfterImportCheckBox.IsChecked == true ? projectPath : null);
    }

    private OperationResult RunReimport(string projectPath, string exportDirectory, bool saveWithErrors, IProgress<string> progress, CancellationToken cancellationToken) =>
        WithPlcSoftware(projectPath, "Reimport", progress, cancellationToken, (session, plcSoftware, assembly) =>
        {
            WorkbookReimportResult result = _workbookReimportService.ReimportAndCompile(plcSoftware, assembly, exportDirectory, progress, cancellationToken);
            var messages = result.Messages.ToList();

            var compileErrors = 0;
            if (!result.Success)
            {
                if (!result.OnlyCompileFailed
                    || !SaveDespiteCompileErrors(new[] { result.Compile! }, result.TouchedBlocks, saveWithErrors, messages))
                {
                    messages.Add("Project NOT saved due to compile errors or failures above.");
                    return new OperationResult(Outcome.Error, "Reimport finished with errors. The project was not saved.", messages);
                }

                compileErrors = Math.Max(1, result.Compile!.Errors.Count);
            }

            // A cancel that arrived during the compile still means "don't save".
            cancellationToken.ThrowIfCancellationRequested();
            progress.Report("Saving the project...");
            session.Save();
            messages.Add("Project saved.");
            return compileErrors == 0
                ? new OperationResult(Outcome.Success, "Reimport completed and the project was saved.", messages)
                : new OperationResult(Outcome.Warning, "Reimport completed and the project was saved " + WithErrorsNote(compileErrors), messages);
        });

    private async void ImportFolder_Click(object sender, RoutedEventArgs e)
    {
        var projectPath = RequireProject("importing");
        if (projectPath is null)
        {
            return;
        }

        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Select the import folder (*.tags.tsv tag tables, *.xml blocks, *.scl sources; subfolders = block groups; HMI\\<device> = HMI export)"
        };

        var key = ProjectKey(projectPath);
        if (key is not null && _importFolders.TryGetValue(key, out var lastImportFolder) && Directory.Exists(lastImportFolder))
        {
            dialog.SelectedPath = lastImportFolder;
        }

        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK)
        {
            return;
        }

        var importFolder = dialog.SelectedPath;
        if (key is not null)
        {
            _importFolders[key] = importFolder;
        }
        var hmiFolders = HmiImportService.FindHmiFolders(importFolder);
        var hmiNote = hmiFolders.Count == 0
            ? string.Empty
            : $"\n\nHMI exports found, imported too (changed and new objects; for objects also changed in TIA since the export you're asked which version to keep):\n{string.Join("\n", hmiFolders)}";
        if (!Confirm(
                "Confirm import",
                $"Import tag tables and blocks from\n{importFolder}\n\ninto the project\n{projectPath}?{hmiNote}\n\n"
                + $"Tags and blocks with the same names are replaced. The PLC program (and any HMI imported into) is compiled{SaveRule()} "
                + "Archive the project first if you want a backup."))
        {
            return;
        }

        var saveWithErrors = SaveWithErrorsCheckBox.IsChecked == true;
        await RunOperationAsync(
            "Import Folder",
            "Opening the project and importing...",
            (progress, cancellationToken) => RunFolderImport(projectPath, importFolder, saveWithErrors, progress, cancellationToken),
            logDirectory: importFolder,
            resultFolder: null,
            openProjectAfter: OpenAfterImportCheckBox.IsChecked == true ? projectPath : null);
    }

    private OperationResult RunFolderImport(string projectPath, string importFolder, bool saveWithErrors, IProgress<string> progress, CancellationToken cancellationToken) =>
        WithPlcSoftware(projectPath, "Import", progress, cancellationToken, (session, plcSoftware, assembly) =>
        {
            FolderImportResult result = _folderImportService.ImportAndCompile(plcSoftware, assembly, importFolder, progress, cancellationToken);
            var messages = result.Messages.ToList();

            // Asked right after the PLC compile, so a "no" doesn't wait for a slow HMI import first.
            var compileErrors = 0;
            if (!result.Success)
            {
                if (!result.OnlyCompileFailed
                    || !SaveDespiteCompileErrors(new[] { result.Compile! }, result.TouchedBlocks, saveWithErrors, messages))
                {
                    messages.Add("Project NOT saved due to the import/compile errors above.");
                    return new OperationResult(Outcome.Error, "Import finished with errors. The project was not saved.", messages);
                }

                compileErrors += Math.Max(1, result.Compile!.Errors.Count);
            }

            // HMI exports in the same folder: same conflict check as Import HMI, so a change made in TIA is never lost unasked.
            HmiImportResult? hmi = null;
            if (HmiImportService.FindHmiFolders(importFolder).Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                hmi = _hmiImportService.ImportAll(session.Project, assembly, importFolder, new HmiImportOptions { ResolveConflict = AskConflict }, progress, cancellationToken);
                messages.AddRange(hmi.Messages);
                if (hmi.Warnings.Count > 0)
                {
                    messages.Add($"{hmi.Warnings.Count} HMI warning(s):");
                    messages.AddRange(hmi.Warnings.Select(w => "  " + w));
                }

                if (hmi.FailedCount > 0
                    || (hmi.CompileFailed && !SaveDespiteCompileErrors(hmi.FailedCompiles, null, saveWithErrors, messages)))
                {
                    messages.Add("Project NOT saved due to the HMI import/compile errors above.");
                    return new OperationResult(Outcome.Error, "HMI import finished with errors. The project was not saved.", messages);
                }

                compileErrors += hmi.FailedCompiles.Sum(c => Math.Max(1, c.Errors.Count));

                if (result.NothingForPlc && hmi.ImportedCount == 0)
                {
                    return hmi.ConflictCount > 0
                        ? new OperationResult(Outcome.Warning, $"Nothing imported: {hmi.ConflictCount} HMI object(s) were kept as they are in TIA (see the warnings).", messages)
                        : new OperationResult(Outcome.Success, "Nothing to import: the HMI objects in the folder already match the project.", messages);
                }
            }

            // Project texts last: they can name HMI alarms and blocks imported above.
            foreach (var textFile in ProjectTextService.FindFiles(importFolder))
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress.Report($"Importing project texts {Path.GetFileName(textFile)}...");
                if (!new ProjectTextService().Import(session.Project, textFile, messages))
                {
                    messages.Add("Project NOT saved due to the project text import error above.");
                    return new OperationResult(Outcome.Error, "Project text import failed. The project was not saved.", messages);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            progress.Report("Saving the project...");
            session.Save();
            if (hmi is not null)
            {
                HmiImportService.WriteManifests(hmi);
            }

            messages.Add("Project saved.");
            if (compileErrors > 0)
            {
                var kept = hmi is { ConflictCount: > 0 } ? $" {hmi.ConflictCount} HMI object(s) were kept as they are in TIA." : string.Empty;
                return new OperationResult(Outcome.Warning, "Import completed and the project was saved " + WithErrorsNote(compileErrors) + kept, messages);
            }

            return hmi is { ConflictCount: > 0 }
                ? new OperationResult(Outcome.Warning, $"Import completed and the project was saved; {hmi.ConflictCount} HMI object(s) were kept as they are in TIA.", messages)
                : new OperationResult(Outcome.Success, "Import completed and the project was saved.", messages);
        });

    private async void ImportHmi_Click(object sender, RoutedEventArgs e)
    {
        var projectPath = RequireProject("importing");
        if (projectPath is null)
        {
            return;
        }

        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Select the HMI export to import: an HMI\\<device> folder, the HMI folder, or the export folder"
        };

        var defaultFolder = Path.Combine(GetExportDirectory(projectPath), HmiExportService.HmiFolderName);
        if (Directory.Exists(defaultFolder))
        {
            dialog.SelectedPath = defaultFolder;
        }

        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK)
        {
            return;
        }

        var hmiFolders = HmiImportService.FindHmiFolders(dialog.SelectedPath);
        if (hmiFolders.Count == 0)
        {
            ShowNotice($"No HMI export found in {dialog.SelectedPath}. Pick a folder made by Export with the HMI option (it has an hmi_manifest.json).");
            return;
        }

        if (!Confirm(
                "Confirm HMI import",
                $"Import the HMI objects changed or added in\n{string.Join("\n", hmiFolders)}\n\ninto the project\n{projectPath}?\n\n"
                + "Objects with the same names are replaced. For objects that were also changed in TIA since the export, you're "
                + $"asked which version to keep. The HMI is compiled{SaveRule()} "
                + "Archive the project first if you want a backup."))
        {
            return;
        }

        var saveWithErrors = SaveWithErrorsCheckBox.IsChecked == true;
        await RunOperationAsync(
            "Import HMI",
            "Opening the project and importing the HMI...",
            (progress, cancellationToken) => RunHmiImport(projectPath, dialog.SelectedPath, saveWithErrors, progress, cancellationToken),
            logDirectory: dialog.SelectedPath,
            resultFolder: null,
            openProjectAfter: OpenAfterImportCheckBox.IsChecked == true ? projectPath : null);
    }

    private OperationResult RunHmiImport(string projectPath, string folder, bool saveWithErrors, IProgress<string> progress, CancellationToken cancellationToken) =>
        WithSession(projectPath, "Import HMI", progress, cancellationToken, (session, assembly) =>
        {
            HmiImportResult result = _hmiImportService.ImportAll(session.Project, assembly, folder, new HmiImportOptions { ResolveConflict = AskConflict }, progress, cancellationToken);
            var messages = result.Messages.ToList();
            if (result.Warnings.Count > 0)
            {
                messages.Add($"{result.Warnings.Count} warning(s):");
                messages.AddRange(result.Warnings.Select(w => "  " + w));
            }

            if (result.ImportedCount == 0 && result.FailedCount == 0)
            {
                var nothing = result.ConflictCount > 0
                    ? $"Nothing imported: {result.ConflictCount} object(s) were kept as they are in TIA (see the warnings)."
                    : "Nothing to import: no HMI file was changed or added since the export.";
                return new OperationResult(result.ConflictCount > 0 ? Outcome.Warning : Outcome.Success, nothing, messages);
            }

            if (!result.CanSave
                && (!result.OnlyCompileFailed || !SaveDespiteCompileErrors(result.FailedCompiles, null, saveWithErrors, messages)))
            {
                messages.Add("Project NOT saved due to the import/compile errors above.");
                return new OperationResult(Outcome.Error, "HMI import finished with errors. The project was not saved.", messages);
            }

            cancellationToken.ThrowIfCancellationRequested();
            progress.Report("Saving the project...");
            session.Save();
            HmiImportService.WriteManifests(result);
            messages.Add("Project saved.");
            if (result.CompileFailed)
            {
                var errors = result.FailedCompiles.Sum(c => Math.Max(1, c.Errors.Count));
                var kept = result.ConflictCount > 0 ? $" {result.ConflictCount} object(s) were kept as they are in TIA." : string.Empty;
                return new OperationResult(Outcome.Warning, "HMI import completed and the project was saved " + WithErrorsNote(errors) + kept, messages);
            }

            return result.ConflictCount > 0
                ? new OperationResult(Outcome.Warning, $"HMI import completed and the project was saved; {result.ConflictCount} object(s) were kept as they are in TIA.", messages)
                : new OperationResult(Outcome.Success, "HMI import completed and the project was saved.", messages);
        });

    /// <summary>The end of the confirm dialogs' "... is compiled" sentence, following the "save even with errors" box.</summary>
    private string SaveRule() => SaveWithErrorsCheckBox.IsChecked == true
        ? ", and the project is saved even if the compile has errors (as long as every file imported)."
        : "; if the compile has errors you're asked whether to save anyway.";

    private static string WithErrorsNote(int errors) =>
        $"with {errors} compile error(s). It can't be downloaded until they're fixed (see the log).";

    /// <summary>
    /// Everything imported but a compile has errors: logs them (split by whether they're in a block this import brought
    /// in, when <paramref name="touchedBlocks"/> is given), then saves when the "save even with errors" box was ticked,
    /// or asks. Called from the import's worker thread, so the prompt runs on the UI thread.
    /// </summary>
    private bool SaveDespiteCompileErrors(IReadOnlyList<CompileOutcome> compiles, IReadOnlyCollection<string>? touchedBlocks, bool saveWithErrors, List<string> messages)
    {
        var errors = compiles.SelectMany(c => c.Errors).ToList();
        var lines = new List<string>();
        if (touchedBlocks is null)
        {
            lines.AddRange(errors.Select(e => "  " + e));
        }
        else
        {
            bool Touched(CompileError e) => touchedBlocks.Contains(e.BlockName, StringComparer.OrdinalIgnoreCase);
            var inImported = errors.Where(Touched).ToList();
            var inOthers = errors.Where(e => !Touched(e)).ToList();
            if (inImported.Count > 0)
            {
                lines.Add($"In blocks this import brought in ({inImported.Count}):");
                lines.AddRange(inImported.Select(e => "  " + e));
            }

            if (inOthers.Count > 0)
            {
                lines.Add($"In blocks this import didn't change ({inOthers.Count}): already in the project, or caused by imported tags or types:");
                lines.AddRange(inOthers.Select(e => "  " + e));
            }
        }

        if (errors.Count == 0)
        {
            lines.Add("  (TIA listed no individual errors; see the compiler messages above.)");
        }

        var what = string.Join(" and ", compiles.Select(c => c.What));
        messages.Add($"Compile errors in the {what}:");
        messages.AddRange(lines.Select(l => "  " + l));

        if (saveWithErrors)
        {
            messages.Add("\"Save even if the compile has errors\" is ticked: saving anyway.");
            return true;
        }

        const int MaxShown = 15;
        var shown = string.Join("\n", lines.Take(MaxShown)) + (lines.Count > MaxShown ? $"\n  ... and {lines.Count - MaxShown} more line(s) in the log" : string.Empty);
        var question = $"Everything imported, but the compile of the {what} has {(errors.Count == 0 ? "errors" : $"{errors.Count} error(s)")}:\n\n{shown}\n\n"
                       + "Save the project anyway? It can't be downloaded until the errors are fixed.\n\n"
                       + "Yes = save with the errors\nNo = don't save (the project file stays as it was)";
        var save = Dispatcher.Invoke(() => MessageBox.Show(this, question, "Compile errors", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No)) == MessageBoxResult.Yes;
        messages.Add(save ? "You chose to save the project with the compile errors." : "You chose not to save the project.");
        return save;
    }

    /// <summary>
    /// Asks which version to keep for an HMI object changed both in its file and in TIA. Called from the import's worker
    /// thread, so the prompt runs on the UI thread.
    /// </summary>
    private HmiConflictChoice AskConflict(HmiConflict conflict)
    {
        var differences = conflict.Differences.Count == 0
            ? "  (no difference left once values TIA works out itself are ignored)"
            : string.Join("\n", conflict.Differences.Select(d => "  " + d));
        var why = conflict.HasBaseline
            ? "was changed in TIA since the export, and its file was changed too."
            : "differs from its file, and the export has no hash to tell which is newer.";
        var message = $"{conflict.Label} {why}\n\nWhat differs (file vs TIA now):\n{differences}\n\n"
                      + "Yes = import the file over TIA's version\nNo = keep TIA's version (listed as a conflict)\nCancel = stop the import; nothing is saved";
        return Dispatcher.Invoke(() => MessageBox.Show(this, message, "HMI object changed on both sides", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning, MessageBoxResult.No)) switch
        {
            MessageBoxResult.Yes => HmiConflictChoice.Overwrite,
            MessageBoxResult.No => HmiConflictChoice.Keep,
            _ => HmiConflictChoice.Cancel,
        };
    }

    private async void Diagnostics_Click(object sender, RoutedEventArgs e)
    {
        var projectPath = ProjectPathTextBox.Text?.Trim();
        await RunOperationAsync(
            "Diagnostics",
            "Running diagnostics...",
            (_, _) =>
            {
                var result = _probe.Diagnose(projectPath);
                return new OperationResult(result.ProblemCount > 0 ? Outcome.Warning : Outcome.Success, result.StatusMessage, result.Notes);
            },
            logDirectory: null,
            resultFolder: null,
            cancellable: false);
    }

    /// <summary>
    /// Opens the project headless, finds its PLC software, runs <paramref name="work"/>, and always closes the session.
    /// A cancel closes the project without saving. It's checked between steps, since a running Openness call can't be interrupted.
    /// </summary>
    private OperationResult WithPlcSoftware(string projectPath, string actionName, IProgress<string> progress, CancellationToken cancellationToken, Func<TiaSession, dynamic, Assembly, OperationResult> work) =>
        WithSession(projectPath, actionName, progress, cancellationToken, (session, assembly) =>
        {
            dynamic? plcSoftware = PlcSoftwareLocator.FindFirstPlcSoftware(session.Project, assembly);
            if (plcSoftware is null)
            {
                return new OperationResult(Outcome.Error, $"{actionName} failed: no PLC software found in this project.", Array.Empty<string>());
            }

            return work(session, (object)plcSoftware, assembly);
        });

    /// <summary>Opens the project headless, runs <paramref name="work"/>, and always closes the session (without saving unless the work saved).</summary>
    private OperationResult WithSession(string projectPath, string actionName, IProgress<string> progress, CancellationToken cancellationToken, Func<TiaSession, Assembly, OperationResult> work)
    {
        TiaSession? session = null;
        try
        {
            session = _sessionService.Open(projectPath, progress);
            cancellationToken.ThrowIfCancellationRequested();
            var assembly = Assembly.LoadFrom(session.AssemblyPath);
            return work(session, assembly);
        }
        catch (TiaSessionOpenException ex)
        {
            return new OperationResult(Outcome.Error, $"{actionName} failed: the project couldn't be opened.", new[] { ex.Message });
        }
        catch (OperationCanceledException)
        {
            return Cancelled(actionName);
        }
        catch (Exception ex)
        {
            return new OperationResult(Outcome.Error, $"{actionName} failed.", new[] { ex.ToString() });
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

    private async Task RunOperationAsync(string actionName, string busyText, Func<IProgress<string>, CancellationToken, OperationResult> work, string? logDirectory, string? resultFolder, bool cancellable = true, string? openProjectAfter = null)
    {
        ClearLog();
        ShowRunView(actionName, logDirectory ?? resultFolder);
        _cancellation = new CancellationTokenSource();
        var cancellationToken = _cancellation.Token;
        SetBusy(true);
        CancelButton.IsEnabled = true;
        CancelButton.Visibility = cancellable ? Visibility.Visible : Visibility.Collapsed;
        ShowBadge(null);
        OpenResultFolderButton.Visibility = Visibility.Collapsed;
        _resultFolder = null;
        StatusBarText.Text = busyText;
        StatusBarText.SetResourceReference(TextBlock.ForegroundProperty, TextBrush);
        var logPath = logDirectory is null ? null : StartLogFile(logDirectory, actionName);
        AppendLog(busyText);
        if (logPath is not null)
        {
            AppendLog($"Logging to {logPath}");
        }

        _stopwatch.Restart();
        UpdateElapsed();
        _elapsedTimer.Start();

        // Progress<T> captures the UI thread here, so reports from the worker land on the UI thread.
        var progress = new Progress<string>(ReportProgress);
        OperationResult result;
        try
        {
            result = await Task.Run(() => work(progress, cancellationToken));
        }
        catch (OperationCanceledException)
        {
            result = Cancelled(actionName);
        }
        catch (Exception ex)
        {
            result = new OperationResult(Outcome.Error, $"{actionName} failed.", new[] { ex.ToString() });
        }

        _stopwatch.Stop();
        _elapsedTimer.Stop();
        _log.EndRun();
        UpdateElapsed();

        foreach (var message in result.Messages)
        {
            AppendLog(message);
        }

        _log.AppendResult(result.Outcome switch
        {
            Outcome.Success => RunResult.Success,
            Outcome.Warning => RunResult.Warnings,
            Outcome.Cancelled => RunResult.Cancelled,
            _ => RunResult.Failed
        }, $"{result.Summary} ({FormatElapsed(_stopwatch.Elapsed)})");

        _logFile?.Dispose();
        _logFile = null;

        StatusBarText.Text = result.Summary;
        ShowBadge(result.Outcome);
        if (resultFolder is not null && result.Outcome != Outcome.Error && Directory.Exists(resultFolder))
        {
            _resultFolder = resultFolder;
            OpenResultFolderButton.Visibility = Visibility.Visible;
        }

        if (openProjectAfter is not null && result.Outcome is Outcome.Success or Outcome.Warning)
        {
            OpenProjectInTia(openProjectAfter);
        }

        CancelButton.Visibility = Visibility.Collapsed;
        _cancellation.Dispose();
        _cancellation = null;
        SetBusy(false);
        UpdateOutputHint();
    }

    /// <summary>Opens the project in the TIA Portal GUI through the .ap* file association.</summary>
    private void OpenProjectInTia(string projectPath)
    {
        try
        {
            AppendLog($"Opening {Path.GetFileName(projectPath)} in TIA Portal...");
            Process.Start(new ProcessStartInfo(projectPath) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            AppendLog($"Couldn't open the project in TIA Portal: {ex.Message}");
        }
    }

    // ---- Setup view (pick what to do) and run view (watch it happen) ----

    private bool _hasRunOutput;

    private void ShowRunView(string actionName, string? folder)
    {
        var project = Path.GetFileNameWithoutExtension(ProjectPathTextBox.Text?.Trim() ?? string.Empty);
        RunTitleRun.Text = actionName;
        RunDetailRun.Text = "   " + string.Join("   ·   ", new[] { project, folder is null ? null : "→ " + folder }.Where(t => !string.IsNullOrEmpty(t)));
        _hasRunOutput = true;
        SetView(run: true);
    }

    private void SetView(bool run)
    {
        RunStrip.Visibility = run ? Visibility.Visible : Visibility.Collapsed;
        OutputCard.Visibility = run ? Visibility.Visible : Visibility.Collapsed;
        SettingsCard.Visibility = run ? Visibility.Collapsed : Visibility.Visible;
        ActionsGrid.Visibility = run ? Visibility.Collapsed : Visibility.Visible;
        ShowOutputButton.Visibility = !run && _hasRunOutput ? Visibility.Visible : Visibility.Collapsed;
    }

    private void BackToActions_Click(object sender, RoutedEventArgs e) => SetView(run: false);

    private void ShowOutput_Click(object sender, RoutedEventArgs e) => SetView(run: true);

    private static OperationResult Cancelled(string actionName) =>
        new(Outcome.Cancelled, $"{actionName} cancelled. The project was closed without saving.", Array.Empty<string>());

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (_cancellation is null || _cancellation.IsCancellationRequested)
        {
            return;
        }

        _cancellation.Cancel();
        CancelButton.IsEnabled = false;
        AppendLog("Cancelling: the run stops after the current step, and nothing is saved. Starting TIA Portal, "
                  + "opening the project, and compiling can't be interrupted, so this can take a while.", LogLevel.Warning);
        StatusBarText.Text = "Cancelling after the current step. Nothing will be saved.";
        StatusBarText.SetResourceReference(TextBlock.ForegroundProperty, WarningBrush);
    }

    private string? RequireProject(string verb)
    {
        var projectPath = ProjectPathTextBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(projectPath))
        {
            ShowNotice($"Select a project before {verb}.");
            return null;
        }

        if (!File.Exists(projectPath))
        {
            ShowNotice($"Project file not found: {projectPath}");
            return null;
        }

        return projectPath;
    }

    private bool Confirm(string title, string message) =>
        MessageBox.Show(this, message, title, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;

    private void Back_Click(object sender, RoutedEventArgs e) => LauncherWindow.ReturnFrom(this);

    private void SetBusy(bool busy)
    {
        _busy = busy;
        BackButton.IsEnabled = !busy;
        BackToActionsButton.IsEnabled = !busy;

        foreach (var control in new Control[]
                 {
                     ExportButton, ReimportButton, ImportFolderButton, ImportHmiButton, DiagnosticsButton,
                     ProjectPathTextBox, BrowseProjectButton, OutputDirectoryTextBox, BrowseOutputButton,
                     ExportTagsCheckBox, ExportCommentsCheckBox, CompileBeforeExportCheckBox, ExportHmiCheckBox,
                     OpenAfterImportCheckBox, SaveWithErrorsCheckBox
                 })
        {
            control.IsEnabled = !busy;
        }

        BusyProgressBar.IsIndeterminate = busy;
        BusyProgressBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        // AppStarting rather than Wait: the log stays usable (scroll, select, copy) while TIA works.
        Cursor = busy ? Cursors.AppStarting : null;
    }

    // ---- Status bar ----

    private void ShowNotice(string text)
    {
        ShowBadge(null);
        StatusBarText.Text = text;
        StatusBarText.SetResourceReference(TextBlock.ForegroundProperty, WarningBrush);
    }

    private void ShowBadge(Outcome? outcome)
    {
        if (outcome is null)
        {
            ResultBadge.Visibility = Visibility.Collapsed;
            return;
        }

        var (text, background, foreground) = outcome switch
        {
            Outcome.Success => ("DONE", "Badge.Done.Background", "Badge.Done.Foreground"),
            Outcome.Warning => ("WARNINGS", "Badge.Pre.Background", "Badge.Pre.Foreground"),
            Outcome.Cancelled => ("CANCELLED", "Badge.Neutral.Background", "Badge.Neutral.Foreground"),
            _ => ("FAILED", "Badge.Fail.Background", "Badge.Fail.Foreground")
        };

        ResultBadgeText.Text = text;
        ResultBadgeText.SetResourceReference(TextBlock.ForegroundProperty, foreground);
        ResultBadge.SetResourceReference(Border.BackgroundProperty, background);
        ResultBadge.Visibility = Visibility.Visible;
    }

    private void UpdateElapsed() => ElapsedText.Text = FormatElapsed(_stopwatch.Elapsed);

    private void ReportProgress(string step)
    {
        if (!_busy)
        {
            return;
        }

        AppendLog(step);
        // Warnings are logged but don't replace the current step in the status bar.
        if (!step.StartsWith("Warning", StringComparison.OrdinalIgnoreCase) && !_log.NeedsAttention)
        {
            // Keep "Cancelling..." visible until the run actually stops.
            if (_cancellation?.IsCancellationRequested != true)
            {
                StatusBarText.Text = step;
                StatusBarText.SetResourceReference(TextBlock.ForegroundProperty, TextBrush);
            }
        }
    }

    // While the Output pane pulses, the status bar says what TIA Portal may be waiting for.
    private void OnAttentionChanged(string? reason)
    {
        if (reason is not null)
        {
            StatusBarText.Text = reason;
            StatusBarText.SetResourceReference(TextBlock.ForegroundProperty, WarningBrush);
        }
    }

    private static string FormatElapsed(TimeSpan elapsed) =>
        elapsed.TotalHours >= 1
            ? $"{(int)elapsed.TotalHours}:{elapsed.Minutes:00}:{elapsed.Seconds:00}"
            : $"{elapsed.Minutes:00}:{elapsed.Seconds:00}";

    // ---- Log ----

    private void CopyLog_Click(object sender, RoutedEventArgs e)
    {
        if (_log.LineCount == 0)
        {
            return;
        }

        try
        {
            Clipboard.SetText(_log.Text);
            StatusBarText.Text = $"Copied {_log.LineCount} log line(s) to the clipboard.";
            StatusBarText.SetResourceReference(TextBlock.ForegroundProperty, TextBrush);
        }
        catch (Exception ex)
        {
            // The clipboard can be briefly locked by another app.
            ShowNotice("Couldn't copy to the clipboard: " + ex.Message);
        }
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e) => ClearLog();

    private void ClearLog() => _log.Clear();

    private void AppendLog(string text, LogLevel? level = null) => _log.Append(text, level);

    /// <summary>Opens &lt;folder&gt;\TIA_Portal_Tool_Logs\&lt;timestamp&gt;_&lt;action&gt;.log for this run; returns its path, or null if it couldn't be created.</summary>
    private string? StartLogFile(string outputDirectory, string actionName)
    {
        try
        {
            var logDirectory = Path.Combine(outputDirectory, FolderImportService.LogFolderName);
            Directory.CreateDirectory(logDirectory);
            var slug = actionName.ToLowerInvariant().Replace(' ', '-');
            var logPath = Path.Combine(logDirectory, $"{DateTime.Now:yyyy-MM-dd_HHmmss}_{slug}.log");
            _logFile = new StreamWriter(logPath, append: false) { AutoFlush = true };
            return logPath;
        }
        catch (Exception ex)
        {
            _logFile = null;
            AppendLog($"Warning: couldn't create the log file: {ex.Message}", LogLevel.Warning);
            return null;
        }
    }

    private void WriteLogFile(string line)
    {
        if (_logFile is null)
        {
            return;
        }

        try
        {
            _logFile.WriteLine(line);
        }
        catch (Exception)
        {
            // Losing the file (e.g. the drive went away) shouldn't break the on-screen log.
            _logFile.Dispose();
            _logFile = null;
        }
    }


    private void OpenFolder(string path)
    {
        try
        {
            Process.Start("explorer.exe", $"\"{path}\"");
        }
        catch (Exception ex)
        {
            ShowNotice($"Couldn't open {path}: {ex.Message}");
        }
    }

}
