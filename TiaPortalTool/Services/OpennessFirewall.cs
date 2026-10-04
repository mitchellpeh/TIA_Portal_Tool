using Microsoft.Win32;
using System.IO;
using System.Security.Cryptography;

namespace TiaPortalTool.Services;

/// <summary>
/// TIA Portal's Openness "firewall" asks the user to approve every exe that connects, and remembers
/// approvals under HKLM\SOFTWARE\Siemens\Automation\Openness\&lt;version&gt;\Whitelist\&lt;exe name&gt;\Entry*
/// as Path + FileHash (base64 SHA-256). A rebuilt exe has a new hash, so it has to be approved again.
/// </summary>
public static class OpennessFirewall
{
    public const string PromptAdvice =
        "TIA Portal will ask whether to allow this app (the Openness access prompt). TIA has to start up first, " +
        "so the prompt can take a couple of minutes to appear, and it can open behind other windows (check the " +
        "taskbar). Choose \"Yes to all\" to remember this copy of the app.";

    /// <summary>The running exe's path.</summary>
    public static string? CurrentExePath => System.Reflection.Assembly.GetEntryAssembly()?.Location;

    /// <summary>True if approved, false if TIA will prompt, null if it can't be determined.</summary>
    public static bool? IsApproved(string? exePath, int? tiaVersion)
    {
        if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
        {
            return null;
        }

        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var root = baseKey.OpenSubKey(@"SOFTWARE\Siemens\Automation\Openness");
            if (root is null)
            {
                return null;
            }

            var versionKeys = root.GetSubKeyNames();
            var matching = tiaVersion.HasValue ? versionKeys.Where(k => k.StartsWith(tiaVersion + ".", StringComparison.Ordinal)).ToArray() : versionKeys;
            if (matching.Length == 0)
            {
                matching = versionKeys;
            }

            string hash;
            using (var sha = SHA256.Create())
            using (var stream = File.OpenRead(exePath))
            {
                hash = Convert.ToBase64String(sha.ComputeHash(stream));
            }

            foreach (var version in matching)
            {
                using var app = root.OpenSubKey($@"{version}\Whitelist\{Path.GetFileName(exePath)}");
                if (app is null)
                {
                    continue;
                }

                foreach (var entryName in app.GetSubKeyNames())
                {
                    using var entry = app.OpenSubKey(entryName);
                    if (string.Equals(entry?.GetValue("FileHash") as string, hash, StringComparison.Ordinal)
                        && string.Equals(entry?.GetValue("Path") as string, exePath, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }

            return false;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
