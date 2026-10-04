using System.IO;
using System.Reflection;

namespace TiaPortalTool.Services;

public sealed class BlockReimportResult
{
    public bool Success { get; set; }
    public IReadOnlyList<string> Messages { get; set; } = Array.Empty<string>();
}

public sealed class BlockReimportService
{
    public BlockReimportResult ReimportAndCompile(dynamic plcSoftware, Assembly assembly, IReadOnlyList<BlockManifestEntry> manifest, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        progress?.Report($"Importing {manifest.Count} changed block(s)...");
        var importOptionsType = assembly.GetType("Siemens.Engineering.ImportOptions")
            ?? throw new InvalidOperationException("Siemens.Engineering.ImportOptions type not found.");
        dynamic overrideOption = Enum.Parse(importOptionsType, "Override");

        var messages = new List<string>();
        var blocksToCompile = new List<(string Name, dynamic Block)>();
        bool anyFailure = false;

        foreach (var entry in manifest)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(entry.XmlPath))
            {
                messages.Add($"Skipped {entry.BlockName}: file not found at {entry.XmlPath}.");
                anyFailure = true;
                continue;
            }

            dynamic? group = FindGroupByPath(plcSoftware.BlockGroup, entry.GroupPath);
            if (group is null)
            {
                messages.Add($"Skipped {entry.BlockName}: could not locate group '{entry.GroupPath}'.");
                anyFailure = true;
                continue;
            }

            try
            {
                group.Blocks.Import(new FileInfo(entry.XmlPath), overrideOption);
            }
            catch (Exception ex)
            {
                messages.Add($"Import failed for {entry.BlockName}: {ex.Message}");
                anyFailure = true;
                continue;
            }

            dynamic? block = FindBlockByName(group.Blocks, entry.BlockName);
            if (block is null)
            {
                messages.Add($"Imported {entry.BlockName} but could not find it afterward to compile.");
                anyFailure = true;
                continue;
            }

            messages.Add($"Imported {entry.BlockName}.");
            blocksToCompile.Add((entry.BlockName, block));
        }

        // Compiling blocks one at a time (in whatever order the manifest happens to list
        // them) blows up on any caller/callee ordering: a block compiled before something
        // it calls fails with "Block <X> that is accessed has not been compiled", even
        // though both blocks are individually fine. Compiling the whole PLC program as one
        // unit — the same thing TIA's own "Compile All" does — resolves the call graph
        // itself instead of depending on manifest order.
        cancellationToken.ThrowIfCancellationRequested();
        if (blocksToCompile.Count > 0 && !PlcCompiler.CompileAll(plcSoftware, assembly, messages, progress))
        {
            anyFailure = true;
        }

        return new BlockReimportResult { Success = !anyFailure, Messages = messages };
    }

    internal static dynamic? FindGroupByPath(dynamic rootGroup, string groupPath)
    {
        dynamic group = rootGroup;
        if (string.IsNullOrEmpty(groupPath))
        {
            return group;
        }

        foreach (var segment in groupPath.Split('/'))
        {
            dynamic? next = null;
            foreach (dynamic candidate in group.Groups)
            {
                if (string.Equals((string)candidate.Name, segment, StringComparison.OrdinalIgnoreCase))
                {
                    next = candidate;
                    break;
                }
            }

            if (next is null)
            {
                return null;
            }

            group = next;
        }

        return group;
    }

    internal static dynamic? FindBlockByName(dynamic blocks, string blockName)
    {
        foreach (dynamic block in blocks)
        {
            if (string.Equals((string)block.Name, blockName, StringComparison.OrdinalIgnoreCase))
            {
                return block;
            }
        }

        return null;
    }
}
