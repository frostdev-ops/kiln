use std::fs;
use std::env;
use std::path::{Path, PathBuf};

fn main() -> std::io::Result<()> {
    generate_definitions_file()?;

    // kiln-eml: deterministic cache schema identifier; never embed the build clock.
    let root = Path::new("../..");
    let mut inputs = Vec::new();
    collect_build_inputs(&root.join("eml"), &mut inputs)?;
    collect_build_inputs(&root.join("kiln-eml"), &mut inputs)?;
    inputs.extend([root.join("Cargo.toml"), root.join("Cargo.lock")]);
    inputs.sort();
    let mut hash = 0xcbf29ce484222325u64;
    for path in inputs {
        println!("cargo:rerun-if-changed={}", path.display());
        for byte in path.strip_prefix(root).unwrap().to_string_lossy().bytes().chain(fs::read(&path)?) {
            hash = (hash ^ u64::from(byte)).wrapping_mul(0x100000001b3);
        }
    }
    println!("cargo:rustc-env=BUILD_ID=kiln-eml-v2-{hash:016x}");

    Ok(())
}

fn generate_definitions_file() -> std::io::Result<()> {
    let definitions_dir = Path::new("definitions");

    println!("cargo:rerun-if-changed={}", definitions_dir.display());

    let mut lua_files = Vec::new();

    collect_lua_files(definitions_dir, &mut lua_files)?;

    lua_files.sort();

    for file in &lua_files {
        println!("cargo:rerun-if-changed={}", file.display());
    }

    let mut combined = String::new();

    combined.push_str("--- @meta\n");

    for file in lua_files {
        let content = fs::read_to_string(&file)?;

        if let Some(pos) = content.find('\n') {
            if content[..pos].trim().contains("@meta") {
                combined.push_str(&content[pos + 1..]);
            } else {
                combined.push_str(&content);
            }
        } else {
            combined.push_str(&content);
        }
    }

    let out_dir = env::var("OUT_DIR").unwrap();
    let out_path = Path::new(&out_dir).join("definitions.lua");

    let need_write = match fs::read_to_string(&out_path) {
        Ok(existing) => existing != combined,
        Err(_) => true,
    };

    if need_write {
        std::fs::write(&out_path, combined.as_bytes())?;
    }

    Ok(())
}

fn collect_lua_files(
    dir: &Path,
    out: &mut Vec<PathBuf>
) -> std::io::Result<()> {
    for entry in fs::read_dir(dir)? {
        let p = entry?.path();

        if p.is_dir() {
            collect_lua_files(&p, out)?;
        } else if p.extension().and_then(|s| s.to_str()) == Some("lua") {
            out.push(p);
        }
    }
    Ok(())
}

// kiln-eml: a reproducible code fingerprint invalidates old patches after a runner/parser change too.
fn collect_build_inputs(dir: &Path, out: &mut Vec<PathBuf>) -> std::io::Result<()> {
    for entry in fs::read_dir(dir)? {
        let path = entry?.path();
        if path.is_dir() { collect_build_inputs(&path, out)?; }
        else if path.extension().is_some_and(|e| e == "rs" || e == "lua" || e == "toml") { out.push(path); }
    }
    Ok(())
}
