// SPDX-License-Identifier: MIT
//! `kilnctl client`: Kiln in the game client on kiln_manage (status, install, uninstall, profile activation,
//! play-once). Never writes Steam's configuration; the real Steam folder needs --real.
use crate::bundle::{Printer, data_dirs, game_at};
use crate::util::Km;
use anyhow::{Result, bail};
use kiln_manage::game::{GameInstall, GameKind};
use kiln_manage::playonce::{PlayOnce, PlayOnceMode};
use std::path::{Path, PathBuf};

/// `--dir`, else the client Steam knows (read-only discovery).
pub fn client_at(dir: Option<&Path>, real: bool) -> Result<GameInstall> {
    let g = match dir {
        Some(d) => game_at(d, real)?,
        None => {
            let found = kiln_manage::game::detect().km()?;
            let Some(g) = found.into_iter().find(|g| g.kind == GameKind::Client) else {
                bail!("no Enshrouded client in any Steam library (pass --dir)");
            };
            if !real {
                bail!("{} is the real game folder: add --real to change it (or pass --dir to a copy)", g.path.display());
            }
            g
        }
    };
    if g.kind != GameKind::Client {
        bail!("{} is a dedicated server folder; use kilnctl deploy or kilnctl bundle install", g.path.display());
    }
    Ok(g)
}

fn yes_no(b: bool) -> &'static str {
    if b { "yes" } else { "no" }
}

pub fn status(dir: Option<PathBuf>) -> Result<i32> {
    // status only reads: the real folder needs no --real
    let g = match &dir {
        Some(d) => kiln_manage::game::inspect(d).km()?,
        None => kiln_manage::game::detect().km()?.into_iter().find(|g| g.kind == GameKind::Client)
            .ok_or_else(|| anyhow::anyhow!("no Enshrouded client in any Steam library (pass --dir)"))?,
    };
    let dirs = data_dirs()?;
    let st = kiln_manage::game::status(&dirs, &g).km()?;
    println!("client    {} ({}, {:?}, install id {})", g.path.display(), g.exe, g.platform, g.id);
    println!("build     {}{}", g.build_id.as_deref().unwrap_or("?"), g.prefix.as_ref().map(|p| format!(", prefix {}", p.display())).unwrap_or_default());
    match &st.runtime.proxy {
        Some(p) => println!("dinput8   {:?}{}{}", p.kind, p.version.as_deref().map(|v| format!(" {v}")).unwrap_or_default(),
            p.detail.as_deref().map(|d| format!(" ({d})")).unwrap_or_default()),
        None => println!("dinput8   ?"),
    }
    match &st.runtime.receipt {
        Some(r) => println!("kiln      {} installed {} ({}), components {}{}", r.runtime.version, r.installed,
            r.installer.as_deref().unwrap_or("?"), r.runtime.components.join(", "),
            if st.runtime.modified.is_empty() { String::new() } else { format!("; {} file(s) changed since", st.runtime.modified.len()) }),
        None => println!("kiln      not installed by the installer (no kiln/.install.json)"),
    }
    if let Some(off) = st.runtime.receipt.as_ref().and_then(|r| r.runtime.deselected.as_ref()).filter(|d| !d.is_empty()) {
        println!("off       {} (opt-outs; updates keep them off unless required)", off.join(", "));
    }
    if !st.runtime.missing_parts.is_empty() {
        println!("missing   {} (defaults or requirements of the installed bundle; `kilnctl client install` adds them)", st.runtime.missing_parts.join(", "));
    }
    if let Some(u) = &st.runtime.upgrade_available {
        println!("upgrade   {u} is in {}", dirs.runtime().display());
    }
    if let Ok(c) = kiln_manage::chain::read(&g.path) {
        for e in &c.chain {
            println!("chain     {}", e.path);
        }
        for e in &c.profile {
            println!("dllmain   {}", e.path);
        }
    }
    println!("profile   {}", st.active_profile.as_deref().unwrap_or("none (legacy layout: kiln/mods)"));
    println!("running   {}{}", yes_no(st.running), st.pid.map(|p| format!(" (pid {p})")).unwrap_or_default());
    if st.steam_updating {
        println!("steam     updating or validating the game");
    }
    if st.play_once_pending {
        println!("play-once kiln/play-once.ini is pending (the proxy has not consumed it)");
    }
    if let Some(lo) = &st.launch_options {
        println!("launch    options {:?}; dinput8 override {}{}", lo.value.as_deref().unwrap_or(""), yes_no(lo.has_dll_override),
            lo.advice.as_deref().map(|a| format!("; set the launch options to: {a}")).unwrap_or_default());
    }
    if let Some(b) = &st.boot {
        println!("boot      Kiln {} at {} (pid {}), hooks {}, profile {:?}{}, play-once {}", b.kiln, b.written, b.pid, yes_no(b.hooks),
            b.profile.mode, b.profile.id.as_deref().map(|i| format!(" {i} gen {}", b.profile.generation)).unwrap_or_default(),
            b.play_once.as_ref().map(|p| p.mode.as_str()).unwrap_or("none"));
    }
    Ok(if st.running { 0 } else { 3 })
}

pub fn install(zip: &Path, dir: Option<PathBuf>, components: Option<Vec<String>>, sf: bool, real: bool) -> Result<i32> {
    let g = client_at(dir.as_deref(), real)?;
    crate::bundle::install_into(&g, zip, components, sf)?;
    Ok(0)
}

pub fn uninstall(dir: Option<PathBuf>, real: bool) -> Result<i32> {
    let g = client_at(dir.as_deref(), real)?;
    let dirs = data_dirs()?;
    let rep = kiln_manage::install::uninstall_report(&dirs, &g, &mut Printer::new()).km()?;
    println!("uninstalled Kiln from {}: {} file(s) removed, restored {}, {} file(s) that were there before left in place; kiln/ kept",
        g.path.display(), rep.removed, if rep.restored.is_empty() { "nothing".into() } else { rep.restored.join(", ") }, rep.adopted.len());
    Ok(0)
}

pub fn activate(profile: Option<String>, deactivate: bool, dir: Option<PathBuf>, real: bool) -> Result<i32> {
    let g = client_at(dir.as_deref(), real)?;
    let dirs = data_dirs()?;
    if deactivate {
        kiln_manage::profile::deactivate(&dirs, &g).km()?;
        println!("deactivated: {} boots the legacy layout (kiln/mods)", g.path.display());
        return Ok(0);
    }
    let Some(pid) = profile else { bail!("name a profile (or pass --deactivate)") };
    let a = kiln_manage::profile::activate(&dirs, &pid, &g).km()?;
    println!("activated profile {} (generation {}) on {}: {:?} paths, {} folder(s) copied, fingerprint {}", a.profile_id, a.generation,
        g.path.display(), a.path_mode, a.copied, a.fingerprint);
    println!("  {}", a.profile_json);
    Ok(0)
}

pub fn play_once(mode: &str, profile: Option<String>, dir: Option<PathBuf>, real: bool) -> Result<i32> {
    let g = client_at(dir.as_deref(), real)?;
    let mode = match mode {
        "vanilla" => PlayOnceMode::Vanilla,
        "no-mods" => PlayOnceMode::NoMods,
        "profile" => PlayOnceMode::Profile,
        "clear" => {
            kiln_manage::playonce::clear(&g.path).km()?;
            println!("cleared {}", g.path.join("kiln/play-once.ini").display());
            return Ok(0);
        }
        m => bail!("mode {m:?}: vanilla, no-mods, profile or clear"),
    };
    if mode == PlayOnceMode::Profile {
        let Some(pid) = &profile else { bail!("play-once profile needs --profile <id>") };
        let dirs = data_dirs()?;
        kiln_manage::profile::write_once(&dirs, pid, &g).km()?;
    } else if profile.is_some() {
        bail!("--profile only goes with mode profile");
    }
    let p = PlayOnce { mode, created: kiln_manage::util::now_unix(), nonce: kiln_manage::playonce::new_nonce().km()?, profile: None };
    kiln_manage::playonce::write(&g.path, &p).km()?;
    println!("wrote {} (mode {}, nonce {}); start the game within {} s (Steam's Play button or steam://rungameid/{})",
        g.path.join("kiln/play-once.ini").display(),
        match mode { PlayOnceMode::Vanilla => "vanilla", PlayOnceMode::NoMods => "no-mods", PlayOnceMode::Profile => "profile" },
        p.nonce, kiln_manage::playonce::MAX_AGE_S, g.app_id);
    Ok(0)
}
