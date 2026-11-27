use crate::{
    disk::{self, Drive, FileSystem, MemoryMode, Profile},
    monitor::{self, INTERVALS, METRICS, Monitor},
    startup,
};
use anyhow::{Context as _, Result};
use chrono::{DateTime, Local, Utc};
use gpui::{prelude::*, *};
use m3e::{
    Text,
    core::{
        color::{Color, ColorRole},
        tokens::TypeStyle,
    },
    *,
};
use std::{cell::Cell, rc::Rc};
use std::{path::PathBuf, time::Duration};
use uuid::Uuid;

#[derive(Clone, Copy, PartialEq)]
enum Page {
    Disks,
    Monitor,
    Startup,
}
#[derive(Clone, Copy)]
enum Operation {
    Mount(Uuid),
    Delete(Uuid),
    Unmount(Uuid, bool),
    Startup(bool),
    AutoMount,
}
impl Operation {
    fn run(self, dir: &std::path::Path, profiles: &mut Vec<Profile>) -> Result<Option<Operation>> {
        match self {
            Self::Mount(id) => disk::mount(dir, profiles, id)?,
            Self::Unmount(id, force) => {
                if !disk::unmount(dir, profiles, id, force)? {
                    return Ok(Some(Self::Unmount(id, true)));
                }
            }
            Self::Delete(id) => disk::delete_profile(dir, profiles, id)?,
            Self::Startup(enable) => startup::configure_interactive(enable)?,
            Self::AutoMount => {
                let targets: Vec<_> = profiles
                    .iter()
                    .filter(|p| p.mount_on_app_start)
                    .map(|p| (p.id, p.drive_letter))
                    .collect();
                let mut errors = Vec::new();
                for (id, letter) in targets {
                    let result = disk::inspect(letter).and_then(|drive| {
                        let profile = profiles
                            .iter()
                            .find(|p| p.id == id)
                            .expect("startup profile");
                        if drive.owned(profile) {
                            Ok(())
                        } else {
                            disk::mount(dir, profiles, id)
                        }
                    });
                    if let Err(error) = result {
                        errors.push(format!("{letter}: {error}"));
                    }
                }
                if !errors.is_empty() {
                    anyhow::bail!("{}", errors.join("\n"));
                }
            }
        }
        Ok(None)
    }
}
struct Editor {
    draft: Profile,
    name: Entity<TextField>,
    size: Entity<TextField>,
    letter: Entity<TextField>,
    label: Entity<TextField>,
    gib: bool,
}
impl Editor {
    fn new(draft: Profile, cx: &mut Context<Studio>) -> Self {
        let mut field = |label: &str, value: String| {
            cx.new(|cx| {
                TextField::with_props(
                    TextFieldProps {
                        label: SharedString::from(label.to_owned()),
                        value: value.into(),
                        variant: TextFieldVariant::Outlined,
                        ..Default::default()
                    },
                    cx,
                )
            })
        };
        Self {
            name: field("名前", draft.name.clone()),
            size: field("容量", draft.size_mib.to_string()),
            letter: field("ドライブ文字", draft.drive_letter.to_string()),
            label: field("ボリューム名", draft.volume_label.clone()),
            draft,
            gib: false,
        }
    }
    fn value(&self, cx: &App) -> Result<Profile> {
        let mut profile = self.draft.clone();
        profile.name = self.name.read(cx).value().trim().to_string();
        profile.volume_label = self.label.read(cx).value().trim().to_string();
        let letter = self.letter.read(cx).value();
        let mut chars = letter.trim().trim_end_matches(':').chars();
        profile.drive_letter = chars
            .next()
            .context("ドライブ文字を入力してください")?
            .to_ascii_uppercase();
        anyhow::ensure!(chars.next().is_none(), "ドライブ文字は1文字です");
        let raw: f64 = self
            .size
            .read(cx)
            .value()
            .trim()
            .parse()
            .context("容量を数値で入力してください")?;
        let mib = raw * if self.gib { 1024.0 } else { 1.0 };
        anyhow::ensure!(
            mib.is_finite()
                && (64.0..=1_048_576.0).contains(&mib)
                && (mib - mib.round()).abs() < 0.000001,
            "容量は整数の MiB に換算できる値を入力してください"
        );
        profile.size_mib = mib.round() as u32;
        profile.validate()?;
        Ok(profile)
    }
}

fn button(id: impl Into<ElementId>, label: impl Into<SharedString>) -> Button {
    Button::new(id, label)
        .size(ComponentSize::Xs)
        .variant(ButtonVariant::Outlined)
}
#[track_caller]
fn title(label: impl Into<SharedString>) -> Text {
    Text::new(label).type_style(TypeStyle::HeadlineSmall)
}
#[track_caller]
fn sub(label: impl Into<SharedString>) -> Text {
    Text::new(label).color_role(ColorRole::OnSurfaceVariant)
}
fn wrap() -> Div {
    row().gap(px(8.0)).flex_wrap().items_center()
}
fn time_label(time: i64) -> String {
    DateTime::from_timestamp(time, 0)
        .map(|t| t.with_timezone(&Local).format("%m/%d %H:%M:%S").to_string())
        .unwrap_or_default()
}

pub struct Studio {
    dir: PathBuf,
    profiles: Vec<Profile>,
    drives: Vec<Drive>,
    selected: Option<Uuid>,
    page: Page,
    monitor: Option<Monitor>,
    history_id: Option<String>,
    metric: [usize; 2],
    interval: usize,
    span: i64,
    preset_span: i64,
    end: Option<i64>,
    scale: [usize; 2],
    y_range: [Option<ChartRange>; 2],
    comparison: bool,
    wide: bool,
    series: [bool; 2],
    cursor: Option<i64>,
    chart_height: f32,
    resize_origin: Option<(Pixels, f32)>,
    drag: Option<(usize, Point<Pixels>, bool, bool)>,
    editor: Option<Editor>,
    confirmation: Option<Operation>,
    busy: bool,
    isolated: bool,
    locked: bool,
    smoke_phase: Option<u8>,
    status: String,
    startup: Result<Option<PathBuf>, String>,
    help: bool,
}
impl Studio {
    pub fn can_close(&self) -> bool {
        !self.busy
    }

    pub fn new(
        dir: PathBuf,
        isolated: bool,
        smoke: bool,
        show_monitor: bool,
        cx: &mut Context<Self>,
    ) -> Self {
        let loaded = disk::load_profiles(&dir);
        let locked = loaded.is_err();
        let mut status = loaded
            .as_ref()
            .err()
            .map(ToString::to_string)
            .unwrap_or_default();
        let mut profiles = loaded.unwrap_or_default();
        if isolated {
            profiles.push(Profile::default());
        }
        profiles.sort_by_key(|p| p.drive_letter);
        let selected = profiles.first().map(|p| p.id);
        let monitor = match Monitor::open(&dir, !isolated && !locked) {
            Ok(mut monitor) => {
                if let Err(error) = monitor.history.register(&profiles) {
                    status = error.to_string();
                }
                if isolated {
                    monitor
                        .history
                        .seed_preview(&profiles[0])
                        .expect("seed temporary chart data");
                }
                Some(monitor)
            }
            Err(error) => {
                if status.is_empty() {
                    status = format!("監視履歴を開けません: {error}");
                }
                None
            }
        };
        let mut studio = Self {
            dir,
            profiles,
            drives: vec![],
            selected,
            page: if show_monitor {
                Page::Monitor
            } else {
                Page::Disks
            },
            monitor,
            history_id: None,
            metric: [0, 6],
            interval: 0,
            span: 120,
            preset_span: 120,
            end: None,
            scale: [0, 0],
            y_range: [None; 2],
            comparison: true,
            wide: false,
            series: [true, true],
            cursor: None,
            chart_height: 154.0,
            resize_origin: None,
            drag: None,
            editor: None,
            confirmation: None,
            busy: false,
            isolated,
            locked,
            smoke_phase: smoke.then_some(0),
            status,
            startup: if isolated {
                Ok(None)
            } else {
                startup_on_worker()
            },
            help: false,
        };
        if isolated {
            studio.drives = vec![Drive::default(); studio.profiles.len()];
        } else {
            studio.refresh();
        }
        if !isolated && !locked {
            if disk::elevated()
                && disk::driver_installed()
                && studio.profiles.iter().any(|p| p.mount_on_app_start)
            {
                studio.operate(Operation::AutoMount, cx);
            }
            let timer = cx.background_executor().clone();
            cx.spawn(async move |view, cx| {
                loop {
                    timer.timer(Duration::from_secs(1)).await;
                    if view
                        .update(cx, |view, cx| {
                            if !view.busy {
                                view.refresh();
                                if let Some(monitor) = &mut view.monitor {
                                    if let Err(error) = monitor.tick(&view.profiles, &view.drives) {
                                        view.status = format!("監視履歴: {error}");
                                    }
                                }
                                cx.notify();
                            }
                        })
                        .is_err()
                    {
                        break;
                    }
                }
            })
            .detach();
        }
        studio
    }

    fn refresh(&mut self) {
        self.drives = self
            .profiles
            .iter()
            .map(|p| match disk::inspect(p.drive_letter) {
                Ok(drive) => drive,
                Err(error) => {
                    self.status = error.to_string();
                    Drive {
                        device: Some("unknown".into()),
                        ..Default::default()
                    }
                }
            })
            .collect();
    }

    fn operate(&mut self, operation: Operation, cx: &mut Context<Self>) {
        if self.isolated
            && matches!(
                operation,
                Operation::Mount(_)
                    | Operation::Unmount(_, _)
                    | Operation::Startup(_)
                    | Operation::AutoMount
            )
        {
            return;
        }
        if self.busy || (self.locked && !matches!(operation, Operation::Startup(_))) {
            return;
        }
        self.busy = true;
        self.confirmation = None;
        self.status.clear();
        let dir = self.dir.clone();
        let mut profiles = self.profiles.clone();
        let task = cx.background_executor().spawn(async move {
            let result = operation.run(&dir, &mut profiles);
            let startup = if matches!(operation, Operation::Startup(_)) {
                Some(startup::status().map_err(|e| e.to_string()))
            } else {
                None
            };
            (profiles, result, startup)
        });
        cx.spawn(async move |view, cx| {
            let (profiles, result, startup) = task.await;
            let _ = view.update(cx, |view, cx| {
                view.profiles = profiles;
                view.busy = false;
                if let Some(startup) = startup {
                    view.startup = startup;
                }
                match result {
                    Ok(confirmation) => {
                        if matches!(operation, Operation::Startup(true)) && !disk::elevated() {
                            if let Ok(Some(executable)) = &view.startup {
                                match disk::shell_open(executable, "open", "") {
                                    Ok(()) => {
                                        cx.quit();
                                        return;
                                    }
                                    Err(error) => {
                                        view.status = error.to_string();
                                        cx.notify();
                                        return;
                                    }
                                }
                            }
                        }
                        view.confirmation = confirmation;
                        if confirmation.is_none() && !matches!(operation, Operation::AutoMount) {
                            view.status = "完了".into();
                        }
                    }
                    Err(error) => view.status = format!("{error:#}"),
                }
                view.refresh();
                if let Some(monitor) = &mut view.monitor {
                    if let Err(error) = monitor.history.register(&view.profiles) {
                        view.status = error.to_string();
                    }
                }
                cx.notify();
            });
        })
        .detach();
    }

    fn reset_chart(&mut self) {
        self.end = None;
        self.span = self.preset_span;
        self.y_range = [None; 2];
        self.cursor = None;
    }

    fn open_editor(&mut self, id: Option<Uuid>, cx: &mut Context<Self>) {
        let profile = if let Some(id) = id {
            let Some(profile) = self.profiles.iter().find(|p| p.id == id) else {
                return;
            };
            profile.clone()
        } else {
            let letter = ('D'..='Z').find(|&letter| {
                !self.profiles.iter().any(|p| p.drive_letter == letter)
                    && disk::inspect(letter).is_ok_and(|d| !d.exists())
            });
            let Some(letter) = letter else {
                self.status = "空いているドライブ文字がありません".into();
                cx.notify();
                return;
            };
            Profile {
                drive_letter: letter,
                ..Default::default()
            }
        };
        self.editor = Some(Editor::new(profile, cx));
        self.status.clear();
        cx.notify();
    }

    fn save_editor(&mut self, cx: &mut Context<Self>) {
        let result = self
            .editor
            .as_ref()
            .context("設定がありません")
            .and_then(|editor| editor.value(cx))
            .and_then(|profile| {
                let id = profile.id;
                disk::save_edit(&self.dir, &mut self.profiles, profile)?;
                Ok(id)
            });
        match result {
            Ok(id) => {
                self.editor = None;
                self.selected = Some(id);
                self.refresh();
                self.status = "保存しました".into();
                if let Some(monitor) = &mut self.monitor {
                    if let Err(error) = monitor.history.register(&self.profiles) {
                        self.status = error.to_string();
                    }
                }
            }
            Err(error) => self.status = error.to_string(),
        }
        cx.notify();
    }

    fn editor_view(&self, cx: &Context<Self>) -> Div {
        let editor = self.editor.as_ref().expect("editor is open");
        let fs_buttons = wrap().children(
            [FileSystem::Ntfs, FileSystem::ExFat, FileSystem::Fat32]
                .into_iter()
                .enumerate()
                .map(|(i, fs)| {
                    button(("fs", i), fs.label())
                        .variant(if editor.draft.file_system == fs {
                            ButtonVariant::Filled
                        } else {
                            ButtonVariant::Outlined
                        })
                        .on_click(cx.listener(move |v, _, _, cx| {
                            if let Some(e) = &mut v.editor {
                                e.draft.file_system = fs;
                            }
                            cx.notify();
                        }))
                }),
        );
        let memory = wrap().children(
            [
                ("標準", MemoryMode::Virtual),
                ("物理メモリ固定", MemoryMode::Physical),
            ]
            .into_iter()
            .enumerate()
            .map(|(i, (name, mode))| {
                button(("memory", i), name)
                    .variant(if editor.draft.memory_mode == mode {
                        ButtonVariant::Filled
                    } else {
                        ButtonVariant::Outlined
                    })
                    .on_click(cx.listener(move |v, _, _, cx| {
                        if let Some(e) = &mut v.editor {
                            e.draft.memory_mode = mode;
                        }
                        cx.notify();
                    }))
            }),
        );
        column()
            .gap(px(14.0))
            .max_w(px(720.0))
            .child(title(
                if self.profiles.iter().any(|p| p.id == editor.draft.id) {
                    "ディスクを編集"
                } else {
                    "ディスクを追加"
                },
            ))
            .child(editor.name.clone())
            .child(
                wrap()
                    .child(div().w(px(170.0)).child(editor.size.clone()))
                    .child(
                        button("unit", if editor.gib { "GiB" } else { "MiB" }).on_click(
                            cx.listener(|v, _, _, cx| {
                                if let Some(e) = &mut v.editor {
                                    if let Ok(value) = e.size.read(cx).value().trim().parse::<f64>()
                                    {
                                        let value = if e.gib {
                                            value * 1024.0
                                        } else {
                                            value / 1024.0
                                        };
                                        e.size.update(cx, |field, cx| {
                                            field.set_value(value.to_string(), cx)
                                        });
                                    }
                                    e.gib = !e.gib;
                                }
                                cx.notify();
                            }),
                        ),
                    )
                    .child(div().w(px(144.0)).child(editor.letter.clone())),
            )
            .child(sub("ファイルシステム"))
            .child(fs_buttons)
            .child(editor.label.clone())
            .child(sub("メモリ方式"))
            .child(memory)
            .when(
                editor.draft.memory_mode == MemoryMode::Physical && !disk::awe_installed(),
                |c| c.child(sub("awealloc が未導入です")),
            )
            .child(
                wrap()
                    .child(
                        button(
                            "temp",
                            if editor.draft.create_temp_folder {
                                "✓ Temp フォルダー"
                            } else {
                                "Temp フォルダー"
                            },
                        )
                        .on_click(cx.listener(|v, _, _, cx| {
                            if let Some(e) = &mut v.editor {
                                e.draft.create_temp_folder = !e.draft.create_temp_folder;
                            }
                            cx.notify();
                        })),
                    )
                    .child(
                        button(
                            "auto-mount",
                            if editor.draft.mount_on_app_start {
                                "✓ 起動時に作成"
                            } else {
                                "起動時に作成"
                            },
                        )
                        .on_click(cx.listener(|v, _, _, cx| {
                            if let Some(e) = &mut v.editor {
                                e.draft.mount_on_app_start = !e.draft.mount_on_app_start;
                            }
                            cx.notify();
                        })),
                    ),
            )
            .child(
                wrap()
                    .child(button("cancel-editor", "キャンセル").on_click(cx.listener(
                        |v, _, _, cx| {
                            v.editor = None;
                            v.status.clear();
                            cx.notify();
                        },
                    )))
                    .child(
                        Button::new("save-editor", "保存")
                            .on_click(cx.listener(|v, _, _, cx| v.save_editor(cx))),
                    ),
            )
    }

    fn disks(&self, cx: &Context<Self>) -> Div {
        let installed = disk::driver_installed();
        let elevated = disk::elevated();
        let driver_status = if !installed {
            "ImDisk 未導入"
        } else if !elevated {
            "管理者権限なし"
        } else {
            ""
        };
        let memory = disk::memory().ok();
        let (count, assigned) = self
            .profiles
            .iter()
            .zip(&self.drives)
            .filter(|(p, d)| d.owned(p))
            .fold((0, 0u64), |(n, sum), (p, _)| {
                (n + 1, sum + u64::from(p.size_mib))
            });
        let tiles = [
            (
                "空きメモリ",
                memory
                    .map(|(total, free)| {
                        format!(
                            "{:.1} / {:.1} GiB",
                            free as f64 / 1073741824.0,
                            total as f64 / 1073741824.0
                        )
                    })
                    .unwrap_or_else(|| "—".into()),
            ),
            ("稼働中", format!("{count} 台")),
            (
                "割り当て済み",
                format!("{:.2} GiB", assigned as f64 / 1024.0),
            ),
        ];
        let mut page =
            column()
                .gap(px(16.0))
                .child(
                    row()
                        .justify_between()
                        .items_center()
                        .child(title("RAM ディスク"))
                        .child(
                            Button::new("add", "ディスクを追加")
                                .disabled(self.busy || self.locked)
                                .on_click(cx.listener(|v, _, _, cx| v.open_editor(None, cx))),
                        ),
                )
                .child(wrap().children(tiles.into_iter().map(|(label, value)| {
                    Card::new()
                        .variant(CardVariant::Filled)
                        .min_w(px(148.0))
                        .flex_1()
                        .child(sub(label).id(format!("stat-label-{label}")))
                        .child(
                            Text::new(value)
                                .id(format!("stat-value-{label}"))
                                .type_style(TypeStyle::TitleLarge),
                        )
                })))
                .when(!driver_status.is_empty(), |c| {
                    c.child(
                        wrap()
                            .child(sub(driver_status))
                            .when(!installed, |r| {
                                r.child(button("get-driver", "ドライバーの入手").on_click(
                                    cx.listener(|v, _, _, cx| {
                                        if let Err(e) = disk::shell_open(
                                            "https://www.ltr-data.se/opencode.html/#ImDisk",
                                            "open",
                                            "",
                                        ) {
                                            v.status = e.to_string();
                                            cx.notify();
                                        }
                                    }),
                                ))
                            })
                            .when(!elevated, |r| {
                                r.child(
                                    button("admin", "管理者として再起動")
                                        .disabled(self.busy)
                                        .on_click(cx.listener(|v, _, _, cx| {
                                            match std::env::current_exe()
                                                .map_err(anyhow::Error::from)
                                                .and_then(|exe| {
                                                    disk::shell_open(exe, "runas", "--elevated")
                                                }) {
                                                Ok(()) => cx.quit(),
                                                Err(error) => {
                                                    v.status = error.to_string();
                                                    cx.notify();
                                                }
                                            }
                                        })),
                                )
                            }),
                    )
                });
        let mut list = column()
            .gap(px(4.0))
            .child(Text::new("ディスク一覧").type_style(TypeStyle::TitleMedium));
        for (index, (profile, drive)) in self.profiles.iter().zip(&self.drives).enumerate() {
            let id = profile.id;
            let mounted = drive.owned(profile);
            let status = if mounted {
                "稼働中"
            } else if drive.exists() {
                "文字が使用中"
            } else {
                "未作成"
            };
            let mem = if profile.memory_mode == MemoryMode::Physical {
                "物理メモリ固定"
            } else {
                "標準メモリ"
            };
            list = list.child(
                ListItem::new(
                    ("disk", index),
                    format!("{}  {}", profile.mount_point(), profile.name),
                )
                .supporting(format!(
                    "{:.2} GiB · {} · {}{}",
                    profile.size_mib as f64 / 1024.0,
                    profile.file_system.label(),
                    mem,
                    if profile.mount_on_app_start {
                        " · 自動作成"
                    } else {
                        ""
                    }
                ))
                .trailing_text(status)
                .selected(self.selected == Some(id))
                .on_click(cx.listener(move |v, _, _, cx| {
                    v.selected = Some(id);
                    cx.notify();
                })),
            );
        }
        page = page.child(
            Card::new()
                .variant(CardVariant::Outlined)
                .child(list)
                .when(self.profiles.is_empty(), |c| c.child(sub("ディスク未登録"))),
        );
        if let Some((profile, drive)) = self
            .profiles
            .iter()
            .zip(&self.drives)
            .find(|(p, _)| Some(p.id) == self.selected)
        {
            let id = profile.id;
            let mounted = drive.owned(profile);
            let root = profile.root();
            let disabled = self.busy || self.locked || self.confirmation.is_some();
            let details = drive.capacity.filter(|_| mounted).map(|(_, free)| {
                format!(
                    "{} 空き {:.2} GiB",
                    profile.mount_point(),
                    free as f64 / 1073741824.0
                )
            });
            page = page.child(
                wrap()
                    .when_some(details, |row, text| row.child(sub(text)))
                    .child(
                        button("edit", "編集")
                            .disabled(disabled || drive.exists())
                            .on_click(cx.listener(move |v, _, _, cx| v.open_editor(Some(id), cx))),
                    )
                    .child(
                        button("delete", "削除")
                            .disabled(disabled || mounted)
                            .on_click(cx.listener(move |v, _, _, cx| {
                                v.confirmation = Some(Operation::Delete(id));
                                cx.notify();
                            })),
                    )
                    .child(
                        button("open", "開く")
                            .disabled(disabled || !mounted || drive.capacity.is_none())
                            .on_click(cx.listener(move |v, _, _, cx| {
                                if let Err(error) = disk::shell_open(&root, "open", "") {
                                    v.status = error.to_string();
                                    cx.notify();
                                }
                            })),
                    )
                    .child(
                        Button::new("mount", if mounted { "解除" } else { "作成" })
                            .disabled(
                                disabled || !installed || !elevated || (drive.exists() && !mounted),
                            )
                            .on_click(cx.listener(move |v, _, _, cx| {
                                if mounted {
                                    v.confirmation = Some(Operation::Unmount(id, false));
                                    cx.notify();
                                } else {
                                    v.operate(Operation::Mount(id), cx);
                                }
                            })),
                    ),
            );
        }
        page.child(sub("解除・再起動・電源断でデータは消えます。"))
    }

    fn startup_view(&self, cx: &Context<Self>) -> Div {
        let state = match &self.startup {
            Ok(Some(_)) => "有効".into(),
            Ok(None) => "無効".into(),
            Err(e) => e.clone(),
        };
        column()
            .gap(px(16.0))
            .child(title("自動起動"))
            .child(Text::new(state))
            .child(sub("ログオン時に指定したディスクを自動作成します。"))
            .child(
                wrap()
                    .child(
                        Button::new(
                            "startup-enable",
                            if matches!(self.startup, Ok(Some(_))) {
                                "登録を更新"
                            } else {
                                "有効にする"
                            },
                        )
                        .disabled(self.busy)
                        .on_click(
                            cx.listener(|v, _, _, cx| v.operate(Operation::Startup(true), cx)),
                        ),
                    )
                    .child(
                        button("startup-disable", "無効にする")
                            .disabled(self.busy || matches!(self.startup, Ok(None)))
                            .on_click(
                                cx.listener(|v, _, _, cx| v.operate(Operation::Startup(false), cx)),
                            ),
                    ),
            )
    }

    fn confirmation(&self, op: Operation, cx: &Context<Self>) -> Card {
        let (label, info) = match op {
            Operation::Unmount(_, true) => (
                "強制解除",
                "使用中です。データと未保存の変更はすべて消えます。",
            ),
            Operation::Unmount(_, false) => ("解除", "ドライブ内のデータはすべて消えます。"),
            Operation::Delete(_) => ("削除", "選択したディスク設定を削除します。"),
            _ => unreachable!(),
        };
        Card::new()
            .variant(CardVariant::Filled)
            .child(Text::new(info))
            .child(
                wrap()
                    .child(button("not-confirmed", "キャンセル").on_click(cx.listener(
                        |v, _, _, cx| {
                            v.confirmation = None;
                            cx.notify();
                        },
                    )))
                    .child(
                        Button::new("confirm-op", label)
                            .on_click(cx.listener(move |v, _, _, cx| v.operate(op, cx))),
                    ),
            )
    }

    fn monitor_view(&self, cx: &Context<Self>) -> Div {
        let Some(monitor) = &self.monitor else {
            return column()
                .child(title("モニター"))
                .child(sub("監視履歴を開けません"));
        };
        let disks = monitor.history.disks().unwrap_or_default();
        let id = self
            .history_id
            .as_ref()
            .filter(|id| disks.iter().any(|(d, _)| d == *id))
            .or_else(|| disks.first().map(|(id, _)| id));
        let Some(id) = id else {
            return column()
                .gap(px(12.0))
                .child(title("モニター"))
                .child(sub("監視対象なし"));
        };
        let now = Utc::now().timestamp();
        let end = self.end.unwrap_or(now);
        let auto = INTERVALS
            .iter()
            .position(|(step, age, _)| self.span <= step * 600 && now - (end - self.span) <= *age)
            .unwrap_or(3);
        let (step, _, _) = INTERVALS[self.interval.checked_sub(1).unwrap_or(auto)];
        let span = self.span.max(step * 2);
        let start = end - span;
        let read = monitor.history.read(id, step, start, end);
        let (points, totals, first) = match read {
            Ok(read) => read,
            Err(error) => {
                return column()
                    .child(title("モニター"))
                    .child(sub(format!("履歴を開けません: {error}")));
            }
        };
        let scope = wrap().children(disks.iter().enumerate().map(|(idx, (disk_id, label))| {
            let disk_id = disk_id.clone();
            button(("monitor-disk", idx), label.clone())
                .variant(if &disk_id == id {
                    ButtonVariant::Filled
                } else {
                    ButtonVariant::Outlined
                })
                .on_click(cx.listener(move |v, _, _, cx| {
                    v.history_id = Some(disk_id.clone());
                    v.cursor = None;
                    cx.notify();
                }))
        }));
        let presets = [
            (120, "2分"),
            (900, "15分"),
            (3600, "1時間"),
            (86400, "1日"),
            (604800, "7日"),
            (31536000, "1年"),
        ];
        let range_buttons =
            wrap().children(presets.into_iter().enumerate().map(|(i, (span, label))| {
                button(("range", i), label)
                    .variant(if span == self.span {
                        ButtonVariant::Filled
                    } else {
                        ButtonVariant::Outlined
                    })
                    .on_click(cx.listener(move |v, _, _, cx| {
                        v.span = span;
                        v.preset_span = span;
                        v.end = None;
                        v.cursor = None;
                        cx.notify();
                    }))
            }));
        let intervals = wrap()
            .child(
                button("interval-auto", "自動")
                    .variant(if self.interval == 0 {
                        ButtonVariant::Filled
                    } else {
                        ButtonVariant::Outlined
                    })
                    .on_click(cx.listener(|v, _, _, cx| {
                        v.interval = 0;
                        v.cursor = None;
                        cx.notify();
                    })),
            )
            .children(INTERVALS.iter().enumerate().map(|(i, (_, _, label))| {
                button(("interval", i), *label)
                    .variant(if self.interval == i + 1 {
                        ButtonVariant::Filled
                    } else {
                        ButtonVariant::Outlined
                    })
                    .on_click(cx.listener(move |v, _, _, cx| {
                        v.interval = i + 1;
                        v.cursor = None;
                        cx.notify();
                    }))
            }));
        let summary = format!(
            "累計  読み取り {:.2} MiB / {} 回  ·  書き込み {:.2} MiB / {} 回",
            totals.rb as f64 / 1048576.0,
            totals.ro,
            totals.wb as f64 / 1048576.0,
            totals.wo
        );
        column()
            .gap(px(10.0))
            .child(
                row()
                    .justify_between()
                    .items_center()
                    .child(title("モニター"))
                    .child(sub(monitor.status())),
            )
            .child(wrap().child(scope).child(sub(summary)))
            .child(wrap().child(range_buttons).child(intervals))
            .child(
                wrap()
                    .child(
                        button(
                            "live",
                            if self.end.is_none() {
                                "✓ ライブ"
                            } else {
                                "ライブ"
                            },
                        )
                        .on_click(cx.listener(|v, _, _, cx| {
                            v.end = None;
                            cx.notify();
                        })),
                    )
                    .child(button("reset-chart", "リセット").on_click(cx.listener(
                        |v, _, _, cx| {
                            v.reset_chart();
                            cx.notify();
                        },
                    )))
                    .child(
                        button(
                            "compare",
                            if self.comparison {
                                "✓ 比較"
                            } else {
                                "比較"
                            },
                        )
                        .on_click(cx.listener(|v, _, _, cx| {
                            v.comparison = !v.comparison;
                            cx.notify();
                        })),
                    )
                    .child(
                        button(
                            "wide",
                            if self.wide {
                                "標準表示"
                            } else {
                                "広く表示"
                            },
                        )
                        .on_click(cx.listener(|v, _, _, cx| {
                            v.wide = !v.wide;
                            cx.notify();
                        })),
                    )
                    .children(["読み取り", "書き込み"].into_iter().enumerate().map(
                        |(idx, name)| {
                            button(
                                ("series", idx),
                                if self.series[idx] {
                                    format!("✓ {name}")
                                } else {
                                    name.into()
                                },
                            )
                            .on_click(cx.listener(
                                move |v, _, _, cx| {
                                    v.series[idx] = !v.series[idx];
                                    v.cursor = None;
                                    cx.notify();
                                },
                            ))
                        },
                    )),
            )
            .child(
                wrap()
                    .child(sub(format!("{} ～ {}", time_label(start), time_label(end))))
                    .when_some(first, |row, first| {
                        row.child(sub(format!("記録開始 {}", time_label(first))))
                    }),
            )
            .child(self.chart_panel(0, &points, start, end, step, cx))
            .when(self.comparison, |c| {
                c.child(
                    div()
                        .id("chart-divider")
                        .h(px(14.0))
                        .cursor(CursorStyle::ResizeUpDown)
                        .child(Divider::new())
                        .on_mouse_down(
                            MouseButton::Left,
                            cx.listener(|v, e: &MouseDownEvent, _, _| {
                                v.resize_origin = Some((e.position.y, v.chart_height));
                            }),
                        )
                        .on_mouse_up(
                            MouseButton::Left,
                            cx.listener(|v, e: &MouseUpEvent, _, cx| {
                                v.resize_charts(e.position.y);
                                cx.notify();
                            }),
                        )
                        .on_mouse_up_out(
                            MouseButton::Left,
                            cx.listener(|v, e: &MouseUpEvent, _, cx| {
                                v.resize_charts(e.position.y);
                                cx.notify();
                            }),
                        ),
                )
                .child(self.chart_panel(1, &points, start, end, step, cx))
            })
    }

    fn resize_charts(&mut self, y: Pixels) {
        if let Some((origin, height)) = self.resize_origin.take() {
            self.chart_height = (height + f32::from(y - origin)).clamp(120.0, 210.0);
        }
    }

    fn chart_panel(
        &self,
        index: usize,
        points: &[monitor::Point],
        start: i64,
        end: i64,
        step: i64,
        cx: &Context<Self>,
    ) -> Stateful<Div> {
        let metric = self.metric[index];
        let scale = self.scale[index];
        let unit = METRICS[metric].1;
        let baseline = match metric {
            0 | 1 | 5 => 1.0 / 1024.0,
            6 => 0.01,
            _ => 1.0,
        };
        let transform = |value: f64| {
            if scale == 1 {
                (1.0 + value / baseline).log10()
            } else {
                value
            }
        };
        let plot = |series: usize| {
            let mut last = None;
            let mut data = Vec::new();
            for p in points {
                if let Some(value) = p.values(metric)[series] {
                    if let Some(last) = last {
                        if p.time > last + step {
                            data.push(ChartPoint {
                                x: (last + step - start) as f64,
                                y: f64::NAN,
                                label: String::new(),
                            });
                        }
                    }
                    data.push(ChartPoint {
                        x: (p.time - start) as f64,
                        y: transform(value),
                        label: format!("{}  {:.3} {unit}", time_label(p.time), value),
                    });
                    last = Some(p.time);
                }
            }
            data
        };
        let mut series = Vec::new();
        let labels = if metric < 5 {
            ["読み取り", "書き込み"]
        } else {
            [METRICS[metric].0, ""]
        };
        for (i, label) in labels.into_iter().enumerate() {
            if !label.is_empty() && (metric >= 5 || self.series[i]) {
                series.push(ChartSeries {
                    name: label.into(),
                    points: plot(i),
                    color: None,
                });
            }
        }
        let selection = self.cursor.and_then(|time| {
            series.iter().enumerate().find_map(|(series, values)| {
                let x = (time - start) as f64;
                values
                    .points
                    .iter()
                    .position(|p| p.x == x && p.y.is_finite())
                    .map(|index| ChartSelection { series, index })
            })
        });
        let selected = self
            .cursor
            .and_then(|time| points.iter().find(|p| p.time == time))
            .map(|point| {
                let values = point.values(metric);
                let details = labels
                    .iter()
                    .zip(values)
                    .filter_map(|(&name, v)| v.map(|v| format!("{name} {v:.3} {unit}")))
                    .collect::<Vec<_>>()
                    .join(" · ");
                if details.is_empty() {
                    "—".into()
                } else {
                    format!("{}  {details}", time_label(point.time))
                }
            });
        let max = series
            .iter()
            .flat_map(|s| s.points.iter().map(|p| p.y))
            .fold(0.0_f64, f64::max);
        let min = series
            .iter()
            .flat_map(|s| s.points.iter().map(|p| p.y))
            .fold(max, f64::min);
        let vertical = self.y_range[index].unwrap_or(ChartRange {
            min: if scale == 2 { min } else { 0.0 },
            max: if scale == 0 && metric == 6 {
                100.0
            } else {
                max.max(if scale == 2 { min + 0.00001 } else { 0.00001 })
            },
        });
        let selection_times: Vec<Vec<_>> = series
            .iter()
            .map(|s| s.points.iter().map(|p| start + p.x as i64).collect())
            .collect();
        let data = ChartData {
            series,
            include_zero: scale != 2,
            x_range: Some(ChartRange {
                min: 0.0,
                max: (end - start) as f64,
            }),
            y_range: Some(vertical),
            ..Default::default()
        };
        let controls = wrap().children(METRICS.iter().enumerate().map(|(i, (name, _))| {
            button((if index == 0 { "metric-a" } else { "metric-b" }, i), *name)
                .variant(if i == metric {
                    ButtonVariant::Filled
                } else {
                    ButtonVariant::Outlined
                })
                .on_click(cx.listener(move |v, _, _, cx| {
                    v.metric[index] = i;
                    v.y_range[index] = None;
                    v.cursor = None;
                    cx.notify();
                }))
        }));
        let scales =
            wrap()
                .children(["線形", "対数", "変化を強調"].into_iter().enumerate().map(
                    |(i, label)| {
                        button((if index == 0 { "scale-a" } else { "scale-b" }, i), label)
                            .variant(if i == scale {
                                ButtonVariant::Filled
                            } else {
                                ButtonVariant::Outlined
                            })
                            .on_click(cx.listener(move |v, _, _, cx| {
                                v.scale[index] = i;
                                v.y_range[index] = None;
                                cx.notify();
                            }))
                    },
                ))
                .when(self.y_range[index].is_some(), |r| {
                    r.child(
                        button(("auto-y", index), "縦軸を自動").on_click(cx.listener(
                            move |v, _, _, cx| {
                                v.y_range[index] = None;
                                cx.notify();
                            },
                        )),
                    )
                });
        let height = if !self.comparison {
            330.0
        } else if index == 0 {
            self.chart_height
        } else {
            330.0 - self.chart_height
        };
        let chart = Chart::new(("history", index), data)
            .height(height)
            .show_markers(false)
            .axis_labels("表示開始からの秒", if scale == 1 { "対数" } else { unit })
            .selected(selection)
            .on_selection_change(
                cx.listener(move |v, value: &Option<ChartSelection>, _, cx| {
                    v.cursor = value
                        .and_then(|s| selection_times.get(s.series)?.get(s.index))
                        .copied();
                    cx.notify();
                }),
            );
        let bounds = Rc::new(Cell::new(Bounds::<Pixels>::default()));
        let measure = bounds.clone();
        let scroll_bounds = bounds.clone();
        let hover_bounds = bounds.clone();
        let chart_fraction = |bounds: Bounds<Pixels>, x: Pixels| {
            let width = (f32::from(bounds.size.width) - 48.0).max(1.0);
            ((f32::from(x - bounds.left()) - 40.0) / width).clamp(0.0, 1.0) as f64
        };
        let surface = div()
            .id(("chart-input", index))
            .relative()
            .child(chart)
            .child(
                canvas(move |bounds, _, _| measure.set(bounds), |_, _, _, _| {})
                    .absolute()
                    .inset_0()
                    .size_full(),
            )
            .on_mouse_move(cx.listener(move |v, e: &MouseMoveEvent, _, cx| {
                if v.drag.is_none() {
                    let time = start
                        + (chart_fraction(hover_bounds.get(), e.position.x) * (end - start) as f64)
                            as i64;
                    let time = Some(time.div_euclid(step) * step);
                    if v.cursor != time {
                        v.cursor = time;
                        cx.notify();
                    }
                }
            }))
            .on_scroll(cx.listener(move |v, e: &ScrollEvent, _, cx| {
                let sign = f32::from(e.delta.pixel_delta(px(22.0)).y).signum();
                if sign == 0.0 {
                    return;
                }
                let factor = if sign > 0.0 { 0.8 } else { 1.25 };
                if e.modifiers.control {
                    let midpoint = (vertical.min + vertical.max) * 0.5;
                    let half = ((vertical.max - vertical.min) * factor * 0.5).max(0.000001);
                    v.y_range[index] = Some(ChartRange {
                        min: midpoint - half,
                        max: midpoint + half,
                    });
                } else {
                    let shown = end - start;
                    let fraction = chart_fraction(scroll_bounds.get(), e.position.x);
                    let next = ((shown as f64 * factor).round() as i64).clamp(10, 63072000);
                    v.span = next;
                    let anchor_shift = ((next - shown) as f64 * (1.0 - fraction)).round() as i64;
                    v.end = Some((end + anchor_shift).min(Utc::now().timestamp()));
                }
                v.cursor = None;
                cx.stop_propagation();
                cx.notify();
            }))
            .on_mouse_down(
                MouseButton::Left,
                cx.listener(move |v, e: &MouseDownEvent, _, cx| {
                    if e.click_count >= 2 {
                        v.reset_chart();
                        cx.notify();
                    } else {
                        v.drag = Some((index, e.position, e.modifiers.control, e.modifiers.shift));
                    }
                }),
            )
            .on_mouse_up(
                MouseButton::Left,
                cx.listener(move |v, e: &MouseUpEvent, _, cx| {
                    let Some((i, origin, ctrl, shift)) = v.drag.take() else {
                        return;
                    };
                    if i != index {
                        return;
                    }
                    let dx = f32::from(e.position.x - origin.x) as f64;
                    let dy = f32::from(e.position.y - origin.y) as f64;
                    if dx.abs().max(dy.abs()) < 5.0 {
                        return;
                    }
                    if ctrl {
                        let delta = dy / (height as f64 - 32.0) * (vertical.max - vertical.min);
                        v.y_range[index] = Some(ChartRange {
                            min: vertical.min + delta,
                            max: vertical.max + delta,
                        });
                    } else {
                        let width = (f32::from(bounds.get().size.width) - 48.0).max(1.0) as f64;
                        let shown = end - start;
                        let delta = (dx / width * shown as f64) as i64;
                        if shift {
                            let left = chart_fraction(bounds.get(), e.position.x.min(origin.x));
                            let right = chart_fraction(bounds.get(), e.position.x.max(origin.x));
                            v.span = (((right - left) * shown as f64).round() as i64)
                                .clamp(10, 63072000);
                            v.end = Some(start + (right * shown as f64).round() as i64);
                        } else {
                            v.end = Some((end - delta).min(Utc::now().timestamp()));
                        }
                    }
                    v.cursor = None;
                    cx.notify();
                }),
            )
            .on_mouse_up_out(
                MouseButton::Left,
                cx.listener(move |v, _, _, _| {
                    if v.drag.is_some_and(|(i, ..)| i == index) {
                        v.drag = None;
                    }
                }),
            )
            .on_key_down(cx.listener(move |v, key: &KeyDownEvent, _, cx| {
                match key.keystroke.key.as_str() {
                    "home" => v.reset_chart(),
                    "escape" if v.drag.is_some() => v.drag = None,
                    "left" | "right" if !key.keystroke.modifiers.control => {
                        let direction = if key.keystroke.key == "left" { -1 } else { 1 };
                        v.end = Some(
                            (end + direction * ((end - start) / 8).max(1))
                                .min(Utc::now().timestamp()),
                        );
                        v.cursor = None;
                    }
                    "+" | "=" | "-" => {
                        let factor = if key.keystroke.key == "-" { 1.25 } else { 0.8 };
                        if key.keystroke.modifiers.control {
                            let middle = (vertical.min + vertical.max) / 2.0;
                            let half = ((vertical.max - vertical.min) * factor / 2.0).max(0.000001);
                            v.y_range[index] = Some(ChartRange {
                                min: middle - half,
                                max: middle + half,
                            });
                        } else {
                            let next = (((end - start) as f64 * factor).round() as i64)
                                .clamp(10, 63072000);
                            v.span = next;
                            v.end = Some(
                                (start + (end - start + next) / 2).min(Utc::now().timestamp()),
                            );
                        }
                    }
                    _ => return,
                }
                cx.stop_propagation();
                cx.notify();
            }));
        div().id(("chart-panel", index)).child(
            Card::new()
                .variant(CardVariant::Outlined)
                .child(controls)
                .child(scales)
                .child(surface)
                .child(sub(selected
                    .filter(|s| !s.is_empty())
                    .unwrap_or_else(|| "—".into()))),
        )
    }
}

fn startup_on_worker() -> Result<Option<PathBuf>, String> {
    std::thread::spawn(|| startup::status().map_err(|e| e.to_string()))
        .join()
        .unwrap_or_else(|_| Err("自動起動の状態を確認できません".into()))
}

pub fn theme(dir: &std::path::Path) -> Theme {
    let light = std::fs::read(dir.join("appearance.json"))
        .ok()
        .and_then(|b| serde_json::from_slice::<serde_json::Value>(&b).ok())
        .is_some_and(|data| data["Version"] == 1 && data["Theme"] == "Light");
    let mut theme = Theme::from_seed(Color::rgb(0x149790), 0.0);
    theme.mode = if light {
        ThemeMode::Light
    } else {
        ThemeMode::Dark
    };
    theme
}

impl Render for Studio {
    fn render(&mut self, window: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        if let Some(phase) = self.smoke_phase.take() {
            let weak = cx.entity().downgrade();
            window.on_next_frame(move |window, cx| {
                if phase == 2 {
                    cx.quit();
                    return;
                }
                let _ = weak.update(cx, |v, cx| {
                    v.smoke_phase = Some(phase + 1);
                    v.page = Page::Monitor;
                    if phase == 0 {
                        let mut next = Theme::get(cx);
                        next.mode = ThemeMode::Light;
                        next.set(cx);
                    } else {
                        v.page = Page::Disks;
                        v.open_editor(v.selected, cx);
                    }
                    cx.notify();
                });
                window.refresh();
            });
            window.refresh();
        }
        let theme = Theme::get(cx);
        let dark = theme.is_dark(window);
        let mut sidebar = column()
            .w(px(176.0))
            .flex_shrink_0()
            .p(px(14.0))
            .gap(px(12.0))
            .h_full()
            .bg(theme.color(ColorRole::SurfaceContainer, window))
            .child(
                row()
                    .gap(px(8.0))
                    .items_center()
                    .child(img("ramdisk.png").size(px(28.0)))
                    .child(Text::new("RAM Disk Studio").type_style(TypeStyle::TitleMedium)),
            );
        for (i, (page, label)) in [
            (Page::Disks, "ディスク"),
            (Page::Monitor, "モニター"),
            (Page::Startup, "自動起動"),
        ]
        .into_iter()
        .enumerate()
        {
            sidebar = sidebar.child(
                button(("nav", i), label)
                    .variant(if page == self.page {
                        ButtonVariant::Filled
                    } else {
                        ButtonVariant::Outlined
                    })
                    .disabled(self.busy || self.confirmation.is_some())
                    .on_click(cx.listener(move |v, _, _, cx| {
                        v.page = page;
                        v.editor = None;
                        v.help = false;
                        v.status.clear();
                        cx.notify();
                    })),
            );
        }
        sidebar = sidebar.child(div().flex_1())
            .child(button("help", "ヘルプ").on_click(cx.listener(|v, _, _, cx| { v.help = !v.help; cx.notify(); })))
            .child(button("theme", if dark { "ライトへ" } else { "ダークへ" }).on_click(cx.listener(move |v, _, _, cx| {
                let mut theme = Theme::get(cx);
                theme.mode = if dark { ThemeMode::Light } else { ThemeMode::Dark };
                theme.set(cx);
                let data = serde_json::json!({ "Version": 1, "Theme": if dark { "Light" } else { "Dark" } });
                if let Err(e) = disk::write_json(&v.dir.join("appearance.json"), &data, false) { v.status = e.to_string(); }
                cx.notify();
            })));
        let page = if self.editor.is_some() {
            self.editor_view(cx)
        } else {
            match self.page {
                Page::Disks => self.disks(cx),
                Page::Monitor => self.monitor_view(cx),
                Page::Startup => self.startup_view(cx),
            }
        };
        let content = column()
            .gap(px(12.0))
            .p(px(22.0))
            .when(self.help, |c| {
                c.child(
                    Card::new()
                        .variant(CardVariant::Filled)
                        .child(Text::new(
                            "ホイールで拡縮、ドラッグで移動。Ctrl で縦軸、Shift で範囲選択。",
                        ))
                        .child(sub(
                            "RAM ディスクのデータは解除・再起動・電源断で消えます。",
                        )),
                )
            })
            .when_some(self.confirmation, |c, operation| {
                c.child(self.confirmation(operation, cx))
            })
            .when(self.busy, |c| c.child(LinearProgress::new("working")))
            .when(!self.status.is_empty(), |c| {
                c.child(Text::new(self.status.clone()))
            })
            .child(page);
        row()
            .size_full()
            .bg(theme.color(ColorRole::Surface, window))
            .text_color(theme.color(ColorRole::OnSurface, window))
            .when(self.page != Page::Monitor || !self.wide, |r| {
                r.child(sidebar)
            })
            .child(
                div()
                    .id("content-scroll")
                    .h_full()
                    .flex_1()
                    .min_w(px(0.0))
                    .overflow_y_scroll()
                    .child(content),
            )
    }
}
