// SPDX-License-Identifier: MIT
//! Update resilience: the exe cache, `tools/sigport/port.py` (port the symbol database to a new exe) and marking a
//! build verified. See docs/kiln/sigport.md.
//!
//! An update replaces the exe, and a port needs the old exe next to the new one, so kilnctl keeps a copy of every exe
//! that has a verified record (`<state>/exe-cache/<sha256>.exe`, reflinked when the filesystem can). The copy is
//! never executed.
use crate::config::Instance;
use crate::{kiln, util};
use anyhow::{Context, Result, bail};
use std::path::{Path, PathBuf};
use std::process::Command;

pub fn cache_dir() -> PathBuf {
    util::state_dir().join("exe-cache")
}

/// Copies `exe` into the cache as `<sha>.exe` (once). Returns the cached path.
pub fn cache_exe(exe: &Path, sha: &str) -> Result<PathBuf> {
    let dst = cache_dir().join(format!("{sha}.exe"));
    if dst.exists() {
        return Ok(dst);
    }
    std::fs::create_dir_all(cache_dir())?;
    let tmp = cache_dir().join(format!(".{sha}.tmp{}", std::process::id()));
    util::run_status(Command::new("cp").args(["--reflink=auto", "--"]).arg(exe).arg(&tmp), "cp (exe cache)")?;
    if util::sha256(&tmp)? != sha {
        let _ = std::fs::remove_file(&tmp);
        bail!("{} changed while it was copied", exe.display());
    }
    std::fs::rename(&tmp, &dst)?;
    Ok(dst)
}

/// Build records of the instance (deployed) and of the source tree, by sha: verified?
fn record_verified(inst: &Instance, sha: &str) -> bool {
    if kiln::verified(inst, sha) == Some(true) {
        return true;
    }
    let p = inst.kiln.source.join(format!("sigdb/builds/server/{sha}.json"));
    std::fs::read_to_string(p).ok().and_then(|t| serde_json::from_str::<serde_json::Value>(&t).ok())
        .and_then(|v| v.get("verified")?.as_bool()).unwrap_or(false)
}

/// The newest cached exe with a verified record, other than `except`.
pub fn baseline_exe(inst: &Instance, except: &str) -> Option<(PathBuf, String)> {
    let mut v: Vec<(std::time::SystemTime, PathBuf, String)> = std::fs::read_dir(cache_dir())
        .into_iter()
        .flatten()
        .flatten()
        .filter_map(|e| {
            let name = e.file_name().to_string_lossy().into_owned();
            let sha = name.strip_suffix(".exe")?.to_string();
            if sha.len() != 64 || sha == except || !record_verified(inst, &sha) {
                return None;
            }
            Some((e.metadata().ok()?.modified().ok()?, e.path(), sha))
        })
        .collect();
    v.sort();
    v.pop().map(|(_, p, s)| (p, s))
}

/// Caches the instance's exe when its build is verified (bootstraps the next update's port).
pub fn remember_verified_exe(inst: &Instance) -> Option<PathBuf> {
    let sha = util::sha256(&inst.exe()).ok()?;
    if !record_verified(inst, &sha) {
        return None;
    }
    cache_exe(&inst.exe(), &sha).ok()
}

#[derive(Debug, Default, Clone)]
pub struct PortReport {
    pub out: PathBuf,
    pub new_sha: String,
    pub complete: bool,
    pub defs_unchanged: bool,
    pub symbols: (u64, u64),
    pub constants: (u64, u64),
    pub low: Vec<String>,
    pub lost: Vec<String>,
    pub features_lost: Vec<String>,
    pub changed: usize,
    pub unverified_fixed: Vec<String>,
}

impl PortReport {
    pub fn record(&self) -> PathBuf {
        self.out.join(format!("builds/server/{}.json", self.new_sha))
    }
    pub fn tree(&self) -> PathBuf {
        self.out.join("tree")
    }
    pub fn patch(&self) -> PathBuf {
        self.out.join("symbols.patch")
    }
    pub fn print(&self) {
        println!("sigport   {}/{} symbols, {}/{} constants ported; {} strategies changed{}",
            self.symbols.0, self.symbols.1, self.constants.0, self.constants.1, self.changed,
            if self.defs_unchanged { " (the deployed definitions work as they are)" } else { " (needs symbols.patch)" });
        if !self.low.is_empty() {
            println!("          low confidence: {}", self.low.join(", "));
        }
        if !self.lost.is_empty() {
            println!("          lost: {}", self.lost.join(", "));
        }
        if !self.features_lost.is_empty() {
            println!("          features off on the new build: {}", self.features_lost.join(", "));
        }
        if !self.unverified_fixed.is_empty() {
            println!("          fixed constants nothing checks (the gate's tests do): {}", self.unverified_fixed.len());
        }
        println!("          report {}", self.out.join("report.txt").display());
    }
}

fn strings(v: &serde_json::Value, path: &[&str]) -> Vec<String> {
    let mut x = v;
    for p in path {
        x = &x[*p];
    }
    x.as_array().into_iter().flatten().filter_map(|s| s.as_str().map(str::to_string)).collect()
}

pub fn parse_report(text: &str, out: PathBuf) -> Result<PortReport> {
    let v: serde_json::Value = serde_json::from_str(text).context("sigport report.json")?;
    let s = &v["summary"];
    let n = |a: &str, b: &str| s[a][b].as_u64().unwrap_or(0);
    Ok(PortReport {
        out,
        new_sha: v["new"]["sha256"].as_str().unwrap_or_default().to_string(),
        complete: s["complete"].as_bool().unwrap_or(false),
        defs_unchanged: s["defs_unchanged"].as_bool().unwrap_or(false),
        symbols: (n("symbols", "ported"), n("symbols", "resolved_old")),
        constants: (n("constants", "ported"), n("constants", "resolved_old")),
        low: [strings(s, &["symbols", "low_confidence"]), strings(s, &["constants", "low_confidence"])].concat(),
        lost: [strings(s, &["symbols", "lost"]), strings(s, &["constants", "lost"])].concat(),
        features_lost: strings(s, &["features", "lost"]),
        changed: s["strategies_changed"].as_array().map(|a| a.len()).unwrap_or(0),
        unverified_fixed: strings(s, &["unverified_fixed"]),
    })
}

/// The definitions a port starts from: the deployed ones, else the source tree's.
pub fn defs(inst: &Instance) -> PathBuf {
    let deployed = kiln::kiln_dir(inst).join("sigdb/server.json");
    if deployed.exists() { deployed } else { inst.kiln.source.join("sigdb/symbols/server.json") }
}

/// Runs tools/sigport/port.py. Exit 1 (incomplete) still gives a report.
pub fn port(inst: &Instance, old: &Path, new: &Path, defs: &Path, out: &Path) -> Result<PortReport> {
    let tool = inst.kiln.source.join("tools/sigport/port.py");
    if !tool.exists() {
        bail!("{} not found (kiln.source)", tool.display());
    }
    std::fs::create_dir_all(out)?;
    println!("python3 {} --old {} --new {} --defs {} --out {}", tool.display(), old.display(), new.display(),
        defs.display(), out.display());
    let st = Command::new("python3")
        .arg("-I")
        .arg(&tool)
        .args(["--side", "server", "--quiet", "--old"])
        .arg(old)
        .arg("--new")
        .arg(new)
        .arg("--defs")
        .arg(defs)
        .arg("--out")
        .arg(out)
        .stdout(std::process::Stdio::null())
        .status()
        .context("running python3 tools/sigport/port.py")?;
    let text = std::fs::read_to_string(out.join("report.json"))
        .with_context(|| format!("port.py wrote no report ({st})"))?;
    if !matches!(st.code(), Some(0 | 1)) {
        bail!("port.py failed ({st})");
    }
    parse_report(&text, out.to_path_buf())
}

/// Writes the candidate record with `"verified": true` into the instance's kiln/sigdb/builds/server/. Refuses to
/// replace a record that is already verified. Returns the path.
pub fn mark_verified(inst: &Instance, report: &PortReport, note: &str) -> Result<PathBuf> {
    let text = std::fs::read_to_string(report.record()).with_context(|| format!("{}", report.record().display()))?;
    let mut rec: serde_json::Value = serde_json::from_str(&text)?;
    rec["verified"] = serde_json::Value::Bool(true);
    rec["note"] = serde_json::Value::String(note.to_string());
    let dst = kiln::kiln_dir(inst).join(format!("sigdb/builds/server/{}.json", report.new_sha));
    if kiln::verified(inst, &report.new_sha) == Some(true) {
        bail!("{} is already verified; not replaced", dst.display());
    }
    std::fs::create_dir_all(dst.parent().context("builds folder")?)?;
    std::fs::write(&dst, serde_json::to_string_pretty(&rec)? + "\n")?;
    // the copy a human commits into the repository (deploy-server.sh copies sigdb/builds/server/*.json)
    std::fs::write(report.out.join(format!("verified-{}.json", report.new_sha)), serde_json::to_string_pretty(&rec)? + "\n")?;
    Ok(dst)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn report_summary() {
        let j = r#"{"new": {"sha256": "ab"}, "summary": {"complete": false, "defs_unchanged": false,
            "symbols": {"resolved_old": 120, "ported": 119, "low_confidence": ["x"], "lost": []},
            "constants": {"resolved_old": 300, "ported": 298, "low_confidence": [], "lost": ["c1", "c2"]},
            "features": {"lost": ["time.weather"]}, "strategies_changed": ["a", "b"], "unverified_fixed": ["f"]}}"#;
        let r = parse_report(j, PathBuf::from("/o")).unwrap();
        assert_eq!((r.symbols, r.constants), ((119, 120), (298, 300)));
        assert_eq!(r.low, ["x"]);
        assert_eq!(r.lost, ["c1", "c2"]);
        assert_eq!(r.changed, 2);
        assert!(!r.complete && !r.defs_unchanged);
        assert_eq!(r.record(), PathBuf::from("/o/builds/server/ab.json"));
        assert!(parse_report("{", PathBuf::new()).is_err());
    }
}
