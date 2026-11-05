// SPDX-License-Identifier: MIT
//! Instance files: `~/.config/kiln/instances/<name>.toml` (`$XDG_CONFIG_HOME` respected). Shims over
//! kiln_manage::server::instance.
use crate::util::Km;
use anyhow::Result;
use std::path::Path;

#[allow(unused_imports)]
pub use kiln_manage::server::instance::{
    DEFAULT_PORT, InitSpec, Instance, Kiln, Policy, compiled_source, instance_file, instances_dir, render,
};

/// `--policy` on the command line (the same values and help texts as kiln_manage's Policy).
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default, clap::ValueEnum)]
pub enum PolicyArg {
    /// Hooks only on builds with a verified record (kiln/sigdb/builds/server/<sha256>.json)
    #[default]
    VerifiedOnly,
    /// Also on unverified builds (labs)
    Any,
}

impl From<PolicyArg> for Policy {
    fn from(p: PolicyArg) -> Policy {
        match p {
            PolicyArg::VerifiedOnly => Policy::VerifiedOnly,
            PolicyArg::Any => Policy::Any,
        }
    }
}

/// Instance names double as systemd instance names (`kiln@<name>`) and file names.
pub fn validate_name(name: &str) -> Result<()> {
    kiln_manage::server::instance::validate_name(name).km()
}

pub fn parse(name: &str, file: &Path, text: &str, home: &Path) -> Result<Instance> {
    kiln_manage::server::instance::parse(name, file, text, home).km()
}

pub fn load(name: &str) -> Result<Instance> {
    kiln_manage::server::instance::load(name).km()
}

/// Names of all instance files.
pub fn list() -> Vec<String> {
    kiln_manage::server::instance::names()
}

// Unix: kilnctl's Wine instances (Unix paths, tar, /proc); on Windows kilnctl only bundles, installs and administers.
#[cfg(all(test, unix))]
mod tests {
    use super::*;
    use crate::wine::WineSpec;
    use std::path::PathBuf;
    use std::time::Duration;

    const H: &str = "/home/u";

    fn p(text: &str) -> Result<Instance> {
        parse("t1", Path::new("/cfg/t1.toml"), text, Path::new(H))
    }

    #[test]
    fn names() {
        for ok in ["a", "kilnctl-test", "srv_2", "9lives"] {
            assert!(validate_name(ok).is_ok(), "{ok}");
        }
        for bad in ["", "-x", "_x", "A", "a b", "a/b", "a.b", "a@b", &"x".repeat(49)] {
            assert!(validate_name(bad).is_err(), "{bad}");
        }
    }

    #[test]
    fn minimal_defaults() {
        let i = p("dir = \"~/srv/x\"\n").unwrap();
        assert_eq!(i.dir, PathBuf::from("/home/u/srv/x"));
        assert_eq!(i.prefix, PathBuf::from("/home/u/srv/x.pfx"));
        assert_eq!(i.backup_dir, PathBuf::from("/home/u/srv/x.backups"));
        assert_eq!(i.wine, WineSpec::GeLatest);
        assert_eq!(i.query_port, DEFAULT_PORT);
        assert!(i.require_ntsync);
        assert_eq!(i.stop_timeout, Duration::from_secs(90));
        assert_eq!(i.kiln.policy, Policy::VerifiedOnly);
        assert_eq!(i.kiln.build, i.kiln.source.join("native/build"));
        assert!(!i.kiln.samples);
        assert_eq!(i.unit(), "kiln@t1.service");
    }

    #[test]
    fn full_file() {
        let i = p(r#"
dir = "/srv/e/"
prefix = "/srv/pfx/e"
wine = "ge:GE-Proton11-11-x86_64"
query_port = 25760
backup_dir = "~/bk"
require_ntsync = false
stop_timeout = 120
[kiln]
source = "/src/kiln"
build = "/src/kiln/.out/w"
policy = "any"
samples = true
[env]
WINEDEBUG = "+seh"
"#)
        .unwrap();
        assert_eq!(i.dir, PathBuf::from("/srv/e"));
        assert_eq!(i.prefix, PathBuf::from("/srv/pfx/e"));
        assert_eq!(i.wine, WineSpec::Ge("GE-Proton11-11-x86_64".into()));
        assert_eq!(i.query_port, 25760);
        assert_eq!(i.backup_dir, PathBuf::from("/home/u/bk"));
        assert!(!i.require_ntsync);
        assert_eq!(i.stop_timeout, Duration::from_secs(120));
        assert_eq!(i.kiln.source, PathBuf::from("/src/kiln"));
        assert_eq!(i.kiln.build, PathBuf::from("/src/kiln/.out/w"));
        assert_eq!(i.kiln.policy, Policy::Any);
        assert!(i.kiln.samples);
        assert_eq!(i.env["WINEDEBUG"], "+seh");
    }

    #[test]
    fn rejects() {
        let bad = [
            "",                                                                     // no dir
            "dir = \"rel/x\"",                                                      // relative
            "dir = \"/x\"\nprefix = \"/x\"",                                        // same folder
            "dir = \"/x\"\nquery_port = 80",                                        // privileged port
            "dir = \"/x\"\nquery_port = 70000",                                     // not a u16
            "dir = \"/x\"\nstop_timeout = 1",                                       // too short for a save
            "dir = \"/x\"\nwine = \"wine\"",                                        // not ge/system/absolute
            "dir = \"/x\"\nbogus = 1",                                              // unknown key
            "dir = \"/x\"\n[kiln]\npolicy = \"sometimes\"",                         // bad policy
            "dir = \"/x\"\n[env]\nWINEPREFIX = \"/y\"",                             // prefix comes from prefix=
            "dir = \"/x\"\n[env]\nWINEDLLOVERRIDES = \"mscoree=;dinput8=n,b\"",     // mscoree disabled
            "dir = \"/x\"\n[env]\n\"A-B\" = \"1\"",                                 // not a variable name
            "dir = \"~/.local/share/Steam/steamapps/common/EnshroudedServer\"",     // Steam client library
            "dir = \"/x\"\nprefix = \"/s/steamapps/compatdata/2278520/pfx\"",        // Proton prefix
        ];
        for t in bad {
            assert!(p(t).is_err(), "accepted: {t:?}");
        }
    }

    #[test]
    fn render_round_trips() {
        let wine = WineSpec::Ge("GE-Proton11-11-x86_64".into());
        let text = render(&InitSpec {
            name: "t1",
            dir: Path::new("/srv/with \"quote\""),
            prefix: Path::new("/srv/t1.pfx"),
            wine: &wine,
            port: 25760,
            backup_dir: Path::new("/srv/t1.backups"),
            source: Path::new("/src/kiln"),
            build: Path::new("/src/kiln/native/build"),
            policy: Policy::Any,
            samples: false,
        });
        let i = p(&text).unwrap();
        assert_eq!(i.dir, PathBuf::from("/srv/with \"quote\""));
        assert_eq!(i.wine, wine);
        assert_eq!(i.query_port, 25760);
        assert_eq!(i.kiln.policy, Policy::Any);
        assert!(i.env.is_empty());
    }
}
