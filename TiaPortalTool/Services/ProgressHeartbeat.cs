using System.Diagnostics;

namespace TiaPortalTool.Services;

/// <summary>
/// For Openness calls that block for minutes (compile, exporting or importing a big object): reports once a minute
/// that the step is still running and how much CPU TIA used in that minute, so a long step can be told from a hung
/// one. TIA's processes (including its HMI compiler) all run as Siemens.Automation.Portal.
/// </summary>
public sealed class ProgressHeartbeat : IDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    private readonly IProgress<string> _progress;
    private readonly string _what;
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private readonly Timer _timer;
    private Dictionary<int, TimeSpan> _lastCpu;

    private ProgressHeartbeat(IProgress<string> progress, string what)
    {
        _progress = progress;
        _what = what;
        _lastCpu = TiaCpu();
        _timer = new Timer(_ => Beat(), null, Interval, Interval);
    }

    /// <summary>Starts reporting; dispose when the step ends. Does nothing without a progress sink.</summary>
    public static IDisposable Start(IProgress<string>? progress, string what) =>
        progress is null ? new NoOp() : new ProgressHeartbeat(progress, what);

    public void Dispose() => _timer.Dispose();

    private void Beat()
    {
        // Per process: a TIA helper that exits (or starts) between beats must not make the total look idle.
        var cpu = TiaCpu();
        var used = TimeSpan.Zero;
        foreach (var pair in cpu)
        {
            used += pair.Value - (_lastCpu.TryGetValue(pair.Key, out var before) ? before : TimeSpan.Zero);
        }

        _lastCpu = cpu;
        var elapsed = _stopwatch.Elapsed;
        var state = used.TotalSeconds >= 1
            ? $"TIA used {used.TotalSeconds:0} s of CPU in the last minute"
            : "TIA used no CPU in the last minute; if that repeats, it may be stuck";
        _progress.Report($"  ... still {_what} ({(int)elapsed.TotalMinutes}:{elapsed.Seconds:00}); {state}.");
    }

    /// <summary>CPU time used so far by each running TIA process, by process id.</summary>
    private static Dictionary<int, TimeSpan> TiaCpu()
    {
        var cpu = new Dictionary<int, TimeSpan>();
        foreach (var process in Process.GetProcessesByName("Siemens.Automation.Portal"))
        {
            try
            {
                cpu[process.Id] = process.TotalProcessorTime;
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Exited or not ours to read: leave it out.
            }
            finally
            {
                process.Dispose();
            }
        }

        return cpu;
    }

    private sealed class NoOp : IDisposable
    {
        public void Dispose()
        {
        }
    }
}
