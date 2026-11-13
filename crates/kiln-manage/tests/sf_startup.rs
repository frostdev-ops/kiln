//! ShroudForge's failed startup pass on the launcher's engine list (`engine_mods::status`, the `engine_status`
//! command), whatever other loaders are in the game folder. Fixtures are made up.

use kiln_manage::engine_mods;
use kiln_manage::pe::build_test_dll;
use std::path::{Path, PathBuf};

struct Tmp(PathBuf);
impl Drop for Tmp {
    fn drop(&mut self) {
        let _ = std::fs::remove_dir_all(&self.0);
    }
}

fn game(tag: &str) -> Tmp {
    let p = std::env::temp_dir().join(format!("km-sf-startup-{tag}-{}", std::process::id()));
    let _ = std::fs::remove_dir_all(&p);
    std::fs::create_dir_all(&p).unwrap();
    Tmp(p)
}

fn put(root: &Path, rel: &str, data: &[u8]) {
    let p = root.join(rel);
    std::fs::create_dir_all(p.parent().unwrap()).unwrap();
    std::fs::write(p, data).unwrap();
}

/// 2026-11-10T11:00:00Z.
const T0: u64 = 1_794_308_400;

/// Kiln with its own ShroudForge, a profile that lists one ShroudForge mod (so Kiln hosts ShroudForge), and the start
/// of T0 in which the hosted pass failed.
fn hosted_failure(g: &Path) {
    put(g, "enshrouded.exe", b"MZ");
    put(g, "kiln/plugins/kiln-shroudforge.dll", b"adapter");
    put(g, "kiln/plugins/shroudforge/shroudforge-runtime.dll", b"runtime");
    put(g, "kiln/plugins/shroudforge/version.json", br#"{"version":"1.7.9","build":"kiln.63b8862","host":"kiln"}"#);
    put(g, "mods/bone-caller/mod.json", br#"{"id":"bone-caller","name":"Bone Caller","version":"1.0.0","capabilities":["patch","runtime"]}"#);
    put(g, "mods/bone-caller/extended.mod.json", br#"{"schemaVersion":1,"launcher":"SF","enabled":true,"targets":["client"],"settings":{}}"#);
    put(g, "mods/bone-caller/src/mod.lua", b"return {}");
    put(g, "kiln/profile.json", serde_json::to_vec(&serde_json::json!({
        "format": 1, "id": "my-mods", "name": "My mods", "generation": 3, "dropins": false,
        "configDir": r"Z:\var\home\frostling\.local\share\kiln\profiles\my-mods\config",
        "mods": {"shroudforge": [{"id": "bone-caller", "version": "1.0.0", "path": r"Z:\var\home\frostling\Enshrouded\mods\bone-caller"}]},
    })).unwrap().as_slice());
    put(g, "kiln/state/boot.json", serde_json::to_vec(&serde_json::json!({
        "format": 1, "kiln": "0.2.0-dev", "pid": 412, "started": "2026-11-10T11:00:00Z", "written": "2026-11-10T11:00:02Z", "side": "client",
        "hooks": true, "profile": {"mode": "active", "id": "my-mods", "generation": 3}, "chain": [], "plugins": [], "errors": [],
    })).unwrap().as_slice());
    put(g, "kiln/state/shroudforge.json", format!(r#"{{"format":1,"mode":"hosted","source":"bundled","pid":412,"side":"client","updated":{},
        "bundled":{{"version":"1.7.9","build":"kiln.63b8862"}},"startupPrepared":true}}"#, T0 + 5).as_bytes());
    put(g, "shroudforge/state.json", format!(r#"{{"schemaVersion":1,"assets":{{"status":"failed","phase":"startup","target":"enshrouded",
        "completedAt":{},"error":"pregame phase failed: could not create a new enshrouded.kfc working baseline"}}}}"#, T0 + 3).as_bytes());
}

fn rows(g: &Path) -> Vec<serde_json::Value> {
    serde_json::to_value(engine_mods::status(g)).unwrap().as_array().unwrap().clone()
}

fn row<'a>(rows: &'a [serde_json::Value], kind: &str) -> &'a serde_json::Value {
    rows.iter().find(|r| r["kind"] == kind).unwrap_or_else(|| panic!("no {kind} row"))
}

/// Review of 6e231b37, finding 4. EML's own `dbghelp.dll` is still in the game folder (under Proton it does not load
/// without an override) while Kiln hosts ShroudForge. The failure used to ride on the own-loader note of a ShroudForge
/// loader: here the only note is EML's, of EML's proxy, so no line of the engine list said that the pass failed. It
/// is on ShroudForge's own line now, whatever notes there are.
#[test]
fn a_hosted_failure_is_on_shroudforges_line_beside_emls_proxy() {
    let t = game("eml-proxy");
    let g = &t.0;
    hosted_failure(g);
    put(g, "dbghelp.dll", &build_test_dll(&[], b"EML_LOG_FILE_PATH"));
    let rows = rows(g);
    let said: Vec<&str> = rows.iter().filter(|r| r.to_string().contains("could not write its mods' changes")).filter_map(|r| r["kind"].as_str()).collect();
    assert!(said.contains(&"shroudforge"), "ShroudForge's line says it; the lines that do: {said:?}");
    let sf = row(&rows, "shroudforge");
    let text = sf["startupFailed"]["text"].as_str().unwrap_or_else(|| panic!("no startupFailed.text on ShroudForge's line: {sf}"));
    assert!(text.starts_with("ShroudForge 1.7.9, hosted by Kiln, could not write its mods' changes into the game's data files"), "{text}");
    assert!(text.contains("(bone-caller)"), "{text}");
    // the EML line points there while ShroudForge is what runs the EML mods
    let eml = row(&rows, "eml");
    assert_eq!(eml["startupFailed"]["ownLoader"], false, "the EML line carries it too: {eml}");
    // no other engine's line has it
    for r in rows.iter().filter(|r| r["kind"] != "shroudforge" && r["kind"] != "eml") {
        assert!(r.get("startupFailed").is_none_or(|v| v.is_null()), "{r}");
    }
}

/// The same without any other loader, and nothing once the pass applied.
#[test]
fn shroudforges_line_has_the_failure_only_while_the_record_says_failed() {
    let t = game("plain");
    let g = &t.0;
    hosted_failure(g);
    assert!(row(&rows(g), "shroudforge")["startupFailed"]["text"].is_string());
    put(g, "shroudforge/state.json", format!(r#"{{"schemaVersion":1,"assets":{{"status":"applied","phase":"startup","target":"enshrouded","completedAt":{}}}}}"#, T0 + 3).as_bytes());
    let rows = rows(g);
    assert!(rows.iter().all(|r| r.get("startupFailed").is_none_or(|v| v.is_null())), "{rows:?}");
}
