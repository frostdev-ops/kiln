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
//! The record names no process. A `failed` one is the last start's whenever ShroudForge ran at that start: it does not
//! take a failed pass as done (`calculate_preparation_fingerprint.rs` `matches` wants `applied`), so every start in
//! which it runs writes `preparing` and then the new outcome over it.
//!
//! One failure has a cure worth naming. ShroudForge up to 1.7.9 replaces `<exe>.kfc_resources` while its own reader
//! still has that file open (`game_file_transaction.rs` `replace`, kfc's `KFCCursor`). Windows lets that pass; Wine and
//! Proton answer "Access denied (os error 5)", at every start, for every startup mod. The ShroudForge that comes with
//! Kiln (the fork, since `03b4950`) closes the reader first, as upstream's 1.8.0 branch does. A player whose Steam
//! launch options load ShroudForge's own `dbghelp.dll` under Proton therefore gets no startup mod until Kiln hosts
//! ShroudForge (player report 327, 13 Nov 2026: `docs/kiln/shroudforge-compat.md` "ShroudForge's own loader under
//! Wine and Proton").

use serde::{Deserialize, Serialize};
use std::path::{Path, PathBuf};

/// ShroudForge's words are quoted up to this many characters.
const MAX_ERROR: usize = 300;
/// Mod ids named in a sentence.
const MAX_NAMES: usize = 5;

/// ShroudForge's last startup pass failed, and it is the last start's.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SfStartupFailure {
    /// ShroudForge's own words (the record's `error`), on one line.
    pub error: String,
    /// The record's `completedAt`, RFC 3339 (UTC).
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub at: Option<String>,
    /// ShroudForge's own loader ran the pass (true), or the runtime kiln-shroudforge hosts (false).
    pub own_loader: bool,
    /// The version of the ShroudForge that ran the pass, when known: the installed one's `shroudforge/version.json`, or
    /// the hosted runtime's as kiln-shroudforge reported it.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub version: Option<String>,
    /// The switched-on packages of ShroudForge's mods folder that need the pass on this side (ids, sorted): they did
    /// not load.
    pub mods: Vec<String>,
    /// What to change, when the failure is the one above (a ShroudForge before 1.8.0 under Wine or Proton, "os error
    /// 5") and Kiln's own ShroudForge is there to run the mods instead. A sentence without its full stop.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub cure: Option<String>,
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

/// `1.7.9`, `1.7.9-build.4` are before 1.8.0; a version that does not start with two numbers is not judged.
fn before_1_8(version: &str) -> bool {
    let mut parts = version.trim().trim_start_matches('v').split(['.', '-', '+']);
    match (parts.next().and_then(|p| p.parse::<u32>().ok()), parts.next().and_then(|p| p.parse::<u32>().ok())) {
        (Some(major), Some(minor)) => (major, minor) < (1, 8),
        _ => false,
    }
}

/// ShroudForge's `ModManifest::requires_pregame`: a `patch` part, or `export` without a runtime part. A legacy EML
/// package is applied by the same pass.
fn needs_pass(m: &crate::shroudforge::SfMod) -> bool {
    let has = |c: &str| m.capabilities.iter().any(|x| x == c);
    has("patch") || (has("export") && !has("runtime") && !has("runtime-register-dll"))
        || m.launcher.as_deref().is_some_and(|l| l.eq_ignore_ascii_case("EML"))
}

/// The failed startup pass of ShroudForge's last start in this game folder, or None: no record, a pass that applied or
/// is still `preparing`, or a record of an earlier start (kiln-shroudforge's status of Kiln's last start says
/// ShroudForge did not run, and the record is not newer than it). `wine`: the game runs under Wine or Proton.
/// Reads only; starts nothing.
pub fn failure(game_dir: &Path, wine: bool) -> Option<SfStartupFailure> {
    let file = state_file(game_dir)?;
    let doc = crate::shroudforge::read_effect_json(game_dir, &file).ok()??;
    let assets = doc.get("assets")?.as_object()?;
    if assets.get("status")?.as_str()? != "failed" {
        return None;
    }
    let completed = assets.get("completedAt").and_then(|v| v.as_u64());
    let status = crate::shroudforge::status(game_dir).ok().flatten();
    let word = |key: &str| status.as_ref().and_then(|s| s.get(key)).and_then(|v| v.as_str());
    let installed = crate::shroudforge::detect(game_dir);
    let own_loader = match word("mode") {
        Some("hosted") => false,
        Some("self-hosted") => true,
        // Kiln's adapter has not said anything in this folder: ShroudForge's own loader, while it is installed
        None => installed.as_ref().is_some_and(|i| i.bootstrap || i.dbghelp),
        // off, not-installed, failed: ShroudForge did not run at Kiln's last start. The record is a later start's only
        // when it is newer than that status (ShroudForge's own loader in a start without Kiln).
        Some(_) => {
            let updated = status.as_ref().and_then(|s| s.get("updated")).and_then(|v| v.as_u64());
            matches!((completed, updated), (Some(c), Some(u)) if c > u)
        }
    };
    if !own_loader && word("mode") != Some("hosted") {
        return None;
    }
    let hosted_installed = !own_loader && word("source") == Some("installed");
    let version = if own_loader || hosted_installed {
        installed.as_ref().and_then(|i| i.version.clone())
    } else {
        status.as_ref().and_then(|s| s.pointer("/bundled/version")).and_then(|v| v.as_str()).map(str::to_owned)
    };
    let error = one_line(assets.get("error").and_then(|v| v.as_str()).unwrap_or("it gave no reason"));
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
    // The open-file fault: a ShroudForge before 1.8.0 that publishes under Wine or Proton. Kiln's own runtime is the
    // cure while it is installed; hosting the installed one ("Use my installed ShroudForge") runs the same code.
    let fault = wine && error.contains("(os error 5)") && version.as_deref().is_some_and(before_1_8) && (own_loader || hosted_installed);
    let bundled = game_dir.join("kiln/plugins/shroudforge/shroudforge-runtime.dll").is_file()
        && game_dir.join("kiln/plugins/kiln-shroudforge.dll").is_file();
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
    Some(SfStartupFailure { error, at: completed.and_then(|c| i64::try_from(c).ok()).map(crate::util::rfc3339), own_loader, version, mods, cure })
}

impl SfStartupFailure {
    /// "ShroudForge 1.7.9's own loader", "ShroudForge, hosted by Kiln,".
    fn who(&self) -> String {
        match (self.own_loader, &self.version) {
            (true, Some(v)) => format!("ShroudForge {v}'s own loader"),
            (true, None) => "ShroudForge's own loader".into(),
            (false, Some(v)) => format!("ShroudForge {v}, hosted by Kiln,"),
            (false, None) => "ShroudForge, hosted by Kiln,".into(),
        }
    }

    /// What happened, which mods that leaves out and what to change, for a person. No full stop at the end (the
    /// launcher's notes and the report add their own).
    pub fn text(&self) -> String {
        let mut t = format!("{} could not write its mods' changes into the game's data files at its last start{}: {}. It put the game data back \
            and the game started without them", self.who(), self.at.as_deref().map(|a| format!(" ({a})")).unwrap_or_default(), self.error);
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

    /// The record in one line for a list of facts: "failed at 2026-11-10T11:00:14Z: pregame phase failed: ...".
    pub fn line(&self) -> String {
        format!("failed{}: {}", self.at.as_deref().map(|a| format!(" at {a}")).unwrap_or_default(), self.error)
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

        /// Kiln's own ShroudForge (the component and its adapter).
        fn bundled(&self) {
            self.w("kiln/plugins/kiln-shroudforge.dll", "x");
            self.w("kiln/plugins/shroudforge/shroudforge-runtime.dll", "x");
        }

        fn status(&self, json: &str) {
            self.w("kiln/state/shroudforge.json", json);
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

    #[test]
    fn shroudforges_words_are_one_bounded_line() {
        assert_eq!(one_line("a\r\n  b\tc\u{7} d."), "a b c d");
        let long = "x".repeat(MAX_ERROR + 50);
        assert_eq!(one_line(&long).chars().count(), MAX_ERROR + 3);
        assert!(before_1_8("1.7.9") && before_1_8("v1.7.9-build.42") && before_1_8("0.9") && !before_1_8("1.8.0") && !before_1_8("2.0.1") && !before_1_8("") && !before_1_8("next"));
    }
}
