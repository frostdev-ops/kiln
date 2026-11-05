//! `kiln/chain.ini` ([chain]: proxies the installer displaced; [profile]: native mods with phase dllmain of the
//! active profile) and `kiln/chain/once.ini` (play-once mode=profile reads it instead of [profile]). Grammar:
//! contracts.md "INI files"; reference: schema/fixtures/ini/kini.py; fixtures: schema/fixtures/ini/chain.
//! The installer writes [chain], profile activation writes [profile]; each rewrite keeps the other section's lines
//! as they were and replaces the file atomically.

use crate::Result;
use serde::{Deserialize, Serialize};
use std::path::Path;

pub const MAX_ENTRIES: usize = 256;

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum EntryStatus {
    Ok,
    Refused,
}

/// One DLL line as parsed (fixture shape).
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ChainEntry {
    /// As written, '/' turned into '\'.
    pub path: String,
    /// Relative to `<game>/kiln/`.
    pub relative: bool,
    pub status: EntryStatus,
    /// status refused: "too-many".
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub detail: Option<String>,
}

#[derive(Debug, Clone, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ChainIni {
    pub chain: Vec<ChainEntry>,
    pub profile: Vec<ChainEntry>,
}

/// kiln/chain/once.ini
#[derive(Debug, Clone, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct OnceIni {
    pub profile: Vec<ChainEntry>,
}

// ---- the shared INI dialect (play-once.ini uses it too) ----

/// cp1252 0x80..=0x9F (the rest of the byte range maps to the same code point).
const CP1252_HIGH: [u16; 32] = [
    0x20AC, 0xFFFD, 0x201A, 0x0192, 0x201E, 0x2026, 0x2020, 0x2021, 0x02C6, 0x2030, 0x0160, 0x2039, 0x0152, 0xFFFD,
    0x017D, 0xFFFD, 0xFFFD, 0x2018, 0x2019, 0x201C, 0x201D, 0x2022, 0x2013, 0x2014, 0x02DC, 0x2122, 0x0161, 0x203A,
    0x0153, 0xFFFD, 0x017E, 0x0178,
];

/// UTF-8 (BOM optional), UTF-16LE with BOM, or ANSI (the system code page; like the reference: cp1252).
pub(crate) fn decode(data: &[u8]) -> String {
    if let Some(rest) = data.strip_prefix(b"\xef\xbb\xbf") {
        return String::from_utf8_lossy(rest).into_owned();
    }
    if let Some(rest) = data.strip_prefix(b"\xff\xfe") {
        let units: Vec<u16> = rest.chunks(2).map(|c| u16::from_le_bytes([c[0], *c.get(1).unwrap_or(&0)])).collect();
        return String::from_utf16_lossy(&units);
    }
    match std::str::from_utf8(data) {
        Ok(s) => s.to_string(),
        Err(_) => data
            .iter()
            .map(|&b| match b {
                0x80..=0x9F => char::from_u32(CP1252_HIGH[(b - 0x80) as usize] as u32).unwrap_or('\u{FFFD}'),
                _ => b as char,
            })
            .collect(),
    }
}

/// A meaningful line: a section header (lower case), or a text line with the section it is in (None before any).
pub(crate) enum IniLine<'a> {
    #[allow(dead_code)] // the name: callers so far only need the Text lines' sections
    Section(String),
    Text(Option<String>, &'a str),
}

/// Lines trimmed of spaces/tabs (and a CR); blank lines and `;` `#` `//` comments skipped.
pub(crate) fn lines(text: &str) -> Vec<IniLine<'_>> {
    let mut out = Vec::new();
    let mut section: Option<String> = None;
    for raw in text.split('\n') {
        let line = raw.strip_suffix('\r').unwrap_or(raw).trim_matches([' ', '\t']);
        if line.is_empty() || line.starts_with(';') || line.starts_with('#') || line.starts_with("//") {
            continue;
        }
        if line.starts_with('[') && line.ends_with(']') && line.len() >= 2 {
            let name = line[1..line.len() - 1].trim_matches([' ', '\t']).to_lowercase();
            section = Some(name.clone());
            out.push(IniLine::Section(name));
            continue;
        }
        out.push(IniLine::Text(section.clone(), line));
    }
    out
}

/// '/' read as '\'. Absolute: a drive letter + '\' (Z:\, C:\) or UNC (\\server\...); anything else is relative to
/// `<game>/kiln/`.
pub fn game_path(p: &str) -> (String, bool) {
    let p = p.replace('/', "\\");
    let b = p.as_bytes();
    let absolute = (b.len() >= 3 && b[0].is_ascii_alphabetic() && b[1] == b':' && b[2] == b'\\') || p.starts_with("\\\\");
    (p, !absolute)
}

fn dll_entries(data: &[u8], wanted: &[&str], default_section: &str) -> Vec<Vec<ChainEntry>> {
    let text = decode(data);
    let mut out: Vec<Vec<ChainEntry>> = wanted.iter().map(|_| Vec::new()).collect();
    for l in lines(&text) {
        let IniLine::Text(section, text) = l else { continue };
        let name = section.as_deref().unwrap_or(default_section);
        let Some(idx) = wanted.iter().position(|w| *w == name) else { continue };
        let value = match text.split_once('=') {
            Some((_, v)) => v.trim_matches([' ', '\t']),
            None => text,
        };
        if !value.to_lowercase().ends_with(".dll") {
            continue;
        }
        let (path, relative) = game_path(value);
        let mut e = ChainEntry { path, relative, status: EntryStatus::Ok, detail: None };
        if out[idx].len() >= MAX_ENTRIES {
            e.status = EntryStatus::Refused;
            e.detail = Some("too-many".into());
        }
        out[idx].push(e);
    }
    out
}

/// Decodes (UTF-8 with or without BOM, UTF-16LE with BOM, else ANSI) and parses chain.ini.
pub fn parse(bytes: &[u8]) -> ChainIni {
    let mut v = dll_entries(bytes, &["chain", "profile"], "chain");
    let profile = v.pop().unwrap_or_default();
    let chain = v.pop().unwrap_or_default();
    ChainIni { chain, profile }
}

pub fn parse_once(bytes: &[u8]) -> OnceIni {
    OnceIni { profile: dll_entries(bytes, &["profile"], "profile").pop().unwrap_or_default() }
}

pub fn read(game_dir: &Path) -> Result<ChainIni> {
    let p = game_dir.join(crate::paths::game::CHAIN);
    match std::fs::read(&p) {
        Ok(b) => Ok(parse(&b)),
        Err(e) if e.kind() == std::io::ErrorKind::NotFound => Ok(ChainIni::default()),
        Err(e) => Err(crate::util::io_at(&p, "reading")(e)),
    }
}

// ---- writers ----

const HEADER: &str = "; Kiln chain-load list (docs/kiln/launcher/contracts.md 5.3): [chain] = proxies the installer moved \
aside (kiln/chain/), [profile] = native mods with phase dllmain of the active profile. A leading ; switches a line off.";

/// The file split into blocks by section: (lower-case section name or None for the lines before any section, raw
/// lines including their line ends).
fn blocks(text: &str) -> Vec<(Option<String>, Vec<String>)> {
    let mut out: Vec<(Option<String>, Vec<String>)> = vec![(None, Vec::new())];
    for raw in text.split_inclusive('\n') {
        let line = raw.trim_end_matches(['\r', '\n']).trim_matches([' ', '\t']);
        if line.starts_with('[') && line.ends_with(']') && line.len() >= 2 {
            let name = line[1..line.len() - 1].trim_matches([' ', '\t']).to_lowercase();
            out.push((Some(name), vec![raw.to_string()]));
        } else {
            out.last_mut().expect("one block").1.push(raw.to_string());
        }
    }
    out
}

fn eol(text: &str) -> &'static str {
    if text.contains("\r\n") || !text.contains('\n') { "\r\n" } else { "\n" }
}

fn ensure_eol(lines: &mut [String], nl: &str) {
    if let Some(last) = lines.last_mut()
        && !last.ends_with('\n')
    {
        last.push_str(nl);
    }
}

/// Rewrites one section of chain.ini (`section` = "chain" or "profile"). Layout of the result: Kiln's header
/// comment, `[chain]` (the lines before any section count as [chain]; several [chain] blocks are merged), then every
/// other block in its order, several [profile] blocks merged into the first one (a missing [profile] goes last).
/// `edit` gets the section's raw lines (comments included, without the header) and returns the new ones; the lines of
/// every other section are written back as they were.
fn rewrite_chain_ini(text: &str, section: &str, edit: impl FnOnce(Vec<String>) -> Vec<String>) -> String {
    let nl = eol(text);
    let mut chain: Vec<String> = Vec::new();
    let mut profile: Option<Vec<String>> = None;
    // other blocks in order; None marks where the (merged) [profile] goes
    let mut order: Vec<Option<Vec<String>>> = Vec::new();
    for (name, mut lines) in blocks(text) {
        ensure_eol(&mut lines, nl);
        match name.as_deref() {
            None => chain.extend(lines.into_iter().filter(|l| l.trim_end() != HEADER)),
            Some("chain") => chain.extend(lines.into_iter().skip(1)),
            Some("profile") => {
                let body: Vec<String> = lines.into_iter().skip(1).collect();
                match &mut profile {
                    Some(p) => p.extend(body),
                    None => {
                        profile = Some(body);
                        order.push(None);
                    }
                }
            }
            Some(_) => order.push(Some(lines)),
        }
    }
    let trim = |mut v: Vec<String>| {
        while v.first().is_some_and(|l| l.trim().is_empty()) {
            v.remove(0);
        }
        while v.last().is_some_and(|l| l.trim().is_empty()) {
            v.pop();
        }
        v
    };
    let mut chain = trim(chain);
    let had_profile = profile.is_some();
    let mut profile = profile.unwrap_or_default();
    if section == "chain" {
        chain = edit(chain);
    } else {
        profile = edit(trim(profile));
    }
    if !had_profile {
        order.push(None);
    }
    let mut out = String::new();
    let push = |out: &mut String, l: &str| {
        out.push_str(l);
        if !l.ends_with('\n') {
            out.push_str(nl);
        }
    };
    push(&mut out, HEADER);
    push(&mut out, "[chain]");
    for l in &chain {
        push(&mut out, l);
    }
    for block in &order {
        match block {
            None => {
                push(&mut out, "[profile]");
                for l in &profile {
                    push(&mut out, l);
                }
            }
            Some(lines) => {
                for l in lines {
                    push(&mut out, l);
                }
            }
        }
    }
    out
}

fn read_text(path: &Path) -> Result<String> {
    match std::fs::read(path) {
        Ok(b) => Ok(decode(&b)),
        Err(e) if e.kind() == std::io::ErrorKind::NotFound => Ok(String::new()),
        Err(e) => Err(crate::util::io_at(path, "reading")(e)),
    }
}

fn dll_line(p: &str) -> String {
    p.replace('/', "\\")
}

fn write_section(path: &Path, section: &str, edit: impl FnOnce(Vec<String>) -> Vec<String>) -> Result<()> {
    let text = read_text(path)?;
    let out = rewrite_chain_ini(&text, section, edit);
    crate::util::write_atomic(path, out.as_bytes())
}

/// Replaces the [chain] section (installer), keeping [profile] and everything else.
pub fn write_chain_section(game_dir: &Path, dlls: &[String]) -> Result<()> {
    let lines: Vec<String> = dlls.iter().map(|d| dll_line(d)).collect();
    write_section(&game_dir.join(crate::paths::game::CHAIN), "chain", |_| lines)
}

/// Replaces the [profile] section (activation), keeping [chain] and everything else.
pub fn write_profile_section(game_dir: &Path, dlls: &[String]) -> Result<()> {
    let lines: Vec<String> = dlls.iter().map(|d| dll_line(d)).collect();
    write_section(&game_dir.join(crate::paths::game::CHAIN), "profile", |_| lines)
}

fn same_entry(line: &str, path: &str) -> bool {
    let value = match line.split_once('=') {
        Some((_, v)) => v,
        None => line,
    };
    let v = value.trim_matches([' ', '\t', '\r', '\n']).replace('/', "\\");
    v.eq_ignore_ascii_case(&path.replace('/', "\\"))
}

/// Adds one DLL line to [chain] unless an active line names it already (installer: a displaced proxy). The
/// section's other lines, comments included, stay as they are.
pub fn add_chain_entry(game_dir: &Path, dll: &str) -> Result<()> {
    let dll = dll_line(dll);
    write_section(&game_dir.join(crate::paths::game::CHAIN), "chain", |mut lines| {
        if !lines.iter().any(|l| !is_comment(l) && same_entry(l, &dll)) {
            lines.push(dll.clone());
        }
        lines
    })
}

/// Removes the active lines of [chain] that name `dll` (uninstall); other lines stay. Returns how many it removed.
pub fn remove_chain_entry(game_dir: &Path, dll: &str) -> Result<usize> {
    let path = game_dir.join(crate::paths::game::CHAIN);
    if !path.exists() {
        return Ok(0);
    }
    let mut removed = 0;
    write_section(&path, "chain", |lines| {
        lines
            .into_iter()
            .filter(|l| {
                let hit = !is_comment(l) && same_entry(l, dll);
                removed += hit as usize;
                !hit
            })
            .collect()
    })?;
    Ok(removed)
}

fn is_comment(l: &str) -> bool {
    let t = l.trim_matches([' ', '\t', '\r', '\n']);
    t.is_empty() || t.starts_with(';') || t.starts_with('#') || t.starts_with("//")
}

/// Writes kiln/chain/once.ini (launch mode once), or removes it when `dlls` is empty.
pub fn write_once(game_dir: &Path, dlls: &[String]) -> Result<()> {
    let path = game_dir.join(crate::paths::game::CHAIN_ONCE);
    if dlls.is_empty() {
        return match std::fs::remove_file(&path) {
            Ok(()) => Ok(()),
            Err(e) if e.kind() == std::io::ErrorKind::NotFound => Ok(()),
            Err(e) => Err(crate::util::io_at(&path, "removing")(e)),
        };
    }
    let mut s = String::from("; Kiln play-once profile: native mods with phase dllmain for this one start\r\n[profile]\r\n");
    for d in dlls {
        s.push_str(&dll_line(d));
        s.push_str("\r\n");
    }
    crate::util::write_atomic(&path, s.as_bytes())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn rewrites_keep_the_other_section() {
        let d = std::env::temp_dir().join(format!("km-chain-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&d);
        std::fs::create_dir_all(d.join("kiln")).unwrap();
        let f = d.join("kiln/chain.ini");
        let original = "[chain]\r\nchain\\emf-dinput8.dll\r\n; chain\\off.dll\r\n\r\n[profile]\r\n  Z:\\a\\b.dll  \r\n[other]\r\nx = 1\r\n";
        std::fs::write(&f, original).unwrap();
        write_profile_section(&d, &["Z:/m/fps.dll".into()]).unwrap();
        let t = std::fs::read_to_string(&f).unwrap();
        assert!(t.contains("[chain]\r\nchain\\emf-dinput8.dll\r\n; chain\\off.dll\r\n"), "{t}");
        assert!(t.contains("[profile]\r\nZ:\\m\\fps.dll\r\n[other]\r\nx = 1\r\n"), "{t}");
        let c = read(&d).unwrap();
        assert_eq!(c.chain.len(), 1);
        assert_eq!(c.profile[0].path, "Z:\\m\\fps.dll");
        add_chain_entry(&d, "chain/unknown-dinput8.dll").unwrap();
        add_chain_entry(&d, "chain\\unknown-dinput8.dll").unwrap();
        let t2 = std::fs::read_to_string(&f).unwrap();
        assert!(t2.contains("[profile]\r\nZ:\\m\\fps.dll\r\n[other]\r\nx = 1\r\n"), "{t2}");
        let c = read(&d).unwrap();
        assert_eq!(c.chain.iter().map(|e| e.path.as_str()).collect::<Vec<_>>(), ["chain\\emf-dinput8.dll", "chain\\unknown-dinput8.dll"]);
        assert_eq!(remove_chain_entry(&d, "CHAIN\\emf-dinput8.dll").unwrap(), 1);
        let t3 = std::fs::read_to_string(&f).unwrap();
        assert!(t3.contains("; chain\\off.dll"), "{t3}");
        assert_eq!(read(&d).unwrap().chain.len(), 1);
        // a fresh file
        let d2 = d.join("fresh");
        std::fs::create_dir_all(d2.join("kiln")).unwrap();
        write_chain_section(&d2, &["chain\\emf-dinput8.dll".into()]).unwrap();
        write_profile_section(&d2, &[]).unwrap();
        let t4 = std::fs::read_to_string(d2.join("kiln/chain.ini")).unwrap();
        assert!(t4.starts_with("; Kiln chain-load list"), "{t4}");
        assert_eq!(parse(t4.as_bytes()).chain.len(), 1);
        assert!(t4.contains("[profile]"));
        write_once(&d2, &["a.dll".into()]).unwrap();
        assert_eq!(parse_once(&std::fs::read(d2.join("kiln/chain/once.ini")).unwrap()).profile[0].path, "a.dll");
        write_once(&d2, &[]).unwrap();
        assert!(!d2.join("kiln/chain/once.ini").exists());
        let _ = std::fs::remove_dir_all(&d);
    }
}
