//! The launcher's cook with the real cook tools (kiln_manage::cook::cook_due): the mods of a game's profile are cooked
//! from that game by the tools this build carries, unpacked into a cache, as the launcher does it. The made-up game and
//! the mod come from tools/modelcook/tests/fakegame.py (nothing from the real game). Skipped when python3 cannot build
//! them or cannot run the tools.
//!
//! One test in this file, on purpose: it sets KILN_OUT for the tools it starts (the made-up exe's reflection is there),
//! and no other test may run beside that.
#![cfg(feature = "cooktools")]

use kiln_manage::cook::{self, CookOptions, CookReason};
use kiln_manage::cooktools::{self, Locate};
use kiln_manage::game::{GameInstall, GameKind, PathMode, Platform};
use kiln_manage::manifest::ModKind;
use kiln_manage::paths::DataDirs;
use kiln_manage::profile::{self, ProfileMod};
use kiln_manage::task::NoProgress;
use kiln_manage::{ErrorCode, hash, library};
use std::path::{Path, PathBuf};
use std::process::Command;

fn repo() -> PathBuf {
    Path::new(env!("CARGO_MANIFEST_DIR")).join("../..").canonicalize().unwrap()
}

/// A made-up client game in `game` and a mod project with a model, a visual and animation steps in `mod_dir`.
fn fake_game(root: &Path, game: &Path, mod_dir: &Path) -> bool {
    let mut c = Command::new("python3");
    c.arg(repo().join("tools/modelcook/tests/fakegame.py")).arg(game).args(["--side", "client", "--mod"]).arg(mod_dir);
    c.arg("--kiln-out").arg(root.join("out"));
    match c.output() {
        Ok(o) if o.status.success() => true,
        Ok(o) => {
            eprintln!("skipped: the made-up game could not be built: {}", String::from_utf8_lossy(&o.stderr));
            false
        }
        Err(e) => {
            eprintln!("skipped: no python3: {e}");
            false
        }
    }
}

fn manifest(dir: &Path, id: &str, name: &str) {
    std::fs::write(dir.join("kiln.mod.json"),
        format!(r#"{{ "id": "{id}", "name": "{name}", "version": "1.0.0", "apiVersion": "0.2", "entryAssembly": "M.dll", "side": "both" }}"#)).unwrap();
    std::fs::write(dir.join("M.dll"), id).unwrap();
}

#[test]
fn the_launchers_cook_makes_a_profiles_mods_from_the_game_with_the_tools_this_build_carries() {
    let root = std::env::temp_dir().join(format!("km-cook-real-{}", std::process::id()));
    let _ = std::fs::remove_dir_all(&root);
    std::fs::create_dir_all(&root).unwrap();
    let root = root.canonicalize().unwrap();
    let (game, sword) = (root.join("client"), root.join("src/fake-sword"));
    if !fake_game(&root, &game, &sword) {
        std::fs::remove_dir_all(&root).unwrap();
        return;
    }
    // SAFETY: the only test of this binary, and nothing else reads the environment while it is set.
    unsafe { std::env::set_var("KILN_OUT", root.join("out")) };
    // as the launcher asks for them: no checkout, the copy this build carries, unpacked under the data folder
    let locate = Locate::from_process(None, Some(root.join("data/cooktools")), None);
    let tools = match cooktools::locate(&locate) {
        Ok(t) => t,
        Err(e) => {
            eprintln!("skipped: the cook tools cannot run here: {}", e.message);
            std::fs::remove_dir_all(&root).unwrap();
            return;
        }
    };
    assert_eq!(tools.source, cooktools::Source::Shipped);
    assert!(tools.root.starts_with(root.join("data/cooktools")), "{}", tools.root.display());

    // The author's side: the mod is built (its cook writes assets/assets.json, which ships) and packed.
    let _ = std::fs::remove_file(sword.join("CMakeLists.txt"));
    manifest(&sword, "fake-sword", "Fake Sword");
    let author = CookOptions { game: Some(game.clone()), ..CookOptions::default() };
    let built = cook::cook_folder(&tools, &sword, &author, &mut |_| {}).unwrap();
    assert!(built.ok, "the author's cook: {}", built.text);
    assert!(sword.join("assets/assets.json").is_file() && sword.join("assets/cooked/cook-report.json").is_file(), "{}", built.text);
    // a second mod whose recipe names a source file that is not in it: its cook fails
    let broken = root.join("src/broken");
    kiln_manage::util::copy_tree(&sword, &broken).unwrap();
    manifest(&broken, "broken", "Broken");
    std::fs::remove_file(broken.join("assets/src/sword.gltf")).unwrap();

    // The player's side: both mods in the Library (an import leaves assets/cooked out), in a profile, in the game.
    let dirs = DataDirs::at(root.join("data"));
    let r = library::import(&dirs, &[sword.clone(), broken.clone()], &mut NoProgress).unwrap();
    std::fs::create_dir_all(game.join("kiln")).unwrap();
    let mut inst = GameInstall {
        id: "g-test".into(), kind: GameKind::Client, path: game.clone(), exe: "enshrouded.exe".into(),
        app_id: kiln_manage::CLIENT_APP_ID, library: None, platform: Platform::Proton, prefix: None, build_id: Some("100".into()),
        exe_sha256: None, steam_flavor: None, path_mode: PathMode::Copy, source: "manual".into(),
    };
    let mut p = profile::create(&dirs, "p", None).unwrap();
    p.mods.kiln = ["fake-sword", "broken"].iter().map(|id| {
        let x = r.imported.iter().find(|x| x.kind == ModKind::Kiln && x.id == *id).unwrap_or_else(|| panic!("{id} not imported: {r:?}"));
        ProfileMod { id: x.id.clone(), version: x.version.clone(), hash: x.hash.clone(), enabled: true }
    }).collect();
    let p = profile::save(&dirs, &p).unwrap();
    profile::activate(&dirs, &p.id, &inst).unwrap();
    let jobs = cook::plan(&inst).unwrap();
    assert_eq!(jobs.iter().map(|j| (j.id.as_str(), j.reason)).collect::<Vec<_>>(), [("fake-sword", Some(CookReason::NotCooked)), ("broken", Some(CookReason::NotCooked))]);
    let (folder, identity) = (jobs[0].folder.clone(), jobs[0].identity.clone());
    assert!(!folder.join("assets/cooked").exists(), "the import left the author's cooked files out");

    // Play: both are cooked from this game. One fails with the tool's own words; the other is made, and is the same mod.
    let mut lines = Vec::new();
    let mut note = |i: usize, n: usize, job: &cook::CookJob, line: Option<&str>| lines.push(format!("{}/{n} {}: {}", i + 1, job.id, line.unwrap_or("start")));
    let run = cook::cook_due(&dirs, &inst, None, &locate, &mut note, &|| false).unwrap();
    assert_eq!(run.mods.iter().map(|m| (m.id.as_str(), m.ok)).collect::<Vec<_>>(), [("fake-sword", true), ("broken", false)], "{lines:#?}");
    let said = run.mods[1].error.clone().unwrap();
    assert!(said.contains("sword.gltf"), "the tool's own text names the file: {said}");
    assert!(lines.iter().any(|l| l.starts_with("1/2 fake-sword: kiln: cooking ")) && lines.iter().any(|l| l.starts_with("1/2 fake-sword: ok: ")), "{lines:#?}");
    assert!(folder.join("assets/cooked/cook-report.json").is_file() && folder.join("assets/cooked/a.bin").is_file(), "{lines:#?}");
    assert_eq!(hash::mod_identity(&folder).unwrap(), identity, "a cook writes only into assets/cooked: the mod is the one that was installed");
    assert_eq!(profile::activate(&dirs, &p.id, &inst).unwrap().copied, 0, "the cooked copy stays at the next activation");
    let state = cook::read_state(&game);
    assert!(state.mods["fake-sword"].ok && state.mods["fake-sword"].files && state.mods["fake-sword"].game_build == "steam:100");
    assert!(!state.mods["broken"].ok);

    // The next Play: the cooked mod is left alone (no tool is started for it), the failed one is tried again.
    let jobs = cook::plan(&inst).unwrap();
    assert_eq!(jobs.iter().map(|j| (j.id.as_str(), j.reason)).collect::<Vec<_>>(), [("fake-sword", None), ("broken", Some(CookReason::LastFailed))]);
    let run = cook::cook_due(&dirs, &inst, None, &locate, &mut |_, _, _, _| {}, &|| false).unwrap();
    assert_eq!(run.mods.iter().map(|m| (m.id.as_str(), m.ok)).collect::<Vec<_>>(), [("broken", false)]);

    // What the cook made is listed in this game's receipts (the tools wrote them), each file as it was made.
    let (listed, intact) = cook::check_receipts(&game, &folder);
    assert!(listed >= 3 && intact, "{listed} {intact}");
    assert!(cook::receipts_of(&game, &folder).iter().all(|(_, r)| r.files.keys().all(|f| f.starts_with("cooked/"))));
    let report = folder.join("assets/cooked/cook-report.json");
    let made = std::fs::read(&report).unwrap();

    // A file the cook made was changed (or swapped for a file from elsewhere): the mod is due again, and cooked again.
    std::fs::write(folder.join("assets/cooked/a.bin"), b"not what this game's cook made").unwrap();
    assert_eq!(cook::plan(&inst).unwrap()[0].reason, Some(CookReason::FilesGone));
    let run = cook::cook_due(&dirs, &inst, None, &locate, &mut |_, _, _, _| {}, &|| false).unwrap();
    assert_eq!(run.mods.iter().map(|m| (m.id.as_str(), m.reason, m.ok)).collect::<Vec<_>>(),
        [("fake-sword", CookReason::FilesGone, true), ("broken", CookReason::LastFailed, false)]);
    assert_eq!(cook::check_receipts(&game, &folder).1, true);
    assert_eq!(cook::plan(&inst).unwrap()[0].reason, None);

    // The game was updated. The recipe's own check reads the mod's files only and calls the mod up to date ...
    std::fs::write(folder.join("assets/cooked/stale.bin"), b"a file no cook of this build made").unwrap();
    std::thread::sleep(std::time::Duration::from_millis(20));
    inst.build_id = Some("101".into());
    let own = cook::cook_folder(&tools, &folder, &CookOptions { game: Some(game.clone()), check: true, ..CookOptions::default() }, &mut |_| {}).unwrap();
    assert!(own.ok && own.recipes.iter().all(|r| r.needed == Some(false)), "{}", own.text);
    // ... so the launcher's cook does not ask it: the mod is due, and its files are made again from the game as it is now
    let mut lines = Vec::new();
    let run = cook::cook_due(&dirs, &inst, None, &locate, &mut |_, _, job, line| lines.push(format!("{}: {}", job.id, line.unwrap_or("start"))), &|| false).unwrap();
    assert_eq!(run.mods.iter().map(|m| (m.id.as_str(), m.reason, m.ok)).collect::<Vec<_>>(),
        [("fake-sword", CookReason::GameChanged, true), ("broken", CookReason::GameChanged, false)], "{lines:#?}");
    assert!(lines.iter().any(|l| l.starts_with("fake-sword: kiln: cooking ")), "the tools ran: {lines:#?}");
    assert_eq!(cook::read_state(&game).mods["fake-sword"].game_build, "steam:101");
    assert_eq!(hash::mod_identity(&folder).unwrap(), identity);
    // ... made from nothing: the folder holds what this cook made (a file of an older cook is gone), the report again
    assert!(!folder.join("assets/cooked/stale.bin").exists());
    assert!(report.is_file() && std::fs::read(&report).unwrap() != made || report.metadata().unwrap().modified().unwrap() > std::time::SystemTime::now() - std::time::Duration::from_secs(60));
    assert!(cook::check_receipts(&game, &folder).1);

    // "Cook again": that one mod, whatever its record says; a mod with nothing to cook is refused.
    let mut lines = Vec::new();
    let run = cook::cook_due(&dirs, &inst, Some("fake-sword"), &locate, &mut |_, _, job, line| lines.push(format!("{}: {}", job.id, line.unwrap_or("start"))), &|| false).unwrap();
    assert_eq!(run.mods.iter().map(|m| (m.id.as_str(), m.reason, m.ok)).collect::<Vec<_>>(), [("fake-sword", CookReason::Asked, true)]);
    assert!(lines.iter().any(|l| l.starts_with("fake-sword: kiln: cooking ")), "{lines:#?}");
    assert_eq!(cook::cook_due(&dirs, &inst, Some("no-such-mod"), &locate, &mut |_, _, _, _| {}, &|| false).unwrap_err().code, ErrorCode::NotFound);

    // "Cook again" that fails (a source of the recipe went missing): the mod keeps the files and the receipts of its last
    // cook, which still load; it is recorded as failed and tried again at the next start.
    let (listed, _) = cook::check_receipts(&game, &folder);
    let blade = std::fs::read(folder.join("assets/cooked/a.bin")).unwrap();
    std::fs::rename(folder.join("assets/src/sword.gltf"), root.join("sword.gltf.away")).unwrap();
    let run = cook::cook_due(&dirs, &inst, Some("fake-sword"), &locate, &mut |_, _, _, _| {}, &|| false).unwrap();
    assert!(!run.mods[0].ok && run.mods[0].error.as_deref().is_some_and(|e| e.contains("sword.gltf")), "{:?}", run.mods);
    assert_eq!(std::fs::read(folder.join("assets/cooked/a.bin")).unwrap(), blade, "the working files stay");
    assert_eq!(cook::check_receipts(&game, &folder), (listed, true), "and so do their receipts");
    assert!(std::fs::read_dir(folder.join("assets")).unwrap().flatten().all(|e| !e.file_name().to_string_lossy().starts_with(".kiln-cooked-prev-")));
    assert_eq!(cook::plan(&inst).unwrap()[0].reason, Some(CookReason::LastFailed));
    std::fs::rename(root.join("sword.gltf.away"), folder.join("assets/src/sword.gltf")).unwrap();

    // Canceled while the tool runs (the task's Cancel): the tool is stopped, the mod is recorded as not cooked, no
    // further mod starts, and the mod keeps the files of its last cook.
    let seen = std::cell::Cell::new(0);
    let e = cook::cook_due(&dirs, &inst, Some("fake-sword"), &locate, &mut |_, _, _, line| if line.is_some() { seen.set(seen.get() + 1) }, &|| seen.get() > 0).unwrap_err();
    assert_eq!(e.code, ErrorCode::Canceled, "{e}");
    assert!(!cook::read_state(&game).mods["fake-sword"].ok);
    assert_eq!(cook::plan(&inst).unwrap()[0].reason, Some(CookReason::LastFailed));
    assert_eq!(std::fs::read(folder.join("assets/cooked/a.bin")).unwrap(), blade);
    assert_eq!(cook::check_receipts(&game, &folder), (listed, true));

    // Another cook or a start of this game holds its lock: the cook waits a moment, then says so and starts nothing.
    let held = kiln_manage::state::lock_game(&game, std::time::Duration::ZERO, &|| false).unwrap().unwrap();
    let e = cook::cook_due(&dirs, &inst, None, &locate, &mut |_, _, _, _| panic!("nothing starts"), &|| false).unwrap_err();
    assert_eq!(e.code, ErrorCode::Busy, "{e}");
    drop(held);

    // Cook again right after a start through Kiln is refused: the game may be reading the files
    let opener = |_: &str| Ok(());
    kiln_manage::launch::play_with(&dirs, &inst, &kiln_manage::launch::LaunchRequest { mode: kiln_manage::launch::LaunchMode::Profile, profile_id: None, skip_backup: true }, Some(&[]), &opener).unwrap();
    let e = cook::cook_due(&dirs, &inst, Some("fake-sword"), &locate, &mut |_, _, _, _| {}, &|| false).unwrap_err();
    assert_eq!(e.code, ErrorCode::GameRunning, "{e}");

    // The profile changed after its activation (kilnctl, another window: a mod switched on that the game's profile file
    // does not name yet). Play's cook writes the profile again first and cooks that mod too, where the start loads it.
    let late = root.join("src/late");
    kiln_manage::util::copy_tree(&sword, &late).unwrap();
    let _ = std::fs::remove_dir_all(late.join("assets/cooked"));
    manifest(&late, "late", "Late");
    let r2 = library::import(&dirs, std::slice::from_ref(&late), &mut NoProgress).unwrap();
    let x = r2.imported.iter().find(|x| x.id == "late").unwrap();
    let mut p2 = profile::get(&dirs, &p.id).unwrap();
    p2.mods.kiln.push(ProfileMod { id: x.id.clone(), version: x.version.clone(), hash: x.hash.clone(), enabled: true });
    profile::save(&dirs, &p2).unwrap();
    assert!(cook::plan(&inst).unwrap().iter().all(|j| j.id != "late"), "the game's profile file does not name it yet");
    let run = cook::cook_due(&dirs, &inst, None, &locate, &mut |_, _, _, _| {}, &|| false).unwrap();
    assert!(run.activation.is_some(), "the profile was written again before the cook");
    assert!(run.mods.iter().any(|m| m.id == "late" && m.reason == CookReason::NotCooked && m.ok), "{:?}", run.mods);
    let late_job = cook::plan(&inst).unwrap().into_iter().find(|j| j.id == "late").unwrap();
    assert_eq!(late_job.reason, None);
    assert!(late_job.folder.join("assets/cooked/cook-report.json").is_file());

    // Two games that load one Library entry (Z mode: both read the Library through Z:). A start of one holds the lock
    // of each folder it loads until Steam was asked, so a cook of that folder for the other waits; and the other's cook
    // leaves the folder alone while the first game runs or was just started.
    let pfx = root.join("pfx");
    std::fs::create_dir_all(pfx.join("dosdevices")).unwrap();
    std::os::unix::fs::symlink("/", pfx.join("dosdevices/z:")).unwrap();
    let z = |name: &str| {
        let g = root.join(name);
        std::fs::create_dir_all(g.join("kiln")).unwrap();
        std::fs::write(g.join("enshrouded.exe"), b"MZ").unwrap();
        let i = GameInstall { id: format!("g-{name}"), path: g.clone(), prefix: Some(pfx.clone()), build_id: Some("101".into()), ..inst.clone() };
        kiln_manage::game::write_record(&dirs, &i, None).unwrap();
        profile::activate(&dirs, &p.id, &i).unwrap();
        i
    };
    let (z1, z2) = (z("client-z1"), z("client-z2"));
    let shared = cook::plan(&z1).unwrap().into_iter().find(|j| j.id == "fake-sword").unwrap().folder;
    assert_eq!(cook::plan(&z2).unwrap().into_iter().find(|j| j.id == "fake-sword").unwrap().folder, shared);
    assert!(shared.starts_with(root.join("data")), "the Library entry itself: {}", shared.display());
    let held = kiln_manage::state::lock_cook_folder(&shared, std::time::Duration::ZERO, &|| false, "x").unwrap();
    let start = kiln_manage::launch::LaunchRequest { mode: kiln_manage::launch::LaunchMode::Profile, profile_id: None, skip_backup: true };
    let e = kiln_manage::launch::play_with(&dirs, &z2, &start, Some(&[]), &opener).unwrap_err();
    assert_eq!(e.code, ErrorCode::Busy, "a start waits for the cook of a folder it loads: {e}");
    drop(held);
    kiln_manage::launch::play_with(&dirs, &z2, &start, Some(&[]), &opener).unwrap();
    let run = cook::cook_due(&dirs, &z1, None, &locate, &mut |_, _, _, _| {}, &|| false).unwrap();
    let sword_run = run.mods.iter().find(|m| m.id == "fake-sword").unwrap();
    assert!(!sword_run.ok && sword_run.error.as_deref().is_some_and(|e| e.contains("loads this mod's folder too and was just started")), "{:?}", run.mods);

    // one unpacked copy of the tools served every cook
    let copies: Vec<PathBuf> = std::fs::read_dir(root.join("data/cooktools")).unwrap().flatten().map(|e| e.path()).filter(|p| p.is_dir()).collect();
    assert_eq!(copies.len(), 1, "{copies:?}");
    std::fs::remove_dir_all(&root).unwrap();
}
