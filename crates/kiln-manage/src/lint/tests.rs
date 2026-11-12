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

#[test]
fn line_and_column() {
    let t = "ab\ncé d".as_bytes();
    assert_eq!(line_col(t, 0), (1, 1));
    assert_eq!(line_col(t, 3), (2, 1));
    assert_eq!(line_col(t, 6), (2, 3)); // after "cé" (é is 2 bytes)
}
