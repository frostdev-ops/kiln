//! C# <-> Rust parity: runs every case of schema/fixtures/{semver,manifest,resolver} exactly as
//! managed/tests/Kiln.Host.Tests/FixtureTests.cs does (the "expect" values come from the real C# code).

use kiln_manage::jsonc::{self, Node};
use kiln_manage::manifest::{self, KilnManifest};
use kiln_manage::resolver::{self, Candidate, Environment};
use kiln_manage::semver::{SemVersion, VersionRange};
use serde_json::{Value, json};
use std::cmp::Ordering;
use std::path::{Path, PathBuf};

fn fixtures(kind: &str) -> PathBuf {
    Path::new(env!("CARGO_MANIFEST_DIR")).join("../../schema/fixtures").join(kind)
}

fn files(kind: &str) -> Vec<PathBuf> {
    let mut v: Vec<PathBuf> = std::fs::read_dir(fixtures(kind))
        .unwrap()
        .flatten()
        .map(|e| e.path())
        .filter(|p| p.extension().is_some_and(|x| x == "json"))
        .collect();
    v.sort();
    v
}

fn load(path: &Path) -> (String, Value) {
    let text = std::fs::read_to_string(path).unwrap();
    let v = serde_json::from_str(&text).unwrap_or_else(|e| panic!("{}: {e}", path.display()));
    (text, v)
}

fn v(s: &str) -> SemVersion {
    SemVersion::parse(s).unwrap_or_else(|e| panic!("{s}: {e}"))
}

#[test]
fn semver_parse() {
    let (_, doc) = load(&fixtures("semver").join("parse.json"));
    let cases = doc["cases"].as_array().unwrap();
    assert!(cases.len() > 10);
    for c in cases {
        let text = c["text"].as_str().unwrap();
        let got = match SemVersion::try_parse(text) {
            Some(v) => json!({"ok": true, "major": v.major, "minor": v.minor, "patch": v.patch, "pre": v.pre,
                "build": v.build, "text": v.to_string()}),
            None => json!({"ok": false}),
        };
        assert_eq!(got, c["expect"], "parse {text:?}");
    }
}

#[test]
fn semver_ranges() {
    let (_, doc) = load(&fixtures("semver").join("ranges.json"));
    let cases = doc["cases"].as_array().unwrap();
    assert!(cases.len() > 20);
    for c in cases {
        let (range, version) = (c["range"].as_str().unwrap(), c["version"].as_str().unwrap());
        let r = VersionRange::parse(range).unwrap_or_else(|e| panic!("{range}: {e}"));
        assert_eq!(r.satisfies(&v(version)), c["satisfies"].as_bool().unwrap(), "{range} ~ {version}");
    }
    for c in doc["invalid"].as_array().unwrap() {
        let range = c["range"].as_str().unwrap();
        assert_eq!(VersionRange::try_parse(range).is_ok(), c["ok"].as_bool().unwrap(), "{range}");
    }
}

#[test]
fn semver_order() {
    let (_, doc) = load(&fixtures("semver").join("order.json"));
    let ordered: Vec<SemVersion> = doc["ordered"].as_array().unwrap().iter().map(|x| v(x.as_str().unwrap())).collect();
    for w in ordered.windows(2) {
        assert_eq!(w[0].compare(&w[1]), Ordering::Less, "{} < {}", w[0], w[1]);
    }
    for pair in doc["equal"].as_array().unwrap() {
        let (a, b) = (v(pair[0].as_str().unwrap()), v(pair[1].as_str().unwrap()));
        assert_eq!(a.compare(&b), Ordering::Equal, "{a} == {b}");
    }
}

#[test]
fn semver_api() {
    let (_, doc) = load(&fixtures("semver").join("api.json"));
    for c in doc["cases"].as_array().unwrap() {
        let (api, current) = (c["api"].as_str().unwrap(), c["current"].as_str().unwrap());
        let got = VersionRange::at_least_same_major(&v(api)).satisfies(&v(current));
        assert_eq!(got, c["satisfies"].as_bool().unwrap(), "{api} on {current}");
    }
}

#[test]
fn semver_has_no_unknown_files() {
    let names: Vec<String> = files("semver").iter().map(|p| p.file_name().unwrap().to_string_lossy().into_owned()).collect();
    assert_eq!(names, ["api.json", "order.json", "parse.json", "ranges.json"]);
}

/// FixtureTests.Describe.
fn describe(m: &KilnManifest) -> Value {
    json!({
        "id": m.id, "name": m.name, "version": m.version, "parsedVersion": m.parsed_version, "apiVersion": m.api_version,
        "entryAssembly": m.entry_assembly, "entryType": m.entry_type,
        "side": serde_json::to_value(m.side).unwrap(), "clients": serde_json::to_value(m.clients).unwrap(),
        "authors": m.authors, "description": m.description, "license": m.license, "url": m.url,
        "dependencies": m.dependencies.iter().map(|d| json!({"id": d.id, "range": d.range, "optional": d.optional})).collect::<Vec<_>>(),
        "loadBefore": m.load_before, "loadAfter": m.load_after,
        "incompatible": m.incompatible.iter().map(|x| json!({"id": x.id, "range": x.range})).collect::<Vec<_>>(),
        "features": m.features, "exports": m.exports, "advanced": m.advanced,
        "tests": m.tests.as_ref().map(|t| json!({"autoRun": t.auto_run, "timeoutSeconds": t.timeout_seconds, "filter": t.filter})),
        "kilnBuild": m.kiln_build.as_ref().map(|b| json!({"version": b.version, "created": b.created})),
        "warnings": m.warnings,
    })
}

#[test]
fn manifest_fixtures() {
    let all = files("manifest");
    assert!(all.len() >= 20);
    for f in all {
        let (_, doc) = load(&f);
        let name = f.file_name().unwrap().to_string_lossy().into_owned();
        let raw = doc["raw"].as_str().unwrap();
        let expect = &doc["expect"];
        match manifest::parse_kiln(raw) {
            Ok(m) => {
                assert_eq!(expect["ok"], true, "{name}: parsed, but C# refuses it: {}", expect["error"]);
                let got = describe(&m);
                for (k, want) in expect["manifest"].as_object().unwrap() {
                    assert_eq!(&got[k], want, "{name}: field {k}");
                }
                assert_eq!(got.as_object().unwrap().len(), expect["manifest"].as_object().unwrap().len(), "{name}: field count");
            }
            Err(e) => {
                assert_eq!(expect["ok"], false, "{name}: refused ({e}), but C# parses it");
                let want = expect["error"].as_str().unwrap();
                if doc["syntax"] == true {
                    assert!(e.message.starts_with("kiln.mod.json: not valid JSON"), "{name}: {}", e.message);
                    assert!(want.starts_with("kiln.mod.json: not valid JSON"));
                } else {
                    assert_eq!(e.message, want, "{name}");
                }
            }
        }
    }
}

/// The exact source text of a fixture's sub-value (keeps key order, as JsonNode.ToJsonString does).
fn raw_of<'a>(text: &'a str, node: &Node) -> &'a str {
    node.raw(text)
}

fn candidate(text: &str, m: &Node) -> Candidate {
    let dir = m.get("dir").and_then(Node::as_str).unwrap().to_string();
    let raw = match m.get("raw").and_then(Node::as_str) {
        Some(r) => r.to_string(),
        None => raw_of(text, m.get("manifest").unwrap()).to_string(),
    };
    match manifest::parse_kiln(&raw) {
        Ok(man) => Candidate { dir, manifest: Some(man), error: None },
        Err(e) => Candidate { dir, manifest: None, error: Some(e.message) },
    }
}

#[test]
fn resolver_fixtures() {
    let all = files("resolver");
    assert!(all.len() >= 17);
    for f in all {
        let name = f.file_name().unwrap().to_string_lossy().into_owned();
        let text = std::fs::read_to_string(&f).unwrap();
        let tree = jsonc::parse_tree(&text).unwrap();
        let env = tree.get("env").unwrap();
        let s = |k: &str| env.get(k).and_then(Node::as_str).map(str::to_string);
        let loaded: Vec<KilnManifest> = env
            .get("loaded")
            .map(|l| l.elements().iter().map(|e| manifest::parse_kiln(e.node.raw(&text)).unwrap()).collect())
            .unwrap_or_default();
        let environment = Environment {
            kiln: s("kiln").unwrap(),
            api: s("api").unwrap(),
            side: if s("side").as_deref() == Some("client") { manifest::RunSide::Client } else { manifest::RunSide::Server },
            unavailable_features: env
                .get("unavailableFeatures")
                .map(|a| a.elements().iter().map(|e| e.node.as_str().unwrap().to_string()).collect())
                .unwrap_or_default(),
            loaded,
            hosting: env.get("hosting").and_then(Node::as_bool).unwrap_or(false),
        };
        let candidates: Vec<Candidate> = tree.get("mods").unwrap().elements().iter().map(|e| candidate(&text, &e.node)).collect();
        let res = resolver::resolve(&candidates, &environment).unwrap();
        let mut rejected = res.rejected.clone();
        rejected.sort_by(|a, b| a.dir.as_bytes().cmp(b.dir.as_bytes()).then(a.id.as_bytes().cmp(b.id.as_bytes())));
        let got = json!({
            "order": res.order,
            "rejected": rejected.iter().map(|r| json!({"id": r.id, "dir": r.dir, "state": serde_json::to_value(r.state).unwrap(), "reason": r.reason})).collect::<Vec<_>>(),
        });
        let doc: Value = serde_json::from_str(&text).unwrap();
        assert_eq!(got["order"], doc["expect"]["order"], "{name}: order");
        assert_eq!(got["rejected"], doc["expect"]["rejected"], "{name}: rejected");
    }
}
