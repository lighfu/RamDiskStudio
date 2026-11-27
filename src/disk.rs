use anyhow::{Context, Result, bail, ensure};
use serde::{Deserialize, Serialize};
use std::{
    collections::HashSet,
    fs,
    io::Write,
    os::windows::{ffi::OsStrExt, process::CommandExt},
    path::{Path, PathBuf},
    process::{Command, Output},
    thread,
    time::Duration,
};
use uuid::Uuid;
use windows::{
    Win32::{
        Foundation::{CloseHandle, ERROR_FILE_NOT_FOUND, HANDLE},
        Security::{GetTokenInformation, TOKEN_ELEVATION, TOKEN_QUERY, TokenElevation},
        Storage::FileSystem::{
            GetDiskFreeSpaceExW, GetLogicalDrives, GetVolumeInformationW, QueryDosDeviceW,
            REPLACE_FILE_FLAGS, ReplaceFileW,
        },
        System::{
            SystemInformation::{GetSystemDirectoryW, GlobalMemoryStatusEx, MEMORYSTATUSEX},
            Threading::{CreateMutexW, GetCurrentProcess, OpenProcessToken},
        },
        UI::Shell::{
            FOLDERID_LocalAppData, FOLDERID_ProgramFiles, KF_FLAG_DEFAULT, SHGetKnownFolderPath,
        },
    },
    core::PCWSTR,
};
use winreg::{RegKey, enums::HKEY_LOCAL_MACHINE};

pub fn wide(value: impl AsRef<std::ffi::OsStr>) -> Vec<u16> {
    value.as_ref().encode_wide().chain([0]).collect()
}

pub fn known_folder(id: &windows::core::GUID) -> Result<PathBuf> {
    unsafe {
        let path = SHGetKnownFolderPath(id, KF_FLAG_DEFAULT, None)?;
        let result = path.to_string().map(PathBuf::from);
        windows::Win32::System::Com::CoTaskMemFree(Some(path.0.cast()));
        Ok(result?)
    }
}

pub fn data_dir() -> Result<PathBuf> {
    Ok(known_folder(&FOLDERID_LocalAppData)?.join("RamDiskStudio"))
}

pub fn program_files() -> Result<PathBuf> {
    known_folder(&FOLDERID_ProgramFiles)
}

pub fn system_dir() -> Result<PathBuf> {
    let mut value = [0u16; 32768];
    let size = unsafe { GetSystemDirectoryW(Some(&mut value)) } as usize;
    ensure!(size > 0 && size < value.len(), "System32 を確認できません");
    Ok(PathBuf::from(String::from_utf16(&value[..size])?))
}

pub fn elevated() -> bool {
    unsafe {
        let mut token = HANDLE::default();
        if OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &mut token).is_err() {
            return false;
        }
        let mut value = TOKEN_ELEVATION::default();
        let mut bytes = 0;
        let result = GetTokenInformation(
            token,
            TokenElevation,
            Some((&mut value as *mut TOKEN_ELEVATION).cast()),
            size_of::<TOKEN_ELEVATION>() as u32,
            &mut bytes,
        );
        let _ = CloseHandle(token);
        result.is_ok() && value.TokenIsElevated != 0
    }
}

pub struct Instance(HANDLE);
impl Instance {
    pub fn acquire() -> Result<Option<Self>> {
        use windows::Win32::Foundation::{ERROR_ALREADY_EXISTS, GetLastError};
        let name = wide(r"Local\RamDiskStudio.Manager");
        let handle = unsafe { CreateMutexW(None, false, PCWSTR(name.as_ptr()))? };
        if unsafe { GetLastError() } == ERROR_ALREADY_EXISTS {
            unsafe {
                let _ = CloseHandle(handle);
            }
            return Ok(None);
        }
        Ok(Some(Self(handle)))
    }
}
impl Drop for Instance {
    fn drop(&mut self) {
        unsafe {
            let _ = CloseHandle(self.0);
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub enum FileSystem {
    #[serde(rename = "NTFS")]
    Ntfs,
    #[serde(rename = "exFAT")]
    ExFat,
    #[serde(rename = "FAT32")]
    Fat32,
}
impl FileSystem {
    pub fn label(self) -> &'static str {
        match self {
            Self::Ntfs => "NTFS",
            Self::ExFat => "exFAT",
            Self::Fat32 => "FAT32",
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub enum MemoryMode {
    Virtual,
    Physical,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "PascalCase")]
pub struct Identity {
    pub device_path: String,
    pub volume_serial: u32,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "PascalCase")]
pub struct Profile {
    pub id: Uuid,
    pub name: String,
    pub drive_letter: char,
    #[serde(rename = "SizeMiB")]
    pub size_mib: u32,
    pub file_system: FileSystem,
    pub memory_mode: MemoryMode,
    pub volume_label: String,
    pub create_temp_folder: bool,
    pub mount_on_app_start: bool,
    pub identity: Option<Identity>,
}
impl Default for Profile {
    fn default() -> Self {
        Self {
            id: Uuid::new_v4(),
            name: "ワークスペース".into(),
            drive_letter: 'R',
            size_mib: 1024,
            file_system: FileSystem::Ntfs,
            memory_mode: MemoryMode::Virtual,
            volume_label: "RAMDISK".into(),
            create_temp_folder: true,
            mount_on_app_start: false,
            identity: None,
        }
    }
}
impl Profile {
    pub fn mount_point(&self) -> String {
        format!("{}:", self.drive_letter)
    }
    pub fn root(&self) -> String {
        format!("{}:\\", self.drive_letter)
    }
    pub fn validate(&self) -> Result<()> {
        ensure!(!self.id.is_nil(), "設定 ID が無効です");
        ensure!(
            !self.name.trim().is_empty() && self.name.encode_utf16().count() <= 60,
            "名前は1～60文字で入力してください"
        );
        ensure!(
            ('D'..='Z').contains(&self.drive_letter),
            "ドライブ文字は D～Z を指定してください"
        );
        ensure!(
            (64..=1_048_576).contains(&self.size_mib),
            "容量は64～1,048,576 MiBで指定してください"
        );
        ensure!(
            self.file_system != FileSystem::Fat32 || (256..=32768).contains(&self.size_mib),
            "FAT32 の容量は256～32,768 MiBです"
        );
        ensure!(
            (1..=11).contains(&self.volume_label.len())
                && self
                    .volume_label
                    .bytes()
                    .all(|b| b.is_ascii_alphanumeric() || b == b'-' || b == b'_'),
            "ボリューム名は半角英数字、-、_ の1～11文字です"
        );
        Ok(())
    }
    fn create_args(&self) -> Vec<String> {
        let mut args = vec!["-a".to_owned(), "-n".to_owned()];
        let options: &[&str] = if self.memory_mode == MemoryMode::Physical {
            &["-o", "awe,hd"]
        } else {
            &["-t", "vm", "-o", "hd"]
        };
        args.extend(options.iter().map(|s| s.to_string()));
        args.extend([
            "-s".into(),
            format!("{}M", self.size_mib),
            "-m".into(),
            self.mount_point(),
            "-p".into(),
            format!(
                "/fs:{} /q /y /v:{}",
                self.file_system.label(),
                self.volume_label
            ),
        ]);
        args
    }
}

#[derive(Serialize, Deserialize)]
#[serde(rename_all = "PascalCase")]
struct Document {
    version: u32,
    disks: Vec<Profile>,
}

fn validate_profiles(profiles: &[Profile]) -> Result<()> {
    let mut ids = HashSet::new();
    let mut letters = HashSet::new();
    for p in profiles {
        p.validate()?;
        ensure!(
            ids.insert(p.id) && letters.insert(p.drive_letter),
            "ID またはドライブ文字が重複しています"
        );
    }
    Ok(())
}

pub fn load_profiles(dir: &Path) -> Result<Vec<Profile>> {
    let bytes = match fs::read(dir.join("profiles.json")) {
        Ok(bytes) => bytes,
        Err(e) if e.kind() == std::io::ErrorKind::NotFound => return Ok(Vec::new()),
        Err(e) => return Err(e).context("設定を開けません"),
    };
    let document: Document =
        serde_json::from_slice(bytes.strip_prefix(&[0xef, 0xbb, 0xbf]).unwrap_or(&bytes))
            .context("設定を読み込めません")?;
    ensure!(document.version == 1, "未対応の設定バージョンです");
    validate_profiles(&document.disks)?;
    Ok(document.disks)
}

pub fn save_profiles(dir: &Path, profiles: &[Profile]) -> Result<()> {
    validate_profiles(profiles)?;
    write_json(
        &dir.join("profiles.json"),
        &Document {
            version: 1,
            disks: profiles.to_vec(),
        },
        true,
    )
}

pub fn write_json(path: &Path, data: &impl Serialize, backup: bool) -> Result<()> {
    let dir = path.parent().context("保存先がありません")?;
    fs::create_dir_all(dir)?;
    let mut file = tempfile::NamedTempFile::new_in(dir)?;
    serde_json::to_writer_pretty(&mut file, data)?;
    file.write_all(b"\n")?;
    file.as_file().sync_all()?;
    let file = file.into_temp_path();
    if backup && path.try_exists()? {
        let mut backup_path = path.as_os_str().to_owned();
        backup_path.push(".bak");
        let (path, source, backup_path) =
            (wide(path), wide(file.as_ref() as &Path), wide(backup_path));
        unsafe {
            ReplaceFileW(
                PCWSTR(path.as_ptr()),
                PCWSTR(source.as_ptr()),
                PCWSTR(backup_path.as_ptr()),
                REPLACE_FILE_FLAGS(0),
                None,
                None,
            )?;
        }
    } else {
        file.persist(path)?;
    }
    Ok(())
}

#[derive(Clone, Debug, Default)]
pub struct Drive {
    pub device: Option<String>,
    pub serial: Option<u32>,
    pub capacity: Option<(u64, u64)>,
}
impl Drive {
    pub fn exists(&self) -> bool {
        self.device.is_some()
    }
    pub fn owned(&self, profile: &Profile) -> bool {
        match (&self.device, self.serial, &profile.identity) {
            (Some(device), Some(serial), Some(identity)) => {
                is_imdisk(device)
                    && device.eq_ignore_ascii_case(&identity.device_path)
                    && serial == identity.volume_serial
            }
            _ => false,
        }
    }
}
fn is_imdisk(device: &str) -> bool {
    let prefix = r"\Device\ImDisk";
    device
        .get(..prefix.len())
        .is_some_and(|s| s.eq_ignore_ascii_case(prefix))
        && device
            .get(prefix.len()..)
            .is_some_and(|s| !s.is_empty() && s.bytes().all(|c| c.is_ascii_digit()))
}

pub fn inspect(letter: char) -> Result<Drive> {
    ensure!(letter.is_ascii_uppercase(), "ドライブ文字が無効です");
    let dos = wide(format!("{letter}:"));
    let root = wide(format!("{letter}:\\"));
    let mut buffer = vec![0u16; 32_768];
    let chars = unsafe { QueryDosDeviceW(PCWSTR(dos.as_ptr()), Some(&mut buffer)) };
    if chars == 0 {
        let error = unsafe { windows::Win32::Foundation::GetLastError() };
        if error == ERROR_FILE_NOT_FOUND {
            let drives = unsafe { GetLogicalDrives() };
            ensure!(drives != 0, "ドライブ一覧を取得できません");
            if drives & (1 << (letter as u8 - b'A')) != 0 {
                return Ok(Drive {
                    device: Some("occupied".into()),
                    ..Default::default()
                });
            }
            return Ok(Drive::default());
        }
        bail!("ドライブを確認できません ({})", error.0);
    }
    let end = buffer
        .iter()
        .position(|c| *c == 0)
        .unwrap_or(chars as usize);
    let device = String::from_utf16(&buffer[..end])?;
    let mut drive = Drive {
        device: Some(device.clone()),
        ..Default::default()
    };
    if is_imdisk(&device) {
        let mut serial = 0;
        let volume = unsafe {
            GetVolumeInformationW(
                PCWSTR(root.as_ptr()),
                None,
                Some(&mut serial),
                None,
                None,
                None,
            )
        };
        if volume.is_ok() {
            drive.serial = Some(serial);
            let (mut total, mut free) = (0, 0);
            if unsafe {
                GetDiskFreeSpaceExW(
                    PCWSTR(root.as_ptr()),
                    None,
                    Some(&mut total),
                    Some(&mut free),
                )
            }
            .is_ok()
            {
                drive.capacity = Some((total, free));
            }
        }
    }
    Ok(drive)
}

pub fn memory() -> Result<(u64, u64)> {
    let mut status = MEMORYSTATUSEX {
        dwLength: size_of::<MEMORYSTATUSEX>() as u32,
        ..Default::default()
    };
    unsafe {
        GlobalMemoryStatusEx(&mut status)?;
    }
    Ok((status.ullTotalPhys, status.ullAvailPhys))
}

pub fn driver_installed() -> bool {
    system_dir().is_ok_and(|dir| {
        dir.join("imdisk.exe").is_file() && dir.join("drivers/imdisk.sys").is_file()
    })
}
pub fn awe_installed() -> bool {
    system_dir().is_ok_and(|dir| dir.join("drivers/awealloc.sys").is_file())
        && RegKey::predef(HKEY_LOCAL_MACHINE)
            .open_subkey(r"SYSTEM\CurrentControlSet\Services\awealloc")
            .is_ok_and(|key| {
                key.get_value::<u32, _>("Type")
                    .is_ok_and(|value| value == 1)
                    && key
                        .get_value::<u32, _>("Start")
                        .is_ok_and(|start| start < 4)
            })
}

fn run_imdisk(arguments: impl IntoIterator<Item = impl AsRef<std::ffi::OsStr>>) -> Result<Output> {
    let dir = system_dir()?;
    Command::new(dir.join("imdisk.exe"))
        .current_dir(&dir)
        .args(arguments)
        .creation_flags(0x08000000)
        .stdin(std::process::Stdio::null())
        .output()
        .context("ImDisk を起動できません")
}
fn driver_required() -> Result<()> {
    ensure!(driver_installed(), "ImDisk が未導入です");
    ensure!(elevated(), "管理者権限が必要です");
    Ok(())
}
fn command_result(output: Output, label: &str) -> Result<()> {
    ensure!(
        output.status.success(),
        "{label}に失敗しました ({})\n{}",
        output.status,
        String::from_utf8_lossy(&output.stderr).trim()
    );
    Ok(())
}

pub fn save_edit(dir: &Path, profiles: &mut Vec<Profile>, mut profile: Profile) -> Result<()> {
    profile.validate()?;
    if let Some(existing) = profiles.iter().find(|p| p.id == profile.id) {
        ensure!(
            !inspect(existing.drive_letter)?.exists(),
            "解除してから編集してください"
        );
    }
    ensure!(
        !inspect(profile.drive_letter)?.exists()
            && !profiles
                .iter()
                .any(|p| p.id != profile.id && p.drive_letter == profile.drive_letter),
        "ドライブ文字は使用中です"
    );
    let mut next = profiles.clone();
    next.retain(|p| p.id != profile.id);
    profile.identity = None;
    next.push(profile);
    next.sort_by_key(|p| p.drive_letter);
    save_profiles(dir, &next)?;
    *profiles = next;
    Ok(())
}

pub fn delete_profile(dir: &Path, profiles: &mut Vec<Profile>, id: Uuid) -> Result<()> {
    let profile = profiles
        .iter()
        .find(|p| p.id == id)
        .context("設定がありません")?;
    ensure!(
        !inspect(profile.drive_letter)?.owned(profile),
        "解除してから削除してください"
    );
    let next: Vec<_> = profiles.iter().filter(|p| p.id != id).cloned().collect();
    save_profiles(dir, &next)?;
    *profiles = next;
    Ok(())
}

pub fn mount(dir: &Path, profiles: &mut [Profile], id: Uuid) -> Result<()> {
    let index = profiles
        .iter()
        .position(|p| p.id == id)
        .context("設定がありません")?;
    let profile = &profiles[index];
    profile.validate()?;
    driver_required()?;
    ensure!(
        !inspect(profile.drive_letter)?.exists(),
        "{} は使用中です",
        profile.mount_point()
    );
    let (total, available) = memory()?;
    ensure!(
        u64::from(profile.size_mib) * 1024 * 1024
            <= available.saturating_sub((512 * 1024 * 1024).max(total / 20)),
        "空きメモリが不足しています"
    );
    ensure!(
        profile.memory_mode != MemoryMode::Physical || awe_installed(),
        "awealloc が未導入です"
    );
    save_profiles(dir, profiles)?;
    let output = run_imdisk(profile.create_args())?;
    let device = String::from_utf8_lossy(&output.stdout)
        .lines()
        .map(str::trim)
        .find_map(|v| v.parse::<u32>().ok())
        .map(|unit| format!(r"\Device\ImDisk{unit}"));
    command_result(output, "作成")?;
    let current = inspect(profile.drive_letter)?;
    let path = current.device.context("作成したディスクを確認できません")?;
    let serial = current.serial.context("ボリュームを確認できません")?;
    ensure!(
        current.capacity.is_some()
            && is_imdisk(&path)
            && device.is_some_and(|s| path.eq_ignore_ascii_case(&s)),
        "作成したディスクを確認できません"
    );
    profiles[index].identity = Some(Identity {
        device_path: path,
        volume_serial: serial,
    });
    save_profiles(dir, profiles).context("ディスクは作成済みです。設定を保存できません")?;
    let profile = &profiles[index];
    if profile.create_temp_folder && inspect(profile.drive_letter)?.owned(profile) {
        fs::create_dir_all(Path::new(&profile.root()).join("Temp"))
            .context("Temp を作成できません")?;
    }
    Ok(())
}

pub fn unmount(dir: &Path, profiles: &mut [Profile], id: Uuid, force: bool) -> Result<bool> {
    let profile = profiles
        .iter_mut()
        .find(|p| p.id == id)
        .context("設定がありません")?;
    driver_required()?;
    for attempt in 0..4 {
        ensure!(
            inspect(profile.drive_letter)?.owned(profile),
            "このアプリが作成したディスクを確認できません"
        );
        let output = run_imdisk([
            if force { "-D" } else { "-d" },
            "-m",
            &profile.mount_point(),
        ])?;
        if !force && output.status.code() == Some(2) {
            if attempt == 3 {
                return Ok(false);
            }
            thread::sleep(Duration::from_millis(750));
            continue;
        }
        command_result(output, "解除")?;
        ensure!(
            !inspect(profile.drive_letter)?.owned(profile),
            "ディスクがまだ接続されています"
        );
        profile.identity = None;
        save_profiles(dir, profiles)?;
        return Ok(true);
    }
    unreachable!()
}

pub fn shell_open(target: impl AsRef<std::ffi::OsStr>, verb: &str, args: &str) -> Result<()> {
    use windows::Win32::{
        Foundation::HWND,
        UI::{Shell::ShellExecuteW, WindowsAndMessaging::SW_SHOWNORMAL},
    };
    let (target, verb, args) = (wide(target), wide(verb), wide(args));
    let result = unsafe {
        ShellExecuteW(
            Some(HWND::default()),
            PCWSTR(verb.as_ptr()),
            PCWSTR(target.as_ptr()),
            PCWSTR(args.as_ptr()),
            PCWSTR::null(),
            SW_SHOWNORMAL,
        )
    };
    ensure!(
        result.0 as usize > 32,
        "起動できません ({})",
        result.0 as usize
    );
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn reads_wpf_settings_and_backs_up_previous_values() -> Result<()> {
        let folder = tempfile::tempdir()?;
        let path = folder.path().join("profiles.json");
        let legacy = r#"{
          "Version": 1,
          "Disks": [{
            "Id": "237cde89-1619-47f0-9988-1f4d32aab1de", "Name": "作業",
            "DriveLetter": "R", "SizeMiB": 12288, "FileSystem": "exFAT",
            "MemoryMode": "Physical", "VolumeLabel": "WORK",
            "CreateTempFolder": true, "MountOnAppStart": true,
            "Identity": { "DevicePath": "\\Device\\ImDisk1", "VolumeSerial": 123 },
            "MountPoint": "R:", "RootPath": "R:\\", "SizeBytes": 12884901888
          }]
        }"#;
        fs::write(&path, legacy)?;
        let mut profiles = load_profiles(folder.path())?;
        let profile = &profiles[0];
        assert_eq!(profile.file_system, FileSystem::ExFat);
        assert_eq!(profile.size_mib, 12288);
        assert!(profile.mount_on_app_start);
        let drive = Drive {
            device: Some(r"\device\imdisk1".into()),
            serial: Some(123),
            capacity: None,
        };
        assert!(drive.owned(profile));
        assert!(
            !Drive {
                serial: Some(456),
                ..drive.clone()
            }
            .owned(profile)
        );
        assert!(
            !Drive {
                device: Some(r"\Device\ImDisk1-extra".into()),
                ..drive
            }
            .owned(profile)
        );
        assert_eq!(&profile.create_args()[..4], ["-a", "-n", "-o", "awe,hd"]);
        profiles[0].name = "更新".into();
        save_profiles(folder.path(), &profiles)?;
        assert_eq!(
            fs::read_to_string(folder.path().join("profiles.json.bak"))?,
            legacy
        );
        assert_eq!(load_profiles(folder.path())?[0].name, "更新");
        profiles[0].name = "再更新".into();
        save_profiles(folder.path(), &profiles)?;
        assert_eq!(load_profiles(folder.path())?[0].name, "再更新");
        Ok(())
    }

    #[test]
    fn rejects_labels_that_change_format_arguments() {
        let profile = Profile {
            volume_label: "X /y".into(),
            ..Default::default()
        };
        assert!(profile.validate().is_err());
    }
}
