//! Steam Deck mode (docs/kiln/launcher/deck.md): whether this is a Deck, SteamOS or gamescope ([`detect`],
//! `app_info.deck`), the native gamepad reader ([`gamepad`], `kiln://gamepad`) and Add mods' folder browser
//! ([`browse`]). The Tauri commands are in commands/deck.rs. The Deck UI never writes Steam's files: it tells the
//! player what to set in Steam instead.

pub mod browse;
pub mod detect;
pub mod gamepad;

pub use detect::DeckInfo;

/// The event the gamepad reader emits (contracts.md section 10).
pub const GAMEPAD_EVENT: &str = "kiln://gamepad";
