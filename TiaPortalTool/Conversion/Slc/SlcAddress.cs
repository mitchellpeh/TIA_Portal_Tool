using System.Globalization;
using System.Text.RegularExpressions;

namespace TiaPortalTool.Conversion.Slc;

/// <summary>
/// One SLC 500 / MicroLogix operand address, normalized so different spellings of the same bit compare equal
/// (for example "I:1/272" and "I:1.17/0", or "B3/20" and "B3:1/4").
/// </summary>
/// <remarks>
/// For I/O, <see cref="Element"/> is the slot and <see cref="Word"/> the word within the slot.
/// For every other file, <see cref="Element"/> is the element (word, timer, counter...) and <see cref="Word"/> is 0.
/// <see cref="Member"/> is a named sub-element such as PRE, ACC, DN or EN.
/// </remarks>
public sealed class SlcAddress : IEquatable<SlcAddress>
{
    // Standard SLC file numbers for the files whose number may be left out (O:, I:, S:).
    private static readonly Dictionary<string, int> DefaultFileNumbers = new(StringComparer.Ordinal)
    {
        ["O"] = 0, ["I"] = 1, ["S"] = 2
    };

    // TYPE[file]:element[.word|.MEMBER][/bit|/MEMBER], optionally prefixed with # (file/range addressing).
    private static readonly Regex ElementForm = new(
        @"^(?<hash>#)?(?<type>[A-Z]{1,3})(?<file>\d+)?:(?<elem>\d+)(?:\.(?<sub>\d+|[A-Z]+))?(?:/(?<bit>\d+|[A-Z]+))?$",
        RegexOptions.Compiled);

    // TYPE file/bit, the "bit number within the file" form: B3/20 is B3:1/4.
    private static readonly Regex FileBitForm = new(@"^(?<hash>#)?(?<type>[A-Z])(?<file>\d+)/(?<bit>\d+)$", RegexOptions.Compiled);

    private SlcAddress(string fileType, int fileNumber, int element, int word, string? member, int? bit, bool isFileReference)
    {
        FileType = fileType;
        FileNumber = fileNumber;
        Element = element;
        Word = word;
        Member = member;
        Bit = bit;
        IsFileReference = isFileReference;
    }

    public string FileType { get; }
    public int FileNumber { get; }
    public int Element { get; }
    public int Word { get; }
    public string? Member { get; }
    public int? Bit { get; }

    /// <summary>True for "#N7:0" style operands that address a range starting at this element.</summary>
    public bool IsFileReference { get; }

    public bool IsIo => FileType is "I" or "O";

    /// <summary>The data file name, e.g. "N7", "T4", "I1".</summary>
    public string FileName => FileType + FileNumber.ToString(CultureInfo.InvariantCulture);

    /// <summary>The same address without its bit or member, i.e. the word or element it lives in.</summary>
    public SlcAddress WordAddress => new(FileType, FileNumber, Element, Word, null, null, false);

    public static SlcAddress? TryParse(string text)
    {
        if (string.IsNullOrEmpty(text) || text.IndexOf('[') >= 0)
        {
            return null;
        }

        var match = ElementForm.Match(text);
        if (match.Success)
        {
            return FromElementForm(match);
        }

        match = FileBitForm.Match(text);
        if (match.Success)
        {
            var bitInFile = Int(match.Groups["bit"].Value);
            return new SlcAddress(match.Groups["type"].Value, Int(match.Groups["file"].Value), bitInFile / 16, 0, null, bitInFile % 16,
                match.Groups["hash"].Success);
        }

        return null;
    }

    private static SlcAddress? FromElementForm(Match match)
    {
        var type = match.Groups["type"].Value;
        int fileNumber;
        if (match.Groups["file"].Success)
        {
            fileNumber = Int(match.Groups["file"].Value);
        }
        else if (!DefaultFileNumbers.TryGetValue(type, out fileNumber))
        {
            return null;
        }

        var element = Int(match.Groups["elem"].Value);
        var hash = match.Groups["hash"].Success;
        var sub = match.Groups["sub"].Success ? match.Groups["sub"].Value : null;
        var bitText = match.Groups["bit"].Success ? match.Groups["bit"].Value : null;
        int? bit = bitText is not null && char.IsDigit(bitText[0]) ? Int(bitText) : null;
        string? member = bitText is not null && bit is null ? bitText : null;

        if (type is "I" or "O" or "M" or "G")
        {
            // Slot-based files: I:slot.word/bit, or I:slot/bit with the bit counted across the slot.
            var word = sub is not null && char.IsDigit(sub[0]) ? Int(sub) : 0;
            if (sub is null && bit is not null)
            {
                word = bit.Value / 16;
                bit %= 16;
            }

            return new SlcAddress(type, fileNumber, element, word, member, bit, hash);
        }

        // A word member (T4:0.PRE) or a numbered word inside a function-file element (HSC0:0.5): keep it as text.
        member = sub ?? member;

        return new SlcAddress(type, fileNumber, element, 0, member, bit, hash);
    }

    private static int Int(string text) => int.Parse(text, NumberStyles.None, CultureInfo.InvariantCulture);

    public override string ToString()
    {
        var file = FileType is "O" or "I" or "S" && DefaultFileNumbers[FileType] == FileNumber ? FileType : FileName;
        var text = (IsFileReference ? "#" : string.Empty) + file + ":" + Element.ToString(CultureInfo.InvariantCulture);
        if (FileType is "I" or "O" or "M" or "G")
        {
            text += "." + Word.ToString(CultureInfo.InvariantCulture);
        }

        if (Member is not null)
        {
            text += (Bit is null && IsBitMember(Member) ? "/" : ".") + Member;
        }

        if (Bit is not null)
        {
            text += "/" + Bit.Value.ToString(CultureInfo.InvariantCulture);
        }

        return text;
    }

    /// <summary>Timer/counter/control status bits are written with "/" (T4:0/DN); words with "." (T4:0.PRE).</summary>
    public static bool IsBitMember(string member) => member is "EN" or "TT" or "DN" or "CU" or "CD" or "OV" or "UN" or "UA"
        or "EU" or "EM" or "ER" or "UL" or "IN" or "FD";

    public bool Equals(SlcAddress? other) =>
        other is not null && FileType == other.FileType && FileNumber == other.FileNumber && Element == other.Element
        && Word == other.Word && Member == other.Member && Bit == other.Bit && IsFileReference == other.IsFileReference;

    public override bool Equals(object? obj) => Equals(obj as SlcAddress);

    public override int GetHashCode()
    {
        unchecked
        {
            var hash = FileType.GetHashCode();
            hash = hash * 31 + FileNumber;
            hash = hash * 31 + Element;
            hash = hash * 31 + Word;
            hash = hash * 31 + (Member?.GetHashCode() ?? 0);
            hash = hash * 31 + (Bit ?? -1);
            return hash * 31 + (IsFileReference ? 1 : 0);
        }
    }
}
