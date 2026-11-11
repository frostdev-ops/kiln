// SPDX-License-Identifier: GPL-3.0-or-later
// kiln-eml: build privately, then publish the header last. While installing data the live header is vanilla,
// whose containers and resource prefix are immutable. A crash at any publication boundary leaves a usable game.
use std::{fs::{self, File, OpenOptions}, io, path::{Path, PathBuf}, rc::Rc};
use kfc::{container::{KFCFile, KFCWriter, KFCWriteError}, reflection::TypeRegistry};

pub type Writer = KFCWriter<Rc<KFCFile>, Rc<TypeRegistry>>;
pub(crate) const VANILLA_HEADER: &str = ".vanilla-header";
#[cfg(test)]
thread_local! { pub(crate) static BEFORE_PUBLISH: std::cell::Cell<Option<fn(&Path)>> = const { std::cell::Cell::new(None) }; }
#[cfg(test)]
thread_local! { static TEMP_OPEN: std::cell::Cell<Option<fn(&Path)>> = const { std::cell::Cell::new(None) }; }
// kiln-eml: between the two copies of a restore (load.rs), for the tests that look at what is held meanwhile
#[cfg(test)]
thread_local! { pub(crate) static DURING_RESTORE: std::cell::Cell<Option<fn(&Path)>> = const { std::cell::Cell::new(None) }; }

pub fn regular(path: &Path) -> io::Result<()> {
    let m = fs::symlink_metadata(path)?;
    if !m.is_file() || m.file_type().is_symlink() {
        return Err(io::Error::other(format!("not a regular file: {}", path.display())));
    }
    #[cfg(windows)] {
        use std::os::windows::fs::MetadataExt;
        if (m.file_attributes() | attributes_by_name(path)?) & 0x400 != 0 { return Err(io::Error::other("reparse point")); }
    }
    Ok(())
}

/// kiln-eml: the attributes of `path` asked by name, under Wine (0 on Windows proper). Wine follows links made on the
/// Linux side even with OPEN_REPARSE_POINT, so the handle-based metadata above shows a plain folder there; asked by
/// name, Wine reports directory links as reparse points (discover.rs does the same). Wine shows file links to neither
/// query. Windows proper reports junctions and links through the handle query already, and a raw by-name query of a
/// path longer than MAX_PATH fails there where std's (verbatim) one works: a deep game folder must not stop every
/// publication.
#[cfg(windows)]
pub(crate) fn attributes_by_name(path: &Path) -> io::Result<u32> {
    use std::os::windows::ffi::OsStrExt;
    if !under_wine() { return Ok(0); }
    #[link(name = "kernel32")]
    unsafe extern "system" { fn GetFileAttributesW(path: *const u16) -> u32; }
    let name: Vec<u16> = path.as_os_str().encode_wide().chain(Some(0)).collect();
    let attributes = unsafe { GetFileAttributesW(name.as_ptr()) };
    if attributes == u32::MAX { return Err(io::Error::last_os_error()); }
    Ok(attributes)
}

pub(crate) fn sync_dir(dir: &Path) -> io::Result<()> {
    #[cfg(unix)] { File::open(dir)?.sync_all()?; }
    #[cfg(windows)] {
        if under_wine() {
            use std::os::windows::fs::OpenOptionsExt;
            // Wine's MoveFileEx ignores WRITE_THROUGH. A writable directory handle maps FlushFileBuffers to
            // fsync(dirfd), making the vanilla-header barrier and each subsequent rename durable on Unix too.
            OpenOptions::new().read(true).write(true).custom_flags(0x02000000) // FILE_FLAG_BACKUP_SEMANTICS
                .open(dir)?.sync_all()?;
        }
    }
    #[cfg(not(any(unix, windows)))] { let _ = dir; }
    Ok(())
}

#[cfg(windows)]
fn under_wine() -> bool {
    unsafe extern "system" {
        fn GetModuleHandleW(name: *const u16) -> *mut std::ffi::c_void;
        fn GetProcAddress(module: *mut std::ffi::c_void, name: *const u8) -> *mut std::ffi::c_void;
    }
    let name: Vec<u16> = "ntdll.dll".encode_utf16().chain(Some(0)).collect();
    let ntdll = unsafe { GetModuleHandleW(name.as_ptr()) };
    !ntdll.is_null() && !unsafe { GetProcAddress(ntdll, c"wine_get_version".as_ptr().cast()) }.is_null()
}

pub fn replace(src: &Path, dst: &Path) -> io::Result<()> {
    if dst.exists() { regular(dst)?; }
    #[cfg(windows)] {
        use std::os::windows::ffi::OsStrExt;
        unsafe extern "system" { fn MoveFileExW(a: *const u16, b: *const u16, flags: u32) -> i32; }
        let a: Vec<_> = src.as_os_str().encode_wide().chain(Some(0)).collect();
        let b: Vec<_> = dst.as_os_str().encode_wide().chain(Some(0)).collect();
        if unsafe { MoveFileExW(a.as_ptr(), b.as_ptr(), 1 | 8) } == 0 { return Err(io::Error::last_os_error()); }
    }
    #[cfg(not(windows))] { fs::rename(src, dst)?; }
    sync_dir(dst.parent().ok_or_else(|| io::Error::other("no parent"))?)
}

pub fn atomic_copy(src: &Path, dst: &Path) -> io::Result<()> {
    regular(src)?;
    if fs::symlink_metadata(dst).is_ok() { regular(dst)?; }
    let tmp = dst.with_extension(format!("kiln-{}", uuid::Uuid::new_v4()));
    let record = crate::cleanup::record_temp(dst, &tmp)?;
    let result = (|| {
        let mut f = OpenOptions::new().write(true).create_new(true).open(&tmp)?;
        #[cfg(test)]
        TEMP_OPEN.with(|check| { if let Some(check) = check.take() { check(&tmp); } });
        io::copy(&mut File::open(src)?, &mut f)?;
        f.sync_all()?;
        drop(f);
        replace(&tmp, dst)
    })();
    let _ = fs::remove_file(&tmp);
    if !tmp.try_exists().unwrap_or(true) {
        if let Some(record) = record {
            // The rename/removal must persist before its write-ahead cleanup proof can disappear.
            sync_dir(dst.parent().unwrap())?;
            fs::remove_file(record)?;
        }
    }
    result
}

pub fn atomic_write(dst: &Path, bytes: &[u8]) -> io::Result<()> {
    use std::io::Write;
    let tmp = dst.with_extension(format!("kiln-{}", uuid::Uuid::new_v4()));
    let record = crate::cleanup::record_temp(dst, &tmp)?;
    let result = (|| {
        let mut f = OpenOptions::new().write(true).create_new(true).open(&tmp)?;
        f.write_all(bytes)?;
        f.sync_all()?;
        drop(f);
        replace(&tmp, dst)
    })();
    let _ = fs::remove_file(&tmp);
    if !tmp.try_exists().unwrap_or(true) {
        if let Some(record) = record {
            // The rename/removal must persist before its write-ahead cleanup proof can disappear.
            sync_dir(dst.parent().unwrap())?;
            fs::remove_file(record)?;
        }
    }
    result
}

pub struct PatchWriter {
    pub writer: Option<Writer>,
    pub stage: PathBuf,
    pub game: PathBuf,
    pub name: String,
}
impl std::ops::Deref for PatchWriter {
    type Target = Writer;
    fn deref(&self) -> &Writer { self.writer.as_ref().unwrap() }
}
impl std::ops::DerefMut for PatchWriter {
    fn deref_mut(&mut self) -> &mut Writer { self.writer.as_mut().unwrap() }
}
impl Drop for PatchWriter {
    fn drop(&mut self) {
        self.writer.take();
        if let Some(name) = self.stage.file_name().and_then(|n| n.to_str()) { crate::cleanup::finish_stage(&self.game, name); }
    }
}
impl PatchWriter {
    pub fn finalize(mut self) -> Result<(), KFCWriteError> {
        self.writer.take().unwrap().finalize()?;
        #[cfg(test)]
        BEFORE_PUBLISH.with(|check| { if let Some(check) = check.take() { check(&self.game); } });
        self.publish(|_| Ok(()))?;
        Ok(())
    }

    // Hook is used by crash-boundary tests; production never injects a failure.
    fn publish(&self, mut boundary: impl FnMut(usize) -> io::Result<()>) -> io::Result<()> {
        let header = format!("{}.kfc", self.name);
        let mut data = fs::read_dir(&self.stage)?.map(|e| e.map(|e| e.path())).collect::<io::Result<Vec<_>>>()?;
        data.retain(|p| p.file_name() != Some(std::ffi::OsStr::new(VANILLA_HEADER)));
        data.sort();
        for p in &data { regular(p)?; OpenOptions::new().read(true).write(true).open(p)?.sync_all()?; }
        // SF's locks (when its prepared-output state was handed over) stay held until the patched header is live
        let _sf = crate::ownership::prepare(&self.game, &self.name, &data)?;
        // No old patched header may refer to the containers we are about to replace.
        atomic_copy(&self.stage.join(VANILLA_HEADER), &self.game.join(&header))?;
        boundary(0)?;
        let mut n = 1;
        for p in data.iter().filter(|p| p.file_name() != Some(std::ffi::OsStr::new(&header))) {
            replace(p, &self.game.join(p.file_name().unwrap()))?;
            boundary(n)?;
            n += 1;
        }
        replace(&self.stage.join(&header), &self.game.join(&header))?;
        // the new set is what the game runs now: the previous generation is no proof any more, and the containers only
        // it had go. A failure here leaves the receipt's previous generation (it can only match if those exact files
        // come back) or those containers (unreferenced) in place; the publication stands.
        if let Err(error) = crate::ownership::committed(&self.game, &self.name) {
            tracing::warn!(%error, "EML output published; tidying up after the output it replaced did not finish");
        }
        boundary(n)
    }
}

#[cfg(test)]
pub(crate) mod tests {
    use super::*;
    #[test]
    fn killed_resource_temp_has_durable_cleanup_proof_before_any_copy() {
        let game = std::env::temp_dir().join(format!("kiln-temp-proof-{}", uuid::Uuid::new_v4()));
        fs::create_dir(&game).unwrap();
        fs::write(game.join("g.kfc_resources"), "live resources").unwrap();
        TEMP_OPEN.with(|check| check.set(Some(|tmp| {
            let root = tmp.parent().unwrap();
            let records: Vec<_> = fs::read_dir(root.join("kiln/eml/ownership")).unwrap().flatten().collect();
            assert_eq!(records.len(), 1);
            let proof: serde_json::Value = serde_json::from_slice(&fs::read(records[0].path()).unwrap()).unwrap();
            assert_eq!(proof["name"], tmp.file_name().unwrap().to_str().unwrap());
            assert_eq!(fs::metadata(tmp).unwrap().len(), 0, "proof precedes staged bytes");
            fs::write(tmp, "half copied").unwrap();
            panic!("simulated killed copy");
        })));
        let killed = std::panic::catch_unwind(|| atomic_copy(&game.join("g.kfc_resources"), &game.join("g.kfc_resources.bak")));
        assert!(killed.is_err());
        crate::prune(&game, "g");
        assert_eq!(fs::read_dir(game.join("kiln/eml/ownership")).unwrap().count(), 0);
        assert!(crate::recovery_guidance(&game, "g").unwrap().is_empty());
        assert_eq!(fs::read(game.join("g.kfc_resources")).unwrap(), b"live resources");
        assert!(!game.join("g.kfc_resources.bak").exists());
        fs::remove_dir_all(game).unwrap();
    }
    #[test]
    fn directory_barrier_propagates_sync_errors() {
        #[cfg(windows)]
        if !under_wine() { return; } // native Windows uses MoveFileEx's WRITE_THROUGH instead
        let absent = std::env::temp_dir().join(format!("kiln-missing-directory-{}", uuid::Uuid::new_v4()));
        assert!(sync_dir(&absent).is_err());
    }
    #[test]
    fn interrupted_publication_keeps_vanilla_until_header_is_ready() {
        for fail in 0..=3 {
            let game = std::env::temp_dir().join(format!("kiln-transaction-{}", uuid::Uuid::new_v4()));
            let stage = game.join("stage");
            fs::create_dir_all(&stage).unwrap();
            for (n, b) in [("g.kfc", "old patch"), ("g.kfc.bak", "vanilla"), ("g.kfc_resources", "base old")] {
                fs::write(game.join(n), b).unwrap();
            }
            for (n, b) in [("g.kfc", "new patch"), ("g.kfc_resources", "base new"), ("g_002.dat", "new")] {
                fs::write(stage.join(n), b).unwrap();
            }
            fs::write(stage.join(VANILLA_HEADER), "vanilla").unwrap();
            let w = PatchWriter { writer: None, game: game.clone(), stage, name: "g".into() };
            assert!(w.publish(|n| if n == fail { Err(io::Error::other("power loss")) } else { Ok(()) }).is_err());
            assert_eq!(fs::read(game.join("g.kfc")).unwrap(), if fail == 3 { b"new patch".as_slice() } else { b"vanilla".as_slice() });
            assert!(fs::read(game.join("g.kfc_resources")).unwrap().starts_with(b"base"));
            assert_eq!(crate::owns_output(&game, "g").unwrap(), fail == 3,
                "a partial publication must never authorize restoring shared backups");
            drop(w);
            fs::remove_dir_all(game).unwrap();
        }
    }
    // The receipt is written ahead of the first change. Until that change happens, the previous (owned) output is what
    // is live: it must stay provable, or a player who then switches EML off keeps the old patch for good.
    #[test]
    fn a_publication_that_fails_before_its_first_change_keeps_the_previous_proof() {
        let game = std::env::temp_dir().join(format!("kiln-generations-{}", uuid::Uuid::new_v4()));
        fs::create_dir_all(&game).unwrap();
        for (n, b) in [("g.kfc", "vanilla"), ("g.kfc.bak", "vanilla"), ("g.kfc_resources", "base"), ("g.kfc_resources.bak", "base")] {
            fs::write(game.join(n), b).unwrap();
        }
        let stage = |tag: &str, vanilla: bool| {
            let stage = game.join(format!("stage-{tag}"));
            fs::create_dir_all(&stage).unwrap();
            for (n, b) in [("g.kfc", "patch"), ("g.kfc_resources", "base+"), ("g_002.dat", "data")] {
                fs::write(stage.join(n), format!("{b} {tag}")).unwrap();
            }
            if vanilla { fs::write(stage.join(VANILLA_HEADER), "vanilla").unwrap(); }
            PatchWriter { writer: None, game: game.clone(), stage, name: "g".into() }
        };
        stage("one", true).publish(|_| Ok(())).unwrap();
        assert!(crate::owns_output(&game, "g").unwrap());
        // the first step (the vanilla header) fails: nothing of the new set is live
        assert!(stage("two", false).publish(|_| Ok(())).is_err());
        assert_eq!(fs::read(game.join("g.kfc")).unwrap(), b"patch one");
        assert!(crate::owns_output(&game, "g").unwrap(), "the live previous output is still EML's");
        // a publication that completes replaces the proof: the older set is no longer EML's current output
        stage("three", true).publish(|_| Ok(())).unwrap();
        assert!(crate::owns_output(&game, "g").unwrap());
        for (n, b) in [("g.kfc", "patch one"), ("g.kfc_resources", "base+ one"), ("g_002.dat", "data one")] { fs::write(game.join(n), b).unwrap(); }
        assert!(!crate::owns_output(&game, "g").unwrap(), "only the last published generation is proof");
        fs::remove_dir_all(game).unwrap();
    }
    /// Is ShroudForge's write lock in `game` held by someone (this process's handoff guard, in these tests)?
    pub(crate) fn sf_write_lock_busy(game: &Path) -> bool {
        OpenOptions::new().read(true).write(true).open(game.join("shroudforge/config/.shroudforge-write.lock"))
            .is_ok_and(|f| f.try_lock().is_err())
    }
    /// ShroudForge's state after it prepared asset mods for `stem` (asset-preparation.schema.json).
    pub(crate) fn sf_applied(game: &Path, stem: &str) {
        fs::create_dir_all(game.join("shroudforge/config")).unwrap();
        fs::write(game.join("shroudforge/state.json"), serde_json::to_vec(&serde_json::json!({"schemaVersion":1,"assets":{
            "schemaVersion":1,"status":"applied","phase":"startup","target":stem,"fingerprint":"a".repeat(64),
            "completedAt":42,"mods":[{"id":"sf-mod","version":"1.0.0"}]}})).unwrap()).unwrap();
    }
    // SF writes `preparing` under its write lock before its own preparation touches an archive: the handoff's lock is
    // held from the state edit through every step of the publication.
    #[test]
    fn the_sf_write_lock_is_held_at_every_publication_boundary() {
        let game = std::env::temp_dir().join(format!("kiln-sf-lock-{}", uuid::Uuid::new_v4()));
        let stage = game.join("stage");
        fs::create_dir_all(&stage).unwrap();
        for (n, b) in [("g.kfc", "old patch"), ("g.kfc.bak", "vanilla"), ("g.kfc_resources", "base old")] {
            fs::write(game.join(n), b).unwrap();
        }
        for (n, b) in [("g.kfc", "new patch"), ("g.kfc_resources", "base new"), ("g_002.dat", "new")] { fs::write(stage.join(n), b).unwrap(); }
        fs::write(stage.join(VANILLA_HEADER), "vanilla").unwrap();
        sf_applied(&game, "g");
        let w = PatchWriter { writer: None, game: game.clone(), stage, name: "g".into() };
        let mut seen = Vec::new();
        w.publish(|n| { seen.push((n, sf_write_lock_busy(&game))); Ok(()) }).unwrap();
        assert_eq!(seen, [(0, true), (1, true), (2, true), (3, true)], "held at every boundary");
        assert!(!sf_write_lock_busy(&game), "released once published");
        drop(w);
        fs::remove_dir_all(game).unwrap();
    }
    #[test]
    fn sf_lock_also_covers_missing_state_and_byte_identical_publications() {
        for unchanged in [true, false] {
            let game = std::env::temp_dir().join(format!("kiln-sf-all-writes-{}", uuid::Uuid::new_v4()));
            let stage = game.join("stage");
            fs::create_dir_all(&stage).unwrap();
            for (name, old, new) in [("g.kfc", "old patch", "new patch"), ("g.kfc_resources", "base old", "base new")] {
                fs::write(game.join(name), if unchanged { new } else { old }).unwrap();
                fs::write(stage.join(name), new).unwrap();
            }
            fs::write(game.join("g.kfc.bak"), "vanilla").unwrap();
            fs::write(stage.join(VANILLA_HEADER), "vanilla").unwrap();
            fs::create_dir_all(game.join("shroudforge")).unwrap();
            if unchanged { sf_applied(&game, "g"); }
            let state = game.join("shroudforge/state.json");
            let original = fs::read(&state).ok();
            let w = PatchWriter { writer: None, game: game.clone(), stage, name: "g".into() };
            w.publish(|n| {
                assert!(sf_write_lock_busy(&game), "SF lock missing at boundary {n}, unchanged={unchanged}");
                Ok(())
            }).unwrap();
            assert_eq!(fs::read(&state).ok(), original, "no state invented or unchanged claim invalidated");
            assert!(!sf_write_lock_busy(&game));
            drop(w);
            fs::remove_dir_all(game).unwrap();
        }
    }
    #[test]
    fn busy_sf_without_state_refuses_before_any_archive_write() {
        let game = std::env::temp_dir().join(format!("kiln-sf-first-start-{}", uuid::Uuid::new_v4()));
        let stage = game.join("stage");
        fs::create_dir_all(&stage).unwrap();
        fs::create_dir_all(game.join("shroudforge/config")).unwrap();
        let lock = File::create(game.join("shroudforge/config/.shroudforge-write.lock")).unwrap();
        lock.lock().unwrap();
        for name in ["g.kfc", "g.kfc_resources"] {
            fs::write(game.join(name), "old").unwrap();
            fs::write(stage.join(name), "new").unwrap();
        }
        fs::write(game.join("g.kfc.bak"), "vanilla").unwrap();
        fs::write(stage.join(VANILLA_HEADER), "vanilla").unwrap();
        let w = PatchWriter { writer: None, game: game.clone(), stage, name: "g".into() };
        assert!(w.publish(|_| panic!("busy SF allowed an archive write")).is_err());
        assert_eq!(fs::read(game.join("g.kfc")).unwrap(), b"old");
        drop((w, lock));
        fs::remove_dir_all(game).unwrap();
    }
    #[test]
    fn real_kfc_is_readable_at_every_publication_boundary() {
        use kfc::{container::KFCReader, guid::ContentHash};
        for fail in 0..=3 {
            let game = std::env::temp_dir().join(format!("kiln-real-transaction-{}", uuid::Uuid::new_v4()));
            fs::create_dir(&game).unwrap();
            File::create(game.join("g.kfc")).unwrap();
            File::create(game.join("g.kfc_resources")).unwrap();
            let types = Rc::new(TypeRegistry::default());
            let mut vanilla = Writer::new(&game, "g", types.clone(), "fixture").unwrap();
            vanilla.write_content(&ContentHash::from_data(b"original"), b"original").unwrap();
            vanilla.finalize().unwrap();
            let header = fs::read(game.join("g.kfc")).unwrap();
            let resources = fs::read(game.join("g.kfc_resources")).unwrap();
            let dir = camino::Utf8Path::from_path(&game).unwrap();
            crate::load::create_backup(dir.join("g.kfc")).unwrap();
            let base = Rc::new(KFCFile::from_path(game.join("g.kfc.bak"), false).unwrap());
            let mut patch = crate::load::create_writer(dir, "g", &types, &base).unwrap();
            patch.write_content(&ContentHash::from_data(b"new content"), b"new content").unwrap();
            patch.writer.take().unwrap().finalize().unwrap();
            // The barrier must use the validated private snapshot, even if the public backup changes mid-run.
            fs::write(game.join("g.kfc.bak"), b"damaged after preparation").unwrap();
            assert!(patch.publish(|n| if n == fail { Err(io::Error::other("interrupted")) } else { Ok(()) }).is_err());
            let live = KFCFile::from_path(game.join("g.kfc"), false).unwrap();
            for (index, info) in live.containers().iter().enumerate() {
                assert_eq!(game.join(format!("g_{index:03}.dat")).metadata().unwrap().len(), info.size);
            }
            assert!(fs::read(game.join("g.kfc_resources")).unwrap().starts_with(&resources));
            let mut reader = KFCReader::new(&game, "g").unwrap().into_cursor().unwrap();
            assert_eq!(reader.read_content(&ContentHash::from_data(b"original")).unwrap().unwrap(), b"original");
            if fail < 3 { assert_eq!(fs::read(game.join("g.kfc")).unwrap(), header); }
            else { assert_eq!(reader.read_content(&ContentHash::from_data(b"new content")).unwrap().unwrap(), b"new content"); }
            drop(reader);
            let stage = patch.stage.clone();
            std::mem::forget(patch); // abrupt exit: no PatchWriter::drop
            crate::prune(&game, "g");
            assert!(!stage.exists(), "restart reclaims staged bytes at publication boundary {fail}");
            assert!(crate::recovery_guidance(&game, "g").unwrap().is_empty());
            fs::remove_dir_all(game).unwrap();
        }
    }
    // A game folder deep enough that its stage files pass MAX_PATH (Steam libraries in long folder names).
    #[cfg(windows)]
    #[test]
    fn a_long_game_folder_path_passes_the_link_checks() {
        let mut game = std::env::temp_dir().join(format!("kiln-long-{}", uuid::Uuid::new_v4()));
        while game.as_os_str().len() < 300 { game = game.join("a-rather-long-steam-library-folder-name"); }
        fs::create_dir_all(&game).unwrap();
        let file = game.join(".kiln-eml-stage-0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0").join("enshrouded.kfc_resources");
        fs::create_dir_all(file.parent().unwrap()).unwrap();
        fs::write(&file, "staged").unwrap();
        regular(&file).unwrap();
        crate::ownership::tests::checked(&game, &file).unwrap();
        let mut top = game.as_path();
        while top.parent() != Some(&std::env::temp_dir()) { top = top.parent().unwrap(); }
        fs::remove_dir_all(top).unwrap();
    }
    #[cfg(unix)]
    #[test]
    fn atomic_copy_rejects_symlink_backup() {
        let d = std::env::temp_dir().join(format!("kiln-copy-{}", uuid::Uuid::new_v4()));
        fs::create_dir(&d).unwrap();
        fs::write(d.join("src"), "original").unwrap();
        fs::write(d.join("victim"), "keep").unwrap();
        std::os::unix::fs::symlink(d.join("victim"), d.join("dst")).unwrap();
        assert!(atomic_copy(&d.join("src"), &d.join("dst")).is_err());
        assert_eq!(fs::read(d.join("victim")).unwrap(), b"keep");
        fs::remove_dir_all(d).unwrap();
    }
}
