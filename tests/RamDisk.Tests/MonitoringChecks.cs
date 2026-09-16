using System.IO;
using RamDisk.Core;
using RamDisk.App;

static class MonitoringChecks
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }

    public static void Add(List<(string Name, Func<Task> Run)> tests, string root, bool integration, bool physical = false)
    {
        void Test(string name, Action action) => tests.Add((name, () => { action(); return Task.CompletedTask; }));
        Test("Monitor filters exact owned drive roots and concurrent drains preserve counts", () =>
        {
            var profile = new DiskProfile { Identity = new(@"\Device\ImDisk1", 123) };
            var counter = new IoAccumulator();
            counter.SetTargets([new(profile, new(@"\Device\ImDisk1", 456))]);
            Check(!counter.Record(@"R:\private.bin", 1, false), "Foreign serial accepted");
            counter.SetTargets([new(profile, new(@"\Device\ImDisk1", 123))]);
            Check(!counter.Record(@"R:\cache-flush.bin", 4096, true, 0x42), "Paging I/O was double counted");
            foreach (var path in new[] { @"\Device\ImDisk10\file", @"C:\file", @"RR:\file", "", @"R:relative" })
                Check(!counter.Record(path, 123, true), "Foreign path accepted: " + path);
            Parallel.For(0, 10000, i => counter.Record(i % 2 == 0 ? @"r:\a" : @"\Device\ImDisk1\a", 4096, i % 2 == 0));
            var total = counter.Drain()[profile.Id];
            Check(total == new IoCounts(5000L * 4096, 5000L * 4096, 5000, 5000), "Concurrent counts were lost");
            Check(counter.Drain().Count == 0, "Drain duplicated counters");
        });
        Test("History rollups persist across restart, preserve UTC boundaries and lifetime after retention", () =>
        {
            var path = Path.Combine(root, "monitor-history.db");
            var store = new MonitorStore(path);
            var profile = new DiskProfile();
            store.Register([profile]);
            var now = new DateTimeOffset(2026, 9, 13, 0, 0, 0, TimeSpan.Zero);
            store.Append([new(profile.Id, now.AddSeconds(-1), new(1024, 2048, 1, 2), 1, 100, 1000),
                new(profile.Id, now, new(3072, 4096, 3, 4), 0.5, 200, 1000),
                new(profile.Id, now.AddSeconds(1), new(5120, 6144, 5, 6), 1, 300, 1000)]);
            var reopened = new MonitorStore(path);
            Check(reopened.Disks().Single().Id == profile.Id, "Disk metadata was lost");
            var seconds = reopened.Read(profile.Id, HistoryInterval.Second, now.AddMinutes(-1), now.AddMinutes(1));
            var minutes = reopened.Read(profile.Id, HistoryInterval.Minute, now.AddMinutes(-1), now.AddMinutes(1));
            var days = reopened.Read(profile.Id, HistoryInterval.Day, now.AddDays(-1), now.AddDays(1));
            Check(seconds.Points.Count == 3 && minutes.Points.Count == 2 && days.Points.Count == 2, "Bucket boundary wrong");
            Check(minutes.Points[1].Io.ReadBytes == 8192 && minutes.Points[1].IoSeconds == 1.5, "Weighted rollup wrong");
            Check(minutes.Points[1].Values(MonitorMetric.Rate).Read == 8192 / 1.5 / 1048576, "Rate must use observed duration");
            Check(days.Lifetime == new IoCounts(9216, 12288, 9, 12), "Totals double counted resolutions");
            reopened.Prune(now.AddDays(8));
            Check(reopened.Read(profile.Id, HistoryInterval.Minute, now.AddMinutes(-1), now.AddMinutes(1)).Points.Count == 0, "Retention failed");
            Check(reopened.Read(profile.Id, HistoryInterval.Day, now.AddDays(-1), now.AddDays(1)).Lifetime == days.Lifetime, "Pruning reset totals");
            var another = profile with { Id = Guid.NewGuid() };
            reopened.Register([another]);
            Check(reopened.Read(another.Id, HistoryInterval.Day, now, now).Lifetime == new IoCounts(), "Drive letter reuse leaked totals");
        });
        Test("Missing I/O stays unknown, idle stays zero, capacity remains independently available", () =>
        {
            var missing = new HistoryPoint(1, new(), 0, 250, 1000, 1);
            Check(missing.Values(MonitorMetric.Rate).Read is null && missing.Values(MonitorMetric.UsagePercent).Read == 25, "Unknown became idle");
            var idle = missing with { IoSeconds = 1 };
            Check(idle.Values(MonitorMetric.Iops).Read == 0 && idle.Values(MonitorMetric.AverageSize).Read is null, "Idle mean size invalid");
        });
        Test("Invalid history batch is rejected without partial writes", () =>
        {
            var store = new MonitorStore(Path.Combine(root, "invalid-monitor.db"));
            var id = Guid.NewGuid();
            var now = DateTimeOffset.UtcNow;
            try
            {
                store.Append([new(id, now, new(100), 1, null, null), new(id, now, new(), double.NaN, null, null)]);
                throw new Exception("Invalid sample was accepted");
            }
            catch (ArgumentException) { }
            Check(store.Read(id, HistoryInterval.Day, now, now).Lifetime.ReadBytes == 0, "Partial batch persisted");
        });
        if (integration) tests.Add(("Real ETW captures ImDisk reads/writes and durable monitor history", () => Integration(root, physical)));
    }

    private static async Task Integration(string root, bool physical)
    {
        var platform = new WindowsDiskPlatform();
        Check(platform.IsAdministrator, "Integration requires administrator");
        var letter = Enumerable.Range('D', 23).Select(x => (char)x).Reverse().First(x => !platform.Inspect(x).Exists);
        var profiles = new ProfileStore(Path.Combine(root, "monitor-integration-profile.json"));
        var manager = new DiskManager(platform, profiles);
        var profile = new DiskProfile { Name = "Integration test", DriveLetter = letter, SizeMiB = 64, MemoryMode = physical ? MemoryMode.Physical : MemoryMode.Virtual };
        manager.Save(profile);
        var store = new MonitorStore(Path.Combine(root, "monitor-integration.db"));
        var service = new MonitoringService(platform, store);
        try
        {
            await manager.MountAsync(profile.Id);
            using var alreadyOpen = new FileStream(Path.Combine(profile.RootPath, "already-open.bin"), FileMode.Create,
                FileAccess.ReadWrite, FileShare.Read, 4096, FileOptions.WriteThrough);
            alreadyOpen.SetLength(1 << 20);
            service.SetProfiles(manager.Profiles);
            service.Start();
            await Task.Delay(3000);
            Console.WriteLine("  Monitor status: " + service.Status);
            var bytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(1 << 20);
            alreadyOpen.Write(bytes);
            alreadyOpen.Flush(true);
            alreadyOpen.Position = 0;
            var preexistingRead = new byte[bytes.Length];
            alreadyOpen.ReadExactly(preexistingRead);
            Check(preexistingRead.SequenceEqual(bytes), "Already-open handle payload mismatch");
            var file = Path.Combine(profile.RootPath, "monitor-test.bin");
            for (var i = 0; i < 12; i++)
            {
                using (var stream = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough))
                { stream.Write(bytes); stream.Flush(true); }
                Check(File.ReadAllBytes(file).SequenceEqual(bytes), "Payload mismatch");
                await Task.Delay(150);
            }
            // Real-time kernel ETW has no reliable initial filename rundown. Reopening establishes attribution.
            alreadyOpen.Dispose();
            using (var reopened = new FileStream(Path.Combine(profile.RootPath, "already-open.bin"), FileMode.Open,
                FileAccess.ReadWrite, FileShare.Read, 4096, FileOptions.WriteThrough))
            {
                reopened.Write(bytes);
                reopened.Flush(true);
                reopened.Position = 0;
                reopened.ReadExactly(preexistingRead);
            }
            await Task.Delay(4000);
            await service.StopAsync();
            var end = DateTimeOffset.UtcNow;
            var result = new MonitorStore(store.FilePath).Read(profile.Id, HistoryInterval.Second, end.AddMinutes(-2), end);
            Console.WriteLine($"  ETW totals: read {result.Lifetime.ReadBytes:N0} bytes / {result.Lifetime.ReadOps:N0} ops; write {result.Lifetime.WriteBytes:N0} bytes / {result.Lifetime.WriteOps:N0} ops; {result.Points.Count} samples. {service.Status}");
            Check(result.Lifetime.ReadBytes >= 13L << 20 && result.Lifetime.WriteBytes >= 13L << 20, "ETW did not capture known file I/O workload after reopening existing file");
            Check(result.Lifetime.ReadOps >= 13 && result.Lifetime.WriteOps >= 13, "ETW operation counters empty");
            Check(result.Points.Any(x => x.Values(MonitorMetric.UsagePercent).Read > 0), "Capacity not sampled");
        }
        finally
        {
            await service.StopAsync();
            if (platform.Inspect(letter).IsOwnedBy(manager.Profiles.Single())) await manager.UnmountAsync(profile.Id, force: true);
        }
    }
}
