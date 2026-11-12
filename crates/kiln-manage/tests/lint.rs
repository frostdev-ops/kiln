//! kiln lint: the schemas against the real mods and the real readers, fixtures with expected findings, the output
//! contract (schema/fixtures/lint/), and cross-checks with ajv and the Python cook tools when they are installed.

use kiln_manage::lint::{self, LintOptions, LintReport, Severity};
use serde_json::Value;
use std::path::{Path, PathBuf};

fn repo() -> PathBuf {
    Path::new(env!("CARGO_MANIFEST_DIR")).join("../..").canonicalize().unwrap()
}

fn opts() -> LintOptions {
    LintOptions { no_catalog: true, ..Default::default() }
}

fn run(paths: &[PathBuf], o: &LintOptions) -> LintReport {
    lint::lint_paths(paths, o)
}

/// Prints the report for $LINT_PATHS (colon-separated): `cargo test --test lint print -- --ignored --nocapture`.
#[test]
#[ignore]
fn print() {
    let paths: Vec<PathBuf> = std::env::var("LINT_PATHS").unwrap_or_else(|_| repo().join("mods").display().to_string()).split(':').map(PathBuf::from).collect();
    let mut o = opts();
    if let Ok(c) = std::env::var("LINT_CATALOG") {
        o.catalog = Some(c.into());
        o.no_catalog = false;
    }
    let r = run(&paths, &o);
    if std::env::var("LINT_JSON").is_ok() {
        println!("{}", r.to_json());
    } else {
        print!("{}", r.to_text());
    }
}

fn fixtures() -> PathBuf {
    repo().join("schema/fixtures/lint")
}

fn rel(p: &str, base: &Path) -> String {
    Path::new(p).strip_prefix(base).map(|x| x.to_string_lossy().replace('\\', "/")).unwrap_or_else(|_| p.to_string())
}

/// One fixture case: (file, code, severity, line, col, message) sorted, with paths relative to the case folder.
fn case_rows(case: &Path) -> (LintReport, Vec<Value>) {
    let exp: Value = serde_json::from_str(&std::fs::read_to_string(case.join("expect.json")).unwrap()).unwrap();
    let mut o = opts();
    if exp["catalog"].as_bool() == Some(true) {
        o.catalog = Some(fixtures().join("catalog/client.json"));
        o.no_catalog = false;
    }
    let r = run(&[case.to_path_buf()], &o);
    let rows = r
        .diagnostics
        .iter()
        .map(|d| serde_json::json!({"file": rel(&d.file, case), "code": d.code, "severity": d.severity.as_str(), "line": d.line, "col": d.col, "message": d.message.replace(&case.display().to_string(), "<case>")}))
        .collect();
    (r, rows)
}

/// schema/fixtures/lint/cases/*: every finding, exactly. KILN_LINT_WRITE=1 rewrites the expect.json files (review
/// the diff: it is the linter's contract).
#[test]
fn fixture_cases() {
    let mut cases: Vec<PathBuf> = std::fs::read_dir(fixtures().join("cases")).unwrap().flatten().map(|e| e.path()).filter(|p| p.join("expect.json").is_file()).collect();
    cases.sort();
    assert!(cases.len() >= 10);
    let write = std::env::var("KILN_LINT_WRITE").is_ok();
    let mut failed = Vec::new();
    for case in &cases {
        let (_, rows) = case_rows(case);
        let path = case.join("expect.json");
        let mut exp: Value = serde_json::from_str(&std::fs::read_to_string(&path).unwrap()).unwrap();
        if write {
            exp["diagnostics"] = Value::Array(rows);
            std::fs::write(&path, serde_json::to_string_pretty(&exp).unwrap() + "\n").unwrap();
        } else if exp["diagnostics"] != Value::Array(rows.clone()) {
            failed.push(format!("{}: got\n{}", case.display(), serde_json::to_string_pretty(&rows).unwrap()));
        }
    }
    assert!(failed.is_empty(), "{}", failed.join("\n"));
}

// ------------------------------------------------------------------------------------------- cross-checks ----

fn tmp(name: &str) -> PathBuf {
    let d = Path::new(env!("CARGO_TARGET_TMPDIR")).join("lint").join(name);
    let _ = std::fs::remove_dir_all(&d);
    std::fs::create_dir_all(&d).unwrap();
    d
}

fn has_tool(cmd: &str, arg: &str) -> bool {
    std::process::Command::new(cmd).arg(arg).output().is_ok_and(|o| o.status.success())
}

/// Lints `text` as if it were the file `name` (assets/cook.json, impact/natives.json, ...) outside any mod.
fn lint_as(dir: &Path, name: &str, text: &str) -> LintReport {
    let p = dir.join(name);
    std::fs::create_dir_all(p.parent().unwrap()).unwrap();
    lint::lint_text(&p, text, &opts())
}

fn refuses(r: &LintReport, extra: &[&str]) -> Vec<String> {
    r.diagnostics
        .iter()
        .filter(|d| d.severity == Severity::Error && (d.code.starts_with("KL0") || d.code.starts_with("KL1") || extra.contains(&d.code.as_str())))
        .map(|d| format!("{} {}", d.code, d.message))
        .collect()
}

fn json_files(dir: &Path, out: &mut Vec<PathBuf>) {
    for e in std::fs::read_dir(dir).into_iter().flatten().flatten() {
        let p = e.path();
        let n = e.file_name().to_string_lossy().into_owned();
        if p.is_dir() {
            if !["bin", "obj", "node_modules", "cooked"].contains(&n.as_str()) {
                json_files(&p, out);
            }
        } else if n.ends_with(".json") && n != "expect.json" {
            out.push(p);
        }
    }
}

/// The real Python readers (tools/modelcook, tools/soundcook, tools/impact) refuse exactly the cook recipes and
/// natives files kiln lint refuses: every schema/fixtures/lint/readers file, the cases' and the samples' recipes.
#[test]
fn python_readers_agree() {
    if !has_tool("python3", "--version") {
        eprintln!("skipped: no python3");
        return;
    }
    let root = repo();
    let mut files: Vec<PathBuf> = std::fs::read_dir(fixtures().join("readers")).unwrap().flatten().map(|e| e.path()).collect();
    let mut more = Vec::new();
    json_files(&root.join("mods/samples"), &mut more);
    json_files(&fixtures().join("cases"), &mut more);
    files.extend(more.into_iter().filter(|p| p.ends_with("assets/cook.json") || p.ends_with("impact/cook.json") || p.ends_with("impact/natives.json")));
    files.sort();
    let out = std::process::Command::new("python3").arg(root.join("crates/kiln-manage/tests/lint/py-check.py")).arg(&root).args(&files).output().unwrap();
    assert!(out.status.success(), "{}", String::from_utf8_lossy(&out.stderr));
    let dir = tmp("py");
    let mut bad = Vec::new();
    for (i, line) in String::from_utf8_lossy(&out.stdout).lines().enumerate() {
        let v: Value = serde_json::from_str(line).unwrap();
        let f = PathBuf::from(v["file"].as_str().unwrap());
        let text = std::fs::read_to_string(&f).unwrap_or_default();
        let name = f.file_name().unwrap().to_string_lossy().into_owned();
        let virt = if name.ends_with("natives.json") {
            "impact/natives.json"
        } else if text.contains("\"tool\": \"impact\"") || f.ends_with("impact/cook.json") {
            "impact/cook.json"
        } else {
            "assets/cook.json"
        };
        let r = lint_as(&dir.join(i.to_string()), virt, &text);
        let rust = refuses(&r, &["KL513", "KL514", "KL208"]);
        let py_ok = v["ok"].as_bool().unwrap();
        if py_ok != rust.is_empty() {
            bad.push(format!("{}: python {} ({}), kiln lint {:?}", f.display(), if py_ok { "accepts" } else { "refuses" }, v["error"], rust));
        }
    }
    assert!(files.len() > 30, "{}", files.len());
    assert!(bad.is_empty(), "{}", bad.join("\n"));
}

/// assets/player-states.json is a file kiln lint knows, in a mod and on its own: no KL903, checked against its schema.
#[test]
fn player_states_files_are_known() {
    let case = fixtures().join("cases/player-states");
    for p in [case.join("note/assets/player-states.json"), repo().join("mods/samples/PlayerStates/assets/player-states.json")] {
        let r = run(&[p.clone()], &opts());
        assert!(r.checked.iter().any(|c| Path::new(c) == p), "{} not checked:\n{}", p.display(), r.to_text());
        assert!(r.diagnostics.iter().all(|d| d.code != "KL903" && d.severity < Severity::Warning), "{}", r.to_text());
    }
    let dir = tmp("player-states");
    let r = lint_as(&dir, "assets/player-states.json", "{\"format\": 1, \"states\": {\"a\": {\"clip\": \"x\", \"like\": \"Block_FullBody\", \"speed\": 0}}}");
    assert!(r.diagnostics.iter().all(|d| d.code != "KL903"), "{}", r.to_text());
    assert!(r.diagnostics.iter().any(|d| d.code == "KL107" && d.pointer.as_deref() == Some("/states/a/speed")), "{}", r.to_text());
}

/// The player-states reader (tools/animcook/anicook/playerstates.py; native/services/playeranim/states.cpp ports it) refuses
/// exactly the player-states files kiln lint refuses: the fixture case's, the repository's, and a few made for the dialect.
#[test]
fn player_states_reader_agrees() {
    if !has_tool("python3", "--version") {
        eprintln!("skipped: no python3");
        return;
    }
    let root = repo();
    let mut files = Vec::new();
    json_files(&fixtures().join("cases/player-states"), &mut files);
    json_files(&root.join("mods"), &mut files);
    files.retain(|p| p.ends_with("assets/player-states.json"));
    let dir = tmp("player-states-reader");
    let state = "\"a\": {\"clip\": \"x\", \"like\": \"Block_FullBody\"}";
    for (i, t) in [
        format!("\u{feff}{{\"format\": 1, \"states\": {{{state}}}}}"),
        format!("{{\"format\": 1, \"states\": {{{state}}}, \"format\": 1}}"),
        format!("{{\"format\": 2, \"states\": {{{state}}}}}"),
        "{\"format\": 1, \"states\": {\"a\": {\"clip\": \"x\", \"like\": \"Block_FullBody\", \"speed\": NaN}}}".to_string(),
        "{\"format\": 1, \"states\": {\"a/b\": {\"clip\": \"x\", \"like\": \"Block_FullBody\"}}}".to_string(),
        "{\"format\": 1, \"states\": {\"a\": {\"clip\": \"x\", \"like\": \"M/S/T\"}}}".to_string(),
        format!("{{\"format\": 1, \"states\": {{\"{}\": {{\"clip\": \"x\", \"like\": \"Block_FullBody\"}}}}}}", "n".repeat(65)),
        format!("{{\"format\": 1, \"states\": {{\"{}\": {{\"clip\": \"x\", \"like\": \"Block_FullBody\", \"blendIn\": 5, \"loop\": false}}}}}}", "n".repeat(64)),
    ]
    .iter()
    .enumerate()
    {
        let p = dir.join(format!("case{i}/assets/player-states.json"));
        std::fs::create_dir_all(p.parent().unwrap()).unwrap();
        std::fs::write(&p, t).unwrap();
        files.push(p);
    }
    let out = std::process::Command::new("python3").arg(root.join("crates/kiln-manage/tests/lint/py-player-states.py")).arg(&root).args(&files).output().unwrap();
    assert!(out.status.success(), "{}", String::from_utf8_lossy(&out.stderr));
    let mut bad = Vec::new();
    let mut n = 0;
    for line in String::from_utf8_lossy(&out.stdout).lines() {
        let v: Value = serde_json::from_str(line).unwrap();
        let f = PathBuf::from(v["file"].as_str().unwrap());
        let text = std::fs::read_to_string(&f).unwrap();
        let r = lint_as(&dir.join(format!("lint{n}")), "assets/player-states.json", &text);
        let rust = refuses(&r, &[]);
        n += 1;
        if v["ok"].as_bool().unwrap() != rust.is_empty() {
            bad.push(format!("{}: the reader {} ({}), kiln lint {:?}", f.display(), if v["ok"].as_bool().unwrap() { "accepts" } else { "refuses" }, v["error"], rust));
        }
    }
    assert!(n >= 23, "{n}");
    assert!(bad.is_empty(), "{}", bad.join("\n"));
}

/// kiln lint's schema validator agrees with ajv (draft 2020-12) on every fixture and every mod file of the repo.
#[test]
fn ajv_agrees() {
    let root = repo();
    let modules = root.join("launcher/node_modules");
    if !has_tool("node", "--version") || !modules.join("ajv").exists() {
        eprintln!("skipped: no node or launcher/node_modules/ajv");
        return;
    }
    let mut files = Vec::new();
    json_files(&fixtures().join("cases"), &mut files);
    json_files(&root.join("mods"), &mut files);
    let dir = tmp("ajv");
    let mut by_schema: std::collections::BTreeMap<String, Vec<PathBuf>> = Default::default();
    for f in &files {
        if let Some(e) = lint::schema_for(f) {
            by_schema.entry(e.schema).or_default().push(f.clone());
        }
    }
    // the readers fixtures: as cook.json / natives.json
    for e in std::fs::read_dir(fixtures().join("readers")).unwrap().flatten() {
        let p = e.path();
        let s = if p.to_string_lossy().ends_with("natives.json") { "impact-natives.schema.json" } else { "cook.schema.json" };
        by_schema.entry(s.into()).or_default().push(p);
    }
    let mut bad = Vec::new();
    let mut n = 0;
    for (schema, list) in &by_schema {
        let out = std::process::Command::new("node").arg(root.join("crates/kiln-manage/tests/lint/ajv-check.mjs")).arg(root.join("schema")).arg(&modules).arg(schema).args(list).output().unwrap();
        assert!(out.status.success(), "{}", String::from_utf8_lossy(&out.stderr));
        for (i, line) in String::from_utf8_lossy(&out.stdout).lines().enumerate() {
            let v: Value = serde_json::from_str(line).unwrap();
            if v["syntax"].as_bool() == Some(true) {
                continue;
            }
            let f = PathBuf::from(v["file"].as_str().unwrap());
            let text = std::fs::read_to_string(&f).unwrap();
            // the schema verdict alone: the file on its own, outside its mod
            let virt = lint::schema_for(&f).and_then(|e| e.file_match.first().map(|g| g.trim_start_matches("**/").replace("*.json", "x.json").replace("*.kschem", "x.kschem"))).unwrap_or_else(|| {
                if schema == "impact-natives.schema.json" { "impact/natives.json".into() } else { "assets/cook.json".into() }
            });
            let r = lint_as(&dir.join(format!("{schema}-{i}")), &virt, &text);
            if r.diagnostics.iter().any(|d| d.code.starts_with("KL0") && d.severity == Severity::Error) {
                continue; // the dialect, not the schema (ajv's reader allows comments and trailing commas)
            }
            let rust: Vec<String> = r
                .diagnostics
                .iter()
                .filter(|d| d.code.starts_with("KL1") && !matches!(d.code.as_str(), "KL103" | "KL112") && d.severity >= Severity::Warning)
                .map(|d| format!("{} {} {}", d.code, d.pointer.clone().unwrap_or_default(), d.message))
                .collect();
            n += 1;
            if v["valid"].as_bool().unwrap() != rust.is_empty() {
                bad.push(format!("{} ({schema}): ajv {} {:?}, kiln lint {:?}", f.display(), v["valid"], v["errors"], rust));
            }
        }
    }
    assert!(n > 60, "{n}");
    assert!(bad.is_empty(), "{}", bad.join("\n"));
}

/// The manifest fixtures (their expectations come from managed/Kiln.Host/Manifest.cs, FixtureTests.cs): kiln lint
/// refuses exactly the manifests Kiln.Host refuses.
#[test]
fn manifest_fixtures_agree_with_kiln_host() {
    let dir = tmp("manifest");
    let mut n = 0;
    let mut bad = Vec::new();
    for e in std::fs::read_dir(repo().join("schema/fixtures/manifest")).unwrap().flatten() {
        let v: Value = serde_json::from_str(&std::fs::read_to_string(e.path()).unwrap()).unwrap();
        let mdir = dir.join(e.path().file_stem().unwrap());
        std::fs::create_dir_all(&mdir).unwrap();
        std::fs::write(mdir.join("kiln.mod.json"), v["raw"].as_str().unwrap()).unwrap();
        std::fs::write(mdir.join("Mod.csproj"), "").unwrap(); // a source folder: no entryAssembly check
        let r = run(&[mdir.join("kiln.mod.json")], &opts());
        let rust = refuses(&r, &["KL209"]);
        let ok = v["expect"]["ok"].as_bool().unwrap();
        n += 1;
        if ok != rust.is_empty() {
            bad.push(format!("{}: Kiln.Host {} ({}), kiln lint {:?}", e.path().display(), if ok { "accepts" } else { "refuses" }, v["expect"]["error"], rust));
        }
        // Manifest.cs's warnings (unknown keys) come back as KL103
        let warns = v["expect"]["manifest"]["warnings"].as_array().map_or(0, Vec::len);
        let kl103 = r.diagnostics.iter().filter(|d| d.code == "KL103").count();
        if ok && warns != kl103 {
            bad.push(format!("{}: {warns} warning(s) from Kiln.Host, {kl103} KL103", e.path().display()));
        }
    }
    assert!(n >= 20);
    assert!(bad.is_empty(), "{}", bad.join("\n"));
}

/// The data-pack dialect (comments yes, trailing commas no) is nlohmann::json's: the real parser of packs.cpp,
/// assets.cpp and skills.cpp agrees with kiln lint's syntax verdict on every content, assets and skills file.
#[test]
fn nlohmann_dialect() {
    if !has_tool("g++", "--version") {
        eprintln!("skipped: no g++");
        return;
    }
    let root = repo();
    let bin = Path::new(env!("CARGO_TARGET_TMPDIR")).join("nlohmann-check");
    if !bin.exists() {
        let st = std::process::Command::new("g++")
            .args(["-std=c++17", "-O1", "-o"])
            .arg(&bin)
            .arg("-I")
            .arg(root.join("native/third_party"))
            .arg(root.join("crates/kiln-manage/tests/lint/nlohmann-check.cpp"))
            .status()
            .unwrap();
        assert!(st.success());
    }
    let mut files = Vec::new();
    json_files(&fixtures().join("cases"), &mut files);
    json_files(&root.join("mods"), &mut files);
    files.retain(|p| lint::schema_for(p).is_some_and(|e| matches!(e.kind.as_str(), "content" | "assets" | "skills")));
    // and a few made to test the dialect itself
    let dir = tmp("nlohmann");
    for (i, t) in ["{\"a\": 1, // c\n \"b\": 2 /* x */}", "{\"a\": [1, 2,]}", "{\"a\": 1,}", "\u{feff}{\"a\": 1}", "{\"a\": 01}", "{\"a\": 'x'}", "{\"a\": 1} {}", "[1, 2] // tail"].iter().enumerate() {
        let p = dir.join(format!("case{i}/content/x.json"));
        std::fs::create_dir_all(p.parent().unwrap()).unwrap();
        std::fs::write(&p, t).unwrap();
        files.push(p);
    }
    let out = std::process::Command::new(&bin).args(&files).output().unwrap();
    let mut bad = Vec::new();
    for line in String::from_utf8_lossy(&out.stdout).lines() {
        let (f, verdict) = line.split_once('\t').unwrap();
        let r = run(&[PathBuf::from(f)], &opts());
        let syntax = r.diagnostics.iter().any(|d| d.file == f && d.code.starts_with("KL0") && d.severity == Severity::Error);
        if (verdict == "ok") == syntax {
            bad.push(format!("{f}: nlohmann {verdict}, kiln lint syntax error {syntax}"));
        }
    }
    assert!(files.len() > 25);
    assert!(bad.is_empty(), "{}", bad.join("\n"));
}

/// schema/fixtures/lint/skills-nodes.json: kiln lint refuses a game-tree node exactly when the real native reader
/// (native/services/skills/tree.cpp parse_node) does. The schema is deliberately stricter on a few things the reader
/// ignores (a non-boolean "bidirectional", a non-string "like" or "near", "iconFile" without .png): not in the list.
#[test]
fn skills_native_reader_agrees() {
    if !has_tool("g++", "--version") {
        eprintln!("skipped: no g++");
        return;
    }
    let root = repo();
    let check = root.join("crates/kiln-manage/tests/lint/skills-check.cpp");
    let tree = root.join("native/services/skills/tree.cpp");
    let bin = Path::new(env!("CARGO_TARGET_TMPDIR")).join("skills-check");
    let mtime = |p: &Path| p.metadata().and_then(|m| m.modified()).ok();
    let inputs = [mtime(&check), mtime(&tree), mtime(&root.join("native/services/skills/tree.h"))];
    if mtime(&bin).is_none_or(|b| inputs.iter().flatten().any(|i| *i > b)) {
        let st = std::process::Command::new("g++")
            .args(["-std=c++20", "-O0", "-o"])
            .arg(&bin)
            .arg("-I")
            .arg(root.join("native/services/skills"))
            .arg("-I")
            .arg(root.join("native/core/abi"))
            .arg("-I")
            .arg(root.join("native/third_party"))
            .arg(&check)
            .arg(&tree)
            .status()
            .unwrap();
        assert!(st.success());
    }
    let list_path = fixtures().join("skills-nodes.json");
    let list: Vec<Value> = serde_json::from_str(&std::fs::read_to_string(&list_path).unwrap()).unwrap();
    let out = std::process::Command::new(&bin).arg(&list_path).output().unwrap();
    assert!(out.status.success(), "{}", String::from_utf8_lossy(&out.stderr));
    let text = String::from_utf8_lossy(&out.stdout).into_owned();
    let verdicts: Vec<&str> = text.lines().collect();
    assert_eq!(verdicts.len(), list.len());
    let dir = tmp("skills");
    let mut bad = Vec::new();
    for (i, (e, verdict)) in list.iter().zip(&verdicts).enumerate() {
        let file = serde_json::json!({"format": 1, "gameTree": {"nodes": [e["node"]]}}).to_string();
        let r = lint_as(&dir.join(i.to_string()), "skills/x.json", &file);
        let rust = refuses(&r, &[]);
        if (*verdict == "ok") != rust.is_empty() {
            bad.push(format!("{}: native {verdict}, kiln lint {rust:?}", e["case"]));
        }
    }
    assert!(verdicts.iter().filter(|v| **v == "ok").count() > 15 && verdicts.iter().filter(|v| **v != "ok").count() > 15);
    assert!(bad.is_empty(), "{}", bad.join("\n"));
}

/// schema/cook.schema.json's sound steps list exactly the keys and enum values tools/soundcook's reader accepts.
#[test]
fn sound_schema_matches_the_recipe_reader() {
    if !has_tool("python3", "--version") {
        eprintln!("skipped: no python3");
        return;
    }
    let root = repo();
    let out = std::process::Command::new("python3").arg(root.join("crates/kiln-manage/tests/lint/py-sound-keys.py")).arg(&root).output().unwrap();
    assert!(out.status.success(), "{}", String::from_utf8_lossy(&out.stderr));
    let reader: Value = serde_json::from_slice(&out.stdout).unwrap();
    let schema: Value = serde_json::from_str(&std::fs::read_to_string(root.join("schema/cook.schema.json")).unwrap()).unwrap();
    let keys = |v: &Value| -> Vec<String> {
        let mut k: Vec<String> = v.as_object().unwrap().keys().cloned().collect();
        k.sort();
        k
    };
    let strings = |v: &Value| -> Vec<String> { v.as_array().unwrap().iter().map(|x| x.as_str().unwrap().to_string()).collect() };
    let mut ops = 0;
    for branch in schema["$defs"]["soundStep"]["allOf"].as_array().unwrap() {
        let op = branch["if"]["properties"]["op"]["const"].as_str().unwrap();
        assert_eq!(keys(&branch["then"]["properties"]), strings(&reader["keys"][op]), "keys of a {op} step");
        ops += 1;
    }
    assert_eq!(ops, reader["keys"].as_object().unwrap().len());
    let defs = &schema["$defs"];
    assert_eq!(keys(&defs["cueObject"]["properties"]), strings(&reader["cueKeys"]));
    for k in ["scene", "playScene", "stealingMode"] {
        assert_eq!(strings(&defs["cueFields"]["properties"][k]["enum"]), strings(&reader[k]), "{k}");
    }
}

/// Every mod in the repository passes the schemas (the resolver samples are broken on purpose).
#[test]
fn repository_mods_pass_the_schemas() {
    let r = run(&[repo().join("mods")], &opts());
    let bad: Vec<String> = r
        .diagnostics
        .iter()
        .filter(|d| d.severity == Severity::Error && (d.code.starts_with("KL0") || d.code.starts_with("KL1")) && !d.file.contains("sample-bad-manifest"))
        .map(|d| format!("{}:{}: {} {}", d.file, d.line, d.code, d.message))
        .collect();
    assert!(r.checked.len() > 60, "{}", r.checked.len());
    assert!(bad.is_empty(), "{}", bad.join("\n"));
}

// ----------------------------------------------------------------------------------------------- contract ----

fn normalize_report(v: &mut Value, root: &str) {
    match v {
        Value::String(s) => *s = s.replace(root, "<repo>"),
        Value::Array(a) => a.iter_mut().for_each(|x| normalize_report(x, root)),
        Value::Object(o) => o.values_mut().for_each(|x| normalize_report(x, root)),
        _ => {}
    }
}

/// schema/fixtures/lint/output.json: the full JSON report of the "files" case (paths from the repository root as
/// <repo>), valid against schema/lint-report.schema.json. tools/vscode tests its reader against it.
#[test]
fn output_contract() {
    let case = fixtures().join("cases/files");
    let mut o = opts();
    o.catalog = Some(fixtures().join("catalog/client.json"));
    o.no_catalog = false;
    let r = run(&[case], &o);
    let mut v: Value = serde_json::from_str(&r.to_json()).unwrap();
    v["version"] = Value::String("<version>".into());
    normalize_report(&mut v, &repo().display().to_string());
    let path = fixtures().join("output.json");
    let text = serde_json::to_string_pretty(&v).unwrap() + "\n";
    if std::env::var("KILN_LINT_WRITE").is_ok() {
        std::fs::write(&path, &text).unwrap();
    } else {
        assert_eq!(std::fs::read_to_string(&path).unwrap(), text, "schema/fixtures/lint/output.json is stale: KILN_LINT_WRITE=1");
    }
    // the report against its schema, with kiln lint's own validator
    let schema_text = std::fs::read_to_string(repo().join("schema/lint-report.schema.json")).unwrap();
    let schemas = lint::schema::Schemas::new([("lint-report.schema.json".to_string(), serde_json::from_str(&schema_text).unwrap())]);
    let p = lint::json::parse("output.json", &r.to_json(), 0, lint::json::Dialect::STRICT);
    let out = schemas.validate("lint-report.schema.json", p.root.as_ref().unwrap());
    assert!(out.issues.is_empty(), "{:?}", out.issues);
    assert!(r.diagnostics.iter().all(|d| d.line >= 1 && d.col >= 1 && d.span.end >= d.span.start));
    // text form: <path>:<line>:<col>: <severity> <code>: <message>
    let first = r.to_text().lines().next().unwrap().to_string();
    let d = &r.diagnostics[0];
    assert_eq!(first, format!("{}:{}:{}: {} {}: {}", d.file, d.line, d.col, d.severity.as_str(), d.code, d.message));
    assert_eq!(r.exit_code(), 1);
}

/// schema/idrefs.json and docs/kiln/modding/schemas/*.md are generated from the schemas.
#[test]
fn generated_files_are_current() {
    let write = std::env::var("KILN_LINT_WRITE").is_ok();
    let mut stale = Vec::new();
    let mut check = |path: PathBuf, text: String| {
        if write {
            std::fs::create_dir_all(path.parent().unwrap()).unwrap();
            std::fs::write(&path, &text).unwrap();
        } else if std::fs::read_to_string(&path).ok().as_deref() != Some(text.as_str()) {
            stale.push(path.display().to_string());
        }
    };
    let idrefs = lint::docs::idrefs();
    assert!(idrefs["refs"].as_array().unwrap().len() > 40);
    check(repo().join("schema/idrefs.json"), serde_json::to_string_pretty(&idrefs).unwrap() + "\n");
    let index: Value = serde_json::from_str(&std::fs::read_to_string(repo().join("schema/schemas.json")).unwrap()).unwrap();
    for row in index["schemas"].as_array().unwrap() {
        let id = row["schema"].as_str().unwrap();
        check(repo().join("docs/kiln/modding/schemas").join(lint::docs::page_name(id)), lint::docs::schema_markdown(id).unwrap());
    }
    // a section of blocks/*.json with a page of its own, so not a row of the index
    let vfx = "kiln-vfx.schema.json";
    check(repo().join("docs/kiln/modding/schemas").join(lint::docs::page_name(vfx)), lint::docs::schema_markdown(vfx).unwrap());
    assert!(stale.is_empty(), "stale (regenerate with KILN_LINT_WRITE=1): {stale:?}");
}

/// docs/kiln/modding/lint.md documents every code, under the heading the reports' `docs` links point at.
#[test]
fn every_rule_is_documented() {
    let doc = std::fs::read_to_string(repo().join("docs/kiln/modding/lint.md")).unwrap_or_default();
    let missing: Vec<&str> = lint::rules().iter().filter(|r| !doc.contains(&format!("### {} {}\n", r.code, r.name))).map(|r| r.code).collect();
    assert!(missing.is_empty(), "lint.md lacks: {missing:?}");
}
