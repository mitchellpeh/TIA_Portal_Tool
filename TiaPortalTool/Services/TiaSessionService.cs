using System.IO;
using System.Reflection;

namespace TiaPortalTool.Services;

public sealed class TiaSessionOpenException : Exception
{
    public TiaSessionOpenException(string message, Exception? inner = null) : base(message, inner)
    {
    }
}

public sealed class TiaSession : IDisposable
{
    private readonly dynamic _tiaPortal;
    private readonly ResolveEventHandler? _resolveHandler;

    internal TiaSession(dynamic tiaPortal, dynamic project, string assemblyPath, string assemblyVersion, ResolveEventHandler? resolveHandler)
    {
        _tiaPortal = tiaPortal;
        Project = project;
        AssemblyPath = assemblyPath;
        AssemblyVersion = assemblyVersion;
        _resolveHandler = resolveHandler;
    }

    public dynamic Project { get; }

    public string AssemblyPath { get; }

    public string AssemblyVersion { get; }

    public void Save()
    {
        Project.Save();
    }

    public void Dispose()
    {
        try
        {
            Project.Close();
        }
        catch
        {
            // Best-effort close; the TiaPortal instance is disposed regardless.
        }

        try
        {
            _tiaPortal.Dispose();
        }
        catch
        {
            // Nothing further we can do if the process-level cleanup fails.
        }

        if (_resolveHandler is not null)
        {
            AppDomain.CurrentDomain.AssemblyResolve -= _resolveHandler;
        }
    }
}

public sealed class TiaSessionService
{
    /// <summary>Progress steps that can sit waiting on TIA Portal (and its Openness access prompt).</summary>
    public const string StartingStep = "Starting TIA Portal in the background";
    public const string OpeningStep = "Opening project";

    public TiaSession Open(string projectPath, IProgress<string>? progress = null)
    {
        if (string.IsNullOrWhiteSpace(projectPath))
        {
            throw new TiaSessionOpenException("No project path supplied.");
        }

        if (!File.Exists(projectPath))
        {
            throw new TiaSessionOpenException($"Project file does not exist: {projectPath}");
        }

        return Start(projectPath, progress, (tiaPortal, assemblyPath) =>
        {
            progress?.Report($"{OpeningStep} {Path.GetFileName(projectPath)}...");
            try
            {
                return tiaPortal.Projects.Open(new FileInfo(projectPath));
            }
            catch (Exception openEx)
            {
                throw new TiaSessionOpenException(
                    $"Could not open the project using {assemblyPath}. If it is currently open in the TIA Portal GUI, close it there first and try again. Details: {Describe(openEx)}",
                    openEx);
            }
        });
    }

    /// <summary>
    /// Creates an empty project &lt;directory&gt;\&lt;name&gt;\&lt;name&gt;.apNN with the newest TIA Portal installed
    /// (TIA Portal always creates a project in its own folder named after the project).
    /// </summary>
    public TiaSession Create(string directory, string name, IProgress<string>? progress = null)
    {
        var version = OpennessProjectProbe.GetNewestInstalledVersion()
            ?? throw new TiaSessionOpenException("No TIA Portal Openness installation was found on this machine.");
        var projectFolder = Path.Combine(directory, name);
        if (Directory.Exists(projectFolder) && Directory.EnumerateFileSystemEntries(projectFolder).Any())
        {
            throw new TiaSessionOpenException($"{projectFolder} already exists and isn't empty. Pick another project name or folder.");
        }

        Directory.CreateDirectory(directory);

        // The path only steers the choice of Openness assembly towards that TIA Portal version.
        var versionHint = Path.Combine(projectFolder, name + ".ap" + version);
        return Start(versionHint, progress, (tiaPortal, assemblyPath) =>
        {
            progress?.Report($"Creating project {name} (TIA Portal V{version}) in {directory}...");
            try
            {
                return tiaPortal.Projects.Create(new DirectoryInfo(directory), name);
            }
            catch (Exception createEx)
            {
                throw new TiaSessionOpenException($"Could not create the project using {assemblyPath}. Details: {Describe(createEx)}", createEx);
            }
        });
    }

    /// <summary>Starts TIA Portal headless with the Openness assembly that best fits <paramref name="projectPath"/>, then opens or creates the project.</summary>
    private TiaSession Start(string projectPath, IProgress<string>? progress, Func<dynamic, string, object> openProject)
    {
        progress?.Report("Finding the TIA Portal Openness installation...");
        var candidateAssemblies = OpennessProjectProbe.GetCandidateAssemblyPaths(projectPath);
        if (candidateAssemblies.Count == 0)
        {
            throw new TiaSessionOpenException("No Siemens Openness assemblies were found on this machine.");
        }

        // Each TIA Portal install keeps Openness's dependent assemblies (e.g.
        // Siemens.Engineering.Contract.dll) in "Portal VXX\Bin\PublicAPI" — a
        // sibling of "Portal VXX\PublicAPI\VYY" where Siemens.Engineering.dll
        // itself lives. .NET never probes sibling folders automatically, so
        // without this resolver, constructing TiaPortal fails with a
        // MissingMethod/FileLoad error against Siemens.Engineering.Contract.
        // Optional Openness parts such as Siemens.Engineering.Hmi.dll sit next to Siemens.Engineering.dll in
        // "PublicAPI\VYY", so that folder is probed too (needed as soon as HMI objects are touched).
        var probeDirectories = candidateAssemblies
            .SelectMany(path => new[] { GetBinPublicApiDirectory(path), Path.GetDirectoryName(path) ?? string.Empty })
            .Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var targetVersion = OpennessProjectProbe.TryParseTargetVersion(projectPath);
        progress?.Report("Checking whether TIA Portal's Openness firewall has approved this app...");
        if (OpennessFirewall.IsApproved(OpennessFirewall.CurrentExePath, targetVersion) == false)
        {
            progress?.Report("Warning: this copy of the app hasn't been approved in TIA Portal's Openness firewall yet. " + OpennessFirewall.PromptAdvice);
        }

        ResolveEventHandler resolveHandler = (_, args) => ResolveFromProbeDirectories(args.Name, probeDirectories);
        AppDomain.CurrentDomain.AssemblyResolve += resolveHandler;

        var diagnostics = new List<string>();
        var opened = false;

        try
        {
            foreach (var assemblyPath in candidateAssemblies)
            {
                try
                {
                    var assembly = Assembly.LoadFrom(assemblyPath);
                    var tiaPortalType = assembly.GetType("Siemens.Engineering.TiaPortal", throwOnError: false);
                    var modeType = assembly.GetType("Siemens.Engineering.TiaPortalMode", throwOnError: false);
                    if (tiaPortalType is null || modeType is null)
                    {
                        diagnostics.Add($"{assemblyPath}: loaded, but TiaPortal/TiaPortalMode type not found in it.");
                        continue;
                    }

                    var withoutUserInterface = Enum.Parse(modeType, "WithoutUserInterface");
                    progress?.Report($"{StartingStep} (Openness {assembly.GetName().Version})...");
                    dynamic tiaPortal;
                    try
                    {
                        tiaPortal = Activator.CreateInstance(tiaPortalType, withoutUserInterface)!;
                    }
                    catch (Exception ctorEx)
                    {
                        diagnostics.Add($"{assemblyPath}: failed to construct TiaPortal instance: {Describe(ctorEx)}");
                        continue;
                    }

                    dynamic project;
                    try
                    {
                        project = openProject(tiaPortal, assemblyPath);
                    }
                    catch
                    {
                        tiaPortal.Dispose();
                        throw;
                    }

                    var assemblyVersion = assembly.GetName().Version?.ToString() ?? "unknown";
                    opened = true;
                    return new TiaSession(tiaPortal, project, assemblyPath, assemblyVersion, resolveHandler);
                }
                catch (TiaSessionOpenException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    diagnostics.Add($"{assemblyPath}: {Describe(ex)}");
                }
            }

            var detail = diagnostics.Count > 0
                ? " Details:\n" + string.Join("\n", diagnostics)
                : string.Empty;

            throw new TiaSessionOpenException(
                "None of the candidate Openness assemblies could start TIA Portal. If you declined TIA Portal's "
                + "Openness access prompt, run the action again and choose Yes." + detail);
        }
        finally
        {
            if (!opened)
            {
                AppDomain.CurrentDomain.AssemblyResolve -= resolveHandler;
            }
        }
    }

    private static string GetBinPublicApiDirectory(string siemensEngineeringDllPath)
    {
        var apiVersionDir = Path.GetDirectoryName(siemensEngineeringDllPath);
        var publicApiDir = apiVersionDir is null ? null : Path.GetDirectoryName(apiVersionDir);
        var portalRoot = publicApiDir is null ? null : Path.GetDirectoryName(publicApiDir);
        return portalRoot is null ? string.Empty : Path.Combine(portalRoot, "Bin", "PublicAPI");
    }

    private static Assembly? ResolveFromProbeDirectories(string requestedAssemblyName, List<string> probeDirectories)
    {
        var simpleName = new AssemblyName(requestedAssemblyName).Name;
        if (string.IsNullOrEmpty(simpleName))
        {
            return null;
        }

        foreach (var dir in probeDirectories)
        {
            var candidate = Path.Combine(dir, simpleName + ".dll");
            if (File.Exists(candidate))
            {
                try
                {
                    return Assembly.LoadFrom(candidate);
                }
                catch
                {
                    // Try the next probe directory.
                }
            }
        }

        return null;
    }

    private static string Describe(Exception ex) =>
        ex is TargetInvocationException { InnerException: { } inner } ? inner.Message : ex.Message;
}
