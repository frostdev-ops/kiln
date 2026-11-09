//! `kiln/chain.ini` ([chain]: proxies the installer displaced; [profile]: native mods with phase dllmain of the
//! active profile) and `kiln/chain/once.ini` (play-once mode=profile reads it instead of [profile]). Grammar:
//! contracts.md "INI files"; reference: schema/fixtures/ini/kini.py; fixtures: schema/fixtures/ini/chain.
//! The installer writes [chain], profile activation writes [profile]; each rewrite keeps the other section's lines
//! as they were and replaces the file atomically.
//!
//! `[late]` (dev.21): DLLs that load after Kiln's plugins (kiln.hooks links, the ShroudForge runtime) instead of in
//! the proxy. A line names an entry of [chain], [profile] or dinput8-chain.ini (same path rules); the proxy skips that
//! entry and the core loads it later. Lines come from three places: the player ([`set_late`], game-wide), the active
//! profile (its `late` native mods, rewritten on every activation) and compat rules (preceded by a
//! `; kiln rule <id>` comment, rewritten on every activation). A rule that switches a [chain] line off writes it as
//! `; kiln rule <id> off: <line>`; the next activation puts it back when the rule no longer applies.

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
    /// [late]: entries of the other sections that load after Kiln's plugins (absent from fixtures without one).
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub late: Vec<ChainEntry>,
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
    let mut v = dll_entries(bytes, &["chain", "profile", "late"], "chain");
    let late = v.pop().unwrap_or_default();
    let profile = v.pop().unwrap_or_default();
    let chain = v.pop().unwrap_or_default();
    ChainIni { chain, profile, late }
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

/// Rewrites chain.ini's own sections. Layout of the result: Kiln's header comment, `[chain]` (the lines before any
/// section count as [chain]; several [chain] blocks are merged), then every other block in its order, several
/// [profile] (or [late]) blocks merged into the first one (a missing [profile] goes last; a missing [late] goes after
/// it, and only when it gets lines). `edit(section, lines)` gets each of "chain", "profile" and "late" with its raw
/// lines (comments included, without the header; blank lines at either end trimmed) and returns the new ones, or None
/// to leave the section as it was; other sections are written back as they were.
fn rewrite_chain_ini(text: &str, mut edit: impl FnMut(&str, Vec<String>) -> Option<Vec<String>>) -> String {
    let nl = eol(text);
    let mut chain: Vec<String> = Vec::new();
    let mut merged: [Option<Vec<String>>; 2] = [None, None]; // profile, late
    // other blocks in order; Err(i) marks where merged section i goes
    let mut order: Vec<std::result::Result<Vec<String>, usize>> = Vec::new();
    for (name, mut lines) in blocks(text) {
        ensure_eol(&mut lines, nl);
        match name.as_deref() {
            None => chain.extend(lines.into_iter().filter(|l| l.trim_end() != HEADER)),
            Some("chain") => chain.extend(lines.into_iter().skip(1)),
            Some(n @ ("profile" | "late")) => {
                let i = (n == "late") as usize;
                let body: Vec<String> = lines.into_iter().skip(1).collect();
                match &mut merged[i] {
                    Some(p) => p.extend(body),
                    None => {
                        merged[i] = Some(body);
                        order.push(Err(i));
                    }
                }
            }
            Some(_) => order.push(Ok(lines)),
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
    let chain = trim(chain);
    let chain = edit("chain", chain.clone()).unwrap_or(chain);
    let mut bodies: [Vec<String>; 2] = [Vec::new(), Vec::new()];
    for (i, name) in ["profile", "late"].into_iter().enumerate() {
        let had = merged[i].is_some();
        let raw = merged[i].take().unwrap_or_default();
        bodies[i] = match edit(name, trim(raw.clone())) {
            Some(new) => new,
            None => raw,
        };
        if !had && (i == 0 || !bodies[i].is_empty()) {
            order.push(Err(i));
        }
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
            Err(i) => {
                push(&mut out, if *i == 0 { "[profile]" } else { "[late]" });
                for l in &bodies[*i] {
                    push(&mut out, l);
                }
            }
            Ok(lines) => {
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
    let mut edit = Some(edit);
    let out = rewrite_chain_ini(&text, |name, lines| if name == section { edit.take().map(|f| f(lines)) } else { None });
    crate::util::write_atomic(path, out.as_bytes())
}

/// Replaces the [chain] section (installer), keeping [profile] and everything else.
pub fn write_chain_section(game_dir: &Path, dlls: &[String]) -> Result<()> {
    let lines: Vec<String> = dlls.iter().map(|d| dll_line(d)).collect();
    write_section(&game_dir.join(crate::paths::game::CHAIN), "chain", |_| lines)
}

/// Replaces the [profile] section (deactivation: none), keeping [chain] and everything else. [late] lines that named
/// a [profile] entry go with it (a profile's late entries come from the profile).
pub fn write_profile_section(game_dir: &Path, dlls: &[String]) -> Result<()> {
    let path = game_dir.join(crate::paths::game::CHAIN);
    let text = read_text(&path)?;
    let plan = ProfilePlan { profile: dlls.iter().map(|d| dll_line(d)).collect(), ..Default::default() };
    // keeps rule lines: only an activation evaluates rules
    let out = render_plan(&text, &plan, false);
    crate::util::write_atomic(&path, out.as_bytes())
}

/// What a profile activation writes into chain.ini: [profile], the late lines of the profile and of compat rules,
/// and the [chain] lines a rule switches off.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct ProfilePlan {
    /// [profile] lines (game-side DLL paths).
    pub profile: Vec<String>,
    /// Those of `profile` that load late (the profile's choice).
    pub profile_late: Vec<String>,
    /// (rule id, entry): entries a compat rule loads late.
    pub rule_late: Vec<(String, String)>,
    /// (rule id, [chain] entry): [chain] lines a compat rule switches off.
    pub rule_off: Vec<(String, String)>,
}

const RULE_MARK: &str = "; kiln rule ";
const RULE_OFF: &str = " off: ";

/// `; kiln rule <id> off: <line>` -> (id, line).
fn rule_off_line(l: &str) -> Option<(&str, &str)> {
    let rest = l.trim_matches([' ', '\t', '\r', '\n']).strip_prefix(RULE_MARK)?;
    let (id, line) = rest.split_once(RULE_OFF)?;
    Some((id, line))
}

/// `; kiln rule <id>` (the comment before a late line a rule added) -> id.
fn rule_mark_line(l: &str) -> Option<&str> {
    let rest = l.trim_matches([' ', '\t', '\r', '\n']).strip_prefix(RULE_MARK)?;
    (!rest.contains(RULE_OFF)).then_some(rest)
}

/// A rule id as written into a comment (one line, no `off:`).
fn rule_id_text(id: &str) -> String {
    id.chars().map(|c| if c.is_control() { ' ' } else { c }).collect::<String>().replace(RULE_OFF, " ")
}

fn entry_value(line: &str) -> String {
    let value = match line.split_once('=') {
        Some((_, v)) => v,
        None => line,
    };
    value.trim_matches([' ', '\t', '\r', '\n']).replace('/', "\\")
}

/// `; kiln rule <id> off: <line>` lines back to `<line>`.
fn restore_off(lines: Vec<String>) -> Vec<String> {
    lines
        .into_iter()
        .map(|l| match rule_off_line(&l) {
            Some((_, orig)) => format!("{orig}\n"),
            None => l,
        })
        .collect()
}

/// Active lines naming an entry of `off` become `; kiln rule <id> off: <line>`.
fn mark_off(lines: Vec<String>, off: &[(String, String)]) -> Vec<String> {
    lines
        .into_iter()
        .map(|l| match (!is_comment(&l)).then(|| off.iter().find(|(_, d)| same_entry(&l, d))).flatten() {
            Some((id, _)) => format!("{RULE_MARK}{}{RULE_OFF}{}", rule_id_text(id), l.trim_matches([' ', '\t', '\r', '\n'])),
            None => l,
        })
        .collect()
}

/// chain.ini as an activation leaves it. `rules`: false keeps the rule-made lines as they are (deactivation).
fn render_plan(text: &str, plan: &ProfilePlan, rules: bool) -> String {
    let mut old_profile: Vec<String> = Vec::new();
    let mut kept_rule_late: Vec<(String, String)> = Vec::new();
    rewrite_chain_ini(text, |name, lines| match name {
        "chain" if rules => Some(mark_off(restore_off(lines), &plan.rule_off)),
        "profile" => {
            old_profile = lines.iter().filter(|l| !is_comment(l)).map(|l| entry_value(l)).collect();
            Some(plan.profile.iter().map(|d| dll_line(d)).collect())
        }
        "late" => {
            let lines = if rules { restore_off(lines) } else { lines };
            let from_profile = |l: &str| old_profile.iter().chain(&plan.profile).any(|p| same_entry(l, p));
            let mut out: Vec<String> = Vec::new();
            let mut mark: Option<String> = None;
            for l in lines {
                if let Some(id) = rule_mark_line(&l) {
                    mark = Some(id.to_string());
                    continue;
                }
                if let Some(id) = mark.take()
                    && !is_comment(&l)
                {
                    // a rule's line: rewritten below (or kept as it was when rules are not evaluated)
                    if !rules && !from_profile(&l) {
                        kept_rule_late.push((id, entry_value(&l)));
                    }
                    continue;
                }
                if !is_comment(&l) && from_profile(&l) {
                    continue;
                }
                out.push(l);
            }
            let active = |out: &[String], d: &str| out.iter().any(|l| !is_comment(l) && same_entry(l, d));
            for d in &plan.profile_late {
                if !active(&out, d) {
                    out.push(dll_line(d));
                }
            }
            let rule_late = if rules { plan.rule_late.clone() } else { std::mem::take(&mut kept_rule_late) };
            for (id, d) in &rule_late {
                if !active(&out, d) {
                    out.push(format!("{RULE_MARK}{}", rule_id_text(id)));
                    out.push(dll_line(d));
                }
            }
            // a [late] line of a [chain] entry a rule switches off is switched off with it (back when the rule goes)
            Some(if rules { mark_off(out, &plan.rule_off) } else { out })
        }
        _ => None,
    })
}

/// Writes [profile], [late] and the rule switches of [chain] (activation). Returns false when the file already said
/// that (nothing written).
pub fn write_profile_plan(game_dir: &Path, plan: &ProfilePlan) -> Result<bool> {
    let path = game_dir.join(crate::paths::game::CHAIN);
    let text = read_text(&path)?;
    let out = render_plan(&text, plan, true);
    if out == text {
        return Ok(false);
    }
    crate::util::write_atomic(&path, out.as_bytes())?;
    Ok(true)
}

/// chain.ini as text ("" when there is none).
pub fn read_raw(game_dir: &Path) -> Result<String> {
    read_text(&game_dir.join(crate::paths::game::CHAIN))
}

/// The [chain] lines a compat rule switched off: (rule id, entry path).
pub fn rule_off_entries(text: &str) -> Vec<(String, String)> {
    let mut out = Vec::new();
    for (name, lines) in blocks(text) {
        if !matches!(name.as_deref(), None | Some("chain")) {
            continue;
        }
        for l in lines {
            if let Some((id, line)) = rule_off_line(&l) {
                let v = entry_value(line);
                if v.to_lowercase().ends_with(".dll") {
                    out.push((id.to_string(), game_path(&v).0));
                }
            }
        }
    }
    out
}

/// True when chain.ini (`text`) already says what `plan` says (an activation would write nothing new).
pub fn plan_is_current(text: &str, plan: &ProfilePlan) -> bool {
    let out = render_plan(text, plan, true);
    parse(out.as_bytes()) == parse(text.as_bytes()) && rule_off_entries(&out) == rule_off_entries(text)
}

/// True when [late] has an active line naming `dll` (case ignored, '/' read as '\').
pub fn is_late(ini: &ChainIni, dll: &str) -> bool {
    ini.late.iter().any(|e| e.path.eq_ignore_ascii_case(&dll.replace('/', "\\")))
}

/// The player's late switch for one entry (game-wide; [chain] and dinput8-chain.ini entries). On: adds a [late]
/// line unless one names it; off: removes the lines that name it (a rule's line too: the next activation puts a
/// rule's line back while the rule applies). Other lines stay as they are.
pub fn set_late(game_dir: &Path, dll: &str, late: bool) -> Result<()> {
    let dll = dll_line(dll);
    write_section(&game_dir.join(crate::paths::game::CHAIN), "late", |lines| {
        let mut out: Vec<String> = Vec::new();
        let mut found = false;
        for l in lines {
            let hit = !is_comment(&l) && same_entry(&l, &dll);
            found |= hit;
            if hit && !late {
                if out.last().is_some_and(|p| rule_mark_line(p).is_some()) {
                    out.pop();
                }
                continue;
            }
            out.push(l);
        }
        if late && !found {
            out.push(dll.clone());
        }
        out
    })
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

    fn fixture(name: &str) -> String {
        let p = std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join("../../schema/fixtures/ini/chain").join(name);
        String::from_utf8(std::fs::read(p).unwrap()).unwrap()
    }

    #[test]
    fn late_round_trip() {
        let text = fixture("late.ini");
        let fps = "Z:\\home\\user\\.local\\share\\kiln\\library\\native\\fps-unlock\\2.0.0+00000000\\fpsunlock.dll";
        let fps2 = "Z:\\home\\user\\.local\\share\\kiln\\library\\native\\fps-unlock\\2.1.0+00000000\\fpsunlock.dll";
        // an activation with the same entries, the profile's late native and no rule: the player's [late] line stays,
        // the rule's late line goes, the switched-off [chain] line comes back
        let plan = ProfilePlan { profile: vec![fps.into()], profile_late: vec![fps.into()], ..Default::default() };
        let out = render_plan(&text, &plan, true);
        let ini = parse(out.as_bytes());
        assert_eq!(ini.chain.iter().map(|e| e.path.as_str()).collect::<Vec<_>>(),
            ["chain\\emf-dinput8.dll", "chain\\chain-loader-dinput8.dll", "chain\\old-tool-dinput8.dll"], "{out}");
        assert_eq!(ini.late.iter().map(|e| e.path.as_str()).collect::<Vec<_>>(), ["chain\\chain-loader-dinput8.dll", fps], "{out}");
        assert!(!out.contains("; kiln rule"), "{out}");
        assert!(out.contains("[late]\r\n") || out.contains("[late]\n") || out.contains("[Late]"), "{out}");
        // idempotent
        assert_eq!(render_plan(&out, &plan, true), out);
        assert!(plan_is_current(&out, &plan));
        // rules: one entry late, one [chain] line off; a new version of the profile's native (path changes)
        let plan2 = ProfilePlan {
            profile: vec![fps2.into()],
            profile_late: vec![],
            rule_late: vec![("xhl.after-sf".into(), "chain\\emf-dinput8.dll".into())],
            rule_off: vec![("old".into(), "chain\\old-tool-dinput8.dll".into())],
        };
        assert!(!plan_is_current(&out, &plan2));
        let out2 = render_plan(&out, &plan2, true);
        let ini2 = parse(out2.as_bytes());
        assert_eq!(ini2.chain.len(), 2, "{out2}");
        assert_eq!(rule_off_entries(&out2), [("old".to_string(), "chain\\old-tool-dinput8.dll".to_string())]);
        assert_eq!(ini2.profile[0].path, fps2);
        assert_eq!(ini2.late.iter().map(|e| e.path.as_str()).collect::<Vec<_>>(), ["chain\\chain-loader-dinput8.dll", "chain\\emf-dinput8.dll"], "{out2}");
        assert!(out2.replace("\r\n", "\n").contains("; kiln rule xhl.after-sf\nchain\\emf-dinput8.dll"), "{out2}");
        assert_eq!(render_plan(&out2, &plan2, true), out2);
        // deactivation: [profile] empty, rule lines kept as they are, nothing names the old profile entry
        let out3 = render_plan(&out2, &ProfilePlan::default(), false);
        let ini3 = parse(out3.as_bytes());
        assert!(ini3.profile.is_empty());
        assert_eq!(ini3.late.len(), 2, "{out3}");
        assert_eq!(rule_off_entries(&out3).len(), 1);
        // rules gone: the [chain] line comes back
        let out4 = render_plan(&out3, &ProfilePlan::default(), true);
        assert_eq!(parse(out4.as_bytes()).chain.len(), 3, "{out4}");
        assert_eq!(parse(out4.as_bytes()).late.iter().map(|e| e.path.as_str()).collect::<Vec<_>>(), ["chain\\chain-loader-dinput8.dll"]);
        // a rule switches off a [chain] entry the player loads late: its [late] line goes off with it, and comes back
        let off = ProfilePlan { rule_off: vec![("x".into(), "chain\\chain-loader-dinput8.dll".into())], ..Default::default() };
        let out5 = render_plan(&out4, &off, true);
        let ini5 = parse(out5.as_bytes());
        assert!(ini5.late.is_empty() && ini5.chain.len() == 2, "{out5}");
        assert_eq!(render_plan(&out5, &off, true), out5);
        assert_eq!(render_plan(&out5, &ProfilePlan::default(), true), out4);
    }

    #[test]
    fn set_late_switches_one_line() {
        let d = std::env::temp_dir().join(format!("km-chain-late-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&d);
        std::fs::create_dir_all(d.join("kiln")).unwrap();
        let f = d.join("kiln/chain.ini");
        std::fs::write(&f, "[chain]\nchain\\xhl-core.dll\n[profile]\nZ:\\a.dll\n[other]\nx = 1\n").unwrap();
        set_late(&d, "chain/xhl-core.dll", true).unwrap();
        set_late(&d, "CHAIN\\XHL-core.dll", true).unwrap();
        let t = std::fs::read_to_string(&f).unwrap();
        assert!(t.contains("[other]\nx = 1\n[late]\nchain\\xhl-core.dll\n"), "{t}");
        let c = read(&d).unwrap();
        assert!(is_late(&c, "chain/xhl-core.dll"));
        assert_eq!(c.late.len(), 1);
        // a later [profile] write keeps the player's line
        write_profile_section(&d, &["Z:\\b.dll".into()]).unwrap();
        assert!(is_late(&read(&d).unwrap(), "chain\\xhl-core.dll"));
        set_late(&d, "chain\\xhl-core.dll", false).unwrap();
        let c = read(&d).unwrap();
        assert!(c.late.is_empty());
        assert_eq!(c.profile[0].path, "Z:\\b.dll");
        let _ = std::fs::remove_dir_all(&d);
    }
}
