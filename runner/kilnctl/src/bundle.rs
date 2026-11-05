// SPDX-License-Identifier: MIT
//! `kilnctl bundle`: runtime bundles (docs/kiln/launcher/bundle.md) on kiln_manage::{bundle, install}.
use crate::util::Km;
use anyhow::{Context, Result, bail};
use kiln_manage::game::{GameInstall, GameKind};
use kiln_manage::install::{self, InstallOptions};
use kiln_manage::paths::DataDirs;
use kiln_manage::task::Progress;
use std::path::{Path, PathBuf};

/// Prints a long operation's progress: every 10 %, and the messages that are not per-file steps.
pub struct Printer {
    last: i32,
}

impl Printer {
    pub fn new() -> Self {
        Printer { last: -1 }
    }
}

impl Progress for Printer {
    fn report(&mut self, fraction: Option<f32>, message: &str) {
        let per_file = ["verified ", "staged ", "installed kiln/", "installed dinput8", "removed "].iter().any(|p| message.starts_with(p));
        let step = fraction.map(|f| (f * 10.0) as i32).unwrap_or(-1);
        if !per_file {
            println!("  {message}");
        } else if step > self.last {
            self.last = step;
            println!("  {:>3}% {message}", step * 10);
        }
    }
}

pub fn data_dirs() -> Result<DataDirs> {
    DataDirs::locate().km()
}

/// The git id and commit time of the Kiln tree (bundle names, entry times).
pub fn git_facts(source: &Path) -> Result<(String, i64)> {
    let out = std::process::Command::new("git").arg("-C").arg(source).args(["log", "-1", "--format=%h %ct", "--abbrev=7"]).output()
        .context("running git")?;
    let s = String::from_utf8_lossy(&out.stdout);
    let mut it = s.split_whitespace();
    match (it.next(), it.next().and_then(|t| t.parse().ok())) {
        (Some(h), Some(t)) if out.status.success() => Ok((h.to_string(), t)),
        _ => bail!("no git commit in {} (pass --git and --epoch)", source.display()),
    }
}

pub fn create(stage: &Path, out: &Path, git: Option<String>, epoch: Option<i64>) -> Result<i32> {
    let (g, e) = match (git, epoch) {
        (Some(g), Some(e)) => (g, e),
        (g, e) => {
            let (fg, fe) = git_facts(&kiln_manage::server::instance::compiled_source())?;
            (g.unwrap_or(fg), e.unwrap_or(fe))
        }
    };
    let zip = kiln_manage::bundle::create(stage, out, &kiln_manage::bundle::CreateOptions { source_date_epoch: e, git: g }).km()?;
    let m = kiln_manage::bundle::verify(&zip, &mut kiln_manage::task::NoProgress).km()?;
    let sha = kiln_manage::util::sha256(&zip).km()?;
    let size: u64 = m.files.iter().map(|f| f.size).sum();
    println!("bundle {} ({} files, {} unpacked, components {})", zip.display(), m.files.len(),
        crate::util::human_size(size), m.components.keys().cloned().collect::<Vec<_>>().join(", "));
    println!("sha256 {sha}");
    Ok(0)
}

pub fn verify(zip: &Path) -> Result<i32> {
    let m = kiln_manage::bundle::verify(zip, &mut kiln_manage::task::NoProgress).km()?;
    println!("ok: {} {} files, every size and sha256 matches kiln-runtime.json", m.version, m.files.len());
    for (name, c) in &m.components {
        let n = m.files.iter().filter(|f| &f.component == name).count();
        let sides: Vec<&str> = c.sides.iter().map(|s| if *s == kiln_manage::manifest::RunSide::Server { "server" } else { "client" }).collect();
        println!("  {name:<13} {n:>4} files  {:<14} {}{}", sides.join(","), if c.default { "default" } else { "optional" },
            c.description.as_deref().map(|d| format!("  {d}")).unwrap_or_default());
    }
    for (side, b) in [("server", &m.builds.server), ("client", &m.builds.client)] {
        for x in b {
            println!("  verified {side} build {}{}", &x.sha256[..12], x.steam_build.as_deref().map(|s| format!(" (steam build {s})")).unwrap_or_default());
        }
    }
    Ok(0)
}

/// A game folder as an install, refusing a Steam library folder unless `real` (tests run on copies).
pub fn game_at(dir: &Path, real: bool) -> Result<GameInstall> {
    let g = kiln_manage::game::inspect(dir).km()?;
    let s = format!("{}/", g.path.display());
    if s.contains("/steamapps/common/") && !real {
        bail!("{} is inside a Steam library (the real game): add --real to change it", g.path.display());
    }
    Ok(g)
}

pub fn print_receipt(r: &install::InstallReceipt, dir: &Path) {
    println!("installed Kiln {} into {} ({} side, install id {})", r.runtime.version, dir.display(),
        if r.side == kiln_manage::manifest::RunSide::Server { "server" } else { "client" }, r.install_id);
    println!("  components {}; {} files; receipt {}", r.runtime.components.join(", "), r.files.len(), dir.join("kiln/.install.json").display());
    let adopted = r.replaced.iter().filter(|x| x.action == install::ReplacedAction::Adopted).count();
    if adopted > 0 {
        println!("  adopted {adopted} file(s) that were already there with the bundle's bytes (uninstall leaves them)");
    }
    for x in r.replaced.iter().filter(|x| x.action != install::ReplacedAction::Adopted) {
        println!("  replaced {} ({:?}, {:?}){}{}", x.path, x.class.map(|c| format!("{c:?}").to_lowercase()).unwrap_or_default(),
            x.action, x.chain_path.as_deref().map(|c| format!(" -> chained as {c}")).unwrap_or_default(),
            x.backup.as_deref().map(|b| format!(", backup {b}")).unwrap_or_default());
    }
}

pub struct InstallArgs {
    pub zip: PathBuf,
    pub dir: PathBuf,
    pub components: Option<Vec<String>>,
    pub switch_from_shroudforge: bool,
    pub real: bool,
}

pub fn install_cmd(a: InstallArgs) -> Result<i32> {
    let g = game_at(&a.dir, a.real)?;
    install_into(&g, &a.zip, a.components, a.switch_from_shroudforge)?;
    Ok(0)
}

pub fn install_into(g: &GameInstall, zip: &Path, components: Option<Vec<String>>, sf: bool) -> Result<install::InstallReceipt> {
    let dirs = data_dirs()?;
    println!("installing {} into {} ({})", zip.display(), g.path.display(), if g.kind == GameKind::Server { "server" } else { "client" });
    let r = install::install(&dirs, g, zip, &InstallOptions {
        components,
        switch_from_shroudforge: sf,
        installer: Some(format!("kilnctl {}", env!("CARGO_PKG_VERSION"))),
    }, &mut Printer::new())
    .km()?;
    print_receipt(&r, &g.path);
    Ok(r)
}

pub fn uninstall_cmd(dir: &Path, real: bool) -> Result<i32> {
    let g = game_at(dir, real)?;
    let dirs = data_dirs()?;
    let rep = install::uninstall_report(&dirs, &g, &mut Printer::new()).km()?;
    println!("uninstalled Kiln from {}: {} file(s) removed, restored {}, {} file(s) that were there before left in place; kiln/ kept",
        g.path.display(), rep.removed, if rep.restored.is_empty() { "nothing".into() } else { rep.restored.join(", ") }, rep.adopted.len());
    if !rep.kept.is_empty() {
        println!("  changed since the install, left in place: {}", rep.kept.join(", "));
    }
    Ok(0)
}
