using Microsoft.Win32;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using TiaPortalTool.Conversion.Slc;
using TiaPortalTool.Services;

namespace TiaPortalTool;

/// <summary>
/// Front end for the SLC 500 converter: pick an export, convert it, and import the result into a new or existing
/// TIA Portal project.
/// </summary>
public partial class SlcConverterWindow : Window
{
    private readonly SlcProjectImportService _importService = new();
    private readonly Stopwatch _stopwatch = new();
    private readonly DispatcherTimer _elapsedTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private CancellationTokenSource? _cancellation;
    private string? _reportPath;
    private string? _autoProjectName;
    private string? _newestTiaVersion;

    // The Output pane: colour-coded log that pulses when TIA Portal may be waiting for the user.
    private readonly OutputLog _log;

    public SlcConverterWindow()
    {
        InitializeComponent();
        WindowSizing.FitToScreen(this);
        VersionText.Text = AppVersions.Footer("SLC converter", AppVersions.SlcConverter);
        NewProjectFolderTextBox.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Automation");
        _log = new OutputLog(this, LogBox, OutputCard);
        _elapsedTimer.Tick += (_, _) =>
        {
            ElapsedText.Text = _stopwatch.Elapsed.ToString(@"mm\:ss");
            _log.CheckForStall();
        };
        Closing += Window_Closing;
        UpdateTargetFields();
        UpdateButtons();
    }

    private bool Busy => _cancellation is not null;

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (Busy && MessageBox.Show(this, "An import is still running. Closing now stops it part-way, and the project won't be saved.\n\nClose anyway?",
                "Import in progress", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            e.Cancel = true;
        }
    }

    // ---- Inputs ----

    private void BrowseSlc_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "RSLogix 500 ASCII export (*.slc)|*.slc|All files (*.*)|*.*", Title = "Select the RSLogix 500 export" };
        if (dialog.ShowDialog(this) == true)
        {
            SlcPathTextBox.Text = dialog.FileName;
        }
    }

    private void BrowseOutput_Click(object sender, RoutedEventArgs e) => BrowseFolder(OutputPathTextBox, "Select the output folder");

    private void BrowseNewFolder_Click(object sender, RoutedEventArgs e) => BrowseFolder(NewProjectFolderTextBox, "Select where to create the TIA Portal project");

    private void BrowseExisting_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "TIA Portal project files (*.ap*)|*.ap*|All files (*.*)|*.*", Title = "Select the TIA Portal project" };
        if (dialog.ShowDialog(this) == true)
        {
            ExistingProjectTextBox.Text = dialog.FileName;
        }
    }

    private static void BrowseFolder(TextBox target, string description)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog { Description = description };
        if (Directory.Exists(target.Text))
        {
            dialog.SelectedPath = target.Text;
        }

        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            target.Text = dialog.SelectedPath;
        }
    }

    private void SlcPathTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var path = SlcPathTextBox.Text.Trim();
        if (!File.Exists(path))
        {
            return;
        }

        // The symbol file is the one Save As wrote next to the .SLC; just say which one will be used.
        var found = SlcSymbolTable.FindBesideExport(path);

        // The export's own name, minus anything Windows won't take in a folder name.
        var name = string.Concat(Path.GetFileNameWithoutExtension(path).Where(c => Array.IndexOf(Path.GetInvalidFileNameChars(), c) < 0)).Trim();
        if (NewProjectNameTextBox.Text.Length == 0 || NewProjectNameTextBox.Text == _autoProjectName)
        {
            NewProjectNameTextBox.Text = name;
            _autoProjectName = name;
        }

        SlcHintText.Text = found is null
            ? "No .SY6 symbol file with the same name next to the .SLC, so there are no symbols or descriptions; data is named after the SLC addresses."
            : $"Symbols and descriptions: {Path.GetFileName(found)}";
    }

    private void TargetRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (IsInitialized)
        {
            UpdateTargetFields();
        }
    }

    private void UpdateTargetFields()
    {
        var isNew = NewProjectRadio.IsChecked == true;
        foreach (UIElement element in new UIElement[] { NewFolderLabel, NewProjectPanel, BrowseNewFolderButton })
        {
            element.Visibility = isNew ? Visibility.Visible : Visibility.Collapsed;
        }

        foreach (UIElement element in new UIElement[] { ExistingLabel, ExistingProjectTextBox, BrowseExistingButton })
        {
            element.Visibility = isNew ? Visibility.Collapsed : Visibility.Visible;
        }

        // Scanning the install folders takes a moment, so do it once.
        _newestTiaVersion ??= OpennessProjectProbe.GetNewestInstalledVersion()?.ToString() ?? "??";
        var version = _newestTiaVersion;
        TargetHintText.Text = isNew
            ? $"Creates <folder>\\<name>\\<name>.ap{version} with TIA Portal V{version} and a basic CPU of the chosen family. "
              + "Swap in the real CPU later with Change device."
            : "Blocks and data types with the same name are overwritten; everything else in the project is left alone. "
              + "The project is saved only if the compile is clean. Close it in the TIA Portal GUI first.";
    }

    private string OutputDirectory(string slcPath)
    {
        var text = OutputPathTextBox.Text.Trim();
        return text.Length > 0
            ? text
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "SLCConvert", Path.GetFileNameWithoutExtension(slcPath));
    }

    /// <summary>Reads the form into conversion options, or logs why it can't and returns null.</summary>
    private SlcConversionOptions? ReadOptions()
    {
        var slcPath = SlcPathTextBox.Text.Trim();
        if (!File.Exists(slcPath))
        {
            Log("Select an .SLC export first.");
            return null;
        }

        return new SlcConversionOptions
        {
            SlcPath = slcPath,
            SymbolsPath = null,   // found next to the .SLC
            OutputDirectory = OutputDirectory(slcPath),
            Cpu = Cpu1200Radio.IsChecked == true ? CpuFamily.S71200 : CpuFamily.S71500
        };
    }

    private SlcImportTarget? ReadTarget()
    {
        var target = new SlcImportTarget { S71200 = Cpu1200Radio.IsChecked == true };
        if (NewProjectRadio.IsChecked == true)
        {
            target.NewProjectDirectory = NewProjectFolderTextBox.Text.Trim();
            target.NewProjectName = NewProjectNameTextBox.Text.Trim();
            if (target.NewProjectDirectory.Length == 0 || target.NewProjectName.Length == 0)
            {
                Log("Enter a folder and a name for the new project.");
                return null;
            }

            if (target.NewProjectName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                Log("The project name can't contain \\ / : * ? \" < > |");
                return null;
            }
        }
        else
        {
            target.ExistingProjectPath = ExistingProjectTextBox.Text.Trim();
            if (!File.Exists(target.ExistingProjectPath) || OpennessProjectProbe.TryParseTargetVersion(target.ExistingProjectPath) is null)
            {
                Log("Select an existing TIA Portal project (*.apNN).");
                return null;
            }
        }

        return target;
    }

    // ---- Actions ----

    private async void Convert_Click(object sender, RoutedEventArgs e)
    {
        var options = ReadOptions();
        if (options is null)
        {
            return;
        }

        BeginRun(cancellable: false);

        // Created here so progress reports come back on the UI thread.
        var progress = new Progress<string>(Log);
        try
        {
            var result = await Task.Run(() => SlcConverter.Convert(options, progress));
            LogConversion(result);
            Log(string.Empty);
            Log("Converted only. Use Convert and Import (or Import Folder in the Import / Export tool) to bring it into TIA Portal.");
            _log.AppendResult(RunResult.Success, $"converted in {Elapsed()}; opening the report.");
            OpenReport(result.ReportPath);
        }
        catch (Exception ex)
        {
            Log("Conversion failed: " + ex);
            _log.AppendResult(RunResult.Failed, "the conversion stopped with an error (see above).");
        }
        finally
        {
            EndRun();
        }
    }

    private async void ConvertAndImport_Click(object sender, RoutedEventArgs e)
    {
        var options = ReadOptions();
        var target = options is null ? null : ReadTarget();
        if (options is null || target is null)
        {
            return;
        }

        var version = SlcProjectImportService.TargetVersion(target);
        if (version is null)
        {
            Log("No TIA Portal Openness installation was found.");
            return;
        }

        if (!target.IsNewProject && MessageBox.Show(this,
                $"Add the converted blocks to\n{target.ExistingProjectPath}?\n\nBlocks and data types with the same names are overwritten. "
                + "The project is saved only if the compile is clean. Archive it first if you want a backup.",
                "Confirm import", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            return;
        }

        // The SimaticML has to say the same TIA Portal version as the project it goes into.
        options.EngineeringVersion = "V" + version;
        var token = BeginRun(cancellable: true);
        var progress = new Progress<string>(Log);
        string? reportPath = null;
        try
        {
            if (version != 20)
            {
                Log($"Note: the converter has only been tested with TIA Portal V20; this import uses V{version}.");
            }

            var conversion = await Task.Run(() => SlcConverter.Convert(options, progress));
            LogConversion(conversion);
            reportPath = conversion.ReportPath;
            Log(string.Empty);

            var import = await Task.Run(() => _importService.Import(target, options.OutputDirectory, progress, token));
            foreach (var message in import.Messages)
            {
                Log(message);
            }

            Log(string.Empty);
            Log(import.Saved
                ? $"Done{(import.Success ? string.Empty : " with errors")}: {import.ProjectPath}"
                : "The project was not saved.");

            // Check the result: export the converted blocks back out of TIA and compare every rung with the SLC.
            var problems = -1;
            if (import.Saved && !token.IsCancellationRequested)
            {
                Log(string.Empty);
                Log("Checking the converted logic against the SLC program...");
                var symbols = options.SymbolsPath;
                var verification = await Task.Run(() => new SlcVerificationService().Verify(options.SlcPath, symbols, import.ProjectPath, options.OutputDirectory, progress));
                foreach (var message in verification.Messages)
                {
                    Log("  " + message);
                }

                foreach (var check in verification.Checks.Where(c => c.Status is "Mismatch" or "Missing"))
                {
                    Log($"  {check.Status}: {check.Block} rung {check.Rung}: {check.Detail}");
                }

                problems = verification.Checks.Count(c => c.Status is "Mismatch" or "Missing");
            }

            if (token.IsCancellationRequested)
            {
                _log.AppendResult(RunResult.Cancelled, $"stopped after {Elapsed()}; the project was closed without saving.");
            }
            else if (!import.Saved)
            {
                _log.AppendResult(RunResult.Failed, $"the project was not saved ({Elapsed()}). See the errors above.");
            }
            else if (!import.Success)
            {
                _log.AppendResult(RunResult.Problems, $"imported and saved with errors in {Elapsed()}; see above.");
            }
            else if (problems > 0)
            {
                _log.AppendResult(RunResult.Problems, $"imported and compiled in {Elapsed()}, but {problems} rung(s) don't match the SLC (SLC_Verification.xlsx).");
            }
            else
            {
                _log.AppendResult(RunResult.Success, $"converted, imported, compiled and verified in {Elapsed()}; opening the report.");
            }

            SaveLog(options.OutputDirectory);
            if (!token.IsCancellationRequested)
            {
                OpenReport(reportPath);
            }
        }
        catch (Exception ex)
        {
            Log("Failed: " + ex);
            _log.AppendResult(token.IsCancellationRequested ? RunResult.Cancelled : RunResult.Failed,
                token.IsCancellationRequested ? "stopped; the project was closed without saving." : "the import stopped with an error (see above).");
            SaveLog(options.OutputDirectory);
        }
        finally
        {
            EndRun();
        }
    }

    private void Back_Click(object sender, RoutedEventArgs e) => LauncherWindow.ReturnFrom(this);

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (_cancellation is { IsCancellationRequested: false })
        {
            _cancellation.Cancel();
            CancelButton.IsEnabled = false;
            Log("Cancelling after the current step. Starting TIA Portal, opening the project and compiling can't be interrupted.");
        }
    }

    private void LogConversion(SlcConversionResult result)
    {
        foreach (var message in result.Messages)
        {
            Log(message);
        }

        Log(result.Warnings.Count == 0
            ? "Nothing needs attention."
            : $"{result.Warnings.Count} item(s) need attention (also on the report's Manual work sheet):");
        foreach (var warning in result.Warnings)
        {
            Log("  - " + warning, LogLevel.Warning);
        }

        _reportPath = result.ReportPath;
    }

    private CancellationToken BeginRun(bool cancellable)
    {
        _log.Clear();
        _cancellation = new CancellationTokenSource();
        ConvertButton.IsEnabled = false;
        ImportButton.IsEnabled = false;
        BackButton.IsEnabled = false;
        CancelButton.IsEnabled = true;
        CancelButton.Visibility = cancellable ? Visibility.Visible : Visibility.Collapsed;
        Cursor = Cursors.AppStarting;
        _stopwatch.Restart();
        _elapsedTimer.Start();
        return _cancellation.Token;
    }

    private void EndRun()
    {
        _stopwatch.Stop();
        _elapsedTimer.Stop();
        _log.EndRun();
        ElapsedText.Text = _stopwatch.Elapsed.ToString(@"mm\:ss");
        _cancellation?.Dispose();
        _cancellation = null;
        ConvertButton.IsEnabled = true;
        ImportButton.IsEnabled = true;
        BackButton.IsEnabled = true;
        CancelButton.Visibility = Visibility.Collapsed;
        Cursor = null;
        UpdateButtons();
    }

    private void OpenReport_Click(object sender, RoutedEventArgs e)
    {
        if (_reportPath is not null && File.Exists(_reportPath))
        {
            Start(_reportPath);
        }
    }

    private void OpenOutput_Click(object sender, RoutedEventArgs e)
    {
        var directory = OutputDirectory(SlcPathTextBox.Text.Trim());
        if (Directory.Exists(directory))
        {
            Start(directory);
        }
    }

    private void CopyLog_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(_log.Text);
        }
        catch (Exception ex)
        {
            // The clipboard can be briefly locked by another app.
            Log("Couldn't copy to the clipboard: " + ex.Message);
        }
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e) => _log.Clear();

    private void UpdateButtons() => OpenReportButton.IsEnabled = _reportPath is not null && File.Exists(_reportPath);

    private void Log(string text) => _log.Append(text);

    private void Log(string text, LogLevel level) => _log.Append(text, level);

    /// <summary>Keeps a copy of the import log in &lt;output&gt;\logs, since TIA runs take minutes and the window may be closed.</summary>
    private void SaveLog(string outputDirectory)
    {
        try
        {
            var directory = Path.Combine(outputDirectory, "logs");
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, $"{DateTime.Now:yyyy-MM-dd_HHmmss}_slc-import.log"), _log.Text);
        }
        catch (Exception)
        {
            // The on-screen log is still there.
        }
    }

    private string Elapsed() => _stopwatch.Elapsed.ToString(@"mm\:ss");

    // Both actions end by opening the conversion report (its cover says what to do next).
    private void OpenReport(string? path)
    {
        if (path is not null && File.Exists(path))
        {
            Start(path);
        }
    }

    private void Start(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log($"Couldn't open {path}: {ex.Message}");
        }
    }
}
