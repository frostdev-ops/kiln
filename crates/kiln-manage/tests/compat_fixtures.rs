//! dev.21: the compat-rules fixtures shared with kiln-core (schema/fixtures/compat-rules/cases.json, run on the C++ side by
//! native/core/tests/test-compat.sh) through the launcher's reader and evaluator: both must give the same results.

use kiln_manage::compat::*;
use std::path::PathBuf;

fn cases() -> serde_json::Value {
    let p = PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("../../schema/fixtures/compat-rules/cases.json");
    serde_json::from_str(&std::fs::read_to_string(p).unwrap()).unwrap()
}

fn strings(v: &serde_json::Value) -> Vec<String> {
    v.as_array().unwrap().iter().map(|s| s.as_str().unwrap().to_string()).collect()
}

fn facts(f: &serde_json::Value) -> Facts {
    Facts { modules: strings(&f["modules"]), engines: strings(&f["engines"]), mods: strings(&f["mods"]), symbols: strings(&f["symbols"]) }
}

#[test]
fn glob_cases() {
    let mut bad = Vec::new();
    for c in cases()["glob"].as_array().unwrap() {
        let (p, n, m) = (c["pattern"].as_str().unwrap(), c["name"].as_str().unwrap(), c["match"].as_bool().unwrap());
        if glob(p, n) != m {
            bad.push(format!("glob {p:?} vs {n:?}: want {m}"));
        }
    }
    assert!(bad.is_empty(), "{}", bad.join("\n"));
}

#[test]
fn parse_cases() {
    let mut bad = Vec::new();
    for c in cases()["parse"].as_array().unwrap() {
        let name = c["name"].as_str().unwrap();
        let text = match &c["input"] {
            serde_json::Value::String(s) => s.clone(),
            v => v.to_string(),
        };
        let (rules, errors) = parse_rules(text.as_bytes(), RuleSource::Kiln);
        let ids: Vec<String> = rules.iter().map(|r| r.rule.id.clone()).collect();
        let want = strings(&c["rules"]);
        if ids != want {
            bad.push(format!("{name}: kept {ids:?}, want {want:?} (errors {errors:?})"));
        }
        // a file-level failure gives no rules and one error; a good file gives one error per dropped rule
        let ok = c["ok"].as_bool().unwrap();
        let dropped = c["dropped"].as_u64().unwrap() as usize;
        // one line per dropped rule, unless the case says otherwise (the rules past the limit give one summary line)
        let problems = c["problems"].as_u64().map_or(dropped, |n| n as usize);
        if ok && errors.len() != problems {
            bad.push(format!("{name}: {} errors, want {problems}: {errors:?}", errors.len()));
        }
        if let Some(last) = c["lastProblem"].as_str()
            && errors.last().map(String::as_str) != Some(last)
        {
            bad.push(format!("{name}: last problem {:?}, want {last:?}", errors.last()));
        }
        if !ok && errors.len() != 1 {
            bad.push(format!("{name}: want one file error, got {errors:?}"));
        }
    }
    assert!(bad.is_empty(), "{}", bad.join("\n"));
}

#[test]
fn merge_cases() {
    let dir = PathBuf::from(env!("CARGO_TARGET_TMPDIR")).join("compat-fixtures-merge");
    let mut bad = Vec::new();
    for (i, c) in cases()["merge"].as_array().unwrap().iter().enumerate() {
        let name = c["name"].as_str().unwrap();
        let case = dir.join(i.to_string());
        let _ = std::fs::remove_dir_all(&case);
        std::fs::create_dir_all(&case).unwrap();
        let mut files = Vec::new();
        for (key, source) in [("shipped", RuleSource::Kiln), ("user", RuleSource::User)] {
            let path = case.join(format!("{key}.json"));
            if !c[key].is_null() {
                std::fs::write(&path, serde_json::json!({"format": 1, "rules": c[key]}).to_string()).unwrap();
            }
            files.push((path, source));
        }
        let (rules, errors) = load_rules(&files);
        let ids: Vec<String> = rules.iter().map(|r| r.rule.id.clone()).collect();
        let want = strings(&c["ids"]);
        if ids != want || !errors.is_empty() {
            bad.push(format!("{name}: {ids:?}, want {want:?} ({errors:?})"));
        }
    }
    let _ = std::fs::remove_dir_all(&dir);
    assert!(bad.is_empty(), "{}", bad.join("\n"));
}

#[test]
fn evaluate_cases() {
    let mut bad = Vec::new();
    for c in cases()["evaluate"].as_array().unwrap() {
        let name = c["name"].as_str().unwrap();
        let text = serde_json::json!({"format": 1, "rules": c["rules"]}).to_string();
        let (rules, errors) = parse_rules(text.as_bytes(), RuleSource::Kiln);
        let f = facts(&c["facts"]);
        let fired: Vec<String> = rules.iter().filter(|r| r.rule.when.iter().all(|m| f.holds(m))).map(|r| r.rule.id.clone()).collect();
        let want = strings(&c["fires"]);
        if fired != want {
            bad.push(format!("{name}: fired {fired:?}, want {want:?} ({errors:?})"));
        }
    }
    assert!(bad.is_empty(), "{}", bad.join("\n"));
}
