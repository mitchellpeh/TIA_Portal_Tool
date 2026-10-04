using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Principal;

namespace TiaPortalTool.Services;

public sealed class OpennessDiagnosticsResult
{
    public OpennessDiagnosticsResult(int problemCount, string statusMessage, IReadOnlyList<string> notes)
    {
        ProblemCount = problemCount;
        StatusMessage = statusMessage;
        Notes = notes;
    }

    public int ProblemCount { get; }
    public string StatusMessage { get; }
    public IReadOnlyList<string> Notes { get; }
}

public sealed class OpennessProjectProbe
{
    private const string OpennessGroupName = "Siemens TIA Openness";
    private const string TiaPortalProcessName = "Siemens.Automation.Portal";

    /// <summary>
    /// Checks the environment Openness needs without loading any Siemens assembly. Only one
    /// Siemens.Engineering.dll can ever be loaded per process, so a diagnostic that loaded one
    /// could lock the app onto the wrong version for the rest of the session.
    /// </summary>
    public OpennessDiagnosticsResult Diagnose(string? projectPath)
    {
        var notes = new List<string>();
        var problems = 0;

        notes.Add("Project");
        int? targetVersion = null;
        if (string.IsNullOrWhiteSpace(projectPath))
        {
            notes.Add("  PROBLEM: no project selected.");
            problems++;
        }
        else
        {
            if (File.Exists(projectPath))
            {
                notes.Add($"  File: {projectPath}");
            }
            else
            {
                notes.Add($"  PROBLEM: file not found: {projectPath}");
                problems++;
            }

            targetVersion = TryParseTargetVersion(projectPath!);
            notes.Add(targetVersion.HasValue
                ? $"  Format: TIA Portal V{targetVersion} ({Path.GetExtension(projectPath)})"
                : $"  PROBLEM: can't read a TIA Portal version from the extension '{Path.GetExtension(projectPath)}' (expected .apXX).");
            if (!targetVersion.HasValue)
            {
                problems++;
            }
        }

        notes.Add(string.Empty);
        var candidates = GetCandidateAssemblyPaths(projectPath);
        notes.Add($"Openness assemblies ({candidates.Count} found, in the order they'll be tried)");
        if (candidates.Count == 0)
        {
            notes.Add("  PROBLEM: none found. Install the TIA Portal Openness package for your TIA Portal version.");
            problems++;
        }

        foreach (var path in candidates)
        {
            string version;
            try
            {
                version = AssemblyName.GetAssemblyName(path).Version?.ToString() ?? "unknown";
            }
            catch (Exception ex)
            {
                version = "unreadable: " + ex.Message;
            }

            notes.Add($"  {path} (assembly {version})");
        }

        if (targetVersion.HasValue && candidates.Count > 0 && GetVersionMatchTier(candidates[0], targetVersion.Value) != 0)
        {
            notes.Add($"  PROBLEM: no PublicAPI\\V{targetVersion} assembly found. Install the Openness package for TIA Portal V{targetVersion}.");
            problems++;
        }

        var loaded = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(asm => string.Equals(asm.GetName().Name, "Siemens.Engineering", StringComparison.OrdinalIgnoreCase));
        if (loaded is not null)
        {
            notes.Add($"  Already loaded in this session: {loaded.Location}. Restart the app to switch versions.");
        }

        notes.Add(string.Empty);
        notes.Add("Windows account");
        var inGroup = IsInOpennessGroup();
        if (inGroup == true)
        {
            notes.Add($"  Member of '{OpennessGroupName}'.");
        }
        else if (inGroup == false)
        {
            notes.Add($"  PROBLEM: not a member of '{OpennessGroupName}'. Add your account to that group, then sign out and back in.");
            problems++;
        }
        else
        {
            notes.Add($"  Couldn't check '{OpennessGroupName}' membership.");
        }

        notes.Add(string.Empty);
        notes.Add("Openness firewall");
        var exePath = OpennessFirewall.CurrentExePath;
        notes.Add(OpennessFirewall.IsApproved(exePath, targetVersion) switch
        {
            true => $"  This copy of the app is approved: {exePath}",
            false => "  This copy of the app isn't approved yet (new or rebuilt exe). " + OpennessFirewall.PromptAdvice,
            _ => "  Couldn't check whether this copy of the app is approved."
        });

        notes.Add(string.Empty);
        notes.Add("TIA Portal");
        var running = Process.GetProcessesByName(TiaPortalProcessName);
        notes.Add(running.Length > 0
            ? $"  Running ({running.Length} instance(s)). If the selected project is open there, close it before exporting or importing."
            : "  Not running.");
        foreach (var process in running)
        {
            process.Dispose();
        }

        var status = problems == 0
            ? "Diagnostics: no problems found."
            : $"Diagnostics: {problems} problem(s) found.";
        return new OpennessDiagnosticsResult(problems, status, notes);
    }

    private static bool? IsInOpennessGroup()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            foreach (var sid in identity.Groups ?? new IdentityReferenceCollection())
            {
                try
                {
                    var name = sid.Translate(typeof(NTAccount)).Value;
                    if (name.EndsWith("\\" + OpennessGroupName, StringComparison.OrdinalIgnoreCase)
                        || name.Equals(OpennessGroupName, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
                catch (IdentityNotMappedException)
                {
                    // Orphaned SIDs can't be named; skip them.
                }
            }

            return false;
        }
        catch
        {
            return null;
        }
    }

    public static IReadOnlyList<string> GetCandidateAssemblyPaths(string? projectPath = null)
    {
        var roots = new[]
        {
            @"C:\Program Files\Siemens\Automation",
            @"C:\Program Files (x86)\Siemens\Automation"
        };

        var results = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            foreach (var assemblyPath in Directory.EnumerateFiles(root, "Siemens.Engineering.dll", SearchOption.AllDirectories))
            {
                if (assemblyPath.IndexOf("\\PublicAPI\\", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    results.Add(assemblyPath);
                }
            }
        }

        return PrioritizeAssemblyPaths(results.OrderBy(path => path).ToList(), projectPath);
    }

    public static IReadOnlyList<string> PrioritizeAssemblyPaths(IReadOnlyList<string> candidatePaths, string? projectPath)
    {
        if (string.IsNullOrWhiteSpace(projectPath))
        {
            return candidatePaths;
        }

        var targetVersion = TryParseTargetVersion(projectPath);
        if (!targetVersion.HasValue)
        {
            return candidatePaths;
        }

        return candidatePaths
            .OrderBy(path => GetVersionMatchTier(path, targetVersion.Value))
            .ToList();
    }

    private static int GetVersionMatchTier(string assemblyPath, int targetVersion)
    {
        var normalized = assemblyPath.Replace('\\', '/');
        if (normalized.IndexOf($"/PublicAPI/V{targetVersion}/", StringComparison.OrdinalIgnoreCase) >= 0
            || normalized.IndexOf($"/PublicAPI/V{targetVersion} SP", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return 0;
        }

        if (IsPathForVersion(assemblyPath, targetVersion))
        {
            return 1;
        }

        return 2;
    }

    /// <summary>The newest TIA Portal major version with Openness installed (from "Portal VNN" in the install path), or null.</summary>
    public static int? GetNewestInstalledVersion()
    {
        var versions = GetCandidateAssemblyPaths()
            .Select(path => System.Text.RegularExpressions.Regex.Match(path, @"Portal V(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            .Where(m => m.Success)
            .Select(m => int.Parse(m.Groups[1].Value))
            .ToList();
        return versions.Count == 0 ? null : versions.Max();
    }

    /// <summary>Reads the TIA Portal major version from a ".apNN" project extension.</summary>
    public static int? TryParseTargetVersion(string projectPath)
    {
        var extension = Path.GetExtension(projectPath);
        if (extension.StartsWith(".ap", StringComparison.OrdinalIgnoreCase) && int.TryParse(extension.Substring(3), out var version))
        {
            return version;
        }

        return null;
    }

    private static bool IsPathForVersion(string assemblyPath, int targetVersion)
    {
        var normalized = assemblyPath.Replace('\\', '/');
        return normalized.IndexOf($"/Portal V{targetVersion}/", StringComparison.OrdinalIgnoreCase) >= 0
            || normalized.IndexOf($"/V{targetVersion}/", StringComparison.OrdinalIgnoreCase) >= 0
            || normalized.IndexOf($"/V{targetVersion} SP", StringComparison.OrdinalIgnoreCase) >= 0;
    }
}
