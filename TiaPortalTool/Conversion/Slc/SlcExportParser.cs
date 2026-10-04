using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace TiaPortalTool.Conversion.Slc;

/// <summary>
/// Reads an RSLogix 500 ASCII export (.SLC): processor and rack, program file names, ladder rungs with their
/// titles and descriptions, and the data tables. Forces are ignored.
/// </summary>
public static class SlcExportParser
{
    private static readonly Regex StartLine = new(@"^START\s+(?<cat>\S+)\s*(?:%\s*(?<desc>.*?)\s*%)?", RegexOptions.Compiled);
    private static readonly Regex SlotLine = new(@"^SLOT\s+(?<slot>\d+)\s+(?<cat>\S+)\s*(?:%\s*(?<desc>.*?)\s*%)?(?<rest>.*)$", RegexOptions.Compiled);
    private static readonly Regex ProjectLine = new("^PROJECT\\s+\"(?<name>[^\"]*)\"", RegexOptions.Compiled);
    private static readonly Regex ProgramNameLine = new("^\\s+(?<num>\\d+)\\s+\"(?<name>[^\"]*)\"\\s*$", RegexOptions.Compiled);
    private static readonly Regex LadderLine = new(@"^LADDER\s+(?<num>\d+)", RegexOptions.Compiled);
    private static readonly Regex RungNumberLine = new(@"^%\s*Rung:\s*(?<num>\d+)\s*%", RegexOptions.Compiled);
    private static readonly Regex DataLine = new(@"^DATA\s+(?<type>[A-Z]+)(?<file>\d*):(?<elem>\d+)\s*$", RegexOptions.Compiled);
    private static readonly Regex WordRow = new(@"^%\s*[A-Z]+\d*:(?<elem>\d+)(?:\.(?<word>\d+))?\s*%(?<values>.*)$", RegexOptions.Compiled);
    private static readonly Regex StructRow = new(@"^(?<ctl>0X[0-9A-F]+)\s+(?<a>-?\d+)\s+(?<b>-?\d+)\s*%\s*[A-Z]+\d*:(?<elem>\d+)\s*%", RegexOptions.Compiled);
    private static readonly Regex StringHeader = new(@"^%\s*ST\d+:(?<elem>\d+)\s*%\s*$", RegexOptions.Compiled);

    private static readonly HashSet<string> DataFileTypes = new(StringComparer.Ordinal) { "B", "N", "F", "L", "T", "C", "R", "ST", "S", "A", "MG", "PD" };

    public static SlcProgram Parse(string path)
    {
        var program = new SlcProgram { SourcePath = path };
        var lines = File.ReadAllLines(path, Encoding.GetEncoding(1252));

        var i = 0;
        while (i < lines.Length)
        {
            var line = lines[i];
            Match m;

            if ((m = StartLine.Match(line)).Success)
            {
                program.Processor = m.Groups["cat"].Value;

                // The description often repeats the catalog number ("Bul.1763     MicroLogix 1100 Series A").
                var description = Regex.Replace(m.Groups["desc"].Value, @"\s+", " ").Trim();
                if (description.StartsWith(program.Processor, StringComparison.Ordinal))
                {
                    description = description.Substring(program.Processor.Length).Trim();
                }

                program.ProcessorDescription = description;
                i++;
            }
            else if ((m = SlotLine.Match(line)).Success)
            {
                program.Slots.Add(new SlcSlot
                {
                    Slot = int.Parse(m.Groups["slot"].Value, CultureInfo.InvariantCulture),
                    Catalog = m.Groups["cat"].Value,
                    Description = m.Groups["desc"].Value.Trim(),
                    InputWords = ReadKeyword(m.Groups["rest"].Value, "SCAN_IN"),
                    OutputWords = ReadKeyword(m.Groups["rest"].Value, "SCAN_OUT")
                });
                i++;
            }
            else if ((m = ProjectLine.Match(line)).Success)
            {
                program.ProjectName = m.Groups["name"].Value;
                i++;
                while (i < lines.Length && (m = ProgramNameLine.Match(lines[i])).Success)
                {
                    var number = int.Parse(m.Groups["num"].Value, CultureInfo.InvariantCulture);
                    GetProgramFile(program, number).Name = m.Groups["name"].Value.Trim();
                    i++;
                }
            }
            else if ((m = LadderLine.Match(line)).Success)
            {
                i = ParseLadder(program, lines, i + 1, GetProgramFile(program, int.Parse(m.Groups["num"].Value, CultureInfo.InvariantCulture)));
            }
            else if ((m = DataLine.Match(line)).Success)
            {
                i = ParseData(program, lines, i + 1, m.Groups["type"].Value, m.Groups["file"].Value, int.Parse(m.Groups["elem"].Value, CultureInfo.InvariantCulture));
            }
            else
            {
                i++;
            }
        }

        return program;
    }

    private static SlcProgramFile GetProgramFile(SlcProgram program, int number)
    {
        if (!program.ProgramFiles.TryGetValue(number, out var file))
        {
            file = new SlcProgramFile { Number = number };
            program.ProgramFiles[number] = file;
        }

        return file;
    }

    private static int ReadKeyword(string text, string keyword)
    {
        var m = Regex.Match(text, keyword + @"\s+(-?\d+)");
        return m.Success ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
    }

    /// <summary>Reads rungs until the next top-level section. Returns the index of the first line not consumed.</summary>
    private static int ParseLadder(SlcProgram program, string[] lines, int i, SlcProgramFile file)
    {
        var rungNumber = file.Rungs.Count;
        SlcRung? lastRung = null;

        while (i < lines.Length)
        {
            var line = lines[i];
            if (IsTopLevelSection(line))
            {
                break;
            }

            Match m;
            if ((m = RungNumberLine.Match(line)).Success)
            {
                rungNumber = int.Parse(m.Groups["num"].Value, CultureInfo.InvariantCulture);
                i++;
            }
            else if (line.StartsWith("SOR", StringComparison.Ordinal))
            {
                // A rung is normally one line, but keep reading if a long one was wrapped.
                var start = i;
                var text = new StringBuilder(line.TrimEnd());
                while (!EndsRung(text.ToString()) && i + 1 < lines.Length && !IsTopLevelSection(lines[i + 1]) && !lines[i + 1].StartsWith("%", StringComparison.Ordinal))
                {
                    i++;
                    text.Append(' ').Append(lines[i].Trim());
                }

                lastRung = new SlcRung { Number = rungNumber, Text = text.ToString(), SourceLine = start + 1 };
                file.Rungs.Add(lastRung);
                rungNumber++;
                i++;
            }
            else if (line.StartsWith("% TITLE/RUNG DESCRIPTION", StringComparison.Ordinal))
            {
                i = ParseRungComment(lines, i + 1, lastRung, program);
            }
            else
            {
                i++;
            }
        }

        return i;
    }

    private static bool EndsRung(string text) => text.EndsWith(" EOR", StringComparison.Ordinal) || text == "SOR EOR";

    /// <summary>
    /// Reads a "TITLE: ... RUNG DESCRIPTION: ... %" block. The block follows the rung it belongs to and ends at
    /// the first line whose last character is '%'.
    /// </summary>
    private static int ParseRungComment(string[] lines, int i, SlcRung? rung, SlcProgram program)
    {
        var title = new List<string>();
        var description = new List<string>();
        List<string>? current = null;

        while (i < lines.Length)
        {
            var line = lines[i];
            var trimmed = line.TrimEnd();
            var isLast = trimmed.EndsWith("%", StringComparison.Ordinal);
            if (isLast)
            {
                trimmed = trimmed.Substring(0, trimmed.Length - 1);
            }

            if (trimmed.StartsWith("TITLE:", StringComparison.Ordinal))
            {
                current = title;
                trimmed = trimmed.Substring("TITLE:".Length);
            }
            else if (trimmed.StartsWith("RUNG DESCRIPTION:", StringComparison.Ordinal))
            {
                current = description;
                trimmed = trimmed.Substring("RUNG DESCRIPTION:".Length);
            }

            if (current is not null && trimmed.Trim().Length > 0)
            {
                current.Add(trimmed.Trim());
            }

            i++;
            if (isLast)
            {
                break;
            }
        }

        if (rung is null)
        {
            program.ParseWarnings.Add($"Line {i}: a rung comment appears before any rung; ignored.");
            return i;
        }

        rung.Title = string.Join(" ", title);
        rung.Description = string.Join(Environment.NewLine, description);
        return i;
    }

    private static bool IsTopLevelSection(string line) =>
        line.StartsWith("LADDER ", StringComparison.Ordinal)
        || line.StartsWith("DATA ", StringComparison.Ordinal)
        || line.StartsWith("FORCE ", StringComparison.Ordinal)
        || line.StartsWith("PROJECT ", StringComparison.Ordinal)
        || line.StartsWith("SLOT ", StringComparison.Ordinal)
        || line.StartsWith("START ", StringComparison.Ordinal);

    private static int ParseData(SlcProgram program, string[] lines, int i, string type, string fileText, int firstElement)
    {
        // I/O sections are per slot ("DATA I:2" holds slot 2); there is no file number in the header.
        if (type is "I" or "O")
        {
            var key = $"{type}:{firstElement}";
            var words = new List<int>();
            program.IoData[key] = words;
            for (; i < lines.Length && !IsSectionBreak(lines[i]); i++)
            {
                var row = WordRow.Match(lines[i].Trim());
                if (row.Success)
                {
                    words.AddRange(SplitValues(row.Groups["values"].Value).Select(ParseInt).Select(v => (int)v));
                }
            }

            return i;
        }

        var number = fileText.Length > 0 ? int.Parse(fileText, CultureInfo.InvariantCulture) : type == "S" ? 2 : 0;
        if (!DataFileTypes.Contains(type))
        {
            program.FunctionFiles.Add(type + number);
            return SkipSection(lines, i);
        }

        var file = new SlcDataFile { Type = type, Number = number };
        program.DataFiles[file.Name] = file;

        switch (file.Kind)
        {
            case SlcFileKind.Timer:
            case SlcFileKind.Counter:
            case SlcFileKind.Control:
                for (; i < lines.Length && !IsSectionBreak(lines[i]); i++)
                {
                    var row = StructRow.Match(lines[i].Trim());
                    if (row.Success)
                    {
                        file.Structures.Add(((int)ParseInt(row.Groups["ctl"].Value), (int)ParseInt(row.Groups["a"].Value), (int)ParseInt(row.Groups["b"].Value)));
                    }
                }

                return i;

            case SlcFileKind.String:
                for (; i < lines.Length && !IsSectionBreak(lines[i]); i++)
                {
                    if (StringHeader.IsMatch(lines[i].Trim()) && i + 1 < lines.Length)
                    {
                        var value = lines[i + 1].Trim();
                        file.Strings.Add(value.Length >= 2 && value[0] == '"' && value[value.Length - 1] == '"' ? value.Substring(1, value.Length - 2) : value);
                        i++;
                    }
                }

                return i;

            case SlcFileKind.Other:
                return SkipSection(lines, i);

            default:
                for (; i < lines.Length && !IsSectionBreak(lines[i]); i++)
                {
                    var row = WordRow.Match(lines[i].Trim());
                    if (!row.Success)
                    {
                        continue;
                    }

                    foreach (var value in SplitValues(row.Groups["values"].Value))
                    {
                        if (file.Kind == SlcFileKind.Float)
                        {
                            file.Reals.Add(value);
                        }
                        else
                        {
                            file.Words.Add(ParseInt(value));
                        }
                    }
                }

                return i;
        }
    }

    private static int SkipSection(string[] lines, int i)
    {
        while (i < lines.Length && !IsSectionBreak(lines[i]))
        {
            i++;
        }

        return i;
    }

    private static bool IsSectionBreak(string line) => line.Trim().Length == 0 || IsTopLevelSection(line);

    private static IEnumerable<string> SplitValues(string text) => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Parses decimal or 0X-prefixed hex as written in the export.</summary>
    public static long ParseInt(string text)
    {
        if (text.StartsWith("0X", StringComparison.OrdinalIgnoreCase))
        {
            return long.Parse(text.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }

        return long.Parse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
    }
}
