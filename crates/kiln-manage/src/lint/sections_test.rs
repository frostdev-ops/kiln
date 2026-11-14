// SPDX-License-Identifier: MIT
//! rules.rs's lists of blocks/*.json sections against schema/kiln-blocks.schema.json. The lists stay inline in rules.rs (so a new section is a
//! one-line edit there); this reads them from its source.

use std::collections::BTreeSet;

const RULES: &str = include_str!("rules.rs");
const SCHEMA: &str = include_str!("../../../../schema/kiln-blocks.schema.json");

/// Top-level schema properties that are no section of blocks. `resourcePatches` (08-onboot S10) is read by kiln.boot's patches
/// collector, not by Kiln.Blocks, and its names do not share the blocks' name space, so the duplicate-name rule must not see it.
const NOT_SECTIONS: &[&str] = &["$schema", "format", "comment", "resourcePatches"];

fn schema_sections() -> BTreeSet<String> {
    let v: serde_json::Value = serde_json::from_str(SCHEMA).expect("kiln-blocks.schema.json parses");
    v["properties"].as_object().expect("properties").keys().filter(|k| !NOT_SECTIONS.contains(&k.as_str())).cloned().collect()
}

/// The first string of every `("key", ...)` tuple between `start` and the next `end`.
fn list(start: &str, end: &str) -> Vec<String> {
    let at = RULES.find(start).unwrap_or_else(|| panic!("rules.rs no longer has `{start}`: update sections_test.rs"));
    let text = &RULES[at + start.len()..];
    let text = &text[..text.find(end).expect("end of the list")];
    text.split("(\"").skip(1).map(|t| t[..t.find('"').expect("closing quote")].to_string()).collect()
}

#[test]
fn block_sections_are_the_schemas() {
    let lint = list("const SECTIONS: &[(&str, &str)] = &[", "];");
    let set: BTreeSet<String> = lint.iter().cloned().collect();
    assert_eq!(set.len(), lint.len(), "rules.rs SECTIONS names a section twice");
    let schema = schema_sections();
    let only_lint: Vec<_> = set.difference(&schema).collect();
    let only_schema: Vec<_> = schema.difference(&set).collect();
    assert!(only_lint.is_empty() && only_schema.is_empty(), "rules.rs SECTIONS vs schema: only in lint {only_lint:?}, only in the schema {only_schema:?}");
}

#[test]
fn new_definition_sections_are_schema_sections() {
    let lint = list("for (key, what) in [", "] {");
    assert!(lint.len() > 5, "the \"a new spell\" list looks empty: {lint:?}");
    let schema = schema_sections();
    let unknown: Vec<_> = lint.iter().filter(|s| !schema.contains(*s)).collect();
    assert!(unknown.is_empty(), "rules.rs's \"a new ...\" list names sections the schema does not have: {unknown:?}");
}
