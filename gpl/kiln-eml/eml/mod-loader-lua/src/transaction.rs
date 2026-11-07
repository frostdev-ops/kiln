// SPDX-License-Identifier: GPL-3.0-or-later
// kiln-eml: build privately, then publish the header last. While installing data the live header is vanilla,
// whose containers and resource prefix are immutable. A crash at any publication boundary leaves a usable game.
use std::{fs::{self, File, OpenOptions}, io, path::{Path, PathBuf}, rc::Rc};
use kfc::{container::{KFCFile, KFCWriter, KFCWriteError}, reflection::TypeRegistry};

pub type Writer = KFCWriter<Rc<KFCFile>, Rc<TypeRegistry>>;

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
    #[cfg(not(unix))] { let _ = dir; }
    Ok(())
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
        self.publish(|_| Ok(()))?;
        Ok(())
    }

    // Hook is used by crash-boundary tests; production never injects a failure.
    fn publish(&self, mut boundary: impl FnMut(usize) -> io::Result<()>) -> io::Result<()> {
        let header = format!("{}.kfc", self.name);
        let mut data = fs::read_dir(&self.stage)?.map(|e| e.map(|e| e.path())).collect::<io::Result<Vec<_>>>()?;
        data.sort();
        for p in &data { regular(p)?; OpenOptions::new().read(true).write(true).open(p)?.sync_all()?; }
        // No old patched header may refer to the containers we are about to replace.
        atomic_copy(&self.game.join(format!("{header}.bak")), &self.game.join(&header))?;
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
            let w = PatchWriter { writer: None, game: game.clone(), stage, name: "g".into() };
            assert!(w.publish(|n| if n == fail { Err(io::Error::other("power loss")) } else { Ok(()) }).is_err());
            assert_eq!(fs::read(game.join("g.kfc")).unwrap(), if fail == 3 { b"new patch".as_slice() } else { b"vanilla".as_slice() });
            assert!(fs::read(game.join("g.kfc_resources")).unwrap().starts_with(b"base"));
            drop(w);
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
