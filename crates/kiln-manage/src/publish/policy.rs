//! The artifact policy of `kiln publish`: what a published mod zip looks like and what it may never hold. Pure rules
//! over names and bytes; [`super::artifact`] applies them to a zip.
//!
//! A refusal has a stable [`Refusal::reason`] (for scripts and tests) and names the member. The policy is
//! deliberately one-sided: it refuses what is known to be wrong and says so; passing it is not proof that a zip is
//! fine to publish (a file name is no proof of who owns its content). The plan step keeps a human in the loop.
//!
//! Changing a rule changes [`POLICY_VERSION`]: a plan records the version it was audited with.

use crate::{Error, ErrorCode};

/// Bumped with every change of a rule or a limit in this file.
pub const POLICY_VERSION: u32 = 1;

pub const MAX_ZIP_BYTES: u64 = 512 * 1024 * 1024;
pub const MAX_EXPANDED_BYTES: u64 = 2 * 1024 * 1024 * 1024;
pub const MAX_MEMBERS: usize = 4096;
pub const MAX_MEMBER_BYTES: u64 = 256 * 1024 * 1024;
/// Expanded bytes per compressed byte, per member and for the whole zip.
pub const MAX_RATIO: u64 = 100;
/// Path components of a member, the mod's root folder included.
pub const MAX_DEPTH: usize = 16;

/// Why a zip was refused.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Refusal {
    /// A stable code: `encrypted`, `game-data`, `private-key`, ...
    pub reason: &'static str,
    /// The member (its name in the zip), when the refusal is about one.
    pub member: Option<String>,
    pub message: String,
}

impl Refusal {
    pub fn new(reason: &'static str, member: Option<&str>, message: impl Into<String>) -> Refusal {
        Refusal { reason, member: member.map(str::to_string), message: message.into() }
    }
}

impl From<Refusal> for Error {
    fn from(r: Refusal) -> Error {
        let text = match &r.member {
            Some(m) => format!("The zip cannot be published: {m}: {}.", r.message),
            None => format!("The zip cannot be published: {}.", r.message),
        };
        Error::new(ErrorCode::Refused, text).with_detail(serde_json::json!({"reason": r.reason, "member": r.member, "policyVersion": POLICY_VERSION}))
    }
}

/// The refusal inside an [`Error`] made from one (its `detail.reason`).
pub fn reason_of(e: &Error) -> Option<&str> {
    e.detail.as_ref()?.get("reason")?.as_str()
}

/// A member name as the policy sees it: `/` separators, no trailing slash. Refuses names that are not UTF-8, absolute,
/// drive or UNC paths, `.` and `..` components, empty components, NUL and control characters, components Windows
/// would rename (a trailing dot or space), a colon (drive letters, alternate streams) and paths deeper than
/// [`MAX_DEPTH`].
pub fn normalize_name(raw: &[u8]) -> Result<String, Refusal> {
    let shown = String::from_utf8_lossy(raw).into_owned();
    let bad = |reason: &'static str, why: &str| Err(Refusal::new(reason, Some(&shown), why.to_string()));
    let Ok(name) = std::str::from_utf8(raw) else { return bad("name-not-utf8", "its name is not UTF-8") };
    if name.chars().any(|c| (c as u32) < 32 || c as u32 == 127) {
        return bad("path-control", "its name has a control character");
    }
    let name = name.replace('\\', "/");
    if name.starts_with("//") {
        return bad("path-absolute", "its name is a network path");
    }
    if name.starts_with('/') {
        return bad("path-absolute", "its name is an absolute path");
    }
    if name.contains(':') {
        return bad("path-drive", "its name has a drive letter or a stream name");
    }
    let trimmed = name.strip_suffix('/').unwrap_or(&name);
    if trimmed.is_empty() {
        return bad("path-dots", "its name is empty");
    }
    let mut depth = 0;
    for part in trimmed.split('/') {
        if part.is_empty() || part == "." || part == ".." {
            return bad("path-dots", "its name has an empty, . or .. part");
        }
        if part.ends_with(['.', ' ']) {
            return bad("path-dots", "a part of its name ends with a dot or a space, which Windows renames");
        }
        depth += 1;
    }
    if depth > MAX_DEPTH {
        return bad("depth", "it is nested too deeply");
    }
    Ok(trimmed.to_string())
}

/// What an entry of `packageFiles` (kiln.publish.json) allows.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Purpose {
    /// A shipped default config under `config/defaults/`.
    DefaultConfig,
    /// A third-party DLL the mod needs.
    Dependency,
}

/// One declared file: its path inside the mod folder, its SHA-256 and why it is there.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct PackageFile {
    /// Relative to the mod's root folder, `/` separated.
    pub path: String,
    pub sha256: String,
    pub purpose: Purpose,
    /// Required for a dependency: the licence it is shipped under.
    pub license: Option<String>,
}

fn ext(lower: &str) -> &str {
    let base = lower.rsplit('/').next().unwrap_or(lower);
    base.rsplit_once('.').map(|(_, e)| e).unwrap_or("")
}

fn base(lower: &str) -> &str {
    lower.rsplit('/').next().unwrap_or(lower)
}

/// Runtime DLLs that are never part of a mod, whatever a declaration says.
pub fn is_runtime_dll(base_lower: &str) -> bool {
    if !base_lower.ends_with(".dll") {
        return false;
    }
    let stem = &base_lower[..base_lower.len() - 4];
    base_lower.starts_with("kiln.") || base_lower.starts_with("system.") || matches!(stem, "coreclr" | "hostfxr" | "hostpolicy" | "dbgshim" | "netcoredbg" | "mscorlib" | "netstandard" | "clrjit" | "dinput8" | "kiln-core")
}

/// Text and config extensions: where the secret-file name patterns apply.
fn is_text_ext(e: &str) -> bool {
    matches!(e, "" | "txt" | "json" | "jsonc" | "yaml" | "yml" | "toml" | "ini" | "cfg" | "conf" | "config" | "xml" | "env" | "properties" | "md")
}

fn secret_basename(b: &str) -> bool {
    let stem_is = |word: &str| b == word || b.strip_prefix(word).is_some_and(|rest| rest.starts_with(['.', '_', '-']));
    stem_is("credentials") || stem_is("secrets") || stem_is("token")
}

/// The refusal for a member by its path alone (relative to the mod's root folder, lower case), or None. DLLs,
/// default configs and content are judged elsewhere.
pub fn refuse_by_path(rel_lower: &str) -> Option<(&'static str, &'static str)> {
    let parts: Vec<&str> = rel_lower.split('/').collect();
    let b = base(rel_lower);
    let e = ext(rel_lower);
    // the game's own archives and anything named like them
    if matches!(e, "kfc" | "kfc_resources" | "kfc_dir") || b.starts_with("enshrouded.kfc") {
        return Some(("game-data", "it is one of the game's data archives"));
    }
    if matches!(b, "enshrouded.exe" | "enshrouded_server.exe" | "steam_api64.dll" | "steamclient64.dll") {
        return Some(("game-data", "it is a file of the game"));
    }
    // cooked output: made from the player's own game files on the player's PC, never shipped
    if rel_lower.starts_with("assets/cooked/") || parts.iter().any(|p| *p == "cooked") {
        return Some(("cooked-output", "it is cooked output (made from the game's files at install time, not shipped)"));
    }
    // saves and world data
    if matches!(e, "db" | "sqlite" | "sqlite3" | "sav" | "save" | "bak") || b.ends_with("-index") || b.contains("characters.json") {
        return Some(("save-data", "it looks like a save or a database"));
    }
    if matches!(parts[0], "worlds" | "characters" | "savegames" | "savegame" | "logs" | "state" | "data")
        || rel_lower.starts_with("kiln/data/")
        || rel_lower.starts_with("kiln/logs/")
        || rel_lower.starts_with("kiln/state/")
    {
        return Some(("runtime-state", "it is in a folder of player or server state"));
    }
    if parts.iter().any(|p| matches!(*p, ".git" | ".svn" | ".hg" | "bin" | "obj" | "node_modules" | ".vs" | ".idea")) {
        return Some(("vcs-or-build-folder", "it is in a version-control or build folder"));
    }
    if b.starts_with(".env") {
        return Some(("secret-file", "it is an environment file"));
    }
    if matches!(e, "pfx" | "p12" | "key" | "pem" | "jks" | "keystore") || b.starts_with("id_rsa") || b.starts_with("id_ed25519") || b.starts_with("id_ecdsa") {
        return Some(("secret-file", "it is a key or certificate store"));
    }
    if is_text_ext(e) && secret_basename(b) {
        return Some(("secret-file", "its name says it holds credentials"));
    }
    if matches!(e, "pdb") {
        return Some(("vcs-or-build-folder", "it is a debug symbol file"));
    }
    None
}

/// A config key that names a secret: password, token, secret, apiKey, credential (any case, `_` and `-` ignored).
pub fn is_secret_key(key: &str) -> bool {
    let k: String = key.chars().filter(|c| *c != '_' && *c != '-' && *c != '.').flat_map(char::to_lowercase).collect();
    ["password", "passwd", "token", "secret", "apikey", "credential", "privatekey"].iter().any(|w| k.contains(w))
}

/// The first key path in a JSON value whose key names a secret and whose value is not empty (null, "", [] and {}
/// are placeholders and allowed).
pub fn secret_value(v: &serde_json::Value, at: &str) -> Option<String> {
    use serde_json::Value;
    let empty = |v: &Value| match v {
        Value::Null => true,
        Value::String(s) => s.is_empty(),
        Value::Array(a) => a.is_empty(),
        Value::Object(o) => o.is_empty(),
        _ => false,
    };
    match v {
        Value::Object(o) => o.iter().find_map(|(k, child)| {
            let here = if at.is_empty() { k.clone() } else { format!("{at}.{k}") };
            if is_secret_key(k) && !empty(child) {
                return Some(here);
            }
            secret_value(child, &here)
        }),
        Value::Array(a) => a.iter().enumerate().find_map(|(i, child)| secret_value(child, &format!("{at}[{i}]"))),
        _ => None,
    }
}

/// Finds a private-key PEM header in a stream of chunks. Feed every chunk; a header split over two chunks is found.
#[derive(Default)]
pub struct PemScan {
    tail: Vec<u8>,
    pub found: bool,
}

impl PemScan {
    const BEGIN: &'static [u8] = b"-----BEGIN ";
    const KEY: &'static [u8] = b"PRIVATE KEY-----";
    /// The longest header: `-----BEGIN ` + up to 32 bytes of kind (`ENCRYPTED `, `OPENSSH `, `RSA `, `EC `) + KEY.
    const WINDOW: usize = 11 + 32 + 16;

    pub fn feed(&mut self, chunk: &[u8]) {
        if self.found {
            return;
        }
        let mut buf = std::mem::take(&mut self.tail);
        buf.extend_from_slice(chunk);
        let mut i = 0;
        while let Some(p) = find(&buf[i..], Self::BEGIN) {
            let start = i + p + Self::BEGIN.len();
            let end = (start + 32 + Self::KEY.len()).min(buf.len());
            if let Some(k) = find(&buf[start..end], Self::KEY) {
                // only letters and spaces between BEGIN and PRIVATE KEY
                if buf[start..start + k].iter().all(|b| b.is_ascii_uppercase() || *b == b' ') {
                    self.found = true;
                    return;
                }
            }
            i = start;
        }
        let keep = buf.len().min(Self::WINDOW);
        self.tail = buf[buf.len() - keep..].to_vec();
    }
}

fn find(hay: &[u8], needle: &[u8]) -> Option<usize> {
    hay.windows(needle.len()).position(|w| w == needle)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn names_are_normalized_or_refused() {
        assert_eq!(normalize_name(b"mod\\assets\\a.json").unwrap(), "mod/assets/a.json");
        assert_eq!(normalize_name(b"mod/sub/").unwrap(), "mod/sub");
        assert_eq!(normalize_name("mod/gr\u{fc}n.json".as_bytes()).unwrap(), "mod/gr\u{fc}n.json");
        let cases: &[(&[u8], &str)] = &[
            (b"/etc/passwd", "path-absolute"),
            (b"\\\\server\\share\\x", "path-absolute"),
            (b"//server/share/x", "path-absolute"),
            (b"C:/x", "path-drive"),
            (b"c:\\x", "path-drive"),
            (b"mod/a.txt:stream", "path-drive"),
            (b"mod/../x", "path-dots"),
            (b"../x", "path-dots"),
            (b"mod/./x", "path-dots"),
            (b"mod//x", "path-dots"),
            (b"mod/x./y", "path-dots"),
            (b"mod/x /y", "path-dots"),
            (b"mod/.. /y", "path-dots"),
            (b"", "path-dots"),
            (b"/", "path-absolute"),
            (b"mod/a\x00b", "path-control"),
            (b"mod/a\nb", "path-control"),
            (b"mod/\xff\xfe", "name-not-utf8"),
        ];
        for (raw, reason) in cases {
            assert_eq!(normalize_name(raw).unwrap_err().reason, *reason, "{:?}", String::from_utf8_lossy(raw));
        }
        let deep = vec!["d"; MAX_DEPTH].join("/");
        assert!(normalize_name(deep.as_bytes()).is_ok());
        assert_eq!(normalize_name(format!("{deep}/x").as_bytes()).unwrap_err().reason, "depth");
    }

    #[test]
    fn forbidden_paths_have_their_reason() {
        let cases = [
            ("assets/enshrouded.kfc", "game-data"),
            ("x/data.kfc_resources", "game-data"),
            ("enshrouded_server.exe", "game-data"),
            ("assets/cooked/model.bin", "cooked-output"),
            ("impact/cooked/x.bin", "cooked-output"),
            ("world.db", "save-data"),
            ("saves/3ad85aea-index", "save-data"),
            ("characters/a.json", "runtime-state"),
            ("savegames/x", "runtime-state"),
            ("logs/kiln.log", "runtime-state"),
            ("kiln/data/x.json", "runtime-state"),
            (".git/config", "vcs-or-build-folder"),
            ("src/obj/x.txt", "vcs-or-build-folder"),
            ("bin/release/x.txt", "vcs-or-build-folder"),
            ("mymod.pdb", "vcs-or-build-folder"),
            (".env", "secret-file"),
            (".env.local", "secret-file"),
            ("cert.pfx", "secret-file"),
            ("store.p12", "secret-file"),
            ("server.key", "secret-file"),
            ("id_rsa", "secret-file"),
            ("id_rsa.pub", "secret-file"),
            ("credentials.json", "secret-file"),
            ("credentials", "secret-file"),
            ("secrets-prod.yaml", "secret-file"),
            ("token_github.txt", "secret-file"),
        ];
        for (path, reason) in cases {
            assert_eq!(refuse_by_path(path).map(|r| r.0), Some(reason), "{path}");
        }
        for ok in ["kiln.mod.json", "mymod.dll", "assets/assets.json", "assets/cook.json", "blocks/items.json", "locale/en.json", "readme.md", "tokens.png", "assets/tokenizer.json", "icons/secretdoor.png", "config/defaults/mymod.json", "assets/models/key.glb"] {
            assert_eq!(refuse_by_path(ok), None, "{ok}");
        }
    }

    #[test]
    fn runtime_dlls_are_known() {
        for n in ["kiln.api.dll", "kiln.items.dll", "system.text.json.dll", "coreclr.dll", "hostfxr.dll", "dbgshim.dll", "netcoredbg.dll", "kiln-core.dll", "dinput8.dll"] {
            assert!(is_runtime_dll(n), "{n}");
        }
        for n in ["mymod.dll", "newtonsoft.json.dll", "kilnhelper.dll", "systematic.dll", "kiln.api.txt"] {
            assert!(!is_runtime_dll(n), "{n}");
        }
    }

    #[test]
    fn secret_keys_with_values_are_found_and_placeholders_pass() {
        let v = serde_json::json!({"webhook": {"url": "https://example.com", "API_Key": "abc"}, "limit": 3});
        assert_eq!(secret_value(&v, "").as_deref(), Some("webhook.API_Key"));
        let v = serde_json::json!({"list": [{"name": "a"}, {"Password": "hunter2"}]});
        assert_eq!(secret_value(&v, "").as_deref(), Some("list[1].Password"));
        let v = serde_json::json!({"apiKey": "", "token": null, "secrets": {}, "client-secret": [], "name": "x"});
        assert_eq!(secret_value(&v, ""), None);
        assert!(is_secret_key("accessToken") && is_secret_key("client_secret") && is_secret_key("api-key") && is_secret_key("Credentials"));
        assert!(!is_secret_key("name") && !is_secret_key("keybind") && !is_secret_key("apiVersion"));
    }

    #[test]
    fn a_private_key_header_is_found_also_across_chunks() {
        for header in ["-----BEGIN PRIVATE KEY-----", "-----BEGIN RSA PRIVATE KEY-----", "-----BEGIN OPENSSH PRIVATE KEY-----", "-----BEGIN ENCRYPTED PRIVATE KEY-----", "-----BEGIN EC PRIVATE KEY-----"] {
            let text = format!("{}{header}\nMIIE...", "x".repeat(1000));
            let mut one = PemScan::default();
            one.feed(text.as_bytes());
            assert!(one.found, "{header}");
            // every split point
            for cut in 990..text.len().min(1050) {
                let mut s = PemScan::default();
                s.feed(&text.as_bytes()[..cut]);
                s.feed(&text.as_bytes()[cut..]);
                assert!(s.found, "{header} cut at {cut}");
            }
            // byte by byte
            let mut s = PemScan::default();
            for b in text.as_bytes() {
                s.feed(std::slice::from_ref(b));
            }
            assert!(s.found, "{header} byte by byte");
        }
        for clean in ["-----BEGIN CERTIFICATE-----", "-----BEGIN PUBLIC KEY-----", "PRIVATE KEY-----", "-----BEGIN something else and much later PRIVATE KEY-----", "-----BEGIN -----BEGIN CERT"] {
            let mut s = PemScan::default();
            s.feed(clean.as_bytes());
            assert!(!s.found, "{clean}");
        }
    }
}
