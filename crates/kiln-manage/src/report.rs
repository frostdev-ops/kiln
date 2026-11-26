//! Support reports: one zip with what a maintainer needs to diagnose "mods don't load", with nothing private in it
//! (docs/kiln/launcher/support-report.md). The launcher's "Save report..." (`report_save`) and `kilnctl report` write
//! it; "Copy summary" (`report_summary`) is [`Report::short`].
//!
//! - [`build`] reads everything (each section on its own: one that fails says why and the rest is still written) and
//!   returns the summary, the findings and the files, already redacted. Nothing is written to the game folder.
//! - [`redact`] is the one redaction function every text goes through: the home folder in Linux, Windows and Wine `Z:\`
//!   form (also with doubled backslashes inside JSON), the user name and the machine name alone, Steam ids, IP (v4 and v6)
//!   and MAC addresses, mail addresses, other players' names in Kiln's and ShroudLink's log lines ([`player_names`]), and
//!   the value after a secret's name in plain text (`password=...`).
//!   [`redact_json`] also blanks fields whose names say they hold a secret or an address.
//! - [`write_zip`] (feature `zip`) writes the zip.
//!
//! Layout of the zip: `summary.txt` (for a forum post), `report.json` (findings and failed sections), `game/<path>`
//! (tails of kiln.log, proxy.log, eml.log, tick-stats.jsonl, shroudlink_client.log, XHL-Core's XHL-Native-Plugin-Host.log,
//! other mods' logs in the game folder (BuildingTools.log, shroudlink.log, auto-loot-plus.log: OTHER_MOD_LOGS), the newest crash
//! report; `kiln/state/*.json`, `kiln/profile.json`,
//! `kiln/chain.ini`, `kiln/kiln.json`, `kiln/.install.json`), `greeneye-mods/<id>/<game|library>/*.log|*.status` (tails of each Greeneye
//! mod's own log and status files, with Greeneye Loader's logs under `game/`), `launcher/launcher.log` (tail), `listings/*.txt` (names,
//! sizes and times of the game folder's top level, `kiln/` two levels deep, each engine's mods folder).

use crate::engine_mods;
use crate::game::{GameInstall, GameKind, Platform};
use crate::install::InstallReceipt;
use crate::manifest::ModKind;
use crate::paths::DataDirs;
use crate::pe::{ProxyClass, ProxyKind};
use crate::profile::{Dropins, ProfileFile};
use crate::steam::SteamRoot;
use serde::{Deserialize, Serialize};
use std::fmt::Write as _;
use std::path::{Path, PathBuf};
use std::time::SystemTime;

/// Tail of kiln.log in the zip.
pub const KILN_LOG_MAX: u64 = 1 << 20;
/// Tail of proxy.log, eml.log, shroudlink_client.log, launcher.log.
pub const SMALL_LOG_MAX: u64 = 192 * 1024;
/// Tail of tick-stats.jsonl (one line a minute per world, about 2 KiB each since 0.2.0-dev.29: about 3 hours of a hosted
/// session with three worlds, 8 of a dedicated server).
pub const TICK_STATS_MAX: u64 = 1024 * 1024;
/// The newest crash report.
pub const CRASH_MAX: u64 = 64 * 1024;
/// One Greeneye log or status file (Greeneye Loader's loader.log, a Greeneye mod's own log in its folder).
pub const GREENEYE_LOG_MAX: u64 = 64 * 1024;
/// Greeneye mod folders whose own log and status files are read, and files read in each.
const GREENEYE_MOD_FOLDERS: usize = 12;
const GREENEYE_FILES_PER_MOD: usize = 4;
/// One JSON or INI file (hooks.json may be 8 MiB: its head is kept).
pub const FILE_MAX: u64 = 1 << 20;
/// Lines of one listing.
pub const LIST_MAX: usize = 2000;
/// All files together, before compression (the zip is a fraction of this).
pub const TOTAL_MAX: usize = 6 << 20;
/// chain.ini as quoted in summary.txt.
const CHAIN_IN_SUMMARY: usize = 4096;
/// The short summary ("Copy summary"), in characters.
const SHORT_MAX: usize = 3500;

// ---------------------------------------------------------------- redaction

/// What [`redact`] replaces. Build it with [`Redactor::new`] (tests, explicit values) or [`Redactor::current`] (this
/// machine: home, user, host, the Steam accounts in loginusers.vdf).
#[derive(Debug, Clone, Default)]
pub struct Redactor {
    paths: Vec<PathNeedle>,
    words: Vec<Word>,
}

#[derive(Debug, Clone)]
struct PathNeedle {
    /// Linux form: a separator comes first (`/home/x`); the Windows and Wine forms start with their drive.
    lead_sep: bool,
    comps: Vec<Vec<char>>,
}

#[derive(Debug, Clone)]
struct Word {
    text: Vec<char>,
    token: &'static str,
    /// Only where no letter or digit is next to it (short or common names, display names: `Oryx593_world` too); else
    /// anywhere (`ivan-pc`, `IVAN~1`).
    whole: bool,
}

/// Words a user name may be that Kiln's own files are full of: such a name is replaced only inside the home folder.
const COMMON: &[&str] = &[
    "kiln", "mod", "mods", "game", "games", "steam", "user", "users", "admin", "root", "test", "data", "log", "logs",
    "home", "server", "client", "host", "config", "state", "local", "share", "proton", "wine", "plugin", "plugins",
    "native", "emf", "eml", "set", "flame", "ember", "enshrouded", "player", "default", "public", "guest", "owner",
    // default account names that name nobody: every Steam Deck's user is `deck`, Proton's prefix user is `steamuser`
    "deck", "steamuser", "desktop", "windows", "linux",
    // the tokens themselves: a report redacted twice must not change
    "redacted", "steamid", "name", "steam-name", "ip", "mac", "email",
];

fn lower(c: char) -> char {
    c.to_lowercase().next().unwrap_or(c)
}

fn is_word(c: char) -> bool {
    c.is_alphanumeric() || c == '_'
}

fn is_sep(c: char) -> bool {
    c == '/' || c == '\\'
}

impl Redactor {
    /// `home`: a home folder in Linux (`/home/x`) or Windows (`C:\Users\x`) form; `user`, `host`: names.
    pub fn new(home: Option<&str>, user: Option<&str>, host: Option<&str>) -> Self {
        let mut r = Redactor::default();
        if let Some(h) = home {
            r.add_home(h);
        }
        if let Some(h) = host {
            r.add_word(h, "<host>");
        }
        if let Some(u) = user {
            r.add_word(u, "<user>");
        }
        r
    }

    /// This machine: the home folder, the user name (and the home folder's last component when it differs), the
    /// machine name, and the account and persona names in Steam's loginusers.vdf (read, never written).
    pub fn current(roots: &[SteamRoot]) -> Self {
        let home = crate::util::home();
        let home_s = home.to_string_lossy().into_owned();
        let user = ["USER", "USERNAME", "LOGNAME"].iter().find_map(|k| std::env::var(k).ok().filter(|v| !v.trim().is_empty()));
        // a machine named after its distribution (`omarchy`, `steamdeck`) keeps that word in the OS line
        let (os_name, os_id) = std::fs::read_to_string("/etc/os-release").map(|t| os_release(&t)).unwrap_or_default();
        let host = host_name().filter(|h| {
            let h = h.to_ascii_lowercase();
            os_id.as_deref() != Some(h.as_str()) && !os_name.as_deref().is_some_and(|n| n.to_ascii_lowercase().split_whitespace().any(|w| w == h))
                && h != "steamdeck"
        });
        let mut r = Redactor::new(Some(&home_s), None, host.as_deref());
        if let Some(u) = &user {
            r.add_word(u, "<user>");
        }
        if let Some(last) = home.file_name().map(|n| n.to_string_lossy().into_owned()) {
            r.add_word(&last, "<user>");
        }
        r.add_steam_accounts(roots, &home);
        r
    }

    /// The Steam accounts of this machine (read only): AccountName (`<user>`) and PersonaName (`<steam-name>`) of every
    /// account in each root's `config/loginusers.vdf`; the PersonaName of each `userdata/<id>/config/localconfig.vdf` (the
    /// account that signed in there, also when loginusers.vdf does not remember it); and AutoLoginUser of Steam's
    /// `registry.vdf` under `home` (native `~/.steam`, Flatpak, Snap). The roots are `steam::roots`' (native, Debian,
    /// Flatpak, Snap, a Deck's `~/.local/share/Steam`; Windows: the registry's).
    pub fn add_steam_accounts(&mut self, roots: &[SteamRoot], home: &Path) {
        let read = |p: &Path| std::fs::read(p).ok().and_then(|b| crate::vdf::parse(&String::from_utf8_lossy(&b)).ok());
        for root in roots {
            for e in std::fs::read_dir(root.path.join("userdata")).into_iter().flatten().flatten() {
                if let Some(v) = read(&e.path().join("config/localconfig.vdf"))
                    && let Some(p) = v.path(&["UserLocalConfigStore", "friends", "PersonaName"]).and_then(|x| x.as_str())
                {
                    self.add_whole_word(p, "<steam-name>");
                }
            }
        }
        for rel in [".steam/registry.vdf", ".var/app/com.valvesoftware.Steam/.steam/registry.vdf", "snap/steam/common/.steam/registry.vdf"] {
            if let Some(v) = read(&home.join(rel))
                && let Some(a) = v.path(&["Registry", "HKCU", "Software", "Valve", "Steam", "AutoLoginUser"]).and_then(|x| x.as_str())
            {
                self.add_word(a, "<user>");
            }
        }
        for root in roots {
            let Ok(text) = std::fs::read(root.path.join("config/loginusers.vdf")) else { continue };
            let Ok(v) = crate::vdf::parse(&String::from_utf8_lossy(&text)) else { continue };
            for (_, u) in v.get("users").map(|u| u.entries()).unwrap_or_default() {
                if let Some(a) = u.get("AccountName").and_then(|x| x.as_str()) {
                    self.add_word(a, "<user>");
                }
                if let Some(p) = u.get("PersonaName").and_then(|x| x.as_str()) {
                    self.add_whole_word(p, "<steam-name>");
                }
            }
        }
    }

    /// Adds a home folder (Linux form also matched as Wine's `Z:\...`; Windows form with its drive).
    pub fn add_home(&mut self, home: &str) {
        let comps: Vec<Vec<char>> =
            home.split(is_sep).filter(|c| !c.is_empty()).map(|c| c.chars().map(lower).collect()).collect();
        if comps.is_empty() || comps.iter().map(Vec::len).sum::<usize>() < 3 {
            return;
        }
        let drive = comps[0].len() == 2 && comps[0][1] == ':';
        if drive {
            if comps.len() < 2 {
                return;
            }
            self.paths.push(PathNeedle { lead_sep: false, comps });
        } else {
            // Wine's Z:\ form first: at the Z it is the longer match
            let mut wine = vec![vec!['z', ':']];
            wine.extend(comps.iter().cloned());
            self.paths.push(PathNeedle { lead_sep: false, comps: wine });
            self.paths.push(PathNeedle { lead_sep: true, comps });
        }
    }

    /// Replaces `word` with `token` (case ignored), also inside other words; names of 3 characters or fewer only as
    /// whole words. A common word (`user`, `steam`, `kiln`...) is left alone outside the home folder: replacing it
    /// everywhere would make the report unreadable, and it names nobody.
    pub fn add_word(&mut self, word: &str, token: &'static str) {
        let w = word.trim();
        if w.chars().count() < 2 || COMMON.iter().any(|c| c.eq_ignore_ascii_case(w)) {
            return;
        }
        self.push_word(w, token, w.chars().count() <= 3);
    }

    /// Replaces `word` only as a whole word (display names that may be ordinary words), at least 3 characters.
    pub fn add_whole_word(&mut self, word: &str, token: &'static str) {
        let w = word.trim();
        if w.chars().count() >= 3 && !COMMON.iter().any(|c| c.eq_ignore_ascii_case(w)) {
            self.push_word(w, token, true);
        }
    }

    /// Adds the players a log names ([`player_names`]) as whole words (`<player>`): the lines that name a player without
    /// saying so (`peer Name: dropped frame`, a mod's own line) lose the name too.
    pub fn add_players_from(&mut self, log: &str) {
        for n in player_names(log) {
            self.add_whole_word(&n, "<player>");
        }
    }

    fn push_word(&mut self, w: &str, token: &'static str, whole: bool) {
        let text: Vec<char> = w.chars().map(lower).collect();
        if !self.words.iter().any(|x| x.text == text) {
            self.words.push(Word { text, token, whole });
        }
    }
}

/// The machine name (Windows `COMPUTERNAME`; elsewhere the kernel's hostname, `/etc/hostname`, `HOSTNAME`).
pub fn host_name() -> Option<String> {
    let clean = |s: String| Some(s.trim().to_string()).filter(|s| !s.is_empty());
    if cfg!(windows) {
        return std::env::var("COMPUTERNAME").ok().and_then(clean);
    }
    std::fs::read_to_string("/proc/sys/kernel/hostname")
        .ok()
        .and_then(clean)
        .or_else(|| std::fs::read_to_string("/etc/hostname").ok().and_then(clean))
        .or_else(|| std::env::var("HOSTNAME").ok().and_then(clean))
}

/// Every text in a report goes through this: the home folder (any separator style, case ignored) becomes `<home>`, the
/// machine name `<host>`, the user name `<user>` (also inside other words), Steam ids `<steamid>`, IPv4 addresses other
/// than loopback `<ip>`.
pub fn redact(text: &str, r: &Redactor) -> String {
    let mut chars: Vec<char> = text.chars().collect();
    if !r.paths.is_empty() {
        chars = redact_paths(&chars, &r.paths);
    }
    for w in &r.words {
        chars = redact_word(&chars, w);
    }
    chars = redact_steam_ids(&chars);
    chars = redact_ips(&chars);
    chars = redact_ipv6_and_macs(&chars);
    chars = redact_emails(&chars);
    let text: String = chars.into_iter().collect();
    redact_secret_values(&redact_player_lines(&text))
}

// ---------------------------------------------------------------- player names

/// Where one log line names a player (byte ranges of the name), in the lines Kiln and ShroudLink write about the
/// players of a session: other people's names, which [`Redactor::current`] cannot know. Kiln's players service
/// (`players: joined slot 0 entity 1 'Name' (steam ...)`), Kiln.Network (`net: Name (0x80) joined with Kiln ...`,
/// `net: peer Name (0x80) left`, `kick`/`kicking`/`banned`/`refusing`, `peer Name said bye`, `peer Name: ...`,
/// `... sent to Name`), Kiln.Saves (`Kiln.Saves: Name has no character id`), ShroudLink's host (`shroudlink: client
/// <id> is Name`, `world: client <id> is player 1 (Name)`).
fn player_spans(line: &str) -> Vec<(usize, usize)> {
    let mut out = Vec::new();
    let mut push = |s: usize, e: usize| {
        let name = line[s..e].trim();
        if !name.is_empty() && !(name.starts_with('<') && name.ends_with('>')) {
            let lead = line[s..e].len() - line[s..e].trim_start().len();
            out.push((s + lead, s + lead + name.len()));
        }
    };
    // Kiln's players service
    if let Some(p) = line.find("players: ")
        && let Some(e) = line[p..].find(" entity ")
    {
        let at = p + e + " entity ".len();
        let digits = line[at..].bytes().take_while(u8::is_ascii_digit).count();
        if digits > 0 && line[at + digits..].starts_with(" '") {
            let s = at + digits + 2;
            if let Some(q) = line[s..].rfind("' (steam ") {
                push(s, s + q);
            }
        }
    }
    // Kiln.Network
    if let Some(p) = line.find("net: ") {
        let mut s = p + "net: ".len();
        let mut verb = "";
        for w in ["admission: ", "peer ", "kicking ", "kick ", "banned ", "refusing "] {
            if line[s..].starts_with(w) {
                s += w.len();
                verb = w;
                break;
            }
        }
        let rest = &line[s..];
        if let Some(e) = hex_paren(rest) {
            push(s, s + e);
        } else if verb == "refusing " && let Some(e) = rest.find(" (Kiln ") {
            push(s, s + e);
        } else if verb == "peer " {
            if let Some(e) = rest.find(" said bye").or_else(|| rest.find(": ")) {
                push(s, s + e);
            }
        } else if rest.starts_with("config state ") && let Some(e) = rest.rfind(" sent to ") {
            push(s + e + " sent to ".len(), line.len());
        }
    }
    // Kiln.Saves
    if let Some(p) = line.find("Kiln.Saves: ")
        && let Some(e) = line[p..].find(" has no character id")
    {
        push(p + "Kiln.Saves: ".len(), p + e);
    }
    // ShroudLink's host (native/plugins/shroudlink: mods.rs, world.rs)
    for lead in ["shroudlink: client ", "world: client "] {
        let Some(p) = line.find(lead) else { continue };
        let at = p + lead.len();
        let hex = line[at..].bytes().take_while(u8::is_ascii_hexdigit).count();
        if hex == 0 || !line[at + hex..].starts_with(" is ") {
            continue;
        }
        let s = at + hex + " is ".len();
        let rest = line[s..].trim_end();
        if rest.starts_with("player ") {
            // `is player 1 (Name)`, `is player 1 (the only unmatched one) (Name)`
            if rest.ends_with(')') && let Some(o) = rest.rfind(" (") {
                let name = &rest[o + 2..rest.len() - 1];
                if name != "the only unmatched one" {
                    push(s + o + 2, s + rest.len() - 1);
                }
            }
        } else if lead == "shroudlink: client " && !rest.starts_with("addon ") && !rest.starts_with("an addon") {
            push(s, s + rest.len());
        }
    }
    out
}

/// The length of the name before ` (0x<hex>)` at the start of `s` (Kiln.Network's `Name (0x80)`).
fn hex_paren(s: &str) -> Option<usize> {
    let mut from = 0;
    while let Some(i) = s[from..].find(" (0x") {
        let at = from + i;
        let hex = s[at + 4..].bytes().take_while(u8::is_ascii_hexdigit).count();
        if hex > 0 && s[at + 4 + hex..].starts_with(')') {
            return (at > 0).then_some(at);
        }
        from = at + 4;
    }
    None
}

/// Every name [`player_spans`] finds becomes `<player>` in its line.
fn redact_player_lines(text: &str) -> String {
    if !text.contains("players: ") && !text.contains("net: ") && !text.contains("Kiln.Saves: ") && !text.contains(" client ") {
        return text.to_string();
    }
    let mut out = String::with_capacity(text.len());
    for line in text.split_inclusive('\n') {
        let spans = player_spans(line);
        if spans.is_empty() {
            out.push_str(line);
            continue;
        }
        let mut at = 0;
        let mut spans = spans;
        spans.sort();
        for (s, e) in spans {
            if s < at {
                continue;
            }
            out.push_str(&line[at..s]);
            out.push_str("<player>");
            at = e;
        }
        out.push_str(&line[at..]);
    }
    out
}

/// The names of the players a log shows ([`player_spans`]), each once.
pub fn player_names(text: &str) -> Vec<String> {
    let mut names: Vec<String> = Vec::new();
    for line in text.lines() {
        for (s, e) in player_spans(line) {
            let n = line[s..e].to_string();
            if n.chars().count() <= 64 && !names.contains(&n) {
                names.push(n);
            }
        }
    }
    names
}

// ---------------------------------------------------------------- addresses, mail, secrets in plain text

/// IPv6 addresses (`<ip>`, not `::1` and `::`) and MAC addresses (`<mac>`).
fn redact_ipv6_and_macs(text: &[char]) -> Vec<char> {
    let hexc = |c: char| c.is_ascii_hexdigit() || c == ':';
    let mut out = Vec::with_capacity(text.len());
    let mut i = 0;
    while i < text.len() {
        let c = text[i];
        let start_ok = hexc(c) && (i == 0 || !(text[i - 1].is_alphanumeric() || matches!(text[i - 1], ':' | '.' | '_')));
        if start_ok {
            let mut j = i;
            while j < text.len() && hexc(text[j]) {
                j += 1;
            }
            let word_goes_on = j < text.len() && (text[j].is_alphanumeric() || text[j] == '_');
            // a sentence's colon after the address is no part of it
            let mut end = j;
            if end >= i + 2 && text[end - 1] == ':' && text[end - 2] != ':' {
                end -= 1;
            }
            let tok: String = text[i..end].iter().collect();
            let kind = if word_goes_on { None } else { address_kind(&tok) };
            if let Some(token) = kind {
                out.extend(token.chars());
                out.extend(&text[end..j]);
                i = j;
                continue;
            }
            out.extend(&text[i..j]);
            i = j;
            continue;
        }
        out.push(c);
        i += 1;
    }
    out
}

/// `<ip>` for an IPv6 address other than `::1`/`::`, `<mac>` for a MAC address, else None.
fn address_kind(tok: &str) -> Option<&'static str> {
    if tok.matches(':').count() < 2 || tok.contains(":::") || tok == "::1" || tok == "::" {
        return None;
    }
    let groups: Vec<&str> = tok.split(':').collect();
    let hex = |g: &str| !g.is_empty() && g.len() <= 4 && g.bytes().all(|b| b.is_ascii_hexdigit());
    match tok.matches("::").count() {
        0 if groups.len() == 6 && groups.iter().all(|g| g.len() == 2 && hex(g)) => Some("<mac>"),
        0 if groups.len() == 8 && groups.iter().all(|g| hex(g)) => Some("<ip>"),
        1 => {
            let full: Vec<&&str> = groups.iter().filter(|g| !g.is_empty()).collect();
            // `fe80::1`, `2001:db8::7`; not C++'s `abc::def` (an address has a digit)
            (full.len() >= 2 && full.len() <= 7 && full.iter().all(|g| hex(g)) && tok.bytes().any(|b| b.is_ascii_digit())).then_some("<ip>")
        }
        _ => None,
    }
}

/// Mail addresses (`name@example.org`) become `<email>`.
fn redact_emails(text: &[char]) -> Vec<char> {
    if !text.contains(&'@') {
        return text.to_vec();
    }
    let local = |c: char| c.is_ascii_alphanumeric() || matches!(c, '.' | '_' | '%' | '+' | '-');
    let domain = |c: char| c.is_ascii_alphanumeric() || matches!(c, '.' | '-');
    let mut out: Vec<char> = Vec::with_capacity(text.len());
    let mut i = 0;
    while i < text.len() {
        if text[i] == '@' {
            let mut l = 0;
            while l < out.len() && local(out[out.len() - 1 - l]) {
                l += 1;
            }
            let mut r = i + 1;
            while r < text.len() && domain(text[r]) {
                r += 1;
            }
            while r > i + 1 && text[r - 1] == '.' {
                r -= 1;
            }
            let dom: String = text[i + 1..r].iter().collect();
            let tld = dom.rsplit('.').next().unwrap_or("");
            if l > 0 && dom.contains('.') && !dom.starts_with('.') && tld.len() >= 2 && tld.chars().all(|c| c.is_ascii_alphabetic()) {
                out.truncate(out.len() - l);
                out.extend("<email>".chars());
                i = r;
                continue;
            }
        }
        out.push(text[i]);
        i += 1;
    }
    out
}

/// A word that names a secret in plain text (`password=...`, `ApiKey = ...`, `token: ...`, `--password ...`).
fn is_secret_word(k: &str) -> bool {
    let k = k.trim_start_matches('-').to_ascii_lowercase();
    ["token", "password", "passwd", "passphrase", "secret", "apikey", "api_key", "api-key", "cookie", "credential"].iter().any(|p| k.contains(p))
}

/// Logs and INI files: the value after a secret's name (`password=hunter2`, `ApiKey = abc`, `token: abc`,
/// `?token=abc&`, `--password abc`) becomes `<redacted>`, as [`redact_json`] does for JSON fields.
fn redact_secret_values(text: &str) -> String {
    let b = text.as_bytes();
    let ident = |c: u8| c.is_ascii_alphanumeric() || matches!(c, b'_' | b'.' | b'-');
    let mut out = String::with_capacity(text.len());
    let mut at = 0; // copied up to here
    let mut i = 0;
    while i < b.len() {
        if !ident(b[i]) || (i > 0 && ident(b[i - 1])) {
            i += 1;
            continue;
        }
        let mut j = i;
        while j < b.len() && ident(b[j]) {
            j += 1;
        }
        if !is_secret_word(&text[i..j]) {
            i = j;
            continue;
        }
        let mut k = j;
        if k < b.len() && matches!(b[k], b'"' | b'\'') {
            k += 1;
        }
        while k < b.len() && matches!(b[k], b' ' | b'\t') {
            k += 1;
        }
        let sep = k < b.len() && (b[k] == b'=' || (b[k] == b':' && b.get(k + 1) != Some(&b':')));
        let flag = !sep && text[i..j].starts_with('-') && k == j + 1 && b[j] == b' ' && k < b.len() && b[k] != b'-';
        if !sep && !flag {
            i = j;
            continue;
        }
        if sep {
            k += 1;
            while k < b.len() && matches!(b[k], b' ' | b'\t') {
                k += 1;
            }
        }
        let (v, e) = match b.get(k) {
            Some(&q @ (b'"' | b'\'')) => {
                let e = text[k + 1..].find(q as char).map(|e| k + 1 + e).unwrap_or(k + 1);
                (k + 1, e)
            }
            Some(b'[' | b'{') | None => (k, k),
            Some(_) => {
                let mut e = k;
                while e < b.len() && !b[e].is_ascii_whitespace() && !matches!(b[e], b',' | b';' | b'&' | b')' | b']' | b'}' | b'"' | b'\'') {
                    e += 1;
                }
                (k, e)
            }
        };
        if e > v && &text[v..e] != "<redacted>" && text.is_char_boundary(v) && text.is_char_boundary(e) {
            out.push_str(&text[at..v]);
            out.push_str("<redacted>");
            at = e;
        }
        i = e.max(j);
    }
    out.push_str(&text[at..]);
    out
}

fn redact_paths(text: &[char], needles: &[PathNeedle]) -> Vec<char> {
    let low: Vec<char> = text.iter().map(|&c| lower(c)).collect();
    let mut out = Vec::with_capacity(text.len());
    let mut i = 0;
    while i < text.len() {
        let hit = needles.iter().find_map(|n| match_path(&low, i, n));
        match hit {
            Some(end) => {
                out.extend("<home>".chars());
                i = end;
            }
            None => {
                out.push(text[i]);
                i += 1;
            }
        }
    }
    out
}

/// The end of `n` matched at `i` (separators: any run of `/` and `\`), None when it does not match there.
fn match_path(low: &[char], i: usize, n: &PathNeedle) -> Option<usize> {
    let mut p = i;
    let seps = |p: &mut usize| {
        let s = *p;
        while *p < low.len() && is_sep(low[*p]) {
            *p += 1;
        }
        *p > s
    };
    if n.lead_sep {
        if !seps(&mut p) {
            return None;
        }
    } else if i > 0 && is_word(low[i - 1]) {
        return None;
    }
    for (k, c) in n.comps.iter().enumerate() {
        if low.len() < p + c.len() || low[p..p + c.len()] != c[..] {
            return None;
        }
        p += c.len();
        if k + 1 < n.comps.len() && !seps(&mut p) {
            return None;
        }
    }
    (p == low.len() || !is_word(low[p])).then_some(p)
}

fn redact_word(text: &[char], w: &Word) -> Vec<char> {
    let n = w.text.len();
    let mut out = Vec::with_capacity(text.len());
    let mut i = 0;
    while i < text.len() {
        let fits = i + n <= text.len()
            && text[i..i + n].iter().zip(&w.text).all(|(&a, &b)| lower(a) == b)
            && (!w.whole || ((i == 0 || !text[i - 1].is_alphanumeric()) && (i + n == text.len() || !text[i + n].is_alphanumeric())));
        if fits {
            out.extend(w.token.chars());
            i += n;
        } else {
            out.push(text[i]);
            i += 1;
        }
    }
    out
}

fn ends_with_ci(out: &[char], s: &str) -> bool {
    let s: Vec<char> = s.chars().collect();
    out.len() >= s.len() && out[out.len() - s.len()..].iter().zip(&s).all(|(&a, &b)| lower(a) == b)
}

/// SteamID64s (`7656119` + 10 digits), the account number in `userdata/<n>` and in `[U:1:<n>]`.
fn redact_steam_ids(text: &[char]) -> Vec<char> {
    let mut out: Vec<char> = Vec::with_capacity(text.len());
    let mut i = 0;
    while i < text.len() {
        if !text[i].is_ascii_digit() || (i > 0 && text[i - 1].is_ascii_digit()) {
            out.push(text[i]);
            i += 1;
            continue;
        }
        let mut j = i;
        while j < text.len() && text[j].is_ascii_digit() {
            j += 1;
        }
        let run: String = text[i..j].iter().collect();
        let after_userdata = {
            let mut k = out.len();
            while k > 0 && is_sep(out[k - 1]) {
                k -= 1;
            }
            k < out.len() && ends_with_ci(&out[..k], "userdata")
        };
        let id64 = run.len() == 17 && run.starts_with("7656119");
        if id64 || after_userdata || ends_with_ci(&out, "[u:1:") {
            out.extend("<steamid>".chars());
        } else {
            out.extend(text[i..j].iter());
        }
        i = j;
    }
    out
}

/// Dotted IPv4 addresses (each part 0-255) except 127.x.x.x and 0.x.x.x.
fn redact_ips(text: &[char]) -> Vec<char> {
    let mut out = Vec::with_capacity(text.len());
    let mut i = 0;
    while i < text.len() {
        let start_ok = text[i].is_ascii_digit() && (i == 0 || !(text[i - 1].is_alphanumeric() || text[i - 1] == '.'));
        if start_ok && let Some((end, parts)) = ipv4_at(text, i) {
            // 0.x.x.x is no host's address (and is a version number: `Kiln.Host 0.2.0.0`)
            if parts[0] == 127 || parts[0] == 0 {
                out.extend(&text[i..end]);
            } else {
                out.extend("<ip>".chars());
            }
            i = end;
            continue;
        }
        out.push(text[i]);
        i += 1;
    }
    out
}

fn ipv4_at(t: &[char], i: usize) -> Option<(usize, [u32; 4])> {
    let mut p = i;
    let mut parts = [0u32; 4];
    for (k, part) in parts.iter_mut().enumerate() {
        let s = p;
        while p < t.len() && t[p].is_ascii_digit() && p - s < 4 {
            p += 1;
        }
        let n = p - s;
        if n == 0 || n > 3 {
            return None;
        }
        *part = t[s..p].iter().collect::<String>().parse().ok()?;
        if *part > 255 {
            return None;
        }
        if k < 3 {
            if p >= t.len() || t[p] != '.' {
                return None;
            }
            p += 1;
        }
    }
    let next_digit = p < t.len() && t[p].is_ascii_digit();
    let next_part = p + 1 < t.len() && t[p] == '.' && t[p + 1].is_ascii_digit();
    (!next_digit && !next_part).then_some((p, parts))
}

/// Field names whose string or number values never go into a report.
pub fn is_secret_key(key: &str) -> bool {
    let k = key.to_ascii_lowercase();
    const PART: &[&str] = &["token", "password", "passwd", "passphrase", "secret", "apikey", "api_key", "cookie", "credential", "private"];
    const EXACT: &[&str] = &["auth", "key", "ip", "host", "hostname", "endpoint", "sid", "steamid", "steam_id", "steamid64", "nonce"];
    // names of people: an account, a player, a character, a mail address
    let bare: String = k.chars().filter(|c| !matches!(c, '_' | '-')).collect();
    const PERSON: &[&str] = &["username", "accountname", "personaname", "playername", "charactername", "email"];
    PART.iter().any(|p| k.contains(p))
        || bare.contains("apikey")
        || PERSON.iter().any(|p| bare.ends_with(p))
        || EXACT.contains(&k.as_str())
        || k.ends_with("address")
        || k.ends_with("url")
        || k.ends_with("steamid")
        || k.ends_with("oauth")
}

/// Blanks the string and number values of secret-looking fields ([`is_secret_key`]) anywhere in `v` with `<redacted>`;
/// objects and arrays under such a name are walked, not dropped (a `host` section of settings stays readable).
pub fn redact_json(v: &mut serde_json::Value) {
    match v {
        serde_json::Value::Object(m) => {
            for (k, x) in m.iter_mut() {
                if is_secret_key(k) && (x.is_string() || x.is_number()) {
                    *x = serde_json::Value::String("<redacted>".into());
                } else {
                    redact_json(x);
                }
            }
        }
        serde_json::Value::Array(a) => a.iter_mut().for_each(redact_json),
        _ => {}
    }
}

/// For JSON that does not parse (or was cut): every `"<secret key>": value` keeps only its name.
fn redact_json_lines(text: &str) -> String {
    let mut out = String::with_capacity(text.len());
    for line in text.split_inclusive('\n') {
        let mut done = false;
        let mut rest = line;
        let mut at = 0;
        while let Some(q) = rest.find('"') {
            let after = &rest[q + 1..];
            let Some(e) = after.find('"') else { break };
            let key = &after[..e];
            let tail = after[e + 1..].trim_start();
            if tail.starts_with(':') && is_secret_key(key) {
                let cut = at + q + 1 + e + 1;
                let colon = line[cut..].find(':').map(|c| cut + c + 1).unwrap_or(cut);
                out.push_str(&line[..colon]);
                out.push_str(" \"<redacted>\"");
                if line.trim_end().ends_with(',') {
                    out.push(',');
                }
                if line.ends_with('\n') {
                    out.push('\n');
                }
                done = true;
                break;
            }
            at += q + 1 + e + 1;
            rest = &after[e + 1..];
        }
        if !done {
            out.push_str(line);
        }
    }
    out
}

// ---------------------------------------------------------------- input and output

/// What the caller knows about where the report runs.
#[derive(Debug, Clone, Default)]
pub struct ReportInput {
    /// The game or server (None: only the system and the launcher log).
    pub install: Option<GameInstall>,
    /// A folder without an install description (inspected; kilnctl's servers, a broken folder).
    pub game_dir: Option<PathBuf>,
    pub dirs: Option<DataDirs>,
    /// Who writes it: `kiln-launcher 0.2.0-dev.22`, `kilnctl 0.2.0-dev.22`.
    pub tool: String,
    /// The launcher's own log (default `<data>/logs/launcher.log`).
    pub launcher_log: Option<PathBuf>,
    /// A Steam Deck / SteamOS / gamescope, when the caller detected it (the launcher's deck detection).
    pub deck: Option<bool>,
    /// Steam roots (default `steam::roots()`).
    pub roots: Option<Vec<SteamRoot>>,
    /// Extra facts for summary.txt (kilnctl: the instance's Wine runner).
    pub facts: Vec<(String, String)>,
    /// (dev.29.18) Findings only the caller can know (the Linux launcher: WebKit's sandbox, `webkit-sandbox`), listed
    /// with the report's own (redacted like them).
    pub findings: Vec<Finding>,
    /// (dev.29) The launcher's update channel: summary.txt, the short summary and report.json say it (a tester's report
    /// says which test build). None: not a launcher (kilnctl) or not known.
    pub channel: Option<ReportChannel>,
    /// Default [`Redactor::current`].
    pub redactor: Option<Redactor>,
    /// (dev.29.22) Files the player leaves out of the zip (the launcher's "What's in this report", `kilnctl report
    /// --exclude`): zip paths, or [`EXCLUDE_OTHER_MODS`] for all of them. Only [`excludable`] files (other mods' and
    /// tools' own files) can be left out; any other name here is ignored, since support needs Kiln's own logs, the
    /// settings and the listings to diagnose anything. A file left out is still read and redacted ([`Report::left_out`],
    /// for [`contents`]) but is not in the zip; summary.txt names it.
    pub exclude: Vec<String>,
}

/// The launcher's channel for a report: the channel its build belongs to, its beta number (a test build), and the
/// channel it follows (the setting).
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ReportChannel {
    /// "stable", "dev" or "beta".
    pub build: String,
    pub beta: Option<u32>,
    pub follows: String,
}

impl ReportChannel {
    /// "Beta, test build 3 (follows Beta)", "Release (follows Release)".
    pub fn words(&self) -> String {
        let name = |c: &str| match c {
            "beta" => "Beta",
            "stable" => "Stable",
            _ => "Release",
        };
        let build = match self.beta {
            Some(n) => format!("{}, test build {n}", name(&self.build)),
            None => name(&self.build).to_string(),
        };
        format!("{build} (follows {})", name(&self.follows))
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, PartialOrd, Ord, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum Level {
    Problem,
    Warning,
    Info,
}

/// One check that explains a common failure, in a plain sentence.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Finding {
    /// Stable name: `not-installed`, `proxy-not-kiln`, `files-missing`, `files-changed`, `not-loaded`,
    /// `proton-override`, `proton-chained-override`, `unknown-build`, `safe-idle`, `own-loader`, `loader-in-folder`, `unlisted-mods`,
    /// `mod-failed`, `hosted-mods`, `hosted-off`, `crash`, `profile-invalid`, `newer-mods`, `no-game`, `sf-startup-failed`, `sf-startup-skipped`,
    /// `sf-mod-bundled-newer`,
    /// `optiscaler`, `install-blocked`; from the launcher ([`ReportInput::findings`]): `webkit-sandbox`, `launcher-files-missing`.
    pub id: String,
    pub level: Level,
    pub text: String,
}

/// What a public post about the report may say of the setup (the launcher's "Send to Kiln support",
/// docs/kiln/launcher/discord.md): versions, the system and how the game runs, how many mods are on. Never which mods.
/// In report.json as `versions` ([`PublicFacts::versions`]).
#[derive(Debug, Clone, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PublicFacts {
    /// Kiln's version in the game (None: not installed).
    pub kiln: Option<String>,
    /// Who wrote the report (`kiln-launcher 0.2.0-dev.29.18`).
    pub tool: String,
    /// The operating system's name (`Arch Linux`, `SteamOS 3.7`, `Windows 11`).
    pub os: String,
    pub deck: bool,
    /// How the game runs: the Proton or Wine build, or `Windows`.
    pub runner: Option<String>,
    /// The game's Steam build.
    pub game_build: Option<String>,
    /// Mods on in the active profile, every kind (None: no profile).
    pub mods_on: Option<usize>,
}

impl PublicFacts {
    /// The launcher's version from `tool` (`kiln-launcher 0.2.0-dev.29.18 beta 3` -> `0.2.0-dev.29.18`).
    pub fn launcher(&self) -> Option<&str> {
        self.tool.strip_prefix("kiln-launcher ")?.split_whitespace().next().filter(|v| v.starts_with(|c: char| c.is_ascii_digit()))
    }

    /// report.json's `versions` (the relay's contract, docs/relay-api.md in the kilnbot repo: `{kiln, launcher, os,
    /// runner, gameBuild, modsOn, deck}`, strings of at most 60 characters, `modsOn` a number, `deck` a boolean). A
    /// fact that is not known is left out.
    pub fn versions(&self) -> serde_json::Value {
        let cut = |s: &str, n: usize| -> Option<String> {
            let s: String = s.chars().filter(|c| !c.is_control()).collect::<String>().trim().chars().take(n).collect();
            (!s.is_empty()).then_some(s)
        };
        let mut v = serde_json::Map::new();
        let mut put = |k: &str, x: Option<String>| {
            if let Some(x) = x {
                v.insert(k.to_string(), serde_json::Value::String(x));
            }
        };
        put("kiln", self.kiln.as_deref().and_then(|x| cut(x, 40)));
        put("launcher", self.launcher().and_then(|x| cut(x, 40)));
        put("os", cut(&self.os, 60));
        put("runner", self.runner.as_deref().and_then(|x| cut(x, 60)));
        put("gameBuild", self.game_build.as_deref().and_then(|x| cut(x, 20)));
        if let Some(n) = self.mods_on {
            v.insert("modsOn".into(), serde_json::json!(n));
        }
        v.insert("deck".into(), serde_json::json!(self.deck));
        serde_json::Value::Object(v)
    }
}

/// report.json alone with only what a public post shows (the launcher's version, the findings, the versions): what
/// "Send to Kiln support" sends in place of the zip when the player chooses that nobody gets the zip (the relay builds
/// the post from it; the privacy page: "only the facts shown in the post go to frostdev.io"). Not when the report was
/// written, not a test build's number, not the sections that could not be read, not the channel.
pub fn public_json(r: &Report) -> Vec<u8> {
    // the relay takes `kiln-launcher <version>`: the version the post shows, without a test build's " beta N"
    let tool = r.facts.launcher().map(|v| format!("kiln-launcher {v}")).unwrap_or_else(|| r.facts.tool.clone());
    serde_json::to_vec_pretty(&serde_json::json!({
        "format": 1, "tool": tool, "findings": r.findings, "versions": r.facts.versions(),
    }))
    .unwrap_or_default()
}

/// A built report. Everything in it is redacted.
#[derive(Debug, Clone, Default, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Report {
    pub written: String,
    /// summary.txt
    pub summary: String,
    /// A few lines for a forum post ("Copy summary").
    pub short: String,
    pub findings: Vec<Finding>,
    /// Sections that could not be read, with why.
    pub errors: Vec<String>,
    /// (0.2.0-dev.29.18+) The setup a public post may name.
    #[serde(default)]
    pub facts: PublicFacts,
    /// (path in the zip, bytes), summary.txt and report.json included.
    #[serde(skip)]
    pub files: Vec<(String, Vec<u8>)>,
    /// (dev.29.22) The files [`ReportInput::exclude`] left out, as they would have been in the zip (redacted): not
    /// written, only described by [`contents`].
    #[serde(skip)]
    pub left_out: Vec<(String, Vec<u8>)>,
}

// ---------------------------------------------------------------- what is in a report (the launcher's preview)

/// [`ReportInput::exclude`]: every other mod's and tool's file ([`excludable`]).
pub const EXCLUDE_OTHER_MODS: &str = "other-mods";

/// The groups "What's in this report" shows a report's files in.
#[derive(Debug, Clone, Copy, PartialEq, Eq, PartialOrd, Ord, Hash, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum FileGroup {
    /// Kiln's and the launcher's own logs, summary.txt, report.json, the crash report.
    Kiln,
    /// Other mods' and tools' own files (their logs; OptiScaler's settings): the player may leave these out.
    Mods,
    /// Kiln's state files, the profile, the chain, the install receipt.
    Settings,
    /// The listings of folders (names, sizes, times; no contents).
    Listings,
}

/// What one file in a report is: its group and a stable name for what it holds (the launcher's words for it are
/// `report.about.<name>`). A file this does not know is another mod's (it can be left out).
pub fn file_kind(path: &str) -> (FileGroup, &'static str) {
    use FileGroup::*;
    let low = path.to_ascii_lowercase();
    let name = low.rsplit('/').next().unwrap_or("");
    match path {
        "summary.txt" => return (Kiln, "summary"),
        "report.json" => return (Kiln, "findings"),
        "launcher/launcher.log" => return (Kiln, "launcher-log"),
        "launcher/game-start.log" => return (Kiln, "game-start"),
        "game/kiln/logs/kiln.log" => return (Kiln, "kiln-log"),
        "game/kiln/logs/proxy.log" => return (Kiln, "proxy-log"),
        "game/kiln/logs/eml.log" => return (Kiln, "eml-log"),
        "game/kiln/logs/tick-stats.jsonl" => return (Kiln, "tick-stats"),
        "game/kiln/logs/greeneye-loader.log" => return (Kiln, "greeneye-bridge"),
        "game/kiln/profile.json" => return (Settings, "profile"),
        "game/kiln/chain.ini" | "game/dinput8-chain.ini" => return (Settings, "chain"),
        "game/kiln/kiln.json" => return (Settings, "kiln-json"),
        "game/kiln/.install.json" => return (Settings, "receipt"),
        "listings/game-folder.txt" => return (Listings, "game-folder"),
        "listings/kiln-folder.txt" => return (Listings, "kiln-folder"),
        "listings/mod-folders.txt" => return (Listings, "mod-folders"),
        _ => {}
    }
    if path.starts_with("game/kiln/crash-reports/") {
        (Kiln, "crash")
    } else if path.starts_with("game/kiln/state/") {
        (Settings, "state")
    } else if path.starts_with("listings/") {
        (Listings, "listing")
    } else if name == engine_mods::XHL_CORE_LOG.to_ascii_lowercase() {
        (Mods, "xhl-core")
    } else if path.starts_with("game/kiln/logs/") {
        (Kiln, "kiln-other")
    } else if path.starts_with("greeneye-mods/") || low.starts_with("game/greeneye/") {
        (Mods, "greeneye")
    } else if name.starts_with("buildingtools") {
        (Mods, "building-tools")
    } else if name.starts_with("shroudlink") {
        (Mods, "shroudlink")
    } else if name == "auto-loot-plus.log" {
        (Mods, "auto-loot")
    } else if name == "optiscaler.ini" {
        (Mods, "optiscaler-settings")
    } else if name == "optiscaler.log" {
        (Mods, "optiscaler-log")
    } else {
        (Mods, "mod-log")
    }
}

/// Whether the player may leave a file out ([`ReportInput::exclude`]): other mods' and tools' files only.
pub fn excludable(path: &str) -> bool {
    file_kind(path).0 == FileGroup::Mods
}

/// What [`redact`] and [`redact_json`] put in place of what they take out, by the name [`contents`] counts them
/// under (`home` for `<home>`).
pub const TOKENS: [&str; 10] = ["<home>", "<user>", "<host>", "<steam-name>", "<steamid>", "<player>", "<ip>", "<mac>", "<email>", "<redacted>"];

/// How often each replacement token stands in a text (only those that do), by name without the brackets.
pub fn replacements(text: &str) -> std::collections::BTreeMap<String, usize> {
    TOKENS.iter()
        .map(|t| (t.trim_matches(|c| c == '<' || c == '>').to_string(), text.matches(t).count()))
        .filter(|(_, n)| *n > 0)
        .collect()
}

/// One file of [`Contents`].
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ContentsFile {
    /// The path in the zip.
    pub path: String,
    pub group: FileGroup,
    /// What it holds ([`file_kind`]).
    pub about: String,
    /// Bytes before compression.
    pub size: u64,
    /// The player may leave it out ([`excludable`]).
    pub excludable: bool,
    /// Left out by [`ReportInput::exclude`]: not in the zip.
    pub excluded: bool,
    /// How often each replacement stands in it (`{"home": 169, "player": 3}`); summary.txt's own "Left out"
    /// section, which names every token once, is not counted.
    pub replaced: std::collections::BTreeMap<String, usize>,
}

/// One thing a report never holds ([`NEVER_COLLECTED`]).
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct NeverItem {
    pub id: String,
    pub text: String,
}

/// "What's in this report" (the launcher's `report_preview`, `kilnctl report --contents`): each file with its size and
/// replacements, those left out, and what a report never holds. Built from a [`Report`], so it describes exactly the
/// zip that report writes.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Contents {
    pub files: Vec<ContentsFile>,
    /// All files in the zip together (before compression).
    pub size: u64,
    /// summary.txt (the launcher's "View").
    pub summary: String,
    pub never: Vec<NeverItem>,
}

/// What a report holds ([`Contents`]).
pub fn contents(r: &Report) -> Contents {
    let own = replacements(LEFT_OUT);
    let line = |path: &str, bytes: &[u8], excluded: bool| {
        let (group, about) = file_kind(path);
        let text = String::from_utf8_lossy(bytes);
        let mut replaced = replacements(&text);
        if path == "summary.txt" && text.contains(LEFT_OUT) {
            for (k, n) in &own {
                if let Some(v) = replaced.get_mut(k) {
                    *v = v.saturating_sub(*n);
                }
            }
            replaced.retain(|_, n| *n > 0);
        }
        ContentsFile { path: path.to_string(), group, about: about.to_string(), size: bytes.len() as u64, excludable: group == FileGroup::Mods, excluded, replaced }
    };
    let mut files: Vec<ContentsFile> = r.files.iter().map(|(n, b)| line(n, b, false)).collect();
    files.extend(r.left_out.iter().map(|(n, b)| line(n, b, true)));
    Contents {
        size: r.files.iter().map(|(_, b)| b.len() as u64).sum(),
        summary: r.summary.clone(),
        never: NEVER_COLLECTED.iter().map(|(id, text)| NeverItem { id: id.to_string(), text: text.to_string() }).collect(),
        files,
    }
}

/// `kiln-report-2026-11-10.zip`
pub fn file_name(t: SystemTime) -> String {
    let (y, mo, d, ..) = crate::util::utc(t);
    format!("kiln-report-{y:04}-{mo:02}-{d:02}.zip")
}

fn rfc(t: SystemTime) -> String {
    crate::util::rfc3339(crate::util::unix(t))
}

// ---------------------------------------------------------------- the system

/// What the operating system is.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct OsInfo {
    /// `Windows 11 (23H2, build 22631)`, `Arch Linux`, `SteamOS`.
    pub name: String,
    pub kernel: Option<String>,
    pub deck: bool,
    /// What said it is a Deck.
    pub deck_signals: Vec<String>,
}

/// `PRETTY_NAME` (else `NAME`) and `ID` of an os-release file.
pub fn os_release(text: &str) -> (Option<String>, Option<String>) {
    let get = |key: &str| {
        text.lines().find_map(|l| l.trim().strip_prefix(key)?.strip_prefix('=').map(|v| v.trim().trim_matches(['"', '\'']).to_string()))
            .filter(|v| !v.is_empty())
    };
    (get("PRETTY_NAME").or_else(|| get("NAME")), get("ID").map(|s| s.to_ascii_lowercase()))
}

/// Windows: ProductName, DisplayVersion and CurrentBuild from the registry (read only).
#[cfg(windows)]
pub fn os_info() -> OsInfo {
    use windows_sys::Win32::System::Registry::HKEY_LOCAL_MACHINE;
    let key = r"SOFTWARE\Microsoft\Windows NT\CurrentVersion";
    let get = |v: &str| crate::steam::win::reg_string(HKEY_LOCAL_MACHINE, key, v);
    let build = get("CurrentBuild");
    let mut product = get("ProductName").unwrap_or_else(|| "Windows".into());
    // Windows 11 still says "Windows 10" in ProductName; its builds start at 22000
    if build.as_deref().and_then(|b| b.parse::<u32>().ok()).is_some_and(|b| b >= 22000) {
        product = product.replace("Windows 10", "Windows 11");
    }
    let detail: Vec<String> = [get("DisplayVersion"), build.map(|b| format!("build {b}"))].into_iter().flatten().collect();
    let wine = std::env::var_os("WINEPREFIX").is_some();
    OsInfo {
        name: format!("{product}{}{}", if detail.is_empty() { String::new() } else { format!(" ({})", detail.join(", ")) },
            if wine { " under Wine" } else { "" }),
        kernel: None,
        deck: false,
        deck_signals: Vec::new(),
    }
}

/// Linux and others: os-release, the kernel, and whether this is a Steam Deck (DMI product name, SteamOS, `SteamDeck=1`).
#[cfg(not(windows))]
pub fn os_info() -> OsInfo {
    let (pretty, id) = std::fs::read_to_string("/etc/os-release").map(|t| os_release(&t)).unwrap_or_default();
    let mut signals = Vec::new();
    if let Ok(p) = std::fs::read_to_string("/sys/class/dmi/id/product_name") {
        let p = p.trim();
        if p.eq_ignore_ascii_case("jupiter") || p.eq_ignore_ascii_case("galileo") {
            signals.push(format!("dmi:{p}"));
        }
    }
    if id.as_deref() == Some("steamos") {
        signals.push("os-release:steamos".into());
    }
    if std::env::var("SteamDeck").is_ok_and(|v| v.trim() == "1") {
        signals.push("SteamDeck=1".into());
    }
    OsInfo {
        name: pretty.unwrap_or_else(|| std::env::consts::OS.to_string()),
        kernel: std::fs::read_to_string("/proc/sys/kernel/osrelease").ok().map(|s| s.trim().to_string()).filter(|s| !s.is_empty()),
        deck: !signals.is_empty(),
        deck_signals: signals,
    }
}

// ---------------------------------------------------------------- reading helpers

fn rel(game: &Path, r: &str) -> PathBuf {
    r.split('/').fold(game.to_path_buf(), |p, c| p.join(c))
}

/// The last `max` bytes of a file (from a line start), CR removed. Err: why it is not readable. A link is never followed
/// (the addon's log sits in the game root, where no Kiln component writes and an unpacked archive can leave one).
fn tail(path: &Path, max: u64) -> std::result::Result<Option<(String, bool)>, String> {
    match std::fs::symlink_metadata(path) {
        Err(e) if e.kind() == std::io::ErrorKind::NotFound => return Ok(None),
        Err(e) => return Err(e.to_string()),
        Ok(m) if crate::quick::import_is_link(&m) => return Err("a link, not followed".into()),
        Ok(m) if !m.is_file() => return Err("not a file".into()),
        Ok(_) => {}
    }
    crate::logs::read_path(path, None, Some(max)).map(|c| Some((c.text, c.truncated))).map_err(|e| e.message)
}

/// The first `max` bytes of a file; (bytes, cut).
fn head(path: &Path, max: u64) -> std::result::Result<Option<(Vec<u8>, bool)>, String> {
    use std::io::Read;
    // as in `tail`: the file itself, never what a link points at (the zip is posted in public)
    match std::fs::symlink_metadata(path) {
        Err(e) if e.kind() == std::io::ErrorKind::NotFound => return Ok(None),
        Err(e) => return Err(e.to_string()),
        Ok(m) if crate::quick::import_is_link(&m) => return Err("a link, not followed".into()),
        Ok(m) if !m.is_file() => return Err("not a file".into()),
        Ok(_) => {}
    }
    let f = match std::fs::File::open(path) {
        Ok(f) => f,
        Err(e) if e.kind() == std::io::ErrorKind::NotFound => return Ok(None),
        Err(e) => return Err(e.to_string()),
    };
    let mut buf = Vec::new();
    f.take(max + 1).read_to_end(&mut buf).map_err(|e| e.to_string())?;
    let cut = buf.len() as u64 > max;
    buf.truncate(max as usize);
    Ok(Some((buf, cut)))
}

/// `<kind> <size> <modified> <path>` lines, `depth` levels below `root` (symlinks are listed, never followed).
fn listing(root: &Path, prefix: &str, depth: usize, out: &mut Vec<String>, skip: &dyn Fn(&str) -> bool) {
    let Ok(rd) = std::fs::read_dir(root) else { return };
    let mut entries: Vec<_> = rd.flatten().collect();
    entries.sort_by_key(|e| e.file_name());
    for e in entries {
        if out.len() >= LIST_MAX {
            out.push(format!("... (cut at {LIST_MAX} lines)"));
            return;
        }
        let name = e.file_name().to_string_lossy().into_owned();
        let path = if prefix.is_empty() { name.clone() } else { format!("{prefix}/{name}") };
        if skip(&path) {
            continue;
        }
        let Ok(md) = std::fs::symlink_metadata(e.path()) else {
            out.push(format!("?  -  -  {path}"));
            continue;
        };
        let when = md.modified().map(|t| crate::util::fmt_utc(t)).unwrap_or_else(|_| "-".into());
        let (k, size) = if md.file_type().is_symlink() {
            ("l", "-".to_string())
        } else if md.is_dir() {
            ("d", "-".to_string())
        } else {
            ("f", md.len().to_string())
        };
        out.push(format!("{k}  {size:>10}  {when}  {path}{}", if k == "d" { "/" } else { "" }));
        if k == "d" && depth > 1 {
            listing(&e.path(), &path, depth - 1, out, skip);
            if out.last().is_some_and(|l| l.starts_with("... (cut")) {
                return;
            }
        }
    }
}

/// What a proxy DLL is, for a person: `Kiln 0.2.0-dev.21`, `EML (Enshrouded Mod Loader) (text eml_log_file_path)`.
fn proxy_text(c: &ProxyClass) -> String {
    let name = match c.kind {
        ProxyKind::None => return "missing".into(),
        ProxyKind::Unknown if c.detail.as_deref() == Some(crate::pe::STEAMWORKS_DETAIL) => return format!("Steam's own Steamworks DLL{}",
            c.size.map(|n| format!(", {}", crate::util::human_size(n))).unwrap_or_default()),
        ProxyKind::Unknown => "an unknown DLL".to_string(),
        ProxyKind::NotDll => "not a 64-bit DLL".to_string(),
        k => crate::engines::Engine::of_proxy(k).map(|e| e.name().to_string()).unwrap_or_else(|| format!("{k:?}")),
    };
    let mut s = name;
    if let Some(v) = &c.version {
        let _ = write!(s, " {v}");
    }
    if let Some(n) = c.size {
        let _ = write!(s, ", {}", crate::util::human_size(n));
    }
    if let Some(d) = c.detail.as_deref().filter(|_| c.kind != ProxyKind::Kiln) {
        let _ = write!(s, " ({d})");
    }
    s
}

/// An engine's name without its explanation: `EML`, `Ember`.
fn short_name(e: crate::engines::Engine) -> &'static str {
    let n = e.name();
    n.split(" (").next().unwrap_or(n)
}

fn kind_name(k: ModKind) -> &'static str {
    match k.engine() {
        Some(e) => short_name(e),
        None => match k {
            ModKind::Kiln => "Kiln",
            ModKind::Native => "native",
            _ => "plugin",
        },
    }
}

// ---------------------------------------------------------------- engine state files

type Obj = serde_json::Map<String, serde_json::Value>;

fn jstr(v: Option<&serde_json::Value>) -> Option<String> {
    match v? {
        serde_json::Value::String(s) if !s.is_empty() => Some(s.clone()),
        serde_json::Value::Number(n) => Some(n.to_string()),
        _ => None,
    }
}

/// The state word of an engine plugin's state file (the launcher's `headline` in components/EngineStatus.tsx).
pub fn engine_headline(state: &serde_json::Value) -> Option<String> {
    let o = state.as_object()?;
    if let Some(w) = jstr(o.get("status")).or_else(|| jstr(o.get("phase"))).or_else(|| jstr(o.get("mode"))).or_else(|| jstr(o.get("state"))) {
        return Some(w);
    }
    if o.get("steppedAside").and_then(|v| v.as_bool()) == Some(true) {
        return Some("stepped aside".into());
    }
    if o.get("abi").is_some_and(|a| a.is_null()) {
        return Some("waiting for Ember's ABI".into());
    }
    o.get("started").and_then(|v| v.as_bool()).map(|s| if s { "running" } else { "idle" }.to_string())
}

/// (id, state, reason) of each mod in a state file (the launcher's `modLines`).
pub fn engine_mod_lines(state: &serde_json::Value) -> Vec<(String, Option<String>, Option<String>)> {
    let Some(mods) = state.get("mods").and_then(|m| m.as_array()) else { return Vec::new() };
    mods.iter()
        .filter_map(|m| m.as_object())
        .map(|m: &Obj| {
            let id = jstr(m.get("id")).or_else(|| jstr(m.get("name"))).or_else(|| jstr(m.get("folder"))).unwrap_or_else(|| "?".into());
            let state = jstr(m.get("state")).or_else(|| jstr(m.get("status"))).or_else(|| jstr(m.get("action")))
                .or_else(|| m.get("active").and_then(|a| a.as_bool()).map(|a| if a { "active" } else { "off" }.to_string()));
            let reason = jstr(m.get("reason")).or_else(|| jstr(m.get("detail"))).or_else(|| jstr(m.get("error"))).or_else(|| jstr(m.get("note")));
            (id, state, reason)
        })
        .collect()
}

/// An engine plugin's `own-loader` reason for a mod the profile has off that ran from a folder or file of the player's own,
/// which Kiln never writes into: kiln-eml's "… although it is off in the profile: it is your own folder …", the compat
/// loaders' "runs (your own folder …; …) although it is off in the profile: …" (compat-aside-truth, own_off.h). Kiln's copy
/// ("runs (Kiln's copy … is still there; …)") is not: the next Play takes it out.
fn ran_off_from_own(reason: &str) -> bool {
    let Some((head, tail)) = reason.split_once(" although it is off in the profile: ") else { return false };
    tail.starts_with("it is your own folder ") || head.starts_with("runs (your own folder ") || head.starts_with("runs (your own file ")
}

/// The plugin stood aside for the engine's own loader (EML `aside`, ShroudForge `self-hosted`, `steppedAside`).
fn stood_aside(state: &serde_json::Value) -> bool {
    state.get("steppedAside").and_then(|v| v.as_bool()) == Some(true)
        || engine_headline(state).is_some_and(|w| {
            let w = w.to_ascii_lowercase();
            w.contains("aside") || w == "self-hosted"
        })
}

/// kiln-eml stood aside for ShroudForge, not for EML's own loader (docs/kiln/eml.md, "ShroudForge asset mods and EML mods
/// together"): ShroudForge is the one writer of the game data in such a start and runs the EML packages that are switched
/// on in its mods folder. kiln-eml's `aside` is the reason Kiln's ShroudForge plugin gives it when it takes the start's
/// data writer (it starts with the engine's name; the reason for EML's own loader names the loader's DLL), so kiln-eml's
/// own file says whom it stood aside for. Whether that file is of the last start is its pid's to say: the pid of
/// kiln/state/shroudforge.json when that says `hosted` and is of the last start (`sf_hosted_pid`), or the last start's
/// own (`last_pid`: kiln.log's last boot, else boot.json). A player's report of 13 Nov 2026 read "EML's own loader runs
/// in the game" in a start that had no such loader; KR-YNQZ (26 Nov) again, written before ShroudForge's state file.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum EmlHandoff {
    /// In the last start; `kiln_hosted`: no ShroudForge state of that start says another mode than `hosted`.
    LastStart { kiln_hosted: bool },
    /// In the start the file is of (`pid`), which is not known to be the last start.
    OtherStart { pid: Option<u64> },
}

/// The EML packs kiln-eml's file says run under ShroudForge (`own-loader` rows: "runs under ShroudForge ...").
fn handed_packs(state: &serde_json::Value) -> usize {
    engine_mod_lines(state).iter().filter(|(_, st, _)| st.as_deref() == Some("own-loader")).count()
}

fn eml_handoff(kind: ModKind, state: &serde_json::Value, sf: Option<(u64, &str)>, sf_hosted_pid: Option<u64>,
               last_pid: Option<u64>) -> Option<EmlHandoff> {
    if kind != ModKind::Eml || !jstr(state.get("aside")).is_some_and(|a| a.starts_with("ShroudForge ")) { return None; }
    let pid = state.get("pid").and_then(|v| v.as_u64());
    if pid.is_none() || (pid != sf_hosted_pid && pid != last_pid) { return Some(EmlHandoff::OtherStart { pid }); }
    let kiln_hosted = !sf.is_some_and(|(p, mode)| Some(p) == pid && mode != "hosted");
    Some(EmlHandoff::LastStart { kiln_hosted })
}

/// kiln-emf stood aside because EMF's own loader or core runs in the game (kiln-emf's `steppedAsideReason`, plugin.cpp
/// emf_in_process): the reason, for the finding. EMF then runs the EMF mods of emf\mods itself.
fn emf_runs_its_own(kind: ModKind, state: &serde_json::Value) -> Option<String> {
    if kind != ModKind::Emf { return None; }
    jstr(state.get("steppedAsideReason")).filter(|r| r.starts_with("EMF's loader is loaded") || r.starts_with("EMF's core is loaded"))
}

fn failed_word(w: &str) -> bool {
    let w = w.to_ascii_lowercase();
    w.contains("fail") || w.contains("error") || w.contains("refus")
}

// ---------------------------------------------------------------- mods in the game folder

/// The game's `kiln/profile.json` as a launcher profile, for [`crate::found::scan`]: the same lists and the same
/// `dropins`, so the report judges the game folder by what the game reads, with the rules of the launcher's Game folder
/// panel.
pub fn profile_view(file: &ProfileFile) -> crate::profile::Profile {
    use crate::profile::ProfileMod;
    let mut mods = crate::profile::ProfileMods::default();
    for kind in ModKind::ALL {
        let list: Vec<ProfileMod> = match file.mods.folders(kind) {
            Some(l) => l.iter().map(|m| ProfileMod {
                id: m.id.clone(), version: m.version.clone().unwrap_or_default(), hash: m.hash.clone().unwrap_or_default(), enabled: m.enabled,
            }).collect(),
            None => file.mods.plugin.iter().map(|m| ProfileMod {
                id: m.id.clone().unwrap_or_else(|| {
                    let name = m.path.rsplit(['/', '\\']).next().unwrap_or_default();
                    name.strip_suffix(".dll").or_else(|| name.strip_suffix(".DLL")).unwrap_or(name).to_string()
                }),
                version: m.version.clone().unwrap_or_default(), hash: m.hash.clone().unwrap_or_default(), enabled: m.enabled,
            }).collect(),
        };
        *mods.list_mut(kind) = list;
    }
    let dropins = match &file.dropins {
        Dropins::All(true) => ModKind::ALL.to_vec(),
        Dropins::All(false) => Vec::new(),
        Dropins::Kinds(l) => l.clone(),
    };
    crate::profile::Profile {
        format: 1,
        id: file.id.clone(),
        name: file.name.clone().unwrap_or_else(|| file.id.clone()),
        created: String::new(),
        updated: String::new(),
        generation: file.generation,
        notes: String::new(),
        mods,
        dropins,
        dropins_engines: true,
        data_dir: Default::default(),
        server: file.server.clone(),
        late: Vec::new(),
    }
}

// ---------------------------------------------------------------- the report

struct Ctx<'a> {
    game: Option<&'a Path>,
    red: &'a Redactor,
    exclude: &'a [String],
    files: Vec<(String, Vec<u8>)>,
    left_out: Vec<(String, Vec<u8>)>,
    total: usize,
    errors: Vec<String>,
    findings: Vec<Finding>,
}

impl Ctx<'_> {
    fn err(&mut self, what: &str, why: impl std::fmt::Display) {
        self.errors.push(redact(&format!("{what}: {why}"), self.red));
    }

    fn find(&mut self, id: &str, level: Level, text: impl Into<String>) {
        self.findings.push(Finding { id: id.into(), level, text: redact(&text.into(), self.red) });
    }

    /// Adds a text file (redacted); cut to what is left of [`TOTAL_MAX`].
    fn add_text(&mut self, name: &str, text: &str, note: Option<&str>) {
        let mut t = redact(text, self.red);
        if let Some(n) = note {
            t = format!("{n}\n{t}");
        }
        // left out by the player: described, never in the zip, and takes nothing of TOTAL_MAX
        if excludable(name) && self.exclude.iter().any(|e| e == name || e == EXCLUDE_OTHER_MODS) {
            self.left_out.push((name.to_string(), t.into_bytes()));
            return;
        }
        let left = TOTAL_MAX.saturating_sub(self.total);
        if t.len() > left {
            let mut cut = left.saturating_sub(64);
            while cut > 0 && !t.is_char_boundary(cut) {
                cut -= 1;
            }
            t.truncate(cut);
            t.push_str("\n... (the report's size limit cut this file)\n");
            self.errors.push(format!("{name}: cut, the report reached its size limit"));
        }
        self.total += t.len();
        self.files.push((name.to_string(), t.into_bytes()));
    }

    /// A game file's tail (logs).
    fn add_tail(&mut self, r: &str, max: u64) -> Option<String> {
        let game = self.game?;
        match tail(&rel(game, r), max) {
            Ok(Some((text, cut))) => {
                let note = cut.then(|| format!("... (older lines left out: the last {} only)", crate::util::human_size(max)));
                self.add_text(&format!("game/{r}"), &text, note.as_deref());
                Some(text)
            }
            Ok(None) => None,
            Err(e) => {
                self.err(&format!("game/{r}"), format!("could not read: {e}"));
                None
            }
        }
    }

    /// A file's tail by its own path (a Greeneye mod's log in its folder, which may be in the launcher's Library).
    fn add_tail_at(&mut self, name: &str, p: &Path, max: u64) {
        match tail(p, max) {
            Ok(Some((text, cut))) => {
                let note = cut.then(|| format!("... (older lines left out: the last {} only)", crate::util::human_size(max)));
                self.add_text(name, &text, note.as_deref());
            }
            Ok(None) => {}
            Err(e) => self.err(name, format!("could not read: {e}")),
        }
    }

    /// A JSON or INI game file, redacted (JSON field by field when it parses).
    fn add_file(&mut self, r: &str) {
        let Some(game) = self.game else { return };
        self.add_path(&format!("game/{r}"), &rel(game, r));
    }

    fn add_path(&mut self, name: &str, p: &Path) {
        match head(p, FILE_MAX) {
            Ok(None) => {}
            Err(e) => self.err(name, format!("could not read: {e}")),
            Ok(Some((bytes, cut))) => {
                let text = String::from_utf8_lossy(bytes.strip_prefix(b"\xef\xbb\xbf").unwrap_or(&bytes)).replace('\r', "");
                let json = name.ends_with(".json");
                let parsed = (json && !cut).then(|| serde_json::from_str::<serde_json::Value>(&text).ok()).flatten();
                let body = match parsed {
                    Some(mut v) => {
                        redact_json(&mut v);
                        serde_json::to_string_pretty(&v).unwrap_or_default()
                    }
                    None if json => redact_json_lines(&text),
                    None => text,
                };
                let note = if cut { Some(format!("... (only the first {} of this file)", crate::util::human_size(FILE_MAX))) } else { None };
                self.add_text(name, &body, note.as_deref());
            }
        }
    }
}

/// Reads everything a support report holds. Never fails: a section that cannot be read is named in `errors` (and in
/// summary.txt) and the rest is still there.
pub fn build(input: &ReportInput) -> Report {
    let now = SystemTime::now();
    let roots = input.roots.clone().unwrap_or_else(|| crate::steam::roots().unwrap_or_default());
    let mut red = input.redactor.clone().unwrap_or_else(|| Redactor::current(&roots));
    let dirs = input.dirs.clone().or_else(|| DataDirs::locate().ok());
    let install: Option<GameInstall> = input.install.clone().or_else(|| {
        let d = input.game_dir.as_ref()?;
        let data_root = dirs.as_ref().map(|d| d.root.clone()).unwrap_or_default();
        crate::game::inspect_with(d, &roots, &data_root).ok()
    });
    let game_dir: Option<PathBuf> = install.as_ref().map(|i| i.path.clone()).or_else(|| input.game_dir.clone());
    // other people's names: the players the logs in the report name (a co-op host's friends, a server's players)
    if let Some(g) = game_dir.as_deref() {
        for (r, max) in [("kiln/logs/kiln.log", KILN_LOG_MAX), ("shroudlink.log", SMALL_LOG_MAX)] {
            if let Ok(Some((t, _))) = tail(&rel(g, r), max) {
                red.add_players_from(&t);
            }
        }
    }
    let mut cx = Ctx { game: game_dir.as_deref(), red: &red, exclude: &input.exclude, files: Vec::new(), left_out: Vec::new(), total: 0, errors: Vec::new(),
        findings: Vec::new() };
    let mut s = String::new();
    let tool = if input.tool.is_empty() { format!("kiln-manage {}", crate::VERSION) } else { input.tool.clone() };

    let _ = writeln!(s, "Kiln support report");
    let _ = writeln!(s, "Written {} by {tool} (kiln-manage {}).", rfc(now), crate::VERSION);
    let _ = writeln!(s, "Paths, user and machine names are replaced: see \"Left out\" at the end.");

    // ---- system
    let os = os_info();
    let deck = input.deck.unwrap_or(os.deck);
    let mut sys = String::new();
    let _ = writeln!(sys, "OS:            {}{}", os.name, os.kernel.as_deref().map(|k| format!(" (kernel {k})")).unwrap_or_default());
    let _ = writeln!(sys, "Steam Deck:    {}{}", if deck { "yes" } else { "no" },
        if os.deck_signals.is_empty() { String::new() } else { format!(" ({})", os.deck_signals.join(", ")) });
    if let Some(c) = &input.channel {
        let _ = writeln!(sys, "{:<15}{}", "Channel:", c.words());
    }
    for (k, v) in &input.facts {
        let _ = writeln!(sys, "{:<15}{v}", format!("{k}:"));
    }
    for f in &input.findings {
        cx.find(&f.id, f.level, f.text.clone());
    }

    let Some(game) = game_dir.as_deref() else {
        cx.find("no-game", Level::Info, "No game folder was given: this report has the system and the launcher's log only.");
        return finish(cx, s, sys, String::new(), input, dirs.as_ref(), &tool, now, &Facts::default());
    };
    let mut facts = Facts::default();
    let mut g = String::new();

    // ---- the game
    let exists = game.is_dir();
    let _ = writeln!(g, "Folder:        {}{}", game.display(), if exists { "" } else { " (does not exist)" });
    if !exists {
        cx.err("game folder", "does not exist or is not readable");
    }
    if input.game_dir.is_some() && input.install.is_none() && install.is_none() && exists {
        cx.err("game folder", "has neither enshrouded.exe nor enshrouded_server.exe");
    }
    if let Some(i) = &install {
        let side = match i.kind { GameKind::Client => "client", GameKind::Server => "dedicated server" };
        let _ = writeln!(g, "Kind:          {side} ({}), {:?}, found by {}", i.exe, i.platform, i.source);
        let _ = writeln!(g, "Steam build:   {}", i.build_id.as_deref().unwrap_or("unknown"));
        if let Some(p) = &i.prefix {
            let _ = writeln!(g, "Prefix:        {}", p.display());
        }
        // how the launcher starts it: through Steam, or directly (direct.rs) with its umu-run, Proton or Wine and prefix
        let start = (i.kind == GameKind::Client).then(|| {
            let settings = dirs.as_ref().map(|d| crate::direct::settings(d, i)).unwrap_or_default();
            (crate::direct::effective_mode(i, Some(&settings)), settings)
        });
        let direct = matches!(start, Some((crate::direct::StartMode::Direct, _)));
        facts.direct = direct;
        // compat tool and Proton version: Steam's config.vdf (a start through Steam only) and the prefix's version file, read only
        if i.platform == Platform::Proton {
            let tool = (!direct).then(|| roots.iter().find_map(|r| crate::steam::compat_tool(r, i.app_id).ok().flatten())).flatten();
            let version = i.prefix.as_ref().and_then(|p| p.parent()).and_then(|c| std::fs::read_to_string(c.join("version")).ok())
                .map(|v| v.trim().to_string()).filter(|v| !v.is_empty());
            if direct {
                let _ = writeln!(g, "Prefix Proton: {}", version.as_deref().unwrap_or("unknown (no version file yet)"));
            } else {
                let _ = writeln!(g, "Compat tool:   {} (prefix version: {})", tool.as_deref().unwrap_or("Steam's default"), version.as_deref().unwrap_or("unknown"));
            }
            facts.proton = tool.or(version);
        }
        if crate::direct::is_custom(i) {
            let _ = writeln!(g, "Custom folder: yes (no Steam library knows it)");
        }
        match &start {
            Some((crate::direct::StartMode::Direct, settings)) => {
                let how = if cfg!(windows) {
                    "the launcher starts enshrouded.exe itself".to_string()
                } else {
                    match crate::direct::plan(i, settings, &roots) {
                        Ok(c) => {
                            let r = c.runner.as_ref();
                            let env = |k: &str| c.env.iter().find(|(n, _)| n == k).map(|(_, v)| v.clone());
                            if let Some(r) = r && r.kind != crate::direct::RunnerKind::Wine {
                                facts.proton.get_or_insert_with(|| r.proton.as_ref().and_then(|p| p.file_name()).map(|n| n.to_string_lossy().into_owned())
                                    .unwrap_or_else(|| r.label.clone()));
                            }
                            format!("{}{}{} (WINEDLLOVERRIDES {})", r.map(|r| format!("{:?} {}", r.kind, r.label)).unwrap_or_default(),
                                r.and_then(|r| r.proton.as_ref()).map(|p| format!(" with {}", p.file_name().map(|n| n.to_string_lossy().into_owned()).unwrap_or_default())).unwrap_or_default(),
                                r.map(|r| if r.source == "custom" { ", the player's choice" } else if settings.runner.is_none() { ", automatic" } else { "" }).unwrap_or_default(),
                                env("WINEDLLOVERRIDES").unwrap_or_else(|| "-".into()))
                        }
                        Err(e) => format!("cannot start: {}", e.message),
                    }
                };
                let _ = writeln!(g, "Start:         directly ({how})");
                if let Some(d) = dirs.as_ref() {
                    cx.add_tail_at("launcher/game-start.log", &crate::launch::direct_log(d, i), SMALL_LOG_MAX);
                }
            }
            Some((crate::direct::StartMode::Steam, _)) => {
                let _ = writeln!(g, "Start:         through Steam (steam://rungameid/{})", i.app_id);
            }
            None => {}
        }
        if let Some(lo) = (!direct).then(|| crate::game::launch_options_info(i, &roots)).flatten() {
            let _ = writeln!(g, "Launch opts:   {} (dinput8 override: {}; chained xinput9_1_0 override missing: {})", lo.value.as_deref().unwrap_or("(none)"), if lo.has_dll_override { "yes" } else { "no" }, if lo.chained_override_missing { "yes" } else { "no" });
            facts.override_missing = !lo.has_dll_override && !lo.dinput8_native_by_runner;
            facts.has_dinput8_override = lo.has_dll_override;
            if lo.chained_override_missing {
                cx.find("proton-chained-override", Level::Warning, format!("Ultimate ASI Loader is chained as xinput9_1_0.dll and neither Steam's launch options nor the prefix registry sets xinput9_1_0 to native first: under Valve's Proton, Wine loads its own xinput9_1_0 instead and the loader with its .asi plugins does not start (GE-Proton loads it without). Set: {}", lo.advice.as_deref().unwrap_or(crate::game::CHAINED_DLL_OVERRIDE_ADVICE)));
            }
            facts.override_advice = lo.fallback_advice;
        }
        let running = crate::proc::find_game(i).ok().flatten();
        facts.running = running.is_some();
        let _ = writeln!(g, "Running:       {}", match &running {
            Some(p) => format!("yes (pid {}{})", p.pid, crate::proc::start_rfc3339(p.pid).map(|t| format!(", since {t}")).unwrap_or_default()),
            None => "no".into(),
        });
        facts.side = Some(if i.kind == GameKind::Client { "client" } else { "server" });
        facts.platform = Some(i.platform);
        facts.build = i.build_id.clone();
    }
    // the exe's build and whether Kiln has a verified record for it (kiln/sigdb/builds/<side>/<sha256>.json)
    let exe = install.as_ref().map(|i| i.exe.clone()).or_else(|| {
        [crate::game::CLIENT_EXE, crate::game::SERVER_EXE].into_iter().find(|e| game.join(e).is_file()).map(str::to_string)
    });
    if let Some(exe) = &exe {
        let side = if exe == crate::game::SERVER_EXE { "server" } else { "client" };
        facts.side.get_or_insert(side);
        match crate::hash::sha256_file(&game.join(exe)) {
            Ok((sha, _)) => {
                let record = game.join("kiln/sigdb/builds").join(side).join(format!("{sha}.json"));
                let verified = std::fs::read(&record).ok().and_then(|b| serde_json::from_slice::<serde_json::Value>(&b).ok())
                    .map(|v| v.get("verified").and_then(|x| x.as_bool()).unwrap_or(false));
                facts.known_build = Some(verified);
                facts.exe_sha = Some(sha.clone());
                let _ = writeln!(g, "Exe sha256:    {sha}");
                let _ = writeln!(g, "Known to Kiln: {}", match verified {
                    Some(true) => "yes (a verified build record)",
                    Some(false) => "a build record, not verified",
                    None => "no build record",
                });
            }
            Err(e) => cx.err(&format!("game/{exe}"), format!("could not hash it: {}", e.message)),
        }
    }

    // ---- Kiln in the game
    let mut k = String::new();
    let receipt = match crate::install::read_receipt(&game) {
        Ok(r) => r,
        Err(e) => {
            cx.err("kiln/.install.json", &e.message);
            None
        }
    };
    let proxies = crate::engines::scan_proxies(&game);
    let dinput8 = proxies.iter().find(|p| p.name.eq_ignore_ascii_case("dinput8.dll")).map(|p| p.class.clone());
    match &receipt {
        Some(r) => {
            let _ = writeln!(k, "Installed:     Kiln {} on {} by {} ({} files, components {})", r.runtime.version,
                r.updated.as_deref().unwrap_or(&r.installed), r.installer.as_deref().unwrap_or("?"), r.files.len(), r.runtime.components.join(", "));
            facts.kiln = Some(r.runtime.version.clone());
        }
        None => {
            let _ = writeln!(k, "Installed:     no install receipt (kiln/.install.json)");
        }
    }
    check_install(&mut cx, &game, receipt.as_ref(), dinput8.as_ref());
    facts.game = Some(game.to_path_buf());
    facts.receipt_at = receipt.as_ref().and_then(|r| crate::util::parse_rfc3339(r.updated.as_deref().unwrap_or(&r.installed)));
    let _ = writeln!(k, "Proxies next to the game:");
    if proxies.is_empty() {
        let _ = writeln!(k, "  (none)");
    }
    for p in &proxies {
        let _ = writeln!(k, "  {:<16} {}", p.name, proxy_text(&p.class));
    }
    // (dev.29.18) OptiScaler next to the game, under any of its names: what Kiln's overlay does next to it (kiln.ui
    // decides the same from the same OptiScaler.ini: native/services/ui/overlay_optiscaler.h)
    for g in crate::game::graphics_tools(&game) {
        let _ = writeln!(k, "Graphics tool: {} ({}): Kiln's overlay {}", g.engine.name(), g.file,
            if g.kiln_overlay { "draws next to it (its own menu and frame generation are off in OptiScaler.ini)" } else { "drawing steps aside" });
        cx.find("optiscaler", Level::Info, format!("{} is installed ({}). {}", g.engine.name(), g.file, g.text));
        // its settings (OverlayMenu, frame generation), and the tail of its own log when it writes one ([Log]
        // LogToFile=true; it can grow large, so a log's budget, as for the other tools' logs), under their names on disk
        // (a Linux folder is case sensitive; the finding read the ini in any case)
        let on_disk = |name: &str| {
            std::fs::read_dir(&game).into_iter().flatten().flatten()
                .map(|e| e.file_name().to_string_lossy().into_owned())
                .find(|n| n.eq_ignore_ascii_case(name))
                .unwrap_or_else(|| name.to_string())
        };
        cx.add_file(&on_disk("OptiScaler.ini"));
        cx.add_tail(&on_disk("OptiScaler.log"), SMALL_LOG_MAX);
    }
    match crate::chain::read_raw(&game) {
        Ok(t) if t.trim().is_empty() => {
            let _ = writeln!(k, "chain.ini:     (none)");
        }
        Ok(t) => {
            let _ = writeln!(k, "chain.ini:");
            let mut shown: String = t.chars().take(CHAIN_IN_SUMMARY).collect();
            if shown.len() < t.len() {
                shown.push_str("\n... (cut; the whole file is in game/kiln/chain.ini)");
            }
            for l in shown.replace('\r', "").lines() {
                let _ = writeln!(k, "  {l}");
            }
        }
        Err(e) => cx.err("kiln/chain.ini", e.message),
    }

    // ---- profile
    let mut pr = String::new();
    let profile = match crate::profile::read_game_profile(&game) {
        Ok(p) => p,
        Err(e) => {
            cx.err("kiln/profile.json", &e.message);
            cx.find("profile-invalid", Level::Problem, format!("kiln/profile.json is not readable ({}). Kiln loads no mods with a broken profile: activate the profile again in the launcher.", e.message));
            facts.profile_broken = true;
            None
        }
    };
    match &profile {
        Some(p) => {
            let dropins = match &p.dropins {
                Dropins::All(true) => "all".to_string(),
                Dropins::All(false) => "none".to_string(),
                Dropins::Kinds(l) => l.iter().map(|k| k.as_str()).collect::<Vec<_>>().join(", "),
            };
            let _ = writeln!(pr, "Active:        {} ({}), generation {}, drop-ins: {dropins}", p.name.as_deref().unwrap_or("?"), p.id, p.generation);
            let mut n = 0;
            let mut off = 0;
            for kind in ModKind::ALL {
                let rows: Vec<(String, Option<String>, bool)> = match p.mods.folders(kind) {
                    Some(l) => l.iter().map(|m| (m.id.clone(), m.version.clone(), m.enabled)).collect(),
                    None => p.mods.plugin.iter().map(|m| (m.id.clone().unwrap_or_else(|| m.path.rsplit(['/', '\\']).next().unwrap_or("?").to_string()), m.version.clone(), m.enabled)).collect(),
                };
                for (id, v, on) in rows {
                    n += 1;
                    // an EML mod the player has on that activation could not apply (emlRuntimeBlocked) is written
                    // enabled: false; it is the player's "on", not applied (inbox 38)
                    let blocked = (kind == ModKind::Eml && !on).then(|| p.eml_runtime_blocked.get(&id)).flatten();
                    off += usize::from(!on && blocked.is_none());
                    let state = match blocked {
                        Some(why) => format!("on, not applied: {why}"),
                        None => (if on { "on" } else { "off" }).to_string(),
                    };
                    let _ = writeln!(pr, "  {:<12} {:<32} {:<16} {state}", kind.as_str(), id, v.as_deref().unwrap_or("-"));
                }
            }
            if n == 0 {
                let _ = writeln!(pr, "  (no mods)");
            }
            facts.profile = Some(format!("{} ({}{})", p.name.as_deref().unwrap_or(&p.id), plural(n, "mod", "mods"), if off > 0 { format!(", {off} off") } else { String::new() }));
            facts.mods_on = Some(n.saturating_sub(off));
        }
        None if !facts.profile_broken => {
            let _ = writeln!(pr, "Active:        none (legacy layout: the drop-in folders load)");
            facts.profile = Some("none (legacy layout)".into());
        }
        None => {
            let _ = writeln!(pr, "Active:        kiln/profile.json is broken");
        }
    }
    check_unlisted(&mut cx, install.as_ref(), dirs.as_ref(), profile.as_ref());
    if let (Some(g), Some(p)) = (install.as_ref(), profile.as_ref()) {
        let fallback = DataDirs::at(g.path.join("kiln/report-no-library"));
        match crate::compat::shroudlink_report_missing(dirs.as_ref().unwrap_or(&fallback), g, p) {
            Ok(true) => {
                let mods = p.mods.shroudlink.iter().filter(|m| m.enabled).map(|m| m.id.clone()).collect::<Vec<_>>();
                cx.find("shroudlink-client-addon", Level::Info, shroudlink_finding_text(p.name.as_deref().unwrap_or(&p.id), &mods));
            }
            Err(e) => cx.err("shroudlink-client-addon", &e.message),
            _ => {}
        }
    }

    // ---- the last start
    let mut ls = String::new();
    let boot = match crate::state::read_boot(&game) {
        Ok(b) => b,
        Err(e) => {
            cx.err("kiln/state/boot.json", e.message);
            None
        }
    };
    let boot_time = std::fs::metadata(game.join(crate::paths::game::BOOT_JSON)).and_then(|m| m.modified()).ok();
    match &boot {
        Some(b) => {
            let _ = writeln!(ls, "boot.json:     Kiln {} ({}) at {}, pid {}, hooks {}, policy {}{}", b.kiln, format!("{:?}", b.side).to_lowercase(), b.written, b.pid,
                if b.hooks { "on" } else { "off" }, b.policy.as_deref().unwrap_or("?"),
                b.boot_ms.map(|m| format!(", boot {m:.0} ms")).unwrap_or_default());
            if let Some(e) = &b.exe {
                let _ = writeln!(ls, "Exe:           {} verified {}", e.name.as_deref().unwrap_or("?"), e.verified.map(|v| if v { "yes" } else { "no" }).unwrap_or("?"));
            }
            let _ = writeln!(ls, "Profile:       {:?}{}{}", b.profile.mode, b.profile.id.as_deref().map(|i| format!(" {i}, generation {}", b.profile.generation)).unwrap_or_default(),
                b.profile.error.as_deref().map(|e| format!(" ({e})")).unwrap_or_default());
            if let Some(p) = &b.play_once {
                let _ = writeln!(ls, "Play once:     {}", p.mode);
            }
            if let Some(x) = &b.safe_idle {
                let _ = writeln!(ls, "Safe idle:     {x}");
            }
            for c in &b.chain {
                let _ = writeln!(ls, "  chain    {:<8} {}{}", format!("{:?}", c.status).to_lowercase(), c.path,
                    c.detail.map(|d| format!(" ({})", format!("{d:?}").to_lowercase())).unwrap_or_default());
            }
            for p in &b.plugins {
                let _ = writeln!(ls, "  plugin   {:<8} {}{}{}", p.status, p.file, p.version.as_deref().map(|v| format!(" {v}")).unwrap_or_default(),
                    p.error.as_deref().map(|e| format!(": {e}")).unwrap_or_default());
            }
            for e in &b.errors {
                let _ = writeln!(ls, "  error    {e}");
            }
            facts.last_start = Some(format!("{}, hooks {}", b.written, if b.hooks { "on" } else { "off" }));
        }
        None => {
            let _ = writeln!(ls, "boot.json:     none (Kiln has not started in this game, or this is not where it writes)");
        }
    }
    let mods = crate::state::read_mods(&game).unwrap_or_else(|e| {
        cx.err("kiln/state/mods.json", e.message);
        None
    });
    let native = crate::state::read_native(&game).unwrap_or_else(|e| {
        cx.err("kiln/state/native.json", e.message);
        None
    });
    let engines = engine_mods::status(&game);
    let mut failed: Vec<String> = Vec::new();
    // a client's server mods: skipped in the menu, they start with the player's own world (docs/kiln/client.md)
    let (mut hosted, mut hosted_off): (Vec<&str>, Vec<&str>) = (Vec::new(), Vec::new());
    if let Some(m) = &mods {
        let _ = writeln!(ls, "Kiln mods (mods.json, {}):", m.written);
        for x in &m.mods {
            let st = format!("{:?}", x.state).to_lowercase();
            let _ = writeln!(ls, "  {:<32} {:<16} {:<9}{}", x.id, x.version.as_deref().unwrap_or("-"), st, x.reason.as_deref().map(|r| format!(" {r}")).unwrap_or_default());
            if x.state == crate::state::ManagedState::Failed {
                failed.push(format!("{} (Kiln): {}", x.id, x.reason.as_deref().unwrap_or("no reason given")));
            }
            if x.state == crate::state::ManagedState::Skipped {
                match x.reason.as_deref() {
                    Some(crate::profile::GAME_HOSTED_WAITING) => hosted.push(&x.id),
                    Some(crate::profile::GAME_HOSTED_OFF) => hosted_off.push(&x.id),
                    _ => {}
                }
            }
        }
    }
    if !hosted.is_empty() {
        let one = hosted.len() == 1;
        cx.find("hosted-mods", Level::Info, format!("{} {} with the player's own world (single-player or co-op), so \"skipped\" in the main \
            menu is expected; {} not run in someone else's world.", hosted.join(", "), if one { "starts" } else { "start" }, if one { "it does" } else { "they do" }));
    }
    if !hosted_off.is_empty() {
        cx.find("hosted-off", Level::Warning, format!("kiln/kiln.json has \"mods\": {{\"hosted\": false}}, so {} never {} in this game, not even \
            in the player's own world.", hosted_off.join(", "), if hosted_off.len() == 1 { "starts" } else { "start" }));
    }
    if let Some(n) = &native {
        let _ = writeln!(ls, "Native mods (native.json, {}, policy {}):", n.written, n.policy);
        // 0.2.0-dev.31.12 (compat-aside-truth): an off native mod whose DLL the player also put in as a chain proxy
        // (kiln\chain.ini [chain], EMF's dinput8-chain.ini; never Kiln's own [profile] rows): Kiln's proxy loaded it
        // before kiln-core read the profile, so its DllMain ran (`chainProxy` names the line's file)
        let proxied: Vec<&str> = n.mods.iter().filter(|x| x.chain_proxy.is_some()).map(|x| x.id.as_str()).collect();
        if !proxied.is_empty() {
            let one = proxied.len() == 1;
            cx.find("off-but-runs", Level::Warning, format!(
                "{} ran at the last start although {} off: {}. A DLL of {} also loads as a chain proxy (kiln\\chain.ini [chain] or \
                 dinput8-chain.ini), which Kiln's proxy loads before kiln-core reads the profile. The lines under \"Native mods\" in \
                 \"The last start\" name the file and how to take {} out.",
                plural(proxied.len(), "native mod", "native mods"), if one { "it is" } else { "they are" }, proxied.join(", "),
                if one { "it" } else { "each" }, if one { "it" } else { "each" }));
        }
        for x in &n.mods {
            let st = format!("{:?}", x.state).to_lowercase();
            let _ = writeln!(ls, "  {:<32} {:<16} {:<9}{}", x.id, x.version.as_deref().unwrap_or("-"), st, x.reason.as_deref().map(|r| format!(" {r}")).unwrap_or_default());
            if x.state == crate::state::NativeModState::Failed {
                failed.push(format!("{} (native): {}", x.id, x.reason.as_deref().unwrap_or("no reason given")));
            }
        }
    }
    if let Some(b) = &boot {
        for p in b.plugins.iter().filter(|p| p.status == "failed") {
            failed.push(format!("{} (Kiln plugin): {}", p.file, p.error.as_deref().unwrap_or("no reason given")));
        }
        for c in b.chain.iter().filter(|c| matches!(c.status, crate::state::ChainStatus::Failed | crate::state::ChainStatus::Missing)) {
            failed.push(format!("{} (chain.ini): {}", c.path.rsplit(['\\', '/']).next().unwrap_or(&c.path), format!("{:?}", c.status).to_lowercase()));
        }
    }
    // the profile's native mods that are another engine's own loader (r1110: SET's powrprof.dll imported as a native mod)
    let native_loaders: Vec<(String, crate::engines::Engine, String)> = match (&profile, &install) {
        (Some(p), Some(i)) => p.mods.native.iter().filter(|m| m.enabled)
            .filter_map(|m| {
                let host = crate::winpath::to_host(&m.path, i).ok()?;
                let (engine, file) = crate::engines::own_loader_in(&host)?;
                Some((m.id.clone(), engine, file))
            }).collect(),
        _ => Vec::new(),
    };
    for (id, engine, file) in &native_loaders {
        let aside = engines.iter().any(|e| e.kind.engine() == Some(*engine) && e.state.as_ref().is_some_and(stood_aside));
        if !aside {
            cx.find("native-loader", Level::Warning, format!("The profile's {}.", crate::profile::native_loader_warning(id, *engine, file)));
        }
    }
    let boot_pid = boot.as_ref().map(|b| u64::from(b.pid));
    // the last start's process: kiln.log's last boot (written first: a start that did not get to boot.json, KR-YNQZ),
    // else boot.json's
    let log_pid = tail(&rel(&game, "kiln/logs/kiln.log"), KILN_LOG_MAX).ok().flatten()
        .and_then(|(text, _)| crate::logs::boot_state(&text).pid).map(u64::from);
    let last_pid = log_pid.or(boot_pid);
    // Kiln hosted ShroudForge in the start its state file is of: that file's pid, when it is the last start's (the
    // process above, when one is known; boot.json's alone would take a pair of the start before for a later start
    // that ended before its boot.json). Without a pid the file is of no known start.
    let sf_hosted_pid = engines.iter().filter(|e| e.kind == ModKind::ShroudForge).filter_map(|e| e.state.as_ref())
        .find(|st| jstr(st.get("mode")).as_deref() == Some("hosted"))
        .and_then(|st| st.get("pid").and_then(|v| v.as_u64()))
        .filter(|pid| last_pid.is_none_or(|b| b == *pid));
    // ShroudForge's state file: its pid and mode (kiln-eml's handoff names Kiln's ShroudForge unless that start's file
    // says another mode)
    let sf_state: Option<(u64, String)> = engines.iter().filter(|e| e.kind == ModKind::ShroudForge).filter_map(|e| e.state.as_ref())
        .find_map(|st| Some((st.get("pid").and_then(|v| v.as_u64())?, jstr(st.get("mode")).unwrap_or_default())));
    let sf = sf_state.as_ref().map(|(p, m)| (*p, m.as_str()));
    let handoff = |kind: ModKind, st: &serde_json::Value| eml_handoff(kind, st, sf, sf_hosted_pid, last_pid);
    let _ = writeln!(ls, "Engines:");
    let mut any_engine = false;
    for e in &engines {
        if let Some(err) = &e.error {
            any_engine = true;
            let _ = writeln!(ls, "  {:<18} unreadable: {err}", e.plugin);
            cx.err(&format!("kiln/state/{}", engine_mods::state_file(e.kind).unwrap_or("?")), err);
            continue;
        }
        let Some(st) = &e.state else { continue };
        any_engine = true;
        let word = engine_headline(st).unwrap_or_else(|| "?".into());
        let reason = jstr(st.get("reason")).or_else(|| jstr(st.get("error")));
        let _ = writeln!(ls, "  {:<18} {word}{}", e.plugin, reason.as_deref().map(|r| format!(": {r}")).unwrap_or_default());
        for (id, state, why) in engine_mod_lines(st) {
            let _ = writeln!(ls, "    {:<30} {:<10}{}", id, state.as_deref().unwrap_or("-"), why.as_deref().map(|r| format!(" {r}")).unwrap_or_default());
            if state.as_deref().is_some_and(failed_word) {
                failed.push(format!("{id} ({}): {}", kind_name(e.kind), why.as_deref().unwrap_or("no reason given")));
            }
        }
        // 0.2.0-dev.31.12: a mod the profile has off that ran all the same, from a folder of the player's that nothing could
        // skip (kiln-eml's `own-loader` row "… although it is off in the profile: it is your own folder …"; the `status`
        // rows of emf.json, greeneye.json, shroudtopia.json and flame.json "runs (your own folder …; …) although it is off in
        // the profile: …" (compat-aside-truth); kiln-shroudforge's `profileOff` rows with `own`). Kiln's copy that ran so
        // is Kiln's ("runs (Kiln's copy … is still there; …)"), and the next Play switches it off: no finding about a
        // player's folder.
        let ran_off: Vec<String> = match e.kind {
            ModKind::ShroudForge => st.get("profileOff").and_then(|v| v.as_array()).into_iter().flatten()
                .filter(|r| r.get("skipped").and_then(|b| b.as_bool()) == Some(false) && r.get("own").and_then(|b| b.as_bool()) == Some(true))
                .filter_map(|r| jstr(r.get("id"))).collect(),
            _ => st.get("mods").and_then(|v| v.as_array()).into_iter().flatten()
                .filter(|m| [m.get("state"), m.get("status")].into_iter().any(|s| jstr(s).as_deref() == Some("own-loader"))
                    && jstr(m.get("reason")).is_some_and(|r| ran_off_from_own(&r)))
                .filter_map(|m| jstr(m.get("id")).or_else(|| jstr(m.get("name")))).collect(),
        };
        if !ran_off.is_empty() {
            cx.find("off-but-runs", Level::Warning, format!(
                "{} ran at the last start although the profile has {} off: {}. {} in a folder of the player's own, which Kiln never \
                 writes into, and nothing skipped {}. The lines under {} in \"The last start\" say why and how to turn {} off.",
                plural(ran_off.len(), &format!("{} mod", kind_name(e.kind)), &format!("{} mods", kind_name(e.kind))),
                if ran_off.len() == 1 { "it" } else { "them" }, ran_off.join(", "),
                if ran_off.len() == 1 { "It is" } else { "They are" }, if ran_off.len() == 1 { "it" } else { "them" },
                e.plugin, if ran_off.len() == 1 { "it" } else { "each" }));
        }
        if stood_aside(st) && let Some((id, engine, file)) = native_loaders.iter().find(|(_, en, _)| Some(*en) == e.kind.engine()) {
            cx.find("native-loader", Level::Problem, format!("{} stood aside at the last start: the profile's {}.",
                e.plugin, crate::profile::native_loader_warning(id, *engine, file)));
        } else if stood_aside(st) && let Some(EmlHandoff::OtherStart { pid }) = handoff(e.kind, st) {
            // kiln-eml handed the packs to ShroudForge in the start its file is of: that, and not an own loader the file
            // does not show (a loader in the game folder has its own finding: check_loaders_in_folder)
            let n = handed_packs(st);
            let of = match (pid, last_pid) {
                (Some(p), Some(l)) => format!("an earlier start (pid {p}) than the last one (pid {l})"),
                (Some(p), None) => format!("a start (pid {p}) that no file here names as the last one"),
                (None, _) => "a start it does not name (no pid)".to_string(),
            };
            cx.find("eml-under-shroudforge", Level::Info, format!(
                "{}'s state file is of {of}: in that start it handed {} to ShroudForge. It says nothing about the last start.",
                e.plugin, if n == 0 { "its EML packs".to_string() } else { plural(n, "EML pack", "EML packs") }));
        } else if stood_aside(st) && let Some(EmlHandoff::LastStart { kiln_hosted }) = handoff(e.kind, st) {
            // by design, and no own loader: the mods that did not run are the finding, if there are any
            let off: Vec<String> = engine_mod_lines(st).into_iter()
                .filter(|(_, state, _)| matches!(state.as_deref(), Some("off-in-shroudforge" | "not-loaded")))
                .map(|(id, _, _)| id).collect();
            // dev.30.3: also the drop-ins nothing runs (source game-mods or kiln-dropin), which no profile names
            let dropins = st.get("mods").and_then(|m| m.as_array()).is_some_and(|mods| mods.iter().any(|m| {
                matches!(jstr(m.get("state")).as_deref(), Some("off-in-shroudforge" | "not-loaded"))
                    && jstr(m.get("source")).is_some_and(|s| s != "profile")
            }));
            let (one, many) = if dropins { ("EML mod", "EML mods") } else { ("EML mod of the profile", "EML mods of the profile") };
            let sf_name = if kiln_hosted { "Kiln's ShroudForge" } else { "ShroudForge" };
            if off.is_empty() {
                // what kiln-eml did: the packs it handed over (KR-YNQZ: "handed your 8 EML packs to Kiln's ShroudForge")
                let n = handed_packs(st);
                let packs = if n == 0 { "the EML packs".to_string() } else { format!("your {}", plural(n, "EML pack", "EML packs")) };
                cx.find("eml-under-shroudforge", Level::Info, format!(
                    "{} handed {packs} to {sf_name} at the last start: ShroudForge alone writes the game data then, and it runs \
                     the EML packs that are switched on in its mods folder. This is how the two work together.", e.plugin));
            } else {
                cx.find("eml-under-shroudforge", Level::Warning, format!(
                    "{} did not run at the last start: {} ShroudForge, which runs the EML mods then, and {} switched off in \
                     ShroudForge or missing from its mods folder: {}. The lines under {} in \"The last start\" say what to do for each.",
                    plural(off.len(), one, many), if kiln_hosted { "Kiln hosted" } else { "kiln-eml handed them to" },
                    if off.len() == 1 { "this one is" } else { "these are" }, off.join(", "), e.plugin));
            }
        } else if stood_aside(st) && e.kind == ModKind::Shroudlink
            && let Some(core) = jstr(st.get("corePath")).filter(|p| !p.is_empty())
        {
            // The plugin records the actual loaded core, which may also be under kiln/chain/.
            cx.find("own-loader", Level::Problem, format!(
                "{} stood aside at the last start for shroudlink's own core ({core}). That core runs only the mods in shroudlink_mods, \
                 so the shroudlink mods in the profile did not load through Kiln. Remove {core}: Kiln then runs them, and the client \
                 addon keeps its MODS tab.", e.plugin));
        } else if stood_aside(st) && let Some(why) = emf_runs_its_own(e.kind, st) {
            // EMF's own loader (or core) chained on purpose: EMF runs its mods, which is how that setup works. A problem
            // only when it ran none (its core went idle) or a profile mod is not where it reads (PepegaJedi, 2026-11-21).
            let lost: Vec<String> = engine_mod_lines(st).into_iter()
                .filter(|(_, state, _)| state.as_deref() == Some("not-loaded")).map(|(id, _, _)| id).collect();
            if let Some(idle) = jstr(st.get("emfIdle")).filter(|s| !s.is_empty()) {
                cx.find("own-loader", Level::Problem, format!(
                    "{} stood aside at the last start for EMF's own loader ({why}), and {idle}. To let Kiln run the EMF mods instead, \
                     switch EMF's loader off: put ';' in front of its line in kiln/chain.ini, then start again.", e.plugin));
            } else if !lost.is_empty() {
                cx.find("own-loader", Level::Problem, format!(
                    "{} stood aside at the last start: EMF runs its own mods ({why}), but {} not in emf\\mods, where EMF reads: {}. \
                     The lines under {} in \"The last start\" say what to do for each.",
                    e.plugin, plural(lost.len(), "EMF mod of the profile is", "EMF mods of the profile are"), lost.join(", "), e.plugin));
            } else {
                cx.find("own-loader", Level::Info, format!(
                    "EMF runs its own mods; that's how this setup works. {} stood aside at the last start for EMF's own loader ({why}), \
                     which runs the EMF mods in emf\\mods.", e.plugin));
            }
        } else if stood_aside(st) {
            let n = kind_name(e.kind);
            cx.find("own-loader", Level::Problem, format!(
                "{} stood aside at the last start: {n}'s own loader runs in the game{}. The {n} mods in the profile did not load through Kiln; only the mods in {n}'s own folder ran.",
                e.plugin, reason.as_deref().map(|r| format!(" ({r})")).unwrap_or_default()));
        }
    }
    if !any_engine {
        let _ = writeln!(ls, "  (no engine plugin wrote a state file)");
    }
    // ShroudForge's own record of its startup pass (sf_startup.rs): a failed pass leaves out every mod it writes into
    // the game data, and only ShroudForge's own log said so (player report 327). Its words are redacted whole, before
    // they are put on one line and cut (a cut name, or a home folder's collapsed spaces, would not match).
    let under_wine = install.as_ref().map(|i| i.platform != Platform::Windows).unwrap_or(cfg!(not(windows)));
    // in kiln mode that record belongs to a hosted start: it is not read (Kiln's interpreter has the
    // start; its own record is kiln/state/sfi.json, in the raw state files)
    let sf_record = !crate::sfi_routing::route_of(&game).interpreter();
    if let Some(f) = sf_record.then(|| crate::sf_startup::failure(&game, under_wine)).flatten().map(|f| f.redacted(|t| redact(t, cx.red))) {
        let _ = writeln!(ls, "ShroudForge's record of its startup pass (state.json \"assets\"): {}", f.line());
        cx.find("sf-startup-failed", Level::Problem, format!("{}.", f.text()));
    }
    // Kiln's ShroudForge ran the pass once more without the mod it failed in: the others loaded, that one did not.
    if let Some(s) = sf_record.then(|| crate::sf_startup::skipped(game)).flatten().map(|s| s.redacted(|t| redact(t, cx.red))) {
        let _ = writeln!(ls, "ShroudForge's record of its startup pass (state.json \"assets\"): {}", s.line());
        cx.find("sf-startup-skipped", Level::Warning, format!("{}.", s.text()));
    }
    let aside: Vec<ModKind> = engines.iter()
        .filter(|e| e.state.as_ref().is_some_and(|st| stood_aside(st) && handoff(e.kind, st).is_none()))
        .map(|e| e.kind).collect();
    // a plugin that says it did not stand aside, in a state file of the last start (boot.json's time or later)
    // of the last start: the process boot.json names (kiln-greeneye writes its pid; it writes before boot.json, so the
    // times say nothing). A state file without a pid (an older plugin) is not taken as this start's.
    let ran: Vec<ModKind> = engines.iter().filter(|e| e.state.as_ref().is_some_and(|st| {
        st.get("steppedAside").and_then(|v| v.as_bool()) == Some(false)
            && boot_pid.is_some() && st.get("pid").and_then(|v| v.as_u64()) == boot_pid
    })).map(|e| e.kind).collect();
    check_loaders_in_folder(&mut cx, &proxies, facts.platform, &aside, &ran);
    if !failed.is_empty() {
        let shown: Vec<&String> = failed.iter().take(20).collect();
        cx.find("mod-failed", Level::Problem, format!("{} failed at the last start: {}{}", plural(failed.len(), "mod or DLL", "mods or DLLs"),
            shown.iter().map(|s| s.as_str()).collect::<Vec<_>>().join("; "), if failed.len() > 20 { "; ..." } else { "" }));
    }
    if let Some(b) = &boot {
        if let Some(x) = &b.safe_idle {
            cx.find("safe-idle", Level::Problem, format!("Kiln stayed idle at the last start and loaded no mods: {x}."));
        }
        if b.profile.mode == crate::state::ProfileMode::Invalid && !facts.profile_broken {
            cx.find("profile-invalid", Level::Problem, format!("The last start found the profile invalid and loaded no mods: {}.", b.profile.error.as_deref().unwrap_or("no reason given")));
        }
    }
    // kiln.log: the last boot as the log tells it (logs::boot_state)
    if let Some(text) = cx.add_tail("kiln/logs/kiln.log", KILN_LOG_MAX) {
        let st = crate::logs::boot_state(&text);
        if st.started {
            let mut line = format!("kiln.log:      last boot Kiln {}", st.kiln.as_deref().unwrap_or("?"));
            if let Some(t) = &st.start_time {
                let _ = write!(line, " at {t}");
            }
            if let Some(p) = st.pid {
                let _ = write!(line, " (pid {p})");
            }
            if let Some(x) = &st.side {
                let _ = write!(line, ", side {x}");
            }
            if let Some(x) = &st.build {
                let _ = write!(line, ", build {x}");
            }
            if let Some(x) = &st.safe_idle {
                let _ = write!(line, ", safe idle: {x}");
            }
            let _ = writeln!(ls, "{line}, boot complete {}, {} warnings, {} errors", if st.boot_complete { "yes" } else { "no" }, st.warnings, st.errors.len());
            for e in st.errors.iter().take(15) {
                let _ = writeln!(ls, "  {e}");
            }
            // The game drew, but Kiln never got its graphics device: nothing of Kiln's shows in the game.
            if st.ui_swapchain && !st.ui_device {
                let who = st.ui_patched_by.as_deref().map(|w| format!(" ({w} has its hook on the same graphics functions)")).unwrap_or_default();
                let _ = writeln!(ls, "Overlay:       cannot draw: kiln.ui saw the game's swapchain but never its Vulkan device{who}");
                cx.find("overlay-off", Level::Problem, format!(
                    "Kiln could not draw in the game at the last start: it never saw the game's graphics device{who}. The in-game Mods page and Kiln's menus stay empty. \
                     The mods themselves are not affected. Another program that hooks the game's graphics (ReShade, an overlay, another loader's in-game window) \
                     next to Kiln does this; Kiln 0.2.0-dev.24 and later take the device over when the PC has one graphics device."));
            }
        }
    } else {
        let _ = writeln!(ls, "kiln.log:      none");
    }
    let proxy_log = cx.add_tail(crate::paths::game::PROXY_LOG, SMALL_LOG_MAX);
    cx.add_tail("kiln/logs/eml.log", SMALL_LOG_MAX);
    // Kiln's per-minute step times of each world (core/src/tick.cpp; rewritten at every game start): whether a slow
    // session was slow inside the steps, between them (saves), or in Kiln's own Pre / Post (GitHub issue 39)
    cx.add_tail("kiln/logs/tick-stats.jsonl", TICK_STATS_MAX);
    // Native UI initialization and later player-menu visibility are separate addon paths; kiln.log alone cannot
    // distinguish them. Read only this bounded log, not the addon's key bindings or per-server mod settings.
    cx.add_tail("shroudlink_client.log", SMALL_LOG_MAX);
    xhl_core_logs(&mut cx, game);
    check_not_loaded(&mut cx, install.as_ref(), dirs.as_ref(), receipt.is_some() || dinput8.as_ref().is_some_and(|d| d.kind == ProxyKind::Kiln),
        boot.as_ref(), boot_time, proxy_log.is_some(), &facts);
    if receipt.is_some() {
        match facts.known_build {
            Some(None) => cx.find("unknown-build", Level::Problem, format!("{} (Steam build {}; a Kiln update that knows the build fixes it.)",
                crate::direct::unknown_build_sentence(facts.exe_sha.as_deref().map(|h| &h[..12.min(h.len())]).unwrap_or("?")),
                facts.build.as_deref().unwrap_or("unknown"))),
            Some(Some(false)) => cx.find("unknown-build", Level::Warning, "Kiln has a record of this game build but it is not verified: with the policy verified-only Kiln stays idle."),
            _ => {}
        }
    }
    // the newest crash report
    match crate::logs::crash_reports(&game) {
        Ok(list) => {
            if let Some(c) = list.first() {
                let _ = writeln!(ls, "Crash report:  {} ({}, {} reports in all)", c.name, c.created, list.len());
                cx.find("crash", Level::Warning, format!("Kiln wrote a crash report on {}: {} ({} in all; the newest is in this report).", c.created, c.name, list.len()));
                cx.add_tail(&c.name, CRASH_MAX);
            }
        }
        Err(e) => cx.err("kiln/crash-reports", e.message),
    }
    // after Kiln's own logs and the crash report, so they never take TOTAL_MAX's room from those
    for name in OTHER_MOD_LOGS {
        cx.add_tail(name, SMALL_LOG_MAX);
    }
    // the profile's Kiln mods that need a newer Kiln (game::newer_mods, as on Play)
    if let (Some(d), Some(i)) = (dirs.as_ref(), install.as_ref()) {
        if let Ok(st) = crate::install::status(d, i) {
            let newer = crate::game::newer_mods(d, &st, profile.as_ref().map(|p| p.id.as_str()));
            if !newer.is_empty() {
                cx.find("newer-mods", Level::Warning, format!("{} {} a newer Kiln than the one in the game: update Kiln on the launcher's Runtime page.",
                    newer.iter().map(|m| m.id.as_str()).collect::<Vec<_>>().join(", "), if newer.len() == 1 { "needs" } else { "need" }));
            }
        }
    }

    check_sf_mod_bundled_newer(&mut cx, game);

    // ---- raw files
    let mut state_files: Vec<String> = std::fs::read_dir(game.join(crate::paths::game::STATE))
        .into_iter()
        .flatten()
        .flatten()
        .filter(|e| e.file_type().is_ok_and(|t| t.is_file()))
        .map(|e| e.file_name().to_string_lossy().into_owned())
        .filter(|n| n.ends_with(".json") && !n.starts_with('.'))
        .collect();
    state_files.sort();
    for n in state_files {
        cx.add_file(&format!("kiln/state/{n}"));
    }
    // Kiln's interpreter: the launch plans kiln-manage pinned and kiln-eml's ownership and clean-up
    // records (what its startup pass published or left to clean), the newest three of each, read like the state files
    // (bounded, JSON redacted); no package bytes
    for dir in ["kiln/launch-plans", "kiln/eml/ownership"] {
        let mut files: Vec<(std::time::SystemTime, String)> = std::fs::read_dir(game.join(dir))
            .into_iter()
            .flatten()
            .flatten()
            .filter(|e| e.file_type().is_ok_and(|t| t.is_file()))
            .filter_map(|e| {
                let n = e.file_name().to_string_lossy().into_owned();
                (n.ends_with(".json") && !n.starts_with('.'))
                    .then(|| (e.metadata().and_then(|m| m.modified()).unwrap_or(std::time::UNIX_EPOCH), n))
            })
            .collect();
        files.sort_by(|a, b| b.0.cmp(&a.0).then_with(|| a.1.cmp(&b.1)));
        for (_, n) in files.into_iter().take(3) {
            cx.add_file(&format!("{dir}/{n}"));
        }
    }
    for f in [crate::paths::game::PROFILE, crate::paths::game::CHAIN, "kiln/kiln.json", crate::paths::game::RECEIPT, crate::paths::game::EMF_CHAIN] {
        cx.add_file(f);
    }
    greeneye_logs(&mut cx, &game, install.as_ref(), dirs.as_ref());
    // listings: names, sizes and times only
    let skip_none = |_: &str| false;
    let mut top = Vec::new();
    listing(&game, "", 1, &mut top, &skip_none);
    cx.add_text("listings/game-folder.txt", &(top.join("\n") + "\n"), Some("The game folder's top level: type, size, modified, name."));
    let mut kl = Vec::new();
    listing(&game.join("kiln"), "kiln", 2, &mut kl, &skip_none);
    cx.add_text("listings/kiln-folder.txt", &(kl.join("\n") + "\n"), Some("kiln/, two levels deep: type, size, modified, name."));
    let mut ml = Vec::new();
    let mut roots_listed: Vec<String> = Vec::new();
    for root in mod_roots(&game) {
        if roots_listed.contains(&root) || !rel(&game, &root).is_dir() {
            continue;
        }
        let mut lines = Vec::new();
        listing(&rel(&game, &root), &root, 2, &mut lines, &skip_none);
        ml.push(format!("[{root}]"));
        ml.extend(lines);
        roots_listed.push(root);
    }
    cx.add_text("listings/mod-folders.txt", &(ml.join("\n") + "\n"), Some("Each engine's mods folder, two levels deep: type, size, modified, name. No file contents."));

    let mut body = String::new();
    let _ = writeln!(body, "\n== Game ==\n{g}\n== Kiln in the game ==\n{k}\n== Profile ==\n{pr}\n== The last start ==\n{ls}");
    finish(cx, s, sys, body, input, dirs.as_ref(), &tool, now, &facts)
}

/// Facts the short summary and the checks share.
#[derive(Debug, Default)]
struct Facts {
    side: Option<&'static str>,
    platform: Option<Platform>,
    build: Option<String>,
    exe_sha: Option<String>,
    known_build: Option<Option<bool>>,
    kiln: Option<String>,
    proton: Option<String>,
    override_missing: bool,
    /// Whether an explicit dinput8 override is already configured, not whether the runner needs one.
    has_dinput8_override: bool,
    override_advice: Option<String>,
    /// The launcher starts this client directly (direct.rs), not through Steam.
    direct: bool,
    running: bool,
    profile: Option<String>,
    profile_broken: bool,
    last_start: Option<String>,
    /// Mods on in the active profile (every kind).
    mods_on: Option<usize>,
    /// The game folder, for the launcher.log check of a blocked install (`install-blocked`).
    game: Option<PathBuf>,
    /// When the receipt was written last (an install, update or Repair that worked), Unix seconds.
    receipt_at: Option<i64>,
}

fn plural(n: usize, one: &str, many: &str) -> String {
    format!("{n} {}", if n == 1 { one } else { many })
}

/// The folders each engine reads mods from, game-relative.
fn mod_roots(game: &Path) -> Vec<String> {
    let mut v: Vec<String> = vec![
        crate::paths::game::DROPIN_KILN.into(),
        crate::paths::game::DROPIN_NATIVE.into(),
        crate::paths::game::DROPIN_PLUGIN.into(),
        crate::paths::game::DROPIN_EMF.into(),
        "kiln/eml/mods".into(),
        "mods".into(),
    ];
    for k in ModKind::ALL {
        if let Some(r) = engine_mods::server_root(k, game).and_then(|r| r.strip_prefix(game).ok().map(|x| x.to_string_lossy().replace('\\', "/"))) {
            if !r.is_empty() && !v.contains(&r) {
                v.push(r);
            }
        }
    }
    v
}

/// XHL-Core's own log, `XHL-Native-Plugin-Host.log` ([`engine_mods::XHL_CORE_LOG`]): which plugin folders of `mods/` it
/// loaded, skipped or refused (inbox 86: a mod the profile had off that XHL-Core loaded anyway). XHL-Core writes it next
/// to the DLL it runs as: the game's `dinput8.dll` (its author's instructions) or Kiln's copy of it,
/// `kiln/chain/<id>/xhl-core.dll` (a Native mod of the profile). The tail of each that is there, at most
/// [`XHL_CORE_LOGS`], redacted as Kiln's logs are; a link is not followed (neither the folder nor the file).
fn xhl_core_logs(cx: &mut Ctx, game: &Path) {
    cx.add_tail(engine_mods::XHL_CORE_LOG, SMALL_LOG_MAX);
    let chain = rel(game, crate::paths::game::CHAIN_DIR);
    let real_dir = |p: &Path| std::fs::symlink_metadata(p).is_ok_and(|m| m.is_dir() && !crate::quick::import_is_link(&m));
    if !real_dir(&chain) { return; }
    let mut ids: Vec<String> = std::fs::read_dir(&chain).into_iter().flatten().flatten()
        .filter(|e| real_dir(&e.path()) && e.path().join(engine_mods::XHL_CORE_LOG).exists())
        .filter_map(|e| e.file_name().to_str().filter(|id| crate::manifest::is_mod_id(id)).map(str::to_owned))
        .collect();
    ids.sort();
    for id in ids.into_iter().take(XHL_CORE_LOGS) {
        cx.add_tail(&format!("{}/{id}/{}", crate::paths::game::CHAIN_DIR, engine_mods::XHL_CORE_LOG), SMALL_LOG_MAX);
    }
}

/// Other mods' and loaders' own logs in the game folder, by the names recent support reports listed (netjunk's report
/// 2026-11-20 had Building Tools' only in the listing): Building Tools (`BuildingTools.log`, and `.prev.log` from the start
/// before), ShroudLink's core (`shroudlink.log`; its client addon's is `shroudlink_client.log` above), Auto Loot Plus'
/// native part (`auto-loot-plus.log`: which of its code patterns it found). Each a bounded tail, redacted as Kiln's logs;
/// their settings files are never read.
const OTHER_MOD_LOGS: [&str; 4] = ["BuildingTools.log", "BuildingTools.prev.log", "shroudlink.log", "auto-loot-plus.log"];

/// Kiln's copies of XHL-Core whose logs [`xhl_core_logs`] reads (a profile has one; more are copies of other profiles).
const XHL_CORE_LOGS: usize = 4;

/// Greeneye's logs (docs/kiln/engines/greeneye.md): kiln-greeneye's `kiln/logs/greeneye-loader.log`, Greeneye Loader's own
/// `greeneye/loader.log` (and `loader.second-copy.log`), and each Greeneye mod's own `*.log` and `*.status` files in its
/// folder. A Greeneye mod writes what it found and patched only there (The Worst Map Mod: unlock_map_markers.log,
/// UnlockMapMarkers.status), not through the loader into kiln.log. The folders are the ones `kiln/state/greeneye.json`
/// names (Kiln's copy in `greeneye/`, or a Library entry under an older Kiln) and those in `<game>/greeneye/`. The zip is
/// posted in public, so a folder is read only when it lies inside `<game>/greeneye/` or the launcher's Library folder for
/// Greeneye mods, after resolving it and with links refused from that root down; any other folder is left out with a
/// note. Top-level files only, each a bounded tail, redacted like every file; settings (`.ini`) and data are never read.
fn greeneye_logs(cx: &mut Ctx, game: &Path, install: Option<&GameInstall>, dirs: Option<&DataDirs>) {
    cx.add_tail("kiln/logs/greeneye-loader.log", GREENEYE_LOG_MAX);
    cx.add_tail("greeneye/loader.log", GREENEYE_LOG_MAX);
    cx.add_tail("greeneye/loader.second-copy.log", GREENEYE_LOG_MAX);
    let roots: Vec<(&str, PathBuf)> = std::iter::once(("game", game.join(crate::engine_mods::GREENEYE_MODS)))
        .chain(dirs.map(|d| ("library", d.library().join(ModKind::Greeneye.as_str())))).collect();
    // The root a folder lies in ("game": <game>/greeneye, "library": the launcher's Library), or None: no `..`, neither
    // the root nor the folder a link, and the resolved folder strictly inside the resolved root. Compared resolved, so a
    // case or alias difference on Windows is the same folder; a link further down that leads out resolves outside.
    let is_link = |p: &Path| std::fs::symlink_metadata(p).is_ok_and(|m| crate::quick::import_is_link(&m));
    let place = |dir: &Path| -> Option<&str> {
        if dir.components().any(|c| matches!(c, std::path::Component::ParentDir)) || is_link(dir) { return None; }
        let d = dir.canonicalize().ok()?;
        roots.iter().find_map(|(label, root)| {
            let r = root.canonicalize().ok()?;
            (!is_link(root) && d != r && d.starts_with(&r)).then_some(*label)
        })
    };
    let mut used: Vec<String> = Vec::new();
    let mut folders: Vec<(String, PathBuf)> = Vec::new();
    let state = crate::util::read_json::<serde_json::Value>(&game.join(crate::paths::game::STATE).join("greeneye.json")).ok().flatten();
    for m in state.as_ref().and_then(|s| s.get("mods")).and_then(|m| m.as_array()).into_iter().flatten() {
        let (Some(id), Some(folder)) = (jstr(m.get("id")), jstr(m.get("folder"))) else { continue };
        let host = match install {
            Some(i) => crate::winpath::to_host(&folder, i).ok(),
            None => Some(PathBuf::from(&folder)),
        };
        if let Some(h) = host { folders.push((id, h)); }
    }
    let mut own: Vec<PathBuf> = std::fs::read_dir(game.join(crate::engine_mods::GREENEYE_MODS)).into_iter().flatten().flatten()
        .map(|e| e.path()).filter(|p| p.join("mod.json").is_file()).collect();
    own.sort();
    for p in own {
        let name = p.file_name().map(|n| n.to_string_lossy().into_owned()).unwrap_or_default();
        folders.push((name, p));
    }
    let mut seen: Vec<PathBuf> = Vec::new();
    let mut skipped: Vec<String> = Vec::new();
    for (id, dir) in folders {
        if !dir.is_dir() { continue; }
        let Some(label) = place(&dir) else {
            if !skipped.contains(&id) { skipped.push(id); }
            continue;
        };
        let key = dir.canonicalize().unwrap_or_else(|_| dir.clone());
        if seen.contains(&key) { continue; }
        if seen.len() == GREENEYE_MOD_FOLDERS {
            cx.err("greeneye mod logs", format!("more than {GREENEYE_MOD_FOLDERS} Greeneye mod folders: the others are left out"));
            break;
        }
        seen.push(key);
        let safe: String = id.chars().map(|c| if c.is_ascii_alphanumeric() || "._+-".contains(c) { c } else { '_' }).collect();
        let mut logs: Vec<PathBuf> = std::fs::read_dir(&dir).into_iter().flatten().flatten().map(|e| e.path())
            .filter(|p| std::fs::symlink_metadata(p).is_ok_and(|m| m.is_file()))
            .filter(|p| p.extension().is_some_and(|x| x.eq_ignore_ascii_case("log") || x.eq_ignore_ascii_case("status")))
            .collect();
        logs.sort();
        // one zip folder per mod folder: `greeneye-mods/<id>/game/` (the copy in greeneye/) and `.../library/` (a Library
        // entry), so one id with logs in both never gives two entries of one name (the zip refuses those)
        let mut at = format!("greeneye-mods/{safe}/{label}");
        let mut n = 2;
        while used.contains(&at) {
            at = format!("greeneye-mods/{safe}/{label}-{n}");
            n += 1;
        }
        used.push(at.clone());
        for p in logs.into_iter().take(GREENEYE_FILES_PER_MOD) {
            let file: String = p.file_name().map(|n| n.to_string_lossy().into_owned()).unwrap_or_default()
                .chars().map(|c| if c.is_ascii_alphanumeric() || "._+-".contains(c) { c } else { '_' }).collect();
            cx.add_tail_at(&format!("{at}/{file}"), &p, GREENEYE_LOG_MAX);
        }
    }
    if !skipped.is_empty() {
        cx.find("greeneye-log-skipped", Level::Info, format!(
            "The logs of {} were left out: {} folder is not inside the game's greeneye folder or the launcher's Library \
             (or is reached through a link), and the report reads no other folder.",
            skipped.join(", "), if skipped.len() == 1 { "its" } else { "their" }));
    }
}

/// Identify packages without the general detector's INI/DLL probes: a report reads manifests, never native payloads.
/// Native-only packages without explicit ShroudForge metadata are ambiguous, so leave them out rather than guess.
fn sf_report_is_mod(folder: &Path) -> std::result::Result<bool, String> {
    let extended = folder.join("extended.mod.json").is_file(); // both manifests have already passed the link checks
    let manifest = match head(&folder.join("mod.json"), crate::shroudforge::MAX_FILE_BYTES as u64)? {
        None if !extended => return Ok(false), // native plugins and unrelated folders need no Lua manifest
        None => return Err("mod.json is missing".into()),
        Some((_, true)) => return Err("mod.json is larger than 2 MiB".into()),
        Some((bytes, false)) => serde_json::from_slice::<serde_json::Value>(&bytes).map_err(|e| e.to_string())?,
    };
    if ["dll", "main", "emf", "emfMin", "emf_api"].iter().any(|k| manifest.get(*k).is_some()) {
        return Ok(false);
    }
    if extended || manifest.get("$schema").and_then(|s| s.as_str()).is_some_and(|s| s.to_ascii_lowercase().contains("shroudforge")) {
        if !["id", "name", "version"].iter().all(|k| manifest.get(*k).and_then(|v| v.as_str()).is_some()) {
            return Err("mod.json must name an id, name and version".into());
        }
        return Ok(true);
    }
    // Legacy Lua packages: inspect names only, and never enumerate a linked src folder.
    for file in ["mod.lua", "src"] {
        let path = folder.join(file);
        match std::fs::symlink_metadata(&path) {
            Err(e) if e.kind() == std::io::ErrorKind::NotFound => {}
            Err(e) => return Err(e.to_string()),
            Ok(m) if crate::quick::import_is_link(&m) => return Err(format!("{file}: a link, not followed")),
            Ok(m) if file == "mod.lua" && m.is_file() => return Ok(true),
            Ok(m) if file == "src" && m.is_dir() => {
                for entry in std::fs::read_dir(path).map_err(|e| e.to_string())? {
                    let entry = entry.map_err(|e| e.to_string())?;
                    if entry.path().extension().is_some_and(|x| x == "lua") {
                        return Ok(true);
                    }
                }
            }
            Ok(_) => {}
        }
    }
    Ok(false)
}

/// Package metadata only: the shared ShroudForge reader, bounded and without following links into another folder.
fn sf_report_versions(cx: &mut Ctx, game: &Path, root: &str) -> Vec<(crate::shroudforge::SfMod, crate::semver::SemVersion)> {
    let mut dir = game.to_path_buf();
    for part in root.split('/') {
        dir = crate::sf_paths::ci_child(&dir, part);
        match std::fs::symlink_metadata(&dir) {
            Err(e) if e.kind() == std::io::ErrorKind::NotFound => return Vec::new(),
            Err(e) => { cx.err(root, e); return Vec::new(); }
            Ok(m) if crate::quick::import_is_link(&m) => { cx.err(root, "a link, not followed"); return Vec::new(); }
            Ok(m) if !m.is_dir() => { cx.err(root, "not a folder"); return Vec::new(); }
            Ok(_) => {}
        }
    }
    let entries = match std::fs::read_dir(&dir) {
        Ok(rd) => rd,
        Err(e) => { cx.err(root, e); return Vec::new(); }
    };
    let mut folders = Vec::new();
    for entry in entries {
        let entry = match entry { Ok(e) => e, Err(e) => { cx.err(root, e); continue; } };
        let name = format!("{root}/{}", entry.file_name().to_string_lossy());
        match std::fs::symlink_metadata(entry.path()) {
            Ok(m) if crate::quick::import_is_link(&m) => cx.err(&name, "a link, not followed"),
            Ok(m) if m.is_dir() => folders.push((name, entry.path())),
            Ok(_) => {}
            Err(e) => cx.err(&name, e),
        }
    }
    folders.sort();
    let mut mods = Vec::new();
    'folder: for (name, folder) in folders {
        for file in ["mod.json", "extended.mod.json"] {
            let why = match std::fs::symlink_metadata(folder.join(file)) {
                Err(e) if e.kind() == std::io::ErrorKind::NotFound => None,
                Err(e) => Some(e.to_string()),
                Ok(m) if crate::quick::import_is_link(&m) => Some("a link, not followed".into()),
                Ok(m) if !m.is_file() => Some("not a file".into()),
                Ok(_) => None,
            };
            if let Some(why) = why {
                cx.err(&format!("{name}/{file}"), format!("could not read: {why}"));
                continue 'folder;
            }
        }
        match sf_report_is_mod(&folder) {
            Ok(true) => {}
            Ok(false) => continue,
            Err(e) => { cx.err(&name, format!("could not read: {e}")); continue; }
        }
        match crate::shroudforge::read_mod(&folder) {
            Ok(m) => match crate::semver::SemVersion::parse(&m.version) {
                Ok(v) => mods.push((m, v)),
                Err(e) => cx.err(&format!("{name}/mod.json"), e.message),
            },
            Err(e) => cx.err(&name, format!("could not read: {}", e.message)),
        }
    }
    mods
}

/// An older copy in the player's mods folder is context, never a reason to change that copy or its switch.
fn check_sf_mod_bundled_newer(cx: &mut Ctx, game: &Path) {
    let own = sf_report_versions(cx, game, "mods");
    let bundled = sf_report_versions(cx, game, "kiln/plugins/shroudforge/mods");
    for (m, version) in own {
        if let Some((newer, _)) = bundled.iter().find(|(b, v)| b.id == m.id && version.compare(v).is_lt()) {
            cx.find("sf-mod-bundled-newer", Level::Info, format!(
                "{} {} is older than the {} Kiln brings.", m.id, m.version, newer.version));
        }
    }
}

/// Kiln not installed, another DLL at dinput8.dll, Kiln's files missing or changed against the receipt.
fn check_install(cx: &mut Ctx, game: &Path, receipt: Option<&InstallReceipt>, dinput8: Option<&ProxyClass>) {
    let kind = dinput8.map(|d| d.kind).unwrap_or(ProxyKind::None);
    match (receipt, kind) {
        (None, ProxyKind::None) => cx.find("not-installed", Level::Problem,
            "Kiln is not installed in this game folder: there is no kiln/.install.json and no dinput8.dll. Install Kiln on the launcher's Runtime page."),
        (None, ProxyKind::Kiln) => cx.find("not-installed", Level::Info,
            "Kiln's dinput8.dll is there but no install receipt: Kiln was copied in by hand or by a script, so its files can't be checked."),
        (None, _) => cx.find("proxy-not-kiln", Level::Problem, format!(
            "Kiln is not installed: dinput8.dll is {}, not Kiln. Install Kiln on the launcher's Runtime page; it keeps that DLL and loads it after Kiln.", proxy_text(dinput8.expect("some")))),
        (Some(_), ProxyKind::Kiln | ProxyKind::None) => {}
        (Some(_), _) => cx.find("proxy-not-kiln", Level::Problem, format!(
            "Kiln is installed, but dinput8.dll is now {}: another mod's installer replaced Kiln's. Reinstall Kiln on the launcher's Runtime page; it keeps the other DLL and loads it after Kiln.",
            proxy_text(dinput8.expect("some")))),
    }
    let Some(r) = receipt else { return };
    let modified = crate::install::modified_files(game, r);
    let (missing, changed): (Vec<String>, Vec<String>) = modified.into_iter().partition(|p| !rel(game, p).exists());
    let names = |v: &[String]| {
        let mut s = v.iter().take(12).cloned().collect::<Vec<_>>().join(", ");
        if v.len() > 12 {
            let _ = write!(s, " and {} more", v.len() - 12);
        }
        s
    };
    if !missing.is_empty() {
        cx.find("files-missing", Level::Problem, format!(
            "{} of Kiln's installed files {} missing: {}. An antivirus often removes or quarantines DLLs like these: restore them from its quarantine or add an exception for the game folder, then reinstall Kiln on the launcher's Runtime page.",
            missing.len(), if missing.len() == 1 { "is" } else { "are" }, names(&missing)));
    }
    if !changed.is_empty() {
        cx.find("files-changed", Level::Warning, format!(
            "{} of Kiln's installed files changed since the install: {}. Another installer, a manual copy or an antivirus changed them; reinstalling Kiln puts them back.",
            changed.len(), names(&changed)));
    }
}

/// (dev.29.18, inbox 92) The last install, update, Repair or rollback of Kiln in this game folder failed because Windows
/// refused one of Kiln's DLLs or exes (an antivirus, or another program: crate::blocked), as launcher.log says, and no
/// install worked since (the receipt is older, or there is none).
fn check_blocked_install(cx: &mut Ctx, launcher_log: &str, facts: &Facts) {
    let Some(game) = facts.game.as_deref() else { return };
    let Some(l) = crate::blocked::last_in_log(launcher_log, game) else { return };
    let at = crate::util::parse_rfc3339(&l.at);
    if facts.receipt_at.is_some_and(|r| at.is_none_or(|a| r >= a)) {
        return;
    }
    cx.find("install-blocked", Level::Problem, crate::blocked::finding_text(&l));
}

/// Other engines' own loaders under the proxy names the game loads (EML's dbghelp.dll, SET's powrprof.dll ...).
/// `ran`: the kinds whose plugin wrote at the last start that it did not stand aside (kiln-greeneye: the loader's
/// claim was not there): the loader file is in the folder but did not start, and Kiln ran its mods itself.
fn check_loaders_in_folder(cx: &mut Ctx, proxies: &[crate::engines::ProxySlot], platform: Option<Platform>, aside: &[ModKind],
                           ran: &[ModKind]) {
    use crate::engines::Engine;
    for p in proxies.iter().filter(|p| !p.name.eq_ignore_ascii_case("dinput8.dll")) {
        let Some(engine) = Engine::of_proxy(p.class.kind).filter(|e| *e != Engine::Kiln) else { continue };
        let Some(kind) = ModKind::ALL.into_iter().find(|k| k.engine() == Some(engine)) else { continue };
        if aside.contains(&kind) {
            continue; // the own-loader finding says it, from what the plugin saw
        }
        let windows = platform == Some(Platform::Windows);
        // a modified EML loader (Holster Toggle's dbghelp.dll): its own code runs only while it is the loader that runs
        let is = match p.class.modified.as_deref().filter(|_| engine == Engine::Eml) {
            Some(what) => format!("{} is a modified EML loader with code of its own ({what})", p.name),
            None => format!("{} is {}'s own loader", p.name, engine.name()),
        };
        let own_code = if p.class.modified.is_some() && engine == Engine::Eml { " nor its own code" } else { "" };
        if ran.contains(&kind) {
            // r1111 (Steam Deck, no dbgcore override): the note said kiln-greeneye stood aside while it had run the mods
            cx.find("loader-in-folder", Level::Info, format!(
                "{is}. It did not start at the last start ({}), so Kiln's {} plugin ran the {} mods itself{}.",
                if windows { "something kept it from loading" } else { "under Wine or Proton it loads only with a DLL override for that name" },
                short_name(engine), short_name(engine),
                if own_code.is_empty() { "" } else { " (their EML part; the loader's own code did not run)" }));
            continue;
        }
        cx.find("loader-in-folder", if windows { Level::Warning } else { Level::Info }, format!(
            "{is}. {}, and Kiln's {} plugin then stands aside for it: the {} mods in the profile do not load through Kiln.{}",
            if windows { "On Windows the game always loads it" } else { "Under Wine or Proton it loads only with a DLL override for that name" },
            short_name(engine), short_name(engine),
            if own_code.is_empty() || windows { String::new() } else {
                format!(" Without that override neither EML{own_code} runs from it, and Kiln runs the EML mods' data part itself.")
            }));
    }
}

/// Mods in the game folder the active profile does not load (drop-ins switched off, an engine the profile manages):
/// [`crate::found::scan`] over the game's profile.json, the same scan as the launcher's Game folder panel.
fn check_unlisted(cx: &mut Ctx, install: Option<&GameInstall>, dirs: Option<&DataDirs>, profile: Option<&ProfileFile>) {
    let (Some(p), Some(install)) = (profile, install) else { return };
    // without the launcher's data folder (a server's report) no Library copy is known: the scan matches by id only
    let none = DataDirs::at(install.path.join(crate::paths::game::KILN).join(".no-launcher-data"));
    let found = crate::found::scan(dirs.unwrap_or(&none), install, &profile_view(p));
    // Kiln not installed: the not-installed finding says what to do
    if !found.kiln_installed {
        return;
    }
    let off: Vec<&crate::found::FoundMod> = found.mods.iter().filter(|m| m.state == crate::found::FoundState::Off).collect();
    if off.is_empty() {
        return;
    }
    let kind_of = |m: &crate::found::FoundMod| m.kind.map(kind_name).unwrap_or("folder");
    let mut kinds: Vec<&str> = off.iter().map(|m| kind_of(m)).collect();
    kinds.dedup();
    let shown: Vec<String> = off.iter().take(15).map(|m| format!("{} ({})", m.path, kind_of(m))).collect();
    cx.find("unlisted-mods", Level::Warning, format!(
        "{} in the game folder {} not loaded by the profile '{}': {}{}. Add {} to the profile in the Library, or turn on drop-ins for {} in the profile's settings.",
        plural(off.len(), "mod", "mods"), if off.len() == 1 { "is" } else { "are" }, p.name.as_deref().unwrap_or(&p.id),
        shown.join(", "), if off.len() > 15 { ", ..." } else { "" }, if off.len() == 1 { "it" } else { "them" }, kinds.join(", ")));
}

/// The progress.json of a start that began at `launched` (unix seconds) or later, give or take boot.json's 30 s, and ended
/// while Kiln was starting: not `ready`, `idle` or `failed` (each written with boot.json, or after it).
fn stopped_while_starting(game: &Path, launched: i64) -> Option<crate::start_progress::Progress> {
    let p = crate::start_progress::parse_progress(&std::fs::read_to_string(rel(game, crate::start_progress::PROGRESS_JSON)).ok()?)?;
    let started = crate::start_progress::parse_ms(&p.started)?.div_euclid(1000);
    (started + 30 >= launched && !p.is_final()).then_some(p)
}

/// The step a start began last, as the launcher's start window says it: "Running the early plugins (ShroudForge, 3 of 5)".
fn progress_step(p: &crate::start_progress::Progress) -> Option<String> {
    let s = p.steps.last()?;
    let label = if s.label.is_empty() { s.id.as_str() } else { s.label.as_str() };
    let count = match (s.done, s.total) {
        (Some(d), Some(t)) => Some(format!("{d} of {t}")),
        _ => None,
    };
    let more: Vec<String> = [s.detail.clone().filter(|d| !d.is_empty()), count].into_iter().flatten().collect();
    Some(if more.is_empty() { label.to_string() } else { format!("{label} ({})", more.join(", ")) })
}

/// The last start did not load Kiln: the game runs without a fresh boot.json, a launch from the launcher wrote none,
/// or the game ran and Kiln's proxy never logged anything. Under Proton without the DLL override, why.
#[allow(clippy::too_many_arguments)]
fn check_not_loaded(cx: &mut Ctx, install: Option<&GameInstall>, dirs: Option<&DataDirs>, kiln_there: bool,
    boot: Option<&crate::state::BootJson>, boot_time: Option<SystemTime>, proxy_log: bool, facts: &Facts) {
    let Some(i) = install else { return };
    if !kiln_there {
        return;
    }
    let proton = i.platform == Platform::Proton;
    let why = if facts.direct && i.platform != Platform::Windows {
        // a direct start sets WINEDLLOVERRIDES itself (direct.rs): its own output says what went wrong
        " The launcher starts this game directly with dinput8 native first: see launcher/game-start.log for what umu-run, Proton or Wine said, and check that dinput8.dll in the game folder is Kiln's.".to_string()
    } else if proton && facts.has_dinput8_override {
        // The override is already set: repeating it cannot fix a failed start.
        " Check Steam's launch options for Enshrouded (WINEDLLOVERRIDES) and the Proton version.".to_string()
    } else if proton {
        format!(" If Steam did not load Kiln's dinput8.dll, try this fallback in Enshrouded's Steam launch options: {}. If Kiln still does not load, check the Proton version and kiln/logs/proxy.log.", facts.override_advice.as_deref().unwrap_or(crate::game::DLL_OVERRIDE_ADVICE))
    } else {
        " Check that dinput8.dll in the game folder is Kiln's and that no antivirus blocks it.".to_string()
    };
    let mut said = false;
    if facts.running {
        let current = boot.is_some_and(|b| crate::launch::boot_is_current(i, b));
        if !current {
            cx.find("not-loaded", Level::Problem, format!("The game runs now, but Kiln did not start in it: there is no boot.json from this start.{why}"));
            said = true;
        }
    } else if let Some(last) = dirs.and_then(|d| crate::launch::last_launch(d, i).ok().flatten())
        && let Some(launched) = crate::util::parse_rfc3339(&last.launched)
        && last.mode != crate::launch::LaunchMode::Vanilla
    {
        let boot_at = boot_time.map(crate::util::unix);
        if boot_at.is_none_or(|b| b + 30 < launched) {
            let boot_is = boot_at.map(|b| format!("older ({})", crate::util::rfc3339(b))).unwrap_or_else(|| "missing".into());
            // Kiln loaded in that start when its progress.json is of it: the game ended while Kiln was starting (KR-YNQZ:
            // in ShroudForge's early step, 1.2 s in), which is no reason to doubt dinput8.dll
            let text = match stopped_while_starting(&i.path, launched) {
                Some(p) => {
                    let secs = p.at_ms as f64 / 1000.0;
                    let at = progress_step(&p).map(|s| format!("its last step, {secs:.1} s in, was \"{s}\""))
                        .unwrap_or_else(|| format!("{secs:.1} s in, before its first step"));
                    format!("The last start from the launcher ({}) loaded Kiln (pid {}), but the game ended before Kiln finished starting: \
                        {at} ({}), and boot.json is {boot_is}.", last.launched, p.pid, crate::start_progress::PROGRESS_JSON)
                }
                None => format!("The last start from the launcher ({}) did not load Kiln: boot.json is {boot_is}.{why}", last.launched),
            };
            cx.find("not-loaded", Level::Problem, text);
            said = true;
        }
    }
    let game_log = ["enshrouded.log", "logs/enshrouded_server.log"].iter().any(|l| rel(&i.path, l).is_file());
    if !said && boot.is_none() && !proxy_log && game_log {
        cx.find("not-loaded", Level::Problem, format!("The game has run here, but Kiln never started: there is no kiln/logs/proxy.log and no boot.json.{why}"));
        said = true;
    }
    if !said && proton && facts.override_missing {
        cx.find("proton-override", Level::Warning, format!("Steam's launch options for Enshrouded have no dinput8 override: under Proton Kiln may not load. Set: {}", facts.override_advice.as_deref().unwrap_or(crate::game::DLL_OVERRIDE_ADVICE)));
    }
}

#[allow(clippy::too_many_arguments)]
fn finish(mut cx: Ctx, mut s: String, sys: String, body: String, input: &ReportInput, dirs: Option<&DataDirs>, tool: &str, now: SystemTime, facts: &Facts) -> Report {
    // the launcher's own log
    let llog = input.launcher_log.clone().or_else(|| dirs.map(|d| d.logs().join("launcher.log")));
    if let Some(p) = llog {
        match tail(&p, SMALL_LOG_MAX) {
            Ok(Some((t, cut))) => {
                check_blocked_install(&mut cx, &t, facts);
                cx.add_text("launcher/launcher.log", &t, cut.then_some("... (older lines left out)"))
            }
            Ok(None) => {}
            Err(e) => cx.err("launcher.log", format!("could not read: {e}")),
        }
    }
    cx.findings.sort_by_key(|f| f.level);
    let _ = writeln!(s, "\n== Findings ==");
    if cx.findings.is_empty() {
        let _ = writeln!(s, "Nothing that explains a common failure was found.");
    }
    for f in &cx.findings {
        let _ = writeln!(s, "- [{}] {}", match f.level { Level::Problem => "problem", Level::Warning => "warning", Level::Info => "note" }, f.text);
    }
    let _ = writeln!(s, "\n== System ==\n{sys}");
    s.push_str(&body);
    if !cx.errors.is_empty() {
        let _ = writeln!(s, "== Not readable ==");
        for e in &cx.errors {
            let _ = writeln!(s, "- {e}");
        }
        s.push('\n');
    }
    let _ = writeln!(s, "== Files in this report ==");
    for (n, b) in &cx.files {
        let _ = writeln!(s, "  {n} ({})", crate::util::human_size(b.len() as u64));
    }
    if !cx.left_out.is_empty() {
        let _ = writeln!(s, "Left out by the player (other mods' own files):");
        for (n, _) in &cx.left_out {
            let _ = writeln!(s, "  {n}");
        }
    }
    let _ = writeln!(s, "\n== Left out ==\n{LEFT_OUT}");
    let s: String = s.lines().map(str::trim_end).collect::<Vec<_>>().join("\n") + "\n";
    let summary = redact(&s, cx.red);

    let mut short = String::new();
    let _ = writeln!(short, "Kiln support report, {}", rfc(now));
    let _ = writeln!(short, "{tool}; Kiln in the game: {}", facts.kiln.as_deref().unwrap_or("not installed"));
    if let Some(c) = &input.channel {
        let _ = writeln!(short, "Channel: {}", c.words());
    }
    let os = os_info();
    let _ = writeln!(short, "OS: {}{}; Steam Deck: {}", os.name, facts.proton.as_deref().map(|p| format!("; Proton: {p}")).unwrap_or_default(),
        if input.deck.unwrap_or(os.deck) { "yes" } else { "no" });
    if let Some(side) = facts.side {
        let _ = writeln!(short, "Game: {side}, Steam build {}, known to Kiln: {}", facts.build.as_deref().unwrap_or("unknown"), match facts.known_build {
            Some(Some(true)) => "yes",
            Some(Some(false)) => "not verified",
            Some(None) => "no",
            None => "?",
        });
    }
    if let Some(p) = &facts.profile {
        let _ = writeln!(short, "Profile: {p}");
    }
    let _ = writeln!(short, "Last start: {}", facts.last_start.as_deref().unwrap_or("no boot.json"));
    if !cx.findings.is_empty() {
        let _ = writeln!(short, "Findings:");
        for f in cx.findings.iter().filter(|f| f.level != Level::Info) {
            let _ = writeln!(short, "- {}", f.text);
        }
    }
    let mut short = redact(&short, cx.red);
    if short.chars().count() > SHORT_MAX {
        short = short.chars().take(SHORT_MAX).collect::<String>() + "\n... (more in the report's summary.txt)\n";
    }

    let deck = input.deck.unwrap_or(os.deck);
    let runner = match facts.platform {
        Some(Platform::Windows) => Some("Windows".to_string()),
        _ => facts.proton.clone(),
    };
    let public = PublicFacts {
        kiln: facts.kiln.as_deref().map(|k| redact(k, cx.red)),
        tool: redact(tool, cx.red),
        os: redact(&os.name, cx.red),
        deck,
        runner: runner.map(|r| redact(&r, cx.red)),
        game_build: facts.build.clone(),
        mods_on: facts.mods_on,
    };
    let mut report = Report { written: rfc(now), summary, short, findings: cx.findings, errors: cx.errors, facts: public, files: Vec::new(),
        left_out: Vec::new() };
    let json = serde_json::to_vec_pretty(&serde_json::json!({
        "format": 1, "written": report.written, "tool": redact(tool, cx.red), "channel": input.channel, "findings": report.findings, "errors": report.errors,
        "versions": report.facts.versions(), "leftOut": cx.left_out.iter().map(|(n, _)| n.as_str()).collect::<Vec<_>>(),
    }))
    .unwrap_or_default();
    let mut files = vec![("summary.txt".to_string(), report.summary.clone().into_bytes()), ("report.json".to_string(), json)];
    files.append(&mut cx.files);
    report.files = files;
    report.left_out = cx.left_out;
    report
}

/// What a report never holds, one by one (id, the words in [`LEFT_OUT`]): "What's in this report" lists them.
pub const NEVER_COLLECTED: [(&str, &str); 5] = [
    ("saves", "your saves and world data (the game's savegame folder, kiln/data/)"),
    ("settings", "mod settings and Kiln's config folder (kiln/config/, the profile's config folder): they may hold server passwords and tokens"),
    ("admin", "Kiln.Admin's files (kiln/data/kiln-admin/: the admin token and endpoint)"),
    ("mods", "the contents of your mods (only folder and file names, sizes and times are listed, plus ShroudForge manifest ids and versions in findings)"),
    ("steam", "Steam's own files, besides the compat tool and launch options shown above"),
];

/// What a report never holds (summary.txt's last section).
pub const LEFT_OUT: &str = "Never collected:
- your saves and world data (the game's savegame folder, kiln/data/);
- mod settings and Kiln's config folder (kiln/config/, the profile's config folder): they may hold server passwords and tokens;
- Kiln.Admin's files (kiln/data/kiln-admin/: the admin token and endpoint);
- the contents of your mods (only folder and file names, sizes and times are listed, plus ShroudForge manifest ids and versions in findings);
- Steam's own files, besides the compat tool and launch options shown above.
Replaced in every file: your home folder (<home>), user name (<user>), computer name (<host>), Steam names and ids
(<steam-name>, <steamid>), the names of other players in Kiln's and ShroudLink's logs (<player>), IP and MAC addresses
(<ip>, <mac>), mail addresses (<email>), and the values of fields named like passwords, tokens, keys, addresses, hosts,
URLs or account names (<redacted>; the server you joined last keeps its name, not its address).";

/// Writes the report as a zip (atomically: a temp file of its own next to `out`, `<out>.<pid>.<n>.tmp` created new,
/// renamed to `out`). Returns its size in bytes, read back from `out`: a report that is not there is an error.
#[cfg(feature = "zip")]
pub fn write_zip(report: &Report, out: &Path) -> crate::Result<u64> {
    use std::io::Write;
    static SEQ: std::sync::atomic::AtomicU64 = std::sync::atomic::AtomicU64::new(0);
    let n = SEQ.fetch_add(1, std::sync::atomic::Ordering::Relaxed);
    let tmp = crate::util::with_suffix(out, &format!(".{}.{n}.tmp", std::process::id()));
    let f = std::fs::OpenOptions::new().write(true).create_new(true).open(&tmp).map_err(crate::util::io_at(&tmp, "writing"))?;
    let zerr = |e: zip::result::ZipError| crate::Error::new(crate::ErrorCode::Io, format!("{}: {e}", out.display()));
    let mut w = zip::ZipWriter::new(std::io::BufWriter::new(f));
    let opts = zip_options();
    let result = (|| -> crate::Result<()> {
        for (name, data) in &report.files {
            w.start_file(name.as_str(), opts).map_err(zerr)?;
            w.write_all(data)?;
        }
        let mut inner = w.finish().map_err(zerr)?;
        inner.flush()?;
        inner.into_inner().map_err(|e| crate::Error::from(e.into_error()))?.sync_all()?;
        Ok(())
    })();
    if let Err(e) = result {
        let _ = std::fs::remove_file(&tmp);
        return Err(e);
    }
    crate::util::rename_replace(&tmp, out).inspect_err(|_| {
        let _ = std::fs::remove_file(&tmp);
    })?;
    std::fs::metadata(out).map(|m| m.len()).map_err(crate::util::io_at(out, "reading back"))
}

/// A chosen file, or the dialog-free destinations when a native file chooser cannot be used.
#[cfg(feature = "zip")]
#[derive(Debug, Clone, Copy, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum SaveDestination {
    #[default]
    Chosen,
    Automatic,
    Downloads,
}

/// Existing launcher and user folders, supplied by the caller rather than the working directory.
#[cfg(feature = "zip")]
pub struct SaveFolders {
    pub owned: PathBuf,
    pub downloads: Vec<PathBuf>,
    pub home: Option<PathBuf>,
}

/// Saves a report, retrying from Kiln's folder and falling back to Downloads/home. Returns the actual file and size.
/// Automatic destinations get a new filename on collision: only the player's explicitly chosen file may be replaced.
#[cfg(feature = "zip")]
pub fn save_zip(report: &Report, out: &Path, destination: SaveDestination, folders: &SaveFolders, mut log: impl FnMut(String)) -> crate::Result<(PathBuf, u64)> {
    save_zip_with(report, out, destination, folders, &mut log, &mut write_zip)
}

/// Direct writes stay atomic. If a folder refuses the adjacent temporary file or rename, build the zip in Kiln's
/// state folder and copy it to the destination instead. Only Kiln's folder is created; a missing Downloads falls
/// through to home. All path operations use native Paths, including Windows drive letters and redirected Downloads.
#[cfg(feature = "zip")]
fn save_zip_with(report: &Report, out: &Path, destination: SaveDestination, folders: &SaveFolders, log: &mut impl FnMut(String), write: &mut impl FnMut(&Report, &Path) -> crate::Result<u64>) -> crate::Result<(PathBuf, u64)> {
    let name = out.file_name().ok_or_else(|| crate::Error::invalid("Choose a report file name."))?;
    let mut paths = match destination {
        SaveDestination::Chosen => vec![out.to_path_buf()],
        SaveDestination::Automatic => vec![folders.owned.join(name)],
        SaveDestination::Downloads => Vec::new(),
    };
    paths.extend(folders.downloads.iter().chain(folders.home.iter()).filter(|p| p.is_absolute()).map(|p| p.join(name)));
    let mut tried = Vec::new();
    let mut failures = Vec::new();
    for path in paths {
        if tried.contains(&path) { continue; }
        tried.push(path.clone());
        log(format!("support report: trying {}", path.display()));
        let automatic = !matches!(destination, SaveDestination::Chosen) || path != out;
        let path = if automatic {
            let reserved = (|| {
                if path.parent() == Some(folders.owned.as_path()) {
                    std::fs::create_dir_all(&folders.owned).map_err(crate::util::io_at(&folders.owned, "creating Kiln's state folder"))?;
                }
                reserve_report_path(&path, log)
            })();
            match reserved {
                Ok(path) => path,
                Err(e) => {
                    let message = format!("support report: reserving {} failed: {}", path.display(), e.message);
                    log(message.clone());
                    failures.push(message);
                    continue;
                }
            }
        } else { path };
        let direct = (|| {
            if path.parent() == Some(folders.owned.as_path()) {
                std::fs::create_dir_all(&folders.owned).map_err(crate::util::io_at(&folders.owned, "creating Kiln's state folder"))?;
            }
            write(report, &path)
        })();
        match direct {
            Ok(size) => {
                log(format!("support report written to {} ({size} bytes)", path.display()));
                return Ok((path, size));
            }
            Err(e) => {
                let message = format!("support report: writing {} failed: {}", path.display(), e.message);
                log(message.clone());
                failures.push(message);
            }
        }
        // An unpredictable, per-save path: concurrent saves cannot copy or delete each other's staged zip.
        // This also helps a chosen file in Kiln's own folder when only its adjacent temporary filename is refused.
        let stage = match crate::util::random_bytes::<16>() {
            Ok(bytes) => folders.owned.join(format!(".kiln-report-{}.zip", crate::hash::hex(&bytes))),
            Err(e) => {
                let message = format!("support report: staging in {} for {} failed: {}", folders.owned.display(), path.display(), e.message);
                log(message.clone());
                failures.push(message);
                if automatic { remove_report_temp(&path, log); }
                continue;
            }
        };
        log(format!("support report: trying staging file {} for {}", stage.display(), path.display()));
        let copied = (|| {
            std::fs::create_dir_all(&folders.owned).map_err(crate::util::io_at(&folders.owned, "creating Kiln's state folder"))?;
            let size = write(report, &stage)?;
            log(format!("support report: copying {} to {}", stage.display(), path.display()));
            std::fs::copy(&stage, &path).map_err(crate::util::io_at(&path, "copying the report to"))?;
            std::fs::OpenOptions::new().write(true).open(&path).and_then(|f| f.sync_all()).map_err(crate::util::io_at(&path, "flushing"))?;
            let actual = std::fs::metadata(&path).map_err(crate::util::io_at(&path, "reading back"))?.len();
            if actual != size { return Err(crate::Error::new(crate::ErrorCode::Io, format!("{}: copied report has {actual} bytes, expected {size}", path.display()))); }
            Ok(size)
        })();
        remove_report_temp(&stage, log);
        match copied {
            Ok(size) => {
                log(format!("support report written to {} ({size} bytes, copied from Kiln's state folder)", path.display()));
                return Ok((path, size));
            }
            Err(e) => {
                let message = format!("support report: staging {} and copying to {} failed: {}", stage.display(), path.display(), e.message);
                log(message.clone());
                failures.push(message);
                if automatic { remove_report_temp(&path, log); }
            }
        }
    }
    let message = format!("The support report was NOT saved. {}", failures.join("; "));
    log(message.clone());
    Err(crate::Error::new(crate::ErrorCode::Io, message))
}

/// Reserve a new file without following or replacing an existing file/link, including a racing report save.
#[cfg(feature = "zip")]
fn reserve_report_path(path: &Path, log: &mut impl FnMut(String)) -> crate::Result<PathBuf> {
    for n in 0..1000 {
        let candidate = if n == 0 { path.to_path_buf() } else {
            let mut name = path.file_stem().unwrap_or_default().to_os_string();
            name.push(format!("-{n}.zip"));
            path.with_file_name(name)
        };
        log(format!("support report: reserving {}", candidate.display()));
        match std::fs::OpenOptions::new().write(true).create_new(true).open(&candidate) {
            Ok(_) => return Ok(candidate),
            Err(e) if e.kind() == std::io::ErrorKind::AlreadyExists => {
                log(format!("support report: reserving {} failed: {e}; trying a new name", candidate.display()));
            }
            Err(e) => return Err(crate::util::io_at(&candidate, "reserving a report file")(e)),
        }
    }
    Err(crate::Error::new(crate::ErrorCode::AlreadyExists, format!("{}: no free report filename", path.display())))
}

#[cfg(feature = "zip")]
fn remove_report_temp(path: &Path, log: &mut impl FnMut(String)) {
    if let Err(e) = std::fs::remove_file(path) {
        if e.kind() != std::io::ErrorKind::NotFound {
            log(format!("support report: removing temporary file {} failed: {e}", path.display()));
        }
    }
}

/// Each file's options in a report zip: deflated, the time of writing.
#[cfg(feature = "zip")]
fn zip_options() -> zip::write::SimpleFileOptions {
    let (y, mo, d, h, mi, s) = crate::util::utc(SystemTime::now());
    let when = zip::DateTime::from_date_and_time(y.clamp(1980, 2107) as u16, mo as u8, d as u8, h as u8, mi as u8, s as u8)
        .unwrap_or_else(|_| zip::DateTime::default_for_write());
    zip::write::SimpleFileOptions::default()
        .compression_method(zip::CompressionMethod::Deflated)
        .compression_level(Some(6))
        .last_modified_time(when)
        .unix_permissions(0o644)
}

/// The report zip [`write_zip`] writes, in memory: for a caller that sends it on (the launcher's "Send to Kiln
/// support") and must not leave it in a shared temp folder, even for a moment.
#[cfg(feature = "zip")]
pub fn zip_bytes(report: &Report) -> crate::Result<Vec<u8>> {
    use std::io::Write;
    let zerr = |e: zip::result::ZipError| crate::Error::new(crate::ErrorCode::Io, format!("the report zip: {e}"));
    let mut w = zip::ZipWriter::new(std::io::Cursor::new(Vec::new()));
    let opts = zip_options();
    for (name, data) in &report.files {
        w.start_file(name.as_str(), opts).map_err(zerr)?;
        w.write_all(data)?;
    }
    Ok(w.finish().map_err(zerr)?.into_inner())
}

#[cfg(test)]
#[path = "report_tests.rs"]
mod tests;

fn shroudlink_finding_text(profile: &str, mods: &[String]) -> String {
    format!("Profile '{profile}' enables Shroudlink mods: {}. Shroudlink's MODS tab comes from its client addon, which this profile does not load. Your Shroudlink mods still run; switch the addon on to get the tab.", mods.join(", "))
}

#[cfg(test)]
mod shroudlink_notice_tests {
    #[test]
    fn finding_names_profile_and_mods() {
        let text = super::shroudlink_finding_text("test", &["assistants".into(), "expeditions".into()]);
        assert!(text.contains("Profile 'test'") && text.contains("assistants, expeditions"));
        assert!(text.contains("MODS tab") && text.contains("mods still run"));
    }
}
