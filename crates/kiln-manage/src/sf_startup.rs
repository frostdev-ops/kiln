//! ShroudForge's own record of its startup pass, read for the launcher and the support report.
//!
//! ShroudForge writes its startup mods (capability `patch`, and the EML packages it runs) into the game's data files
//! in one pass before the game's own code runs, and records the outcome in the `assets` section of its state file
//! (`<paths.state>/state.json`, by default `<game>/shroudforge/state.json`): `status` (`preparing`, `applied`,
//! `failed`), `phase`, `target` (`enshrouded`, `enshrouded_server`), `completedAt` (unix seconds) and, when it failed,
//! `error` (ShroudForge's `asset_preparation/apply_asset_mods.rs` and `recover_failed_startup_assets.rs`). After a
//! failed pass ShroudForge puts the game data back and lets the game start, so every mod that needs the pass is missing
//! and only ShroudForge's own log and window say so. [`failure`] reads the record, so Kiln's launcher and its support
//! report say it too.
//!
//! The record names no process, so [`failure`] ties it to Kiln's last start (`kiln/state/boot.json`): it was written in
//! that start or later ([`BOOT_SLACK`]), and something ran ShroudForge then. Kiln's adapter (kiln-shroudforge) says in
//! `kiln/state/shroudforge.json` whether it hosted ShroudForge or found ShroudForge's own loader in the process
//! (`self-hosted`); that word counts only when the adapter wrote it in Kiln's last start (its `pid` and `updated` against
//! boot.json's `pid` and `started`). A start that skips Kiln's plugins ("Play once: without mods") leaves the adapter's
//! word and ShroudForge's record as an earlier start left them: nothing is said about it unless ShroudForge's own loader
//! wrote a newer record. ShroudForge does not take a failed pass as done (`calculate_preparation_fingerprint.rs`
//! `matches` wants `applied`), so every start in which it runs writes `preparing` and then the new outcome over it.
//!
//! Kiln's ShroudForge (since 0.2.0-dev.29.18) runs a pass that failed in one mod's own code once more without that mod
//! (and the mods that need it). The record then says `applied` with the mods left out in `skipped` (id, name, version,
//! error), or, when the second pass failed too, `failed` with the first error in `error`, `skipped` and the second error
//! in `retryError`. [`skipped`] reads the first case for a warning: the other mods loaded. ShroudForge keeps an `applied`
//! record while the next starts' passes are unchanged, so that one counts however old it is, while Kiln's adapter hosted
//! ShroudForge at Kiln's last start and its preparation returned.
//!
//! One failure has a cure worth naming. ShroudForge up to 1.7.9 replaces `<exe>.kfc_resources` while its own reader
//! still has that file open (`game_file_transaction.rs` `replace`, kfc's `KFCCursor`). Windows lets that pass; Wine and
//! Proton answer "Access denied (os error 5)", at every start, for every startup mod. The ShroudForge that comes with
//! Kiln (the fork, since `03b4950`, Kiln 0.2.0-dev.20) closes the reader first, as upstream's 1.8.0 branch does. A player
//! whose Steam launch options load ShroudForge's own `dbghelp.dll` under Proton therefore gets no startup mod until Kiln
//! hosts ShroudForge (player report 327, 13 Nov 2026: `docs/kiln/shroudforge-compat.md` "ShroudForge's own loader under
//! Wine and Proton"). The fork calls itself 1.7.9 too: its `version.json` says `build: kiln.<commit>` and `host: kiln`,
//! and that, not the version alone, decides whether a build has the fault ([`has_fix`]).

use crate::manifest::ModKind;
use crate::own_loaders::OwnLoader;
use serde::{Deserialize, Serialize};
use std::path::{Path, PathBuf};

/// ShroudForge's words are quoted up to this many characters.
const MAX_ERROR: usize = 300;
/// Mod ids named in a sentence.
const MAX_NAMES: usize = 5;
/// Entries of the record's `skipped` read.
const MAX_SKIPPED: usize = 20;
/// Seconds a pass of Kiln's last start may end before boot.json's `started`: ShroudForge's own loader holds the game's
/// entry point for its pass (about 16 s in report 327) and Kiln boots at that entry point, so its pass can end just
/// before Kiln's start. A record older than this is an earlier start's.
const BOOT_SLACK: u64 = 120;
/// Builds of Kiln's ShroudForge fork that Kiln shipped before the fork closed its reader before publishing (the pins
/// before `03b4950`, Kiln 0.2.0-dev.18 and dev.19): they have the fault of upstream's builds before 1.8.0.
const FORK_BEFORE_FIX: [&str; 2] = ["kiln.e4c6535", "kiln.3586804"];

/// ShroudForge's last startup pass failed, and it is the last start's.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SfStartupFailure {
    /// ShroudForge's own words (the record's `error`), on one line, at most [`MAX_ERROR`] characters.
    pub error: String,
    /// The record's `completedAt`, RFC 3339 (UTC).
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub at: Option<String>,
    /// ShroudForge's own loader ran the pass (true), or the runtime kiln-shroudforge hosts (false).
    pub own_loader: bool,
    /// The version of the ShroudForge that ran the pass, when known (its `version.json`, or the hosted runtime's as
    /// kiln-shroudforge reported it).
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub version: Option<String>,
    /// Its build, when known (`version.json` `build`: upstream's build number, `kiln.<commit>` for Kiln's fork).
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub build: Option<String>,
    /// The switched-on packages of ShroudForge's mods folder that need the pass on this side (ids, sorted): they did
    /// not load.
    pub mods: Vec<String>,
    /// What to change, when the failure is the one above (a ShroudForge build with the open-file fault under Wine or
    /// Proton, "os error 5") and Kiln's own ShroudForge, which has the fix, is there to run the mods instead. A
    /// sentence without its full stop.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub cure: Option<String>,
    /// Kiln's ShroudForge (0.2.0-dev.29.18 on) ran the pass once more without the mod it failed in, and that failed too:
    /// the mod (then the mods left out with it), the record's `skipped`.
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub skipped: Vec<SfSkippedMod>,
    /// The second pass's error (the record's `retryError`), on one line as `error` is.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub retry_error: Option<String>,
    /// ShroudForge's words as the record has them, whole: a report redacts these, then puts them on one line and cuts
    /// them ([`SfStartupFailure::redacted`]).
    #[serde(skip)]
    raw_error: String,
    #[serde(skip)]
    raw_retry_error: Option<String>,
}

/// A mod ShroudForge's startup pass ran without (the record's `skipped`, written by Kiln's ShroudForge since
/// 0.2.0-dev.29.18): the pass failed in it, so ShroudForge ran the pass once more without it (the fork's
/// `asset_preparation/retry_without_failed_mod.rs`). The first is the mod the pass failed in; any after it were left out
/// because they need it.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SfSkippedMod {
    pub id: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub name: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub version: Option<String>,
    /// Its error on one line, at most [`MAX_ERROR`] characters ("needs <id>, which failed in the pregame pass" for a mod
    /// left out with it).
    pub error: String,
    #[serde(skip)]
    raw_error: String,
}

impl SfSkippedMod {
    /// "Wisp Light Timer (wisp_light_timer_a9 1.0.3)", "wisp_light_timer_a9 1.0.3", "wisp_light_timer_a9".
    pub fn label(&self) -> String {
        let id = match &self.version {
            Some(v) => format!("{} {v}", self.id),
            None => self.id.clone(),
        };
        match self.name.as_deref().filter(|n| !n.is_empty() && *n != self.id) {
            Some(name) => format!("{name} ({id})"),
            None => id,
        }
    }

    /// The name a person knows it by: its name, else its id.
    fn called(&self) -> &str {
        self.name.as_deref().filter(|n| !n.is_empty()).unwrap_or(&self.id)
    }

    fn redacted(&self, redact: &impl Fn(&str) -> String) -> SfSkippedMod {
        let raw_error = redact(&self.raw_error);
        SfSkippedMod { error: one_line(&raw_error), raw_error, ..self.clone() }
    }
}

/// The record's `skipped` (at most [`MAX_SKIPPED`] entries; one without an id is no entry).
fn skipped_mods(assets: &serde_json::Map<String, serde_json::Value>) -> Vec<SfSkippedMod> {
    let Some(list) = assets.get("skipped").and_then(|v| v.as_array()) else { return Vec::new() };
    list.iter().filter_map(|m| {
        // an id, name or version: its words on one line, at most 100 characters (they come from a file in the game folder)
        let word = |k: &str| m.get(k).and_then(|v| v.as_str())
            .map(|v| v.split(|c: char| c.is_whitespace() || c.is_control()).filter(|w| !w.is_empty()).collect::<Vec<_>>().join(" "))
            .filter(|v| !v.is_empty()).map(|v| v.chars().take(100).collect::<String>());
        let raw_error = m.get("error").and_then(|v| v.as_str()).unwrap_or("it gave no reason").to_string();
        Some(SfSkippedMod { id: word("id")?, name: word("name"), version: word("version"), error: one_line(&raw_error), raw_error })
    }).take(MAX_SKIPPED).collect()
}

/// ShroudForge's last startup pass failed in one mod and ran again without it (Kiln's ShroudForge since
/// 0.2.0-dev.29.18): the other mods of the pass loaded, that one did not. A warning, not a failure.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SfStartupSkipped {
    /// The mod the pass failed in, then the mods left out because they need it, as far as they are still switched on.
    pub skipped: Vec<SfSkippedMod>,
    /// How many mods that need the pass loaded (the record's `mods`).
    pub loaded: usize,
    /// The record's `completedAt`, RFC 3339 (UTC).
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub at: Option<String>,
    /// ShroudForge's own loader ran the pass (true), or the runtime kiln-shroudforge hosts (false).
    pub own_loader: bool,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub version: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub build: Option<String>,
}

/// ShroudForge's state file: `<paths.state>/state.json`, by default `<game>/shroudforge/state.json` (as
/// [`crate::shroudforge::effects`] finds it). None: the configured folder cannot be named on this system.
fn state_file(game_dir: &Path) -> Option<PathBuf> {
    let default = || game_dir.join("shroudforge/state.json");
    let config = crate::shroudforge::read_effect_json(game_dir, &game_dir.join(crate::shroudforge::CONFIG_FILE)).ok()?;
    match config.as_ref().and_then(|v| v.pointer("/paths/state")) {
        None | Some(serde_json::Value::Null) => Some(default()),
        Some(serde_json::Value::String(p)) if p.trim().is_empty() => Some(default()),
        Some(serde_json::Value::String(p)) => crate::shroudforge::resolve_game_folder(game_dir, p.trim()).map(|d| d.join("state.json")),
        _ => None,
    }
}

/// One line of at most [`MAX_ERROR`] characters: the text comes from a file in the game folder.
fn one_line(text: &str) -> String {
    let mut out = String::new();
    for word in text.split(|c: char| c.is_whitespace() || c.is_control()).filter(|w| !w.is_empty()) {
        if !out.is_empty() {
            out.push(' ');
        }
        out.push_str(word);
    }
    if out.chars().count() > MAX_ERROR {
        return out.chars().take(MAX_ERROR).collect::<String>() + "...";
    }
    // the sentences around it end it themselves
    out.trim_end_matches(['.', ' ']).to_string()
}

/// A ShroudForge's `version.json`: `{"version": "1.7.9", "build": "kiln.63b8862", "host": "kiln"}` (Kiln's fork), upstream's
/// without `host`.
#[derive(Debug, Clone, Default)]
struct Build {
    version: Option<String>,
    build: Option<String>,
    host: Option<String>,
}

/// `<dir>/version.json`, None when it is missing or not a JSON object.
fn read_build(game_dir: &Path, dir: &Path) -> Option<Build> {
    let v = crate::shroudforge::read_effect_json(game_dir, &dir.join("version.json")).ok()??;
    let word = |k: &str| v.get(k).and_then(|x| x.as_str()).map(str::to_owned);
    v.is_object().then(|| Build { version: word("version"), build: word("build"), host: word("host") })
}

/// `1.7.9`, `v1.7.9`, `1.6.0-beta.2`, `1.7.9+build.42`: a semantic version with its three numbers; anything else is
/// none (`1.7`, `1.7.garbage`, `1.7.9.1`).
fn release_version(text: &str) -> Option<(u64, u64)> {
    let p = crate::semver::try_parse_partial(text)?;
    (p.count == 3 && !p.wildcard).then_some((p.parts[0], p.parts[1]))
}

/// Whether that ShroudForge publishes its pass with its reader closed: 1.8.0 and later, and Kiln's fork (but for the
/// builds Kiln shipped before the fix). Some(false): an upstream build before 1.8.0. None: its version is no version, and
/// nothing is judged on it.
fn has_fix(b: &Build) -> Option<bool> {
    let (major, minor) = b.version.as_deref().and_then(release_version)?;
    if (major, minor) >= (1, 8) {
        return Some(true);
    }
    let build = b.build.as_deref().unwrap_or_default();
    let fork = b.host.as_deref() == Some("kiln") || build.starts_with("kiln.");
    Some(fork && !FORK_BEFORE_FIX.contains(&build))
}

/// ShroudForge's `ModManifest::requires_pregame`: a `patch` part, or `export` without a runtime part. A legacy EML
/// package is applied by the same pass.
fn needs_pass(m: &crate::shroudforge::SfMod) -> bool {
    let has = |c: &str| m.capabilities.iter().any(|x| x == c);
    has("patch") || (has("export") && !has("runtime") && !has("runtime-register-dll"))
        || m.launcher.as_deref().is_some_and(|l| l.eq_ignore_ascii_case("EML"))
}

/// Kiln's last start in this game folder (`kiln/state/boot.json`): the game's pid and when Kiln began (`started`; from
/// an older core without it, `written` less the boot's length).
struct LastBoot {
    pid: Option<u64>,
    start: Option<u64>,
}

fn last_boot(game_dir: &Path) -> Option<LastBoot> {
    let v: serde_json::Value = crate::util::read_json(&game_dir.join(crate::paths::game::BOOT_JSON)).ok()??;
    let time = |k: &str| v.get(k).and_then(|x| x.as_str()).and_then(crate::util::parse_rfc3339).and_then(|t| u64::try_from(t).ok());
    let boot_secs = v.get("bootMs").and_then(|x| x.as_f64()).filter(|m| m.is_finite() && *m >= 0.0).map(|m| (m / 1000.0).ceil() as u64);
    let start = time("started").or_else(|| time("written").map(|w| w.saturating_sub(boot_secs.map_or(60, |b| b + 1))));
    Some(LastBoot { pid: v.get("pid").and_then(|x| x.as_u64()), start })
}

impl LastBoot {
    /// A state file with this `pid` and `updated` time was written in this start. Under Proton the game's pid is the
    /// same at every start, so the time decides; a pid that differs decides alone.
    fn wrote(&self, pid: Option<u64>, updated: Option<u64>) -> bool {
        if matches!((self.pid, pid), (Some(a), Some(b)) if a != b) {
            return false;
        }
        match (self.start, updated) {
            (Some(start), Some(u)) => u >= start,
            (Some(_), None) => false,
            (None, _) => true,
        }
    }
}

/// ShroudForge's record of its startup pass (`assets` of its state file), when there is one.
fn record(game_dir: &Path) -> Option<serde_json::Map<String, serde_json::Value>> {
    let file = state_file(game_dir)?;
    let doc = crate::shroudforge::read_effect_json(game_dir, &file).ok()??;
    doc.get("assets")?.as_object().cloned()
}

/// Who ran the pass a record describes, when it is Kiln's last start's pass.
struct Ran {
    own_loader: bool,
    /// Kiln hosted the player's installed ShroudForge ("Use my installed ShroudForge").
    hosted_installed: bool,
    /// The ShroudForge that ran the pass.
    build: Build,
    /// kiln-shroudforge's word (`kiln/state/shroudforge.json`).
    status: Option<serde_json::Value>,
}

/// Ties a record of ShroudForge's pass, written at `completed`, to Kiln's last start (see the module's text): None when
/// it is an earlier start's. `kept`: an `applied` record, which ShroudForge leaves as it is at a later start whose pass
/// is unchanged. Older than that start, it still describes the game data of the start in which Kiln's adapter hosted
/// ShroudForge and its startup preparation returned (`startupPrepared`).
fn ran(game_dir: &Path, completed: Option<u64>, kept: bool) -> Option<Ran> {
    let boot = last_boot(game_dir);
    let status = crate::shroudforge::status(game_dir).ok().flatten();
    let word = |key: &str| status.as_ref().and_then(|s| s.get(key)).and_then(|v| v.as_str());
    let number = |key: &str| status.as_ref().and_then(|s| s.get(key)).and_then(|v| v.as_u64());
    let updated = number("updated");
    // the adapter's word is about Kiln's last start (no boot.json: nothing to compare it with)
    let current = status.is_some() && boot.as_ref().is_none_or(|b| b.wrote(number("pid"), updated));
    let prepared = kept && current && word("mode") == Some("hosted")
        && status.as_ref().and_then(|s| s.get("startupPrepared")).and_then(|v| v.as_bool()) == Some(true);
    // written before Kiln's last start: a start without ShroudForge came after it
    if let (Some(c), Some(start)) = (completed, boot.as_ref().and_then(|b| b.start)) {
        if c.saturating_add(BOOT_SLACK) < start && !prepared {
            return None;
        }
    }
    let installed = crate::shroudforge::detect(game_dir);
    let own_files = installed.as_ref().is_some_and(|i| i.bootstrap || i.dbghelp);
    let hosted = current && word("mode") == Some("hosted");
    let own_loader = match (current, word("mode")) {
        (true, Some("hosted")) => false,
        (true, Some("self-hosted")) => true,
        // Kiln's adapter has not said anything in this folder: ShroudForge's own loader, while it is installed
        _ if status.is_none() => own_files,
        // The adapter's word is of an earlier start, or it did not host ShroudForge and found no ShroudForge in the
        // process (it looks for ShroudForge's own bootstrap first): the record is a later start's only when it is newer
        // than that word, by ShroudForge's own loader in a start the adapter did not see.
        _ => own_files && matches!((completed, updated), (Some(c), Some(u)) if c > u),
    };
    if !own_loader && !hosted {
        return None;
    }
    let hosted_installed = !own_loader && word("source") == Some("installed");
    let build = if own_loader || hosted_installed {
        read_build(game_dir, &game_dir.join("shroudforge")).unwrap_or_default()
    } else {
        let reported = |k: &str| status.as_ref().and_then(|s| s.pointer(&format!("/bundled/{k}"))).and_then(|v| v.as_str()).map(str::to_owned);
        read_build(game_dir, &game_dir.join("kiln/plugins/shroudforge"))
            .unwrap_or(Build { version: reported("version"), build: reported("build"), host: None })
    };
    Some(Ran { own_loader, hosted_installed, build, status })
}

/// The failed startup pass of ShroudForge's last start in this game folder, or None: no record, a pass that applied or
/// is still `preparing`, or a record of an earlier start (see the module's text: one older than Kiln's last start, or
/// one Kiln's adapter does not place in it). `wine`: the game runs under Wine or Proton. Reads only; starts nothing.
pub fn failure(game_dir: &Path, wine: bool) -> Option<SfStartupFailure> {
    let assets = record(game_dir)?;
    if assets.get("status")?.as_str()? != "failed" {
        return None;
    }
    let completed = assets.get("completedAt").and_then(|v| v.as_u64());
    let Ran { own_loader, hosted_installed, build: ran, status } = ran(game_dir, completed, false)?;
    let bundled_dir = game_dir.join("kiln/plugins/shroudforge");
    let raw_error = assets.get("error").and_then(|v| v.as_str()).unwrap_or("it gave no reason").to_string();
    let error = one_line(&raw_error);
    let server = match assets.get("target").and_then(|v| v.as_str()) {
        Some("enshrouded_server") => true,
        Some("enshrouded") => false,
        _ => game_dir.join(crate::game::SERVER_EXE).is_file() && !game_dir.join(crate::game::CLIENT_EXE).is_file(),
    };
    let mut mods: Vec<String> = crate::shroudforge::list_mods(game_dir).0.into_iter()
        .filter(|m| {
            let (client, srv) = (m.targets.iter().any(|t| t == "client"), m.targets.iter().any(|t| t == "server"));
            m.enabled && (if server { srv } else { client || !srv }) && needs_pass(m)
        })
        .map(|m| m.id)
        .collect();
    mods.sort();
    mods.dedup();
    // The open-file fault: a build without the fix (a valid version, judged with its build) that published under Wine
    // or Proton. Kiln's own runtime is the cure while it is installed and has the fix itself; hosting the installed one
    // ("Use my installed ShroudForge") runs the same code as its own loader.
    let fault = wine && raw_error.contains("(os error 5)") && has_fix(&ran) == Some(false) && (own_loader || hosted_installed);
    let bundled = bundled_dir.join("shroudforge-runtime.dll").is_file() && game_dir.join("kiln/plugins/kiln-shroudforge.dll").is_file()
        && read_build(game_dir, &bundled_dir).is_some_and(|b| has_fix(&b) == Some(true));
    let use_installed = hosted_installed || status.as_ref().and_then(|s| s.get("useInstalled")).and_then(|v| v.as_bool()) == Some(true);
    let cure = (fault && bundled).then(|| {
        let setting = "switch off \"Use my installed ShroudForge\" in the launcher's Settings";
        let why = "Under Wine and Proton a ShroudForge before 1.8.0 fails this way at every start (it replaces a game file it still has open); \
            the ShroudForge that comes with Kiln does not";
        if own_loader {
            format!("{why}. To use that one, take dbghelp and winmm out of WINEDLLOVERRIDES in the game's Steam launch options and keep \
                dinput8=n,b{}: Kiln then runs the ShroudForge mods of the active profile itself",
                if use_installed { format!(", and {setting}") } else { String::new() })
        } else {
            format!("{why}. To use that one, {setting}")
        }
    });
    let raw_retry_error = assets.get("retryError").and_then(|v| v.as_str()).map(str::to_owned);
    Some(SfStartupFailure { error, at: completed.and_then(|c| i64::try_from(c).ok()).map(crate::util::rfc3339), own_loader,
        version: ran.version, build: ran.build, mods, cure, skipped: skipped_mods(&assets), retry_error: raw_retry_error.as_deref().map(one_line),
        raw_error, raw_retry_error })
}

/// The mod ShroudForge's last startup pass in this game folder ran without, after the pass failed in it, or None: no such
/// record, the mod has been switched off (or removed) since, or the record is an earlier start's. An `applied` record
/// stays as it is while the next starts' passes are unchanged, so it counts while Kiln's adapter says it hosted
/// ShroudForge at Kiln's last start and its preparation returned, however old it is ([`ran`]). Reads only.
pub fn skipped(game_dir: &Path) -> Option<SfStartupSkipped> {
    let assets = record(game_dir)?;
    if assets.get("status")?.as_str()? != "applied" {
        return None;
    }
    let list = skipped_mods(&assets);
    let first = list.first()?;
    let completed = assets.get("completedAt").and_then(|v| v.as_u64());
    let ran = ran(game_dir, completed, true)?;
    // a mod switched off since is no longer left out: the next pass runs without it anyway
    let on: std::collections::HashSet<String> = crate::shroudforge::list_mods(game_dir).0.into_iter().filter(|m| m.enabled).map(|m| m.id).collect();
    if !on.contains(&first.id) {
        return None;
    }
    let skipped = list.into_iter().filter(|m| on.contains(&m.id)).collect();
    Some(SfStartupSkipped {
        skipped,
        loaded: assets.get("mods").and_then(|v| v.as_array()).map_or(0, Vec::len),
        at: completed.and_then(|c| i64::try_from(c).ok()).map(crate::util::rfc3339),
        own_loader: ran.own_loader,
        version: ran.build.version,
        build: ran.build.build,
    })
}

/// "ShroudForge 1.7.9's own loader", "ShroudForge, hosted by Kiln,".
fn who(own_loader: bool, version: Option<&str>) -> String {
    match (own_loader, version) {
        (true, Some(v)) => format!("ShroudForge {v}'s own loader"),
        (true, None) => "ShroudForge's own loader".into(),
        (false, Some(v)) => format!("ShroudForge {v}, hosted by Kiln,"),
        (false, None) => "ShroudForge, hosted by Kiln,".into(),
    }
}

/// "the mod that needs it (a)", "the 2 mods that need it (a, b)".
fn needing(mods: &[SfSkippedMod]) -> String {
    let ids: Vec<&str> = mods.iter().map(|m| m.id.as_str()).collect();
    let names = if ids.len() > MAX_NAMES { format!("{} and {} more", ids[..MAX_NAMES].join(", "), ids.len() - MAX_NAMES) } else { ids.join(", ") };
    if ids.len() == 1 { format!("the mod that needs it ({names})") } else { format!("the {} mods that need it ({names})", ids.len()) }
}

impl SfStartupSkipped {
    /// What happened, what loaded and what to do, for a person. No full stop at the end.
    pub fn text(&self) -> String {
        let Some(failed) = self.skipped.first() else { return String::new() };
        let mut t = format!("{} left {} out of its startup pass{}: the pass failed in that mod ({}), so ShroudForge put the game data back \
            and ran the pass again without it", who(self.own_loader, self.version.as_deref()), failed.label(),
            self.at.as_deref().map(|a| format!(" ({a})")).unwrap_or_default(), failed.error);
        if self.skipped.len() > 1 {
            t.push_str(&format!(" and without {}", needing(&self.skipped[1..])));
        }
        t.push_str(&match self.loaded {
            0 => ". No other mod needs that step".to_string(),
            1 => ". The other mod that needs that step loaded".to_string(),
            n => format!(". The other {n} mods that need that step loaded"),
        });
        t.push_str(&format!(". Turn {} off, or check whether it clashes with another mod: one that changes the same game data can make its \
            check fail", failed.called()));
        t
    }

    /// The record in one line for a list of facts: "applied at 2026-11-18T06:24:48Z without wisp_light_timer_a9 1.0.3
    /// (runtime error: ...); 4 other mods loaded (ShroudForge 1.7.9, build kiln.4b1ab16, hosted by Kiln)".
    pub fn line(&self) -> String {
        let failed = self.skipped.first();
        format!("applied{} without {} ({}){}; {} loaded (ShroudForge {}{}, {})", self.at.as_deref().map(|a| format!(" at {a}")).unwrap_or_default(),
            failed.map(|m| m.label()).unwrap_or_default(), failed.map(|m| m.error.as_str()).unwrap_or_default(),
            if self.skipped.len() > 1 { format!(" and {}", needing(&self.skipped[1..])) } else { String::new() },
            match self.loaded { 1 => "1 other mod".to_string(), n => format!("{n} other mods") },
            self.version.as_deref().unwrap_or("of unknown version"), self.build.as_deref().map(|b| format!(", build {b}")).unwrap_or_default(),
            if self.own_loader { "its own loader" } else { "hosted by Kiln" })
    }

    /// The same with the mods' errors passed whole through `redact` before they are put on one line and cut.
    pub fn redacted(&self, redact: impl Fn(&str) -> String) -> SfStartupSkipped {
        SfStartupSkipped { skipped: self.skipped.iter().map(|m| m.redacted(&redact)).collect(), ..self.clone() }
    }
}

impl SfStartupFailure {
    /// "ShroudForge 1.7.9's own loader", "ShroudForge, hosted by Kiln,".
    fn who(&self) -> String {
        who(self.own_loader, self.version.as_deref())
    }

    /// What happened, which mods that leaves out and what to change, for a person. No full stop at the end (the
    /// launcher's notes and the report add their own).
    pub fn text(&self) -> String {
        let mut t = format!("{} could not write its mods' changes into the game's data files at its last start{}: {}", self.who(),
            self.at.as_deref().map(|a| format!(" ({a})")).unwrap_or_default(), self.error);
        // Kiln's ShroudForge tried once more without the mod the pass failed in
        if let Some(second) = &self.retry_error {
            t.push_str(&format!(". It tried again without {}, and that failed too: {second}",
                self.skipped.first().map(|m| m.label()).unwrap_or_else(|| "the mod it failed in".into())));
        }
        t.push_str(". It put the game data back and the game started without them");
        match self.mods.len() {
            0 => {}
            n => {
                let names = if n > MAX_NAMES { format!("{} and {} more", self.mods[..MAX_NAMES].join(", "), n - MAX_NAMES) } else { self.mods.join(", ") };
                t.push_str(&format!(", so the {} that {} that step did not load ({names})", if n == 1 { "mod".to_string() } else { format!("{n} mods") },
                    if n == 1 { "needs" } else { "need" }));
            }
        }
        match &self.cure {
            Some(c) => {
                t.push_str(". ");
                t.push_str(c);
            }
            None if self.own_loader => t.push_str(". ShroudForge's own log (shroudforge/logs/) says more"),
            None => t.push_str(". The [sf] lines of kiln.log say more"),
        }
        t
    }

    /// The record in one line for a list of facts: "failed at 2026-11-10T11:00:14Z: pregame phase failed: ... (ShroudForge
    /// 1.7.9, build kiln.63b8862, hosted by Kiln)".
    pub fn line(&self) -> String {
        let again = match (&self.retry_error, self.skipped.first()) {
            (Some(second), Some(m)) => format!("; again without {}: {second}", m.id),
            (Some(second), None) => format!("; again: {second}"),
            _ => String::new(),
        };
        format!("failed{}: {}{again} (ShroudForge {}{}, {})", self.at.as_deref().map(|a| format!(" at {a}")).unwrap_or_default(), self.error,
            self.version.as_deref().unwrap_or("of unknown version"), self.build.as_deref().map(|b| format!(", build {b}")).unwrap_or_default(),
            if self.own_loader { "its own loader" } else { "hosted by Kiln" })
    }

    /// The same failure with ShroudForge's words passed whole through `redact` (a report's redaction) before they are
    /// put on one line and cut: collapsed spaces in a home folder's name, or a cut inside a user name, would no longer
    /// match it.
    pub fn redacted(&self, redact: impl Fn(&str) -> String) -> SfStartupFailure {
        let raw_error = redact(&self.raw_error);
        let raw_retry_error = self.raw_retry_error.as_deref().map(&redact);
        SfStartupFailure { error: one_line(&raw_error), raw_error, retry_error: raw_retry_error.as_deref().map(one_line), raw_retry_error,
            skipped: self.skipped.iter().map(|m| m.redacted(&redact)).collect(), ..self.clone() }
    }
}

/// ShroudForge's failed startup pass on a line of the launcher's engine list (`engine_status` `startupFailed`).
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SfStartupNote {
    #[serde(flatten)]
    pub failure: SfStartupFailure,
    /// The sentence for that line, without its full stop: the whole of [`SfStartupFailure::text`] on ShroudForge's line,
    /// a pointer to it on EML's.
    pub text: String,
}

/// ShroudForge's startup pass ran without a mod at the last start, on ShroudForge's line of the launcher's engine list
/// (`engine_status` `startupSkipped`): a warning.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SfSkipNote {
    #[serde(flatten)]
    pub skipped: SfStartupSkipped,
    /// [`SfStartupSkipped::text`], without its full stop.
    pub text: String,
}

/// The engine list's word on ShroudForge's last startup pass: read once for all its lines.
pub struct EngineNotes {
    failure: Option<SfStartupFailure>,
    skipped: Option<SfStartupSkipped>,
    /// ShroudForge runs the EML mods here (its own loader carries EML, or Kiln hosts it for the profile): they go through
    /// the same pass.
    runs_eml: bool,
}

impl EngineNotes {
    /// `loaders`: [`crate::own_loaders::find`] for the active profile. A launcher built for Linux manages games under
    /// Wine or Proton.
    pub fn read(game_dir: &Path, loaders: &[OwnLoader]) -> EngineNotes {
        EngineNotes { failure: failure(game_dir, cfg!(not(windows))), skipped: skipped(game_dir), runs_eml: crate::own_loaders::shroudforge_runs_eml(loaders) }
    }

    /// The note for ShroudForge's line when its last pass ran without a mod (none on the other lines).
    pub fn skip_of(&self, kind: ModKind) -> Option<SfSkipNote> {
        let s = self.skipped.as_ref().filter(|_| kind == ModKind::ShroudForge)?;
        Some(SfSkipNote { skipped: s.clone(), text: s.text() })
    }

    /// The note for one engine's line: ShroudForge's, whoever ran the pass (whatever own loaders there are), and EML's
    /// while ShroudForge runs the EML mods.
    pub fn of(&self, kind: ModKind) -> Option<SfStartupNote> {
        let f = self.failure.as_ref()?;
        let text = match kind {
            ModKind::ShroudForge => f.text(),
            ModKind::Eml if self.runs_eml => "ShroudForge's startup pass failed at its last start, so the changes of the EML mods it runs are not in \
                the game data either (ShroudForge's line says why)".to_string(),
            _ => return None,
        };
        Some(SfStartupNote { failure: f.clone(), text })
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// The made-up time of the fixtures: 2026-11-10T11:00:00Z.
    const T0: u64 = 1_794_308_400;

    struct Game(PathBuf);

    impl Game {
        fn new(name: &str) -> Game {
            let d = std::env::temp_dir().join(format!("kiln-sf-startup-{name}-{}", std::process::id()));
            let _ = std::fs::remove_dir_all(&d);
            std::fs::create_dir_all(&d).unwrap();
            let g = Game(d);
            g.w("enshrouded.exe", "MZ");
            g
        }

        fn w(&self, rel: &str, text: &str) {
            let p = self.0.join(rel);
            std::fs::create_dir_all(p.parent().unwrap()).unwrap();
            std::fs::write(p, text).unwrap();
        }

        /// ShroudForge installed with its own loader next to the game.
        fn own_loader(&self, version: &str) {
            std::fs::write(self.0.join("dbghelp.dll"), crate::pe::build_test_dll(&[], b"EML_LOG_FILE_PATH shroudforge")).unwrap();
            self.w("shroudforge/shroudforge-runtime.dll", "x");
            self.w("shroudforge/version.json", &format!(r#"{{"version":"{version}"}}"#));
        }

        /// Kiln's own ShroudForge (the component and its adapter), a build of the fork with the fix.
        fn bundled(&self) {
            self.w("kiln/plugins/kiln-shroudforge.dll", "x");
            self.w("kiln/plugins/shroudforge/shroudforge-runtime.dll", "x");
            self.w("kiln/plugins/shroudforge/version.json", r#"{"version":"1.7.9","build":"kiln.63b8862","host":"kiln"}"#);
        }

        fn status(&self, json: &str) {
            self.w("kiln/state/shroudforge.json", json);
        }

        /// Kiln's record of a start (`kiln/state/boot.json`): when it began, the game's pid, the profile mode.
        fn boot(&self, started: u64, pid: u32, mode: &str) {
            let at = |t: u64| crate::util::rfc3339(t as i64);
            self.w("kiln/state/boot.json", &serde_json::json!({
                "format": 1, "kiln": "0.2.0-dev", "pid": pid, "started": at(started), "written": at(started + 2), "side": "client", "hooks": true,
                "profile": {"mode": mode, "id": "my-mods", "generation": 3}, "chain": [], "plugins": [], "errors": [],
            }).to_string());
        }

        fn record(&self, status: &str, at: u64, error: Option<&str>) {
            let mut assets = serde_json::json!({"schemaVersion": 1, "status": status, "phase": "startup", "target": "enshrouded", "completedAt": at});
            if let Some(e) = error {
                assets["error"] = e.into();
            }
            self.w("shroudforge/state.json", &serde_json::json!({"schemaVersion": 1, "assets": assets, "runtime": {"running": false}}).to_string());
        }

        fn package(&self, id: &str, capabilities: &[&str], enabled: bool, targets: &[&str]) {
            self.w(&format!("mods/{id}/mod.json"), &serde_json::json!({"id": id, "name": id, "version": "1.0.0", "capabilities": capabilities}).to_string());
            self.w(&format!("mods/{id}/extended.mod.json"), &serde_json::json!({"schemaVersion": 1, "enabled": enabled, "targets": targets, "settings": {}}).to_string());
            self.w(&format!("mods/{id}/src/mod.lua"), "return {}");
        }
    }

    impl Drop for Game {
        fn drop(&mut self) {
            let _ = std::fs::remove_dir_all(&self.0);
        }
    }

    const DENIED: &str = "pregame phase failed: Access denied. (os error 5)";

    #[test]
    fn only_a_failed_pass_of_the_last_start_is_a_failure() {
        let g = Game::new("which");
        assert_eq!(failure(&g.0, true), None, "no ShroudForge, no record");
        g.own_loader("1.7.9");
        assert_eq!(failure(&g.0, true), None, "no record yet");
        g.record("applied", T0 + 14, None);
        assert_eq!(failure(&g.0, true), None);
        g.record("preparing", T0 + 14, None);
        assert_eq!(failure(&g.0, true), None, "a pass that is running, or was cut off: not a recorded failure");
        g.record("failed", T0 + 14, Some(DENIED));
        let f = failure(&g.0, true).expect("ShroudForge's own loader, Kiln's adapter silent");
        assert!(f.own_loader && f.at.as_deref() == Some("2026-11-10T11:00:14Z") && f.error == "pregame phase failed: Access denied. (os error 5)", "{f:?}");
        // Kiln's adapter saw ShroudForge's own bootstrap at that start
        g.status(&format!(r#"{{"mode":"self-hosted","source":"installed","updated":{T0},"useInstalled":false}}"#));
        assert!(failure(&g.0, true).is_some_and(|f| f.own_loader));
        // Kiln's adapter was off at Kiln's last start, after the record: the record is an earlier start's
        g.status(&format!(r#"{{"mode":"off","reason":"the active profile lists no ShroudForge mods","updated":{}}}"#, T0 + 3600));
        assert_eq!(failure(&g.0, true), None);
        // ... and before the record: ShroudForge's own loader ran later, in a start without Kiln
        g.status(&format!(r#"{{"mode":"off","updated":{}}}"#, T0 - 3600));
        assert!(failure(&g.0, true).is_some_and(|f| f.own_loader));
        // a record without ShroudForge's loader or Kiln's word (ShroudForge was removed): nothing runs it any more
        let h = Game::new("gone");
        h.record("failed", T0, Some(DENIED));
        assert_eq!(failure(&h.0, true), None);
        // a state file that is no JSON, or has no such section, is no failure
        g.w("shroudforge/state.json", "{ not json");
        assert_eq!(failure(&g.0, true), None);
        g.w("shroudforge/state.json", r#"{"schemaVersion":1,"assets":"failed"}"#);
        assert_eq!(failure(&g.0, true), None);
    }

    #[test]
    fn names_the_mods_that_need_the_pass_on_this_side() {
        let g = Game::new("mods");
        g.own_loader("1.7.9");
        g.record("failed", T0, Some(DENIED));
        g.package("bone-caller", &["patch", "runtime"], true, &["client"]);
        g.package("sky-lamp", &["runtime"], true, &["client"]);
        g.package("ore-sheet", &["export"], true, &["client", "server"]);
        g.package("live-sheet", &["export", "runtime"], true, &["client"]);
        g.package("off-patch", &["patch"], false, &["client"]);
        g.package("server-patch", &["patch"], true, &["server"]);
        let f = failure(&g.0, false).unwrap();
        assert_eq!(f.mods, ["bone-caller", "ore-sheet"], "switched on, this side, with a startup part");
        let t = f.text();
        assert!(t.starts_with("ShroudForge 1.7.9's own loader could not write its mods' changes into the game's data files at its last start \
            (2026-11-10T11:00:00Z): pregame phase failed: Access denied. (os error 5). It put the game data back and the game started without them, \
            so the 2 mods that need that step did not load (bone-caller, ore-sheet)"), "{t}");
        assert!(t.ends_with("ShroudForge's own log (shroudforge/logs/) says more") && !t.contains("WINEDLLOVERRIDES"), "not under Wine: no cure to name: {t}");
        // a dedicated server's record names the server's packages
        g.w("shroudforge/state.json", &serde_json::json!({"assets": {"status": "failed", "target": "enshrouded_server", "error": DENIED}}).to_string());
        let f = failure(&g.0, false).unwrap();
        assert_eq!((f.mods.clone(), f.at.clone()), (vec!["ore-sheet".to_string(), "server-patch".to_string()], None));
        assert!(f.text().contains("at its last start: pregame"), "{}", f.text());
    }

    #[test]
    fn the_wine_fault_of_shroudforge_before_1_8_has_a_cure() {
        let g = Game::new("cure");
        g.own_loader("1.7.9");
        g.bundled();
        g.package("bone-caller", &["patch", "runtime"], true, &["client"]);
        g.record("failed", T0 + 14, Some("pregame phase failed: Accès refusé. (os error 5)"));
        g.status(&format!(r#"{{"mode":"self-hosted","source":"installed","updated":{T0},"useInstalled":false,"bundled":{{"version":"1.7.9","build":"kiln.0000000"}}}}"#));
        let f = failure(&g.0, true).unwrap();
        let cure = f.cure.clone().expect("ShroudForge 1.7.9's own loader under Wine, os error 5, Kiln's own ShroudForge installed");
        assert!(cure.contains("take dbghelp and winmm out of WINEDLLOVERRIDES") && cure.contains("keep dinput8=n,b") && !cure.contains("Use my installed"), "{cure}");
        assert!(f.text().ends_with("Kiln then runs the ShroudForge mods of the active profile itself"), "{}", f.text());
        assert!(f.text().contains("(os error 5). It put"), "ShroudForge's words in the system's language, one full stop: {}", f.text());
        // the setting that makes Kiln host the same installed runtime has to go too
        g.status(&format!(r#"{{"mode":"self-hosted","source":"installed","updated":{T0},"useInstalled":true}}"#));
        assert!(failure(&g.0, true).unwrap().cure.unwrap().contains("and switch off \"Use my installed ShroudForge\""));
        g.status(&format!(r#"{{"mode":"self-hosted","source":"installed","updated":{T0},"useInstalled":false}}"#));
        // no cure where it would not be one
        assert_eq!(failure(&g.0, false).unwrap().cure, None, "Windows");
        g.record("failed", T0 + 14, Some("pregame phase failed: failed to restore the clean enshrouded.kfc baseline"));
        assert_eq!(failure(&g.0, true).unwrap().cure, None, "another failure");
        g.record("failed", T0 + 14, Some(DENIED));
        g.w("shroudforge/version.json", r#"{"version":"1.8.0"}"#);
        assert_eq!(failure(&g.0, true).unwrap().cure, None, "a ShroudForge with the fix: the denial has another cause");
        g.w("shroudforge/version.json", r#"{"version":"1.7.9"}"#);
        std::fs::remove_file(g.0.join("kiln/plugins/shroudforge/shroudforge-runtime.dll")).unwrap();
        assert_eq!(failure(&g.0, true).unwrap().cure, None, "without Kiln's own ShroudForge there is nothing to switch to");
    }

    #[test]
    fn a_pass_kiln_hosted_says_who_ran_it() {
        let g = Game::new("hosted");
        g.bundled();
        g.record("failed", T0, Some("pregame phase failed: could not create a new enshrouded.kfc working baseline"));
        g.status(&format!(r#"{{"mode":"hosted","source":"bundled","updated":{},"useInstalled":false,"bundled":{{"version":"1.7.9","build":"kiln.0000000"}},"startupPrepared":true}}"#, T0 + 7200));
        let f = failure(&g.0, true).expect("a hosted start rewrites the record, however long the game then ran");
        assert!(!f.own_loader && f.cure.is_none(), "{f:?}");
        assert!(f.text().starts_with("ShroudForge 1.7.9, hosted by Kiln, could not write") && f.text().ends_with("The [sf] lines of kiln.log say more"), "{}", f.text());
        // Kiln hosting the player's installed ShroudForge 1.7.9 runs the same code: the cure is the setting
        g.own_loader("1.7.9");
        std::fs::remove_file(g.0.join("dbghelp.dll")).unwrap();
        g.record("failed", T0, Some(DENIED));
        g.status(&format!(r#"{{"mode":"hosted","source":"installed","updated":{T0},"useInstalled":true}}"#));
        let f = failure(&g.0, true).unwrap();
        let cure = f.cure.clone().unwrap();
        assert!(cure.ends_with("To use that one, switch off \"Use my installed ShroudForge\" in the launcher's Settings") && !cure.contains("WINEDLLOVERRIDES"), "{cure}");
    }

    /// Review of 6e231b37, finding 2. A hosted pass failed; the next start was "Play once: without mods", which skips
    /// every Kiln plugin (early_plugins.cpp), so the adapter's status and ShroudForge's record both stay as that earlier
    /// start left them. Nothing is said about a start in which ShroudForge did not run. Under Proton the game's pid is
    /// the same at every start, so the times decide.
    #[test]
    fn a_failure_of_an_earlier_start_is_not_the_last_starts() {
        let g = Game::new("old-boot");
        g.bundled();
        g.package("bone-caller", &["patch"], true, &["client"]);
        // the start in which the hosted pass failed
        g.boot(T0, 412, "active");
        g.status(&format!(r#"{{"mode":"hosted","source":"bundled","pid":412,"updated":{},"bundled":{{"version":"1.7.9","build":"kiln.63b8862"}},"startupPrepared":true}}"#, T0 + 900));
        g.record("failed", T0 + 3, Some("pregame phase failed: could not create a new enshrouded.kfc working baseline"));
        assert!(failure(&g.0, true).is_some_and(|f| !f.own_loader), "that start's own failure");
        // the next start, without mods: another pid (Windows), then the same one (Proton)
        for pid in [977, 412] {
            g.boot(T0 + 3600, pid, "no-mods");
            assert_eq!(failure(&g.0, true), None, "pid {pid}: ShroudForge did not run at the last start");
        }
        // ShroudForge's own loader does not ask Kiln: it ran at that start too, and failed again
        g.own_loader("1.7.9");
        g.record("failed", T0 + 3612, Some(DENIED));
        assert!(failure(&g.0, true).is_some_and(|f| f.own_loader), "a record written in the last start, by the loader the adapter did not see");
        // a self-hosted start, then one where the override is gone and Kiln's adapter says it is off: the old record is old
        g.boot(T0 + 7200, 412, "active");
        g.status(&format!(r#"{{"mode":"off","pid":412,"reason":"the active profile lists no ShroudForge mods","updated":{}}}"#, T0 + 7201));
        assert_eq!(failure(&g.0, true), None);
    }

    /// Review of 6e231b37, finding 3. The cure ("use the ShroudForge that comes with Kiln") is for upstream's builds
    /// before 1.8.0. Kiln's own build calls itself 1.7.9 too (`build: kiln.<commit>`, `host: kiln`) and has the fix: a
    /// denial there has another cause, and switching runtimes would not help. A version that is no version gets none.
    #[test]
    fn the_cure_is_not_offered_to_a_build_that_has_the_fix_or_to_a_version_that_is_none() {
        let fork = r#"{"version":"1.7.9","build":"kiln.63b8862","host":"kiln"}"#;
        // Kiln hosting the copy in the installed location, which is Kiln's own build
        let g = Game::new("fork-hosted");
        g.bundled();
        g.own_loader("1.7.9");
        std::fs::remove_file(g.0.join("dbghelp.dll")).unwrap();
        g.record("failed", T0, Some(DENIED));
        g.status(&format!(r#"{{"mode":"hosted","source":"installed","updated":{T0},"useInstalled":true}}"#));
        assert!(failure(&g.0, true).unwrap().cure.is_some(), "upstream 1.7.9 hosted from the installed location: the setting is the cure");
        g.w("shroudforge/version.json", fork);
        let f = failure(&g.0, true).unwrap();
        assert_eq!(f.cure, None, "Kiln's build in the installed location: {f:?}");
        // the same build started by its own loader
        let g = Game::new("fork-own");
        g.bundled();
        g.own_loader("1.7.9");
        g.record("failed", T0, Some(DENIED));
        assert!(failure(&g.0, true).unwrap().cure.is_some(), "upstream 1.7.9's own loader");
        g.w("shroudforge/version.json", fork);
        assert_eq!(failure(&g.0, true).unwrap().cure, None);
        // versions that are none: no cure rests on them
        for bad in ["1.7.garbage", "1.7", "1.7.9.1", "one.7.9", "", "1.-7.9", "1.7.9garbage"] {
            g.w("shroudforge/version.json", &serde_json::json!({"version": bad}).to_string());
            assert_eq!(failure(&g.0, true).unwrap().cure, None, "version {bad:?}");
        }
        for good in ["1.7.9", "v1.7.8", "1.6.0-beta.2", "1.7.9+build.42"] {
            g.w("shroudforge/version.json", &serde_json::json!({"version": good, "build": "42095151546"}).to_string());
            assert!(failure(&g.0, true).unwrap().cure.is_some(), "version {good:?}");
        }
        for fixed in ["1.8.0", "1.10.2", "2.0.0"] {
            g.w("shroudforge/version.json", &serde_json::json!({"version": fixed}).to_string());
            assert_eq!(failure(&g.0, true).unwrap().cure, None, "version {fixed:?}");
        }
        // Kiln's own ShroudForge must be one that has the fix, or it is no cure: the fork's first pins (Kiln 0.2.0-dev.18
        // and dev.19) publish as upstream's 1.7.9 does
        g.w("shroudforge/version.json", r#"{"version":"1.7.9","build":"42095151546"}"#);
        g.w("kiln/plugins/shroudforge/version.json", r#"{"version":"1.7.9","build":"kiln.3586804","host":"kiln"}"#);
        assert_eq!(failure(&g.0, true).unwrap().cure, None, "a bundle from before the fix");
        g.w("kiln/plugins/shroudforge/version.json", r#"{"version":"1.7"}"#);
        assert_eq!(failure(&g.0, true).unwrap().cure, None, "a bundle whose version is none");
        std::fs::remove_file(g.0.join("kiln/plugins/shroudforge/version.json")).unwrap();
        assert_eq!(failure(&g.0, true).unwrap().cure, None, "a bundle that does not say what it is");
        g.w("kiln/plugins/shroudforge/version.json", r#"{"version":"1.7.9","build":"kiln.63b8862","host":"kiln"}"#);
        let f = failure(&g.0, true).unwrap();
        assert!(f.cure.is_some(), "{f:?}");
        assert_eq!((f.version.as_deref(), f.build.as_deref()), (Some("1.7.9"), Some("42095151546")), "the build that ran is kept");
        assert!(f.line().ends_with("(ShroudForge 1.7.9, build 42095151546, its own loader)"), "{}", f.line());
    }

    /// The record Kiln's ShroudForge writes after a pass that failed in one mod (`wisp`, which `lamp-pack` needs) and ran
    /// again without them: `applied`, `skipped`, and `mods` without the two.
    fn retried(g: &Game, at: u64) {
        g.w("shroudforge/state.json", &serde_json::json!({"schemaVersion": 1, "assets": {
            "schemaVersion": 1, "status": "applied", "phase": "startup", "target": "enshrouded", "completedAt": at, "fingerprint": "f",
            "mods": [{"id": "bone-caller", "version": "1.0.0"}, {"id": "ore-sheet", "version": "1.0.0"}],
            "skipped": [
                {"id": "wisp", "name": "Wisp Light Timer", "version": "1.0.3",
                    "error": "runtime error: [string \"wisp\"]:719: Missing base localization: keenglish"},
                {"id": "lamp-pack", "name": "Lamp Pack", "version": "2.0.0", "error": "needs wisp, which failed in the pregame pass"}]},
            "runtime": {"running": false}}).to_string());
    }

    /// Kiln's ShroudForge hosted at the start of `T0` (pid 412) and its preparation returned.
    fn hosted_start(g: &Game) {
        g.boot(T0, 412, "active");
        g.status(&format!(r#"{{"mode":"hosted","source":"bundled","pid":412,"updated":{},"bundled":{{"version":"1.7.9","build":"kiln.4b1ab16"}},"startupPrepared":true}}"#, T0 + 30));
    }

    /// dev.29.18 (inbox 79): one mod's own check failed, Kiln's ShroudForge ran the pass again without it, and the
    /// other mods loaded. That is a warning naming the mod, its error and what to do, not a failure.
    #[test]
    fn a_pass_that_ran_without_a_mod_is_a_warning_that_names_it() {
        let g = Game::new("skipped");
        g.bundled();
        for id in ["bone-caller", "ore-sheet", "wisp", "lamp-pack"] {
            g.package(id, &["patch"], true, &["client"]);
        }
        hosted_start(&g);
        retried(&g, T0 + 5);
        assert_eq!(failure(&g.0, true), None, "applied: no failure");
        let s = skipped(&g.0).expect("the last start's pass ran without wisp");
        assert_eq!(s.skipped.iter().map(|m| m.id.as_str()).collect::<Vec<_>>(), ["wisp", "lamp-pack"]);
        assert_eq!((s.loaded, s.own_loader, s.version.as_deref(), s.at.as_deref()), (2, false, Some("1.7.9"), Some("2026-11-10T11:00:05Z")));
        assert_eq!(s.text(), "ShroudForge 1.7.9, hosted by Kiln, left Wisp Light Timer (wisp 1.0.3) out of its startup pass (2026-11-10T11:00:05Z): \
            the pass failed in that mod (runtime error: [string \"wisp\"]:719: Missing base localization: keenglish), so ShroudForge put the game \
            data back and ran the pass again without it and without the mod that needs it (lamp-pack). The other 2 mods that need that step \
            loaded. Turn Wisp Light Timer off, or check whether it clashes with another mod: one that changes the same game data can make its \
            check fail");
        assert_eq!(s.line(), "applied at 2026-11-10T11:00:05Z without Wisp Light Timer (wisp 1.0.3) (runtime error: [string \"wisp\"]:719: \
            Missing base localization: keenglish) and the mod that needs it (lamp-pack); 2 other mods loaded (ShroudForge 1.7.9, build \
            kiln.63b8862, hosted by Kiln)", "the build of the bundle's version.json");
        // the launcher's rows: the warning on ShroudForge's, nothing on EML's
        let notes = EngineNotes { failure: None, skipped: Some(s.clone()), runs_eml: true };
        let note = notes.skip_of(ModKind::ShroudForge).unwrap();
        assert_eq!(note.text, s.text());
        let json = serde_json::to_value(&note).unwrap();
        assert_eq!((json["skipped"][0]["id"].as_str(), json["loaded"].as_u64(), json["text"].is_string()), (Some("wisp"), Some(2), true), "{json}");
        assert_eq!(notes.skip_of(ModKind::Eml), None);
        assert_eq!(notes.of(ModKind::ShroudForge), None);
        // a mod left out with it that is off now is not named; the failed one off: nothing to say any more
        g.package("lamp-pack", &["patch"], false, &["client"]);
        assert_eq!(skipped(&g.0).unwrap().skipped.len(), 1);
        assert!(!skipped(&g.0).unwrap().text().contains("lamp-pack"));
        g.package("wisp", &["patch"], false, &["client"]);
        assert_eq!(skipped(&g.0), None, "switched off since: the next pass runs without it anyway");
    }

    /// ShroudForge keeps an applied record as it is while the next starts' passes are unchanged: the mod stays out of the
    /// game data, so the warning stays while Kiln hosted ShroudForge at its last start, however old the record is.
    #[test]
    fn a_kept_record_counts_while_kiln_hosted_shroudforge_at_its_last_start() {
        let g = Game::new("kept");
        g.bundled();
        g.package("wisp", &["patch"], true, &["client"]);
        retried(&g, T0 - 86_400);
        hosted_start(&g);
        assert!(skipped(&g.0).is_some(), "an unchanged pass a day later");
        // ... but not when the adapter's preparation did not return, or the last start did not host ShroudForge
        g.status(&format!(r#"{{"mode":"hosted","source":"bundled","pid":412,"updated":{},"bundled":{{"version":"1.7.9"}}}}"#, T0 + 30));
        assert_eq!(skipped(&g.0), None);
        hosted_start(&g);
        g.boot(T0 + 3600, 412, "no-mods");
        assert_eq!(skipped(&g.0), None, "a start without mods came after it");
        // a fresh record needs no word on the preparation (as a failed one)
        retried(&g, T0 + 3605);
        g.status(&format!(r#"{{"mode":"hosted","source":"bundled","pid":412,"updated":{}}}"#, T0 + 3630));
        assert!(skipped(&g.0).is_some());
    }

    /// Records of ShroudForge without the retry (upstream's, and Kiln's before dev.29.18) read as before; the new fields
    /// are additive, and serialize only when present.
    #[test]
    fn old_records_read_as_before_and_the_new_fields_only_add() {
        let g = Game::new("old-record");
        g.bundled();
        g.package("wisp", &["patch"], true, &["client"]);
        hosted_start(&g);
        g.record("applied", T0 + 5, None);
        assert_eq!(skipped(&g.0), None);
        g.record("failed", T0 + 5, Some("pregame phase failed: runtime error: [string \"wisp\"]:719: Missing base localization: keenglish"));
        let f = failure(&g.0, true).unwrap();
        assert!(f.skipped.is_empty() && f.retry_error.is_none(), "{f:?}");
        assert!(!f.text().contains("tried again"), "{}", f.text());
        let json = serde_json::to_value(&f).unwrap();
        assert!(json.get("skipped").is_none() && json.get("retryError").is_none(), "{json}");
        // what a launcher of before dev.29.18 reads from a note of this one (it knows neither field)
        #[derive(serde::Deserialize)]
        #[serde(rename_all = "camelCase")]
        #[allow(dead_code)]
        struct OldNote { error: String, own_loader: bool, mods: Vec<String>, text: String }
        g.w("shroudforge/state.json", &serde_json::json!({"schemaVersion": 1, "assets": {"status": "failed", "phase": "startup",
            "target": "enshrouded", "completedAt": T0 + 5, "error": "pregame phase failed: first",
            "skipped": [{"id": "wisp", "error": "first"}], "retryError": "pregame phase failed: second"}}).to_string());
        let note = EngineNotes { failure: failure(&g.0, true), skipped: None, runs_eml: false }.of(ModKind::ShroudForge).unwrap();
        let old: OldNote = serde_json::from_value(serde_json::to_value(&note).unwrap()).unwrap();
        assert_eq!(old.error, "pregame phase failed: first");
    }

    /// The second pass failed too: no mod of the pass loaded, as before, and the text gives both errors.
    #[test]
    fn a_retry_that_failed_too_gives_both_errors() {
        let g = Game::new("retry-failed");
        g.bundled();
        g.package("wisp", &["patch"], true, &["client"]);
        g.package("bone-caller", &["patch"], true, &["client"]);
        hosted_start(&g);
        g.w("shroudforge/state.json", &serde_json::json!({"schemaVersion": 1, "assets": {"schemaVersion": 1, "status": "failed",
            "phase": "startup", "target": "enshrouded", "completedAt": T0 + 5,
            "error": "pregame phase failed: runtime error: [string \"wisp\"]:719: Missing base localization: keenglish\nstack traceback:\n\t[C]: in ?",
            "skipped": [{"id": "wisp", "name": "Wisp Light Timer", "version": "1.0.3", "error": "runtime error: [string \"wisp\"]:719: Missing base localization: keenglish"}],
            "retryError": "pregame phase failed: runtime error: [string \"bone-caller\"]:3: bones\nstack traceback:\n\t[C]: in ?"}}).to_string());
        assert_eq!(skipped(&g.0), None, "failed: no warning");
        let f = failure(&g.0, true).unwrap();
        assert_eq!(f.retry_error.as_deref(), Some("pregame phase failed: runtime error: [string \"bone-caller\"]:3: bones stack traceback: [C]: in ?"));
        assert!(f.text().starts_with("ShroudForge 1.7.9, hosted by Kiln, could not write its mods' changes into the game's data files at its last \
            start (2026-11-10T11:00:05Z): pregame phase failed: runtime error: [string \"wisp\"]:719: Missing base localization: keenglish stack \
            traceback: [C]: in ?. It tried again without Wisp Light Timer (wisp 1.0.3), and that failed too: pregame phase failed: runtime error: \
            [string \"bone-caller\"]:3: bones stack traceback: [C]: in ?. It put the game data back and the game started without them, so the 2 \
            mods that need that step did not load (bone-caller, wisp)"), "{}", f.text());
        assert!(f.line().contains("; again without wisp: pregame phase failed: runtime error: [string \"bone-caller\"]:3: bones"), "{}", f.line());
        let json = serde_json::to_value(&f).unwrap();
        assert_eq!((json["skipped"][0]["id"].as_str(), json["retryError"].is_string()), (Some("wisp"), true), "{json}");
        // a report redacts every word ShroudForge quotes
        g.w("shroudforge/state.json", &serde_json::json!({"schemaVersion": 1, "assets": {"status": "failed", "phase": "startup",
            "target": "enshrouded", "completedAt": T0 + 5, "error": "pregame phase failed: /home/frostling/a",
            "skipped": [{"id": "wisp", "error": "/home/frostling/b"}], "retryError": "pregame phase failed: /home/frostling/c"}}).to_string());
        let r = failure(&g.0, true).unwrap().redacted(|t| t.replace("/home/frostling", "<home>"));
        assert!(!r.text().contains("frostling") && r.text().contains("<home>/c"), "{}", r.text());
        assert_eq!(r.skipped[0].error, "<home>/b");
        retried(&g, T0 + 5);
        g.w("shroudforge/state.json", &std::fs::read_to_string(g.0.join("shroudforge/state.json")).unwrap().replace("keenglish", "/home/frostling/x"));
        let r = skipped(&g.0).unwrap().redacted(|t| t.replace("/home/frostling", "<home>"));
        assert!(r.text().contains("<home>/x") && !r.text().contains("frostling"), "{}", r.text());
    }

    #[test]
    fn shroudforges_words_are_one_bounded_line() {
        assert_eq!(one_line("a\r\n  b\tc\u{7} d."), "a b c d");
        let long = "x".repeat(MAX_ERROR + 50);
        assert_eq!(one_line(&long).chars().count(), MAX_ERROR + 3);
        let b = |version: &str, build: Option<&str>, host: Option<&str>| has_fix(&Build { version: Some(version.into()), build: build.map(Into::into), host: host.map(Into::into) });
        assert_eq!(b("1.7.9", None, None), Some(false));
        assert_eq!(b("v1.7.9-build.42", Some("42095151546"), None), Some(false));
        assert_eq!(b("0.9.0", None, None), Some(false));
        assert_eq!(b("1.8.0", None, None), Some(true));
        assert_eq!(b("2.0.1", None, None), Some(true));
        assert_eq!(b("1.7.9", Some("kiln.63b8862"), Some("kiln")), Some(true), "Kiln's fork");
        assert_eq!(b("1.7.9", Some("kiln.63b8862"), None), Some(true), "Kiln's fork, by its build");
        assert_eq!(b("1.7.7", Some("kiln.3586804"), Some("kiln")), Some(false), "a pin from before the fix");
        for none in ["", "next", "0.9", "1.7.garbage"] {
            assert_eq!(b(none, Some("kiln.63b8862"), Some("kiln")), None, "{none:?}");
        }
        assert_eq!(has_fix(&Build::default()), None, "no version.json");
    }

    /// Review of 6e231b37, finding 1: ShroudForge's words reach a redactor whole, and are put on one line and cut after it.
    #[test]
    fn the_words_are_redacted_whole_before_they_are_put_on_one_line_and_cut() {
        let g = Game::new("redact");
        g.own_loader("1.7.9");
        g.record("failed", T0, Some("pregame phase failed:\n  C:\\Users\\Frost  Ling\\x: Access denied. (os error 5)"));
        let f = failure(&g.0, false).unwrap();
        assert_eq!(f.error, "pregame phase failed: C:\\Users\\Frost Ling\\x: Access denied. (os error 5)", "on one line for the launcher");
        let r = f.redacted(|t| t.replace("Frost  Ling", "<user>"));
        assert_eq!(r.error, "pregame phase failed: C:\\Users\\<user>\\x: Access denied. (os error 5)");
        assert!(r.text().contains("C:\\Users\\<user>\\x") && !r.text().contains("Frost"), "{}", r.text());
        let long = format!("{} /home/frostling/x", "y".repeat(MAX_ERROR - 11));
        g.record("failed", T0, Some(&long));
        let f = failure(&g.0, false).unwrap();
        assert!(f.error.ends_with("/home/fros..."), "the launcher's own line is cut where it is: {}", f.error);
        let r = f.redacted(|t| t.replace("/home/frostling", "<home>"));
        assert!(r.error.ends_with(" <home>/x") && !r.error.contains("fros"), "{}", r.error);
    }
}
