//! The CLI must participate in the launcher's data-folder move/recovery lock.
use kiln_manage::data_folder::DataLock;
use std::process::Command;

#[test]
fn a_cli_command_refuses_an_exclusively_held_data_folder() {
    let root = std::env::temp_dir().join(format!("kiln-cli-data-lock-{}", std::process::id()));
    std::fs::create_dir_all(&root).unwrap();
    let lock = DataLock::exclusive(&root).unwrap().unwrap();
    let run = || Command::new(env!("CARGO_BIN_EXE_kiln"))
        .env("KILN_DATA_DIR", &root)
        .args(["--json", "catalog", "path"])
        .output().unwrap();
    let blocked = run();
    drop(lock);
    let free = run();
    std::fs::remove_dir_all(&root).unwrap();
    assert!(!blocked.status.success(), "CLI ignored the recovery lock: {}", String::from_utf8_lossy(&blocked.stdout));
    let out: serde_json::Value = serde_json::from_slice(&blocked.stdout).unwrap();
    assert_eq!(out["error"]["code"], "busy", "{out}");
    assert!(free.status.success(), "{}", String::from_utf8_lossy(&free.stdout));
}
