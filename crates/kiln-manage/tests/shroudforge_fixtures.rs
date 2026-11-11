//! schema/fixtures/shroudforge/cases.json: ShroudForge's extended.mod.json as the launcher reads and writes it. The
//! in-game Mods page (managed/services/Kiln.Ui/ShroudForge.cs) runs the same cases in
//! managed/tests/Kiln.Ui.Tests/ShroudForgeTests.cs, so the two writers cannot drift apart.

use kiln_manage::shroudforge::{self, SfMod};
use serde_json::{Value, json};
use std::path::{Path, PathBuf};

struct Tmp(PathBuf);
impl Drop for Tmp {
    fn drop(&mut self) {
        let _ = std::fs::remove_dir_all(&self.0);
    }
}

fn cases() -> Vec<Value> {
    let p = Path::new(env!("CARGO_MANIFEST_DIR")).join("../../schema/fixtures/shroudforge/cases.json");
    let doc: Value = serde_json::from_str(&std::fs::read_to_string(&p).unwrap()).unwrap();
    doc["cases"].as_array().unwrap().clone()
}

fn options(s: &shroudforge::SfSetting) -> Value {
    // the launcher keeps options as value -> label; ShroudForge types each value like the setting's own
    let Some(o) = s.options.as_ref().filter(|o| !o.is_empty()) else { return Value::Null };
    // in the order the mod declares them (the order both pages show)
    let list: Vec<Value> = shroudforge::option_keys(s)
        .into_iter()
        .map(|k| {
            let v = shroudforge::option_value(s, k).expect("valid typed option");
            json!({"value": v, "label": o[k.as_str()]})
        })
        .collect();
    Value::Array(list)
}

fn read_view(m: &SfMod) -> Value {
    let settings: Vec<Value> = m
        .settings
        .iter()
        .map(|s| {
            let mut v = json!({"key": s.key, "type": shroudforge::value_type(s), "label": shroudforge::label_of(s),
                "control": shroudforge::control_of(s), "applyAt": s.apply_at, "live": s.live});
            if let Some(w) = &s.enabled_when {
                v["enabledWhen"] = w.clone();
            }
            let o = options(s);
            if !o.is_null() {
                v["options"] = o;
            }
            v
        })
        .collect();
    let values = shroudforge::settings_values(m);
    let enabled_now: Vec<&str> =
        m.settings.iter().filter(|s| shroudforge::setting_enabled(&s.key, &m.settings, &values)).map(|s| s.key.as_str()).collect();
    json!({"enabled": m.enabled, "switchLive": m.switch_live, "settings": settings, "enabledNow": enabled_now})
}

/// The bytes of a case's extended.mod.json: `extended` as UTF-8, or `extendedLatin1` one byte per character (a file
/// that is not UTF-8).
fn extended_bytes(c: &Value) -> Option<Vec<u8>> {
    if let Some(text) = c["extendedLatin1"].as_str() {
        return Some(text.chars().map(|ch| u8::try_from(u32::from(ch)).expect("a Latin-1 character")).collect());
    }
    c["extended"].as_str().map(|t| t.as_bytes().to_vec())
}

#[test]
fn shroudforge_packages_read_and_write_like_the_in_game_page() {
    let root = std::env::temp_dir().join(format!("kiln-sf-fixtures-{}", std::process::id()));
    let _t = Tmp(root.clone());
    let cases = cases();
    assert!(cases.len() >= 7);
    // every case runs; the failures are listed together
    let failed: Vec<String> = cases
        .iter()
        .filter(|c| std::panic::catch_unwind(|| run_case(&root, c)).is_err())
        .map(|c| c["name"].as_str().unwrap().to_owned())
        .collect();
    assert!(failed.is_empty(), "failed cases: {failed:?}");
}

fn run_case(root: &Path, c: &Value) {
    {
        let name = c["name"].as_str().unwrap();
        let dir = root.join(name);
        std::fs::create_dir_all(dir.join("src")).unwrap();
        std::fs::write(dir.join("mod.json"), serde_json::to_string(&c["mod"]).unwrap()).unwrap();
        std::fs::write(dir.join("src/mod.lua"), "return {}").unwrap();
        let original = extended_bytes(c);
        if let Some(bytes) = &original {
            std::fs::write(dir.join("extended.mod.json"), bytes).unwrap();
        }
        let refused = c["refused"].as_bool() == Some(true);
        if refused {
            // ShroudForge refuses the package: so does the launcher, and nothing is written
            assert!(shroudforge::read_mod(&dir).is_err(), "{name}: read should be refused");
        } else {
            let m = shroudforge::read_mod(&dir).unwrap_or_else(|e| panic!("{name}: read: {e:?}"));
            assert_eq!(read_view(&m), c["read"], "{name}: read");
        }
        for e in c["edits"].as_array().unwrap() {
            let before = std::fs::read(dir.join("extended.mod.json")).ok();
            let result = if let Some(on) = e.get("enabled") {
                shroudforge::set_enabled(&dir, on.as_bool().unwrap()).map(|_| ())
            } else {
                shroudforge::set_setting(&dir, e["setting"].as_str().unwrap(), &e["value"]).map(|_| ())
            };
            assert_eq!(result.is_ok(), e["ok"].as_bool().unwrap(), "{name}: {e} -> {result:?}");
            if result.is_err() {
                assert_eq!(std::fs::read(dir.join("extended.mod.json")).ok(), before, "{name}: a refused edit wrote");
            }
        }
        let bytes = std::fs::read(dir.join("extended.mod.json")).unwrap();
        if refused {
            assert_eq!(Some(bytes), original, "{name}: a refused package was written");
        } else {
            assert_eq!(String::from_utf8(bytes).unwrap(), c["expect"].as_str().unwrap(), "{name}: the file");
        }
        if let Some(after) = c.get("enabledAfter") {
            let m = shroudforge::read_mod(&dir).unwrap();
            let values = shroudforge::settings_values(&m);
            let now: Vec<&str> =
                m.settings.iter().filter(|s| shroudforge::setting_enabled(&s.key, &m.settings, &values)).map(|s| s.key.as_str()).collect();
            assert_eq!(json!(now), *after, "{name}: enabledWhen after the edits");
        }
    }
}

/// `limits.fileBytes`: a larger extended.mod.json is refused by both readers (the in-game page reads it on the game's
/// process); one of exactly that size is read.
#[test]
fn the_settings_file_size_is_bounded_like_the_in_game_page() {
    let p = Path::new(env!("CARGO_MANIFEST_DIR")).join("../../schema/fixtures/shroudforge/cases.json");
    let doc: Value = serde_json::from_str(&std::fs::read_to_string(&p).unwrap()).unwrap();
    let limit = doc["limits"]["fileBytes"].as_u64().unwrap() as usize;
    let root = std::env::temp_dir().join(format!("kiln-sf-limit-{}", std::process::id()));
    let _t = Tmp(root.clone());
    std::fs::create_dir_all(root.join("src")).unwrap();
    std::fs::write(root.join("mod.json"), r#"{"id":"big","name":"Big","version":"1.0.0","capabilities":["runtime"]}"#).unwrap();
    std::fs::write(root.join("src/mod.lua"), "return {}").unwrap();
    let body = r#"{"enabled":true,"settings":{"a":{"value":1}}}"#;
    let padded = |n: usize| format!("{body}{}", " ".repeat(n - body.len()));
    std::fs::write(root.join("extended.mod.json"), padded(limit)).unwrap();
    assert!(shroudforge::read_mod(&root).is_ok(), "a file of exactly the limit is read");
    std::fs::write(root.join("extended.mod.json"), padded(limit + 1)).unwrap();
    assert!(shroudforge::read_mod(&root).is_err(), "a file over the limit is refused");
    assert!(shroudforge::set_enabled(&root, false).is_err());
    assert_eq!(std::fs::read(root.join("extended.mod.json")).unwrap().len(), limit + 1, "nothing was written");
}

/// A package may declare many settings: reading it and building the launcher's form take time in proportion to them, not
/// to their square (each lookup by key used to scan the list).
#[test]
fn many_settings_are_read_without_quadratic_work() {
    let n = 20_000;
    let root = std::env::temp_dir().join(format!("kiln-sf-many-{}", std::process::id()));
    let _t = Tmp(root.clone());
    std::fs::create_dir_all(root.join("src")).unwrap();
    std::fs::write(root.join("mod.json"), r#"{"id":"many","name":"Many","version":"1.0.0","capabilities":["runtime"]}"#).unwrap();
    std::fs::write(root.join("src/mod.lua"), "return {}").unwrap();
    let keys: Vec<String> = (0..n).map(|i| format!("s{i:05}")).collect();
    let settings: Vec<String> = keys.iter().rev().map(|k| format!(r#""{k}":{{"value":1}}"#)).collect();
    let listed: Vec<String> = keys.iter().map(|k| format!(r#""{k}""#)).collect();
    std::fs::write(
        root.join("extended.mod.json"),
        format!(r#"{{"enabled":true,"settings":{{{}}},"groups":[{{"label":"All","settings":[{}]}}]}}"#, settings.join(","), listed.join(",")),
    )
    .unwrap();
    let start = std::time::Instant::now();
    let m = shroudforge::read_mod(&root).unwrap();
    let schema = shroudforge::settings_schema(&m);
    let took = start.elapsed();
    assert_eq!(m.settings.len(), n);
    assert_eq!(m.settings[0].key, keys[n - 1], "declared order");
    assert_eq!(schema["properties"][&keys[0]]["x-kiln-group"], "All");
    assert!(took < std::time::Duration::from_secs(5), "{n} settings took {took:?}");
}
