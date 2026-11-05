//! `kiln/play-once.ini`: the launcher writes it right before starting the game; the proxy reads it, deletes it at
//! once (before validating), and applies it to that one start. Grammar and outcomes: contracts.md "INI files";
//! reference: schema/fixtures/ini/kini.py; fixtures: schema/fixtures/ini/play-once.

use crate::{Error, Result};
use serde::{Deserialize, Serialize};
use std::path::Path;

pub const MAX_AGE_S: i64 = 600;
pub const MAX_FUTURE_S: i64 = 60;
/// Default `profile=` (relative to `<game>/kiln/`).
pub const DEFAULT_ONCE_PROFILE: &str = "profile.once.json";

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "kebab-case")]
pub enum PlayOnceMode {
    Vanilla,
    NoMods,
    Profile,
}

/// What the launcher writes.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PlayOnce {
    pub mode: PlayOnceMode,
    /// Unix seconds (the launcher writes seconds; the proxy also reads RFC 3339 with a zone).
    pub created: i64,
    pub nonce: String,
    /// mode profile: None = kiln/profile.once.json
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub profile: Option<String>,
}

/// The proxy's decision (fixture "expect.status"; KILN_PLAY_ONCE_* lower-case, '_' -> '-').
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "kebab-case")]
pub enum PlayOnceStatus {
    None,
    Vanilla,
    NoMods,
    Profile,
    IgnoredExpired,
    IgnoredUndeletable,
    IgnoredServer,
    IgnoredInvalid,
}

/// The text of a play-once.ini for `p` (`mode = ...`, `created = ...`, `nonce = ...`, `profile = ...`; CRLF).
pub fn render(p: &PlayOnce) -> String {
    let mut s = String::from("; Kiln play-once flag: read and deleted by the proxy at the next start\r\n");
    s.push_str(&format!("mode = {}\r\n", match p.mode {
        PlayOnceMode::Vanilla => "vanilla",
        PlayOnceMode::NoMods => "no-mods",
        PlayOnceMode::Profile => "profile",
    }));
    s.push_str(&format!("created = {}\r\nnonce = {}\r\n", p.created, p.nonce));
    if let Some(profile) = &p.profile {
        s.push_str(&format!("profile = {profile}\r\n"));
    }
    s
}

/// The proxy's decision with everything the fixtures check (`schema/fixtures/ini/play-once/*.json` "expect").
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Decision {
    pub status: PlayOnceStatus,
    /// Valid flags (applied or expired): what was read; `profile` is normalized ('/' -> '\', default applied).
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub flag: Option<PlayOnce>,
    /// mode profile: the profile path is relative to `<game>/kiln/`.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub profile_relative: Option<bool>,
    /// ignored-invalid: why (informative; the reference parser's texts).
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub error: Option<String>,
}

fn parse_created(text: &str) -> Result<i64, String> {
    if !text.is_empty() && text.len() <= 12 && text.bytes().all(|b| b.is_ascii_digit()) {
        return text.parse().map_err(|_| "created must be unix seconds or RFC 3339 with a zone".to_string());
    }
    crate::util::parse_rfc3339(text).ok_or_else(|| "created must be unix seconds or RFC 3339 with a zone".to_string())
}

/// What the proxy decides (kini.py `play_once`), with the details.
pub fn decide_detail(bytes: &[u8], now: i64, server: bool, deletable: bool) -> Decision {
    let d = |status| Decision { status, flag: None, profile_relative: None, error: None };
    if !deletable {
        return d(PlayOnceStatus::IgnoredUndeletable);
    }
    if server {
        return d(PlayOnceStatus::IgnoredServer);
    }
    let text = crate::chain::decode(bytes);
    let mut keys: std::collections::HashMap<String, String> = std::collections::HashMap::new();
    for l in crate::chain::lines(&text) {
        let crate::chain::IniLine::Text(_, line) = l else { continue };
        let Some((k, v)) = line.split_once('=') else { continue };
        keys.insert(k.trim_matches([' ', '\t']).to_lowercase(), v.trim_matches([' ', '\t']).to_string());
    }
    let invalid = |e: &str| Decision { status: PlayOnceStatus::IgnoredInvalid, flag: None, profile_relative: None, error: Some(e.into()) };
    let mode = match keys.get("mode").map(|m| m.to_lowercase()).as_deref() {
        Some("vanilla") => PlayOnceMode::Vanilla,
        Some("no-mods") => PlayOnceMode::NoMods,
        Some("profile") => PlayOnceMode::Profile,
        _ => return invalid("mode must be one of vanilla, no-mods, profile"),
    };
    let created = match parse_created(keys.get("created").map(String::as_str).unwrap_or("")) {
        Ok(c) => c,
        Err(e) => return invalid(&e),
    };
    let nonce = keys.get("nonce").cloned().unwrap_or_default();
    if nonce.is_empty() {
        return invalid("nonce is required");
    }
    let (profile, profile_relative) = if mode == PlayOnceMode::Profile {
        let raw = keys.get("profile").filter(|p| !p.is_empty()).map(String::as_str).unwrap_or(DEFAULT_ONCE_PROFILE);
        let (p, rel) = crate::chain::game_path(raw);
        (Some(p), Some(rel))
    } else {
        (None, None)
    };
    let flag = PlayOnce { mode, created, nonce, profile };
    if now - created > MAX_AGE_S || created - now > MAX_FUTURE_S {
        return Decision { status: PlayOnceStatus::IgnoredExpired, flag: Some(flag), profile_relative, error: None };
    }
    let status = match mode {
        PlayOnceMode::Vanilla => PlayOnceStatus::Vanilla,
        PlayOnceMode::NoMods => PlayOnceStatus::NoMods,
        PlayOnceMode::Profile => PlayOnceStatus::Profile,
    };
    Decision { status, flag: Some(flag), profile_relative, error: None }
}

/// What the proxy would decide for these bytes at `now` (the reference semantics, for tests and the UI).
pub fn decide(bytes: &[u8], now: i64, server: bool, deletable: bool) -> (PlayOnceStatus, Option<PlayOnce>) {
    let d = decide_detail(bytes, now, server, deletable);
    (d.status, d.flag)
}

/// Writes `<game>/kiln/play-once.ini` atomically.
pub fn write(game_dir: &Path, p: &PlayOnce) -> Result<()> {
    if p.nonce.is_empty() || p.nonce.contains(['\r', '\n']) {
        return Err(Error::invalid("play-once nonce must be one non-empty line"));
    }
    crate::util::write_atomic(&game_dir.join(crate::paths::game::PLAY_ONCE), render(p).as_bytes())
}

/// The pending flag, if the proxy has not consumed it yet (as written, whatever its age; None when the file is
/// missing or does not parse).
pub fn pending(game_dir: &Path) -> Result<Option<PlayOnce>> {
    let path = game_dir.join(crate::paths::game::PLAY_ONCE);
    let bytes = match std::fs::read(&path) {
        Ok(b) => b,
        Err(e) if e.kind() == std::io::ErrorKind::NotFound => return Ok(None),
        Err(e) => return Err(crate::util::io_at(&path, "reading")(e)),
    };
    // decide at the file's own creation time: only the syntax matters here
    let probe = decide_detail(&bytes, i64::MIN / 4, false, true);
    let created = probe.flag.as_ref().map(|f| f.created);
    Ok(match created {
        Some(c) => decide_detail(&bytes, c, false, true).flag,
        None => None,
    })
}

/// Removes a pending flag (the launcher cancels a launch). Ok when there is none.
pub fn clear(game_dir: &Path) -> Result<()> {
    let path = game_dir.join(crate::paths::game::PLAY_ONCE);
    match std::fs::remove_file(&path) {
        Ok(()) => Ok(()),
        Err(e) if e.kind() == std::io::ErrorKind::NotFound => Ok(()),
        Err(e) => Err(crate::util::io_at(&path, "removing")(e)),
    }
}

/// A fresh nonce: 16 random bytes as 32 lower-case hex chars.
pub fn new_nonce() -> Result<String> {
    Ok(crate::hash::hex(&crate::util::random_bytes::<16>()?))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn written_flags_decide_as_written() {
        let d = std::env::temp_dir().join(format!("km-playonce-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&d);
        let now = crate::util::now_unix();
        let p = PlayOnce { mode: PlayOnceMode::Profile, created: now, nonce: new_nonce().unwrap(), profile: None };
        assert_eq!(p.nonce.len(), 32);
        write(&d, &p).unwrap();
        let bytes = std::fs::read(d.join("kiln/play-once.ini")).unwrap();
        let dec = decide_detail(&bytes, now + 5, false, true);
        assert_eq!(dec.status, PlayOnceStatus::Profile);
        assert_eq!(dec.flag.as_ref().unwrap().profile.as_deref(), Some("profile.once.json"));
        assert_eq!(dec.profile_relative, Some(true));
        let pend = pending(&d).unwrap().unwrap();
        assert_eq!((pend.mode, pend.created, &pend.nonce), (p.mode, p.created, &p.nonce));
        assert_eq!(decide(&bytes, now + 601, false, true).0, PlayOnceStatus::IgnoredExpired);
        clear(&d).unwrap();
        assert!(pending(&d).unwrap().is_none());
        assert!(write(&d, &PlayOnce { nonce: "a\nb".into(), ..p }).is_err());
        let _ = std::fs::remove_dir_all(&d);
    }
}
