namespace TiaPortalTool.Services;

/// <summary>
/// Adds a CPU to a project. Openness needs an exact order number and firmware version, and which firmware a TIA
/// Portal version knows about differs, so this tries a short list (newest firmware first) and keeps the first that
/// works. The user can swap to the real CPU later with "Change device" in TIA Portal.
/// </summary>
public static class PlcDeviceCreator
{
    public const string DeviceName = "PLC_1";

    // Order number, description, firmware versions newest first.
    private static readonly (string Order, string Description, string[] Firmware)[] S71500 =
    {
        ("6ES7 516-3AN02-0AB0", "CPU 1516-3 PN/DP", new[] { "V4.0", "V3.1", "V3.0", "V2.9", "V2.8", "V2.6" }),
        ("6ES7 515-2AM02-0AB0", "CPU 1515-2 PN", new[] { "V4.0", "V3.1", "V3.0", "V2.9", "V2.8", "V2.6" }),
    };

    private static readonly (string Order, string Description, string[] Firmware)[] S71200 =
    {
        ("6ES7 215-1AG40-0XB0", "CPU 1215C DC/DC/DC", new[] { "V4.7", "V4.6", "V4.5", "V4.4", "V4.2" }),
        ("6ES7 214-1AG40-0XB0", "CPU 1214C DC/DC/DC", new[] { "V4.7", "V4.6", "V4.5", "V4.4", "V4.2" }),
    };

    /// <summary>Creates the CPU and returns a description of what was created. Throws if nothing on the list works.</summary>
    public static string CreatePlc(dynamic project, bool s71200, IProgress<string>? progress)
    {
        var failures = new List<string>();
        foreach (var (order, description, firmwareList) in s71200 ? S71200 : S71500)
        {
            foreach (var firmware in firmwareList)
            {
                var typeIdentifier = $"OrderNumber:{order}/{firmware}";
                try
                {
                    project.Devices.CreateWithItem(typeIdentifier, DeviceName, DeviceName);
                    var created = $"{description} ({order}, firmware {firmware}) named {DeviceName}";
                    progress?.Report($"Added {created}.");
                    return created;
                }
                catch (Exception ex)
                {
                    failures.Add($"{typeIdentifier}: {Describe(ex)}");
                }
            }
        }

        throw new InvalidOperationException("Couldn't add a CPU with any of the known order numbers and firmware versions:\n"
                                            + string.Join("\n", failures));
    }

    private static string Describe(Exception ex) =>
        ex is System.Reflection.TargetInvocationException { InnerException: { } inner } ? inner.Message : ex.Message;
}
