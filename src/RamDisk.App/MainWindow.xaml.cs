using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using RamDisk.Core;

namespace RamDisk.App;

public partial class MainWindow : Window
{
    private readonly IDiskPlatform platform;
    private readonly DiskManager manager;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(3) };
    private bool busy;
    private MonitoringService? monitoring;
    private bool closingMonitor;
    private bool monitorStopped;
    private readonly Queue<string> activity = new();

    public MainWindow() : this(new WindowsDiskPlatform(), new ProfileStore(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RamDiskStudio", "profiles.json"))) { }

    public MainWindow(IDiskPlatform diskPlatform, ProfileStore store, bool autoMount = true, bool enableMonitoring = true)
    {
        InitializeComponent();
        ThemeManager.Attach(this);
        MonitorPage.ExpandedChanged += expanded =>
        {
            Sidebar.Visibility = expanded ? Visibility.Collapsed : Visibility.Visible;
            SidebarColumn.Width = new GridLength(expanded ? 0 : 204);
        };
        platform = diskPlatform;
        manager = new DiskManager(platform, store);
        if (enableMonitoring)
        {
            try
            {
                var history = new MonitorStore(Path.Combine(Path.GetDirectoryName(store.FilePath)!, "monitoring.db"));
                monitoring = new MonitoringService(platform, history);
                MonitorPage.Attach(history, monitoring);
            }
            catch (Exception ex) { MonitorPage.ShowError("監視履歴を開けません: " + ex.Message); }
        }
        RefreshSafely();
        timer.Tick += (_, _) => { if (!busy) RefreshSafely(); };
        Loaded += async (_, _) =>
        {
            RefreshSafely();
            timer.Start();
            monitoring?.Start();
            if (!autoMount || !platform.IsAdministrator || !platform.IsDriverInstalled) return;
            foreach (var profile in manager.Profiles.Where(x => x.MountOnAppStart).ToArray())
            {
                if (platform.Inspect(profile.DriveLetter).IsOwnedBy(profile)) continue;
                await RunOperation($"{profile.MountPoint} を自動作成しています…", () => manager.MountAsync(profile.Id), $"{profile.MountPoint} を作成しました。");
            }
        };
        Closed += (_, _) => timer.Stop();
    }

    private DiskRow? Selected => DiskList.SelectedItem as DiskRow;

    private void RefreshSafely()
    {
        try { Refresh(); }
        catch (Exception ex) { Log($"状態の更新に失敗しました: {ex.Message}"); }
    }

    private void Refresh(Guid? select = null)
    {
        select ??= Selected?.Profile.Id;
        var memory = platform.GetMemory();
        MemoryAvailableText.Text = $"{memory.AvailableBytes / 1073741824.0:F1} GiB";
        MemoryDetailText.Text = $"合計 {memory.TotalBytes / 1073741824.0:F1} GiB · 使用中 {memory.UsedPercent:F0}%";
        MemoryBar.Value = memory.UsedPercent;
        var rows = manager.Profiles.OrderBy(x => x.DriveLetter).Select(x => new DiskRow(x, platform.Inspect(x.DriveLetter))).ToArray();
        monitoring?.SetProfiles(manager.Profiles);
        DiskList.ItemsSource = rows;
        DiskList.SelectedItem = rows.FirstOrDefault(x => x.Profile.Id == select) ?? rows.FirstOrDefault();
        EmptyState.Visibility = rows.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        var mounted = rows.Where(x => x.IsMounted).ToArray();
        MountedCountText.Text = mounted.Length.ToString();
        ProfileCountText.Text = $"保存済み設定  {rows.Length} 件";
        AllocatedText.Text = $"{mounted.Sum(x => x.Profile.SizeBytes) / 1073741824.0:0.##} GiB";
        SetupBanner.Visibility = platform.IsDriverInstalled && platform.IsAdministrator ? Visibility.Collapsed : Visibility.Visible;
        DriverButton.Visibility = platform.IsDriverInstalled ? Visibility.Collapsed : Visibility.Visible;
        AdminButton.Visibility = platform.IsAdministrator ? Visibility.Collapsed : Visibility.Visible;
        SetupTitle.Text = !platform.IsDriverInstalled ? "ドライバーのセットアップが必要です" : "設定モードで起動しています";
        SetupDescription.Text = !platform.IsDriverInstalled ? "ImDisk を導入すると、RAM ドライブを作成できます。設定は先に保存できます。" : "ドライブの作成・解除には、管理者として再起動してください。";
        UpdateActions();
    }

    private void UpdateActions()
    {
        if (MountButton is null) return;
        var row = Selected;
        AddButton.IsEnabled = !busy;
        RefreshButton.IsEnabled = !busy;
        DriverButton.IsEnabled = !busy;
        AdminButton.IsEnabled = !busy;
        DiskList.IsEnabled = !busy;
        EditButton.IsEnabled = !busy && row is { Snapshot.Exists: false };
        DeleteButton.IsEnabled = !busy && row is { IsMounted: false };
        OpenButton.IsEnabled = !busy && row is { IsMounted: true, Snapshot.IsReady: true };
        MountButton.Content = row?.IsMounted == true ? "解除" : "作成";
        MountButton.IsEnabled = !busy && row is not null && (!row.Snapshot.Exists || row.IsMounted) && platform.IsDriverInstalled && platform.IsAdministrator;
        SelectionDetail.Text = row is null ? "ディスクを選択してください" : row.IsMounted
            ? $"{row.Profile.MountPoint}  空き {row.Snapshot.FreeBytes / 1073741824.0:F2} GiB"
            : row.Snapshot.Exists ? "この文字は別のドライブが使用中です" : "設定を保存済み · 作成できます";
        BusyBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Selection_Changed(object sender, SelectionChangedEventArgs e) => UpdateActions();
    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshSafely();
    private void Disks_Click(object sender, RoutedEventArgs e) => ShowMonitor(false);
    private void Monitor_Click(object sender, RoutedEventArgs e) => ShowMonitor(true);
    private void Theme_Click(object sender, RoutedEventArgs e) => ThemeManager.Current.Toggle(this);
    public void ShowMonitor(bool visible = true)
    {
        DisksPage.Visibility = visible ? Visibility.Collapsed : Visibility.Visible;
        MonitorPage.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (visible) { DisksNav.Background = Brushes.Transparent; MonitorNav.SetResourceReference(BackgroundProperty, "SidebarSelected"); }
        else { MonitorNav.Background = Brushes.Transparent; DisksNav.SetResourceReference(BackgroundProperty, "SidebarSelected"); }
    }
    private void Add_Click(object sender, RoutedEventArgs e) => EditProfile(null);
    private void Edit_Click(object sender, RoutedEventArgs e) { if (Selected is { } row) EditProfile(row.Profile); }

    private void EditProfile(DiskProfile? profile)
    {
        try
        {
            var free = Enumerable.Range('D', 23).Select(x => (char)x)
                .Where(x => !platform.Inspect(x).Exists && !manager.Profiles.Any(p => p.Id != profile?.Id && p.DriveLetter == x));
            var editor = new DiskEditor(profile, free, platform.GetMemory(), platform.IsPhysicalMemoryDriverInstalled) { Owner = this };
            if (editor.ShowDialog() != true || editor.Result is not { } result) return;
            manager.Save(result);
            Log($"{result.MountPoint}「{result.Name}」の設定を保存しました。");
            Refresh(result.Id);
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private async void Mount_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } row) return;
        if (row.IsMounted)
        {
            if (ThemedMessageBox.Show(this, $"{row.Profile.MountPoint} を解除します。\nドライブ内のデータはすべて失われます。必要なファイルは別のドライブに保存してください。",
                "ディスクの解除", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) != MessageBoxResult.OK) return;
            await RunOperation($"{row.Profile.MountPoint} を解除しています…", async () =>
            {
                try { await manager.UnmountAsync(row.Profile.Id); }
                catch (DiskBusyException)
                {
                    if (ThemedMessageBox.Show(this,
                        $"{row.Profile.MountPoint} のロックを取得できませんでした。\nファイルや他のアプリがドライブを使用している可能性があります。\n\n強制解除すると、ドライブ内のデータと未保存の変更が失われます。\n必要なファイルを別のドライブに保存したうえで、強制解除しますか？",
                        "使用中のディスクを強制解除", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
                        throw new OperationCanceledException("強制解除をキャンセルしました。ディスクは接続されたままです。");
                    await manager.UnmountAsync(row.Profile.Id, force: true);
                }
            }, $"{row.Profile.MountPoint} を解除しました。");
        }
        else await RunOperation($"{row.Profile.MountPoint} を作成・フォーマットしています…", () => manager.MountAsync(row.Profile.Id), $"{row.Profile.MountPoint} を作成しました。");
    }

    private async Task RunOperation(string pending, Func<Task> operation, string complete)
    {
        if (busy) return;
        busy = true;
        UpdateActions();
        Log(pending);
        try { await operation(); Log(complete); }
        catch (OperationCanceledException ex) { Log(ex.Message); }
        catch (Exception ex) { ShowError(ex); }
        finally { busy = false; RefreshSafely(); }
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } row) return;
        try
        {
            if (ThemedMessageBox.Show(this, $"「{row.Profile.Name}」の保存済み設定を削除しますか？", "設定の削除", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
            manager.Delete(row.Profile);
            Log($"{row.Profile.MountPoint} の設定を削除しました。");
            Refresh();
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (Selected is not { } row || !platform.Inspect(row.Profile.DriveLetter).IsOwnedBy(row.Profile)) return;
            Process.Start(new ProcessStartInfo("explorer.exe", row.Profile.RootPath) { UseShellExecute = true });
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void Driver_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("https://www.ltr-data.se/opencode.html/#ImDisk") { UseShellExecute = true }); }
        catch (Exception ex) { ShowError(ex); }
    }

    private void Admin_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var executable = Environment.ProcessPath ?? throw new IOException("アプリの実行ファイルが見つかりません。");
            Process.Start(new ProcessStartInfo(executable, "--elevated") { UseShellExecute = true, Verb = "runas", WorkingDirectory = AppContext.BaseDirectory });
            Close();
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { Log("管理者としての再起動をキャンセルしました。"); }
        catch (Exception ex) { ShowError(ex); }
    }

    private void Help_Click(object sender, RoutedEventArgs e)
    {
        ThemedMessageBox.Show(this,
            "1. ImDisk Virtual Disk Driver を公式サイトからインストールします。\n2. このアプリを管理者として起動します。\n3.「ディスクを追加」で容量・ドライブ文字などを保存します。\n4. 一覧で「作成」を押すとエクスプローラーから利用できます。\n\n" +
            "NTFS / exFAT / FAT32、複数ディスク、Temp フォルダー作成に対応します。容量は 1 GiB = 1,024 MiB です。OS 用に総 RAM の 5% または 512 MiB の大きい方を残します。\n\n" +
            "標準メモリはページアウトされることがあります。物理メモリ固定には awealloc が必要です。現在は Arsenal Image Mounter の公式ドライバーパッケージに同梱されます。メモリ方式は作成時に確定するため、稼働中の方式変更にはバックアップと再作成が必要です。\n\n" +
            "動的メモリ解放・イメージ保存・Windows 起動時の自動作成はこの版にはありません。\n\n" +
            "アプリを閉じても作成済みドライブは残ります。解除・Windows の再起動・電源断でドライブ内のデータは失われます。重要なファイルの保管には使わないでください。\n\n" +
            "モニター: 管理者で起動している間、登録済み RAM ドライブのファイル I/O と使用容量を記録します。秒・分・時・日と 7 種類の指標を切り替えられます。履歴は設定と同じ場所の monitoring.db に保存します。ファイル名や内容は保存しません。\n\n" +
            "設定: %LOCALAPPDATA%\\RamDiskStudio\\profiles.json\nRAM Disk Studio は SoftPerfect とは無関係の独立したアプリです。",
            "RAM Disk Studio の使い方", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (busy) { e.Cancel = true; Log("ディスク操作が完了するまでお待ちください。"); return; }
        if (monitoring is null || monitorStopped) return;
        e.Cancel = true;
        if (closingMonitor) return;
        closingMonitor = true;
        IsEnabled = false;
        timer.Stop();
        await monitoring.StopAsync();
        monitorStopped = true;
        Close();
    }

    private void ShowError(Exception ex)
    {
        Log(ex.Message.Split('\n')[0]);
        ThemedMessageBox.Show(this, ex.Message, "操作を完了できませんでした", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void Log(string message)
    {
        activity.Enqueue($"{DateTime.Now:HH:mm:ss}  {message}");
        while (activity.Count > 3) activity.Dequeue();
        ActivityText.Text = string.Join("\n", activity);
    }

    public sealed record DiskRow(DiskProfile Profile, DriveSnapshot Snapshot)
    {
        public bool IsMounted => Snapshot.IsOwnedBy(Profile);
        public string Name => Profile.Name;
        public string Letter => Profile.MountPoint;
        public string SizeText => Profile.SizeMiB >= 1024 ? $"{Profile.SizeMiB / 1024.0:0.##} GiB" : $"{Profile.SizeMiB} MiB";
        public string FileSystemText => Profile.FileSystem.ToString();
        public string MemoryText => Profile.MemoryMode == MemoryMode.Physical ? "物理固定" : "標準";
        public string StartupText => Profile.MountOnAppStart ? "アプリ起動時に作成" : "手動で作成";
        public string StatusText => IsMounted ? "● 稼働中" : Snapshot.Exists ? "● 文字競合" : "○ 未作成";
    }
}
