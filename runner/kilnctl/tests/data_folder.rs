//! `kilnctl` and the launcher's data folder lock (contracts.md section 3), with the real binary and scratch folders
//! only: a command that uses the data root refuses a folder a launcher holds exclusively (a move, the removal of an
//! unfinished copy) before it writes anything, runs once the lock is gone and next to a shared holder (a launcher, the
//! `kiln` CLI, a `kilnctl` that started this one); a command that needs no data root runs without one and makes none.
use kiln_manage::data_folder::DataLock;
use std::path::{Path, PathBuf};
use std::process::{Command, Output};

const BUSY: &str = "Kiln is moving or recovering its data folder: wait for it to finish.";

/// A scratch folder for one test, under the test run's TMPDIR.
fn scratch(name: &str) -> PathBuf {
    let t = std::env::temp_dir().join(format!("kilnctl-lock-{name}-{}", std::process::id()));
    let _ = std::fs::remove_dir_all(&t);
    std::fs::create_dir_all(&t).unwrap();
    t
}

/// `kilnctl <args>` with every folder it could use inside `t` and the data root at `data`.
fn kilnctl(t: &Path, data: &Path, args: &[&str]) -> Output {
    Command::new(env!("CARGO_BIN_EXE_kilnctl"))
        .env("KILN_DATA_DIR", data)
        .env("HOME", t.join("home"))
        .env("XDG_DATA_HOME", t.join("xdg-data"))
        .env("XDG_STATE_HOME", t.join("xdg-state"))
        .env("XDG_CONFIG_HOME", t.join("xdg-config"))
        .args(args)
        .output()
        .unwrap()
}

fn text(o: &Output) -> String {
    format!("{}{}", String::from_utf8_lossy(&o.stdout), String::from_utf8_lossy(&o.stderr))
}

/// Every path below `root`.
fn tree(root: &Path) -> Vec<PathBuf> {
    let mut v = Vec::new();
    let mut todo = vec![root.to_path_buf()];
    while let Some(d) = todo.pop() {
        for e in std::fs::read_dir(&d).into_iter().flatten().flatten() {
            if e.file_type().is_ok_and(|f| f.is_dir()) {
                todo.push(e.path());
            }
            v.push(e.path());
        }
    }
    v.sort();
    v
}

/// The verifier's case: a launcher holds the folder exclusively, `kilnctl profile create` must not write the profile.
#[test]
fn profile_create_refuses_an_exclusively_held_folder_and_writes_nothing() {
    let t = scratch("exclusive");
    let data = t.join("data");
    std::fs::create_dir_all(&data).unwrap();
    let lock = DataLock::exclusive(&data).unwrap().unwrap();
    let before = tree(&data);
    let blocked = kilnctl(&t, &data, &["profile", "create", "Lock probe"]);
    let after = tree(&data);
    drop(lock);
    assert!(!blocked.status.success(), "kilnctl wrote while a launcher held the folder exclusively: {}", text(&blocked));
    assert_eq!(blocked.status.code(), Some(1));
    assert!(text(&blocked).contains(&format!("kilnctl: error: {BUSY}")), "{}", text(&blocked));
    assert_eq!(after, before, "nothing was written");
    assert!(!data.join("profiles").exists());

    // the lock is gone: it works
    let free = kilnctl(&t, &data, &["profile", "create", "Lock probe"]);
    assert!(free.status.success(), "{}", text(&free));
    assert!(data.join("profiles").join("lock-probe").is_dir(), "{:?}", tree(&data));
    let _ = std::fs::remove_dir_all(&t);
}

/// Shared holders coexist: a launcher (or the `kiln` CLI, or a `kilnctl` that started this one) holds the lock shared,
/// and `kilnctl` runs; while it runs a launcher's move cannot take the lock exclusively.
#[test]
fn a_command_runs_next_to_a_shared_holder() {
    let t = scratch("shared");
    let data = t.join("data");
    std::fs::create_dir_all(&data).unwrap();
    let parent = DataLock::shared(&data).unwrap().unwrap();
    let o = kilnctl(&t, &data, &["profile", "create", "Next to a launcher"]);
    assert!(o.status.success(), "{}", text(&o));
    assert!(data.join("profiles").join("next-to-a-launcher").is_dir());
    let list = kilnctl(&t, &data, &["library", "list"]);
    assert!(list.status.success(), "{}", text(&list));
    drop(parent);
    // and the command let the lock go when it ended
    assert!(DataLock::exclusive(&data).unwrap().is_some(), "kilnctl still holds the lock after it ended");
    let _ = std::fs::remove_dir_all(&t);
}

/// A command that needs no data root (here `list`, the instances in Kiln's config folder) runs with none and makes
/// none, and runs while a launcher holds the folder exclusively (no lock taken).
#[test]
fn a_command_without_the_data_root_needs_none_and_makes_none() {
    let t = scratch("none");
    let missing = t.join("no-data-here");
    let o = kilnctl(&t, &missing, &["list"]);
    assert!(o.status.success(), "{}", text(&o));
    assert!(!missing.exists(), "a data folder was made just to lock it");

    let data = t.join("data");
    std::fs::create_dir_all(&data).unwrap();
    let lock = DataLock::exclusive(&data).unwrap().unwrap();
    let o = kilnctl(&t, &data, &["list"]);
    drop(lock);
    assert!(o.status.success(), "a command that does not use the data root was refused: {}", text(&o));
    let _ = std::fs::remove_dir_all(&t);
}

/// A data root that is not there yet (a first run): a command that uses it takes no lock and makes no folder for one;
/// the command itself does what it did before.
#[test]
fn a_first_run_without_a_data_folder_takes_no_lock() {
    let t = scratch("first-run");
    let missing = t.join("data");
    let o = kilnctl(&t, &missing, &["library", "list"]);
    assert!(!text(&o).contains(BUSY), "{}", text(&o));
    assert!(!missing.join(kiln_manage::data_folder::LOCK).exists(), "no lock file made in a folder the command did not use");
    let _ = std::fs::remove_dir_all(&t);
}
