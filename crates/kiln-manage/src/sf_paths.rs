//! Where ShroudForge's folders and files are, and whether kiln-shroudforge hosts ShroudForge for drop-ins, read the way
//! the adapter (`native/plugins/shroudforge`) and ShroudForge read them: as Windows programs, under Wine or Proton on
//! Linux. Every launcher question about ShroudForge's mods folder goes through [`sf_directory`]
//! (`crate::shroudforge::mods_dir`), so the folder Kiln copies the profile's EML mods into is the folder the hosting
//! decision looked at.
//!
//! The adapter reads `shroudforge/config/modloader-config.json`, `state.json` and each package's `extended.mod.json` with
//! ifstream and nlohmann::json (plugin.cpp `read_text` and `legacy_wanted`, processes.cpp `read_config` and
//! `sf_directory`): links are followed, there is no size limit, file and folder names are found without regard to case
//! (Windows), `paths.<name>` is taken exactly as written (a Windows path, absolute or relative to the game folder), and a
//! value of a type it does not expect makes it fall back (`sf_directory`) or answer what its `catch` says
//! (`legacy_wanted`). [`scan`] keeps only the few fields these answers need and skips everything else while parsing, so a
//! file of any size gives the adapter's answer with little memory and no copy of the document. Where the two can still
//! differ:
//! - only regular files are read (a link is followed to its target, which must be a file): a link to a device or a pipe
//!   would block the launcher. The launcher answers as for a missing file.
//! - JSON nested deeper than 128 levels is invalid for the launcher (serde_json's limit), valid for nlohmann.
//! - a Windows folder this system cannot name (a drive the game's Wine prefix does not map and the game's own path does
//!   not lead to, a network path, a drive-relative `C:x`): the hosting answer is then "hosted" (the copies Kiln places
//!   get ShroudForge's switch, which kiln-eml ignores if it runs them instead), and `mods_dir` falls back to
//!   `<game>/mods`.
//! - a root-relative `\x` is taken on the drive whose folder holds the game folder (the longest such mapping), which may
//!   not be the drive of the executable's Windows path when two drives map the same folder.

use serde::de::{DeserializeSeed, Deserializer, IgnoredAny, MapAccess, SeqAccess, Visitor};
use std::io::{BufRead, BufReader, Read};
use std::path::{Path, PathBuf};

/// A JSON value as far as [`scan`] keeps it: strings and booleans, objects with the wanted members, anything else
/// [`Field::Other`].
#[derive(Debug, Clone, PartialEq)]
pub(crate) enum Field {
    Str(String),
    Bool(bool),
    /// The wanted members in file order (a repeated key: the last one counts, as in nlohmann).
    Obj(Vec<(String, Field)>),
    Other,
}

impl Field {
    /// The member `key` of an object (the last of repeated ones); None for a missing member or a value that is no object.
    pub(crate) fn get(&self, key: &str) -> Option<&Field> {
        match self {
            Field::Obj(m) => m.iter().rev().find(|(k, _)| k == key).map(|(_, v)| v),
            _ => None,
        }
    }
}

#[derive(Clone, Copy)]
struct Seed<'a> {
    keep: &'a [&'a str],
    inner: &'a [&'a str],
}

impl<'de> DeserializeSeed<'de> for Seed<'_> {
    type Value = Field;
    fn deserialize<D: Deserializer<'de>>(self, d: D) -> Result<Field, D::Error> {
        d.deserialize_any(self)
    }
}

impl<'de> Visitor<'de> for Seed<'_> {
    type Value = Field;
    fn expecting(&self, f: &mut std::fmt::Formatter) -> std::fmt::Result {
        f.write_str("a JSON value")
    }
    fn visit_bool<E>(self, b: bool) -> Result<Field, E> { Ok(Field::Bool(b)) }
    fn visit_str<E>(self, s: &str) -> Result<Field, E> { Ok(Field::Str(s.to_owned())) }
    fn visit_string<E>(self, s: String) -> Result<Field, E> { Ok(Field::Str(s)) }
    fn visit_i64<E>(self, _: i64) -> Result<Field, E> { Ok(Field::Other) }
    fn visit_u64<E>(self, _: u64) -> Result<Field, E> { Ok(Field::Other) }
    fn visit_f64<E>(self, _: f64) -> Result<Field, E> { Ok(Field::Other) }
    fn visit_unit<E>(self) -> Result<Field, E> { Ok(Field::Other) }
    fn visit_none<E>(self) -> Result<Field, E> { Ok(Field::Other) }
    fn visit_seq<A: SeqAccess<'de>>(self, mut a: A) -> Result<Field, A::Error> {
        while a.next_element::<IgnoredAny>()?.is_some() {}
        Ok(Field::Other)
    }
    fn visit_map<A: MapAccess<'de>>(self, mut a: A) -> Result<Field, A::Error> {
        let mut out = Vec::new();
        while let Some(k) = a.next_key::<String>()? {
            if self.keep.contains(&k.as_str()) {
                let v = a.next_value_seed(Seed { keep: self.inner, inner: &[] })?;
                out.push((k, v));
            } else {
                a.next_value::<IgnoredAny>()?;
            }
        }
        Ok(Field::Obj(out))
    }
}

/// How a file read for the adapter's answer turned out.
#[derive(Debug, PartialEq)]
pub(crate) enum Scan {
    /// No regular file there (missing, a folder, a device or pipe behind a link).
    NoFile,
    /// A regular file that cannot be opened or read (ifstream fails; `read_text` gives "").
    Unreadable,
    /// Not valid JSON (nlohmann throws).
    Invalid,
    Doc(Field),
}

/// The JSON document `file`, keeping the members `keep` of the top object and `inner` of those that are objects;
/// everything else is skipped as it is parsed. A UTF-8 byte order mark is skipped, as nlohmann does.
pub(crate) fn scan(file: &Path, keep: &[&str], inner: &[&str]) -> Scan {
    if !std::fs::metadata(file).is_ok_and(|m| m.is_file()) {
        return Scan::NoFile;
    }
    let Ok(f) = std::fs::File::open(file) else { return Scan::Unreadable };
    let mut r = BufReader::new(f);
    match r.fill_buf() {
        Ok(b) if b.starts_with(b"\xef\xbb\xbf") => r.consume(3),
        Ok(_) => {}
        Err(_) => return Scan::Unreadable,
    }
    scan_reader(r, keep, inner)
}

fn scan_reader(r: impl Read, keep: &[&str], inner: &[&str]) -> Scan {
    let mut de = serde_json::Deserializer::from_reader(r);
    let doc = match (Seed { keep, inner }).deserialize(&mut de) {
        Ok(d) => d,
        Err(e) if e.is_io() => return Scan::Unreadable,
        Err(_) => return Scan::Invalid,
    };
    match de.end() {
        Ok(()) => Scan::Doc(doc),
        Err(e) if e.is_io() => Scan::Unreadable,
        Err(_) => Scan::Invalid,
    }
}

/// `dir`'s entry `name` as Windows finds it: the exact name when it is there, else the first entry whose name is the same
/// but for case, else `dir/name` (not there).
pub(crate) fn ci_child(dir: &Path, name: &str) -> PathBuf {
    let exact = dir.join(name);
    if cfg!(windows) || std::fs::symlink_metadata(&exact).is_ok() {
        return exact;
    }
    let low = name.to_lowercase();
    let mut found: Vec<std::ffi::OsString> = std::fs::read_dir(dir).into_iter().flatten().flatten().map(|e| e.file_name())
        .filter(|n| n.to_str().is_some_and(|n| n.to_lowercase() == low)).collect();
    found.sort();
    found.into_iter().next().map(|n| dir.join(n)).unwrap_or(exact)
}

/// [`ci_child`] for each part of a relative path.
pub(crate) fn ci_path(base: &Path, parts: &[&str]) -> PathBuf {
    parts.iter().fold(base.to_path_buf(), |dir, p| ci_child(&dir, p))
}

/// `shroudforge/config/modloader-config.json` of the game folder.
fn config_file(game_dir: &Path) -> PathBuf {
    ci_path(game_dir, &["shroudforge", "config", "modloader-config.json"])
}

/// A folder ShroudForge's config names, as the adapter's `sf_directory(root, name, fallback)` finds it: `paths.<name>`
/// when it is a non-empty string, else `<game>/<fallback>`. None: this system cannot name that folder.
pub(crate) fn sf_directory(game_dir: &Path, name: &str, fallback: &str) -> Option<PathBuf> {
    // read_config: any failure is an empty object; `value` of another type throws and sf_directory falls back
    let configured = match scan(&config_file(game_dir), &["paths"], &["mods", "state"]) {
        Scan::Doc(d) => match d.get("paths").and_then(|p| p.get(name)) {
            Some(Field::Str(s)) if !s.is_empty() => Some(s.clone()),
            _ => None,
        },
        _ => None,
    };
    match configured {
        Some(v) => windows_folder(game_dir, &v),
        None => Some(ci_child(game_dir, fallback)),
    }
}

/// `value` as the game (a Windows process) names the folder: absolute, else joined to the game folder; then found on
/// this system with Windows' rules: `.` and `..` resolved by name and never above the drive's root, case ignored,
/// trailing dots and spaces dropped from a name unless the path is verbatim (`\\?\`).
pub(crate) fn windows_folder(game_dir: &Path, value: &str) -> Option<PathBuf> {
    if cfg!(windows) {
        let p = Path::new(value);
        return Some(if p.is_absolute() { p.to_path_buf() } else { game_dir.join(p) });
    }
    let raw = value.replace('/', "\\");
    let verbatim = raw.starts_with(r"\\?\");
    let plain = crate::winpath::strip_verbatim(&raw).into_owned();
    let b = plain.as_bytes();
    // `ups`: how many `..` the path may climb before it reaches the drive's root, where Windows stays
    let (base, mut ups, rest): (PathBuf, usize, &str) = if b.len() >= 2 && b[0].is_ascii_alphabetic() && b[1] == b':' {
        // `C:\x` is absolute; `C:x` is relative to that drive's current folder, which the launcher cannot know
        if b.len() > 2 && b[2] != b'\\' { return None; }
        match drive_host(game_dir, b[0] as char) {
            Some(d) => (d, 0, &plain[2..]),
            // a drive the prefix does not map (or no prefix found): the part of the path inside the game folder
            None => return through_game_folder(game_dir, &plain),
        }
    } else if plain.starts_with(r"\\") {
        return None; // a network path
    } else if plain.starts_with('\\') {
        (game_drive_root(game_dir)?, 0, &plain[..]) // `\x`: the root of the game's drive
    } else {
        // relative to the game folder: as many `..` as the game folder is deep below its drive's root
        let depth = game_drive_root(game_dir).and_then(|root| {
            let game = std::fs::canonicalize(game_dir).ok()?;
            Some(game.components().count() - root.components().count())
        });
        (game_dir.to_path_buf(), depth.unwrap_or(usize::MAX), &plain[..])
    };
    let mut out = base;
    for c in rest.split('\\').filter(|c| !c.is_empty()) {
        let c = if verbatim || c == "." || c == ".." { c } else { c.trim_end_matches([' ', '.']) };
        match c {
            "" | "." => {}
            ".." => {
                if ups > 0 && out.pop() { ups -= 1; }
            }
            _ => {
                out = ci_child(&out, c);
                ups = ups.saturating_add(1);
            }
        }
    }
    Some(out)
}

/// A drive path the prefix does not map, found through the game folder: the two name the same folders from where the
/// Windows path enters the game's path (`\\?\S:\steamapps\common\Enshrouded\mods` on a Steam Deck).
fn through_game_folder(game_dir: &Path, plain: &str) -> Option<PathBuf> {
    let win: Vec<&str> = plain.split('\\').filter(|c| !c.is_empty()).skip(1).collect();
    let game: Vec<String> = game_dir.components().filter_map(|c| match c {
        std::path::Component::Normal(n) => Some(n.to_string_lossy().into_owned()),
        _ => None,
    }).collect();
    (1..=win.len().min(game.len())).rev()
        .find(|&k| win[..k].iter().zip(&game[game.len() - k..]).all(|(a, b)| a.eq_ignore_ascii_case(b)))
        .map(|k| win[k..].iter().fold(game_dir.to_path_buf(), |dir, c| ci_child(&dir, c)))
}

/// The Wine prefix of the game in `game_dir`: kilnctl's `<game>.pfx`, else Steam's `compatdata/<app>/pfx` beside the
/// library the game is in, for the app of this folder (the server app when only `enshrouded_server.exe` is there, the
/// client app when only `enshrouded.exe` is, as `game::from_steam` tells them apart). When the folder holds both or
/// neither, no Steam prefix is taken: a wrong prefix maps drive letters to another app's folders, while no prefix
/// leaves only Z: and paths through the game folder, and every other drive path unnamed (see the module's note).
pub(crate) fn wine_prefix(game_dir: &Path) -> Option<PathBuf> {
    let mut own = game_dir.as_os_str().to_owned();
    own.push(".pfx");
    let own = PathBuf::from(own);
    if own.join("dosdevices").is_dir() {
        return Some(own);
    }
    let has = |exe: &str| std::fs::metadata(ci_child(game_dir, exe)).is_ok_and(|m| m.is_file());
    let app = match (has(crate::game::CLIENT_EXE), has(crate::game::SERVER_EXE)) {
        (true, false) => crate::CLIENT_APP_ID,
        (false, true) => crate::SERVER_APP_ID,
        _ => return None,
    };
    let steamapps = game_dir.parent().and_then(Path::parent)?;
    Some(steamapps.join("compatdata").join(app.to_string()).join("pfx")).filter(|p| p.join("dosdevices").is_dir())
}

/// Where the drive `letter` of the game's prefix is on this system.
fn drive_host(game_dir: &Path, letter: char) -> Option<PathBuf> {
    crate::winpath::drive_target(wine_prefix(game_dir).as_deref(), letter)
}

/// The root of the drive the game folder is on, as Wine maps it (the drive whose folder holds the game folder, the
/// longest such one; Z: is `/`).
fn game_drive_root(game_dir: &Path) -> Option<PathBuf> {
    let game = std::fs::canonicalize(game_dir).unwrap_or_else(|_| game_dir.to_path_buf());
    let pfx = wine_prefix(game_dir);
    ('a'..='z').filter_map(|l| crate::winpath::drive_target(pfx.as_deref(), l))
        .filter_map(|t| std::fs::canonicalize(&t).ok().filter(|t| game.starts_with(t)))
        .max_by_key(|t| t.components().count())
}

/// The adapter's `legacy_wanted` (plugin.cpp), which decides with drop-ins on: true when ShroudForge's mods folder holds
/// a package whose `extended.mod.json` it counts, or ShroudForge's state file records a startup patch (`assets.status`
/// applied, preparing or failed).
pub(crate) fn adapter_hosts_dropins(game_dir: &Path) -> bool {
    let Some(mods) = sf_directory(game_dir, "mods", "mods") else { return true };
    for e in std::fs::read_dir(&mods).into_iter().flatten().flatten() {
        let ext = ci_child(&e.path(), "extended.mod.json");
        // `is_regular_file(extended)`: no file, no package
        if !std::fs::metadata(&ext).is_ok_and(|m| m.is_file()) { continue; }
        // `if (j.value("enabled", true) && j.value("launcher", "") != "EML") return true;` inside `catch (...) { return
        // true; }`: an unreadable or invalid file, a document that is no object, and a value of another type all count;
        // `enabled: false` stops there, before `launcher` is read
        let Scan::Doc(d @ Field::Obj(_)) = scan(&ext, &["enabled", "launcher"], &[]) else { return true };
        let enabled = match d.get("enabled") {
            None => true,
            Some(Field::Bool(b)) => *b,
            Some(_) => return true,
        };
        if !enabled { continue; }
        match d.get("launcher") {
            Some(Field::Str(s)) if s == "EML" => {}
            _ => return true,
        }
    }
    // `state.find("assets")`, then `it->value("status", "")` inside the catch: an assets of another type, or a status
    // that is no string, is "no"
    let Some(state) = sf_directory(game_dir, "state", "shroudforge") else { return true };
    match scan(&ci_child(&state, "state.json"), &["assets"], &["status"]) {
        Scan::Doc(d) => matches!(d.get("assets").and_then(|a| a.get("status")),
            Some(Field::Str(s)) if matches!(s.as_str(), "applied" | "preparing" | "failed")),
        _ => false,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn scan_keeps_only_the_wanted_members_and_the_last_of_repeated_ones() {
        let doc = |t: &str| scan_reader(t.as_bytes(), &["paths"], &["state"]);
        assert_eq!(doc(r#"{"x":[1,2,{"y":3}],"paths":{"state":"a","mods":"b"},"paths":{"state":"c"}}"#),
            Scan::Doc(Field::Obj(vec![("paths".into(), Field::Obj(vec![("state".into(), Field::Str("a".into()))])),
                ("paths".into(), Field::Obj(vec![("state".into(), Field::Str("c".into()))]))])));
        assert_eq!(match doc(r#"{"paths":{"state":"a"},"paths":{"state":"c"}}"#) { Scan::Doc(d) => d.get("paths").and_then(|p| p.get("state")).cloned(), s => panic!("{s:?}") },
            Some(Field::Str("c".into())));
        assert_eq!(doc(r#"{"paths":{}} x"#), Scan::Invalid, "trailing text");
        assert_eq!(doc(r#"[1,2]"#), Scan::Doc(Field::Other));
    }
}
