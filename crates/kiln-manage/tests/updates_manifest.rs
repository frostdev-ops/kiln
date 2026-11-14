//! Manifest `"updates"`: the JSON schema, the reader and `kiln lint` agree on what is a readable value, and a value
//! that is not readable is a warning, never an error (the mod loads).

use kiln_manage::lint::{self, LintOptions, Severity};
use kiln_manage::manifest;
use std::path::{Path, PathBuf};

fn tmp(tag: &str) -> PathBuf {
    let d = Path::new(env!("CARGO_TARGET_TMPDIR")).join(format!("updates-manifest-{tag}-{}", std::process::id()));
    let _ = std::fs::remove_dir_all(&d);
    std::fs::create_dir_all(&d).unwrap();
    d
}

/// What the linter says about `"updates": <value>` in a manifest: (schema findings under /updates, KL103 of the reader).
fn lint_of(dir: &Path, n: usize, file: &str, text: &str) -> (Vec<String>, Vec<String>) {
    let mdir = dir.join(format!("case-{n}"));
    std::fs::create_dir_all(&mdir).unwrap();
    std::fs::write(mdir.join(file), text).unwrap();
    std::fs::write(mdir.join("Mod.csproj"), "").unwrap();
    let r = lint::lint_paths(&[mdir.join(file)], &LintOptions { no_catalog: true, ..Default::default() });
    let about: Vec<_> = r.diagnostics.iter().filter(|d| d.pointer.as_deref().is_some_and(|p| p.starts_with("/updates"))).collect();
    for d in &about {
        assert!(d.severity <= Severity::Warning, "an error about updates, though the mod loads: {} {}", d.code, d.message);
    }
    let reader = about.iter().filter(|d| d.code == "KL103" && d.message.starts_with("\"updates\": Kiln ignores it")).map(|d| d.message.clone()).collect();
    let schema = about.iter().filter(|d| !d.message.starts_with("\"updates\": Kiln ignores it")).map(|d| format!("{} {}", d.code, d.message)).collect();
    (schema, reader)
}

const CASES: &[&str] = &[
    // readable
    r#"{"provider": "github", "repository": "example/mod"}"#,
    r#"{"provider": "github", "repository": "Example-Org/my.mod_2", "tagPattern": "mods/example/v{version}", "asset": "mod-{version}.zip", "channel": "prerelease"}"#,
    r#"{"provider": "github", "repository": "a/b", "tagPattern": "{version}", "asset": "mod.zip", "channel": "stable"}"#,
    r#"{"provider": "github", "repository": "a/b", "tagPattern": "v{version}-kiln"}"#,
    r#"{"provider": "nexus", "gameId": 5811, "modId": 310, "fileChainId": "90310"}"#,
    "null",
    // not readable
    r#""https://example.org/feed.json""#,
    r#"{"repository": "a/b"}"#,
    r#"{"provider": "github"}"#,
    r#"{"provider": "github", "repository": "a"}"#,
    r#"{"provider": "github", "repository": "a/b/c"}"#,
    r#"{"provider": "github", "repository": "a/.."}"#,
    r#"{"provider": "github", "repository": "../b"}"#,
    r#"{"provider": "github", "repository": "./b"}"#,
    r#"{"provider": "github", "repository": "a/."}"#,
    r#"{"provider": "github", "repository": "https://github.com/a/b"}"#,
    r#"{"provider": "github", "repository": "a/b", "tagPattern": "release"}"#,
    r#"{"provider": "github", "repository": "a/b", "tagPattern": "{version}-{version}"}"#,
    r#"{"provider": "github", "repository": "a/b", "tagPattern": "a/../v{version}"}"#,
    r#"{"provider": "github", "repository": "a/b", "tagPattern": "/v{version}"}"#,
    r#"{"provider": "github", "repository": "a/b", "tagPattern": "a//v{version}"}"#,
    r#"{"provider": "github", "repository": "a/b", "tagPattern": "v{version}/"}"#,
    r#"{"provider": "github", "repository": "a/b", "tagPattern": "v {version}"}"#,
    r#"{"provider": "github", "repository": "a/b", "tagPattern": "v{version}{x}"}"#,
    r#"{"provider": "github", "repository": "a/b", "tagPattern": "v*{version}"}"#,
    r#"{"provider": "github", "repository": "a/b", "asset": "dist/mod-{version}.zip"}"#,
    r#"{"provider": "github", "repository": "a/b", "asset": "mod-{version}-{version}.zip"}"#,
    r#"{"provider": "github", "repository": "a/b", "asset": "mod..zip"}"#,
    r#"{"provider": "github", "repository": "a/b", "asset": "."}"#,
    r#"{"provider": "github", "repository": "a/b", "asset": ""}"#,
    r#"{"provider": "github", "repository": "a/b", "asset": "mod-{tag}.zip"}"#,
    r#"{"provider": "github", "repository": "a/b", "channel": "nightly"}"#,
    r#"{"provider": "github", "repository": "a/b", "url": "https://example.org"}"#,
    r#"{"provider": "nexus", "gameId": 1704, "modId": 310, "fileChainId": "90310"}"#,
    r#"{"provider": "nexus", "gameId": 5811, "modId": "310", "fileChainId": "90310"}"#,
    r#"{"provider": "nexus", "gameId": 5811, "modId": 0, "fileChainId": "90310"}"#,
    r#"{"provider": "nexus", "gameId": 5811, "modId": 1234567890, "fileChainId": "90310"}"#,
    r#"{"provider": "nexus", "gameId": 5811, "modId": 310}"#,
    r#"{"provider": "nexus", "gameId": 5811, "modId": 310, "fileChainId": "a b"}"#,
    r#"{"provider": "nexus", "gameId": 5811, "modId": 310, "fileChainId": "90310", "fileVersionId": "1"}"#,
];

#[test]
fn the_schema_the_reader_and_the_linter_agree_on_updates() {
    let dir = tmp("agree");
    let mut readable = 0;
    for (n, updates) in CASES.iter().enumerate() {
        let text = format!(r#"{{"id": "m{n}", "version": "1.0.0", "apiVersion": "0.2", "entryAssembly": "M.dll", "updates": {updates}}}"#);
        let m = manifest::parse_kiln(&text).unwrap_or_else(|e| panic!("{updates}: refused: {e}"));
        let warned = m.warnings.iter().any(|w| w.starts_with("\"updates\" is not read"));
        assert_eq!(warned, m.updates.is_none() && *updates != "null", "{updates}");
        let (schema, reader) = lint_of(&dir, n, "kiln.mod.json", &text);
        assert_eq!(reader.len(), usize::from(warned), "{updates}: the linter's reader finding: {reader:?}");
        assert_eq!(schema.is_empty(), !warned, "{updates}: the schema says {schema:?}, the reader {:?}", m.warnings);
        readable += usize::from(m.updates.is_some());
        // native.json: the same schema block, the same reader
        let native = format!(r#"{{"id": "n{n}", "version": "1.0.0", "dlls": ["n.dll"], "updates": {updates}}}"#);
        let parsed = manifest::parse_native(&native).unwrap_or_else(|e| panic!("native {updates}: refused: {e}"));
        assert_eq!(parsed.updates, m.updates, "{updates}");
        let (schema, _) = lint_of(&dir, 1000 + n, "native.json", &native);
        assert_eq!(schema.is_empty(), !warned, "native {updates}: {schema:?}");
    }
    assert_eq!(readable, 5);
    // a provider this Kiln does not know is not the schema's business (a newer Kiln may know it): the reader's
    // warning is the one finding
    let text = r#"{"id": "future", "version": "1.0.0", "apiVersion": "0.2", "entryAssembly": "M.dll", "updates": {"provider": "gitlab", "project": "a/b"}}"#;
    let (schema, reader) = lint_of(&dir, 999, "kiln.mod.json", text);
    assert_eq!((schema.len(), reader.len()), (0, 1), "{schema:?} {reader:?}");
    assert!(reader[0].contains("provider 'gitlab' is not known"), "{}", reader[0]);
    let _ = std::fs::remove_dir_all(&dir);
}
