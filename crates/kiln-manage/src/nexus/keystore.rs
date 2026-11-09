//! OAuth credentials stay in the OS keyring, with an owner-readable file only when no backend exists.
//! Legacy personal-key entries are removed without using them for authentication.

use crate::{Error, ErrorCode, Result};
use super::oauth::Tokens;
use serde::{Deserialize, Serialize};
use std::path::{Path, PathBuf};

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum TokenPlace { Keyring, File }

pub trait SecretBackend: Send + Sync {
    fn get(&self) -> Result<Option<String>>;
    fn set(&self, secret: &str) -> Result<()>;
    fn clear(&self) -> Result<()>;
}

pub struct TokenStore {
    file: PathBuf,
    lock: PathBuf,
    keyring: Option<Box<dyn SecretBackend>>,
}
#[derive(Serialize, Deserialize)]
struct TokenFile { format: u32, tokens: Tokens }

impl TokenStore {
    pub fn new(root: &Path) -> Self {
        let off = std::env::var("KILN_NO_KEYRING").is_ok_and(|v| !v.is_empty() && v != "0");
        let keyring = if off { None } else { platform_keyring(false) };
        // The platform keyring slot is shared across data roots, so its lock must be shared too.
        let lock = if keyring.is_some() { crate::util::state_dir().join("nexus-oauth.lock") } else { root.join("oauth.lock") };
        Self { file: root.join("oauth.json"), lock, keyring }
    }
    pub fn with_backend(root: &Path, keyring: Option<Box<dyn SecretBackend>>) -> Self {
        Self { file: root.join("oauth.json"), lock: root.join("oauth.lock"), keyring }
    }
    pub fn file_path(&self) -> &Path { &self.file }
    pub(crate) fn lock(&self) -> Result<std::fs::File> {
        std::fs::create_dir_all(self.lock.parent().ok_or_else(|| Error::invalid("No OAuth lock folder."))?)?;
        let mut options = std::fs::OpenOptions::new();
        options.create(true).truncate(false).read(true).write(true);
        #[cfg(unix)] { use std::os::unix::fs::OpenOptionsExt; options.mode(0o600).custom_flags(libc::O_NOFOLLOW); }
        #[cfg(windows)] { use std::os::windows::fs::OpenOptionsExt; options.custom_flags(windows_sys::Win32::Storage::FileSystem::FILE_FLAG_OPEN_REPARSE_POINT); }
        let file = options.open(&self.lock).map_err(crate::util::io_at(&self.lock, "locking"))?;
        let metadata = file.metadata()?;
        if !metadata.is_file() { return Err(Error::new(ErrorCode::Refused, "The OAuth lock is not a regular file.")); }
        #[cfg(windows)] { use std::os::windows::fs::MetadataExt; if metadata.file_attributes() & 0x400 != 0 { return Err(Error::new(ErrorCode::Refused, "The OAuth lock must not be a reparse point.")); } }
        let deadline = std::time::Instant::now() + std::time::Duration::from_secs(10);
        loop {
            match file.try_lock() {
                Ok(()) => return Ok(file),
                Err(std::fs::TryLockError::WouldBlock) if std::time::Instant::now() < deadline => std::thread::sleep(std::time::Duration::from_millis(25)),
                Err(std::fs::TryLockError::WouldBlock) => return Err(Error::new(ErrorCode::Busy, "Another launcher is updating Nexus Mods authorization. Try again.")),
                Err(std::fs::TryLockError::Error(e)) => return Err(crate::util::io_at(&self.lock, "locking")(e)),
            }
        }
    }
    fn parse(secret: &str) -> Result<Tokens> {
        if secret.len() > 40 * 1024 { return Err(Error::new(ErrorCode::Parse, "Nexus OAuth credential storage is invalid.")); }
        let tokens: Tokens = serde_json::from_str(secret).map_err(|_| Error::new(ErrorCode::Parse, "Nexus OAuth credential storage is invalid."))?;
        tokens.validate()?;
        Ok(tokens)
    }
    pub fn get(&self) -> Result<Option<(Tokens, TokenPlace)>> {
        if let Some(k) = &self.keyring {
            if let Some(secret) = k.get()?.filter(|s| !s.is_empty()) {
                let tokens = Self::parse(&secret)?;
                self.remove_file()?;
                return Ok(Some((tokens, TokenPlace::Keyring)));
            }
        }
        let mut options = std::fs::OpenOptions::new();
        options.read(true);
        #[cfg(unix)] { use std::os::unix::fs::OpenOptionsExt; options.custom_flags(libc::O_NOFOLLOW | libc::O_NONBLOCK); }
        #[cfg(windows)] { use std::os::windows::fs::OpenOptionsExt; options.custom_flags(windows_sys::Win32::Storage::FileSystem::FILE_FLAG_OPEN_REPARSE_POINT); }
        let file = match options.open(&self.file) {
          Ok(file) => {
            let meta = file.metadata()?;
            if !meta.is_file() || meta.len() > 40 * 1024 {
                return Err(Error::new(ErrorCode::Refused, "Nexus OAuth credential storage must be a private regular file."));
            }
            #[cfg(windows)] { use std::os::windows::fs::MetadataExt; if meta.file_attributes() & 0x400 != 0 { return Err(Error::new(ErrorCode::Refused, "Nexus OAuth credential storage must not be a reparse point.")); } }
            #[cfg(unix)] {
                use std::os::unix::fs::PermissionsExt;
                if meta.permissions().mode() & 0o077 != 0 { return Err(Error::new(ErrorCode::Refused, "Nexus OAuth credential storage is not private.")); }
            }
            file
          }
          Err(e) if e.kind() == std::io::ErrorKind::NotFound => return Ok(None),
          Err(e) => return Err(crate::util::io_at(&self.file, "reading")(e)),
        };
        use std::io::Read;
        let mut text = Vec::new();
        file.take(40 * 1024 + 1).read_to_end(&mut text).map_err(crate::util::io_at(&self.file, "reading"))?;
        if text.len() > 40 * 1024 { return Err(Error::new(ErrorCode::Parse, "Nexus OAuth credential storage is invalid.")); }
        let file: TokenFile = serde_json::from_slice(&text)
            .map_err(|_| Error::new(ErrorCode::Parse, "Nexus OAuth credential storage is invalid."))?;
        if file.format != 1 { return Err(Error::new(ErrorCode::Parse, "Unsupported Nexus OAuth credential storage.")); }
        file.tokens.validate()?;
        if self.keyring.is_some() {
            self.set(&file.tokens)?;
            Ok(Some((file.tokens, TokenPlace::Keyring)))
        } else { Ok(Some((file.tokens, TokenPlace::File))) }
    }
    pub fn set(&self, tokens: &Tokens) -> Result<TokenPlace> {
        tokens.validate()?;
        if let Some(k) = &self.keyring {
            let secret = serde_json::to_string(tokens)?;
            k.set(&secret)?;
            if k.get()?.as_deref() != Some(secret.as_str()) {
                return Err(Error::new(ErrorCode::Io, "The keyring did not retain Nexus OAuth credentials."));
            }
            self.remove_file()?;
            return Ok(TokenPlace::Keyring);
        }
        write_private(&self.file, &TokenFile { format: 1, tokens: tokens.clone() })?;
        Ok(TokenPlace::File)
    }
    fn remove_file(&self) -> Result<()> { remove_file(&self.file) }
    /// Clear stored authorization even when damaged credentials prevent creating a Session.
    pub fn clear(&self) -> Result<()> {
        let _lock = self.lock()?;
        self.clear_locked()
    }
    pub(crate) fn clear_locked(&self) -> Result<()> {
        let ring = self.keyring.as_ref().map(|k| k.clear()).transpose();
        self.remove_file()?;
        ring.map(|_| ())
    }
}
fn remove_file(path: &Path) -> Result<()> {
    match std::fs::remove_file(path) {
        Ok(()) => Ok(()),
        Err(e) if e.kind() == std::io::ErrorKind::NotFound => Ok(()),
        Err(e) => Err(crate::util::io_at(path, "removing")(e)),
    }
}
/// Delete the retired credential slots; no personal key is loaded or sent to Nexus.
pub fn retire_legacy(root: &Path) -> Result<()> {
    remove_file(&root.join("apikey.json"))?;
    let off = std::env::var("KILN_NO_KEYRING").is_ok_and(|v| !v.is_empty() && v != "0");
    if !off { if let Some(backend) = platform_keyring(true) { backend.clear()?; } }
    Ok(())
}

/// Writes JSON readable by the user only (created 0600 on Unix, then renamed over the target).
pub fn write_private<T: Serialize>(path: &Path, value: &T) -> Result<()> {
    use std::io::Write;
    let dir = path.parent().ok_or_else(|| Error::invalid("no folder"))?;
    std::fs::create_dir_all(dir).map_err(crate::util::io_at(dir, "creating"))?;
    let tmp = dir.join(format!(".nexus-{}.tmp", crate::hash::hex(&crate::util::random_bytes::<16>()?)));
    let mut o = std::fs::OpenOptions::new();
    o.write(true).create_new(true);
    #[cfg(unix)]
    {
        use std::os::unix::fs::OpenOptionsExt;
        o.mode(0o600);
    }
    let mut f = o.open(&tmp).map_err(crate::util::io_at(&tmp, "writing"))?;
    let result = (|| {
        f.write_all(&serde_json::to_vec_pretty(value)?)?;
        f.sync_all()?;
        drop(f);
        crate::util::rename_replace(&tmp, path)
    })();
    if result.is_err() {
        let _ = std::fs::remove_file(&tmp);
    }
    result
}

#[cfg(not(any(target_os = "linux", windows)))]
fn platform_keyring(legacy: bool) -> Option<Box<dyn SecretBackend>> {
    let _ = legacy;
    None
}

// ---------------------------------------------------------------- Linux: secret-tool (libsecret)

#[cfg(target_os = "linux")]
fn platform_keyring(legacy: bool) -> Option<Box<dyn SecretBackend>> {
    crate::util::which("secret-tool").map(|exe| Box::new(SecretTool { exe, legacy }) as Box<dyn SecretBackend>)
}

#[cfg(target_os = "linux")]
pub struct SecretTool {
    exe: PathBuf,
    legacy: bool,
}

#[cfg(target_os = "linux")]
fn attributes(legacy: bool) -> [&'static str; 4] { ["application", "kiln-launcher", "service", if legacy { "nexusmods-api-key" } else { "nexusmods-oauth" }] }

#[cfg(target_os = "linux")]
impl SecretTool {
    /// Runs secret-tool with a 5 s limit (a keyring that waits for an unlock prompt nobody answers must not hang us).
    fn run(&self, args: &[&str], stdin: Option<&str>) -> Result<(bool, String)> {
        use std::io::Write;
        use std::process::{Command, Stdio};
        let mut child = Command::new(&self.exe)
            .args(args)
            .args(attributes(self.legacy))
            .stdin(if stdin.is_some() { Stdio::piped() } else { Stdio::null() })
            .stdout(Stdio::piped())
            .stderr(Stdio::null())
            .spawn()
            .map_err(|e| Error::new(ErrorCode::Unsupported, format!("secret-tool: {e}")))?;
        if let Some(s) = stdin
            && let Some(mut i) = child.stdin.take()
        {
            let _ = i.write_all(s.as_bytes());
        }
        let deadline = std::time::Instant::now() + std::time::Duration::from_secs(5);
        loop {
            if let Some(st) = child.try_wait()? {
                let mut out = String::new();
                if let Some(mut o) = child.stdout.take() {
                    use std::io::Read;
                    let _ = o.read_to_string(&mut out);
                }
                return Ok((st.success(), out));
            }
            if std::time::Instant::now() > deadline {
                let _ = child.kill();
                let _ = child.wait();
                return Err(Error::new(ErrorCode::Timeout, "the keyring did not answer"));
            }
            std::thread::sleep(std::time::Duration::from_millis(30));
        }
    }
}

#[cfg(target_os = "linux")]
impl SecretBackend for SecretTool {
    fn get(&self) -> Result<Option<String>> {
        let (ok, out) = self.run(&["lookup"], None)?;
        Ok(ok.then(|| out.trim().to_string()).filter(|s| !s.is_empty()))
    }
    fn set(&self, secret: &str) -> Result<()> {
        let (ok, _) = self.run(&["store", "--label=Kiln Launcher: Nexus Mods authorization"], Some(secret))?;
        if ok { Ok(()) } else { Err(Error::new(ErrorCode::Unsupported, "the keyring refused authorization")) }
    }
    fn clear(&self) -> Result<()> {
        let (ok, _) = self.run(&["clear"], None)?;
        if ok || self.get()?.is_none() { Ok(()) } else { Err(Error::new(ErrorCode::Io, "The keyring could not remove the Nexus Mods authorization.")) }
    }
}

// ---------------------------------------------------------------- Windows: Credential Manager

#[cfg(windows)]
fn platform_keyring(legacy: bool) -> Option<Box<dyn SecretBackend>> {
    Some(Box::new(WinCred { legacy }))
}

#[cfg(windows)]
pub struct WinCred { legacy: bool }

#[cfg(windows)]
fn target(legacy: bool) -> &'static str { if legacy { "Kiln Launcher/nexusmods-api-key" } else { "Kiln Launcher/nexusmods-oauth" } }

#[cfg(windows)]
fn wide(s: &str) -> Vec<u16> {
    s.encode_utf16().chain(std::iter::once(0)).collect()
}

#[cfg(windows)]
impl SecretBackend for WinCred {
    fn get(&self) -> Result<Option<String>> {
        use windows_sys::Win32::Security::Credentials::{CRED_TYPE_GENERIC, CREDENTIALW, CredFree, CredReadW};
        let target = wide(target(self.legacy));
        let mut p: *mut CREDENTIALW = std::ptr::null_mut();
        // SAFETY: target is NUL-terminated; on success p points to a CREDENTIALW the API allocated, freed below.
        unsafe {
            if CredReadW(target.as_ptr(), CRED_TYPE_GENERIC, 0, &mut p) == 0 {
                let code = windows_sys::Win32::Foundation::GetLastError();
                return if code == windows_sys::Win32::Foundation::ERROR_NOT_FOUND { Ok(None) } else { Err(Error::new(ErrorCode::Io, "Credential Manager could not read the Nexus Mods authorization.")) };
            }
            if p.is_null() {
                return Err(Error::new(ErrorCode::Io, "Credential Manager returned no credential."));
            }
            let c = &*p;
            let blob = if c.CredentialBlobSize == 0 { &[] } else { std::slice::from_raw_parts(c.CredentialBlob, c.CredentialBlobSize as usize) };
            let s = String::from_utf8_lossy(blob).into_owned();
            CredFree(p as *const core::ffi::c_void);
            Ok(Some(s))
        }
    }
    fn set(&self, secret: &str) -> Result<()> {
        use windows_sys::Win32::Security::Credentials::{CRED_PERSIST_LOCAL_MACHINE, CRED_TYPE_GENERIC, CREDENTIALW, CredWriteW};
        let target = wide(target(self.legacy));
        let user = wide("nexusmods");
        let mut blob = secret.as_bytes().to_vec();
        // SAFETY: every pointer in the struct outlives the call; CredWriteW copies what it keeps.
        let ok = unsafe {
            let mut c: CREDENTIALW = std::mem::zeroed();
            c.Type = CRED_TYPE_GENERIC;
            c.TargetName = target.as_ptr() as *mut u16;
            c.UserName = user.as_ptr() as *mut u16;
            c.CredentialBlobSize = blob.len() as u32;
            c.CredentialBlob = blob.as_mut_ptr();
            c.Persist = CRED_PERSIST_LOCAL_MACHINE;
            CredWriteW(&c, 0)
        };
        if ok != 0 { Ok(()) } else { Err(Error::new(ErrorCode::Unsupported, "Credential Manager refused authorization")) }
    }
    fn clear(&self) -> Result<()> {
        use windows_sys::Win32::Security::Credentials::{CRED_TYPE_GENERIC, CredDeleteW};
        let target = wide(target(self.legacy));
        // SAFETY: NUL-terminated target.
        let ok = unsafe { CredDeleteW(target.as_ptr(), CRED_TYPE_GENERIC, 0) };
        if ok != 0 || unsafe { windows_sys::Win32::Foundation::GetLastError() } == windows_sys::Win32::Foundation::ERROR_NOT_FOUND {
            Ok(())
        } else {
            Err(Error::new(ErrorCode::Io, "Credential Manager could not remove the Nexus Mods authorization."))
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::sync::{Arc, Mutex};
    #[derive(Default)]
    struct Ring { value: Mutex<Option<String>>, fail_get: bool, fail_set: bool, fail_clear: bool }
    impl SecretBackend for Arc<Ring> {
        fn get(&self) -> Result<Option<String>> { if self.fail_get { Err(Error::new(ErrorCode::Io, "locked")) } else { Ok(self.value.lock().unwrap().clone()) } }
        fn set(&self, secret: &str) -> Result<()> { if self.fail_set { return Err(Error::new(ErrorCode::Io, "locked")); } *self.value.lock().unwrap() = Some(secret.into()); Ok(()) }
        fn clear(&self) -> Result<()> { if self.fail_clear { return Err(Error::new(ErrorCode::Io, "locked")); } *self.value.lock().unwrap() = None; Ok(()) }
    }
    fn tokens() -> Tokens { Tokens { client_id: "kiln-test".into(), access_token: "access-secret".into(), refresh_token: "refresh-secret".into(), expires_at: 2000000000 } }
    fn temp() -> crate::library::TempDir { crate::library::TempDir::new(&std::env::temp_dir(), "nexus-tokenstore-test").unwrap() }
    #[test]
    fn present_but_failing_keyring_never_downgrades_to_file() {
        for ring in [Ring { fail_get: true, ..Default::default() }, Ring { fail_set: true, ..Default::default() }] {
            let tmp = temp(); let store = TokenStore::with_backend(tmp.path(), Some(Box::new(Arc::new(ring))));
            assert!(store.set(&tokens()).is_err());
            assert!(!store.file_path().exists());
        }
    }
    #[test]
    fn fallback_oauth_tokens_move_to_keyring_and_clear_failures_surface() {
        let tmp = temp(); let store = TokenStore::with_backend(tmp.path(), None);
        assert_eq!(store.set(&tokens()).unwrap(), TokenPlace::File);
        let ring = Arc::new(Ring::default()); let store = TokenStore::with_backend(tmp.path(), Some(Box::new(ring)));
        assert_eq!(store.get().unwrap(), Some((tokens(), TokenPlace::Keyring)));
        assert!(!store.file_path().exists());
        let store = TokenStore::with_backend(tmp.path(), Some(Box::new(Arc::new(Ring { fail_clear: true, ..Default::default() }))));
        assert!(store.clear().is_err());
    }
    #[test]
    fn legacy_personal_key_file_is_never_read_as_oauth_credentials() {
        let tmp = temp(); std::fs::write(tmp.path().join("apikey.json"), b"untrusted retired key bytes").unwrap();
        let store = TokenStore::with_backend(tmp.path(), None);
        assert!(store.get().unwrap().is_none());
    }
    #[test]
    fn malformed_storage_errors_do_not_echo_token_values() {
        let tmp = temp(); let store = TokenStore::with_backend(tmp.path(), None);
        write_private(store.file_path(), &serde_json::json!({"format":1,"tokens":{"client_id":"kiln","access_token":"secret-access","refresh_token":"secret-refresh","expires_at":"secret-refresh"}})).unwrap();
        let error = store.get().unwrap_err();
        assert!(!format!("{error:?}").contains("secret-"));
    }
    #[cfg(unix)]
    #[test]
    fn fallback_refuses_links_and_public_credentials_but_sign_out_can_remove_them() {
        use std::os::unix::{fs::symlink, fs::PermissionsExt};
        let tmp = temp(); let store = TokenStore::with_backend(tmp.path(), None);
        let target = tmp.path().join("private-target.json");
        write_private(&target, &TokenFile { format: 1, tokens: tokens() }).unwrap();
        symlink(&target, store.file_path()).unwrap();
        assert!(store.get().is_err());
        store.clear().unwrap();
        assert!(target.exists());
        assert!(!store.file_path().exists());
        store.set(&tokens()).unwrap();
        std::fs::set_permissions(store.file_path(), std::fs::Permissions::from_mode(0o644)).unwrap();
        assert!(store.get().is_err());
        store.clear().unwrap();
        assert!(!store.file_path().exists());
    }
    #[test]
    fn private_writes_preserve_other_writers_temporary_files() {
        let tmp = temp(); let path = tmp.path().join("state.json"); let old_tmp = tmp.path().join("state.json.tmp");
        std::fs::write(&old_tmp, b"another write").unwrap();
        std::thread::scope(|scope| { for n in 0..12 { let path = &path; scope.spawn(move || write_private(path, &vec![n; 100]).unwrap()); } });
        let data: Vec<u8> = serde_json::from_slice(&std::fs::read(&path).unwrap()).unwrap();
        assert_eq!(data.len(), 100); assert!(data.iter().all(|n| *n == data[0]));
        assert_eq!(std::fs::read(old_tmp).unwrap(), b"another write");
        #[cfg(unix)] { use std::os::unix::fs::PermissionsExt; assert_eq!(std::fs::metadata(path).unwrap().permissions().mode() & 0o777, 0o600); }
    }
}
