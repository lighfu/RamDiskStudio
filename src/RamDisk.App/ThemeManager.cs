using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using RamDisk.Core;

namespace RamDisk.App;

public sealed class ThemeManager : INotifyPropertyChanged
{
    public static ThemeManager Current { get; } = new();
    private ThemePreferenceStore? store;
    public AppTheme Theme { get; private set; } = AppTheme.Dark;
    public string ToggleLabel => Theme == AppTheme.Dark ? "☀ ライトへ" : "☾ ダークへ";
    public event PropertyChangedEventHandler? PropertyChanged;

    public void Initialize(ThemePreferenceStore preferences)
    {
        store = preferences;
        try { Apply(preferences.Load()); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            // A damaged appearance preference must not prevent access to the disk manager.
            Apply(AppTheme.Dark);
        }
    }

    public void Toggle(Window? owner)
    {
        var theme = Theme == AppTheme.Dark ? AppTheme.Light : AppTheme.Dark;
        Apply(theme);
        try { store?.Save(theme); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ThemedMessageBox.Show(owner, "表示を切り替えましたが、次回起動用のテーマを保存できませんでした。\n" + ex.Message,
                "テーマ設定", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    public void Apply(AppTheme theme)
    {
        if (!Enum.IsDefined(theme)) throw new ArgumentOutOfRangeException(nameof(theme));
        var resources = Application.Current.Resources.MergedDictionaries;
        var palette = new ResourceDictionary { Source = new Uri($"/RamDiskStudio;component/Themes/{theme}.xaml", UriKind.Relative) };
        var previous = resources.FirstOrDefault(x => x.Source?.OriginalString.Contains("/Themes/", StringComparison.Ordinal) == true);
        if (previous is not null) resources[resources.IndexOf(previous)] = palette;
        else resources.Insert(0, palette);
        Theme = theme;
        foreach (Window window in Application.Current.Windows) UpdateCaption(window);
        PropertyChanged?.Invoke(this, new(nameof(ToggleLabel)));
    }

    public static void Attach(Window window) => window.SourceInitialized += (_, _) => UpdateCaption(window);
    private static void UpdateCaption(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;
        var dark = Current.Theme == AppTheme.Dark ? 1 : 0;
        // Unsupported Windows versions simply leave the system title bar unchanged.
        _ = DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
}
