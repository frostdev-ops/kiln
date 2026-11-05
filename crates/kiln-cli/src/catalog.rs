// SPDX-License-Identifier: MIT
//! `kiln catalog status | build | path | search`: kiln_manage::catalog (docs/kiln/modding/catalogs.md).

use crate::out::{self, CliError};
use crate::{CatalogArgs, CatalogCmd};
use anyhow::Result;
use kiln_manage::catalog;
use serde_json::json;

pub fn run(a: CatalogArgs, json: bool) -> Result<i32> {
    match a.cmd {
        CatalogCmd::Path => {
            let dir = catalog::catalog_dir()?;
            let client = catalog::file_for(&dir, "client");
            let server = catalog::file_for(&dir, "server");
            let st = catalog::status(&dir, None).unwrap_or_default();
            let exists = client.is_file() || server.is_file();
            let current = st.iter().filter(|s| s.exists).map(|s| s.current).collect::<Vec<_>>();
            if json {
                out::print_json(&json!({"dir": dir, "client": client, "server": server, "exists": exists,
                                        "current": if current.is_empty() { None } else { Some(current.iter().all(|c| *c != Some(false))) }}));
            } else {
                println!("{}", dir.display());
            }
            Ok(0)
        }
        CatalogCmd::Status { game } => {
            let dir = catalog::catalog_dir()?;
            let mut all = Vec::new();
            match game {
                Some(g) => all.extend(catalog::status(&dir, Some(&g))?),
                None => {
                    // compare each side with its installed game when one is found
                    #[cfg(feature = "catalog-build")]
                    {
                        let games = catalog::game_dirs();
                        for side in ["client", "server"] {
                            let g = games.iter().find(|(d, _)| catalog::side_of(d) == Some(side)).map(|(d, _)| d.clone());
                            all.extend(catalog::status(&dir, g.as_deref())?.into_iter().filter(|s| s.side == side));
                        }
                    }
                    #[cfg(not(feature = "catalog-build"))]
                    all.extend(catalog::status(&dir, None)?);
                }
            }
            if json {
                out::print_json(&json!({"dir": dir, "catalogs": all}));
            } else {
                for s in &all {
                    let cur = match s.current {
                        Some(true) => "current",
                        Some(false) => "OUTDATED (kiln catalog build)",
                        None => "unknown",
                    };
                    if s.exists {
                        let counts = s.counts.iter().map(|(k, v)| format!("{k} {v}")).collect::<Vec<_>>().join(", ");
                        println!("{}: {} ({cur}; generated {}): {counts}", s.side, s.path.display(), s.generated.as_deref().unwrap_or("?"));
                    } else {
                        println!("{}: none ({})", s.side, s.path.display());
                    }
                }
            }
            Ok(0)
        }
        CatalogCmd::Build { side, game, force, types } => build(side, game, force, types, json),
        CatalogCmd::Search { query, section, limit, side } => {
            let path = catalog::find(side.as_deref(), None)
                .ok_or_else(|| CliError::new("not_found", "no game-data catalog yet: kiln catalog build"))?;
            let cat = catalog::Catalog::load(&path)?;
            let hits = catalog::search(&cat, &query, section.as_deref(), limit);
            if json {
                out::print_json(&json!({"catalog": path, "hits": hits}));
            } else {
                for h in &hits {
                    println!(
                        "{:<10} {:<48} {:<10} {:<36} {}",
                        h.section,
                        h.name.as_deref().unwrap_or("-"),
                        h.id.as_deref().unwrap_or(""),
                        h.guid.as_deref().unwrap_or(""),
                        h.display_name.as_deref().unwrap_or("")
                    );
                }
                if hits.is_empty() {
                    println!("nothing matches '{query}'");
                }
            }
            Ok(0)
        }
    }
}

#[cfg(feature = "catalog-build")]
fn build(side: Option<String>, game: Option<std::path::PathBuf>, force: bool, types: bool, json: bool) -> Result<i32> {
    struct P(bool);
    impl kiln_manage::task::Progress for P {
        fn report(&mut self, f: Option<f32>, m: &str) {
            if !self.0 && !m.is_empty() {
                eprintln!("{}{m}", f.map(|f| format!("[{:>3.0}%] ", f * 100.0)).unwrap_or_default());
            }
        }
    }
    let opts = catalog::BuildOptions { force, types_dump: types, ..Default::default() };
    let infos = match game {
        Some(g) => vec![catalog::build(&g, &catalog::catalog_dir()?, &opts, &mut P(json))?],
        None => catalog::build_default(side.as_deref(), &opts, &mut P(json))?,
    };
    if json {
        out::print_json(&json!({"ok": true, "catalogs": infos}));
    } else {
        for i in &infos {
            let counts = i.counts.iter().map(|(k, v)| format!("{k} {v}")).collect::<Vec<_>>().join(", ");
            println!("{}: {} ({}; {:.1} s): {counts}", i.side, i.path.display(), if i.built { "built" } else { "already current" }, i.seconds);
        }
        if infos.is_empty() {
            println!("no installed client or server found (--game <folder>)");
        }
    }
    Ok(if infos.is_empty() { 1 } else { 0 })
}

#[cfg(not(feature = "catalog-build"))]
fn build(_: Option<String>, _: Option<std::path::PathBuf>, _: bool, _: bool, _: bool) -> Result<i32> {
    Err(CliError::new("unavailable", "this kiln was built without `catalog-build` (it reads the game through kiln-manage's gameart)").into())
}
