//! `kiln cook --game <dedicated server folder>`: the cook tools skip what only a game client draws (the models) and cook
//! the rest, so the cook succeeds, and it leaves the same mod as a player's cook: only assets/cooked differs. Made-up
//! games from tools/modelcook/tests/fakegame.py (nothing from the real game), the repository's cook tools through
//! KILN_REPO. Skipped when python3 cannot build the made-up games.
use std::path::{Path, PathBuf};
use std::process::Command;

fn repo() -> PathBuf {
    Path::new(env!("CARGO_MANIFEST_DIR")).join("../..").canonicalize().unwrap()
}

/// Every command gets scratch folders: the owner's Kiln data and settings are never resolved.
fn scratch_env<'a>(cmd: &'a mut Command, root: &Path) -> &'a mut Command {
    for (k, sub) in [("KILN_DATA_DIR", "data"), ("XDG_DATA_HOME", "xdg/data"), ("XDG_CONFIG_HOME", "xdg/config"), ("XDG_CACHE_HOME", "xdg/cache"),
                     ("XDG_STATE_HOME", "xdg/state"), ("TMPDIR", "tmp"), ("KILN_OUT", "out")] {
        let p = root.join(sub);
        std::fs::create_dir_all(&p).unwrap();
        cmd.env(k, p);
    }
    cmd.env("KILN_REPO", repo()).env_remove("KILN_GAME")
}

fn fake(root: &Path, side: &str, mod_dir: &Path) -> bool {
    let mut c = Command::new("python3");
    c.arg(repo().join("tools/modelcook/tests/fakegame.py"))
        .arg(root.join(side))
        .args(["--side", side, "--mod"])
        .arg(mod_dir)
        .arg("--kiln-out")
        .arg(root.join("out"));
    match scratch_env(&mut c, root).output() {
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

fn cook(root: &Path, mod_dir: &Path, game: &Path) -> std::process::Output {
    let mut c = Command::new(env!("CARGO_BIN_EXE_kiln"));
    c.arg("cook").arg(mod_dir).arg("--game").arg(game);
    scratch_env(&mut c, root).output().unwrap()
}

#[test]
fn cook_against_a_dedicated_server_skips_the_models_and_succeeds() {
    let root = std::env::temp_dir().join(format!("kiln-cli-cook-server-{}", std::process::id()));
    let _ = std::fs::remove_dir_all(&root);
    std::fs::create_dir_all(&root).unwrap();
    let (client_mod, server_mod) = (root.join("mod-client"), root.join("mod-server"));
    if !fake(&root, "client", &client_mod) || !fake(&root, "server", &server_mod) {
        std::fs::remove_dir_all(&root).unwrap();
        return;
    }
    // the author cooks on a client and ships assets.json; the server operator cooks the same mod against the server
    let client = cook(&root, &client_mod, &root.join("client"));
    assert!(client.status.success(), "client cook: {}{}", String::from_utf8_lossy(&client.stdout), String::from_utf8_lossy(&client.stderr));
    std::fs::copy(client_mod.join("assets/assets.json"), server_mod.join("assets/assets.json")).unwrap();
    // installed under the server's kiln/mods: the mod's folder with its kiln.mod.json, no project file
    std::fs::remove_file(server_mod.join("CMakeLists.txt")).unwrap();
    let server = cook(&root, &server_mod, &root.join("server"));
    let said = format!("{}{}", String::from_utf8_lossy(&server.stdout), String::from_utf8_lossy(&server.stderr));
    assert!(server.status.success(), "server cook: {said}");
    assert!(said.contains("step 0 model a: skipped on a dedicated server: only a game client draws it"), "{said}");
    assert!(said.contains("step 1 model b: skipped on a dedicated server: only a game client draws it"), "{said}");
    let cooked = server_mod.join("assets/cooked");
    assert!(cooked.join("clip_idle.bin").is_file() && cooked.join("v.bin").is_file());
    assert!(!cooked.join("a.bin").exists() && !cooked.join("b.blob").exists());
    // the manifest is the shipped file, byte for byte: no entry is marked (kiln.assets skips a model on a server by its type)
    let shipped = std::fs::read(client_mod.join("assets/assets.json")).unwrap();
    assert_eq!(std::fs::read(server_mod.join("assets/assets.json")).unwrap(), shipped);
    assert!(!String::from_utf8_lossy(&shipped).contains("\"side\""));
    // so both cooked folders are one mod: what the server advertises is what the player's game has
    std::fs::remove_file(client_mod.join("CMakeLists.txt")).unwrap();
    use kiln_manage::hash;
    assert_ne!(hash::dirhash(&server_mod).unwrap(), hash::dirhash(&client_mod).unwrap(), "the cooked files differ");
    assert_eq!(hash::mod_identity(&server_mod).unwrap(), hash::mod_identity(&client_mod).unwrap());
    let again = cook(&root, &server_mod, &root.join("server"));
    assert!(again.status.success());
    assert!(String::from_utf8_lossy(&again.stdout).contains("up to date"), "{}", String::from_utf8_lossy(&again.stdout));
    // "up to date" is the recipe's own check, which reads the mod's files and never the game. After the server was
    // updated its operator says --force: every recipe is cooked again from the server's files as they are now,
    // and the mod stays the one it was
    let mut forced = Command::new(env!("CARGO_BIN_EXE_kiln"));
    forced.arg("cook").arg(&server_mod).arg("--game").arg(root.join("server")).arg("--force");
    let forced = scratch_env(&mut forced, &root).output().unwrap();
    let said = format!("{}{}", String::from_utf8_lossy(&forced.stdout), String::from_utf8_lossy(&forced.stderr));
    assert!(forced.status.success(), "{said}");
    assert!(said.contains("kiln: cooking ") && !said.contains("up to date"), "{said}");
    assert_eq!(std::fs::read(server_mod.join("assets/assets.json")).unwrap(), shipped);
    assert_eq!(hash::mod_identity(&server_mod).unwrap(), hash::mod_identity(&client_mod).unwrap());

    // a server folder cooked with 0.2.0-dev.26: the models' entries marked "side": "client" in its copy of the manifest, and the
    // report of that cook. `kiln cook` sees the old report, cooks again and puts the shipped file back
    // (written by Python as that cook wrote it: the entries' keys in their order, json.dumps(indent=2))
    let dev26 = r#"
import json, sys
manifest, report = sys.argv[1:]
man = json.load(open(manifest))
for e in man["entries"]:
    if e["file"] in ("cooked/a.bin", "cooked/a.blob", "cooked/b.bin", "cooked/b.blob"):
        e["side"] = "client"
open(manifest, "w").write(json.dumps(man, indent=2) + "\n")
rep = json.load(open(report))
rep["manifest"] = {"missing": [], "mismatched": []}
open(report, "w").write(json.dumps(rep, indent=2) + "\n")
"#;
    let marked = Command::new("python3").arg("-c").arg(dev26).arg(server_mod.join("assets/assets.json")).arg(server_mod.join("assets/cooked/cook-report.json")).status().unwrap();
    assert!(marked.success());
    assert!(std::fs::read_to_string(server_mod.join("assets/assets.json")).unwrap().contains("\"side\": \"client\""));
    assert_ne!(hash::mod_identity(&server_mod).unwrap(), hash::mod_identity(&client_mod).unwrap(), "what dev.26 left: another mod");
    let healed = cook(&root, &server_mod, &root.join("server"));
    let said = format!("{}{}", String::from_utf8_lossy(&healed.stdout), String::from_utf8_lossy(&healed.stderr));
    assert!(healed.status.success(), "{said}");
    assert!(said.contains("took the \"side\": \"client\" mark of an older cook out of 4 entries"), "{said}");
    assert_eq!(std::fs::read(server_mod.join("assets/assets.json")).unwrap(), shipped);
    assert_eq!(hash::mod_identity(&server_mod).unwrap(), hash::mod_identity(&client_mod).unwrap());
    std::fs::remove_dir_all(&root).unwrap();
}
