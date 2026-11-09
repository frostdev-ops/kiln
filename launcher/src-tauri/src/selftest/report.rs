//! The JSON contract emitted by the headless selftest and consumed by release checks.
use serde::Serialize;

#[derive(Serialize)]
pub(super) struct Check {
    pub(super) name: &'static str,
    pub(super) ok: bool,
    pub(super) ms: u128,
    pub(super) detail: String,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub(super) struct Report {
    pub(super) format: u32,
    pub(super) ok: bool,
    pub(super) launcher: &'static str,
    pub(super) kiln: &'static str,
    /// "direct" (updates itself) or "nexus" (no self-update): crate::distribution.
    pub(super) distribution: &'static str,
    pub(super) build_marker: &'static str,
    pub(super) release_build: bool,
    #[serde(rename = "nexusOAuth")]
    pub(super) nexus_oauth: NexusOAuth,
    pub(super) platform: &'static str,
    pub(super) arch: &'static str,
    pub(super) temp: String,
    pub(super) passed: usize,
    pub(super) failed: usize,
    pub(super) checks: Vec<Check>,
}

/// Public registration metadata only; no account, token store or network is accessed by selftest.
#[derive(Serialize, Default)]
#[serde(rename_all = "camelCase")]
pub(super) struct NexusOAuth {
    pub(super) enabled: bool,
    pub(super) configured: bool,
    pub(super) client_id: Option<String>,
    pub(super) redirect_uri: Option<String>,
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn serialized_report_uses_the_release_checker_oauth_contract() {
        let report = Report {
            format: 1, ok: true, launcher: "unit-test", kiln: "unit-test", distribution: "nexus",
            build_marker: "KILN-LAUNCHER-DISTRIBUTION:nexus:self-update=0", release_build: false,
            nexus_oauth: NexusOAuth::default(), platform: "unit-test", arch: "unit-test", temp: String::new(),
            passed: 0, failed: 0, checks: vec![],
        };
        let value = serde_json::to_value(&report).unwrap();
        assert_eq!(value["releaseBuild"], false);
        assert_eq!(value.get("nexusOAuth"), Some(&serde_json::json!({"enabled": false, "configured": false, "clientId": null, "redirectUri": null})));
        assert!(value.get("nexusOauth").is_none());
    }
}
