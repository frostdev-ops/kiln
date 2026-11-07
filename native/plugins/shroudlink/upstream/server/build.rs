//! Lets the DLL double as a `steam_api64.dll` proxy: generates one jump stub per
//! Steam API function export (see src/steam_proxy.rs). The list comes from
//! `steam_api64.exports`; data exports are skipped, the server does not import any.

use std::fmt::Write;

const DATA_EXPORTS: &[&str] = &["g_pSteamClientGameServer"];

fn main() {
    // kiln: the Rust ABI cannot catch Windows hardware exceptions from native DLL initialization.
    if std::env::var_os("CARGO_FEATURE_KILN").is_some() {
        println!("cargo:rerun-if-changed=../../src/guard.c");
        cc::Build::new().file("../../src/guard.c").compile("kiln_sl_guard");
    }
    println!("cargo:rerun-if-changed=steam_api64.exports");
    let exports = std::fs::read_to_string("steam_api64.exports").expect("steam_api64.exports");
    let names: Vec<&str> = exports
        .lines()
        .map(str::trim)
        .filter(|n| !n.is_empty() && !DATA_EXPORTS.contains(n))
        .collect();

    let mut code = String::new();
    writeln!(code, "pub const COUNT: usize = {};", names.len()).unwrap();
    writeln!(code, "pub static NAMES: [&[u8]; COUNT] = [").unwrap();
    for name in &names {
        writeln!(code, "    b\"{name}\\0\",").unwrap();
    }
    writeln!(code, "];").unwrap();
    writeln!(code, "core::arch::global_asm!(").unwrap();
    for (i, name) in names.iter().enumerate() {
        writeln!(code, "    \".globl {name}\", \"{name}:\", \"jmp qword ptr [rip + {{table}} + {}]\",", i * 8).unwrap();
        // kiln: as a Kiln plugin the DLL is no Steam proxy: the stubs stay internal, nothing is exported
        if std::env::var_os("CARGO_FEATURE_KILN").is_none() {
            println!("cargo:rustc-cdylib-link-arg=/EXPORT:{name}");
        }
    }
    writeln!(code, "    table = sym TABLE,").unwrap();
    writeln!(code, ");").unwrap();

    let out = std::path::Path::new(&std::env::var("OUT_DIR").unwrap()).join("steam_stubs.rs");
    std::fs::write(out, code).unwrap();
}
