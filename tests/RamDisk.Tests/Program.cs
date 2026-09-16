using RamDisk.Core;
using RamDisk.App;
using System.IO;

if (args.Length == 2 && args[0] is "--cleanup" or "--cleanup-test-force")
{
    var cleanupManager = new DiskManager(new WindowsDiskPlatform(), new ProfileStore(args[1]));
    foreach (var profile in cleanupManager.Profiles.ToArray())
    {
        if (args[0] == "--cleanup-test-force" && profile.Name != "Integration test") throw new InvalidOperationException("Only integration-test disks can be force-cleaned by this command.");
        await cleanupManager.UnmountAsync(profile.Id, force: args[0] == "--cleanup-test-force");
    }
    Console.WriteLine("Test disk detached using persisted device identity.");
    return 0;
}

var tests = new List<(string Name, Func<Task> Run)>();
var testRoot = Path.GetFullPath(Path.Combine("artifacts", "tests", Guid.NewGuid().ToString("N")));
Directory.CreateDirectory(testRoot);

void Test(string name, Action test) => tests.Add((name, () => { test(); return Task.CompletedTask; }));
void AsyncTest(string name, Func<Task> test) => tests.Add((name, test));
static void Check(bool condition, string message = "Assertion failed") { if (!condition) throw new Exception(message); }
static void Throws<T>(Action action) where T : Exception
{
    try { action(); } catch (T) { return; }
    throw new Exception($"Expected {typeof(T).Name}");
}
static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
{
    try { await action(); } catch (T) { return; }
    throw new Exception($"Expected {typeof(T).Name}");
}
(DiskManager Manager, FakePlatform Platform, ProfileStore Store, DiskProfile Profile) Fixture()
{
    var store = new ProfileStore(Path.Combine(testRoot, Guid.NewGuid() + ".json"));
    var platform = new FakePlatform();
    var manager = new DiskManager(platform, store);
    var profile = new DiskProfile();
    manager.Save(profile);
    return (manager, platform, store, profile);
}

Test("Reject dangerous drive letters and formatter arguments", () =>
{
    foreach (var letter in new[] { 'A', 'B', 'C', '\\', 'r' }) Throws<ArgumentException>(() => (new DiskProfile { DriveLetter = letter }).Validate());
    foreach (var label in new[] { "X /p:1", "a\" /fs:ntfs", "$(whoami)", "", "RAM\nDISK" })
        Throws<ArgumentException>(() => DiskCommands.Create(new DiskProfile { VolumeLabel = label }));
});
Test("Validate capacity and FAT32 limits", () =>
{
    Throws<ArgumentException>(() => (new DiskProfile { SizeMiB = -1 }).Validate());
    Throws<ArgumentException>(() => (new DiskProfile { SizeMiB = int.MaxValue }).Validate());
    Throws<ArgumentException>(() => (new DiskProfile { FileSystem = DiskFileSystem.FAT32, SizeMiB = 64 }).Validate());
    Throws<ArgumentException>(() => (new DiskProfile { FileSystem = DiskFileSystem.FAT32, SizeMiB = 32769 }).Validate());
    (new DiskProfile { FileSystem = DiskFileSystem.FAT32, SizeMiB = 256 }).Validate();
});
Test("Physical mode uses awe without conflicting vm type", () =>
{
    var args = DiskCommands.Create(new DiskProfile { MemoryMode = MemoryMode.Physical });
    Check(args.Contains("awe,hd") && !args.Contains("vm") && !args.Contains("-t"));
});
Test("Only numeric ImDisk creation line establishes device identity", () =>
{
    Check(DiskCommands.CreatedDevice("Creating device...\r\n17\r\nFormatting disk...\r\nDone.") == @"\Device\ImDisk17");
    Check(DiskCommands.CreatedDevice("Error 17") is null);
});
Test("Ownership requires ImDisk path and matching volume serial", () =>
{
    var profile = new DiskProfile { Identity = new(@"\Device\ImDisk1", 123) };
    Check(new DriveSnapshot(@"\Device\ImDisk1", 123).IsOwnedBy(profile));
    Check(!new DriveSnapshot(@"\Device\ImDisk1", 456).IsOwnedBy(profile));
    Check(!new DriveSnapshot(@"\Device\ImDisk1suffix", 123).IsOwnedBy(profile));
    Throws<InvalidOperationException>(() => DiskCommands.Remove(profile, new(@"\Device\HarddiskVolume1", 123)));
});
Test("Memory reserve cannot underflow", () =>
{
    Check(new MemorySnapshot(8UL << 30, 128UL << 20).MaxDiskBytes == 0);
    Check(new MemorySnapshot(32UL << 30, 8UL << 30).ReservedBytes == (32UL << 30) / 20);
});
Test("Atomic settings round trip and backup", () =>
{
    var (manager, _, store, profile) = Fixture();
    manager.Save(profile with { Name = "ビルド・キャッシュ", SizeMiB = 3072 });
    Check(store.Load().Single().Name == "ビルド・キャッシュ");
    Check(File.Exists(store.FilePath + ".bak"));
    Check(new ProfileStore(store.FilePath + ".bak").Load().Single().Name == profile.Name);
});
Test("Corrupt settings fail without being overwritten", () =>
{
    var path = Path.Combine(testRoot, "corrupt.json");
    File.WriteAllText(path, "{broken}");
    Throws<InvalidDataException>(() => new ProfileStore(path).Load());
    Check(File.ReadAllText(path) == "{broken}");
});
Test("Unknown configuration version is rejected", () =>
{
    var path = Path.Combine(testRoot, "future.json");
    File.WriteAllText(path, "{\"Version\":2,\"Disks\":[]}");
    Throws<InvalidDataException>(() => new ProfileStore(path).Load());
});
Test("Duplicate drive profiles cannot overwrite settings", () =>
{
    var (manager, _, store, _) = Fixture();
    Throws<InvalidOperationException>(() => manager.Save(new DiskProfile()));
    Check(store.Load().Count == 1);
});
AsyncTest("Mount persists identity and creates Temp after success", async () =>
{
    var (manager, platform, store, profile) = Fixture();
    await manager.MountAsync(profile.Id);
    Check(platform.TempCreated && store.Load().Single().Identity?.VolumeSerial == 123);
});
AsyncTest("Existing ordinary drive is never formatted", async () =>
{
    var (manager, platform, _, profile) = Fixture();
    platform.Snapshot = new(@"\Device\HarddiskVolume2", 100);
    await ThrowsAsync<InvalidOperationException>(() => manager.MountAsync(profile.Id));
    Check(platform.Calls == 0);
});
AsyncTest("Low memory is rejected before driver command", async () =>
{
    var (manager, platform, _, profile) = Fixture();
    platform.Memory = new(8UL << 30, 512UL << 20);
    await ThrowsAsync<InvalidOperationException>(() => manager.MountAsync(profile.Id));
    Check(platform.Calls == 0);
});
AsyncTest("Missing admin rights and missing driver block mutations", async () =>
{
    var (manager, platform, _, profile) = Fixture();
    platform.IsAdministrator = false;
    await ThrowsAsync<InvalidOperationException>(() => manager.MountAsync(profile.Id));
    platform.IsAdministrator = true;
    platform.IsDriverInstalled = false;
    await ThrowsAsync<InvalidOperationException>(() => manager.MountAsync(profile.Id));
    Check(platform.Calls == 0);
});
AsyncTest("awealloc absence blocks physical mode", async () =>
{
    var (manager, platform, _, profile) = Fixture();
    manager.Save(profile with { MemoryMode = MemoryMode.Physical });
    platform.IsPhysicalMemoryDriverInstalled = false;
    await ThrowsAsync<InvalidOperationException>(() => manager.MountAsync(profile.Id));
    Check(platform.Calls == 0);
});
AsyncTest("Format failure does not claim or automatically detach a device", async () =>
{
    var (manager, platform, store, profile) = Fixture();
    platform.ExitCode = 8;
    await ThrowsAsync<InvalidOperationException>(() => manager.MountAsync(profile.Id));
    Check(store.Load().Single().Identity is null && platform.Calls == 1 && !platform.TempCreated);
});
AsyncTest("Device mismatch after command success is rejected", async () =>
{
    var (manager, platform, store, profile) = Fixture();
    platform.CreatedPath = @"\Device\ImDisk8";
    await ThrowsAsync<InvalidOperationException>(() => manager.MountAsync(profile.Id));
    Check(store.Load().Single().Identity is null);
});
AsyncTest("Mounted configuration cannot be edited or deleted", async () =>
{
    var (manager, _, _, profile) = Fixture();
    await manager.MountAsync(profile.Id);
    Throws<InvalidOperationException>(() => manager.Save(profile with { SizeMiB = 2048 }));
    Throws<InvalidOperationException>(() => manager.Delete(manager.Profiles.Single()));
    Throws<InvalidOperationException>(() => manager.Delete(profile));
});
AsyncTest("Stale ownership does not allow detach of replacement disk", async () =>
{
    var (manager, platform, _, profile) = Fixture();
    await manager.MountAsync(profile.Id);
    platform.Snapshot = platform.Snapshot with { VolumeSerial = 999 };
    await ThrowsAsync<InvalidOperationException>(() => manager.UnmountAsync(profile.Id));
    Check(platform.Calls == 1);
});
AsyncTest("Busy disk detach failure retains ownership", async () =>
{
    var (manager, platform, store, profile) = Fixture();
    await manager.MountAsync(profile.Id);
    platform.ExitCode = 2;
    await ThrowsAsync<InvalidOperationException>(() => manager.UnmountAsync(profile.Id));
    Check(store.Load().Single().Identity is not null);
});
AsyncTest("Unmount notifies applications through the drive letter and clears identity", async () =>
{
    var (manager, platform, store, profile) = Fixture();
    await manager.MountAsync(profile.Id);
    await manager.UnmountAsync(profile.Id);
    Check(platform.LastArguments.SequenceEqual(new[] { "-d", "-m", "R:" }));
    Check(store.Load().Single().Identity is null && !platform.Snapshot.Exists);
});
AsyncTest("A transient lock retries normal detach after checking ownership", async () =>
{
    var (manager, platform, _, profile) = Fixture();
    await manager.MountAsync(profile.Id);
    platform.TransientRemoveFailures = 1;
    await manager.UnmountAsync(profile.Id);
    Check(platform.Calls == 3 && !platform.Snapshot.Exists);
    Check(!platform.LastArguments.Contains("-D"));
});
AsyncTest("Force detach requires explicit selection and still checks identity", async () =>
{
    var (manager, platform, _, profile) = Fixture();
    await manager.MountAsync(profile.Id);
    platform.Snapshot = platform.Snapshot with { VolumeSerial = 999 };
    await ThrowsAsync<InvalidOperationException>(() => manager.UnmountAsync(profile.Id, force: true));
    Check(platform.Calls == 1);
    platform.Snapshot = platform.Snapshot with { VolumeSerial = 123 };
    await manager.UnmountAsync(profile.Id, force: true);
    Check(platform.LastArguments.SequenceEqual(new[] { "-D", "-m", "R:" }));
});
AsyncTest("Concurrent mount requests cannot allocate the same drive twice", async () =>
{
    var (manager, platform, _, profile) = Fixture();
    var first = manager.MountAsync(profile.Id);
    var second = manager.MountAsync(profile.Id);
    await first;
    await ThrowsAsync<InvalidOperationException>(() => second);
    Check(platform.Calls == 1);
});

if (args.Contains("--ui")) Test("WPF views render; editor validation and saving work", () => UiChecks.Run(testRoot));

if (args.Contains("--integration") || args.Contains("--integration-standard") || args.Contains("--integration-physical"))
{
    AsyncTest("REAL DRIVER: create, format, write/read, reopen manager, detach", async () =>
    {
        var real = new WindowsDiskPlatform();
        Check(real.IsAdministrator && real.IsDriverInstalled, "Administrator and ImDisk are required");
        var letter = Enumerable.Range('D', 23).Select(x => (char)x).Reverse().First(x => !real.Inspect(x).Exists);
        var scenarios = new[] { (DiskFileSystem.NTFS, MemoryMode.Virtual, 64), (DiskFileSystem.exFAT, MemoryMode.Virtual, 64), (DiskFileSystem.FAT32, MemoryMode.Virtual, 256),
            (DiskFileSystem.NTFS, MemoryMode.Physical, 64), (DiskFileSystem.exFAT, MemoryMode.Physical, 64), (DiskFileSystem.FAT32, MemoryMode.Physical, 256) };
        foreach (var scenario in scenarios.Where(x => (!args.Contains("--integration-standard") || x.Item2 == MemoryMode.Virtual) &&
            (!args.Contains("--integration-physical") || x.Item2 == MemoryMode.Physical)))
        {
            var store = new ProfileStore(Path.Combine(testRoot, "real-" + Guid.NewGuid() + ".json"));
            var manager = new DiskManager(real, store);
            var profile = new DiskProfile { DriveLetter = letter, SizeMiB = scenario.Item3, FileSystem = scenario.Item1, MemoryMode = scenario.Item2, Name = "Integration test" };
            manager.Save(profile);
            try
            {
                await manager.MountAsync(profile.Id);
                var mounted = manager.Profiles.Single();
                Check(real.Inspect(letter).IsOwnedBy(mounted));
                if (scenario.Item2 == MemoryMode.Physical)
                {
                    var details = await real.ExecuteAsync(["-l", "-m", mounted.MountPoint]);
                    Console.WriteLine(details.StandardOutput);
                    Check(details.ExitCode == 0 && details.StandardOutput.Contains("Physical Memory", StringComparison.Ordinal), "Disk does not use AWEAlloc physical memory backend");
                }
                Check(new DriveInfo(profile.RootPath).DriveFormat.Equals(scenario.Item1.ToString(), StringComparison.OrdinalIgnoreCase), "Filesystem type mismatch");
                var bytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(1024 * 1024);
                var file = Path.Combine(profile.RootPath, "Temp", "round-trip.bin");
                await File.WriteAllBytesAsync(file, bytes);
                var readBack = await File.ReadAllBytesAsync(file);
                Check(bytes.SequenceEqual(readBack), "Read/write content mismatch");
                var reopened = new DiskManager(real, store);
                try { await reopened.UnmountAsync(profile.Id); }
                catch (DiskBusyException)
                {
                    Console.WriteLine($"  Normal detach was blocked on {letter}:; explicitly forcing removal of this verified test disk.");
                    await reopened.UnmountAsync(profile.Id, force: true);
                }
                Check(!real.Inspect(letter).Exists, "Drive letter remained after detach");
                Console.WriteLine($"  Verified {scenario.Item1} / {scenario.Item2} / {scenario.Item3} MiB on {letter}:");
            }
            finally
            {
                if (real.Inspect(letter).IsOwnedBy(manager.Profiles.Single())) await manager.UnmountAsync(profile.Id, force: true);
            }
        }
    });
}

MonitoringChecks.Add(tests, testRoot, args.Contains("--monitoring-integration") || args.Contains("--monitoring-physical"), args.Contains("--monitoring-physical"));
ChartChecks.Add(tests);
Test("Theme preference survives restart and corrupt settings are preserved", () =>
{
    var path = Path.Combine(testRoot, "appearance.json");
    var store = new ThemePreferenceStore(path);
    Check(store.Load() == AppTheme.Dark);
    foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark, AppTheme.Light })
    {
        store.Save(theme);
        Check(new ThemePreferenceStore(path).Load() == theme);
    }
    File.WriteAllText(path, "{broken}");
    Throws<System.Text.Json.JsonException>(() => store.Load());
    Check(File.ReadAllText(path) == "{broken}");
    File.WriteAllText(path, "{\"Version\":99,\"Theme\":\"Dark\"}");
    Throws<InvalidDataException>(() => store.Load());
});

var failures = 0;
foreach (var (name, run) in tests)
{
    try { await run(); Console.WriteLine($"PASS {name}"); }
    catch (Exception ex) { failures++; Console.Error.WriteLine($"FAIL {name}: {ex}"); }
}
Console.WriteLine($"{tests.Count - failures}/{tests.Count} passed. Artifacts: {testRoot}");
return failures == 0 ? 0 : 1;

sealed class FakePlatform : IDiskPlatform
{
    public bool IsAdministrator { get; set; } = true;
    public bool IsDriverInstalled { get; set; } = true;
    public bool IsPhysicalMemoryDriverInstalled { get; set; } = true;
    public MemorySnapshot Memory { get; set; } = new(16UL << 30, 8UL << 30);
    public DriveSnapshot Snapshot { get; set; } = new(null);
    public string CreatedPath { get; set; } = @"\Device\ImDisk7";
    public int ExitCode { get; set; }
    public int TransientRemoveFailures { get; set; }
    public int Calls { get; private set; }
    public bool TempCreated { get; private set; }
    public IReadOnlyList<string> LastArguments { get; private set; } = [];
    public MemorySnapshot GetMemory() => Memory;
    public DriveSnapshot Inspect(char letter) => Snapshot;
    public void CreateTempFolder(DiskProfile profile) => TempCreated = true;
    public async Task<CommandResult> ExecuteAsync(IReadOnlyList<string> arguments)
    {
        Calls++;
        LastArguments = arguments;
        await Task.Delay(5);
        if (arguments.Contains("-d") && TransientRemoveFailures > 0)
        {
            TransientRemoveFailures--;
            return new(2, "Locking volume...", "Transient lock");
        }
        if (ExitCode == 0) Snapshot = arguments.Contains("-a") ? new(CreatedPath, 123, 1024L << 20, 900L << 20, true) : new(null);
        return new(ExitCode, "Creating device...\r\n7\r\nDone.", ExitCode == 0 ? "" : "Simulated failure");
    }
}
