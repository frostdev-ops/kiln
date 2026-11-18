// SPDX-License-Identifier: MIT
//! Build-script helper (`#[path]`-included by runner/kilnctl/build.rs and crates/kiln-cli/build.rs): a Windows exe gets
//! Kiln's icon (branding/logo/kiln.ico: 16, 24, 32, 48, 64, 256 px) and its name and description as resources, so
//! Explorer, the taskbar and Task Manager show them. Other targets get nothing. The .rc goes through embed-resource:
//! llvm-rc when cross-building for MSVC from Linux (cargo xwin, tools/release/release.sh), rc.exe on Windows. A
//! Windows build stops when the resource cannot be made: a release never ships an exe without its icon.

use std::path::{Path, PathBuf};

/// `name`: the exe's name without `.exe`; `description`: one line (Task Manager's "Description").
pub fn embed(name: &str, description: &str) {
    let ico = Path::new(&std::env::var("CARGO_MANIFEST_DIR").expect("CARGO_MANIFEST_DIR")).join("../../branding/logo/kiln.ico");
    println!("cargo:rerun-if-changed={}", ico.display());
    println!("cargo:rerun-if-changed=../../tools/winres/winres.rs");
    if std::env::var("CARGO_CFG_TARGET_OS").as_deref() != Ok("windows") {
        return;
    }
    let ico = ico.canonicalize().unwrap_or_else(|e| panic!("{}: {e}", ico.display()));
    let out = PathBuf::from(std::env::var("OUT_DIR").expect("OUT_DIR"));
    let rc = out.join(format!("{name}.rc"));
    std::fs::write(&rc, rc_text(name, description, &ico)).unwrap_or_else(|e| panic!("{}: {e}", rc.display()));
    embed_resource::compile(&rc, embed_resource::NONE).manifest_required().unwrap_or_else(|e| panic!("{name}.exe's icon resource: {e}"));
}

/// An .rc string literal: `"` doubled, `\` doubled.
fn rc_string(s: &str) -> String {
    format!("\"{}\"", s.replace('\\', "\\\\").replace('"', "\"\""))
}

/// No #include: numeric constants only, so no Windows SDK headers are needed (VS_FF / VOS_NT_WINDOWS32 = 0x40004,
/// VFT_APP = 1; translation US English, Unicode).
fn rc_text(name: &str, description: &str, ico: &Path) -> String {
    let s = rc_string;
    format!(
        "1 ICON {ico}\n\
         1 VERSIONINFO\nFILEOS 0x40004\nFILETYPE 0x1\nBEGIN\n  BLOCK \"StringFileInfo\"\n  BEGIN\n    BLOCK \"040904B0\"\n    BEGIN\n\
         \x20     VALUE \"CompanyName\", \"frostdev\"\n\
         \x20     VALUE \"FileDescription\", {desc}\n\
         \x20     VALUE \"InternalName\", {name}\n\
         \x20     VALUE \"OriginalFilename\", {file}\n\
         \x20     VALUE \"ProductName\", \"Kiln\"\n\
         \x20     VALUE \"LegalCopyright\", \"Kiln contributors, MIT\"\n\
         \x20   END\n  END\n  BLOCK \"VarFileInfo\"\n  BEGIN\n    VALUE \"Translation\", 0x409, 1200\n  END\nEND\n",
        ico = s(&ico.to_string_lossy()),
        desc = s(description),
        name = s(name),
        file = s(&format!("{name}.exe")),
    )
}
