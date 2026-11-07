//! "Mod Manager Download" links: `nxm://<game>/mods/<mod>/files/<file>?key=<key>&expires=<unix>&user_id=<id>`.
//! The site makes one when a user clicks Mod Manager Download; `key` + `expires` let a free account download that one
//! file through the API. Collections (`nxm://<game>/collections/...`) are not mod files: refused here.
//!
//! The key in a link is a short-lived download key, not the user's API key; it is still never logged: [`NxmLink`]'s
//! `Display` and [`NxmLink::redacted`] leave the query out.

use crate::{Error, ErrorCode, Result};
use serde::{Deserialize, Serialize};

#[derive(Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct NxmLink {
    /// The game's domain on Nexus Mods, lower case ("enshrouded").
    pub game: String,
    /// The game-scoped mod id (the number in the page URL).
    pub mod_id: String,
    /// The game-scoped file (version) id.
    pub file_id: String,
    #[serde(skip_serializing)]
    pub key: Option<String>,
    #[serde(skip_serializing)]
    pub expires: Option<i64>,
}

impl std::fmt::Debug for NxmLink {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(f, "NxmLink({})", self.redacted())
    }
}

impl std::fmt::Display for NxmLink {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str(&self.redacted())
    }
}

impl NxmLink {
    /// The link without its query (safe for logs).
    pub fn redacted(&self) -> String {
        format!("nxm://{}/mods/{}/files/{}", self.game, self.mod_id, self.file_id)
    }

    /// The key has expired (with a minute's slack for clocks).
    pub fn expired(&self, now_unix: i64) -> bool {
        self.expires.is_some_and(|e| e + 60 < now_unix)
    }
}

fn pct_decode(s: &str) -> String {
    let b = s.as_bytes();
    let mut out = Vec::with_capacity(b.len());
    let mut i = 0;
    while i < b.len() {
        if b[i] == b'%' && i + 2 < b.len()
            && let Some(v) = std::str::from_utf8(&b[i + 1..i + 3]).ok().and_then(|h| u8::from_str_radix(h, 16).ok())
        {
            out.push(v);
            i += 3;
            continue;
        }
        out.push(if b[i] == b'+' { b' ' } else { b[i] });
        i += 1;
    }
    String::from_utf8_lossy(&out).into_owned()
}

/// The game domain of any nxm link (also collections), lower case; None when it is not an nxm link.
pub fn game_of(url: &str) -> Option<String> {
    let rest = strip_scheme(url.trim())?;
    let game = rest.split(['/', '?']).next()?.to_ascii_lowercase();
    (!game.is_empty()).then_some(game)
}

fn strip_scheme(url: &str) -> Option<&str> {
    let (scheme, rest) = url.split_once("://")?;
    scheme.eq_ignore_ascii_case("nxm").then_some(rest)
}

/// Parses a mod file link. Refuses anything else (collections, other shapes, ids that are not numbers).
pub fn parse(url: &str) -> Result<NxmLink> {
    let bad = |why: &str| Error::new(ErrorCode::InvalidArgument, format!("not a Mod Manager Download link: {why}"));
    let rest = strip_scheme(url.trim()).ok_or_else(|| bad("it does not start with nxm://"))?;
    let (path, query) = rest.split_once('?').unwrap_or((rest, ""));
    let parts: Vec<&str> = path.trim_end_matches('/').split('/').collect();
    let digits = |s: &str| !s.is_empty() && s.len() <= 20 && s.bytes().all(|b| b.is_ascii_digit());
    let link = match parts.as_slice() {
        [game, m, mod_id, f, file_id] if m.eq_ignore_ascii_case("mods") && f.eq_ignore_ascii_case("files") && digits(mod_id) && digits(file_id) => NxmLink {
            game: game.to_ascii_lowercase(),
            mod_id: mod_id.to_string(),
            file_id: file_id.to_string(),
            key: None,
            expires: None,
        },
        [_, c, ..] if c.eq_ignore_ascii_case("collections") => return Err(bad("collections are not supported")),
        _ => return Err(bad("expected nxm://<game>/mods/<id>/files/<id>")),
    };
    if link.game.is_empty() || !link.game.bytes().all(|b| b.is_ascii_alphanumeric() || b == b'-' || b == b'_') {
        return Err(bad("the game name"));
    }
    let mut link = link;
    for pair in query.split('&').filter(|p| !p.is_empty()) {
        let (k, v) = pair.split_once('=').unwrap_or((pair, ""));
        match k {
            "key" => link.key = Some(pct_decode(v)).filter(|k| !k.is_empty() && k.len() <= 256),
            "expires" => link.expires = v.parse().ok(),
            _ => {}
        }
    }
    Ok(link)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn mod_file_links() {
        let l = parse("nxm://Enshrouded/mods/306/files/1234?key=abc%2Bdef&expires=1794030767&user_id=42").unwrap();
        assert_eq!((l.game.as_str(), l.mod_id.as_str(), l.file_id.as_str()), ("enshrouded", "306", "1234"));
        assert_eq!(l.key.as_deref(), Some("abc+def"));
        assert_eq!(l.expires, Some(1794030767));
        assert_eq!(l.redacted(), "nxm://enshrouded/mods/306/files/1234");
        assert!(!format!("{l:?} {l}").contains("abc"), "the key never shows in Debug or Display");
        let json = serde_json::to_string(&l).unwrap();
        assert!(!json.contains("abc") && !json.contains("expires"), "{json}");
        assert!(l.expired(1794030767 + 61));
        assert!(!l.expired(1794030767));
        // premium: no key at all
        let p = parse("nxm://enshrouded/mods/301/files/9").unwrap();
        assert_eq!((p.key, p.expires), (None, None));
    }

    #[test]
    fn other_shapes_are_refused() {
        assert!(parse("https://www.nexusmods.com/enshrouded/mods/1").is_err());
        assert!(parse("nxm://enshrouded/collections/abcd/revisions/3").unwrap_err().message.contains("collections"));
        assert!(parse("nxm://enshrouded/mods/x/files/1").is_err());
        assert!(parse("nxm://enshr%20ouded/mods/1/files/1").is_err());
        assert!(parse("nxm://enshrouded/mods/1/files/1/../2").is_err());
        assert_eq!(game_of("nxm://SkyrimSpecialEdition/mods/1/files/2?key=x").as_deref(), Some("skyrimspecialedition"));
        assert_eq!(game_of("nxm://stardewvalley/collections/x/revisions/1").as_deref(), Some("stardewvalley"));
        assert_eq!(game_of("https://x"), None);
    }
}
