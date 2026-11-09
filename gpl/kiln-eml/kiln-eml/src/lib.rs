// SPDX-License-Identifier: GPL-3.0-or-later
//! kiln-eml: EML (Enshrouded Mod Loader by Brabb3l, kfc-parser v0.2.0) as a Kiln plugin.
//!
//! - `discover`: which EML mods this start runs (Kiln profile, engine plugins, drop-in folders).
//! - `runner`: one EML run over them, failing soft (bad mods are switched off, a failed run restores vanilla data).
//! - `logging`: EML's tracing events as plain lines (kiln.log, kiln/logs/eml.log, stderr).
//! - `plugin` (Windows): the DLL's exports (kiln_plugin_early, kiln_plugin_main, kiln_eml_service).
//!
//! GPL-3.0-or-later. Kiln (MIT) talks to it only through its exports and native/core/abi/services/kiln_eml.h.

pub mod discover;
pub mod logging;
pub mod runner;
pub mod portraits;
pub mod portrait_export;

// Status codes of the bounded portrait export (not KilnResult).
pub const OK: i32 = 0;
pub const BAD_REQUEST: i32 = 2;
pub const RUN_FAILED: i32 = 3;
pub const ITEMS_FAILED: i32 = 4;

#[cfg(any(windows, test))]
mod plugin;

/// kiln-eml's own version.
pub const VERSION: &str = env!("CARGO_PKG_VERSION");
/// The EML release eml/* was taken from.
pub const EML_VERSION: &str = "0.2.0";
