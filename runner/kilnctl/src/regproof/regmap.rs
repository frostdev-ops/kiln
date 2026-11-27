// SPDX-License-Identifier: MIT
//! Kiln's regmap rows as the registry proof reads them: `regmap/common/<registry>.json` of a Kiln tree (the format is
//! regmap/README.md's; tools/registrygen/regmap_check.py checks it against the schema). Only what the proof needs: where a
//! registry's entries come from, and per Kiln field the game's member paths, the conversion, the back-end and the state.
use anyhow::{Context, Result};
use serde::Deserialize;
use std::collections::BTreeMap;
use std::path::Path;

/// Where a registry's entries come from (`entries` of a common file).
#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Entries {
    /// resource | array | single
    #[serde(default)]
    pub kind: String,
    #[serde(default)]
    pub resource_type: String,
    /// kind array: the member holding the entries
    #[serde(default)]
    pub array: String,
    #[serde(default)]
    pub name: String,
    #[serde(default)]
    pub guid: String,
    #[serde(default)]
    pub id: String,
    /// drop tables: "<component type>.<member>" (the templates that use the entry name it)
    #[serde(default)]
    pub name_from: String,
    /// creatures: the entries are the templates holding a component of one of these types (by type name without namespaces)
    #[serde(default)]
    pub with_component: Vec<String>,
    /// stations: with key "items", the member whose 32-bit item id names the entry (the item that builds it)
    #[serde(default)]
    pub key_member: String,
}

/// One Kiln field's row.
#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Row {
    #[serde(default)]
    pub paths: Vec<String>,
    #[serde(default)]
    pub conv: String,
    /// live | boot | none
    #[serde(default)]
    pub backend: String,
    #[serde(default)]
    pub formula: Option<String>,
    /// enum, flags, optionalEnum: Kiln name -> the game's name
    #[serde(default)]
    pub values: BTreeMap<String, String>,
    /// mapped (default) | unproven | unmapped
    #[serde(default)]
    pub state: Option<String>,
    /// why an unproven or unmapped row is not mapped, in Kiln words (players read it)
    #[serde(default)]
    pub reason: Option<String>,
}

impl Row {
    /// The row maps the field onto members (mapped or unproven); an unmapped row has nothing to prove.
    pub fn has_mapping(&self) -> bool {
        self.state.as_deref() != Some("unmapped") && !self.paths.is_empty()
    }

    /// A write goes through a back-end (live: kiln.content's store at world load; boot: the boot generation).
    pub fn writable(&self) -> bool {
        self.backend == "live" || self.backend == "boot"
    }
}

/// One `regmap/common/<registry>.json`.
#[derive(Debug, Clone, Default, Deserialize)]
pub struct RegFile {
    #[serde(default)]
    pub registry: String,
    #[serde(default)]
    pub entries: Entries,
    #[serde(default)]
    pub rows: BTreeMap<String, Row>,
}

/// Every registry by name.
pub type RegMap = BTreeMap<String, RegFile>;

/// The list a field of list elements belongs to: `inputs[].count` -> `inputs`.
pub fn list_of(field: &str) -> Option<&str> {
    field.find("[]").map(|i| &field[..i])
}

pub fn parse(text: &str, fallback_name: &str) -> Result<RegFile> {
    let mut f: RegFile = serde_json::from_str(text)?;
    if f.registry.is_empty() {
        f.registry = fallback_name.to_string();
    }
    Ok(f)
}

/// The JSON files of `<tree or kiln folder>/regmap/common`, in name order.
fn common_files(root: &Path) -> Result<Vec<std::path::PathBuf>> {
    let dir = root.join("regmap/common");
    let mut files: Vec<_> = std::fs::read_dir(&dir)
        .with_context(|| format!("reading {}", dir.display()))?
        .flatten()
        .map(|e| e.path())
        // regular files only (a link followed), as kiln.content's load_dir (review3 N5)
        .filter(|p| p.extension().is_some_and(|x| x == "json") && p.is_file())
        .collect();
    files.sort();
    Ok(files)
}

/// `regmapDigest` of the proof's lab files: the mapping a run used, `<root>/regmap/common/*.json` (root: a Kiln tree or a
/// game's Kiln folder) as sha256 over "<file name>\n<sha256 of its bytes>\n" per file in name order. The build records
/// are left out: they are what the proof writes.
pub fn digest(root: &Path) -> Result<String> {
    let mut text = String::new();
    for f in common_files(root)? {
        let bytes = std::fs::read(&f).with_context(|| format!("reading {}", f.display()))?;
        let name = f.file_name().map(|n| n.to_string_lossy().into_owned()).unwrap_or_default();
        text.push_str(&format!("{name}\n{}\n", kiln_manage::hash::sha256_hex(&bytes)));
    }
    Ok(kiln_manage::hash::sha256_hex(text.as_bytes()))
}

/// How two regmap/common folders differ (file names only in one, files whose bytes differ); empty when equal.
pub fn differences(a: &Path, b: &Path) -> Result<Vec<String>> {
    let names = |root: &Path| -> Result<BTreeMap<String, std::path::PathBuf>> {
        Ok(common_files(root)?.into_iter().map(|p| (p.file_name().map(|n| n.to_string_lossy().into_owned()).unwrap_or_default(), p)).collect())
    };
    let (x, y) = (names(a)?, names(b)?);
    let mut out = Vec::new();
    for n in x.keys().chain(y.keys()).collect::<std::collections::BTreeSet<_>>() {
        match (x.get(n), y.get(n)) {
            (Some(_), None) => out.push(format!("{n} (only in the first)")),
            (None, Some(_)) => out.push(format!("{n} (only in the second)")),
            (Some(p), Some(q)) if std::fs::read(p)? != std::fs::read(q)? => out.push(format!("{n} (different bytes)")),
            _ => {}
        }
    }
    Ok(out)
}

/// `<tree>/regmap/common/*.json`, by registry.
pub fn load(tree: &Path) -> Result<RegMap> {
    let files = common_files(tree)?;
    let mut out = RegMap::new();
    for p in files {
        let stem = p.file_stem().map(|s| s.to_string_lossy().into_owned()).unwrap_or_default();
        let text = std::fs::read_to_string(&p).with_context(|| format!("reading {}", p.display()))?;
        let f = parse(&text, &stem).with_context(|| format!("{} is not a regmap file", p.display()))?;
        out.insert(f.registry.clone(), f);
    }
    Ok(out)
}

/// Every `<registry>.<field>` with its row, in name order.
pub fn qualified(map: &RegMap) -> Vec<(String, &RegFile, &str, &Row)> {
    let mut v = Vec::new();
    for (name, f) in map {
        for (field, row) in &f.rows {
            v.push((format!("{name}.{field}"), f, field.as_str(), row));
        }
    }
    v
}

#[cfg(test)]
pub(crate) mod tests {
    use super::*;

    /// The regmapDigest the synthetic lab files carry (the proof's mapping in unit tests).
    pub(crate) const DIGEST: &str = "d1d1d1d1d1d1d1d1d1d1d1d1d1d1d1d1d1d1d1d1d1d1d1d1d1d1d1d1d1d1d1d1";

    #[test]
    fn the_digest_and_the_differences_cover_the_common_files_only() {
        let root = std::env::temp_dir().join(format!("kilnctl-regmap-digest-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&root);
        let (a, b) = (root.join("tree"), root.join("game/kiln"));
        for d in [&a, &b] {
            std::fs::create_dir_all(d.join("regmap/common")).unwrap();
            std::fs::create_dir_all(d.join("regmap/server")).unwrap();
            std::fs::write(d.join("regmap/common/items.json"), "{\"rows\": {}}").unwrap();
            std::fs::write(d.join("regmap/common/loot.json"), "{}").unwrap();
        }
        std::fs::write(a.join("regmap/server/x.json"), "a record").unwrap();
        let want = kiln_manage::hash::sha256_hex(format!("items.json\n{}\nloot.json\n{}\n",
            kiln_manage::hash::sha256_hex(b"{\"rows\": {}}"), kiln_manage::hash::sha256_hex(b"{}")).as_bytes());
        // review3 N5: regular files only, as kiln.content (a folder named like a file is not part of it)
        std::fs::create_dir_all(a.join("regmap/common/folder.json")).unwrap();
        assert_eq!(digest(&a).unwrap(), want, "records are not part of it");
        assert_eq!(digest(&b).unwrap(), want);
        assert!(differences(&a, &b).unwrap().is_empty());
        std::fs::write(b.join("regmap/common/loot.json"), "{ }").unwrap();
        std::fs::write(b.join("regmap/common/drops.json"), "{}").unwrap();
        assert_ne!(digest(&b).unwrap(), want);
        assert_eq!(differences(&a, &b).unwrap(), ["drops.json (only in the second)", "loot.json (different bytes)"]);
        let _ = std::fs::remove_dir_all(&root);
    }

    /// A synthetic regmap (invented types and members, the shapes of the real files): a resource registry with a
    /// scalar, an enum, a formula and an unmapped row, and an array registry with a list and a field of its elements.
    pub(crate) fn sample() -> RegMap {
        let gadgets = r#"{"regmap": 1, "registry": "gadgets",
            "entries": {"kind": "resource", "resourceType": "demo::Gadget", "name": "label", "guid": "uid", "id": "num"},
            "rows": {
              "size":  {"paths": ["stackLimit"], "conv": "int", "read": "now", "backend": "live"},
              "tier":  {"paths": ["grade"], "conv": "enum", "read": "now", "backend": "live",
                        "values": {"low": "Low", "high": "High"}},
              "power": {"paths": ["hit.mod", "hit.pace", "range.min"], "conv": "formula", "formula": "gear.power",
                        "read": "now", "backend": "live"},
              "label": {"paths": ["hit.isSet"], "conv": "bool", "read": "now", "backend": "none"},
              "glow":  {"state": "unmapped", "reason": "not found yet"}}}"#;
        let plans = r#"{"regmap": 1, "registry": "plans",
            "entries": {"kind": "array", "resourceType": "demo::PlanBook", "array": "plans", "id": "planId"},
            "rows": {
              "parts":         {"paths": ["parts"], "conv": "list", "read": "now", "backend": "live"},
              "parts[].count": {"paths": ["amount"], "conv": "int", "read": "now", "backend": "live"},
              "cap":           {"paths": ["limit"], "conv": "int", "read": "restart", "backend": "boot"}}}"#;
        let mut m = RegMap::new();
        m.insert("gadgets".into(), parse(gadgets, "gadgets").unwrap());
        m.insert("plans".into(), parse(plans, "plans").unwrap());
        m
    }

    #[test]
    fn reads_rows_and_entries() {
        let m = sample();
        let g = &m["gadgets"];
        assert_eq!(g.entries.resource_type, "demo::Gadget");
        assert_eq!(g.rows["power"].formula.as_deref(), Some("gear.power"));
        assert_eq!(g.rows["tier"].values["high"], "High");
        assert!(g.rows["size"].writable() && !g.rows["label"].writable());
        assert!(!g.rows["glow"].has_mapping());
        assert_eq!(m["plans"].entries.array, "plans");
        assert_eq!(list_of("parts[].count"), Some("parts"));
        assert_eq!(list_of("cap"), None);
        let q: Vec<String> = qualified(&m).into_iter().map(|(q, ..)| q).collect();
        assert_eq!(q.first().map(String::as_str), Some("gadgets.glow"));
        assert_eq!(q.len(), 8);
    }

    #[test]
    fn loads_a_tree_and_names_a_file_by_its_stem() {
        let root = std::env::temp_dir().join(format!("kilnctl-regmap-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&root);
        std::fs::create_dir_all(root.join("regmap/common")).unwrap();
        std::fs::write(root.join("regmap/common/things.json"), r#"{"regmap": 1, "rows": {"a": {"paths": ["x"], "conv": "int"}}}"#).unwrap();
        std::fs::write(root.join("regmap/common/notes.txt"), "not json").unwrap();
        let m = load(&root).unwrap();
        assert_eq!(m.keys().collect::<Vec<_>>(), ["things"]);
        assert_eq!(m["things"].rows["a"].paths, ["x"]);
        std::fs::write(root.join("regmap/common/bad.json"), "{").unwrap();
        assert!(load(&root).is_err());
        let _ = std::fs::remove_dir_all(&root);
    }
}
