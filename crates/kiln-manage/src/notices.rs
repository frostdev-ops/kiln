//! Which activation notices the launcher has already shown for a game (docs/kiln/launcher/app.md "Notices about mod
//! folders"). An activation says, every time, which folders Kiln left as they are, which mod files it kept and its
//! notes on the mods (a player's own folder that serves a mod, a setting a new version refuses). The launcher raises
//! each such sentence once per game: [`take_unseen`] answers which of an activation's sentences are new and remembers
//! them. The memory is `<data>/installs/<game id>.notices-seen.json`, with the launcher's other records of that game,
//! so it moves with the data folder. It holds sha256 hashes, not the sentences, and at most [`MAX_SEEN`] of them per
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
    /// sha256 (hex) of each sentence shown, oldest first.
    #[serde(default)]
    seen: Vec<String>,
}

/// One take at a time per process: two activations at once must not lose each other's sentences.
static LOCK: Mutex<()> = Mutex::new(());

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

/// The sentences of `sentences` not shown before for `game_id`, in their order and without repeats; all of
/// `sentences` count as shown from now on (one shown again moves to the newest end, so the bound drops the ones not
/// said for longest). A memory that cannot be read counts as empty and is written again: a notice is shown once more
/// rather than never.
pub fn take_unseen(dirs: &DataDirs, game_id: &str, sentences: &[String]) -> Result<Vec<String>> {
    let path = file(dirs, game_id)?;
    let _one = LOCK.lock().unwrap_or_else(|e| e.into_inner());
    let mut seen = crate::util::read_json::<Seen>(&path).ok().flatten().unwrap_or_default();
    let mut new = Vec::new();
    for s in sentences {
        let k = key(s);
        match seen.seen.iter().position(|x| *x == k) {
            Some(i) => { seen.seen.remove(i); }
            None if !new.contains(s) => new.push(s.clone()),
            None => {}
        }
        seen.seen.push(k);
    }
    if sentences.is_empty() { return Ok(new); }
    if seen.seen.len() > MAX_SEEN {
        let extra = seen.seen.len() - MAX_SEEN;
        seen.seen.drain(..extra);
    }
    seen.format = 1;
    std::fs::create_dir_all(dirs.installs()).map_err(crate::util::io_at(&dirs.installs(), "creating"))?;
    crate::util::write_json_atomic(&path, &seen)?;
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
