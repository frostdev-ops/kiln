//! `kiln cook` with no Kiln checkout: the cook tools are the copy this kiln carries (kiln_manage::cooktools), unpacked
//! once under the data folder. A player and a server operator are in this place. The made-up game comes from
//! tools/modelcook/tests/fakegame.py (nothing from the real game). Skipped when python3 cannot build it.
use std::path::{Path, PathBuf};
use std::process::Command;

fn repo() -> PathBuf {
    Path::new(env!("CARGO_MANIFEST_DIR")).join("../..").canonicalize().unwrap()
}

/// Scratch folders for everything Kiln resolves, and KILN_REPO at a folder with no tools in it: the kiln under test
/// was built in a checkout and would take that checkout's tools otherwise.
fn no_checkout<'a>(cmd: &'a mut Command, root: &Path) -> &'a mut Command {
    for (k, sub) in [("KILN_DATA_DIR", "data"), ("XDG_DATA_HOME", "xdg/data"), ("XDG_CONFIG_HOME", "xdg/config"), ("XDG_CACHE_HOME", "xdg/cache"),
                     ("XDG_STATE_HOME", "xdg/state"), ("TMPDIR", "tmp"), ("KILN_OUT", "out"), ("KILN_REPO", "no-checkout")] {
        let p = root.join(sub);
        std::fs::create_dir_all(&p).unwrap();
        cmd.env(k, p);
    }
    cmd.env_remove("KILN_GAME").env_remove("KILN_PYTHON")
}

fn fake_game(root: &Path, mod_dir: &Path) -> bool {
    let mut c = Command::new("python3");
    c.arg(repo().join("tools/modelcook/tests/fakegame.py")).arg(root.join("client")).args(["--side", "client", "--mod"]).arg(mod_dir);
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

#[test]
fn cook_without_a_checkout_runs_the_tools_this_kiln_carries() {
    let root = std::env::temp_dir().join(format!("kiln-cli-cook-shipped-{}", std::process::id()));
    let _ = std::fs::remove_dir_all(&root);
    std::fs::create_dir_all(&root).unwrap();
    let mod_dir = root.join("mod");
    if !fake_game(&root, &mod_dir) {
        std::fs::remove_dir_all(&root).unwrap();
        return;
    }
    let cook = |extra: &[&str]| {
        let mut c = Command::new(env!("CARGO_BIN_EXE_kiln"));
        c.arg("cook").arg(&mod_dir).arg("--game").arg(root.join("client")).args(extra);
        let o = no_checkout(&mut c, &root).output().unwrap();
        (o.status.success(), format!("{}{}", String::from_utf8_lossy(&o.stdout), String::from_utf8_lossy(&o.stderr)))
    };
    let (ok, said) = cook(&["--check"]);
    assert!(!ok && said.contains("cook needed"), "before the cook, --check says it is needed: {said}");
    let (ok, said) = cook(&[]);
    assert!(ok, "the cook: {said}");
    assert!(mod_dir.join("assets/cooked").is_dir(), "{said}");
    // the tools ran from the copy under the data folder, not from a checkout
    let copies: Vec<PathBuf> = std::fs::read_dir(root.join("data/cooktools")).unwrap().flatten().map(|e| e.path()).collect();
    assert_eq!(copies.len(), 1, "one unpacked copy: {copies:?}");
    assert!(copies[0].join("tools/modelcook/modelcook.py").is_file() && copies[0].join(".complete").is_file());
    assert!(!root.join("no-checkout/tools").exists());
    let (ok, said) = cook(&["--check"]);
    assert!(ok && said.contains("up to date"), "after the cook: {said}");
    std::fs::remove_dir_all(&root).unwrap();
}
