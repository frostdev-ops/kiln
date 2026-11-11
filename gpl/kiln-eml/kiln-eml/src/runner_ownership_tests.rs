// SPDX-License-Identifier: GPL-3.0-or-later
//! The runner's ownership gates on real archives: a valid KFC pair, its sealed backups, and output a real EML run
//! published (header, resource file, a new container). Each test names the gate it holds.

use std::rc::Rc;

use camino::{Utf8Path, Utf8PathBuf};
use kfc::{container::{KFCFile, KFCWriter}, guid::ContentHash, reflection::TypeRegistry};

use super::*;

const ARCHIVES: [&str; 2] = ["enshrouded.kfc", "enshrouded.kfc_resources"];

/// A game folder with a valid KFC pair (version "fixture") and kiln-eml's type cache, and an EML mod outside it whose
/// Lua creates content (`lua`): a run publishes for real.
struct Game { root: Utf8PathBuf, cfg: RunConfig, vanilla: Vec<Vec<u8>> }

impl Game {
    fn new(tag: &str, lua: &str) -> Self {
        let root = Utf8PathBuf::from_path_buf(std::env::temp_dir()).unwrap()
            .join(format!("kiln-eml-owner-{tag}-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&root);
        let game = root.join("game");
        std::fs::create_dir_all(game.join("kiln/eml/cache")).unwrap();
        for name in ARCHIVES { std::fs::File::create(game.join(name)).unwrap(); }
        let mut writer = KFCWriter::<Rc<KFCFile>, _>::new(game.as_std_path(), "enshrouded", Rc::new(TypeRegistry::default()), "fixture").unwrap();
        writer.write_content(&ContentHash::from_data(b"vanilla blob"), b"vanilla blob").unwrap();
        writer.finalize().unwrap();
        let mut types = TypeRegistry::default();
        types.version = "fixture".into();
        std::fs::write(game.join("kiln/eml/cache/enshrouded-types.json"), serde_json::to_vec(&types).unwrap()).unwrap();
        let module = root.join("mods/a");
        std::fs::create_dir_all(module.join("src")).unwrap();
        std::fs::write(module.join("mod.json"), r#"{"id":"a","name":"a","version":"1.0.0","capabilities":["patch"]}"#).unwrap();
        std::fs::write(module.join("src/mod.lua"), lua).unwrap();
        let vanilla = ARCHIVES.iter().map(|n| std::fs::read(game.join(n)).unwrap()).collect();
        let cfg = RunConfig { game_dir: game.clone(), file_name: Some("enshrouded".into()), cache_dir: game.join("kiln/eml/cache"), patch: true, ..Default::default() };
        Self { root, cfg, vanilla }
    }
    fn game(&self) -> &Utf8Path { &self.cfg.game_dir }
    fn with_mod(&self) -> Outcome {
        let candidates = vec![crate::discover::Candidate { path: self.root.join("mods/a"), source: "test".into(), enabled: true }];
        let (mods, entries) = crate::discover::resolve(&candidates);
        run(&self.cfg, mods, entries)
    }
    fn without_mods(&self) -> Outcome { run(&self.cfg, Vec::new(), Vec::new()) }
    fn archives(&self) -> Vec<Vec<u8>> { ARCHIVES.iter().map(|n| std::fs::read(self.game().join(n)).unwrap()).collect() }
    fn receipt(&self) -> Utf8PathBuf { self.game().join("kiln/eml/ownership/enshrouded.json") }
    /// The state an earlier kiln-eml (no receipts) left after a patch run: its output, sealed backups and change cache.
    fn as_released_before_receipts(&self) {
        std::fs::remove_file(self.receipt()).unwrap();
    }
    /// The same archives and backups, but nothing that proves who wrote them: another loader built on the shared backup.
    fn as_written_by_another_loader(&self) {
        std::fs::remove_file(self.receipt()).unwrap();
        std::fs::remove_file(self.cfg.cache_dir.join("files.json")).unwrap();
    }
}

impl Drop for Game {
    fn drop(&mut self) { let _ = std::fs::remove_dir_all(&self.root); }
}

const CONTENT: &str = r#"game.assets.create_content(buffer.wrap("kiln fixture content"))"#;

/// A real EML publication: archives patched, backups sealed, receipt and change cache written.
fn published(tag: &str, lua: &str) -> Game {
    let g = Game::new(tag, lua);
    let out = g.with_mod();
    assert_eq!((out.phase, out.error.as_deref()), (Phase::Done, None), "{out:?}");
    assert_ne!(g.archives(), g.vanilla, "the run published");
    assert!(g.receipt().exists() && g.cfg.cache_dir.join("files.json").exists());
    for backup in ["enshrouded.kfc.bak", "enshrouded.kfc_resources.bak", "enshrouded.kfc.bak.integrity"] {
        assert!(g.game().join(backup).exists(), "{backup}");
    }
    g
}

// F1: dev.21 to dev.23 published without receipts. Their change cache names the output exactly: switching EML mods
// off after the update must restore it, as those releases did.
#[test]
fn output_of_a_release_without_receipts_is_restored_when_eml_mods_are_switched_off() {
    let g = published("legacy", CONTENT);
    let added = g.game().join("enshrouded_001.dat");
    assert!(added.metadata().unwrap().len() > 0, "the run added a content container");
    g.as_released_before_receipts();
    let out = g.without_mods();
    assert_eq!((out.phase, out.restored, out.reason.as_deref()), (Phase::Idle, true, None), "{out:?}");
    assert_eq!(g.archives(), g.vanilla);
    assert!(!added.exists(), "the container that release added, proven by its cache, goes too");
    assert!(g.game().join("enshrouded_000.dat").exists());
}

// F1: the adoption needs exact proof. A cache naming other bytes (here: the live header changed after it was saved)
// is none; the data is kept and the reason says what the player can do.
#[test]
fn a_change_cache_that_names_other_bytes_proves_nothing_and_says_so() {
    let g = published("legacy-changed", CONTENT);
    g.as_released_before_receipts();
    let header = g.game().join("enshrouded.kfc");
    let mut bytes = std::fs::read(&header).unwrap();
    let last = bytes.len() - 1;
    bytes[last] ^= 1; // same length: only the content can tell
    std::fs::write(&header, &bytes).unwrap();
    let out = g.without_mods();
    assert_eq!((out.phase, out.restored), (Phase::Idle, false));
    assert_eq!(std::fs::read(&header).unwrap(), bytes);
    let reason = out.reason.expect("a reason");
    assert!(reason.contains("cannot prove") && reason.contains("enable any EML mod for one start") && reason.contains("Verify integrity"), "{reason}");
    assert!(!g.receipt().exists(), "no receipt for unproven data");
}

// F1: after the update with EML mods still on, the first start adopts the old output before it publishes (so its
// publication has a previous generation to fall back on), publishes, and a later start without EML mods restores.
#[test]
fn output_of_a_release_without_receipts_is_adopted_before_the_first_patch() {
    let g = published("legacy-on", CONTENT);
    g.as_released_before_receipts();
    let out = g.with_mod();
    assert_eq!((out.phase, out.error.as_deref()), (Phase::Done, None), "{out:?}");
    let receipt: serde_json::Value = serde_json::from_slice(&std::fs::read(g.receipt()).unwrap()).unwrap();
    assert!(receipt.get("previous").is_none(), "a completed publication keeps one generation: {receipt}");
    let out = g.without_mods();
    assert_eq!((out.phase, out.restored), (Phase::Idle, true), "{out:?}");
    assert_eq!(g.archives(), g.vanilla);
}

// F8, gate in the idle branch: a complete, restorable set another loader built on the shared backup (valid KFC pair,
// valid sealed backups, no receipt, no cache) is left exactly as it is.
#[test]
fn no_mods_leaves_a_restorable_set_written_by_another_loader() {
    let g = published("foreign-idle", CONTENT);
    g.as_written_by_another_loader();
    let live = g.archives();
    let out = g.without_mods();
    assert_eq!((out.phase, out.restored), (Phase::Idle, false));
    assert!(out.reason.as_deref().is_some_and(|r| r.contains("no ownership receipt")), "{out:?}");
    assert_eq!(g.archives(), live);
}

// F8, gate when every mod was dropped (here: its entry module is gone by the time the run reads it).
#[test]
fn all_mods_dropped_leaves_a_restorable_set_written_by_another_loader() {
    let g = published("foreign-dropped", CONTENT);
    g.as_written_by_another_loader();
    let live = g.archives();
    let candidates = vec![crate::discover::Candidate { path: g.root.join("mods/a"), source: "test".into(), enabled: true }];
    let (mods, entries) = crate::discover::resolve(&candidates);
    std::fs::remove_dir_all(g.root.join("mods/a/src")).unwrap();
    let out = run(&g.cfg, mods, entries);
    assert_eq!((out.phase, out.restored), (Phase::Idle, false), "{out:?}");
    assert_eq!(g.archives(), live);
    // R3: this idle branch says why it left the data too
    assert!(out.reason.as_deref().is_some_and(|r| r.contains("no ownership receipt")), "{out:?}");
}

// F8, gate on the failed path: a run that fails without naming a mod (a game input changed while it ran) restores
// only provable output.
fn fail_without_naming_a_mod(g: &mut Game) -> Outcome {
    std::fs::write(g.root.join("mods/a/mod.json"), r#"{"id":"a","name":"a","version":"1.0.0","capabilities":["patch","export"]}"#).unwrap();
    std::fs::write(g.root.join("mods/a/src/mod.lua"), r#"io.export("eml.json", "{}")"#).unwrap();
    g.cfg.export = true;
    g.cfg.export_dir = Some(g.game().as_std_path().to_owned());
    let out = g.with_mod();
    let _ = std::fs::remove_file(g.game().join("eml.json"));
    out
}

#[test]
fn a_failed_run_leaves_a_restorable_set_written_by_another_loader() {
    let mut g = published("foreign-failed", CONTENT);
    g.as_written_by_another_loader();
    let live = g.archives();
    let out = fail_without_naming_a_mod(&mut g);
    assert_eq!((out.phase, out.restored), (Phase::Failed, false), "{out:?}");
    assert!(out.error.as_deref().is_some_and(|e| e.contains("Game inputs changed")), "{out:?}");
    assert_eq!(g.archives(), live);
    // the same failure over EML's own output does restore it (the gate is ownership, not the failure)
    let mut g = published("owned-failed", CONTENT);
    let out = fail_without_naming_a_mod(&mut g);
    assert_eq!((out.phase, out.restored), (Phase::Failed, true), "{out:?}");
    assert_eq!(g.archives(), g.vanilla);
}

// F1: only a cache that is the record of a patch run of this stem, naming the output by content, is proof.
#[test]
fn an_earlier_cache_proves_only_a_patch_run_of_this_stem_that_read_the_output() {
    type Edit = fn(&mut serde_json::Value, &str);
    let cases: [(&str, Edit, &str); 4] = [
        ("header recorded by stamp only", |v, game| {
            let header = format!("{game}/enshrouded.kfc");
            v["unread"] = serde_json::json!([header]);
        }, "change stamp only"),
        ("not a patch run", |v, _| { let id = v["build_id"].as_str().unwrap().replace("patch: true", "patch: false"); v["build_id"] = id.into(); }, "completed EML patch run"),
        ("another stem", |v, _| { let id = v["build_id"].as_str().unwrap().replace("enshrouded|", "enshrouded_server|"); v["build_id"] = id.into(); }, "completed EML patch run"),
        ("an empty build id (a run whose snapshot failed)", |v, _| v["build_id"] = "".into(), "completed EML patch run"),
    ];
    for (what, edit, why) in cases {
        let g = published(&format!("legacy-{}", what.split(' ').next().unwrap()), CONTENT);
        g.as_released_before_receipts();
        let cache = g.cfg.cache_dir.join("files.json");
        let mut value: serde_json::Value = serde_json::from_slice(&std::fs::read(&cache).unwrap()).unwrap();
        edit(&mut value, g.game().as_str());
        std::fs::write(&cache, serde_json::to_vec(&value).unwrap()).unwrap();
        let live = g.archives();
        let out = g.without_mods();
        assert_eq!((out.phase, out.restored), (Phase::Idle, false), "{what}");
        assert!(out.reason.as_deref().is_some_and(|r| r.contains(why)), "{what}: {:?}", out.reason);
        assert_eq!(g.archives(), live, "{what}");
        assert!(!g.receipt().exists(), "{what}");
    }
}

/// ShroudForge's state after it prepared its asset mods for this target (asset-preparation.schema.json).
fn sf_applied(g: &Game, mods: serde_json::Value) -> Utf8PathBuf {
    let state = g.game().join("shroudforge/state.json");
    std::fs::create_dir_all(g.game().join("shroudforge/config")).unwrap();
    std::fs::write(&state, serde_json::to_vec(&serde_json::json!({"schemaVersion":1,"assets":{"schemaVersion":1,
        "status":"applied","phase":"startup","target":"enshrouded","fingerprint":"a".repeat(64),"completedAt":42,"mods":mods}})).unwrap()).unwrap();
    state
}

// kiln-eml publishes only in a start where ShroudForge does not run (hosted SF reserves the writer and kiln-eml stands
// aside). If SF's state still says it prepared asset mods in an earlier start, this start's data drops them: the state
// names them and says why.
#[test]
fn publishing_over_shroudforge_asset_mods_names_the_ones_this_start_lacks() {
    let g = published("sf-displaced", CONTENT);
    let state = sf_applied(&g, serde_json::json!([{"id":"better-chests","version":"1.2.0"},{"id":"unlock-blueprints","version":"0.4.1"}]));
    // a changed EML mod: this start publishes over what SF prepared
    std::fs::write(g.root.join("mods/a/src/mod.lua"), r#"game.assets.create_content(buffer.wrap("another content"))"#).unwrap();
    let out = g.with_mod();
    assert_eq!((out.phase, out.error.as_deref()), (Phase::Done, None), "{out:?}");
    let reason = out.reason.as_deref().unwrap_or_default();
    assert!(reason.contains("better-chests 1.2.0") && reason.contains("unlock-blueprints 0.4.1"), "{reason:?}");
    assert!(reason.contains("ShroudForge did not run this start") && reason.contains("prepares them again the next time it runs"), "{reason:?}");
    assert!(!reason.contains("every start") && !reason.contains("rewrites"), "{reason:?}");
    let json = serde_json::to_value(&out).unwrap();
    assert_eq!(json["sfDisplaced"], serde_json::json!(["better-chests 1.2.0", "unlock-blueprints 0.4.1"]));
    let sf: serde_json::Value = serde_json::from_slice(&std::fs::read(&state).unwrap()).unwrap();
    assert_eq!(sf["assets"]["status"], "failed", "SF prepares again next start");
    // nothing to name: an SF state with no asset mods, and a start that publishes nothing new
    let g = published("sf-none", CONTENT);
    sf_applied(&g, serde_json::json!([]));
    std::fs::write(g.root.join("mods/a/src/mod.lua"), r#"game.assets.create_content(buffer.wrap("another content"))"#).unwrap();
    let out = g.with_mod();
    assert_eq!((out.phase, out.reason.as_deref()), (Phase::Done, None), "{out:?}");
    assert!(serde_json::to_value(&out).unwrap().get("sfDisplaced").is_none());
    let unchanged = g.with_mod();
    assert_eq!((unchanged.phase, unchanged.cached, unchanged.reason.as_deref()), (Phase::Done, true, None), "{unchanged:?}");
}

// R1: adoption lists only the containers the adopted header references. A container file above them (a leftover of a
// bigger earlier output, or a player's own) that the old cache happens to name by content is not that run's output.
#[test]
fn adoption_lists_only_the_containers_the_adopted_header_references() {
    let g = Game::new("legacy-extra", CONTENT);
    let extra = g.game().join("enshrouded_009.dat");
    std::fs::write(&extra, "not written by that run").unwrap();
    let out = g.with_mod();
    assert_eq!(out.phase, Phase::Done, "{out:?}");
    let cache: serde_json::Value = serde_json::from_slice(&std::fs::read(g.cfg.cache_dir.join("files.json")).unwrap()).unwrap();
    assert!(cache["files"].get(extra.as_str()).is_some() && !cache["unread"].as_array().is_some_and(|u| u.iter().any(|p| p == extra.as_str())),
        "the old cache names it by content");
    g.as_released_before_receipts();
    let out = g.without_mods();
    assert_eq!((out.phase, out.restored), (Phase::Idle, true), "{out:?}");
    assert_eq!(std::fs::read(&extra).unwrap(), b"not written by that run", "a file that run did not write stays");
    assert!(!g.game().join("enshrouded_001.dat").exists(), "the container it wrote goes");
}

// R2: after a game update the backup is of the earlier build. The game data is not compared with it and nothing is
// claimed about it: no reason (Steam's verify could not clear it anyway), one info line, `staleBackup` for the banner.
#[test]
fn a_backup_of_an_earlier_game_build_is_not_reported_as_unproven_data() {
    let g = published("stale-backup", CONTENT);
    assert!(g.without_mods().restored, "EML switched off once: vanilla data, backups and receipt stay");
    // Steam updates the game: new vanilla files of another build
    for entry in std::fs::read_dir(g.game()).unwrap().flatten() {
        if entry.file_name().to_string_lossy().ends_with(".dat") { std::fs::remove_file(entry.path()).unwrap(); }
    }
    for name in ARCHIVES { std::fs::File::create(g.game().join(name)).unwrap(); }
    let mut update = KFCWriter::<Rc<KFCFile>, _>::new(g.game().as_std_path(), "enshrouded", Rc::new(TypeRegistry::default()), "fixture 2").unwrap();
    update.write_content(&ContentHash::from_data(b"updated blob"), b"updated blob").unwrap();
    update.finalize().unwrap();
    let live = g.archives();
    for _ in 0..2 {
        let out = g.without_mods();
        assert_eq!((out.phase, out.restored, out.reason.as_deref()), (Phase::Idle, false, None), "{out:?}");
        assert_eq!(serde_json::to_value(&out).unwrap()["staleBackup"], true);
        assert_eq!(g.archives(), live);
    }
    // the next start with an EML mod makes a backup of the new build and patches from it (the fixture has no exe to read
    // the new build's types from: give it their cache, as the exe of a real update would)
    let mut types = TypeRegistry::default();
    types.version = "fixture 2".into();
    std::fs::write(g.cfg.cache_dir.join("enshrouded-types.json"), serde_json::to_vec(&types).unwrap()).unwrap();
    let out = g.with_mod();
    assert_eq!((out.phase, out.error.as_deref()), (Phase::Done, None), "{out:?}");
    assert_eq!(std::fs::read(g.game().join("enshrouded.kfc.bak")).unwrap(), live[0]);
}
