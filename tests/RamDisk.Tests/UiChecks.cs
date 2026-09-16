using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Input;
using System.Windows.Threading;
using RamDisk.App;
using RamDisk.Core;

static class UiChecks
{
    public static void Run(string root)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var app = new RamDisk.App.App(startManager: false) { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                app.InitializeComponent();
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
                ThemeManager.Current.Initialize(new ThemePreferenceStore(Path.Combine(root, "ui-appearance.json")));
                var platform = new FakePlatform { IsDriverInstalled = false, IsAdministrator = false };
                var store = new ProfileStore(Path.Combine(root, "ui-profiles.json"));
                var main = new MainWindow(platform, store, autoMount: false, enableMonitoring: false);
                Render(main, Path.Combine(root, "main-empty.png"));
                main.Close();

                store.Save([new DiskProfile { Name = "ビルドキャッシュ", DriveLetter = 'R', SizeMiB = 4096 },
                    new DiskProfile { Name = "一時ファイル", DriveLetter = 'T', SizeMiB = 1024, MountOnAppStart = true }]);
                var populated = new MainWindow(platform, store, autoMount: false, enableMonitoring: false);
                Render(populated, Path.Combine(root, "main-profiles.png"));
                populated.ShowMonitor();
                var monitor = (MonitorView)populated.FindName("MonitorPage");
                var diskChoice = (ComboBox)monitor.FindName("DiskChoice");
                diskChoice.ItemsSource = new[] { new HistoryDisk(Guid.NewGuid(), "ビルドキャッシュ", 'R') };
                diskChoice.SelectedIndex = 0;
                monitor.ShowError("描画テスト · R: ビルドキャッシュ · ファイル I/O を監視中");
                var end = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var history = Enumerable.Range(0, 120).Where(i => i < 48 || i > 62).Select(i =>
                    new HistoryPoint(end - 119 + i, new((long)((Math.Sin(i / 7.0) + 1) * 20_000_000), (i % 31) * 1_000_000, 70 + (i % 23) * 11, (i % 31) * 9), 1, 3L << 30, 4L << 30, 1)).ToArray();
                monitor.Present(new(history, new(20L << 30, 9L << 30, 456789, 234567), end - 86400), HistoryInterval.Second, MonitorMetric.Rate,
                    end - 119, end, "MiB/s", new(Guid.NewGuid(), DateTimeOffset.UtcNow, new(23L << 20, 12L << 20, 184, 96), 1, 3L << 30, 4L << 30));
                Render(populated, Path.Combine(root, "monitor-populated.png"));
                CheckThemeSwitch(populated, monitor, root);
                CheckChartInput(populated, monitor, history, end, root);
                monitor.Present(new([], new(), null), HistoryInterval.Second, MonitorMetric.Rate, end - 119, end, "MiB/s", null);
                Render(populated, Path.Combine(root, "monitor-empty.png"));
                populated.Close();
                CheckAsyncHistory(root);
                CheckThemedDialogs(root);

                var editor = new DiskEditor(null, ['R', 'S', 'T'], platform.Memory, aweallocInstalled: true);
                ((ComboBox)editor.FindName("MemoryInput")).SelectedIndex = 1;
                Render(editor, Path.Combine(root, "editor.png"));
                // Exercise the real WPF click handler in a modal dispatcher without desktop input.
                editor.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
                {
                    try
                    {
                        ((TextBox)editor.FindName("SizeInput")).Text = "-1";
                        CheckEditorTheme(editor, root);
                        var save = Descendants(editor).OfType<Button>().Single(x => x.IsDefault);
                        save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        if (string.IsNullOrEmpty(((TextBlock)editor.FindName("ErrorText")).Text))
                            throw new Exception("Editor accepted negative capacity");
                        ((TextBox)editor.FindName("SizeInput")).Text = "2.5";
                        ((TextBox)editor.FindName("NameInput")).Text = "日本語 UI テスト";
                        save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    }
                    catch (Exception ex) { failure = ex; editor.Close(); }
                }));
                editor.ShowDialog();
                if (failure is not null) throw failure;
                if (editor.Result is not { SizeMiB: 2560, Name: "日本語 UI テスト", MemoryMode: MemoryMode.Physical }) throw new Exception("Editor did not save input and physical memory mode");
                app.Shutdown();
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(30))) throw new TimeoutException("WPF UI checks timed out");
        if (failure is not null) throw new Exception("WPF UI check failed", failure);
    }

    private static void CheckThemeSwitch(MainWindow main, MonitorView monitor, string root)
    {
        var chart = (HistoryChart)monitor.FindName("Chart");
        chart.ZoomTime(0.8); chart.ZoomVertical(0.8);
        var range = chart.Window;
        var dark = ((SolidColorBrush)chart.SurfaceBrush).Color;
        var button = (Button)main.FindName("ThemeButton");
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Render(main, Path.Combine(root, "monitor-light.png"));
        if (ThemeManager.Current.Theme != AppTheme.Light || ((SolidColorBrush)chart.SurfaceBrush).Color == dark || chart.Window != range || !chart.IsVerticalManual)
            throw new Exception("Theme switch did not update the chart or reset its inspected range");
        var preferences = new ThemePreferenceStore(Path.Combine(root, "ui-appearance.json"));
        if (preferences.Load() != AppTheme.Light) throw new Exception("Theme button did not persist light preference");
        ThemeManager.Current.Apply(AppTheme.Dark);
        ThemeManager.Current.Initialize(preferences);
        if (ThemeManager.Current.Theme != AppTheme.Light) throw new Exception("Theme was not restored from saved preference");
        CheckContrast();
        main.ShowMonitor(false); Render(main, Path.Combine(root, "main-light.png"));
        main.ShowMonitor();
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Render(main, Path.Combine(root, "monitor-dark-restored.png"));
        if (ThemeManager.Current.Theme != AppTheme.Dark || ((SolidColorBrush)chart.SurfaceBrush).Color != dark || chart.Window != range)
            throw new Exception("Switching back to dark did not restore palette or preserve range");
        CheckContrast();
        chart.ResetVertical();
    }

    private static void CheckContrast()
    {
        static double Luminance(string key)
        {
            var color = ((SolidColorBrush)Application.Current.FindResource(key)).Color;
            double Channel(byte c) { var value = c / 255.0; return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4); }
            return Channel(color.R) * 0.2126 + Channel(color.G) * 0.7152 + Channel(color.B) * 0.0722;
        }
        foreach (var (foreground, background) in new[] { ("Ink", "Surface"), ("Muted", "Surface"), ("Muted", "WindowBackground"),
            ("ReadSeries", "Surface"), ("WriteSeries", "Surface"), ("OnAccent", "AccentSurface"), ("InfoText", "InfoBackground"), ("Error", "WindowBackground") })
        {
            var a = Luminance(foreground); var b = Luminance(background);
            if ((Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05) < 4.5) throw new Exception($"Low text contrast: {foreground}/{background}");
        }
    }

    private static void CheckThemedDialogs(string root)
    {
        var dialog = new ThemedDialog("ドライブ内のデータは失われます。これは UI の描画・ボタン検証で、実際のディスク操作は行いません。",
            "使用中のディスクを強制解除", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (dialog.Result != MessageBoxResult.No || !Equals(dialog.DefaultButton.Content, "いいえ")) throw new Exception("Themed confirmation changed the safe default");
        Render(dialog, Path.Combine(root, "dialog-dark.png"));
        dialog.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => dialog.DefaultButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent))));
        dialog.ShowDialog();
        if (dialog.Result != MessageBoxResult.No) throw new Exception("Default confirmation did not return No");
        var cancel = new ThemedDialog("必要なファイルを別のドライブへ保存してください。", "ディスクの解除", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
        cancel.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(cancel.Close));
        cancel.ShowDialog();
        if (cancel.Result != MessageBoxResult.Cancel) throw new Exception("Closing confirmation did not cancel");
        ThemeManager.Current.Apply(AppTheme.Light);
        var ok = new ThemedDialog("テーマと確認ダイアログの検証が完了しました。", "検証", MessageBoxButton.OK, MessageBoxImage.Information, MessageBoxResult.OK);
        Render(ok, Path.Combine(root, "dialog-light.png"));
        ok.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => ok.DefaultButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent))));
        ok.ShowDialog();
        if (ok.Result != MessageBoxResult.OK) throw new Exception("Dialog did not return OK");
        ThemeManager.Current.Apply(AppTheme.Dark);
    }

    private static void CheckEditorTheme(DiskEditor editor, string root)
    {
        var combo = (ComboBox)editor.FindName("MemoryInput");
        combo.IsDropDownOpen = true;
        combo.UpdateLayout();
        var popup = (System.Windows.Controls.Primitives.Popup)combo.Template.FindName("PART_Popup", combo);
        var surface = (Border)popup.Child;
        if (((SolidColorBrush)surface.Background).Color != ((SolidColorBrush)Application.Current.FindResource("SurfaceRaised")).Color)
            throw new Exception("Dark dropdown retained a light background");
        var popupSize = new Size(440, 90);
        surface.Measure(popupSize); surface.Arrange(new Rect(popupSize)); surface.UpdateLayout();
        SaveVisual(surface, popupSize, Path.Combine(root, "dropdown-dark.png"), surface.Background);
        combo.IsDropDownOpen = false;
        ThemeManager.Current.Apply(AppTheme.Light);
        Render(editor, Path.Combine(root, "editor-light.png"));
        if (((SolidColorBrush)((TextBox)editor.FindName("NameInput")).Background).Color != ((SolidColorBrush)Application.Current.FindResource("Surface")).Color)
            throw new Exception("Open editor did not follow theme switch");
        ThemeManager.Current.Apply(AppTheme.Dark);
    }

    private static void CheckChartInput(MainWindow main, MonitorView monitor, HistoryPoint[] history, long end, string root)
    {
        static void Check(bool value, string message) { if (!value) throw new Exception(message); }
        var chart = (HistoryChart)monitor.FindName("Chart");
        var second = (HistoryChart)monitor.FindName("SecondaryChart");
        var bounds = chart.PlotBounds;
        Point At(double x, double y) => new(bounds.Left + bounds.Width * x, bounds.Top + bounds.Height * y);
        var original = chart.Window;
        var originalAnchor = original.Start + original.Span * 0.25;
        Check(chart.ApplyWheel(At(0.25, 0.5), 120, ModifierKeys.None), "Plot wheel was not handled");
        Check(Math.Abs(chart.Window.Span - original.Span * 0.8) < 0.001, "Wheel did not zoom time");
        Check(Math.Abs(chart.Window.Start + chart.Window.Span * 0.25 - originalAnchor) < 0.001, "Wheel moved the pointer's time");
        Check(chart.Window == second.Window, "Comparison time axis did not follow zoom");
        Check(((TextBlock)monitor.FindName("RangeText")).Text.Contains("追従停止"), "Navigation did not pause live mode");
        var beforeDrag = chart.Window;
        chart.StartDrag(At(0.5, 0.5), ModifierKeys.None);
        chart.ContinueDrag(At(0.75, 0.5));
        chart.EndDrag(At(0.75, 0.5));
        Check(chart.Window.End < beforeDrag.End && Math.Abs(chart.Window.Span - beforeDrag.Span) < 0.001, "Drag did not pan backwards");
        beforeDrag = chart.Window;
        chart.StartDrag(At(0.2, 0.5), ModifierKeys.Shift);
        chart.ContinueDrag(At(0.8, 0.5));
        Check(chart.Window == beforeDrag, "Selection changed range before release");
        chart.EndDrag(At(0.8, 0.5));
        Check(Math.Abs(chart.Window.Span - beforeDrag.Span * 0.6) < 0.001 && chart.Window == second.Window, "Range selection did not zoom both charts");
        var beforeVertical = chart.Window;
        chart.ApplyWheel(At(0.5, 0.6), 120, ModifierKeys.Control);
        Check(chart.IsVerticalManual && chart.Window == beforeVertical && !second.IsVerticalManual, "Vertical zoom changed time or the other chart");
        chart.SetData(history, HistoryInterval.Second, MonitorMetric.Rate, end - 119, end, "MiB/s");
        Check(chart.IsVerticalManual, "A refresh reset manual vertical zoom");
        chart.StartDrag(At(0.5, 0.5), ModifierKeys.Control);
        chart.ContinueDrag(At(0.5, 0.7)); chart.EndDrag(At(0.5, 0.7));
        Check(chart.IsVerticalManual && chart.Window.Span == 119, "Vertical drag changed the time span");
        chart.ResetVertical();
        Check(!chart.IsVerticalManual, "Automatic vertical range did not resume");
        Check(!chart.ApplyWheel(new(0, 0), 120, ModifierKeys.None), "Wheel outside plot was consumed");

        // Small activity, true zeroes, a large spike, and a missing segment all share a log plot.
        var logPoints = Enumerable.Range(0, 120).Where(i => i < 45 || i > 59).Select(i => new HistoryPoint(end - 119 + i,
            new(i < 10 ? 0 : i == 80 ? 1_000_000_000 : (i % 20 + 1) * 1024, i < 10 ? 0 : (i % 7 + 1) * 2048, i % 20, i % 7), 1, (3L << 30) + i * 1000, 4L << 30, 1)).ToArray();
        monitor.Present(new(logPoints, new(20L << 30, 9L << 30), end - 86400), HistoryInterval.Second, MonitorMetric.Rate, end - 119, end, "MiB/s", null);
        ((ComboBox)monitor.FindName("ScaleChoice")).SelectedIndex = 1;
        chart.SetHover(end - 39); second.SetHover(end - 39);
        main.Width = 1100; main.Height = 800;
        Render(main, Path.Combine(root, "monitor-log-minimum.png"));
        Check(((TextBlock)monitor.FindName("ReadoutText")).Text.Contains("953.674"), "Log hover did not preserve original units");
        chart.SetHover(end - 69);
        Render(main, Path.Combine(root, "monitor-log-gap.png"));
        Check(((TextBlock)monitor.FindName("ReadoutText")).Text.Contains("未測定"), "A missing log sample became zero");
        chart.SetHover(end - 115);
        Render(main, Path.Combine(root, "monitor-log-zero.png"));
        Check(((TextBlock)monitor.FindName("ReadoutText")).Text.Contains("読: 0 MiB/s"), "A true log zero disappeared");
        var check = (CheckBox)monitor.FindName("WriteVisible");
        check.IsChecked = false; check.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        ((ComboBox)monitor.FindName("MetricChoice")).SelectedIndex = 6;
        ((ComboBox)monitor.FindName("SecondaryMetricChoice")).SelectedIndex = 5;
        ((ComboBox)monitor.FindName("ScaleChoice")).SelectedIndex = 2;
        chart.SetHover(null); second.SetHover(null);
        Render(main, Path.Combine(root, "monitor-focused-capacity.png"));
        ((Button)monitor.FindName("ExpandButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(((ColumnDefinition)main.FindName("SidebarColumn")).Width.Value == 0, "Wide mode did not hide sidebar");
        check = (CheckBox)monitor.FindName("SecondaryVisible");
        check.IsChecked = false; check.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        Render(main, Path.Combine(root, "monitor-wide-single.png"));
        Check(((Grid)monitor.FindName("SecondaryPanel")).Visibility == Visibility.Collapsed, "Comparison chart did not collapse");
        Check(chart.ActualHeight > 300, "Single chart did not reclaim comparison space");
        ((Button)monitor.FindName("ExpandButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        check.IsChecked = true; check.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        ((ComboBox)monitor.FindName("MetricChoice")).SelectedIndex = 0;
        ((ComboBox)monitor.FindName("SecondaryMetricChoice")).SelectedIndex = 3;
        ((ComboBox)monitor.FindName("ScaleChoice")).SelectedIndex = 0;
        check = (CheckBox)monitor.FindName("WriteVisible");
        check.IsChecked = true; check.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
    }

    private static void CheckAsyncHistory(string root)
    {
        static void Check(bool value, string message) { if (!value) throw new Exception(message); }
        static void Complete(Task task)
        {
            if (!task.IsCompleted)
            {
                var frame = new DispatcherFrame();
                var dispatcher = Dispatcher.CurrentDispatcher;
                _ = task.ContinueWith(_ => dispatcher.BeginInvoke(new Action(() => frame.Continue = false)), TaskScheduler.Default);
                Dispatcher.PushFrame(frame);
            }
            task.GetAwaiter().GetResult();
        }
        var profile = new DiskProfile { Name = "履歴表示テスト" };
        var store = new MonitorStore(Path.Combine(root, "chart-refresh.db"));
        store.Register([profile]);
        var now = DateTimeOffset.UtcNow;
        store.Append(Enumerable.Range(1, 1000).Select(i => new MonitorSample(profile.Id, now.AddSeconds(-i), new(1024, 2048, 1, 2), 1, 100, 1000)).ToArray());
        var view = new MonitorView();
        var service = new MonitoringService(new FakePlatform(), store);
        view.Attach(store, service);
        var inFlight = view.RefreshAsync();
        ((ComboBox)view.FindName("RangeChoice")).SelectedIndex = 2;
        ((ComboBox)view.FindName("MetricChoice")).SelectedIndex = 3;
        var chart = (HistoryChart)view.FindName("Chart");
        chart.PanTime(-600);
        chart.ZoomTime(0.5);
        var requested = chart.Window;
        Complete(inFlight);
        Check(chart.Window == requested, "An old database result overwrote the latest pan/zoom");
        Complete(view.RefreshAsync());
        Check(chart.Window == requested, "Periodic refresh resumed live mode during inspection");
        Check(((TextBlock)view.FindName("ReadTotalText")).Text == "1000 KiB", "History totals were not loaded from SQLite");
        Check(((TextBlock)view.FindName("PeakText")).Text.Contains("回/s"), "An old metric survived a pending database query");
        chart.ZoomVertical(0.5);
        store.Append([new(profile.Id, now.AddSeconds(1), new(4096, 4096, 1, 1), 1, 200, 1000)]);
        Complete(view.RefreshAsync());
        Check(chart.IsVerticalManual && chart.Window == requested, "New samples reset the inspection window or vertical scale");
        var width = chart.Window.Span;
        ((Button)view.FindName("LiveButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Complete(view.RefreshAsync());
        Check(Math.Abs(chart.Window.Span - width) < 0.001 && chart.Window.End >= now.ToUnixTimeSeconds(), "Resume live lost the zoom width");
        Check(((TextBlock)view.FindName("ReadTotalText")).Text == "1004 KiB", "History refresh missed newly appended totals");
        Complete(service.StopAsync());
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            yield return child;
            foreach (var next in Descendants(child)) yield return next;
        }
    }

    private static void Render(Window window, string path)
    {
        // Render the actual WPF content, including the layout system and data templates.
        var content = (FrameworkElement)window.Content;
        var height = window.Height;
        if (double.IsNaN(height))
        {
            content.Measure(new Size(window.Width - 16, double.PositiveInfinity));
            height = Math.Min(window.MaxHeight, content.DesiredSize.Height + 39);
        }
        var size = new Size(window.Width - 16, height - 39);
        content.Measure(size);
        content.Arrange(new Rect(size));
        content.UpdateLayout();
        SaveVisual(content, size, path, window.Background);
    }

    private static void SaveVisual(Visual content, Size size, string path, Brush backgroundBrush)
    {
        var bitmap = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);
        var background = new DrawingVisual();
        using (var drawing = background.RenderOpen()) drawing.DrawRectangle(backgroundBrush, null, new Rect(size));
        bitmap.Render(background);
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        encoder.Save(file);
    }
}
