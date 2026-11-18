//! game::graphics_step_aside: the tools in a game folder next to which Kiln's graphics tools (kiln.json "graphics")
//! step aside, each kind, names in any case, the game's own nvngx_dlss.dll, and the order. Temp folders and fake DLLs.

use kiln_manage::game::{graphics_step_aside, GraphicsStepAside};
use kiln_manage::pe::build_test_dll;
use std::path::{Path, PathBuf};

struct Tmp(PathBuf);
impl Drop for Tmp {
    fn drop(&mut self) {
        let _ = std::fs::remove_dir_all(&self.0);
    }
}
fn tmp(tag: &str) -> Tmp {
    let p = std::env::temp_dir().join(format!("km-gfx-aside-{tag}-{}", std::process::id()));
    let _ = std::fs::remove_dir_all(&p);
    std::fs::create_dir_all(&p).unwrap();
    Tmp(p)
}
fn put(root: &Path, rel: &str, data: &[u8]) {
    let p = root.join(rel);
    std::fs::create_dir_all(p.parent().unwrap()).unwrap();
    std::fs::write(&p, data).unwrap();
}
fn aside(kind: &str, file: &str, frame_cap: bool) -> GraphicsStepAside {
    GraphicsStepAside { kind: kind.into(), file: file.into(), frame_cap }
}

#[test]
fn an_empty_folder_and_the_games_own_files_give_nothing() {
    let t = tmp("empty");
    assert!(graphics_step_aside(&t.0).is_empty());
    assert!(graphics_step_aside(&t.0.join("missing")).is_empty(), "a folder that is not there");
    // the game ships nvngx_dlss.dll; OptiScaler.ini alone is a leftover; a folder named like a file is not one
    put(&t.0, "nvngx_dlss.dll", b"x");
    put(&t.0, "OptiScaler.ini", b"[Menu]\n");
    std::fs::create_dir_all(t.0.join("nvngx.dll")).unwrap();
    put(&t.0, "Streamline/other.dll", b"x");
    assert!(graphics_step_aside(&t.0).is_empty());
}

#[test]
fn optiscaler_by_its_contents() {
    let t = tmp("optiscaler");
    put(&t.0, "dxgi.dll", &build_test_dll(&["CreateDXGIFactory"], b"ReShade 6.3"));
    assert!(graphics_step_aside(&t.0).is_empty(), "ReShade's dxgi.dll is not OptiScaler");
    put(&t.0, "dxgi.dll", &build_test_dll(&["CreateDXGIFactory"], b"OptiScaler.ini"));
    assert_eq!(graphics_step_aside(&t.0), [aside("optiscaler", "dxgi.dll", true)]);
}

#[test]
fn streamline_in_the_folder_or_its_streamline_folder() {
    let t = tmp("streamline");
    put(&t.0, "streamline/SL.Interposer.DLL", b"x");
    assert_eq!(graphics_step_aside(&t.0), [aside("streamline", "streamline/SL.Interposer.DLL", true)]);
    put(&t.0, "sl.interposer.dll", b"x");
    assert_eq!(graphics_step_aside(&t.0), [aside("streamline", "sl.interposer.dll", true)], "the game folder's first");
}

#[test]
fn vkfg_dlsstweaks_and_nvngx_in_any_case() {
    let t = tmp("cases");
    put(&t.0, "enshroudedvkfg.INI", b"");
    assert_eq!(graphics_step_aside(&t.0), [aside("vkfg", "enshroudedvkfg.INI", true)]);
    std::fs::remove_file(t.0.join("enshroudedvkfg.INI")).unwrap();
    put(&t.0, "DLSSTweaks.ini", b"");
    assert_eq!(graphics_step_aside(&t.0), [aside("dlsstweaks", "DLSSTweaks.ini", false)]);
    std::fs::remove_file(t.0.join("DLSSTweaks.ini")).unwrap();
    put(&t.0, "_NVNGX.dll", b"x");
    put(&t.0, "nvngx_dlss.dll", b"x");
    assert_eq!(graphics_step_aside(&t.0), [aside("nvngx", "_NVNGX.dll", false)]);
    put(&t.0, "NvNgx.Dll", b"x");
    assert_eq!(graphics_step_aside(&t.0), [aside("nvngx", "NvNgx.Dll", false)], "one entry, nvngx.dll first");
}

#[test]
fn every_kind_in_order_and_as_json() {
    let t = tmp("all");
    put(&t.0, "nvngx.dll", b"x");
    put(&t.0, "dlsstweaks.ini", b"");
    put(&t.0, "EnshroudedVKFG.ini", b"");
    put(&t.0, "Streamline/sl.interposer.dll", b"x");
    put(&t.0, "OptiScaler.asi", &build_test_dll(&["x"], b"OptiScaler.log"));
    let all = graphics_step_aside(&t.0);
    assert_eq!(
        all,
        [
            aside("optiscaler", "OptiScaler.asi", true),
            aside("streamline", "Streamline/sl.interposer.dll", true),
            aside("vkfg", "EnshroudedVKFG.ini", true),
            aside("dlsstweaks", "dlsstweaks.ini", false),
            aside("nvngx", "nvngx.dll", false),
        ]
    );
    let json = serde_json::to_value(&all[0]).unwrap();
    assert_eq!(json, serde_json::json!({ "kind": "optiscaler", "file": "OptiScaler.asi", "frameCap": true }));
}

#[cfg(unix)]
#[test]
fn a_linked_file_or_folder_counts_as_the_game_sees_it() {
    // Linux mod managers deploy by symbolic links; Wine (and kiln.ui's own check in the game) follows them
    let t = tmp("links");
    let store = tmp("links-store");
    put(&store.0, "dlsstweaks.ini", b"");
    put(&store.0, "Streamline/sl.interposer.dll", b"x");
    std::os::unix::fs::symlink(store.0.join("dlsstweaks.ini"), t.0.join("dlsstweaks.ini")).unwrap();
    assert_eq!(graphics_step_aside(&t.0), [aside("dlsstweaks", "dlsstweaks.ini", false)]);
    std::os::unix::fs::symlink(store.0.join("Streamline"), t.0.join("Streamline")).unwrap();
    assert_eq!(
        graphics_step_aside(&t.0),
        [aside("streamline", "Streamline/sl.interposer.dll", true), aside("dlsstweaks", "dlsstweaks.ini", false)]
    );
    // a link to nothing is no tool
    std::fs::remove_file(t.0.join("dlsstweaks.ini")).unwrap();
    std::os::unix::fs::symlink(store.0.join("gone.ini"), t.0.join("dlsstweaks.ini")).unwrap();
    assert_eq!(graphics_step_aside(&t.0), [aside("streamline", "Streamline/sl.interposer.dll", true)]);
}
