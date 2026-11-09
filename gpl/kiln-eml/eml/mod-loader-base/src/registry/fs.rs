use std::{collections::HashMap, fs::File, io::{BufReader, Error, ErrorKind, Read, Result}};

use walkdir::WalkDir;
use zip::ZipArchive;

use crate::{alias::{Path, PathBuf}, log::debug};

pub trait FileRead: Read {}

impl<R: Read> FileRead for R {}

#[derive(Debug)]
pub enum FileSystem {
    Disk(DiskFileSystem),
    Zip(ZipFileSystem),
    Empty,
}

impl FileSystem {

    pub fn new_disk<P: AsRef<Path>>(root: P) -> Result<Self> {
        DiskFileSystem::new(root.as_ref()).map(Self::Disk)
    }

    pub fn new_zip<P: AsRef<Path>>(path: P) -> Result<Self> {
        ZipFileSystem::new(path.as_ref()).map(Self::Zip)
    }

    pub fn root(&self) -> &Path {
        match self {
            Self::Disk(fs) => fs.root(),
            Self::Zip(fs) => fs.root(),
            Self::Empty => Path::new(""),
        }
    }

    pub fn files(&self) -> Vec<Result<PathBuf>> {
        let files = match self {
            Self::Disk(fs) => fs.files(),
            Self::Zip(fs) => fs.files(),
            Self::Empty => Vec::new(),
        };

        debug!(
            event = "files",
            result = ?files,
            root = self.root().as_str(),
        );

        files
    }

    pub fn read_directory<P: AsRef<Path>>(
        &self,
        path: P,
    ) -> Result<Vec<Result<PathBuf>>> {
        let files = match self {
            Self::Disk(fs) => fs.read_directory(path.as_ref()),
            Self::Zip(fs) => fs.read_directory(path.as_ref()),
            Self::Empty => {
                Err(Error::new(
                    ErrorKind::NotFound,
                    "No file system available"
                ))
            }
        };

        debug!(
            event = "read_directory",
            result = ?files,
            path = path.as_ref().as_str(),
            root = self.root().as_str(),
        );

        files
    }

    pub fn read_file<P: AsRef<Path>>(
        &mut self,
        path: P,
    ) -> Result<Box<dyn FileRead + '_>> {
        let root = if tracing::enabled!(tracing::Level::DEBUG) {
            self.root().to_owned()
        } else {
            PathBuf::new()
        };

        let file = match self {
            Self::Disk(fs) => fs.read_file(path.as_ref()),
            Self::Zip(fs) => fs.read_file(path.as_ref()),
            Self::Empty => {
                Err(Error::new(
                    ErrorKind::NotFound,
                    "No file system available"
                ))
            }
        };

        debug!(
            event = "read_file",
            result = file.is_ok(),
            path = path.as_ref().as_str(),
            root = root.as_str(),
        );

        file
    }

    pub fn exists<P: AsRef<Path>>(
        &self,
        path: P,
    ) -> bool {
        let result = match self {
            Self::Disk(fs) => fs.exists(path.as_ref()),
            Self::Zip(fs) => fs.exists(path.as_ref()),
            Self::Empty => false,
        };

        debug!(
            event = "exists",
            result,
            path = path.as_ref().as_str(),
            root = self.root().as_str(),
        );

        result
    }

    pub fn is_file<P: AsRef<Path>>(
        &mut self,
        path: P,
    ) -> bool {
        let result = match self {
            Self::Disk(fs) => fs.is_file(path.as_ref()),
            Self::Zip(fs) => fs.is_file(path.as_ref()),
            Self::Empty => false,
        };

        debug!(
            event = "is_file",
            result,
            exists = self.exists(path.as_ref()),
            path = path.as_ref().as_str(),
            root = self.root().as_str(),
        );

        result
    }

    pub fn is_directory<P: AsRef<Path>>(
        &mut self,
        path: P,
    ) -> bool {
        let result = match self {
            Self::Disk(fs) => fs.is_directory(path.as_ref()),
            Self::Zip(fs) => fs.is_directory(path.as_ref()),
            Self::Empty => false,
        };

        debug!(
            event = "is_directory",
            result,
            exists = self.exists(path.as_ref()),
            path = path.as_ref().as_str(),
            root = self.root().as_str(),
        );

        result
    }

    pub fn absolute_path<P: AsRef<Path>>(
        &self,
        path: P,
    ) -> Result<Option<PathBuf>> {
        let abs_path = match self {
            Self::Disk(fs) => Some(sanitize_path(fs.root(), path.as_ref())?),
            Self::Zip(_) => None,
            Self::Empty => None,
        };

        debug!(
            event = "absolute_path",
            result = ?abs_path,
            path = path.as_ref().as_str(),
            root = self.root().as_str(),
        );

        Ok(abs_path)
    }

    /// kiln-eml: reads `path` completely, the way `require` does (open, then read to a UTF-8 string), and says which
    /// stage failed when that does not work. Kiln's preflight calls this for each mod's entry module before the long
    /// game-data phase, through the same root and `sanitize_path` EML reads with later. Returns the size in bytes.
    pub fn probe_read<P: AsRef<Path>>(
        &mut self,
        path: P,
    ) -> std::result::Result<usize, ReadFailure> {
        let path = path.as_ref();
        let read = self.read_file(path)
            .and_then(std::io::read_to_string);

        match read {
            Ok(text) => Ok(text.len()),
            Err(e) => Err(self.diagnose(path, &e)),
        }
    }

    /// kiln-eml: names the stage at which reading `path` fails, from the raw OS errors: root metadata, directory
    /// metadata (each folder below the root), file metadata, canonicalize, the containment/link check, open, read.
    /// `failed` is the error the caller already got; it is reported when every stage passes now (a transient failure).
    pub fn diagnose<P: AsRef<Path>>(
        &self,
        path: P,
        failed: &Error,
    ) -> ReadFailure {
        match self {
            Self::Disk(fs) => diagnose_disk(fs.root(), path.as_ref(), failed),
            Self::Zip(fs) => diagnose_zip(fs, path.as_ref(), failed),
            Self::Empty => ReadFailure {
                stage: "file system".to_string(),
                error: failed.to_string(),
                state: "no file system available".to_string(),
                missing: false,
            },
        }
    }

}

/// kiln-eml: why reading one file of a mod failed. `Display`: `<stage> failed: <raw error>; <state>`, for example
/// `directory metadata (src) failed: Path not found (os error 3); root exists, src missing`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ReadFailure {
    /// The first stage that fails, in order: `root metadata`, `directory metadata (<folder>)`, `file metadata`,
    /// `canonicalize`, `containment check`, `open`, `read`.
    pub stage: String,
    /// The raw OS error text of that stage.
    pub error: String,
    /// Which parts of the path exist right now: `root exists, src missing`.
    pub state: String,
    /// The mod's root and its `src` folder are there, only the file (or a folder below `src`) is not: the path names
    /// nothing this mod has (a typo in a `require`, or a module another version of the mod has). False when the mod's
    /// own files cannot be read (root or `src` gone, a file that cannot be opened or read, a link leaving the mod).
    pub missing: bool,
}

impl std::fmt::Display for ReadFailure {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(f, "{} failed: {}; {}", self.stage, self.error, self.state)
    }
}

impl std::error::Error for ReadFailure {}

#[derive(Debug)]
pub struct DiskFileSystem {
    root: PathBuf,
}

impl DiskFileSystem {

    fn new(root: &Path) -> Result<Self> {
        Ok(Self {
            root: root.canonicalize_utf8()?
        })
    }

    fn root(&self) -> &Path {
        &self.root
    }

    fn files(&self) -> Vec<Result<PathBuf>> {
        WalkDir::new(self.root.as_std_path())
            .follow_links(false)
            .follow_root_links(false)
            .into_iter()
            .map(|entry| {
                match entry {
                    Ok(e) => PathBuf::try_from(e.into_path())
                        .map_err(Error::other),
                    Err(e) => Err(e.into_io_error()
                        .expect("Failed to convert error to IO error")),
                }
            })
            .collect::<Vec<_>>()
    }

    fn read_directory(
        &self,
        path: &Path,
    ) -> Result<Vec<Result<PathBuf>>> {
        let path = sanitize_path(&self.root, path)?;

        if !path.is_dir() {
            return Err(Error::from(ErrorKind::NotADirectory));
        }

        let mut results = Vec::new();

        for entry in path.read_dir()? {
            match entry {
                Ok(e) => {
                    let p = match PathBuf::try_from(e.path()) {
                        Ok(p) => p,
                        Err(e) => {
                            results.push(Err(Error::other(e)));
                            continue;
                        }
                    };

                    let p = match p.strip_prefix(&self.root) {
                        Ok(p) => p.to_path_buf(),
                        Err(e) => {
                            results.push(Err(Error::other(e)));
                            continue;
                        }
                    };

                    results.push(Ok(p));
                }
                Err(e) => {
                    results.push(Err(e));
                }
            }
        }

        Ok(results)
    }

    fn read_file(
        &mut self,
        path: &Path
    ) -> Result<Box<dyn FileRead>> {
        let path = sanitize_path(&self.root, path)?;

        if !path.is_file() {
            return Err(Error::from(ErrorKind::NotFound));
        }

        Ok(Box::new(File::open(path)?))
    }

    fn exists(
        &self,
        path: &Path,
    ) -> bool {
        sanitize_path(&self.root, path)
            .map(|p| p.exists())
            .unwrap_or(false)
    }

    fn is_file(
        &self,
        path: &Path,
    ) -> bool {
        sanitize_path(&self.root, path)
            .map(|p| p.is_file())
            .unwrap_or(false)
    }

    fn is_directory(
        &self,
        path: &Path,
    ) -> bool {
        sanitize_path(&self.root, path)
            .map(|p| p.is_dir())
            .unwrap_or(false)
    }

}

#[derive(Debug)]
pub struct ZipFileSystem {
    root: PathBuf,
    archive: ZipArchive<BufReader<File>>,
    node: TreeNode,
}

impl ZipFileSystem {

    fn new(path: &Path) -> Result<Self> {
        let file = File::open(path)?;
        let mut archive = ZipArchive::new(BufReader::new(file))?;

        let mut node = TreeNode::new();

        for i in 0..archive.len() {
            let file = archive.by_index(i)?;

            let path = file.name();
            let is_file = file.is_file();

            let components: Vec<&str> = Path::new(path)
                .components()
                .map(|c| c.as_str())
                .filter(|s| !s.is_empty())
                .collect();

            node.insert_path(&components, Some(i), is_file)?;
        }

        Ok(Self {
            root: path.into(),
            archive,
            node,
        })
    }

    fn root(&self) -> &Path {
        &self.root
    }

    fn files(&self) -> Vec<Result<PathBuf>> {
        self.archive.file_names()
            .map(|name| Ok(PathBuf::from(name)))
            .collect()
    }

    fn read_directory(
        &self,
        path: &Path,
    ) -> Result<Vec<Result<PathBuf>>> {
        let node = self.node.get(path)
            .ok_or_else(|| Error::from(ErrorKind::NotFound))?;

        Ok(node.iter()
            .map(|name| path.join(name))
            .map(|name| Ok(PathBuf::from(name)))
            .collect::<Vec<_>>())
    }

    fn read_file(
        &mut self,
        path: &Path,
    ) -> Result<Box<dyn FileRead + '_>> {
        let node = self.node.get(path)
            .ok_or_else(|| Error::from(ErrorKind::NotFound))?;
        let index = node.index
            .ok_or_else(|| Error::from(ErrorKind::NotFound))?;
        let file = self.archive.by_index(index)
            .map_err(Error::other)?;

        Ok(Box::new(file))
    }

    fn exists(
        &self,
        path: &Path,
    ) -> bool {
        self.node.get(path).is_some()
    }

    fn is_file(
        &mut self,
        path: &Path,
    ) -> bool {
        self.node.get(path)
            .map(|n| n.is_file)
            .unwrap_or(false)
    }

    fn is_directory(
        &mut self,
        path: &Path,
    ) -> bool {
        self.node.get(path)
            .map(|n| !n.is_file)
            .unwrap_or(false)
    }

}

#[derive(Debug)]
struct TreeNode {
    children: HashMap<String, TreeNode>,
    index: Option<usize>,
    is_file: bool,
}

impl TreeNode {

    fn new() -> Self {
        Self {
            children: HashMap::new(),
            index: None,
            is_file: false,
        }
    }

    fn insert_path(
        &mut self,
        components: &[&str],
        index: Option<usize>,
        is_file: bool,
    ) -> Result<()> {
        if components.is_empty() {
            return Ok(());
        }

        let component = components[0];

        if let Some(existing) = self.children.get_mut(component) {
            if components.len() == 1 {
                if existing.is_file != is_file {
                    return Err(Error::other(format!(
                        "Path conflict between file and directory: {}",
                        components.join("/"),
                    )));
                }

                existing.index = index;
                existing.is_file = is_file;
            } else {
                existing.insert_path(&components[1..], index, is_file)?;
            }
        } else {
            let mut node = Self::new();

            if components.len() == 1 {
                node.index = index;
                node.is_file = is_file;
            } else {
                node.insert_path(&components[1..], index, is_file)?;
            }

            self.children.insert(component.to_string(), node);
        }

        Ok(())
    }

    fn get(
        &self,
        path: impl AsRef<Path>,
    ) -> Option<&Self> {
        let path = path.as_ref();
        let components: Vec<&str> = path.components()
            .map(|c| c.as_str())
            .filter(|s| !s.is_empty())
            .collect();

        self.get_internal(&components)
    }

    fn get_internal(
        &self,
        components: &[&str],
    ) -> Option<&Self> {
        if components.is_empty() {
            return Some(self);
        }

        let component = components[0];

        if let Some(child) = self.children.get(component) {
            child.get_internal(&components[1..])
        } else {
            None
        }
    }

    fn iter(&self) -> impl Iterator<Item = &String> {
        self.children.keys()
    }

}

// kiln-eml: the parts of a relative path, without empty and `.` components.
fn rel_parts(rel: &Path) -> Vec<&str> {
    rel.components()
        .map(|c| c.as_str())
        .filter(|s| !s.is_empty() && *s != ".")
        .collect()
}

// kiln-eml: "root exists, src missing": walks the path from the root and stops at the first part that is not there.
fn disk_state(root: &Path, rel: &Path) -> String {
    let parts = rel_parts(rel);
    let describe = |path: &Path, want_dir: bool| match std::fs::metadata(path) {
        Ok(m) if want_dir && !m.is_dir() => "is not a directory".to_string(),
        Ok(m) if !want_dir && !m.is_file() => "is not a file".to_string(),
        Ok(_) => "exists".to_string(),
        Err(e) if e.kind() == ErrorKind::NotFound => "missing".to_string(),
        Err(e) => format!("cannot be read ({e})"),
    };

    let mut out = vec![format!("root {}", describe(root, true))];
    if out[0] != "root exists" {
        return out.remove(0);
    }

    let mut current = root.to_path_buf();
    let mut label = String::new();

    for (i, part) in parts.iter().enumerate() {
        current.push(part);

        if !label.is_empty() {
            label.push('/');
        }

        label.push_str(part);

        let word = describe(&current, i + 1 < parts.len());
        let ok = word == "exists";

        out.push(format!("{label} {word}"));

        if !ok {
            break;
        }
    }

    out.join(", ")
}

// kiln-eml: the stage-by-stage walk behind `FileSystem::diagnose` for a folder mod. The same root and the same
// `sanitize_path` steps (canonicalize, containment, link check) EML reads with, each one on its own.
fn diagnose_disk(root: &Path, rel: &Path, failed: &Error) -> ReadFailure {
    let state = disk_state(root, rel);
    let fail = |stage: String, error: String| ReadFailure { stage, error, state: state.clone(), missing: false };
    // the file, or a folder below the first one (`src`), is not there while everything above it is
    let absent = |stage: String, e: std::io::Error| ReadFailure {
        missing: e.kind() == ErrorKind::NotFound,
        ..fail(stage, e.to_string())
    };

    if let Err(e) = std::fs::metadata(root) {
        return fail("root metadata".to_string(), e.to_string());
    }

    let parts = rel_parts(rel);
    let Some((file, folders)) = parts.split_last() else {
        return fail("file metadata".to_string(), "the path is empty".to_string());
    };

    let mut current = root.to_path_buf();

    for (i, folder) in folders.iter().enumerate() {
        current.push(folder);

        let stage = format!("directory metadata ({})", parts[..=i].join("/"));

        match std::fs::metadata(&current) {
            Err(e) if i > 0 => return absent(stage, e),
            Err(e) => return fail(stage, e.to_string()),
            Ok(m) if !m.is_dir() => return fail(stage, "not a directory".to_string()),
            Ok(_) => {}
        }
    }

    current.push(file);

    match std::fs::metadata(&current) {
        Err(e) if !folders.is_empty() => return absent("file metadata".to_string(), e),
        Err(e) => return fail("file metadata".to_string(), e.to_string()),
        Ok(m) if !m.is_file() => return fail("file metadata".to_string(), "not a regular file".to_string()),
        Ok(_) => {}
    }

    let resolved = match root.join(rel).canonicalize_utf8() {
        Ok(p) => p,
        Err(e) => return fail("canonicalize".to_string(), e.to_string()),
    };

    if !resolved.starts_with(root) {
        return fail("containment check".to_string(), format!("{resolved} is outside {root}"));
    }

    if resolved.is_symlink() {
        return fail("containment check".to_string(), format!("{resolved} is a link"));
    }

    match File::open(&resolved) {
        Err(e) => fail("open".to_string(), e.to_string()),
        Ok(mut f) => match std::io::read_to_string(&mut f) {
            Err(e) => fail("read".to_string(), e.to_string()),
            // every stage passes now: the failure was transient (the file or a folder changed in between)
            Ok(_) => fail("read".to_string(), format!("{failed} (it reads fine now: the mod changed on disk meanwhile)")),
        },
    }
}

// kiln-eml: the same for a zip mod: the archive file, then the entry.
fn diagnose_zip(fs: &ZipFileSystem, rel: &Path, failed: &Error) -> ReadFailure {
    let parts = rel_parts(rel);
    let name = parts.join("/");

    if let Err(e) = std::fs::metadata(fs.root()) {
        return ReadFailure {
            stage: "root metadata".to_string(),
            error: e.to_string(),
            state: "zip missing".to_string(),
            missing: false,
        };
    }

    if !fs.exists(rel) {
        // the archive has the first folder (`src`) but not this entry: a path the mod does not have
        let missing = parts.len() > 1 && fs.exists(Path::new(parts[0]));

        return ReadFailure {
            stage: "zip entry".to_string(),
            error: "no such entry in the archive".to_string(),
            state: format!("zip exists, {name} missing"),
            missing,
        };
    }

    ReadFailure {
        stage: "read".to_string(),
        error: failed.to_string(),
        state: format!("zip exists, {name} present"),
        missing: false,
    }
}

fn sanitize_path(
    root: &Path,
    path: &Path,
) -> Result<PathBuf> {
    assert!(root.is_absolute(), "Root path must be absolute");

    let path = root.join(path).canonicalize_utf8()?;

    if !path.starts_with(root) {
        return Err(Error::from(ErrorKind::PermissionDenied));
    }

    if path.is_symlink() {
        return Err(Error::from(ErrorKind::PermissionDenied));
    }

    Ok(path)
}
