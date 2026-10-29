using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace TiaPortalTool.Services.Hmi;

/// <summary>Which compiled file of a classic HMI to read names from (both in &lt;project&gt;\IM\HMI\C\&lt;n&gt;).</summary>
public enum HmiRuntimeSource
{
    /// <summary>
    /// Generates\pdata.fwc, the runtime that is downloaded to the device. Every compile rewrites it before the
    /// compile call returns, also without a save, so the import guard reads it before and after the import.
    /// </summary>
    Download,

    /// <summary>
    /// RtData.plf, the compiler's database. TIA only writes it out completely on save/close (right after an unsaved
    /// compile it's a stub), so it shows the last saved compile: good for the export check, not for the import.
    /// </summary>
    Database,
}

/// <summary>
/// The names in a classic WinCC HMI's compiled runtime. Openness exports leave some screen objects out (alarm views
/// and trend views at least), so the compiled runtime is the only place the tool can see them. Both formats are
/// undocumented; these are heuristic reads, worked out on WinCC RT Advanced V20 files:
/// - pdata.fwc: strings are "16-bit length + UTF-16 characters". Every such string that looks like a name counts,
///   texts included (one count per occurrence). Two compiles of the same content give the same counts.
/// - RtData.plf: a screen object is a record "0x27 + 38 hex chars (its runtime ID) + length byte (name length + 1)
///   + name"; a screen, template or pop-up has a header record "length byte (name length + 1) + name + 1E 00 00 00
///   16 00 00 00". The file keeps old copies of records after incremental compiles, so a name is counted once per
///   distinct runtime ID.
/// Names the compiler makes up are skipped in both: references such as "{15732791-10115}", and faceplate parts named
/// "&lt;instance&gt;_&lt;screen&gt;_&lt;id&gt;_&lt;id&gt;", which change when a screen is rebuilt.
/// </summary>
public sealed class HmiRuntimeNames
{
    private static readonly byte[] HeaderMarker = { 0x1E, 0x00, 0x00, 0x00, 0x16, 0x00, 0x00, 0x00 };
    private const int IdLength = 38;

    // The download also holds generated names cut short around the IDs ("..._1_Overview_12345678", "..._12345678901234_").
    private static readonly Regex GeneratedName = new(@"^\{\d+-\d+\}$|_\d{8,}(_\d*)?$", RegexOptions.CultureInvariant);

    private HmiRuntimeNames(string path, HmiRuntimeSource source, DateTime lastWriteUtc, Dictionary<string, int> counts)
    {
        Path = path;
        Source = source;
        LastWriteUtc = lastWriteUtc;
        Counts = counts;
    }

    public string Path { get; }
    public HmiRuntimeSource Source { get; }
    public DateTime LastWriteUtc { get; }

    /// <summary>Name -> how many times the runtime has it (a name can be used on many screens).</summary>
    public IReadOnlyDictionary<string, int> Counts { get; }

    public static string RelativePath(HmiRuntimeSource source) => source == HmiRuntimeSource.Download ? @"Generates\pdata.fwc" : "RtData.plf";

    /// <summary>That file of every compiled classic HMI in the project folder (one IM\HMI\C\&lt;n&gt; folder each).</summary>
    public static IReadOnlyList<string> FindFiles(string projectDirectory, HmiRuntimeSource source)
    {
        var root = System.IO.Path.Combine(projectDirectory, "IM", "HMI", "C");
        return Directory.Exists(root)
            ? Directory.GetDirectories(root).Select(d => System.IO.Path.Combine(d, RelativePath(source))).Where(File.Exists).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList()
            : Array.Empty<string>();
    }

    /// <summary>
    /// The file of the HMI that has these screens/templates: TIA names the IM folders by an internal ID that Openness
    /// doesn't show, so the file that has the most of these names wins. Null when none has any.
    /// </summary>
    public static string? FindFileFor(string projectDirectory, HmiRuntimeSource source, IEnumerable<string> containerNames)
    {
        var names = containerNames.Distinct(StringComparer.Ordinal).ToList();
        string? best = null;
        var bestScore = 0;
        foreach (var file in FindFiles(projectDirectory, source))
        {
            int score;
            if (source == HmiRuntimeSource.Download)
            {
                var counts = Read(file).Counts;
                score = names.Count(counts.ContainsKey);
            }
            else
            {
                var data = ReadShared(file);
                score = names.Count(n => HasHeader(data, n));
            }

            if (score > bestScore)
            {
                best = file;
                bestScore = score;
            }
        }

        return best;
    }

    /// <summary>Reads a pdata.fwc or an RtData.plf (told apart by the extension).</summary>
    public static HmiRuntimeNames Read(string path)
    {
        var data = ReadShared(path);
        var download = path.EndsWith(".fwc", StringComparison.OrdinalIgnoreCase);
        var counts = download ? ReadDownload(data) : ReadDatabase(data);
        return new HmiRuntimeNames(path, download ? HmiRuntimeSource.Download : HmiRuntimeSource.Database, File.GetLastWriteTimeUtc(path), counts);
    }

    private static Dictionary<string, int> ReadDownload(byte[] data)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var i = 0;
        while (i + 2 < data.Length)
        {
            var length = data[i] | data[i + 1] << 8;
            if (length >= 3 && length <= 255 && i + 2 + 2 * length <= data.Length && LooksLikeName(data, i + 2, length))
            {
                var name = Encoding.Unicode.GetString(data, i + 2, 2 * length);
                if (!GeneratedName.IsMatch(name))
                {
                    counts[name] = counts.TryGetValue(name, out var n) ? n + 1 : 1;
                }

                i += 2 + 2 * length;
                continue;
            }

            i++;
        }

        return counts;
    }

    /// <summary>
    /// UTF-16 characters that could be a name or text (letters, digits, spaces, common punctuation), with at least
    /// three ASCII letters in a row. Binary data that happens to fit a length prefix rarely passes this, and two
    /// compiles of the same content give the same result (any string accepted moves where the next one is looked for).
    /// </summary>
    private static bool LooksLikeName(byte[] data, int start, int length)
    {
        var run = 0;
        var hasWord = false;
        for (var k = 0; k < length; k++)
        {
            var c = (char)(data[start + 2 * k] | data[start + 2 * k + 1] << 8);
            if (c is >= 'A' and <= 'Z' or >= 'a' and <= 'z')
            {
                hasWord |= ++run >= 3;
                continue;
            }

            run = 0;
            if (!char.IsLetterOrDigit(c) && c != '_' && " .,&()/:#+%'-[]@$".IndexOf(c) < 0)
            {
                return false;
            }
        }

        return hasWord;
    }

    private static Dictionary<string, int> ReadDatabase(byte[] data)
    {
        var ids = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        for (var i = 0; i + 1 + IdLength + 1 < data.Length; i++)
        {
            if (data[i] != 0x27 || !IsIdAt(data, i + 1))
            {
                continue;
            }

            var lengthAt = i + 1 + IdLength;
            var length = data[lengthAt] - 1;
            if (length <= 0 || length > 126 || lengthAt + 1 + length > data.Length)
            {
                continue;
            }

            var name = DecodeName(data, lengthAt + 1, length);

            // An ID can itself sit where a name goes (records that point at other records).
            if (name is null || GeneratedName.IsMatch(name) || (name.Length == IdLength && IsIdAt(data, lengthAt + 1)))
            {
                continue;
            }

            if (!ids.TryGetValue(name, out var set))
            {
                ids[name] = set = new HashSet<string>(StringComparer.Ordinal);
            }

            set.Add(Encoding.ASCII.GetString(data, i + 1, IdLength));
            i = lengthAt + length;
        }

        return ids.ToDictionary(p => p.Key, p => p.Value.Count, StringComparer.Ordinal);
    }

    private static bool HasHeader(byte[] data, string name)
    {
        var bytes = Encoding.UTF8.GetBytes(name);
        if (bytes.Length + 1 > 255)
        {
            return false;
        }

        var pattern = new byte[1 + bytes.Length + HeaderMarker.Length];
        pattern[0] = (byte)(bytes.Length + 1);
        bytes.CopyTo(pattern, 1);
        HeaderMarker.CopyTo(pattern, 1 + bytes.Length);
        return IndexOf(data, pattern) >= 0;
    }

    private static bool IsIdAt(byte[] data, int start)
    {
        if (start + IdLength > data.Length)
        {
            return false;
        }

        for (var k = start; k < start + IdLength; k++)
        {
            var c = data[k];
            if (!(c is >= (byte)'0' and <= (byte)'9' || c is >= (byte)'A' and <= (byte)'F'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The name as UTF-8, or null when the bytes aren't text (then it wasn't a name record).</summary>
    private static string? DecodeName(byte[] data, int start, int length)
    {
        for (var k = start; k < start + length; k++)
        {
            if (data[k] < 0x20 || data[k] == 0x7F)
            {
                return null;
            }
        }

        try
        {
            return new UTF8Encoding(false, true).GetString(data, start, length);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.GetEncoding(28591).GetString(data, start, length);
        }
    }

    private static int IndexOf(byte[] data, byte[] pattern)
    {
        var first = pattern[0];
        for (var i = Array.IndexOf(data, first); i >= 0 && i <= data.Length - pattern.Length; i = Array.IndexOf(data, first, i + 1))
        {
            var match = true;
            for (var k = 1; k < pattern.Length; k++)
            {
                if (data[i + k] != pattern[k])
                {
                    match = false;
                    break;
                }
            }

            if (match)
            {
                return i;
            }
        }

        return -1;
    }

    // TIA may have the file open; read it without locking it.
    private static byte[] ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var data = new byte[stream.Length];
        var read = 0;
        while (read < data.Length)
        {
            var n = stream.Read(data, read, data.Length - read);
            if (n == 0)
            {
                break;
            }

            read += n;
        }

        return data;
    }
}
