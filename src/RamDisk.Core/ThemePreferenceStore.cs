using System.Text.Json;

namespace RamDisk.Core;

public enum AppTheme { Light, Dark }

public sealed class ThemePreferenceStore(string filePath)
{
    private sealed record Preference(int Version, string Theme);
    public AppTheme Load()
    {
        if (!File.Exists(filePath)) return AppTheme.Dark;
        var value = JsonSerializer.Deserialize<Preference>(File.ReadAllText(filePath));
        if (value is null || value.Version != 1 || !Enum.TryParse<AppTheme>(value.Theme, out var theme) || !Enum.IsDefined(theme))
            throw new InvalidDataException("テーマ設定の形式を読み取れません。");
        return theme;
    }

    public void Save(AppTheme theme)
    {
        if (!Enum.IsDefined(theme)) throw new ArgumentOutOfRangeException(nameof(theme));
        var path = Path.GetFullPath(filePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(new Preference(1, theme.ToString())));
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
