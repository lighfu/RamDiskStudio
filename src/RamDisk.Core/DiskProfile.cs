using System.Text.RegularExpressions;

namespace RamDisk.Core;

public enum DiskFileSystem { NTFS, exFAT, FAT32 }
public enum MemoryMode { Virtual, Physical }

public sealed record MountIdentity(string DevicePath, uint VolumeSerial);

public sealed record DiskProfile
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = "ワークスペース";
    public char DriveLetter { get; init; } = 'R';
    public int SizeMiB { get; init; } = 1024;
    public DiskFileSystem FileSystem { get; init; } = DiskFileSystem.NTFS;
    public MemoryMode MemoryMode { get; init; } = MemoryMode.Virtual;
    public string VolumeLabel { get; init; } = "RAMDISK";
    public bool CreateTempFolder { get; init; } = true;
    public bool MountOnAppStart { get; init; }
    public MountIdentity? Identity { get; init; }
    public string MountPoint => $"{DriveLetter}:";
    public string RootPath => $"{DriveLetter}:\\";
    public long SizeBytes => (long)SizeMiB * 1024 * 1024;

    public void Validate()
    {
        if (Id == Guid.Empty) throw new ArgumentException("設定の ID が無効です。");
        if (string.IsNullOrWhiteSpace(Name) || Name.Length > 60)
            throw new ArgumentException("名前は 1～60 文字で入力してください。");
        if (DriveLetter is < 'D' or > 'Z') throw new ArgumentException("ドライブ文字は D～Z を指定してください。");
        if (SizeMiB is < 64 or > 1048576) throw new ArgumentException("容量は 64～1,048,576 MiB で指定してください。");
        if (!Enum.IsDefined(FileSystem) || !Enum.IsDefined(MemoryMode)) throw new ArgumentException("ディスク方式が無効です。");
        if (FileSystem == DiskFileSystem.FAT32 && (SizeMiB < 256 || SizeMiB > 32768))
            throw new ArgumentException("FAT32 の容量は 256～32,768 MiB にしてください。");
        // The label becomes part of format.com's parameter string. Keep it strictly bounded.
        if (!Regex.IsMatch(VolumeLabel, @"\A[A-Za-z0-9_-]{1,11}\z"))
            throw new ArgumentException("ボリューム名は半角英数字・ハイフン・アンダースコアの 1～11 文字です。");
    }
}

public sealed record MemorySnapshot(ulong TotalBytes, ulong AvailableBytes)
{
    public ulong ReservedBytes => Math.Max(512UL * 1024 * 1024, TotalBytes / 20);
    public ulong MaxDiskBytes => AvailableBytes > ReservedBytes ? AvailableBytes - ReservedBytes : 0;
    public double UsedPercent => TotalBytes == 0 ? 0 : 100.0 * (TotalBytes - AvailableBytes) / TotalBytes;
}

public sealed record DriveSnapshot(string? DevicePath, uint? VolumeSerial = null, long TotalBytes = 0, long FreeBytes = 0, bool IsReady = false)
{
    public bool Exists => DevicePath is not null;
    public bool IsImDisk => DevicePath is not null && Regex.IsMatch(DevicePath, @"\A\\Device\\ImDisk[0-9]+\z", RegexOptions.IgnoreCase);
    public bool IsOwnedBy(DiskProfile profile) => IsImDisk && profile.Identity is { } identity
        && string.Equals(DevicePath, identity.DevicePath, StringComparison.OrdinalIgnoreCase)
        && VolumeSerial == identity.VolumeSerial;
}

public static class DiskCommands
{
    public static IReadOnlyList<string> Create(DiskProfile profile)
    {
        profile.Validate();
        var args = new List<string> { "-a", "-n" };
        if (profile.MemoryMode == MemoryMode.Physical) args.AddRange(["-o", "awe,hd"]);
        else args.AddRange(["-t", "vm", "-o", "hd"]);
        args.AddRange(["-s", $"{profile.SizeMiB}M", "-m", profile.MountPoint,
            "-p", $"/fs:{profile.FileSystem} /q /y /v:{profile.VolumeLabel}"]);
        return args;
    }

    public static IReadOnlyList<string> Remove(DiskProfile profile, DriveSnapshot current, bool force = false)
    {
        profile.Validate();
        if (!current.IsOwnedBy(profile)) throw new InvalidOperationException("このアプリが作成したディスクと確認できないため解除できません。");
        // ImDisk's drive-letter path notifies applications before locking the volume.
        return [force ? "-D" : "-d", "-m", profile.MountPoint];
    }

    public static string? CreatedDevice(string output)
    {
        // ImDisk -n emits exactly one numeric line before invoking format.com.
        var line = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim()).FirstOrDefault(x => Regex.IsMatch(x, @"\A[0-9]+\z"));
        return uint.TryParse(line, out var unit) ? $@"\Device\ImDisk{unit}" : null;
    }
}
