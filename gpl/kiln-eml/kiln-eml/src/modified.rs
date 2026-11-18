// SPDX-License-Identifier: GPL-3.0-or-later
//! Modified EML loaders: rebuilds of EML's dbghelp.dll that carry code of their own besides EML. "Put Away Your Weapon"
//! (Nexus 371, the EML mod `holster-toggle`) ships one: EML's `mod-loader-runtime` crate grew a `native` layer (a
//! weapon holster hotkey that rewrites weapon poses in the game's memory from the map the mod's Lua part exports, a
//! PvP patch of the game's isEnemy/isFriend checks, a stance dump key), configured by `m182_native.json` next to the
//! game. That code runs only while such a file is the process's EML loader; when kiln-eml runs the EML mods, only
//! their Lua part runs. kiln-manage's `engines::eml_loader_extras` applies the same rule in the launcher.

use std::path::Path;

/// EML 0.2.0's `mod-loader-runtime` is a stub (`loader_attach` only logs): its panic and log locations name these
/// files. A build naming another file of that crate carries code of its own there.
const STOCK_RUNTIME_FILES: [&str; 2] = ["lib.rs", "log.rs"];

/// Texts of known rebuilds' own code, and what it does for a player.
const KNOWN: [(&str, &str); 3] = [
    ("m182-holster", "a weapon holster hotkey"),
    ("isEnemy(values)", "a PvP patch of the game's faction checks"),
    ("m182-stancedump", "a stance dump key"),
];

/// Settings files of known rebuilds' own code, next to the game, and what that code does.
pub const SETTINGS: [(&str, &str); 1] = [("m182_native.json", "a weapon holster hotkey and a PvP patch of the game's faction checks")];

fn find(hay: &[u8], needle: &[u8]) -> Option<usize> {
    if needle.is_empty() || hay.len() < needle.len() { return None; }
    hay.windows(needle.len()).position(|w| w == needle)
}

/// A file of `mod-loader-runtime` other than EML 0.2.0's own, named in the bytes.
fn own_runtime_file(bytes: &[u8]) -> Option<String> {
    for prefix in [b"mod-loader-runtime\\src\\".as_slice(), b"mod-loader-runtime/src/".as_slice()] {
        let mut from = 0;
        while let Some(at) = find(&bytes[from..], prefix) {
            let start = from + at + prefix.len();
            let rest = &bytes[start..bytes.len().min(start + 96)];
            let n = rest.iter().take_while(|c| c.is_ascii_alphanumeric() || matches!(c, b'_' | b'-' | b'.' | b'\\' | b'/')).count();
            let name = String::from_utf8_lossy(&rest[..n]);
            if let Some(end) = name.find(".rs") {
                let file = name[..end + 3].replace('/', "\\");
                if !STOCK_RUNTIME_FILES.contains(&file.as_str()) { return Some(file); }
            }
            from = start;
        }
    }
    None
}

/// What the own code of an EML loader file does ("a weapon holster hotkey and a PvP patch of the game's faction
/// checks; settings in m182_native.json", or "code kiln-eml does not recognise"), or None for a stock EML build as far
/// as kiln-eml can tell. Only meaningful for a file with EML's marker.
pub fn what(bytes: &[u8]) -> Option<String> {
    let features: Vec<&str> = KNOWN.iter().filter(|(t, _)| find(bytes, t.as_bytes()).is_some()).map(|(_, f)| *f).collect();
    if features.is_empty() && own_runtime_file(bytes).is_none() { return None; }
    let mut s = match features.as_slice() {
        [] => "code kiln-eml does not recognise".to_string(),
        [one] => one.to_string(),
        [first @ .., last] => format!("{} and {last}", first.join(", ")),
    };
    if let Some((file, _)) = SETTINGS.iter().find(|(f, _)| find(bytes, f.as_bytes()).is_some()) {
        s += &format!("; settings in {file}");
    }
    Some(s)
}

/// The kiln.log line for a known rebuild's settings file next to the game while no modified EML loader runs in this
/// process: its code (the holster hotkey) does nothing this start, whatever the player expects of it.
pub fn settings_without_loader(game: &Path, modified_loaded: bool) -> Option<String> {
    if modified_loaded { return None; }
    let (file, does) = SETTINGS.iter().find(|(f, _)| game.join(f).is_file())?;
    Some(format!("kiln-eml: {file} is in the game folder: it is the settings file of a modified EML loader's own code ({does}; \
        Holster Toggle's dbghelp.dll, for one). No such loader runs in this process, so that code does nothing this start: \
        kiln-eml runs only the EML mods' Lua part. That code runs only while its dbghelp.dll is the game's EML loader in \
        place of Kiln's EML support (Kiln wiki: EML, modified loaders)"))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn stock_eml_is_not_modified() {
        let stock = b"EML_LOG_FILE_PATH FieldSet corrupted (this is a bug)crates\\mod-loader-runtime\\src\\lib.rs Attaching runtime loader";
        assert_eq!(what(stock), None);
        assert_eq!(what(b"crates/mod-loader-runtime/src/log.rs"), None);
    }

    #[test]
    fn a_rebuild_with_a_native_layer_is_named_by_what_it_does() {
        let holster = b"EML_LOG_FILE_PATH crates\\mod-loader-runtime\\src\\lib.rs event crates\\mod-loader-runtime\\src\\native\\holster.rs:376 \
            m182-holster\0isEnemy(values)isEnemy(pointers)\0Errorm182_native.json";
        assert_eq!(what(holster).as_deref(),
            Some("a weapon holster hotkey and a PvP patch of the game's faction checks; settings in m182_native.json"));
        // code of its own that kiln-eml does not know is still not stock EML
        assert_eq!(what(b"EML_LOG_FILE_PATH crates\\mod-loader-runtime\\src\\hotkeys.rs").as_deref(), Some("code kiln-eml does not recognise"));
    }

    #[test]
    fn the_settings_file_without_its_loader_is_one_line() {
        let dir = std::env::temp_dir().join(format!("kiln-eml-modified-{}", std::process::id()));
        std::fs::create_dir_all(&dir).unwrap();
        assert_eq!(settings_without_loader(&dir, false), None);
        std::fs::write(dir.join("m182_native.json"), "{}").unwrap();
        let line = settings_without_loader(&dir, false).unwrap();
        assert!(line.contains("m182_native.json is in the game folder") && line.contains("does nothing this start"), "{line}");
        assert_eq!(settings_without_loader(&dir, true), None, "the loader runs: its code reads the file");
        let _ = std::fs::remove_dir_all(&dir);
    }
}
