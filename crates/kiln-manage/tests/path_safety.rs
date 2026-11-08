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
#[test]
fn windows_junctions_are_refused() {
    let t = tmp("junction");
    let target = t.0.join("target");
    let junction = t.0.join("junction");
    std::fs::create_dir(&target).unwrap();
    let output = std::process::Command::new("cmd").args(["/C", "mklink", "/J"])
        .arg(&junction).arg(&target).output().unwrap();
    assert!(output.status.success(), "mklink: {output:?}");
    for path in [&junction, &junction.join("missing/child")] {
        assert_eq!(refuse_symlink_path(path).unwrap_err().code, ErrorCode::Refused, "{}", path.display());
    }
}
