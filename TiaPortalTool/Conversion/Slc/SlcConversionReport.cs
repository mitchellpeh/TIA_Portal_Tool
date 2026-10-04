using ClosedXML.Excel;
using System.Globalization;
using System.IO;

namespace TiaPortalTool.Conversion.Slc;

/// <summary>Writes the conversion report workbook: what was found, where every address went, and what needs manual work.</summary>
public static class SlcConversionReport
{
    /// <summary>Writes the report and returns the path it was saved under (see <see cref="ReportFiles.Save"/>).</summary>
    public static string Write(string path, SlcConversionOptions options, SlcConversionResult result)
    {
        var program = result.Program!;
        var analysis = result.Analysis!;
        var data = result.Data!;

        using var workbook = new XLWorkbook();
        WriteCover(workbook.Worksheets.Add("Cover"), options, result);
        WriteManualWork(workbook.Worksheets.Add("Manual work"), program, analysis, result);
        if (result.Logic is not null)
        {
            WriteLadder(workbook.Worksheets.Add("Ladder"), result.Logic);
        }

        WriteProgramFiles(workbook.Worksheets.Add("Program files"), program);
        WriteInstructions(workbook.Worksheets.Add("Instructions"), analysis);
        WriteDataBlocks(workbook.Worksheets.Add("Data blocks"), data);
        WriteTimers(workbook.Worksheets.Add("Timers"), program, analysis);
        WriteSetpoints(workbook.Worksheets.Add("Setpoints"), program, analysis);
        WriteHardware(workbook.Worksheets.Add("HAL zz tags"), data, result.Logic?.Hal ?? new SlcHalConversion());
        WriteAddressMap(workbook.Worksheets.Add("Address map"), data);
        return ReportFiles.Save(workbook, path);
    }

    // Sheet names and what each is for, in workbook order; the cover lists them with links.
    private static readonly (string Name, string Purpose)[] Sheets =
    {
        ("Manual work", "Everything to check or finish by hand: status bits, module data, CPU settings, instructions with no TIA equivalent."),
        ("Ladder", "Rungs marked MANUAL CONVERSION REQUIRED, with the reason and the original SLC rung; then a summary per block."),
        ("Program files", "The SLC program files and how many rungs and rung comments each has."),
        ("Instructions", "Every SLC instruction used, how often, and how it was converted."),
        ("Data blocks", "The generated DBs: number, group, access type, retain, size."),
        ("Timers", "Every timer: timebase, preset, and where the preset comes from (constant, HMI setpoint, calculated)."),
        ("Setpoints", "Timer presets entered from outside the PLC (HMI, SCADA or another system over a comms card), now hours/minutes/seconds setpoint objects in TIME_SP."),
        ("HAL zz tags", "The I/O placeholders to map to the new hardware, with the SLC address, description, hw tag, placeholder address and mapping FC."),
        ("Address map", "Every SLC address and the TIA operand it became."),
    };

    private static readonly XLColor Navy = XLColor.FromHtml("#1F3864");
    private static readonly XLColor Muted = XLColor.FromHtml("#595959");
    private static readonly XLColor Green = XLColor.FromHtml("#2E7D32");
    private static readonly XLColor Amber = XLColor.FromHtml("#B26A00");

    /// <summary>The first sheet: what was converted, the headline numbers, what to do next, and links to the details.</summary>
    private static void WriteCover(IXLWorksheet sheet, SlcConversionOptions options, SlcConversionResult result)
    {
        var program = result.Program!;
        var analysis = result.Analysis!;
        var data = result.Data!;
        sheet.ShowGridLines = false;
        sheet.SetTabColor(Navy);
        sheet.Column(1).Width = 3;
        sheet.Column(2).Width = 38;
        sheet.Column(3).Width = 95;

        // Title band.
        sheet.Range("B2:C3").Style.Fill.BackgroundColor = Navy;
        sheet.Cell("B2").Value = "SLC 500 \u2192 TIA Portal conversion report";
        sheet.Cell("B2").Style.Font.SetBold().Font.SetFontSize(20).Font.SetFontColor(XLColor.White);
        sheet.Cell("B3").Value = Path.GetFileNameWithoutExtension(options.SlcPath)
                                 + (program.ProjectName.Length > 0 ? $"   (processor name {program.ProjectName})" : string.Empty);
        sheet.Cell("B3").Style.Font.SetFontSize(13).Font.SetFontColor(XLColor.White);
        sheet.Row(2).Height = 32;
        sheet.Row(3).Height = 22;

        var row = 5;
        void Detail(string label, string value)
        {
            sheet.Cell(row, 2).Value = label;
            sheet.Cell(row, 2).Style.Font.SetFontColor(Muted);
            sheet.Cell(row, 3).Value = value;
            row++;
        }

        Detail("Source", Path.GetFullPath(options.SlcPath));
        Detail("Symbols and descriptions", program.Symbols.SourcePath.Length > 0 ? Path.GetFullPath(program.Symbols.SourcePath) : "(none found: data is named after the SLC addresses)");
        Detail("Converted", DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
        Detail("SLC processor", $"{program.Processor} {program.ProcessorDescription}".Trim());
        Detail("Target CPU family", options.Cpu == CpuFamily.S71500 ? "S7-1500" : "S7-1200");
        Detail("Converted with", $"SLC converter v{SlcConverter.ToolVersion}");

        // Headline numbers.
        row++;
        Section(sheet, ref row, "At a glance");
        var logic = result.Logic;
        var rungs = logic?.Blocks.Where(b => b.Group == SlcProgramBuilder.ProgramGroup).Sum(b => b.Networks.Count(n => !n.IsValueNetwork)) ?? 0;
        var manual = logic?.Blocks.Sum(b => b.Networks.Count(n => n.ManualReason is not null)) ?? 0;
        // zz tags the logic actually uses; the I/O DBs also hold every other bit of a used word.
        var used = new HashSet<string>(analysis.References.Select(r => r.ToString()), StringComparer.Ordinal);
        var zzAll = data.AddressMap.Where(m => m.Block is "I1" or "O0" && m.Member.StartsWith(SlcDataBlockBuilder.HardwarePrefix, StringComparison.Ordinal)).ToList();
        var zzUsed = zzAll.Count(m => used.Contains(m.SlcAddress));

        void Metric(string label, string value, XLColor? color = null)
        {
            sheet.Cell(row, 2).Value = label;
            var cell = sheet.Cell(row, 3);
            cell.Value = value;
            cell.Style.Font.SetBold().Font.SetFontSize(12);
            if (color is not null)
            {
                cell.Style.Font.SetFontColor(color);
            }

            row++;
        }

        if (rungs > 0)
        {
            var percent = 100.0 * (rungs - manual) / rungs;
            Metric("Rungs converted", $"{rungs - manual} of {rungs}   ({percent.ToString("0.0", CultureInfo.InvariantCulture)} %)", Green);
            Metric("Rungs needing manual conversion", manual.ToString(CultureInfo.InvariantCulture) + (manual > 0 ? "   (Ladder sheet)" : string.Empty), manual > 0 ? Amber : Green);
        }

        Metric("Other items to check", result.Warnings.Count.ToString(CultureInfo.InvariantCulture) + (result.Warnings.Count > 0 ? "   (Manual work sheet)" : string.Empty),
            result.Warnings.Count > 0 ? Amber : Green);
        Metric("Program files \u2192 LAD FCs", program.ProgramFiles.Values.Count(f => f.Number >= 2).ToString(CultureInfo.InvariantCulture));
        Metric("Data files \u2192 data blocks", $"{program.DataFiles.Count} data files, {data.DataBlocks.Count} data blocks in all");
        Metric("Timer setpoint objects (HMI/SCADA)", analysis.Setpoints.Count.ToString(CultureInfo.InvariantCulture));
        Metric("zz I/O tags to map to hardware", $"{zzUsed} used by the logic   ({zzAll.Count} in the I/O image)");
        var inbound = result.ExternalSignals.Count(s => s.Direction == "In");
        Metric("External (HMI/SCADA) signal candidates", $"{inbound} in, {result.ExternalSignals.Count - inbound} out   ({SlcExternalSignals.FileName})");

        // Next steps.
        row++;
        Section(sheet, ref row, "What to do next");
        var steps = new[]
        {
            "Finish the rungs marked MANUAL CONVERSION REQUIRED (Ladder sheet). Each network keeps the original SLC rung in its comment.",
            "Go through the Manual work sheet: status file use, module (DeviceNet/comms) data, and CPU settings such as clock memory.",
            "Set up the real hardware, then give each hw tag its real address in the HAL Slot tag tables (HAL zz tags sheet; see HAL and hw tags below).",
            $"Open {SlcExternalSignals.FileName} (next to this report) and tag each external signal as HMI or SCADA (see Files in this folder).",
            "Swap the placeholder CPU for the real one in TIA Portal (Change device).",
            "After Convert and Import, open SLC_Verification.xlsx: every converted rung is checked against the original.",
        };
        for (var i = 0; i < steps.Length; i++)
        {
            var range = sheet.Range(row, 2, row, 3).Merge();
            range.Value = $"{i + 1}.  {steps[i]}";
            range.Style.Alignment.SetWrapText();
            row++;
        }

        // Terms the rest of the report uses.
        row++;
        Section(sheet, ref row, "Words used in this report");
        var terms = new (string Term, string Meaning)[]
        {
            ("HAL (hardware abstraction layer)",
                "The converted logic never touches real inputs and outputs. Every SLC I/O point (I:, O:) became a zz tag in the DBs I1 (inputs) "
                + "and O0 (outputs), and the logic uses those tags. The HAL is the one place that ties them to the new hardware: "
                + "HAL_INPUTS copies each real input into its zz tag before the logic runs and HAL_OUTPUTS copies each output zz tag out "
                + "after it, through one FC per SLC slot with one network per signal. Changing or replacing hardware never means changing the logic."),
            ("hw tags",
                "The real-signal side of the HAL: one PLC tag per signal, in a tag table per SLC slot (\"HAL Slot 2 1746-IB16\"). Their %I/%Q "
                + "addresses are placeholders laid out like a matching Siemens card; set them to the real addresses (or set the module "
                + "addresses in Device configuration to match) and the HAL is mapped. For remote devices (DeviceNet, now PROFINET), use the "
                + "device's addresses or replace the hw tag in its mapping network."),
            ("zz tags",
                "The HAL tags, named zz + the SLC symbol (or zzI_slot_word_bit). \"zz\" marks a placeholder still to be mapped to real hardware; "
                + "the HAL zz tags sheet lists them with their SLC addresses, descriptions, hw tags and mapping FCs."),
            ("Data file DBs",
                "Each SLC data file is a DB with the same number and name (N7 is DB7 \"N7\", B3 is DB3 \"B3\"). Elements keep their SLC symbols; "
                + "unnamed ones are named after their address (\"B3\".\"0/5\", \"N7\".\"30\")."),
            ("TIME_SP setpoint objects",
                "Timer presets that came from an HMI or another system are now hours/minutes/seconds setpoints in the DB TIME_SP, "
                + "worked out into each timer's preset every scan."),
            ("SLC Support",
                "A group of small commented SCL functions (FC 9001 and up) for the few SLC instructions LAD can't express on the converted "
                + "data (file copies, indirect addresses, timer values as numbers, the status clocks)."),
            ("MANUAL CONVERSION REQUIRED",
                "The title of a network the converter couldn't convert. It keeps the original SLC rung in its comment; the Ladder sheet lists them all."),
        };
        foreach (var (term, meaning) in terms)
        {
            sheet.Cell(row, 2).Value = term;
            sheet.Cell(row, 2).Style.Font.SetBold();
            sheet.Cell(row, 3).Value = meaning;
            sheet.Cell(row, 3).Style.Alignment.SetWrapText();
            row++;
        }

        // What else the conversion wrote, and what each file is for.
        row++;
        Section(sheet, ref row, "Files in this folder");
        var files = new (string File, string Purpose)[]
        {
            (Path.GetFileName(ReportFileNameFor(result)), "This report."),
            (Path.GetFileName(result.ExternalSignalsPath.Length > 0 ? result.ExternalSignalsPath : SlcExternalSignals.FileName),
                "A second workbook for you to fill in: the data the program most likely exchanges with an HMI or SCADA system "
                + "(buttons, setpoints, status, communication-card data; card data is flagged as likely SCADA). Tag each row HMI, SCADA, Both, Internal or Not used; "
                + "converting again into this folder keeps your tags."),
            ("SLC_Verification.xlsx",
                "Written after Convert and Import: every converted rung read back out of TIA Portal and checked against the original SLC rung."),
            ("logs\\", "The log of each import run."),
            ("Block folders",
                "Data Files, HAL, Program Files, Setpoints, SLC Support and _types: the converted blocks and data types, which "
                + "Convert and Import (or Import Folder) brings into TIA Portal as block groups."),
        };
        foreach (var (file, purpose) in files)
        {
            sheet.Cell(row, 2).Value = file;
            sheet.Cell(row, 2).Style.Font.SetBold();
            sheet.Cell(row, 3).Value = purpose;
            sheet.Cell(row, 3).Style.Alignment.SetWrapText();
            row++;
        }

        // Sheet index.
        row++;
        Section(sheet, ref row, "In this report");
        foreach (var (name, purpose) in Sheets)
        {
            var cell = sheet.Cell(row, 2);
            cell.Value = name;
            cell.SetHyperlink(new XLHyperlink($"'{name}'!A1"));
            cell.Style.Font.SetFontColor(XLColor.FromHtml("#0563C1")).Font.SetUnderline();
            sheet.Cell(row, 3).Value = purpose;
            sheet.Cell(row, 3).Style.Alignment.SetWrapText();
            row++;
        }

        // The SLC rack, for reference.
        row++;
        Section(sheet, ref row, "SLC rack");
        foreach (var slot in program.Slots)
        {
            sheet.Cell(row, 2).Value = $"Slot {slot.Slot}";
            sheet.Cell(row, 3).Value = $"{slot.Catalog}  {slot.Description}  (in {slot.InputWords} / out {slot.OutputWords} words)";
            row++;
        }

        sheet.Range(5, 2, row, 3).Style.Alignment.SetVertical(XLAlignmentVerticalValues.Top);
        sheet.PageSetup.PageOrientation = XLPageOrientation.Landscape;
        sheet.PageSetup.FitToPages(1, 0);
    }

    // The report's own file name; the converter may have had to use another name if the usual one was open.
    private static string ReportFileNameFor(SlcConversionResult result) =>
        result.ReportPath.Length > 0 ? result.ReportPath : "SLC_Conversion_Report.xlsx";

    private static void Section(IXLWorksheet sheet, ref int row, string title)
    {
        var range = sheet.Range(row, 2, row, 3);
        sheet.Cell(row, 2).Value = title;
        range.Style.Font.SetBold().Font.SetFontSize(13).Font.SetFontColor(Navy);
        range.Style.Border.BottomBorder = XLBorderStyleValues.Medium;
        range.Style.Border.BottomBorderColor = Navy;
        row++;
    }

    private static void WriteManualWork(IXLWorksheet sheet, SlcProgram program, SlcProgramAnalysis analysis, SlcConversionResult result)
    {
        var rows = new List<object[]>();
        foreach (var warning in result.Warnings)
        {
            rows.Add(new object[] { "Check", warning });
        }

        foreach (var file in program.ProgramFiles.Values)
        {
            foreach (var rung in file.Rungs)
            {
                if (!analysis.Rungs.TryGetValue((file.Number, rung.Number), out var tokenized))
                {
                    continue;
                }

                foreach (var element in tokenized.Elements.Where(e => e.Info?.Support == ConversionSupport.Manual))
                {
                    rows.Add(new object[] { "Manual instruction", $"File {file.Number} {file.Name} rung {rung.Number}: {element} - {element.Info!.TiaEquivalent}" });
                }
            }
        }

        foreach (var indirect in analysis.IndirectReferences)
        {
            rows.Add(new object[] { "Indirect address", indirect + " (converted with computed addressing on the standard-access DB layout)" });
        }

        foreach (var functionFile in analysis.References.Where(r => program.FunctionFiles.Contains(r.FileName)).Select(r => r.ToString()).Distinct())
        {
            rows.Add(new object[] { "Function file", $"{functionFile} is a MicroLogix function file; replace by hand." });
        }

        WriteTable(sheet, new[] { "Kind", "Detail" }, rows);
        sheet.Column(2).Width = 140;
    }

    /// <summary>Every rung that needs manual conversion first, then a per-block summary.</summary>
    private static void WriteLadder(IXLWorksheet sheet, SlcProgramConversion logic)
    {
        var rows = new List<object[]>();
        foreach (var block in logic.Blocks)
        {
            foreach (var network in block.Networks.Where(n => n.ManualReason is not null))
            {
                var slc = network.Comment.Substring(Math.Max(0, network.Comment.LastIndexOf("SLC: ", StringComparison.Ordinal)));
                rows.Add(new object[] { block.Name, network.Title.Replace(" - MANUAL CONVERSION REQUIRED", string.Empty), "Manual", network.ManualReason!, slc });
            }
        }

        foreach (var block in logic.Blocks)
        {
            var manual = block.Networks.Count(n => n.ManualReason is not null);
            var count = block.Networks.Count(n => !n.IsValueNetwork);
            rows.Add(new object[] { block.Name, $"{block.BlockType}{block.Number}", "Summary", $"{count - manual} of {count} rungs/networks converted", string.Empty });
        }

        WriteTable(sheet, new[] { "Block", "Rung", "Status", "Reason / result", "SLC rung" }, rows);
        sheet.Column(5).Width = 100;
    }

    private static void WriteProgramFiles(IXLWorksheet sheet, SlcProgram program)
    {
        var rows = program.ProgramFiles.Values.Select(f =>
        {
            var symbol = program.Symbols.FindProgramFile(f.Number);
            return new object[]
            {
                f.Number, f.Name, f.Rungs.Count, f.Rungs.Count(r => r.Description.Length > 0 || r.Title.Length > 0),
                symbol?.Symbol ?? string.Empty, symbol?.Description ?? string.Empty
            };
        });
        WriteTable(sheet, new[] { "File", "Name", "Rungs", "Rungs with comments", "Symbol", "Description" }, rows);
    }

    private static void WriteInstructions(IXLWorksheet sheet, SlcProgramAnalysis analysis)
    {
        var rows = analysis.InstructionCounts
            .OrderByDescending(p => p.Value)
            .Select(p =>
            {
                var info = SlcInstructionSet.Find(p.Key);
                return new object[] { p.Key, p.Value, info?.Support.ToString() ?? "Unknown", info?.TiaEquivalent ?? string.Empty };
            });
        WriteTable(sheet, new[] { "Instruction", "Count", "Conversion", "TIA Portal plan" }, rows);
    }

    private static void WriteDataBlocks(IXLWorksheet sheet, SlcDataConversion data)
    {
        var rows = data.DataBlocks.OrderBy(b => b.Number).Select(b => new object[]
        {
            b.Number, b.Name, b.Group, b.StandardAccess ? "Standard" : "Optimized", b.Retain ? "Yes" : "No", b.Members.Count, b.SizeBytes, b.Comment
        });
        WriteTable(sheet, new[] { "DB", "Name", "Group", "Access", "Retain", "Members", "Approx. bytes", "Comment" }, rows);
    }

    private static void WriteTimers(IXLWorksheet sheet, SlcProgram program, SlcProgramAnalysis analysis)
    {
        var rows = analysis.Timers.Values
            .OrderBy(t => t.Timer.FileNumber).ThenBy(t => t.Timer.Element)
            .Select(t =>
            {
                var symbol = program.Symbols.Find(t.Timer);
                var preset = t.Timebase is { } tb && t.Preset == PresetSource.Constant && int.TryParse(t.InstructionPresets.FirstOrDefault(), out var p)
                    ? SlcDataBlockBuilder.FormatTime(p * tb)
                    : string.Empty;
                return new object[]
                {
                    t.Timer.ToString(), symbol?.Symbol ?? string.Empty, string.Join("/", t.Instructions), string.Join("/", t.Timebases),
                    string.Join("/", t.InstructionPresets), t.Preset.ToString(), string.Join(", ", t.PresetWords), preset,
                    symbol?.Description ?? string.Empty
                };
            });
        WriteTable(sheet, new[] { "Timer", "Symbol", "Instruction", "Timebase (s)", "Preset in instruction", "Preset source", "Preset words", "TIA preset", "Description" }, rows);
    }

    private static void WriteSetpoints(IXLWorksheet sheet, SlcProgram program, SlcProgramAnalysis analysis)
    {
        var rows = analysis.Setpoints.Select(s =>
        {
            var symbol = program.Symbols.Find(s.Word);
            return new object[]
            {
                s.Word.ToString(), symbol?.Symbol ?? string.Empty, string.Join(", ", s.Timers),
                s.SecondsPerCount?.ToString(CultureInfo.InvariantCulture) ?? "unknown", string.Join(", ", s.EnteredAt),
                s.Note, symbol?.Description ?? string.Empty
            };
        });
        WriteTable(sheet, new[] { "Word", "Symbol", "Timers", "Seconds per count", "HMI enters at", "Note", "Description" }, rows);
    }

    // Every zz tag; the ones the logic uses also show their hw tag, placeholder address and mapping FC.
    private static void WriteHardware(IXLWorksheet sheet, SlcDataConversion data, SlcHalConversion hal)
    {
        var signals = hal.Signals.ToDictionary(s => s.SlcAddress, StringComparer.Ordinal);
        var rows = data.AddressMap
            .Where(m => m.Block is "I1" or "O0" && m.Member.Length > 0)
            .Select(m =>
            {
                signals.TryGetValue(m.SlcAddress, out var s);
                return new object[]
                {
                    m.SlcAddress, m.TiaOperand, m.DataType, m.Symbol, m.Description,
                    s?.Source ?? string.Empty, s?.SourceKind ?? string.Empty, s?.Device ?? string.Empty,
                    s?.HardwareTag ?? "(not used by the logic)", s?.Address ?? string.Empty, s?.TagTable ?? string.Empty, s?.MappingBlock ?? string.Empty
                };
            });
        WriteTable(sheet, new[]
        {
            "SLC address", "zz tag", "Type", "SLC symbol", "Description", "SLC source", "Source kind", "DeviceNet device",
            "hw tag", "Placeholder address", "Tag table", "Mapping FC"
        }, rows);
    }

    private static void WriteAddressMap(IXLWorksheet sheet, SlcDataConversion data)
    {
        var numbers = data.DataBlocks.ToDictionary(b => b.Name, b => b.Number);
        var rows = data.AddressMap.Select(m => new object[]
        {
            m.SlcAddress, m.Symbol, m.TiaOperand, numbers.TryGetValue(m.Block, out var n) ? m.AbsoluteOperand(n) : string.Empty, m.DataType, m.Description
        });
        WriteTable(sheet, new[] { "SLC address", "SLC symbol", "TIA operand", "Absolute", "Type", "Description" }, rows);
    }

    private static void WriteTable(IXLWorksheet sheet, string[] headers, IEnumerable<object[]> rows)
    {
        for (var c = 0; c < headers.Length; c++)
        {
            sheet.Cell(1, c + 1).Value = headers[c];
        }

        var r = 2;
        foreach (var row in rows)
        {
            for (var c = 0; c < row.Length; c++)
            {
                sheet.Cell(r, c + 1).Value = XLCellValue.FromObject(row[c]);
            }

            r++;
        }

        var header = sheet.Range(1, 1, 1, headers.Length);
        header.Style.Font.Bold = true;
        header.Style.Fill.BackgroundColor = XLColor.FromHtml("#DCE6F1");
        sheet.SheetView.FreezeRows(1);
        if (r > 2)
        {
            sheet.Range(1, 1, r - 1, headers.Length).SetAutoFilter();
        }

        sheet.Columns(1, headers.Length).AdjustToContents(1, Math.Min(r, 500), 8, 80);
    }
}
