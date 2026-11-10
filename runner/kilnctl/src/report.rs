// SPDX-License-Identifier: MIT
//! `kilnctl report`: a support report (kiln_manage::report) of a server instance, a server or client folder, or the
//! client Steam knows. Reads only; the zip goes where --out says (default: the current folder).
use crate::bundle::data_dirs;
use crate::util::Km;
use anyhow::Result;
use kiln_manage::report::{self, ReportInput};
use std::path::PathBuf;

pub struct ReportArgs {
    pub name: Option<String>,
    pub dir: Option<PathBuf>,
    pub out: Option<PathBuf>,
    pub print: bool,
}

pub fn run(a: ReportArgs) -> Result<i32> {
    let mut input = ReportInput {
        tool: format!("kilnctl {}", kiln_manage::VERSION),
        dirs: data_dirs().ok(),
        ..Default::default()
    };
    if let Some(name) = &a.name {
        let inst = crate::config::load(name)?;
        input.facts.push(("Instance".into(), format!("{} (query port {})", inst.name, inst.query_port)));
        input.facts.push(("Wine".into(), inst.wine.to_config()));
        input.facts.push(("Policy".into(), inst.kiln.policy.as_str().to_string()));
        match kiln_manage::game::inspect(&inst.dir) {
            Ok(mut g) => {
                g.prefix = Some(inst.prefix.clone());
                input.install = Some(g);
            }
            Err(_) => input.game_dir = Some(inst.dir.clone()),
        }
    } else if let Some(d) = &a.dir {
        match kiln_manage::game::inspect(d) {
            Ok(g) => input.install = Some(g),
            Err(_) => input.game_dir = Some(d.clone()),
        }
    } else {
        // reading only: the real client folder needs no --real
        input.install = kiln_manage::game::detect().km()?.into_iter().find(|g| g.kind == kiln_manage::game::GameKind::Client);
        if input.install.is_none() {
            eprintln!("kilnctl: no Enshrouded client in any Steam library (pass an instance name or --dir); the report has the system only");
        }
    }
    let r = report::build(&input);
    if a.print {
        print!("{}", r.summary);
        return Ok(0);
    }
    let out = a.out.unwrap_or_else(|| PathBuf::from(report::file_name(std::time::SystemTime::now())));
    let size = report::write_zip(&r, &out).km()?;
    print!("{}", r.short);
    println!("\nwrote {} ({}): attach it to your report. Names and paths in it are replaced; summary.txt says what was left out.",
        out.display(), kiln_manage::util::human_size(size));
    Ok(0)
}
