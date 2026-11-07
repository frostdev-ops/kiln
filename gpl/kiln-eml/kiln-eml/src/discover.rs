// SPDX-License-Identifier: GPL-3.0-or-later
//! Which EML mods this start runs: the Kiln profile's `mods.eml` entries, folders other engine plugins hand in
//! (kiln.eml add_mod), then the drop-in folders `<game>/kiln/eml/mods/` and EML's own `<game>/mods/`. Every candidate
//! gets an entry in the state file, loaded or not, with the reason.

use std::io::Read;

use camino::{Utf8Path, Utf8PathBuf};
use mod_loader::ModManifest;
use serde::Serialize;

/// One mod folder or zip and where it came from (`profile`, `kiln-dropin`, `game-mods`, or an engine's name).
#[derive(Clone, Debug)]
pub struct Candidate {
    pub path: Utf8PathBuf,
    pub source: String,
    pub enabled: bool,
}

/// A line of `<game>/kiln/state/eml.json` `mods[]`.
#[derive(Clone, Debug, Serialize)]
pub struct Entry {
    pub id: Option<String>,
    pub version: Option<String>,
    pub name: Option<String>,
    pub source: String,
    pub path: String,
    /// `loaded` | `failed` | `skipped` | `disabled` | `duplicate` | `pending`
    pub state: String,
    pub reason: Option<String>,
    pub capabilities: Vec<String>,
}

/// The `mods.eml` list of a Kiln profile (`[{ "id", "path", "enabled" }]`, the profile's folderMod shape).
pub fn profile_candidates(profile_json: &str) -> Vec<Candidate> {
    let Ok(v) = serde_json::from_str::<serde_json::Value>(profile_json.trim_start_matches('\u{feff}')) else {
        return Vec::new();
    };
    let Some(list) = v.pointer("/mods/eml").and_then(|l| l.as_array()) else { return Vec::new() };
    list.iter()
        .filter_map(|e| {
            let path = e.get("path")?.as_str()?;
            Some(Candidate {
                path: Utf8PathBuf::from(path),
                source: "profile".into(),
                enabled: e.get("enabled").and_then(|b| b.as_bool()).unwrap_or(true),
            })
        })
        .collect()
}

/// Does the profile ask for EML's drop-in folders (`"dropins": true` or a list holding "eml")?
pub fn profile_dropins(profile_json: &str) -> bool {
    let Ok(v) = serde_json::from_str::<serde_json::Value>(profile_json.trim_start_matches('\u{feff}')) else {
        return false;
    };
    match v.get("dropins") {
        Some(serde_json::Value::Bool(b)) => *b,
        Some(serde_json::Value::Array(a)) => a.iter().any(|k| k.as_str() == Some("eml")),
        _ => false,
    }
}

/// The entries of a drop-in folder, sorted by name: folders and `.zip` files, without hidden ones (EML's rule).
pub fn folder_candidates(dir: &Utf8Path, source: &str) -> Vec<Candidate> {
    let mut out: Vec<Candidate> = std::fs::read_dir(dir)
        .into_iter()
        .flatten()
        .flatten()
        .filter_map(|e| Utf8PathBuf::from_path_buf(e.path()).ok())
        .filter(|p| !p.file_name().unwrap_or_default().starts_with('.'))
        .filter(|p| p.is_dir() || p.extension().is_some_and(|x| x.eq_ignore_ascii_case("zip")))
        .map(|path| Candidate { path, source: source.into(), enabled: true })
        .collect();
    out.sort_by(|a, b| a.path.cmp(&b.path));
    out
}

/// The manifest of an EML mod, or why the folder/zip is not one EML runs.
pub fn classify(path: &Utf8Path) -> Result<ModManifest, String> {
    if path.is_dir() {
        let has = |f: &str| path.join(f).is_file();
        if !has("mod.json") {
            return Err("no mod.json (not an EML mod)".into());
        }
        if has("flame.json") {
            return Err("a Flame mod (flame.json)".into());
        }
        let text = std::fs::read_to_string(path.join("mod.json")).map_err(|e| format!("mod.json: {e}"))?;
        let raw: serde_json::Value =
            serde_json::from_str(text.trim_start_matches('\u{feff}')).map_err(|e| format!("mod.json: {e}"))?;
        if ["dll", "emf", "emfMin", "emf_api", "main"].iter().any(|k| raw.get(*k).is_some()) {
            return Err("an EMF mod (mod.json names a dll/main or an EMF API version)".into());
        }
        if has("extended.mod.json") {
            let launcher = std::fs::read_to_string(path.join("extended.mod.json"))
                .ok()
                .and_then(|t| serde_json::from_str::<serde_json::Value>(t.trim_start_matches('\u{feff}')).ok())
                .and_then(|v| v.get("launcher").and_then(|l| l.as_str()).map(str::to_owned));
            if launcher.as_deref() != Some("EML") {
                return Err("a ShroudForge package (extended.mod.json): ShroudForge's runtime runs it".into());
            }
        }
        let manifest = serde_json::from_value::<ModManifest>(raw).map_err(|e| format!("not an EML manifest: {e}"))?;
        if !has("src/mod.lua") {
            return Err("no src/mod.lua".into());
        }
        Ok(manifest)
    } else if path.is_file() {
        let file = std::fs::File::open(path).map_err(|e| format!("{e}"))?;
        let mut zip = zip::ZipArchive::new(file).map_err(|e| format!("not a readable zip: {e}"))?;
        let mut text = String::new();
        zip.by_name("mod.json")
            .map_err(|_| "no mod.json at the zip's root (not an EML mod)".to_string())?
            .read_to_string(&mut text)
            .map_err(|e| format!("mod.json: {e}"))?;
        let manifest = serde_json::from_str::<ModManifest>(text.trim_start_matches('\u{feff}'))
            .map_err(|e| format!("not an EML manifest: {e}"))?;
        if zip.by_name("src/mod.lua").is_err() {
            return Err("no src/mod.lua in the zip".into());
        }
        Ok(manifest)
    } else {
        Err("missing".into())
    }
}

fn capability_names(m: &ModManifest) -> Vec<String> {
    m.capabilities
        .iter()
        .map(|c| serde_json::to_value(c).ok().and_then(|v| v.as_str().map(str::to_owned)).unwrap_or_default())
        .collect()
}

/// Candidates in order -> the mod paths to run and one entry per candidate. The first mod with an id wins; later ones
/// are `duplicate`. Entries of folders that are not EML mods at all ("no mod.json") are kept, state `skipped`.
pub fn resolve(candidates: &[Candidate]) -> (Vec<Utf8PathBuf>, Vec<Entry>) {
    let mut run = Vec::new();
    let mut entries: Vec<Entry> = Vec::new();
    for c in candidates {
        let mut e = Entry {
            id: None,
            version: None,
            name: None,
            source: c.source.clone(),
            path: c.path.to_string(),
            state: "pending".into(),
            reason: None,
            capabilities: Vec::new(),
        };
        match classify(&c.path) {
            Ok(m) => {
                e.id = Some(m.id.clone());
                e.version = Some(m.version.to_string());
                e.name = Some(m.name.clone());
                e.capabilities = capability_names(&m);
                if !c.enabled {
                    e.state = "disabled".into();
                    e.reason = Some("switched off in the profile".into());
                } else if let Some(first) = entries.iter().find(|o| o.id.as_deref() == Some(&m.id) && o.state == "pending") {
                    e.state = "duplicate".into();
                    e.reason = Some(format!("id {} is already loaded from {}", m.id, first.path));
                } else {
                    run.push(c.path.clone());
                }
            }
            Err(why) => {
                e.state = "skipped".into();
                e.reason = Some(why);
            }
        }
        entries.push(e);
    }
    (run, entries)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn tmp(name: &str) -> Utf8PathBuf {
        let d = Utf8PathBuf::from_path_buf(std::env::temp_dir()).unwrap().join(format!("kiln-eml-test-{name}-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&d);
        std::fs::create_dir_all(&d).unwrap();
        d
    }

    fn eml_mod(root: &Utf8Path, dir: &str, id: &str) -> Utf8PathBuf {
        let p = root.join(dir);
        std::fs::create_dir_all(p.join("src")).unwrap();
        std::fs::write(p.join("mod.json"), format!(r#"{{"id":"{id}","name":"N","version":"1.0.0","capabilities":["patch"]}}"#)).unwrap();
        std::fs::write(p.join("src/mod.lua"), "-- x").unwrap();
        p
    }

    #[test]
    fn classifies_eml_and_others() {
        let d = tmp("classify");
        let ok = eml_mod(&d, "a", "a");
        assert_eq!(classify(&ok).unwrap().id, "a");
        let emf = d.join("emf");
        std::fs::create_dir_all(&emf).unwrap();
        std::fs::write(emf.join("mod.json"), r#"{"id":"x","name":"x","version":"1.0.0","dll":"x.dll","emf":21}"#).unwrap();
        assert!(classify(&emf).unwrap_err().contains("EMF"));
        let sf = eml_mod(&d, "sf", "sf");
        std::fs::write(sf.join("extended.mod.json"), r#"{"launcher":"SF"}"#).unwrap();
        assert!(classify(&sf).unwrap_err().contains("ShroudForge"));
        let legacy = eml_mod(&d, "legacy", "legacy");
        std::fs::write(legacy.join("extended.mod.json"), r#"{"launcher":"EML"}"#).unwrap();
        assert!(classify(&legacy).is_ok());
        let bad_cap = eml_mod(&d, "cap", "cap");
        std::fs::write(bad_cap.join("mod.json"), r#"{"id":"cap","name":"c","version":"1.0.0","capabilities":["services"]}"#).unwrap();
        assert!(classify(&bad_cap).unwrap_err().contains("not an EML manifest"));
        let nolua = d.join("nolua");
        std::fs::create_dir_all(&nolua).unwrap();
        std::fs::write(nolua.join("mod.json"), r#"{"id":"n","name":"n","version":"1.0.0"}"#).unwrap();
        assert_eq!(classify(&nolua).unwrap_err(), "no src/mod.lua");
        let empty = d.join("empty");
        std::fs::create_dir_all(&empty).unwrap();
        assert!(classify(&empty).unwrap_err().starts_with("no mod.json"));
    }

    #[test]
    fn resolve_orders_dedupes_and_disables() {
        let d = tmp("resolve");
        let p1 = eml_mod(&d, "one", "same");
        let p2 = eml_mod(&d, "two", "same");
        let p3 = eml_mod(&d, "three", "other");
        let c = |p: &Utf8PathBuf, enabled| Candidate { path: p.clone(), source: "t".into(), enabled };
        let (run, entries) = resolve(&[c(&p1, true), c(&p2, true), c(&p3, false)]);
        assert_eq!(run, vec![p1.clone()]);
        assert_eq!(entries[1].state, "duplicate");
        assert_eq!(entries[2].state, "disabled");
    }

    #[test]
    fn profile_lists_and_dropins() {
        let j = r#"{"format":1,"id":"p","generation":1,"dropins":["emf","eml"],
                    "mods":{"eml":[{"id":"a","path":"Z:\\lib\\eml\\a"},{"id":"b","path":"Z:\\b","enabled":false}]}}"#;
        let c = profile_candidates(j);
        assert_eq!(c.len(), 2);
        assert!(!c[1].enabled);
        assert!(profile_dropins(j));
        assert!(!profile_dropins(r#"{"dropins":["emf"]}"#));
        assert!(profile_dropins(r#"{"dropins":true}"#));
        assert!(profile_candidates("not json").is_empty());
    }

    #[test]
    fn folder_candidates_skip_hidden_and_files() {
        let d = tmp("folder");
        eml_mod(&d, "b", "b");
        eml_mod(&d, "a", "a");
        eml_mod(&d, ".hidden", "h");
        std::fs::write(d.join("readme.txt"), "x").unwrap();
        std::fs::write(d.join("z.zip"), "x").unwrap();
        let names: Vec<_> = folder_candidates(&d, "s").iter().map(|c| c.path.file_name().unwrap().to_string()).collect();
        assert_eq!(names, ["a", "b", "z.zip"]);
    }
}
