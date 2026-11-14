//! Where `kiln publish` gets the GitHub token: the OS credential store under the fixed name `kiln-publish/github`
//! (libsecret through `secret-tool` on Linux, Credential Manager on Windows), else the environment variable
//! `KILN_PUBLISH_GITHUB_TOKEN`, and nothing else: no other variable, no config key, no argument. The token is never
//! written to a plan, a journal, a log or an error.

use super::github::TokenSource;
use crate::{Error, ErrorCode, Result};

/// The one environment variable that is read, and only when the store has no entry.
pub const ENV: &str = "KILN_PUBLISH_GITHUB_TOKEN";
/// The entry's name in the credential store.
pub const STORE_NAME: &str = "kiln-publish/github";

pub trait SecretStore {
    fn get(&self) -> Result<Option<String>>;
    fn set(&self, secret: &str) -> Result<()>;
    fn clear(&self) -> Result<()>;
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Origin {
    Store,
    Environment,
}

/// A token as it may be stored or used: 1 to 512 visible ASCII characters, no space.
pub fn check_token(t: &str) -> Result<()> {
    if t.is_empty() || t.len() > 512 || !t.bytes().all(|b| (0x21..=0x7e).contains(&b)) {
        // the text never echoes what was given
        return Err(Error::invalid("That is not a token: 1 to 512 characters without spaces are expected."));
    }
    Ok(())
}

/// The token source of the CLI. `env` is the value of [`ENV`] as the caller read it (so tests do not touch the
/// process environment).
pub struct StoredToken {
    store: Option<Box<dyn SecretStore>>,
    env: Option<String>,
}

impl StoredToken {
    pub fn new(store: Option<Box<dyn SecretStore>>, env: Option<String>) -> StoredToken {
        StoredToken { store, env: env.filter(|e| !e.is_empty()) }
    }

    /// The store of this PC and the environment of this process.
    pub fn from_system() -> StoredToken {
        StoredToken::new(platform_store(), std::env::var(ENV).ok())
    }

    /// The token and where it came from. The store wins; a store that cannot be read is an error, not a silent
    /// fall back to the environment.
    pub fn resolve(&self) -> Result<(String, Origin)> {
        let none = || Error::new(ErrorCode::NotFound, format!("No GitHub token is stored for kiln publish. Store one with: kiln publish --store-token github (or set {ENV})."));
        if let Some(s) = &self.store
            && let Some(t) = s.get()?
        {
            let t = t.trim().to_string();
            check_token(&t).map_err(|_| Error::new(ErrorCode::Refused, "The stored GitHub token is not usable; store it again."))?;
            return Ok((t, Origin::Store));
        }
        let t = self.env.clone().ok_or_else(none)?;
        let t = t.trim().to_string();
        check_token(&t).map_err(|_| Error::new(ErrorCode::Refused, format!("{ENV} does not hold a usable token.")))?;
        Ok((t, Origin::Environment))
    }

    pub fn store(&self, token: &str) -> Result<()> {
        let token = token.trim();
        check_token(token)?;
        match &self.store {
            Some(s) => s.set(token),
            None => Err(Error::new(ErrorCode::Unsupported, format!("This PC has no credential store Kiln can use (Linux: install secret-tool). Set {ENV} instead."))),
        }
    }

    pub fn forget(&self) -> Result<()> {
        match &self.store {
            Some(s) => s.clear(),
            None => Ok(()),
        }
    }
}

impl TokenSource for StoredToken {
    fn token(&self) -> Result<String> {
        self.resolve().map(|(t, _)| t)
    }
}

// ---------------------------------------------------------------- Linux: secret-tool (libsecret)

#[cfg(target_os = "linux")]
pub fn platform_store() -> Option<Box<dyn SecretStore>> {
    crate::util::which("secret-tool").map(|exe| Box::new(SecretTool { exe }) as Box<dyn SecretStore>)
}

#[cfg(target_os = "linux")]
struct SecretTool {
    exe: std::path::PathBuf,
}

#[cfg(target_os = "linux")]
impl SecretTool {
    const ATTRS: [&'static str; 4] = ["application", "kiln", "service", STORE_NAME];

    /// Runs secret-tool with a 5 s limit (a keyring waiting for an unlock nobody answers must not hang the command).
    /// The secret goes in through stdin, never through an argument.
    fn run(&self, args: &[&str], stdin: Option<&str>) -> Result<(bool, String)> {
        use std::io::{Read, Write};
        use std::process::Stdio;
        let mut child = crate::hostenv::command(&self.exe)
            .args(args)
            .args(Self::ATTRS)
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
                if let Some(o) = child.stdout.take() {
                    let _ = o.take(4096).read_to_string(&mut out);
                }
                return Ok((st.success(), out));
            }
            if std::time::Instant::now() > deadline {
                let _ = child.kill();
                let _ = child.wait();
                return Err(Error::new(ErrorCode::Timeout, "The keyring did not answer."));
            }
            std::thread::sleep(std::time::Duration::from_millis(30));
        }
    }
}

#[cfg(target_os = "linux")]
impl SecretStore for SecretTool {
    fn get(&self) -> Result<Option<String>> {
        let (ok, out) = self.run(&["lookup"], None)?;
        Ok((ok && !out.trim().is_empty()).then(|| out.trim().to_string()))
    }
    fn set(&self, secret: &str) -> Result<()> {
        let (ok, _) = self.run(&["store", "--label=Kiln: GitHub token for kiln publish"], Some(secret))?;
        if ok { Ok(()) } else { Err(Error::new(ErrorCode::Io, "The keyring did not take the token.")) }
    }
    fn clear(&self) -> Result<()> {
        self.run(&["clear"], None).map(|_| ())
    }
}

// ---------------------------------------------------------------- Windows: Credential Manager

#[cfg(windows)]
pub fn platform_store() -> Option<Box<dyn SecretStore>> {
    Some(Box::new(WinCred))
}

#[cfg(windows)]
struct WinCred;

#[cfg(windows)]
fn wide(s: &str) -> Vec<u16> {
    s.encode_utf16().chain(std::iter::once(0)).collect()
}

#[cfg(windows)]
impl SecretStore for WinCred {
    fn get(&self) -> Result<Option<String>> {
        use windows_sys::Win32::Security::Credentials::{CRED_TYPE_GENERIC, CREDENTIALW, CredFree, CredReadW};
        let target = wide(STORE_NAME);
        let mut p: *mut CREDENTIALW = std::ptr::null_mut();
        // SAFETY: target is NUL-terminated; on success p points to a CREDENTIALW the API allocated, freed below.
        unsafe {
            if CredReadW(target.as_ptr(), CRED_TYPE_GENERIC, 0, &mut p) == 0 {
                let code = windows_sys::Win32::Foundation::GetLastError();
                return if code == windows_sys::Win32::Foundation::ERROR_NOT_FOUND { Ok(None) } else { Err(Error::new(ErrorCode::Io, "Credential Manager could not be read.")) };
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
        let target = wide(STORE_NAME);
        let user = wide("github");
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
        if ok != 0 { Ok(()) } else { Err(Error::new(ErrorCode::Io, "Credential Manager did not take the token.")) }
    }
    fn clear(&self) -> Result<()> {
        use windows_sys::Win32::Security::Credentials::{CRED_TYPE_GENERIC, CredDeleteW};
        let target = wide(STORE_NAME);
        // SAFETY: NUL-terminated target.
        let ok = unsafe { CredDeleteW(target.as_ptr(), CRED_TYPE_GENERIC, 0) };
        if ok != 0 || unsafe { windows_sys::Win32::Foundation::GetLastError() } == windows_sys::Win32::Foundation::ERROR_NOT_FOUND {
            Ok(())
        } else {
            Err(Error::new(ErrorCode::Io, "Credential Manager could not remove the token."))
        }
    }
}

#[cfg(not(any(target_os = "linux", windows)))]
pub fn platform_store() -> Option<Box<dyn SecretStore>> {
    None
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::sync::{Arc, Mutex};

    #[derive(Clone, Default)]
    struct Mem(Arc<Mutex<Option<String>>>, bool);
    impl SecretStore for Mem {
        fn get(&self) -> Result<Option<String>> {
            if self.1 { Err(Error::new(ErrorCode::Timeout, "The keyring did not answer.")) } else { Ok(self.0.lock().unwrap().clone()) }
        }
        fn set(&self, s: &str) -> Result<()> {
            *self.0.lock().unwrap() = Some(s.to_string());
            Ok(())
        }
        fn clear(&self) -> Result<()> {
            *self.0.lock().unwrap() = None;
            Ok(())
        }
    }

    const A: &str = "ghp_FAKESTORETOKEN0123456789";
    const B: &str = "ghp_FAKEENVTOKEN9876543210";

    #[test]
    fn the_store_wins_and_the_environment_is_the_fallback() {
        let mem = Mem::default();
        let t = StoredToken::new(Some(Box::new(mem.clone())), Some(B.into()));
        assert_eq!(t.resolve().unwrap(), (B.to_string(), Origin::Environment));
        t.store(&format!("  {A}\n")).unwrap();
        assert_eq!(t.resolve().unwrap(), (A.to_string(), Origin::Store));
        assert_eq!(t.token().unwrap(), A);
        t.forget().unwrap();
        assert_eq!(t.resolve().unwrap().1, Origin::Environment);
        // nothing anywhere
        let none = StoredToken::new(Some(Box::new(Mem::default())), None);
        let e = none.resolve().unwrap_err();
        assert_eq!(e.code, ErrorCode::NotFound);
        assert!(e.message.contains("--store-token") && e.message.contains(ENV));
        assert_eq!(StoredToken::new(None, Some(String::new())).resolve().unwrap_err().code, ErrorCode::NotFound);
        // no store on this PC: the environment alone works, storing says why it cannot
        let env_only = StoredToken::new(None, Some(B.into()));
        assert_eq!(env_only.resolve().unwrap().1, Origin::Environment);
        assert_eq!(env_only.store(A).unwrap_err().code, ErrorCode::Unsupported);
        // a store that cannot be read is an error, not a quiet switch to the environment
        let broken = StoredToken::new(Some(Box::new(Mem(Default::default(), true))), Some(B.into()));
        assert_eq!(broken.resolve().unwrap_err().code, ErrorCode::Timeout);
    }

    #[test]
    fn a_bad_token_is_refused_without_being_echoed() {
        let t = StoredToken::new(Some(Box::new(Mem::default())), None);
        for bad in ["", "has space", "line\nbreak", "caf\u{e9}", &"x".repeat(513)] {
            let e = t.store(bad).unwrap_err();
            assert_eq!(e.code, ErrorCode::InvalidArgument);
            assert!(bad.is_empty() || !format!("{e:?}").contains(bad));
        }
        let env = StoredToken::new(None, Some("SECRET WITH SPACE".into()));
        let e = env.resolve().unwrap_err();
        assert!(!format!("{e:?}").contains("SECRET WITH SPACE"));
        // the environment is read in one place, for the one name
        let src = include_str!("token.rs");
        assert_eq!(src.matches(concat!("env::", "var(")).count(), 1);
        assert!(src.contains(concat!("std::env::", "var(ENV)")));
    }
}
