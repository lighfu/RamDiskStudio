namespace RamDisk.Core;

public enum HistoryInterval { Second = 1, Minute = 60, Hour = 3600, Day = 86400 }
public enum MonitorMetric { Rate, Bytes, Operations, Iops, AverageSize, UsedBytes, UsagePercent }

public sealed record IoCounts(long ReadBytes = 0, long WriteBytes = 0, long ReadOps = 0, long WriteOps = 0)
{
    public static IoCounts operator +(IoCounts a, IoCounts b) => new(
        a.ReadBytes + b.ReadBytes, a.WriteBytes + b.WriteBytes, a.ReadOps + b.ReadOps, a.WriteOps + b.WriteOps);
}

// One received-event sampling interval. Duration uses a monotonic clock; UTC is used for storage.
public sealed record MonitorSample(Guid DiskId, DateTimeOffset Time, IoCounts Io, double IoSeconds,
    long? UsedBytes, long? TotalBytes);

public sealed record HistoryPoint(long UnixTime, IoCounts Io, double IoSeconds,
    double UsedBytesSum, double TotalBytesSum, long CapacitySamples)
{
    public (double? Read, double? Write) Values(MonitorMetric metric) => metric switch
    {
        MonitorMetric.Rate => IoSeconds > 0 ? (Io.ReadBytes / IoSeconds / 1048576, Io.WriteBytes / IoSeconds / 1048576) : (null, null),
        MonitorMetric.Bytes => IoSeconds > 0 ? (Io.ReadBytes / 1048576.0, Io.WriteBytes / 1048576.0) : (null, null),
        MonitorMetric.Operations => IoSeconds > 0 ? (Io.ReadOps, Io.WriteOps) : (null, null),
        MonitorMetric.Iops => IoSeconds > 0 ? (Io.ReadOps / IoSeconds, Io.WriteOps / IoSeconds) : (null, null),
        MonitorMetric.AverageSize => (Io.ReadOps > 0 ? Io.ReadBytes / (double)Io.ReadOps / 1024 : null,
            Io.WriteOps > 0 ? Io.WriteBytes / (double)Io.WriteOps / 1024 : null),
        MonitorMetric.UsedBytes => (CapacitySamples > 0 ? UsedBytesSum / CapacitySamples / 1073741824 : null, null),
        MonitorMetric.UsagePercent => (TotalBytesSum > 0 ? UsedBytesSum / TotalBytesSum * 100 : null, null),
        _ => (null, null)
    };
}

public sealed record HistoryDisk(Guid Id, string Name, char Letter)
{
    public override string ToString() => $"{Letter}:  {Name}";
}
public sealed record MonitorHistory(IReadOnlyList<HistoryPoint> Points, IoCounts Lifetime, long? FirstTime);

public sealed record MonitorTarget(DiskProfile Profile, DriveSnapshot Snapshot);

/// <summary>Only aggregate counters are retained; filenames are never stored.</summary>
public sealed class IoAccumulator
{
    private readonly object gate = new();
    private MonitorTarget[] targets = [];
    private sealed class MutableCounts { public long ReadBytes, WriteBytes, ReadOps, WriteOps; }
    private readonly Dictionary<Guid, MutableCounts> counts = [];

    public void SetTargets(IEnumerable<MonitorTarget> value)
    {
        lock (gate) targets = value.Where(x => x.Snapshot.IsOwnedBy(x.Profile)).ToArray();
    }

    public bool Record(string path, long bytes, bool write, int irpFlags = 0)
    {
        // WDK IRP_PAGING_IO. Exclude cache flush/page-in traffic to avoid counting a file request twice.
        if ((irpFlags & 0x2) != 0 || bytes < 0 || string.IsNullOrEmpty(path)) return false;
        lock (gate)
        {
            foreach (var target in targets)
            {
                if (!Matches(path, target.Profile.RootPath) && !Matches(path, target.Snapshot.DevicePath + "\\")) continue;
                if (!counts.TryGetValue(target.Profile.Id, out var current)) counts[target.Profile.Id] = current = new();
                if (write) { current.WriteBytes += bytes; current.WriteOps++; }
                else { current.ReadBytes += bytes; current.ReadOps++; }
                return true;
            }
        }
        return false;
    }

    private static bool Matches(string path, string root) => path.StartsWith(root, StringComparison.OrdinalIgnoreCase);

    public Dictionary<Guid, IoCounts> Drain()
    {
        lock (gate)
        {
            var result = counts.ToDictionary(x => x.Key, x => new IoCounts(x.Value.ReadBytes, x.Value.WriteBytes, x.Value.ReadOps, x.Value.WriteOps));
            counts.Clear();
            return result;
        }
    }
}
