//! The Windows updater's leftovers. Tauri's updater writes the verified NSIS installer to
//! `%TEMP%\<productName>-<version>-updater-<random>\`, runs it and exits, so nothing removes that folder. The next
//! start removes such folders: only directories (no links) whose name has exactly that shape, best effort (a folder
//! still in use stays for the start after).

use std::path::Path;

/// `<app_name>-<version>-updater-<random>`: the version starts with a digit and holds only version characters, the
/// random part is 1 to 32 letters and digits (the updater uses 6).
pub fn is_updater_leftover(name: &str, app_name: &str) -> bool {
    let Some(rest) = name.strip_prefix(app_name).and_then(|r| r.strip_prefix('-')) else { return false };
    let Some((version, random)) = rest.rsplit_once("-updater-") else { return false };
    version.starts_with(|c: char| c.is_ascii_digit())
        && version.chars().all(|c| c.is_ascii_alphanumeric() || matches!(c, '.' | '-' | '+'))
        && (1..=32).contains(&random.len())
        && random.chars().all(|c| c.is_ascii_alphanumeric())
}

/// Removes the updater's leftover folders in `temp`. Returns the names removed; a folder that cannot go (an installer
/// that still runs) is skipped.
pub fn clean(temp: &Path, app_name: &str) -> Vec<String> {
    let mut removed = Vec::new();
    for e in std::fs::read_dir(temp).into_iter().flatten().flatten() {
        let name = e.file_name().to_string_lossy().into_owned();
        if !is_updater_leftover(&name, app_name) {
            continue;
        }
        // file_type does not follow links: a link (or junction) with that name is left alone
        if !e.file_type().is_ok_and(|t| t.is_dir()) {
            continue;
        }
        if std::fs::remove_dir_all(e.path()).is_ok() {
            removed.push(name);
        }
    }
    removed.sort();
    removed
}

#[cfg(test)]
mod tests {
    use super::*;

    const APP: &str = "Kiln Launcher";

    #[test]
    fn only_the_updaters_folder_names_match() {
        for ok in ["Kiln Launcher-0.2.1-updater-aB3xYz", "Kiln Launcher-0.2.0-dev.3-updater-Q1w2E3", "Kiln Launcher-1.0.0+build.7-updater-x"] {
            assert!(is_updater_leftover(ok, APP), "{ok}");
        }
        for no in [
            "Kiln Launcher-0.2.1-updater-",          // no random part
            "Kiln Launcher--updater-aB3xYz",         // no version
            "Kiln Launcher-beta-updater-aB3xYz",     // a version starts with a digit
            "Kiln Launcher-0.2.1-updater-aB3/..",    // not letters and digits
            "Kiln Launcher-0.2.1-updater-aB3 xYz",
            "Kiln Launcher-0.2.1 copy-updater-aB3xYz",
            "Kiln Launcher-0.2.1",
            "Kiln Launcher",
            "Other App-0.2.1-updater-aB3xYz",
            "kiln launcher-0.2.1-updater-aB3xYz",    // the updater writes the product name as it is
            "XKiln Launcher-0.2.1-updater-aB3xYz",
            "Kiln Launcher-0.2.1-updater-aB3xYz0123456789012345678901234567",  // too long for a random part
        ] {
            assert!(!is_updater_leftover(no, APP), "{no}");
        }
    }

    #[test]
    fn removes_the_leftover_folders_and_nothing_else() {
        let temp = std::env::temp_dir().join(format!("kl-updater-cleanup-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&temp);
        let mk = |rel: &str| std::fs::create_dir_all(temp.join(rel)).unwrap();
        mk("Kiln Launcher-0.2.1-updater-aB3xYz");
        std::fs::write(temp.join("Kiln Launcher-0.2.1-updater-aB3xYz/Kiln Launcher-0.2.1-installer.exe"), b"MZ").unwrap();
        mk("Kiln Launcher-0.2.0-updater-Zz9yY8/nested");
        mk("Kiln Launcher-data");
        mk("Other App-0.2.1-updater-aB3xYz");
        // a file with the folder name stays (only folders are the updater's)
        std::fs::write(temp.join("Kiln Launcher-0.3.0-updater-file01"), b"x").unwrap();
        #[cfg(unix)]
        {
            // a link with the name stays, and so does what it points at
            mk("target");
            std::fs::write(temp.join("target/keep.txt"), b"keep").unwrap();
            std::os::unix::fs::symlink(temp.join("target"), temp.join("Kiln Launcher-0.4.0-updater-link01")).unwrap();
        }
        let removed = clean(&temp, APP);
        assert_eq!(removed, ["Kiln Launcher-0.2.0-updater-Zz9yY8", "Kiln Launcher-0.2.1-updater-aB3xYz"]);
        assert!(!temp.join("Kiln Launcher-0.2.1-updater-aB3xYz").exists());
        for stays in ["Kiln Launcher-data", "Other App-0.2.1-updater-aB3xYz", "Kiln Launcher-0.3.0-updater-file01"] {
            assert!(temp.join(stays).exists(), "{stays}");
        }
        #[cfg(unix)]
        {
            assert!(temp.join("Kiln Launcher-0.4.0-updater-link01").symlink_metadata().is_ok());
            assert!(temp.join("target/keep.txt").exists());
        }
        assert!(clean(&temp, APP).is_empty(), "nothing left the second time");
        assert!(clean(&temp.join("missing"), APP).is_empty());
        let _ = std::fs::remove_dir_all(&temp);
    }
}
