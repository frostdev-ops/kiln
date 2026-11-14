//! `kiln.publish.json`: the project's publishing config. Schema: `schema/kiln-publish.schema.json`.
//!
//! ```json
//! { "format": 1, "provider": "github",
//!   "github": { "repository": "owner/repo", "tag": "v{version}", "commit": "<40 hex>", "prerelease": false },
//!   "notes": "release-notes.md",
//!   "packageFiles": [ { "path": "config/defaults/my-mod.json", "sha256": "<64 hex>", "purpose": "default-config" } ] }
//! ```
//!
//! The file is written by hand, so the reader is strict: plain JSON with comments allowed, no key twice, no key it
//! does not know (a typo must not silently change what is published). It never holds a secret: the token is named
//! by the credential store, not written here.

use super::policy::{PackageFile, Purpose};
use crate::jsonc::{self, JValue, Node};
use crate::{Error, ErrorCode, Result};
use serde_json::json;

pub const FILE_NAME: &str = "kiln.publish.json";
pub const MAX_BYTES: usize = 256 * 1024;

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct PublishConfig {
    pub github: GithubDestination,
    /// Path of the release notes, relative to the config file. None: no notes.
    pub notes: Option<String>,
    pub package_files: Vec<PackageFile>,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct GithubDestination {
    /// `owner/repo`.
    pub repository: String,
    /// The tag with exactly one `{version}`, e.g. `v{version}` or `mods/my-mod/v{version}`.
    pub tag: String,
    /// The commit the tag is created at: 40 lower-case hex digits. Never a branch name.
    pub commit: String,
    pub prerelease: bool,
}

fn bad(what: impl std::fmt::Display) -> Error {
    Error::new(ErrorCode::Schema, format!("{FILE_NAME}: {what}"))
}

/// `owner/repo`: two segments of 1 to 100 characters `A-Z a-z 0-9 _ . -`, neither `.` nor `..`.
pub fn is_repository(s: &str) -> bool {
    let seg = |p: &str| !p.is_empty() && p.len() <= 100 && p != "." && p != ".." && p.bytes().all(|b| b.is_ascii_alphanumeric() || matches!(b, b'_' | b'.' | b'-'));
    matches!(s.split_once('/'), Some((o, r)) if seg(o) && seg(r))
}

/// A tag pattern: exactly one `{version}`; before and after it at most 64 characters of `A-Z a-z 0-9 . _ / -`; no
/// other brace, no `..`, no leading slash, no `//`.
pub fn is_tag_pattern(s: &str) -> bool {
    let Some((pre, post)) = s.split_once("{version}") else { return false };
    let part = |p: &str| p.len() <= 64 && p.bytes().all(|b| b.is_ascii_alphanumeric() || matches!(b, b'.' | b'_' | b'/' | b'-'));
    part(pre) && part(post) && !s.contains("..") && !s.starts_with('/') && !s.contains("//") && !post.ends_with('/')
}

pub fn is_commit(s: &str) -> bool {
    s.len() == 40 && s.bytes().all(|b| b.is_ascii_digit() || (b'a'..=b'f').contains(&b))
}

fn no_duplicates(n: &Node) -> Result<()> {
    match &n.value {
        JValue::Object(members) => {
            for (i, m) in members.iter().enumerate() {
                if members[..i].iter().any(|o| o.key == m.key) {
                    return Err(bad(format!("the key {} is given twice", m.key)));
                }
                no_duplicates(&m.value)?;
            }
        }
        JValue::Array(items) => items.iter().try_for_each(|e| no_duplicates(&e.node))?,
        _ => {}
    }
    Ok(())
}

fn only(o: &Node, at: &str, known: &[&str]) -> Result<()> {
    match o.members().iter().find(|m| !known.contains(&m.key.as_str())) {
        Some(m) => Err(bad(format!("{at}unknown key {}", m.key))),
        None => Ok(()),
    }
}

fn string(o: &Node, key: &str, at: &str) -> Result<String> {
    o.get(key).and_then(|n| n.as_str()).map(str::to_string).ok_or_else(|| bad(format!("{at}{key} must be a string")))
}

impl PublishConfig {
    pub fn parse(bytes: &[u8]) -> Result<PublishConfig> {
        if bytes.len() > MAX_BYTES {
            return Err(bad("the file is larger than 256 KiB"));
        }
        let text = std::str::from_utf8(bytes).map_err(|_| bad("the file is not UTF-8"))?;
        let text = text.strip_prefix('\u{feff}').unwrap_or(text);
        let root = jsonc::parse_tree(text).map_err(|e| Error::new(ErrorCode::Parse, format!("{FILE_NAME}: {e}")))?;
        no_duplicates(&root)?;
        if !root.is_object() {
            return Err(bad("the file must be an object"));
        }
        only(&root, "", &["$schema", "format", "provider", "github", "notes", "packageFiles"])?;
        if root.get("format").and_then(|n| n.as_number()) != Some("1") {
            return Err(bad("format must be 1"));
        }
        match string(&root, "provider", "")?.as_str() {
            "github" => {}
            "nexus" => return Err(Error::new(ErrorCode::Unsupported, format!("{FILE_NAME}: publishing to Nexus Mods is not part of this Kiln; use provider github"))),
            other => return Err(bad(format!("provider {other} is not known (github)"))),
        }
        let g = root.get("github").filter(|n| n.is_object()).ok_or_else(|| bad("github must be an object"))?;
        only(g, "github.", &["repository", "tag", "commit", "prerelease"])?;
        let github = GithubDestination {
            repository: string(g, "repository", "github.")?,
            tag: string(g, "tag", "github.")?,
            commit: string(g, "commit", "github.")?,
            prerelease: match g.get("prerelease") {
                None => false,
                Some(n) => n.as_bool().ok_or_else(|| bad("github.prerelease must be true or false"))?,
            },
        };
        if !is_repository(&github.repository) {
            return Err(bad("github.repository must be owner/repo"));
        }
        if !is_tag_pattern(&github.tag) {
            return Err(bad("github.tag must hold {version} once, e.g. v{version}"));
        }
        if !is_commit(&github.commit) {
            return Err(bad("github.commit must be a full commit id (40 lower-case hex digits), not a branch or a short id"));
        }
        let notes = match root.get("notes").filter(|n| !n.is_null()) {
            None => None,
            Some(n) => {
                let p = n.as_str().ok_or_else(|| bad("notes must be a path"))?;
                let norm = super::policy::normalize_name(p.as_bytes()).map_err(|_| bad("notes must be a path inside the project, relative to this file"))?;
                Some(norm)
            }
        };
        let mut package_files = Vec::new();
        if let Some(list) = root.get("packageFiles").filter(|n| !n.is_null()) {
            if !list.is_array() || list.elements().len() > 256 {
                return Err(bad("packageFiles must be a list of at most 256 entries"));
            }
            for (i, e) in list.elements().iter().enumerate() {
                let at = format!("packageFiles[{i}].");
                let o = &e.node;
                if !o.is_object() {
                    return Err(bad(format!("packageFiles[{i}] must be an object")));
                }
                only(o, &at, &["path", "sha256", "purpose", "license"])?;
                let purpose = match string(o, "purpose", &at)?.as_str() {
                    "default-config" => Purpose::DefaultConfig,
                    "dependency" => Purpose::Dependency,
                    other => return Err(bad(format!("{at}purpose {other} is not known (default-config, dependency)"))),
                };
                let path = super::policy::normalize_name(string(o, "path", &at)?.as_bytes()).map_err(|_| bad(format!("{at}path is not a path inside the mod")))?;
                let sha256 = string(o, "sha256", &at)?;
                if !crate::release_binding::is_sha256_hex(&sha256) {
                    return Err(bad(format!("{at}sha256 must be 64 lower-case hex digits")));
                }
                let license = match o.get("license").filter(|n| !n.is_null()) {
                    None => None,
                    Some(n) => Some(n.as_str().filter(|s| !s.trim().is_empty() && s.len() <= 128).ok_or_else(|| bad(format!("{at}license must be a short text")))?.to_string()),
                };
                if package_files.iter().any(|p: &PackageFile| p.path.eq_ignore_ascii_case(&path)) {
                    return Err(bad(format!("{at}path is listed twice")));
                }
                package_files.push(PackageFile { path, sha256, purpose, license });
            }
        }
        Ok(PublishConfig { github, notes, package_files })
    }

    /// The config as it is frozen into a transaction: every field that decides what is published, nothing else
    /// (no comments, no `$schema`). Canonical bytes of this value are `config.json`.
    pub fn effective(&self) -> serde_json::Value {
        let files: Vec<serde_json::Value> = self
            .package_files
            .iter()
            .map(|p| {
                let mut o = json!({"path": p.path, "sha256": p.sha256, "purpose": match p.purpose { Purpose::DefaultConfig => "default-config", Purpose::Dependency => "dependency" }});
                if let Some(l) = &p.license {
                    o["license"] = json!(l);
                }
                o
            })
            .collect();
        json!({
            "format": 1,
            "provider": "github",
            "github": {"repository": self.github.repository, "tag": self.github.tag, "commit": self.github.commit, "prerelease": self.github.prerelease},
            "notes": self.notes,
            "packageFiles": files,
        })
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    const COMMIT: &str = "0123456789abcdef0123456789abcdef01234567";

    fn doc(extra: &str) -> String {
        format!(r#"{{ // my mod
  "format": 1, "provider": "github",
  "github": {{ "repository": "example/my-mod", "tag": "v{{version}}", "commit": "{COMMIT}" }}{extra}
}}"#)
    }

    #[test]
    fn a_full_config_reads() {
        let sha = "a".repeat(64);
        let text = doc(&format!(r#", "notes": "docs/notes.md", "packageFiles": [
            {{"path": "config/defaults/my-mod.json", "sha256": "{sha}", "purpose": "default-config"}},
            {{"path": "lib\\Third.dll", "sha256": "{sha}", "purpose": "dependency", "license": "MIT"}}]"#));
        let c = PublishConfig::parse(text.as_bytes()).unwrap();
        assert_eq!(c.github, GithubDestination { repository: "example/my-mod".into(), tag: "v{version}".into(), commit: COMMIT.into(), prerelease: false });
        assert_eq!(c.notes.as_deref(), Some("docs/notes.md"));
        assert_eq!(c.package_files.len(), 2);
        assert_eq!(c.package_files[1].path, "lib/Third.dll");
        // the frozen form has no comment and is the same whatever the spacing of the source
        let again = PublishConfig::parse(text.replace("  ", " ").as_bytes()).unwrap();
        assert_eq!(super::super::canon::canonical(&c.effective()).unwrap(), super::super::canon::canonical(&again.effective()).unwrap());
        assert!(PublishConfig::parse(doc("").as_bytes()).unwrap().notes.is_none());
    }

    #[test]
    fn mistakes_are_refused() {
        let cases = [
            ("unknown key", doc(r#", "visiblity": "public""#)),
            ("key twice", doc(r#", "notes": "a.md", "notes": "b.md""#)),
            ("notes outside", doc(r#", "notes": "../secret.md""#)),
            ("notes absolute", doc(r#", "notes": "/etc/passwd""#)),
            ("format", doc("").replace("\"format\": 1", "\"format\": 2")),
            ("provider", doc("").replace("\"github\",", "\"gitlab\",")),
            ("repository url", doc("").replace("example/my-mod", "https://github.com/example/my-mod")),
            ("repository dots", doc("").replace("example/my-mod", "example/..")),
            ("tag without version", doc("").replace("v{version}", "latest")),
            ("tag twice", doc("").replace("v{version}", "{version}-{version}")),
            ("tag other brace", doc("").replace("v{version}", "v{version}{x}")),
            ("tag dots", doc("").replace("v{version}", "a/../v{version}")),
            ("commit branch", doc("").replace(COMMIT, "main")),
            ("commit short", doc("").replace(COMMIT, &COMMIT[..12])),
            ("commit upper", doc("").replace(COMMIT, &COMMIT.to_uppercase())),
            ("github unknown key", doc("").replace("\"tag\"", "\"token\": \"x\", \"tag\"")),
            ("package purpose", doc(&format!(r#", "packageFiles": [{{"path": "a.dll", "sha256": "{}", "purpose": "waive"}}]"#, "a".repeat(64)))),
            ("package sha", doc(r#", "packageFiles": [{"path": "a.dll", "sha256": "abc", "purpose": "dependency", "license": "MIT"}]"#)),
            ("package path", doc(&format!(r#", "packageFiles": [{{"path": "../a.dll", "sha256": "{}", "purpose": "dependency", "license": "MIT"}}]"#, "a".repeat(64)))),
            ("package twice", doc(&format!(r#", "packageFiles": [{{"path": "a.dll", "sha256": "{0}", "purpose": "dependency", "license": "MIT"}}, {{"path": "A.DLL", "sha256": "{0}", "purpose": "dependency", "license": "MIT"}}]"#, "a".repeat(64)))),
        ];
        for (what, text) in cases {
            assert!(PublishConfig::parse(text.as_bytes()).is_err(), "accepted: {what}");
        }
        let nexus = doc("").replace("\"github\",", "\"nexus\",");
        assert_eq!(PublishConfig::parse(nexus.as_bytes()).unwrap_err().code, ErrorCode::Unsupported);
    }

    /// The schema file and the reader agree on the example and on the key lists.
    #[test]
    fn the_schema_example_reads_and_the_schema_lists_the_same_keys() {
        let root = std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join("../../schema");
        let c = PublishConfig::parse(&std::fs::read(root.join("examples/kiln.publish.json")).unwrap()).unwrap();
        assert_eq!(c.package_files.len(), 1);
        let schema: serde_json::Value = serde_json::from_slice(&std::fs::read(root.join("kiln-publish.schema.json")).unwrap()).unwrap();
        let keys = |v: &serde_json::Value| -> Vec<String> {
            let mut k: Vec<String> = v["properties"].as_object().unwrap().keys().cloned().collect();
            k.sort();
            k
        };
        assert_eq!(keys(&schema), ["$schema", "format", "github", "notes", "packageFiles", "provider"]);
        assert_eq!(keys(&schema["properties"]["github"]), ["commit", "prerelease", "repository", "tag"]);
        assert_eq!(keys(&schema["properties"]["packageFiles"]["items"]), ["license", "path", "purpose", "sha256"]);
        assert_eq!(schema["additionalProperties"], false);
    }

    #[test]
    fn grammars() {
        assert!(is_repository("a/b") && is_repository("Example-Org/my.mod_2") && !is_repository("a") && !is_repository("a/b/c") && !is_repository("a/") && !is_repository("a b/c"));
        assert!(is_tag_pattern("v{version}") && is_tag_pattern("{version}") && is_tag_pattern("mods/example/v{version}") && is_tag_pattern("v{version}-kiln"));
        assert!(!is_tag_pattern("/v{version}") && !is_tag_pattern("a//v{version}") && !is_tag_pattern("v{version}/") && !is_tag_pattern("v {version}") && !is_tag_pattern(&format!("{}{{version}}", "a".repeat(65))));
    }
}
