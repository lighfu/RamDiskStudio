using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using RamDisk.Core;

namespace RamDisk.App;

public sealed class HistoryChart : FrameworkElement
{
    private static DependencyProperty PaletteBrush(string name) => DependencyProperty.Register(name, typeof(Brush), typeof(HistoryChart), new FrameworkPropertyMetadata(Brushes.Transparent, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ReadBrushProperty = PaletteBrush("ReadBrush");
    public static readonly DependencyProperty WriteBrushProperty = PaletteBrush("WriteBrush");
    public static readonly DependencyProperty MutedBrushProperty = PaletteBrush("MutedBrush");
    public static readonly DependencyProperty SurfaceBrushProperty = PaletteBrush("SurfaceBrush");
    public static readonly DependencyProperty GridBrushProperty = PaletteBrush("GridBrush");
    public static readonly DependencyProperty SelectionBrushProperty = PaletteBrush("SelectionBrush");
    public Brush ReadBrush => (Brush)GetValue(ReadBrushProperty);
    public Brush WriteBrush => (Brush)GetValue(WriteBrushProperty);
    public Brush MutedBrush => (Brush)GetValue(MutedBrushProperty);
    public Brush SurfaceBrush => (Brush)GetValue(SurfaceBrushProperty);
    public Brush GridBrush => (Brush)GetValue(GridBrushProperty);
    public Brush SelectionBrush => (Brush)GetValue(SelectionBrushProperty);
    private Pen GridPen => new(GridBrush, 1);
    private IReadOnlyList<HistoryPoint> points = [];
    private HistoryInterval interval;
    private MonitorMetric metric;
    private string unit = "MiB/s";
    private ChartScale scale;
    private bool showRead = true, showWrite = true;
    private double? hoverTime;
    private string readout = "";
    private Rect plot;
    private Point? dragOrigin, selectionEnd;
    private ChartWindow dragWindow;
    private double yMin, yMax = 1, dragMin, dragMax;
    private bool manualY, dragWasManual, selecting, verticalDrag;

    public ChartWindow Window { get; private set; }
    public bool IsVerticalManual => manualY;
    public bool IsInteracting => dragOrigin.HasValue;
    public event Action<ChartWindow>? WindowChanged;
    public event Action<double?>? HoverChanged;
    public event Action<string>? ReadoutChanged;
    public event Action? ResetRequested;
    public event Action? InteractionFinished;
    internal Rect PlotBounds => plot;

    public HistoryChart()
    {
        SetResourceReference(ReadBrushProperty, "ReadSeries"); SetResourceReference(WriteBrushProperty, "WriteSeries");
        SetResourceReference(MutedBrushProperty, "Muted"); SetResourceReference(SurfaceBrushProperty, "Surface");
        SetResourceReference(GridBrushProperty, "ChartGrid"); SetResourceReference(SelectionBrushProperty, "ChartSelection");
        Focusable = true;
        Cursor = Cursors.Cross;
        System.Windows.Automation.AutomationProperties.SetName(this, "履歴グラフ。ホイールで時間を拡縮、ドラッグで移動。Control で縦軸操作、Shift ドラッグで期間選択。Home でリセット。");
    }

    public void SetData(IReadOnlyList<HistoryPoint> data, HistoryInterval step, MonitorMetric value, long start, long end, string units)
    {
        if (metric != value) ResetVertical();
        points = data; interval = step; metric = value; unit = units;
        SetWindow(new(start, Math.Max(start + 1, end)));
    }
    public void SetWindow(ChartWindow window) { Window = window; UpdateReadout(); InvalidateVisual(); }
    public void SetHover(double? time) { hoverTime = time; UpdateReadout(); InvalidateVisual(); }
    public void SetAppearance(ChartScale value, bool read, bool write)
    {
        if (scale != value) { scale = value; ResetVertical(); }
        showRead = read; showWrite = write; InvalidateVisual();
    }
    public void ResetVertical() { manualY = false; InvalidateVisual(); }
    public void ZoomTime(double factor, double anchor = 0.5) => ChangeWindow(Window.Zoom(factor, anchor, Now));
    public void PanTime(double seconds) => ChangeWindow(Window.Pan(seconds, Now));
    private static double Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    private void ChangeWindow(ChartWindow value) { SetWindow(value); WindowChanged?.Invoke(value); }

    private void UpdateReadout()
    {
        var text = "カーソルを合わせると時刻・実測値を表示";
        if (hoverTime is { } time && time >= Window.Start && time <= Window.End)
        {
            var nearest = points.Where(p => p.UnixTime >= Window.Start && p.UnixTime <= Window.End).MinBy(p => Math.Abs(p.UnixTime - time));
            if (nearest is not null && Math.Abs(nearest.UnixTime - time) <= (int)interval * 0.55)
            {
                var pair = nearest.Values(metric);
                var capacity = metric is MonitorMetric.UsedBytes or MonitorMetric.UsagePercent;
                text = $"{DateTimeOffset.FromUnixTimeSeconds(nearest.UnixTime).ToLocalTime():MM/dd HH:mm:ss}  {(capacity ? "使用" : "読")}: {Number(pair.Read)} {unit}" +
                    (capacity ? "" : $"   書: {Number(pair.Write)} {unit}") + $"   観測 {nearest.IoSeconds:0.#} 秒";
            }
            else text = $"{DateTimeOffset.FromUnixTimeSeconds((long)time).ToLocalTime():MM/dd HH:mm:ss}  未測定";
        }
        if (text != readout) { readout = text; ReadoutChanged?.Invoke(text); }
    }

    // Vertical state is in transformed units; on a log axis equal movements represent equal ratios.
    public void ZoomVertical(double factor, double anchor = 0.5)
    {
        anchor = Math.Clamp(anchor, 0, 1);
        var value = yMin + (yMax - yMin) * anchor;
        var span = Math.Clamp((yMax - yMin) * factor, 1e-9, scale == ChartScale.Logarithmic ? 18 : 1e18);
        yMin = Math.Max(0, value - span * anchor); yMax = yMin + span;
        manualY = true; InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        dc.DrawRectangle(SurfaceBrush, null, new Rect(RenderSize));
        if (ActualWidth < 160 || ActualHeight < 85 || Window.Span <= 0) return;
        plot = new Rect(68, 23, ActualWidth - 84, ActualHeight - 49);
        var visible = points.Where(x => x.UnixTime >= Window.Start && x.UnixTime <= Window.End).ToArray();
        var values = visible.SelectMany(p =>
        {
            var pair = p.Values(metric);
            return new[] { showRead ? pair.Read : null, showWrite ? pair.Write : null };
        }).Where(v => v.HasValue).Select(v => v!.Value).ToArray();
        if (!manualY)
        {
            var maximum = values.DefaultIfEmpty(0).Max();
            var minimum = values.DefaultIfEmpty(0).Min();
            yMin = 0;
            if (scale == ChartScale.Focused)
            {
                var padding = maximum > minimum ? Math.Max((maximum - minimum) * 0.08, 1e-12) : Math.Max(maximum * 0.001, ChartMath.LogKnee(metric));
                yMin = Math.Max(0, minimum - padding); yMax = maximum + padding;
            }
            else if (scale == ChartScale.Logarithmic)
                yMax = Math.Max(1, Math.Ceiling(ChartMath.Transform(maximum, scale, metric) * 2) / 2);
            else yMax = metric == MonitorMetric.UsagePercent ? 100 : NiceMaximum(maximum);
        }
        Text(dc, unit, new(0, 1), MutedBrush);
        var axisLabel = manualY ? "縦軸固定 · 自動に戻すには「縦軸を自動」" : scale switch
        { ChartScale.Logarithmic => "対数 · 0 対応", ChartScale.Focused => "変化を強調 · 縦軸の下端に注意", _ => "線形 · 0 基準" };
        Text(dc, axisLabel, new(plot.Left, 1), MutedBrush);
        var ticks = Math.Clamp((int)plot.Height / 45, 2, 6);
        for (var i = 0; i <= ticks; i++)
        {
            var y = plot.Bottom - i * plot.Height / ticks;
            dc.DrawLine(GridPen, new(plot.Left, y), new(plot.Right, y));
            var value = ChartMath.Inverse(yMin + (yMax - yMin) * i / ticks, scale, metric);
            var rawSpan = ChartMath.Inverse(yMax, scale, metric) - ChartMath.Inverse(yMin, scale, metric);
            var digits = Math.Clamp((int)Math.Ceiling(-Math.Log10(Math.Max(rawSpan, 1e-12))) + 2, 0, 7);
            var label = scale != ChartScale.Logarithmic && rawSpan < 1 ? value.ToString("0." + new string('#', digits), CultureInfo.CurrentCulture) : Number(value);
            Text(dc, label, new(0, y - 8), MutedBrush);
        }
        var xTicks = Math.Clamp((int)plot.Width / 135, 2, 8);
        for (var i = 0; i <= xTicks; i++)
        {
            var time = DateTimeOffset.FromUnixTimeSeconds((long)(Window.Start + Window.Span * i / xTicks)).ToLocalTime();
            var label = Window.Span <= 600 ? time.ToString("HH:mm:ss") : Window.Span <= 43200 ? time.ToString("HH:mm") : time.ToString("M/d HH:mm");
            var x = plot.Left + plot.Width * i / xTicks;
            if (i > 0 && i < xTicks) dc.DrawLine(GridPen, new(x, plot.Top), new(x, plot.Bottom));
            Text(dc, label, new(x - (i == xTicks ? TextWidth(label) : i == 0 ? 0 : TextWidth(label) / 2), plot.Bottom + 6), MutedBrush);
        }
        double X(double time) => plot.Left + (time - Window.Start) * plot.Width / Window.Span;
        double Y(double value) => plot.Bottom - (ChartMath.Transform(value, scale, metric) - yMin) / (yMax - yMin) * plot.Height;
        void Series(bool write, Brush brush)
        {
            // Retain every bucket, including spikes. Missing intervals start a new figure.
            var geometry = new StreamGeometry();
            using (var context = geometry.Open())
            {
                long? previousTime = null;
                foreach (var point in visible)
                {
                    var pair = point.Values(metric);
                    var value = write ? pair.Write : pair.Read;
                    if (value is null) { previousTime = null; continue; }
                    var position = new Point(X(point.UnixTime), Y(value.Value));
                    if (previousTime is null || point.UnixTime - previousTime > (int)interval * 1.5) context.BeginFigure(position, false, false);
                    else context.LineTo(position, true, false);
                    if (visible.Length < plot.Width / 3 || previousTime is null || point.UnixTime - previousTime > (int)interval * 1.5)
                        dc.DrawEllipse(brush, null, position, 1.8, 1.8);
                    previousTime = point.UnixTime;
                }
            }
            geometry.Freeze(); dc.DrawGeometry(null, new Pen(brush, 1.6), geometry);
        }
        dc.PushClip(new RectangleGeometry(plot));
        if (showRead) Series(false, ReadBrush);
        if (showWrite) Series(true, WriteBrush);
        dc.Pop();
        if (values.Length == 0)
            Text(dc, !showRead && !showWrite ? "凡例から表示する系列を選んでください" : visible.Length == 0 && points.Count > 0 ? "集計点が表示範囲外です。縮小すると表示できます" : "この期間の測定データはありません", new(plot.Left + 18, plot.Top + plot.Height / 2 - 8), MutedBrush, 12);

        if (hoverTime is { } timeAt && timeAt >= Window.Start && timeAt <= Window.End)
        {
            var x = X(timeAt);
            dc.DrawLine(new Pen(MutedBrush, 1) { DashStyle = DashStyles.Dash }, new(x, plot.Top), new(x, plot.Bottom));
            var nearest = visible.MinBy(p => Math.Abs(p.UnixTime - timeAt));
            if (nearest is not null && Math.Abs(nearest.UnixTime - timeAt) <= (int)interval * 0.55)
            {
                var pair = nearest.Values(metric);
                dc.PushClip(new RectangleGeometry(plot));
                if (showRead && pair.Read is { } read) dc.DrawEllipse(SurfaceBrush, new Pen(ReadBrush, 2), new(X(nearest.UnixTime), Y(read)), 3.5, 3.5);
                if (showWrite && pair.Write is { } write) dc.DrawEllipse(SurfaceBrush, new Pen(WriteBrush, 2), new(X(nearest.UnixTime), Y(write)), 3.5, 3.5);
                dc.Pop();
            }
        }
        if (selecting && dragOrigin is { } origin && selectionEnd is { } end)
        {
            var left = Math.Clamp(Math.Min(origin.X, end.X), plot.Left, plot.Right);
            var right = Math.Clamp(Math.Max(origin.X, end.X), plot.Left, plot.Right);
            dc.DrawRectangle(SelectionBrush, new Pen(ReadBrush, 1), new Rect(left, plot.Top, right - left, plot.Height));
        }
        if (IsKeyboardFocused) dc.DrawRectangle(null, new Pen(ReadBrush, 1) { DashStyle = DashStyles.Dot }, new Rect(0.5, 0.5, ActualWidth - 1, ActualHeight - 1));
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (ApplyWheel(e.GetPosition(this), e.Delta, Keyboard.Modifiers)) { Focus(); e.Handled = true; }
    }
    internal bool ApplyWheel(Point position, int delta, ModifierKeys modifiers)
    {
        if (!plot.Contains(position) || IsInteracting) return false;
        var factor = Math.Pow(1.25, -Math.Clamp(delta / 120.0, -10, 10));
        if ((modifiers & ModifierKeys.Control) != 0) ZoomVertical(factor, 1 - (position.Y - plot.Top) / plot.Height);
        else ZoomTime(factor, (position.X - plot.Left) / plot.Width);
        return true;
    }
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (!plot.Contains(e.GetPosition(this))) return;
        Focus();
        if (e.ClickCount == 2) { ResetRequested?.Invoke(); e.Handled = true; return; }
        if (!CaptureMouse()) return;
        StartDrag(e.GetPosition(this), Keyboard.Modifiers);
        e.Handled = true;
    }
    internal void StartDrag(Point position, ModifierKeys modifiers)
    {
        dragOrigin = position; dragWindow = Window; dragMin = yMin; dragMax = yMax; dragWasManual = manualY;
        selecting = (modifiers & ModifierKeys.Shift) != 0;
        verticalDrag = !selecting && (modifiers & ModifierKeys.Control) != 0;
        Cursor = selecting ? Cursors.Cross : verticalDrag ? Cursors.SizeNS : Cursors.SizeWE;
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        ContinueDrag(e.GetPosition(this));
    }
    internal void ContinueDrag(Point position)
    {
        if (dragOrigin is { } origin)
        {
            if (selecting) selectionEnd = position;
            else if (verticalDrag)
            {
                var shift = (position.Y - origin.Y) / plot.Height * (dragMax - dragMin);
                yMin = Math.Max(0, dragMin + shift); yMax = yMin + dragMax - dragMin; manualY = true;
            }
            else ChangeWindow(dragWindow.Pan((origin.X - position.X) / plot.Width * dragWindow.Span, Now));
        }
        hoverTime = plot.Contains(position) ? Window.Start + (position.X - plot.Left) / plot.Width * Window.Span : null;
        UpdateReadout(); HoverChanged?.Invoke(hoverTime); InvalidateVisual();
    }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!IsInteracting) return;
        EndDrag(e.GetPosition(this)); e.Handled = true;
    }
    internal void EndDrag(Point end)
    {
        if (dragOrigin is not { } origin) return;
        if (selecting && Math.Abs(end.X - origin.X) >= 6)
        {
            var a = Math.Clamp((origin.X - plot.Left) / plot.Width, 0, 1);
            var b = Math.Clamp((end.X - plot.Left) / plot.Width, 0, 1);
            ChangeWindow(new ChartWindow(dragWindow.Start + Math.Min(a, b) * dragWindow.Span, dragWindow.Start + Math.Max(a, b) * dragWindow.Span).Constrain(Now));
        }
        FinishDrag();
    }
    private void FinishDrag()
    {
        var wasDragging = IsInteracting;
        dragOrigin = selectionEnd = null; selecting = false; Cursor = Cursors.Cross;
        if (IsMouseCaptured) ReleaseMouseCapture();
        InvalidateVisual();
        if (wasDragging) InteractionFinished?.Invoke();
    }
    protected override void OnLostMouseCapture(MouseEventArgs e) { base.OnLostMouseCapture(e); FinishDrag(); }
    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (IsInteracting) return;
        SetHover(null); HoverChanged?.Invoke(null);
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        switch (e.Key)
        {
            case Key.Add: case Key.OemPlus: if (ctrl) ZoomVertical(0.8); else ZoomTime(0.8); break;
            case Key.Subtract: case Key.OemMinus: if (ctrl) ZoomVertical(1.25); else ZoomTime(1.25); break;
            case Key.Left: PanTime(-Window.Span / 5); break;
            case Key.Right: PanTime(Window.Span / 5); break;
            case Key.Home: ResetRequested?.Invoke(); break;
            case Key.Escape:
                if (dragOrigin is not null) { ChangeWindow(dragWindow); yMin = dragMin; yMax = dragMax; manualY = dragWasManual; FinishDrag(); }
                break;
            default: return;
        }
        e.Handled = true;
    }
    public static string Number(double? value) => value is not { } v ? "—" : v == 0 ? "0" :
        Math.Abs(v) >= 1000000 || Math.Abs(v) < 0.01 ? v.ToString("0.###E+0", CultureInfo.CurrentCulture) : v.ToString("0.######", CultureInfo.CurrentCulture);
    private static double NiceMaximum(double value)
    {
        if (value <= 0) return 1;
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(value)));
        return Math.Ceiling(value * 1.05 / magnitude / 2) * magnitude * 2;
    }
    private FormattedText Label(string value, Brush brush, double size = 11) => new(value, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Yu Gothic UI"), size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
    private double TextWidth(string text) => Label(text, MutedBrush).Width;
    private void Text(DrawingContext dc, string value, Point origin, Brush brush, double size = 11) => dc.DrawText(Label(value, brush, size), origin);
}
