#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

mod disk;
mod monitor;
mod startup;
mod ui;

use anyhow::{Result, anyhow};
use gpui::{App, AppContext, Bounds, TitlebarOptions, WindowBounds, WindowOptions, px, size};
use std::borrow::Cow;

struct Assets;
impl gpui::AssetSource for Assets {
    fn load(&self, path: &str) -> gpui::Result<Option<Cow<'static, [u8]>>> {
        Ok((path == "ramdisk.png")
            .then_some(Cow::Borrowed(include_bytes!("../assets/ramdisk.png"))))
    }

    fn list(&self, path: &str) -> gpui::Result<Vec<gpui::SharedString>> {
        Ok(if path.is_empty() {
            vec!["ramdisk.png".into()]
        } else {
            Vec::new()
        })
    }
}

fn main() {
    if let Err(error) = run() {
        eprintln!("{error:#}");
        let message = disk::wide(format!("{error:#}"));
        unsafe {
            use windows::{
                Win32::UI::WindowsAndMessaging::{MB_ICONERROR, MB_OK, MessageBoxW},
                core::{PCWSTR, w},
            };
            MessageBoxW(
                None,
                PCWSTR(message.as_ptr()),
                w!("RAM Disk Studio"),
                MB_OK | MB_ICONERROR,
            );
        }
        std::process::exit(1);
    }
}

fn run() -> Result<()> {
    let args: Vec<_> = std::env::args().skip(1).collect();
    if let [op, sid] = args.as_slice() {
        if matches!(op.as_str(), "--enable-startup" | "--disable-startup") {
            return startup::configure(op == "--enable-startup", sid);
        }
    }
    let smoke = args.iter().any(|arg| arg == "--smoke-test");
    let preview = cfg!(debug_assertions) && args.iter().any(|arg| arg == "--preview");
    let isolated = smoke || preview;
    let launch = !isolated && !disk::elevated() && !args.iter().any(|arg| arg == "--startup");
    if launch {
        let executable = std::env::current_exe()?;
        if startup::launch_if_installed(&executable).unwrap_or(false) {
            return Ok(());
        }
    }
    let _instance = if isolated {
        None
    } else {
        let mut instance = disk::Instance::acquire()?;
        if instance.is_none() && args.iter().any(|arg| arg == "--elevated") {
            for _ in 0..40 {
                std::thread::sleep(std::time::Duration::from_millis(250));
                instance = disk::Instance::acquire()?;
                if instance.is_some() {
                    break;
                }
            }
        }
        if instance.is_none() {
            if args.iter().any(|arg| arg == "--startup") {
                return Ok(());
            }
            return Err(anyhow!("RAM Disk Studio は起動済みです"));
        }
        instance
    };
    let temp = isolated.then(tempfile::tempdir).transpose()?;
    let dir = match &temp {
        Some(temp) => temp.path().to_path_buf(),
        None => disk::data_dir()?,
    };
    let show_monitor = args.iter().any(|arg| arg == "--monitor");
    m3e::application()
        .with_assets(Assets)
        .run(move |cx: &mut App| {
            m3e::init(cx);
            ui::theme(&dir).set(cx);
            cx.on_window_closed(|cx, _| {
                if cx.windows().is_empty() {
                    cx.quit();
                }
            })
            .detach();
            let result = cx.open_window(
                WindowOptions {
                    titlebar: Some(TitlebarOptions {
                        title: Some(
                            if preview {
                                "RAM Disk Studio プレビュー"
                            } else {
                                "RAM Disk Studio"
                            }
                            .into(),
                        ),
                        ..Default::default()
                    }),
                    window_bounds: Some(WindowBounds::Windowed(Bounds::centered(
                        None,
                        size(px(1120.0), px(830.0)),
                        cx,
                    ))),
                    focus: !smoke,
                    ..Default::default()
                },
                |window, cx| {
                    let view = cx.new(|cx| ui::Studio::new(dir, isolated, smoke, show_monitor, cx));
                    let weak = view.downgrade();
                    window.on_window_should_close(cx, move |_, cx| {
                        weak.upgrade().is_none_or(|view| view.read(cx).can_close())
                    });
                    view
                },
            );
            if let Err(error) = result {
                eprintln!("ウィンドウを開けません: {error:#}");
                std::process::exit(1);
            }
            if !smoke {
                cx.activate(true);
            }
        });
    Ok(())
}
