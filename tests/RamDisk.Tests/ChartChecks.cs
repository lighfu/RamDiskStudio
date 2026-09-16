using RamDisk.Core;

static class ChartChecks
{
    public static void Add(List<(string Name, Func<Task> Run)> tests)
    {
        void Test(string name, Action action) => tests.Add((name, () => { action(); return Task.CompletedTask; }));
        static void Check(bool value) { if (!value) throw new Exception("Chart assertion failed"); }
        const double now = 1_800_000_000;
        Test("Chart zoom preserves the time beneath the pointer", () =>
        {
            var range = new ChartWindow(now - 1000, now - 200);
            var zoomed = range.Zoom(0.5, 0.27, now);
            Check(Math.Abs((range.Start + range.Span * 0.27) - (zoomed.Start + zoomed.Span * 0.27)) < 1e-6);
            Check(zoomed.Span == 400);
            Check(zoomed.Zoom(2, 0.27, now) == range);
        });
        Test("Chart navigation is bounded at now and retained history", () =>
        {
            var range = new ChartWindow(now - 120, now);
            var future = range.Pan(1000, now);
            Check(future == range);
            var old = range.Pan(-1e10, now);
            Check(old.Start == now - ChartWindow.MaximumSpan && old.Span == 120);
            Check(range.Zoom(1e10, 0, now).Span == ChartWindow.MaximumSpan);
            Check(range.Zoom(1e-10, 0.5, now).Span == ChartWindow.MinimumSpan);
        });
        Test("Live resumes with the selected zoom width", () =>
        {
            var range = new ChartWindow(now - 9999, now - 9399);
            var live = range.Live(now);
            Check(live.End == now && live.Span == 600);
        });
        Test("Zero-safe log is finite, ordered and reversible for every metric", () =>
        {
            foreach (var metric in Enum.GetValues<MonitorMetric>())
            {
                var previous = -1.0;
                foreach (var value in new[] { 0.0, 0.000001, 0.01, 1, 1000, 1e12 })
                {
                    var transformed = ChartMath.Transform(value, ChartScale.Logarithmic, metric);
                    var restored = ChartMath.Inverse(transformed, ChartScale.Logarithmic, metric);
                    Check(double.IsFinite(transformed) && transformed > previous);
                    Check(Math.Abs(value - restored) <= Math.Max(1e-12, value * 1e-10));
                    previous = transformed;
                }
                Check(ChartMath.Transform(0, ChartScale.Logarithmic, metric) == 0);
            }
        });
        Test("Log makes low activity visible beside large spikes", () =>
        {
            var low = ChartMath.Transform(0.01, ChartScale.Logarithmic, MonitorMetric.Rate);
            var high = ChartMath.Transform(1000, ChartScale.Logarithmic, MonitorMetric.Rate);
            Check(low / high > 0.1);
            Check(0.01 / 1000 < 0.0001);
        });
        Test("Automatic aggregation respects both zoom density and retention", () =>
        {
            Check(ChartMath.AutomaticInterval(new(now - 120, now), now) == HistoryInterval.Second);
            Check(ChartMath.AutomaticInterval(new(now - 3600, now), now) == HistoryInterval.Minute);
            Check(ChartMath.AutomaticInterval(new(now - 3 * 3600, now - 3 * 3600 + 120), now) == HistoryInterval.Minute);
            Check(ChartMath.AutomaticInterval(new(now - 8 * 86400, now - 8 * 86400 + 120), now) == HistoryInterval.Hour);
            Check(ChartMath.AutomaticInterval(new(now - 100 * 86400, now - 100 * 86400 + 120), now) == HistoryInterval.Day);
        });
    }
}
