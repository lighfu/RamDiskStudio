using System.Globalization;
using System.Windows;
using RamDisk.Core;
using System.Diagnostics;

namespace RamDisk.App;

public partial class DiskEditor : Window
{
    private readonly DiskProfile original;
    public DiskProfile? Result { get; private set; }

    public DiskEditor(DiskProfile? profile, IEnumerable<char> freeLetters, MemorySnapshot memory, bool aweallocInstalled = false)
    {
        InitializeComponent();
        ThemeManager.Attach(this);
        original = profile ?? new DiskProfile();
        var letters = freeLetters.Distinct().Order().ToArray();
        LetterInput.ItemsSource = letters;
        LetterInput.SelectedItem = letters.Contains(original.DriveLetter) ? original.DriveLetter : letters.FirstOrDefault();
        FileSystemInput.ItemsSource = Enum.GetValues<DiskFileSystem>();
        FileSystemInput.SelectedItem = original.FileSystem;
        NameInput.Text = original.Name;
        LabelInput.Text = original.VolumeLabel;
        if (original.SizeMiB % 1024 == 0) SizeInput.Text = (original.SizeMiB / 1024).ToString(CultureInfo.CurrentCulture);
        else { UnitInput.SelectedIndex = 1; SizeInput.Text = original.SizeMiB.ToString(CultureInfo.CurrentCulture); }
        MemoryInput.SelectedIndex = (int)original.MemoryMode;
        TempInput.IsChecked = original.CreateTempFolder;
        StartupInput.IsChecked = original.MountOnAppStart;
        AvailableText.Text = $"空きメモリ {memory.AvailableBytes / 1073741824.0:F1} GiB · 作成時に空き容量を再確認します";
        AweStatusText.Text = aweallocInstalled ? "● awealloc 導入済み · 物理メモリ固定を利用できます" : "awealloc 未導入 / 無効 · 設定は保存できますが、作成には導入が必要です";
        AweDownloadButton.Visibility = aweallocInstalled ? Visibility.Collapsed : Visibility.Visible;
        if (profile is not null) HeadingText.Text = "ディスクの設定を編集";
        Loaded += (_, _) => NameInput.Focus();
    }

    private void AweDownload_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("https://github.com/ArsenalRecon/Arsenal-Image-Mounter/tree/master/DriverSetup") { UseShellExecute = true }); }
        catch (Exception ex) { ErrorText.Text = ex.Message; }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!decimal.TryParse(SizeInput.Text, NumberStyles.Number, CultureInfo.CurrentCulture, out var size))
                throw new ArgumentException("容量を数値で入力してください。");
            var sizeMiB = size * (UnitInput.SelectedIndex == 0 ? 1024 : 1);
            if (sizeMiB != decimal.Truncate(sizeMiB) || sizeMiB < 64 || sizeMiB > 1048576)
                throw new ArgumentException("容量は 64～1,048,576 MiB の整数になるよう指定してください。");
            if (LetterInput.SelectedItem is not char letter) throw new ArgumentException("利用できるドライブ文字がありません。");
            Result = original with
            {
                Name = NameInput.Text.Trim(), DriveLetter = letter, SizeMiB = (int)sizeMiB,
                FileSystem = (DiskFileSystem)FileSystemInput.SelectedItem,
                VolumeLabel = LabelInput.Text.Trim(), MemoryMode = (MemoryMode)MemoryInput.SelectedIndex,
                CreateTempFolder = TempInput.IsChecked == true, MountOnAppStart = StartupInput.IsChecked == true,
                Identity = null
            };
            Result.Validate();
            DialogResult = true;
        }
        catch (Exception ex) when (ex is ArgumentException or OverflowException)
        {
            ErrorText.Text = ex.Message;
        }
    }
}
