// SPDX-License-Identifier: MIT
//! `kilnctl run`: the server in the foreground, the way systemd runs it. Shims over kiln_manage::server::run
//!: SIGINT, SIGTERM and SIGHUP become one SIGINT to the server process, then `wineserver -w`.
use crate::config::Instance;
#[cfg(unix)]
use crate::util::Km;
use crate::wine::Runner;
use anyhow::Result;
use std::time::Duration;

#[cfg(unix)]
#[allow(unused_imports)]
pub use kiln_manage::server::run::{exit_code, signame};

/// `wineserver -w` for the instance's prefix, bounded: on timeout `wineserver -k`.
#[cfg(unix)]
pub fn wineserver_wait(runner: &Runner, inst: &Instance, timeout: Duration) -> Result<()> {
    kiln_manage::server::run::wineserver_wait(runner, inst, timeout).km()
}

#[cfg(unix)]
pub fn run(inst: &Instance) -> Result<i32> {
    kiln_manage::server::run::run_foreground(inst).km()
}

// Windows: kilnctl's instances are Wine servers on Linux; bundle, client, admin and deploy --bundle work on Windows,
// a native Windows server is run by the Kiln Launcher (server::run's windows runner).
#[cfg(not(unix))]
pub fn wineserver_wait(_runner: &Runner, _inst: &Instance, _timeout: Duration) -> Result<()> {
    anyhow::bail!("{}", crate::util::UNIX_ONLY)
}

#[cfg(not(unix))]
pub fn run(_inst: &Instance) -> Result<i32> {
    anyhow::bail!("{}", crate::util::UNIX_ONLY)
}

#[cfg(all(test, unix))]
mod tests {
    use super::*;
    use std::os::unix::process::ExitStatusExt;
    use std::process::ExitStatus;

    #[test]
    fn exit_codes() {
        assert_eq!(exit_code(ExitStatus::from_raw(0)), 0);
        assert_eq!(exit_code(ExitStatus::from_raw(3 << 8)), 3);
        assert_eq!(exit_code(ExitStatus::from_raw(libc::SIGKILL)), 128 + 9);
        assert_eq!(signame(libc::SIGTERM), "SIGTERM");
    }
}
