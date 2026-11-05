// SPDX-License-Identifier: MIT
//! Wine runner selection and the environment every Wine call of an instance gets. Shims over kiln_manage::wine
//! (decisions in docs/kiln/linux-server.md and the crate module).
use crate::util::Km;
use anyhow::Result;
use std::path::Path;

#[allow(unused_imports)]
pub use kiln_manage::wine::{
    DLL_OVERRIDES, Runner, WineSpec, apply_env, dinput8_native_first, effective, env, ge_installs, ge_roots, ge_version,
    parse_overrides,
};

pub fn resolve(spec: &WineSpec, home: &Path) -> Result<Runner> {
    kiln_manage::wine::resolve(spec, home).km()
}

/// Refuses overrides that take Wine's builtin `mscoree` away (disabled, or native-only).
pub fn check_mscoree(overrides: &str) -> Result<()> {
    kiln_manage::wine::check_mscoree(overrides).km()
}

// Unix: kilnctl's Wine instances (Unix paths, tar, /proc); on Windows kilnctl only bundles, installs and administers.
#[cfg(all(test, unix))]
mod tests {
    use super::*;

    #[test]
    fn spec_parsing() {
        let h = Path::new("/home/u");
        assert_eq!(WineSpec::parse("ge", h).unwrap(), WineSpec::GeLatest);
        assert_eq!(WineSpec::parse(" system ", h).unwrap(), WineSpec::System);
        assert_eq!(WineSpec::parse("ge:GE-Proton11-11-x86_64", h).unwrap(), WineSpec::Ge("GE-Proton11-11-x86_64".into()));
        assert_eq!(WineSpec::parse("~/wine/bin/wine", h).unwrap(), WineSpec::Path("/home/u/wine/bin/wine".into()));
        assert!(WineSpec::parse("wine", h).is_err());
        assert!(WineSpec::parse("ge:", h).is_err());
        assert!(WineSpec::parse("", h).is_err());
        for s in ["ge", "system", "ge:GE-Proton9-27", "/opt/wine/bin/wine"] {
            assert_eq!(WineSpec::parse(s, h).unwrap().to_config(), s);
        }
    }

    #[test]
    fn ge_versions() {
        assert_eq!(ge_version("GE-Proton11-11-x86_64"), Some(vec![11, 11]));
        assert_eq!(ge_version("GE-Proton9-27"), Some(vec![9, 27]));
        assert_eq!(ge_version("UMU-Proton-10.0-4"), None);
        assert_eq!(ge_version("GE-Proton"), None);
        assert!(ge_version("GE-Proton11-11-x86_64") > ge_version("GE-Proton11-7-x86_64"));
        assert!(ge_version("GE-Proton10-4") > ge_version("GE-Proton9-27"));
    }

    #[test]
    fn overrides() {
        let l = parse_overrides("mshtml=;dinput8=n,b");
        assert_eq!(l, vec![("mshtml".into(), "".into()), ("dinput8".into(), "n,b".into())]);
        assert_eq!(parse_overrides("a.dll,B=n;c")[1], ("b".into(), "n".into()));
        assert!(check_mscoree(DLL_OVERRIDES).is_ok());
        assert!(check_mscoree("mshtml=;mscoree=;dinput8=n,b").is_err());
        assert!(check_mscoree("mscoree=d").is_err());
        assert!(check_mscoree("mscoree.dll=n").is_err());
        assert!(check_mscoree("mscoree=n,b").is_ok());
        assert!(check_mscoree("*=n").is_err());
        assert!(check_mscoree("*=n;mscoree=b").is_ok());
        assert!(check_mscoree("mscoree=;mscoree=b").is_ok());
        assert!(dinput8_native_first(DLL_OVERRIDES));
        assert!(!dinput8_native_first("dinput8=b,n"));
        assert!(!dinput8_native_first("mshtml="));
        assert!(dinput8_native_first("DINPUT8.DLL=native,builtin"));
    }
}
