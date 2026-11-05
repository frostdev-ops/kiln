// SPDX-License-Identifier: MIT
//! The lab gate: boot a copy of an instance with policy `any` and Kiln's self tests (mods/tests/*), and compare the
//! results with a baseline run on a verified build. Used by `kilnctl port` / `kilnctl update` before a build is
//! marked verified, and by `kilnctl gate` (docs/kiln/sigport.md).
//!
//! The copy is the sibling instance `<name>-gate`: `<dir>.gate` (game files reflinked from the instance, the
//! instance's deployed `kiln/` folder and savegame copied, so it runs exactly the Kiln the instance would run) and
//! `<dir>.gate.pfx` (reflinked from the instance's prefix once). Its query port is the instance's + 1 unless given.
//! The instance itself is only read.
use crate::config::{self, InitSpec, Instance, Policy};
use crate::{kiln, ops, procs, server, util};
use anyhow::{Context, Result, bail};
use serde::{Deserialize, Serialize};
use std::collections::{BTreeMap, BTreeSet};
use std::path::{Path, PathBuf};
use std::process::{Command, Stdio};
use std::time::{Duration, Instant, SystemTime};

/// Self tests that run unattended on a dedicated server without a player (one boot).
pub const DEFAULT_TESTS: &[&str] = &[
    "world-test", "players-test", "chat-test", "items-test", "settings-test", "gameplay-test", "hooks-test",
    "combat-test", "spawn-test", "content-test",
];

#[derive(Debug, Clone)]
pub struct GateOpts {
    pub tests: Vec<String>,
    pub port: Option<u16>,
    pub boot_timeout: Duration,
    pub test_timeout: Duration,
    /// Replaces the copy's exe (dry runs against an exe the instance does not have yet).
    pub exe: Option<PathBuf>,
    /// Candidate definitions (a sigport output tree: server.json + server.d/).
    pub sigdb_tree: Option<PathBuf>,
    /// Candidate build record copied into the copy's kiln/sigdb/builds/server/.
    pub record: Option<PathBuf>,
    /// Keep the instance's own mods and native plugins in the copy. Off by default: a mod such as Kiln.Admin may
    /// talk to something outside the server folder (the dashboard's bridge folder), which the copy must not touch.
    pub instance_mods: bool,
}

#[derive(Debug, Clone, Default, Serialize, Deserialize, PartialEq)]
pub struct TestResult {
    pub summary: bool,
    pub pass: u64,
    pub fail: u64,
    /// FAIL lines, digits replaced by '#' (entity ids and counts vary between runs).
    pub fails: Vec<String>,
}

#[derive(Debug, Clone, Default, Serialize, Deserialize)]
pub struct GateResult {
    pub instance: String,
    pub sha256: String,
    pub date: String,
    pub kiln_active: bool,
    pub sigdb: String,
    pub tests: BTreeMap<String, TestResult>,
    pub green: bool,
    pub reasons: Vec<String>,
    pub baseline: Option<String>,
}

pub fn gate_name(inst: &Instance) -> String {
    format!("{}-gate", inst.name)
}

fn results_dir(inst: &Instance) -> PathBuf {
    util::state_dir().join("gate").join(&inst.name)
}

pub fn baseline_path(inst: &Instance) -> PathBuf {
    results_dir(inst).join("baseline.json")
}

pub fn load_baseline(inst: &Instance) -> Option<GateResult> {
    serde_json::from_str(&std::fs::read_to_string(baseline_path(inst)).ok()?).ok()
}

// ---- log parsing ---------------------------------------------------------------------------------------------------

/// `(pass, fail)` of a summary line: "N PASS, M FAIL", "N passed, M failed", "PASS N FAIL M".
fn summary_counts(text: &str) -> Option<(u64, u64)> {
    let w: Vec<&str> = text.split(|c: char| c.is_whitespace() || c == ',' || c == ';' || c == ':' || c == '(' || c == ')')
        .filter(|s| !s.is_empty())
        .collect();
    let num = |s: &str| s.parse::<u64>().ok();
    let is_pass = |s: &str| s == "PASS" || s == "passed";
    let is_fail = |s: &str| s == "FAIL" || s == "failed";
    for i in 0..w.len().saturating_sub(3) {
        if let (Some(p), true, Some(f), true) = (num(w[i]), is_pass(w[i + 1]), num(w[i + 2]), is_fail(w[i + 3])) {
            return Some((p, f));
        }
        if let (true, Some(p), true, Some(f)) = (w[i] == "PASS", num(w[i + 1]), w[i + 2] == "FAIL", num(w[i + 3])) {
            return Some((p, f));
        }
    }
    None
}

fn normalise(s: &str) -> String {
    s.chars().map(|c| if c.is_ascii_digit() { '#' } else { c }).collect()
}

/// Kiln active (SimStep hooked), the sigdb boot line, and each test's results from kiln/logs/kiln.log.
pub fn parse_log(text: &str, tests: &[String]) -> (bool, String, BTreeMap<String, TestResult>) {
    let mut out: BTreeMap<String, TestResult> = tests.iter().map(|t| (t.clone(), TestResult::default())).collect();
    let mut active = false;
    let mut sigdb = String::new();
    for line in text.lines() {
        let line = line.trim_end_matches('\r');
        if line.contains("[kiln] tick: SimStep hooked") {
            active = true;
        }
        if let Some(i) = line.find("[kiln] sigdb: ") {
            sigdb = line[i + 14..].to_string();
        }
        let Some(i) = line.find("[mod:") else { continue };
        let rest = &line[i + 5..];
        let Some(j) = rest.find(']') else { continue };
        let Some(r) = out.get_mut(&rest[..j]) else { continue };
        let msg = rest[j + 1..].trim();
        if let Some((p, f)) = summary_counts(msg) {
            r.summary = true;
            r.pass = p;
            r.fail = f;
        } else if msg.split_whitespace().any(|w| w == "FAIL" || w == "FAIL:") {
            let n = normalise(msg);
            if !r.fails.contains(&n) {
                r.fails.push(n);
            }
        }
    }
    (active, sigdb, out)
}

/// Green: Kiln active, every test summarised, and no test fails more (or other checks) than in the baseline; without a
/// baseline, no test may fail at all.
pub fn verdict(r: &GateResult, baseline: Option<&GateResult>) -> (bool, Vec<String>) {
    let mut why = Vec::new();
    if !r.kiln_active {
        why.push("Kiln did not hook SimStep (sigdb or policy problem; see kiln/logs/kiln.log of the gate)".into());
    }
    for (t, x) in &r.tests {
        if !x.summary {
            why.push(format!("{t}: no summary line (did not finish, or did not load)"));
            continue;
        }
        match baseline.and_then(|b| b.tests.get(t)) {
            Some(b) if b.summary => {
                let known: BTreeSet<&String> = b.fails.iter().collect();
                let new: Vec<&String> = x.fails.iter().filter(|f| !known.contains(f)).collect();
                if x.fail > b.fail || !new.is_empty() {
                    why.push(format!("{t}: {} FAIL (baseline {}){}", x.fail, b.fail,
                        if new.is_empty() { String::new() } else { format!("; new: {}", new.iter().map(|s| s.as_str()).collect::<Vec<_>>().join(" | ")) }));
                }
            }
            _ if x.fail > 0 => why.push(format!("{t}: {} FAIL and no baseline for this test", x.fail)),
            _ => {}
        }
    }
    (why.is_empty(), why)
}

// ---- the copy ------------------------------------------------------------------------------------------------------

fn cp(args: &[&std::ffi::OsStr]) -> Result<()> {
    util::run_status(Command::new("cp").args(["-a", "--reflink=auto"]).args(args), "cp --reflink=auto")
}

fn save_dir(inst: &Instance) -> PathBuf {
    let cfg = server::read_config(&inst.server_json()).ok();
    let dir = cfg.map(|c| c.save_dir).unwrap_or_else(|| "./savegame".into());
    server::host_path(&inst.dir, &inst.prefix, &dir)
}

fn random_password() -> Result<String> {
    use std::io::Read;
    let mut b = [0u8; 12];
    std::fs::File::open("/dev/urandom")?.read_exact(&mut b)?;
    Ok(b.iter().map(|x| format!("{x:02x}")).collect())
}

fn toml_str(s: &str) -> String {
    toml::Value::String(s.to_string()).to_string()
}

/// Builds one test mod (dotnet, Release) into <state>/gate-tests/<test>.
fn build_test(inst: &Instance, test: &str) -> Result<PathBuf> {
    let src = inst.kiln.source.join("mods/tests").join(test);
    let csproj = std::fs::read_dir(&src)
        .with_context(|| format!("no test mod {}", src.display()))?
        .flatten()
        .map(|e| e.path())
        .find(|p| p.extension().is_some_and(|x| x == "csproj"))
        .with_context(|| format!("{} has no .csproj", src.display()))?;
    let root = util::state_dir().join("gate-tests");
    let out = root.join(test);
    let st = Command::new("dotnet")
        .arg("build")
        .arg(&csproj)
        .args(["-c", "Release", "-v", "q", "-nologo", "-o"])
        .arg(&out)
        .arg("--artifacts-path")
        .arg(root.join(".dotnet"))
        .stdout(Stdio::null())
        .status()
        .context("running dotnet build")?;
    if !st.success() {
        bail!("dotnet build {} failed ({st})", csproj.display());
    }
    Ok(out)
}

/// A built mod folder into kiln/mods/<id>/ without the assemblies kiln/host provides (deploy-server.sh's rule).
fn install_mod(built: &Path, dst: &Path) -> Result<()> {
    std::fs::create_dir_all(dst)?;
    for e in std::fs::read_dir(built)?.flatten() {
        let n = e.file_name().to_string_lossy().into_owned();
        if n.starts_with("Kiln.") || n.ends_with(".deps.json") || n.ends_with(".runtimeconfig.json") {
            continue;
        }
        cp(&[e.path().as_os_str(), dst.as_os_str()])?;
    }
    Ok(())
}

/// Lays out `<dir>.gate` and writes the `<name>-gate` instance file. The instance is only read.
pub fn prepare(inst: &Instance, o: &GateOpts) -> Result<Instance> {
    if !kiln::is_deployed(inst) {
        bail!("{} has no Kiln deployed; the gate boots the instance's own kiln/ folder", inst.name);
    }
    let name = gate_name(inst);
    config::validate_name(&name)?;
    let gdir = util::with_suffix(&inst.dir, ".gate");
    let gpfx = util::with_suffix(&inst.dir, ".gate.pfx");
    let port = o.port.unwrap_or(inst.query_port.saturating_add(1));
    let file = config::instance_file(&name);
    let text = config::render(&InitSpec {
        name: &name,
        dir: &gdir,
        prefix: &gpfx,
        wine: &inst.wine,
        port,
        backup_dir: &util::with_suffix(&gdir, ".backups"),
        source: &inst.kiln.source,
        build: &inst.kiln.build,
        policy: Policy::Any,
        samples: false,
    });
    let mut text = text.replacen("# kilnctl instance", "# kilnctl gate copy (written by kilnctl port/gate; regenerated each run) of an instance", 1);
    for (k, v) in &inst.env {
        text.push_str(&format!("{} = {}\n", toml_str(k), toml_str(v)));
    }
    let g = config::parse(&name, &file, &text, &util::home())?;
    if let Some(p) = procs::find_server(&g.prefix) {
        println!("gate: {name} is still running (pid {p}); stopping it");
        ops::stop(&g, None)?;
    }
    if procs::find_server(&inst.prefix).is_some() {
        println!("gate: note: {} is running; its save is copied as it is now", inst.name);
    }
    if gdir.exists() {
        std::fs::remove_dir_all(&gdir).with_context(|| format!("removing the old copy {}", gdir.display()))?;
    }
    std::fs::create_dir_all(&gdir)?;
    let mut entries: Vec<PathBuf> = std::fs::read_dir(&inst.dir)?
        .flatten()
        .filter(|e| !kiln::INSTANCE_FILES.contains(&e.file_name().to_string_lossy().as_ref()))
        .map(|e| e.path())
        .collect();
    entries.sort();
    entries.push(inst.dir.join("kiln"));
    if inst.dir.join("dinput8.dll").exists() {
        entries.push(inst.dir.join("dinput8.dll"));
    }
    let mut args: Vec<&std::ffi::OsStr> = entries.iter().map(|p| p.as_os_str()).collect();
    args.push(gdir.as_os_str());
    cp(&args)?;
    let mut clean = vec!["kiln/logs", "kiln/control"];
    if !o.instance_mods {
        clean.extend(["kiln/mods", "kiln/plugins"]);
    }
    for d in clean {
        let p = gdir.join(d);
        let _ = std::fs::remove_dir_all(&p);
        std::fs::create_dir_all(&p)?;
    }
    let save = save_dir(inst);
    if save.is_dir() {
        cp(&[save.as_os_str(), gdir.join("savegame").as_os_str()])?;
    }
    let mut cfg: serde_json::Value = serde_json::from_str(&std::fs::read_to_string(inst.server_json())?)
        .context("the instance's enshrouded_server.json")?;
    cfg["name"] = format!("Kiln gate {}", inst.name).into();
    cfg["queryPort"] = port.into();
    cfg["saveDirectory"] = "./savegame".into();
    cfg["logDirectory"] = "./logs".into();
    // the copy is listed like any server for a minute: nobody who knows the instance's passwords may join it
    if let Some(groups) = cfg.get_mut("userGroups").and_then(|g| g.as_array_mut()) {
        for g in groups {
            if g.get("password").is_some() {
                g["password"] = random_password()?.into();
            }
        }
    }
    std::fs::write(g.server_json(), serde_json::to_string_pretty(&cfg)? + "\n")?;
    if !gpfx.join("system.reg").exists() {
        println!("gate: copying the prefix {} -> {}", inst.prefix.display(), gpfx.display());
        let _ = std::fs::remove_dir_all(&gpfx);
        cp(&[inst.prefix.as_os_str(), gpfx.as_os_str()])?;
    }
    let kj = gdir.join("kiln/kiln.json");
    if let Some(t) = kiln::set_policy(&std::fs::read_to_string(&kj)?, "any")? {
        std::fs::write(&kj, t)?;
    }
    if let Some(exe) = &o.exe {
        std::fs::remove_file(g.exe())?;
        cp(&[exe.as_os_str(), g.exe().as_os_str()])?;
    }
    if let Some(tree) = &o.sigdb_tree {
        let sd = gdir.join("kiln/sigdb");
        std::fs::copy(tree.join("server.json"), sd.join("server.json"))?;
        let _ = std::fs::remove_dir_all(sd.join("server.d"));
        std::fs::create_dir_all(sd.join("server.d"))?;
        for e in std::fs::read_dir(tree.join("server.d"))?.flatten() {
            std::fs::copy(e.path(), sd.join("server.d").join(e.file_name()))?;
        }
    }
    if let Some(rec) = &o.record {
        let dst = gdir.join("kiln/sigdb/builds/server");
        std::fs::create_dir_all(&dst)?;
        std::fs::copy(rec, dst.join(rec.file_name().context("record name")?))?;
    }
    for t in &o.tests {
        let built = build_test(inst, t)?;
        install_mod(&built, &gdir.join("kiln/mods").join(t))?;
    }
    std::fs::create_dir_all(config::instances_dir())?;
    std::fs::write(&file, &text)?;
    println!("gate: {name} prepared: {} (UDP {port}), {} test mod(s){}{}{}", gdir.display(), o.tests.len(),
        if o.instance_mods { " + the instance's mods" } else { "" },
        if o.sigdb_tree.is_some() { ", candidate definitions" } else { "" },
        if o.exe.is_some() { ", exe replaced" } else { "" });
    Ok(g)
}

/// Prepares the copy, starts it, waits for the tests' summaries, stops it and judges the results.
pub fn run(inst: &Instance, o: &GateOpts) -> Result<GateResult> {
    let g = prepare(inst, o)?;
    let sha = util::sha256(&g.exe())?;
    let log = g.dir.join("kiln/logs/kiln.log");
    ops::start(&g, false, o.boot_timeout).context("gate: starting the copy")?;
    let t0 = Instant::now();
    let mut last = String::new();
    loop {
        let text = server::read_log(&log).unwrap_or_default();
        let (_, _, r) = parse_log(&text, &o.tests);
        let pending: Vec<&String> = r.iter().filter(|(_, x)| !x.summary).map(|(t, _)| t).collect();
        let now = format!("{} of {} tests done", r.len() - pending.len(), r.len());
        if now != last {
            println!("gate: {now}");
            last = now;
        }
        if pending.is_empty() || t0.elapsed() > o.test_timeout || procs::find_server(&g.prefix).is_none() {
            break;
        }
        std::thread::sleep(Duration::from_secs(2));
    }
    let stopped = ops::stop(&g, None);
    let text = server::read_log(&log).unwrap_or_default();
    let (active, sigdb, tests) = parse_log(&text, &o.tests);
    let mut r = GateResult {
        instance: inst.name.clone(),
        sha256: sha.clone(),
        date: util::fmt_utc(SystemTime::now()),
        kiln_active: active,
        sigdb,
        tests,
        ..Default::default()
    };
    let base = load_baseline(inst);
    let (green, mut why) = verdict(&r, base.as_ref());
    if let Err(e) = &stopped {
        why.push(format!("the copy did not stop cleanly: {e:#}"));
    }
    r.green = green && stopped.is_ok();
    r.reasons = why;
    r.baseline = base.map(|b| format!("{} ({})", &b.sha256[..12.min(b.sha256.len())], b.date));
    std::fs::create_dir_all(results_dir(inst))?;
    let file = results_dir(inst).join(format!("{}-{}.json", &sha[..12], util::stamp(SystemTime::now())));
    std::fs::write(&file, serde_json::to_string_pretty(&r)? + "\n")?;
    print(&r);
    println!("gate: results {}", file.display());
    Ok(r)
}

pub fn print(r: &GateResult) {
    println!("gate: Kiln {}; sigdb {}", if r.kiln_active { "active (SimStep hooked)" } else { "NOT active" },
        if r.sigdb.is_empty() { "?" } else { &r.sigdb });
    for (t, x) in &r.tests {
        if x.summary {
            println!("gate:   {t:16} {:3} PASS {:3} FAIL", x.pass, x.fail);
        } else {
            println!("gate:   {t:16} no summary");
        }
    }
    println!("gate: {}{}", if r.green { "GREEN" } else { "RED" },
        r.baseline.as_ref().map(|b| format!(" (baseline {b})")).unwrap_or_else(|| " (no baseline: every FAIL counts)".into()));
    for w in &r.reasons {
        println!("gate:   {w}");
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    const LOG: &str = "[I 01:35:15.423] [kiln] tick: SimStep hooked (world.storage=0xe179c0)\n\
[I 01:35:15.421] [kiln] sigdb: 185/185 symbols, 303/303 constants resolved in 323.7 ms\n\
[I 01:35:37.308] [mod:settings-test] FAIL day/night: clock moved 12 s, want 10\n\
[I 01:35:37.308] [mod:settings-test] SUMMARY settings-test: 41 PASS, 1 FAIL\n\
[I 01:35:46.384] [mod:spawn-test] spawn-test: INFO summary: 42 PASS, 1 FAIL\n\
[I 01:35:46.384] [mod:spawn-test] spawn-test: FAIL entity 1127 not found\n\
[I 01:35:47.959] [mod:gameplay-test] gameplay-test: done, 48 PASS, 0 FAIL\r\n\
[I 01:35:48.000] [mod:world-test] world-test: PASS status\n\
[I 01:42:32.165] [mod:net-test] net-test: PASS 7 FAIL 0\n\
[I 01:42:32.165] [mod:chat-test] chat-test: 50 passed, 0 failed\n";

    fn tests() -> Vec<String> {
        ["settings-test", "spawn-test", "gameplay-test", "world-test", "net-test", "chat-test"].map(String::from).to_vec()
    }

    #[test]
    fn parses_summaries_and_fails() {
        let (active, sigdb, r) = parse_log(LOG, &tests());
        assert!(active);
        assert!(sigdb.starts_with("185/185 symbols"));
        assert_eq!((r["settings-test"].pass, r["settings-test"].fail), (41, 1));
        assert_eq!(r["settings-test"].fails, ["FAIL day/night: clock moved ## s, want ##"]);
        assert_eq!(r["spawn-test"].fails, ["spawn-test: FAIL entity #### not found"]);
        assert_eq!((r["gameplay-test"].pass, r["gameplay-test"].summary), (48, true));
        assert!(!r["world-test"].summary);
        assert_eq!((r["net-test"].pass, r["net-test"].fail), (7, 0));
        assert_eq!((r["chat-test"].pass, r["chat-test"].fail), (50, 0));
    }

    #[test]
    fn verdict_against_a_baseline() {
        let (active, sigdb, tests) = parse_log(LOG, &tests());
        let mut r = GateResult { kiln_active: active, sigdb, tests, ..Default::default() };
        r.tests.remove("world-test");
        // no baseline: known failures count
        let (green, why) = verdict(&r, None);
        assert!(!green);
        assert_eq!(why.len(), 2, "{why:?}");
        // the same failures in the baseline: green
        let base = r.clone();
        assert!(verdict(&r, Some(&base)).0);
        // a new failure is a regression even at the same count
        let mut worse = r.clone();
        worse.tests.get_mut("spawn-test").unwrap().fails = vec!["spawn-test: FAIL other".into()];
        let (g2, why2) = verdict(&worse, Some(&base));
        assert!(!g2 && why2[0].contains("new: spawn-test: FAIL other"), "{why2:?}");
        // a missing summary and an idle Kiln are red
        let mut idle = r.clone();
        idle.kiln_active = false;
        idle.tests.get_mut("chat-test").unwrap().summary = false;
        assert_eq!(verdict(&idle, Some(&base)).1.len(), 2);
    }
}
