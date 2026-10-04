using ClosedXML.Excel;
using System.IO;
using System.Reflection;
using System.Text.Json;

namespace TiaPortalTool.Services;

public sealed class WorkbookReimportService
{
    private const string ManifestFileName = "workbooks_manifest.json";

    public WorkbookReimportResult ReimportAndCompile(dynamic plcSoftware, Assembly assembly, string exportDirectory, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        progress?.Report("Reading the edited workbooks and applying tag comments...");
        var messages = new List<string>();
        var manifestPath = Path.Combine(exportDirectory, ManifestFileName);
        if (!File.Exists(manifestPath))
        {
            return new WorkbookReimportResult
            {
                Success = false,
                Messages = new[] { $"No workbook manifest found at {manifestPath}. Export before reimporting." }
            };
        }

        var manifest = JsonSerializer.Deserialize<List<WorkbookManifestEntry>>(File.ReadAllText(manifestPath)) ?? new();
        bool anyFailure = false;

        var tagExportService = new TagExportService();
        var blockManifestForCompile = new List<BlockManifestEntry>();

        foreach (var group in manifest.GroupBy(e => e.WorkbookPath))
        {
            if (!File.Exists(group.Key))
            {
                messages.Add($"Skipped workbook {group.Key}: file not found.");
                anyFailure = true;
                continue;
            }

            using var workbook = new XLWorkbook(group.Key);
            foreach (var entry in group)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var worksheet = workbook.Worksheets.FirstOrDefault(w => w.Name == entry.SheetName);
                if (worksheet is null)
                {
                    messages.Add($"Skipped {entry.SheetName}: sheet not found in {group.Key}.");
                    anyFailure = true;
                    continue;
                }

                switch (entry.Kind)
                {
                    case "Tag":
                        var tagData = ReadTagSheet(worksheet, entry);
                        messages.AddRange(tagExportService.ApplyEdits(plcSoftware, tagData));
                        break;

                    case "BlockEditable":
                        try
                        {
                            dynamic? blockGroup = BlockReimportService.FindGroupByPath(plcSoftware.BlockGroup, entry.BlockGroupPath ?? string.Empty);
                            dynamic? liveBlock = blockGroup is null ? null : BlockReimportService.FindBlockByName(blockGroup.Blocks, entry.BlockName ?? string.Empty);
                            if (liveBlock is null)
                            {
                                messages.Add($"Skipped {entry.BlockName}: could not locate this block in the live project (renamed/moved/deleted?).");
                                anyFailure = true;
                                break;
                            }

                            // Re-export a fresh snapshot from the live project right before diffing.
                            // The cached XML from a prior export can be stale relative to the live
                            // project if an earlier reimport run got partway through (e.g. wrote this
                            // file, then failed before saving) — diffing against a stale cache produces
                            // false "no change" results forever after, since the cache already matches
                            // whatever you last typed even though the project never actually got it.
                            try
                            {
                                RefreshExportedXml(liveBlock, entry.XmlPath!, assembly);
                            }
                            catch (Exception refreshEx)
                            {
                                // A block can be in TIA Portal's "inconsistent" (stale UDT) state for
                                // reasons that have nothing to do with this run's edits. Failing to
                                // refresh ITS baseline shouldn't block saving real changes made to
                                // other, healthy blocks — just skip change-detection for this one block.
                                messages.Add($"{entry.BlockName}: could not refresh from the live project (likely needs a "
                                    + $"compile in TIA Portal first to resolve a stale block/UDT) — skipping this block this "
                                    + $"run: {Describe(refreshEx)}");
                                break;
                            }

                            if (ApplyBlockSheet(worksheet, entry))
                            {
                                messages.Add($"{entry.BlockName}: change detected in sheet '{entry.SheetName}' — queued for reimport.");
                                blockManifestForCompile.Add(new BlockManifestEntry
                                {
                                    BlockName = entry.BlockName!,
                                    GroupPath = entry.BlockGroupPath!,
                                    XmlPath = entry.XmlPath!
                                });
                            }
                            else
                            {
                                messages.Add($"{entry.BlockName}: no change detected in sheet '{entry.SheetName}' (workbook: {Path.GetFileName(group.Key)}).");
                            }
                        }
                        catch (Exception ex)
                        {
                            messages.Add($"Failed to apply edits for block '{entry.BlockName}': {ex.Message}");
                            anyFailure = true;
                        }
                        break;

                    case "BlockReadOnly":
                        messages.Add($"Skipped {entry.BlockName}: SCL/STL comment editing is not supported yet (read-only preview only).");
                        break;
                }
            }
        }

        if (blockManifestForCompile.Count > 0)
        {
            var blockReimportService = new BlockReimportService();
            var result = blockReimportService.ReimportAndCompile(plcSoftware, assembly, blockManifestForCompile, progress, cancellationToken);
            messages.AddRange(result.Messages);
            if (!result.Success)
            {
                anyFailure = true;
            }
        }
        else
        {
            messages.Add("No block comment/title changes detected — nothing to reimport or recompile.");
        }

        return new WorkbookReimportResult { Success = !anyFailure, Messages = messages };
    }

    private static string Describe(Exception ex) =>
        ex is System.Reflection.TargetInvocationException { InnerException: { } inner } ? inner.Message : ex.Message;

    private static void RefreshExportedXml(dynamic liveBlock, string xmlPath, Assembly assembly)
    {
        var exportOptionsType = assembly.GetType("Siemens.Engineering.ExportOptions")
            ?? throw new InvalidOperationException("Siemens.Engineering.ExportOptions type not found.");
        dynamic withDefaults = Enum.Parse(exportOptionsType, "WithDefaults");

        if (File.Exists(xmlPath))
        {
            // Export() refuses to overwrite an existing file.
            File.Delete(xmlPath);
        }

        liveBlock.Export(new FileInfo(xmlPath), withDefaults);
    }

    private static TagTableData ReadTagSheet(IXLWorksheet worksheet, WorkbookManifestEntry entry)
    {
        var data = new TagTableData
        {
            TableName = entry.TagTableName ?? string.Empty,
            GroupPath = entry.TagGroupPath ?? string.Empty,
            Languages = entry.Languages
        };

        int lastRow = worksheet.LastRowUsed()?.RowNumber() ?? 1;
        for (int row = 2; row <= lastRow; row++)
        {
            string name = worksheet.Cell(row, 1).GetString();
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var comments = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < entry.Languages.Count; i++)
            {
                comments[entry.Languages[i]] = worksheet.Cell(row, 4 + i).GetString();
            }

            data.Rows.Add(new TagRowData { Name = name, Comments = comments });
        }

        return data;
    }

    // Reads columns by the header text actually present in the sheet rather than trusting
    // fixed positions captured at export time, so the parse stays correct even if the user
    // reordered/inserted columns in Excel.
    private static bool ApplyBlockSheet(IXLWorksheet worksheet, WorkbookManifestEntry entry)
    {
        int blockCommentHeaderRow = FindRowWithValue(worksheet, "Language");
        var blockComment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int row = blockCommentHeaderRow + 1; !worksheet.Cell(row, 1).IsEmpty(); row++)
        {
            string lang = worksheet.Cell(row, 1).GetString();
            blockComment[lang] = worksheet.Cell(row, 2).GetString();
        }

        int rungHeaderRow = FindRowWithValue(worksheet, "Rung");
        var titleColumns = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var commentColumns = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int lastColumn = worksheet.Row(rungHeaderRow).LastCellUsed()?.Address.ColumnNumber ?? 1;
        for (int col = 2; col <= lastColumn; col++)
        {
            string header = worksheet.Cell(rungHeaderRow, col).GetString();
            if (header.StartsWith("Title:", StringComparison.OrdinalIgnoreCase))
            {
                titleColumns[header.Substring("Title:".Length)] = col;
            }
            else if (header.StartsWith("Comment:", StringComparison.OrdinalIgnoreCase))
            {
                commentColumns[header.Substring("Comment:".Length)] = col;
            }
        }

        var rungEdits = new List<RungEdit>();
        int lastRow = worksheet.LastRowUsed()?.RowNumber() ?? rungHeaderRow;
        for (int row = rungHeaderRow + 1; row <= lastRow; row++)
        {
            var numberCell = worksheet.Cell(row, 1);
            if (numberCell.IsEmpty())
            {
                continue;
            }

            var edit = new RungEdit { Number = (int)numberCell.GetDouble() };
            foreach (var pair in titleColumns)
            {
                edit.Title[pair.Key] = worksheet.Cell(row, pair.Value).GetString();
            }
            foreach (var pair in commentColumns)
            {
                edit.Comment[pair.Key] = worksheet.Cell(row, pair.Value).GetString();
            }
            rungEdits.Add(edit);
        }

        return BlockXmlEditor.ApplyEdits(entry.XmlPath!, blockComment, rungEdits);
    }

    private static int FindRowWithValue(IXLWorksheet worksheet, string value)
    {
        int lastRow = worksheet.LastRowUsed()?.RowNumber() ?? 1;
        for (int row = 1; row <= lastRow; row++)
        {
            if (string.Equals(worksheet.Cell(row, 1).GetString(), value, StringComparison.OrdinalIgnoreCase))
            {
                return row;
            }
        }
        throw new InvalidOperationException($"Could not locate '{value}' header row in sheet '{worksheet.Name}'.");
    }
}
