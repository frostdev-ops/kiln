// SPDX-License-Identifier: GPL-3.0-or-later
// kiln-eml: working .bak files are shared with other writers. Neither their presence nor an EML cache is ownership.
use std::{collections::BTreeMap, fs, io::{self, Read}, path::{Path, PathBuf}};
use serde::{Deserialize, Serialize};
use sha2::{Digest as _, Sha256};

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
struct Digest { size: u64, sha256: String }

fn digest(root: &Path, path: &Path) -> io::Result<Digest> {
    checked_path(root, path)?;
    super::transaction::regular(path)?;
    let mut file = fs::File::open(path)?;
    let mut hash = Sha256::new();
    let mut size = 0;
    let mut buffer = [0u8; 65536];
    loop {
        let n = file.read(&mut buffer)?;
        if n == 0 { break; }
        size += n as u64;
        hash.update(&buffer[..n]);
    }
    Ok(Digest { size, sha256: format!("{:x}", hash.finalize()) })
}

fn invalid(message: &str) -> io::Error { io::Error::other(message) }

fn check_stem(stem: &str) -> io::Result<()> {
    if stem.is_empty() || !stem.bytes().all(|b| b.is_ascii_alphanumeric() || b == b'_' || b == b'-') {
        return Err(invalid("invalid archive stem"));
    }
    Ok(())
}

fn above_root(root: &Path, path: &Path) -> bool {
    #[cfg(windows)]
    fn lexical(path: &Path) -> PathBuf {
        // SF persists verbatim paths; Kiln's native host may supply ordinary drive/UNC paths.
        let text = path.to_string_lossy();
        if let Some(rest) = text.strip_prefix(r"\\?\UNC\") { PathBuf::from(format!(r"\\{rest}")) }
        else { PathBuf::from(text.strip_prefix(r"\\?\").unwrap_or(&text)) }
    }
    #[cfg(windows)]
    let (root, path) = (lexical(root), lexical(path));
    root != path && root.starts_with(path)
}

// Check existing descendants of the selected game location, including Windows junctions.
// Ancestors ABOVE that trusted location may be legitimate volume mounts (Wine reports them as links).
// Custom SF state outside the game is checked up to its common ancestor with that location, or its drive root.
fn checked_path(root: &Path, path: &Path) -> io::Result<()> {
    for component in path.ancestors() {
        if component.as_os_str().is_empty() { continue; }
        if above_root(root, component) { break; }
        if component.parent().is_none() { break; }
        match fs::symlink_metadata(component) {
            Ok(m) => {
                if m.file_type().is_symlink() { return Err(invalid(&format!("linked archive/state path: {}", component.display()))); }
                #[cfg(windows)] {
                    use std::os::windows::fs::MetadataExt;
                    if m.file_attributes() & 0x400 != 0 { return Err(invalid("reparse archive/state path")); }
                }
            },
            Err(e) if e.kind() == io::ErrorKind::NotFound => {},
            Err(e) => return Err(e),
        }
    }
    Ok(())
}

fn bounded(root: &Path, path: &Path) -> io::Result<Vec<u8>> {
    checked_path(root, path)?;
    super::transaction::regular(path)?;
    let mut bytes = Vec::new();
    fs::File::open(path)?.take((16 << 20) + 1).read_to_end(&mut bytes)?;
    if bytes.len() > 16 << 20 { return Err(invalid("archive/state receipt is too large")); }
    Ok(bytes)
}

fn directory(root: &Path) -> io::Result<PathBuf> {
    let dir = root.join("kiln/eml/ownership");
    checked_path(root, &dir)?;
    fs::create_dir_all(&dir)?;
    checked_path(root, &dir)?;
    Ok(dir)
}

fn valid_name(stem: &str, name: &str) -> bool {
    name == format!("{stem}.kfc") || name == format!("{stem}.kfc_resources") || name
        .strip_prefix(&format!("{stem}_")).and_then(|s| s.strip_suffix(".dat"))
        .is_some_and(|s| !s.is_empty() && s.bytes().all(|b| b.is_ascii_digit()))
}

#[derive(Serialize, Deserialize)]
struct Receipt {
    format: u32,
    stem: String,
    files: BTreeMap<String, Digest>,
    backups: BTreeMap<String, Digest>,
}

fn backups(root: &Path, stem: &str) -> io::Result<BTreeMap<String, Digest>> {
    let mut result = BTreeMap::new();
    for extension in ["kfc.bak", "kfc_resources.bak", "kfc.bak.integrity"] {
        let name = format!("{stem}.{extension}");
        let path = root.join(&name);
        if path.try_exists()? { result.insert(name, digest(root, &path)?); }
    }
    if !result.contains_key(&format!("{stem}.kfc.bak")) { return Err(invalid("no working baseline")); }
    Ok(result)
}

/// Exact output and original working-baseline proof. Legacy performance caches never authorize restoration.
pub fn owns_output(root: &Path, stem: &str) -> io::Result<bool> {
    check_stem(stem)?;
    let path = root.join("kiln/eml/ownership").join(format!("{stem}.json"));
    checked_path(root, &path)?;
    if !path.try_exists()? { return Ok(false); }
    let receipt: Receipt = serde_json::from_slice(&bounded(root, &path)?).map_err(|_| invalid("invalid EML ownership receipt"))?;
    if receipt.format != 1 || receipt.stem != stem || receipt.files.len() < 2 ||
        !receipt.files.contains_key(&format!("{stem}.kfc")) ||
        !receipt.files.contains_key(&format!("{stem}.kfc_resources")) ||
        receipt.files.keys().any(|n| !valid_name(stem, n)) {
        return Err(invalid("unknown EML ownership receipt"));
    }
    if backups(root, stem)? != receipt.backups { return Ok(false); }
    for (name, expected) in receipt.files {
        if digest(root, &root.join(name))? != expected { return Ok(false); }
    }
    Ok(true)
}

/// Write ahead: only the exact staged set can later match this proof, including after a publication failure.
pub(crate) fn prepare(root: &Path, stem: &str, files: &[PathBuf]) -> io::Result<()> {
    check_stem(stem)?;
    let path = directory(root)?.join(format!("{stem}.json"));
    if path.try_exists()? {
        let previous: Receipt = serde_json::from_slice(&bounded(root, &path)?)?;
        if previous.format != 1 || previous.stem != stem {
            return Err(invalid("unknown EML ownership receipt; retain it for recovery before retrying"));
        }
    }
    let mut output = BTreeMap::new();
    let mut changed = false;
    for path in files {
        let name = path.file_name().and_then(|n| n.to_str()).ok_or_else(|| invalid("invalid output name"))?;
        if !valid_name(stem, name) { return Err(invalid("unexpected EML output file")); }
        let expected = digest(root, path)?;
        let target = root.join(name);
        checked_path(root, &target)?;
        if !target.try_exists()? || digest(root, &target)? != expected { changed = true; }
        output.insert(name.to_owned(), expected);
    }
    if !output.contains_key(&format!("{stem}.kfc")) || !output.contains_key(&format!("{stem}.kfc_resources")) {
        return Err(invalid("incomplete EML output set"));
    }
    let receipt = Receipt { format: 1, stem: stem.into(), files: output, backups: backups(root, stem)? };
    if changed { invalidate_sf(root, stem)?; }
    super::transaction::atomic_write(&path, &serde_json::to_vec(&receipt)?)?;
    Ok(())
}

pub(crate) fn before_restore(root: &Path, stem: &str) -> io::Result<()> {
    check_stem(stem)?;
    for extension in ["kfc", "kfc_resources"] {
        let backup = root.join(format!("{stem}.{extension}.bak"));
        if backup.try_exists()? && digest(root, &backup)? != digest(root, &root.join(format!("{stem}.{extension}")))? {
            return invalidate_sf(root, stem);
        }
    }
    Ok(())
}

// Only invalidate a recognized, unchanged prepared-output cache, never settings or the rest of state.json.
// Keep the exact original bytes and replacement in a recovery journal. Do not restore a now-false "applied" claim.
fn invalidate_sf(root: &Path, stem: &str) -> io::Result<()> {
    let config_dir = root.join("shroudforge/config");
    let config_path = config_dir.join("modloader-config.json");
    checked_path(root, &config_path)?;
    let state_dir = if config_path.try_exists()? {
        let config: serde_json::Value = serde_json::from_slice(&bounded(root, &config_path)?)?;
        match config.pointer("/paths/state") {
            None | Some(serde_json::Value::Null) => root.join("shroudforge"),
            Some(serde_json::Value::String(s)) if s.is_empty() => root.join("shroudforge"),
            Some(serde_json::Value::String(s)) => {
                let path = PathBuf::from(s);
                if path.is_absolute() { path } else { root.join(path) }
            },
            _ => return Err(invalid("unknown ShroudForge state path")),
        }
    } else { root.join("shroudforge") };
    let state = state_dir.join("state.json");
    checked_path(root, &state)?;
    if !state.try_exists()? { return Ok(()); }
    // Same installation lock as ShroudForge's document writer. Never wait on an active standalone loader.
    checked_path(root, &config_dir)?;
    fs::create_dir_all(&config_dir)?;
    let lock_path = config_dir.join(".shroudforge-write.lock");
    checked_path(root, &lock_path)?;
    let lock = fs::OpenOptions::new().create(true).truncate(false).read(true).write(true).open(&lock_path)?;
    lock.try_lock().map_err(|e| invalid(&format!("ShroudForge state is in use: {e}")))?;
    // Recheck the configured destination under the lock, not only the state bytes.
    if config_path.try_exists()? {
        let config: serde_json::Value = serde_json::from_slice(&bounded(root, &config_path)?)?;
        let current = config.pointer("/paths/state").and_then(|v| v.as_str()).filter(|s| !s.is_empty())
            .map(|s| { let p = PathBuf::from(s); if p.is_absolute() { p } else { root.join(p) } })
            .unwrap_or_else(|| root.join("shroudforge"));
        if current != state_dir { return Err(invalid("ShroudForge state path changed during handoff")); }
    }
    let original = bounded(root, &state)?;
    let mut value: serde_json::Value = serde_json::from_slice(&original)?;
    let Some(assets) = value.get_mut("assets") else { return Ok(()); };
    if assets.get("status").and_then(|v| v.as_str()) != Some("applied") { return Ok(()); }
    if assets.get("target").and_then(|v| v.as_str()) != Some(stem) { return Ok(()); }
    let fingerprint = assets.get("fingerprint").and_then(|v| v.as_str()).unwrap_or("");
    if assets.get("schemaVersion").and_then(|v| v.as_u64()) != Some(1) || fingerprint.len() != 64 ||
        !fingerprint.bytes().all(|b| b.is_ascii_digit() || (b'a'..=b'f').contains(&b)) ||
        !assets.get("mods").is_some_and(|v| v.is_array()) ||
        !assets.get("completedAt").is_some_and(|v| v.is_u64()) ||
        !matches!(assets.get("phase").and_then(|v| v.as_str()), Some("prepare" | "startup")) {
        return Err(invalid("unknown ShroudForge prepared-output cache; refusing archive handoff"));
    }
    assets["status"] = "failed".into();
    assets["error"] = "Kiln's EML writer changed game data; ShroudForge must prepare its asset mods again".into();
    let replacement = serde_json::to_vec_pretty(&value)?;
    let recovery = directory(root)?.join(format!("sf-handoff-{}.json", uuid::Uuid::new_v4()));
    let journal = serde_json::json!({"format":1,"target":stem,"statePath":state,"original":String::from_utf8(original.clone()).map_err(|_| invalid("non UTF-8 SF state"))?,"replacement":String::from_utf8(replacement.clone()).unwrap()});
    super::transaction::atomic_write(&recovery, &serde_json::to_vec(&journal)?)?;
    if bounded(root, &state)? != original { return Err(invalid("ShroudForge state changed during handoff; original retained")); }
    super::transaction::atomic_write(&state, &replacement)?;
    tracing::info!(path = %state.display(), recovery = %recovery.display(), "Invalidated only ShroudForge's stale prepared-output cache before EML changed game data; settings retained");
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    fn fixture() -> (PathBuf, Vec<PathBuf>) {
        let root = std::env::temp_dir().join(format!("kiln-ownership-{}", uuid::Uuid::new_v4()));
        fs::create_dir_all(root.join("stage")).unwrap();
        let mut files = Vec::new();
        for (name, old, new) in [("g.kfc", "SF header", "EML header"), ("g.kfc_resources", "SF resources", "EML resources"), ("g_010.dat", "", "content")] {
            fs::write(root.join(name), old).unwrap();
            let path = root.join("stage").join(name);
            fs::write(&path, new).unwrap();
            files.push(path);
        }
        fs::write(root.join("g.kfc.bak"), "original header").unwrap();
        fs::write(root.join("g.kfc_resources.bak"), "original resources").unwrap();
        (root, files)
    }
    fn sf(root: &Path, custom: bool) -> (PathBuf, Vec<u8>) {
        fs::create_dir_all(root.join("shroudforge/config")).unwrap();
        let dir = if custom { root.join("custom-state") } else { root.join("shroudforge") };
        fs::create_dir_all(&dir).unwrap();
        fs::write(root.join("shroudforge/config/modloader-config.json"), serde_json::to_vec(&serde_json::json!({"paths":{"state":dir},"playerSetting":"keep"})).unwrap()).unwrap();
        let bytes = serde_json::to_vec(&serde_json::json!({"format":1,"player":{"favorite":"cyan"},"assets":{"schemaVersion":1,"status":"applied","phase":"startup","target":"g","fingerprint":"a".repeat(64),"completedAt":42,"mods":[{"id":"player-sf","version":"1.0.0"}]}})).unwrap();
        let path = dir.join("state.json");
        fs::write(&path, &bytes).unwrap();
        (path, bytes)
    }
    fn publish(root: &Path, files: &[PathBuf]) {
        for path in files { fs::copy(path, root.join(path.file_name().unwrap())).unwrap(); }
    }
    #[test]
    fn real_eml_content_write_disable_restores_original_pair_and_referenced_dat_sizes() {
        use std::rc::Rc;
        use kfc::{container::{KFCFile, KFCWriter}, guid::ContentHash, reflection::TypeRegistry};
        let root = std::env::temp_dir().join(format!("kiln-real-owner-{}", uuid::Uuid::new_v4()));
        fs::create_dir(&root).unwrap();
        fs::File::create(root.join("g.kfc")).unwrap();
        fs::File::create(root.join("g.kfc_resources")).unwrap();
        let types = Rc::new(TypeRegistry::default());
        let mut original: crate::transaction::Writer = KFCWriter::new(&root, "g", types.clone(), "fixture").unwrap();
        original.write_content(&ContentHash::from_data(b"original"), b"original").unwrap();
        original.finalize().unwrap();
        let original_header = fs::read(root.join("g.kfc")).unwrap();
        let original_resources = fs::read(root.join("g.kfc_resources")).unwrap();
        let path = camino::Utf8Path::from_path(&root).unwrap();
        crate::load::create_backup(path.join("g.kfc")).unwrap();
        let base = Rc::new(KFCFile::from_path(root.join("g.kfc.bak"), false).unwrap());
        let mut patch = crate::load::create_writer(path, "g", &types, &base).unwrap();
        patch.write_content(&ContentHash::from_data(b"new content"), b"new content").unwrap();
        patch.finalize().unwrap();
        assert!(owns_output(&root, "g").unwrap());
        assert!(crate::restore(path, "g"));
        assert_eq!(fs::read(root.join("g.kfc")).unwrap(), original_header);
        assert_eq!(fs::read(root.join("g.kfc_resources")).unwrap(), original_resources);
        let restored = KFCFile::from_path(root.join("g.kfc"), false).unwrap();
        for (index, container) in restored.containers().iter().enumerate() {
            assert_eq!(fs::metadata(root.join(format!("g_{index:03}.dat"))).unwrap().len(), container.size,
                "disabling content mods must not leave nonempty padding DAT files");
        }
        assert!(!owns_output(&root, "g").unwrap());
        fs::remove_dir_all(root).unwrap();
    }
    #[test]
    fn legacy_cache_and_foreign_backups_do_not_authorize_restore() {
        let (root, _) = fixture();
        fs::create_dir_all(root.join("kiln/eml/cache")).unwrap();
        fs::write(root.join("kiln/eml/cache/files.json"), "{}").unwrap();
        assert!(!owns_output(&root, "g").unwrap());
        fs::remove_dir_all(root).unwrap();
    }
    #[test]
    fn proof_requires_exact_published_header_resources_dat_and_original_backups() {
        for change in ["g.kfc", "g.kfc_resources", "g_010.dat", "g.kfc.bak", "g.kfc_resources.bak"] {
            let (root, files) = fixture();
            prepare(&root, "g", &files).unwrap();
            assert!(!owns_output(&root, "g").unwrap(), "staging is not publication");
            publish(&root, &files);
            assert!(owns_output(&root, "g").unwrap());
            fs::write(root.join(change), "player or other writer changed it").unwrap();
            assert!(!owns_output(&root, "g").unwrap(), "{change}");
            fs::remove_dir_all(root).unwrap();
        }
    }
    #[test]
    fn only_recognized_sf_cache_is_invalidated_and_exact_original_is_journalled() {
        for custom in [false, true] {
            let (root, files) = fixture();
            let (state, original) = sf(&root, custom);
            let config = fs::read(root.join("shroudforge/config/modloader-config.json")).unwrap();
            prepare(&root, "g", &files).unwrap();
            let current: serde_json::Value = serde_json::from_slice(&fs::read(&state).unwrap()).unwrap();
            let mut expected: serde_json::Value = serde_json::from_slice(&original).unwrap();
            expected["assets"]["status"] = "failed".into();
            expected["assets"]["error"] = current["assets"]["error"].clone();
            assert_eq!(current, expected, "all non-cache player state survives");
            assert_eq!(fs::read(root.join("shroudforge/config/modloader-config.json")).unwrap(), config);
            let journal = fs::read_dir(root.join("kiln/eml/ownership")).unwrap().flatten().find(|e| e.file_name().to_string_lossy().starts_with("sf-handoff-")).unwrap();
            let value: serde_json::Value = serde_json::from_slice(&fs::read(journal.path()).unwrap()).unwrap();
            assert_eq!(value["original"].as_str().unwrap().as_bytes(), original);
            assert_eq!(value["replacement"].as_str().unwrap().as_bytes(), fs::read(state).unwrap());
            fs::remove_dir_all(root).unwrap();
        }
    }
    #[test]
    fn unchanged_output_never_mutates_sf_state() {
        let (root, files) = fixture();
        publish(&root, &files);
        let (state, original) = sf(&root, false);
        prepare(&root, "g", &files).unwrap();
        assert_eq!(fs::read(state).unwrap(), original);
        assert_eq!(fs::read_dir(root.join("kiln/eml/ownership")).unwrap().count(), 1);
        fs::remove_dir_all(root).unwrap();
    }
    #[test]
    fn unknown_sf_schema_blocks_publication_without_modifying_state_or_game() {
        let (root, files) = fixture();
        let (state, _) = sf(&root, false);
        let bytes = fs::read_to_string(&state).unwrap().replace("\"schemaVersion\":1", "\"schemaVersion\":99");
        fs::write(&state, &bytes).unwrap();
        assert!(prepare(&root, "g", &files).is_err());
        assert_eq!(fs::read_to_string(state).unwrap(), bytes);
        assert_eq!(fs::read(root.join("g.kfc")).unwrap(), b"SF header");
        assert!(!owns_output(&root, "g").unwrap());
        fs::remove_dir_all(root).unwrap();
    }
    #[test]
    fn unknown_receipt_is_not_adopted() {
        let (root, files) = fixture();
        prepare(&root, "g", &files).unwrap(); publish(&root, &files);
        fs::write(root.join("kiln/eml/ownership/g.json"), r#"{"format":99,"stem":"g","files":{},"backups":{}}"#).unwrap();
        assert!(owns_output(&root, "g").is_err());
        let original = fs::read(root.join("kiln/eml/ownership/g.json")).unwrap();
        assert!(prepare(&root, "g", &files).is_err());
        assert_eq!(fs::read(root.join("kiln/eml/ownership/g.json")).unwrap(), original);
        fs::remove_dir_all(root).unwrap();
    }
    #[test]
    fn invalid_stem_never_creates_or_reads_a_receipt() {
        let (root, files) = fixture();
        for stem in ["", "../g", "g/other", r"g\other", "."] {
            assert!(owns_output(&root, stem).is_err());
            assert!(prepare(&root, stem, &files).is_err());
            assert!(before_restore(&root, stem).is_err());
        }
        assert!(!root.join("kiln").exists());
        fs::remove_dir_all(root).unwrap();
    }
    #[test]
    fn blocked_handoff_keeps_previous_output_proof_for_safe_retry() {
        let (root, files) = fixture();
        prepare(&root, "g", &files).unwrap(); publish(&root, &files);
        let original_receipt = fs::read(root.join("kiln/eml/ownership/g.json")).unwrap();
        let (state, _) = sf(&root, false);
        let original = fs::read_to_string(&state).unwrap().replace("\"schemaVersion\":1", "\"schemaVersion\":99");
        fs::write(&state, &original).unwrap();
        fs::write(&files[0], "next EML header").unwrap();
        assert!(prepare(&root, "g", &files).is_err());
        assert!(owns_output(&root, "g").unwrap());
        assert_eq!(fs::read_to_string(state).unwrap(), original);
        assert_eq!(fs::read(root.join("kiln/eml/ownership/g.json")).unwrap(), original_receipt);
        fs::remove_dir_all(root).unwrap();
    }
    #[test]
    fn busy_sf_writer_blocks_archive_handoff() {
        let (root, files) = fixture();
        let (state, original) = sf(&root, false);
        let lock = fs::File::create(root.join("shroudforge/config/.shroudforge-write.lock")).unwrap();
        lock.lock().unwrap();
        assert!(prepare(&root, "g", &files).is_err());
        assert_eq!(fs::read(state).unwrap(), original);
        drop(lock); fs::remove_dir_all(root).unwrap();
    }
    #[cfg(unix)]
    #[test]
    fn trusted_location_can_have_mounted_ancestors_but_not_linked_descendants() {
        let (root, _) = fixture();
        fs::create_dir_all(root.join("volume/game")).unwrap();
        std::os::unix::fs::symlink(root.join("volume"), root.join("mount")).unwrap();
        let game = root.join("mount/game");
        fs::write(game.join("safe"), "mounted volume").unwrap();
        assert!(checked_path(&game, &game.join("safe")).is_ok());
        std::os::unix::fs::symlink(game.join("safe"), game.join("linked")).unwrap();
        assert!(checked_path(&game, &game.join("linked")).is_err());
        assert!(checked_path(&root, &game.join("safe")).is_err());
        fs::remove_dir_all(root).unwrap();
    }
    #[cfg(windows)]
    #[test]
    fn logical_windows_game_root_accepts_ordinary_and_verbatim_paths() {
        let (root, files) = fixture();
        assert!(checked_path(&root, &files[0]).is_ok());
        let verbatim = root.canonicalize().unwrap();
        assert!(checked_path(&root, &verbatim.join("stage/g.kfc")).is_ok());
        assert!(checked_path(&verbatim, &root.join("stage/g.kfc")).is_ok());
        fs::remove_dir_all(root).unwrap();
    }
    #[cfg(any(unix, windows))]
    #[test]
    fn linked_state_and_linked_ownership_parent_are_not_followed() {
        for state_link in [false, true] {
            let (root, files) = fixture();
            let (state, original) = sf(&root, false);
            if state_link {
                fs::rename(&state, root.join("victim.json")).unwrap();
                #[cfg(unix)]
                std::os::unix::fs::symlink(root.join("victim.json"), &state).unwrap();
                #[cfg(windows)]
                std::os::windows::fs::symlink_file(root.join("victim.json"), &state).unwrap();
            } else {
                fs::create_dir_all(root.join("kiln/eml")).unwrap();
                fs::create_dir(root.join("victim")).unwrap();
                #[cfg(unix)]
                std::os::unix::fs::symlink(root.join("victim"), root.join("kiln/eml/ownership")).unwrap();
                #[cfg(windows)]
                std::os::windows::fs::symlink_dir(root.join("victim"), root.join("kiln/eml/ownership")).unwrap();
            }
            assert!(prepare(&root, "g", &files).is_err());
            assert_eq!(fs::read(&state).unwrap(), original);
            fs::remove_dir_all(root).unwrap();
        }
    }
}
