//! Add mods on the Deck: no drag and drop with a controller, so the Deck UI lists the mods it finds in a folder
//! (Downloads by default) and browses folders without the desktop's file dialog (`deck_browse`). What counts as a
//! mod is the library's own detection (`kiln_manage::library::detect`); zips are looked into (their file list) before
//! they are unpacked for it, and results are cached by path, size and time.

use kiln_manage::library::{DetectKind, Detected};
use kiln_manage::{Error, ErrorCode};
use serde::Serialize;
use std::collections::HashMap;
use std::path::{Path, PathBuf};
use std::sync::{LazyLock, Mutex};
use std::time::SystemTime;

/// `deck_browse`'s answer: one folder.
#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Folder {
    pub dir: String,
    /// The folder's own name ("Downloads").
    pub name: String,
    /// null at the file system's root.
    pub parent: Option<String>,
    /// Sub-folders that are not mods themselves (to open), by name; hidden ones left out.
    pub folders: Vec<Entry>,
    /// What the library recognises here: Kiln, native, plugin, EMF mods, DLL mods, modpacks, folders and zips holding
    /// mods; newest first.
    pub mods: Vec<FoundMod>,
    /// Other files left out (not mods, too big, unreadable).
    pub other: usize,
    /// Only the newest entries were looked at.
    pub truncated: bool,
    /// `enshrouded.exe` is in this folder (Home's "Find game").
    pub has_game: bool,
    /// Where to start browsing: Downloads, Home, Desktop, SD cards and USB drives, Steam's game folders.
    pub places: Vec<Place>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Entry {
    pub name: String,
    pub path: String,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct FoundMod {
    pub path: String,
    /// The file or folder name.
    pub file: String,
    pub folder: bool,
    pub size: u64,
    /// RFC 3339.
    pub modified: Option<String>,
    pub detected: Detected,
}

#[derive(Debug, Clone, Serialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct Place {
    pub name: String,
    pub path: String,
    /// `downloads` | `home` | `desktop` | `card` | `games`
    pub kind: String,
}

/// The user's folders (tests give fake ones).
pub struct Env {
    pub home: PathBuf,
    /// XDG download dir, when set.
    pub downloads: Option<PathBuf>,
    /// `/run/media` (SD cards and USB drives on SteamOS and most desktops).
    pub media: PathBuf,
    pub user: Option<String>,
}

impl Env {
    pub fn current() -> Env {
        let home = kiln_manage::util::home();
        Env { downloads: xdg_download_dir(&home), home, media: PathBuf::from("/run/media"), user: std::env::var("USER").ok() }
    }

    fn downloads_dir(&self) -> PathBuf {
        self.downloads.clone().filter(|d| d.is_dir()).unwrap_or_else(|| self.home.join("Downloads"))
    }
}

/// `XDG_DOWNLOAD_DIR` from `~/.config/user-dirs.dirs` (`"$HOME/Downloads"`).
fn xdg_download_dir(home: &Path) -> Option<PathBuf> {
    let cfg = std::env::var_os("XDG_CONFIG_HOME").map(PathBuf::from).unwrap_or_else(|| home.join(".config"));
    let text = std::fs::read_to_string(cfg.join("user-dirs.dirs")).ok()?;
    let line = text.lines().find_map(|l| l.trim().strip_prefix("XDG_DOWNLOAD_DIR="))?;
    let v = line.trim().trim_matches('"');
    let p = if let Some(rest) = v.strip_prefix("$HOME") { home.join(rest.trim_start_matches('/')) } else { PathBuf::from(v) };
    (p.is_absolute() && p != home).then_some(p)
}

/// At most this many entries of a folder are looked at (the newest).
pub const MAX_ENTRIES: usize = 400;
/// Zips bigger than this are not opened (a game, a video).
pub const MAX_ZIP: u64 = 512 * 1024 * 1024;
/// DLLs bigger than this are not read.
pub const MAX_DLL: u64 = 96 * 1024 * 1024;

/// The manifests that make a folder (or a zip) a mod.
const MANIFESTS: &[&str] = &["kiln.mod.json", "native.json", "mod.json", "kiln-modpack.json"];

type CacheKey = (PathBuf, u64, Option<SystemTime>);
static CACHE: LazyLock<Mutex<HashMap<CacheKey, Option<Detected>>>> = LazyLock::new(|| Mutex::new(HashMap::new()));

/// One folder: `dir` null is Downloads (Home when there is no Downloads folder).
pub fn browse(dir: Option<&Path>, env: &Env) -> Result<Folder, Error> {
    let dir = match dir {
        Some(d) if !d.as_os_str().is_empty() => kiln_manage::util::expand(&d.to_string_lossy(), &env.home),
        _ => {
            let d = env.downloads_dir();
            if d.is_dir() { d } else { env.home.clone() }
        }
    };
    let rd = std::fs::read_dir(&dir).map_err(|e| {
        let code = if e.kind() == std::io::ErrorKind::NotFound { ErrorCode::NotFound } else if e.kind() == std::io::ErrorKind::PermissionDenied { ErrorCode::Permission } else { ErrorCode::Io };
        Error::new(code, format!("Can't open {}.", dir.display())).with_detail(e.to_string())
    })?;
    let mut items: Vec<(PathBuf, std::fs::Metadata)> = rd
        .flatten()
        .filter(|e| !e.file_name().to_string_lossy().starts_with('.'))
        .filter_map(|e| std::fs::metadata(e.path()).ok().map(|m| (e.path(), m)))
        .collect();
    items.sort_by(|a, b| b.1.modified().ok().cmp(&a.1.modified().ok()).then_with(|| a.0.cmp(&b.0)));
    let truncated = items.len() > MAX_ENTRIES;
    items.truncate(MAX_ENTRIES);

    let mut folders = Vec::new();
    let mut mods = Vec::new();
    let mut other = 0;
    for (path, meta) in items {
        let file = path.file_name().map(|n| n.to_string_lossy().into_owned()).unwrap_or_default();
        let modified = meta.modified().ok().map(|t| kiln_manage::util::rfc3339(kiln_manage::util::unix(t)));
        if meta.is_dir() {
            match detect_cached(&path, &meta, true) {
                Some(d) => mods.push(FoundMod { path: path.display().to_string(), file, folder: true, size: 0, modified, detected: d }),
                None => folders.push(Entry { name: file, path: path.display().to_string() }),
            }
            continue;
        }
        match detect_cached(&path, &meta, false) {
            Some(d) => mods.push(FoundMod { path: path.display().to_string(), file, folder: false, size: meta.len(), modified, detected: d }),
            None => other += 1,
        }
    }
    folders.sort_by_key(|f| f.name.to_lowercase());
    Ok(Folder {
        name: dir.file_name().map(|n| n.to_string_lossy().into_owned()).unwrap_or_else(|| dir.display().to_string()),
        parent: dir.parent().map(|p| p.display().to_string()),
        has_game: dir.join("enshrouded.exe").is_file(),
        dir: dir.display().to_string(),
        folders,
        mods,
        other,
        truncated,
        places: places(env),
    })
}

/// A recognised mod, or None. Folders: only those with a manifest, or with a child folder that has one (an unpacked
/// zip), go to the library's detection; zips: only those listing a manifest or a DLL.
fn detect_cached(path: &Path, meta: &std::fs::Metadata, dir: bool) -> Option<Detected> {
    let key = (path.to_path_buf(), meta.len(), meta.modified().ok());
    if !dir {
        if let Some(hit) = CACHE.lock().unwrap().get(&key) {
            return hit.clone();
        }
    }
    let found = if dir { folder_candidate(path) } else { file_candidate(path, meta.len()) };
    let d = found.then(|| kiln_manage::library::detect(path).ok()).flatten().filter(recognised);
    if !dir {
        let mut c = CACHE.lock().unwrap();
        if c.len() > 2000 {
            c.clear();
        }
        c.insert(key, d.clone());
    }
    d
}

fn has_manifest(dir: &Path) -> bool {
    MANIFESTS.iter().any(|m| dir.join(m).is_file())
}

fn folder_candidate(dir: &Path) -> bool {
    if has_manifest(dir) {
        return true;
    }
    std::fs::read_dir(dir).into_iter().flatten().flatten().take(64).any(|e| e.path().is_dir() && has_manifest(&e.path()))
}

fn file_candidate(path: &Path, size: u64) -> bool {
    let lower = path.to_string_lossy().to_lowercase();
    if lower.ends_with(".dll") {
        return size <= MAX_DLL;
    }
    if !lower.ends_with(".zip") || size > MAX_ZIP {
        return false;
    }
    zip_lists_a_mod(path)
}

/// The zip's file list names a manifest or a DLL (reading only its central directory).
fn zip_lists_a_mod(path: &Path) -> bool {
    let Ok(f) = std::fs::File::open(path) else { return false };
    let Ok(a) = zip::ZipArchive::new(f) else { return false };
    a.file_names().filter_map(|n| n.ok()).any(|n| {
        let base = n.rsplit(['/', '\\']).next().unwrap_or(&n).to_lowercase();
        MANIFESTS.contains(&base.as_str()) || base.ends_with(".dll")
    })
}

/// Kinds the Deck adds: everything the library imports, modpacks (their own consent flow) and a zip or folder holding
/// mods. Not runtime bundles, not unknown files.
fn recognised(d: &Detected) -> bool {
    match d.kind {
        DetectKind::Kiln | DetectKind::Native | DetectKind::Plugin | DetectKind::Emf | DetectKind::Modpack | DetectKind::BareDll => true,
        DetectKind::Runtime => false,
        DetectKind::Unknown => d.items.iter().any(recognised),
    }
}

/// Downloads, Home, Desktop, each SD card or USB drive under /run/media, and Steam's game folders.
pub fn places(env: &Env) -> Vec<Place> {
    let mut v = Vec::new();
    let mut add = |name: &str, p: PathBuf, kind: &str| {
        if p.is_dir() && !v.iter().any(|x: &Place| x.path == p.display().to_string()) {
            v.push(Place { name: name.into(), path: p.display().to_string(), kind: kind.into() });
        }
    };
    add("Downloads", env.downloads_dir(), "downloads");
    add("Home", env.home.clone(), "home");
    add("Desktop", env.home.join("Desktop"), "desktop");
    let mut cards: Vec<PathBuf> = Vec::new();
    for e in std::fs::read_dir(&env.media).into_iter().flatten().flatten() {
        let p = e.path();
        if !p.is_dir() {
            continue;
        }
        // /run/media/<user>/<label> (udisks) or /run/media/<device> (SteamOS' SD card)
        if env.user.as_deref().is_some_and(|u| e.file_name().to_string_lossy() == u) {
            cards.extend(std::fs::read_dir(&p).into_iter().flatten().flatten().map(|x| x.path()).filter(|x| x.is_dir()));
        } else {
            cards.push(p);
        }
    }
    cards.sort();
    for c in &cards {
        let label = c.file_name().map(|n| n.to_string_lossy().into_owned()).unwrap_or_default();
        add(&if label.starts_with("mmcblk") { "SD card".to_string() } else { label }, c.clone(), "card");
    }
    add("Steam games", env.home.join(".local/share/Steam/steamapps/common"), "games");
    for c in &cards {
        add("Games on card", c.join("steamapps/common"), "games");
    }
    v
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::io::Write;

    struct Tmp(PathBuf);
    impl Tmp {
        fn new(tag: &str) -> Tmp {
            let p = std::env::temp_dir().join(format!("kiln-deck-browse-{tag}-{}", std::process::id()));
            let _ = std::fs::remove_dir_all(&p);
            std::fs::create_dir_all(&p).unwrap();
            Tmp(p)
        }
    }
    impl Drop for Tmp {
        fn drop(&mut self) {
            let _ = std::fs::remove_dir_all(&self.0);
        }
    }

    fn kiln_mod(dir: &Path, id: &str, version: &str) {
        std::fs::create_dir_all(dir).unwrap();
        let m = format!(
            r#"{{"id":"{id}","name":"{id} mod","version":"{version}","apiVersion":"0.2","entryAssembly":"{id}.dll","side":"both"}}"#
        );
        std::fs::write(dir.join("kiln.mod.json"), m).unwrap();
        std::fs::write(dir.join(format!("{id}.dll")), b"MZ").unwrap();
    }

    fn zip_dir(src: &Path, out: &Path) {
        let f = std::fs::File::create(out).unwrap();
        let mut z = zip::ZipWriter::new(f);
        let opts = zip::write::SimpleFileOptions::default().compression_method(zip::CompressionMethod::Stored);
        let mut stack = vec![src.to_path_buf()];
        while let Some(d) = stack.pop() {
            for e in std::fs::read_dir(&d).unwrap().flatten() {
                let p = e.path();
                let rel = p.strip_prefix(src).unwrap().to_string_lossy().replace('\\', "/");
                if p.is_dir() {
                    stack.push(p);
                } else {
                    z.start_file(rel, opts).unwrap();
                    z.write_all(&std::fs::read(&p).unwrap()).unwrap();
                }
            }
        }
        z.finish().unwrap();
    }

    fn env(t: &Tmp) -> Env {
        Env { home: t.0.join("home"), downloads: None, media: t.0.join("media"), user: Some("deck".into()) }
    }

    #[test]
    fn downloads_lists_only_mods() {
        let t = Tmp::new("dl");
        let e = env(&t);
        let dl = e.home.join("Downloads");
        std::fs::create_dir_all(&dl).unwrap();
        // a Kiln mod zip, an unpacked mod folder, a folder holding a mod one level down, junk
        let src = t.0.join("src/night-sky");
        kiln_mod(&src, "night-sky", "0.9.0");
        zip_dir(&src, &dl.join("night-sky-0.9.0.zip"));
        kiln_mod(&dl.join("big-stacks"), "big-stacks", "0.3.1");
        kiln_mod(&dl.join("Pack-1.0/weather-plus"), "weather-plus", "1.4.2");
        std::fs::write(dl.join("notes.txt"), "hi").unwrap();
        std::fs::write(dl.join("photos.zip"), b"PK\x05\x06\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0").unwrap();
        std::fs::create_dir_all(dl.join("Screenshots")).unwrap();
        std::fs::create_dir_all(dl.join(".cache")).unwrap();

        let f = browse(None, &e).unwrap();
        assert_eq!(f.name, "Downloads");
        assert_eq!(f.dir, dl.display().to_string());
        let mut ids: Vec<(String, DetectKind)> = f.mods.iter().map(|m| (m.file.clone(), m.detected.kind)).collect();
        ids.sort_by(|a, b| a.0.cmp(&b.0));
        assert_eq!(ids, vec![
            ("Pack-1.0".into(), DetectKind::Unknown),
            ("big-stacks".into(), DetectKind::Kiln),
            ("night-sky-0.9.0.zip".into(), DetectKind::Kiln),
        ]);
        let zip = f.mods.iter().find(|m| m.file.ends_with(".zip")).unwrap();
        assert_eq!(zip.detected.id.as_deref(), Some("night-sky"));
        assert!(!zip.folder && zip.size > 0);
        let pack = f.mods.iter().find(|m| m.file == "Pack-1.0").unwrap();
        assert_eq!(pack.detected.items.len(), 1);
        assert_eq!(f.folders.iter().map(|x| x.name.as_str()).collect::<Vec<_>>(), vec!["Screenshots"]);
        assert_eq!(f.other, 2);
        assert!(!f.has_game);
        assert_eq!(f.places[0].kind, "downloads");
        // the cache answers the same the second time
        let again = browse(Some(&dl), &e).unwrap();
        assert_eq!(again.mods.len(), 3);
    }

    #[test]
    fn browsing_finds_the_game_and_the_parent() {
        let t = Tmp::new("game");
        let e = env(&t);
        let g = t.0.join("lib/steamapps/common/Enshrouded");
        std::fs::create_dir_all(&g).unwrap();
        std::fs::write(g.join("enshrouded.exe"), b"MZ").unwrap();
        let f = browse(Some(&g), &e).unwrap();
        assert!(f.has_game);
        assert_eq!(f.parent.as_deref(), Some(t.0.join("lib/steamapps/common").display().to_string().as_str()));
        assert!(f.mods.is_empty());
        assert!(matches!(browse(Some(&t.0.join("nope")), &e), Err(err) if err.code == ErrorCode::NotFound));
    }

    #[test]
    fn no_downloads_folder_starts_at_home() {
        let t = Tmp::new("home");
        let e = env(&t);
        std::fs::create_dir_all(e.home.join("Desktop")).unwrap();
        let f = browse(None, &e).unwrap();
        assert_eq!(f.dir, e.home.display().to_string());
        assert_eq!(f.folders.iter().map(|x| x.name.as_str()).collect::<Vec<_>>(), vec!["Desktop"]);
    }

    #[test]
    fn places_include_cards_and_steam_games() {
        let t = Tmp::new("places");
        let e = env(&t);
        std::fs::create_dir_all(e.home.join("Downloads")).unwrap();
        std::fs::create_dir_all(e.home.join(".local/share/Steam/steamapps/common")).unwrap();
        std::fs::create_dir_all(e.media.join("mmcblk0p1/steamapps/common")).unwrap();
        std::fs::create_dir_all(e.media.join("deck/USBSTICK")).unwrap();
        let p = places(&e);
        let names: Vec<(&str, &str)> = p.iter().map(|x| (x.name.as_str(), x.kind.as_str())).collect();
        assert_eq!(names, vec![
            ("Downloads", "downloads"), ("Home", "home"), ("USBSTICK", "card"), ("SD card", "card"), ("Steam games", "games"), ("Games on card", "games"),
        ]);
    }

    #[test]
    fn xdg_download_dir_is_read() {
        let t = Tmp::new("xdg");
        let home = t.0.join("home");
        std::fs::create_dir_all(home.join(".config")).unwrap();
        std::fs::write(home.join(".config/user-dirs.dirs"), "XDG_DESKTOP_DIR=\"$HOME/Desktop\"\nXDG_DOWNLOAD_DIR=\"$HOME/Telechargements\"\n").unwrap();
        // XDG_CONFIG_HOME may be set in the test's environment: only check the parser through a home without it
        if std::env::var_os("XDG_CONFIG_HOME").is_none() {
            assert_eq!(xdg_download_dir(&home), Some(home.join("Telechargements")));
        }
    }
}
