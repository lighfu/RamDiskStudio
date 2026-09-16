using Microsoft.Data.Sqlite;

namespace RamDisk.Core;

/// <summary>Transactional multi-resolution history. Opening a corrupt DB never replaces it.</summary>
public sealed class MonitorStore
{
    private readonly string connectionString;
    private readonly object gate = new();
    public string FilePath { get; }
    private static readonly HistoryInterval[] Intervals = Enum.GetValues<HistoryInterval>();

    public MonitorStore(string path)
    {
        FilePath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        connectionString = new SqliteConnectionStringBuilder { DataSource = FilePath, Pooling = false }.ToString();
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = "PRAGMA user_version";
        var version = Convert.ToInt32(command.ExecuteScalar());
        if (version is not (0 or 1)) throw new InvalidDataException("監視履歴のバージョンが新しいため開けません。");
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS disks(id TEXT PRIMARY KEY, name TEXT NOT NULL, letter TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS totals(id TEXT PRIMARY KEY, first INTEGER NOT NULL,
                rb INTEGER NOT NULL, wb INTEGER NOT NULL, ro INTEGER NOT NULL, wo INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS samples(id TEXT NOT NULL, step INTEGER NOT NULL, time INTEGER NOT NULL,
                rb INTEGER NOT NULL, wb INTEGER NOT NULL, ro INTEGER NOT NULL, wo INTEGER NOT NULL,
                seconds REAL NOT NULL, used REAL NOT NULL, total REAL NOT NULL, capacity INTEGER NOT NULL,
                PRIMARY KEY(id,step,time)) WITHOUT ROWID;
            PRAGMA user_version=1;
            """;
        command.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var db = new SqliteConnection(connectionString);
        db.Open();
        return db;
    }

    public void Register(IEnumerable<DiskProfile> profiles)
    {
        lock (gate)
        {
            using var db = Open();
            using var tx = db.BeginTransaction();
            foreach (var profile in profiles)
            {
                using var cmd = db.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "INSERT INTO disks VALUES($id,$name,$letter) ON CONFLICT(id) DO UPDATE SET name=excluded.name,letter=excluded.letter";
                cmd.Parameters.AddWithValue("$id", profile.Id.ToString());
                cmd.Parameters.AddWithValue("$name", profile.Name);
                cmd.Parameters.AddWithValue("$letter", profile.DriveLetter.ToString());
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    public IReadOnlyList<HistoryDisk> Disks()
    {
        lock (gate)
        {
            using var db = Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT id,name,letter FROM disks ORDER BY letter,name";
            using var reader = cmd.ExecuteReader();
            var result = new List<HistoryDisk>();
            while (reader.Read()) result.Add(new(Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2)[0]));
            return result;
        }
    }

    public void Append(IReadOnlyList<MonitorSample> samples)
    {
        // Validate the entire batch before beginning the transaction.
        foreach (var s in samples)
            if (s.DiskId == Guid.Empty || !double.IsFinite(s.IoSeconds) || s.IoSeconds < 0 ||
                s.Io.ReadBytes < 0 || s.Io.WriteBytes < 0 || s.Io.ReadOps < 0 || s.Io.WriteOps < 0 ||
                (s.UsedBytes.HasValue != s.TotalBytes.HasValue) || s.UsedBytes < 0 || s.TotalBytes < s.UsedBytes)
                throw new ArgumentException("Invalid monitor sample");
        lock (gate)
        {
            using var db = Open();
            using var tx = db.BeginTransaction();
            foreach (var s in samples)
            {
                foreach (var interval in Intervals)
                {
                    using var cmd = db.CreateCommand();
                    cmd.Transaction = tx;
                    cmd.CommandText = """
                        INSERT INTO samples VALUES($id,$step,$time,$rb,$wb,$ro,$wo,$seconds,$used,$total,$capacity)
                        ON CONFLICT(id,step,time) DO UPDATE SET
                        rb=rb+excluded.rb, wb=wb+excluded.wb, ro=ro+excluded.ro, wo=wo+excluded.wo,
                        seconds=seconds+excluded.seconds, used=used+excluded.used,
                        total=total+excluded.total, capacity=capacity+excluded.capacity
                        """;
                    AddCounters(cmd, s);
                    cmd.Parameters.AddWithValue("$step", (int)interval);
                    cmd.Parameters.AddWithValue("$time", Bucket(s.Time, interval));
                    cmd.Parameters.AddWithValue("$seconds", s.IoSeconds);
                    cmd.Parameters.AddWithValue("$used", s.UsedBytes ?? 0);
                    cmd.Parameters.AddWithValue("$total", s.TotalBytes ?? 0);
                    cmd.Parameters.AddWithValue("$capacity", s.TotalBytes.HasValue ? 1 : 0);
                    cmd.ExecuteNonQuery();
                }
                using var total = db.CreateCommand();
                total.Transaction = tx;
                total.CommandText = """
                    INSERT INTO totals VALUES($id,$first,$rb,$wb,$ro,$wo) ON CONFLICT(id) DO UPDATE SET
                    first=MIN(first,excluded.first),rb=rb+excluded.rb,wb=wb+excluded.wb,ro=ro+excluded.ro,wo=wo+excluded.wo
                    """;
                AddCounters(total, s);
                total.Parameters.AddWithValue("$first", s.Time.ToUnixTimeSeconds());
                total.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    private static void AddCounters(SqliteCommand cmd, MonitorSample sample)
    {
        cmd.Parameters.AddWithValue("$id", sample.DiskId.ToString());
        cmd.Parameters.AddWithValue("$rb", sample.Io.ReadBytes);
        cmd.Parameters.AddWithValue("$wb", sample.Io.WriteBytes);
        cmd.Parameters.AddWithValue("$ro", sample.Io.ReadOps);
        cmd.Parameters.AddWithValue("$wo", sample.Io.WriteOps);
    }

    public static long Bucket(DateTimeOffset time, HistoryInterval interval) =>
        time.ToUnixTimeSeconds() / (int)interval * (int)interval;

    public MonitorHistory Read(Guid id, HistoryInterval interval, DateTimeOffset from, DateTimeOffset to)
    {
        lock (gate)
        {
            using var db = Open();
            using var tx = db.BeginTransaction();
            using var cmd = db.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT time,rb,wb,ro,wo,seconds,used,total,capacity FROM samples WHERE id=$id AND step=$step AND time >= $from AND time <= $to ORDER BY time";
            cmd.Parameters.AddWithValue("$id", id.ToString());
            cmd.Parameters.AddWithValue("$step", (int)interval);
            cmd.Parameters.AddWithValue("$from", Bucket(from, interval));
            cmd.Parameters.AddWithValue("$to", Bucket(to, interval));
            var points = new List<HistoryPoint>();
            using (var reader = cmd.ExecuteReader())
                while (reader.Read()) points.Add(new(reader.GetInt64(0), new(reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4)),
                    reader.GetDouble(5), reader.GetDouble(6), reader.GetDouble(7), reader.GetInt64(8)));
            cmd.CommandText = "SELECT rb,wb,ro,wo,first FROM totals WHERE id=$id";
            using var totals = cmd.ExecuteReader();
            return totals.Read() ? new(points, new(totals.GetInt64(0), totals.GetInt64(1), totals.GetInt64(2), totals.GetInt64(3)), totals.GetInt64(4)) : new(points, new(), null);
        }
    }

    public void Prune(DateTimeOffset now)
    {
        lock (gate)
        {
            using var db = Open();
            using var tx = db.BeginTransaction();
            foreach (var (step, age) in new[] { (HistoryInterval.Second, TimeSpan.FromHours(2)), (HistoryInterval.Minute, TimeSpan.FromDays(7)),
                (HistoryInterval.Hour, TimeSpan.FromDays(90)), (HistoryInterval.Day, TimeSpan.FromDays(730)) })
            {
                using var cmd = db.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "DELETE FROM samples WHERE step=$step AND time<$cutoff";
                cmd.Parameters.AddWithValue("$step", (int)step);
                cmd.Parameters.AddWithValue("$cutoff", Bucket(now - age, step));
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }
}
