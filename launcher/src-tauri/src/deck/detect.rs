//! Is this a Steam Deck, SteamOS or a gamescope session? The answer picks the launcher's Deck UI at the start
//! (docs/kiln/launcher/deck.md "How Deck mode is chosen"): `app_info.deck`. The frontend decides: `forced` (the
//! command line) wins, then the launcher setting `uiMode`, then `detected`.
//!
//! Signals (any one is enough):
//! - env `SteamDeck=1` (Steam sets it for what it starts on a Deck, Game Mode included), `SteamOS=1`;
//! - env `XDG_CURRENT_DESKTOP` naming gamescope, or `GAMESCOPE_WAYLAND_DISPLAY` set (inside a gamescope session);
//! - `/sys/class/dmi/id/product_name` "Jupiter" (Steam Deck LCD) or "Galileo" (Steam Deck OLED): also in Desktop Mode,
//!   where Steam's variables may be missing;
//! - `/etc/os-release` `ID=steamos` (SteamOS on other handhelds, and Desktop Mode).

use serde::{Deserialize, Serialize};
use std::path::{Path, PathBuf};
use std::sync::OnceLock;

/// `app_info.deck`.
#[derive(Debug, Clone, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DeckInfo {
    /// A Steam Deck, SteamOS or gamescope was seen (any signal).
    pub detected: bool,
    /// What was seen, e.g. `["SteamDeck=1", "XDG_CURRENT_DESKTOP=gamescope", "dmi:Jupiter"]`.
    pub signals: Vec<String>,
    /// Inside a gamescope session (Game Mode).
    pub gamescope: bool,
    /// The Deck model from DMI: `"jupiter"` (LCD) or `"galileo"` (OLED); null elsewhere.
    pub device: Option<String>,
    /// `--deck` / `--desktop` on the command line, or `KILN_LAUNCHER_UI=deck|desktop`: `"deck"` or `"desktop"`. Wins over
    /// the setting and the detection.
    pub forced: Option<String>,
}

/// Where the probes read from (tests point it at fake files and a fake environment).
pub struct Probe<'a> {
    pub env: &'a dyn Fn(&str) -> Option<String>,
    /// The folder holding `product_name` (`/sys/class/dmi/id`).
    pub dmi_dir: &'a Path,
    /// `/etc/os-release`.
    pub os_release: &'a Path,
}

/// The Deck models by DMI product name.
const MODELS: &[&str] = &["jupiter", "galileo"];

/// Runs every probe. `forced` comes from [`forced_from`].
pub fn detect(p: &Probe) -> DeckInfo {
    let mut info = DeckInfo::default();
    let set = |k: &str| (p.env)(k).map(|v| v.trim().to_string()).filter(|v| !v.is_empty());
    for k in ["SteamDeck", "SteamOS"] {
        if set(k).is_some_and(|v| v == "1") {
            info.signals.push(format!("{k}=1"));
        }
    }
    if let Some(d) = set("XDG_CURRENT_DESKTOP") {
        if d.split(':').any(|x| x.eq_ignore_ascii_case("gamescope")) {
            info.signals.push(format!("XDG_CURRENT_DESKTOP={d}"));
            info.gamescope = true;
        }
    }
    if set("GAMESCOPE_WAYLAND_DISPLAY").is_some() {
        info.signals.push("GAMESCOPE_WAYLAND_DISPLAY".into());
        info.gamescope = true;
    }
    if let Ok(name) = std::fs::read_to_string(p.dmi_dir.join("product_name")) {
        let name = name.trim();
        if let Some(m) = MODELS.iter().find(|m| name.eq_ignore_ascii_case(m)) {
            info.signals.push(format!("dmi:{name}"));
            info.device = Some((*m).to_string());
        }
    }
    if let Ok(text) = std::fs::read_to_string(p.os_release) {
        if os_release_id(&text).is_some_and(|id| id == "steamos") {
            info.signals.push("os-release:steamos".into());
        }
    }
    info.detected = !info.signals.is_empty();
    info
}

/// `ID=` of an os-release file, unquoted and lower-cased.
fn os_release_id(text: &str) -> Option<String> {
    text.lines().find_map(|l| l.trim().strip_prefix("ID=")).map(|v| v.trim().trim_matches(|c| c == '"' || c == '\'').to_ascii_lowercase())
}

/// `--deck` / `--desktop` (the last one given wins), else `KILN_LAUNCHER_UI` (`deck` | `desktop`, anything else is
/// ignored).
pub fn forced_from(args: &[String], env: &dyn Fn(&str) -> Option<String>) -> Option<String> {
    let flag = args.iter().skip(1).rev().find_map(|a| match a.as_str() {
        "--deck" => Some("deck"),
        "--desktop" => Some("desktop"),
        _ => None,
    });
    if let Some(f) = flag {
        return Some(f.into());
    }
    env("KILN_LAUNCHER_UI").map(|v| v.trim().to_ascii_lowercase()).filter(|v| v == "deck" || v == "desktop")
}

/// This process's answer (computed once: the environment and the hardware do not change while it runs).
pub fn current() -> DeckInfo {
    static INFO: OnceLock<DeckInfo> = OnceLock::new();
    INFO.get_or_init(|| {
        let env = |k: &str| std::env::var(k).ok();
        let probe = Probe { env: &env, dmi_dir: &PathBuf::from("/sys/class/dmi/id"), os_release: Path::new("/etc/os-release") };
        let mut info = if cfg!(target_os = "linux") { detect(&probe) } else { DeckInfo::default() };
        let args: Vec<String> = std::env::args().collect();
        info.forced = forced_from(&args, &env);
        info
    })
    .clone()
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::collections::HashMap;

    struct Fake {
        _tmp: tempdir::TempDir,
        dmi: PathBuf,
        os: PathBuf,
    }

    mod tempdir {
        /// A temp folder removed on drop (the crate has no tempfile dependency).
        pub struct TempDir(pub std::path::PathBuf);
        impl TempDir {
            pub fn new(tag: &str) -> TempDir {
                let p = std::env::temp_dir().join(format!("kiln-deck-{tag}-{}-{:?}", std::process::id(), std::thread::current().id()));
                let _ = std::fs::remove_dir_all(&p);
                std::fs::create_dir_all(&p).unwrap();
                TempDir(p)
            }
        }
        impl Drop for TempDir {
            fn drop(&mut self) {
                let _ = std::fs::remove_dir_all(&self.0);
            }
        }
    }

    fn fake(tag: &str, product: Option<&str>, os_release: Option<&str>) -> Fake {
        let t = tempdir::TempDir::new(tag);
        let dmi = t.0.join("dmi");
        std::fs::create_dir_all(&dmi).unwrap();
        if let Some(p) = product {
            std::fs::write(dmi.join("product_name"), format!("{p}\n")).unwrap();
        }
        let os = t.0.join("os-release");
        if let Some(o) = os_release {
            std::fs::write(&os, o).unwrap();
        }
        Fake { _tmp: t, dmi, os }
    }

    fn run(f: &Fake, env: &[(&str, &str)]) -> DeckInfo {
        let map: HashMap<String, String> = env.iter().map(|(k, v)| (k.to_string(), v.to_string())).collect();
        let get = move |k: &str| map.get(k).cloned();
        detect(&Probe { env: &get, dmi_dir: &f.dmi, os_release: &f.os })
    }

    #[test]
    fn plain_desktop_is_not_a_deck() {
        let f = fake("plain", Some("MS-7D25"), Some("NAME=\"Arch Linux\"\nID=arch\n"));
        let i = run(&f, &[("XDG_CURRENT_DESKTOP", "Hyprland"), ("SteamDeck", "0")]);
        assert!(!i.detected);
        assert!(i.signals.is_empty());
        assert!(!i.gamescope);
        assert_eq!(i.device, None);
    }

    #[test]
    fn game_mode_env_alone_is_enough() {
        // a Deck's Game Mode as Steam starts a non-Steam game, on hardware this test does not have
        let f = fake("gamemode", None, None);
        let i = run(&f, &[("SteamDeck", "1"), ("SteamOS", "1"), ("XDG_CURRENT_DESKTOP", "gamescope"), ("GAMESCOPE_WAYLAND_DISPLAY", "gamescope-0")]);
        assert!(i.detected);
        assert!(i.gamescope);
        assert_eq!(i.signals, vec!["SteamDeck=1", "SteamOS=1", "XDG_CURRENT_DESKTOP=gamescope", "GAMESCOPE_WAYLAND_DISPLAY"]);
        // each one alone
        for (k, v) in [("SteamDeck", "1"), ("SteamOS", "1"), ("XDG_CURRENT_DESKTOP", "gamescope"), ("GAMESCOPE_WAYLAND_DISPLAY", "gamescope-1")] {
            assert!(run(&f, &[(k, v)]).detected, "{k}={v}");
        }
        assert!(!run(&f, &[("GAMESCOPE_WAYLAND_DISPLAY", " ")]).detected);
        assert!(run(&f, &[("XDG_CURRENT_DESKTOP", "KDE:gamescope")]).gamescope);
    }

    #[test]
    fn desktop_mode_on_a_deck_by_dmi() {
        for (product, model) in [("Jupiter", "jupiter"), ("Galileo", "galileo"), ("galileo", "galileo")] {
            let f = fake(model, Some(product), None);
            let i = run(&f, &[("XDG_CURRENT_DESKTOP", "KDE")]);
            assert!(i.detected, "{product}");
            assert!(!i.gamescope);
            assert_eq!(i.device.as_deref(), Some(model));
            assert_eq!(i.signals, vec![format!("dmi:{product}")]);
        }
        let f = fake("legion", Some("83E1"), None);
        assert!(!run(&f, &[]).detected);
    }

    #[test]
    fn steamos_by_os_release() {
        let f = fake("steamos", Some("83E1"), Some("NAME=\"SteamOS\"\nID=steamos\nID_LIKE=arch\n"));
        let i = run(&f, &[]);
        assert!(i.detected);
        assert_eq!(i.signals, vec!["os-release:steamos"]);
        let f = fake("quoted", None, Some("ID=\"SteamOS\"\n"));
        assert!(run(&f, &[]).detected);
        let f = fake("like", None, Some("ID=arch\nID_LIKE=steamos\n"));
        assert!(!run(&f, &[]).detected);
    }

    #[test]
    fn flags_and_env_force_a_ui() {
        let none = |_: &str| None;
        let args = |v: &[&str]| v.iter().map(|s| s.to_string()).collect::<Vec<_>>();
        assert_eq!(forced_from(&args(&["kiln-launcher"]), &none), None);
        assert_eq!(forced_from(&args(&["kiln-launcher", "--deck"]), &none).as_deref(), Some("deck"));
        assert_eq!(forced_from(&args(&["kiln-launcher", "--desktop"]), &none).as_deref(), Some("desktop"));
        assert_eq!(forced_from(&args(&["kiln-launcher", "--deck", "--desktop"]), &none).as_deref(), Some("desktop"));
        // the program name itself never counts
        assert_eq!(forced_from(&args(&["--deck"]), &none), None);
        let env = |k: &str| (k == "KILN_LAUNCHER_UI").then(|| " Deck ".to_string());
        assert_eq!(forced_from(&args(&["kiln-launcher"]), &env).as_deref(), Some("deck"));
        assert_eq!(forced_from(&args(&["kiln-launcher", "--desktop"]), &env).as_deref(), Some("desktop"));
        let bad = |k: &str| (k == "KILN_LAUNCHER_UI").then(|| "big".to_string());
        assert_eq!(forced_from(&args(&["kiln-launcher"]), &bad), None);
    }

    #[test]
    fn serializes_camel_case() {
        let i = DeckInfo { detected: true, signals: vec!["SteamDeck=1".into()], gamescope: true, device: Some("jupiter".into()), forced: None };
        let v = serde_json::to_value(&i).unwrap();
        assert_eq!(v, serde_json::json!({"detected": true, "signals": ["SteamDeck=1"], "gamescope": true, "device": "jupiter", "forced": null}));
    }
}
