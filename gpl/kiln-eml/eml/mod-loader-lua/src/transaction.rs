// SPDX-License-Identifier: GPL-3.0-or-later
// kiln-eml: build privately, then publish the header last. While installing data the live header is vanilla,
// whose containers and resource prefix are immutable. A crash at any publication boundary leaves a usable game.
use std::{fs::{self, File, OpenOptions}, io, path::{Path, PathBuf}, rc::Rc};
use kfc::{container::{KFCFile, KFCWriter, KFCWriteError}, reflection::TypeRegistry};

pub type Writer = KFCWriter<Rc<KFCFile>, Rc<TypeRegistry>>;
pub(crate) const VANILLA_HEADER: &str = ".vanilla-header";
#[cfg(test)]
thread_local! { pub(crate) static BEFORE_PUBLISH: std::cell::Cell<Option<fn(&Path)>> = const { std::cell::Cell::new(None) }; }

pub fn regular(path: &Path) -> io::Result<()> {
    let m = fs::symlink_metadata(path)?;
    if !m.is_file() || m.file_type().is_symlink() {
        return Err(io::Error::other(format!("not a regular file: {}", path.display())));
    }
    #[cfg(windows)] {
        use std::os::windows::fs::MetadataExt;
        if m.file_attributes() & 0x400 != 0 { return Err(io::Error::other("reparse point")); }
    }
    Ok(())
}

fn sync_dir(dir: &Path) -> io::Result<()> {
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
    let result = (|| {
        let mut f = OpenOptions::new().write(true).create_new(true).open(&tmp)?;
        io::copy(&mut File::open(src)?, &mut f)?;
        f.sync_all()?;
        drop(f);
        replace(&tmp, dst)
    })();
    let _ = fs::remove_file(tmp);
    result
}

pub fn atomic_write(dst: &Path, bytes: &[u8]) -> io::Result<()> {
    use std::io::Write;
    let tmp = dst.with_extension(format!("kiln-{}", uuid::Uuid::new_v4()));
    let result = (|| {
        let mut f = OpenOptions::new().write(true).create_new(true).open(&tmp)?;
        f.write_all(bytes)?;
        f.sync_all()?;
        drop(f);
        replace(&tmp, dst)
    })();
    let _ = fs::remove_file(tmp);
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
    fn drop(&mut self) { self.writer.take(); let _ = fs::remove_dir_all(&self.stage); }
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
        boundary(n)
    }
}

#[cfg(test)]
mod tests {
    use super::*;
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
            for (n, b) in [("g.kfc", "old patch"), ("g.kfc.bak", "vanilla"), ("g.kfc_resources", "base old"), ("g_002.dat", "old")] {
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
            drop(patch);
            fs::remove_dir_all(game).unwrap();
        }
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
