//! `kiln cook` as it was before its body moved into kiln_manage::cook (0.2.0-dev.27): the exit codes, `--check`, the
//! `--json` shape, the sound blocks' step, the looks of the blocks, the lines and the stream each goes to. Made-up games
//! from tools/modelcook/tests/fakegame.py (nothing from the real game), the repository's cook tools through KILN_REPO.
//! Skipped when python3 cannot build the made-up game.
//!
//! `KILN_CLI_UNDER_TEST=<path to a kiln binary>` runs the same checks against another build (the proof that a build
//! from before the move answers alike).
use serde_json::{Value, json};
use std::path::{Path, PathBuf};
use std::process::{Command, Output};

fn repo() -> PathBuf {
    Path::new(env!("CARGO_MANIFEST_DIR")).join("../..").canonicalize().unwrap()
}

fn kiln() -> Command {
    Command::new(std::env::var_os("KILN_CLI_UNDER_TEST").map(PathBuf::from).unwrap_or_else(|| PathBuf::from(env!("CARGO_BIN_EXE_kiln"))))
}

/// Every command gets scratch folders: the owner's Kiln data and settings are never resolved, and no game is found but
/// the one named.
fn scratch_env<'a>(cmd: &'a mut Command, root: &Path) -> &'a mut Command {
    for (k, sub) in [("KILN_DATA_DIR", "data"), ("XDG_DATA_HOME", "xdg/data"), ("XDG_CONFIG_HOME", "xdg/config"), ("XDG_CACHE_HOME", "xdg/cache"),
                     ("XDG_STATE_HOME", "xdg/state"), ("TMPDIR", "tmp"), ("KILN_OUT", "out"), ("HOME", "home")] {
        let p = root.join(sub);
        std::fs::create_dir_all(&p).unwrap();
        cmd.env(k, p);
    }
    cmd.env("KILN_REPO", repo()).env("KILN_GAME", root.join("client"))
}

fn run(root: &Path, args: &[&str], dir: &Path) -> (i32, String, String) {
    let mut c = kiln();
    c.arg("cook").arg(dir).args(args);
    let o: Output = scratch_env(&mut c, root).output().unwrap();
    (o.status.code().unwrap_or(-1), String::from_utf8_lossy(&o.stdout).into_owned(), String::from_utf8_lossy(&o.stderr).into_owned())
}

fn json_of(stdout: &str) -> Value {
    serde_json::from_str(stdout.trim()).unwrap_or_else(|e| panic!("stdout is one JSON document ({e}): {stdout}"))
}

#[test]
fn kiln_cook_answers_as_it_did_before_the_move() {
    let root = std::env::temp_dir().join(format!("kiln-cook-cli-{}", std::process::id()));
    let _ = std::fs::remove_dir_all(&root);
    std::fs::create_dir_all(&root).unwrap();
    let root = root.canonicalize().unwrap();
    let sword = root.join("mods/sword");
    let mut c = Command::new("python3");
    c.arg(repo().join("tools/modelcook/tests/fakegame.py")).arg(root.join("client")).args(["--side", "client", "--mod"]).arg(&sword)
        .arg("--kiln-out").arg(root.join("out"));
    match scratch_env(&mut c, &root).output() {
        Ok(o) if o.status.success() => {}
        other => {
            eprintln!("skipped: the made-up game could not be built: {other:?}");
            let _ = std::fs::remove_dir_all(&root);
            return;
        }
    }
    let _ = std::fs::remove_file(sword.join("CMakeLists.txt"));
    std::fs::write(sword.join("kiln.mod.json"), r#"{ "id": "sword", "name": "Sword", "version": "1.0.0", "apiVersion": "0.2", "entryAssembly": "M.dll", "side": "both" }"#).unwrap();
    let recipe = sword.join("assets/cook.json");
    let r = recipe.display().to_string();

    // a folder without anything to cook: said, exit 0; --json: ok and no recipes
    let plain = root.join("mods/plain");
    std::fs::create_dir_all(&plain).unwrap();
    std::fs::write(plain.join("kiln.mod.json"), r#"{ "id": "plain", "version": "1.0.0", "apiVersion": "0.2", "entryAssembly": "M.dll" }"#).unwrap();
    let (code, out, _) = run(&root, &[], &plain);
    assert_eq!((code, out.trim()), (0, "no cook recipes (*/cook.json) in this project"));
    let (code, out, err) = run(&root, &["--json"], &plain);
    assert_eq!((code, json_of(&out)), (0, json!({ "ok": true, "recipes": [] })));
    assert_eq!(err.trim(), "no cook recipes (*/cook.json) in this project");
    // blocks with no looks in them: the looks' probe says so, exit 0
    std::fs::create_dir_all(plain.join("blocks")).unwrap();
    std::fs::write(plain.join("blocks/items.json"), r#"{ "format": 1, "items": {} }"#).unwrap();
    let (code, out, _) = run(&root, &[], &plain);
    assert_eq!((code, out.trim()), (0, "no cook recipes (*/cook.json) in this project, and no looks with files in blocks/*.json"));

    // --check before the first cook: exit 1, the recipe "cook needed"; --json: needed true, no ok/cooked keys
    let (code, out, _) = run(&root, &["--check"], &sword);
    assert_eq!((code, out.trim()), (1, format!("{r}: cook needed").as_str()));
    let (code, out, _) = run(&root, &["--check", "--json"], &sword);
    assert_eq!((code, json_of(&out)), (1, json!({ "ok": false, "recipes": [{ "recipe": r, "tool": "modelcook", "needed": true }] })));

    // the cook: what Kiln says and each line of the tools on stdout, exit 0; nothing in the game folder but what the
    // tools read (a run by hand writes no receipt; this game has no kiln folder)
    let (code, out, err) = run(&root, &[], &sword);
    assert_eq!(code, 0, "{out}{err}");
    assert!(out.starts_with(&format!("kiln: cooking {r} (modelcook)\n")), "{out}");
    assert!(out.contains("ok: ") && out.contains(" file(s), report "), "{out}");
    assert!(sword.join("assets/cooked/cook-report.json").is_file());
    // again: up to date, said; --json: ok true, cooked false
    let (code, out, _) = run(&root, &[], &sword);
    assert_eq!((code, out.trim()), (0, format!("{r}: up to date").as_str()));
    let (code, out, _) = run(&root, &["--json"], &sword);
    assert_eq!((code, json_of(&out)), (0, json!({ "ok": true, "recipes": [{ "recipe": r, "tool": "modelcook", "ok": true, "cooked": false }] })));
    let (code, out, _) = run(&root, &["--check"], &sword);
    assert_eq!((code, out.trim()), (0, format!("{r}: up to date").as_str()));

    // a cook that fails: exit 1; the tool's error goes to stderr as it did, Kiln's line to stdout; --json: stdout is the
    // result alone (ok false, cooked true), every line on stderr
    let broken = root.join("mods/broken");
    kiln_manage::util::copy_tree(&sword, &broken).unwrap();
    std::fs::remove_dir_all(broken.join("assets/cooked")).unwrap();
    std::fs::remove_file(broken.join("assets/src/sword.gltf")).unwrap();
    let br = broken.join("assets/cook.json").display().to_string();
    let (code, out, err) = run(&root, &[], &broken);
    assert_eq!(code, 1, "{out}{err}");
    assert!(out.starts_with(&format!("kiln: cooking {br} (modelcook)\n")), "{out}");
    assert!(err.contains("sword.gltf") && !out.contains("sword.gltf"), "the tool's error on stderr: {err} / {out}");
    let (code, out, err) = run(&root, &["--json"], &broken);
    assert_eq!((code, json_of(&out)), (1, json!({ "ok": false, "recipes": [{ "recipe": br, "tool": "modelcook", "ok": false, "cooked": true }] })));
    assert!(err.contains(&format!("kiln: cooking {br} (modelcook)")) && err.contains("sword.gltf"), "{err}");

    // a recipe of a tool there is none of: exit 1 with the error, nothing cooked
    std::fs::write(broken.join("assets/cook.json"), r#"{ "format": 1, "tool": "nonsense", "out": "cooked", "steps": [] }"#).unwrap();
    let (code, out, err) = run(&root, &[], &broken);
    assert_eq!(code, 1, "{out}{err}");
    assert!(err.trim().starts_with(&format!("kiln: {br}: no cook tool 'nonsense' in ")), "{err}");
    let (code, out, _) = run(&root, &["--json"], &broken);
    let v = json_of(&out);
    assert_eq!((code, &v["ok"], &v["error"]["code"]), (1, &json!(false), &json!("tool_missing")), "{v}");

    // the sound blocks' step comes first: a check says the recipe is behind the blocks (exit 1); the cook writes their
    // steps into assets/cook.json and says so
    let sounds = root.join("mods/sounds");
    std::fs::create_dir_all(sounds.join("blocks")).unwrap();
    std::fs::create_dir_all(sounds.join("audio")).unwrap();
    std::fs::write(sounds.join("audio/a.wav"), b"RIFF").unwrap();
    std::fs::write(sounds.join("kiln.mod.json"), r#"{ "id": "sounds", "version": "1.0.0", "apiVersion": "0.2", "entryAssembly": "M.dll" }"#).unwrap();
    std::fs::write(sounds.join("blocks/s.json"), r#"{ "format": 1, "sounds": { "hum": { "clips": ["audio/a.wav"] } } }"#).unwrap();
    let (code, out, _) = run(&root, &["--check"], &sounds);
    assert_eq!(code, 1, "{out}");
    assert!(out.lines().next().is_some_and(|l| l.starts_with("kiln: ") && l.contains("behind the blocks (1 sound block(s))")), "{out}");
    let (_, out, err) = run(&root, &[], &sounds);
    assert!(out.lines().next().is_some_and(|l| l.starts_with("kiln: ") && l.contains("written, 1 sound block(s): hum")), "{out}{err}");
    assert!(sounds.join("assets/cook.json").is_file());
    let (_, out, _) = run(&root, &["--check"], &sounds);
    assert!(out.lines().next().is_some_and(|l| l.contains("up to date (1 sound block(s))")), "{out}");
    let _ = std::fs::remove_dir_all(&root);
}

/// `kiln build`'s cook before the build: silent when nothing is to cook, the tools' lines when it cooks, a warning (and
/// the build goes on) for a recipe whose tool or game is missing. The build itself fails here on purpose (no dotnet on
/// the PATH these commands get): only what the cook said before it is looked at.
#[test]
fn kiln_builds_cook_answers_as_it_did_before_the_move() {
    let root = std::env::temp_dir().join(format!("kiln-build-cook-{}", std::process::id()));
    let _ = std::fs::remove_dir_all(&root);
    std::fs::create_dir_all(root.join("bin")).unwrap();
    let root = root.canonicalize().unwrap();
    let python = std::env::split_paths(&std::env::var_os("PATH").unwrap_or_default()).map(|d| d.join("python3")).find(|p| p.is_file());
    let Some(python) = python else {
        eprintln!("skipped: no python3");
        return;
    };
    #[cfg(unix)]
    std::os::unix::fs::symlink(&python, root.join("bin/python3")).unwrap();
    let sword = root.join("mods/sword");
    let mut c = Command::new(&python);
    c.arg(repo().join("tools/modelcook/tests/fakegame.py")).arg(root.join("client")).args(["--side", "client", "--mod"]).arg(&sword)
        .arg("--kiln-out").arg(root.join("out"));
    match scratch_env(&mut c, &root).output() {
        Ok(o) if o.status.success() => {}
        other => {
            eprintln!("skipped: the made-up game could not be built: {other:?}");
            let _ = std::fs::remove_dir_all(&root);
            return;
        }
    }
    let _ = std::fs::remove_file(sword.join("CMakeLists.txt"));
    std::fs::write(sword.join("kiln.mod.json"), r#"{ "id": "sword", "name": "Sword", "version": "1.0.0", "apiVersion": "0.2", "entryAssembly": "M.dll", "side": "both" }"#).unwrap();
    std::fs::write(sword.join("Sword.csproj"), "<Project Sdk=\"Kiln.Sdk\"></Project>\n").unwrap();
    let r = sword.join("assets/cook.json").display().to_string();
    let build = |game: bool| {
        let mut c = kiln();
        c.arg("build").arg(&sword);
        scratch_env(&mut c, &root).env("PATH", root.join("bin"));
        if !game {
            c.env_remove("KILN_GAME");
        }
        let o = c.output().unwrap();
        (o.status.code().unwrap_or(-1), String::from_utf8_lossy(&o.stdout).into_owned(), String::from_utf8_lossy(&o.stderr).into_owned())
    };
    // no game found: a warning, nothing cooked, and the build goes on (to dotnet, which is not there)
    let (code, out, err) = build(false);
    assert_eq!(code, 1, "{out}{err}");
    assert_eq!(out.trim(), format!("kiln: warning: {r}: the cook reads your installed game; none found (--game or KILN_GAME)"), "{err}");
    assert!(err.contains("dotnet"), "{err}");
    // the cook runs: Kiln's line and the tools' lines, then the build
    let (_, out, err) = build(true);
    assert!(out.starts_with(&format!("kiln: cooking {r} (modelcook)\n")) && out.contains(" file(s), report "), "{out}{err}");
    assert!(err.contains("dotnet"), "{err}");
    // up to date: not a word from the cook
    let (_, out, err) = build(true);
    assert_eq!(out, "", "{err}");
    // a recipe of a tool there is none of: a warning, and the build goes on
    std::fs::write(sword.join("assets/cook.json"), r#"{ "format": 1, "tool": "nonsense", "out": "cooked", "steps": [] }"#).unwrap();
    let (_, out, err) = build(true);
    assert!(out.trim().starts_with(&format!("kiln: warning: {r}: no cook tool 'nonsense' in ")), "{out}{err}");
    assert!(err.contains("dotnet"), "{err}");
    let _ = std::fs::remove_dir_all(&root);
}
