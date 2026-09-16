namespace RamDisk.Core;

/// <summary>A continuous time window. Wheel zoom keeps the time under the pointer fixed.</summary>
public readonly record struct ChartWindow(double Start, double End)
{
    public const double MinimumSpan = 10;
    public const double MaximumSpan = 730 * 86400;
    public double Span => End - Start;

    public ChartWindow Constrain(double now)
    {
        var span = Math.Clamp(Span, MinimumSpan, MaximumSpan);
        var end = Math.Clamp(End, now - MaximumSpan + span, now);
        return new(end - span, end);
    }

    public ChartWindow Zoom(double factor, double anchor, double now)
    {
        anchor = Math.Clamp(anchor, 0, 1);
        var span = Math.Clamp(Span * factor, MinimumSpan, MaximumSpan);
        var time = Start + Span * anchor;
        return new ChartWindow(time - span * anchor, time + span * (1 - anchor)).Constrain(now);
    }

    public ChartWindow Pan(double seconds, double now) => new ChartWindow(Start + seconds, End + seconds).Constrain(now);
    public ChartWindow Live(double now) => new ChartWindow(now - Span, now).Constrain(now);
}

public enum ChartScale { Linear, Logarithmic, Focused }

public static class ChartMath
{
    // Fixed, unit-aware knees keep log scales comparable between refreshes and admit genuine zeroes.
    public static double LogKnee(MonitorMetric metric) => metric switch
    {
        MonitorMetric.Rate or MonitorMetric.Bytes or MonitorMetric.UsedBytes => 1.0 / 1024,
        MonitorMetric.UsagePercent => 0.01,
        _ => 1
    };

    public static double Transform(double value, ChartScale scale, MonitorMetric metric) =>
        scale == ChartScale.Logarithmic ? Math.Log10(1 + Math.Max(0, value) / LogKnee(metric)) : value;

    public static double Inverse(double value, ChartScale scale, MonitorMetric metric) =>
        scale == ChartScale.Logarithmic ? (Math.Pow(10, value) - 1) * LogKnee(metric) : value;

    public static HistoryInterval AutomaticInterval(ChartWindow window, double now)
    {
        // Retention matters as well as point density: old second samples no longer exist.
        var age = now - window.Start;
        if (age <= 2 * 3600 - 5 && window.Span <= 1200) return HistoryInterval.Second;
        if (age <= 7 * 86400 - 60 && window.Span <= 1200 * 60) return HistoryInterval.Minute;
        if (age <= 90 * 86400 - 3600 && window.Span <= 1200 * 3600) return HistoryInterval.Hour;
        return HistoryInterval.Day;
    }
}
