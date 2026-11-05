// SPDX-License-Identifier: MIT
//! SteamCMD (anonymous login; the dedicated server is app 2278520, a Windows depot). Shims over
//! kiln_manage::server::steamcmd.
use crate::util::Km;
use anyhow::Result;
use std::path::Path;

#[allow(unused_imports)]
pub use kiln_manage::server::steamcmd::{APP_ID, buildid, is_success, parse_buildid, steamcmd_args, strip_ansi};

/// Installs or validates the server in `dir`, echoing SteamCMD's output.
pub fn app_update(dir: &Path) -> Result<()> {
    let mut first = true;
    kiln_manage::server::steamcmd::app_update(dir, &mut |line| {
        if std::mem::take(&mut first) {
            println!("{line}");
        } else {
            println!("  {line}");
        }
    })
    .km()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn args() {
        let a = steamcmd_args(Path::new("/srv/x"));
        assert_eq!(
            a.join(" "),
            "+@sSteamCmdForcePlatformType windows +force_install_dir /srv/x +login anonymous +app_update 2278520 validate +quit"
        );
    }

    #[test]
    fn success_line() {
        assert!(is_success("Success! App '2278520' fully installed."));
        assert!(is_success("Success! App '2278520' already up to date."));
        assert!(!is_success("ERROR! Failed to install app '2278520' (No subscription)"));
        assert!(!is_success("Success! App '228980' fully installed."));
        assert_eq!(strip_ansi("\u{1b}[0mSuccess! App '2278520' fully installed."), "Success! App '2278520' fully installed.");
        assert_eq!(strip_ansi("Loading Steam API...\u{1b}[0mOK"), "Loading Steam API...OK");
    }

    #[test]
    fn manifest() {
        let acf = "\"AppState\"\n{\n\t\"appid\"\t\t\"2278520\"\n\t\"buildid\"\t\t\"26404334\"\n\t\"TargetBuildID\"\t\t\"26404334\"\n}\n";
        assert_eq!(parse_buildid(acf).as_deref(), Some("26404334"));
        assert_eq!(parse_buildid("\"AppState\"\n{\n}\n"), None);
    }
}
