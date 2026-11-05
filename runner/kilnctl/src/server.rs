// SPDX-License-Identifier: MIT
//! The game side: enshrouded_server.json, the game log, the savegame folder. Shims over kiln_manage::server::game
//!.
use crate::util::Km;
use anyhow::Result;
use std::path::Path;

#[allow(unused_imports)]
pub use kiln_manage::server::game::{
    CONFIG, EXE, LOG, LogWatch, READY, ServerConfig, host_path, last_state, newest_file, read_log,
};

#[allow(dead_code)] // the tests use it
pub fn parse_config(text: &str) -> Result<ServerConfig> {
    kiln_manage::server::game::parse_config(text).km()
}

pub fn read_config(path: &Path) -> Result<ServerConfig> {
    kiln_manage::server::game::read_config(path).km()
}

/// A complete enshrouded_server.json like the game writes for a fresh server (random group passwords).
pub fn default_config(name: &str, port: u16) -> Result<String> {
    kiln_manage::server::game::default_config(name, port).km()
}

#[cfg(test)]
mod tests {
    use super::*;
    #[cfg(unix)]
    use std::path::PathBuf;

    #[test]
    fn config_parsing() {
        let c = parse_config(r#"{"name":"x","queryPort":25760,"saveDirectory":"./save","slotCount":4}"#).unwrap();
        assert_eq!(c.query_port, 25760);
        assert_eq!(c.save_dir, "./save");
        assert_eq!(c.log_dir, "./logs");
        assert_eq!(c.slot_count, Some(4));
        assert!(parse_config("{").is_err());
        assert!(parse_config("[]").is_err());
        assert!(parse_config(r#"{"name":"x"}"#).is_err());
        assert!(parse_config(r#"{"queryPort":"25760"}"#).is_err());
        assert!(parse_config(r#"{"queryPort":0}"#).is_err());
        assert!(parse_config(r#"{"queryPort":70000}"#).is_err());
        assert!(parse_config(r#"{"queryPort":1,"saveDirectory":3}"#).is_err());
    }

    #[test]
    fn default_config_is_valid() {
        let text = default_config("Kiln \"t\"", 25760).unwrap();
        let c = parse_config(&text).unwrap();
        assert_eq!(c.query_port, 25760);
        assert_eq!(c.name, "Kiln \"t\"");
        assert_eq!(c.save_dir, "./savegame");
        let v: serde_json::Value = serde_json::from_str(&text).unwrap();
        let groups = v["userGroups"].as_array().unwrap();
        assert_eq!(groups.len(), 4);
        assert_eq!(groups[0]["name"], "Admin");
        assert_eq!(groups[0]["canKickBan"], true);
        assert_eq!(groups[3]["canEditWorld"], false);
        let pw: Vec<&str> = groups.iter().map(|g| g["password"].as_str().unwrap()).collect();
        assert!(pw.iter().all(|p| p.len() == 16 && p.bytes().all(|b| b.is_ascii_alphanumeric())));
        assert_ne!(pw[0], pw[1]);
    }

    #[cfg(unix)] // Wine drive mapping (Z:, C: under the prefix)
    #[test]
    fn paths_from_config() {
        let (d, pf) = (Path::new("/srv/x"), Path::new("/srv/x.pfx"));
        assert_eq!(host_path(d, pf, "./savegame"), PathBuf::from("/srv/x/savegame"));
        assert_eq!(host_path(d, pf, ".\\logs\\"), PathBuf::from("/srv/x/logs"));
        assert_eq!(host_path(d, pf, "Z:\\data\\save"), PathBuf::from("/data/save"));
        assert_eq!(host_path(d, pf, "c:\\saves"), PathBuf::from("/srv/x.pfx/drive_c/saves"));
        assert_eq!(host_path(d, pf, "/abs"), PathBuf::from("/abs"));
    }

    #[test]
    fn states() {
        let log = "[I 00:00:12,084] [game_server] Switching state from Load to LoadEcsScene after 10.71 s\n\
                   [I 00:00:12,591] [game_server] Switching state from LoadEcsScene to Run after 507.38 ms\n\
                   [I 00:00:12,698] [ecs] Build zones changed\n";
        assert_eq!(last_state(log).as_deref(), Some("Run"));
        assert!(log.contains(READY));
        assert_eq!(last_state("nothing"), None);
        let stop = format!("{log}[I 00:00:16,080] [game_server] Switching state from Run to Shutdown after 3.49 s\n");
        assert_eq!(last_state(&stop).as_deref(), Some("Shutdown"));
    }

    #[test]
    fn log_watch() {
        let dir = std::env::temp_dir().join(format!("kilnctl-test-{}", std::process::id()));
        std::fs::create_dir_all(&dir).unwrap();
        let f = dir.join("log");
        std::fs::write(&f, "old run\r\nLoadEcsScene to Run\r\n").unwrap();
        let mut w = LogWatch::new(f.clone());
        assert_eq!(w.poll().as_deref(), Some(""));
        // appended in place
        std::fs::write(&f, "old run\r\nLoadEcsScene to Run\r\nnew line\r\n").unwrap();
        assert_eq!(w.poll().as_deref(), Some("new line\n"));
        // truncated and rewritten
        std::fs::write(&f, "boot\r\n").unwrap();
        assert_eq!(w.poll().as_deref(), Some("boot\n"));
        // replaced by a new file
        let tmp = dir.join("log.new");
        std::fs::write(&tmp, "fresh\nLoadEcsScene to Run\n").unwrap();
        std::fs::rename(&tmp, &f).unwrap();
        assert!(w.poll().unwrap().contains(READY));
        std::fs::remove_dir_all(&dir).unwrap();
    }
}
