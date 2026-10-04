using System.Diagnostics;
using System.Globalization;

namespace Odx.Core;

/// <summary>
/// Input event kinds; <see cref="InputLogWriter.KindName"/> gives their input_events.csv names. odx input only produces
/// the raw kinds (KeyDown..MouseMove); actions come from the game profile's bindings at build time.
/// </summary>
public enum InputKind { KeyDown, KeyUp, ButtonDown, ButtonUp, MouseMove, ActionStart, ActionEnd }

/// <summary>One raw input event. <c>TQpcMs</c> is QPC in milliseconds, shared by every process on the machine.</summary>
public readonly record struct InputEvent(double TQpcMs, InputKind Kind, string Value = "", double Dx = 0, double Dy = 0);

public static class QpcClock
{
    /// <summary>QPC in milliseconds. On Windows, Stopwatch.GetTimestamp() is QueryPerformanceCounter.</summary>
    public static double NowMs() => Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency;
}

/// <summary>
/// Writes input_events.csv: <c>t_qpc_ms,kind,value,dx,dy</c>. Read back by <see cref="InputLogReader"/>, then folded
/// into per-frame input columns by <see cref="InputAggregator"/>. The format is pinned by tests/fixtures/input_events.csv.
/// </summary>
public sealed class InputLogWriter(TextWriter writer) : IDisposable
{
    public const string Header = "t_qpc_ms,kind,value,dx,dy";
    private bool _headerWritten;

    public void Write(InputEvent e)
    {
        if (!_headerWritten)
        {
            writer.Write(Header + "\n");
            _headerWritten = true;
        }
        writer.Write(string.Join(",", Num(e.TQpcMs), KindName(e.Kind), e.Value, Num(e.Dx), Num(e.Dy)) + "\n");
    }

    public void Flush() => writer.Flush();
    public void Dispose() => writer.Dispose();

    public static string KindName(InputKind kind) => kind switch
    {
        InputKind.KeyDown => "key_down",
        InputKind.KeyUp => "key_up",
        InputKind.ButtonDown => "button_down",
        InputKind.ButtonUp => "button_up",
        InputKind.MouseMove => "mouse_move",
        InputKind.ActionStart => "action_start",
        InputKind.ActionEnd => "action_end",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    internal static string Num(double v) => v.ToString("R", CultureInfo.InvariantCulture);
}

/// <summary>
/// Writes clock_samples.csv: <c>t_qpc_ms,utc_unix_ms</c>, one row per call. Maps QPC to wall-clock UTC so inputs can be
/// aligned with server events and demo ticks (which are stamped in UTC).
/// </summary>
public sealed class ClockSampleWriter(TextWriter writer) : IDisposable
{
    public const string Header = "t_qpc_ms,utc_unix_ms";
    private bool _headerWritten;

    public void Write(double tQpcMs, long utcUnixMs)
    {
        if (!_headerWritten)
        {
            writer.Write(Header + "\n");
            _headerWritten = true;
        }
        writer.Write($"{InputLogWriter.Num(tQpcMs)},{utcUnixMs}\n");
    }

    public void Flush() => writer.Flush();
    public void Dispose() => writer.Dispose();
}

public static class InputLogReader
{
    public static List<InputEvent> Read(string path)
    {
        var (_, rows) = Csv.ReadFile(path);
        return rows.Select(r => new InputEvent(Frames.ParseDouble(r["t_qpc_ms"]), ParseKind(r["kind"]), r["value"],
            Frames.ParseDouble(r["dx"]), Frames.ParseDouble(r["dy"]))).ToList();
    }

    public static InputKind ParseKind(string name)
    {
        foreach (var kind in Enum.GetValues<InputKind>())
            if (InputLogWriter.KindName(kind) == name) return kind;
        throw new FormatException($"unknown input event kind: {name}");
    }
}
