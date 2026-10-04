using ClosedXML.Excel;
using System.IO;
using System.Reflection;
using TiaPortalTool.Conversion.Slc;

namespace TiaPortalTool.Services;

public sealed class SlcVerificationResult
{
    public List<RungCheck> Checks { get; } = new();
    public string ReportPath { get; set; } = string.Empty;
    public List<string> Messages { get; } = new();
}

/// <summary>
/// Checks a converted project against its SLC source: exports the blocks back out of TIA Portal and compares every
/// rung (see <see cref="SlcVerifier"/>). Writes SLC_Verification.xlsx with one row per rung.
/// </summary>
public sealed class SlcVerificationService
{
    public SlcVerificationResult Verify(string slcPath, string? symbolsPath, string projectPath, string outputFolder, IProgress<string> progress)
    {
        var result = new SlcVerificationResult();

        // Rebuild the conversion's maps from the source; nothing is written.
        var program = SlcExportParser.Parse(slcPath);
        var symbols = symbolsPath ?? SlcSymbolTable.FindBesideExport(slcPath);
        if (symbols is not null)
        {
            program.Symbols = SlcSymbolTable.Load(symbols);
        }

        var analysis = SlcProgramAnalysis.Analyze(program);
        var data = SlcDataBlockBuilder.Build(analysis);
        var verifier = new SlcVerifier(analysis, data, SlcProgramBuilder.BlockNames(program));

        var exportFolder = Path.Combine(Path.GetTempPath(), "slc-verify-" + Guid.NewGuid().ToString("N"));
        try
        {
            progress.Report("Exporting the converted blocks from TIA Portal...");
            using (var session = new TiaSessionService().Open(projectPath, progress))
            {
                var assembly = Assembly.LoadFrom(session.AssemblyPath);
                dynamic plcSoftware = PlcSoftwareLocator.FindFirstPlcSoftware(session.Project, assembly)
                                      ?? throw new InvalidOperationException("No PLC in the project.");
                new BlockExportService().ExportAll((object)plcSoftware, assembly, exportFolder);
            }

            progress.Report("Comparing every rung with the SLC source...");
            result.Checks.AddRange(verifier.Verify(exportFolder));
        }
        finally
        {
            try
            {
                Directory.Delete(exportFolder, recursive: true);
            }
            catch (IOException)
            {
                // A leftover temp folder doesn't matter.
            }
        }

        foreach (var group in result.Checks.GroupBy(c => c.Status).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            result.Messages.Add($"{group.Key}: {group.Count()} rung(s)");
        }

        var reportPath = Path.Combine(outputFolder, "SLC_Verification.xlsx");
        result.ReportPath = WriteReport(result, reportPath);
        if (ReportFiles.LockedNote(reportPath, result.ReportPath) is { } note)
        {
            result.Messages.Add(note);
        }
        result.Messages.Add($"Verification report: {result.ReportPath}");
        return result;
    }

    private static string WriteReport(SlcVerificationResult result, string path)
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add("Verification");
        var headers = new[] { "Block", "File", "Rung", "Status", "Detail", "SLC rung" };
        for (var c = 0; c < headers.Length; c++)
        {
            sheet.Cell(1, c + 1).Value = headers[c];
        }

        // Problems first, then the rest in program order.
        var rows = result.Checks
            .OrderBy(c => c.Status switch { "Mismatch" => 0, "Missing" => 1, "Unchecked" => 2, "Manual" => 3, _ => 4 })
            .ThenBy(c => c.File).ThenBy(c => c.Rung);
        var r = 2;
        foreach (var check in rows)
        {
            sheet.Cell(r, 1).Value = check.Block;
            sheet.Cell(r, 2).Value = check.File;
            sheet.Cell(r, 3).Value = check.Rung;
            sheet.Cell(r, 4).Value = check.Status;
            sheet.Cell(r, 5).Value = check.Detail;
            sheet.Cell(r, 6).Value = check.SlcText;
            r++;
        }

        var header = sheet.Range(1, 1, 1, headers.Length);
        header.Style.Font.Bold = true;
        header.Style.Fill.BackgroundColor = XLColor.FromHtml("#DCE6F1");
        sheet.SheetView.FreezeRows(1);
        sheet.Range(1, 1, Math.Max(2, r - 1), headers.Length).SetAutoFilter();
        sheet.Columns(1, 4).AdjustToContents();
        sheet.Column(5).Width = 80;
        sheet.Column(6).Width = 100;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return ReportFiles.Save(workbook, path);
    }
}
