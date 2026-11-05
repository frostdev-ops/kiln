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
    assert_eq!(k("mods/x/content/a.json").as_deref(), Some("content"));
    assert_eq!(k("mods/x/assets/assets.json").as_deref(), Some("assets"));
    assert_eq!(k("mods/x/assets/cook.json").as_deref(), Some("cook"));
    assert_eq!(k("mods/x/impact/cook.json").as_deref(), Some("cook"));
    assert_eq!(k("mods/x/impact/natives.json").as_deref(), Some("natives"));
    assert_eq!(k("mods/x/native.json").as_deref(), Some("native"));
    assert_eq!(k("game/kiln/config/native.json"), None);
    assert_eq!(k("hut.kschem").as_deref(), Some("schematic"));
}

#[test]
fn line_and_column() {
    let t = "ab\ncé d".as_bytes();
    assert_eq!(line_col(t, 0), (1, 1));
    assert_eq!(line_col(t, 3), (2, 1));
    assert_eq!(line_col(t, 6), (2, 3)); // after "cé" (é is 2 bytes)
}
