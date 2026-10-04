using ClosedXML.Excel;
using System.IO;

namespace TiaPortalTool.Conversion.Slc;

public static class ReportFiles
{
    /// <summary>
    /// Saves a report workbook. If the file is open in a spreadsheet program (which locks it), saves it next to the
    /// original with the time in its name instead, so an open report never stops a conversion. Returns the path used.
    /// </summary>
    public static string Save(XLWorkbook workbook, string path)
    {
        try
        {
            workbook.SaveAs(path);
            return path;
        }
        catch (IOException)
        {
            var alternative = Path.Combine(Path.GetDirectoryName(path)!,
                $"{Path.GetFileNameWithoutExtension(path)} ({DateTime.Now:yyyy-MM-dd HHmmss}){Path.GetExtension(path)}");
            workbook.SaveAs(alternative);
            return alternative;
        }
    }

    /// <summary>Log line for a report that had to be saved under another name.</summary>
    public static string? LockedNote(string intended, string actual) =>
        string.Equals(intended, actual, StringComparison.OrdinalIgnoreCase)
            ? null
            : $"{Path.GetFileName(intended)} is open in another program, so this run's copy is {Path.GetFileName(actual)}. "
              + "Close the old one to have the next run write the usual name.";
}
