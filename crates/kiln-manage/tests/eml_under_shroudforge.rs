//! The profile's EML mods while ShroudForge runs them (Kiln hosts ShroudForge for the profile's ShroudForge mods).
//! ShroudForge runs a package of its mods folder only when its `extended.mod.json` says `enabled: true`, and a legacy
//! EML package has no such file until ShroudForge writes one at the first change of its switch. A real client
//! (2026-11-11) ran one of four EML mods: the three without that file were applied by nobody. So Kiln's copy of a
//! profile EML mod in ShroudForge's folder carries ShroudForge's switch, set from the profile, and the file is part of
//! what Kiln placed; what the player (or ShroudForge) changes in it later is the player's.
#![cfg(all(unix, feature = "zip"))]

use kiln_manage::engine_mods;
use kiln_manage::library::{self, LibraryRef};
use kiln_manage::manifest::ModKind;
use kiln_manage::own_loaders;
use kiln_manage::paths::DataDirs;
use kiln_manage::pe::build_test_dll;
use kiln_manage::profile::{self, ProfileMod};
use kiln_manage::shroudforge;
use kiln_manage::task::NoProgress;
use std::path::{Path, PathBuf};

struct Tmp(PathBuf);
impl Drop for Tmp {
    fn drop(&mut self) {
        let _ = std::fs::remove_dir_all(&self.0);
    }
}
fn tmp(tag: &str) -> Tmp {
    let p = std::env::temp_dir().join(format!("km-eml-under-sf-{tag}-{}", std::process::id()));
    let _ = std::fs::remove_dir_all(&p);
    std::fs::create_dir_all(&p).unwrap();
    Tmp(p.canonicalize().unwrap())
}
fn put(root: &Path, rel: &str, data: &[u8]) {
    let p = root.join(rel);
    std::fs::create_dir_all(p.parent().unwrap()).unwrap();
    std::fs::write(p, data).unwrap();
}

/// A legacy EML package (More Gliders, Aldrich Bare Body): mod.json and Lua, no extended.mod.json.
fn legacy(root: &Path, id: &str, version: &str) -> PathBuf {
    let d = root.join("src").join(format!("{id}-{version}"));
    put(&d, "mod.json", format!(r#"{{"id":"{id}","name":"{id}","version":"{version}","capabilities":["patch"]}}"#).as_bytes());
    put(&d, "src/mod.lua", format!("-- {version}\nreturn {{}}").as_bytes());
    d
}

/// An EML package whose author ships ShroudForge's file (Wardrobe - Transmog: `launcher: "EML"`), with a setting.
const SHIPPED: &str = "{\n    \"$schema\" : \"https://bonsaibauer.github.io/shroudforge/schemas/extended.mod.schema.json\",\n    \"schemaVersion\" : 1,\n    \"enabled\" :   false,\n    \"targets\": [ \"client\" ],\n    \"launcher\": \"EML\",\n    \"settings\": { \"radius\": { \"label\": \"Radius\", \"control\": \"slider\", \"min\": 1, \"max\": 50, \"value\": 5 } }\n}\n";

fn shipped(root: &Path, id: &str, version: &str, ext: &str) -> PathBuf {
    let d = legacy(root, id, version);
    put(&d, "extended.mod.json", ext.as_bytes());
    d
}

/// A ShroudForge package of its own (`launcher: "SF"`): with one of them switched on, Kiln hosts ShroudForge.
fn sf_package(root: &Path, id: &str) -> PathBuf {
    let d = root.join("src").join(id);
    put(&d, "mod.json", format!(r#"{{"id":"{id}","name":"{id}","version":"1.0.0","capabilities":["runtime"]}}"#).as_bytes());
    put(&d, "extended.mod.json", br#"{"schemaVersion":1,"launcher":"SF","enabled":false,"targets":["client"]}"#);
    put(&d, "src/mod.lua", b"return {}");
    d
}

fn import(dirs: &DataDirs, src: &Path, kind: ModKind, id: &str) -> LibraryRef {
    let r = library::import(dirs, &[src.to_path_buf()], &mut NoProgress).unwrap();
    r.imported.iter().chain(&r.existing).find(|x| x.kind == kind && x.id == id).cloned()
        .unwrap_or_else(|| panic!("{kind:?} {id} not imported: {r:?}"))
}

fn pm(r: &LibraryRef, enabled: bool) -> ProfileMod {
    ProfileMod { id: r.id.clone(), version: r.version.clone(), hash: r.hash.clone(), enabled }
}

/// A game folder with Kiln's ShroudForge plugin and the bundled runtime (the files own_loaders::find looks for).
fn game(t: &Tmp) -> PathBuf {
    let g = t.0.join("game");
    put(&g, "enshrouded.exe", b"MZ");
    put(&g, "kiln/plugins/kiln-shroudforge.dll", b"x");
    put(&g, "kiln/plugins/shroudforge/shroudforge-runtime.dll", b"x");
    g
}

struct Setup {
    t: Tmp,
    dirs: DataDirs,
    game: PathBuf,
    sf: LibraryRef,
}

fn setup(tag: &str) -> Setup {
    let t = tmp(tag);
    let dirs = DataDirs::at(t.0.join("data"));
    let game = game(&t);
    let sf = import(&dirs, &sf_package(&t.0, "sf-crafting-essentials"), ModKind::ShroudForge, "sf-crafting-essentials");
    Setup { t, dirs, game, sf }
}

impl Setup {
    /// A profile with the ShroudForge mod on (Kiln hosts ShroudForge) and these EML mods.
    fn profile(&self, eml: Vec<ProfileMod>) -> profile::Profile {
        let mut p = profile::create(&self.dirs, "mixed", None).unwrap();
        p.mods.shroudforge = vec![pm(&self.sf, true)];
        p.mods.eml = eml;
        p
    }
}

fn ext_of(dir: &Path) -> Option<String> {
    std::fs::read_to_string(dir.join("extended.mod.json")).ok()
}

#[test]
fn a_legacy_eml_mod_placed_while_kiln_hosts_shroudforge_gets_shroudforges_switch_on() {
    let s = setup("legacy");
    let gliders = import(&s.dirs, &legacy(&s.t.0, "more-gliders", "0.2.2"), ModKind::Eml, "more-gliders");
    let p = s.profile(vec![pm(&gliders, true)]);
    let rep = engine_mods::place(&s.dirs, &p, &s.game).unwrap();
    assert!(rep.placed.contains(&"eml:more-gliders".to_string()), "{rep:?}");
    let copy = s.game.join("mods/more-gliders");
    // the file ShroudForge's ensure_eml_extension writes for a legacy EML package, with the profile's switch
    let ext: serde_json::Value = serde_json::from_str(&ext_of(&copy).expect("Kiln's copy carries ShroudForge's switch")).unwrap();
    assert_eq!(ext, serde_json::json!({"$schema": "https://bonsaibauer.github.io/shroudforge/schemas/extended.mod.schema.json",
        "schemaVersion": 1, "enabled": true, "targets": ["client", "server"], "launcher": "EML", "settings": {}}));
    // read as ShroudForge 1.7.9 reads packages: switched on, an EML package, both sides
    let m = shroudforge::read_mod(&copy).unwrap();
    assert_eq!((m.enabled, m.extended, m.launcher.as_deref(), m.targets.clone()), (true, true, Some("EML"), vec!["client".to_string(), "server".into()]));
    let (listed, _) = shroudforge::list_mods(&s.game);
    assert!(listed.iter().any(|x| x.id == "more-gliders" && x.enabled), "{listed:?}");
    // the Library entry is immutable: nothing is written there
    assert!(ext_of(&library::path_of(&s.dirs, &gliders).unwrap()).is_none());
    // the file is part of what Kiln placed: the next activation keeps the copy, and uninstalling removes it
    let rep = engine_mods::place(&s.dirs, &p, &s.game).unwrap();
    assert!(rep.kept.contains(&"eml:more-gliders".to_string()) && rep.changed.is_empty() && rep.retained.is_empty(), "{rep:?}");
    assert!(rep.own_loaders.iter().any(|n| n.loader.kind == ModKind::Eml && n.copied == ["more-gliders"] && n.off.is_empty()), "{:?}", rep.own_loaders);
    let cleanup = engine_mods::unplace(&s.dirs, &s.game).unwrap();
    assert!(cleanup.retained.is_empty() && cleanup.removed.contains(&copy), "{cleanup:?}");
}

#[test]
fn a_shipped_extension_only_has_its_switch_changed() {
    let s = setup("shipped");
    let wardrobe = import(&s.dirs, &shipped(&s.t.0, "fcu-render-observer", "0.8.4", SHIPPED), ModKind::Eml, "fcu-render-observer");
    let p = s.profile(vec![pm(&wardrobe, true)]);
    engine_mods::place(&s.dirs, &p, &s.game).unwrap();
    let copy = s.game.join("mods/fcu-render-observer");
    // byte for byte the author's file, with only the value of `enabled` replaced
    assert_eq!(ext_of(&copy).unwrap(), SHIPPED.replace("\"enabled\" :   false", "\"enabled\" :   true"));
    let m = shroudforge::read_mod(&copy).unwrap();
    assert_eq!((m.enabled, m.targets.clone()), (true, vec!["client".to_string()]));
    assert_eq!(ext_of(&library::path_of(&s.dirs, &wardrobe).unwrap()).unwrap(), SHIPPED, "the Library entry is not written");
}

#[test]
fn what_the_player_changes_in_shroudforges_file_survives_activation_and_an_update() {
    let s = setup("player");
    let v1 = import(&s.dirs, &shipped(&s.t.0, "radius-mod", "1.0.0", SHIPPED), ModKind::Eml, "radius-mod");
    let mut p = s.profile(vec![pm(&v1, true)]);
    engine_mods::place(&s.dirs, &p, &s.game).unwrap();
    let copy = s.game.join("mods/radius-mod");
    // in the game: a setting changed and the switch turned off on Kiln's ShroudForge page (or ShroudForge's Modloader)
    shroudforge::set_setting(&copy, "radius", &serde_json::json!(9)).unwrap();
    shroudforge::set_enabled(&copy, false).unwrap();
    // the next activation: the copy is still Kiln's, the profile is the switch, the setting is the player's
    let rep = engine_mods::place(&s.dirs, &p, &s.game).unwrap();
    assert!(rep.kept.contains(&"eml:radius-mod".to_string()) && rep.changed.is_empty() && rep.retained.is_empty(), "{rep:?}");
    let m = shroudforge::read_mod(&copy).unwrap();
    assert_eq!((m.enabled, &m.settings[0].value), (true, &serde_json::json!(9)));
    // a new version (new Lua, a new setting): placed, the player's value carried, the switch from the profile
    let next = SHIPPED.replace("\"value\": 5 } }", "\"value\": 5 }, \"extra\": { \"label\": \"Extra\", \"value\": true } }");
    let v2 = import(&s.dirs, &shipped(&s.t.0, "radius-mod", "1.1.0", &next), ModKind::Eml, "radius-mod");
    p.mods.eml = vec![pm(&v2, true)];
    let rep = engine_mods::place(&s.dirs, &p, &s.game).unwrap();
    assert!(rep.placed.contains(&"eml:radius-mod".to_string()) && rep.retained.is_empty(), "{rep:?}");
    assert_eq!(std::fs::read(copy.join("src/mod.lua")).unwrap(), b"-- 1.1.0\nreturn {}");
    let m = shroudforge::read_mod(&copy).unwrap();
    let value = |k: &str| m.settings.iter().find(|x| x.key == k).map(|x| x.value.clone());
    assert_eq!((m.enabled, value("radius"), value("extra")), (true, Some(serde_json::json!(9)), Some(serde_json::json!(true))));
    // the player's value is not Kiln's to throw away: uninstalling keeps the copy and says why
    let cleanup = engine_mods::unplace(&s.dirs, &s.game).unwrap();
    assert!(cleanup.retained.iter().any(|r| r.path == copy), "{cleanup:?}");
    assert_eq!(shroudforge::read_mod(&copy).unwrap().settings[0].value, serde_json::json!(9));
}

#[test]
fn the_profile_switching_an_eml_mod_off_takes_it_from_shroudforge_without_losing_the_players_values() {
    let s = setup("off");
    let gliders = import(&s.dirs, &legacy(&s.t.0, "more-gliders", "0.2.2"), ModKind::Eml, "more-gliders");
    let radius = import(&s.dirs, &shipped(&s.t.0, "radius-mod", "1.0.0", SHIPPED), ModKind::Eml, "radius-mod");
    let mut p = s.profile(vec![pm(&gliders, true), pm(&radius, true)]);
    engine_mods::place(&s.dirs, &p, &s.game).unwrap();
    let (gcopy, rcopy) = (s.game.join("mods/more-gliders"), s.game.join("mods/radius-mod"));
    shroudforge::set_setting(&rcopy, "radius", &serde_json::json!(12)).unwrap();
    p.mods.eml.iter_mut().for_each(|m| m.enabled = false);
    let rep = engine_mods::place(&s.dirs, &p, &s.game).unwrap();
    // a copy with nothing of the player's goes, as before: ShroudForge no longer sees it
    assert!(rep.removed.contains(&"eml:more-gliders".to_string()) && !gcopy.exists(), "{rep:?}");
    // a copy holding the player's ShroudForge setting stays, switched off in ShroudForge's file, and the result says so
    let m = shroudforge::read_mod(&rcopy).unwrap();
    assert_eq!((m.enabled, &m.settings[0].value), (false, &serde_json::json!(12)));
    assert!(rep.retained.iter().any(|r| r.path == rcopy && r.reason.contains("switched it off")), "{rep:?}");
    let (listed, _) = shroudforge::list_mods(&s.game);
    assert!(!listed.iter().any(|x| x.enabled && x.id != "sf-crafting-essentials"), "{listed:?}");
    // on again: the same copy, the switch on, the value as the player left it
    p.mods.eml.iter_mut().for_each(|m| m.enabled = true);
    let rep = engine_mods::place(&s.dirs, &p, &s.game).unwrap();
    assert!(rep.kept.contains(&"eml:radius-mod".to_string()) && rep.placed.contains(&"eml:more-gliders".to_string()), "{rep:?}");
    let m = shroudforge::read_mod(&rcopy).unwrap();
    assert_eq!((m.enabled, &m.settings[0].value), (true, &serde_json::json!(12)));
    assert!(shroudforge::read_mod(&gcopy).unwrap().enabled);
}

/// The DLL-carrying EML mods (Auto Loot Plus) live in `<game>/mods/<folder>`, which is ShroudForge's mods folder too.
fn alp_package(root: &Path) -> PathBuf {
    let d = root.join("pkg/AutoLootPlus 1.1.8");
    put(&d, "dinput8.dll", &build_test_dll(&["DirectInput8Create"], b"auto-loot-plus.log\0dinput8-chain.ini\0[Chain]\0mods\\auto-loot-plus\\config.txt"));
    put(&d, "mods/auto-loot-plus/mod.json", br#"{"id":"auto-loot-plus","name":"Auto Loot Plus","version":"1.1.8","capabilities":["patch"]}"#);
    put(&d, "mods/auto-loot-plus/src/mod.lua", b"return {}");
    put(&d, "mods/auto-loot-plus/config.txt", b"radius=15");
    d
}

#[test]
fn without_shroudforge_mods_no_switch_is_written_and_kiln_eml_runs_the_copy() {
    let s = setup("no-sf");
    let r = library::import(&s.dirs, &[alp_package(&s.t.0)], &mut NoProgress).unwrap();
    let alp = r.imported.iter().find(|x| x.kind == ModKind::Eml).cloned().expect("the EML part");
    let gliders = import(&s.dirs, &legacy(&s.t.0, "more-gliders", "0.2.2"), ModKind::Eml, "more-gliders");
    let mut p = s.profile(vec![pm(&alp, true), pm(&gliders, true)]);
    p.mods.shroudforge.clear();
    let rep = engine_mods::place(&s.dirs, &p, &s.game).unwrap();
    let copy = s.game.join("mods/auto-loot-plus");
    assert!(rep.placed.contains(&"eml:auto-loot-plus".to_string()), "{rep:?}");
    assert!(ext_of(&copy).is_none(), "no ShroudForge this start: kiln-eml runs the copy, and nothing needs the switch");
    assert!(!s.game.join("mods/more-gliders").exists(), "kiln-eml runs it from the Library");
    // the DLL writes its settings; then the profile gains a ShroudForge mod: the same copy gets the switch, settings stay
    std::fs::write(copy.join("config.txt"), b"radius=42").unwrap();
    p.mods.shroudforge = vec![pm(&s.sf, true)];
    let rep = engine_mods::place(&s.dirs, &p, &s.game).unwrap();
    assert!(rep.changed.is_empty() && rep.retained.is_empty(), "{rep:?}");
    assert!(shroudforge::read_mod(&copy).unwrap().enabled, "{rep:?}");
    assert!(shroudforge::read_mod(&s.game.join("mods/more-gliders")).unwrap().enabled);
    assert_eq!(std::fs::read(copy.join("config.txt")).unwrap(), b"radius=42");
    // and without them again: the game-folder copy stays as it is (its switch is harmless: kiln-eml reads only
    // `launcher`), the other copy goes
    p.mods.shroudforge.clear();
    let rep = engine_mods::place(&s.dirs, &p, &s.game).unwrap();
    assert!(rep.kept.contains(&"eml:auto-loot-plus".to_string()) && rep.removed.contains(&"eml:more-gliders".to_string()), "{rep:?}");
    assert!(rep.retained.is_empty(), "{rep:?}");
}

/// EML's own loader (dbghelp.dll) next to the game: it runs every EML mod in mods/, whatever ShroudForge's switch says.
fn eml_loader(game: &Path) {
    put(game, "dbghelp.dll", &build_test_dll(&[], b"EML_LOG_FILE_PATH"));
}

/// [`alp_package`] whose EML part ships ShroudForge's file too.
fn alp_shipping(root: &Path, ext: &str) -> PathBuf {
    let d = alp_package(root);
    put(&d, "mods/auto-loot-plus/extended.mod.json", ext.as_bytes());
    d
}

/// The ways a profile's EML mod stops: switched off in the profile, removed from it, and "No profile" or uninstall
/// (profile::deactivate_report and install::uninstall_report both call engine_mods::unplace).
#[derive(Clone, Copy, Debug)]
enum Stop {
    Off,
    Gone,
    Unplace,
}

/// Stops every EML mod of `p` the way `how` says; the copies Kiln kept, as the result names them.
fn stop(s: &Setup, p: &mut profile::Profile, how: Stop) -> (Vec<PathBuf>, Vec<PathBuf>) {
    let retained = |r: &[engine_mods::RetainedPlacement]| r.iter().map(|r| r.path.clone()).collect::<Vec<_>>();
    match how {
        Stop::Off | Stop::Gone => {
            if let Stop::Off = how { p.mods.eml.iter_mut().for_each(|m| m.enabled = false) } else { p.mods.eml.clear() }
            let rep = engine_mods::place(&s.dirs, p, &s.game).unwrap();
            (retained(&rep.retained), rep.removed.iter().map(|t| s.game.join("mods").join(t.trim_start_matches("eml:"))).collect())
        }
        Stop::Unplace => {
            let c = engine_mods::unplace(&s.dirs, &s.game).unwrap();
            (retained(&c.retained), c.removed)
        }
    }
}

/// pr-manage P1: an EML copy that ships `extended.mod.json`, placed while ShroudForge was not the EML runner (EML's own
/// loader ran them, or a game-folder copy before the profile had ShroudForge mods), got no record of Kiln's bytes. When
/// ShroudForge later ran it and the player changed a value there, every way of stopping the mod removed the copy and the
/// value with it, without a word.
#[test]
fn values_changed_in_a_copy_placed_before_shroudforge_ran_it_are_never_removed() {
    for how in [Stop::Off, Stop::Gone, Stop::Unplace] {
        let s = setup(&format!("before-sf-{how:?}"));
        let radius = import(&s.dirs, &shipped(&s.t.0, "radius-mod", "1.0.0", SHIPPED), ModKind::Eml, "radius-mod");
        let steady = import(&s.dirs, &shipped(&s.t.0, "steady-mod", "1.0.0", SHIPPED), ModKind::Eml, "steady-mod");
        let r = library::import(&s.dirs, &[alp_shipping(&s.t.0, SHIPPED)], &mut NoProgress).unwrap();
        let alp = r.imported.iter().find(|x| x.kind == ModKind::Eml).cloned().expect("the EML part");
        eml_loader(&s.game);
        let mut p = s.profile(vec![pm(&radius, true), pm(&steady, true), pm(&alp, true)]);
        p.mods.shroudforge.clear();
        let rep = engine_mods::place(&s.dirs, &p, &s.game).unwrap();
        assert_eq!(rep.placed.len(), 3, "{rep:?}");
        let (rcopy, scopy, acopy) = (s.game.join("mods/radius-mod"), s.game.join("mods/steady-mod"), s.game.join("mods/auto-loot-plus"));
        for c in [&rcopy, &scopy, &acopy] { assert_eq!(ext_of(c).unwrap(), SHIPPED, "no ShroudForge yet: the shipped file as it is"); }
        // later ShroudForge runs them, at the same version, and the player changes a value in ShroudForge's Modloader
        std::fs::remove_file(s.game.join("dbghelp.dll")).unwrap();
        p.mods.shroudforge = vec![pm(&s.sf, true)];
        let rep = engine_mods::place(&s.dirs, &p, &s.game).unwrap();
        assert_eq!(rep.switched.len(), 3, "{rep:?}");
        for c in [&rcopy, &acopy] { shroudforge::set_setting(c, "radius", &serde_json::json!(9)).unwrap(); }
        let (retained, removed) = stop(&s, &mut p, how);
        for c in [&rcopy, &acopy] {
            assert!(retained.contains(c), "{how:?}: {} holds the player's value and must stay: retained {retained:?}, removed {removed:?}", c.display());
            assert_eq!(shroudforge::read_mod(c).unwrap().settings[0].value, serde_json::json!(9), "{how:?}");
        }
        // the switch Kiln wrote is Kiln's: a copy holding nothing of the player's still goes
        assert!(removed.contains(&scopy) && !scopy.exists(), "{how:?}: retained {retained:?}, removed {removed:?}");
    }
}

/// pr-manage P1, the receipts an earlier 0.2.0-dev.24 build wrote for such a copy: `extended.mod.json` listed without
/// its switch and settings, and no record of Kiln's bytes. A file whose values differ from the Library's is the
/// player's; one that holds the Library's values (Kiln's switch aside) is not.
#[test]
fn an_older_receipt_without_kilns_bytes_takes_values_that_differ_from_the_librarys_as_the_players() {
    for how in [Stop::Off, Stop::Gone, Stop::Unplace] {
        let s = setup(&format!("older-receipt-{how:?}"));
        let radius = import(&s.dirs, &shipped(&s.t.0, "radius-mod", "1.0.0", SHIPPED), ModKind::Eml, "radius-mod");
        let steady = import(&s.dirs, &shipped(&s.t.0, "steady-mod", "1.0.0", SHIPPED), ModKind::Eml, "steady-mod");
        let r = library::import(&s.dirs, &[alp_shipping(&s.t.0, SHIPPED)], &mut NoProgress).unwrap();
        let alp = r.imported.iter().find(|x| x.kind == ModKind::Eml).cloned().expect("the EML part");
        eml_loader(&s.game);
        let mut p = s.profile(vec![pm(&radius, true), pm(&steady, true), pm(&alp, true)]);
        p.mods.shroudforge.clear();
        engine_mods::place(&s.dirs, &p, &s.game).unwrap();
        let file = s.game.join("kiln/state/library-placements.json");
        let mut receipts: serde_json::Value = serde_json::from_slice(&std::fs::read(&file).unwrap()).unwrap();
        for r in receipts.as_array_mut().unwrap() {
            assert!(r["files"].get("extended.mod.json").is_some(), "{r}");
            r.as_object_mut().unwrap().remove("extension");
        }
        std::fs::write(&file, serde_json::to_vec(&receipts).unwrap()).unwrap();
        std::fs::remove_file(s.game.join("dbghelp.dll")).unwrap();
        p.mods.shroudforge = vec![pm(&s.sf, true)];
        engine_mods::place(&s.dirs, &p, &s.game).unwrap();
        let (rcopy, scopy, acopy) = (s.game.join("mods/radius-mod"), s.game.join("mods/steady-mod"), s.game.join("mods/auto-loot-plus"));
        assert!(shroudforge::read_mod(&scopy).unwrap().enabled);
        for c in [&rcopy, &acopy] { shroudforge::set_setting(c, "radius", &serde_json::json!(9)).unwrap(); }
        let (retained, removed) = stop(&s, &mut p, how);
        for c in [&rcopy, &acopy] {
            assert!(retained.contains(c), "{how:?}: {} holds the player's value and must stay: retained {retained:?}, removed {removed:?}", c.display());
            assert_eq!(shroudforge::read_mod(c).unwrap().settings[0].value, serde_json::json!(9), "{how:?}");
        }
        assert!(removed.contains(&scopy) && !scopy.exists(), "{how:?}: retained {retained:?}, removed {removed:?}");
    }
}

/// The copy and receipt an older Kiln (0.2.0-dev.23, and dev.24 before ShroudForge's switch was part of Kiln's EML
/// copies) left while it hosted ShroudForge: the copy without extended.mod.json, the receipt listing no such file and
/// recording none of Kiln's bytes.
fn as_an_older_kiln_placed_it(game: &Path, copy: &Path) {
    std::fs::remove_file(copy.join("extended.mod.json")).unwrap();
    let file = game.join("kiln/state/library-placements.json");
    let mut receipts: serde_json::Value = serde_json::from_slice(&std::fs::read(&file).unwrap()).unwrap();
    let r = receipts.as_array_mut().unwrap().iter_mut().find(|r| r["path"].as_str() == copy.to_str()).expect("the copy's receipt");
    r["files"].as_object_mut().unwrap().remove("extended.mod.json").expect("listed now");
    r.as_object_mut().unwrap().remove("extension");
    std::fs::write(&file, serde_json::to_vec(&receipts).unwrap()).unwrap();
}

#[test]
fn a_copy_an_older_kiln_placed_without_the_switch_gets_it_at_the_next_play() {
    let s = setup("older");
    let gliders = import(&s.dirs, &legacy(&s.t.0, "more-gliders", "0.2.2"), ModKind::Eml, "more-gliders");
    let p = s.profile(vec![pm(&gliders, true)]);
    engine_mods::place(&s.dirs, &p, &s.game).unwrap();
    let copy = s.game.join("mods/more-gliders");
    as_an_older_kiln_placed_it(&s.game, &copy);
    assert!(copy.is_dir() && ext_of(&copy).is_none());
    let rep = engine_mods::place(&s.dirs, &p, &s.game).unwrap();
    assert!(rep.kept.contains(&"eml:more-gliders".to_string()) && rep.switched.contains(&"eml:more-gliders".to_string()), "{rep:?}");
    assert!(shroudforge::read_mod(&copy).unwrap().enabled);
    // the file Kiln added is Kiln's: not a file the player wrote, so uninstalling removes the copy
    let cleanup = engine_mods::unplace(&s.dirs, &s.game).unwrap();
    assert!(cleanup.retained.is_empty() && cleanup.removed.contains(&copy), "{cleanup:?}");
    assert!(!s.game.join("kiln/kept-mod-files/eml/more-gliders").exists());
}

/// pr-manage P2: EML's own loader (dbghelp.dll) in the game folder, which under Wine or Proton runs only with a DLL
/// override the launcher cannot see. Without one it is not loaded, kiln-eml does not stand aside for it, and the
/// ShroudForge adapter hosts ShroudForge for the profile's ShroudForge mods (its `wanted()` looks at no proxy file):
/// ShroudForge runs the EML mods of its mods folder whose switch is on, and kiln-eml stands aside for it. Kiln's copies
/// there must carry that switch, and the launcher's note must say who runs them in that case.
#[test]
fn with_emls_proxy_file_in_the_folder_kilns_copies_still_carry_shroudforges_switch() {
    let s = setup("eml-proxy");
    let gliders = import(&s.dirs, &legacy(&s.t.0, "more-gliders", "0.2.2"), ModKind::Eml, "more-gliders");
    eml_loader(&s.game);
    let p = s.profile(vec![pm(&gliders, true)]);
    let rep = engine_mods::place(&s.dirs, &p, &s.game).unwrap();
    let copy = s.game.join("mods/more-gliders");
    assert!(rep.placed.contains(&"eml:more-gliders".to_string()), "{rep:?}");
    assert!(ext_of(&copy).is_some(), "the copy must carry ShroudForge's switch: {rep:?}");
    assert!(shroudforge::read_mod(&copy).unwrap().enabled);
    // EML's own loader, when it runs, reads only `launcher` from that file: it runs the copy all the same
    let eml = rep.own_loaders.iter().find(|n| n.loader.kind == ModKind::Eml).expect("a note for the EML mods");
    assert_eq!((eml.loader.file.as_str(), eml.copied.clone()), ("dbghelp.dll", vec!["more-gliders".to_string()]), "{eml:?}");
    assert!(!eml.text.contains("without one Kiln runs these mods itself"), "{}", eml.text);
    assert!(eml.text.contains("without one, ShroudForge (hosted by Kiln for this profile's ShroudForge mods) runs them"), "{}", eml.text);
    // switched off in the game: in that case ShroudForge does not run it, and the note says so
    shroudforge::set_enabled(&copy, false).unwrap();
    let view = own_loaders::ProfileView::of_profile(&p);
    let notes = own_loaders::notes(&s.game, &own_loaders::find(&s.game, Some(&view)), Some(&view));
    let eml = notes.iter().find(|n| n.loader.kind == ModKind::Eml).unwrap();
    assert_eq!(eml.off, ["more-gliders"], "{eml:?}");
    // the next Play switches it on again
    let rep = engine_mods::place(&s.dirs, &p, &s.game).unwrap();
    assert!(rep.switched.contains(&"eml:more-gliders".to_string()) && shroudforge::read_mod(&copy).unwrap().enabled, "{rep:?}");
    // without ShroudForge mods in the profile nothing is hosted: the note is EML's alone, and Kiln runs them itself
    let mut q = p.clone();
    q.mods.shroudforge.clear();
    let rep = engine_mods::place(&s.dirs, &q, &s.game).unwrap();
    let eml = rep.own_loaders.iter().find(|n| n.loader.kind == ModKind::Eml).unwrap();
    assert!(eml.text.contains("without one Kiln runs these mods itself"), "{}", eml.text);
}

/// pr-manage P5: "activate the profile again to switch them on" helps a copy whose switch is off, not one for the other
/// side or one ShroudForge cannot read. Each gets its own list and a sentence that is true, Kiln's copies and the
/// player's own folders alike.
#[test]
fn the_launcher_tells_switched_off_other_side_and_unreadable_eml_mods_apart() {
    let s = setup("why-off");
    let gliders = import(&s.dirs, &legacy(&s.t.0, "more-gliders", "0.2.2"), ModKind::Eml, "more-gliders");
    let server = SHIPPED.replace("\"targets\": [ \"client\" ]", "\"targets\": [ \"server\" ]");
    let srv = import(&s.dirs, &shipped(&s.t.0, "server-tweaks", "1.0.0", &server), ModKind::Eml, "server-tweaks");
    let refused = SHIPPED.replace("\"schemaVersion\" : 1", "\"schemaVersion\" : 2");
    let bad = import(&s.dirs, &shipped(&s.t.0, "schema-two", "1.0.0", &refused), ModKind::Eml, "schema-two");
    // the player's own folders of two more profile mods: one for the other side, one ShroudForge cannot read
    let hand_srv = import(&s.dirs, &legacy(&s.t.0, "hand-server", "1.0.0"), ModKind::Eml, "hand-server");
    let hand_bad = import(&s.dirs, &legacy(&s.t.0, "hand-bad", "1.0.0"), ModKind::Eml, "hand-bad");
    for (dir, id, ext) in [("Hand Server", "hand-server", &server), ("Hand Bad", "hand-bad", &refused)] {
        let d = s.game.join("mods").join(dir);
        put(&d, "mod.json", format!(r#"{{"id":"{id}","name":"{id}","version":"1.0.0","capabilities":["patch"]}}"#).as_bytes());
        put(&d, "src/mod.lua", b"return {}");
        put(&d, "extended.mod.json", ext.replace("\"enabled\" :   false", "\"enabled\" :   true").as_bytes());
    }
    let p = s.profile(vec![pm(&gliders, true), pm(&srv, true), pm(&bad, true), pm(&hand_srv, true), pm(&hand_bad, true)]);
    let rep = engine_mods::place(&s.dirs, &p, &s.game).unwrap();
    shroudforge::set_enabled(&s.game.join("mods/more-gliders"), false).unwrap();
    let view = own_loaders::ProfileView::of_profile(&p);
    let notes = own_loaders::notes(&s.game, &own_loaders::find(&s.game, Some(&view)), Some(&view));
    let eml = notes.iter().find(|n| n.loader.kind == ModKind::Eml).unwrap();
    assert_eq!((eml.off.clone(), eml.other_side.clone(), eml.unreadable.clone()),
        (vec!["more-gliders".to_string()], vec!["server-tweaks".to_string()], vec!["schema-two".to_string()]), "{eml:?}");
    let sentence = |id: &str| eml.text.split(". ").find(|s| s.contains(&format!("({id}"))).unwrap_or_else(|| panic!("{id}: {}", eml.text)).to_string();
    assert!(sentence("more-gliders").contains("activate the profile again to switch them on"), "{}", eml.text);
    assert!(sentence("server-tweaks").contains("server only") && !sentence("server-tweaks").contains("activate"), "{}", eml.text);
    assert!(sentence("schema-two").contains("cannot read") && !sentence("schema-two").contains("activate the profile again"), "{}", eml.text);
    let own = |id: &str| rep.own_notes.iter().find(|n| n.starts_with(&format!("eml:{id}"))).unwrap_or_else(|| panic!("{id}: {:?}", rep.own_notes)).clone();
    assert!(own("hand-server").contains("server only") && !own("hand-server").contains("switch it on"), "{}", own("hand-server"));
    assert!(own("hand-bad").contains("cannot read") && !own("hand-bad").contains("switch it on"), "{}", own("hand-bad"));
}

#[test]
fn the_launcher_says_when_shroudforge_has_an_eml_mod_off_and_never_writes_a_players_folder() {
    let s = setup("notes");
    let gliders = import(&s.dirs, &legacy(&s.t.0, "more-gliders", "0.2.2"), ModKind::Eml, "more-gliders");
    let bare = import(&s.dirs, &legacy(&s.t.0, "aldrich-bare-body", "0.5.3"), ModKind::Eml, "aldrich-bare-body");
    // the player installed this one by hand into the game's mods folder: no extended.mod.json, so ShroudForge has it off
    let hand = s.game.join("mods/Aldrich Body");
    put(&hand, "mod.json", br#"{"id":"aldrich-bare-body","name":"Aldrich","version":"0.5.3","capabilities":["patch"]}"#);
    put(&hand, "src/mod.lua", b"return {}");
    let p = s.profile(vec![pm(&gliders, true), pm(&bare, true)]);
    let rep = engine_mods::place(&s.dirs, &p, &s.game).unwrap();
    assert!(rep.own.contains(&"eml:aldrich-bare-body".to_string()), "{rep:?}");
    assert!(ext_of(&hand).is_none() && engine_mods::marker(&hand).is_none(), "a player's folder is never written to");
    let note = rep.own_notes.iter().find(|n| n.starts_with("eml:aldrich-bare-body")).expect("a note for the player's folder");
    assert!(note.contains("ShroudForge has it switched off") && !note.contains("the game uses your own folder"), "{note}");
    // a copy whose switch is off (turned off in the game, or a write that failed) is not said to run
    shroudforge::set_enabled(&s.game.join("mods/more-gliders"), false).unwrap();
    let view = own_loaders::ProfileView::of_profile(&p);
    let notes = own_loaders::notes(&s.game, &own_loaders::find(&s.game, Some(&view)), Some(&view));
    let eml = notes.iter().find(|n| n.loader.kind == ModKind::Eml).unwrap();
    assert_eq!(eml.off, ["more-gliders"], "{eml:?}");
    assert!(eml.text.contains("switched off in ShroudForge's settings"), "{}", eml.text);
}
