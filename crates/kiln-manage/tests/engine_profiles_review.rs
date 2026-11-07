//! Profile and state regression checks for the engine-kind extension. All files live in private temp fixtures.
use kiln_manage::game::{GameInstall, GameKind, PathMode, Platform};
use kiln_manage::manifest::ModKind;
use kiln_manage::paths::DataDirs;
use kiln_manage::profile::{self, ProfileMod};
use kiln_manage::{ErrorCode, engine_mods, library, state, task::NoProgress};
use std::path::PathBuf;

struct Fixture { root: PathBuf, dirs: DataDirs }
impl Fixture {
    fn new(tag: &str) -> Self {
        let root = std::env::temp_dir().join(format!("kiln-profile-review-{tag}-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&root);
        std::fs::create_dir_all(&root).unwrap();
        Self { dirs: DataDirs::at(root.join("data")), root }
    }
    fn game(&self) -> GameInstall {
        let path = self.root.join("game");
        std::fs::create_dir_all(path.join("kiln")).unwrap();
        GameInstall { id: "fixture".into(), kind: GameKind::Client, path, exe: "enshrouded.exe".into(),
            app_id: kiln_manage::CLIENT_APP_ID, library: None, platform: Platform::Proton, prefix: None,
            build_id: None, exe_sha256: None, steam_flavor: None, path_mode: PathMode::Copy, source: "manual".into() }
    }
}
impl Drop for Fixture { fn drop(&mut self) { let _ = std::fs::remove_dir_all(&self.root); } }

#[test]
fn engine_references_are_validated_before_save_or_render() {
    let f = Fixture::new("refs");
    let p = profile::create(&f.dirs, "Safe", None).unwrap();
    let before = std::fs::read(f.dirs.profile(&p.id).join("meta.json")).unwrap();
    for kind in ModKind::ALL {
        let good_hash = if kind == ModKind::Plugin { "a".repeat(64) } else { format!("kdh1:{}", "a".repeat(64)) };
        for version in ["../../outside", "1.0.0\n", " 1.0.0", "v1.0.0/hidden", "garbage"] {
            let mut bad = p.clone();
            bad.mods.list_mut(kind).push(ProfileMod { id: "safe".into(), version: version.into(), hash: good_hash.clone(), enabled: true });
            assert_eq!(profile::save(&f.dirs, &bad).unwrap_err().code, ErrorCode::Schema, "{kind:?}: {version:?}");
            assert_eq!(profile::render(&f.dirs, &bad, &f.game(), PathMode::Copy).unwrap_err().code, ErrorCode::Schema);
        }
        let mut bad = p.clone();
        bad.mods.list_mut(kind).push(ProfileMod { id: "safe".into(), version: "1.0.0".into(),
            hash: if kind == ModKind::Plugin { format!("kdh1:{}", "a".repeat(64)) } else { "a".repeat(64) }, enabled: true });
        assert_eq!(profile::save(&f.dirs, &bad).unwrap_err().code, ErrorCode::Schema);
    }
    assert_eq!(std::fs::read(f.dirs.profile(&p.id).join("meta.json")).unwrap(), before);
    let mut bad = p.clone();
    bad.mods.eml.push(ProfileMod { id: "safe".into(), version: "../../escape".into(), hash: format!("kdh1:{}", "a".repeat(64)), enabled: true });
    std::fs::write(f.dirs.profile(&p.id).join("meta.json"), serde_json::to_vec(&bad).unwrap()).unwrap();
    assert_eq!(profile::get(&f.dirs, &p.id).unwrap_err().code, ErrorCode::Schema);
}

#[test]
fn generation_exhaustion_is_an_error_without_writes() {
    let f = Fixture::new("generation");
    let mut p = profile::create(&f.dirs, "Limit", None).unwrap();
    p.generation = i64::MAX as u64;
    let meta = f.dirs.profile(&p.id).join("meta.json");
    std::fs::write(&meta, serde_json::to_vec(&p).unwrap()).unwrap();
    let before = std::fs::read(&meta).unwrap();
    assert_eq!(profile::save(&f.dirs, &p).unwrap_err().code, ErrorCode::Schema);
    assert_eq!(std::fs::read(meta).unwrap(), before);
    let game = f.game();
    let mut file = profile::render(&f.dirs, &p, &game, PathMode::Copy).unwrap();
    file.generation = u64::MAX;
    let active = game.path.join("kiln/profile.json");
    std::fs::write(&active, serde_json::to_vec(&file).unwrap()).unwrap();
    let before = std::fs::read(&active).unwrap();
    assert_eq!(profile::activate(&f.dirs, &p.id, &game).unwrap_err().code, ErrorCode::Schema);
    assert_eq!(std::fs::read(active).unwrap(), before);
}

#[test]
fn play_once_cannot_change_fixed_folders_for_a_later_steam_launch() {
    let f = Fixture::new("once");
    let source = f.root.join("lua-mod");
    std::fs::create_dir_all(&source).unwrap();
    std::fs::write(source.join("init.lua"), b"print('safe')").unwrap();
    let imported = library::import(&f.dirs, &[source], &mut NoProgress).unwrap();
    let m = &imported.imported[0];
    assert_eq!(m.kind, ModKind::Set);
    let mut normal = profile::create(&f.dirs, "Normal", None).unwrap();
    normal.mods.set.push(ProfileMod { id: m.id.clone(), version: m.version.clone(), hash: m.hash.clone(), enabled: true });
    let normal = profile::save(&f.dirs, &normal).unwrap();
    let once = profile::create(&f.dirs, "Once", None).unwrap();
    let game = f.game();
    profile::activate(&f.dirs, &normal.id, &game).unwrap();
    let placed = engine_mods::placed_folder(ModKind::Set, &game.path, &m.id).unwrap();
    assert!(placed.join("init.lua").is_file());
    let original_profile = std::fs::read(game.path.join("kiln/profile.json")).unwrap();
    assert_eq!(profile::write_once(&f.dirs, &once.id, &game).unwrap_err().code, ErrorCode::Unsupported);
    assert!(placed.join("init.lua").is_file(), "refusal must precede all changes to fixed engine folders");
    assert!(!game.path.join("kiln/profile.once.json").exists());
    profile::write_once(&f.dirs, &normal.id, &game).unwrap();
    assert!(placed.join("init.lua").is_file(), "same fixed selection remains usable for play-once");
    // Repair drift left by a previous launcher version's one-time activation, without rewriting profile.json.
    engine_mods::place(&f.dirs, &once, &game.path).unwrap();
    assert!(!placed.exists());
    assert!(profile::refresh_active(&f.dirs, &game).unwrap().is_none());
    assert!(placed.join("init.lua").is_file(), "unchanged profile.json must still restore normal SET placement");
    assert_eq!(std::fs::read(game.path.join("kiln/profile.json")).unwrap(), original_profile);
}

#[test]
fn kiln_only_profile_lists_remain_byte_identical_and_old_readers_ignore_new_lists() {
    let original = r#"{"kiln":[],"native":[],"emf":[],"plugin":[]}"#;
    let mods: profile::ProfileFileMods = serde_json::from_str(original).unwrap();
    assert_eq!(serde_json::to_string(&mods).unwrap(), original);
    let original_file = concat!(r#"{"format":1,"id":"legacy","generation":7,"dataDir":null,"dropins":false,"server":null,"mods":{"kiln":[{"id":"old","version":"1.2.0","path":"profiles/legacy/kiln/old"}],"native":[],"emf":[],"plugin":[]}}"#);
    let file: profile::ProfileFile = serde_json::from_str(original_file).unwrap();
    assert_eq!(serde_json::to_string(&file).unwrap(), original_file, "every game profile byte remains in the old field order");
    #[derive(serde::Deserialize)]
    struct OldMods { kiln: Vec<profile::FolderMod>, native: Vec<profile::FolderMod>, emf: Vec<profile::FolderMod>, plugin: Vec<profile::PluginMod> }
    let mut newer = serde_json::to_value(mods).unwrap();
    newer["eml"] = serde_json::json!([{ "id": "extra", "path": "mods/extra" }]);
    let old: OldMods = serde_json::from_value(newer).unwrap();
    assert!(old.kiln.is_empty() && old.native.is_empty() && old.emf.is_empty() && old.plugin.is_empty());
}

#[test]
fn unknown_required_kind_is_preserved_separately_from_legacy_kiln() {
    let legacy = serde_json::json!({"id":"same", "version":"1.0.0", "hash":format!("kdh1:{}", "a".repeat(64))});
    let mut newer = legacy.clone(); newer["kind"] = "future-engine".into();
    let legacy: state::RequiredMod = serde_json::from_value(legacy).unwrap();
    let newer: state::RequiredMod = serde_json::from_value(newer).unwrap();
    assert_eq!(legacy.kind, None); assert_eq!(legacy.unknown_kind, None);
    assert_eq!(newer.kind, None); assert_eq!(newer.unknown_kind.as_deref(), Some("future-engine"));
    assert_eq!(serde_json::to_value(&newer).unwrap()["kind"], "future-engine");
    assert!(serde_json::to_value(legacy).unwrap().get("kind").is_none());
}

#[test]
fn engine_boot_counts_survive_state_read_and_serialization() {
    let counts: state::KindCounts = serde_json::from_value(serde_json::json!({"eml":2,"set":1,"flame":3})).unwrap();
    assert_eq!((counts.eml, counts.set, counts.flame), (2, 1, 3));
    let wire = serde_json::to_value(counts).unwrap();
    assert_eq!(wire["eml"], 2); assert_eq!(wire["set"], 1); assert_eq!(wire["flame"], 3);
    assert!(wire.get("shroudtopia").is_none());
}
