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

/// KL901 says what to do where the user is: the CLI's command, or the launcher's own way (Pack Studio reads the game's
/// items itself; the "Build the game catalog" button).
#[test]
fn no_catalog_hint_follows_the_context() {
    let case = fixtures().join("cases/skills");
    let kl901 = |o: &LintOptions| run(&[case.clone()], o).diagnostics.into_iter().find(|d| d.code == "KL901").map(|d| d.message).expect("KL901 in cases/skills");
    let cli = kl901(&opts());
    assert!(cli.contains("make one with `kiln catalog`, or pass --catalog"), "{cli}");
    let launcher = kl901(&LintOptions { no_catalog_hint: Some(lint::LAUNCHER_CATALOG_HINT.into()), ..opts() });
    assert!(launcher.contains("Kiln will read the game's items when Pack Studio opens") && launcher.contains("'Build the game catalog' button"), "{launcher}");
    assert!(!launcher.contains("kiln catalog") && !launcher.contains("--catalog"), "no CLI wording in the launcher: {launcher}");
}

/// `text` with the path after each `marker` (`<case>`, `<repo>`) written with '/': kiln lint names files as the OS does
/// (`<case>\icons\missing.png` on Windows), the fixtures as Linux does. Only those paths change: a backslash the mod
/// itself wrote (`"C:\icons\x.png"` quoted in a message) stays.
fn slashes_after(text: &str, marker: &str) -> String {
    if !cfg!(windows) {
        return text.to_string();
    }
    let mut out = String::with_capacity(text.len());
    let mut rest = text;
    while let Some(at) = rest.find(marker) {
        let (before, from) = rest.split_at(at + marker.len());
        out.push_str(before);
        let end = from.find(|c: char| c.is_whitespace() || matches!(c, ')' | '"' | ',' | ';' | ':')).unwrap_or(from.len());
        out.push_str(&from[..end].replace('\\', "/"));
        rest = &from[end..];
    }
    out.push_str(rest);
    out
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
        .map(|d| serde_json::json!({"file": rel(&d.file, case), "code": d.code, "severity": d.severity.as_str(), "line": d.line, "col": d.col, "message": slashes_after(&d.message.replace(&case.display().to_string(), "<case>"), "<case>")}))
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

/// tests/lint/ajv-check.mjs on `files` against `schema`: its JSON lines, in the order of `files`. The files go in a list
/// file (`@<list>`): hundreds of paths are longer than a command line may be on Windows (32767 characters, 8191 through
/// cmd.exe, which a node shim such as Volta's may use).
fn ajv_check(root: &Path, modules: &Path, schema: &str, files: &[PathBuf]) -> Vec<Value> {
    static N: std::sync::atomic::AtomicU32 = std::sync::atomic::AtomicU32::new(0);
    let list = Path::new(env!("CARGO_TARGET_TMPDIR")).join("lint").join(format!("ajv-files-{}-{}.txt", std::process::id(), N.fetch_add(1, std::sync::atomic::Ordering::Relaxed)));
    std::fs::create_dir_all(list.parent().unwrap()).unwrap();
    let text: String = files.iter().map(|f| f.to_str().expect("UTF-8 test paths").to_string() + "\n").collect();
    std::fs::write(&list, text).unwrap();
    let mut at = std::ffi::OsString::from("@");
    at.push(&list);
    let out = std::process::Command::new("node").arg(root.join("crates/kiln-manage/tests/lint/ajv-check.mjs")).arg(root.join("schema"))
        .arg(modules).arg(schema).arg(at).output().unwrap();
    let _ = std::fs::remove_file(&list);
    assert!(out.status.success(), "{}", String::from_utf8_lossy(&out.stderr));
    let rows: Vec<Value> = String::from_utf8_lossy(&out.stdout).lines().map(|l| serde_json::from_str(l).unwrap()).collect();
    assert_eq!(rows.len(), files.len(), "one line per file");
    rows
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

/// The version of `name` the launcher's lockfile pins (`"name": ["name@1.2.3", ...` in launcher/bun.lock).
fn locked_version(lock: &str, name: &str) -> Option<String> {
    let key = format!("\"{name}\": [\"{name}@");
    let rest = &lock[lock.find(&key)? + key.len()..];
    Some(rest[..rest.find('"')?].to_string())
}

/// The node_modules `ajv_agrees` runs ajv from: the launcher's when it is installed; else `KILN_AJV_MODULES`; else ajv,
/// ajv-formats and jsonc-parser at the launcher's locked versions, installed once from bun's cache (offline, no
/// scripts) under the build's tmp folder. `None` only with `KILN_LINT_NO_AJV=1`. Otherwise, no node, no bun or a failed
/// install fails the test: the cross-check must not pass by skipping. It did that in every gate without
/// launcher/node_modules, and hid schema patterns kiln lint could not run.
fn ajv_modules(root: &Path) -> Option<PathBuf> {
    if std::env::var("KILN_LINT_NO_AJV").is_ok_and(|v| v == "1") {
        eprintln!("ajv_agrees: skipped (KILN_LINT_NO_AJV=1)");
        return None;
    }
    assert!(has_tool("node", "--version"), "ajv_agrees needs node (set KILN_LINT_NO_AJV=1 to skip this cross-check knowingly)");
    let launcher = root.join("launcher/node_modules");
    let need = ["ajv", "ajv-formats", "jsonc-parser"];
    if need.iter().all(|n| launcher.join(n).join("package.json").is_file()) {
        return Some(launcher);
    }
    if let Ok(m) = std::env::var("KILN_AJV_MODULES") {
        return Some(PathBuf::from(m));
    }
    let lock = std::fs::read_to_string(root.join("launcher/bun.lock")).unwrap();
    let deps: Vec<(&str, String)> = need.iter().map(|n| (*n, locked_version(&lock, n).unwrap_or_else(|| panic!("{n} is not in launcher/bun.lock")))).collect();
    let tag = deps.iter().map(|(n, v)| format!("{n}-{v}")).collect::<Vec<_>>().join("_");
    let dir = Path::new(env!("CARGO_TARGET_TMPDIR")).join("kiln-lint-ajv").join(&tag);
    if need.iter().all(|n| dir.join("node_modules").join(n).join("package.json").is_file()) {
        return Some(dir.join("node_modules"));
    }
    // installed beside, then moved in place: two test runs at once never see a half-installed folder
    let work = dir.with_file_name(format!("{tag}.tmp{}", std::process::id()));
    let _ = std::fs::remove_dir_all(&work);
    std::fs::create_dir_all(&work).unwrap();
    let pkg = serde_json::json!({ "name": "kiln-lint-ajv", "private": true,
        "dependencies": deps.iter().map(|(n, v)| (n.to_string(), Value::String(v.clone()))).collect::<serde_json::Map<_, _>>() });
    std::fs::write(work.join("package.json"), pkg.to_string()).unwrap();
    let out = std::process::Command::new("bun").args(["install", "--offline", "--ignore-scripts", "--no-progress"]).current_dir(&work).output();
    let ok = out.as_ref().is_ok_and(|o| o.status.success()) && need.iter().all(|n| work.join("node_modules").join(n).join("package.json").is_file());
    if !ok {
        let _ = std::fs::remove_dir_all(&work);
    }
    assert!(ok, "ajv_agrees: could not provide ajv: install the launcher's packages (cd launcher && bun install), set KILN_AJV_MODULES \
        to a node_modules with ajv, ajv-formats and jsonc-parser, or set KILN_LINT_NO_AJV=1 to skip knowingly. bun said: {}",
        out.map(|o| String::from_utf8_lossy(&o.stderr).into_owned()).unwrap_or_else(|e| e.to_string()));
    if std::fs::rename(&work, &dir).is_err() {
        let _ = std::fs::remove_dir_all(&work); // another run put it in place first
    }
    Some(dir.join("node_modules"))
}

/// kiln lint's schema validator agrees with ajv (draft 2020-12) on every fixture and every mod file of the repo.
#[test]
fn ajv_agrees() {
    let root = repo();
    let Some(modules) = ajv_modules(&root) else { return };
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
        for (i, v) in ajv_check(&root, &modules, schema, list).into_iter().enumerate() {
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

/// Every mod in the repository passes the schemas (the resolver samples and the content-stop lab's cs-broken are
/// broken on purpose).
#[test]
fn repository_mods_pass_the_schemas() {
    let r = run(&[repo().join("mods")], &opts());
    let bad: Vec<String> = r
        .diagnostics
        .iter()
        // by the path with '/' (Windows names it with '\\')
        .filter(|d| d.severity == Severity::Error && (d.code.starts_with("KL0") || d.code.starts_with("KL1")) && !d.file.contains("sample-bad-manifest") && !d.file.replace('\\', "/").contains("content-stop-test/mods/cs-broken"))
        .map(|d| format!("{}:{}: {} {}", d.file, d.line, d.code, d.message))
        .collect();
    assert!(r.checked.len() > 60, "{}", r.checked.len());
    assert!(bad.is_empty(), "{}", bad.join("\n"));
}

// ----------------------------------------------------------------------------------------------- contract ----

fn normalize_report(v: &mut Value, root: &str) {
    match v {
        Value::String(s) => *s = slashes_after(&s.replace(root, "<repo>"), "<repo>"),
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

/// Every `(#anchor)` link of a generated schema page names a heading of that page, by GitHub's slug rule (lower case;
/// letters, digits, `-`, `_` kept; a space is `-`; anything else dropped; `-1`, `-2` for a repeat). Caught before: a
/// def linked from a table but given no heading (`#effects`, `#guid`, `#code`) and a $ref into a def's properties
/// (`#/$defs/status/properties/modifies` linked as `#modifies`).
#[test]
fn generated_schema_pages_link_only_to_their_own_headings() {
    fn slug(h: &str) -> String {
        h.trim().to_lowercase().chars().filter_map(|c| if c == ' ' { Some('-') } else if c.is_alphanumeric() || c == '-' || c == '_' { Some(c) } else { None }).collect()
    }
    let index: Value = serde_json::from_str(&std::fs::read_to_string(repo().join("schema/schemas.json")).unwrap()).unwrap();
    let mut ids: Vec<String> = index["schemas"].as_array().unwrap().iter().map(|r| r["schema"].as_str().unwrap().to_string()).collect();
    ids.push("kiln-vfx.schema.json".into());
    let mut bad = Vec::new();
    let mut links = 0;
    for id in &ids {
        let page = lint::docs::schema_markdown(id).unwrap();
        let mut seen: std::collections::HashMap<String, usize> = Default::default();
        let mut anchors = std::collections::HashSet::new();
        let mut fenced = false;
        for line in page.lines() {
            if line.starts_with("```") {
                fenced = !fenced;
            }
            if fenced || !line.starts_with('#') {
                continue;
            }
            let base = slug(line.trim_start_matches('#'));
            let n = seen.entry(base.clone()).or_insert(0);
            anchors.insert(if *n == 0 { base.clone() } else { format!("{base}-{n}") });
            *n += 1;
        }
        for (i, line) in page.lines().enumerate() {
            for part in line.split("](#").skip(1) {
                let a = part.split(')').next().unwrap();
                links += 1;
                if !anchors.contains(a) {
                    bad.push(format!("{}:{}: #{a}", lint::docs::page_name(id), i + 1));
                }
            }
        }
    }
    assert!(links > 100, "{links} anchor links: the pages should link their defs");
    assert!(bad.is_empty(), "links with no heading:\n{}", bad.join("\n"));
}

/// docs/kiln/modding/lint.md documents every code, under the heading the reports' `docs` links point at.
#[test]
fn every_rule_is_documented() {
    let doc = std::fs::read_to_string(repo().join("docs/kiln/modding/lint.md")).unwrap_or_default();
    let missing: Vec<&str> = lint::rules().iter().filter(|r| !doc.contains(&format!("### {} {}\n", r.code, r.name))).map(|r| r.code).collect();
    assert!(missing.is_empty(), "lint.md lacks: {missing:?}");
}

/// A very long value is checked whole: the shipped kiln-publish tag pattern on 100,000 characters once aborted the
/// process (a stack overflow). Such a tag cannot have the form (at most 137 characters), so it is KL106, said once.
#[test]
fn a_very_long_value_is_checked_whole() {
    let dir = tmp("very-long");
    let tag = format!("{}cb", "a".repeat(100_000));
    let text = format!(r#"{{ "format": 1, "provider": "github", "github": {{ "repository": "example/hello", "tag": "{tag}", "commit": "0000000000000000000000000000000000000000" }} }}"#);
    let r = lint_as(&dir, "kiln.publish.json", &text);
    let at_tag: Vec<&str> = r.diagnostics.iter().filter(|d| d.pointer.as_deref() == Some("/github/tag")).map(|d| d.code.as_str()).collect();
    assert_eq!(at_tag, ["KL106"]);
    // an ordinary tag is checked as before
    let ok = lint_as(&dir, "kiln.publish.json", &text.replace(&tag, "v{version}"));
    assert!(!ok.diagnostics.iter().any(|d| d.pointer.as_deref() == Some("/github/tag")), "{:?}", ok.diagnostics);
    let bad = lint_as(&dir, "kiln.publish.json", &text.replace(&tag, "v{version}/"));
    assert!(bad.diagnostics.iter().any(|d| d.pointer.as_deref() == Some("/github/tag") && d.code == "KL106"), "{:?}", bad.diagnostics);
}

/// A value over its maxLength is said once (KL108), and is not matched against the pattern too.
#[test]
fn a_value_over_its_max_length_is_not_matched_as_well() {
    let dir = tmp("max-length");
    // kiln-release.schema.json: an artifact name is at most 200 characters of a pattern this one breaks too
    let name = format!("{}..x", "a".repeat(200));
    let text = format!(r#"{{ "format": 1, "mods": [ {{ "artifact": {{ "name": "{name}" }} }} ] }}"#);
    let r = lint_as(&dir, "kiln-release.json", &text);
    let at: Vec<&str> = r.diagnostics.iter().filter(|d| d.pointer.as_deref() == Some("/mods/0/artifact/name")).map(|d| d.code.as_str()).collect();
    assert_eq!(at, ["KL108"], "{:?}", r.diagnostics);
}

#[test]
fn run_bindings_reject_wrong_sizes_and_damage_with_only_knockback() {
    let d = tmp("run-bindings-contract");
    std::fs::create_dir_all(d.join("blocks")).unwrap();
    std::fs::create_dir_all(d.join("impact")).unwrap();
    std::fs::write(d.join("kiln.mod.json"), r#"{"id":"run-contract","version":"1.0.0","apiVersion":"0.2.0","side":"server"}"#).unwrap();
    std::fs::write(d.join("impact/natives.json"), r#"{"format":1,"natives":[
      {"name":"run-contract.WrongSize","argc":1,"sync":true,"args":[{"name":"target","type":"ecs.EntityId","size":8}]},
      {"name":"run-contract.NeedsDamage","argc":1,"sync":true,"args":[{"name":"damage","type":"impact.DamageSetup","size":52}]}
    ]}"#).unwrap();
    std::fs::write(d.join("blocks/run.json"), r#"{"format":1,"effects":{
      "wrong":{"run":"run-contract.WrongSize","heal":1},
      "knock":{"run":"run-contract.NeedsDamage","knockback":"small"}
    }}"#).unwrap();
    let report = run(&[d], &opts());
    let bindings: Vec<_> = report.diagnostics.iter().filter(|d| d.code == "KL340").collect();
    assert_eq!(bindings.len(), 2, "{}", report.to_text());
    assert!(bindings.iter().any(|d| d.message.contains("size")), "{}", report.to_text());
    assert!(bindings.iter().any(|d| d.message.contains("damage") && d.message.contains("nothing")), "{}", report.to_text());
}

#[test]
fn effect_run_names_obey_the_registry_byte_limit() {
    let d = tmp("run-name-limit");
    std::fs::create_dir_all(d.join("blocks")).unwrap();
    std::fs::write(d.join("kiln.mod.json"), r#"{"id":"run-contract","version":"1.0.0","apiVersion":"0.2","side":"server","license":"MIT"}"#).unwrap();
    let name = format!("run-contract.{}", "A".repeat(90));
    std::fs::write(d.join("blocks/run.json"), serde_json::json!({"format":1,"effects":{"long":{"run":name,"heal":1}}}).to_string()).unwrap();
    let report = run(&[d], &opts());
    assert!(report.diagnostics.iter().any(|d| d.severity == Severity::Error && d.message.contains("90")), "{}", report.to_text());
}

/// docs/kiln/modding/resource-patches.md: every blocks-file example passes the schema, and a patch is at least one change
/// of set, append, replace, clear, addComponents, removeComponents, copyComponents (with nothing else, or an appended change of the
/// game's value, refused).
#[test]
fn resource_patch_examples_pass_and_a_patch_needs_a_change() {
    let doc = std::fs::read_to_string(repo().join("docs/kiln/modding/resource-patches.md")).unwrap();
    let examples: Vec<&str> = doc.split("```json").skip(1).filter_map(|b| b.split("```").next()).filter(|b| b.contains("\"resourcePatches\"")).collect();
    assert!(examples.len() >= 2, "{}", examples.len());
    let d = tmp("resource-patch-examples");
    std::fs::create_dir_all(d.join("blocks")).unwrap();
    std::fs::write(d.join("kiln.mod.json"), r#"{"id":"patch-docs","version":"1.0.0","apiVersion":"0.2","side":"server","boot":true,"license":"MIT"}"#).unwrap();
    for (i, e) in examples.iter().enumerate() {
        std::fs::write(d.join(format!("blocks/example-{i}.json")), e).unwrap();
    }
    let r = run(&[d.clone()], &opts());
    let errors: Vec<String> = r.diagnostics.iter().filter(|x| x.severity == Severity::Error).map(|x| format!("{}:{} {} {}", x.file, x.line, x.code, x.message)).collect();
    assert!(errors.is_empty(), "{}", errors.join("\n"));
    let guid = "82706b40-61b1-4b8f-8b23-dcec6971bda1";
    let cases = [
        (serde_json::json!({"resource": guid, "type": "keen::BalancingTable"}), true),
        (serde_json::json!({"resource": guid, "type": "keen::BalancingTable", "append": {"steps": {"values": [{"mul": 2}]}}}), true),
        (serde_json::json!({"resource": guid, "type": "keen::BalancingTable", "append": {"steps": {"values": [1], "below": true}}}), true),
        (serde_json::json!({"resource": guid, "type": "keen::ecs::TemplateResource", "addComponents": []}), true),
        (serde_json::json!({"resource": guid, "type": "keen::ecs::TemplateResource", "removeComponents": ["SpawnTime"]}), false),
        (serde_json::json!({"resource": guid, "type": "keen::BalancingTable", "append": {"steps": {"values": [3, 5], "aboveLast": true}}}), false),
        (serde_json::json!({"resource": guid, "type": "keen::SceneResource", "clear": ["noBuildZones"]}), false),
        (serde_json::json!({"resource": guid, "type": "keen::SceneResource", "clear": []}), true),
        (serde_json::json!({"resource": guid, "type": "keen::FbUiBundle", "replace": {"steps": {"values": [0.5, 1]}}}), false),
        (serde_json::json!({"resource": guid, "type": "keen::FbUiBundle", "replace": {"steps": {"values": [1], "aboveLast": true}}}), true),
        (serde_json::json!({"resource": guid, "type": "keen::ecs::TemplateResource", "copyComponents": {"InventoryCraftingStock": guid}}), false),
        (serde_json::json!({"resource": guid, "type": "keen::ecs::TemplateResource", "copyComponents": {"InventoryCraftingStock": "magic"}}), true),
    ];
    for (i, (patch, bad)) in cases.iter().enumerate() {
        let e = tmp(&format!("resource-patch-case-{i}"));
        std::fs::create_dir_all(e.join("blocks")).unwrap();
        std::fs::write(e.join("kiln.mod.json"), r#"{"id":"patch-case","version":"1.0.0","apiVersion":"0.2","side":"server","boot":true,"license":"MIT"}"#).unwrap();
        std::fs::write(e.join("blocks/p.json"), serde_json::json!({"format": 1, "resourcePatches": {"one": patch}}).to_string()).unwrap();
        let r = run(&[e], &opts());
        let errors = r.diagnostics.iter().filter(|x| x.severity == Severity::Error).count();
        assert_eq!(errors > 0, *bad, "case {i}: {patch}: {}", r.to_text());
    }
}

/// A data pack's "registry" section is checked against registry-section.schema.json (content-pack.schema.json refers to
/// it): a well-formed section passes, an edit naming a field the registry does not have, or a stage that does not exist,
/// is refused.
#[test]
fn content_pack_registry_section() {
    let dir = tmp("registry-section");
    let good = r#"{"registry": {"alter": [{"registry": "items", "select": {"id": "game:material_t1_wood_logs"}, "field": "maxStack", "op": {"mul": 4}}],
        "final": [{"registry": "recipes", "select": {"id": "game:*"}, "field": "inputs[*].count", "op": {"mul": 2}}]}}"#;
    let r = lint_as(&dir.join("good"), "content/a.json", good);
    assert!(refuses(&r, &[]).is_empty(), "{:?}", refuses(&r, &[]));
    let bad_field = r#"{"registry": {"alter": [{"registry": "items", "select": {"id": "game:x"}, "field": "maxStackSize", "op": 5}]}}"#;
    let r = lint_as(&dir.join("field"), "content/a.json", bad_field);
    assert!(!refuses(&r, &[]).is_empty(), "a game member name in place of a Kiln field is refused");
    let bad_stage = r#"{"registry": {"later": []}}"#;
    let r = lint_as(&dir.join("stage"), "content/a.json", bad_stage);
    assert!(!refuses(&r, &[]).is_empty(), "an unknown stage is refused");
    // dev.32 api-shape: what the parser (ir.cpp) takes, the schema takes: "select" as a plain string (the samples' form)
    let plain = r#"{"registry": {"alter": [{"registry": "creatures", "select": "game:enemy_wildbeast_wolf_earlygame", "field": "level", "op": 12}]}}"#;
    let r = lint_as(&dir.join("plain-select"), "content/a.json", plain);
    assert!(refuses(&r, &[]).is_empty(), "a plain string selector: {:?}", refuses(&r, &[]));
}

/// The same boundary matrix Kiln.Blocks.Tests compiles, checked by both kiln lint and independent draft-2020-12 AJV.
#[test]
fn block_range_boundaries_agree_with_schema_and_compiler_contract() {
    let root = repo();
    // Use the real reader's round-trip f64 parsing, including exact float.MaxValue boundaries.
    let read = |path: &str| lint::json::parse(path, &std::fs::read_to_string(root.join(path)).unwrap(), 0, lint::json::Dialect::STRICT).root.unwrap().to_json();
    let matrix = read("schema/fixtures/block-ranges.json");
    let schema = read("schema/kiln-blocks.schema.json");
    let dir = tmp("block-ranges");
    let mut files = Vec::new();
    let mut expected = Vec::new();
    let mut bad = Vec::new();
    for c in matrix.as_array().unwrap() {
        let mut leaf = &schema["$defs"][c["definition"].as_str().unwrap()];
        for part in c["schema"].as_str().unwrap().split('/').filter(|p| !p.is_empty()) {
            leaf = if let Some(name) = part.strip_prefix("$ref:") { &schema["$defs"][name] }
                else if leaf.is_array() { &leaf[part.parse::<usize>().unwrap()] } else { &leaf[part] };
        }
        let integer = leaf["type"] == "integer";
        let step: f64 = if integer { 1.0 } else { 0.001 };
        let bounds = c["bounds"].as_object().unwrap();
        for (key, value) in bounds {
            if leaf[key].as_f64() != value.as_f64() { bad.push(format!("{} {}: schema {key} {} != {value}", c["type"], c["field"], leaf[key])); }
        }
        for bound in bounds.values() {
            let b = bound.as_f64().unwrap();
            let delta = if integer { 1.0 } else { step.max(b.abs() * 0.000001) };
            for offset in [-delta, 0.0, delta] {
                let n = b + offset;
                let valid = bounds.iter().all(|(key, b)| match key.as_str() {
                    "minimum" => n >= b.as_f64().unwrap(), "maximum" => n <= b.as_f64().unwrap(),
                    "exclusiveMinimum" => n > b.as_f64().unwrap(), "exclusiveMaximum" => n < b.as_f64().unwrap(), _ => unreachable!(),
                });
                let mut block = c["block"].clone();
                let parts: Vec<_> = c["field"].as_str().unwrap().split('.').collect();
                let mut at = &mut block;
                for part in &parts[..parts.len()-1] {
                    if at.is_array() { at = &mut at[part.parse::<usize>().unwrap()]; }
                    else { if at[*part].is_null() { at[*part] = serde_json::json!({}); } at = &mut at[*part]; }
                }
                at[*parts.last().unwrap()] = if integer { serde_json::json!(n as i64) } else { serde_json::json!(n) };
                if c["probe"] == "clip" { block = serde_json::json!({"clips":"body.glb", "idle":block}); }
                let section = c["section"].as_str().unwrap();
                let doc = serde_json::json!({"format":1, section:{"thing":block}});
                let path = dir.join(format!("{}/blocks/range.json", files.len()));
                std::fs::create_dir_all(path.parent().unwrap()).unwrap();
                std::fs::write(&path, doc.to_string()).unwrap();
                let report = lint::lint_text(&path, &doc.to_string(), &opts());
                let errors = refuses(&report, &[]);
                if errors.is_empty() != valid { bad.push(format!("{} {} = {n}: expected {valid}, lint {errors:?}", c["type"], c["field"])); }
                files.push(path);
                expected.push(valid);
            }
        }
    }
    let Some(modules) = ajv_modules(&root) else { panic!("boundary parity requires AJV") };
    let rows = ajv_check(&root, &modules, "kiln-blocks.schema.json", &files);
    assert_eq!(rows.len(), expected.len());
    for (i, row) in rows.iter().enumerate() {
        if row["valid"].as_bool() != Some(expected[i]) { bad.push(format!("AJV {}: expected {}, got {}", files[i].display(), expected[i], row)); }
    }
    assert!(bad.is_empty(), "{}", bad.join("\n"));
}

#[test]
fn block_shapes_and_relational_bounds_match_compiler_contract() {
    let root = repo();
    let cases: Value = serde_json::from_str(&std::fs::read_to_string(root.join("schema/fixtures/block-shapes.json")).unwrap()).unwrap();
    let dir = tmp("block-shapes");
    let mut files = Vec::new();
    let mut bad = Vec::new();
    for (i, c) in cases.as_array().unwrap().iter().enumerate() {
        let section = c["section"].as_str().unwrap();
        let doc = serde_json::json!({"format":1, section:{"thing":c["block"]}});
        let path = dir.join(format!("{i}/blocks/shape.json"));
        std::fs::create_dir_all(path.parent().unwrap()).unwrap();
        std::fs::write(&path, doc.to_string()).unwrap();
        let report = lint::lint_text(&path, &doc.to_string(), &opts());
        let errors = refuses(&report, &[]);
        if errors.is_empty() != c["valid"].as_bool().unwrap() { bad.push(format!("{}: lint {errors:?}", c["name"])); }
        files.push(path);
    }
    let Some(modules) = ajv_modules(&root) else { panic!("shape parity requires AJV") };
    let output = std::process::Command::new("node").arg(root.join("crates/kiln-manage/tests/lint/ajv-check.mjs"))
        .arg(root.join("schema")).arg(modules).arg("kiln-blocks.schema.json").args(&files).output().unwrap();
    assert!(output.status.success(), "{}", String::from_utf8_lossy(&output.stderr));
    let rows: Vec<Value> = String::from_utf8_lossy(&output.stdout).lines().map(|s| serde_json::from_str(s).unwrap()).collect();
    assert_eq!(rows.len(), files.len());
    for (c, row) in cases.as_array().unwrap().iter().zip(rows) {
        if row["valid"] != c["schemaValid"] { bad.push(format!("{}: AJV {row}", c["name"])); }
    }
    assert!(bad.is_empty(), "{}", bad.join("\n"));
}
