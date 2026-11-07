//! Run the Deck's pure filesystem detection tests without building Tauri or opening a window.
#![cfg(feature = "full")]
#[path = "../../../launcher/src-tauri/src/deck/browse.rs"]
#[allow(dead_code)]
mod browse;
