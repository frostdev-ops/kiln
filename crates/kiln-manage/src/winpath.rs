//! Host <-> game path translation (contracts.md, "Path translation").
//! - Proton, Z mode: `/home/u/x` <-> `Z:\home\u\x` (valid only when `<pfx>/dosdevices/z:` resolves to `/`).
//! - Proton, other drives: `C:\...` <-> `<pfx>/drive_c/...`; other letters through `<pfx>/dosdevices/<l>:`.
//! - Windows: paths are used as they are.
//! - Copy mode: relative paths, resolved against `<game>/kiln/`.
//!
//! A host path with a component Windows cannot name (`<>:"|?*`, a trailing dot or space, a control character) does
//! not translate (Error::Unsupported): the caller falls back to copy mode.

use crate::{Error, ErrorCode, Result};
use std::path::{Path, PathBuf};

const INVALID: &[char] = &['<', '>', ':', '"', '|', '?', '*', '\\'];

/// `/a/b` -> `Z:\a\b`. Pure string translation; the caller checks the Z: drive.
pub fn unix_to_z(path: &Path) -> Result<String> {
    let s = path.to_str().ok_or_else(|| Error::new(ErrorCode::Unsupported, format!("{}: not UTF-8", path.display())))?;
    if !s.starts_with('/') {
        return Err(Error::invalid(format!("{s}: not an absolute path")));
    }
    let mut out = String::from("Z:");
    for part in s.split('/').filter(|p| !p.is_empty()) {
        if part.contains(INVALID) || part.ends_with('.') || part.ends_with(' ') || part.chars().any(|c| (c as u32) < 32) {
            return Err(Error::new(ErrorCode::Unsupported, format!("{s}: '{part}' cannot be a Windows name")));
        }
        out.push('\\');
        out.push_str(part);
    }
    if out.len() == 2 {
        out.push('\\');
    }
    Ok(out)
}

/// `Z:\a\b` (or `Z:/a/b`) -> `/a/b`. None for another drive or a relative path, and always None on Windows, where
/// `Z:` is a real drive (often a mapped network share): callers then use the path as it is.
pub fn z_to_unix(path: &str) -> Option<PathBuf> {
    if cfg!(windows) {
        return None;
    }
    let rest = path.strip_prefix("Z:").or_else(|| path.strip_prefix("z:"))?;
    if !rest.starts_with(['\\', '/']) {
        return None;
    }
    let parts: Vec<&str> = rest.split(['\\', '/']).filter(|p| !p.is_empty()).collect();
    Some(PathBuf::from(format!("/{}", parts.join("/"))))
}

/// True when `<prefix>/dosdevices/z:` resolves to `/`.
pub fn z_drive_ok(prefix: &Path) -> bool {
    std::fs::canonicalize(prefix.join("dosdevices").join("z:")).map(|p| p == Path::new("/")).unwrap_or(false)
}

/// `\\?\C:\x` -> `C:\x`, `\\?\UNC\server\share` -> `\\server\share` (Windows' verbatim prefix; game-side paths never
/// carry it).
fn strip_verbatim(s: &str) -> std::borrow::Cow<'_, str> {
    if let Some(rest) = s.strip_prefix(r"\\?\UNC\") {
        return format!(r"\\{rest}").into();
    }
    s.strip_prefix(r"\\?\").unwrap_or(s).into()
}

/// A host path as the game sees it, for an install's path mode.
/// - Z: `Z:\...` (the caller has checked the Z: drive: `game::path_mode`).
/// - Native: the path as it is (Windows; no `\\?\` prefix).
/// - Copy: Error::Unsupported (the caller copies the folder under `<game>/kiln/profiles/` and writes a relative path).
pub fn to_game(host: &Path, install: &crate::game::GameInstall) -> Result<String> {
    match install.path_mode {
        crate::game::PathMode::Z => unix_to_z(host),
        crate::game::PathMode::Native => {
            let s = host.to_str().ok_or_else(|| Error::new(ErrorCode::Unsupported, format!("{}: not UTF-8", host.display())))?;
            if !host.is_absolute() {
                return Err(Error::invalid(format!("{s}: not an absolute path")));
            }
            Ok(strip_verbatim(s).into_owned())
        }
        crate::game::PathMode::Copy => Err(Error::new(
            ErrorCode::Unsupported,
            format!("{}: copy mode has no game-side path for a host folder (copy it under kiln/profiles/)", host.display()),
        )),
    }
}

/// True for `X:\...` / `X:/...` and UNC `\\server\...`.
pub fn is_absolute_game_path(p: &str) -> bool {
    let b = p.as_bytes();
    (b.len() >= 3 && b[0].is_ascii_alphabetic() && b[1] == b':' && (b[2] == b'\\' || b[2] == b'/'))
        || p.starts_with("\\\\")
        || p.starts_with("//")
}

/// Drops `.` and resolves `..` without touching the file system.
fn lexical(p: &Path) -> PathBuf {
    let mut out = PathBuf::new();
    for c in p.components() {
        match c {
            std::path::Component::CurDir => {}
            std::path::Component::ParentDir => {
                out.pop();
            }
            other => out.push(other),
        }
    }
    out
}

/// Where a Wine drive letter points: `<prefix>/dosdevices/<l>:` (a symlink, relative ones resolved against
/// dosdevices/); C: falls back to `<prefix>/drive_c`, Z: to `/`.
pub fn drive_target(prefix: Option<&Path>, letter: char) -> Option<PathBuf> {
    let l = letter.to_ascii_lowercase();
    if let Some(pfx) = prefix {
        let dd = pfx.join("dosdevices");
        if let Ok(t) = std::fs::read_link(dd.join(format!("{l}:"))) {
            return Some(if t.is_absolute() { t } else { lexical(&dd.join(t)) });
        }
        if l == 'c' {
            return Some(pfx.join("drive_c"));
        }
    }
    (l == 'z').then(|| PathBuf::from("/"))
}

/// A game-side path (absolute, or relative to `<game>/kiln/`) as a host path.
pub fn to_host(game_path: &str, install: &crate::game::GameInstall) -> Result<PathBuf> {
    let p = game_path.trim();
    if p.is_empty() {
        return Err(Error::invalid("empty game-side path"));
    }
    if !is_absolute_game_path(p) && !p.starts_with('/') {
        let rel: Vec<&str> = p.split(['\\', '/']).filter(|c| !c.is_empty() && *c != ".").collect();
        if rel.contains(&"..") {
            return Err(Error::invalid(format!("{p}: '..' is not allowed in a relative game path")));
        }
        let mut out = install.path.join(crate::paths::game::KILN);
        for c in rel {
            out.push(c);
        }
        return Ok(out);
    }
    if cfg!(windows) || install.platform == crate::game::Platform::Windows {
        return Ok(PathBuf::from(p));
    }
    if p.starts_with('/') && !p.starts_with("//") {
        return Ok(PathBuf::from(p)); // already a host path
    }
    if p.starts_with("\\\\") || p.starts_with("//") {
        return Err(Error::new(ErrorCode::Unsupported, format!("{p}: UNC paths have no host path under Wine")));
    }
    let letter = p.chars().next().unwrap_or('?');
    let base = drive_target(install.prefix.as_deref(), letter)
        .ok_or_else(|| Error::not_found(format!("{p}: drive {letter}: is not mapped in the Wine prefix")))?;
    let mut out = base;
    for c in p[2..].split(['\\', '/']).filter(|c| !c.is_empty() && *c != ".") {
        out.push(c);
    }
    Ok(out)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn z_round_trip() {
        let z = unix_to_z(Path::new("/home/user/.local/share/kiln/library")).unwrap();
        assert_eq!(z, "Z:\\home\\user\\.local\\share\\kiln\\library");
        assert_eq!(unix_to_z(Path::new("/")).unwrap(), "Z:\\");
        assert!(unix_to_z(Path::new("/a/b:c")).is_err());
        assert!(unix_to_z(Path::new("/a/trailing.")).is_err());
        assert!(z_to_unix("C:\\x").is_none());
        if cfg!(windows) {
            // Z: is a real drive there: no translation
            assert!(z_to_unix(&z).is_none());
        } else {
            assert_eq!(z_to_unix(&z).unwrap(), PathBuf::from("/home/user/.local/share/kiln/library"));
        }
    }
}
