//! Where the user's Nexus Mods API key lives: the OS keyring when there is one (Linux: the Secret Service through
//! `secret-tool`, which GNOME Keyring and KWallet provide; Windows: Credential Manager), else
//! `<data>/nexus/apikey.json`, readable by the user only (0600 on Unix). Never in settings.json: that file goes to the
//! UI through `app_settings_get`. The key is never logged.
//!
//! `KILN_NO_KEYRING=1` explicitly skips the keyring (tests or a host without a usable keyring).

use crate::{Error, ErrorCode, Result};
use serde::{Deserialize, Serialize};
use std::path::{Path, PathBuf};

/// Where the key is kept.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum KeyPlace {
    /// The OS keyring.
    Keyring,
    /// `<data>/nexus/apikey.json` (0600).
    File,
}

/// An OS keyring.
pub trait SecretBackend: Send + Sync {
    fn get(&self) -> Result<Option<String>>;
    fn set(&self, secret: &str) -> Result<()>;
    fn clear(&self) -> Result<()>;
}

pub struct KeyStore {
    file: PathBuf,
    keyring: Option<Box<dyn SecretBackend>>,
}

#[derive(Serialize, Deserialize)]
struct KeyFile {
    format: u32,
    key: String,
}

impl KeyStore {
    /// The platform's keyring (unless `KILN_NO_KEYRING`), with the file below `root` as the fallback.
    pub fn new(root: &Path) -> KeyStore {
        let off = std::env::var("KILN_NO_KEYRING").is_ok_and(|v| !v.is_empty() && v != "0");
        KeyStore { file: root.join("apikey.json"), keyring: if off { None } else { platform_keyring() } }
    }

    pub fn with_backend(root: &Path, keyring: Option<Box<dyn SecretBackend>>) -> KeyStore {
        KeyStore { file: root.join("apikey.json"), keyring }
    }

    pub fn file_path(&self) -> &Path {
        &self.file
    }

    /// Reads the keyring, migrating an old fallback file when a backend becomes available.
    /// Backend failures never cause a silent downgrade to plaintext.
    pub fn get(&self) -> Result<Option<(String, KeyPlace)>> {
        if let Some(k) = &self.keyring {
            if let Some(s) = k.get()?.filter(|s| !s.trim().is_empty()) {
                self.remove_file()?;
                return Ok(Some((s.trim().to_string(), KeyPlace::Keyring)));
            }
            if let Some(f) = crate::util::read_json::<KeyFile>(&self.file)? {
                let key = f.key.trim();
                if !key.is_empty() {
                    self.set(key)?;
                    return Ok(Some((key.to_string(), KeyPlace::Keyring)));
                }
            }
            return Ok(None);
        }
        Ok(crate::util::read_json::<KeyFile>(&self.file)?.map(|f| f.key.trim().to_string()).filter(|k| !k.is_empty()).map(|k| (k, KeyPlace::File)))
    }

    /// Stores in the keyring when configured; only hosts without a backend use the private file.
    pub fn set(&self, key: &str) -> Result<KeyPlace> {
        let key = key.trim();
        if key.is_empty() || key.len() > 1024 || key.chars().any(|c| c.is_control() || c.is_whitespace()) {
            return Err(Error::invalid("That does not look like a Nexus Mods API key."));
        }
        if let Some(k) = &self.keyring {
            k.set(key)?;
            if k.get()?.as_deref().map(str::trim) != Some(key) {
                return Err(Error::new(ErrorCode::Io, "The keyring did not retain the Nexus Mods API key."));
            }
            self.remove_file()?;
            return Ok(KeyPlace::Keyring);
        }
        write_private(&self.file, &KeyFile { format: 1, key: key.to_string() })?;
        Ok(KeyPlace::File)
    }

    fn remove_file(&self) -> Result<()> {
        match std::fs::remove_file(&self.file) {
            Ok(()) => Ok(()),
            Err(e) if e.kind() == std::io::ErrorKind::NotFound => Ok(()),
            Err(e) => Err(crate::util::io_at(&self.file, "removing")(e)),
        }
    }

    /// Forgets the key everywhere; report a failure instead of claiming it was removed.
    pub fn clear(&self) -> Result<()> {
        let ring = self.keyring.as_ref().map(|k| k.clear()).transpose();
        self.remove_file()?;
        ring.map(|_| ())
    }
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
fn platform_keyring() -> Option<Box<dyn SecretBackend>> {
    None
}

// ---------------------------------------------------------------- Linux: secret-tool (libsecret)

#[cfg(target_os = "linux")]
fn platform_keyring() -> Option<Box<dyn SecretBackend>> {
    crate::util::which("secret-tool").map(|exe| Box::new(SecretTool { exe }) as Box<dyn SecretBackend>)
}

#[cfg(target_os = "linux")]
pub struct SecretTool {
    exe: PathBuf,
}

#[cfg(target_os = "linux")]
const ATTRS: [&str; 4] = ["application", "kiln-launcher", "service", "nexusmods-api-key"];

#[cfg(target_os = "linux")]
impl SecretTool {
    /// Runs secret-tool with a 5 s limit (a keyring that waits for an unlock prompt nobody answers must not hang us).
    fn run(&self, args: &[&str], stdin: Option<&str>) -> Result<(bool, String)> {
        use std::io::Write;
        use std::process::{Command, Stdio};
        let mut child = Command::new(&self.exe)
            .args(args)
            .args(ATTRS)
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
        let (ok, _) = self.run(&["store", "--label=Kiln Launcher: Nexus Mods API key"], Some(secret))?;
        if ok { Ok(()) } else { Err(Error::new(ErrorCode::Unsupported, "the keyring refused the key")) }
    }
    fn clear(&self) -> Result<()> {
        let (ok, _) = self.run(&["clear"], None)?;
        if ok || self.get()?.is_none() { Ok(()) } else { Err(Error::new(ErrorCode::Io, "The keyring could not remove the Nexus Mods API key.")) }
    }
}

// ---------------------------------------------------------------- Windows: Credential Manager

#[cfg(windows)]
fn platform_keyring() -> Option<Box<dyn SecretBackend>> {
    Some(Box::new(WinCred))
}

#[cfg(windows)]
pub struct WinCred;

#[cfg(windows)]
const TARGET: &str = "Kiln Launcher/nexusmods-api-key";

#[cfg(windows)]
fn wide(s: &str) -> Vec<u16> {
    s.encode_utf16().chain(std::iter::once(0)).collect()
}

#[cfg(windows)]
impl SecretBackend for WinCred {
    fn get(&self) -> Result<Option<String>> {
        use windows_sys::Win32::Security::Credentials::{CRED_TYPE_GENERIC, CREDENTIALW, CredFree, CredReadW};
        let target = wide(TARGET);
        let mut p: *mut CREDENTIALW = std::ptr::null_mut();
        // SAFETY: target is NUL-terminated; on success p points to a CREDENTIALW the API allocated, freed below.
        unsafe {
            if CredReadW(target.as_ptr(), CRED_TYPE_GENERIC, 0, &mut p) == 0 {
                let code = windows_sys::Win32::Foundation::GetLastError();
                return if code == windows_sys::Win32::Foundation::ERROR_NOT_FOUND { Ok(None) } else { Err(Error::new(ErrorCode::Io, "Credential Manager could not read the Nexus Mods API key.")) };
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
        let target = wide(TARGET);
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
        if ok != 0 { Ok(()) } else { Err(Error::new(ErrorCode::Unsupported, "Credential Manager refused the key")) }
    }
    fn clear(&self) -> Result<()> {
        use windows_sys::Win32::Security::Credentials::{CRED_TYPE_GENERIC, CredDeleteW};
        let target = wide(TARGET);
        // SAFETY: NUL-terminated target.
        let ok = unsafe { CredDeleteW(target.as_ptr(), CRED_TYPE_GENERIC, 0) };
        if ok != 0 || unsafe { windows_sys::Win32::Foundation::GetLastError() } == windows_sys::Win32::Foundation::ERROR_NOT_FOUND {
            Ok(())
        } else {
            Err(Error::new(ErrorCode::Io, "Credential Manager could not remove the Nexus Mods API key."))
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::sync::{Arc, Mutex};

    #[derive(Default)]
    struct Ring {
        value: Mutex<Option<String>>,
        fail_set: bool,
        fail_get: bool,
        fail_clear: bool,
    }
    impl SecretBackend for Arc<Ring> {
        fn get(&self) -> Result<Option<String>> {
            if self.fail_get { return Err(Error::new(ErrorCode::Io, "locked")); }
            Ok(self.value.lock().unwrap().clone())
        }
        fn set(&self, value: &str) -> Result<()> {
            if self.fail_set { return Err(Error::new(ErrorCode::Io, "locked")); }
            *self.value.lock().unwrap() = Some(value.into());
            Ok(())
        }
        fn clear(&self) -> Result<()> {
            if self.fail_clear { return Err(Error::new(ErrorCode::Io, "locked")); }
            *self.value.lock().unwrap() = None;
            Ok(())
        }
    }
    fn temp() -> crate::library::TempDir {
        crate::library::TempDir::new(&std::env::temp_dir(), "nexus-keystore-test").unwrap()
    }

    #[test]
    fn a_present_but_failing_keyring_never_creates_plaintext() {
        for ring in [Ring { fail_set: true, ..Default::default() }, Ring { fail_get: true, ..Default::default() }] {
            let dir = temp();
            let ks = KeyStore::with_backend(dir.path(), Some(Box::new(Arc::new(ring))));
            assert!(ks.set("test-secret").is_err());
            assert!(!ks.file_path().exists());
        }
    }

    #[test]
    fn legacy_plaintext_is_migrated_and_removed_when_keyring_returns() {
        let dir = temp();
        let file = KeyStore::with_backend(dir.path(), None);
        assert_eq!(file.set("test-secret").unwrap(), KeyPlace::File);
        let ring = Arc::new(Ring::default());
        let ks = KeyStore::with_backend(dir.path(), Some(Box::new(ring.clone())));
        assert_eq!(ks.get().unwrap(), Some(("test-secret".into(), KeyPlace::Keyring)));
        assert_eq!(ring.value.lock().unwrap().as_deref(), Some("test-secret"));
        assert!(!ks.file_path().exists());
    }

    #[test]
    fn clear_reports_keyring_failure() {
        let dir = temp();
        let ring = Ring { fail_clear: true, ..Default::default() };
        let ks = KeyStore::with_backend(dir.path(), Some(Box::new(Arc::new(ring))));
        assert!(ks.clear().is_err());
    }

    #[test]
    fn private_writes_do_not_share_or_remove_another_writers_temporary_file() {
        let dir = temp();
        let path = dir.path().join("state.json");
        let old_tmp = dir.path().join("state.json.tmp");
        std::fs::write(&old_tmp, b"someone else's write").unwrap();
        std::thread::scope(|scope| {
            for n in 0..12 {
                let path = &path;
                scope.spawn(move || write_private(path, &vec![n; 100]).unwrap());
            }
        });
        let data: Vec<u8> = serde_json::from_slice(&std::fs::read(&path).unwrap()).unwrap();
        assert_eq!(data.len(), 100);
        assert!(data.iter().all(|n| *n == data[0]));
        assert_eq!(std::fs::read(old_tmp).unwrap(), b"someone else's write");
        #[cfg(unix)]
        {
            use std::os::unix::fs::PermissionsExt;
            assert_eq!(std::fs::metadata(path).unwrap().permissions().mode() & 0o777, 0o600);
        }
    }
}
