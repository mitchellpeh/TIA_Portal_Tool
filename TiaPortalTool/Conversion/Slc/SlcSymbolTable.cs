using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace TiaPortalTool.Conversion.Slc;

public sealed class SlcSymbol
{
    public string Address { get; set; } = string.Empty;
    public string Symbol { get; set; } = string.Empty;

    /// <summary>The (up to five) description lines joined with spaces.</summary>
    public string Description { get; set; } = string.Empty;
}

/// <summary>
/// Symbols and address descriptions, read from the fixed-width symbol files that Save As .SLC writes (.SY6/.SY5)
/// or from RSLogix 500's database export (.EAS, quoted CSV).
/// </summary>
public sealed class SlcSymbolTable
{
    private readonly Dictionary<SlcAddress, SlcSymbol> _byAddress = new();

    // Program file symbols, from "U:n" rows (the label RSLogix shows on a JSR to file n).
    private readonly Dictionary<int, SlcSymbol> _programFiles = new();

    public string SourcePath { get; private set; } = string.Empty;

    public int Count => _byAddress.Count;

    public IEnumerable<KeyValuePair<SlcAddress, SlcSymbol>> Entries => _byAddress;

    /// <summary>Rows whose address couldn't be understood (e.g. indirect "N63:[N43:58]").</summary>
    public List<string> SkippedAddresses { get; } = new();

    public SlcSymbol? Find(SlcAddress address) => _byAddress.TryGetValue(address, out var symbol) ? symbol : null;

    public SlcSymbol? FindProgramFile(int number) => _programFiles.TryGetValue(number, out var symbol) ? symbol : null;

    /// <summary>
    /// Finds the symbol file next to an .SLC export (same base name), or null. Save As .SLC writes .SY6 and .SY5,
    /// which hold the same data; .SY6 has 20-character description lines where .SY5 cuts them at 15, so it wins.
    /// A database export (.EAS) is used if it's the only one there.
    /// </summary>
    public static string? FindBesideExport(string slcPath)
    {
        var directory = Path.GetDirectoryName(slcPath) ?? ".";
        var baseName = Path.GetFileNameWithoutExtension(slcPath);
        foreach (var extension in new[] { ".SY6", ".SY5", ".EAS" })
        {
            var candidate = Path.Combine(directory, baseName + extension);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    public static SlcSymbolTable Load(string path)
    {
        var table = new SlcSymbolTable { SourcePath = path };
        var lines = File.ReadAllLines(path, Encoding.GetEncoding(1252));
        if (lines.Length > 0 && lines[0].StartsWith("ADDRESS.", StringComparison.Ordinal))
        {
            table.LoadFixedWidth(lines);
        }
        else
        {
            table.LoadCsv(lines);
        }

        return table;
    }

    // .EAS: "address","scope","symbol","desc1",...,"desc5",...
    private void LoadCsv(string[] lines)
    {
        foreach (var line in lines)
        {
            var fields = SplitCsv(line);
            if (fields.Count >= 3)
            {
                Add(fields[0], fields[2], fields.Skip(3).Take(5));
            }
        }
    }

    // .SY5: a header of dotted column names ("ADDRESS....", "SYMBOL.....", "DES 1.....") gives the column positions.
    private void LoadFixedWidth(string[] lines)
    {
        var header = lines[0];
        var columns = Regex.Matches(header, @"(?:DES \d|[A-Z]+)\.*")
            .Cast<Match>()
            .Select(m => (Name: m.Value.TrimEnd('.'), Start: m.Index))
            .ToList();

        string Column(string row, string name)
        {
            var index = columns.FindIndex(c => c.Name == name);
            if (index < 0 || columns[index].Start >= row.Length)
            {
                return string.Empty;
            }

            var end = index + 1 < columns.Count ? columns[index + 1].Start : row.Length;
            return row.Substring(columns[index].Start, Math.Min(end, row.Length) - columns[index].Start).Trim();
        }

        foreach (var row in lines.Skip(1))
        {
            if (row.Trim().Length == 0)
            {
                continue;
            }

            Add(Column(row, "ADDRESS"), Column(row, "SYMBOL"),
                new[] { "DES 1", "DES 2", "DES 3", "DES 4", "DES 5" }.Select(c => Column(row, c)));
        }
    }

    private void Add(string addressText, string symbol, IEnumerable<string> descriptionLines)
    {
        var description = string.Join(" ", descriptionLines.Select(d => d.Trim()).Where(d => d.Length > 0));
        if (symbol.Length == 0 && description.Length == 0)
        {
            return;
        }

        var entry = new SlcSymbol { Address = addressText, Symbol = symbol.Trim(), Description = description };

        var programFile = Regex.Match(addressText, @"^U:(\d+)$");
        if (programFile.Success)
        {
            _programFiles[int.Parse(programFile.Groups[1].Value)] = entry;
            return;
        }

        var address = SlcAddress.TryParse(addressText);
        if (address is null)
        {
            SkippedAddresses.Add(addressText);
            return;
        }

        _byAddress[address] = entry;
    }

    private static List<string> SplitCsv(string line)
    {
        var fields = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    quoted = false;
                }
                else
                {
                    current.Append(c);
                }
            }
            else if (c == '"')
            {
                quoted = true;
            }
            else if (c == ',')
            {
                fields.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        fields.Add(current.ToString());
        return fields;
    }
}
