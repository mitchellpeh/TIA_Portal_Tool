namespace TiaPortalTool.Services;

internal static class DynamicReflectionHelpers
{
    public static object? GetService(object target, Type serviceType)
    {
        var getServiceMethod = target.GetType().GetMethods()
            .FirstOrDefault(m => m.Name == "GetService" && m.IsGenericMethod);

        return getServiceMethod?.MakeGenericMethod(serviceType).Invoke(target, null);
    }

    /// <summary>
    /// Invokes a method declared on <paramref name="interfaceType"/> against <paramref name="target"/>.
    /// Siemens' service objects often implement their interfaces explicitly, so a plain
    /// `dynamic` call (which binds against the object's concrete runtime type) can't see the
    /// method at all — it only exists on the interface's vtable slot. Looking the method up on
    /// the interface type and invoking it directly goes through the interface dispatch instead.
    /// </summary>
    public static object? InvokeMethod(object target, Type interfaceType, string methodName, object?[]? args = null)
    {
        var method = interfaceType.GetMethod(methodName)
            ?? throw new InvalidOperationException($"{interfaceType.FullName} has no method named '{methodName}'.");

        return method.Invoke(target, args);
    }
}
