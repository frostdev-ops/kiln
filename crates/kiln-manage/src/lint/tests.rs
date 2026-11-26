// Unit tests of the lint driver; the file-based suites are in crates/kiln-manage/tests/lint.rs.
use super::*;

#[test]
fn every_schema_of_the_index_is_embedded() {
    let e = embedded();
    assert!(e.index.len() >= 8);
    for row in &e.index {
        assert!(e.schemas.doc(&row.schema).is_some_and(|d| d.is_object()), "{} is not embedded", row.schema);
    }
}

#[test]
fn file_names_map_to_schemas() {
    let k = |p: &str| schema_for(Path::new(p)).map(|e| e.kind);
    assert_eq!(k("mods/x/kiln.mod.json").as_deref(), Some("manifest"));
    assert_eq!(k("mods/x/kiln.programs.json").as_deref(), Some("programs"));
    assert_eq!(k("mods/x/content/a.json").as_deref(), Some("content"));
    assert_eq!(k("mods/x/assets/assets.json").as_deref(), Some("assets"));
    assert_eq!(k("mods/x/assets/cook.json").as_deref(), Some("cook"));
    assert_eq!(k("mods/x/assets/player-states.json").as_deref(), Some("playerstates"));
    assert_eq!(k("mods/x/assets/cooked/player-states.json"), None); // what the cook writes is not the author's file
    assert_eq!(k("mods/x/impact/cook.json").as_deref(), Some("cook"));
    assert_eq!(k("mods/x/impact/natives.json").as_deref(), Some("natives"));
    assert_eq!(k("mods/x/native.json").as_deref(), Some("native"));
    assert_eq!(k("game/kiln/config/native.json"), None);
    assert_eq!(k("hut.kschem").as_deref(), Some("schematic"));
    assert_eq!(k("mods/x/mod-values/fishing.json").as_deref(), Some("mod-values"));
    assert_eq!(k("mods/x/mod-values/sub/fishing.json"), None); // Kiln.ModValues reads the folder itself, not below it
}

/// A mod folder with these mod-values files, linted whole: (file, code, pointer, severity) of each finding at warning or above.
fn mod_values_findings(name: &str, files: &[(&str, String)]) -> Vec<(String, String, String, Severity)> {
    let dir = std::env::temp_dir().join(format!("kiln-lint-mv-{name}-{}", std::process::id()));
    let _ = std::fs::remove_dir_all(&dir);
    std::fs::create_dir_all(dir.join("mod-values")).unwrap();
    std::fs::write(dir.join("kiln.mod.json"), r#"{"id":"mv-test","name":"MV test","version":"1.0.0","apiVersion":"0.2","side":"both","clients":"mod","license":"MIT"}"#).unwrap();
    for (f, text) in files {
        std::fs::write(dir.join(f), text).unwrap();
    }
    let r = lint_paths(std::slice::from_ref(&dir), &LintOptions { no_catalog: true, ..Default::default() });
    let out = r
        .diagnostics
        .iter()
        .filter(|d| d.severity >= Severity::Warning)
        .map(|d| (Path::new(&d.file).file_name().map(|n| n.to_string_lossy().into_owned()).unwrap_or_default(), d.code.clone(), d.pointer.clone().unwrap_or_default(), d.severity))
        .collect();
    std::fs::remove_dir_all(&dir).unwrap();
    out
}

fn values(defs: &str) -> String {
    format!(r#"{{"format":1,"values":{{{defs}}}}}"#)
}

fn has(found: &[(String, String, String, Severity)], code: &str, pointer: &str) -> bool {
    found.iter().any(|(_, c, p, s)| c == code && p == pointer && *s == Severity::Error)
}

#[test]
fn mod_values_example_lints_clean() {
    let example = include_str!("../../../../schema/examples/kiln-mod-values.json").to_string();
    assert_eq!(mod_values_findings("example", &[("mod-values/fishing.json", example)]), vec![]);
}

#[test]
fn mod_values_numbers_are_64_bit_strings() {
    let f = mod_values_findings("numbers", &[("mod-values/n.json", values(concat!(
        r#""edges":{"kind":"counter","label":"E","min":"-9223372036854775808","max":"9223372036854775807"},"#,
        r#""over":{"kind":"counter","label":"O","max":"9223372036854775808"},"#,
        r#""under":{"kind":"counter","label":"U","min":"-9223372036854775809"},"#,
        r#""plus":{"kind":"counter","label":"P","max":"+5"},"#,
        r#""zeros":{"kind":"counter","label":"Z","max":"007"},"#,
        r#""number":{"kind":"counter","label":"N","max":5}"#)))]);
    assert!(has(&f, "KL350", "/values/over/max"), "{f:?}");
    assert!(has(&f, "KL350", "/values/under/min"), "{f:?}");
    assert!(has(&f, "KL106", "/values/plus/max"), "{f:?}");
    assert!(has(&f, "KL106", "/values/zeros/max"), "{f:?}");
    assert!(has(&f, "KL101", "/values/number/max"), "{f:?}");
    assert!(!f.iter().any(|x| x.2.starts_with("/values/edges")), "{f:?}");
}

#[test]
fn mod_values_bounds_and_initial() {
    let f = mod_values_findings("bounds", &[("mod-values/b.json", values(concat!(
        r#""upside":{"kind":"counter","label":"A","min":"10","max":"5"},"#,
        r#""below-default":{"kind":"counter","label":"B","max":"-1"},"#,
        r#""init-high":{"kind":"counter","label":"C","max":"10","initial":"11"},"#,
        r#""init-low":{"kind":"counter","label":"D","min":"-5","max":"10","initial":"-6"},"#,
        r#""starts-at-min":{"kind":"counter","label":"E","min":"10","max":"20"},"#,
        r#""init-edge":{"kind":"counter","label":"F","min":"-5","max":"10","initial":"10"}"#)))]);
    assert!(has(&f, "KL351", "/values/upside/min"), "{f:?}");
    assert!(has(&f, "KL351", "/values/below-default/max"), "{f:?}");
    assert!(has(&f, "KL351", "/values/init-high/initial"), "{f:?}");
    assert!(has(&f, "KL351", "/values/init-low/initial"), "{f:?}");
    assert!(!f.iter().any(|x| x.2.starts_with("/values/starts-at-min") || x.2.starts_with("/values/init-edge")), "{f:?}");
}

#[test]
fn mod_values_kind_rules() {
    let f = mod_values_findings("kinds", &[("mod-values/k.json", values(concat!(
        r#""xp-no-steps":{"kind":"experience","label":"A"},"#,
        r#""xp-scaled":{"kind":"experience","label":"B","scale":2,"thresholds":["0","10"]},"#,
        r#""xp-negative":{"kind":"experience","label":"C","min":"-1","thresholds":["0"]},"#,
        r#""xp-from-one":{"kind":"experience","label":"D","thresholds":["1","10"]},"#,
        r#""coins-steps":{"kind":"currency","label":"E","thresholds":["0"]},"#,
        r#""coins-negative":{"kind":"currency","label":"F","min":"-10"},"#,
        r#""coins-carried":{"kind":"currency","label":"G","persistence":"carried","trustClientImports":true},"#,
        r#""carried-untrusted":{"kind":"counter","label":"H","persistence":"carried"},"#,
        r#""carried-negative":{"kind":"counter","label":"I","min":"-1","persistence":"carried","trustClientImports":true},"#,
        r#""readers-public":{"kind":"counter","label":"J","readPolicy":"public","readMods":["other-mod"]},"#,
        r#""readers-ok":{"kind":"counter","label":"K","readPolicy":"allowList","readMods":["other-mod"]},"#,
        r#""carried-ok":{"kind":"experience","label":"L","persistence":"carried","trustClientImports":true,"thresholds":["0","5"]},"#,
        r#""negative-counter":{"kind":"counter","label":"M","min":"-100"},"#,
        r#""format-bad":{"kind":"counter","label":"N","numberFormat":"C2"},"#,
        r#""format-ok":{"kind":"counter","label":"O","scale":3,"numberFormat":{"en":"0.000","*":"F3"}},"#,
        r#""typo":{"kind":"counter","label":"P","intial":"1"},"#,
        r#""Bad_Id":{"kind":"counter","label":"Q"}"#)))]);
    let at = |p: &str| f.iter().any(|x| x.3 == Severity::Error && (x.2 == p || x.2.starts_with(&format!("{p}/"))));
    for p in ["/values/xp-no-steps", "/values/xp-scaled/scale", "/values/xp-negative/min", "/values/xp-from-one/thresholds/0",
              "/values/coins-steps/thresholds", "/values/coins-negative/min", "/values/coins-carried/kind", "/values/carried-untrusted",
              "/values/carried-negative/min", "/values/readers-public/readMods", "/values/format-bad/numberFormat", "/values/typo/intial", "/values/Bad_Id"] {
        assert!(at(p), "no error at {p}: {f:?}");
    }
    for p in ["/values/readers-ok", "/values/carried-ok", "/values/negative-counter", "/values/format-ok"] {
        assert!(!at(p), "an error at {p}: {f:?}");
    }
}

/// A carried value takes the higher of the world's value and the copy a player's client brings: with no explicit "max", a
/// client can raise it to the top of the 64-bit range. A warning (KL356), on counters and experience values (the kinds that
/// can be carried) that trust client imports.
#[test]
fn mod_values_carried_without_max_is_a_warning() {
    let f = mod_values_findings("carried-max", &[("mod-values/c.json", values(concat!(
        r#""open":{"kind":"experience","label":"A","persistence":"carried","trustClientImports":true,"thresholds":["0","10"]},"#,
        r#""open-counter":{"kind":"counter","label":"B","persistence":"carried","trustClientImports":true},"#,
        r#""capped":{"kind":"experience","label":"C","persistence":"carried","trustClientImports":true,"thresholds":["0","10"],"max":"1000000"},"#,
        r#""capped-at-top":{"kind":"counter","label":"D","persistence":"carried","trustClientImports":true,"max":"9223372036854775807"},"#,
        r#""world":{"kind":"counter","label":"E"},"#,
        r#""world-trusting":{"kind":"counter","label":"F","trustClientImports":true},"#,
        r#""untrusted":{"kind":"counter","label":"G","persistence":"carried"}"#)))]);
    let warned = |p: &str| f.iter().any(|x| x.1 == "KL356" && x.2 == p && x.3 == Severity::Warning);
    assert!(warned("/values/open"), "{f:?}");
    assert!(warned("/values/open-counter"), "{f:?}");
    assert_eq!(f.iter().filter(|x| x.1 == "KL356").count(), 2, "an explicit max (any), a World value or an untrusted one: no KL356: {f:?}");
    assert!(!f.iter().any(|x| x.2.starts_with("/values/capped") || x.2.starts_with("/values/world")), "{f:?}");
}

#[test]
fn mod_values_thresholds_increase_as_whole_numbers() {
    let f = mod_values_findings("steps", &[("mod-values/t.json", values(concat!(
        r#""flat":{"kind":"experience","label":"A","thresholds":["0","100","100"]},"#,
        r#""down":{"kind":"experience","label":"B","thresholds":["0","900","400","1000"]},"#,
        r#""text-order":{"kind":"experience","label":"C","thresholds":["0","9","10"]},"#,
        r#""past-double":{"kind":"experience","label":"D","thresholds":["0","9007199254740992","9007199254740993"]}"#)))]);
    assert!(has(&f, "KL352", "/values/flat/thresholds/2"), "{f:?}");
    assert!(has(&f, "KL352", "/values/down/thresholds/2"), "{f:?}");
    assert!(!f.iter().any(|x| x.2.contains("/values/down/thresholds/3")), "{f:?}");
    assert!(!f.iter().any(|x| x.2.starts_with("/values/text-order") || x.2.starts_with("/values/past-double")), "{f:?}");
}

#[test]
fn mod_values_size_limits() {
    let long = "a".repeat(1100);
    let fits = "a".repeat(1000); // with its quotes 1002 bytes
    let formats: Vec<String> = (0..30).map(|i| format!(r#""l{i:02}":"N0""#)).collect();
    let steps: Vec<String> = (0..256).map(|i| format!(r#""{}""#, i as i64 * 1_000_000_000_000)).collect();
    let f = mod_values_findings("sizes", &[("mod-values/s.json", values(&format!(
        r#""long":{{"kind":"counter","label":"{long}"}},"fits":{{"kind":"counter","label":"{fits}"}},"formats":{{"kind":"counter","label":"F","numberFormat":{{{}}}}},"big":{{"kind":"experience","label":"B","thresholds":[{}]}}"#,
        formats.join(","), steps.join(","))))]);
    assert!(has(&f, "KL353", "/values/long/label"), "{f:?}");
    assert!(has(&f, "KL353", "/values/formats/numberFormat"), "{f:?}");
    assert!(has(&f, "KL353", "/values/big"), "{f:?}");
    assert!(!f.iter().any(|x| x.2.starts_with("/values/fits")), "{f:?}");
}

#[test]
fn mod_values_ids_across_files() {
    let a: Vec<String> = (0..100).map(|i| format!(r#""a{i}":{{"kind":"counter","label":"A"}}"#)).collect();
    let b: Vec<String> = (0..40).map(|i| format!(r#""b{i}":{{"kind":"counter","label":"B"}}"#)).collect();
    let f = mod_values_findings("ids", &[
        ("mod-values/a.json", values(&format!(r#"{},"shared":{{"kind":"counter","label":"S"}}"#, a.join(",")))),
        ("mod-values/b.json", values(&format!(r#"{},"shared":{{"kind":"counter","label":"S"}}"#, b.join(",")))),
    ]);
    assert!(f.iter().any(|x| x.0 == "b.json" && x.1 == "KL354" && x.2 == "/values/shared"), "{f:?}");
    assert!(!f.iter().any(|x| x.0 == "a.json" && x.1 == "KL354"), "{f:?}");
    // 101 in a.json, then b0..b26 are values 102..128: b27 is the 129th
    assert!(f.iter().any(|x| x.0 == "b.json" && x.1 == "KL355" && x.2 == "/values/b27"), "{f:?}");
    assert_eq!(f.iter().filter(|x| x.1 == "KL355").count(), 1, "{f:?}");
    // one file over 128 is the schema's (KL108), not said twice
    let many: Vec<String> = (0..129).map(|i| format!(r#""v{i}":{{"kind":"counter","label":"V"}}"#)).collect();
    let g = mod_values_findings("many", &[("mod-values/m.json", values(&many.join(",")))]);
    assert!(g.iter().any(|x| x.1 == "KL108" && x.2 == "/values"), "{g:?}");
    assert!(!g.iter().any(|x| x.1 == "KL355"), "{g:?}");
}

#[test]
fn mod_values_dialect_is_strict() {
    let def = r#""a":{"kind":"counter","label":"A"}"#;
    let f = mod_values_findings("dialect", &[
        ("mod-values/twice.json", format!(r#"{{"format":1,"values":{{{def},{def}}}}}"#)),
        ("mod-values/comment.json", format!("// note
{}", values(r#""b":{"kind":"counter","label":"B"}"#))),
        ("mod-values/comma.json", r#"{"format":1,"values":{"c":{"kind":"counter","label":"C",}}}"#.to_string()),
        ("mod-values/v2.json", r#"{"format":2,"values":{}}"#.to_string()),
    ]);
    assert!(f.iter().any(|x| x.0 == "twice.json" && x.1 == "KL004" && x.3 == Severity::Error), "{f:?}");
    assert!(!f.iter().any(|x| x.1 == "KL354"), "a key twice in one file is KL004 only: {f:?}");
    assert!(f.iter().any(|x| x.0 == "comment.json" && x.1 == "KL002"), "{f:?}");
    assert!(f.iter().any(|x| x.0 == "comma.json" && x.1 == "KL003"), "{f:?}");
    assert!(f.iter().any(|x| x.0 == "v2.json" && x.1 == "KL111"), "{f:?}");
}

#[test]
fn program_access_names_match_native_120_byte_limit() {
    for (length, valid) in [(120, true), (121, false)] {
        let text = serde_json::json!({"format":1,"programs":{"pulse":{"otherRead":["a".repeat(length)]}}}).to_string();
        let root = json::parse("kiln.programs.json", &text, 0, json::Dialect::STRICT).root.unwrap();
        assert_eq!(embedded().schemas.validate("kiln-programs.schema.json", &root).issues.is_empty(), valid);
    }
}

#[test]
fn program_world_is_one_of_the_four_kiln_world_reads() {
    // "world" (0.2.0-dev.29, native/services/world/boot_programs.h): absent means "server", as before
    for (world, valid) in [(None, true), (Some("server"), true), (Some("client"), true), (Some("both"), true),
                           (Some("prediction"), true), (Some("Client"), false), (Some("mars"), false)] {
        let mut entry = serde_json::json!({"otherRead":["Health"]});
        if let Some(w) = world { entry["world"] = serde_json::json!(w); }
        let text = serde_json::json!({"format":1,"programs":{"pulse":entry}}).to_string();
        let root = json::parse("kiln.programs.json", &text, 0, json::Dialect::STRICT).root.unwrap();
        assert_eq!(embedded().schemas.validate("kiln-programs.schema.json", &root).issues.is_empty(), valid, "{world:?}");
    }
}

#[test]
fn block_png_paths_refuse_links_and_windows_aliases_before_opening() {
    let dir = std::env::temp_dir().join(format!("kiln-lint-icon-{}", std::process::id()));
    let root = dir.join("mod");
    std::fs::create_dir_all(root.join("blocks")).unwrap();
    std::fs::create_dir_all(root.join("icons")).unwrap();
    std::fs::write(root.join("kiln.mod.json"), r#"{"format":1,"id":"icon-test","name":"Icon test","version":"1.0.0","clients":"mod","side":"both"}"#).unwrap();
    let png = b"\x89PNG\r\n\x1a\n";
    std::fs::write(root.join("icons/ok.png"), png).unwrap();
    std::fs::write(dir.join("outside.png"), png).unwrap();
    #[cfg(unix)]
    {
        std::os::unix::fs::symlink(dir.join("outside.png"), root.join("icons/link.png")).unwrap();
        std::os::unix::fs::symlink(&dir, root.join("icons/out")).unwrap();
        for part in [".. ", "icons.", "icons "] {
            std::fs::create_dir_all(root.join(part)).unwrap();
            std::fs::write(root.join(part).join("a.png"), png).unwrap();
        }
        std::fs::write(root.join("icons/stream:part.png"), png).unwrap();
    }
    let mut results = Vec::new();
    for (icon, valid) in [("icons/ok.png", true), ("icons\\ok.png", true), ("Icons/OK.PNG", true), ("./icons/ok.png", true), ("icons/link.png", false),
        ("icons/out/outside.png", false), (".. /a.png", false), ("icons./a.png", false),
        ("icons /a.png", false), ("icons/stream:part.png", false)] {
        let text = serde_json::json!({"format":1,"items":{"gem":{"name":"Gem","visual":{"icon":icon}}}}).to_string();
        std::fs::write(root.join("blocks/icons.json"), &text).unwrap();
        let r = lint_paths(&[root.join("blocks/icons.json")], &LintOptions { no_catalog: true, ..Default::default() });
        let errors: Vec<_> = r.diagnostics.iter().filter(|d| d.severity == Severity::Error && d.pointer.as_deref() == Some("/items/gem/visual/icon")).collect();
        results.push((icon.to_string(), valid, errors.is_empty(), r.to_text()));
    }
    std::fs::remove_dir_all(&dir).unwrap();
    for (icon, expected, actual, report) in results {
        assert_eq!(actual, expected, "icon {icon}: {report}");
    }
}

/// A mod folder (clients "mod") with one blocks file and other files, linted whole: (code, pointer, severity) of each finding
/// at warning or above.
fn blocks_findings(name: &str, blocks: &str, files: &[(&str, &[u8])]) -> Vec<(String, String, Severity)> {
    let dir = std::env::temp_dir().join(format!("kiln-lint-ui-{name}-{}", std::process::id()));
    let _ = std::fs::remove_dir_all(&dir);
    std::fs::create_dir_all(dir.join("blocks")).unwrap();
    std::fs::write(dir.join("kiln.mod.json"), r#"{"id":"ui-test","name":"UI test","version":"1.0.0","apiVersion":"0.2","side":"both","clients":"mod","license":"MIT"}"#).unwrap();
    std::fs::write(dir.join("blocks/ui.json"), blocks).unwrap();
    for (f, bytes) in files {
        std::fs::create_dir_all(dir.join(f).parent().unwrap()).unwrap();
        std::fs::write(dir.join(f), bytes).unwrap();
    }
    let r = lint_paths(std::slice::from_ref(&dir), &LintOptions { no_catalog: true, ..Default::default() });
    let out = r.diagnostics.iter().filter(|d| d.severity >= Severity::Warning).map(|d| (d.code.clone(), d.pointer.clone().unwrap_or_default(), d.severity)).collect();
    std::fs::remove_dir_all(&dir).unwrap();
    out
}

/// A 1x1 PNG's signature and header chunk (what lint and Kiln.Blocks read before anything is decoded).
const PNG_1X1: &[u8] = b"\x89PNG\r\n\x1a\n\0\0\0\x0dIHDR\0\0\0\x01\0\0\0\x01\x08\x06\0\0\0\x1f\x15\xc4\x89";

fn found(f: &[(String, String, Severity)], code: &str, pointer: &str) -> bool {
    f.iter().any(|(c, p, s)| c == code && p == pointer && *s == Severity::Error)
}

/// Kiln.Blocks refuses a window whose rows share an id (nested rows of tabs and trees included); lint says it first. Two
/// windows may use the same ids, and tab ids are the tabs row's own.
#[test]
fn ui_row_ids_are_unique_in_a_window() {
    let f = blocks_findings("ids", r#"{"format":1,"windows":{
      "a":{"title":"A","state":{"note":{"type":"string"},"open":{"type":"stringSet"}},"rows":[
        {"type":"input","id":"note","label":"Note","bind":"state.note"},
        {"type":"tabs","id":"t","tabs":[{"id":"note","label":"One","rows":[{"type":"image","id":"note","asset":"pic.png"}]}]},
        {"type":"tree","id":"places","nodes":"mod.places","expanded":"state.open","nodeRows":[{"type":"image","id":"t","asset":"pic.png"}]}]},
      "b":{"title":"B","state":{"note":{"type":"string"}},"rows":[{"type":"input","id":"note","label":"Note","bind":"state.note"}]}}}"#,
        &[("pic.png", PNG_1X1)]);
    assert!(found(&f, "KL360", "/windows/a/rows/1/tabs/0/rows/0/id"), "{f:?}");
    assert!(found(&f, "KL360", "/windows/a/rows/2/nodeRows/0/id"), "{f:?}");
    assert_eq!(f.iter().filter(|x| x.0 == "KL360").count(), 2, "a tab's id is not a row's, window b is its own: {f:?}");
}

/// A text field takes the keyboard: only an interactive window or a screen has it (Kiln.Blocks refuses the window otherwise).
#[test]
fn ui_text_field_needs_the_keyboard() {
    let f = blocks_findings("keys", r#"{"format":1,"windows":{
      "panel":{"title":"P","interactive":false,"state":{"n":{"type":"string"}},"rows":[{"type":"input","id":"n","label":"N","bind":"state.n"}]},
      "screen":{"title":"S","kind":"screen","state":{"n":{"type":"string"}},"rows":[{"type":"tabs","id":"t","tabs":[{"id":"a","label":"A","rows":[{"type":"input","id":"n","label":"N","bind":"state.n"}]}]}]},
      "win":{"title":"W","state":{"n":{"type":"string"}},"rows":[{"type":"input","id":"n","label":"N","bind":"state.n"}]}}}"#, &[]);
    assert!(found(&f, "KL360", "/windows/panel/rows/0"), "{f:?}");
    assert_eq!(f.iter().filter(|x| x.0 == "KL360").count(), 1, "a screen and an interactive window take the keyboard: {f:?}");
}

/// An image row's asset is a PNG of the mod's folder: lint checks the path, that the file is there and that it is a PNG.
#[test]
fn ui_image_asset_is_a_png_of_the_mod() {
    let row = |id: &str, asset: &str| format!(r#"{{"type":"image","id":"{id}","asset":"{asset}"}}"#);
    let blocks = format!(r#"{{"format":1,"windows":{{"w":{{"title":"W","rows":[{},{},{},{}]}}}}}}"#,
        row("up", "../pic.png"), row("gone", "missing.png"), row("text", "fake.png"), row("ok", "ui/pic.png"));
    let f = blocks_findings("images", &blocks, &[("ui/pic.png", PNG_1X1), ("fake.png", b"not a png at all, just text")]);
    assert!(found(&f, "KL106", "/windows/w/rows/0/asset"), "{f:?}");
    assert!(found(&f, "KL501", "/windows/w/rows/1/asset"), "{f:?}");
    assert!(found(&f, "KL503", "/windows/w/rows/2/asset"), "{f:?}");
    assert!(!f.iter().any(|x| x.1.starts_with("/windows/w/rows/3")), "{f:?}");
}

/// statImports is a list of "mod:id" names beside the sections, no block kind.
#[test]
fn ui_stat_imports_are_mod_id_names() {
    let f = blocks_findings("stats", r#"{"format":1,"statImports":["fishing:catches","bad"],"windows":{"w":{"title":"W","rows":[{"type":"text","text":"x"}]}}}"#, &[]);
    assert!(found(&f, "KL106", "/statImports/1"), "{f:?}");
    assert!(!f.iter().any(|x| x.1 == "/statImports/0" || x.1 == "/statImports"), "{f:?}");
}

#[test]
fn line_and_column() {
    let t = "ab\ncé d".as_bytes();
    assert_eq!(line_col(t, 0), (1, 1));
    assert_eq!(line_col(t, 3), (2, 1));
    assert_eq!(line_col(t, 6), (2, 3)); // after "cé" (é is 2 bytes)
}

#[test]
fn registry_only_clients_guidance_tracks_the_edited_fields() {
    fn check(name: &str, clients: &str, registry: &str, field: &str, want: Option<&str>) {
        let dir = std::env::temp_dir().join(format!("kiln-lint-registry-{name}-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&dir);
        std::fs::create_dir_all(dir.join("content")).unwrap();
        std::fs::write(dir.join("kiln.mod.json"), format!(r#"{{"id":"registry-test","name":"Test","version":"1.0.0","apiVersion":"0.2","side":"both","clients":"{clients}","license":"MIT"}}"#)).unwrap();
        let value = if field == "unlock" { "\"always\"" } else { "1" };
        std::fs::write(dir.join("content/a.json"), format!(r#"{{"format":1,"registry":{{"alter":[{{"registry":"{registry}","select":{{"id":"*"}},"field":"{field}","op":{value}}}]}}}}"#)).unwrap();
        let r = lint_paths(&[dir.clone()], &LintOptions { no_catalog: true, ..Default::default() });
        let ds: Vec<_> = r.diagnostics.iter().filter(|d| d.code == "KL213").collect();
        if let Some(value) = want {
            assert_eq!(ds.len(), 1, "{name}: {:?}", r.diagnostics);
            assert!(ds[0].message.contains(&format!("\"clients\": \"{value}\"")), "{name}: {:?}", ds);
        } else { assert!(ds.is_empty(), "{name}: {ds:?}"); }
        std::fs::remove_dir_all(dir).unwrap();
    }
    check("loot", "kiln", "loot", "stack.max", Some("none"));
    check("drops", "kiln", "drops", "entries[2].chance", Some("none"));
    check("item", "none", "items", "maxStack", Some("kiln"));
    check("recipe", "none", "recipes", "duration", Some("kiln"));
    check("balance", "kiln", "balance", "levelCap", Some("mod"));
    check("indexed-balance", "kiln", "balance", "xpPerLevel[2]", Some("mod"));
    check("server-item", "kiln", "items", "socket", Some("none"));
    // stamina costs: the server pays them, a client's tooltip and prediction read its own copy (as for mana costs)
    check("stamina-item", "none", "items", "staminaCost", Some("kiln"));
    check("good-stamina-item", "kiln", "items", "staminaCost", None);
    check("client-recipe", "kiln", "recipes", "unlock", Some("mod"));
    check("unproven-item", "kiln", "items", "comfort", None);
    check("good-loot", "none", "loot", "stack.max", None);
    check("good-item", "kiln", "items", "maxStack", None);
    check("good-balance", "mod", "balance", "levelCap", None);
    // dev.32's world rules and limits: restart fields both sides bake (the join compares them)
    check("difficulty", "kiln", "difficulty", "enemyDamageFactor.max", Some("mod"));
    check("world-corner", "none", "world", "noBuildZones[0].min.x", Some("mod"));
    check("limits", "kiln", "limits", "maxAltars", Some("mod"));
    check("good-limits", "mod", "limits", "giftingRange", None);
    check("unknown", "kiln", "unknown", "x", None);
}

#[test]
fn registry_clients_does_not_guess_for_code_mixed_or_raw_packs() {
    use serde_json::json;
    let loot = json!({"registry":"loot","select":{"id":"*"},"field":"stack.max","op":2});
    for (name, code, mixed, edits, expected) in [
        ("code", true, false, vec![loot.clone()], None),
        ("mixed", false, true, vec![loot.clone()], None),
        ("raw", false, false, vec![json!({"registry":"balance","select":{"id":"game:balance"},"raw":{"unknown":1}})], None),
        ("strictest", false, false, vec![loot.clone(),json!({"registry":"balance","select":{"id":"game:balance"},"field":"levelCap","op":60})], Some("mod")),
        ("wrong-side", false, false, vec![json!({"registry":"balance","select":{"id":"game:balance"},"field":"levelCap","op":60})], Some("mod")),
        ("loot-remove", false, false, vec![json!({"registry":"loot","select":{"id":"*"},"entry":"remove"})], Some("none")),
        ("unknown-override", false, false, vec![json!({"registry":"items","entry":"add","id":"registry-test:token","like":"game:x","fields":{"comfort":2}})], None),
    ] {
        let dir = std::env::temp_dir().join(format!("kiln-lint-registry-caution-{name}-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&dir);
        std::fs::create_dir_all(dir.join("content")).unwrap();
        let mut manifest = json!({"id":"registry-test","name":"Test","version":"1.0.0","apiVersion":"0.2","side":"both","clients":"kiln","license":"MIT"});
        if name == "wrong-side" { manifest["side"] = json!("server"); manifest["clients"] = json!("mod"); }
        if code { manifest["entryAssembly"] = json!("Test.dll"); std::fs::write(dir.join("Test.dll"), []).unwrap(); }
        std::fs::write(dir.join("kiln.mod.json"),manifest.to_string()).unwrap();
        let mut content = json!({"format":1,"registry":{"alter":edits}});
        if mixed { content["items"] = json!({"*":{"maxStackSize":20}}); }
        std::fs::write(dir.join("content/a.json"),content.to_string()).unwrap();
        let r = lint_paths(&[dir.clone()], &LintOptions { no_catalog: true, ..Default::default() });
        let found: Vec<_> = r.diagnostics.iter().filter(|d| d.code == "KL213").collect();
        if let Some(value) = expected {
            assert_eq!(found.len(),1,"{name}: {found:?}");
            assert!(found[0].message.contains(&format!("\"clients\": \"{value}\"")),"{name}: {found:?}");
        } else { assert!(found.is_empty(),"{name}: {found:?}"); }
        std::fs::remove_dir_all(dir).unwrap();
    }
}
