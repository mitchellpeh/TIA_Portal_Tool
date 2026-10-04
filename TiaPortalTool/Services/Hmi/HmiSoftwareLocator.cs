using System.Reflection;

namespace TiaPortalTool.Services.Hmi;

public static class HmiSoftwareLocator
{
    private const string ClassicTypeName = "Siemens.Engineering.Hmi.HmiTarget";
    private const string UnifiedTypeName = "Siemens.Engineering.HmiUnified.HmiSoftware";

    /// <summary>Every HMI runtime in the project, classic WinCC and Unified, in device tree order.</summary>
    public static IReadOnlyList<HmiDevice> FindAll(dynamic project, Assembly assembly)
    {
        IReadOnlyList<FoundSoftware> all = SoftwareLocator.FindAll(project, assembly);
        var hmis = new List<HmiDevice>();
        foreach (var software in all)
        {
            var kind = software.TypeName switch
            {
                ClassicTypeName => HmiKind.ClassicWinCC,
                UnifiedTypeName => HmiKind.Unified,
                _ => (HmiKind?)null
            };

            if (kind is not null)
            {
                hmis.Add(new HmiDevice(software.DeviceName, software.DeviceItemName, kind.Value, software.Software));
            }
        }

        return hmis;
    }
}
