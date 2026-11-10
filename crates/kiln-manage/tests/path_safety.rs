//! Path checks used before profile activation and engine-mod placement.
//!
//! `refuse_symlink_path(root, path)` refuses links from the managed `root` down. The folders above `root` belong to the
//! system (Fedora Atomic: `/home -> /var/home`) and may be links.

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
        refuse_symlink_path(&t.0, path).unwrap_or_else(|e| panic!("{}: {e}", path.display()));
    }
}

#[test]
fn parent_traversal_is_refused() {
    let t = tmp("parent");
    // PathBuf::join normalizes `..` in verbatim Windows paths; preserve the input being checked.
    let path = kiln_manage::util::with_suffix(&t.0,
        if cfg!(windows) { r"\missing\..\file.json" } else { "/missing/../file.json" });
    assert!(path.components().any(|c| c == std::path::Component::ParentDir));
    assert_eq!(refuse_symlink_path(&t.0, &path).unwrap_err().code, ErrorCode::Refused);
    // `..` is refused whether or not the link check would have reached it.
    assert_eq!(refuse_symlink_path(&path, &path).unwrap_err().code, ErrorCode::Refused);
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
        refuse_symlink_path(&path, &path).unwrap_or_else(|e| panic!("{}: {e}", path.display()));
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
        assert_eq!(refuse_symlink_path(&t.0, &path).unwrap_err().code, ErrorCode::Refused, "{}", path.display());
    }
}

/// Fedora Atomic / Bazzite: `/home` is a link to `/var/home`. Everything above the managed root may be a link.
#[cfg(unix)]
#[test]
fn symlinked_system_ancestors_above_the_root_are_accepted() {
    let t = tmp("system-ancestors");
    // <t>/var/home/user/.local/share/kiln is the data dir; <t>/home -> var/home (relative, like the real one).
    let data = t.0.join("var/home/user/.local/share/kiln");
    std::fs::create_dir_all(data.join("library/set/m/1.0.0")).unwrap();
    std::fs::write(data.join("library/set/m/1.0.0/init.lua"), b"x").unwrap();
    std::os::unix::fs::symlink("var/home", t.0.join("home")).unwrap();
    let linked = t.0.join("home/user/.local/share/kiln");
    for path in [linked.clone(), linked.join("library/set/m/1.0.0/init.lua"), linked.join("kiln/state/engine-mods.lock"),
                 linked.join("missing/child")] {
        refuse_symlink_path(&linked, &path).unwrap_or_else(|e| panic!("{}: {e}", path.display()));
    }
    // a second linked ancestor, further up the chain
    std::os::unix::fs::symlink(t.0.join("var"), t.0.join("mnt")).unwrap();
    let through_mnt = t.0.join("mnt/home/user/.local/share/kiln");
    refuse_symlink_path(&through_mnt, &through_mnt.join("library")).unwrap();
}

/// A link at or below the managed root is still refused, whatever lies above it.
#[cfg(unix)]
#[test]
fn symlinks_inside_the_root_are_refused_behind_a_linked_ancestor() {
    let t = tmp("inside-root");
    let real = t.0.join("var/home/user/game");
    std::fs::create_dir_all(real.join("mods")).unwrap();
    std::fs::create_dir_all(t.0.join("elsewhere")).unwrap();
    std::os::unix::fs::symlink("var/home", t.0.join("home")).unwrap();
    let game = t.0.join("home/user/game");
    // a link below the root: directory, file, and a dangling one
    std::os::unix::fs::symlink(t.0.join("elsewhere"), real.join("mods/linked")).unwrap();
    std::os::unix::fs::symlink(t.0.join("elsewhere/none"), real.join("mods/dangling")).unwrap();
    for path in [game.join("mods/linked"), game.join("mods/linked/child"), game.join("mods/dangling")] {
        assert_eq!(refuse_symlink_path(&game, &path).unwrap_err().code, ErrorCode::Refused, "{}", path.display());
    }
    refuse_symlink_path(&game, &game.join("mods")).unwrap();
    // the root itself being a link is refused as before (only what lies above it is the system's)
    std::os::unix::fs::symlink(&real, t.0.join("var/home/user/game-link")).unwrap();
    let linked_root = t.0.join("home/user/game-link");
    assert_eq!(refuse_symlink_path(&linked_root, &linked_root.join("mods")).unwrap_err().code, ErrorCode::Refused);
    assert_eq!(refuse_symlink_path(&linked_root, &linked_root).unwrap_err().code, ErrorCode::Refused);
    // a path that is not under the root gets the strict check from its first component: the link above it refuses
    assert_eq!(refuse_symlink_path(&t.0.join("elsewhere"), &game.join("mods")).unwrap_err().code, ErrorCode::Refused);
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
        assert_eq!(refuse_symlink_path(&t.0, path).unwrap_err().code, ErrorCode::Refused, "{}", path.display());
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
    assert_eq!(refuse_symlink_path(&root, &junction).unwrap_err().code, ErrorCode::Refused);
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
                let result = refuse_symlink_path(std::path::Path::new(&format!("{drive}:")), &path);
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
