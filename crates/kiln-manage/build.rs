// SPDX-License-Identifier: MIT
// Embeds the cook tools for kiln_manage::cooktools (feature "cooktools"): the Python sources and data files of
// tools/{animcook,impact,kfc,modelcook,soundcook}, without their tests, labs, samples and native sources, as a table
// of include_bytes! in $OUT_DIR/cooktools_files.rs. A player has no Kiln checkout: `kiln cook` and the launcher unpack
// this copy on first use.
//
// KILN_COOK_HELPERS (optional) names a folder with soundcook's native helper for the target (kiln-soundcook, or
// kiln-soundcook.exe for Windows): it is embedded too. A release build (KILN_RELEASE_BUILD=1) stops when a tool or the
// helper is missing; a development build embeds what is there and says what is not.
use std::fmt::Write as _;
use std::path::{Path, PathBuf};
use std::{env, fs};

const TOOLS: [(&str, &str); 6] = [
    ("animcook", "animcook.py"),
    ("impact", "impact.py"),
    ("kfc", "kfc/archive.py"),
    ("modelcook", "modelcook.py"),
    ("soundcook", "soundcook.py"),
    ("bootsnap", "bootsnap.py"),
];
// never shipped: tests and fixtures, a tool's lab mod and samples, build output, the helper's C sources
const SKIP_DIRS: [&str; 6] = ["tests", "lab", "samples", "native", "__pycache__", "node_modules"];

fn walk(dir: &Path, rel: &str, out: &mut Vec<(String, PathBuf)>) {
    let Ok(rd) = fs::read_dir(dir) else { return };
    for e in rd.flatten() {
        let name = e.file_name().to_string_lossy().into_owned();
        let path = e.path();
        let Ok(ft) = e.file_type() else { continue };
        if name.starts_with('.') || ft.is_symlink() {
            continue;
        }
        if ft.is_dir() {
            if !SKIP_DIRS.contains(&name.as_str()) {
                walk(&path, &format!("{rel}/{name}"), out);
            }
        } else if ft.is_file() && !name.ends_with(".pyc") && !name.ends_with(".sh") {
            out.push((format!("{rel}/{name}"), path));
        }
    }
}

fn main() {
    println!("cargo:rerun-if-changed=build.rs");
    println!("cargo:rerun-if-env-changed=KILN_RELEASE_BUILD");
    println!("cargo:rerun-if-env-changed=KILN_COOK_HELPERS");
    let out = PathBuf::from(env::var_os("OUT_DIR").expect("OUT_DIR")).join("cooktools_files.rs");
    let release = env::var("KILN_RELEASE_BUILD").is_ok_and(|v| v == "1");
    let mut files: Vec<(String, PathBuf)> = Vec::new();
    let mut helper: Option<(String, PathBuf)> = None;
    if env::var_os("CARGO_FEATURE_COOKTOOLS").is_some() {
        let tools = PathBuf::from(env::var_os("CARGO_MANIFEST_DIR").expect("CARGO_MANIFEST_DIR")).join("../../tools");
        for (tool, main) in TOOLS {
            let dir = tools.join(tool);
            println!("cargo:rerun-if-changed={}", dir.display());
            let before = files.len();
            walk(&dir, &format!("tools/{tool}"), &mut files);
            let want = format!("tools/{tool}/{main}");
            if !files[before..].iter().any(|(n, _)| *n == want) {
                let msg = format!("the cook tool {tool} is not at {} ({want} missing)", dir.display());
                if release {
                    panic!("{msg}: a release build must carry every cook tool");
                }
                println!("cargo:warning=kiln-manage: {msg}; this build carries no cook tools (kiln cook then needs KILN_REPO)");
                files.clear();
                break;
            }
        }
        files.sort();
        let windows = env::var("CARGO_CFG_TARGET_OS").is_ok_and(|v| v == "windows");
        let name = if windows { "kiln-soundcook.exe" } else { "kiln-soundcook" };
        match env::var_os("KILN_COOK_HELPERS").filter(|v| !v.is_empty()) {
            Some(dir) => {
                let p = PathBuf::from(dir).join(name);
                println!("cargo:rerun-if-changed={}", p.display());
                if p.is_file() {
                    helper = Some((name.to_string(), p));
                } else if release {
                    panic!("KILN_COOK_HELPERS has no {name} ({}): a release build must carry soundcook's helper", p.display());
                } else {
                    println!("cargo:warning=kiln-manage: no {name} in KILN_COOK_HELPERS; sound cooks use the Python path (48 kHz WAV only)");
                }
            }
            None if release && !files.is_empty() => {
                panic!("KILN_COOK_HELPERS is not set: a release build must carry soundcook's helper ({name})")
            }
            None => {}
        }
    }
    let mut src = String::from("pub(crate) static FILES: &[(&str, &[u8])] = &[\n");
    for (name, path) in &files {
        let path = path.canonicalize().unwrap_or_else(|_| path.clone());
        writeln!(src, "    ({name:?}, include_bytes!({:?})),", path.display().to_string()).unwrap();
    }
    src.push_str("];\n");
    match &helper {
        Some((name, path)) => {
            let path = path.canonicalize().unwrap_or_else(|_| path.clone());
            writeln!(src, "pub(crate) static HELPER: Option<(&str, &[u8])> = Some(({name:?}, include_bytes!({:?})));", path.display().to_string()).unwrap();
        }
        None => src.push_str("pub(crate) static HELPER: Option<(&str, &[u8])> = None;\n"),
    }
    fs::write(&out, src).expect("writing cooktools_files.rs");
}
