using TiaPortalTool.Conversion.Slc;

// Usage: SlcConvert <export.SLC> <output folder> [--1200] [--symbols <file.SY6>]
if (args.Length < 2)
{
    Console.Error.WriteLine("Usage: SlcConvert <export.SLC> <output folder> [--1200] [--symbols <file>]");
    return 2;
}

var options = new SlcConversionOptions { SlcPath = args[0], OutputDirectory = args[1] };
for (var i = 2; i < args.Length; i++)
{
    if (args[i] == "--1200")
    {
        options.Cpu = CpuFamily.S71200;
    }
    else if (args[i] == "--symbols" && i + 1 < args.Length)
    {
        options.SymbolsPath = args[++i];
    }
}

var result = SlcConverter.Convert(options, new Progress<string>(Console.WriteLine));
foreach (var message in result.Messages)
{
    Console.WriteLine(message);
}

Console.WriteLine($"{result.Warnings.Count} item(s) need attention:");
foreach (var warning in result.Warnings)
{
    Console.WriteLine("  " + warning);
}

return 0;
