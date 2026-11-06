//! Where launcher updates come from and which ones count: the two sources in order (frostdev.io, then GitHub
//! Releases), the channels (stable, dev), the version rule, and the Kiln fields of an update manifest.
//!
//! A manifest (`latest.json`) is Tauri's static updater format plus optional fields the updater ignores:
//! `platforms.<target>.{sha256, size}` and a `kiln` object (`format`, `channel`, `page`, `runtime`). Formats and
//! the release layout: docs/kiln/launcher/releasing.md.

use kiln_manage::semver::SemVersion;
use serde::{Deserialize, Serialize};
use std::cmp::Ordering;

/// The page that offers every build, for installs the launcher cannot update itself.
pub const DOWNLOAD_PAGE: &str = "https://frostdev.io/kiln";

const FROSTDEV: &str = "https://frostdev.io/kiln/updates/{channel}/latest.json";
/// GitHub's "latest" release is the newest stable one (pre-releases and drafts are skipped)...
const GITHUB_STABLE: &str = "https://github.com/frostdev-ops/kiln/releases/latest/download/latest.json";
/// ...so the dev channel has a fixed pre-release, `updates-dev`, whose `latest.json` each dev release replaces.
const GITHUB_DEV: &str = "https://github.com/frostdev-ops/kiln/releases/download/updates-dev/latest.json";

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum Channel {
    Stable,
    Dev,
}

impl Channel {
    pub fn parse(s: &str) -> Option<Channel> {
        match s {
            "stable" => Some(Channel::Stable),
            "dev" => Some(Channel::Dev),
            _ => None,
        }
    }

    pub fn as_str(self) -> &'static str {
        match self {
            Channel::Stable => "stable",
            Channel::Dev => "dev",
        }
    }

    /// The channel a build belongs to: a pre-release (`0.2.0-dev.14`, a local `0.2.0-dev`) is dev.
    pub fn of_build(version: &str) -> Channel {
        match SemVersion::parse(version) {
            Ok(v) if !v.is_prerelease() => Channel::Stable,
            _ => Channel::Dev,
        }
    }

    /// The setting (`updateChannel`), else the build's own channel.
    pub fn effective(setting: Option<&str>, version: &str) -> Channel {
        setting.and_then(Channel::parse).unwrap_or_else(|| Channel::of_build(version))
    }
}

/// One place updates come from. `stable` and `dev` are the manifest URLs of the two channels.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Source {
    pub name: String,
    pub stable: String,
    pub dev: String,
}

impl Source {
    pub fn url(&self, channel: Channel) -> &str {
        match channel {
            Channel::Stable => &self.stable,
            Channel::Dev => &self.dev,
        }
    }
}

/// The sources in the order they are tried. Release builds always use frostdev.io, then GitHub. Debug builds and
/// builds with the `update-test-feeds` feature take `KILN_UPDATE_FEEDS` instead, when every URL in it is on the
/// loopback interface (tests run local feeds).
pub fn sources() -> Vec<Source> {
    test_sources().unwrap_or_else(release_sources)
}

pub fn release_sources() -> Vec<Source> {
    vec![
        Source { name: "frostdev.io".into(), stable: FROSTDEV.replace("{channel}", "stable"), dev: FROSTDEV.replace("{channel}", "dev") },
        Source { name: "GitHub".into(), stable: GITHUB_STABLE.into(), dev: GITHUB_DEV.into() },
    ]
}

#[cfg(any(debug_assertions, feature = "update-test-feeds"))]
fn test_sources() -> Option<Vec<Source>> {
    let v = std::env::var("KILN_UPDATE_FEEDS").ok().filter(|v| !v.trim().is_empty())?;
    match parse_test_feeds(&v) {
        Ok(s) => Some(s),
        Err(e) => {
            eprintln!("KILN_UPDATE_FEEDS ignored: {e}");
            None
        }
    }
}

#[cfg(not(any(debug_assertions, feature = "update-test-feeds")))]
fn test_sources() -> Option<Vec<Source>> {
    None
}

/// `name=url;name=url`, each URL with `{channel}` where the channel goes. Only `http(s)://127.0.0.1`, `localhost`
/// or `[::1]` URLs are taken.
pub fn parse_test_feeds(text: &str) -> Result<Vec<Source>, String> {
    let mut out = Vec::new();
    for part in text.split(';').map(str::trim).filter(|p| !p.is_empty()) {
        let (name, url) = part.split_once('=').ok_or_else(|| format!("{part}: expected name=url"))?;
        if !is_loopback_url(url) {
            return Err(format!("{url}: only loopback URLs are allowed"));
        }
        out.push(Source { name: name.trim().into(), stable: url.replace("{channel}", "stable"), dev: url.replace("{channel}", "dev") });
    }
    if out.is_empty() {
        return Err("no feeds".into());
    }
    Ok(out)
}

fn is_loopback_url(url: &str) -> bool {
    let Ok(u) = tauri::Url::parse(url) else { return false };
    if !matches!(u.scheme(), "http" | "https") {
        return false;
    }
    let Some(host) = u.host_str() else { return false };
    host.eq_ignore_ascii_case("localhost")
        || host.trim_start_matches('[').trim_end_matches(']').parse::<std::net::IpAddr>().is_ok_and(|ip| ip.is_loopback())
}

/// Is `remote` an update for a launcher at `current` on `channel`? Newer by semver precedence (build metadata
/// ignored: `0.2.0-dev.9 < 0.2.0-dev.10 < 0.2.0`), and on stable never a pre-release. Unparsable versions never are.
pub fn accepts(current: &str, remote: &str, channel: Channel) -> bool {
    let (Ok(cur), Ok(rem)) = (SemVersion::parse(current), SemVersion::parse(remote)) else { return false };
    if channel == Channel::Stable && rem.is_prerelease() {
        return false;
    }
    rem.compare(&cur) == Ordering::Greater
}

/// The Kiln fields of a manifest (all optional).
#[derive(Debug, Clone, Default, PartialEq, Eq, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct KilnFields {
    #[serde(default)]
    pub format: Option<u32>,
    #[serde(default)]
    pub channel: Option<String>,
    /// The download page for this release.
    #[serde(default)]
    pub page: Option<String>,
    /// The Kiln runtime the launcher ships.
    #[serde(default)]
    pub runtime: Option<String>,
}

/// `kiln` from a manifest; a newer `format` than 1 is read as far as it goes.
pub fn kiln_fields(raw: &serde_json::Value) -> KilnFields {
    raw.get("kiln").and_then(|k| serde_json::from_value(k.clone()).ok()).unwrap_or_default()
}

/// The sha256 a manifest lists for the platform entry with this download URL.
pub fn sha256_for(raw: &serde_json::Value, url: &str) -> Option<String> {
    raw.get("platforms")?
        .as_object()?
        .values()
        .find(|p| p.get("url").and_then(|u| u.as_str()) == Some(url))?
        .get("sha256")?
        .as_str()
        .map(|s| s.to_ascii_lowercase())
}

/// Release notes for the notice: the first paragraphs, at most `max` characters, cut at a word.
pub fn short_notes(notes: &str, max: usize) -> String {
    let t = notes.trim();
    if t.chars().count() <= max {
        return t.to_string();
    }
    let cut: String = t.chars().take(max).collect();
    let cut = cut.rsplit_once(char::is_whitespace).map(|(a, _)| a).unwrap_or(&cut);
    format!("{}…", cut.trim_end_matches([',', ';', ':', '.', ' ']))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn sources_are_frostdev_then_github() {
        let s = release_sources();
        assert_eq!(s.iter().map(|s| s.name.as_str()).collect::<Vec<_>>(), ["frostdev.io", "GitHub"]);
        assert_eq!(s[0].url(Channel::Stable), "https://frostdev.io/kiln/updates/stable/latest.json");
        assert_eq!(s[0].url(Channel::Dev), "https://frostdev.io/kiln/updates/dev/latest.json");
        assert_eq!(s[1].url(Channel::Stable), "https://github.com/frostdev-ops/kiln/releases/latest/download/latest.json");
        assert_eq!(s[1].url(Channel::Dev), "https://github.com/frostdev-ops/kiln/releases/download/updates-dev/latest.json");
        // every release URL is https and carries nothing about the user (no query, no version, no id)
        for src in &s {
            for ch in [Channel::Stable, Channel::Dev] {
                let u = tauri::Url::parse(src.url(ch)).unwrap();
                assert_eq!(u.scheme(), "https");
                assert!(u.query().is_none());
            }
        }
    }

    #[test]
    fn test_feeds_must_be_loopback() {
        let s = parse_test_feeds("frostdev.io=http://127.0.0.1:26721/u/{channel}/latest.json; GitHub=http://localhost:26722/{channel}.json").unwrap();
        assert_eq!(s[0].name, "frostdev.io");
        assert_eq!(s[0].url(Channel::Dev), "http://127.0.0.1:26721/u/dev/latest.json");
        assert_eq!(s[1].url(Channel::Stable), "http://localhost:26722/stable.json");
        assert!(parse_test_feeds("x=http://[::1]:1/{channel}").is_ok());
        assert!(parse_test_feeds("x=https://frostdev.io/kiln/updates/{channel}/latest.json").is_err());
        assert!(parse_test_feeds("x=http://192.168.1.2/{channel}").is_err());
        assert!(parse_test_feeds("x=file:///tmp/{channel}").is_err());
        assert!(parse_test_feeds("nonsense").is_err());
        assert!(parse_test_feeds(" ; ").is_err());
    }

    #[test]
    fn versions_order_by_semver_and_dev_number() {
        // the dev number counts as a number, not text
        assert!(accepts("0.2.0-dev.9", "0.2.0-dev.10", Channel::Dev));
        assert!(!accepts("0.2.0-dev.10", "0.2.0-dev.9", Channel::Dev));
        assert!(!accepts("0.2.0-dev.10", "0.2.0-dev.10", Channel::Dev));
        // a release is newer than its pre-releases; the next dev cycle is newer than the release
        assert!(accepts("0.2.0-dev.40", "0.2.0", Channel::Dev));
        assert!(accepts("0.2.0", "0.2.1-dev.41", Channel::Dev));
        assert!(!accepts("0.2.1-dev.41", "0.2.0", Channel::Dev));
        // a local build (no dev number) takes any dev release of its version
        assert!(accepts("0.2.0-dev", "0.2.0-dev.1", Channel::Dev));
        // build metadata never decides
        assert!(!accepts("0.2.0-dev.5+20261106.abc1234", "0.2.0-dev.5+20261107.def5678", Channel::Dev));
        assert!(!accepts("0.2.0", "garbage", Channel::Dev));
    }

    #[test]
    fn stable_takes_only_releases() {
        assert!(!accepts("0.2.0", "0.3.0-dev.2", Channel::Stable));
        assert!(accepts("0.2.0", "0.2.1", Channel::Stable));
        assert!(accepts("0.2.0-dev.40", "0.2.0", Channel::Stable));
        assert!(!accepts("0.3.0-dev.2", "0.2.1", Channel::Stable));
    }

    #[test]
    fn channel_defaults_to_the_build() {
        assert_eq!(Channel::of_build("0.2.0"), Channel::Stable);
        assert_eq!(Channel::of_build("0.2.0-dev.3"), Channel::Dev);
        assert_eq!(Channel::of_build("0.2.0-dev"), Channel::Dev);
        assert_eq!(Channel::effective(None, "0.2.0"), Channel::Stable);
        assert_eq!(Channel::effective(None, "0.2.0-dev.3"), Channel::Dev);
        assert_eq!(Channel::effective(Some("stable"), "0.2.0-dev.3"), Channel::Stable);
        assert_eq!(Channel::effective(Some("dev"), "0.2.0"), Channel::Dev);
        assert_eq!(Channel::effective(Some("nightly"), "0.2.0"), Channel::Stable);
    }

    #[test]
    fn manifest_fields() {
        let raw: serde_json::Value = serde_json::from_str(include_str!("testdata/latest.json")).unwrap();
        // the updater itself reads the manifest (its own fields; ours are ignored)
        let rel: tauri_plugin_updater::RemoteRelease = serde_json::from_value(raw.clone()).unwrap();
        assert_eq!(rel.version.to_string(), "0.2.0-dev.42");
        assert!(rel.notes.as_deref().unwrap().starts_with("Self-update"));
        let appimage = "https://frostdev.io/kiln/downloads/0.2.0-dev.42/kiln-launcher_0.2.0-dev.42_amd64.AppImage";
        assert_eq!(rel.download_url("linux-x86_64-appimage").unwrap().as_str(), appimage);
        assert_eq!(rel.download_url("linux-x86_64").unwrap().as_str(), appimage);
        assert!(rel.download_url("windows-x86_64-nsis").unwrap().as_str().ends_with("_x64-setup.exe"));
        let k = kiln_fields(&raw);
        assert_eq!(k.format, Some(1));
        assert_eq!(k.channel.as_deref(), Some("dev"));
        assert_eq!(k.page.as_deref(), Some("https://frostdev.io/kiln"));
        assert_eq!(k.runtime.as_deref(), Some("0.2.0-dev+20261106.0f1ffa6"));
        assert_eq!(sha256_for(&raw, appimage).as_deref(), Some("9f2c6f0a1c3a0b7f0f8c9a6d1e2b3c4d5e6f708192a3b4c5d6e7f8091a2b3c4d"));
        assert_eq!(sha256_for(&raw, "https://example.invalid/x"), None);
        // no kiln object: defaults
        assert_eq!(kiln_fields(&serde_json::json!({"version": "1.0.0"})), KilnFields::default());
    }

    #[test]
    fn notes_are_cut_at_a_word() {
        assert_eq!(short_notes("  short  ", 40), "short");
        assert_eq!(short_notes("one two three four five", 12), "one two…");
    }
}
