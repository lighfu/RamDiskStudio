use crate::disk::{self, Drive, Profile};
use anyhow::{Result, ensure};
use chrono::Utc;
use ferrisetw::{
    parser::{Parser, Pointer},
    provider::{Provider, kernel_providers::*},
    trace::{KernelTrace, TraceTrait},
};
use rusqlite::{Connection, OptionalExtension, params};
use std::{
    collections::HashMap,
    path::Path,
    sync::{Arc, Mutex},
    thread::{self, JoinHandle},
    time::{Duration, Instant},
};
use uuid::Uuid;

pub const INTERVALS: [(i64, i64, &str); 4] = [
    (1, 7200, "毎秒"),
    (60, 604800, "毎分"),
    (3600, 7776000, "毎時"),
    (86400, 63072000, "毎日"),
];
pub const METRICS: [(&str, &str); 7] = [
    ("読み書き速度", "MiB/s"),
    ("読み書き量", "MiB"),
    ("アクセス回数", "回"),
    ("IOPS", "回/s"),
    ("平均サイズ", "KiB"),
    ("使用容量", "GiB"),
    ("使用率", "%"),
];
const MIB: f64 = 1048576.0;

#[derive(Clone, Copy, Default, Debug)]
pub struct Counts {
    pub rb: i64,
    pub wb: i64,
    pub ro: i64,
    pub wo: i64,
}

pub struct Point {
    pub time: i64,
    pub io: Counts,
    pub seconds: f64,
    pub used: f64,
    pub total: f64,
    pub capacity: i64,
}
impl Point {
    pub fn values(&self, metric: usize) -> [Option<f64>; 2] {
        let io = self.io;
        let measured = self.seconds > 0.0;
        match metric {
            0 if measured => [
                Some(io.rb as f64 / self.seconds / MIB),
                Some(io.wb as f64 / self.seconds / MIB),
            ],
            1 if measured => [Some(io.rb as f64 / MIB), Some(io.wb as f64 / MIB)],
            2 if measured => [Some(io.ro as f64), Some(io.wo as f64)],
            3 if measured => [
                Some(io.ro as f64 / self.seconds),
                Some(io.wo as f64 / self.seconds),
            ],
            4 => [
                (io.ro > 0).then(|| io.rb as f64 / io.ro as f64 / 1024.0),
                (io.wo > 0).then(|| io.wb as f64 / io.wo as f64 / 1024.0),
            ],
            5 if self.capacity > 0 => [
                Some(self.used / self.capacity as f64 / (1024.0 * MIB)),
                None,
            ],
            6 if self.total > 0.0 => [Some(self.used / self.total * 100.0), None],
            _ => [None, None],
        }
    }
}

pub struct History {
    db: Connection,
}
impl History {
    pub fn seed_preview(&mut self, profile: &Profile) -> Result<()> {
        let now = Utc::now().timestamp();
        for offset in (0..60).rev() {
            let drive = Drive {
                capacity: Some((1024 * 1024 * 1024, (420 + offset) * 1024 * 1024)),
                ..Default::default()
            };
            let counts = Counts {
                rb: ((13 + offset * 7 % 31) * 262144) as i64,
                wb: ((offset * 11 % 23) * 131072) as i64,
                ro: 12,
                wo: 8,
            };
            self.append(
                now - offset as i64,
                1.0,
                &[(profile.id, drive)],
                &HashMap::from([(profile.id, counts)]),
            )?;
        }
        Ok(())
    }

    pub fn open(dir: &Path) -> Result<Self> {
        std::fs::create_dir_all(dir)?;
        let db = Connection::open(dir.join("monitoring.db"))?;
        db.busy_timeout(Duration::from_millis(500))?;
        let version: i32 = db.pragma_query_value(None, "user_version", |r| r.get(0))?;
        ensure!(matches!(version, 0 | 1), "未対応の監視履歴バージョンです");
        db.execute_batch("PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS disks(id TEXT PRIMARY KEY, name TEXT NOT NULL, letter TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS totals(id TEXT PRIMARY KEY, first INTEGER NOT NULL, rb INTEGER NOT NULL, wb INTEGER NOT NULL, ro INTEGER NOT NULL, wo INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS samples(id TEXT NOT NULL, step INTEGER NOT NULL, time INTEGER NOT NULL,
            rb INTEGER NOT NULL, wb INTEGER NOT NULL, ro INTEGER NOT NULL, wo INTEGER NOT NULL, seconds REAL NOT NULL,
            used REAL NOT NULL, total REAL NOT NULL, capacity INTEGER NOT NULL, PRIMARY KEY(id,step,time)) WITHOUT ROWID;
            PRAGMA user_version=1;")?;
        Ok(Self { db })
    }
    pub fn disks(&self) -> Result<Vec<(String, String)>> {
        let mut statement = self
            .db
            .prepare("SELECT id, letter || ': ' || name FROM disks ORDER BY letter,name")?;
        Ok(statement
            .query_map([], |r| Ok((r.get(0)?, r.get(1)?)))?
            .collect::<rusqlite::Result<_>>()?)
    }
    pub fn register(&mut self, profiles: &[Profile]) -> Result<()> {
        let tx = self.db.transaction()?;
        for profile in profiles {
            tx.execute("INSERT INTO disks VALUES (?1,?2,?3) ON CONFLICT(id) DO UPDATE SET name=excluded.name,letter=excluded.letter",
                params![profile.id.to_string(), profile.name, profile.drive_letter.to_string()])?;
        }
        tx.commit()?;
        Ok(())
    }
    pub fn read(
        &self,
        id: &str,
        step: i64,
        from: i64,
        to: i64,
    ) -> Result<(Vec<Point>, Counts, Option<i64>)> {
        let mut query = self.db.prepare(
            "SELECT time,rb,wb,ro,wo,seconds,used,total,capacity FROM samples
            WHERE id=?1 AND step=?2 AND time BETWEEN ?3 AND ?4 ORDER BY time",
        )?;
        let points = query
            .query_map(params![id, step, bucket(from, step), to], |r| {
                Ok(Point {
                    time: r.get(0)?,
                    io: Counts {
                        rb: r.get(1)?,
                        wb: r.get(2)?,
                        ro: r.get(3)?,
                        wo: r.get(4)?,
                    },
                    seconds: r.get(5)?,
                    used: r.get(6)?,
                    total: r.get(7)?,
                    capacity: r.get(8)?,
                })
            })?
            .collect::<rusqlite::Result<_>>()?;
        let (counts, first) = self
            .db
            .query_row(
                "SELECT rb,wb,ro,wo,first FROM totals WHERE id=?1",
                [id],
                |r| {
                    Ok((
                        Counts {
                            rb: r.get(0)?,
                            wb: r.get(1)?,
                            ro: r.get(2)?,
                            wo: r.get(3)?,
                        },
                        Some(r.get(4)?),
                    ))
                },
            )
            .optional()?
            .unwrap_or_default();
        Ok((points, counts, first))
    }
    fn append(
        &mut self,
        now: i64,
        elapsed: f64,
        targets: &[(Uuid, Drive)],
        counters: &HashMap<Uuid, Counts>,
    ) -> Result<()> {
        let tx = self.db.transaction()?;
        for (id, drive) in targets {
            let id_string = id.to_string();
            let io = counters.get(id).copied().unwrap_or_default();
            let (total, free) = drive.capacity.unwrap_or_default();
            for (step, _, _) in INTERVALS {
                tx.execute("INSERT INTO samples VALUES (?1,?2,?3,?4,?5,?6,?7,?8,?9,?10,?11)
                    ON CONFLICT(id,step,time) DO UPDATE SET rb=rb+excluded.rb,wb=wb+excluded.wb,ro=ro+excluded.ro,wo=wo+excluded.wo,
                    seconds=seconds+excluded.seconds,used=used+excluded.used,total=total+excluded.total,capacity=capacity+excluded.capacity",
                    params![id_string, step, bucket(now, step), io.rb, io.wb, io.ro, io.wo, elapsed, total.saturating_sub(free), total, i32::from(drive.capacity.is_some())])?;
            }
            tx.execute("INSERT INTO totals VALUES (?1,?2,?3,?4,?5,?6) ON CONFLICT(id) DO UPDATE SET
                first=MIN(first,excluded.first),rb=rb+excluded.rb,wb=wb+excluded.wb,ro=ro+excluded.ro,wo=wo+excluded.wo",
                params![id_string, now, io.rb, io.wb, io.ro, io.wo])?;
        }
        tx.commit()?;
        Ok(())
    }
    fn prune(&mut self, now: i64) -> Result<()> {
        let tx = self.db.transaction()?;
        for (step, age, _) in INTERVALS {
            tx.execute(
                "DELETE FROM samples WHERE step=?1 AND time<?2",
                params![step, bucket(now - age, step)],
            )?;
        }
        tx.commit()?;
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn retains_totals_across_resolution_and_retention() -> Result<()> {
        let dir = tempfile::tempdir()?;
        let mut history = History::open(dir.path())?;
        let profile = Profile::default();
        history.register(std::slice::from_ref(&profile))?;
        let when = 1_800_000_000;
        let drive = Drive {
            capacity: Some((2048, 1024)),
            ..Default::default()
        };
        let targets = [(profile.id, drive)];
        let counters = HashMap::from([(
            profile.id,
            Counts {
                rb: 1_048_576,
                ro: 1,
                ..Default::default()
            },
        )]);
        history.append(when, 1.0, &targets, &counters)?;
        history.append(when + 1, 0.0, &targets, &HashMap::new())?;
        let id = profile.id.to_string();
        let (second, totals, first) = history.read(&id, 1, when, when + 2)?;
        assert_eq!(first, Some(when));
        assert_eq!(totals.rb, 1_048_576);
        assert_eq!(second[0].values(0), [Some(1.0), Some(0.0)]);
        assert_eq!(second[1].values(0), [None, None]);
        assert_eq!(second[1].values(6), [Some(50.0), None]);
        history.prune(when + 7205)?;
        assert!(history.read(&id, 1, when, when + 2)?.0.is_empty());
        let (minute, totals, _) = history.read(&id, 60, when, when + 2)?;
        assert_eq!(minute[0].values(0), [Some(1.0), Some(0.0)]);
        assert_eq!(totals.ro, 1);
        Ok(())
    }
}
fn bucket(time: i64, step: i64) -> i64 {
    time.div_euclid(step) * step
}

#[derive(Default)]
struct TraceState {
    targets: Vec<(Uuid, String, String)>,
    names: HashMap<usize, Uuid>,
    objects: HashMap<usize, Uuid>,
    counts: HashMap<Uuid, Counts>,
    unresolved: u64,
}
impl TraceState {
    fn remember(&mut self, address: usize, path: &str, object: bool) {
        let id = self
            .targets
            .iter()
            .find(|(_, letter, device)| {
                [letter, device].iter().any(|prefix| {
                    path.get(..prefix.len())
                        .is_some_and(|p| p.eq_ignore_ascii_case(prefix))
                })
            })
            .map(|(id, _, _)| *id);
        let map = if object {
            &mut self.objects
        } else {
            &mut self.names
        };
        if let Some(id) = id {
            if map.len() >= 65_536 {
                map.clear();
            }
            map.insert(address, id);
        } else {
            map.remove(&address);
        }
    }
}

pub struct Monitor {
    pub history: History,
    state: Arc<Mutex<TraceState>>,
    trace: Option<LiveTrace>,
    trace_error: Option<String>,
    last: Instant,
    prune: Instant,
}

struct LiveTrace {
    session: Option<KernelTrace>,
    worker: Option<JoinHandle<()>>,
}
impl LiveTrace {
    fn running(&self) -> bool {
        self.worker
            .as_ref()
            .is_some_and(|worker| !worker.is_finished())
    }
}
impl Drop for LiveTrace {
    fn drop(&mut self) {
        if let Some(trace) = self.session.take() {
            let _ = trace.stop();
        }
        if let Some(worker) = self.worker.take() {
            let _ = worker.join();
        }
    }
}
impl Monitor {
    pub fn open(dir: &Path, capture: bool) -> Result<Self> {
        let history = History::open(dir)?;
        history.disks()?;
        let state = Arc::new(Mutex::new(TraceState::default()));
        let (trace, trace_error) = if capture && disk::elevated() {
            match start_trace(state.clone()) {
                Ok(trace) => (Some(trace), None),
                Err(e) => (None, Some(format!("I/O 監視を開始できません: {e}"))),
            }
        } else {
            (None, None)
        };
        Ok(Self {
            history,
            state,
            trace,
            trace_error,
            last: Instant::now(),
            prune: Instant::now() - Duration::from_secs(900),
        })
    }
    pub fn status(&self) -> String {
        if let Some(error) = &self.trace_error {
            return error.clone();
        }
        if self.trace.is_none() {
            return "容量のみ監視中".into();
        }
        if !self.trace.as_ref().is_some_and(LiveTrace::running) {
            return "I/O 監視が終了しました · 容量のみ監視中".into();
        }
        let unresolved = self
            .state
            .lock()
            .unwrap_or_else(|e| e.into_inner())
            .unresolved;
        if unresolved == 0 {
            "I/O 監視中".into()
        } else {
            format!("I/O 監視中 · 名前未解決 {unresolved} 件")
        }
    }
    pub fn tick(&mut self, profiles: &[Profile], drives: &[Drive]) -> Result<()> {
        let now = Utc::now().timestamp();
        let instant = Instant::now();
        let targets: Vec<_> = profiles
            .iter()
            .zip(drives)
            .filter(|(p, d)| d.owned(p))
            .map(|(p, d)| (p.id, d.clone()))
            .collect();
        let (covered, counters) = {
            let mut state = self.state.lock().unwrap_or_else(|e| e.into_inner());
            let next: Vec<_> = profiles
                .iter()
                .zip(drives)
                .filter(|(p, d)| d.owned(p))
                .filter_map(|(p, d)| Some((p.id, p.root(), format!("{}\\", d.device.as_ref()?))))
                .collect();
            let covered: Vec<_> = state
                .targets
                .iter()
                .filter(|target| next.contains(target))
                .map(|t| t.0)
                .collect();
            if state.targets != next {
                state.names.clear();
                state.objects.clear();
                state.targets = next;
            }
            (covered, std::mem::take(&mut state.counts))
        };
        let elapsed = instant.duration_since(self.last).as_secs_f64();
        self.last = instant;
        // A newly discovered target has no observations for the preceding interval.
        let (seen, unseen): (Vec<_>, Vec<_>) = targets
            .into_iter()
            .partition(|(id, _)| covered.contains(id));
        let io_seconds = if self.trace.as_ref().is_some_and(LiveTrace::running) && elapsed <= 5.0 {
            elapsed
        } else {
            0.0
        };
        self.history.append(now, io_seconds, &seen, &counters)?;
        self.history.append(now, 0.0, &unseen, &HashMap::new())?;
        if instant.duration_since(self.prune) >= Duration::from_secs(900) {
            self.history.prune(now)?;
            self.prune = instant;
        }
        Ok(())
    }
}

fn start_trace(state: Arc<Mutex<TraceState>>) -> Result<LiveTrace> {
    let file = Provider::kernel(&FILE_IO_PROVIDER)
        .add_callback(move |record, locator| {
            let opcode = record.opcode();
            if !matches!(opcode, 0 | 32 | 35 | 36 | 64 | 66 | 67 | 68) {
                return;
            }
            let Ok(schema) = locator.event_schema(record) else {
                return;
            };
            let parser = Parser::create(record, &schema);
            let address = |key: &str| parser.try_parse::<Pointer>(key).ok().map(|ptr| *ptr);
            let mut state = state.lock().unwrap_or_else(|e| e.into_inner());
            match opcode {
                0 | 32 | 36 | 64 => {
                    let property = if opcode == 64 { "OpenPath" } else { "FileName" };
                    if let (Some(address), Ok(path)) =
                        (address("FileObject"), parser.try_parse::<String>(property))
                    {
                        state.remember(address, &path, opcode == 64);
                    }
                }
                35 | 66 => {
                    if let Some(address) = address("FileObject") {
                        if opcode == 35 {
                            state.names.remove(&address);
                        } else {
                            state.objects.remove(&address);
                        }
                    }
                }
                67 | 68 => {
                    let Ok(flags) = parser.try_parse::<u32>("IoFlags") else {
                        return;
                    };
                    if flags & 2 != 0 || state.targets.is_empty() {
                        return;
                    }
                    let disk_id = address("FileKey")
                        .and_then(|key| state.names.get(&key))
                        .or_else(|| address("FileObject").and_then(|key| state.objects.get(&key)))
                        .copied();
                    if let (Some(id), Ok(size)) = (disk_id, parser.try_parse::<u32>("IoSize")) {
                        let counts = state.counts.entry(id).or_default();
                        if opcode == 67 {
                            counts.rb += i64::from(size);
                            counts.ro += 1;
                        } else {
                            counts.wb += i64::from(size);
                            counts.wo += 1;
                        }
                    } else if disk_id.is_none() {
                        state.unresolved = state.unresolved.saturating_add(1);
                    }
                }
                _ => {}
            }
        })
        .build();
    let (session, handle) = KernelTrace::new()
        .named(format!(
            "RamDiskStudio.FileIO.{}.{}",
            std::process::id(),
            Uuid::new_v4()
        ))
        .enable(file)
        .enable(Provider::kernel(&FILE_INIT_IO_PROVIDER).build())
        .enable(Provider::kernel(&DISK_FILE_IO_PROVIDER).build())
        .enable(Provider::kernel(&DISK_IO_PROVIDER).build())
        .start()
        .map_err(|error| anyhow::anyhow!("{error:?}"))?;
    let worker = thread::Builder::new()
        .name("ramdisk-etw".into())
        .spawn(move || {
            if let Err(error) = KernelTrace::process_from_handle(handle) {
                eprintln!("ETW: {error:?}");
            }
        })?;
    Ok(LiveTrace {
        session: Some(session),
        worker: Some(worker),
    })
}
