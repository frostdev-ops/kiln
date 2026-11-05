//! Remote servers over SSH (the user's ssh config and agent; Kiln stores no keys or passwords). Every call is
//! non-interactive (`-o BatchMode=yes`): a host that needs a password fails with ErrorCode::Network instead of
//! prompting. Remote machines are expected to be Linux (POSIX shell, `cat`).

use crate::{Error, ErrorCode, Result};
use serde::{Deserialize, Serialize};
use std::process::{Command, Stdio};
use std::time::Duration;

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct RemoteSpec {
    /// ssh destination as the user's ssh config knows it (host alias or user@host).
    pub host: String,
    #[serde(default)]
    pub port: Option<u16>,
    /// The server folder on the remote machine.
    pub dir: String,
    /// kilnctl instance name there, when it uses kilnctl.
    #[serde(default)]
    pub instance: Option<String>,
}

impl RemoteSpec {
    /// Refuses destinations ssh would read as options, and relative or odd server folders.
    pub fn validate(&self) -> Result<()> {
        let h = &self.host;
        if h.is_empty() || h.starts_with('-') || h.len() > 255 || h.chars().any(|c| c.is_whitespace() || c.is_control()) {
            return Err(Error::invalid(format!("ssh host {h:?} is not a host name or alias")));
        }
        if !self.dir.starts_with('/') || self.dir.contains('\0') || self.dir.contains('\n') {
            return Err(Error::invalid(format!("remote server folder {:?} must be an absolute path", self.dir)));
        }
        Ok(())
    }

    /// `ssh -o BatchMode=yes [-p port] [extra...] <host>`, ready for the remote command.
    pub fn ssh(&self, extra: &[&str]) -> Command {
        let mut c = Command::new(ssh_program());
        c.args(["-o", "BatchMode=yes", "-o", "ConnectTimeout=10"]);
        if let Some(p) = self.port {
            c.args(["-p", &p.to_string()]);
        }
        c.args(extra);
        c.arg("--").arg(&self.host);
        c.stdin(Stdio::null());
        c
    }

    fn path(&self, relative: &str) -> Result<String> {
        if relative.is_empty() || relative.starts_with('/') || relative.split('/').any(|s| s == "..") {
            return Err(Error::invalid(format!("{relative:?} must be a relative path inside the server folder")));
        }
        Ok(format!("{}/{}", self.dir.trim_end_matches('/'), relative))
    }
}

/// The ssh client: `$KILN_SSH` when set (tests, an unusual install), else `ssh` from PATH.
pub fn ssh_program() -> std::ffi::OsString {
    std::env::var_os("KILN_SSH").filter(|s| !s.is_empty()).unwrap_or_else(|| "ssh".into())
}

/// POSIX shell single-quoting.
pub fn sh_quote(s: &str) -> String {
    if !s.is_empty() && s.chars().all(|c| c.is_ascii_alphanumeric() || "-_./:=@%+,".contains(c)) {
        return s.to_string();
    }
    format!("'{}'", s.replace('\'', "'\\''"))
}

/// Runs a command on the remote machine (`ssh -o BatchMode=yes`, each argument shell-quoted), returns stdout.
pub fn run(remote: &RemoteSpec, args: &[&str]) -> Result<String> {
    let out = run_bytes(remote, args, Duration::from_secs(60))?;
    Ok(String::from_utf8_lossy(&out).into_owned())
}

fn run_bytes(remote: &RemoteSpec, args: &[&str], timeout: Duration) -> Result<Vec<u8>> {
    remote.validate()?;
    let line = args.iter().map(|a| sh_quote(a)).collect::<Vec<_>>().join(" ");
    let mut cmd = remote.ssh(&[]);
    cmd.arg(line).stdout(Stdio::piped()).stderr(Stdio::piped());
    let child = cmd.spawn().map_err(|e| Error::new(ErrorCode::Network, format!("cannot run ssh: {e}")))?;
    let out = wait_with_timeout(child, timeout)?;
    if !out.status.success() {
        let err = String::from_utf8_lossy(&out.stderr).trim().to_string();
        // the remote command's own failure ("No such file") vs ssh's (exit 255)
        let code = if out.status.code() == Some(255) { ErrorCode::Network } else if err.contains("No such file") { ErrorCode::NotFound } else { ErrorCode::Io };
        return Err(Error::new(code, format!("ssh {}: {}", remote.host, if err.is_empty() { format!("exit {}", out.status) } else { err })));
    }
    Ok(out.stdout)
}

fn wait_with_timeout(mut child: std::process::Child, timeout: Duration) -> Result<std::process::Output> {
    use std::io::Read;
    let mut stdout = child.stdout.take();
    let mut stderr = child.stderr.take();
    let t_out = std::thread::spawn(move || {
        let mut b = Vec::new();
        if let Some(s) = stdout.as_mut() {
            let _ = s.read_to_end(&mut b);
        }
        b
    });
    let t_err = std::thread::spawn(move || {
        let mut b = Vec::new();
        if let Some(s) = stderr.as_mut() {
            let _ = s.read_to_end(&mut b);
        }
        b
    });
    let start = std::time::Instant::now();
    let status = loop {
        if let Some(st) = child.try_wait()? {
            break st;
        }
        if start.elapsed() > timeout {
            let _ = child.kill();
            let _ = child.wait();
            return Err(Error::new(ErrorCode::Timeout, format!("ssh did not finish within {} s", timeout.as_secs())));
        }
        std::thread::sleep(Duration::from_millis(20));
    };
    Ok(std::process::Output { status, stdout: t_out.join().unwrap_or_default(), stderr: t_err.join().unwrap_or_default() })
}

/// Reads a remote file (e.g. kiln/data/kiln-admin/endpoint.json), relative to the server folder.
pub fn read_file(remote: &RemoteSpec, relative: &str) -> Result<Vec<u8>> {
    let path = remote.path(relative)?;
    run_bytes(remote, &["cat", "--", &path], Duration::from_secs(30))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn quoting_and_validation() {
        assert_eq!(sh_quote("a/b.json"), "a/b.json");
        assert_eq!(sh_quote("it's here"), "'it'\\''s here'");
        assert_eq!(sh_quote(""), "''");
        let r = RemoteSpec { host: "-oProxyCommand=x".into(), port: None, dir: "/srv".into(), instance: None };
        assert!(r.validate().is_err());
        let r = RemoteSpec { host: "box".into(), port: Some(2222), dir: "/srv/e".into(), instance: None };
        assert!(r.validate().is_ok());
        assert!(r.path("../etc/passwd").is_err());
        assert_eq!(r.path("kiln/data/x").unwrap(), "/srv/e/kiln/data/x");
    }
}
