using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using TiaPortalTool.Services;

namespace TiaPortalTool;

public enum LogLevel { Info, Success, Warning, Error }

/// <summary>How a run ended, for its closing line.</summary>
public enum RunResult { Success, Warnings, Problems, Failed, Cancelled }

/// <summary>
/// The Output pane shared by the tool windows: a colour-coded log (yellow warnings, red errors, green success) in a
/// RichTextBox, plus a watch for TIA Portal waiting on the user. When a line says so (the app isn't approved in the
/// Openness firewall yet) or a step runs far longer than normal (TIA starting, a project opening), the Output card
/// pulses yellow and the taskbar button flashes until the log moves on.
/// </summary>
public sealed class OutputLog
{
    public static readonly Brush TextBrush = Frozen("#cbd5e1");
    public static readonly Brush TimestampBrush = Frozen("#64748b");
    public static readonly Brush SuccessBrush = Frozen("#4ade80");
    public static readonly Brush WarningBrush = Frozen("#fbbf24");
    public static readonly Brush ErrorBrush = Frozen("#f87171");

    // TIA normally starts here in 20-40 s once this exe is approved; a slow PC can take a minute or two.
    private static readonly TimeSpan StartingStallDelay = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan OpeningStallDelay = TimeSpan.FromMinutes(2);

    private readonly Window _window;
    private readonly RichTextBox _box;
    private readonly Border _card;
    private readonly Paragraph _paragraph = new();
    private readonly List<string> _lines = new();

    private string _step = string.Empty;
    private DateTime _stepStartedAt;
    private bool _stallHintShown;

    // After the "not approved" warning, TIA still has to start before its prompt appears: keep pulsing through that step.
    private bool _holdThroughStart;

    public OutputLog(Window window, RichTextBox box, Border card)
    {
        _window = window;
        _box = box;
        _card = card;
        _box.Document = new FlowDocument(_paragraph)
        {
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            PagePadding = new Thickness(0)
        };
    }

    /// <summary>Each line as written, "[HH:mm:ss] text", for log files.</summary>
    public event Action<string>? LineWritten;

    /// <summary>Raised with what the user should look at when the pulse starts, and with null when it stops.</summary>
    public event Action<string?>? AttentionChanged;

    public bool NeedsAttention { get; private set; }

    public int LineCount => _lines.Count;

    public string Text => string.Join(Environment.NewLine, _lines);

    public void Append(string text, LogLevel? level = null)
    {
        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            if (line.Length == 0)
            {
                _lines.Add(string.Empty);
                LineWritten?.Invoke(string.Empty);
                _paragraph.Inlines.Add(new LineBreak());
                continue;
            }

            var lineLevel = level ?? Classify(line);
            var stamped = $"[{DateTime.Now:HH:mm:ss}] {line}";
            _lines.Add(stamped);
            LineWritten?.Invoke(stamped);
            _paragraph.Inlines.Add(new Run(stamped.Substring(0, 11)) { Foreground = TimestampBrush });
            _paragraph.Inlines.Add(new Run(line) { Foreground = BrushFor(lineLevel) });
            _paragraph.Inlines.Add(new LineBreak());
            Watch(line, lineLevel);
        }

        _box.ScrollToEnd();
    }

    /// <summary>
    /// The closing line of a run, in bold: "SUCCESS: ..." (green), "DONE WITH WARNINGS: ..." (yellow), "FAILED: ..." (red)
    /// or "CANCELLED: ..." (grey), after a blank line so it stands out at the end of the log.
    /// </summary>
    public void AppendResult(RunResult result, string text)
    {
        var (label, brush) = result switch
        {
            RunResult.Success => ("SUCCESS", SuccessBrush),
            RunResult.Warnings => ("DONE WITH WARNINGS", WarningBrush),
            RunResult.Problems => ("DONE WITH PROBLEMS", WarningBrush),
            RunResult.Cancelled => ("CANCELLED", TimestampBrush),
            _ => ("FAILED", ErrorBrush)
        };

        Append(string.Empty);
        var line = $"{label}: {text}";
        var stamped = $"[{DateTime.Now:HH:mm:ss}] {line}";
        _lines.Add(stamped);
        LineWritten?.Invoke(stamped);
        _paragraph.Inlines.Add(new Run(stamped.Substring(0, 11)) { Foreground = TimestampBrush });
        _paragraph.Inlines.Add(new Run(line) { Foreground = brush, FontWeight = FontWeights.Bold });
        _paragraph.Inlines.Add(new LineBreak());
        EndRun();
        _box.ScrollToEnd();
    }

    public void Clear()
    {
        _lines.Clear();
        _paragraph.Inlines.Clear();
        EndRun();
    }

    /// <summary>The run finished: nothing is waiting any more.</summary>
    public void EndRun()
    {
        _step = string.Empty;
        _holdThroughStart = false;
        StopAttention();
    }

    /// <summary>Call about once a second while a run is busy; adds a hint (and pulses) when a TIA step stalls.</summary>
    public void CheckForStall()
    {
        if (_stallHintShown || NeedsAttention || _step.Length == 0)
        {
            return;
        }

        var waited = DateTime.Now - _stepStartedAt;
        if (_step.StartsWith(TiaSessionService.StartingStep, StringComparison.Ordinal) && waited >= StartingStallDelay)
        {
            _stallHintShown = true;
            Append("Warning: TIA Portal is taking longer than usual to start. If TIA's Openness access prompt is open, answer it. "
                   + "It can be behind other windows, so check the taskbar.", LogLevel.Warning);
        }
        else if (_step.StartsWith(TiaSessionService.OpeningStep, StringComparison.Ordinal) && waited >= OpeningStallDelay)
        {
            _stallHintShown = true;
            Append("Warning: the project is still opening. Large projects can take several minutes. "
                   + "If TIA Portal shows a dialog, answer it; otherwise keep waiting.", LogLevel.Warning);
        }
    }

    // ---- Waiting-for-the-user watch ----

    private void Watch(string line, LogLevel level)
    {
        var trimmed = line.TrimStart();
        if (IsWaitingForUser(trimmed))
        {
            _holdThroughStart |= Contains(trimmed, "firewall");
            StartAttention(trimmed.StartsWith("Warning: ", StringComparison.OrdinalIgnoreCase) ? trimmed.Substring(9) : trimmed);
            return;
        }

        // Warnings and indented detail lines belong to the current step.
        if (level == LogLevel.Warning || line.StartsWith(" ", StringComparison.Ordinal))
        {
            return;
        }

        var isStarting = trimmed.StartsWith(TiaSessionService.StartingStep, StringComparison.Ordinal);
        _step = trimmed;
        _stepStartedAt = DateTime.Now;
        _stallHintShown = false;
        if (isStarting && _holdThroughStart)
        {
            return;
        }

        _holdThroughStart = false;
        StopAttention();
    }

    // Lines from the services and the stall hints that mean TIA Portal is (probably) waiting for an answer.
    private static bool IsWaitingForUser(string line) =>
        line.StartsWith("Warning", StringComparison.OrdinalIgnoreCase)
        && (Contains(line, "answer it")
            || (Contains(line, "Openness") && (Contains(line, "approved") || Contains(line, "prompt"))));

    private void StartAttention(string reason)
    {
        if (!NeedsAttention)
        {
            NeedsAttention = true;
            var border = new SolidColorBrush(Color.FromRgb(0x33, 0x41, 0x55));
            var background = new SolidColorBrush(Color.FromRgb(0x11, 0x18, 0x27));
            _card.BorderBrush = border;
            _card.Background = background;
            _card.BorderThickness = new Thickness(2);
            var pulse = TimeSpan.FromMilliseconds(700);
            border.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(Color.FromRgb(0xfb, 0xbf, 0x24), pulse)
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever
            });
            background.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(Color.FromRgb(0x3a, 0x2e, 0x0c), pulse)
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever
            });
            FlashTaskbar(start: true);
        }

        AttentionChanged?.Invoke(reason);
    }

    private void StopAttention()
    {
        if (!NeedsAttention)
        {
            return;
        }

        NeedsAttention = false;
        _card.ClearValue(Border.BorderBrushProperty);
        _card.ClearValue(Border.BackgroundProperty);
        _card.ClearValue(Border.BorderThicknessProperty);
        FlashTaskbar(start: false);
        AttentionChanged?.Invoke(null);
    }

    // ---- Colours ----

    // Services report plain strings, so colour is inferred from the wording they use.
    public static LogLevel Classify(string line)
    {
        var trimmed = line.TrimStart();
        if (trimmed.StartsWith("PROBLEM", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("Error", StringComparison.OrdinalIgnoreCase)
            || Contains(line, "fail")
            || Contains(line, "NOT saved")
            || Contains(line, "Mismatch")
            || trimmed.StartsWith("Missing:", StringComparison.Ordinal)
            || Contains(line, "state = Error"))
        {
            return LogLevel.Error;
        }

        if (trimmed.StartsWith("Warning", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("Attention", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("MANUAL", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("Unchecked:", StringComparison.Ordinal)
            || Contains(line, "warning(s):")
            || Contains(line, "item(s) need attention")
            || Contains(line, "skipped")
            || Contains(line, "state = Warning"))
        {
            return LogLevel.Warning;
        }

        if (Contains(line, "Project saved")
            || Contains(line, "state = Success")
            || trimmed.StartsWith("RESULT: SUCCESS", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("OK:", StringComparison.OrdinalIgnoreCase))
        {
            return LogLevel.Success;
        }

        return LogLevel.Info;
    }

    public static Brush BrushFor(LogLevel level) => level switch
    {
        LogLevel.Success => SuccessBrush,
        LogLevel.Warning => WarningBrush,
        LogLevel.Error => ErrorBrush,
        _ => TextBrush
    };

    private static bool Contains(string text, string value) => text.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0;

    private static Brush Frozen(string hex)
    {
        var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
        brush.Freeze();
        return brush;
    }

    // ---- Taskbar flash, so a prompt behind other windows still gets noticed ----

    [StructLayout(LayoutKind.Sequential)]
    private struct FlashInfo
    {
        public uint Size;
        public IntPtr Window;
        public uint Flags;
        public uint Count;
        public uint Timeout;
    }

    [DllImport("user32.dll")]
    private static extern bool FlashWindowEx(ref FlashInfo info);

    private const uint FlashStop = 0;
    private const uint FlashAll = 3;
    private const uint FlashUntilForeground = 12;

    private void FlashTaskbar(bool start)
    {
        var handle = new WindowInteropHelper(_window).Handle;
        if (handle == IntPtr.Zero || (start && _window.IsActive))
        {
            return;
        }

        var info = new FlashInfo
        {
            Size = (uint)Marshal.SizeOf(typeof(FlashInfo)),
            Window = handle,
            Flags = start ? FlashAll | FlashUntilForeground : FlashStop
        };
        FlashWindowEx(ref info);
    }
}
