namespace TiaPortalTool.Conversion.Slc;

/// <summary>
/// What the converter does with each SLC status file (S2) address the logic uses. Most are housekeeping with an
/// automatic equivalent or none needed; only a few need a person.
/// </summary>
public static class SlcStatusFile
{
    /// <summary>S:4 (free-running clock) and S:37-S:42 (real-time clock), kept up to date by SLC_STATUS in Main.</summary>
    public static bool IsMaintainedWord(int element) => element == 4 || element is >= 37 and <= 42;

    /// <summary>S:5/0, the math overflow trap: SLC programs clear it so the processor doesn't fault. TIA never faults on overflow.</summary>
    public static bool IsOverflowTrap(SlcAddress address) => address.FileType == "S" && address.Element == 5 && address.Bit == 0;

    /// <summary>A note for the network comment of a rung that uses this status address, or null if there's nothing to say.</summary>
    public static string? RungNote(SlcAddress address)
    {
        if (address.FileType != "S")
        {
            return null;
        }

        return (address.Element, address.Bit) switch
        {
            (1, 15) => null,
            (4, _) => null,
            ( >= 37 and <= 42, _) => null,
            (5, 0) => "S:5/0 (math overflow trap) is dropped: SLC programs clear it so the processor doesn't fault, and TIA never faults on overflow.",
            (5, 11) => "S:5/11 (battery low) has no S7-1500/1200 equivalent (no backup battery), so this contact never turns on.",
            (6, _) => "S:6 (major fault code) has no TIA equivalent and stays 0. Use the CPU diagnostics if this value is needed.",
            (30, _) or (31, _) or (43, _) => "S:30/S:31/S:43 belong to the SLC's selectable timed interrupt (STI); see the Manual work sheet.",
            _ => $"{address.WordAddress} is an SLC status word; the converted value comes from DB S2 and isn't updated by the CPU. Check what the logic expects of it."
        };
    }

    /// <summary>Items for the Manual work sheet, one per kind of status use that needs a person.</summary>
    public static IEnumerable<string> Warnings(IEnumerable<SlcAddress> references)
    {
        var status = references.Where(a => a.FileType == "S").ToList();
        if (status.Any(a => a.Element is 30 or 31 or 43))
        {
            yield return "The SLC uses a selectable timed interrupt (STI: S:30 setpoint, S:31 file number, S:43 interrupt time). In TIA "
                         + "that's a cyclic interrupt OB: create one that calls the interrupt file's FC and set its cycle time "
                         + "(the SLC set S:30 in milliseconds; this program writes it from logic, so check the value it uses).";
        }

        foreach (var address in status
                     .Where(a => !(a.Element == 1 && a.Bit == 15) && !IsMaintainedWord(a.Element) && a.Element is not (5 or 6 or 30 or 31 or 43))
                     .Select(a => a.ToString()).Distinct().OrderBy(a => a, StringComparer.Ordinal))
        {
            yield return $"The logic uses status file {address}, which isn't updated by the CPU in TIA (it's a word in DB S2). Check what the logic expects of it.";
        }
    }
}
