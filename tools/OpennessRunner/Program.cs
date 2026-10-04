using System;
using System.Linq;
using System.Reflection;

// Usage: OpennessRunner <path to TiaPortalTool.exe> <command> [args...]
// Loads that build of the app and runs TiaPortalTool.Testing.HeadlessCommands.Run(args).
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Usage: OpennessRunner <TiaPortalTool.exe> <command> [args...]");
            return 2;
        }

        try
        {
            var app = Assembly.LoadFrom(args[0]);
            var run = app.GetType("TiaPortalTool.Testing.HeadlessCommands", throwOnError: true)!
                .GetMethod("Run", BindingFlags.Public | BindingFlags.Static)!;
            return (int)run.Invoke(null, new object[] { args.Skip(1).ToArray() })!;
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            Console.Error.WriteLine(ex.InnerException);
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }
}
