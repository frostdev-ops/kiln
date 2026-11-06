//! How this copy of the launcher was installed, and whether it can replace itself. The AppImage (Linux) and the NSIS
//! install (Windows) update in place. A .deb or .rpm belongs to the system's package manager, and a bare binary (a
//! development build, an unpacked copy) has no installer: those get the download page instead.

use serde::Serialize;
use std::path::Path;

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize)]
#[serde(rename_all = "lowercase")]
pub enum Kind {
    Appimage,
    Nsis,
    Msi,
    Deb,
    Rpm,
    /// Not from a Tauri bundle: a development build or a copied binary.
    Unbundled,
}

/// The bundle a binary was built into (Tauri writes it into the binary of each bundle).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Bundle {
    AppImage,
    Deb,
    Rpm,
    Nsis,
    Msi,
    Other,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct InstallInfo {
    pub kind: Kind,
    /// The launcher can download, verify and put the update in place itself.
    pub in_place: bool,
    /// Why not, in one sentence (`in_place` false).
    pub reason: Option<String>,
}

/// `os`: `std::env::consts::OS`. `appimage`: the AppImage's path (`$APPIMAGE`) and whether its folder is writable.
pub fn classify(os: &str, bundle: Option<Bundle>, appimage: Option<(&Path, bool)>) -> InstallInfo {
    let manual = |kind, reason: &str| InstallInfo { kind, in_place: false, reason: Some(reason.into()) };
    let ok = |kind| InstallInfo { kind, in_place: true, reason: None };
    match (os, bundle) {
        ("linux", Some(Bundle::Deb)) => manual(Kind::Deb, "Installed from a .deb package: your package manager updates it."),
        ("linux", Some(Bundle::Rpm)) => manual(Kind::Rpm, "Installed from an .rpm package: your package manager updates it."),
        ("linux", Some(Bundle::AppImage)) => match appimage {
            Some((_, true)) => ok(Kind::Appimage),
            Some((path, false)) => manual(Kind::Appimage, &format!("The AppImage's folder ({}) is not writable.", path.parent().unwrap_or(path).display())),
            None => manual(Kind::Appimage, "This AppImage was unpacked, so it cannot replace itself."),
        },
        ("windows", Some(Bundle::Nsis)) => ok(Kind::Nsis),
        ("windows", Some(Bundle::Msi)) => ok(Kind::Msi),
        _ => manual(Kind::Unbundled, "This copy was not installed by a Kiln installer (a development build or a copied file)."),
    }
}

/// This process.
pub fn detect<R: tauri::Runtime>(app: &tauri::AppHandle<R>) -> InstallInfo {
    use tauri::utils::config::BundleType;
    let bundle = tauri::utils::platform::bundle_type().map(|b| match b {
        BundleType::AppImage => Bundle::AppImage,
        BundleType::Deb => Bundle::Deb,
        BundleType::Rpm => Bundle::Rpm,
        BundleType::Nsis => Bundle::Nsis,
        BundleType::Msi => Bundle::Msi,
        _ => Bundle::Other,
    });
    #[cfg(target_os = "linux")]
    let appimage = tauri::Manager::env(app).appimage.map(std::path::PathBuf::from);
    #[cfg(not(target_os = "linux"))]
    let appimage: Option<std::path::PathBuf> = {
        let _ = app;
        None
    };
    let writable = appimage.as_deref().is_some_and(|p| p.parent().is_some_and(dir_writable));
    classify(std::env::consts::OS, bundle, appimage.as_deref().map(|p| (p, writable)))
}

/// Can this process create files in `dir` (the AppImage is replaced by a rename there)?
fn dir_writable(dir: &Path) -> bool {
    #[cfg(unix)]
    {
        use std::os::unix::ffi::OsStrExt;
        let Ok(c) = std::ffi::CString::new(dir.as_os_str().as_bytes()) else { return false };
        // SAFETY: a valid NUL-terminated path; access() only reads it.
        unsafe { libc_access(c.as_ptr(), 2 /* W_OK */) == 0 }
    }
    #[cfg(not(unix))]
    {
        let _ = dir;
        true
    }
}

#[cfg(unix)]
unsafe extern "C" {
    #[link_name = "access"]
    fn libc_access(path: *const std::ffi::c_char, mode: i32) -> i32;
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn deb_and_rpm_are_manual() {
        let deb = classify("linux", Some(Bundle::Deb), None);
        assert_eq!(deb.kind, Kind::Deb);
        assert!(!deb.in_place);
        assert!(deb.reason.unwrap().contains("package manager"));
        // even when an APPIMAGE variable happens to be set
        assert!(!classify("linux", Some(Bundle::Deb), Some((Path::new("/tmp/x.AppImage"), true))).in_place);
        assert!(!classify("linux", Some(Bundle::Rpm), None).in_place);
    }

    #[test]
    fn appimage_updates_in_place_when_its_folder_is_writable() {
        let p = Path::new("/home/player/Apps/Kiln.AppImage");
        assert_eq!(classify("linux", Some(Bundle::AppImage), Some((p, true))), InstallInfo { kind: Kind::Appimage, in_place: true, reason: None });
        let ro = classify("linux", Some(Bundle::AppImage), Some((p, false)));
        assert!(!ro.in_place);
        assert!(ro.reason.unwrap().contains("/home/player/Apps"));
        assert!(!classify("linux", Some(Bundle::AppImage), None).in_place);
    }

    #[test]
    fn windows_installs_update_in_place() {
        assert!(classify("windows", Some(Bundle::Nsis), None).in_place);
        assert!(classify("windows", Some(Bundle::Msi), None).in_place);
        assert_eq!(classify("windows", None, None).kind, Kind::Unbundled);
    }

    #[test]
    fn unbundled_builds_are_manual() {
        let dev = classify("linux", None, None);
        assert_eq!(dev.kind, Kind::Unbundled);
        assert!(!dev.in_place);
        assert!(!classify("linux", Some(Bundle::Other), None).in_place);
    }

    #[test]
    fn this_test_binary_is_unbundled() {
        let app = tauri::test::mock_app();
        assert_eq!(detect(app.handle()).kind, Kind::Unbundled);
    }
}
