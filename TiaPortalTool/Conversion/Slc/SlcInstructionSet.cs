namespace TiaPortalTool.Conversion.Slc;

/// <summary>How an SLC instruction is expected to come across to TIA Portal.</summary>
public enum ConversionSupport
{
    /// <summary>A direct LAD equivalent (XIC → contact, MOV → MOVE...).</summary>
    Direct,

    /// <summary>Converted automatically, but not 1:1 (LIM → two compares, RTO → TONR, MCR → zone condition...).</summary>
    Rewritten,

    /// <summary>Converted to a placeholder network that someone has to finish by hand.</summary>
    Manual,

    /// <summary>Not recognised. The rung can't be parsed until it's added to the table.</summary>
    Unknown
}

public sealed class SlcInstructionInfo
{
    public SlcInstructionInfo(string mnemonic, int operands, int[] writes, ConversionSupport support, string tiaEquivalent)
    {
        Mnemonic = mnemonic;
        Operands = operands;
        Writes = writes;
        Support = support;
        TiaEquivalent = tiaEquivalent;
    }

    public string Mnemonic { get; }

    /// <summary>Fixed operand count, or -1 for CPT (destination plus an expression).</summary>
    public int Operands { get; }

    /// <summary>Indexes of the operands this instruction writes to.</summary>
    public int[] Writes { get; }

    public ConversionSupport Support { get; }

    /// <summary>Short plan for the conversion, shown in the report.</summary>
    public string TiaEquivalent { get; }
}

/// <summary>The SLC 500 / MicroLogix instructions the converter knows, with their operand layout.</summary>
public static class SlcInstructionSet
{
    private static readonly Dictionary<string, SlcInstructionInfo> Table = Build();

    // Branch markers: start, next, end. They take no operands.
    public static bool IsBranchToken(string token) => token is "BST" or "NXB" or "BND";

    public static SlcInstructionInfo? Find(string mnemonic) => Table.TryGetValue(mnemonic, out var info) ? info : null;

    /// <summary>
    /// The operand count for this processor. MicroLogix OSR/OSF take a storage bit and an output bit;
    /// the SLC 500 OSR takes only the storage bit.
    /// </summary>
    public static int OperandCount(SlcInstructionInfo info, bool microLogix) =>
        microLogix && info.Mnemonic is "OSR" or "OSF" ? 2 : info.Operands;

    private static Dictionary<string, SlcInstructionInfo> Build()
    {
        var d = ConversionSupport.Direct;
        var r = ConversionSupport.Rewritten;
        var m = ConversionSupport.Manual;
        var none = Array.Empty<int>();

        var list = new List<SlcInstructionInfo>
        {
            // Bit
            new("XIC", 1, none, d, "Normally open contact"),
            new("XIO", 1, none, d, "Normally closed contact"),
            new("OTE", 1, new[] { 0 }, d, "Coil"),
            new("OTL", 1, new[] { 0 }, d, "Set coil (S)"),
            new("OTU", 1, new[] { 0 }, d, "Reset coil (R)"),
            new("OSR", 1, new[] { 0 }, r, "P_TRIG with the storage bit as edge memory"),
            new("OSF", 2, new[] { 0, 1 }, r, "N_TRIG with the storage bit as edge memory"),
            new("ONS", 1, new[] { 0 }, r, "P_TRIG with the storage bit as edge memory"),

            // Timers and counters
            new("TON", 4, new[] { 0 }, r, "TON (IEC_TIMER in the T DB); preset scaled from the timebase to TIME"),
            new("TOF", 4, new[] { 0 }, r, "TOF (IEC_TIMER in the T DB); preset scaled from the timebase to TIME"),
            new("RTO", 4, new[] { 0 }, r, "TONR (IEC_TIMER in the T DB); RES drives its reset input"),
            new("CTU", 3, new[] { 0 }, r, "CTUD count-up input (IEC_COUNTER in the C DB)"),
            new("CTD", 3, new[] { 0 }, r, "CTUD count-down input (IEC_COUNTER in the C DB)"),
            new("RES", 1, new[] { 0 }, r, "RESET_TIMER / counter reset / control reset"),

            // Compare
            new("EQU", 2, none, d, "CMP =="),
            new("NEQ", 2, none, d, "CMP <>"),
            new("LES", 2, none, d, "CMP <"),
            new("LEQ", 2, none, d, "CMP <="),
            new("GRT", 2, none, d, "CMP >"),
            new("GEQ", 2, none, d, "CMP >="),
            new("LIM", 3, none, r, "IN_RANGE, or two compares when low > high (outside range)"),
            new("MEQ", 3, none, r, "AND with the mask, then CMP =="),

            // Math
            new("ADD", 3, new[] { 2 }, d, "ADD"),
            new("SUB", 3, new[] { 2 }, d, "SUB"),
            new("MUL", 3, new[] { 2 }, d, "MUL"),
            new("DIV", 3, new[] { 2 }, r, "DIV, rounded like the SLC for integers"),
            new("DDV", 2, new[] { 1 }, m, "Double divide (uses the math register)"),
            new("NEG", 2, new[] { 1 }, d, "NEG"),
            new("CLR", 1, new[] { 0 }, d, "MOVE 0"),
            new("SQR", 2, new[] { 1 }, d, "SQRT"),
            new("ABS", 2, new[] { 1 }, d, "ABS"),
            new("SCL", 4, new[] { 3 }, r, "MUL / DIV / ADD (rate per 10000 plus offset)"),
            new("SCP", 6, new[] { 5 }, r, "Linear scale with REAL math (NORM_X / SCALE_X)"),
            new("CPT", -1, new[] { 0 }, r, "CALCULATE box"),
            new("SWP", 2, new[] { 0 }, r, "SWAP on each word"),

            // Logic
            new("AND", 3, new[] { 2 }, d, "AND"),
            new("OR", 3, new[] { 2 }, d, "OR"),
            new("XOR", 3, new[] { 2 }, d, "XOR"),
            new("NOT", 2, new[] { 1 }, d, "INV"),

            // Move and file
            new("MOV", 2, new[] { 1 }, d, "MOVE (converted with ROUND when REAL goes to INT)"),
            new("MVM", 3, new[] { 2 }, r, "(dest AND NOT mask) OR (source AND mask)"),
            new("COP", 3, new[] { 1 }, r, "Block move over the data file layout"),
            new("FLL", 3, new[] { 1 }, r, "Block fill over the data file layout"),
            new("CPW", 3, new[] { 1 }, r, "Block move over the data file layout"),
            new("TOD", 2, new[] { 1 }, r, "Integer to BCD"),
            new("FRD", 2, new[] { 1 }, r, "BCD to integer"),
            new("DCD", 2, new[] { 1 }, r, "DECO"),
            new("ENC", 2, new[] { 1 }, r, "ENCO"),

            // Program control
            new("JSR", 1, none, r, "Call of the FC for that program file"),
            new("SBR", 0, none, r, "Start of subroutine (nothing to convert)"),
            new("RET", 0, none, r, "RET"),
            new("JMP", 1, none, r, "JMP to a label"),
            new("LBL", 1, none, r, "Jump label"),
            new("MCR", 0, none, r, "MCR zone: the zone condition is ANDed into each rung in the zone"),
            new("TND", 0, none, r, "RET from the program file"),
            new("END", 0, none, r, "End of file (nothing to convert)"),
            new("NOP", 0, none, d, "Nothing"),
            new("AFI", 0, none, r, "Always-false contact"),
            new("SUS", 1, none, m, "Suspend (debugging only)"),
            new("INT", 0, none, m, "Start of an interrupt routine"),
            new("IIM", 3, none, m, "Immediate input: use a peripheral (:P) input"),
            new("IOM", 3, none, m, "Immediate output: use a peripheral (:P) output"),
            new("REF", 0, none, m, "I/O refresh"),
            new("STD", 0, none, m, "Disable STI interrupts"),
            new("STE", 0, none, m, "Enable STI interrupts"),
            new("STS", 1, none, m, "Start STI interrupts"),
            new("UID", 0, none, m, "Disable user interrupts"),
            new("UIE", 0, none, m, "Enable user interrupts"),

            // Shift registers, sequencers and FIFO/LIFO (control in an R element)
            new("BSL", 4, new[] { 0, 1 }, r, "Bit shift left over the data file layout"),
            new("BSR", 4, new[] { 0, 1 }, r, "Bit shift right over the data file layout"),
            new("SQO", 6, new[] { 2, 3 }, r, "Sequencer output"),
            new("SQC", 6, new[] { 3 }, r, "Sequencer compare"),
            new("SQL", 5, new[] { 0, 2 }, r, "Sequencer load"),
            new("FFL", 5, new[] { 1, 2 }, r, "FIFO load"),
            new("FFU", 5, new[] { 1, 2 }, r, "FIFO unload"),
            new("LFL", 5, new[] { 1, 2 }, r, "LIFO load"),
            new("LFU", 5, new[] { 1, 2 }, r, "LIFO unload"),

            // Process
            new("PID", 3, new[] { 0, 2 }, m, "PID_Compact, configured by hand from the SLC PID block"),
            new("MSG", 1, none, m, "Communication: replace with TIA communication blocks"),
        };

        return list.ToDictionary(i => i.Mnemonic, StringComparer.Ordinal);
    }
}
