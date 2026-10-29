using System.IO;
using TiaPortalTool.Conversion.Slc;
using TiaPortalTool.Services;

namespace TiaPortalTool.Testing;

/// <summary>
/// Command-line entry points for automated testing against TIA Portal, run through tools/OpennessRunner.
/// The runner's exe never changes, so TIA's Openness firewall only has to approve it once, while these commands
/// come from whatever TiaPortalTool.exe was just built.
/// </summary>
public static class HeadlessCommands
{
    private const string Usage =
        "slc-import --slc <export.SLC> --out <folder> (--new <folder> <name> [--replace] | --existing <project.apNN>) [--1200] [--verify]";

    /// <summary>Called by the runner by reflection. Returns the process exit code.</summary>
    public static int Run(string[] args)
    {
        if (args.Length >= 4 && args[0] == "scl-export")
        {
            return SclExport(args[1], args[2], args[3]);
        }

        if (args.Length >= 3 && args[0] == "verify-folder")
        {
            // Runs the rung comparison on an already exported folder (no TIA Portal needed).
            var program = SlcExportParser.Parse(args[1]);
            var symbols = SlcSymbolTable.FindBesideExport(args[1]);
            if (symbols is not null)
            {
                program.Symbols = SlcSymbolTable.Load(symbols);
            }

            var analysis = SlcProgramAnalysis.Analyze(program);
            var verifier = new SlcVerifier(analysis, SlcDataBlockBuilder.Build(analysis), SlcProgramBuilder.BlockNames(program));
            var checks = verifier.Verify(args[2]);
            foreach (var group in checks.GroupBy(c => c.Status))
            {
                Console.WriteLine($"{group.Key}: {group.Count()}");
            }

            foreach (var check in checks.Where(c => c.Status is "Mismatch" or "Missing" or "Unchecked"))
            {
                Console.WriteLine($"  {check.Status} {check.Block} rung {check.Rung}: {check.Detail}");
            }

            return 0;
        }

        if (args.Length >= 3 && args[0] == "export-blocks")
        {
            // Exports every block of a project as SimaticML, to check what actually landed in TIA Portal.
            using var session = new TiaSessionService().Open(args[1], new ConsoleProgress());
            var assembly = System.Reflection.Assembly.LoadFrom(session.AssemblyPath);
            dynamic plcSoftware = PlcSoftwareLocator.FindFirstPlcSoftware(session.Project, assembly)!;
            var result = new BlockExportService().ExportAll((object)plcSoftware, assembly, args[2]);
            Console.WriteLine($"Exported {result.Entries.Count} block(s) to {args[2]}; {result.Warnings.Count} warning(s).");
            foreach (var warning in result.Warnings)
            {
                Console.WriteLine("  " + warning);
            }

            return 0;
        }

        if (args.Length >= 3 && args[0] == "hmi-export")
        {
            // Same HMI export as the Import / Export window's HMI option, into <export folder>\HMI\<device>.
            using var session = new TiaSessionService().Open(args[1], new ConsoleProgress());
            var assembly = System.Reflection.Assembly.LoadFrom(session.AssemblyPath);
            var result = new Services.Hmi.HmiExportService().ExportAll(session.Project, assembly, args[2], new ConsoleProgress(), CancellationToken.None);
            foreach (var message in result.Messages)
            {
                Console.WriteLine(message);
            }

            Console.WriteLine($"{result.Warnings.Count} warning(s).");
            foreach (var warning in result.Warnings)
            {
                Console.WriteLine("  " + warning);
            }

            return result.ExportedCount > 0 ? 0 : 1;
        }

        if (args.Length >= 3 && args[0] == "hmi-import")
        {
            return HmiImport(args[1], args[2], args.Skip(3).ToArray());
        }

        if (args.Length >= 3 && args[0] == "hmi-item-diff")
        {
            // What the HMI import reports when TIA stores an object differently from its file (no TIA Portal needed).
            var changes = Services.Hmi.HmiItemDiff.Compare(Path.GetFileNameWithoutExtension(args[1]), args[1], args[2], Services.Hmi.HmiXmlHash.CurrentVersion);
            changes.ForEach(c => Console.WriteLine((c.IsLayout ? "LAYOUT " : "       ") + c));
            Console.WriteLine($"{changes.Count} change(s), {changes.Count(c => c.IsLayout)} position/size.");
            return 0;
        }

        if (args.Length >= 2 && args[0] == "hmi-runtime-names")
        {
            return HmiRuntimeNamesCommand(args[1], args.Skip(2).Contains("--compile"));
        }

        if (args.Length >= 3 && args[0] == "library-probe")
        {
            return LibraryProbe(args[1], args[2], args.Length > 3 ? args[3] : string.Empty);
        }

        if (args.Length >= 3 && args[0] == "export-projecttexts")
        {
            var progress = new ConsoleProgress();
            using var session = new TiaSessionService().Open(args[1], progress);
            var messages = new List<string>();
            new ProjectTextService().Export(session.Project, Path.GetFullPath(args[2]), args.Length > 3 ? args[3] : "en-US",
                args.Length > 4 ? args[4] : "de-DE", messages);
            messages.ForEach(Console.WriteLine);
            return 0;
        }

        // A fresh test project with one CPU and an empty program, e.g. for import-folder tests.
        if (args.Length >= 3 && args[0] == "new-project")
        {
            var progress = new ConsoleProgress();
            using var session = new TiaSessionService().Create(Path.GetFullPath(args[1]), args[2], progress);
            PlcDeviceCreator.CreatePlc(session.Project, args.Skip(3).Contains("--1200"), progress);
            session.Save();
            Console.WriteLine("Created and saved.");
            return 0;
        }

        if (args.Length >= 3 && args[0] == "import-folder")
        {
            var flags = args.Skip(3).ToList();
            return ImportFolder(args[1], args[2], flags.Contains("--no-save"), flags.Contains("--force"), flags.Contains("--save-with-errors"),
                flags.Contains("--force-lost-objects"), flags.Contains("--accept-layout-changes"));
        }

        if (args.Length == 0 || args[0] != "slc-import")
        {
            Console.Error.WriteLine("Usage: " + Usage + "\n   or: scl-export <project.apNN> <source.scl> <export folder>"
                                    + "\n   or: export-blocks <project.apNN> <export folder>\n   or: hmi-export <project.apNN> <export folder>"
                                    + "\n   or: " + HmiImportUsage
                                    + "\n   or: hmi-runtime-names <pdata.fwc | RtData.plf | project.apNN> [--compile]"
                                    + "\n   or: hmi-item-diff <imported file.xml> <same object exported from TIA.xml>"
                                    + "\n   or: import-folder <project.apNN> <folder> [--force] [--force-lost-objects] [--accept-layout-changes] [--save-with-errors] [--no-save]"
                                    + "\n   or: new-project <dir> <name> [--1200]"
                                    + "\n   or: export-projecttexts <project.apNN> <out.projecttexts.xlsx> [source en-US] [target de-DE]");
            return 2;
        }

        return SlcImport(args.Skip(1).ToArray());
    }

    private const string HmiImportUsage = "hmi-import <project.apNN> <HMI export folder> [--force] [--force-lost-objects] [--accept-layout-changes] [--no-compile] [--no-save]";

    /// <summary>Same HMI import as the Import / Export window's Import HMI button; saves only when the import and compile are clean.</summary>
    private static int HmiImport(string projectPath, string folder, string[] flags)
    {
        var options = new Services.Hmi.HmiImportOptions();
        var save = true;
        foreach (var flag in flags)
        {
            switch (flag)
            {
                case "--force": options.Force = true; break;
                case "--force-lost-objects": options.ForceLostObjects = true; break;
                case "--accept-layout-changes": options.AcceptLayoutChanges = true; break;
                case "--no-compile": options.Compile = false; break;
                case "--no-save": save = false; break;
                default:
                    Console.Error.WriteLine($"Unknown argument {flag}. Usage: {HmiImportUsage}");
                    return 2;
            }
        }

        var progress = new ConsoleProgress();
        using var session = new TiaSessionService().Open(projectPath, progress);
        var assembly = System.Reflection.Assembly.LoadFrom(session.AssemblyPath);
        var result = new Services.Hmi.HmiImportService().ImportAll(session.Project, assembly, folder, options, progress, CancellationToken.None);
        foreach (var message in result.Messages)
        {
            Console.WriteLine(message);
        }

        Console.WriteLine($"{result.Warnings.Count} warning(s).");
        foreach (var warning in result.Warnings)
        {
            Console.WriteLine("  " + warning);
        }

        if (save && result.CanSave)
        {
            session.Save();
            Services.Hmi.HmiImportService.WriteManifests(result);
            Console.WriteLine("Project saved.");
        }
        else
        {
            Console.WriteLine(result.ImportedCount == 0 && result.FailedCount == 0 ? "Nothing imported, so nothing to save."
                : result.SaveRefused ? "Project NOT saved (lost objects or layout changes; see the warnings)." : "Project NOT saved.");
        }

        return result.FailedCount == 0 && !result.CompileFailed && !result.SaveRefused ? 0 : 1;
    }

    /// <summary>
    /// Prints the names (with counts) in a classic HMI's compiled runtime: the download (pdata.fwc, what the HMI
    /// import's lost-object guard reads) and the compiler database (RtData.plf, what the export check reads). Given a
    /// project, finds each classic HMI's files (compiling first with --compile); the project is never saved. Given a
    /// .fwc or .plf file, just reads it (no TIA Portal needed).
    /// </summary>
    private static int HmiRuntimeNamesCommand(string path, bool compile)
    {
        void Print(Services.Hmi.HmiRuntimeNames names)
        {
            Console.WriteLine($"{names.Path} (written {names.LastWriteUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}): {names.Counts.Count} name(s)");
            foreach (var pair in names.Counts.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                Console.WriteLine($"  {pair.Value}\t{pair.Key}");
            }
        }

        if (path.EndsWith(".plf", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".fwc", StringComparison.OrdinalIgnoreCase))
        {
            Print(Services.Hmi.HmiRuntimeNames.Read(path));
            return 0;
        }

        var progress = new ConsoleProgress();
        using var session = new TiaSessionService().Open(path, progress);
        var assembly = System.Reflection.Assembly.LoadFrom(session.AssemblyPath);
        var exitCode = 0;
        IReadOnlyList<Services.Hmi.HmiDevice> hmis = Services.Hmi.HmiSoftwareLocator.FindAll(session.Project, assembly);
        foreach (var hmi in hmis.Where(h => h.Kind == Services.Hmi.HmiKind.ClassicWinCC))
        {
            var messages = new List<string>();
            if (compile)
            {
                PlcCompiler.Compile(hmi.Software, assembly, messages, progress, $"HMI {hmi.RuntimeName}");
            }

            Console.WriteLine($"HMI {hmi.Describe()}:");
            foreach (var source in new[] { Services.Hmi.HmiRuntimeSource.Download, Services.Hmi.HmiRuntimeSource.Database })
            {
                string? file = Services.Hmi.HmiRuntimeGuard.FindRuntimeFile(session.Project, hmi, source);
                if (file is null)
                {
                    Console.WriteLine($"  no compiled {Services.Hmi.HmiRuntimeNames.RelativePath(source)} found.");
                    exitCode = 1;
                    continue;
                }

                Print(Services.Hmi.HmiRuntimeNames.Read(file));
            }
        }

        return exitCode;
    }

    /// <summary>
    /// Lists the project library types (folder path, type class, versions, states, export formats) and exports the
    /// default/latest version of each type whose path contains <paramref name="filter"/> as SimaticML into
    /// <paramref name="outFolder"/>. Read-only: the project is never saved.
    /// </summary>
    private static int LibraryProbe(string projectPath, string outFolder, string filter)
    {
        var progress = new ConsoleProgress();
        Directory.CreateDirectory(outFolder);
        using var session = new TiaSessionService().Open(projectPath, progress);
        var assembly = System.Reflection.Assembly.LoadFrom(session.AssemblyPath);
        dynamic library = session.Project.ProjectLibrary;
        var exportOptions = Enum.Parse(assembly.GetType("Siemens.Engineering.ExportOptions")!, "WithDefaults");

        void Walk(dynamic folder, string path)
        {
            foreach (dynamic type in folder.Types)
            {
                string typePath = path + "/" + (string)type.Name;
                string formats;
                try { formats = string.Join(",", (IEnumerable<string>)type.GetSupportedExportFormats()); }
                catch (Exception ex) { formats = "n/a (" + ex.GetType().Name + ")"; }
                Console.WriteLine($"{typePath} [{((object)type).GetType().Name}] formats: {formats}");
                dynamic? last = null;
                foreach (dynamic version in type.Versions)
                {
                    Console.WriteLine($"    v{version.VersionNumber} {version.State}{(version.IsDefault ? " default" : string.Empty)}");
                    last = version;
                }

                if (last is null || filter.Length == 0 || typePath.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                var file = Path.Combine(outFolder, System.Text.RegularExpressions.Regex.Replace(typePath.Trim('/'), @"[\\/:*?""<>|]", "_") + ".xml");
                try
                {
                    last.Export(new FileInfo(file), (dynamic)exportOptions);
                    Console.WriteLine($"    exported -> {file}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"    export failed: {ex.Message.Split('\n')[0]}");
                }
            }

            foreach (dynamic sub in folder.Folders)
            {
                Walk(sub, path + "/" + (string)sub.Name);
            }
        }

        Walk(library.TypeFolder, string.Empty);
        return 0;
    }

    /// <summary>
    /// Same as the Import / Export window's Import Folder button: PLC content, then any HMI export. HMI objects also
    /// changed in TIA since the export are kept and listed (there's no one to ask), unless <paramref name="force"/>.
    /// <paramref name="saveWithErrors"/> = the window's "Save even if the compile has errors" box: when every file
    /// imported, compile errors don't stop the save (exit code 3 then, so a script can tell).
    /// <paramref name="forceLostObjects"/> / <paramref name="acceptLayoutChanges"/> = the answer "Yes" to the window's
    /// lost-object and layout-change prompts (see <see cref="Services.Hmi.HmiRuntimeGuard"/>).
    /// </summary>
    private static int ImportFolder(string projectPath, string folder, bool noSave, bool force, bool saveWithErrors, bool forceLostObjects, bool acceptLayoutChanges)
    {
        var progress = new ConsoleProgress();
        using var session = new TiaSessionService().Open(projectPath, progress);
        var assembly = System.Reflection.Assembly.LoadFrom(session.AssemblyPath);
        dynamic plcSoftware = PlcSoftwareLocator.FindFirstPlcSoftware(session.Project, assembly)!;
        FolderImportResult plc = new FolderImportService().ImportAndCompile((object)plcSoftware, assembly, folder, progress);
        foreach (var message in plc.Messages)
        {
            Console.WriteLine(message);
        }

        var compileErrors = 0;
        if (!plc.Success)
        {
            if (!saveWithErrors || !plc.OnlyCompileFailed)
            {
                Console.WriteLine("PLC import/compile failed. Project NOT saved.");
                return 1;
            }

            compileErrors += ReportCompileErrors(plc.Compile!, plc.TouchedBlocks);
        }

        Services.Hmi.HmiImportResult? hmi = null;
        if (Services.Hmi.HmiImportService.FindHmiFolders(folder).Count > 0)
        {
            hmi = new Services.Hmi.HmiImportService().ImportAll(session.Project, assembly, folder,
                new Services.Hmi.HmiImportOptions { Force = force, ForceLostObjects = forceLostObjects, AcceptLayoutChanges = acceptLayoutChanges }, progress, CancellationToken.None);
            foreach (var message in hmi.Messages.Concat(hmi.Warnings.Select(w => "  " + w)))
            {
                Console.WriteLine(message);
            }

            if (hmi.FailedCount > 0 || hmi.SaveRefused || (hmi.CompileFailed && !saveWithErrors))
            {
                Console.WriteLine(hmi.SaveRefused ? "HMI import: lost objects or layout changes (see the warnings). Project NOT saved." : "HMI import/compile failed. Project NOT saved.");
                return 1;
            }

            compileErrors += hmi.FailedCompiles.Sum(c => ReportCompileErrors(c, Array.Empty<string>()));
        }

        foreach (var textFile in ProjectTextService.FindFiles(folder))
        {
            var textMessages = new List<string>();
            var ok = new ProjectTextService().Import(session.Project, textFile, textMessages);
            textMessages.ForEach(Console.WriteLine);
            if (!ok)
            {
                Console.WriteLine("Project text import failed. Project NOT saved.");
                return 1;
            }
        }

        if (noSave)
        {
            Console.WriteLine("--no-save: project NOT saved.");
            return compileErrors > 0 ? 3 : 0;
        }

        session.Save();
        if (hmi is not null)
        {
            Services.Hmi.HmiImportService.WriteManifests(hmi);
        }

        if (compileErrors > 0)
        {
            Console.WriteLine($"Project saved with {compileErrors} compile error(s) (--save-with-errors).");
            return 3;
        }

        Console.WriteLine("Project saved.");
        return 0;
    }

    /// <summary>Lists a failed compile's errors, split by whether the import brought the block in; returns the count (at least 1).</summary>
    private static int ReportCompileErrors(CompileOutcome compile, IReadOnlyCollection<string> touchedBlocks)
    {
        var inImported = compile.Errors.Where(e => touchedBlocks.Contains(e.BlockName, StringComparer.OrdinalIgnoreCase)).ToList();
        var inOthers = compile.Errors.Except(inImported).ToList();
        Console.WriteLine($"{compile.What} compile errors, saving anyway (--save-with-errors):");
        if (inImported.Count > 0)
        {
            Console.WriteLine($"  In blocks this import brought in ({inImported.Count}):");
            inImported.ForEach(e => Console.WriteLine("    " + e));
        }

        if (inOthers.Count > 0)
        {
            Console.WriteLine($"  In other blocks ({inOthers.Count}):");
            inOthers.ForEach(e => Console.WriteLine("    " + e));
        }

        return Math.Max(1, compile.Errors.Count);
    }

    /// <summary>
    /// Generates the blocks in an SCL source inside an existing project and exports them as SimaticML. Used to find out
    /// how TIA Portal itself writes a construct, by writing it in SCL and reading the export.
    /// </summary>
    private static int SclExport(string projectPath, string sourcePath, string exportFolder)
    {
        var progress = new ConsoleProgress();
        Directory.CreateDirectory(exportFolder);
        var importFolder = Path.Combine(Path.GetTempPath(), "scl-export-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(importFolder);
        File.Copy(sourcePath, Path.Combine(importFolder, Path.GetFileName(sourcePath)));

        using var session = new TiaSessionService().Open(projectPath, progress);
        var assembly = System.Reflection.Assembly.LoadFrom(session.AssemblyPath);
        dynamic plcSoftware = PlcSoftwareLocator.FindFirstPlcSoftware(session.Project, assembly)!;
        FolderImportResult result = new FolderImportService().ImportAndCompile((object)plcSoftware, assembly, importFolder, progress);
        foreach (var message in result.Messages)
        {
            progress.Report(message);
        }

        var exportOptions = Enum.Parse(assembly.GetType("Siemens.Engineering.ExportOptions")!, "WithDefaults");
        var names = System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(sourcePath), "FUNCTION(?:_BLOCK)?\\s+\"([^\"]+)\"")
            .Cast<System.Text.RegularExpressions.Match>().Select(m => m.Groups[1].Value);
        foreach (var name in names)
        {
            dynamic? block = BlockReimportService.FindBlockByName(plcSoftware.BlockGroup.Blocks, name);
            if (block is null)
            {
                progress.Report($"{name} not found after generating");
                continue;
            }

            var file = Path.Combine(exportFolder, name + ".xml");
            File.Delete(file);
            block.Export(new FileInfo(file), (dynamic)exportOptions);
            progress.Report($"Exported {file}");
        }

        return result.Success ? 0 : 1;
    }

    private static int SlcImport(string[] args)
    {
        string? slc = null, output = null, newDirectory = null, newName = null, existing = null;
        bool s71200 = false, replace = false, verify = false;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--slc": slc = args[++i]; break;
                case "--out": output = args[++i]; break;
                case "--new": newDirectory = args[++i]; newName = args[++i]; break;
                case "--existing": existing = args[++i]; break;
                case "--1200": s71200 = true; break;
                case "--replace": replace = true; break;
                case "--verify": verify = true; break;
                default:
                    Console.Error.WriteLine($"Unknown argument {args[i]}. Usage: {Usage}");
                    return 2;
            }
        }

        if (slc is null || output is null || (existing is null && (newDirectory is null || newName is null)))
        {
            Console.Error.WriteLine("Usage: " + Usage);
            return 2;
        }

        var target = new SlcImportTarget
        {
            ExistingProjectPath = existing,
            NewProjectDirectory = newDirectory ?? string.Empty,
            NewProjectName = newName ?? string.Empty,
            S71200 = s71200
        };

        // Test projects are throwaway: --replace removes the previous run's project folder.
        if (target.IsNewProject && replace)
        {
            var folder = Path.Combine(target.NewProjectDirectory, target.NewProjectName);
            if (Directory.Exists(folder))
            {
                Console.WriteLine($"Removing previous test project {folder}");
                Directory.Delete(folder, recursive: true);
            }
        }

        var version = SlcProjectImportService.TargetVersion(target);
        if (version is null)
        {
            Console.Error.WriteLine("No TIA Portal Openness installation found.");
            return 1;
        }

        var progress = new ConsoleProgress();
        var conversion = SlcConverter.Convert(new SlcConversionOptions
        {
            SlcPath = slc,
            OutputDirectory = output,
            Cpu = s71200 ? CpuFamily.S71200 : CpuFamily.S71500,
            EngineeringVersion = "V" + version
        }, progress);
        foreach (var message in conversion.Messages.Concat(conversion.Warnings.Select(w => "Attention: " + w)))
        {
            progress.Report(message);
        }

        var result = new SlcProjectImportService().Import(target, output, progress, CancellationToken.None);
        foreach (var message in result.Messages)
        {
            progress.Report(message);
        }

        progress.Report($"RESULT: {(result.Success ? "SUCCESS" : "FAILED")}, saved={result.Saved}, project={result.ProjectPath}");
        if (verify && result.Saved)
        {
            var verification = new SlcVerificationService().Verify(slc, null, result.ProjectPath, output, progress);
            foreach (var message in verification.Messages)
            {
                progress.Report("VERIFY " + message);
            }

            foreach (var check in verification.Checks.Where(c => c.Status is "Mismatch" or "Missing" or "Unchecked"))
            {
                progress.Report($"VERIFY {check.Status} {check.Block} rung {check.Rung}: {check.Detail}");
            }
        }

        return result.Success ? 0 : 1;
    }

    /// <summary>Writes progress straight to the console, in order (Progress&lt;T&gt; would post to the thread pool).</summary>
    private sealed class ConsoleProgress : IProgress<string>
    {
        public void Report(string value) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {value}");
    }
}
