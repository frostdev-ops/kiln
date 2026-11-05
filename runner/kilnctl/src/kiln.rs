// SPDX-License-Identifier: MIT
//! Kiln in a server folder: deploy (tools/deploy-server.sh), consistency checks, the offline sigdb check.
use crate::config::{Instance, Policy};
use crate::preflight::Check;
use crate::util;
use crate::wine;
use anyhow::{Context, Result, bail};
use std::collections::BTreeMap;
use std::path::{Path, PathBuf};
use std::process::Command;

/// Per-instance files that are never copied between installs.
pub const INSTANCE_FILES: &[&str] = &["kiln", "savegame", "logs", "dinput8.dll", "enshrouded_server.json", "backup"];

pub fn kiln_dir(inst: &Instance) -> PathBuf {
    inst.dir.join("kiln")
}

pub fn is_deployed(inst: &Instance) -> bool {
    inst.dir.join("dinput8.dll").exists() || kiln_dir(inst).exists()
}

/// Files `deploy` needs from a build folder (KILN_OUT) when it does not build.
fn missing_build_files(out: &Path) -> Vec<PathBuf> {
    ["cmake/bin/dinput8.dll", "cmake/bin/kiln-core.dll", "cmake/bin/kiln-core.pdb", "host/Kiln.Host.dll", "host/Kiln.Api.dll"]
        .iter()
        .map(|f| out.join(f))
        .filter(|p| !p.exists())
        .collect()
}

/// Lays Kiln out with the repository's tools/deploy-server.sh (the one definition of the layout).
pub fn deploy(inst: &Instance, from: Option<&Path>, samples: bool, build: bool) -> Result<()> {
    let script = inst.kiln.source.join("tools/deploy-server.sh");
    if !script.exists() {
        bail!("{} not found (set kiln.source in {})", script.display(), inst.file.display());
    }
    let out = from.map(Path::to_path_buf).unwrap_or_else(|| inst.kiln.build.clone());
    if !build {
        let missing = missing_build_files(&out);
        if !missing.is_empty() {
            bail!(
                "Kiln build {} is incomplete (missing {}); build it or pass --build",
                out.display(),
                missing.iter().map(|p| p.display().to_string()).collect::<Vec<_>>().join(", ")
            );
        }
    }
    // deploy-server.sh rewrites the whole kiln.json when given --policy; pass it only for a fresh folder and set the
    // policy of an existing kiln.json in place, so hand edits (log_level, clr, ...) survive a redeploy.
    let kiln_json = kiln_dir(inst).join("kiln.json");
    let fresh = !kiln_json.exists();
    let mut args: Vec<String> = vec![inst.dir.display().to_string()];
    if fresh {
        args.extend(["--policy".into(), inst.kiln.policy.as_str().into()]);
    }
    if samples || inst.kiln.samples {
        args.push("--samples".into());
    }
    if !build {
        args.push("--no-build".into());
    }
    println!("KILN_OUT={} {} {}", out.display(), script.display(), args.join(" "));
    util::run_status(Command::new(&script).args(&args).env("KILN_OUT", &out), "tools/deploy-server.sh")?;
    if !fresh {
        let text = std::fs::read_to_string(&kiln_json)?;
        if let Some(new) = set_policy(&text, inst.kiln.policy.as_str())? {
            std::fs::write(&kiln_json, new)?;
            println!("kiln/kiln.json: policy set to {}", inst.kiln.policy.as_str());
        }
    }
    Ok(())
}

/// kiln.json with `policy` set; None when it already has it. Keeps the file's layout when the key is written the way
/// deploy-server.sh writes it.
pub fn set_policy(text: &str, policy: &str) -> Result<Option<String>> {
    let mut v: serde_json::Value = serde_json::from_str(text).context("kiln/kiln.json is not valid JSON")?;
    let o = v.as_object_mut().context("kiln/kiln.json is not a JSON object")?;
    let old = o.get("policy").and_then(|p| p.as_str()).map(str::to_string);
    if old.as_deref() == Some(policy) {
        return Ok(None);
    }
    if let Some(old) = &old {
        let pat = format!("\"policy\": \"{old}\"");
        if text.matches(&pat).count() == 1 {
            return Ok(Some(text.replace(&pat, &format!("\"policy\": \"{policy}\""))));
        }
    }
    o.insert("policy".into(), serde_json::Value::String(policy.into()));
    Ok(Some(serde_json::to_string_pretty(&v)? + "\n"))
}

fn json_file(p: &Path) -> Result<serde_json::Value> {
    let t = std::fs::read_to_string(p).with_context(|| format!("reading {}", p.display()))?;
    serde_json::from_str(&t).with_context(|| format!("{} is not valid JSON", p.display()))
}

fn same_bytes(a: &Path, b: &Path) -> Option<bool> {
    let (ma, mb) = (a.metadata().ok()?, b.metadata().ok()?);
    if ma.len() != mb.len() {
        return Some(false);
    }
    Some(std::fs::read(a).ok()? == std::fs::read(b).ok()?)
}

/// `kiln/sigdb/builds/server/<sha256>.json` says `"verified": true`.
pub fn verified(inst: &Instance, sha: &str) -> Option<bool> {
    let rec = kiln_dir(inst).join(format!("sigdb/builds/server/{sha}.json"));
    if !rec.exists() {
        return None;
    }
    Some(json_file(&rec).ok().and_then(|v| v.get("verified")?.as_bool()).unwrap_or(false))
}

/// Preflight checks for Kiln's files (a vanilla folder is a warning, a half-deployed one a failure).
pub fn checks(inst: &Instance, exe_sha: Option<&str>, out: &mut Vec<Check>) {
    let k = kiln_dir(inst);
    let proxy = inst.dir.join("dinput8.dll");
    match (proxy.exists(), k.exists()) {
        (false, false) => {
            out.push(Check::warn("kiln", format!("not deployed: the server runs vanilla (kilnctl deploy {})", inst.name)));
            return;
        }
        (true, false) => {
            out.push(Check::fail("kiln", "dinput8.dll without kiln/: the proxy has nothing to load (redeploy)"));
            return;
        }
        (false, true) => {
            out.push(Check::fail("kiln", "kiln/ without dinput8.dll next to the exe: Kiln never loads (redeploy)"));
            return;
        }
        (true, true) => {}
    }
    let mut problems = Vec::new();
    for f in ["kiln-core.dll", "sigdb/server.json", "kiln.json", "host/Kiln.Host.dll", "host/Kiln.Api.dll",
        "dotnet/host/fxr", "dotnet/shared/Microsoft.NETCore.App"] {
        if !k.join(f).exists() {
            problems.push(format!("kiln/{f} missing"));
        }
    }
    let mut policy = None;
    if let Ok(v) = json_file(&k.join("kiln.json")) {
        match v.get("policy").and_then(|p| p.as_str()) {
            Some(p @ ("verified-only" | "any")) => policy = Some(p.to_string()),
            None => policy = Some("verified-only".into()),
            Some(p) => problems.push(format!("kiln/kiln.json: unknown policy {p:?}")),
        }
    } else if k.join("kiln.json").exists() {
        problems.push("kiln/kiln.json is not valid JSON".into());
    }
    if k.join("sigdb/server.json").exists() {
        match json_file(&k.join("sigdb/server.json")) {
            Ok(v) if v.get("symbols").is_some_and(|s| s.is_object()) => {}
            Ok(_) => problems.push("kiln/sigdb/server.json has no symbols".into()),
            Err(_) => problems.push("kiln/sigdb/server.json is not valid JSON".into()),
        }
    }
    for d in ["sigdb/server.d", "sigdb/builds/server"] {
        for e in std::fs::read_dir(k.join(d)).into_iter().flatten().flatten() {
            if e.path().extension().is_some_and(|x| x == "json") && json_file(&e.path()).is_err() {
                problems.push(format!("kiln/{d}/{} is not valid JSON", e.file_name().to_string_lossy()));
            }
        }
    }
    let mut mods = 0;
    for e in std::fs::read_dir(k.join("mods")).into_iter().flatten().flatten() {
        if !e.path().is_dir() {
            continue;
        }
        mods += 1;
        let m = e.path().join("kiln.mod.json");
        if json_file(&m).is_err() {
            out.push(Check::warn("kiln", format!("mods/{}: kiln.mod.json missing or invalid; Kiln skips it", e.file_name().to_string_lossy())));
        }
    }
    let overrides = wine::effective(inst, "WINEDLLOVERRIDES").unwrap_or_default();
    if !wine::dinput8_native_first(&overrides) {
        problems.push(format!("WINEDLLOVERRIDES {overrides:?} does not load dinput8 native first: Kiln's proxy would not load"));
    }
    if !problems.is_empty() {
        out.push(Check::fail("kiln", problems.join("; ")));
        return;
    }
    let plugins = std::fs::read_dir(k.join("plugins")).into_iter().flatten().flatten().count();
    let services = std::fs::read_dir(k.join("services")).into_iter().flatten().flatten()
        .filter(|e| e.path().extension().is_some_and(|x| x == "dll")).count();
    let policy = policy.unwrap_or_else(|| "verified-only".into());
    out.push(Check::ok("kiln", format!(
        "deployed: dinput8.dll, kiln-core.dll, sigdb, host, dotnet; {services} service(s), {plugins} plugin(s), {mods} mod(s); policy {policy}")));
    if policy != inst.kiln.policy.as_str() {
        out.push(Check::warn("kiln", format!(
            "kiln/kiln.json has policy {policy}, the instance says {} (kilnctl deploy sets it)", inst.kiln.policy.as_str())));
    }
    let bin = inst.kiln.build.join("cmake/bin");
    let stale: Vec<&str> = [(proxy.clone(), bin.join("dinput8.dll"), "dinput8.dll"),
        (k.join("kiln-core.dll"), bin.join("kiln-core.dll"), "kiln-core.dll"),
        (k.join("host/Kiln.Host.dll"), inst.kiln.build.join("host/Kiln.Host.dll"), "Kiln.Host.dll")]
        .iter()
        .filter(|(d, b, _)| same_bytes(d, b) == Some(false))
        .map(|(_, _, n)| *n)
        .collect();
    if !stale.is_empty() {
        out.push(Check::warn("kiln", format!(
            "{} differ from the build in {} (kilnctl deploy {} to update)", stale.join(", "), inst.kiln.build.display(), inst.name)));
    }
    if let Some(sha) = exe_sha {
        let short = &sha[..12];
        match (verified(inst, sha), policy.as_str()) {
            (Some(true), _) => out.push(Check::ok("kiln", format!("build {short}… verified"))),
            (_, "any") => out.push(Check::ok("kiln", format!("build {short}… unverified; policy any installs hooks anyway"))),
            _ => out.push(Check::warn("kiln", format!(
                "build {short}… has no verified record: Kiln boots but stays idle (policy verified-only)"))),
        }
    }
}

pub struct SigdbReport {
    pub features: BTreeMap<String, bool>,
    pub symbols_ok: usize,
    pub symbols: usize,
    pub constants_ok: usize,
    pub constants: usize,
    pub failed: Vec<String>,
    pub json: PathBuf,
}

impl SigdbReport {
    pub fn off(&self) -> Vec<&str> {
        self.features.iter().filter(|(_, on)| !**on).map(|(n, _)| n.as_str()).collect()
    }
}

/// Parses `sigdb_check.py --json` output.
pub fn parse_sigdb_json(text: &str, json: PathBuf) -> Result<SigdbReport> {
    let v: serde_json::Value = serde_json::from_str(text).context("sigdb_check.py --json output")?;
    let mut r = SigdbReport { features: BTreeMap::new(), symbols_ok: 0, symbols: 0, constants_ok: 0, constants: 0, failed: vec![], json };
    for (n, s) in v.get("symbols").and_then(|s| s.as_object()).into_iter().flatten() {
        if n.starts_with('@') {
            continue; // probes
        }
        r.symbols += 1;
        if s.get("ok").and_then(|o| o.as_bool()) == Some(true) {
            r.symbols_ok += 1;
        } else {
            r.failed.push(format!("{n}: {}", s.get("error").and_then(|e| e.as_str()).unwrap_or("unresolved")));
        }
    }
    for (n, c) in v.get("constants").and_then(|s| s.as_object()).into_iter().flatten() {
        r.constants += 1;
        if c.get("ok").and_then(|o| o.as_bool()) == Some(true) {
            r.constants_ok += 1;
        } else {
            r.failed.push(format!("{n}: {}", c.get("error").and_then(|e| e.as_str()).unwrap_or("unresolved")));
        }
    }
    for (n, on) in v.get("features").and_then(|s| s.as_object()).into_iter().flatten() {
        r.features.insert(n.clone(), on.as_bool().unwrap_or(false));
    }
    Ok(r)
}

/// Runs tools/sigport/sigdb_check.py on `exe` with the deployed definitions (or the source tree's when Kiln is not
/// deployed). Read-only: never writes a build record.
pub fn sigdb_check(inst: &Instance, exe: &Path) -> Result<SigdbReport> {
    let tool = inst.kiln.source.join("tools/sigport/sigdb_check.py");
    if !tool.exists() {
        bail!("{} not found (kiln.source)", tool.display());
    }
    let deployed = kiln_dir(inst).join("sigdb/server.json");
    let defs = if deployed.exists() { deployed } else { inst.kiln.source.join("sigdb/symbols/server.json") };
    let json = util::state_dir().join(format!("{}.sigdb.json", inst.name));
    std::fs::create_dir_all(util::state_dir())?;
    let _ = std::fs::remove_file(&json);
    let out = Command::new("python3")
        .arg("-I")
        .arg(&tool)
        .arg(&defs)
        .arg(exe)
        .arg("--json")
        .arg(&json)
        .output()
        .context("running python3 tools/sigport/sigdb_check.py")?;
    let text = std::fs::read_to_string(&json).with_context(|| {
        format!("sigdb_check.py wrote no report ({}): {}", out.status, String::from_utf8_lossy(&out.stderr).trim())
    })?;
    parse_sigdb_json(&text, json)
}

pub fn policy_note(policy: Policy, verified: Option<bool>) -> &'static str {
    match (verified, policy) {
        (Some(true), _) => "verified record present: Kiln installs its hooks",
        (_, Policy::Any) => "no verified record; policy any: Kiln installs its hooks anyway",
        (_, Policy::VerifiedOnly) => {
            "no verified record: with policy verified-only Kiln boots but stays idle on this build until someone \
             tests it in a lab and adds kiln/sigdb/builds/server/<sha256>.json with \"verified\": true"
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn sigdb_report() {
        let j = r#"{"symbols":{"SimStep":{"ok":true,"rva":1},"@SimStep.fields":{"ok":false,"error":"x"},
            "GameLog":{"ok":false,"error":"find[0]: 0 matches"}},
            "constants":{"world.storage":{"ok":true,"value":1},"storage.kind":{"ok":false,"error":"0 captures, need 1"}},
            "features":{"tick":false,"log.mirror":true,"log.write":false}}"#;
        let r = parse_sigdb_json(j, PathBuf::from("/x.json")).unwrap();
        assert_eq!((r.symbols_ok, r.symbols), (1, 2));
        assert_eq!((r.constants_ok, r.constants), (1, 2));
        assert_eq!(r.off(), vec!["log.write", "tick"]);
        assert_eq!(r.failed.len(), 2);
        assert!(r.failed[0].starts_with("GameLog: find[0]"));
        assert!(parse_sigdb_json("nope", PathBuf::new()).is_err());
    }

    #[test]
    fn kiln_json_policy() {
        let t = "{\n  \"policy\": \"verified-only\",\n  \"clr\": true,\n  \"plugins\": true,\n  \"log_level\": \"debug\"\n}\n";
        assert_eq!(set_policy(t, "verified-only").unwrap(), None);
        let n = set_policy(t, "any").unwrap().unwrap();
        assert_eq!(n, t.replace("verified-only", "any"));
        let compact = r#"{"clr":false,"policy":"any"}"#;
        let n: serde_json::Value = serde_json::from_str(&set_policy(compact, "verified-only").unwrap().unwrap()).unwrap();
        assert_eq!(n["policy"], "verified-only");
        assert_eq!(n["clr"], false);
        let n: serde_json::Value = serde_json::from_str(&set_policy("{\"clr\": true}", "any").unwrap().unwrap()).unwrap();
        assert_eq!(n["policy"], "any");
        assert!(set_policy("nope", "any").is_err());
    }
}
