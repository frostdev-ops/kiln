// SPDX-License-Identifier: MIT
//! Small helpers: paths, time formatting, external commands. Shims over kiln_manage::util.
use anyhow::Result;
use std::path::Path;
use std::process::Command;

#[allow(unused_imports)]
pub use kiln_manage::util::{
    civil_from_days, clean, config_dir, expand, fmt_utc, home, human_duration, human_size, is_executable, stamp,
    state_dir, utc, which, with_suffix,
};

/// What the Wine-instance commands say on Windows (kilnctl's instances are Wine servers on Linux).
#[cfg_attr(unix, allow(dead_code))]
pub const UNIX_ONLY: &str = "kilnctl runs its servers under Wine on Linux; on Windows run a server with the Kiln Launcher \
     (bundle, client, admin and deploy --bundle work here)";

/// kiln_manage's errors as anyhow errors with just their message (kilnctl printed these texts before the move).
pub trait Km<T> {
    fn km(self) -> Result<T>;
}

impl<T> Km<T> for kiln_manage::Result<T> {
    fn km(self) -> Result<T> {
        self.map_err(|e| anyhow::Error::msg(e.message))
    }
}

/// sha256 of a file (in-process sha2; kilnctl used to run coreutils' `sha256sum`).
pub fn sha256(path: &Path) -> Result<String> {
    kiln_manage::util::sha256(path).km()
}

/// Runs a command with inherited stdio; fails on a non-zero exit.
pub fn run_status(cmd: &mut Command, what: &str) -> Result<()> {
    kiln_manage::util::run_status(cmd, what).km()
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::path::PathBuf;
    use std::time::{Duration, UNIX_EPOCH};

    #[test]
    fn civil_dates() {
        assert_eq!(civil_from_days(0), (1970, 1, 1));
        assert_eq!(civil_from_days(-1), (1969, 12, 31));
        assert_eq!(civil_from_days(11_016), (2000, 2, 29));
        assert_eq!(civil_from_days(20_760), (2026, 11, 3));
    }

    #[test]
    fn timestamps() {
        let t = UNIX_EPOCH + Duration::from_secs(1_793_756_357);
        assert_eq!(fmt_utc(t), "2026-11-04 01:39:17Z");
        assert_eq!(stamp(t), "20261104-013917Z");
    }

    #[test]
    fn durations_and_sizes() {
        assert_eq!(human_duration(Duration::from_secs(5)), "5s");
        assert_eq!(human_duration(Duration::from_secs(192)), "3m12s");
        assert_eq!(human_duration(Duration::from_secs(3720)), "1h02m");
        assert_eq!(human_duration(Duration::from_secs(97_200)), "1d03h");
        assert_eq!(human_size(512), "512 B");
        assert_eq!(human_size(42_833), "41.8 KB");
    }

    #[test]
    fn tilde_and_suffix() {
        let h = Path::new("/home/u");
        assert_eq!(expand("~/srv/x", h), PathBuf::from("/home/u/srv/x"));
        assert_eq!(expand("~", h), PathBuf::from("/home/u"));
        assert_eq!(expand("/abs", h), PathBuf::from("/abs"));
        assert_eq!(expand("~other/x", h), PathBuf::from("~other/x"));
        assert_eq!(clean(Path::new("/a/./b/")), PathBuf::from("/a/b"));
        assert_eq!(with_suffix(Path::new("/srv/x"), ".pfx"), PathBuf::from("/srv/x.pfx"));
    }
}
