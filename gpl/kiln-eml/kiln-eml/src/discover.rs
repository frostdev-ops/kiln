// SPDX-License-Identifier: GPL-3.0-or-later
//! Which EML mods this start runs: the Kiln profile's `mods.eml` entries, folders other engine plugins hand in
//! (kiln.eml add_mod), then the drop-in folders `<game>/kiln/eml/mods/` and EML's own `<game>/mods/`. Every candidate
//! gets an entry in the state file, loaded or not, with the reason.

use std::io::Read;
use std::path::Component;

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
pub fn profile_candidates(profile_json: &str, kiln_dir: &Utf8Path) -> Vec<Candidate> {
    let Ok(v) = serde_json::from_str::<serde_json::Value>(profile_json.trim_start_matches('\u{feff}')) else {
        return Vec::new();
    };
    let Some(list) = v.pointer("/mods/eml").and_then(|l| l.as_array()) else { return Vec::new() };
    list.iter()
        .filter_map(|e| {
            let path = e.get("path")?.as_str()?;
            Some(Candidate {
                path: resolve_profile_path(path, kiln_dir),
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

/// Profile paths use the core's base (<game>/kiln), not the working folder or the profile file's parent.
/// A profile explicitly authorizes the resulting mod root, so normalize its spelling before checking that root.
pub fn resolve_profile_path(path: &str, kiln_dir: &Utf8Path) -> Utf8PathBuf {
    let path = Utf8PathBuf::from(path.replace('\\', "/"));
    if path.as_str().is_empty() || path.as_str().contains('\0') { return path; }
    let bytes = path.as_str().as_bytes();
    if bytes.len() >= 2 && bytes[0].is_ascii_alphabetic() && bytes[1] == b':' && bytes.get(2) != Some(&b'/') {
        return path; // drive-relative paths depend on another working directory; validation must refuse them
    }
    #[cfg(windows)]
    if path.has_root() && !path.is_absolute() { return path; }
    let full = if path.is_absolute() { path } else { kiln_dir.join(path) };
    let full = std::path::absolute(&full).ok()
        .and_then(|p| Utf8PathBuf::from_path_buf(p).ok()).unwrap_or(full);
    let mut normalized = Utf8PathBuf::new();
    for part in full.components() {
        match part {
            camino::Utf8Component::CurDir => {},
            camino::Utf8Component::ParentDir => { normalized.pop(); },
            _ => normalized.push(part.as_str()),
        }
    }
    normalized
}

fn component_eq(a: &str, b: &str) -> bool {
    #[cfg(windows)] {
        // Match Windows' ordinal path comparison, including non-ASCII case pairs and either separator spelling.
        #[link(name = "kernel32")]
        unsafe extern "system" {
            fn CompareStringOrdinal(a: *const u16, na: i32, b: *const u16, nb: i32, ignore_case: i32) -> i32;
        }
        let a: Vec<u16> = a.replace('\\', "/").encode_utf16().collect();
        let b: Vec<u16> = b.replace('\\', "/").encode_utf16().collect();
        let (Ok(na), Ok(nb)) = (i32::try_from(a.len()), i32::try_from(b.len())) else { return false; };
        unsafe { CompareStringOrdinal(a.as_ptr(), na, b.as_ptr(), nb, 1) == 2 }
    }
    #[cfg(not(windows))] { a == b }
}

/// Component comparison prevents a sibling named "mods-other" from matching the root "mods".
fn relative_under(path: &Utf8Path, root: &Utf8Path) -> Option<Utf8PathBuf> {
    let mut parts = path.components();
    for parent in root.components() {
        if !component_eq(parent.as_str(), parts.next()?.as_str()) { return None; }
    }
    let mut relative = Utf8PathBuf::new();
    for part in parts { relative.push(part.as_str()); }
    Some(relative)
}

fn checked_metadata(path: &Utf8Path) -> Result<std::fs::Metadata, String> {
    let m = std::fs::symlink_metadata(path).map_err(|e| e.to_string())?;
    if m.file_type().is_symlink() { return Err("symlinks are not allowed at or inside a mod root".into()); }
    #[cfg(windows)] {
        use std::os::windows::fs::MetadataExt;
        #[link(name = "kernel32")]
        unsafe extern "system" { fn GetFileAttributesW(path: *const u16) -> u32; }
        let name: Vec<u16> = path.as_str().encode_utf16().chain(Some(0)).collect();
        // Wine follows Unix symlinks despite OPEN_REPARSE_POINT. Query by name too: this exposes directory links.
        // Wine file links are invisible to Win32 queries; their exclusion is the installer's responsibility.
        let attributes = unsafe { GetFileAttributesW(name.as_ptr()) };
        if attributes == u32::MAX { return Err(std::io::Error::last_os_error().to_string()); }
        if (attributes | m.file_attributes()) & 0x400 != 0 { return Err("reparse points are not allowed at or inside a mod root".into()); }
    }
    if !m.is_dir() && !m.is_file() { return Err("special files are not allowed in mods".into()); }
    Ok(m)
}

fn validate_under(path: &Utf8Path, root: &Utf8Path, relative: &Utf8Path) -> Result<(), String> {
    if relative.components().any(|p| !matches!(p, camino::Utf8Component::Normal(_))) {
        return Err("parent traversal is not allowed inside a mod root".into());
    }
    // Start at the authorized root. Its ancestors may include Steam library and Wine drive links.
    // The root itself, every component down to the candidate, and all candidate descendants must be regular.
    let mut current = root.to_owned();
    let mut m = checked_metadata(&current)?;
    for part in relative.components() {
        if !m.is_dir() { return Err("mod path descends through a file".into()); }
        current.push(part.as_str());
        m = checked_metadata(&current)?;
    }
    let resolved_root = root.canonicalize_utf8().map_err(|e| e.to_string())?;
    let mut todo = vec![path.to_owned()];
    let mut count = 0;
    while let Some(p) = todo.pop() {
        count += 1;
        if count > 50000 { return Err("mod tree exceeds 50000 entries".into()); }
        let m = checked_metadata(&p)?;
        let resolved = p.canonicalize_utf8().map_err(|e| e.to_string())?;
        if relative_under(&resolved, &resolved_root).is_none() {
            return Err("resolved mod path is outside the authorized root".into());
        }
        if m.is_dir() {
            for e in std::fs::read_dir(&p).map_err(|e| e.to_string())? {
                todo.push(Utf8PathBuf::from_path_buf(e.map_err(|e| e.to_string())?.path()).map_err(|_| "non UTF-8 mod path")?);
            }
        }
    }
    Ok(())
}

/// Reject traversal, links/reparse points and special files from an authorized root down, including the root.
/// Ancestors above that boundary belong to the player's layout and are not inspected. Final resolved paths must
/// remain contained. Wine hides Unix file links; the installer must exclude those (engines/README.md path policy).
pub fn validate_path(path: &Utf8Path, roots: &[Utf8PathBuf]) -> Result<(), String> {
    if !path.is_absolute() || path.as_str().contains('\0') {
        return Err("mod path must be absolute without NUL".into());
    }
    let mut error = "mod path is outside the authorized roots".to_owned();
    for root in roots {
        if !root.is_absolute() || root.as_str().contains('\0')
            || root.components().any(|p| matches!(p, camino::Utf8Component::ParentDir)) { continue; }
        let Some(relative) = relative_under(path, root) else { continue; };
        match validate_under(path, root, &relative) {
            Ok(()) => return Ok(()),
            Err(why) => error = why,
        }
    }
    Err(error)
}

fn manifest(text: &str, flame: bool, extended: Option<&str>) -> Result<ModManifest, String> {
    if flame { return Err("a Flame mod (flame.json)".into()); }
    let raw: serde_json::Value = serde_json::from_str(text.trim_start_matches('\u{feff}')).map_err(|e| format!("mod.json: {e}"))?;
    if ["dll", "emf", "emfMin", "emf_api", "main"].iter().any(|k| raw.get(*k).is_some()) {
        return Err("an EMF mod (mod.json names a dll/main or an EMF API version)".into());
    }
    if let Some(text) = extended {
        let v: serde_json::Value = serde_json::from_str(text.trim_start_matches('\u{feff}')).map_err(|e| format!("extended.mod.json: {e}"))?;
        if v.get("launcher").and_then(|v| v.as_str()) != Some("EML") {
            return Err("a ShroudForge package (extended.mod.json): ShroudForge's runtime runs it".into());
        }
    }
    serde_json::from_value(raw).map_err(|e| format!("not an EML manifest: {e}"))
}

/// The manifest of an EML mod, or why the folder/zip is not one EML runs.
pub fn classify(path: &Utf8Path) -> Result<ModManifest, String> {
    if path.is_dir() {
        let has = |f: &str| path.join(f).is_file();
        if !has("mod.json") {
            return Err("no mod.json (not an EML mod)".into());
        }
        fn text(path: &Utf8Path) -> Result<String, String> {
            let mut s = String::new();
            std::fs::File::open(path).map_err(|e| e.to_string())?.take(1024 * 1024 + 1)
                .read_to_string(&mut s).map_err(|e| e.to_string())?;
            if s.len() > 1024 * 1024 { return Err("manifest exceeds 1 MiB".into()); }
            Ok(s)
        }
        let manifest_text = text(&path.join("mod.json"))?;
        let extended = if has("extended.mod.json") {
            Some(text(&path.join("extended.mod.json"))?)
        } else { None };
        let manifest = manifest(&manifest_text, has("flame.json"), extended.as_deref())?;
        if !has("src/mod.lua") {
            return Err("no src/mod.lua".into());
        }
        Ok(manifest)
    } else if path.is_file() {
        let file = std::fs::File::open(path).map_err(|e| format!("{e}"))?;
        let mut zip = zip::ZipArchive::new(file).map_err(|e| format!("not a readable zip: {e}"))?;
        if zip.len() > 50000 { return Err("zip exceeds 50000 entries".into()); }
        let mut total = 0u64;
        for i in 0..zip.len() {
            let f = zip.by_index(i).map_err(|e| e.to_string())?;
            let name = f.name();
            if name.contains('\\') || name.contains(':') || std::path::Path::new(name).components().any(|c| !matches!(c, Component::Normal(_)))
                || f.unix_mode().is_some_and(|m| m & 0o170000 == 0o120000) {
                return Err("unsafe zip entry".into());
            }
            total = total.checked_add(f.size()).ok_or("zip size overflow")?;
            if total > 512 * 1024 * 1024 { return Err("zip exceeds 512 MiB uncompressed".into()); }
        }
        fn text(zip: &mut zip::ZipArchive<std::fs::File>, name: &str) -> Result<String, String> {
            let mut f = zip.by_name(name).map_err(|e| e.to_string())?;
            if f.size() > 1024 * 1024 { return Err("manifest exceeds 1 MiB".into()); }
            let mut text = String::new();
            f.by_ref().take(1024 * 1024 + 1).read_to_string(&mut text).map_err(|e| e.to_string())?;
            if text.len() > 1024 * 1024 { return Err("manifest exceeds 1 MiB".into()); }
            Ok(text)
        }
        let t = text(&mut zip, "mod.json")?;
        let flame = zip.by_name("flame.json").is_ok();
        let extended = if zip.by_name("extended.mod.json").is_ok() { Some(text(&mut zip, "extended.mod.json")?) } else { None };
        let manifest = manifest(&t, flame, extended.as_deref())?;
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
        match validate_path(&c.path, &[c.path.clone()]).and_then(|_| classify(&c.path)) {
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
        let huge = eml_mod(&d, "huge", "huge");
        std::fs::write(huge.join("mod.json"), vec![b' '; 1024 * 1024 + 1]).unwrap();
        assert!(classify(&huge).unwrap_err().contains("exceeds 1 MiB"));
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
        let c = profile_candidates(j, Utf8Path::new("/game/kiln"));
        assert_eq!(c.len(), 2);
        assert!(!c[1].enabled);
        assert!(profile_dropins(j));
        assert!(!profile_dropins(r#"{"dropins":["emf"]}"#));
        assert!(profile_dropins(r#"{"dropins":true}"#));
        assert!(profile_candidates("not json", Utf8Path::new("/game/kiln")).is_empty());
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
    #[test]
    fn rejects_outside_roots_and_links() {
        let d = tmp("roots");
        let m = eml_mod(&d, "mods/a", "a");
        assert!(validate_path(&m, &[d.join("mods")]).is_ok());
        assert!(validate_path(&m, &[d.join("missing")]).is_err());
        assert!(validate_path(&d.join("mods/../mods/a"), &[d.clone()]).is_err());
        #[cfg(unix)] {
            std::os::unix::fs::symlink(d.join("outside"), m.join("link")).unwrap();
            assert!(validate_path(&m, &[d.clone()]).is_err());
        }
        std::fs::remove_dir_all(d).unwrap();
    }
    #[test]
    fn zip_classification_matches_folder_rules() {
        use std::io::Write;
        let d = tmp("zip");
        for (marker, body) in [("extended.mod.json", "{\"launcher\":\"SF\"}"), ("flame.json", "{}")] {
            let path = d.join("mod.zip");
            let mut z = zip::ZipWriter::new(std::fs::File::create(&path).unwrap());
            let opt = zip::write::SimpleFileOptions::default();
            for (name, text) in [("mod.json", "{\"id\":\"a\",\"name\":\"a\",\"version\":\"1.0.0\"}"), ("src/mod.lua", ""), (marker, body)] {
                z.start_file(name, opt).unwrap(); z.write_all(text.as_bytes()).unwrap();
            }
            z.finish().unwrap();
            assert!(classify(&path).is_err());
        }
        std::fs::remove_dir_all(d).unwrap();
    }

    #[cfg(unix)]
    #[test]
    fn validates_only_from_the_authorized_root_down() {
        let d = tmp("root-boundary");
        let real = d.join("real-library");
        let m = eml_mod(&real, "mods/a", "a");
        let layout = d.join("steam-library");
        std::os::unix::fs::symlink(&real, &layout).unwrap();
        let root = layout.join("mods");
        let candidate = root.join("a");
        assert!(validate_path(&candidate, &[root.clone()]).is_ok(),
            "a linked Steam/Wine layout above the authorized root must be accepted");
        let (mods, entries) = resolve(&[Candidate { path: candidate.clone(), source: "profile".into(), enabled: true }]);
        assert_eq!(mods, [candidate.clone()]);
        assert_eq!(entries[0].state, "pending");
        assert!(validate_path(&root.join("a/../a"), &[root.clone()]).is_err());
        let root_link = layout.join("linked-mods");
        std::os::unix::fs::symlink(real.join("mods"), &root_link).unwrap();
        assert!(validate_path(&root_link.join("a"), &[root_link]).is_err(), "the root itself cannot be a link");
        let internal = root.join("internal-link");
        std::os::unix::fs::symlink(&m, &internal).unwrap();
        assert!(validate_path(&internal, &[root.clone()]).is_err(), "even an in-root directory link is refused");
        assert!(validate_path(&internal.join("src"), &[root.clone()]).is_err(), "intermediate directory links are refused");
        std::fs::remove_file(&internal).unwrap();
        let outside = eml_mod(&d, "outside", "outside");
        std::os::unix::fs::symlink(&outside, &internal).unwrap();
        assert!(validate_path(&internal, &[root.clone()]).is_err(), "an escaping directory link is refused");
        std::fs::remove_file(&internal).unwrap();
        std::os::unix::fs::symlink(outside.join("mod.json"), m.join("linked-file")).unwrap();
        assert!(validate_path(&candidate, &[root]).is_err(), "native Unix exposes file links; Wine cannot detect this case");
        std::fs::remove_dir_all(d).unwrap();
    }

    #[test]
    fn profile_paths_resolve_against_kiln_and_remain_authorized() {
        let d = tmp("relative-profile");
        let kiln = d.join("game/kiln");
        let m = eml_mod(&kiln, "eml/mods/relative", "relative");
        let outside = eml_mod(&d, "game/mods/sibling", "sibling");
        let json = r#"{"mods":{"eml":[{"path":"eml\\mods\\relative"},{"path":"../mods/sibling"}]}}"#;
        let candidates = profile_candidates(json, &kiln);
        assert_eq!(candidates[0].path, m);
        assert_eq!(candidates[1].path, outside);
        assert!(validate_path(&m, &[resolve_profile_path("eml/mods/relative", &kiln)]).is_ok());
        let (mods, _) = resolve(&candidates);
        assert_eq!(mods, [m.clone(), outside]);
        #[cfg(unix)] {
            let alias = d.join("proton-layout");
            std::os::unix::fs::symlink(d.join("game"), &alias).unwrap();
            let candidates = profile_candidates(json, &alias.join("kiln"));
            assert_eq!(resolve(&candidates).0.len(), 2);
        }
        assert_eq!(resolve_profile_path(m.as_str(), &kiln), m);
        assert!(!resolve_profile_path("C:mods/a", &kiln).is_absolute());
        assert!(!resolve_profile_path("", &kiln).is_absolute());
        let wrong_root = eml_mod(&d, "game/mods-other/third", "third");
        assert!(validate_path(&wrong_root, &[d.join("game/mods")]).is_err());
        std::fs::remove_dir_all(d).unwrap();
    }

}
