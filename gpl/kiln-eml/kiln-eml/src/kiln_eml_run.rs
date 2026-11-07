// SPDX-License-Identifier: GPL-3.0-or-later
//! kiln-eml-run: the plugin's discovery and run, on the host, against a game folder (tests, lab checks, offline
//! content checks). The same code the plugin runs before the game starts; no game process involved.
//!
//!   kiln-eml-run --game <dir> [--mods <dir>]... [--mod <folder|zip>]... [--profile <profile.json>] [--dropins]
//!                [--no-patch] [--export [--export-dir <dir>]] [--skip-cache] [--cache <dir>] [--state <file>] [-v]
//!
//! Without --mods/--mod/--profile it discovers like the plugin in a game without a profile: <game>/kiln/eml/mods
//! then <game>/mods. Prints the state JSON (the plugin's <game>/kiln/state/eml.json) on stdout; exit 0 when the run
//! is done or idle, 1 when it failed, 2 on bad arguments.

use camino::Utf8PathBuf;
use kiln_eml::discover::{self, Candidate};
use kiln_eml::logging::LineLayer;
use kiln_eml::runner::{self, Phase, RunConfig};
use tracing::Level;
use tracing_subscriber::prelude::*;

fn main() {
    let mut args = std::env::args().skip(1);
    let mut game: Option<Utf8PathBuf> = None;
    let mut mods_dirs = Vec::new();
    let mut mods = Vec::new();
    let mut profile: Option<Utf8PathBuf> = None;
    let mut dropins = false;
    let mut cfg = RunConfig { patch: true, ..Default::default() };
    let mut cache: Option<Utf8PathBuf> = None;
    let mut state: Option<Utf8PathBuf> = None;
    let mut level = Level::INFO;
    let usage = || -> ! {
        eprintln!("usage: kiln-eml-run --game <dir> [--mods <dir>]... [--mod <path>]... [--profile <json>] [--dropins] \
                   [--no-patch] [--export [--export-dir <dir>]] [--skip-cache] [--cache <dir>] [--state <file>] [-v]");
        std::process::exit(2)
    };
    while let Some(a) = args.next() {
        let mut val = || args.next().map(Utf8PathBuf::from).unwrap_or_else(|| usage());
        match a.as_str() {
            "--game" => game = Some(val()),
            "--mods" => mods_dirs.push(val()),
            "--mod" => mods.push(val()),
            "--profile" => profile = Some(val()),
            "--dropins" => dropins = true,
            "--no-patch" => cfg.patch = false,
            "--export" => cfg.export = true,
            "--export-dir" => {
                // EML falls back to <game>/export when the folder does not exist yet
                let d = val();
                let _ = std::fs::create_dir_all(&d);
                cfg.export_dir = Some(d.into_std_path_buf())
            }
            "--skip-cache" => cfg.skip_cache = true,
            "--cache" => cache = Some(val()),
            "--state" => state = Some(val()),
            "-v" => level = Level::DEBUG,
            _ => usage(),
        }
    }
    let Some(game) = game else { usage() };
    tracing_subscriber::registry()
        .with(LineLayer::new(level, Box::new(|l, t, s| eprintln!("[{l}] {t}: {s}"))))
        .init();

    let mut candidates: Vec<Candidate> = Vec::new();
    if let Some(p) = &profile {
        let text = std::fs::read_to_string(p).unwrap_or_else(|e| {
            eprintln!("{p}: {e}");
            std::process::exit(2)
        });
        candidates.extend(discover::profile_candidates(&text));
        dropins |= discover::profile_dropins(&text);
    }
    candidates.extend(mods.iter().map(|p| Candidate { path: p.clone(), source: "cli".into(), enabled: true }));
    for d in &mods_dirs {
        candidates.extend(discover::folder_candidates(d, "cli"));
    }
    if (profile.is_none() && mods.is_empty() && mods_dirs.is_empty()) || dropins {
        candidates.extend(discover::folder_candidates(&game.join("kiln/eml/mods"), "kiln-dropin"));
        candidates.extend(discover::folder_candidates(&game.join("mods"), "game-mods"));
    }
    let (run, entries) = discover::resolve(&candidates);
    cfg.cache_dir = cache.unwrap_or_else(|| game.join("kiln/eml/cache"));
    cfg.game_dir = game;
    let out = runner::run(&cfg, run, entries);
    let json = serde_json::to_string_pretty(&out).unwrap();
    if let Some(s) = state {
        if let Err(e) = std::fs::write(&s, &json) {
            eprintln!("{s}: {e}");
        }
    }
    println!("{json}");
    std::process::exit(if out.phase == Phase::Failed { 1 } else { 0 });
}
