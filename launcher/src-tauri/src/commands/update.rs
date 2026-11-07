//! Tauri commands: the launcher's self-update (crate::selfupdate). Names and arguments: docs/kiln/launcher/contracts.md
//! section 9.
//!
//! The Nexus Mods build (no `self-update` feature, crate::distribution) has the same commands, so the frontend and
//! the command list stay the same, but none of them updates anything: `update_status`, `update_check`, `update_skip`
//! and `update_ack` return a status with phase `disabled` (distribution `nexus`, page: the Kiln page on Nexus Mods),
//! `update_install` and `update_restart` refuse with `unsupported`. Nothing in that build touches the network for it.

use super::TaskId;
use kiln_manage::Error;
use tauri::AppHandle;

pub use imp::UpdateStatus;

/// The update state now (also sent as `kiln://update` at every change).
#[tauri::command]
pub async fn update_status(app: AppHandle) -> Result<UpdateStatus, Error> {
    Ok(imp::status(&app))
}

/// Checks frostdev.io (then GitHub) now. Nexus build: the disabled status, nothing is asked.
#[tauri::command]
pub async fn update_check(app: AppHandle) -> Result<UpdateStatus, Error> {
    Ok(imp::check(&app).await)
}

/// Downloads, verifies and (AppImage) installs the update; the task's result is the status.
#[tauri::command]
pub async fn update_install(app: AppHandle) -> Result<TaskId, Error> {
    imp::install(&app)
}

/// "Restart now" into the update that is ready. Returns only on failure.
#[tauri::command]
pub async fn update_restart(app: AppHandle) -> Result<(), Error> {
    imp::restart(&app)
}

/// Skips `version` (null: un-skips).
#[tauri::command]
pub async fn update_skip(app: AppHandle, version: Option<String>) -> Result<UpdateStatus, Error> {
    Ok(imp::skip(&app, version))
}

/// The user has seen the "updated" notice (clears `justUpdated`).
#[tauri::command]
pub async fn update_ack(app: AppHandle) -> Result<UpdateStatus, Error> {
    Ok(imp::ack(&app))
}

/// Direct builds: the self-update itself.
#[cfg(feature = "self-update")]
mod imp {
    use crate::selfupdate;
    pub use crate::selfupdate::UpdateStatus;
    use kiln_manage::Error;
    use tauri::AppHandle;

    pub fn status(_: &AppHandle) -> UpdateStatus {
        selfupdate::status()
    }

    pub async fn check(app: &AppHandle) -> UpdateStatus {
        selfupdate::check(app, true).await
    }

    pub fn install(app: &AppHandle) -> Result<String, Error> {
        selfupdate::install(app)
    }

    pub fn restart(app: &AppHandle) -> Result<(), Error> {
        selfupdate::restart(app)
    }

    pub fn skip(app: &AppHandle, version: Option<String>) -> UpdateStatus {
        selfupdate::skip(app, version)
    }

    pub fn ack(app: &AppHandle) -> UpdateStatus {
        selfupdate::ack(app)
    }
}

/// The Nexus Mods build: no self-update. The status has the direct build's shape (the frontend's `UpdateStatus`), with
/// phase `disabled`; nothing here reads the network or the update settings.
#[cfg(not(feature = "self-update"))]
mod imp {
    use crate::distribution::{self, Distribution};
    use kiln_manage::{Error, ErrorCode};
    use serde::Serialize;
    use tauri::{AppHandle, Runtime};

    const NO_SELF_UPDATE: &str = "This build comes from Nexus Mods and does not update itself: new versions are on the Kiln page there.";

    #[derive(Debug, Clone, Serialize)]
    #[serde(rename_all = "camelCase")]
    pub struct Install {
        pub kind: &'static str,
        pub in_place: bool,
        pub reason: Option<&'static str>,
    }

    /// `UpdateStatus` of the Nexus build (the fields of crate::selfupdate's, which this build does not compile).
    #[derive(Debug, Clone, Serialize)]
    #[serde(rename_all = "camelCase")]
    pub struct UpdateStatus {
        pub current: String,
        pub distribution: Distribution,
        /// The build's own channel (a pre-release is dev); nothing follows it here.
        pub channel: &'static str,
        pub auto: bool,
        pub install: Install,
        /// Where new versions are: the Kiln page on Nexus Mods.
        pub page: &'static str,
        /// Always `disabled`.
        pub phase: &'static str,
        pub checked: Option<String>,
        pub available: Option<()>,
        pub failures: Vec<()>,
        pub progress: Option<()>,
        pub ready: Option<()>,
        pub skipped: Option<String>,
        pub just_updated: Option<()>,
        pub task_id: Option<String>,
        pub error: Option<Error>,
    }

    /// The bundle this binary was built into (Tauri patches it into each package's binary), for the record.
    fn bundle_kind() -> &'static str {
        use tauri::utils::config::BundleType as B;
        match tauri::utils::platform::bundle_type() {
            Some(B::AppImage) => "appimage",
            Some(B::Deb) => "deb",
            Some(B::Rpm) => "rpm",
            Some(B::Nsis) => "nsis",
            Some(B::Msi) => "msi",
            _ => "unbundled",
        }
    }

    pub fn disabled(current: &str) -> UpdateStatus {
        let pre = kiln_manage::semver::SemVersion::parse(current).map(|v| v.is_prerelease()).unwrap_or(true);
        UpdateStatus {
            current: current.into(),
            distribution: Distribution::Nexus,
            channel: if pre { "dev" } else { "stable" },
            auto: false,
            install: Install { kind: bundle_kind(), in_place: false, reason: Some(NO_SELF_UPDATE) },
            page: distribution::NEXUS_PAGE,
            phase: "disabled",
            checked: None,
            available: None,
            failures: vec![],
            progress: None,
            ready: None,
            skipped: None,
            just_updated: None,
            task_id: None,
            error: None,
        }
    }

    pub fn status<R: Runtime>(app: &AppHandle<R>) -> UpdateStatus {
        disabled(&app.package_info().version.to_string())
    }

    pub async fn check<R: Runtime>(app: &AppHandle<R>) -> UpdateStatus {
        status(app)
    }

    fn refuse() -> Error {
        Error::new(ErrorCode::Unsupported, NO_SELF_UPDATE).with_detail(distribution::NEXUS_PAGE)
    }

    pub fn install<R: Runtime>(_: &AppHandle<R>) -> Result<String, Error> {
        Err(refuse())
    }

    pub fn restart<R: Runtime>(_: &AppHandle<R>) -> Result<(), Error> {
        Err(refuse())
    }

    pub fn skip<R: Runtime>(app: &AppHandle<R>, _version: Option<String>) -> UpdateStatus {
        status(app)
    }

    pub fn ack<R: Runtime>(app: &AppHandle<R>) -> UpdateStatus {
        status(app)
    }

    #[cfg(test)]
    mod tests {
        use super::*;

        #[test]
        fn every_command_reports_disabled_and_nothing_installs() {
            let app = tauri::test::mock_app();
            let h = app.handle();
            let current = h.package_info().version.to_string();
            let s = serde_json::to_value(status(h)).unwrap();
            assert_eq!(s["phase"], "disabled");
            assert_eq!(s["distribution"], "nexus");
            assert_eq!(s["page"], distribution::NEXUS_PAGE);
            assert_eq!(s["current"], current.as_str());
            assert_eq!(s["auto"], false);
            assert_eq!(s["install"]["inPlace"], false);
            assert_eq!(s["install"]["kind"], "unbundled");
            assert!(s["available"].is_null() && s["ready"].is_null() && s["justUpdated"].is_null());
            assert_eq!(s["failures"], serde_json::json!([]));
            // the direct build's fields, so the frontend's UpdateStatus fits
            for k in ["channel", "checked", "progress", "skipped", "taskId", "error"] {
                assert!(s.get(k).is_some(), "{k} missing");
            }
            let c = serde_json::to_value(tauri::async_runtime::block_on(check(h))).unwrap();
            assert_eq!(c, s);
            assert_eq!(serde_json::to_value(skip(h, Some("9.9.9".into()))).unwrap(), s);
            assert_eq!(serde_json::to_value(ack(h)).unwrap(), s);
            let e = install(h).unwrap_err();
            assert_eq!(e.code, ErrorCode::Unsupported);
            assert_eq!(restart(h).unwrap_err().code, ErrorCode::Unsupported);
        }

        #[test]
        fn channel_follows_the_version() {
            assert_eq!(disabled("0.2.0-dev.15").channel, "dev");
            assert_eq!(disabled("0.2.0").channel, "stable");
        }
    }
}
