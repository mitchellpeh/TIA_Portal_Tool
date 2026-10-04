using ClosedXML.Excel;
using System.Globalization;
using System.IO;

namespace TiaPortalTool.Conversion.Slc;

/// <summary>One data-file word or bit that probably comes from, or goes to, something outside the PLC.</summary>
public sealed class ExternalSignal
{
    public string Address { get; set; } = string.Empty;
    public string Symbol { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;

    /// <summary>"In" (set from outside the PLC) or "Out" (for something outside to read).</summary>
    public string Direction { get; set; } = string.Empty;

    public string Reason { get; set; } = string.Empty;

    /// <summary>
    /// The converter's best guess at who's on the other side. Data copied through a communication card is almost
    /// always SCADA (or another system); the rest can't be told apart, as an HMI and a SCADA system reach the data
    /// table the same way.
    /// </summary>
    public string Likely { get; set; } = string.Empty;

    public string UsedAt { get; set; } = string.Empty;
    public string TiaOperand { get; set; } = string.Empty;

    /// <summary>The user's tag (HMI, SCADA, Both, Internal, Not used); kept across conversions.</summary>
    public string BelongsTo { get; set; } = string.Empty;

    public string Notes { get; set; } = string.Empty;
}

/// <summary>
/// Lists the data the SLC program probably exchanges with an HMI or SCADA system, for the user to tag. The program
/// can only show where data crosses the PLC boundary, not who's on the other side, so the list is evidence-based:
/// words copied to or from a communication card's module data, data the logic reads but never writes (set from
/// outside: buttons, setpoints), and data it writes but never reads (shown outside: status, messages).
/// </summary>
public static class SlcExternalSignals
{
    public const string FileName = "SLC_External_Signals.xlsx";
    private const string SheetName = "External signals";
    private static readonly string[] Tags = { "HMI", "SCADA", "Both", "Internal", "Not used" };
    public const string LikelyScada = "SCADA (comms card)";
    public const string LikelyEither = "HMI or SCADA";
    private const string BelongsToHeader = "Belongs to";
    private const string NotesHeader = "Notes";

    public static List<ExternalSignal> Find(SlcProgramAnalysis analysis, SlcDataConversion data)
    {
        var program = analysis.Program;
        var operands = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var mapping in data.AddressMap.Where(m => m.Member.Length > 0))
        {
            operands[mapping.SlcAddress] = mapping.TiaOperand;
        }

        // HMI timer setpoints are entered in their setpoint object in TIA, not in the old word.
        const string presetSuffix = " (as timer preset)";
        var setpointObjects = data.AddressMap
            .Where(m => m.SlcAddress.EndsWith(presetSuffix, StringComparison.Ordinal))
            .ToDictionary(m => m.SlcAddress.Substring(0, m.SlcAddress.Length - presetSuffix.Length),
                m => $"\"TIME_SP\".{m.Member.Replace(".Output_Seconds", string.Empty)}", StringComparer.Ordinal);

        // Communication cards: anything in the rack that isn't local I/O or a field-device scanner (DeviceNet, Remote I/O),
        // whose data belongs to the hardware mapping instead.
        var commsSlots = program.Slots
            .Where(s => !s.Catalog.StartsWith("1746-", StringComparison.OrdinalIgnoreCase)
                        && !s.Catalog.StartsWith("1762-", StringComparison.OrdinalIgnoreCase)
                        && !s.Catalog.StartsWith("1769-", StringComparison.OrdinalIgnoreCase)
                        && !s.Catalog.StartsWith("Bul.", StringComparison.OrdinalIgnoreCase)
                        && !s.Catalog.StartsWith("1747-L", StringComparison.OrdinalIgnoreCase)
                        && s.Catalog is not ("1747-SDN" or "1747-SN" or "1747-BSN"))
            .ToDictionary(s => s.Slot, s => $"slot {s.Slot} {s.Catalog} {s.Description}".Trim());

        var readWords = new HashSet<SlcAddress>(analysis.ReadAddresses.Select(a => a.WordAddress));
        var writtenWordsByBit = new HashSet<SlcAddress>(analysis.WrittenBits.Select(a => a.WordAddress));
        var signals = new List<ExternalSignal>();

        foreach (var file in program.DataFiles.Values.Where(f => f.Kind is SlcFileKind.Bit or SlcFileKind.Integer or SlcFileKind.Float or SlcFileKind.Long or SlcFileKind.String))
        {
            var indirect = analysis.IndirectFiles.Contains(file.Name) || analysis.IndirectFiles.Contains(file.Type + "*");
            for (var element = 0; element < file.Length; element++)
            {
                var word = SlcAddress.TryParse($"{file.Name}:{element}")!;
                if (!analysis.UsedAt.TryGetValue(word, out var places) && !analysis.CopiedFromModule.ContainsKey(word) && !analysis.CopiedToModule.ContainsKey(word))
                {
                    continue;   // the logic never touches it
                }

                var usedAt = places is null ? string.Empty : string.Join(", ", places.Select(p => "file " + p.Replace(":", " rung ")));
                if (file.Kind == SlcFileKind.Bit && !analysis.CopiedFromModule.ContainsKey(word) && !analysis.CopiedToModule.ContainsKey(word))
                {
                    // Bit files: judge each bit; a whole-word read or write counts for all 16.
                    var wordRead = analysis.ReadAddresses.Contains(word);
                    for (var bit = 0; bit < 16; bit++)
                    {
                        var address = SlcAddress.TryParse($"{file.Name}:{element}/{bit}")!;
                        var read = wordRead || analysis.ReadAddresses.Contains(address);
                        var written = analysis.WrittenBits.Contains(address) || analysis.WrittenWords.Contains(word) && !writtenWordsByBit.Contains(word);
                        Add(signals, address, read, written, null, indirect, usedAt, program, operands);
                    }

                    continue;
                }

                var wordIsRead = readWords.Contains(word);
                var wordIsWritten = analysis.WrittenWords.Contains(word) || writtenWordsByBit.Contains(word);
                string? comms = null;
                if (analysis.CopiedFromModule.TryGetValue(word, out var from) && commsSlots.TryGetValue(from.Slot, out var inCard))
                {
                    comms = $"In:copied in from {inCard} ({from.Source})";
                }
                else if (analysis.CopiedToModule.TryGetValue(word, out var to) && commsSlots.TryGetValue(to.Slot, out var outCard))
                {
                    comms = $"Out:copied out to {outCard} ({to.Destination})";
                }

                Add(signals, word, wordIsRead, wordIsWritten, comms, indirect, usedAt, program, operands);
                if (signals.Count > 0 && signals[signals.Count - 1].Address == word.ToString() && setpointObjects.TryGetValue(word.ToString(), out var setpoint))
                {
                    var last = signals[signals.Count - 1];
                    last.TiaOperand = setpoint;
                    last.Reason += $"; a timer preset: in TIA it's entered as hours/minutes/seconds in {setpoint}";
                }
            }
        }

        return signals;
    }

    private static void Add(List<ExternalSignal> signals, SlcAddress address, bool read, bool written, string? comms, bool indirect,
        string usedAt, SlcProgram program, Dictionary<string, string> operands)
    {
        string direction, reason;
        if (comms is not null)
        {
            direction = comms.Substring(0, comms.IndexOf(':'));
            reason = comms.Substring(comms.IndexOf(':') + 1);
        }
        else if (read && !written)
        {
            direction = "In";
            reason = "read by the logic but never written by it: probably set from outside the PLC (button, setpoint)";
        }
        else if (written && !read)
        {
            direction = "Out";
            reason = "written by the logic but never read by it: probably shown or sent outside the PLC (status, message)";
        }
        else
        {
            return;
        }

        if (indirect)
        {
            reason += "; this file is also reached through indirect addresses, so check it";
        }

        var symbol = program.Symbols.Find(address);
        signals.Add(new ExternalSignal
        {
            Address = address.ToString(),
            Symbol = symbol?.Symbol ?? string.Empty,
            Description = symbol?.Description ?? string.Empty,
            Direction = direction,
            Reason = reason,
            Likely = comms is not null ? LikelyScada : LikelyEither,
            UsedAt = usedAt,
            TiaOperand = operands.TryGetValue(address.ToString(), out var operand) ? operand : string.Empty
        });
    }

    /// <summary>
    /// Writes the workbook. Tags and notes already entered in an existing copy are carried over by SLC address, so a
    /// re-conversion never loses the user's work. Returns the path written (a new name if the file is open elsewhere).
    /// </summary>
    public static string Write(string folder, List<ExternalSignal> signals, string sourceName)
    {
        var path = Path.Combine(folder, FileName);
        var previous = ReadTags(path);
        foreach (var signal in signals)
        {
            if (previous.TryGetValue(signal.Address, out var tag))
            {
                signal.BelongsTo = tag.BelongsTo;
                signal.Notes = tag.Notes;
            }
        }

        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add(SheetName);
        var headers = new[] { "SLC address", "Symbol", "Description", "Direction", "Likely", BelongsToHeader, NotesHeader, "Why it's listed", "Used at", "TIA operand" };
        for (var c = 0; c < headers.Length; c++)
        {
            sheet.Cell(1, c + 1).Value = headers[c];
        }

        var row = 2;
        foreach (var signal in signals.OrderBy(s => s.Direction, StringComparer.Ordinal).ThenBy(s => s.Reason.StartsWith("copied", StringComparison.Ordinal) ? 0 : 1))
        {
            sheet.Cell(row, 1).Value = signal.Address;
            sheet.Cell(row, 2).Value = signal.Symbol;
            sheet.Cell(row, 3).Value = signal.Description;
            sheet.Cell(row, 4).Value = signal.Direction;
            sheet.Cell(row, 5).Value = signal.Likely;
            if (signal.Likely == LikelyScada)
            {
                sheet.Cell(row, 5).Style.Font.SetBold();
            }

            sheet.Cell(row, 6).Value = signal.BelongsTo;
            sheet.Cell(row, 7).Value = signal.Notes;
            sheet.Cell(row, 8).Value = signal.Reason;
            sheet.Cell(row, 9).Value = signal.UsedAt;
            sheet.Cell(row, 10).Value = signal.TiaOperand;
            row++;
        }

        var last = Math.Max(2, row - 1);
        var header = sheet.Range(1, 1, 1, headers.Length);
        header.Style.Font.Bold = true;
        header.Style.Fill.BackgroundColor = XLColor.FromHtml("#DCE6F1");

        // The two columns for the user stand out, and "Belongs to" offers the tags as a dropdown.
        var userColumns = sheet.Range(2, 6, last, 7);
        userColumns.Style.Fill.BackgroundColor = XLColor.FromHtml("#FFF8E1");
        sheet.Range(1, 6, 1, 7).Style.Fill.BackgroundColor = XLColor.FromHtml("#FFE082");
        sheet.Range(2, 6, last, 6).CreateDataValidation().List("\"" + string.Join(",", Tags) + "\"", true);

        sheet.SheetView.FreezeRows(1);
        sheet.Range(1, 1, last, headers.Length).SetAutoFilter();
        sheet.PageSetup.PageOrientation = XLPageOrientation.Landscape;
        sheet.PageSetup.FitToPages(1, 0);
        sheet.PageSetup.SetRowsToRepeatAtTop(1, 1);
        sheet.Column(1).Width = 14;
        sheet.Column(2).Width = 24;
        sheet.Column(3).Width = 45;
        sheet.Column(4).Width = 10;
        sheet.Column(5).Width = 18;
        sheet.Column(6).Width = 12;
        sheet.Column(7).Width = 30;
        sheet.Column(8).Width = 70;
        sheet.Column(9).Width = 30;
        sheet.Column(10).Width = 28;

        var about = workbook.Worksheets.Add("How to use");
        about.Column(1).Width = 110;
        var lines = new[]
        {
            $"External signals of {sourceName}",
            string.Empty,
            "These are the words and bits the SLC program most likely exchanges with something outside the PLC: an HMI, a SCADA system, or another controller.",
            "The PLC program only shows where data crosses the boundary, not who is on the other side, so each row says why it's listed:",
            "  - copied in from or out to a communication card's data (for example a Modbus or DH-485 card);",
            "  - read by the logic but never written by it (set from outside: buttons, setpoints);",
            "  - written by the logic but never read by it (shown or sent outside: status, messages).",
            "Words only some of whose bits matter, constants set once by the programmer, and leftovers can show up too; tag those Internal or Not used.",
            string.Empty,
            $"The Likely column is the converter's guess. Data copied through a communication card is marked {LikelyScada}: it almost always "
            + "belongs to a SCADA system or another controller. The rest says HMI or SCADA, because an HMI and a SCADA system read and write "
            + "the SLC data table the same way, so the program can't tell them apart.",
            string.Empty,
            "Tag each row in the Belongs to column (HMI, SCADA, Both, Internal, Not used) and add notes as needed.",
            "Converting the same program again into the same output folder keeps your tags and notes.",
        };
        for (var i = 0; i < lines.Length; i++)
        {
            about.Cell(i + 1, 1).Value = lines[i];
            about.Cell(i + 1, 1).Style.Alignment.SetWrapText();
        }

        about.Cell(1, 1).Style.Font.SetBold().Font.SetFontSize(14);

        return ReportFiles.Save(workbook, path);
    }

    private static Dictionary<string, (string BelongsTo, string Notes)> ReadTags(string path)
    {
        var tags = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
        if (!File.Exists(path))
        {
            return tags;
        }

        try
        {
            using var workbook = new XLWorkbook(path);
            if (!workbook.TryGetWorksheet(SheetName, out var sheet))
            {
                return tags;
            }

            // Find the user's columns by header, so workbooks from older versions (other column order) keep their tags.
            int Column(string header, int fallback) =>
                sheet.Row(1).CellsUsed().FirstOrDefault(c => c.GetString().Trim() == header)?.Address.ColumnNumber ?? fallback;
            var belongsToColumn = Column(BelongsToHeader, 5);
            var notesColumn = Column(NotesHeader, 6);

            foreach (var row in sheet.RowsUsed().Skip(1))
            {
                var address = row.Cell(1).GetString().Trim();
                var belongsTo = row.Cell(belongsToColumn).GetString().Trim();
                var notes = row.Cell(notesColumn).GetString().Trim();
                if (address.Length > 0 && (belongsTo.Length > 0 || notes.Length > 0))
                {
                    tags[address] = (belongsTo, notes);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or InvalidOperationException)
        {
            // An unreadable old copy: start fresh rather than fail the conversion.
        }

        return tags;
    }
}
