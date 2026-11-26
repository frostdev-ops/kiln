// SPDX-License-Identifier: GPL-3.0-or-later
//! The C consumers of the provider tables (tests/c/<name>.c), each built against the MIT headers in native/core/abi
//! (their layout asserts included) as C11 and as C++20, into two shared libraries in this test's temp folder: on Unix
//! with `$CC` and `$CXX` (default cc and c++), on Windows with MSVC's cl.exe, found as cargo's `cc` crate finds it (the
//! C++ build tools the Rust MSVC toolchain links with). The tests take prebuilt libraries from their own variable
//! instead (M6_ARCHIVE_READ_CONSUMERS, M6_VALUES_CONSUMERS).
#![allow(dead_code)]
use std::path::{Path, PathBuf};

/// The C11 and the C++20 build of tests/c/`name`.c: their paths, in that order. A compiler that is missing, or that
/// does not build the file warning-free, fails the test (with `var`, the variable that names prebuilt ones).
pub fn build(name: &str, var: &str) -> Vec<PathBuf> {
    let here = Path::new(env!("CARGO_MANIFEST_DIR"));
    let abi = here.join("../../../native/core/abi");
    let source = here.join("tests/c").join(format!("{name}.c"));
    let out = Path::new(env!("CARGO_TARGET_TMPDIR")).join(format!("{}-{}", name.replace('_', "-"), std::process::id()));
    std::fs::create_dir_all(&out).unwrap();
    ["c11", "cpp20"].iter().map(|standard| compile(name, standard, &abi, &source, &out, var)).collect()
}

#[cfg(unix)]
fn compile(name: &str, standard: &str, abi: &Path, source: &Path, out: &Path, _var: &str) -> PathBuf {
    let (compiler, lang, flag) = match standard {
        "c11" => (std::env::var("CC").unwrap_or_else(|_| "cc".into()), "c", "-std=c11"),
        _ => (std::env::var("CXX").unwrap_or_else(|_| "c++".into()), "c++", "-std=c++20"),
    };
    let library = out.join(format!("lib{name}_{standard}.so"));
    let status = std::process::Command::new(&compiler)
        .args(["-x", lang, flag, "-Wall", "-Wextra", "-Werror", "-fPIC", "-shared", "-I"])
        .arg(abi)
        .arg(source)
        .arg("-o")
        .arg(&library)
        .arg("-lpthread")
        .status()
        .unwrap_or_else(|e| panic!("{compiler}: {e}"));
    assert!(status.success(), "{compiler} {flag} built the consumer");
    library
}

#[cfg(windows)]
fn compile(name: &str, standard: &str, abi: &Path, source: &Path, out: &Path, var: &str) -> PathBuf {
    let flags: &[&str] = match standard {
        "c11" => &["/TC", "/std:c11"],
        _ => &["/TP", "/std:c++20", "/EHsc"],
    };
    let library = out.join(format!("{name}_{standard}.dll"));
    let mut cl = cc::windows_registry::find(std::env::consts::ARCH, "cl.exe").unwrap_or_else(|| {
        panic!(
            "no MSVC cl.exe for {} (Visual Studio's C++ build tools) to build tests/c/{name}.c; or set {var} to the \
             consumer DLLs (C11 and C++20)",
            std::env::consts::ARCH
        )
    });
    // /W4 /WX as -Wall -Wextra -Werror; the CRT's "use the _s functions" deprecations are MSVC's alone
    let output = cl
        .current_dir(out)
        .args(["/nologo", "/W4", "/WX", "/D_CRT_SECURE_NO_WARNINGS", "/LD"])
        .args(flags)
        .arg("/I")
        .arg(abi)
        .arg(source)
        .arg(format!("/Fo{}", out.join(format!("{name}_{standard}.obj")).display()))
        .arg(format!("/Fe{}", library.display()))
        .output()
        .unwrap_or_else(|e| panic!("cl.exe: {e}"));
    assert!(
        output.status.success(),
        "cl.exe {flags:?} built the consumer:\n{}{}",
        String::from_utf8_lossy(&output.stdout),
        String::from_utf8_lossy(&output.stderr)
    );
    library
}
