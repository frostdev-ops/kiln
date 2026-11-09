//! Path checks used before profile activation and engine-mod placement.

use kiln_manage::engine_mods::refuse_symlink_path;
use kiln_manage::ErrorCode;
use std::path::PathBuf;

struct Tmp(PathBuf);
impl Drop for Tmp {
    fn drop(&mut self) {
        let _ = std::fs::remove_dir_all(&self.0);
    }
}
fn tmp(tag: &str) -> Tmp {
    let path = std::env::temp_dir().join(format!("km-path-safety-{tag}-{}", std::process::id()));
    std::fs::create_dir_all(&path).unwrap();
    Tmp(path.canonicalize().unwrap())
}

#[test]
fn existing_paths_and_missing_descendants_are_accepted() {
    let t = tmp("ordinary");
    std::fs::write(t.0.join("file.json"), b"{}").unwrap();
    for path in [&t.0, &t.0.join("file.json"), &t.0.join("missing/child.json")] {
        refuse_symlink_path(path).unwrap_or_else(|e| panic!("{}: {e}", path.display()));
    }
}

#[test]
fn parent_traversal_is_refused() {
    let t = tmp("parent");
    // PathBuf::join normalizes `..` in verbatim Windows paths; preserve the input being checked.
    let path = kiln_manage::util::with_suffix(&t.0,
        if cfg!(windows) { r"\missing\..\file.json" } else { "/missing/../file.json" });
    assert!(path.components().any(|c| c == std::path::Component::ParentDir));
    assert_eq!(refuse_symlink_path(&path).unwrap_err().code, ErrorCode::Refused);
}

#[cfg(windows)]
#[test]
fn windows_drive_roots_and_plain_paths_are_accepted() {
    let t = tmp("windows");
    // canonicalize returns the same extended-length form as the reported \\?\C:\... path.
    assert!(t.0.to_string_lossy().starts_with(r"\\?\"));
    let root = t.0.ancestors().last().unwrap();
    for path in [root.to_path_buf(), kiln_manage::steam::strip_verbatim(root.to_path_buf()),
                 kiln_manage::steam::strip_verbatim(t.0.clone())] {
        refuse_symlink_path(&path).unwrap_or_else(|e| panic!("{}: {e}", path.display()));
    }
}

#[cfg(unix)]
#[test]
fn symlink_leaves_ancestors_and_dangling_links_are_refused() {
    let t = tmp("symlink");
    let target = t.0.join("target");
    std::fs::create_dir(&target).unwrap();
    std::os::unix::fs::symlink(&target, t.0.join("link")).unwrap();
    std::os::unix::fs::symlink(t.0.join("absent"), t.0.join("dangling")).unwrap();
    for path in [t.0.join("link"), t.0.join("link/missing/child"), t.0.join("dangling")] {
        assert_eq!(refuse_symlink_path(&path).unwrap_err().code, ErrorCode::Refused, "{}", path.display());
    }
}

#[cfg(windows)]
fn junction_at(junction: &std::path::Path, target: &std::path::Path) {
    use std::os::windows::process::CommandExt;
    let output = std::process::Command::new("cmd").args(["/D", "/V:OFF", "/C"])
        .raw_arg(r#"mklink /J "%KILN_PATH_JUNCTION%" "%KILN_PATH_TARGET%""#)
        .env("KILN_PATH_JUNCTION", junction).env("KILN_PATH_TARGET", target)
        .env("KILN_PATH_LITERAL", "must-not-expand").output().unwrap();
    assert!(output.status.success(), "mklink: {output:?}");
}

#[cfg(windows)]
#[test]
fn windows_junctions_are_refused() {
    let t = tmp("junction");
    let target = t.0.join("target");
    let junction = t.0.join("junction");
    std::fs::create_dir(&target).unwrap();
    junction_at(&junction, &target);
    for path in [&junction, &junction.join("missing/child")] {
        assert_eq!(refuse_symlink_path(path).unwrap_err().code, ErrorCode::Refused, "{}", path.display());
    }
}

#[cfg(windows)]
#[test]
fn windows_junction_fixture_handles_cmd_metacharacters() {
    use std::os::windows::fs::MetadataExt;
    let t = tmp("cmd-metacharacters");
    let root = t.0.join("R&D!%KILN_PATH_LITERAL%^(data)");
    let target = root.join("target");
    let junction = root.join("junction");
    std::fs::create_dir_all(&target).unwrap();
    junction_at(&junction, &target);
    assert_ne!(std::fs::symlink_metadata(&junction).unwrap().file_attributes() & 0x400, 0);
    assert_eq!(refuse_symlink_path(&junction).unwrap_err().code, ErrorCode::Refused);
}

#[cfg(windows)]
#[test]
fn windows_drive_relative_bases_are_checked() {
    const CASE: &str = "KILN_PATH_BASE_CASE";
    if let Ok(case) = std::env::var(CASE) {
        use std::path::{Component, Prefix};
        let cwd = std::env::current_dir().unwrap();
        let drive = match cwd.components().next().unwrap() {
            Component::Prefix(p) => match p.kind() {
                Prefix::Disk(d) | Prefix::VerbatimDisk(d) => char::from(d),
                other => panic!("expected a drive path, got {other:?}"),
            },
            other => panic!("expected a drive path, got {other:?}"),
        };
        let data = tmp("drive-relative-data");
        let dirs = kiln_manage::paths::DataDirs::at(data.0.join("data"));
        let profile = kiln_manage::profile::create(&dirs, "Path safety", None).unwrap();
        let placed = kiln_manage::engine_mods::place(&dirs, &profile, std::path::Path::new(&format!("{drive}:")));
        let lock = std::path::Path::new("kiln/state/engine-mods.lock");
        if case == "junction" {
            assert!(matches!(&placed, Err(e) if e.code == ErrorCode::Refused),
                "placed through a junction: {placed:?}; lock created: {}", lock.exists());
            assert!(!lock.exists());
        } else {
            placed.unwrap();
            assert!(lock.is_file());
        }
        for tail in ["", "child", r".\child", r"missing\child", "."] {
            for drive in [drive, drive.to_ascii_lowercase()] {
                let path = PathBuf::from(format!("{drive}:{tail}"));
                let result = refuse_symlink_path(&path);
                if case == "junction" {
                    assert_eq!(result.unwrap_err().code, ErrorCode::Refused, "{} in {}", path.display(), cwd.display());
                } else {
                    result.unwrap_or_else(|e| panic!("{} in {}: {e}", path.display(), cwd.display()));
                }
            }
        }
        return;
    }

    let t = tmp("drive-relative");
    let target = t.0.join("target");
    let junction = t.0.join("junction");
    std::fs::create_dir(&target).unwrap();
    std::fs::create_dir(target.join("child")).unwrap();
    junction_at(&junction, &target);
    // A subprocess gives each case its own drive current directory without racing other tests.
    for (cwd, case) in [(&target, "ordinary"), (&junction, "junction")] {
        if target.join("kiln").exists() { std::fs::remove_dir_all(target.join("kiln")).unwrap(); }
        let output = std::process::Command::new(std::env::current_exe().unwrap())
            .args(["--exact", "windows_drive_relative_bases_are_checked", "--nocapture"])
            .env(CASE, case).current_dir(kiln_manage::steam::strip_verbatim(cwd.clone())).output().unwrap();
        assert!(output.status.success(), "{case}: {output:?}");
    }
}
