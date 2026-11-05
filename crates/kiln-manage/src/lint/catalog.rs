//! The local game-data catalog (written by `kiln catalog`; format: kind "kiln.catalog", format 1):
//! names, ids and GUIDs of the game's items, recipes, templates, ... and optionally field paths per section. The file
//! is the contract; this reader takes only what lint needs and ignores everything else.

use super::{LintOptions, suggest};
use serde_json::Value as J;
use std::collections::{HashMap, HashSet};
use std::path::{Path, PathBuf};

#[derive(Debug, Default)]
pub struct Section {
    pub names: HashSet<String>,
    pub names_list: Vec<String>,
    pub lower: HashMap<String, String>,
    pub ids: HashSet<u32>,
    pub guids: HashSet<String>,
    pub display: HashSet<String>,
    /// Template components (short type names), for path checks.
    pub components: HashSet<String>,
    /// Items: gear that keeps `generateRarity` (loot weapons): made without a requested rarity it is the lowest allowed one.
    pub generated_rarity: HashSet<String>,
}

#[derive(Debug, Clone)]
pub struct Field {
    pub path: String,
    pub norm: String,
    pub ty: String,
    pub enums: Vec<String>,
}

#[derive(Debug, Default)]
pub struct Catalog {
    pub path: PathBuf,
    pub side: Option<String>,
    pub build: Option<String>,
    pub sections: HashMap<String, Section>,
    pub fields: HashMap<String, Vec<Field>>,
    pub languages: Vec<String>,
}

pub fn parse_id(s: &str) -> Option<u32> {
    let t = s.trim();
    if let Some(h) = t.strip_prefix("0x").or_else(|| t.strip_prefix("0X")) {
        return u32::from_str_radix(h, 16).ok();
    }
    if !t.is_empty() && t.bytes().all(|b| b.is_ascii_digit()) {
        return t.parse::<u64>().ok().and_then(|v| u32::try_from(v).ok());
    }
    None
}

/// `[3]`, `[Type#1]`, `[*]` -> `[]`, for comparing field paths.
pub fn norm_path(p: &str) -> String {
    let mut out = String::with_capacity(p.len());
    let mut depth = 0;
    for c in p.chars() {
        match c {
            '[' => {
                if depth == 0 {
                    out.push('[');
                }
                depth += 1;
            }
            ']' => {
                depth -= 1;
                if depth == 0 {
                    out.push(']');
                }
            }
            c if depth == 0 => out.push(c),
            _ => {}
        }
    }
    out
}

impl Catalog {
    pub fn load(path: &Path) -> Result<Catalog, String> {
        let text = std::fs::read_to_string(path).map_err(|e| format!("{}: {e}", path.display()))?;
        let doc: J = serde_json::from_str(&text).map_err(|e| format!("{}: not JSON ({e})", path.display()))?;
        if doc["kind"].as_str().is_some_and(|k| k != "kiln.catalog") {
            return Err(format!("{}: kind {} is not kiln.catalog", path.display(), doc["kind"]));
        }
        if doc["format"].as_u64().is_some_and(|f| f > 1) {
            return Err(format!("{}: catalog format {} is newer than this kiln lint reads (1)", path.display(), doc["format"]));
        }
        Ok(Self::from_json(path, &doc))
    }

    pub fn from_json(path: &Path, doc: &J) -> Catalog {
        let mut c = Catalog {
            path: path.to_path_buf(),
            side: doc["game"]["side"].as_str().map(str::to_string),
            build: doc["game"]["build"].as_str().or(doc["game"]["steamBuild"].as_str()).map(str::to_string),
            ..Default::default()
        };
        if let Some(secs) = doc["sections"].as_object() {
            for (name, entries) in secs {
                let mut s = Section::default();
                for e in entries.as_array().into_iter().flatten() {
                    if let Some(n) = e["name"].as_str() {
                        if s.names.insert(n.to_string()) {
                            s.names_list.push(n.to_string());
                        }
                        s.lower.entry(n.to_ascii_lowercase()).or_insert_with(|| n.to_string());
                    }
                    if let Some(id) = e["id"].as_str().and_then(parse_id).or_else(|| e["id"].as_u64().and_then(|v| u32::try_from(v).ok())) {
                        s.ids.insert(id);
                    }
                    if let Some(g) = e["guid"].as_str() {
                        s.guids.insert(g.to_ascii_lowercase());
                    }
                    if let Some(g) = e["sequence"].as_str() {
                        s.guids.insert(g.to_ascii_lowercase());
                    }
                    if let Some(d) = e["displayName"].as_str() {
                        s.display.insert(d.to_string());
                    }
                    if e["generateRarity"].as_bool() == Some(true)
                        && let Some(n) = e["name"].as_str()
                    {
                        s.generated_rarity.insert(n.to_string());
                    }
                    for comp in e["components"].as_array().into_iter().flatten().filter_map(J::as_str) {
                        s.components.insert(comp.to_string());
                    }
                }
                if name == "languages" {
                    c.languages.extend(entries.as_array().into_iter().flatten().filter_map(|x| x.as_str().map(str::to_string)));
                }
                c.sections.insert(name.clone(), s);
            }
        }
        c.languages.extend(doc["languages"].as_array().into_iter().flatten().filter_map(|x| x.as_str().map(str::to_string)));
        if let Some(fs) = doc["fields"].as_object() {
            for (sec, list) in fs {
                let v: Vec<Field> = list
                    .as_array()
                    .into_iter()
                    .flatten()
                    .filter_map(|f| {
                        let p = f["path"].as_str()?;
                        let enums = f["enum"].as_array().into_iter().flatten().filter_map(|x| x.as_str().map(str::to_string)).collect();
                        Some(Field { path: p.to_string(), norm: norm_path(p), ty: f["type"].as_str().unwrap_or("").to_string(), enums })
                    })
                    .collect();
                if !v.is_empty() {
                    c.fields.insert(sec.clone(), v);
                }
            }
        }
        c
    }

    pub fn section(&self, s: &str) -> Option<&Section> {
        self.sections.get(s).filter(|x| !x.names.is_empty() || !x.ids.is_empty() || !x.guids.is_empty())
    }

    /// None: the catalog cannot tell (no such section / no field list). Some(true): known.
    pub fn field_known(&self, section: &str, path: &str) -> Option<bool> {
        let fields = self.fields.get(section)?;
        let mut n = norm_path(path);
        // templates: `Name#1.x` is the second component of that type
        if section == "templates"
            && let Some(hash) = n.find('#')
            && n[..hash].find('.').is_none()
        {
            let end = n[hash..].find(['.', '[']).map_or(n.len(), |e| hash + e);
            n.replace_range(hash..end, "");
        }
        Some(fields.iter().any(|f| {
            f.norm == n
                || f.norm.strip_prefix(n.as_str()).is_some_and(|rest| rest.starts_with('.') || rest.starts_with('['))
                || n.strip_prefix(f.norm.as_str()).is_some_and(|rest| rest.starts_with('.') || rest.starts_with('['))
        }))
    }

    /// The field with exactly this (normalized) path.
    pub fn field(&self, section: &str, path: &str) -> Option<&Field> {
        let n = norm_path(path);
        self.fields.get(section)?.iter().find(|f| f.norm == n)
    }

    pub fn field_suggestion(&self, section: &str, path: &str) -> Option<String> {
        let fields = self.fields.get(section)?;
        let cands: Vec<&str> = fields.iter().map(|f| f.path.as_str()).collect();
        suggest::best(path, &cands).map(str::to_string)
    }
}

/// Finds the catalog: LintOptions.catalog, then env KILN_CATALOG (a file, or a folder holding <side>.json), then
/// `<launcher data dir>/catalog/`. Side: LintOptions.side, else client, then server. Returns (catalog, problem).
pub fn locate(opts: &LintOptions) -> (Option<Catalog>, Option<String>) {
    let sides: Vec<String> = match &opts.side {
        Some(s) => vec![s.clone()],
        None => vec!["client".into(), "server".into()],
    };
    let pick = |p: &Path| -> Option<PathBuf> {
        if p.is_file() {
            return Some(p.to_path_buf());
        }
        if p.is_dir() {
            return sides.iter().map(|s| p.join(format!("{s}.json"))).find(|f| f.is_file());
        }
        None
    };
    let mut candidates: Vec<(PathBuf, bool)> = Vec::new();
    if let Some(c) = &opts.catalog {
        candidates.push((c.clone(), true));
    } else {
        if let Some(e) = std::env::var_os("KILN_CATALOG").filter(|e| !e.is_empty()) {
            candidates.push((PathBuf::from(e), true));
        }
        if let Ok(d) = crate::paths::DataDirs::locate() {
            candidates.push((d.root.join("catalog"), false));
        }
    }
    for (c, explicit) in candidates {
        match pick(&c) {
            Some(f) => {
                return match Catalog::load(&f) {
                    Ok(cat) => (Some(cat), None),
                    Err(e) => (None, Some(e)),
                };
            }
            None if explicit => return (None, Some(format!("{}: no catalog there ({}.json)", c.display(), sides.join(".json / ")))),
            None => {}
        }
    }
    (None, None)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn paths() {
        assert_eq!(norm_path("perkIds[2]"), "perkIds[]");
        assert_eq!(norm_path("events[SpawnEntityEvent#0].time"), "events[].time");
        assert_eq!(norm_path("attacks[*].description.maxRange"), "attacks[].description.maxRange");
        assert_eq!(parse_id("0xCDBE9C80"), Some(0xcdbe9c80));
        assert_eq!(parse_id("123"), Some(123));
        assert_eq!(parse_id("x"), None);
    }

    #[test]
    fn reads_the_sdk_fixture() {
        let p = Path::new(env!("CARGO_MANIFEST_DIR")).join("src/catalog/testdata/client.json");
        if !p.exists() {
            return; // the catalog module's fixture
        }
        let c = Catalog::load(&p).unwrap();
        assert_eq!(c.side.as_deref(), Some("client"));
        assert!(c.section("items").unwrap().names.contains("Test_Frost_Blade"));
        assert!(c.section("buffs").unwrap().ids.contains(&0x0e0e0001));
        assert!(c.languages.contains(&"de-de".to_string()));
        assert_eq!(c.field_known("items", "maxStackSize"), Some(true));
        assert_eq!(c.field_known("items", "maxStackSise"), Some(false));
        assert_eq!(c.field_known("templates", "x"), Some(false));
        assert_eq!(c.field_known("templates", "LifeTime.lifeTime"), Some(true));
        assert_eq!(c.field_known("templates", "LifeTime#1.lifeTime"), Some(true));
        assert_eq!(c.field_known("templates", "LifeTime.lifeTme"), Some(false));
        assert_eq!(c.field_known("templates", "Colliders.shapes[0].radius"), Some(true), "below a variant nothing is listed");
        assert_eq!(c.field_known("arsenals", "x"), None);
        assert_eq!(c.field_suggestion("items", "maxStackSise").as_deref(), Some("maxStackSize"));
    }
}
