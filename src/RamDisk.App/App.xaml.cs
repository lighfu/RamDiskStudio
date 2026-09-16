using System.Windows;

namespace RamDisk.App;

public partial class App : Application
{
    private Mutex? instanceMutex;
    private bool ownsMutex;
    private readonly bool startManager = true;

    public App() { }
    // The rendering harness pumps WPF without opening real profiles, starting ETW, or taking the app mutex.
    internal App(bool startManager) => this.startManager = startManager;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (!startManager) return;
        ThemeManager.Current.Initialize(new RamDisk.Core.ThemePreferenceStore(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RamDiskStudio", "appearance.json")));
        instanceMutex = new Mutex(true, @"Local\RamDiskStudio.Manager", out var created);
        ownsMutex = created;
        if (!created && e.Args.Contains("--elevated"))
        {
            try { ownsMutex = instanceMutex.WaitOne(TimeSpan.FromSeconds(10)); }
            catch (AbandonedMutexException) { ownsMutex = true; }
        }
        if (!ownsMutex)
        {
            ThemedMessageBox.Show("RAM Disk Studio はすでに起動しています。", "RAM Disk Studio");
            Shutdown();
            return;
        }
        try
        {
            var window = new MainWindow();
            MainWindow = window;
            if (e.Args.Contains("--monitor")) window.ShowMonitor();
            window.Show();
        }
        catch (Exception ex)
        {
            ThemedMessageBox.Show($"起動できませんでした。設定ファイルは上書きしていません。\n\n{ex.Message}", "RAM Disk Studio", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (ownsMutex) instanceMutex?.ReleaseMutex();
        instanceMutex?.Dispose();
        base.OnExit(e);
    }
}
