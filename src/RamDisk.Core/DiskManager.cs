namespace RamDisk.Core;

public sealed record CommandResult(int ExitCode, string StandardOutput, string StandardError);

public sealed class DiskBusyException(string message) : InvalidOperationException(message);

public interface IDiskPlatform
{
    bool IsAdministrator { get; }
    bool IsDriverInstalled { get; }
    bool IsPhysicalMemoryDriverInstalled { get; }
    MemorySnapshot GetMemory();
    DriveSnapshot Inspect(char letter);
    Task<CommandResult> ExecuteAsync(IReadOnlyList<string> arguments);
    void CreateTempFolder(DiskProfile profile);
}

public sealed class DiskManager(IDiskPlatform platform, ProfileStore store)
{
    private readonly SemaphoreSlim operationLock = new(1, 1);
    public List<DiskProfile> Profiles { get; } = store.Load();

    public void Save(DiskProfile profile)
    {
        profile.Validate();
        var original = Profiles.Find(x => x.Id == profile.Id);
        if (original is not null && platform.Inspect(original.DriveLetter).Exists)
            throw new InvalidOperationException("ドライブを解除してから設定を編集してください。");
        if (Profiles.Any(x => x.Id != profile.Id && x.DriveLetter == profile.DriveLetter))
            throw new InvalidOperationException("このドライブ文字は別の設定で使用しています。");
        if (platform.Inspect(profile.DriveLetter).Exists)
            throw new InvalidOperationException("このドライブ文字はすでに使用されています。");
        var updated = profile with { Identity = null };
        var next = Profiles.Where(x => x.Id != profile.Id).Append(updated).ToList();
        Commit(next);
    }

    public void Delete(DiskProfile profile)
    {
        profile = Profiles.Single(x => x.Id == profile.Id);
        if (platform.Inspect(profile.DriveLetter).IsOwnedBy(profile))
            throw new InvalidOperationException("ディスクを解除してから設定を削除してください。");
        Commit(Profiles.Where(x => x.Id != profile.Id).ToList());
    }

    public async Task MountAsync(Guid id)
    {
        await operationLock.WaitAsync();
        try
        {
            var profile = Profiles.Single(x => x.Id == id);
            profile.Validate();
            RequireDriver();
            if (platform.Inspect(profile.DriveLetter).Exists)
                throw new InvalidOperationException($"{profile.MountPoint} はすでに使用されています。");
            if ((ulong)profile.SizeBytes > platform.GetMemory().MaxDiskBytes)
                throw new InvalidOperationException("空きメモリが不足しています。OS 用の余裕を残すため容量を減らしてください。");
            if (profile.MemoryMode == MemoryMode.Physical && !platform.IsPhysicalMemoryDriverInstalled)
                throw new InvalidOperationException("物理メモリ固定には、登録・有効化された awealloc ドライバーが必要です。Arsenal Image Mounter の公式ドライバーパッケージから導入してください。ImDisk 2.1.2 以降には同梱されません。");

            // Verify settings are writable before making any system changes.
            store.Save(Profiles);
            var result = await platform.ExecuteAsync(DiskCommands.Create(profile));
            var current = platform.Inspect(profile.DriveLetter);
            var createdDevice = DiskCommands.CreatedDevice(result.StandardOutput);
            if (result.ExitCode != 0)
                throw new InvalidOperationException($"作成に失敗しました (終了コード {result.ExitCode})。\n{result.StandardError}\n{result.StandardOutput}\n部分的に作成された場合は ImDisk コントロールパネルで状態を確認してください。");
            if (!current.IsImDisk || !current.IsReady || current.VolumeSerial is null ||
                !string.Equals(current.DevicePath, createdDevice, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("作成したディスクを確認できません。ImDisk コントロールパネルで状態を確認してください。");
            var mounted = profile with { Identity = new(current.DevicePath!, current.VolumeSerial.Value) };
            // Retain ownership in memory even if persistence fails after device creation.
            Profiles[Profiles.FindIndex(x => x.Id == id)] = mounted;
            try { store.Save(Profiles); }
            catch (Exception ex) { throw new IOException("ディスクは作成されましたが設定保存に失敗しました。アプリを閉じずに解除してください。", ex); }
            if (mounted.CreateTempFolder) platform.CreateTempFolder(mounted);
        }
        finally { operationLock.Release(); }
    }

    public async Task UnmountAsync(Guid id, bool force = false)
    {
        await operationLock.WaitAsync();
        try
        {
            var profile = Profiles.Single(x => x.Id == id);
            RequireDriver();
            var result = await platform.ExecuteAsync(DiskCommands.Remove(profile, platform.Inspect(profile.DriveLetter), force));
            // Shell extensions and filesystem scanners can briefly hold a newly mounted volume.
            // Retrying never escalates to forced removal; that requires an explicit caller choice.
            for (var retry = 0; !force && result.ExitCode == 2 && retry < 3; retry++)
            {
                await Task.Delay(750);
                result = await platform.ExecuteAsync(DiskCommands.Remove(profile, platform.Inspect(profile.DriveLetter)));
            }
            if (!force && result.ExitCode == 2)
                throw new DiskBusyException($"ドライブのロックを取得できませんでした。使用中のファイルを閉じて再試行してください。\n{result.StandardError}\n{result.StandardOutput}");
            if (result.ExitCode != 0)
                throw new InvalidOperationException($"解除できませんでした。ドライブ内のファイルを閉じて再試行してください。\n{result.StandardError}\n{result.StandardOutput}");
            if (platform.Inspect(profile.DriveLetter).IsOwnedBy(profile))
                throw new InvalidOperationException("ドライブがまだ接続されています。状態を確認してください。");
            Commit(Profiles.Select(x => x.Id == id ? x with { Identity = null } : x).ToList());
        }
        finally { operationLock.Release(); }
    }

    private void RequireDriver()
    {
        if (!platform.IsDriverInstalled) throw new InvalidOperationException("ImDisk ドライバーをインストールしてください。");
        if (!platform.IsAdministrator) throw new InvalidOperationException("ドライブの作成・解除には管理者権限が必要です。管理者として再起動してください。");
    }

    private void Commit(List<DiskProfile> next)
    {
        store.Save(next);
        Profiles.Clear();
        Profiles.AddRange(next);
    }
}
