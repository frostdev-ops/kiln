//! The `kiln-asset` protocol: serves local game assets. They are game files: never bundled, never
//! committed. URL `kiln-asset://localhost/<prefix>/<rel>` (Windows: `http://kiln-asset.localhost/<prefix>/<rel>`).
//! - `editor/`: the save editor's fonts, icons, UI textures. Roots: `$KILN_EDITOR_ASSETS`, `<data>/editor-assets/`,
//!   debug builds `launcher/assets-local/editor/` (tools/launcher/fetch-editor-assets.sh).
//! - `admin/`: the admin map and catalogs (`map/…`, `data/…`). Roots: `$KILN_ADMIN_ASSETS`,
//!   `<data>/admin-assets/`, debug builds `launcher/assets-local/`.
//! `app_assets_import` fills the `<data>` folders: the editor's art extracted from the user's own client
//! (kiln_manage::gameart; docs/kiln/launcher/gameart.md) or copied from local checkouts; never downloaded. A missing
//! file answers 404. `KILN_ASSETS_NO_DEV=1` drops the debug-build roots (tests of the missing-art path).
//! Note: WebKitGTK does not load custom-scheme images into a page served from http://localhost (`tauri dev`), so dev
//! builds should read assets from the vite dev server (`/assets-local/…`); fetch() works either way.

use std::borrow::Cow;
use std::path::{Component, Path, PathBuf};
use tauri::http::Response;

/// Folders that may hold `<prefix>` assets, in lookup order.
pub fn roots(prefix: &str) -> Vec<PathBuf> {
    let (env, data, dev) = match prefix {
        "editor" => ("KILN_EDITOR_ASSETS", "editor-assets", "../assets-local/editor"),
        "admin" => ("KILN_ADMIN_ASSETS", "admin-assets", "../assets-local"),
        _ => return Vec::new(),
    };
    let mut v = Vec::new();
    if let Some(d) = std::env::var_os(env).filter(|d| !d.is_empty()) {
        v.push(PathBuf::from(d));
    }
    if let Ok(d) = crate::state::dirs() {
        v.push(d.root.join(data));
    }
    if cfg!(debug_assertions) && std::env::var_os("KILN_ASSETS_NO_DEV").is_none_or(|x| x.is_empty()) {
        v.push(Path::new(env!("CARGO_MANIFEST_DIR")).join(dev));
    }
    v
}

fn mime(path: &str) -> &'static str {
    match path.rsplit('.').next().unwrap_or("").to_ascii_lowercase().as_str() {
        "webp" => "image/webp",
        "png" => "image/png",
        "otf" => "font/otf",
        "ttf" => "font/ttf",
        "woff2" => "font/woff2",
        "json" => "application/json",
        "bin" => "application/octet-stream",
        _ => "application/octet-stream",
    }
}

fn decode(s: &str) -> String {
    let b = s.as_bytes();
    let mut out = Vec::with_capacity(b.len());
    let mut i = 0;
    while i < b.len() {
        if b[i] == b'%' && i + 2 < b.len() {
            if let Ok(v) = u8::from_str_radix(&s[i + 1..i + 3], 16) {
                out.push(v);
                i += 3;
                continue;
            }
        }
        out.push(b[i]);
        i += 1;
    }
    String::from_utf8_lossy(&out).into_owned()
}

/// The file for a URL path (`/editor/ui/x.webp`, `/admin/map/tiles/3/1_2.webp`), or None (unknown prefix, `..`,
/// missing).
pub fn resolve(url_path: &str) -> Option<PathBuf> {
    let rel = decode(url_path.trim_start_matches('/'));
    let (prefix, rel) = rel.split_once('/')?;
    let rel = Path::new(rel);
    if rel.as_os_str().is_empty() || rel.components().any(|c| !matches!(c, Component::Normal(_))) {
        return None;
    }
    roots(prefix).into_iter().map(|r| r.join(rel)).find(|p| p.is_file())
}

pub fn serve(url_path: &str) -> Response<Cow<'static, [u8]>> {
    let not_found = || Response::builder().status(404).body(Cow::Borrowed(&[][..])).unwrap();
    match resolve(url_path).and_then(|p| std::fs::read(&p).ok().map(|b| (p, b))) {
        Some((p, bytes)) => Response::builder()
            .header("Content-Type", mime(&p.to_string_lossy()))
            .header("Access-Control-Allow-Origin", "*")
            .header("Cache-Control", "max-age=3600")
            .body(Cow::Owned(bytes))
            .unwrap_or_else(|_| not_found()),
        None => not_found(),
    }
}

#[cfg(test)]
mod tests {
    #[test]
    fn refuses_traversal_and_other_prefixes() {
        assert!(super::resolve("/editor/../Cargo.toml").is_none());
        assert!(super::resolve("/editor/%2e%2e/Cargo.toml").is_none());
        assert!(super::resolve("/other/x.webp").is_none());
        assert!(super::resolve("/admin/").is_none());
        assert!(super::resolve("/admin/../../x").is_none());
        assert_eq!(super::decode("a%20b"), "a b");
    }
}
