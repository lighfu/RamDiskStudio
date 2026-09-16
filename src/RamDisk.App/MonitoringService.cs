using System.Diagnostics;
using RamDisk.Core;

namespace RamDisk.App;

public sealed class MonitoringService(IDiskPlatform platform, MonitorStore store)
{
    private DiskProfile[] profiles = [];
    private readonly CancellationTokenSource cancellation = new();
    private Task? worker;
    private string status = "監視を準備しています…";
    private long unresolvedEvents;
    public long UnresolvedEvents => Interlocked.Read(ref unresolvedEvents);
    private MonitorSample[] latest = [];
    public string Status => Volatile.Read(ref status);
    public IReadOnlyList<MonitorSample> Latest => Volatile.Read(ref latest);

    public void SetProfiles(IEnumerable<DiskProfile> value) => Volatile.Write(ref profiles, value.ToArray());
    public void Start() => worker ??= Task.Run(RunAsync);
    public async Task StopAsync()
    {
        await cancellation.CancelAsync();
        if (worker is not null) await worker;
    }

    private async Task RunAsync()
    {
        using var io = new EtwIoMonitor();
        var watch = Stopwatch.StartNew();
        var previous = watch.Elapsed.TotalSeconds;
        DiskProfile[] registered = [];
        MonitorTarget[] targets = [];
        string? ioFailure = null;
        long lastPrune = 0;
        try
        {
            if (platform.IsAdministrator)
            {
                try { io.Start(); }
                catch (Exception ex) { ioFailure = "I/O 監視を開始できません: " + ex.Message; io.Dispose(); }
            }
            else ioFailure = "I/O 監視には管理者としての再起動が必要です。容量は監視できます。";

            void Sample(bool final = false)
            {
                var current = Volatile.Read(ref profiles);
                if (!current.SequenceEqual(registered)) { store.Register(current); registered = current; }
                var now = DateTimeOffset.UtcNow;
                var elapsed = watch.Elapsed.TotalSeconds;
                var seconds = elapsed - previous;
                previous = elapsed;
                var counts = io.Counter.Drain();
                var next = current.Select(p => new MonitorTarget(p, platform.Inspect(p.DriveLetter)))
                    .Where(x => x.Snapshot.IsOwnedBy(x.Profile) && x.Snapshot.IsReady).ToArray();
                // Only targets observed at both ends receive a measured duration. Long scheduling/sleep gaps are missing.
                var samples = new List<MonitorSample>();
                foreach (var id in next.Select(x => x.Profile.Id).Concat(counts.Keys).Distinct())
                {
                    var disk = next.FirstOrDefault(x => x.Profile.Id == id);
                    var stable = disk is not null && targets.Any(x => x.Profile.Id == id && x.Profile.Identity == disk.Profile.Identity);
                    var duration = stable && seconds <= 5 && (io.Running || final) ? seconds : 0;
                    samples.Add(new(id, now, counts.GetValueOrDefault(id) ?? new(), duration,
                        disk is null ? null : disk.Snapshot.TotalBytes - disk.Snapshot.FreeBytes, disk?.Snapshot.TotalBytes));
                }
                store.Append(samples);
                Volatile.Write(ref latest, samples.ToArray());
                targets = next;
                io.Counter.SetTargets(next);
                if (now.ToUnixTimeSeconds() - lastPrune > 600)
                {
                    store.Prune(now);
                    lastPrune = now.ToUnixTimeSeconds();
                }
                var lost = io.Running ? io.EventsLost : 0;
                Interlocked.Exchange(ref unresolvedEvents, io.UnresolvedEvents);
                Volatile.Write(ref status, ioFailure ?? io.Error ?? (lost > 0
                    ? $"イベント欠落 {lost:N0} 件（Windows 全体）。集計値に欠落がある可能性があります。"
                    : next.Length > 0 ? $"● 監視中 · {next.Length} ドライブ · 約 1 秒更新" : "作成済みの RAM ディスクを待っています。履歴は表示できます。"));
            }

            Sample();
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            try { while (await timer.WaitForNextTickAsync(cancellation.Token)) Sample(); }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            var wasRunning = io.Running;
            io.Dispose();
            Sample(final: wasRunning);
        }
        catch (Exception ex) { Volatile.Write(ref status, "監視を停止しました: " + ex.Message); }
    }
}
