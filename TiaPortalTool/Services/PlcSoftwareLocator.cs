using System.Reflection;

namespace TiaPortalTool.Services;

/// <summary>A device item that carries software (PLC program, HMI runtime, ...), found by <see cref="SoftwareLocator"/>.</summary>
public sealed class FoundSoftware
{
    public FoundSoftware(string deviceName, string deviceItemName, object software)
    {
        DeviceName = deviceName;
        DeviceItemName = deviceItemName;
        Software = software;
    }

    public string DeviceName { get; }
    public string DeviceItemName { get; }
    public object Software { get; }

    /// <summary>The Openness type of the software, e.g. Siemens.Engineering.SW.PlcSoftware or Siemens.Engineering.Hmi.HmiTarget.</summary>
    public string TypeName => Software.GetType().FullName ?? string.Empty;
}

/// <summary>
/// Walks every device (also inside device groups) and every device item, and returns each item's
/// <c>SoftwareContainer.Software</c>. A project can hold a PLC and one or more HMIs, in any order.
/// </summary>
public static class SoftwareLocator
{
    public static IReadOnlyList<FoundSoftware> FindAll(dynamic project, Assembly assembly)
    {
        var softwareContainerType = assembly.GetType("Siemens.Engineering.HW.Features.SoftwareContainer")
            ?? throw new InvalidOperationException(
                "Siemens.Engineering.HW.Features.SoftwareContainer type not found in the loaded Openness assembly.");

        var found = new List<FoundSoftware>();
        SearchDevices(project.Devices, softwareContainerType, found);
        SearchDeviceGroups(project.DeviceGroups, softwareContainerType, found);
        return found;
    }

    private static void SearchDevices(dynamic devices, Type softwareContainerType, List<FoundSoftware> found)
    {
        foreach (var device in devices)
        {
            string deviceName = device.Name;
            SearchDeviceItems(device.DeviceItems, deviceName, softwareContainerType, found);
        }
    }

    private static void SearchDeviceGroups(dynamic deviceGroups, Type softwareContainerType, List<FoundSoftware> found)
    {
        foreach (var group in deviceGroups)
        {
            SearchDevices(group.Devices, softwareContainerType, found);
            SearchDeviceGroups(group.Groups, softwareContainerType, found);
        }
    }

    private static void SearchDeviceItems(dynamic deviceItems, string deviceName, Type softwareContainerType, List<FoundSoftware> found)
    {
        foreach (var item in deviceItems)
        {
            var software = GetSoftware(item, softwareContainerType);
            if (software is not null)
            {
                found.Add(new FoundSoftware(deviceName, (string)item.Name, software));
            }

            try
            {
                SearchDeviceItems(item.DeviceItems, deviceName, softwareContainerType, found);
            }
            catch
            {
                // Some device items don't expose sub-items; nothing below them to search.
            }
        }
    }

    private static object? GetSoftware(object deviceItem, Type softwareContainerType)
    {
        var container = DynamicReflectionHelpers.GetService(deviceItem, softwareContainerType);
        if (container is null)
        {
            return null;
        }

        dynamic dynamicContainer = container;
        return (object?)dynamicContainer.Software;
    }
}

public static class PlcSoftwareLocator
{
    private const string PlcSoftwareTypeName = "Siemens.Engineering.SW.PlcSoftware";

    /// <summary>The first PLC program in the project. HMI runtimes are skipped, even when their device comes first.</summary>
    public static dynamic? FindFirstPlcSoftware(dynamic project, Assembly assembly)
    {
        IReadOnlyList<FoundSoftware> all = SoftwareLocator.FindAll(project, assembly);
        return all.FirstOrDefault(s => s.TypeName == PlcSoftwareTypeName)?.Software;
    }
}
