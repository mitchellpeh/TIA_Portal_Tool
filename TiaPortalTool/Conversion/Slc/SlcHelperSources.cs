namespace TiaPortalTool.Conversion.Slc;

/// <summary>
/// SCL for the small helper functions the converted ladder calls, each standing in for one SLC instruction that LAD
/// can't express on the converted data files. Parameters use SLC terms (file and element numbers, as in N11:0) so a
/// call reads like the SLC instruction it replaces.
/// <para>
/// The converter ships these as SimaticML exports (Conversion/Slc/Helpers/*.xml, embedded) so they import with fixed
/// numbers into their own group. After changing the SCL here, regenerate them: save <see cref="Helpers"/> to a .scl
/// file and run "OpennessRunner TiaPortalTool.exe scl-export &lt;project&gt; &lt;file.scl&gt; &lt;folder&gt;" on a
/// project that has UDT_TIME_SP, then copy the exported XML into Helpers.
/// </para>
/// </summary>
public static class SlcHelperSources
{
    /// <summary>Helper block names with their fixed FC numbers, in the "SLC Support" group.</summary>
    public static readonly (string Name, int Number)[] Blocks =
    {
        ("SLC_COP_WORD", 9001),
        ("SLC_COP_FLOAT", 9002),
        ("SLC_FLL_WORD", 9003),
        ("SLC_FLL_FLOAT", 9004),
        ("SLC_WORD_READ", 9005),
        ("SLC_WORD_WRITE", 9006),
        ("SLC_TO_TIME", 9007),
        ("SLC_FROM_TIME", 9008),
        ("SLC_SCP", 9009),
        ("SLC_TIME_SP", 9010),
        ("SLC_COP_MIXED", 9011),
        ("SLC_FLOAT_READ", 9012),
        ("SLC_FLOAT_WRITE", 9013),
        ("SLC_BIT_READ", 9014),
        ("SLC_BIT_SET", 9015),
        ("SLC_BIT_RESET", 9016),
        ("SLC_MVM", 9017),
        ("SLC_STATUS", 9018),
    };

    public const string Helpers = """
FUNCTION "SLC_COP_WORD" : Void
TITLE = SLC COP (copy file) for B and N files
{ S7_Optimized_Access := 'TRUE' }
VERSION : 0.1
// Replaces COP #SrcFile:SrcElement #DstFile:DstElement Length for bit and integer files.
// The converted data file DBs are numbered like the SLC files (N11 is DB11) and keep the SLC memory layout,
// 2 bytes per element, so the copy is a plain block copy between the two DBs.
   VAR_INPUT
      SrcFile : DInt;   // Source data file number (11 for #N11:0)
      SrcElement : DInt;   // First source element (0 for #N11:0)
      DstFile : DInt;   // Destination data file number
      DstElement : DInt;   // First destination element
      Length : DInt;   // Number of elements to copy
   END_VAR

BEGIN
	// 16#84 is the DB memory area; each element is 2 bytes.
	POKE_BLK(area_src := 16#84, dbNumber_src := #SrcFile, byteOffset_src := #SrcElement * 2,
	         area_dest := 16#84, dbNumber_dest := #DstFile, byteOffset_dest := #DstElement * 2,
	         count := #Length * 2);
END_FUNCTION

FUNCTION "SLC_COP_FLOAT" : Void
TITLE = SLC COP (copy file) for F files
{ S7_Optimized_Access := 'TRUE' }
VERSION : 0.1
// Replaces COP #SrcFile:SrcElement #DstFile:DstElement Length for floating point files (4 bytes per element).
   VAR_INPUT
      SrcFile : DInt;   // Source data file number (8 for #F8:0)
      SrcElement : DInt;   // First source element
      DstFile : DInt;   // Destination data file number
      DstElement : DInt;   // First destination element
      Length : DInt;   // Number of elements to copy
   END_VAR

BEGIN
	// 16#84 is the DB memory area; each element is 4 bytes.
	POKE_BLK(area_src := 16#84, dbNumber_src := #SrcFile, byteOffset_src := #SrcElement * 4,
	         area_dest := 16#84, dbNumber_dest := #DstFile, byteOffset_dest := #DstElement * 4,
	         count := #Length * 4);
END_FUNCTION

FUNCTION "SLC_COP_MIXED" : Void
TITLE = SLC COP between a float file and a B/N file
{ S7_Optimized_Access := 'TRUE' }
VERSION : 0.1
// Replaces a COP between a floating point file and a bit/integer file, which copies the raw words without
// converting the values (typically to pass floats through integer registers to a SCADA or other system).
// Length counts destination elements, as in the SLC. Note that TIA stores the high word of a float first;
// check that the receiver expects that word order.
   VAR_INPUT 
      SrcFile : DInt;   // Source data file number
      SrcElement : DInt;   // First source element
      SrcIsFloat : Bool;   // TRUE if the source is an F file (4 bytes per element)
      DstFile : DInt;   // Destination data file number
      DstElement : DInt;   // First destination element
      DstIsFloat : Bool;   // TRUE if the destination is an F file (4 bytes per element)
      Length : DInt;   // Number of destination elements
   END_VAR

   VAR_TEMP 
      srcSize : DInt;
      dstSize : DInt;
   END_VAR

BEGIN
	// Element sizes: 4 bytes for a float, 2 for a bit/integer word.
	IF #SrcIsFloat THEN #srcSize := 4; ELSE #srcSize := 2; END_IF;
	IF #DstIsFloat THEN #dstSize := 4; ELSE #dstSize := 2; END_IF;
	
	// 16#84 is the DB memory area.
	POKE_BLK(area_src := 16#84, dbNumber_src := #SrcFile, byteOffset_src := #SrcElement * #srcSize,
	         area_dest := 16#84, dbNumber_dest := #DstFile, byteOffset_dest := #DstElement * #dstSize,
	         count := #Length * #dstSize);
END_FUNCTION

FUNCTION "SLC_FLL_WORD" : Void
TITLE = SLC FLL (fill file) for B and N files
{ S7_Optimized_Access := 'TRUE' }
VERSION : 0.1
// Replaces FLL Value #DstFile:DstElement Length for bit and integer files: the same value into each element.
   VAR_INPUT
      Value : Int;   // Value to fill with
      DstFile : DInt;   // Destination data file number
      DstElement : DInt;   // First element to fill
      Length : DInt;   // Number of elements to fill
   END_VAR

   VAR_TEMP
      i : DInt;
   END_VAR

BEGIN
	FOR #i := 0 TO #Length - 1 DO
	    // 16#84 is the DB memory area; each element is 2 bytes.
	    POKE(area := 16#84, dbNumber := #DstFile, byteOffset := (#DstElement + #i) * 2, value := #Value);
	END_FOR;
END_FUNCTION

FUNCTION "SLC_FLL_FLOAT" : Void
TITLE = SLC FLL (fill file) for F files
{ S7_Optimized_Access := 'TRUE' }
VERSION : 0.1
// Replaces FLL Value #DstFile:DstElement Length for floating point files.
   VAR_INPUT
      Value : Real;   // Value to fill with
      DstFile : DInt;   // Destination data file number
      DstElement : DInt;   // First element to fill
      Length : DInt;   // Number of elements to fill
   END_VAR

   VAR_TEMP
      i : DInt;
   END_VAR

BEGIN
	FOR #i := 0 TO #Length - 1 DO
	    // 16#84 is the DB memory area; each element is 4 bytes.
	    POKE(area := 16#84, dbNumber := #DstFile, byteOffset := (#DstElement + #i) * 4, value := #Value);
	END_FOR;
END_FUNCTION

FUNCTION "SLC_WORD_READ" : Int
TITLE = Read a whole word of a bit file
{ S7_Optimized_Access := 'TRUE' }
VERSION : 0.1
// Reads B<File>:<Element> as a number, for SLC logic that used a whole word of a bit file (MOV B3:16 N7:0,
// EQU B3:5 0, ...). The 16 bits are named members of the DB, laid out so the word reads exactly as in the SLC.
   VAR_INPUT
      File : DInt;   // Bit file number (3 for B3)
      Element : DInt;   // Word number (16 for B3:16)
   END_VAR

BEGIN
	// 16#84 is the DB memory area; each word is 2 bytes.
	#SLC_WORD_READ := WORD_TO_INT(PEEK_WORD(area := 16#84, dbNumber := #File, byteOffset := #Element * 2));
END_FUNCTION

FUNCTION "SLC_WORD_WRITE" : Void
TITLE = Write a whole word of a bit file
{ S7_Optimized_Access := 'TRUE' }
VERSION : 0.1
// Writes B<File>:<Element> as a number, for SLC logic that wrote a whole word of a bit file (MOV N7:100 B3:16,
// CLR B3:5, ...). All 16 named bits of that word change together, as in the SLC.
   VAR_INPUT
      File : DInt;   // Bit file number (3 for B3)
      Element : DInt;   // Word number (16 for B3:16)
      Value : Int;   // New value of the word
   END_VAR

BEGIN
	// 16#84 is the DB memory area; each word is 2 bytes.
	POKE(area := 16#84, dbNumber := #File, byteOffset := #Element * 2, value := INT_TO_WORD(#Value));
END_FUNCTION

FUNCTION "SLC_FLOAT_READ" : Real
TITLE = Read a float by file and element number
{ S7_Optimized_Access := 'TRUE' }
VERSION : 0.1
// Reads F<File>:<Element> where the element (or file) number comes from another word, as in the SLC indirect
// address F90:[N7:55].
   VAR_INPUT 
      File : DInt;   // Float file number (90 for F90)
      Element : DInt;   // Element number
   END_VAR

BEGIN
	// 16#84 is the DB memory area; each float is 4 bytes.
	#SLC_FLOAT_READ := DWORD_TO_REAL(PEEK_DWORD(area := 16#84, dbNumber := #File, byteOffset := #Element * 4));
END_FUNCTION

FUNCTION "SLC_FLOAT_WRITE" : Void
TITLE = Write a float by file and element number
{ S7_Optimized_Access := 'TRUE' }
VERSION : 0.1
// Writes F<File>:<Element> where the element (or file) number comes from another word, as in the SLC indirect
// address F90:[N7:55].
   VAR_INPUT 
      File : DInt;   // Float file number (90 for F90)
      Element : DInt;   // Element number
      Value : Real;   // New value
   END_VAR

BEGIN
	// 16#84 is the DB memory area; each float is 4 bytes.
	POKE(area := 16#84, dbNumber := #File, byteOffset := #Element * 4, value := REAL_TO_DWORD(#Value));
END_FUNCTION

FUNCTION "SLC_BIT_READ" : Bool
TITLE = Read a bit by file, element and bit number
{ S7_Optimized_Access := 'TRUE' }
VERSION : 0.1
// Reads B<File>:<Element>/<Bit> (or a bit of an N word) where a number comes from another word, as in the SLC
// indirect address B3:70/[N7:200]. Bit numbers above 15 carry on into the following words, as in the SLC.
   VAR_INPUT 
      File : DInt;   // Data file number (3 for B3)
      Element : DInt;   // Word number
      Bit : DInt;   // Bit number
   END_VAR

BEGIN
	// Words are stored high byte first, so bits 8-15 are in the first byte and bits 0-7 in the second.
	#SLC_BIT_READ := PEEK_BOOL(area := 16#84, dbNumber := #File,
	                           byteOffset := (#Element + #Bit / 16) * 2 + SEL(G := (#Bit MOD 16) >= 8, IN0 := 1, IN1 := 0),
	                           bitOffset := #Bit MOD 8);
END_FUNCTION

FUNCTION "SLC_BIT_SET" : Void
TITLE = Set (latch) a bit by file, element and bit number
{ S7_Optimized_Access := 'TRUE' }
VERSION : 0.1
// Replaces OTL on an indirect bit address such as B3:70/[N7:200].
   VAR_INPUT 
      File : DInt;   // Data file number (3 for B3)
      Element : DInt;   // Word number
      Bit : DInt;   // Bit number
   END_VAR

BEGIN
	// Words are stored high byte first, so bits 8-15 are in the first byte and bits 0-7 in the second.
	POKE_BOOL(area := 16#84, dbNumber := #File,
	          byteOffset := (#Element + #Bit / 16) * 2 + SEL(G := (#Bit MOD 16) >= 8, IN0 := 1, IN1 := 0),
	          bitOffset := #Bit MOD 8, value := TRUE);
END_FUNCTION

FUNCTION "SLC_BIT_RESET" : Void
TITLE = Reset (unlatch) a bit by file, element and bit number
{ S7_Optimized_Access := 'TRUE' }
VERSION : 0.1
// Replaces OTU on an indirect bit address such as B3:70/[N7:200].
   VAR_INPUT 
      File : DInt;   // Data file number (3 for B3)
      Element : DInt;   // Word number
      Bit : DInt;   // Bit number
   END_VAR

BEGIN
	// Words are stored high byte first, so bits 8-15 are in the first byte and bits 0-7 in the second.
	POKE_BOOL(area := 16#84, dbNumber := #File,
	          byteOffset := (#Element + #Bit / 16) * 2 + SEL(G := (#Bit MOD 16) >= 8, IN0 := 1, IN1 := 0),
	          bitOffset := #Bit MOD 8, value := FALSE);
END_FUNCTION

FUNCTION "SLC_MVM" : Int
TITLE = SLC MVM (masked move)
{ S7_Optimized_Access := 'TRUE' }
VERSION : 0.1
// Replaces MVM Source Mask Dest: the bits of Source where Mask is 1 replace those bits of Dest; the other bits of
// Dest are kept. Returns the new value of Dest.
   VAR_INPUT 
      Source : Int;   // Value to take the masked bits from
      Mask : Int;   // 1 = take this bit from Source, 0 = keep it from Dest
      Dest : Int;   // Current value of the destination
   END_VAR

BEGIN
	#SLC_MVM := WORD_TO_INT((INT_TO_WORD(#Dest) AND NOT INT_TO_WORD(#Mask)) OR (INT_TO_WORD(#Source) AND INT_TO_WORD(#Mask)));
END_FUNCTION

FUNCTION "SLC_STATUS" : Void
TITLE = SLC status file clocks
{ S7_Optimized_Access := 'TRUE' }
VERSION : 0.1
// Keeps the SLC status words that the logic reads as clocks up to date, so rungs such as XIC S:4/7 or NEQ S:42 N7:2
// work exactly as in the SLC without any CPU settings. Called at the start of Main.
   VAR_OUTPUT
      FreeRunningClock : Int;   // S:4: counts up every 10 ms and wraps at 16 bits, like the SLC's
      Year : Int;   // S:37
      Month : Int;   // S:38
      Day : Int;   // S:39
      Hour : Int;   // S:40
      Minute : Int;   // S:41
      Second : Int;   // S:42
   END_VAR

   VAR_TEMP
      now : DTL;
      status : Int;
   END_VAR

BEGIN
	// The CPU's millisecond tick counter, in 10 ms counts, keeping only the low 16 bits as the SLC does.
	#FreeRunningClock := WORD_TO_INT(DINT_TO_WORD(TIME_TO_DINT(TIME_TCK()) / 10));

	// The CPU's local date and time.
	#status := RD_LOC_T(#now);
	#Year := UINT_TO_INT(#now.YEAR);
	#Month := USINT_TO_INT(#now.MONTH);
	#Day := USINT_TO_INT(#now.DAY);
	#Hour := USINT_TO_INT(#now.HOUR);
	#Minute := USINT_TO_INT(#now.MINUTE);
	#Second := USINT_TO_INT(#now.SECOND);
END_FUNCTION

FUNCTION "SLC_TO_TIME" : Time
TITLE = SLC timer counts to TIME
{ S7_Optimized_Access := 'TRUE' }
VERSION : 0.1
// Converts an SLC timer value (counts of the timer's timebase) to a TIA TIME.
// Used where SLC logic moved a calculated word into a timer preset (MOV N7:x T4:y.PRE).
   VAR_INPUT
      Counts : Int;   // Value in timebase counts, as the SLC stored it
      BaseMs : DInt;   // Timebase in milliseconds: 1000 for 1.0 s, 10 for 0.01 s, 1 for 0.001 s
   END_VAR

BEGIN
	// TIME counts milliseconds, so the counts only need scaling by the timebase.
	#SLC_TO_TIME := DINT_TO_TIME(INT_TO_DINT(#Counts) * #BaseMs);
END_FUNCTION

FUNCTION "SLC_FROM_TIME" : Int
TITLE = TIME to SLC timer counts
{ S7_Optimized_Access := 'TRUE' }
VERSION : 0.1
// Converts a timer's PT or ET back to counts of the SLC timebase, for logic that used .PRE or .ACC as a number
// in math or moves ("time remaining" displays and the like).
   VAR_INPUT
      Value : Time;   // Timer preset (PT) or elapsed time (ET)
      BaseMs : DInt;   // Timebase in milliseconds: 1000 for 1.0 s, 10 for 0.01 s, 1 for 0.001 s
   END_VAR

   VAR_TEMP
      counts : DInt;
   END_VAR

BEGIN
	// Whole counts, truncated like the SLC accumulator.
	#counts := TIME_TO_DINT(#Value) / #BaseMs;

	// SLC timer words are 16-bit; keep the result in that range instead of overflowing.
	IF #counts > 32767 THEN
	    #counts := 32767;
	END_IF;

	#SLC_FROM_TIME := DINT_TO_INT(#counts);
END_FUNCTION

FUNCTION "SLC_SCP" : Real
TITLE = SLC SCP (scale with parameters)
{ S7_Optimized_Access := 'TRUE' }
VERSION : 0.1
// Replaces the SLC SCP instruction: linear scaling of Value from InMin..InMax to ScaledMin..ScaledMax.
// Like the SLC, it extrapolates outside the input range rather than clamping.
   VAR_INPUT
      Value : Real;   // Value to scale
      InMin : Real;   // Input value that maps to ScaledMin
      InMax : Real;   // Input value that maps to ScaledMax
      ScaledMin : Real;   // Output at InMin
      ScaledMax : Real;   // Output at InMax
   END_VAR

BEGIN
	IF #InMax = #InMin THEN
	    // The SLC flags this as a math overflow; return the low end instead of dividing by zero.
	    #SLC_SCP := #ScaledMin;
	ELSE
	    #SLC_SCP := (#Value - #InMin) * (#ScaledMax - #ScaledMin) / (#InMax - #InMin) + #ScaledMin;
	END_IF;
END_FUNCTION

FUNCTION "SLC_TIME_SP" : Int
TITLE = Timer setpoint from hours, minutes and seconds
{ S7_Optimized_Access := 'TRUE' }
VERSION : 0.1
// Adds up an HMI timer setpoint entered as hours, minutes and/or seconds into Output_Seconds (TIME), which the
// timers use as their preset. Returns the same value in counts of the SLC timebase so logic that still reads the
// original SLC word keeps seeing the right number.
   VAR_INPUT
      BaseMs : DInt;   // Timebase of the timers using this setpoint, in milliseconds (1000 for 1.0 s)
   END_VAR

   VAR_IN_OUT
      SP : "UDT_TIME_SP";   // The setpoint object
   END_VAR

   VAR_TEMP
      totalMs : Real;
   END_VAR

BEGIN
	// Any of the three can be left at 0, so the HMI can show only the fields that suit the process.
	#totalMs := (#SP.Input_Hours * 3600.0 + #SP.Input_Mins * 60.0 + #SP.Input_Secs) * 1000.0;

	// Keep it inside the TIME range (0 to about 24.8 days).
	IF #totalMs < 0.0 THEN
	    #totalMs := 0.0;
	ELSIF #totalMs > 2147483647.0 THEN
	    #totalMs := 2147483647.0;
	END_IF;

	#SP.Output_Seconds := DINT_TO_TIME(REAL_TO_DINT(#totalMs));

	// The same value in timebase counts, limited to the 16-bit range of the SLC word.
	IF #totalMs / DINT_TO_REAL(#BaseMs) > 32767.0 THEN
	    #SLC_TIME_SP := 32767;
	ELSE
	    #SLC_TIME_SP := REAL_TO_INT(#totalMs / DINT_TO_REAL(#BaseMs));
	END_IF;
END_FUNCTION

""";
}
