//! Where this build of the launcher is distributed, which decides whether it updates itself:
//! - `direct` (frostdev.io, GitHub Releases): the self-update (crate::selfupdate) is compiled in.
//! - `nexus` (Nexus Mods): no self-update at all. Nexus Mods does not allow programs that update themselves, so this
//!   build has no updater plugin, no update feeds and no updater key (tauri.nexus.conf.json removes it from the
//!   config); the `update_*` commands report updates as disabled and never touch the network. New versions come
//!   from the Kiln page on Nexus Mods ([`NEXUS_PAGE`]), which the UI only links to.
//!
//! The cargo feature `self-update` (on by default) is the switch: on = direct, off (`--no-default-features`) = nexus.
//! tools/release/release.sh builds both (docs/kiln/launcher/releasing.md, "The two builds").

use serde::{Deserialize, Serialize};

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum Distribution {
    /// frostdev.io and GitHub Releases: updates itself.
    Direct,
    /// Nexus Mods: never updates itself.
    Nexus,
}

/// This build's distribution.
pub const CURRENT: Distribution = if cfg!(feature = "self-update") { Distribution::Direct } else { Distribution::Nexus };

/// The Kiln page on Nexus Mods: where a Nexus build's new versions are (opened in the browser; nothing is fetched).
pub const NEXUS_PAGE: &str = "https://www.nexusmods.com/enshrouded/mods/301";

impl Distribution {
    pub fn as_str(self) -> &'static str {
        match self {
            Distribution::Direct => "direct",
            Distribution::Nexus => "nexus",
        }
    }

    /// The build updates itself.
    pub fn self_updates(self) -> bool {
        self == Distribution::Direct
    }
}

impl Default for Distribution {
    fn default() -> Self {
        CURRENT
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn the_feature_is_the_distribution() {
        assert_eq!(CURRENT.self_updates(), cfg!(feature = "self-update"));
        assert_eq!(CURRENT.as_str(), if cfg!(feature = "self-update") { "direct" } else { "nexus" });
        assert_eq!(serde_json::to_value(Distribution::Nexus).unwrap(), "nexus");
        assert_eq!(serde_json::to_value(Distribution::Direct).unwrap(), "direct");
        assert!(NEXUS_PAGE.starts_with("https://www.nexusmods.com/"));
    }
}
