// SPDX-License-Identifier: MIT
//! `kilnctl library` and `kilnctl profile`: the launcher's mod library and profiles from the command line
//! (kiln_manage::{library, profile}; data in $KILN_DATA_DIR or ~/.local/share/kiln).
use crate::bundle::{Printer, data_dirs};
use crate::util::Km;
use anyhow::{Result, bail};
use kiln_manage::manifest::{ModKind, RunSide};
use kiln_manage::profile::ProfileMod;
use std::path::PathBuf;

pub fn parse_kind(s: &str) -> Result<ModKind> {
    match ModKind::parse(s) {
        Some(k) => Ok(k),
        None => bail!("kind {s:?}: {}", ModKind::ALL.map(|k| k.as_str()).join(", ")),
    }
}

pub fn import(paths: Vec<PathBuf>) -> Result<i32> {
    let dirs = data_dirs()?;
    let r = kiln_manage::library::import(&dirs, &paths, &mut Printer::new()).km()?;
    for x in &r.imported {
        println!("imported {} {} {} ({})", x.kind.as_str(), x.id, x.version, x.hash);
    }
    for x in &r.existing {
        println!("already  {} {} {} ({})", x.kind.as_str(), x.id, x.version, x.hash);
    }
    for s in &r.skipped {
        println!("skipped  {}: {}", s.path, s.reason);
    }
    Ok(if r.skipped.is_empty() { 0 } else { 1 })
}

pub fn list(kind: Option<String>) -> Result<i32> {
    let dirs = data_dirs()?;
    let k = kind.as_deref().map(parse_kind).transpose()?;
    for e in kiln_manage::library::list(&dirs, k).km()? {
        for v in &e.versions {
            println!("{:<7} {:<24} {:<14} {}  {}{}", e.kind.as_str(), e.id, v.version, v.hash, v.folder.display(),
                if v.changed { "  (changed on disk)" } else { "" });
        }
    }
    Ok(0)
}

/// `kind:id[@version]`: a Library entry whose files were changed in its folder becomes the mod's entry
/// (kiln_manage::library::take_over); the profiles that listed it list the new one.
pub fn take_over(spec: &str) -> Result<i32> {
    let dirs = data_dirs()?;
    let Some((kind, rest)) = spec.split_once(':') else { bail!("{spec:?}: use kind:id[@version]") };
    let kind = parse_kind(kind)?;
    let (id, version) = rest.split_once('@').map(|(i, v)| (i, Some(v))).unwrap_or((rest, None));
    let entries = kiln_manage::library::list(&dirs, Some(kind)).km()?;
    let Some(e) = entries.iter().find(|e| e.id == id) else { bail!("{} {id} is not in the library", kind.as_str()) };
    let Some(v) = e.versions.iter().find(|v| version.is_none_or(|w| v.version == w)) else { bail!("{id}: no version {}", version.unwrap_or("?")) };
    let r = kiln_manage::library::LibraryRef { kind, id: id.into(), version: v.version.clone(), hash: v.hash.clone() };
    let done = kiln_manage::library::take_over(&dirs, &r).km()?;
    if done.to == done.from {
        println!("{} {} {}: its files are the ones imported, nothing to take over", kind.as_str(), id, v.version);
    } else {
        println!("{} {}: now {} {} ({}), was {} ({})", kind.as_str(), id, done.name, done.to.version, done.to.hash, done.from.version, done.from.hash);
    }
    Ok(0)
}

pub fn create(name: &str, copy_from: Option<String>) -> Result<i32> {
    let dirs = data_dirs()?;
    let p = kiln_manage::profile::create(&dirs, name, copy_from.as_deref()).km()?;
    println!("profile {} ({}) in {}", p.id, p.name, dirs.profile(&p.id).display());
    Ok(0)
}

/// `kind:id[@version]`: the newest library version unless one is named.
pub fn add(profile: &str, mods: &[String], disabled: bool) -> Result<i32> {
    let dirs = data_dirs()?;
    let mut p = kiln_manage::profile::get(&dirs, profile).km()?;
    for spec in mods {
        let Some((kind, rest)) = spec.split_once(':') else { bail!("{spec:?}: use kind:id[@version]") };
        let kind = parse_kind(kind)?;
        let (id, version) = rest.split_once('@').map(|(i, v)| (i, Some(v))).unwrap_or((rest, None));
        let entries = kiln_manage::library::list(&dirs, Some(kind)).km()?;
        let Some(e) = entries.iter().find(|e| e.id == id) else { bail!("{} {id} is not in the library (kilnctl library import)", kind.as_str()) };
        let Some(v) = e.versions.iter().find(|v| version.is_none_or(|w| v.version == w)) else { bail!("{id}: no version {}", version.unwrap_or("?")) };
        let m = ProfileMod { id: id.into(), version: v.version.clone(), hash: v.hash.clone(), enabled: !disabled };
        let list = p.mods.list_mut(kind);
        list.retain(|x| x.id != m.id);
        list.push(m);
    }
    let p = kiln_manage::profile::save(&dirs, &p).km()?;
    println!("profile {} saved (generation {}, fingerprint {})", p.id, p.generation, kiln_manage::profile::fingerprint(&p));
    Ok(0)
}

pub fn show(profile: &str, side: &str) -> Result<i32> {
    let dirs = data_dirs()?;
    let p = kiln_manage::profile::get(&dirs, profile).km()?;
    println!("profile {} ({}), generation {}, updated {}", p.id, p.name, p.generation, p.updated);
    for kind in ModKind::ALL {
        let k = kind.as_str();
        for m in p.mods.list(kind) {
            println!("  {k:<7} {:<24} {:<10} {}{}", m.id, m.version, m.hash, if m.enabled { "" } else { "  (disabled)" });
        }
    }
    let side = if side == "server" { RunSide::Server } else { RunSide::Client };
    let r = kiln_manage::profile::resolve_preview(&dirs, &p, side).km()?;
    println!("load order ({}): {}", if side == RunSide::Server { "server" } else { "client" }, r.kiln.order.join(", "));
    for x in &r.kiln.rejected {
        println!("  not loaded {} ({:?}): {}", x.id, x.state, x.reason);
    }
    for n in &r.native {
        println!("  native {} {} {:?}{}", n.id, n.version, n.phase, n.reason.as_deref().map(|r| format!(": {r}")).unwrap_or_default());
    }
    for w in &r.warnings {
        println!("  warning: {w}");
    }
    Ok(0)
}
