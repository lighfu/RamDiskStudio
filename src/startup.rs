use crate::disk::{self, wide};
use anyhow::{Context, Result, bail, ensure};
use std::{
    fs,
    os::windows::fs::MetadataExt,
    path::{Path, PathBuf},
};
use uuid::Uuid;
use windows::{
    Win32::{
        Foundation::{CloseHandle, HANDLE, HLOCAL, LocalFree},
        Security::{
            self, Authorization::*, DACL_SECURITY_INFORMATION, OWNER_SECURITY_INFORMATION,
            PROTECTED_DACL_SECURITY_INFORMATION, PSECURITY_DESCRIPTOR, TOKEN_QUERY, TOKEN_USER,
            TokenUser,
        },
        System::{
            Com::{
                CLSCTX_INPROC_SERVER, COINIT_MULTITHREADED, CoCreateInstance, CoInitializeEx,
                CoUninitialize, IPersistFile,
            },
            TaskScheduler::{
                IRegisteredTask, ITaskFolder, ITaskService, TASK_LOGON_INTERACTIVE_TOKEN,
                TaskScheduler,
            },
            Threading::{GetCurrentProcess, OpenProcessToken},
            Variant::{VARIANT, VT_BSTR, VariantClear},
        },
        UI::Shell::{FOLDERID_Programs, IShellLinkW, ShellLink},
    },
    core::{BSTR, PCWSTR, PWSTR},
};

const EXE: &str = "RamDiskStudio.exe";
const ARGS: &str = "--startup --elevated";
const DIR_ACL: &str = "O:BAG:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;FRFX;;;BU)";
const FILE_ACL: &str = "O:BAG:BAD:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;FRFX;;;BU)";

struct Com;
struct StringVariant(VARIANT);
impl StringVariant {
    fn new(value: BSTR) -> Self {
        let mut variant = VARIANT::default();
        // The active union field owns the BSTR until VariantClear releases it.
        unsafe {
            let inner = &mut *variant.Anonymous.Anonymous;
            inner.vt = VT_BSTR;
            inner.Anonymous.bstrVal = std::mem::ManuallyDrop::new(value);
        }
        Self(variant)
    }
}
impl Drop for StringVariant {
    fn drop(&mut self) {
        unsafe {
            let _ = VariantClear(&mut self.0);
        }
    }
}
impl Com {
    fn init() -> Result<Self> {
        unsafe {
            CoInitializeEx(None, COINIT_MULTITHREADED).ok()?;
        }
        Ok(Self)
    }
}
impl Drop for Com {
    fn drop(&mut self) {
        unsafe { CoUninitialize() }
    }
}
struct LocalBuffer(*mut std::ffi::c_void);
impl Drop for LocalBuffer {
    fn drop(&mut self) {
        unsafe {
            let _ = LocalFree(Some(HLOCAL(self.0)));
        }
    }
}

pub fn user_sid() -> Result<String> {
    struct Token(HANDLE);
    impl Drop for Token {
        fn drop(&mut self) {
            unsafe {
                let _ = CloseHandle(self.0);
            }
        }
    }
    unsafe {
        let mut token = HANDLE::default();
        OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &mut token)?;
        let token = Token(token);
        let mut size = 0;
        let _ = Security::GetTokenInformation(token.0, TokenUser, None, 0, &mut size);
        ensure!(
            size as usize >= size_of::<TOKEN_USER>(),
            "アカウントを確認できません"
        );
        let mut buffer = vec![0usize; (size as usize).div_ceil(size_of::<usize>())];
        Security::GetTokenInformation(
            token.0,
            TokenUser,
            Some(buffer.as_mut_ptr().cast()),
            size,
            &mut size,
        )?;
        let user = &*buffer.as_ptr().cast::<TOKEN_USER>();
        let mut sid = PWSTR::null();
        ConvertSidToStringSidW(user.User.Sid, &mut sid)?;
        let _allocation = LocalBuffer(sid.0.cast());
        Ok(sid.to_string()?)
    }
}

fn user_matches(value: &str, sid: &str) -> bool {
    if value.eq_ignore_ascii_case(sid) {
        return true;
    }
    let value = wide(value);
    let (mut sid_size, mut domain_size, mut usage) = (0, 0, Security::SID_NAME_USE::default());
    unsafe {
        let _ = Security::LookupAccountNameW(
            PCWSTR::null(),
            PCWSTR(value.as_ptr()),
            None,
            &mut sid_size,
            None,
            &mut domain_size,
            &mut usage,
        );
        if sid_size == 0 {
            return false;
        }
        let mut sid_buffer = vec![0usize; (sid_size as usize).div_ceil(size_of::<usize>())];
        let mut domain = vec![0u16; domain_size as usize];
        let sid_pointer = Security::PSID(sid_buffer.as_mut_ptr().cast());
        if Security::LookupAccountNameW(
            PCWSTR::null(),
            PCWSTR(value.as_ptr()),
            Some(sid_pointer),
            &mut sid_size,
            Some(PWSTR(domain.as_mut_ptr())),
            &mut domain_size,
            &mut usage,
        )
        .is_err()
        {
            return false;
        }
        let mut result = PWSTR::null();
        if ConvertSidToStringSidW(sid_pointer, &mut result).is_err() {
            return false;
        }
        let _allocation = LocalBuffer(result.0.cast());
        result
            .to_string()
            .is_ok_and(|s| s.eq_ignore_ascii_case(sid))
    }
}

fn root_path() -> Result<PathBuf> {
    Ok(disk::program_files()?.join("RAM Disk Studio"))
}
fn task_name(sid: &str) -> BSTR {
    BSTR::from(format!("RamDiskStudio-{sid}"))
}

fn folder() -> Result<ITaskFolder> {
    unsafe {
        let service: ITaskService = CoCreateInstance(&TaskScheduler, None, CLSCTX_INPROC_SERVER)?;
        let empty = VARIANT::default();
        service.Connect(&empty, &empty, &empty, &empty)?;
        Ok(service.GetFolder(&BSTR::from("\\"))?)
    }
}
fn task(folder: &ITaskFolder, sid: &str) -> Result<Option<IRegisteredTask>> {
    match unsafe { folder.GetTask(&task_name(sid)) } {
        Ok(task) => Ok(Some(task)),
        Err(e) if matches!(e.code().0 as u32, 0x80070002 | 0x8004130F) => Ok(None),
        Err(e) => Err(e.into()),
    }
}

fn escape_xml(path: &Path) -> Result<String> {
    Ok(path
        .to_str()
        .context("実行ファイル名を読み取れません")?
        .replace('&', "&amp;")
        .replace('<', "&lt;")
        .replace('>', "&gt;"))
}
fn task_xml(sid: &str, exe: &Path) -> Result<String> {
    let dir = escape_xml(exe.parent().context("配置先がありません")?)?;
    let exe = escape_xml(exe)?;
    Ok(format!(
        r#"<Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
<RegistrationInfo><Description>RAM Disk Studio の自動起動</Description></RegistrationInfo>
<Triggers><LogonTrigger><Enabled>true</Enabled><UserId>{sid}</UserId><Delay>PT3S</Delay></LogonTrigger></Triggers>
<Principals><Principal id="Author"><UserId>{sid}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>
<Settings><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy><DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
<StopIfGoingOnBatteries>false</StopIfGoingOnBatteries><AllowHardTerminate>false</AllowHardTerminate><StartWhenAvailable>true</StartWhenAvailable>
<AllowStartOnDemand>true</AllowStartOnDemand><Enabled>true</Enabled><ExecutionTimeLimit>PT0S</ExecutionTimeLimit></Settings>
<Actions Context="Author"><Exec><Command>{exe}</Command><Arguments>{ARGS}</Arguments><WorkingDirectory>{dir}</WorkingDirectory></Exec></Actions></Task>"#
    ))
}

fn child<'a, 'input>(
    node: roxmltree::Node<'a, 'input>,
    name: &str,
) -> Option<roxmltree::Node<'a, 'input>> {
    node.children().find(|node| {
        node.has_tag_name((
            "http://schemas.microsoft.com/windows/2004/02/mit/task",
            name,
        ))
    })
}
fn value<'a>(node: roxmltree::Node<'a, '_>, name: &str) -> &'a str {
    child(node, name).and_then(|n| n.text()).unwrap_or_default()
}
fn only_child<'a, 'input>(
    node: roxmltree::Node<'a, 'input>,
    group: &str,
    kind: &str,
) -> Result<roxmltree::Node<'a, 'input>> {
    let group = child(node, group).context("自動起動タスクが無効です")?;
    let mut elements = group.children().filter(roxmltree::Node::is_element);
    let first = elements.next().context("自動起動タスクが無効です")?;
    ensure!(
        first.tag_name().name() == kind && elements.next().is_none(),
        "自動起動タスクが無効です"
    );
    Ok(first)
}
fn protected_path(path: &Path, dir: bool) -> Result<()> {
    let metadata = fs::symlink_metadata(path)?;
    ensure!(
        metadata.is_dir() == dir && (metadata.file_attributes() & 0x400) == 0,
        "アプリの配置先を確認できません"
    );
    let path_wide = wide(path);
    unsafe {
        let mut owner = Security::PSID::default();
        let mut acl = std::ptr::null_mut();
        let mut descriptor = PSECURITY_DESCRIPTOR::default();
        GetNamedSecurityInfoW(
            PCWSTR(path_wide.as_ptr()),
            SE_FILE_OBJECT,
            OWNER_SECURITY_INFORMATION | DACL_SECURITY_INFORMATION,
            Some(&mut owner),
            None,
            Some(&mut acl),
            None,
            &mut descriptor,
        )
        .ok()?;
        let _allocation = LocalBuffer(descriptor.0);
        ensure!(
            !owner.0.is_null() && !acl.is_null(),
            "アプリのアクセス権を確認できません"
        );
        ensure!(
            Security::IsWellKnownSid(owner, Security::WinBuiltinAdministratorsSid).as_bool()
                || Security::IsWellKnownSid(owner, Security::WinLocalSystemSid).as_bool(),
            "アプリの所有者を確認できません"
        );
        for index in 0..u32::from((*acl).AceCount) {
            let mut ace = std::ptr::null_mut();
            Security::GetAce(acl, index, &mut ace)?;
            let header = &*ace.cast::<Security::ACE_HEADER>();
            // Only the ordinary allow/deny ACE types used by the installer are accepted.
            ensure!(header.AceType <= 1, "アプリのアクセス権を確認できません");
            if header.AceType == 0 {
                let ace = &*ace.cast::<Security::ACCESS_ALLOWED_ACE>();
                let trustee = Security::PSID((&ace.SidStart as *const u32).cast_mut().cast());
                const WRITES: u32 = 0x500D0156;
                ensure!(
                    ace.Mask & WRITES == 0
                        || Security::IsWellKnownSid(trustee, Security::WinBuiltinAdministratorsSid)
                            .as_bool()
                        || Security::IsWellKnownSid(trustee, Security::WinLocalSystemSid).as_bool(),
                    "アプリの配置先に書き込み権限があります"
                );
            }
        }
    }
    Ok(())
}
fn secure(path: &Path, dir: bool) -> Result<()> {
    let path_wide = wide(path);
    let sddl = wide(if dir { DIR_ACL } else { FILE_ACL });
    unsafe {
        let mut descriptor = PSECURITY_DESCRIPTOR::default();
        ConvertStringSecurityDescriptorToSecurityDescriptorW(
            PCWSTR(sddl.as_ptr()),
            SDDL_REVISION_1,
            &mut descriptor,
            None,
        )?;
        let _allocation = LocalBuffer(descriptor.0);
        Security::SetFileSecurityW(
            PCWSTR(path_wide.as_ptr()),
            OWNER_SECURITY_INFORMATION
                | DACL_SECURITY_INFORMATION
                | PROTECTED_DACL_SECURITY_INFORMATION,
            descriptor,
        )
        .ok()?;
    }
    protected_path(path, dir)
}

fn trusted(task: &IRegisteredTask, sid: &str) -> Result<PathBuf> {
    let xml = unsafe { task.Xml()? };
    let (exe, root) = trusted_xml(&xml.to_string(), sid)?;
    protected_path(&root, true)?;
    protected_path(
        exe.parent().context("アプリの配置先を確認できません")?,
        true,
    )?;
    protected_path(&exe, false)?;
    Ok(exe)
}

fn trusted_xml(xml: &str, sid: &str) -> Result<(PathBuf, PathBuf)> {
    let doc = roxmltree::Document::parse(xml)?;
    let root = doc.root_element();
    let principal = only_child(root, "Principals", "Principal")?;
    let trigger = only_child(root, "Triggers", "LogonTrigger")?;
    let action = only_child(root, "Actions", "Exec")?;
    let enabled = |value: &str| matches!(value, "" | "true" | "1");
    ensure!(
        user_matches(value(principal, "UserId"), sid)
            && user_matches(value(trigger, "UserId"), sid)
            && value(principal, "LogonType") == "InteractiveToken"
            && value(principal, "RunLevel") == "HighestAvailable"
            && enabled(value(trigger, "Enabled"))
            && value(action, "Arguments") == ARGS,
        "自動起動を設定し直してください"
    );
    let exe = PathBuf::from(value(action, "Command"));
    let dir = exe.parent().context("アプリの配置先を確認できません")?;
    let root = root_path()?;
    ensure!(
        exe.file_name().is_some_and(|f| f.eq_ignore_ascii_case(EXE))
            && dir
                .parent()
                .is_some_and(|p| p.as_os_str().eq_ignore_ascii_case(root.as_os_str()))
            && dir.file_name().is_some_and(|f| f != "." && f != "..")
            && dir
                .as_os_str()
                .eq_ignore_ascii_case(value(action, "WorkingDirectory")),
        "アプリの配置先を確認できません"
    );
    Ok((exe, root))
}

pub fn status() -> Result<Option<PathBuf>> {
    let _com = Com::init()?;
    let sid = user_sid()?;
    let Some(task) = task(&folder()?, &sid)? else {
        return Ok(None);
    };
    let enabled = unsafe { task.Enabled()? };
    if !enabled.as_bool() {
        return Ok(None);
    }
    Ok(Some(trusted(&task, &sid)?))
}

pub fn launch_if_installed(executable: &Path) -> Result<bool> {
    let _com = Com::init()?;
    let sid = user_sid()?;
    let Some(task) = task(&folder()?, &sid)? else {
        return Ok(false);
    };
    let enabled = unsafe { task.Enabled()? };
    if !enabled.as_bool() {
        return Ok(false);
    }
    if !trusted(&task, &sid)?
        .as_os_str()
        .eq_ignore_ascii_case(executable.as_os_str())
    {
        return Ok(false);
    }
    unsafe {
        task.Run(&VARIANT::default())?;
    }
    Ok(true)
}

pub fn configure(enable: bool, requester: &str) -> Result<()> {
    ensure!(disk::elevated(), "管理者権限が必要です");
    let sid = user_sid()?;
    ensure!(sid == requester, "同じユーザーで承認してください");
    let _com = Com::init()?;
    let folder = folder()?;
    if !enable {
        if task(&folder, &sid)?.is_some() {
            unsafe {
                folder.DeleteTask(&task_name(&sid), 0)?;
            }
        }
        return Ok(());
    }
    let root = root_path()?;
    if !root.exists() {
        fs::create_dir(&root)?;
        secure(&root, true)?;
    } else {
        protected_path(&root, true)?;
    }

    let directory = root.join(format!(
        "{}-{}",
        env!("CARGO_PKG_VERSION"),
        Uuid::new_v4().simple()
    ));
    fs::create_dir(&directory)?;
    secure(&directory, true)?;
    let target = directory.join(EXE);
    fs::copy(std::env::current_exe()?, &target)?;
    secure(&target, false)?;
    let xml = task_xml(&sid, &target)?;
    let user = StringVariant::new(BSTR::from(sid.as_str()));
    let sddl = StringVariant::new(BSTR::from(format!(
        "O:BAG:BAD:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GRGX;;;{sid})"
    )));
    let registered = unsafe {
        folder.RegisterTask(
            &task_name(&sid),
            &BSTR::from(xml),
            6 | 16,
            &user.0,
            &VARIANT::default(),
            TASK_LOGON_INTERACTIVE_TOKEN,
            &sddl.0,
        )?
    };
    ensure!(
        trusted(&registered, &sid)? == target,
        "自動起動を確認できません"
    );
    let link = disk::known_folder(&FOLDERID_Programs)?.join("RAM Disk Studio.lnk");
    let (target_wide, directory_wide, link_wide) = (wide(&target), wide(&directory), wide(link));
    unsafe {
        use windows::core::Interface;
        let shortcut: IShellLinkW = CoCreateInstance(&ShellLink, None, CLSCTX_INPROC_SERVER)?;
        shortcut.SetPath(PCWSTR(target_wide.as_ptr()))?;
        shortcut.SetWorkingDirectory(PCWSTR(directory_wide.as_ptr()))?;
        shortcut.SetIconLocation(PCWSTR(target_wide.as_ptr()), 0)?;
        shortcut
            .cast::<IPersistFile>()?
            .Save(PCWSTR(link_wide.as_ptr()), true)?;
    }
    Ok(())
}

pub fn configure_interactive(enable: bool) -> Result<()> {
    let sid = user_sid()?;
    if disk::elevated() {
        return configure(enable, &sid);
    }
    use windows::Win32::{
        Foundation::{HWND, WAIT_OBJECT_0},
        System::Threading::{GetExitCodeProcess, INFINITE, WaitForSingleObject},
        UI::{
            Shell::{SEE_MASK_NOCLOSEPROCESS, SHELLEXECUTEINFOW, ShellExecuteExW},
            WindowsAndMessaging::SW_HIDE,
        },
    };
    let arguments = wide(format!(
        "{} {sid}",
        if enable {
            "--enable-startup"
        } else {
            "--disable-startup"
        }
    ));
    let executable = wide(std::env::current_exe()?);
    let mut info = SHELLEXECUTEINFOW {
        cbSize: size_of::<SHELLEXECUTEINFOW>() as u32,
        fMask: SEE_MASK_NOCLOSEPROCESS,
        hwnd: HWND::default(),
        lpVerb: windows::core::w!("runas"),
        lpFile: PCWSTR(executable.as_ptr()),
        lpParameters: PCWSTR(arguments.as_ptr()),
        nShow: SW_HIDE.0,
        ..Default::default()
    };
    unsafe {
        ShellExecuteExW(&mut info)?;
        let waited = WaitForSingleObject(info.hProcess, INFINITE);
        let mut code = 0;
        let result = GetExitCodeProcess(info.hProcess, &mut code);
        let _ = CloseHandle(info.hProcess);
        if waited != WAIT_OBJECT_0 || result.is_err() || code != 0 {
            bail!("自動起動を設定できませんでした");
        }
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn validates_task_xml_without_registering() -> Result<()> {
        let _com = Com::init()?;
        let sid = user_sid()?;
        let target = root_path()?.join("test-version").join(EXE);
        let xml = task_xml(&sid, &target)?;
        let empty = VARIANT::default();
        let user = StringVariant::new(BSTR::from(sid.as_str()));
        assert_eq!(trusted_xml(&xml, &sid)?.0, target);
        assert!(trusted_xml(&xml.replace(ARGS, "--other"), &sid).is_err());
        assert!(trusted_xml(&xml.replace("HighestAvailable", "LeastPrivilege"), &sid).is_err());
        assert!(trusted_xml(&xml.replace("<Enabled>true</Enabled>", ""), &sid).is_ok());
        unsafe {
            // TASK_VALIDATE_ONLY does not create or update the task.
            let validation = folder()?.RegisterTask(
                &BSTR::from("RamDiskStudio-ValidateOnly"),
                &BSTR::from(xml),
                1,
                &user.0,
                &empty,
                TASK_LOGON_INTERACTIVE_TOKEN,
                &empty,
            );
            // A successful validation returns a null IRegisteredTask. The
            // generated COM wrapper represents that as an error with S_OK.
            if let Err(error) = validation {
                if error.code().is_err() {
                    return Err(error.into());
                }
            }
        }
        Ok(())
    }
}
