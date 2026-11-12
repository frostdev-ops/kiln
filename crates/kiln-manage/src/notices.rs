//! Which activation notices the player has already closed in the launcher for a game (docs/kiln/launcher/app.md "Notices about mod
//! folders"). An activation says, every time, which folders Kiln left as they are, which mod files it kept and its
//! notes on the mods (a player's own folder that serves a mod, a setting a new version refuses). The launcher raises
//! each such sentence once per game: [`unseen`] answers which of an activation's sentences are new, and [`mark_seen`]
//! remembers the ones whose notice the player closed. The memory is `<data>/installs/<game id>.notices-seen.json`, with
//! the launcher's other records of that game, so it moves with the data folder. It holds sha256 hashes, not the sentences, and at most [`MAX_SEEN`] of them per
//! game: the oldest go first.

use crate::paths::DataDirs;
use crate::{Error, Result};
use serde::{Deserialize, Serialize};
use std::path::PathBuf;
use std::sync::Mutex;

/// Hashes kept per game. An activation says a few sentences per mod; this is far more than one profile's worth.
pub const MAX_SEEN: usize = 512;

/// `<data>/installs/<game id>.notices-seen.json`.
#[derive(Debug, Clone, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
struct Seen {
    format: u32,
    /// sha256 (hex) of each sentence whose notice the player closed, oldest first.
    #[serde(default)]
    seen: Vec<String>,
}

/// One take at a time per process: two activations at once must not lose each other's sentences.
static LOCK: Mutex<()> = Mutex::new(());

/// One take at a time across processes too: two launchers (the desktop window and the Deck's, or a second window on the
/// same data folder) each read the memory, add their sentences and write it back, and without this the later write
/// drops what the earlier one added. The lock is a file of its own next to the memory, `<game id>.notices-seen.lock`,
/// held for the read and the write; the operating system releases it when the process ends, however it ends. Where it
/// cannot be taken (a folder that refuses locks) the take goes on without it: a notice shown once more at worst.
fn lock_across_processes(path: &std::path::Path) -> Option<std::fs::File> {
    let lock = path.with_extension("lock");
    if let Some(dir) = lock.parent() {
        let _ = std::fs::create_dir_all(dir);
    }
    let file = std::fs::OpenOptions::new().create(true).truncate(false).read(true).write(true).open(&lock).ok()?;
    file.lock().ok()?;
    Some(file)
}

fn file(dirs: &DataDirs, game_id: &str) -> Result<PathBuf> {
    // game ids are file names in installs/ already (`<id>.json`): nothing that leaves the folder
    if game_id.is_empty() || game_id.len() > 128 || !game_id.bytes().all(|b| b.is_ascii_alphanumeric() || matches!(b, b'-' | b'_' | b'.'))
        || game_id.starts_with('.') {
        return Err(Error::invalid("invalid game id"));
    }
    Ok(dirs.installs().join(format!("{game_id}.notices-seen.json")))
}

fn key(sentence: &str) -> String {
    crate::hash::sha256_hex(sentence.as_bytes())
}

fn read(path: &std::path::Path) -> Seen {
    crate::util::read_json::<Seen>(path).ok().flatten().unwrap_or_default()
}

/// The sentences of `sentences` not seen before for `game_id`, in their order and without repeats. Asking remembers
/// nothing: a sentence counts as seen only when the window reports that the player closed its notice ([`mark_seen`]),
/// so a notice still open when the launcher closes (or never closed) comes again at a later activation. A memory that
/// cannot be read counts as empty: a notice is shown once more rather than never.
pub fn unseen(dirs: &DataDirs, game_id: &str, sentences: &[String]) -> Result<Vec<String>> {
    let path = file(dirs, game_id)?;
    let seen = read(&path);
    let mut new: Vec<String> = Vec::new();
    for s in sentences {
        if !seen.seen.contains(&key(s)) && !new.contains(s) { new.push(s.clone()); }
    }
    Ok(new)
}

/// Remembers `sentences` as shown for `game_id` (one shown again moves to the newest end, so the bound drops the ones
/// not said for longest). Read, changed and written under a lock two launchers share, and written through a temp file
/// of its own name, so neither loses what the other remembered and neither writes the other's file.
pub fn mark_seen(dirs: &DataDirs, game_id: &str, sentences: &[String]) -> Result<()> {
    let path = file(dirs, game_id)?;
    if sentences.is_empty() { return Ok(()); }
    let _one = LOCK.lock().unwrap_or_else(|e| e.into_inner());
    let _across = lock_across_processes(&path);
    let mut seen = read(&path);
    for s in sentences {
        let k = key(s);
        seen.seen.retain(|x| *x != k);
        seen.seen.push(k);
    }
    if seen.seen.len() > MAX_SEEN {
        let extra = seen.seen.len() - MAX_SEEN;
        seen.seen.drain(..extra);
    }
    seen.format = 1;
    let mut text = serde_json::to_string_pretty(&seen)?;
    text.push('\n');
    crate::util::write_atomic_exclusive(&path, text.as_bytes())
}

/// [`unseen`], then [`mark_seen`] of all of `sentences`: for a caller that shows what it gets at once.
pub fn take_unseen(dirs: &DataDirs, game_id: &str, sentences: &[String]) -> Result<Vec<String>> {
    let new = unseen(dirs, game_id, sentences)?;
    mark_seen(dirs, game_id, sentences)?;
    Ok(new)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn dirs(tag: &str) -> (DataDirs, PathBuf) {
        let p = std::env::temp_dir().join(format!("km-notices-{tag}-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&p);
        (DataDirs::at(p.clone()), p)
    }
    fn s(v: &[&str]) -> Vec<String> {
        v.iter().map(|x| x.to_string()).collect()
    }

    #[test]
    fn a_sentence_is_new_once_per_game_and_the_memory_survives() {
        let (d, root) = dirs("once");
        let own = "shroudforge:hoardershelper: the game uses your own folder /g/mods/HoardersHelper for this mod; Kiln leaves it as it is and places no copy of its own.";
        assert_eq!(take_unseen(&d, "client-steam", &s(&[own, "eml:a: kept 1 file"])).unwrap(), s(&[own, "eml:a: kept 1 file"]));
        // the next activation says the same: nothing new; a changed sentence is new
        assert!(take_unseen(&d, "client-steam", &s(&[own, "eml:a: kept 1 file"])).unwrap().is_empty());
        assert_eq!(take_unseen(&d, "client-steam", &s(&[own, "eml:a: kept 2 files"])).unwrap(), s(&["eml:a: kept 2 files"]));
        // per game
        assert_eq!(take_unseen(&d, "server-lab", &s(&[own])).unwrap(), s(&[own]));
        // the file, with hashes only, in the data folder
        let text = std::fs::read_to_string(root.join("installs/client-steam.notices-seen.json")).unwrap();
        assert!(!text.contains("hoardershelper"), "{text}");
        // a repeat inside one take is one notice
        assert_eq!(take_unseen(&d, "g2", &s(&["x", "x"])).unwrap(), s(&["x"]));
        let _ = std::fs::remove_dir_all(root);
    }

    /// Asking which sentences are new remembers nothing: only a sentence the window reports as shown is remembered (the
    /// Deck shows one stay-until-closed notice at a time; a launcher closed before the others came must show them).
    #[test]
    fn asking_does_not_remember_only_a_shown_sentence_is() {
        let (d, root) = dirs("ask");
        assert_eq!(unseen(&d, "g", &s(&["a", "b"])).unwrap(), s(&["a", "b"]));
        assert_eq!(unseen(&d, "g", &s(&["a", "b"])).unwrap(), s(&["a", "b"]), "asking twice without showing");
        mark_seen(&d, "g", &s(&["a"])).unwrap();
        assert_eq!(unseen(&d, "g", &s(&["a", "b"])).unwrap(), s(&["b"]));
        let _ = std::fs::remove_dir_all(root);
    }

    #[test]
    fn the_memory_is_bounded_and_drops_the_longest_unsaid_first() {
        let (d, root) = dirs("bound");
        let first = s(&["kept"]);
        take_unseen(&d, "g", &first).unwrap();
        let many: Vec<String> = (0..MAX_SEEN - 1).map(|i| format!("note {i}")).collect();
        take_unseen(&d, "g", &many).unwrap();
        // "kept" said again: still known, and now the newest
        assert!(take_unseen(&d, "g", &first).unwrap().is_empty());
        take_unseen(&d, "g", &s(&["one more"])).unwrap();
        let file: Seen = crate::util::read_json(&root.join("installs/g.notices-seen.json")).unwrap().unwrap();
        assert_eq!(file.seen.len(), MAX_SEEN);
        assert!(take_unseen(&d, "g", &first).unwrap().is_empty());
        assert_eq!(take_unseen(&d, "g", &s(&["note 0"])).unwrap(), s(&["note 0"]), "the oldest went");
        let _ = std::fs::remove_dir_all(root);
    }

    /// Two launchers on one data folder: several processes each remember sentences of their own, one at a time, at
    /// the same moment. None may lose another's. The children are this test binary, running `a_launcher_of_its_own`.
    #[test]
    fn launchers_at_once_do_not_lose_each_others_sentences() {
        const LAUNCHERS: usize = 4;
        const EACH: usize = 30;
        let (d, root) = dirs("two");
        let children: Vec<std::process::Child> = (0..LAUNCHERS)
            .map(|n| {
                std::process::Command::new(std::env::current_exe().unwrap())
                    .args(["notices::tests::a_launcher_of_its_own", "--exact", "--nocapture", "--test-threads=1"])
                    .env("KM_NOTICES_ROOT", &root)
                    .env("KM_NOTICES_TAG", n.to_string())
                    .env("KM_NOTICES_EACH", EACH.to_string())
                    .stdout(std::process::Stdio::null())
                    .stderr(std::process::Stdio::null())
                    .spawn()
                    .unwrap()
            })
            .collect();
        for mut c in children {
            assert!(c.wait().unwrap().success(), "a child launcher failed");
        }
        let all: Vec<String> = (0..LAUNCHERS).flat_map(|n| (0..EACH).map(move |i| format!("launcher {n} sentence {i}"))).collect();
        let missing = unseen(&d, "g", &all).unwrap();
        assert!(missing.is_empty(), "{} of {} sentences were lost: {:?}", missing.len(), all.len(), &missing[..missing.len().min(5)]);
        assert!(root.join("installs/g.notices-seen.lock").is_file(), "the lock file is next to the memory");
        let _ = std::fs::remove_dir_all(root);
    }

    /// The child of the test above: does nothing unless that test started it.
    #[test]
    fn a_launcher_of_its_own() {
        let (Ok(root), Ok(tag), Ok(each)) = (std::env::var("KM_NOTICES_ROOT"), std::env::var("KM_NOTICES_TAG"), std::env::var("KM_NOTICES_EACH")) else {
            return;
        };
        let d = DataDirs::at(PathBuf::from(root));
        for i in 0..each.parse::<usize>().unwrap() {
            mark_seen(&d, "g", &[format!("launcher {tag} sentence {i}")]).unwrap();
        }
    }

    /// The four launchers above could pass by luck of timing. This one cannot: while this process holds the lock, another
    /// process must find it held, and free once the guard is dropped. It also shows the lock is a real one in this
    /// folder (a file system that ignores locks would let the first look succeed).
    #[test]
    fn another_process_finds_the_lock_held_exactly_while_one_holds_it() {
        let (d, root) = dirs("held");
        let path = file(&d, "g").unwrap();
        let look = |expect: &str| {
            let marker = root.join(format!("looked-{expect}"));
            let ok = std::process::Command::new(std::env::current_exe().unwrap())
                .args(["notices::tests::a_launcher_that_looks_at_the_lock", "--exact", "--nocapture", "--test-threads=1"])
                .env("KM_NOTICES_LOCK_ROOT", &root)
                .env("KM_NOTICES_LOCK_EXPECT", expect)
                .stdout(std::process::Stdio::null())
                .stderr(std::process::Stdio::null())
                .status()
                .unwrap()
                .success();
            // the marker says the child really looked (a filter that matched no test would also exit with success)
            ok && marker.is_file()
        };
        let guard = lock_across_processes(&path).expect("the lock can be taken in an ordinary folder");
        assert!(look("held"), "another process did not find the lock held while this one held it");
        drop(guard);
        assert!(look("free"), "another process found the lock held after its guard was dropped");
        let _ = std::fs::remove_dir_all(root);
    }

    /// The child of the test above: does nothing unless that test started it. It tries the lock without waiting and
    /// leaves a marker when what it found is what the parent expects.
    #[test]
    fn a_launcher_that_looks_at_the_lock() {
        let (Ok(root), Ok(expect)) = (std::env::var("KM_NOTICES_LOCK_ROOT"), std::env::var("KM_NOTICES_LOCK_EXPECT")) else {
            return;
        };
        let root = PathBuf::from(root);
        let d = DataDirs::at(root.clone());
        let lock = file(&d, "g").unwrap().with_extension("lock");
        let f = std::fs::OpenOptions::new().create(true).truncate(false).read(true).write(true).open(&lock).unwrap();
        let held = matches!(f.try_lock(), Err(std::fs::TryLockError::WouldBlock));
        assert_eq!(held, expect == "held", "the lock is {} for this process", if held { "held by another" } else { "free" });
        std::fs::write(root.join(format!("looked-{expect}")), b"").unwrap();
    }

    #[test]
    fn a_lock_that_cannot_be_taken_does_not_stop_the_memory() {
        let (d, root) = dirs("nolock");
        std::fs::create_dir_all(d.installs()).unwrap();
        // a folder where the lock file should be: it cannot be opened as a file
        std::fs::create_dir_all(d.installs().join("g.notices-seen.lock")).unwrap();
        mark_seen(&d, "g", &s(&["a"])).unwrap();
        assert!(unseen(&d, "g", &s(&["a"])).unwrap().is_empty());
        let _ = std::fs::remove_dir_all(root);
    }

    #[test]
    fn a_broken_memory_shows_again_and_ids_stay_in_the_folder() {
        let (d, root) = dirs("broken");
        std::fs::create_dir_all(d.installs()).unwrap();
        std::fs::write(d.installs().join("g.notices-seen.json"), b"{broken").unwrap();
        assert_eq!(take_unseen(&d, "g", &s(&["a"])).unwrap(), s(&["a"]));
        assert!(take_unseen(&d, "g", &s(&["a"])).unwrap().is_empty());
        for id in ["", "../x", "a/b", ".hidden", "a\\b"] {
            assert!(take_unseen(&d, id, &s(&["a"])).is_err(), "{id}");
        }
        let _ = std::fs::remove_dir_all(root);
    }
}
