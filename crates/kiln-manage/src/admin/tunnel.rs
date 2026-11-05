//! `ssh -N -L 127.0.0.1:<local>:127.0.0.1:<remote port> <host>` with the user's ssh config and agent
//! (`BatchMode=yes`, `ExitOnForwardFailure=yes`); the child is killed when the tunnel closes or drops.

use crate::server::remote::RemoteSpec;
use crate::{Error, ErrorCode, Result};
use std::process::{Child, Stdio};
use std::time::{Duration, Instant};

pub struct SshTunnel {
    pub local_port: u16,
    pub remote_port: u16,
    child: Option<Child>,
}

impl SshTunnel {
    /// Starts the forward on a free local port and waits (up to 15 s) until it accepts connections.
    pub fn open(remote: &RemoteSpec, remote_port: u16) -> Result<SshTunnel> {
        remote.validate()?;
        let local_port = free_port()?;
        let fwd = format!("127.0.0.1:{local_port}:127.0.0.1:{remote_port}");
        let mut cmd = remote.ssh(&["-N", "-o", "ExitOnForwardFailure=yes", "-o", "ServerAliveInterval=15", "-L", &fwd]);
        cmd.stdout(Stdio::null()).stderr(Stdio::piped());
        let mut child = cmd.spawn().map_err(|e| Error::new(ErrorCode::Network, format!("cannot run ssh: {e}")))?;
        let start = Instant::now();
        loop {
            if let Some(st) = child.try_wait()? {
                let mut err = String::new();
                if let Some(mut s) = child.stderr.take() {
                    use std::io::Read;
                    let _ = s.read_to_string(&mut err);
                }
                return Err(Error::new(ErrorCode::Network, format!("ssh tunnel to {} ended ({st}): {}", remote.host, err.trim())));
            }
            if super::discover::probe("127.0.0.1", local_port, Duration::from_millis(200)) {
                return Ok(SshTunnel { local_port, remote_port, child: Some(child) });
            }
            if start.elapsed() > Duration::from_secs(15) {
                let _ = child.kill();
                let _ = child.wait();
                return Err(Error::new(ErrorCode::Timeout, format!("ssh tunnel to {} did not come up in 15 s", remote.host)));
            }
            std::thread::sleep(Duration::from_millis(100));
        }
    }

    /// The ssh child still runs.
    pub fn alive(&mut self) -> bool {
        self.child.as_mut().is_some_and(|c| matches!(c.try_wait(), Ok(None)))
    }

    pub fn close(mut self) -> Result<()> {
        self.kill();
        Ok(())
    }

    fn kill(&mut self) {
        if let Some(mut c) = self.child.take() {
            let _ = c.kill();
            let _ = c.wait();
        }
    }
}

impl Drop for SshTunnel {
    fn drop(&mut self) {
        self.kill();
    }
}

/// A port the OS says is free right now on 127.0.0.1.
pub fn free_port() -> Result<u16> {
    let l = std::net::TcpListener::bind("127.0.0.1:0")?;
    Ok(l.local_addr()?.port())
}
