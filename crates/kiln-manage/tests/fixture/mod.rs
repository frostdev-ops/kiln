//! The data-folder tests' shared fixture: a scratch launcher data folder with every kind of record the launcher keeps,
//! made with the crate's own functions wherever one exists, so the files are what the launcher writes.
//!
//! In `<scratch>/` (below `std::env::temp_dir()`, removed on drop unless `KILN_TEST_KEEP` is set):
//! - the environment of a Linux player ([`Fixture::env`]: `HOME`, `XDG_DATA_HOME`, `XDG_STATE_HOME`,
//!   `XDG_CONFIG_HOME` below the scratch folder) and its default data root `xdg-data/kiln` ([`Fixture::dirs`]); on
//!   Windows a Windows player's (`LOCALAPPDATA`, `APPDATA`, `USERPROFILE` below it), default root `local\Kiln`, with
//!   Kiln's state folder `local\Kiln\state` inside it as on a real PC;
//! - a library ([`library::import`]): a Kiln mod, a native `dllmain` mod, an EMF mod and a SET mod (a fixed-folder
//!   kind that `engine_mods::place` copies into the game folder with a `.kiln-library.json` marker and a receipt);
//! - two profiles ([`profile::create`], [`profile::save`]): "Ember run" with all four, active ([`profile::activate`])
//!   in both game folders, and "Spare";
//! - `game-z/`, a game folder in Z mode (its own Wine prefix `game-z.pfx/` whose `dosdevices/z:` points at `/`, as
//!   `tests/profiles.rs` does), and `game-copy/`, one without a prefix (copy mode); an install record each
//!   ([`game::inspect_with`], [`game::write_record`]); on Windows both are native mode (no prefix);
//! - the quick flow's lock (`quick.lock`, [`quick::resume_active`]);
//! - a save backup under the active profile ([`saves::backup_from`], from a save in the Z prefix);
//! - a runtime bundle under `runtime/` ([`bundle::create`], [`bundle::import`]);
//! - a server folder `steam/steamapps/common/EnshroudedServer/` with a Kiln mod players need, the launcher's record of
//!   it under `servers/`, and a modpack built from it under `modpacks/built/<server>/` ([`modpack::build`]);
//! - `settings.json`, a Nexus `nexus/handler.json`, and one unknown top-level folder with a file (`My Notes/`).
//!
//! Three of these have no crate function that writes into a given data folder, and are written here as the
//! launcher writes them: the server record (`server::instance::record` writes into `DataDirs::locate()`'s root, and
//! `steam_instance` takes its `backupDir` from there), `settings.json` (the launcher's `LauncherSettings`), and
//! `handler.json` (`nexus::handler::register` refuses in a build with Nexus off; written as `keystore::write_private`
//! writes it: pretty JSON, mode 0600).
#![allow(dead_code)]

use kiln_manage::bundle::{self, CreateOptions, RuntimeBundle};
use kiln_manage::game::{self, GameInstall, PathMode};
use kiln_manage::library::{self, LibraryRef};
use kiln_manage::manifest::ModKind;
use kiln_manage::modpack::{self, BuiltModpack};
use kiln_manage::paths::{DataDirs, DataEnv};
use kiln_manage::pe::build_test_dll;
use kiln_manage::profile::{self, Activation, ProfileMod};
use kiln_manage::saves::{self, BackupReason, SaveBackup, SaveDir};
use kiln_manage::task::NoProgress;
use kiln_manage::{quick, util};
use std::path::{Path, PathBuf};

/// A scratch folder below `std::env::temp_dir()`, removed on drop (kept when `KILN_TEST_KEEP` is set).
pub struct Scratch(pub PathBuf);

impl Drop for Scratch {
    fn drop(&mut self) {
        if std::env::var_os("KILN_TEST_KEEP").is_none() {
            let _ = std::fs::remove_dir_all(&self.0);
        }
    }
}

/// Windows: `canonicalize` gives `\\?\C:\…`; the launcher keeps and compares paths without that prefix.
pub fn scratch(tag: &str) -> Scratch {
    let p = std::env::temp_dir().join(format!("km-data-folder-{tag}-{}", std::process::id()));
    let _ = std::fs::remove_dir_all(&p);
    std::fs::create_dir_all(&p).unwrap();
    Scratch(kiln_manage::steam::strip_verbatim(p.canonicalize().unwrap()))
}

pub fn put(root: &Path, rel: &str, data: &[u8]) {
    let p = root.join(rel);
    std::fs::create_dir_all(p.parent().unwrap()).unwrap();
    std::fs::write(p, data).unwrap();
}

/// A Linux player's environment with everything below `base` (on Windows a Windows player's: [`win_env_at`]).
#[cfg(not(windows))]
pub fn env_at(base: &Path) -> DataEnv {
    DataEnv {
        windows: false,
        kiln_data_dir: None,
        local_app_data: None,
        xdg_data_home: Some(base.join("xdg-data").into()),
        home: Some(base.join("home").into()),
        user_profile: None,
        xdg_state_home: Some(base.join("xdg-state").into()),
        app_data: None,
        xdg_config_home: Some(base.join("xdg-config").into()),
    }
}

#[cfg(windows)]
pub fn env_at(base: &Path) -> DataEnv {
    win_env_at(base)
}

/// A Windows player's environment with everything below `base`: `LOCALAPPDATA` `base\local` (default root
/// `local\Kiln`, state folder `local\Kiln\state`), `APPDATA` `base\roaming`, `USERPROFILE` `base\home`.
pub fn win_env_at(base: &Path) -> DataEnv {
    DataEnv {
        windows: true,
        local_app_data: Some(base.join("local").into()),
        app_data: Some(base.join("roaming").into()),
        user_profile: Some(base.join("home").into()),
        ..Default::default()
    }
}

/// The subfolder a non-empty picked folder gets on this platform (`Kiln` on Windows, `kiln` elsewhere).
pub fn sub() -> &'static str {
    if cfg!(windows) { "Kiln" } else { "kiln" }
}

pub fn kiln_mod(root: &Path, id: &str, version: &str) -> PathBuf {
    let d = root.join(format!("src/{id}"));
    put(&d, "kiln.mod.json", format!(r#"{{ "id": "{id}", "version": "{version}", "apiVersion": "0.2", "entryAssembly": "M.dll", "side": "both" }}"#).as_bytes());
    put(&d, "M.dll", id.as_bytes());
    d
}

pub fn native_dllmain(root: &Path, id: &str) -> PathBuf {
    let d = root.join(format!("src/{id}"));
    put(&d, "native.json", format!(r#"{{ "format": 1, "id": "{id}", "version": "1.0.0", "dlls": ["{id}.dll"], "phase": "dllmain", "init": null }}"#).as_bytes());
    put(&d, &format!("{id}.dll"), &build_test_dll(&["kiln_native_init"], id.as_bytes()));
    d
}

pub fn emf_mod(root: &Path, id: &str) -> PathBuf {
    let d = root.join(format!("src/{id}"));
    put(&d, "mod.json", format!(r#"{{ "id": "{id}", "name": "Beastmaster", "version": "0.3.3", "emf": 21 }}"#).as_bytes());
    put(&d, &format!("{id}.dll"), &build_test_dll(&["emf_mod_load"], b""));
    d
}

/// A SET mod (`init.lua` only): activation places it in `<game>/shrouded_engine_tweaks/mods/<id>/`.
pub fn set_mod(root: &Path, id: &str) -> PathBuf {
    let d = root.join(format!("src/{id}"));
    put(&d, "init.lua", b"print('fly')");
    d
}

/// A runtime staging folder like tools/package/stage.sh writes (tests/install.rs).
fn runtime_stage(root: &Path) -> PathBuf {
    let s = root.join("stage");
    put(&s, "kiln-runtime.json", br#"{
  "format": 1, "kiln": "0.2.0-dev", "abi": 2, "api": "0.2.0", "dotnet": "10.0.14",
  "components": {
    "core": { "sides": ["server", "client"], "default": true, "description": "proxy, core, host" }
  },
  "config": ["kiln/kiln.json"]
}"#);
    put(&s, "core/dinput8.dll", &build_test_dll(&["DirectInput8Create", "GetdfDIJoystick", "KilnProxyInfo"], b"KilnProxy/2 0.2.0-dev\0"));
    put(&s, "core/kiln/kiln-core.dll", b"core");
    put(&s, "core/kiln/kiln.json", b"{\n  \"policy\": \"verified-only\"\n}\n");
    s
}

pub struct Mods {
    pub kiln: LibraryRef,
    pub native: LibraryRef,
    pub emf: LibraryRef,
    pub set: LibraryRef,
}

pub struct Fixture {
    pub scratch: Scratch,
    pub base: PathBuf,
    pub env: DataEnv,
    pub dirs: DataDirs,
    pub mods: Mods,
    pub game_z: GameInstall,
    pub game_copy: GameInstall,
    /// Profile ids: "ember-run" (active in both games), "spare".
    pub active: String,
    pub spare: String,
    pub bundle: RuntimeBundle,
    pub server: PathBuf,
    pub server_id: String,
    pub modpack: BuiltModpack,
    pub backup: SaveBackup,
    /// The unknown top-level folder (`<data>/My Notes`).
    pub unknown: PathBuf,
    /// The launcher's path `handler.json` names.
    pub launcher_exe: PathBuf,
    /// The save folder the backup was taken from (in the Z prefix; on Windows `<scratch>/Saved Games/Enshrouded`).
    pub saves_dir: PathBuf,
}

fn refs(r: &library::ImportReport, kind: ModKind, id: &str) -> LibraryRef {
    r.imported.iter().chain(&r.existing).find(|x| x.kind == kind && x.id == id).cloned().unwrap_or_else(|| panic!("{kind:?} {id} not imported: {r:?}"))
}

fn pm(r: &LibraryRef) -> ProfileMod {
    ProfileMod { id: r.id.clone(), version: r.version.clone(), hash: r.hash.clone(), enabled: true }
}

fn game_folder(base: &Path, name: &str, z_prefix: bool, dirs: &DataDirs) -> GameInstall {
    let game = base.join(name);
    put(&game, "enshrouded.exe", b"MZ");
    std::fs::create_dir_all(game.join("kiln")).unwrap();
    #[cfg(unix)]
    if z_prefix {
        // kilnctl's `<dir>.pfx`: game::inspect_with takes it as the folder's own Wine prefix
        let pfx = base.join(format!("{name}.pfx"));
        std::fs::create_dir_all(pfx.join("dosdevices")).unwrap();
        std::os::unix::fs::symlink("/", pfx.join("dosdevices/z:")).unwrap();
    }
    #[cfg(not(unix))]
    let _ = z_prefix; // a game on Windows runs natively: no prefix
    let g = game::inspect_with(&game, &[], &dirs.root).unwrap();
    game::write_record(dirs, &g, None).unwrap();
    g
}

impl Fixture {
    pub fn build(tag: &str) -> Fixture {
        let scratch = scratch(tag);
        let base = scratch.0.clone();
        let env = env_at(&base);
        for d in [env.state_dir(), env.config_dir()] {
            std::fs::create_dir_all(d).unwrap();
        }
        let dirs = DataDirs::at(env.default_root().unwrap());

        // the library
        let r = library::import(
            &dirs,
            &[kiln_mod(&base, "new-items", "1.0.0"), native_dllmain(&base, "fps"), emf_mod(&base, "beastmaster"), set_mod(&base, "flymode")],
            &mut NoProgress,
        )
        .unwrap();
        let mods = Mods {
            kiln: refs(&r, ModKind::Kiln, "new-items"),
            native: refs(&r, ModKind::Native, "fps"),
            emf: refs(&r, ModKind::Emf, "beastmaster"),
            set: refs(&r, ModKind::Set, "flymode"),
        };

        // two game folders with their install records
        let game_z = game_folder(&base, "game-z", true, &dirs);
        let game_copy = game_folder(&base, "game-copy", false, &dirs);
        let modes = if cfg!(windows) { (PathMode::Native, PathMode::Native) } else { (PathMode::Z, PathMode::Copy) };
        assert_eq!((game_z.path_mode, game_copy.path_mode), modes);

        // two profiles, one active in both games
        let mut p = profile::create(&dirs, "Ember run", None).unwrap();
        p.mods.kiln.push(pm(&mods.kiln));
        p.mods.native.push(pm(&mods.native));
        p.mods.emf.push(pm(&mods.emf));
        p.mods.set.push(pm(&mods.set));
        let active = profile::save(&dirs, &p).unwrap().id;
        let mut s = profile::create(&dirs, "Spare", None).unwrap();
        s.mods.kiln.push(pm(&mods.kiln));
        let spare = profile::save(&dirs, &s).unwrap().id;
        for g in [&game_z, &game_copy] {
            profile::activate(&dirs, &active, g).unwrap();
        }
        assert!(game_z.path.join("shrouded_engine_tweaks/mods/flymode/.kiln-library.json").is_file(), "the SET copy is placed with its marker");
        // the quick flow (Play, a runtime install) resumes the active profile under quick.lock
        quick::resume_active(&dirs, &game_z).unwrap();

        // a save backup under the active profile
        let saves_dir = match game_z.prefix.as_ref() {
            Some(pfx) => pfx.join("drive_c/users/steamuser/Saved Games/Enshrouded"),
            None => base.join("Saved Games/Enshrouded"),
        };
        put(&saves_dir, "3ad85aea_1", b"world save");
        let backup = saves::backup_from(
            &dirs,
            &game_z,
            &[SaveDir { label: "local".into(), path: saves_dir.clone(), steam_id: None, recursive: true }],
            BackupReason::Manual,
            Some("data-folder fixture"),
            None,
        )
        .unwrap();
        assert!(dirs.profile(&active).join("save-backups").join(&backup.id).join("backup.json").is_file());

        // a runtime bundle
        let zip = bundle::create(&runtime_stage(&base), &base.join("out"), &CreateOptions { source_date_epoch: 1_793_793_600, git: "aaaaaaa".into() }).unwrap();
        let bundle = bundle::import(&dirs, &zip).unwrap();

        // a server, the launcher's record of it, a modpack built from it
        let server = base.join("steam/steamapps/common/EnshroudedServer");
        put(&server, "enshrouded_server.exe", b"MZ");
        util::copy_tree(&base.join("src/new-items"), &server.join("kiln/mods/new-items")).unwrap();
        let server_id = "isles".to_string();
        let mut rec = kiln_manage::server::instance::steam_instance(&server_id, &server, None);
        rec.source = "launcher".into();
        rec.name = "Isles".into();
        rec.backup_dir = Some(dirs.backups().join("servers").join(&server_id)); // what steam_instance gives with this root
        util::write_json_atomic(&dirs.root.join("servers").join(format!("{server_id}.json")), &rec).unwrap();
        let modpack = modpack::build(&server, &dirs.root.join("modpacks").join("built").join(&server_id), "Isles", "1", &mut NoProgress).unwrap();

        // settings.json (the launcher's LauncherSettings)
        util::write_json_atomic(&dirs.settings(), &serde_json::json!({
            "steamRoots": [], "defaultGameId": game_z.id, "allowHttpModpacks": false, "savesKeep": 10, "updateCheck": true,
            "animatedFire": true, "addToActiveProfile": true
        }))
        .unwrap();

        // nexus/handler.json: the nxm registration (as keystore::write_private writes it)
        let launcher_exe = base.join("apps/Kiln Launcher.AppImage");
        let state = kiln_manage::nexus::handler::HandlerState {
            format: 2,
            registered: true,
            previous: Some(kiln_manage::nexus::handler::Previous::Desktop { id: "com.nexusmods.app.desktop".into() }),
            exe: Some(launcher_exe.display().to_string()),
            registered_at: Some(util::now_rfc3339()),
            snapshot: None,
        };
        let handler = kiln_manage::nexus::catalog::root_of(&dirs).join("handler.json");
        std::fs::create_dir_all(handler.parent().unwrap()).unwrap();
        std::fs::write(&handler, serde_json::to_vec_pretty(&state).unwrap()).unwrap();
        #[cfg(unix)]
        {
            use std::os::unix::fs::PermissionsExt;
            std::fs::set_permissions(&handler, std::fs::Permissions::from_mode(0o600)).unwrap();
        }

        // a top-level folder the launcher does not know
        let unknown = dirs.root.join("My Notes");
        put(&unknown, "readme.txt", b"the player's own notes");

        Fixture { scratch, base, env, dirs, mods, game_z, game_copy, active, spare, bundle, server, server_id, modpack, backup, unknown, launcher_exe, saves_dir }
    }

    /// Activates the active profile again in both games (Z, copy).
    pub fn activate(&self) -> (Activation, Activation) {
        (profile::activate(&self.dirs, &self.active, &self.game_z).unwrap(), profile::activate(&self.dirs, &self.active, &self.game_copy).unwrap())
    }

    /// The folders the fixture made, by the label a report uses, most specific first.
    pub fn areas(&self) -> Vec<(&'static str, PathBuf)> {
        vec![
            ("<data>", self.dirs.root.clone()),
            ("<game Z>", self.game_z.path.clone()),
            ("<prefix Z>", self.game_z.prefix.clone().unwrap_or_else(|| self.saves_dir.clone())),
            ("<game copy>", self.game_copy.path.clone()),
            ("<server>", self.server.clone()),
            ("<state>", self.env.state_dir()),
            ("<config>", self.env.config_dir()),
            ("<scratch>", self.base.clone()),
        ]
    }
}
