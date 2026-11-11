//! Support reports: one zip with what a maintainer needs to diagnose "mods don't load", with nothing private in it
//! (docs/kiln/launcher/support-report.md). The launcher's "Save report..." (`report_save`) and `kilnctl report` write
//! it; "Copy summary" (`report_summary`) is [`Report::short`].
//!
//! - [`build`] reads everything (each section on its own: one that fails says why and the rest is still written) and
//!   returns the summary, the findings and the files, already redacted. Nothing is written to the game folder.
//! - [`redact`] is the one redaction function every text goes through: the home folder in Linux, Windows and Wine `Z:\`
//!   form (also with doubled backslashes inside JSON), the user name and the machine name alone, Steam ids, IP addresses.
//!   [`redact_json`] also blanks fields whose names say they hold a secret or an address.
//! - [`write_zip`] (feature `zip`) writes the zip.
//!
//! Layout of the zip: `summary.txt` (for a forum post), `report.json` (findings and failed sections), `game/<path>`
//! (tails of kiln.log, proxy.log, eml.log, shroudlink_client.log, the newest crash report; `kiln/state/*.json`, `kiln/profile.json`,
//! `kiln/chain.ini`, `kiln/kiln.json`, `kiln/.install.json`), `greeneye-mods/<id>/*.log|*.status` (tails of each Greeneye
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
    /// Only between non-word characters (short or common names); else anywhere (`ivan-pc`, `IVAN~1`).
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
    "redacted", "steamid", "name", "steam-name", "ip",
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
        for root in roots {
            let Ok(text) = std::fs::read(root.path.join("config/loginusers.vdf")) else { continue };
            let Ok(v) = crate::vdf::parse(&String::from_utf8_lossy(&text)) else { continue };
            for (_, u) in v.get("users").map(|u| u.entries()).unwrap_or_default() {
                if let Some(a) = u.get("AccountName").and_then(|x| x.as_str()) {
                    r.add_word(a, "<user>");
                }
                if let Some(p) = u.get("PersonaName").and_then(|x| x.as_str()) {
                    r.add_whole_word(p, "<steam-name>");
                }
            }
        }
        r
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
    chars.into_iter().collect()
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
            && (!w.whole || ((i == 0 || !is_word(text[i - 1])) && (i + n == text.len() || !is_word(text[i + n]))));
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

/// Dotted IPv4 addresses (each part 0-255) except 127.x.x.x and 0.0.0.0.
fn redact_ips(text: &[char]) -> Vec<char> {
    let mut out = Vec::with_capacity(text.len());
    let mut i = 0;
    while i < text.len() {
        let start_ok = text[i].is_ascii_digit() && (i == 0 || !(text[i - 1].is_alphanumeric() || text[i - 1] == '.'));
        if start_ok && let Some((end, parts)) = ipv4_at(text, i) {
            if parts[0] == 127 || parts == [0, 0, 0, 0] {
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
    PART.iter().any(|p| k.contains(p))
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
    /// Default [`Redactor::current`].
    pub redactor: Option<Redactor>,
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
    /// `proton-override`, `unknown-build`, `safe-idle`, `own-loader`, `loader-in-folder`, `unlisted-mods`,
    /// `mod-failed`, `crash`, `profile-invalid`, `newer-mods`, `no-game`.
    pub id: String,
    pub level: Level,
    pub text: String,
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
    /// (path in the zip, bytes), summary.txt and report.json included.
    #[serde(skip)]
    pub files: Vec<(String, Vec<u8>)>,
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

/// The last `max` bytes of a file (from a line start), CR removed. Err: why it is not readable.
fn tail(path: &Path, max: u64) -> std::result::Result<Option<(String, bool)>, String> {
    match std::fs::metadata(path) {
        Err(e) if e.kind() == std::io::ErrorKind::NotFound => return Ok(None),
        Err(e) => return Err(e.to_string()),
        Ok(m) if !m.is_file() => return Err("not a file".into()),
        Ok(_) => {}
    }
    crate::logs::read_path(path, None, Some(max)).map(|c| Some((c.text, c.truncated))).map_err(|e| e.message)
}

/// The first `max` bytes of a file; (bytes, cut).
fn head(path: &Path, max: u64) -> std::result::Result<Option<(Vec<u8>, bool)>, String> {
    use std::io::Read;
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

/// The plugin stood aside for the engine's own loader (EML `aside`, ShroudForge `self-hosted`, `steppedAside`).
fn stood_aside(state: &serde_json::Value) -> bool {
    state.get("steppedAside").and_then(|v| v.as_bool()) == Some(true)
        || engine_headline(state).is_some_and(|w| {
            let w = w.to_ascii_lowercase();
            w.contains("aside") || w == "self-hosted"
        })
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
    files: Vec<(String, Vec<u8>)>,
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
    let red = input.redactor.clone().unwrap_or_else(|| Redactor::current(&roots));
    let dirs = input.dirs.clone().or_else(|| DataDirs::locate().ok());
    let install: Option<GameInstall> = input.install.clone().or_else(|| {
        let d = input.game_dir.as_ref()?;
        let data_root = dirs.as_ref().map(|d| d.root.clone()).unwrap_or_default();
        crate::game::inspect_with(d, &roots, &data_root).ok()
    });
    let game_dir: Option<PathBuf> = install.as_ref().map(|i| i.path.clone()).or_else(|| input.game_dir.clone());
    let mut cx = Ctx { game: game_dir.as_deref(), red: &red, files: Vec::new(), total: 0, errors: Vec::new(), findings: Vec::new() };
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
    for (k, v) in &input.facts {
        let _ = writeln!(sys, "{:<15}{v}", format!("{k}:"));
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
        // compat tool and Proton version: Steam's config.vdf and the prefix's version file, read only
        if i.platform == Platform::Proton {
            let tool = roots.iter().find_map(|r| crate::steam::compat_tool(r, i.app_id).ok().flatten());
            let version = i.prefix.as_ref().and_then(|p| p.parent()).and_then(|c| std::fs::read_to_string(c.join("version")).ok())
                .map(|v| v.trim().to_string()).filter(|v| !v.is_empty());
            let _ = writeln!(g, "Compat tool:   {} (prefix version: {})", tool.as_deref().unwrap_or("Steam's default"), version.as_deref().unwrap_or("unknown"));
            facts.proton = tool.or(version);
        }
        if let Some(lo) = crate::game::launch_options_info(i, &roots) {
            let _ = writeln!(g, "Launch opts:   {} (dinput8 override: {})", lo.value.as_deref().unwrap_or("(none)"), if lo.has_dll_override { "yes" } else { "no" });
            facts.override_missing = !lo.has_dll_override;
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
    let _ = writeln!(k, "Proxies next to the game:");
    if proxies.is_empty() {
        let _ = writeln!(k, "  (none)");
    }
    for p in &proxies {
        let _ = writeln!(k, "  {:<16} {}", p.name, proxy_text(&p.class));
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
                    off += usize::from(!on);
                    let _ = writeln!(pr, "  {:<12} {:<32} {:<16} {}", kind.as_str(), id, v.as_deref().unwrap_or("-"), if on { "on" } else { "off" });
                }
            }
            if n == 0 {
                let _ = writeln!(pr, "  (no mods)");
            }
            facts.profile = Some(format!("{} ({}{})", p.name.as_deref().unwrap_or(&p.id), plural(n, "mod", "mods"), if off > 0 { format!(", {off} off") } else { String::new() }));
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
    if let Some(m) = &mods {
        let _ = writeln!(ls, "Kiln mods (mods.json, {}):", m.written);
        for x in &m.mods {
            let st = format!("{:?}", x.state).to_lowercase();
            let _ = writeln!(ls, "  {:<32} {:<16} {:<9}{}", x.id, x.version.as_deref().unwrap_or("-"), st, x.reason.as_deref().map(|r| format!(" {r}")).unwrap_or_default());
            if x.state == crate::state::ManagedState::Failed {
                failed.push(format!("{} (Kiln): {}", x.id, x.reason.as_deref().unwrap_or("no reason given")));
            }
        }
    }
    if let Some(n) = &native {
        let _ = writeln!(ls, "Native mods (native.json, {}, policy {}):", n.written, n.policy);
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
        if stood_aside(st) && let Some((id, engine, file)) = native_loaders.iter().find(|(_, en, _)| Some(*en) == e.kind.engine()) {
            cx.find("native-loader", Level::Problem, format!("{} stood aside at the last start: the profile's {}.",
                e.plugin, crate::profile::native_loader_warning(id, *engine, file)));
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
    let aside: Vec<ModKind> = engines.iter().filter(|e| e.state.as_ref().is_some_and(stood_aside)).map(|e| e.kind).collect();
    // a plugin that says it did not stand aside, in a state file of the last start (boot.json's time or later)
    let boot_ms = boot_time.map(|t| crate::util::unix(t) * 1000);
    let ran: Vec<ModKind> = engines.iter().filter(|e| e.state.as_ref().is_some_and(|st| {
        st.get("steppedAside").and_then(|v| v.as_bool()) == Some(false)
            && boot_ms.zip(st.get("updatedUnixMs").and_then(|v| v.as_i64())).is_some_and(|(b, u)| u + 1000 >= b)
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
    // Native UI initialization and later player-menu visibility are separate addon paths; kiln.log alone cannot
    // distinguish them. Read only this bounded log, not the addon's key bindings or per-server mod settings.
    cx.add_tail("shroudlink_client.log", SMALL_LOG_MAX);
    check_not_loaded(&mut cx, install.as_ref(), dirs.as_ref(), receipt.is_some() || dinput8.as_ref().is_some_and(|d| d.kind == ProxyKind::Kiln),
        boot.as_ref(), boot_time, proxy_log.is_some(), &facts);
    if receipt.is_some() {
        match facts.known_build {
            Some(None) => cx.find("unknown-build", Level::Problem, format!(
                "This game build ({}, Steam build {}) is one this Kiln does not know: Kiln stays idle and loads no mods until a Kiln update adds it.",
                facts.exe_sha.as_deref().map(|h| &h[..12.min(h.len())]).unwrap_or("?"), facts.build.as_deref().unwrap_or("unknown"))),
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
    running: bool,
    profile: Option<String>,
    profile_broken: bool,
    last_start: Option<String>,
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
    let roots: Vec<PathBuf> = std::iter::once(game.join(crate::engine_mods::GREENEYE_MODS))
        .chain(dirs.map(|d| d.library().join(ModKind::Greeneye.as_str()))).collect();
    // inside one of the roots: no `..`, no link from the root down (the root included), the resolved folder below the
    // resolved root
    let allowed = |dir: &Path| roots.iter().any(|root| {
        !dir.components().any(|c| matches!(c, std::path::Component::ParentDir)) && dir.starts_with(root)
            && engine_mods::refuse_symlink_path(root, dir).is_ok()
            && matches!((root.canonicalize(), dir.canonicalize()), (Ok(r), Ok(d)) if d != r && d.starts_with(&r))
    });
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
        if !allowed(&dir) {
            if !skipped.contains(&id) { skipped.push(id); }
            continue;
        }
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
        for p in logs.into_iter().take(GREENEYE_FILES_PER_MOD) {
            let file: String = p.file_name().map(|n| n.to_string_lossy().into_owned()).unwrap_or_default()
                .chars().map(|c| if c.is_ascii_alphanumeric() || "._+-".contains(c) { c } else { '_' }).collect();
            cx.add_tail_at(&format!("greeneye-mods/{safe}/{file}"), &p, GREENEYE_LOG_MAX);
        }
    }
    if !skipped.is_empty() {
        cx.find("greeneye-log-skipped", Level::Info, format!(
            "The logs of {} were left out: {} folder is not inside the game's greeneye folder or the launcher's Library \
             (or is reached through a link), and the report reads no other folder.",
            skipped.join(", "), if skipped.len() == 1 { "its" } else { "their" }));
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
        if ran.contains(&kind) {
            // r1111 (Steam Deck, no dbgcore override): the note said kiln-greeneye stood aside while it had run the mods
            cx.find("loader-in-folder", Level::Info, format!(
                "{} is {}'s own loader. It did not start at the last start ({}), so Kiln's {} plugin ran the {} mods itself.",
                p.name, engine.name(),
                if windows { "something kept it from loading" } else { "under Wine or Proton it loads only with a DLL override for that name" },
                short_name(engine), short_name(engine)));
            continue;
        }
        cx.find("loader-in-folder", if windows { Level::Warning } else { Level::Info }, format!(
            "{} is {}'s own loader. {}, and Kiln's {} plugin then stands aside for it: the {} mods in the profile do not load through Kiln.",
            p.name, engine.name(),
            if windows { "On Windows the game always loads it" } else { "Under Wine or Proton it loads only with a DLL override for that name" },
            short_name(engine), short_name(engine)));
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
    let why = if proton && facts.override_missing {
        format!(" Under Proton the game loads Kiln's dinput8.dll only with this in Steam's launch options for Enshrouded: {}", crate::game::DLL_OVERRIDE_ADVICE)
    } else if proton {
        " Check Steam's launch options for Enshrouded (WINEDLLOVERRIDES) and the Proton version.".to_string()
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
            cx.find("not-loaded", Level::Problem, format!(
                "The last start from the launcher ({}) did not load Kiln: boot.json is {}.{why}",
                last.launched, boot_at.map(|b| format!("older ({})", crate::util::rfc3339(b))).unwrap_or_else(|| "missing".into())));
            said = true;
        }
    }
    let game_log = ["enshrouded.log", "logs/enshrouded_server.log"].iter().any(|l| rel(&i.path, l).is_file());
    if !said && boot.is_none() && !proxy_log && game_log {
        cx.find("not-loaded", Level::Problem, format!("The game has run here, but Kiln never started: there is no kiln/logs/proxy.log and no boot.json.{why}"));
        said = true;
    }
    if !said && proton && facts.override_missing {
        cx.find("proton-override", Level::Warning, format!("Steam's launch options for Enshrouded have no dinput8 override: under Proton Kiln may not load. Set: {}", crate::game::DLL_OVERRIDE_ADVICE));
    }
}

#[allow(clippy::too_many_arguments)]
fn finish(mut cx: Ctx, mut s: String, sys: String, body: String, input: &ReportInput, dirs: Option<&DataDirs>, tool: &str, now: SystemTime, facts: &Facts) -> Report {
    // the launcher's own log
    let llog = input.launcher_log.clone().or_else(|| dirs.map(|d| d.logs().join("launcher.log")));
    if let Some(p) = llog {
        match tail(&p, SMALL_LOG_MAX) {
            Ok(Some((t, cut))) => cx.add_text("launcher/launcher.log", &t, cut.then_some("... (older lines left out)")),
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
    let _ = writeln!(s, "\n== Left out ==\n{LEFT_OUT}");
    let s: String = s.lines().map(str::trim_end).collect::<Vec<_>>().join("\n") + "\n";
    let summary = redact(&s, cx.red);

    let mut short = String::new();
    let _ = writeln!(short, "Kiln support report, {}", rfc(now));
    let _ = writeln!(short, "{tool}; Kiln in the game: {}", facts.kiln.as_deref().unwrap_or("not installed"));
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

    let mut report = Report { written: rfc(now), summary, short, findings: cx.findings, errors: cx.errors, files: Vec::new() };
    let json = serde_json::to_vec_pretty(&serde_json::json!({
        "format": 1, "written": report.written, "tool": redact(tool, cx.red), "findings": report.findings, "errors": report.errors,
    }))
    .unwrap_or_default();
    let mut files = vec![("summary.txt".to_string(), report.summary.clone().into_bytes()), ("report.json".to_string(), json)];
    files.append(&mut cx.files);
    report.files = files;
    report
}

/// What a report never holds (summary.txt's last section).
pub const LEFT_OUT: &str = "Never collected:
- your saves and world data (the game's savegame folder, kiln/data/);
- mod settings and Kiln's config folder (kiln/config/, the profile's config folder): they may hold server passwords and tokens;
- Kiln.Admin's files (kiln/data/kiln-admin/: the admin token and endpoint);
- the contents of your mods (only folder and file names, sizes and times are listed);
- Steam's own files, besides the compat tool and launch options shown above.
Replaced in every file: your home folder (<home>), user name (<user>), computer name (<host>), Steam names and ids
(<steam-name>, <steamid>), IP addresses (<ip>), and the values of fields named like passwords, tokens, keys, addresses,
hosts or URLs (<redacted>; the server you joined last keeps its name, not its address).";

/// Writes the report as a zip (atomically). Returns its size in bytes.
#[cfg(feature = "zip")]
pub fn write_zip(report: &Report, out: &Path) -> crate::Result<u64> {
    use std::io::Write;
    let tmp = crate::util::with_suffix(out, ".tmp");
    let f = std::fs::File::create(&tmp).map_err(crate::util::io_at(&tmp, "writing"))?;
    let zerr = |e: zip::result::ZipError| crate::Error::new(crate::ErrorCode::Io, format!("{}: {e}", out.display()));
    let mut w = zip::ZipWriter::new(std::io::BufWriter::new(f));
    let (y, mo, d, h, mi, s) = crate::util::utc(SystemTime::now());
    let when = zip::DateTime::from_date_and_time(y.clamp(1980, 2107) as u16, mo as u8, d as u8, h as u8, mi as u8, s as u8)
        .unwrap_or_else(|_| zip::DateTime::default_for_write());
    let opts = zip::write::SimpleFileOptions::default()
        .compression_method(zip::CompressionMethod::Deflated)
        .compression_level(Some(6))
        .last_modified_time(when)
        .unix_permissions(0o644);
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
    crate::util::rename_replace(&tmp, out)?;
    Ok(std::fs::metadata(out)?.len())
}

#[cfg(test)]
#[path = "report_tests.rs"]
mod tests;
