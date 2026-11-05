//! Offline config editing: JSONC edits keep comments, layout and Kiln's meta lines; validation mirrors Kiln.Api's
//! ConfigSchema (messages included); INI values files; the native mod user state.

use kiln_manage::ErrorCode;
use kiln_manage::config::{self, NativeModSwitch, NativeUserState, SchemaSnapshot, durations};
use kiln_manage::jsonc::{self, ConfigDoc};
use serde_json::json;
use std::path::{Path, PathBuf};

/// A file the way Kiln.Api's Config.Generate writes it.
const KILN_FILE: &str = r#"// big-stacks: server config of mod "big-stacks", written by Kiln from the mod's schema.
// Edit and save while the server runs: Kiln reloads it. Entries marked "Requires a restart" apply after the
// mod reloads. Values outside a range are clamped; keys the mod does not know are kept.
{
  // Stack limits
  // Synced to clients.
  "limits": {
    // Stack size multiplier
    // Default: 2 | Range: 1 .. 100 | Synced to clients
    "multiplier": 2,

    // Default: "all" | One of: all, materials, food | Synced to clients
    "mode": "all"
  },

  // Default: "5m" | e.g. "90s", "5m", "1h30m" | Requires a restart
  "autosave": "5m",

  // Default: []
  "excluded": [],

  // Unknown key: not used by the mod, kept as written.
  "legacy": 7
}
"#;

struct Tmp(PathBuf);

impl Tmp {
    fn new(name: &str) -> Tmp {
        let p = std::env::temp_dir().join(format!("kiln-config-test-{name}-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&p);
        std::fs::create_dir_all(&p).unwrap();
        Tmp(p)
    }
}

impl Drop for Tmp {
    fn drop(&mut self) {
        let _ = std::fs::remove_dir_all(&self.0);
    }
}

fn example(name: &str) -> String {
    std::fs::read_to_string(Path::new(env!("CARGO_MANIFEST_DIR")).join("../../schema/examples").join(name)).unwrap()
}

fn kiln_dir(name: &str) -> Tmp {
    let t = Tmp::new(name);
    std::fs::write(t.0.join("big-stacks.json"), KILN_FILE).unwrap();
    std::fs::create_dir_all(t.0.join(".schema")).unwrap();
    std::fs::write(t.0.join(".schema/big-stacks.json"), example("config-schema.json")).unwrap();
    t
}

#[test]
fn set_changes_only_the_value_token_and_keeps_meta_lines() {
    let t = kiln_dir("set");
    let d = config::set(&t.0, "big-stacks.json", "limits.multiplier", &json!(5)).unwrap();
    assert_eq!(d.text, KILN_FILE.replace("\"multiplier\": 2,", "\"multiplier\": 5,"));
    assert_eq!(std::fs::read_to_string(t.0.join("big-stacks.json")).unwrap(), d.text);
    assert_eq!(d.values["limits"]["multiplier"], 5);
    assert!(d.errors.is_empty(), "{:?}", d.errors);
    assert!(d.schema.is_some());
    // canonical forms, as Kiln writes them
    let d = config::set(&t.0, "big-stacks.json", "autosave", &json!(90)).unwrap();
    assert!(d.text.contains("\"autosave\": \"1m30s\","));
    let d = config::set(&t.0, "big-stacks.json", "autosave", &json!("1.5h")).unwrap();
    assert!(d.text.contains("\"autosave\": \"1h30m\","));
    let d = config::set(&t.0, "big-stacks.json", "LIMITS.MODE", &json!("Materials")).unwrap();
    assert!(d.text.contains("\"mode\": \"materials\"\n"));
    let d = config::set(&t.0, "big-stacks.json", "excluded", &json!(["wood", "stone"])).unwrap();
    assert!(d.text.contains("\"excluded\": [\"wood\", \"stone\"],\n"));
    let d = config::set(&t.0, "big-stacks.json", "limits.multiplier", &json!("2.5")).unwrap();
    assert!(d.text.contains("\"multiplier\": 2.5,"));
    // everything else is byte-identical
    for line in KILN_FILE.lines().filter(|l| l.trim_start().starts_with("//")) {
        assert!(d.text.contains(line), "lost {line:?}");
    }
    assert!(d.text.contains("\"legacy\": 7"));
}

#[test]
fn set_refuses_what_kiln_would_refuse() {
    let t = kiln_dir("refuse");
    let e = config::set(&t.0, "big-stacks.json", "limits.multiplier", &json!(200)).unwrap_err();
    assert_eq!(e.code, ErrorCode::Schema);
    assert_eq!(e.message, "big-stacks.json: \"limits.multiplier\" must be in 1..100 (got 200)");
    assert_eq!(e.detail.unwrap()[0]["path"], "limits.multiplier");
    let e = config::set(&t.0, "big-stacks.json", "limits.mode", &json!("wood")).unwrap_err();
    assert_eq!(e.message, "big-stacks.json: \"limits.mode\" = \"wood\" is not one of all, materials, food");
    let e = config::set(&t.0, "big-stacks.json", "autosave", &json!("soon")).unwrap_err();
    assert!(e.message.ends_with("is not a duration (e.g. \"90s\", \"5m\", \"1h30m\", \"00:05:00\")"), "{}", e.message);
    let e = config::set(&t.0, "big-stacks.json", "excluded", &json!("x")).unwrap_err();
    assert_eq!(e.message, "big-stacks.json: \"excluded\" must be a list ([...]); using the default");
    let e = config::set(&t.0, "big-stacks.json", "limits.multiplier", &json!(true)).unwrap_err();
    assert_eq!(e.message, "big-stacks.json: \"limits.multiplier\" = true is not a float");
    let e = config::set(&t.0, "big-stacks.json", "nope", &json!(1)).unwrap_err();
    assert_eq!(e.code, ErrorCode::Schema);
    assert!(config::set(&t.0, "../escape.json", "a", &json!(1)).is_err());
    // nothing was written
    assert_eq!(std::fs::read_to_string(t.0.join("big-stacks.json")).unwrap(), KILN_FILE);
}

#[test]
fn read_reports_kiln_style_problems() {
    let t = kiln_dir("read");
    std::fs::write(t.0.join("big-stacks.json"), KILN_FILE.replace("\"multiplier\": 2,", "\"multiplier\": 0.5,")).unwrap();
    let d = config::read(&t.0, "big-stacks.json").unwrap();
    assert_eq!(d.errors.len(), 1);
    assert_eq!(d.errors[0].message, "\"limits.multiplier\" must be in 1..100 (got 0.5)");
    std::fs::write(t.0.join("big-stacks.json"), "{\n  \"limits\": 3,\n}").unwrap();
    let d = config::read(&t.0, "big-stacks.json").unwrap();
    assert_eq!(d.errors[0].message, "\"limits\" must be a section ({ ... }); using its defaults");
    std::fs::write(t.0.join("big-stacks.json"), "{\n  \"limits\": {\n}").unwrap();
    let d = config::read(&t.0, "big-stacks.json").unwrap();
    assert!(d.errors[0].message.starts_with("line 3: "), "{:?}", d.errors);
    assert!(d.values.is_null());
    assert_eq!(config::read(&t.0, "missing.json").unwrap_err().code, ErrorCode::NotFound);
}

#[test]
fn write_and_reset() {
    let t = kiln_dir("write");
    let e = config::write(&t.0, "big-stacks.json", "{ \"limits\": ").unwrap_err();
    assert_eq!(e.code, ErrorCode::Parse);
    let e = config::write(&t.0, "big-stacks.json", "{ \"limits\": { \"multiplier\": 101 } }").unwrap_err();
    assert_eq!(e.code, ErrorCode::Schema);
    let text = KILN_FILE.replace("\"multiplier\": 2,", "\"multiplier\": 9,").replace("\"autosave\": \"5m\"", "\"autosave\": \"10m\"");
    config::write(&t.0, "big-stacks.json", &text).unwrap();
    let d = config::reset(&t.0, "big-stacks.json", Some("limits.multiplier")).unwrap();
    assert!(d.text.contains("\"multiplier\": 2,") && d.text.contains("\"autosave\": \"10m\","));
    let d = config::reset(&t.0, "big-stacks.json", None).unwrap();
    assert_eq!(d.text, KILN_FILE);
    // without a snapshot: the member goes (Kiln uses its default), a whole reset deletes the file
    std::fs::write(t.0.join("plain.json"), "{\n  \"a\": 1, // one\n  \"b\": 2\n}\n").unwrap();
    let d = config::reset(&t.0, "plain.json", Some("b")).unwrap();
    assert_eq!(d.text, "{\n  \"a\": 1 // one\n}\n");
    config::reset(&t.0, "plain.json", None).unwrap();
    assert!(!t.0.join("plain.json").exists());
}

#[test]
fn list_files_and_snapshots() {
    let t = kiln_dir("list");
    std::fs::create_dir_all(t.0.join("emf")).unwrap();
    std::fs::write(t.0.join("emf/beastmaster.ini"), "[general]\nmax_pets = 3\n").unwrap();
    std::fs::write(t.0.join("native.json"), "{\"format\": 1}").unwrap();
    std::fs::write(t.0.join("big-stacks.json.tmp"), "x").unwrap();
    std::fs::write(t.0.join(".schema/other.json"), example("config-schema.json").replace("big-stacks.json", "other.json")).unwrap();
    let l = config::list(&t.0).unwrap();
    let files: Vec<&str> = l.iter().map(|f| f.file.as_str()).collect();
    assert_eq!(files, ["big-stacks.json", "emf/beastmaster.ini", "native.json", "other.json"]);
    assert!(l[0].has_schema && l[0].kind == "kiln" && l[0].mod_id.as_deref() == Some("big-stacks"));
    assert_eq!(l[0].mod_version.as_deref(), Some("0.3.1"));
    assert!(l[0].modified.as_deref().is_some_and(|m| m.ends_with('Z')));
    assert_eq!((l[1].kind.as_str(), l[1].mod_id.as_deref(), l[1].has_schema), ("emf", Some("beastmaster"), false));
    assert_eq!(l[2].kind, "native");
    assert!(l[3].modified.is_none() && l[3].has_schema);
}

#[test]
fn ini_values_files() {
    let t = Tmp::new("ini");
    std::fs::create_dir_all(t.0.join(".schema/emf")).unwrap();
    std::fs::create_dir_all(t.0.join("emf")).unwrap();
    std::fs::write(t.0.join(".schema/emf/beastmaster.json"), example("config-schema-emf.json")).unwrap();
    let ini = "; Beastmaster settings\r\n[General]\r\nmax_pets = 3 \r\nenabled=yes\r\n\r\n[Other]\r\nx = 1\r\n";
    std::fs::write(t.0.join("emf/beastmaster.ini"), ini).unwrap();
    let d = config::read(&t.0, "emf/beastmaster.ini").unwrap();
    assert_eq!(d.values["General"]["max_pets"], 3);
    assert_eq!(d.values["General"]["enabled"], true);
    assert!(d.errors.is_empty());
    let d = config::set(&t.0, "emf/beastmaster.ini", "general.max_pets", &json!(5)).unwrap();
    assert_eq!(d.text, ini.replace("max_pets = 3 ", "max_pets = 5"));
    let e = config::set(&t.0, "emf/beastmaster.ini", "general.max_pets", &json!(9)).unwrap_err();
    assert_eq!(e.message, "emf/beastmaster.ini: \"general.max_pets\" must be in 1..8 (got 9)");
    let d = config::reset(&t.0, "emf/beastmaster.ini", None).unwrap();
    assert!(d.text.contains("max_pets = 3\r\n") && d.text.contains("enabled=true\r\n") && d.text.starts_with("; Beastmaster"));
    // new keys and sections
    assert_eq!(config::ini::set("[a]\nx = 1\n", "a.y", "2"), "[a]\nx = 1\ny = 2\n");
    assert_eq!(config::ini::set("[a]\nx = 1\n", "b.y", "2"), "[a]\nx = 1\n\n[b]\ny = 2\n");
    assert_eq!(config::ini::set("x=1\n[a]\nz=0\n", "y", "2"), "x=1\ny=2\n[a]\nz=0\n");
    assert_eq!(config::ini::set("[a]\nz=0\n", "y", "2"), "y=2\n[a]\nz=0\n");
    assert_eq!(config::ini::remove("[a]\nx = 1\ny = 2\n", "a.x"), "[a]\ny = 2\n");
}

#[test]
fn native_state_round_trip() {
    let t = Tmp::new("native");
    let s = config::read_native_state(&t.0).unwrap();
    assert_eq!((s.format, s.mods.len()), (1, 0));
    let mut s = NativeUserState { format: 1, mods: Default::default() };
    s.mods.insert("creative-mode".into(), NativeModSwitch { enabled: Some(false) });
    s.mods.insert("fps-unlock".into(), NativeModSwitch { enabled: None });
    config::write_native_state(&t.0, &s).unwrap();
    assert_eq!(config::read_native_state(&t.0).unwrap(), s);
    let text = std::fs::read_to_string(t.0.join("native.json")).unwrap();
    assert!(text.contains("\"enabled\": false") && text.ends_with("}\n"));
    std::fs::write(t.0.join("native.json"), "{\"format\": 2}").unwrap();
    assert_eq!(config::read_native_state(&t.0).unwrap_err().code, ErrorCode::Schema);
    let mut bad = NativeUserState { format: 1, mods: Default::default() };
    bad.mods.insert("Bad Id".into(), NativeModSwitch::default());
    assert!(config::write_native_state(&t.0, &bad).is_err());
}

#[test]
fn durations_like_kiln_api() {
    let f = |s: &str| durations::parse(s).map(durations::format);
    assert_eq!(f("1h30m").as_deref(), Some("1h30m"));
    assert_eq!(f("90").as_deref(), Some("1m30s"));
    assert_eq!(f("00:05:00").as_deref(), Some("5m"));
    assert_eq!(f("1.02:00:00").as_deref(), Some("1d2h"));
    assert_eq!(f("250ms").as_deref(), Some("250ms"));
    assert_eq!(f("1.5h").as_deref(), Some("1h30m"));
    assert_eq!(f("2days").as_deref(), Some("2d"));
    assert_eq!(f("2 days"), None); // Kiln.Api: the unit follows the number directly
    assert_eq!(f("1.25s").as_deref(), Some("1.25s"));
    assert_eq!(f("0").as_deref(), Some("0s"));
    for bad in ["", "soon", "-5", "5x", "1e11", "25:00", "1h-2m"] {
        assert_eq!(durations::parse(bad), None, "{bad}");
    }
}

#[test]
fn list_items_follow_kiln_clamp_warnings() {
    let s: SchemaSnapshot = serde_json::from_value(json!({
        "format": 1, "source": "kiln", "file": "x.json", "id": "x", "mod": "x",
        "entries": [
            {"path": "levels", "type": "list<int>", "default": [1], "min": 1, "max": 3},
            {"path": "label", "type": "string", "default": ""},
            {"path": "count", "type": "int", "default": 0}
        ]
    }))
    .unwrap();
    let errs = config::validate(&s, &json!({"levels": [2, 5], "label": 5, "count": 2.5}));
    let msgs: Vec<&str> = errs.iter().map(|e| e.message.as_str()).collect();
    assert_eq!(msgs, ["\"levels[1]\" = 5 is outside 1..3; using 3", "\"count\" must be a whole number"]);
    assert_eq!(config::check_value(&s.entries[1], &json!(5)).unwrap(), json!("5"));
    assert_eq!(config::check_value(&s.entries[2], &json!("4")).unwrap(), json!(4));
    let e = config::check_value(&s.entries[2], &json!(3e9)).unwrap_err();
    assert_eq!(e, "\"count\" must be in -2147483648..2147483647 (got 3000000000)");
}

#[test]
fn config_doc_keeps_everything_but_the_edited_tokens() {
    let mut d = ConfigDoc::parse(KILN_FILE).unwrap();
    d.set(&jsonc::split_path("limits.multiplier"), &json!(3)).unwrap();
    d.set(&jsonc::split_path("excluded[0]"), &json!("x")).unwrap();
    d.set(&jsonc::split_path("added"), &json!({"k": [1, 2]})).unwrap();
    let t = d.text();
    assert!(t.contains("\"multiplier\": 3,") && t.contains("\"excluded\": [\"x\"],"));
    assert!(t.ends_with("\"legacy\": 7,\n  \"added\": {\n    \"k\": [1, 2]\n  }\n}\n"), "{t}");
    assert_eq!(d.value(&jsonc::split_path("added.k[1]")), Some(json!(2)));
    assert!(d.remove(&jsonc::split_path("added")).unwrap());
    assert_eq!(d.text(), KILN_FILE.replace("\"multiplier\": 2,", "\"multiplier\": 3,").replace("\"excluded\": []", "\"excluded\": [\"x\"]"));
    // the parser's own guarantees
    let tree = jsonc::parse_tree(KILN_FILE).unwrap();
    assert_eq!(tree.get("limits").unwrap().get("mode").unwrap().raw(KILN_FILE), "\"all\"");
    assert_eq!(jsonc::parse(KILN_FILE).unwrap()["autosave"], "5m");
}
