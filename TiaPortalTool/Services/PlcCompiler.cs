using System.Reflection;

namespace TiaPortalTool.Services;

/// <summary>
/// Whole-program compile of a PLC software container, the same thing TIA's own "Compile All" does.
/// Compiling blocks one at a time breaks on caller/callee ordering, so every path compiles this way.
/// </summary>
public static class PlcCompiler
{
    /// <summary>Compiles the whole PLC program; returns false on compile errors.</summary>
    public static bool CompileAll(dynamic plcSoftware, Assembly assembly, List<string> messages, IProgress<string>? progress = null)
    {
        progress?.Report("Compiling the PLC program. This can take a few minutes...");

        var compilableType = assembly.GetType("Siemens.Engineering.Compiler.ICompilable")
            ?? throw new InvalidOperationException("Siemens.Engineering.Compiler.ICompilable type not found.");

        var plcCompilable = DynamicReflectionHelpers.GetService(plcSoftware, compilableType);
        if (plcCompilable is null)
        {
            messages.Add("PLC software does not support whole-program compilation via ICompilable.");
            return false;
        }

        dynamic result = DynamicReflectionHelpers.InvokeMethod(plcCompilable, compilableType, "Compile")!;
        string state = result.State.ToString();

        // With a progress sink the summary shows up as soon as the compile finishes, not with the final results.
        var summary = $"PLC program compile: state = {state}, errors = {result.ErrorCount}, warnings = {result.WarningCount}";
        if (progress is null)
        {
            messages.Add(summary);
        }
        else
        {
            progress.Report(summary);
        }

        if (string.Equals(state, "Warning", StringComparison.OrdinalIgnoreCase))
        {
            // Only the warnings themselves; the full tree is mostly "compiled successfully" lines.
            messages.Add("Compiler warnings:");
            foreach (dynamic message in result.Messages)
            {
                AppendWarnings(messages, message, string.Empty);
            }

            return true;
        }

        if (!string.Equals(state, "Error", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (progress is not null)
        {
            messages.Add("Compiler messages:");
        }

        foreach (dynamic message in result.Messages)
        {
            AppendMessageRecursive(messages, message, 1);
        }

        return false;
    }

    // Context is the nearest block path above, so a warning that only names a network number says which block.
    private static void AppendWarnings(List<string> messages, dynamic message, string context)
    {
        string path = string.Empty;
        try { path = (string?)message.Path ?? string.Empty; } catch { /* not all message kinds expose Path */ }

        if (string.Equals(message.State.ToString(), "Warning", StringComparison.OrdinalIgnoreCase) && message.Messages.Count == 0)
        {
            var where = string.Join(" ", new[] { context, path }.Where(p => !string.IsNullOrEmpty(p)));
            messages.Add($"    Warning: {message.Description}{(where.Length == 0 ? string.Empty : $" [{where}]")}");
        }

        var childContext = path.IndexOf('(') >= 0 ? path : context;
        foreach (dynamic child in message.Messages)
        {
            AppendWarnings(messages, child, childContext);
        }
    }

    // CompilerResultMessage nests its own Messages (same type as CompilerResult.Messages),
    // so the real per-network detail sits one or more levels below what a flat foreach sees.
    // Without recursing here, errors only ever surface as a generic top-level summary line.
    private static void AppendMessageRecursive(List<string> messages, dynamic message, int depth)
    {
        string indent = new string(' ', depth * 4);
        string path = string.Empty;
        try { path = (string)message.Path; } catch { /* not all message kinds expose Path */ }
        string pathSuffix = string.IsNullOrEmpty(path) ? string.Empty : $" [{path}]";

        messages.Add($"{indent}{message.State}: {message.Description}{pathSuffix}");

        foreach (dynamic child in message.Messages)
        {
            AppendMessageRecursive(messages, child, depth + 1);
        }
    }
}
