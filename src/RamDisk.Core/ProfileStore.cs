using System.Text.Json;
using System.Text.Json.Serialization;

namespace RamDisk.Core;

public sealed class ProfileStore(string filePath)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };
    public string FilePath { get; } = Path.GetFullPath(filePath);

    public List<DiskProfile> Load()
    {
        if (!File.Exists(FilePath)) return [];
        try
        {
            var document = JsonSerializer.Deserialize<ProfileDocument>(File.ReadAllText(FilePath), Options)
                ?? throw new InvalidDataException("設定が空です。");
            if (document.Version != 1) throw new InvalidDataException("未対応の設定バージョンです。");
            Validate(document.Disks);
            return document.Disks;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            throw new InvalidDataException("設定ファイルを読み込めません。元のファイルを確認してください。", ex);
        }
    }

    public void Save(IReadOnlyCollection<DiskProfile> profiles)
    {
        Validate(profiles);
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, new ProfileDocument { Disks = profiles.ToList() }, Options);
                stream.Flush(flushToDisk: true);
            }
            if (File.Exists(FilePath)) File.Replace(temporary, FilePath, FilePath + ".bak");
            else File.Move(temporary, FilePath);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static void Validate(IReadOnlyCollection<DiskProfile>? profiles)
    {
        if (profiles is null) throw new InvalidDataException("ディスク設定がありません。");
        foreach (var profile in profiles)
        {
            if (profile is null) throw new InvalidDataException("ディスク設定が無効です。");
            profile.Validate();
        }
        if (profiles.Select(x => x.Id).Distinct().Count() != profiles.Count ||
            profiles.Select(x => x.DriveLetter).Distinct().Count() != profiles.Count)
            throw new InvalidDataException("ID またはドライブ文字が重複しています。");
    }

    private sealed class ProfileDocument
    {
        public int Version { get; set; } = 1;
        public List<DiskProfile> Disks { get; set; } = [];
    }
}
