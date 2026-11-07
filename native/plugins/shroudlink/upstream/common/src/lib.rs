//! Shared plumbing for the chestsort client and server DLLs.

pub mod log;
pub mod proxy;
pub mod scan;

use std::path::PathBuf;

/// Directory of the host executable (the game or server install folder).
pub fn game_dir() -> PathBuf {
    std::env::current_exe()
        .ok()
        .and_then(|p| p.parent().map(|d| d.to_path_buf()))
        .unwrap_or_else(|| PathBuf::from("."))
}
