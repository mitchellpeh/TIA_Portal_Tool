using System.IO;
using System.Text;

namespace TiaPortalTool.Conversion.Slc;

public enum CpuFamily
{
    S71500,
    S71200
}

public sealed class SlcConversionOptions
{
    public string SlcPath { get; set; } = string.Empty;

    /// <summary>Symbol file to use; null (the normal case) to use the .SY6/.SY5 that Save As wrote next to the .SLC.</summary>
    public string? SymbolsPath { get; set; }

    public string OutputDirectory { get; set; } = string.Empty;
    public CpuFamily Cpu { get; set; } = CpuFamily.S71500;

    /// <summary>The TIA Portal version written into the SimaticML files.</summary>
    public string EngineeringVersion { get; set; } = "V20";
}

public sealed class SlcConversionResult
{
    public List<string> Messages { get; } = new();

    /// <summary>Things the user has to check or finish by hand.</summary>
    public List<string> Warnings { get; } = new();

    public string ReportPath { get; set; } = string.Empty;
    public SlcProgram? Program { get; set; }
    public SlcProgramAnalysis? Analysis { get; set; }
    public SlcDataConversion? Data { get; set; }
    public SlcProgramConversion? Logic { get; set; }
    public List<ExternalSignal> ExternalSignals { get; } = new();
    public string ExternalSignalsPath { get; set; } = string.Empty;
}

/// <summary>
/// Converts an RSLogix 500 export into a folder that the Import Folder action can bring into a TIA Portal project.
/// It converts the data table (data file DBs, I/O image, timer setpoints) and the ladder logic (one LAD FC per program
/// file), and writes a report of everything that needs a person to finish it.
/// </summary>
public static class SlcConverter
{
    // Folders this converter writes; they are cleared on each run so stale files never get imported.
    /// <summary>The converter's version, shown in the app and on every report's cover. Bump it when the output changes.</summary>
    public const string ToolVersion = "0.2.2";

    public const string TypesFolder = "_types";
    public const string SourcesFolder = "_sources";
    private const string ReportFileName = "SLC_Conversion_Report.xlsx";

    public static SlcConversionResult Convert(SlcConversionOptions options, IProgress<string>? progress = null)
    {
        var result = new SlcConversionResult();

        progress?.Report($"Reading {Path.GetFileName(options.SlcPath)}...");
        var program = SlcExportParser.Parse(options.SlcPath);
        result.Program = program;
        result.Messages.Add($"Processor: {program.Processor} {program.ProcessorDescription}".TrimEnd());
        result.Messages.Add($"Project: {program.ProjectName}; {program.ProgramFiles.Count} program file(s), "
                            + $"{program.ProgramFiles.Values.Sum(f => f.Rungs.Count)} rung(s), {program.DataFiles.Count} data file(s).");
        result.Warnings.AddRange(program.ParseWarnings);

        var symbolsPath = options.SymbolsPath ?? SlcSymbolTable.FindBesideExport(options.SlcPath);
        if (symbolsPath is null)
        {
            result.Warnings.Add("No .SY6 symbol file found next to the .SLC, so there are no symbols or address descriptions. "
                                + "Members are named after their SLC addresses.");
        }
        else
        {
            progress?.Report($"Reading symbols from {Path.GetFileName(symbolsPath)}...");
            program.Symbols = SlcSymbolTable.Load(symbolsPath);
            result.Messages.Add($"Symbols: {program.Symbols.Count} address(es) with a symbol or description from {Path.GetFileName(symbolsPath)}.");
        }

        progress?.Report("Analyzing the logic...");
        var analysis = SlcProgramAnalysis.Analyze(program);
        result.Analysis = analysis;
        result.Warnings.AddRange(analysis.RungErrors.Select(e => "Couldn't read rung: " + e));
        result.Warnings.AddRange(analysis.Warnings);

        progress?.Report("Building data blocks...");
        var data = SlcDataBlockBuilder.Build(analysis);
        result.Data = data;
        result.Warnings.AddRange(data.Warnings);
        AddCpuWarnings(options, data, analysis, result.Warnings);

        progress?.Report("Converting the ladder logic...");
        var logic = SlcProgramBuilder.Build(analysis, data);
        result.Logic = logic;
        var rungs = logic.Blocks.Where(b => b.Group == SlcProgramBuilder.ProgramGroup).Sum(b => b.Networks.Count(n => !n.IsValueNetwork));
        var manual = logic.Blocks.Sum(b => b.Networks.Count(n => n.ManualReason is not null));
        result.Messages.Add($"Ladder: {rungs - manual} of {rungs} rung(s) converted; {manual} need manual conversion "
                            + "(marked MANUAL CONVERSION REQUIRED in the network titles, listed on the report's Ladder sheet).");

        progress?.Report($"Writing to {options.OutputDirectory}...");
        WriteOutput(options, data, result);
        WriteLogic(options, logic, result);

        progress?.Report("Listing external (HMI/SCADA) signals...");
        result.ExternalSignals.AddRange(SlcExternalSignals.Find(analysis, data));
        result.ExternalSignalsPath = SlcExternalSignals.Write(options.OutputDirectory, result.ExternalSignals, Path.GetFileNameWithoutExtension(options.SlcPath));
        result.Messages.Add($"External signals: {result.ExternalSignals.Count} candidate(s) to tag as HMI or SCADA in {Path.GetFileName(result.ExternalSignalsPath)}.");
        if (ReportFiles.LockedNote(Path.Combine(options.OutputDirectory, SlcExternalSignals.FileName), result.ExternalSignalsPath) is { } signalsNote)
        {
            result.Messages.Add(signalsNote);
        }

        progress?.Report("Writing the conversion report...");
        var reportPath = Path.Combine(options.OutputDirectory, ReportFileName);
        result.ReportPath = SlcConversionReport.Write(reportPath, options, result);
        if (ReportFiles.LockedNote(reportPath, result.ReportPath) is { } note)
        {
            result.Messages.Add(note);
        }
        result.Messages.Add($"Report: {result.ReportPath}");
        return result;
    }

    private static void WriteOutput(SlcConversionOptions options, SlcDataConversion data, SlcConversionResult result)
    {
        Directory.CreateDirectory(options.OutputDirectory);
        foreach (var folder in new[] { TypesFolder, SourcesFolder, SlcDataBlockBuilder.DataFilesGroup, SlcDataBlockBuilder.HalGroup,
                     SlcDataBlockBuilder.SetpointGroup, SlcProgramBuilder.ProgramGroup, SlcProgramBuilder.SupportGroup })
        {
            var path = Path.Combine(options.OutputDirectory, folder);
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }

        // The OBs go at the top level (no block group).
        foreach (var file in new[] { "Main.xml", "Startup.xml" })
        {
            File.Delete(Path.Combine(options.OutputDirectory, file));
        }

        foreach (var type in data.DataTypes)
        {
            Save(SimaticMlWriter.PlcType(type, options.EngineeringVersion), Path.Combine(options.OutputDirectory, TypesFolder, type.Name + ".xml"));
        }

        foreach (var block in data.DataBlocks)
        {
            var folder = Path.Combine(options.OutputDirectory, block.Group.Replace('/', Path.DirectorySeparatorChar));
            Save(SimaticMlWriter.GlobalDb(block, options.EngineeringVersion), Path.Combine(folder, block.Name + ".xml"));
        }

        result.Messages.Add($"Wrote {data.DataTypes.Count} PLC data type(s) and {data.DataBlocks.Count} data block(s) "
                            + $"({data.DataBlocks.Sum(b => b.Members.Count)} members) for Import Folder.");
    }

    private static void WriteLogic(SlcConversionOptions options, SlcProgramConversion logic, SlcConversionResult result)
    {
        foreach (var block in logic.Blocks)
        {
            var folder = Path.Combine(options.OutputDirectory, block.Group.Replace('/', Path.DirectorySeparatorChar));
            Save(SimaticMlWriter.CodeBlock(block, options.EngineeringVersion), Path.Combine(folder, block.Name + ".xml"));
        }

        foreach (var name in logic.HelperBlocks)
        {
            var number = SlcHelperSources.Blocks.First(b => b.Name == name).Number;
            Save(HelperBlock(name, number, options.EngineeringVersion),
                Path.Combine(options.OutputDirectory, SlcProgramBuilder.SupportGroup, name + ".xml"));
        }

        // The hw tag tables go with the HAL blocks; Import Folder brings tag tables in before the blocks that use them.
        foreach (var table in logic.Hal.TagTables)
        {
            var path = Path.Combine(options.OutputDirectory, SlcDataBlockBuilder.HalGroup, table.Name + ".tags.tsv");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllLines(path, new[] { "# Name\tDataType\tAddress\tComment. Placeholder addresses: change them to the real ones." }
                .Concat(table.Signals.Select(SlcHalBuilder.TagFileLine)), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        }

        result.Messages.Add($"Wrote {logic.Blocks.Count} code block(s) and {logic.HelperBlocks.Count} helper block(s).");
        result.Messages.Add(SlcHalBuilder.DescribeCount(logic.Hal));
    }

    /// <summary>A helper block from the embedded exports, with its fixed number and the target TIA Portal version.</summary>
    private static System.Xml.Linq.XDocument HelperBlock(string name, int number, string engineeringVersion)
    {
        using var stream = typeof(SlcConverter).Assembly.GetManifestResourceStream($"SlcHelper.{name}.xml")
                           ?? throw new InvalidOperationException($"Helper block {name} isn't embedded in the app.");
        var document = System.Xml.Linq.XDocument.Load(stream);
        document.Root!.Element("Engineering")?.SetAttributeValue("version", engineeringVersion);
        var attributes = document.Root.Elements().First(e => e.Name.LocalName.StartsWith("SW.Blocks.", StringComparison.Ordinal)).Element("AttributeList")!;
        attributes.Element("AutoNumber")?.SetValue("false");
        attributes.Element("Number")!.SetValue(number);
        return document;
    }

    private static void Save(System.Xml.Linq.XDocument document, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var writer = new StreamWriter(path, false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        document.Save(writer);
    }

    private static void AddCpuWarnings(SlcConversionOptions options, SlcDataConversion data, SlcProgramAnalysis analysis, List<string> warnings)
    {
        var retainBytes = data.DataBlocks.Where(b => b.Retain).Sum(b => b.SizeBytes);
        if (options.Cpu == CpuFamily.S71200 && retainBytes > 10 * 1024)
        {
            warnings.Add($"The retentive data blocks need about {retainBytes / 1024.0:0.#} KB of retentive memory. Most S7-1200 CPUs "
                         + "have 10 KB, so some data files may need to be made non-retentive.");
        }

        // First pass (OB100), the clocks (SLC_STATUS), overflow trap and battery low are handled automatically and
        // explained in the network comments; only status use that needs a person is listed.
        warnings.AddRange(SlcStatusFile.Warnings(analysis.References));
    }
}
