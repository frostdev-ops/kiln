//! `kiln cook --game <dedicated server folder>`: the cook tools skip what only a game client draws (the models) and cook
//! the rest, so the cook succeeds. Made-up games from tools/modelcook/tests/fakegame.py (nothing from the real game), the
//! repository's cook tools through KILN_REPO. Skipped when python3 cannot build the made-up games.
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
    // the manifest keeps the clients' entries; the models' are marked for clients only, so kiln.assets skips them quietly
    let man: serde_json::Value = serde_json::from_str(&std::fs::read_to_string(server_mod.join("assets/assets.json")).unwrap()).unwrap();
    for e in man["entries"].as_array().unwrap() {
        let file = e["file"].as_str().unwrap();
        let model = ["cooked/a.bin", "cooked/a.blob", "cooked/b.bin", "cooked/b.blob"].contains(&file);
        assert_eq!(e.get("side").and_then(|s| s.as_str()) == Some("client"), model, "{e}");
    }
    let again = cook(&root, &server_mod, &root.join("server"));
    assert!(again.status.success());
    assert!(String::from_utf8_lossy(&again.stdout).contains("up to date"), "{}", String::from_utf8_lossy(&again.stdout));
    std::fs::remove_dir_all(&root).unwrap();
}
