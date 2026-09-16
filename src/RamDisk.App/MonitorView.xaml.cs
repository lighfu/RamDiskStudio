using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using RamDisk.Core;

namespace RamDisk.App;

public partial class MonitorView : UserControl
{
    private sealed record MetricOption(MonitorMetric Value, string Label, string Unit) { public override string ToString() => Label; }
    private sealed record IntervalOption(HistoryInterval? Value, string Label) { public override string ToString() => Label; }
    private sealed record RangeOption(double Seconds, string Label) { public override string ToString() => Label; }
    private static readonly MetricOption[] Metrics = [
        new(MonitorMetric.Rate, "読み書き速度", "MiB/s"), new(MonitorMetric.Bytes, "読み書き量 / 区間", "MiB"),
        new(MonitorMetric.Operations, "アクセス回数 / 区間", "回"), new(MonitorMetric.Iops, "IOPS", "回/s"),
        new(MonitorMetric.AverageSize, "平均アクセスサイズ", "KiB/回"), new(MonitorMetric.UsedBytes, "使用容量", "GiB"),
        new(MonitorMetric.UsagePercent, "使用率", "%") ];
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer navigationTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private MonitorStore? store;
    private MonitoringService? service;
    private bool ready, updating, pending, live = true, expanded;
    private int revision;
    private ChartWindow window = new ChartWindow(Now - 120, Now);
    private MonitorHistory? lastHistory;
    private MonitorSample? lastSample;
    private HistoryInterval lastInterval;
    private GridLength secondaryHeight = new(1, GridUnitType.Star);
    private static double Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    public event Action<bool>? ExpandedChanged;

    public MonitorView()
    {
        InitializeComponent();
        MetricChoice.ItemsSource = SecondaryMetricChoice.ItemsSource = Metrics;
        IntervalChoice.ItemsSource = new[] { new IntervalOption(null, "自動"), new(HistoryInterval.Second, "毎秒"), new(HistoryInterval.Minute, "毎分"),
            new(HistoryInterval.Hour, "毎時"), new(HistoryInterval.Day, "毎日") };
        RangeChoice.ItemsSource = new[] { new RangeOption(120, "2 分"), new(600, "10 分"), new(3600, "1 時間"), new(21600, "6 時間"),
            new(86400, "24 時間"), new(7 * 86400, "7 日"), new(30 * 86400, "30 日"), new(365 * 86400, "1 年") };
        MetricChoice.SelectedIndex = IntervalChoice.SelectedIndex = RangeChoice.SelectedIndex = ScaleChoice.SelectedIndex = 0;
        SecondaryMetricChoice.SelectedIndex = 3;
        Chart.WindowChanged += Navigate; SecondaryChart.WindowChanged += Navigate;
        Chart.HoverChanged += SecondaryChart.SetHover; SecondaryChart.HoverChanged += Chart.SetHover;
        Chart.ReadoutChanged += value => { ReadoutText.Text = value; ReadoutText.ToolTip = value; };
        SecondaryChart.ReadoutChanged += value => { SecondaryReadout.Text = value; SecondaryReadout.ToolTip = value; };
        Chart.ResetRequested += Reset; SecondaryChart.ResetRequested += Reset;
        Chart.InteractionFinished += RequestRefresh; SecondaryChart.InteractionFinished += RequestRefresh;
        navigationTimer.Tick += async (_, _) => { navigationTimer.Stop(); await RefreshAsync(); };
        timer.Tick += async (_, _) => { if (IsVisible && !Chart.IsInteracting && !SecondaryChart.IsInteracting) await RefreshAsync(); };
        Loaded += async (_, _) => { timer.Start(); await RefreshAsync(); };
        Unloaded += (_, _) => { timer.Stop(); navigationTimer.Stop(); };
        IsVisibleChanged += async (_, _) => { if (IsVisible) await RefreshAsync(); };
        ready = true;
        ApplyAppearance();
        UpdateNavigation();
    }

    public void Attach(MonitorStore history, MonitoringService monitor) { store = history; service = monitor; }
    public void ShowError(string error) => StatusText.Text = error;
    private void Choice_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!ready) return;
        if (ReferenceEquals(sender, DiskChoice)) { Chart.ResetVertical(); SecondaryChart.ResetVertical(); }
        if (ReferenceEquals(sender, IntervalChoice) && IntervalChoice.SelectedItem is IntervalOption { Value: { } step } && window.Span < (int)step * 2)
            window = new ChartWindow(window.End - (int)step * 30, window.End).Constrain(Now);
        ApplyAppearance();
        if (lastHistory is not null && !ReferenceEquals(sender, DiskChoice) && !ReferenceEquals(sender, IntervalChoice))
            Present(lastHistory, lastInterval, ((MetricOption)MetricChoice.SelectedItem).Value, (long)Math.Ceiling(window.Start), (long)Math.Floor(window.End), ((MetricOption)MetricChoice.SelectedItem).Unit, lastSample);
        RequestRefresh();
    }
    private void Range_Changed(object sender, SelectionChangedEventArgs e) { if (ready) Reset(); }
    private void Scale_Changed(object sender, SelectionChangedEventArgs e) { if (ready) ApplyAppearance(); }
    private void Appearance_Click(object sender, RoutedEventArgs e) => ApplyAppearance();
    private void ApplyAppearance()
    {
        if (!ready) return;
        var scale = (ChartScale)Math.Max(0, ScaleChoice.SelectedIndex);
        Chart.SetAppearance(scale, ReadVisible.IsChecked == true, WriteVisible.IsChecked == true);
        SecondaryChart.SetAppearance(scale, ReadVisible.IsChecked == true, WriteVisible.IsChecked == true);
        ReadVisible.Content = MetricChoice.SelectedItem is MetricOption { Value: MonitorMetric.UsedBytes or MonitorMetric.UsagePercent } ? "● 使用量 / 読取" : "● 読み取り";
        ScaleChoice.ToolTip = "線形: 0 を基準に比較\n対数: log10(1 + 値 / 基準値)。0 も表示でき、小さな値を拡大。目盛とカーソルは元の単位。\n変化を強調: 表示中の最小値～最大値に縦軸を合わせる（0 始まりとは限りません）。\nCtrl＋ホイール / ドラッグで、各グラフの縦軸を個別に調整できます。";
    }
    private void Navigate(ChartWindow value)
    {
        live = false; window = value;
        Chart.SetWindow(window); SecondaryChart.SetWindow(window);
        // Do not leave totals for the previous range beside the preview of the new one.
        PeriodText.Text = "表示期間を集計中…"; PeakText.Text = "";
        UpdateNavigation(); RequestRefresh();
    }
    private void RequestRefresh()
    {
        revision++; pending = true;
        navigationTimer.Stop(); navigationTimer.Start();
    }
    private void Previous_Click(object sender, RoutedEventArgs e) => Navigate(window.Pan(-window.Span / 2, Now));
    private void Next_Click(object sender, RoutedEventArgs e) => Navigate(window.Pan(window.Span / 2, Now));
    private void ZoomIn_Click(object sender, RoutedEventArgs e) => Navigate(window.Zoom(0.5, 0.5, Now));
    private void ZoomOut_Click(object sender, RoutedEventArgs e) => Navigate(window.Zoom(2, 0.5, Now));
    private void Now_Click(object sender, RoutedEventArgs e)
    {
        live = true; window = window.Live(Now); UpdateNavigation(); RequestRefresh();
    }
    private void Reset_Click(object sender, RoutedEventArgs e) => Reset();
    private void Reset()
    {
        var span = (RangeChoice.SelectedItem as RangeOption)?.Seconds ?? 120;
        if (IntervalChoice.SelectedItem is IntervalOption { Value: { } step }) span = Math.Max(span, (int)step * 2);
        window = new ChartWindow(Now - span, Now); live = true;
        Chart.ResetVertical(); SecondaryChart.ResetVertical();
        Chart.SetWindow(window); SecondaryChart.SetWindow(window);
        UpdateNavigation(); RequestRefresh();
    }
    private void AutoY_Click(object sender, RoutedEventArgs e) { Chart.ResetVertical(); SecondaryChart.ResetVertical(); }
    private void Secondary_Click(object sender, RoutedEventArgs e)
    {
        var visible = SecondaryVisible.IsChecked == true;
        if (!visible) secondaryHeight = SecondaryRow.Height;
        SecondaryPanel.Visibility = ChartSplitter.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        SecondaryRow.MinHeight = visible ? 145 : 0;
        SecondaryRow.Height = visible ? secondaryHeight : new(0);
        SplitterRow.Height = new(visible ? 7 : 0);
    }
    private void Expand_Click(object sender, RoutedEventArgs e)
    {
        expanded = !expanded; ExpandButton.Content = expanded ? "サイドバーを戻す" : "広く表示";
        ExpandedChanged?.Invoke(expanded);
    }
    private void Theme_Click(object sender, RoutedEventArgs e) => ThemeManager.Current.Toggle(Window.GetWindow(this));
    private void UpdateNavigation()
    {
        LiveButton.Content = live ? "● ライブ" : "▶ ライブ";
        LiveButton.SetResourceReference(BackgroundProperty, live ? "AccentSurface" : "Surface");
        LiveButton.SetResourceReference(ForegroundProperty, live ? "OnAccent" : "Ink");
        NextButton.IsEnabled = window.End < Now - 1;
        var step = (IntervalChoice.SelectedItem as IntervalOption)?.Value ?? ChartMath.AutomaticInterval(window, Now);
        var duration = TimeSpan.FromSeconds(window.Span);
        var span = duration.TotalDays >= 1 ? $"{duration.TotalDays:0.##} 日" : duration.TotalHours >= 1 ? $"{duration.TotalHours:0.##} 時間" : $"{duration.TotalMinutes:0.##} 分";
        RangeText.Text = $"{(live ? "追従中" : "過去を表示 · 追従停止")}  {DateTimeOffset.FromUnixTimeSeconds((long)window.Start).ToLocalTime():yyyy/MM/dd HH:mm:ss} ～ {DateTimeOffset.FromUnixTimeSeconds((long)window.End).ToLocalTime():MM/dd HH:mm:ss}  ·  幅 {span}  ·  {(IntervalChoice.SelectedIndex == 0 ? "自動集計: " : "集計: ")}{IntervalLabel(step)}";
    }
    private static string IntervalLabel(HistoryInterval step) => step switch
    { HistoryInterval.Second => "毎秒", HistoryInterval.Minute => "毎分", HistoryInterval.Hour => "毎時", _ => "毎日" };

    public async Task RefreshAsync()
    {
        if (!ready || store is null || service is null) return;
        pending = true;
        if (Chart.IsInteracting || SecondaryChart.IsInteracting) return;
        if (updating) return;
        updating = true;
        try
        {
            // A changed selection/viewport supersedes in-flight results and is queried immediately afterwards.
            while (pending)
            {
                pending = false;
                StatusText.Text = service.Status;
                StatusText.ToolTip = service.Status + $"\n名前未解決: {service.UnresolvedEvents:N0} 件（Windows 全体）\n開始前から開いているファイルは開き直してください。";
                var selected = (DiskChoice.SelectedItem as HistoryDisk)?.Id;
                var disks = await Task.Run(store.Disks);
                if (!disks.SequenceEqual(DiskChoice.Items.Cast<HistoryDisk>()))
                {
                    DiskChoice.ItemsSource = disks;
                    DiskChoice.SelectedItem = disks.FirstOrDefault(x => x.Id == selected) ?? disks.FirstOrDefault();
                }
                if (live && !Chart.IsInteracting && !SecondaryChart.IsInteracting) window = window.Live(Now);
                var requestedWindow = window;
                var requestRevision = revision;
                var id = (DiskChoice.SelectedItem as HistoryDisk)?.Id;
                var metric = (MetricOption)MetricChoice.SelectedItem;
                var step = ((IntervalOption)IntervalChoice.SelectedItem).Value ?? ChartMath.AutomaticInterval(window, Now);
                var start = (long)Math.Ceiling(window.Start);
                var end = (long)Math.Floor(window.End);
                var history = id is null ? new MonitorHistory([], new(), null) : await Task.Run(() => store.Read(id.Value, step,
                    DateTimeOffset.FromUnixTimeSeconds(start), DateTimeOffset.FromUnixTimeSeconds(end)));
                if (requestRevision != revision) { pending = true; continue; }
                if (Chart.IsInteracting || SecondaryChart.IsInteracting) { pending = true; return; }
                var sample = service.Latest.FirstOrDefault(x => x.DiskId == id);
                Present(history, step, metric.Value, start, end, metric.Unit, sample);
                window = requestedWindow;
                Chart.SetWindow(window); SecondaryChart.SetWindow(window);
                UpdateNavigation();
            }
        }
        catch (Exception ex) { StatusText.Text = "履歴を表示できません: " + ex.Message; }
        finally { updating = false; }
    }

    // Rendering tests use this same presentation path; null samples never become artificial zeroes.
    public void Present(MonitorHistory history, HistoryInterval interval, MonitorMetric metric, long start, long end, string unit, MonitorSample? current)
    {
        window = new(start, end); lastHistory = history; lastSample = current; lastInterval = interval;
        Chart.SetData(history.Points, interval, metric, start, end, unit);
        var secondary = (MetricOption)SecondaryMetricChoice.SelectedItem;
        SecondaryChart.SetData(history.Points, interval, secondary.Value, start, end, secondary.Unit);
        var currentFresh = current is not null && DateTimeOffset.UtcNow - current.Time < TimeSpan.FromSeconds(5);
        var ioFresh = currentFresh && current!.IoSeconds > 0;
        ReadRateText.Text = ioFresh ? (current!.Io.ReadBytes / current.IoSeconds / 1048576).ToString("N2") : "—";
        WriteRateText.Text = ioFresh ? (current!.Io.WriteBytes / current.IoSeconds / 1048576).ToString("N2") : "—";
        IopsText.Text = ioFresh ? ((current!.Io.ReadOps + current.Io.WriteOps) / current.IoSeconds).ToString("N0") : "—";
        CapacityText.Text = currentFresh && current!.UsedBytes is { } used && current.TotalBytes is > 0 ? $"{used * 100.0 / current.TotalBytes:0.0}%" : "—";
        CapacityDetail.Text = currentFresh && current!.UsedBytes is { } u && current.TotalBytes is { } t ? $"{Bytes(u)} / {Bytes(t)}" : "使用 / 総容量";
        ReadTotalText.Text = Bytes(history.Lifetime.ReadBytes); WriteTotalText.Text = Bytes(history.Lifetime.WriteBytes);
        ReadOpsText.Text = $"{history.Lifetime.ReadOps:N0} 回"; WriteOpsText.Text = $"{history.Lifetime.WriteOps:N0} 回";
        var lifetime = history.FirstTime is { } first ? $"記録開始: {DateTimeOffset.FromUnixTimeSeconds(first).ToLocalTime():yyyy/MM/dd HH:mm}\nアプリ再起動後も累計・履歴を保持" : "このアプリで観測できたアクセスの合計";
        ReadTotalText.ToolTip = WriteTotalText.ToolTip = lifetime;
        var visible = history.Points.Where(p => p.UnixTime >= start && p.UnixTime <= end).ToArray();
        var total = visible.Aggregate(new IoCounts(), (sum, point) => sum + point.Io);
        var seconds = visible.Sum(x => x.IoSeconds);
        PeriodText.Text = $"表示内の集計点: 読 {Bytes(total.ReadBytes)} / {total.ReadOps:N0} 回  ·  書 {Bytes(total.WriteBytes)} / {total.WriteOps:N0} 回  ·  観測 {seconds:N1} 秒";
        var values = visible.Select(p => p.Values(metric)).ToArray();
        double? Peak(bool write) => values.Select(p => write ? p.Write : p.Read).Where(v => v.HasValue).DefaultIfEmpty(null).Max();
        var capacity = metric is MonitorMetric.UsagePercent or MonitorMetric.UsedBytes;
        PeakText.Text = $"区間値の最大: {(capacity ? "使用" : "読")} {HistoryChart.Number(Peak(false))} {unit}" + (capacity ? "" : $" / 書 {HistoryChart.Number(Peak(true))} {unit}") +
            (seconds > 0 ? $"  ·  期間平均速度: 読 {total.ReadBytes / seconds / 1048576:0.###} / 書 {total.WriteBytes / seconds / 1048576:0.###} MiB/s" : "  ·  未測定は空白、無アクセスは 0");
        UpdateNavigation();
    }

    private void Help_Click(object sender, RoutedEventArgs e) => ThemedMessageBox.Show(Window.GetWindow(this),
        "【グラフの操作】\nホイール: カーソル位置を中心に時間を拡大 / 縮小\nドラッグ: 過去・未来へ移動（操作するとライブ追従を停止）\nCtrl＋ホイール: 縦軸の拡大 / 縮小\nCtrl＋ドラッグ: 縦軸を移動\nShift＋ドラッグ: 選んだ期間を拡大\nダブルクリック / Home: 選択中の期間プリセットにリセット\nEsc: ドラッグを取り消す\n＋ / −、← / →: キーボードでも拡縮・移動\n「ライブ」: 表示幅を保って最新に追従\n上下の境界をドラッグ: グラフの高さを変更\n\n【表示】\n対数（0 対応）: 小さい値を拡大。軸の間隔は均等な差を意味しません。\n変化を強調: 表示中の値に縦軸を合わせます。0 始まりとは限りません。\n集計「自動」: 表示幅と履歴の保存期間に合わせて秒・分・時・日を選択。\n毎日など粗い集計を細かく拡大しても、元の細かい変動は復元しません。\n期間合計は、開始時刻が画面内にある集計点を丸ごと合算します。\n\n【測定の意味】\nアプリ起動中のみ記録。未測定は空白、無アクセスは 0。\nキャッシュを含むファイル I/O 要求を集計（ページング除外）。成功バイト数・物理メモリ帯域ではありません。\n監視開始前から開いているファイルは開き直してください。\n保存期間: 秒 2 時間 / 分 7 日 / 時 90 日 / 日 730 日。\n時刻は現地時間。日の区切りは UTC（日本時間 09:00）。\n累計・履歴は再起動後も保持。累計の値にカーソルを合わせると記録開始日時を表示。",
        "モニターの操作・測定", MessageBoxButton.OK, MessageBoxImage.Information);

    private static string Bytes(long value) => value >= 1L << 40 ? $"{value / (double)(1L << 40):0.##} TiB" :
        value >= 1L << 30 ? $"{value / (double)(1L << 30):0.##} GiB" : value >= 1L << 20 ? $"{value / (double)(1L << 20):0.##} MiB" : $"{value / 1024.0:0.##} KiB";
}
