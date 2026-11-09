// SPDX-License-Identifier: GPL-3.0-or-later
//! Offline CPU portrait renderer: kiln-portraits <request.json>. Uses the same production renderer as the DLL.
fn main() {
    let mut args = std::env::args_os().skip(1);
    let Some(path) = args.next() else {
        eprintln!("usage: kiln-portraits <request.json>");
        std::process::exit(2)
    };
    if args.next().is_some() {
        std::process::exit(2)
    }
    let meta = std::fs::symlink_metadata(&path).unwrap_or_else(|e| {
        eprintln!("{e}");
        std::process::exit(2)
    });
    if !meta.is_file() || meta.len() > kiln_eml::portrait_export::MAX_REQUEST as u64 {
        std::process::exit(2)
    }
    let bytes = std::fs::read(path).unwrap_or_else(|e| {
        eprintln!("{e}");
        std::process::exit(2)
    });
    let text = kiln_eml::portrait_export::render_json(&bytes);
    println!("{text}");
    let ok = serde_json::from_str::<serde_json::Value>(&text)
        .ok()
        .and_then(|v| v["code"].as_i64())
        == Some(0);
    if !ok {
        std::process::exit(1)
    }
}
