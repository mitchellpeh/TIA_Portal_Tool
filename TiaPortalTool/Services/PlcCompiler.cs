using System.Reflection;

namespace TiaPortalTool.Services;

/// <summary>
/// Whole-program compile of a PLC software container, the same thing TIA's own "Compile All" does.
/// Compiling blocks one at a time breaks on caller/callee ordering, so every path compiles this way.
/// Also compiles an HMI (HmiTarget is ICompilable too); <c>what</c> names it in the messages.
/// </summary>
public static class PlcCompiler
{
    /// <summary>Compiles the whole PLC program (or HMI); returns false on compile errors.</summary>
    public static bool CompileAll(dynamic plcSoftware, Assembly assembly, List<string> messages, IProgress<string>? progress = null, string what = "PLC program") =>
        Compile(plcSoftware, assembly, messages, progress, what).Clean;

    /// <summary>
    /// Compiles the whole PLC program (or HMI) and also returns its errors as a list, so a caller can show them or let
    /// the user decide whether to save anyway. The messages are the same as <see cref="CompileAll"/> writes.
    /// </summary>
    public static CompileOutcome Compile(dynamic plcSoftware, Assembly assembly, List<string> messages, IProgress<string>? progress = null, string what = "PLC program")
    {
        progress?.Report($"Compiling the {what}. This can take a few minutes...");

        var compilableType = assembly.GetType("Siemens.Engineering.Compiler.ICompilable")
            ?? throw new InvalidOperationException("Siemens.Engineering.Compiler.ICompilable type not found.");

        var plcCompilable = DynamicReflectionHelpers.GetService(plcSoftware, compilableType);
        if (plcCompilable is null)
        {
            messages.Add($"The {what} does not support whole-program compilation via ICompilable.");
            return new CompileOutcome(false, what);
        }

        dynamic result;
        using (ProgressHeartbeat.Start(progress, $"compiling the {what}"))
        {
            result = DynamicReflectionHelpers.InvokeMethod(plcCompilable, compilableType, "Compile")!;
        }

        string state = result.State.ToString();

        // With a progress sink the summary shows up as soon as the compile finishes, not with the final results.
        var summary = $"{char.ToUpperInvariant(what[0])}{what.Substring(1)} compile: state = {state}, errors = {result.ErrorCount}, warnings = {result.WarningCount}";
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

            return new CompileOutcome(true, what);
        }

        if (!string.Equals(state, "Error", StringComparison.OrdinalIgnoreCase))
        {
            return new CompileOutcome(true, what);
        }

        if (progress is not null)
        {
            messages.Add("Compiler messages:");
        }

        var outcome = new CompileOutcome(false, what);
        foreach (dynamic message in result.Messages)
        {
            AppendMessageRecursive(messages, message, 1);

            // A top-level leaf is TIA's "Compiling finished (errors: n; ...)" summary, not an error of its own.
            if (message.Messages.Count > 0)
            {
                CollectErrors(outcome.Errors, message, string.Empty);
            }
        }

        return outcome;
    }

    // Same walk as AppendWarnings: the leaves that are errors, each with the nearest block path above it.
    private static void CollectErrors(List<CompileError> errors, dynamic message, string block)
    {
        string path = string.Empty;
        try { path = (string?)message.Path ?? string.Empty; } catch { /* not all message kinds expose Path */ }

        bool isBlock = path.IndexOf('(') >= 0;
        if (message.Messages.Count == 0)
        {
            if (string.Equals(message.State.ToString(), "Error", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add(new CompileError(isBlock ? path : block, isBlock ? string.Empty : path, (string?)message.Description ?? string.Empty));
            }

            return;
        }

        foreach (dynamic child in message.Messages)
        {
            CollectErrors(errors, child, isBlock ? path : block);
        }
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

/// <summary>One compile error. Block = "FB30_Name (FB30)" (empty when TIA names none), Location = "Network 28" or "".</summary>
public sealed class CompileError
{
    public CompileError(string block, string location, string description)
    {
        Block = block;
        Location = location;
        Description = description;
    }

    public string Block { get; }
    public string Location { get; }
    public string Description { get; }

    /// <summary>The block's name without the "(FB30)" part, to compare with the names an import brought in.</summary>
    public string BlockName => Block.IndexOf(" (", StringComparison.Ordinal) is var i and >= 0 ? Block.Substring(0, i) : Block;

    public override string ToString()
    {
        var where = string.Join(", ", new[] { Block, Location }.Where(s => s.Length > 0));
        return where.Length == 0 ? Description : $"{where}: {Description}";
    }
}

/// <summary>The result of a whole-program compile. Not clean with no errors listed = the compile couldn't run at all.</summary>
public sealed class CompileOutcome
{
    public CompileOutcome(bool clean, string what)
    {
        Clean = clean;
        What = what;
    }

    public bool Clean { get; }

    /// <summary>What was compiled, as the messages name it ("PLC program", "HMI &lt;runtime name&gt;").</summary>
    public string What { get; }

    public List<CompileError> Errors { get; } = new();
}
