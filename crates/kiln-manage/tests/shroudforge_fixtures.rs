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
    let mut list: Vec<Value> = o
        .iter()
        .map(|(k, label)| {
            let v = shroudforge::option_value(s, k).expect("valid typed option");
            json!({"value": v, "label": label})
        })
        .collect();
    list.sort_by_key(|v| v.to_string());
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

fn sorted_options(mut v: Value) -> Value {
    for s in v["settings"].as_array_mut().unwrap() {
        if let Some(o) = s.get_mut("options").and_then(|o| o.as_array_mut()) {
            o.sort_by_key(|x| x.to_string());
        }
    }
    v
}

#[test]
fn shroudforge_packages_read_and_write_like_the_in_game_page() {
    let root = std::env::temp_dir().join(format!("kiln-sf-fixtures-{}", std::process::id()));
    let _t = Tmp(root.clone());
    let cases = cases();
    assert!(cases.len() >= 7);
    for c in cases {
        let name = c["name"].as_str().unwrap();
        let dir = root.join(name);
        std::fs::create_dir_all(dir.join("src")).unwrap();
        std::fs::write(dir.join("mod.json"), serde_json::to_string(&c["mod"]).unwrap()).unwrap();
        std::fs::write(dir.join("src/mod.lua"), "return {}").unwrap();
        if let Some(text) = c["extended"].as_str() {
            std::fs::write(dir.join("extended.mod.json"), text).unwrap();
        }
        let m = shroudforge::read_mod(&dir).unwrap();
        assert_eq!(sorted_options(read_view(&m)), sorted_options(c["read"].clone()), "{name}: read");
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
        let text = std::fs::read_to_string(dir.join("extended.mod.json")).unwrap();
        assert_eq!(text, c["expect"].as_str().unwrap(), "{name}: the file");
        if let Some(after) = c.get("enabledAfter") {
            let m = shroudforge::read_mod(&dir).unwrap();
            let values = shroudforge::settings_values(&m);
            let now: Vec<&str> =
                m.settings.iter().filter(|s| shroudforge::setting_enabled(&s.key, &m.settings, &values)).map(|s| s.key.as_str()).collect();
            assert_eq!(json!(now), *after, "{name}: enabledWhen after the edits");
        }
    }
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
