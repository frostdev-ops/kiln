//! `kiln boot-snapshot`: the command end to end on a made-up dedicated server (tools/modelcook/tests/fakegame.py:
//! a synthetic archive with one template, nothing from the real game) with the repository's tools/bootsnap through
//! KILN_REPO. The made-up archive has no items, skill tree or terraforming registry: those kinds are written empty and
//! named, and the command still ends well. Skipped when python3 cannot build the made-up game.
use serde_json::Value;
use std::path::{Path, PathBuf};
use std::process::Command;

fn repo() -> PathBuf {
    Path::new(env!("CARGO_MANIFEST_DIR")).join("../..").canonicalize().unwrap()
}

fn scratch_env<'a>(cmd: &'a mut Command, root: &Path) -> &'a mut Command {
    for (k, sub) in [("KILN_DATA_DIR", "data"), ("XDG_DATA_HOME", "xdg/data"), ("XDG_CONFIG_HOME", "xdg/config"), ("XDG_CACHE_HOME", "xdg/cache"),
                     ("XDG_STATE_HOME", "xdg/state"), ("TMPDIR", "tmp"), ("KILN_OUT", "out"), ("HOME", "home")] {
        let p = root.join(sub);
        std::fs::create_dir_all(&p).unwrap();
        cmd.env(k, p);
    }
    cmd.env("KILN_REPO", repo())
}

fn kiln(root: &Path, args: &[&str]) -> (i32, String, String) {
    let mut c = Command::new(env!("CARGO_BIN_EXE_kiln"));
    c.args(args);
    let o = scratch_env(&mut c, root).output().unwrap();
    (o.status.code().unwrap_or(-1), String::from_utf8_lossy(&o.stdout).into_owned(), String::from_utf8_lossy(&o.stderr).into_owned())
}

#[test]
fn kiln_boot_snapshot_makes_reuses_and_names_what_it_lacks() {
    let root = std::env::temp_dir().join(format!("kiln-boot-snapshot-cli-{}", std::process::id()));
    let _ = std::fs::remove_dir_all(&root);
    std::fs::create_dir_all(&root).unwrap();
    let root = root.canonicalize().unwrap();
    let server = root.join("server");
    let mut c = Command::new("python3");
    c.arg(repo().join("tools/modelcook/tests/fakegame.py")).arg(&server).args(["--side", "server", "--kiln-out"]).arg(root.join("out"));
    match scratch_env(&mut c, &root).output() {
        Ok(o) if o.status.success() => {}
        other => {
            eprintln!("skipped: the made-up game could not be built: {other:?}");
            let _ = std::fs::remove_dir_all(&root);
            return;
        }
    }
    let game = server.to_str().unwrap();
    let (code, out, err) = kiln(&root, &["boot-snapshot", "--game", game, "--json"]);
    assert_eq!(code, 0, "stdout {out}\nstderr {err}");
    let r: Value = serde_json::from_str(out.trim()).unwrap_or_else(|e| panic!("one JSON document ({e}): {out}"));
    assert_eq!(r["status"], "made", "{r}");
    assert_eq!(r["counts"]["templates"], 1, "{r}");
    let empty: Vec<&str> = r["emptyKinds"].as_array().unwrap().iter().map(|v| v.as_str().unwrap()).collect();
    assert!(empty.contains(&"skills") && empty.contains(&"materials"), "{r}");
    assert!(err.contains("warning: The game data for OnBoot mods has no"), "the empty kinds are named: {err}");
    let key = r["key"].as_str().unwrap();
    let dir = server.join("kiln/data/boot-gamedata").join(key);
    assert!(dir.join("index.json").is_file() && dir.join("templates.data").is_file());
    let idx: Value = serde_json::from_str(&std::fs::read_to_string(dir.join("index.json")).unwrap()).unwrap();
    assert_eq!((idx["format"].as_i64(), idx["key"].as_str(), idx["side"].as_str()), (Some(1), Some(key), Some("server")));
    let tpl: Value = serde_json::from_str(&std::fs::read_to_string(dir.join("templates.index.json")).unwrap()).unwrap();
    assert!(tpl["entries"]["Visuals_Base"].is_array(), "{tpl}");

    // the same game build again: reused, nothing written
    let (code, out, _) = kiln(&root, &["boot-snapshot", "--game", game]);
    assert_eq!(code, 0);
    assert!(out.contains("is current"), "{out}");
    let (code, out, _) = kiln(&root, &["boot-snapshot", "--game", game, "--force", "--json"]);
    assert_eq!(code, 0);
    assert_eq!(serde_json::from_str::<Value>(out.trim()).unwrap()["status"], "made");

    // no game there: exit 1 with the reason
    let (code, out, err) = kiln(&root, &["boot-snapshot", "--game", root.join("nothing").to_str().unwrap(), "--json"]);
    assert_eq!(code, 1, "{out} {err}");
    let r: Value = serde_json::from_str(out.trim()).unwrap();
    assert_eq!(r["status"], "refused");
    assert!(r["reason"].as_str().unwrap().contains("neither enshrouded_server.exe nor enshrouded.exe"), "{r}");
    let _ = std::fs::remove_dir_all(&root);
}
