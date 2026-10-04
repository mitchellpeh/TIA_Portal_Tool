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

        if (args.Length == 0 || args[0] != "slc-import")
        {
            Console.Error.WriteLine("Usage: " + Usage + "\n   or: scl-export <project.apNN> <source.scl> <export folder>"
                                    + "\n   or: export-blocks <project.apNN> <export folder>\n   or: hmi-export <project.apNN> <export folder>");
            return 2;
        }

        return SlcImport(args.Skip(1).ToArray());
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
