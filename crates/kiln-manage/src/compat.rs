//! Closed-source DLL mods next to Kiln (dev.21, launcher side): compat rules, the last session's hook map
//! (`<game>/kiln/state/hooks.json`) and its overlaps, and what a profile's activation writes into chain.ini
//! ([profile], [late], rule switches). Formats: docs/kiln/engines/README.md "Closed DLLs"; schemas
//! `schema/compat-rules.schema.json` and `schema/hooks-state.schema.json` (kiln-core writes hooks.json and evaluates
//! the same rules at boot; the launcher applies `late` and `disable` on chain entries when it writes chain.ini).
//!
//! - [`parse_rules`] / [`load_rules`]: `<game>/kiln/compat-rules.json` (shipped with Kiln) then
//!   `<configDir>/compat-rules.json` (the player's; a rule with the id of a shipped one replaces it).
//! - [`evaluate`]: which rules fire for a set of [`Facts`] and which chain entries they move or switch off.
//! - [`read_hooks`] / [`overlaps`]: the last session's hooks.json and its overlaps (derived from the targets when the
//!   core wrote none).
//! - [`plan`]: the chain.ini lines of a profile; [`view`]: the same plus overlaps mapped to chain entries and Library
//!   items, for the launcher's profile page.

use crate::chain::{self, ProfilePlan};
use crate::game::GameInstall;
use crate::manifest::{ModKind, NativePhase};
use crate::paths::DataDirs;
use crate::profile::Profile;
use crate::{Error, ErrorCode, Result};
use serde::{Deserialize, Deserializer, Serialize};
use std::path::{Path, PathBuf};

/// `<game>/kiln/state/hooks.json` (kiln.hooks' map of the last session).
pub const HOOKS_JSON: &str = "kiln/state/hooks.json";
/// The rules Kiln ships, in the game: `<game>/kiln/compat-rules.json`.
pub const SHIPPED_RULES: &str = "kiln/compat-rules.json";
/// The player's rules: `<configDir>/compat-rules.json`.
pub const RULES_FILE: &str = "compat-rules.json";
/// Larger rule files are refused (a player's file, read on every activation).
pub const MAX_RULES_BYTES: u64 = 1 << 20;
pub const MAX_RULES: usize = 512;
/// Conditions in one rule.
pub const MAX_WHEN: usize = 8;
/// A module glob with wildcards: at most this long and with at most [`MAX_STARS`] `*`.
pub const MAX_GLOB: usize = 64;
pub const MAX_STARS: usize = 4;
/// hooks.json is written by the game's core, but any mod in the process can shape its module names; larger files are
/// not read.
pub const MAX_HOOKS_BYTES: u64 = 8 << 20;
/// Overlaps shown, owners per overlap, and the facts a start can add (module names of at most 260 characters).
pub const MAX_OVERLAPS: usize = 512;
const MAX_OWNERS: usize = 16;
const MAX_FACT_MODULES: usize = 256;
const MAX_FACT_SYMBOLS: usize = 1024;
const MAX_NAME: usize = 260;

/// The file's bytes; an error when it is larger than `max` (checked on what was read: a special file reports no size).
fn read_capped(path: &Path, max: u64) -> std::io::Result<Vec<u8>> {
    use std::io::Read;
    let mut buf = Vec::new();
    std::fs::File::open(path)?.take(max + 1).read_to_end(&mut buf)?;
    if buf.len() as u64 > max {
        return Err(std::io::Error::new(std::io::ErrorKind::InvalidData, format!("larger than {max} bytes")));
    }
    Ok(buf)
}

// ---------------------------------------------------------------------------------------------------------------------
// compat rules

/// One condition. Exactly one key: `{"module": "xhl-*.dll"}` (a DLL file name, case ignored, `*` and `?` globs),
/// `{"engine": "shroudforge"}` (an engine word of engines.rs), `{"mod": "emf:foo"}` (`<engine>:<mod id>`; Kiln's
/// native mods are `kiln:<id>` or `native:<id>`), `{"symbol": "ChatLogAdd"}` (an overlap on that sigdb symbol in the
/// last session).
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "lowercase", deny_unknown_fields)]
pub enum Matcher {
    Module(String),
    Engine(String),
    Mod(String),
    Symbol(String),
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum RuleAction {
    /// Log and show the message.
    Warn,
    /// Load the subject's chain entries after Kiln and ShroudForge (chain.ini [late]).
    Late,
    /// Switch the subject off: its chain entries (launcher) or the engine mod (kiln-core skips it).
    Disable,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct Rule {
    pub id: String,
    /// Every matcher must hold this start (none: always).
    #[serde(default)]
    pub when: Vec<Matcher>,
    pub action: RuleAction,
    /// What `late` / `disable` act on (`warn` may leave it out).
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub subject: Option<Matcher>,
    #[serde(default)]
    pub message: String,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum RuleSource {
    /// `<game>/kiln/compat-rules.json`
    Kiln,
    /// `<configDir>/compat-rules.json`
    User,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SourcedRule {
    #[serde(flatten)]
    pub rule: Rule,
    pub source: RuleSource,
}

/// The rules of a file, and why the others were left out. A file that is not JSON, or not format 1, gives no rules
/// and one error; a bad rule is left out with its error; the others still count.
pub fn parse_rules(bytes: &[u8], source: RuleSource) -> (Vec<SourcedRule>, Vec<String>) {
    let mut errors = Vec::new();
    let bytes = bytes.strip_prefix(b"\xef\xbb\xbf").unwrap_or(bytes);
    let v: serde_json::Value = match serde_json::from_slice(bytes) {
        Ok(v) => v,
        Err(e) => return (Vec::new(), vec![format!("not JSON: {e}")]),
    };
    if v.get("format").and_then(|f| f.as_u64()) != Some(1) {
        return (Vec::new(), vec!["\"format\" must be 1".into()]);
    }
    let Some(list) = v.get("rules").and_then(|r| r.as_array()) else {
        return (Vec::new(), if v.get("rules").is_some() { vec!["\"rules\" must be a list".into()] } else { Vec::new() });
    };
    let mut out: Vec<SourcedRule> = Vec::new();
    for (i, r) in list.iter().enumerate() {
        // the first MAX_RULES entries of the list count (schema maxItems; kiln-core drops the same ones, with this line)
        if i >= MAX_RULES {
            errors.push(format!("more than {MAX_RULES} rules: the last {} are ignored", list.len() - MAX_RULES));
            break;
        }
        // a matcher is an object: `"subject": null` is not one (serde would read it as no subject)
        if r.get("subject").is_some_and(|s| s.is_null()) {
            let id = r.get("id").and_then(|x| x.as_str()).map(|s| format!(" ({s})")).unwrap_or_default();
            errors.push(format!("rule {}{id}: \"subject\" must be a matcher", i + 1));
            continue;
        }
        let rule: Rule = match serde_json::from_value(r.clone()) {
            Ok(rule) => rule,
            Err(e) => {
                let id = r.get("id").and_then(|x| x.as_str()).map(|s| format!(" ({s})")).unwrap_or_default();
                errors.push(format!("rule {}{id}: {e}", i + 1));
                continue;
            }
        };
        if let Err(e) = check_rule(&rule) {
            errors.push(format!("rule {} ({}): {e}", i + 1, rule.id));
            continue;
        }
        if out.iter().any(|o| o.rule.id == rule.id) {
            errors.push(format!("rule {} ({}): the id is used twice; the first one counts", i + 1, rule.id));
            continue;
        }
        out.push(SourcedRule { rule, source });
    }
    (out, errors)
}

fn check_rule(r: &Rule) -> std::result::Result<(), String> {
    if r.id.is_empty() || r.id.len() > 128 || !r.id.chars().all(|c| c.is_ascii_alphanumeric() || "._-:".contains(c)) {
        return Err("\"id\" must be 1-128 characters: letters, digits, . _ - :".into());
    }
    if r.message.chars().count() > 2000 {
        return Err("\"message\" is longer than 2000 characters".into());
    }
    if r.action != RuleAction::Warn && r.subject.is_none() {
        return Err(format!("action {:?} needs a \"subject\"", r.action).to_lowercase());
    }
    if r.when.len() > MAX_WHEN {
        return Err(format!("more than {MAX_WHEN} conditions in \"when\""));
    }
    for m in r.when.iter().chain(&r.subject) {
        let text = match m {
            Matcher::Module(s) | Matcher::Engine(s) | Matcher::Mod(s) | Matcher::Symbol(s) => s,
        };
        // characters, as the schema's maxLength (and kiln-core) count them
        if text.is_empty() || text.chars().count() > 260 {
            return Err("a matcher's text must be 1-260 characters".into());
        }
        if matches!(m, Matcher::Module(g) if g.contains(['*', '?']) && (g.chars().count() > MAX_GLOB || g.matches('*').count() > MAX_STARS)) {
            return Err(format!("a module pattern with wildcards has at most {MAX_GLOB} characters and {MAX_STARS} *"));
        }
        if let Matcher::Mod(s) = m
            && !s.contains(':')
        {
            return Err(format!("mod matcher {s:?} must be <engine>:<mod id>"));
        }
    }
    Ok(())
}

/// Shipped rules then the player's (a player rule with a shipped rule's id replaces it). Missing files count as
/// empty; unreadable or broken ones give errors ("<file>: <why>"), never a failure.
pub fn load_rules(files: &[(PathBuf, RuleSource)]) -> (Vec<SourcedRule>, Vec<String>) {
    let mut rules: Vec<SourcedRule> = Vec::new();
    let mut errors = Vec::new();
    for (path, source) in files {
        let bytes = match read_capped(path, MAX_RULES_BYTES) {
            Ok(b) => b,
            Err(e) if e.kind() == std::io::ErrorKind::NotFound => continue,
            Err(e) => {
                errors.push(format!("{}: {e}", path.display()));
                continue;
            }
        };
        let (rs, es) = parse_rules(&bytes, *source);
        errors.extend(es.into_iter().map(|e| format!("{}: {e}", path.display())));
        for r in rs {
            match rules.iter_mut().find(|o| o.rule.id == r.rule.id) {
                Some(o) => *o = r,
                None => rules.push(r),
            }
        }
    }
    (rules, errors)
}

/// What is present this start, for the `when` matchers. Module names and engine words in lower case.
#[derive(Debug, Clone, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Facts {
    /// DLL file names: chain entries, DLLs under a proxy name next to the game, foreign modules of the last session.
    pub modules: Vec<String>,
    /// Engine words (engines.rs `word`; "kiln" always).
    pub engines: Vec<String>,
    /// `<engine>:<mod id>` of the profile's enabled mods.
    pub mods: Vec<String>,
    /// Symbols with an overlap in the last session.
    pub symbols: Vec<String>,
}

impl Facts {
    fn add_module(&mut self, name: &str) {
        let n = file_name(name).to_lowercase();
        if !n.is_empty() && n.len() <= MAX_NAME && self.modules.len() < MAX_FACT_MODULES && !self.modules.contains(&n) {
            self.modules.push(n);
        }
    }
    fn add_symbol(&mut self, name: &str) {
        if !name.is_empty() && name.len() <= MAX_NAME && self.symbols.len() < MAX_FACT_SYMBOLS && !self.symbols.iter().any(|s| s == name) {
            self.symbols.push(name.to_string());
        }
    }
    fn add_engine(&mut self, word: &str) {
        let w = word.to_lowercase();
        if !self.engines.contains(&w) {
            self.engines.push(w);
        }
    }

    pub fn holds(&self, m: &Matcher) -> bool {
        match m {
            Matcher::Module(g) => self.modules.iter().any(|n| glob(g, n)),
            Matcher::Engine(w) => self.engines.iter().any(|e| e.eq_ignore_ascii_case(w)),
            Matcher::Mod(s) => {
                let want = canon_mod(s);
                self.mods.iter().any(|m| canon_mod(m) == want)
            }
            Matcher::Symbol(s) => self.symbols.iter().any(|x| x == s),
        }
    }
}

/// `native:x` and `kiln:x` name the same mod; the engine word ignores case, the id does not.
fn canon_mod(s: &str) -> String {
    let (e, id) = s.split_once(':').unwrap_or(("", s));
    let e = e.to_ascii_lowercase();
    let e = if e == "native" || e == "plugin" { "kiln".to_string() } else { e };
    format!("{e}:{id}")
}

/// The file name of a game-side or host path ('\' or '/').
pub fn file_name(p: &str) -> &str {
    p.rsplit(['\\', '/']).next().unwrap_or(p)
}

/// `*` (any run) and `?` (one character) glob, ASCII case ignored.
pub fn glob(pattern: &str, name: &str) -> bool {
    // no file name is longer (Windows: 255), and rules are checked to be short: bounds the work of a match
    if name.chars().count() > MAX_NAME || pattern.chars().count() > MAX_NAME {
        return false;
    }
    if !pattern.contains(['*', '?']) {
        return pattern.to_lowercase() == name.to_lowercase();
    }
    let p: Vec<char> = pattern.to_lowercase().chars().collect();
    let n: Vec<char> = name.to_lowercase().chars().collect();
    let (mut pi, mut ni) = (0usize, 0usize);
    let (mut star, mut mark) = (None::<usize>, 0usize);
    while ni < n.len() {
        if pi < p.len() && (p[pi] == '?' || p[pi] == n[ni]) {
            pi += 1;
            ni += 1;
        } else if pi < p.len() && p[pi] == '*' {
            star = Some(pi);
            mark = ni;
            pi += 1;
        } else if let Some(s) = star {
            pi = s + 1;
            mark += 1;
            ni = mark;
        } else {
            return false;
        }
    }
    while pi < p.len() && p[pi] == '*' {
        pi += 1;
    }
    pi == p.len()
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum Section {
    /// chain.ini [chain]: proxies the installer moved aside (game-wide).
    Chain,
    /// chain.ini [profile]: the profile's native mods with phase dllmain.
    Profile,
}

/// One DLL the proxy would load from chain.ini.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PlanEntry {
    /// As in chain.ini ('\', relative to `<game>/kiln/` unless absolute).
    pub path: String,
    pub file: String,
    pub section: Section,
    /// [chain]: the engine word its markers name (None: unknown).
    pub engine: Option<String>,
    /// [profile]: the native mod's id.
    pub mod_id: Option<String>,
}

impl PlanEntry {
    /// Kiln's own modules (`kiln-*.dll`) and a Kiln or ShroudForge proxy: a rule never moves or switches these off
    /// (the proxy refuses them in the chain anyway).
    pub fn is_kiln_own(&self) -> bool {
        let f = self.file.to_ascii_lowercase();
        matches!(self.engine.as_deref(), Some("kiln" | "shroudforge")) || (f.starts_with("kiln-") && f.ends_with(".dll"))
    }

    /// EMF's own loader (a [chain] entry). It starts EMF itself, and kiln-emf decides when Kiln's plugins start whether
    /// the real EMF is in the process, so Kiln's proxy never defers it, whatever chain.ini [late] says. No rule moves it
    /// late and the profile page does not offer the switch.
    pub fn is_emf_loader(&self) -> bool {
        self.section == Section::Chain && self.engine.as_deref() == Some("emf")
    }

    fn matches(&self, m: &Matcher) -> bool {
        match m {
            Matcher::Module(g) => glob(g, &self.file),
            Matcher::Engine(w) => self.engine.as_deref().is_some_and(|e| e.eq_ignore_ascii_case(w)),
            Matcher::Mod(s) => self.mod_id.as_ref().is_some_and(|id| canon_mod(s) == canon_mod(&format!("kiln:{id}"))),
            Matcher::Symbol(_) => false,
        }
    }
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct RuleResult {
    pub id: String,
    pub source: RuleSource,
    pub action: RuleAction,
    pub message: String,
    /// Every `when` matcher held.
    pub fired: bool,
    /// The launcher applies it: it fired and its subject names chain entries (`late` / `disable`), or it is a `warn`
    /// that fired.
    pub applied: bool,
    /// The chain entries it moves or switches off.
    pub targets: Vec<String>,
    /// Why a fired rule is not applied by the launcher (e.g. kiln-core skips an engine mod itself).
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub note: Option<String>,
}

/// Every rule against the facts and the chain entries, in file order.
pub fn evaluate(rules: &[SourcedRule], facts: &Facts, entries: &[PlanEntry]) -> Vec<RuleResult> {
    rules
        .iter()
        .map(|sr| {
            let r = &sr.rule;
            let fired = r.when.iter().all(|m| facts.holds(m));
            let mut res = RuleResult {
                id: r.id.clone(),
                source: sr.source,
                action: r.action,
                message: r.message.clone(),
                fired,
                applied: false,
                targets: Vec::new(),
                note: None,
            };
            if !fired {
                return res;
            }
            match (r.action, &r.subject) {
                (RuleAction::Warn, _) => res.applied = true,
                (_, Some(subject)) => {
                    let matched: Vec<&PlanEntry> = entries.iter().filter(|e| e.matches(subject)).collect();
                    let late = r.action == RuleAction::Late;
                    // EMF's loader can be switched off by a rule, but never loaded late
                    res.targets = matched.iter().filter(|e| !e.is_kiln_own() && !(late && e.is_emf_loader())).map(|e| e.path.clone()).collect();
                    res.applied = !res.targets.is_empty();
                    if res.targets.is_empty() {
                        res.note = Some(match subject {
                            _ if late && matched.iter().any(|e| e.is_emf_loader()) => {
                                "EMF's loader starts EMF itself, so it always loads first and cannot be moved late".into()
                            }
                            _ if !matched.is_empty() => "Kiln's own files and ShroudForge's proxy are never moved or switched off".into(),
                            Matcher::Mod(s) if r.action == RuleAction::Disable && canon_mod(s).split(':').next() != Some("kiln") => {
                                "Kiln skips this mod when the game starts".into()
                            }
                            Matcher::Symbol(_) => "a symbol names no DLL to move".into(),
                            _ => "no chain entry matches the subject".into(),
                        });
                    }
                }
                (_, None) => res.note = Some("no subject".into()),
            }
            res
        })
        .collect()
}

/// The chain.ini lines for a profile's [profile] entries (`profile_late`: their paths that the profile loads late),
/// with the rule results applied: `disable` drops [profile] entries and switches [chain] lines off, `late` adds
/// [late] lines (`disable` wins over `late`).
pub fn apply(entries: &[PlanEntry], profile_late: &[String], results: &[RuleResult]) -> ProfilePlan {
    let fired = |a: RuleAction| results.iter().filter(move |r| r.applied && r.action == a);
    let off = |p: &str| fired(RuleAction::Disable).find(|r| r.targets.iter().any(|t| t.eq_ignore_ascii_case(p)));
    let mut plan = ProfilePlan::default();
    for e in entries {
        let disabled = off(&e.path);
        match (e.section, disabled) {
            (Section::Profile, None) => {
                plan.profile.push(e.path.clone());
                if profile_late.iter().any(|p| p.eq_ignore_ascii_case(&e.path)) {
                    plan.profile_late.push(e.path.clone());
                }
            }
            (Section::Profile, Some(_)) => {}
            (Section::Chain, Some(r)) => plan.rule_off.push((r.id.clone(), e.path.clone())),
            (Section::Chain, None) => {}
        }
        if disabled.is_none()
            && let Some(r) = fired(RuleAction::Late).find(|r| r.targets.iter().any(|t| t.eq_ignore_ascii_case(&e.path)))
            && !plan.profile_late.iter().any(|p| p.eq_ignore_ascii_case(&e.path))
        {
            plan.rule_late.push((r.id.clone(), e.path.clone()));
        }
    }
    plan
}

// ---------------------------------------------------------------------------------------------------------------------
// hooks.json (kiln.hooks' map, WP-E) and overlaps (WP-G1)

/// kiln-core writes `null` where it does not know: a `refused` hook has no sigdb symbol or RVA, and often no culprit
/// module or offset; a target without a sigdb name has no symbol. A plain `String` field would make serde reject the whole
/// hooks.json, so these fields read `null` as the empty string (the public shape stays `String`).
fn null_string<'de, D: Deserializer<'de>>(d: D) -> std::result::Result<String, D::Error> {
    Ok(Option::<String>::deserialize(d)?.unwrap_or_default())
}

fn null_bool<'de, D: Deserializer<'de>>(d: D) -> std::result::Result<bool, D::Error> {
    Ok(Option::<bool>::deserialize(d)?.unwrap_or_default())
}

/// A missing, `null` or unknown kind reads as `other`: one odd row does not hide the rest of hooks.json.
fn kind_or_other<'de, D: Deserializer<'de>>(d: D) -> std::result::Result<OverlapKind, D::Error> {
    Ok(Option::<OverlapKind>::deserialize(d)?.unwrap_or(OverlapKind::Other))
}

/// A jump destination in another module.
#[derive(Debug, Clone, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ForeignDest {
    /// Empty when the jump goes outside any module, or (a `refused` overlap) when the culprit is not known: `null` in the
    /// file.
    #[serde(default, deserialize_with = "null_string")]
    pub module: String,
    /// Hex text ("0x1a2b"); empty for `null` (a `refused` overlap has no offset).
    #[serde(default, deserialize_with = "null_string")]
    pub offset: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub address: Option<String>,
    /// A `kiln-*.dll` (not foreign for overlaps).
    #[serde(default, deserialize_with = "null_bool")]
    pub own: bool,
    #[serde(default, skip_serializing_if = "std::ops::Not::not")]
    pub in_place: bool,
}

#[derive(Debug, Clone, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct HookLink {
    #[serde(default, deserialize_with = "null_string")]
    pub owner: String,
    /// raw | structured | v1 | mid
    #[serde(default, deserialize_with = "null_string")]
    pub kind: String,
    #[serde(default)]
    pub priority: i64,
    #[serde(default, deserialize_with = "null_string")]
    pub label: String,
}

#[derive(Debug, Clone, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct HookTarget {
    #[serde(default, deserialize_with = "null_string")]
    pub rva: String,
    #[serde(default)]
    pub symbol: Option<String>,
    #[serde(default, deserialize_with = "null_string")]
    pub label: String,
    #[serde(default)]
    pub links: Vec<HookLink>,
    #[serde(default)]
    pub foreign_before: Option<ForeignDest>,
    #[serde(default)]
    pub foreign_over: Option<ForeignDest>,
}

#[derive(Debug, Clone, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ForeignSymbol {
    #[serde(default, deserialize_with = "null_string")]
    pub symbol: String,
    #[serde(default, deserialize_with = "null_string")]
    pub rva: String,
    #[serde(flatten)]
    pub dest: ForeignDest,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "kebab-case")]
pub enum OverlapKind {
    /// Another module's jump was there before a Kiln-hosted link.
    ForeignBefore,
    /// Another module hooked over Kiln's detour.
    ForeignOver,
    /// A hosted engine refused or failed a hook there.
    Refused,
    #[serde(other)]
    Other,
}

impl OverlapKind {
    fn other() -> Self {
        OverlapKind::Other
    }
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Overlap {
    /// Empty for a target without a sigdb name and for a `refused` hook (`null` in the file).
    #[serde(default, deserialize_with = "null_string")]
    pub symbol: String,
    /// Hex text; empty for a `refused` hook (`null` in the file).
    #[serde(default, deserialize_with = "null_string")]
    pub rva: String,
    /// A missing or unknown kind reads as `other`: one odd row does not hide the rest of hooks.json.
    #[serde(default = "OverlapKind::other", deserialize_with = "kind_or_other")]
    pub kind: OverlapKind,
    #[serde(default)]
    pub foreign: Option<ForeignDest>,
    /// Kiln-hosted owners on that target ("shroudforge", "emf:foo", "kiln-chat.dll", ...).
    #[serde(default)]
    pub owners: Vec<String>,
    /// `refused`: the hosted engine's name for the hook ("game-thread"); it labels a row that has no symbol.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub hook: Option<String>,
}

/// A rule's result as kiln-core reported it.
#[derive(Debug, Clone, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct CoreRule {
    #[serde(default, deserialize_with = "null_string")]
    pub id: String,
    #[serde(default, deserialize_with = "null_string")]
    pub action: String,
    #[serde(default, deserialize_with = "null_bool")]
    pub applied: bool,
    #[serde(default, deserialize_with = "null_string")]
    pub message: String,
}

/// `<game>/kiln/state/hooks.json`. Unknown fields are ignored; missing ones default.
#[derive(Debug, Clone, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct HooksState {
    #[serde(default)]
    pub format: u32,
    #[serde(default)]
    pub kiln: Option<String>,
    /// boot | world | change | query
    #[serde(default)]
    pub phase: Option<String>,
    #[serde(default)]
    pub world_checked: bool,
    #[serde(default)]
    pub generated: Option<String>,
    #[serde(default)]
    pub targets: Vec<HookTarget>,
    #[serde(default)]
    pub foreign: Vec<ForeignSymbol>,
    /// WP-G1; None from a core that writes none (then [`overlaps`] derives them from the targets).
    #[serde(default)]
    pub overlaps: Option<Vec<Overlap>>,
    #[serde(default)]
    pub rules: Vec<CoreRule>,
}

pub fn parse_hooks(bytes: &[u8]) -> Result<HooksState> {
    let s: HooksState = serde_json::from_slice(bytes).map_err(|e| Error::new(ErrorCode::Parse, format!("hooks.json: {e}")))?;
    if s.format != 1 {
        return Err(Error::new(ErrorCode::Unsupported, format!("hooks.json format {} (this launcher reads 1)", s.format)));
    }
    Ok(s)
}

#[cfg(test)]
thread_local! {
    /// How many times this thread read hooks.json (up to 8 MiB each): the tests check a view reads it once.
    static HOOKS_READS: std::cell::Cell<u32> = const { std::cell::Cell::new(0) };
}

/// The last session's hooks.json; None when the game never wrote one.
pub fn read_hooks(game_dir: &Path) -> Result<Option<HooksState>> {
    Ok(read_hooks_shared(game_dir)?.map(|h| (*h).clone()))
}

/// [`read_hooks`] without a copy of what is remembered.
fn read_hooks_shared(game_dir: &Path) -> Result<Option<std::sync::Arc<HooksState>>> {
    #[cfg(test)]
    HOOKS_READS.with(|c| c.set(c.get() + 1));
    // up to 8 MiB, read by every chain view, activation and late switch: parsed once while the file is unchanged
    static PARSED: std::sync::LazyLock<crate::filecache::Memo<Option<std::sync::Arc<HooksState>>>> =
        std::sync::LazyLock::new(|| crate::filecache::Memo::new(16));
    let p = game_dir.join(HOOKS_JSON);
    let parsed = PARSED.get(&p, |p| match read_capped(p, MAX_HOOKS_BYTES) {
        Ok(b) => parse_hooks(&b).map(|h| Some(std::sync::Arc::new(h))),
        Err(e) if e.kind() == std::io::ErrorKind::NotFound => Ok(None),
        Err(e) => Err(crate::util::io_at(p, "reading")(e)),
    })?;
    Ok(parsed)
}

/// The overlaps of a session: the core's list, or (from a core that writes none) the targets with a foreign jump
/// before or over Kiln's links. Kiln's own modules never count.
pub fn overlaps(s: &HooksState) -> Vec<Overlap> {
    if let Some(o) = &s.overlaps {
        return o
            .iter()
            .filter(|o| !o.foreign.as_ref().is_some_and(|f| f.own))
            .take(MAX_OVERLAPS)
            .map(|o| Overlap { owners: o.owners.iter().take(MAX_OWNERS).cloned().collect(), ..o.clone() })
            .collect();
    }
    let mut out = Vec::new();
    for t in &s.targets {
        let mut owners: Vec<String> = Vec::new();
        for l in &t.links {
            if owners.len() < MAX_OWNERS && !owners.contains(&l.owner) {
                owners.push(l.owner.clone());
            }
        }
        for (kind, d) in [(OverlapKind::ForeignBefore, &t.foreign_before), (OverlapKind::ForeignOver, &t.foreign_over)] {
            if let Some(d) = d.as_ref().filter(|d| !d.own) {
                if out.len() >= MAX_OVERLAPS {
                    return out;
                }
                out.push(Overlap {
                    symbol: t.symbol.clone().unwrap_or_else(|| t.label.clone()),
                    rva: t.rva.clone(),
                    kind,
                    foreign: Some(d.clone()),
                    owners: owners.clone(),
                    hook: None,
                });
            }
        }
    }
    out
}

// ---------------------------------------------------------------------------------------------------------------------
// a profile's chain plan

/// Host path of a chain.ini entry (relative ones resolve against `<game>/kiln/`).
fn entry_host_path(install: &GameInstall, path: &str) -> Option<PathBuf> {
    let (p, relative) = chain::game_path(path);
    if relative {
        let mut h = install.path.join(crate::paths::game::KILN);
        for part in p.split('\\').filter(|s| !s.is_empty()) {
            h.push(part);
        }
        Some(h)
    } else {
        crate::winpath::to_host(&p, install).ok()
    }
}

fn engine_word_of(path: &Path) -> Option<String> {
    let class = crate::pe::classify_proxy(path).ok()?;
    crate::engines::Engine::of_proxy(class.kind).map(|e| crate::engines::info(e).word.to_string())
}

/// The engine word of a mod kind (Kiln's own kinds are "kiln").
pub fn kind_engine(k: ModKind) -> &'static str {
    if ModKind::CORE.contains(&k) && k != ModKind::Emf { "kiln" } else { k.as_str() }
}

/// Everything [`plan`] worked from, and what it writes.
#[derive(Debug, Clone, Default, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Plan {
    /// [chain] entries (switched-off ones too) then the profile's [profile] entries.
    pub entries: Vec<PlanEntry>,
    pub facts: Facts,
    pub rules: Vec<RuleResult>,
    /// Rule files that did not read ("<file>: <why>").
    pub rule_errors: Vec<String>,
    #[serde(skip)]
    pub chain: ProfilePlan,
    /// What [`plan`] read and parsed, for [`view`] to use instead of reading the same files again (hooks.json can be
    /// 8 MiB): chain.ini as text and parsed, and the last session's hooks.json (None: there is none, or it did not read).
    #[serde(skip)]
    pub chain_text: String,
    #[serde(skip)]
    pub ini: chain::ChainIni,
    #[serde(skip)]
    pub hooks: Option<std::sync::Arc<HooksState>>,
    /// chain.ini did not read (the plan then worked from an empty one; [`view`] reports it).
    #[serde(skip)]
    pub chain_error: Option<Error>,
}

/// The chain.ini entries of [chain], in file order, then the ones a rule switched off (`; kiln rule <id> off:`): the
/// whole list a profile's rules work on. Duplicates (case ignored) count once.
fn chain_paths(ini: &chain::ChainIni, text: &str) -> Vec<String> {
    let mut out: Vec<String> = Vec::new();
    let all = ini.chain.iter().filter(|e| e.status == chain::EntryStatus::Ok).map(|e| e.path.clone())
        .chain(chain::rule_off_entries(text).into_iter().map(|(_, p)| p));
    for p in all {
        if !out.iter().any(|o| o.eq_ignore_ascii_case(&p)) {
            out.push(p);
        }
    }
    out
}

/// A [chain] entry: its DLL is classified (PE headers read) to name the engine its markers show.
fn chain_entry(install: &GameInstall, path: String) -> PlanEntry {
    let engine = entry_host_path(install, &path).and_then(|h| engine_word_of(&h));
    PlanEntry { file: file_name(&path).to_string(), path, section: Section::Chain, engine, mod_id: None }
}

fn profile_entry(id: &str, path: &str) -> PlanEntry {
    PlanEntry { path: path.to_string(), file: file_name(path).to_string(), section: Section::Profile, engine: None, mod_id: Some(id.to_string()) }
}

/// The chain.ini plan of a profile in an install. `profile_dlls`: (native mod id, game-side DLL path) of its enabled
/// dllmain natives (profile.rs computes them from the rendered profile). Reads chain.ini, the proxies next to the
/// game, the last hooks.json and both rule files; never fails on a broken one (rule_errors says so). What it read of
/// chain.ini and hooks.json comes back in the [`Plan`] (`chain_text`, `ini`, `hooks`).
pub fn plan(dirs: &DataDirs, profile: &Profile, install: &GameInstall, profile_dlls: &[(String, String)]) -> Plan {
    let (text, chain_error) = match chain::read_raw(&install.path) {
        Ok(t) => (t, None),
        Err(e) => (String::new(), Some(e)),
    };
    let ini = chain::parse(text.as_bytes());
    let mut entries: Vec<PlanEntry> = chain_paths(&ini, &text).into_iter().map(|p| chain_entry(install, p)).collect();
    entries.extend(profile_dlls.iter().map(|(id, p)| profile_entry(id, p)));

    let mut facts = Facts::default();
    facts.add_engine("kiln");
    for e in &entries {
        facts.add_module(&e.file);
        if let Some(w) = &e.engine {
            facts.add_engine(w);
        }
    }
    for slot in crate::engines::scan_proxies(&install.path) {
        facts.add_module(&slot.name);
        if let Some(e) = crate::engines::Engine::of_proxy(slot.class.kind) {
            facts.add_engine(crate::engines::info(e).word);
        }
    }
    for k in ModKind::ALL {
        let on: Vec<&crate::profile::ProfileMod> = profile.mods.list(k).iter().filter(|m| m.enabled).collect();
        if !on.is_empty() && k.is_engine() {
            facts.add_engine(kind_engine(k));
        }
        for m in on {
            let m = format!("{}:{}", kind_engine(k), m.id);
            if !facts.mods.contains(&m) {
                facts.mods.push(m);
            }
        }
    }
    let hooks = read_hooks_shared(&install.path).ok().flatten();
    if let Some(h) = &hooks {
        for f in &h.foreign {
            if !f.dest.own && !f.dest.module.is_empty() {
                facts.add_module(&f.dest.module);
            }
        }
        for o in overlaps(h) {
            if let Some(f) = &o.foreign {
                facts.add_module(&f.module);
            }
            facts.add_symbol(&o.symbol);
        }
    }

    let (rules, rule_errors) = load_rules(&[
        (install.path.join(SHIPPED_RULES), RuleSource::Kiln),
        (dirs.profile(&profile.id).join("config").join(RULES_FILE), RuleSource::User),
    ]);
    let results = evaluate(&rules, &facts, &entries);
    let late_paths: Vec<String> = profile_dlls.iter().filter(|(id, _)| profile.late.contains(id)).map(|(_, p)| p.clone()).collect();
    let chain = apply(&entries, &late_paths, &results);
    Plan { entries, facts, rules: results, rule_errors, chain, chain_text: text, ini, hooks, chain_error }
}

/// The profile's enabled native mods with phase dllmain: (mod id, game-side DLL path), from its rendered file.
pub fn dllmain_entries(dirs: &DataDirs, profile: &Profile, file: &crate::profile::ProfileFile) -> Result<Vec<(String, String)>> {
    let mut out = Vec::new();
    for (m, fm) in profile.mods.native.iter().zip(&file.mods.native) {
        if !m.enabled {
            continue;
        }
        let r = crate::library::LibraryRef { kind: ModKind::Native, id: m.id.clone(), version: m.version.clone(), hash: m.hash.clone() };
        let folder = crate::library::path_of(dirs, &r)?;
        let Ok(n) = crate::manifest::read_native(&folder) else { continue };
        if n.phase != NativePhase::Dllmain {
            continue;
        }
        for d in &n.dlls {
            // chain.ini paths use '\'; relative ones resolve against <game>/kiln/
            out.push((m.id.clone(), format!("{}\\{d}", fm.path.trim_end_matches(['\\', '/'])).replace('/', "\\")));
        }
    }
    Ok(out)
}

// ---------------------------------------------------------------------------------------------------------------------
// the launcher's view

/// One chain entry on the profile page.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ChainViewEntry {
    #[serde(flatten)]
    pub entry: PlanEntry,
    /// Loads after Kiln and ShroudForge.
    pub late: bool,
    /// Who made it late: "player" ([chain], game-wide), "profile", or "rule:<id>".
    pub late_by: Option<String>,
    /// A rule switches it off ("rule:<id>").
    pub off_by: Option<String>,
    /// EMF's loader: it starts EMF itself and always loads first (no late switch).
    #[serde(default)]
    pub loader: bool,
    /// shroudlink's client addon ([`is_gated_addon`]): Kiln's start waits for it, so it is never switched late (only
    /// back from late, when an older version set it).
    #[serde(default)]
    pub gated: bool,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct LibraryHit {
    pub kind: ModKind,
    pub id: String,
    pub name: String,
}

/// An overlap of the last session, mapped to what the launcher can change.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct OverlapView {
    #[serde(flatten)]
    pub overlap: Overlap,
    /// The chain entry that loads the foreign module (None: Kiln did not load it, e.g. another loader did).
    pub entry: Option<String>,
    pub section: Option<Section>,
    /// The Library item it came from, when known.
    pub library: Option<LibraryHit>,
    /// The entry loads late already (in the plan).
    pub late: bool,
    /// "late" (one click: load the entry late), or "none" (only a hint).
    pub fix: String,
    /// The suggestion in words.
    pub hint: String,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SessionInfo {
    pub generated: Option<String>,
    pub phase: Option<String>,
    pub kiln: Option<String>,
    pub world_checked: bool,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ChainView {
    pub entries: Vec<ChainViewEntry>,
    pub rules: Vec<RuleResult>,
    pub rule_errors: Vec<String>,
    /// None: no hooks.json yet (the game has not started with this Kiln).
    pub session: Option<SessionInfo>,
    pub overlaps: Vec<OverlapView>,
    /// The profile is the install's active one.
    pub active: bool,
    /// Active, and chain.ini already says what the plan says (false: the next activation or launch writes it).
    pub current: bool,
    /// Why the profile's entries are not listed (it does not render: a mod missing from the Library).
    pub error: Option<String>,
}

/// The exports by which Kiln's start knows shroudlink's client addon (core/src/foreign_scan.h kAddonExport,
/// kAddonProxyExport).
const GATED_ADDON_EXPORTS: [&str; 2] = ["shroudlink_shutdown", "DirectInput8Create"];

/// shroudlink's client addon, by its DLL (a chain entry or a profile native), recognised as Kiln's start recognises it:
/// by its exports alone, whatever the file is called (the installer's second chain slot is `shroudlink-dinput8-2.dll`,
/// a profile native may have been renamed). Kiln's start waits for it to finish looking for the game's functions
/// before Kiln hooks them; loading it late skips that wait.
fn is_gated_addon(install: &GameInstall, e: &PlanEntry) -> bool {
    entry_host_path(install, &e.path)
        .and_then(|h| crate::pe::exports(&h).ok())
        .is_some_and(|x| GATED_ADDON_EXPORTS.iter().all(|n| x.iter().any(|e| e == n)))
}

/// `gated`: the entry is a DLL Kiln's start waits for ([`is_gated_addon`]); it is never offered "late".
fn overlap_view(o: Overlap, entries: &[ChainViewEntry], lib: &[crate::library::LibraryEntry], gated: &dyn Fn(&PlanEntry) -> bool) -> OverlapView {
    let module = o.foreign.as_ref().map(|f| f.module.clone()).unwrap_or_default();
    // hooks.json names the module by file name only: two entries with that name cannot be told apart
    let named: Vec<&ChainViewEntry> = if module.is_empty() {
        Vec::new()
    } else {
        entries.iter().filter(|e| e.entry.file.eq_ignore_ascii_case(file_name(&module))).collect()
    };
    let ambiguous = named.len() > 1;
    let hit = if ambiguous { None } else { named.first().copied() };
    let library = match hit.and_then(|e| e.entry.mod_id.as_ref()) {
        Some(id) => lib.iter().find(|l| l.kind == ModKind::Native && &l.id == id),
        None if !module.is_empty() => lib.iter().find(|l| {
            l.versions.iter().any(|v| {
                v.manifest.as_ref().and_then(|m| m.get("dlls")).and_then(|d| d.as_array()).is_some_and(|ds| {
                    ds.iter().filter_map(|d| d.as_str()).any(|d| file_name(d).eq_ignore_ascii_case(file_name(&module)))
                })
            })
        }),
        None => None,
    }
    .map(|l| LibraryHit { kind: l.kind, id: l.id.clone(), name: l.name.clone() });
    let late = hit.is_some_and(|e| e.late);
    let loader = hit.is_some_and(|e| e.entry.is_emf_loader());
    let owners = if o.owners.is_empty() { "Kiln".to_string() } else { o.owners.join(", ") };
    let name = if module.is_empty() { "another module".to_string() } else { module.clone() };
    let (fix, hint) = match (o.kind, hit) {
        _ if ambiguous => ("none", format!("{} chain entries are named {name}: switch the right one to load late in the list above.", named.len())),
        // a rule keeps it from loading in this profile: the overlap is from a start before that (or another profile's)
        (_, Some(e)) if e.off_by.is_some() => {
            let by = e.off_by.as_deref().unwrap_or_default();
            ("none", format!("{name} is switched off by {} for this profile, so it does not load here. This overlap is from an earlier start.", by.replacen("rule:", "rule ", 1)))
        }
        (OverlapKind::ForeignBefore | OverlapKind::Refused, Some(_)) if loader => {
            ("none", format!("{name} is EMF's loader: it starts EMF itself, so it always loads first. Switch it or the other mod off, or add a compat rule."))
        }
        // it hooks first on purpose: loading it late skips Kiln's wait for it, and its MODS tab switches off again
        (OverlapKind::ForeignBefore | OverlapKind::Refused, Some(e)) if !late && gated(&e.entry) => {
            ("none", format!("{name} is shroudlink's client addon: Kiln lets it find and hook the game's functions first on purpose. \
                Loading it late would switch its MODS tab off (\"native UI disabled\"). Switch it or the other mod off if they clash."))
        }
        (OverlapKind::ForeignBefore | OverlapKind::Refused, Some(e)) if !late && e.off_by.is_none() => {
            ("late", format!("Load {} after Kiln and ShroudForge, so {owners} hook first and {name} chains after them.", e.entry.file))
        }
        (OverlapKind::ForeignBefore | OverlapKind::Refused, Some(_)) => {
            ("none", format!("{name} loads late already. Switch it or the other mod off, or add a compat rule."))
        }
        (OverlapKind::ForeignOver, Some(e)) if late => {
            ("none", format!("{} loads late and hooked over {owners}. Try it without late, or switch one of them off.", e.entry.file))
        }
        (OverlapKind::ForeignOver, _) => {
            ("none", format!("{name} hooked over {owners} after the start. Switch one of them off, or ask its author."))
        }
        (_, None) => ("none", format!("{name} is not in Kiln's chain (another loader loaded it). Add it to Kiln's chain to load it late, or use a compat rule.")),
        (OverlapKind::Other, Some(_)) => ("none", format!("{name} and {owners} hook the same function.")),
    };
    OverlapView { overlap: o, entry: hit.map(|e| e.entry.path.clone()), section: hit.map(|e| e.entry.section), library, late, fix: fix.into(), hint }
}

/// The rendered profile's dllmain natives: (mod id, game-side DLL path), or why they are not known (the profile does not
/// render: a mod is missing from the Library).
fn profile_dlls(dirs: &DataDirs, profile: &Profile, install: &GameInstall) -> Result<(Vec<(String, String)>, Option<String>)> {
    let mode = crate::game::path_mode(install, dirs)?;
    Ok(match crate::profile::render(dirs, profile, install, mode)
        .or_else(|e| if e.code == ErrorCode::Unsupported { crate::profile::render(dirs, profile, install, crate::game::PathMode::Copy) } else { Err(e) })
        .and_then(|f| dllmain_entries(dirs, profile, &f))
    {
        Ok(d) => (d, None),
        Err(e) => (Vec::new(), Some(e.message)),
    })
}

/// The profile page's chain view: the plan for `profile` in `install`, which entries load late and why, the rules,
/// and the last session's overlaps mapped to chain entries and Library items. chain.ini and hooks.json are read once
/// (by [`plan`]).
pub fn view(dirs: &DataDirs, profile: &Profile, install: &GameInstall) -> Result<ChainView> {
    let (dlls, error) = profile_dlls(dirs, profile, install)?;
    let p = plan(dirs, profile, install, &dlls);
    if let Some(e) = &p.chain_error {
        return Err(e.clone());
    }
    let active = crate::profile::read_game_profile(&install.path).ok().flatten().is_some_and(|f| f.id == profile.id);
    let current = active && error.is_none() && chain::plan_is_current(&p.chain_text, &p.chain);
    // the [late] lines a rule wrote into chain.ini: they show as the rule's, not as the player's
    let rule_marks = chain::rule_late_entries(&p.chain_text);
    let entries: Vec<ChainViewEntry> = p
        .entries
        .iter()
        .map(|e| {
            let eq = |x: &String| x.eq_ignore_ascii_case(&e.path);
            let off_by = p.chain.rule_off.iter().find(|(_, d)| eq(d)).map(|(id, _)| format!("rule:{id}"));
            let removed = e.section == Section::Profile && !p.chain.profile.iter().any(eq);
            let off_by = off_by.or_else(|| {
                removed.then(|| p.rules.iter().find(|r| r.applied && r.action == RuleAction::Disable && r.targets.iter().any(eq)))
                    .flatten().map(|r| format!("rule:{}", r.id))
            });
            let late_by = if p.chain.profile_late.iter().any(eq) {
                Some("profile".to_string())
            } else if let Some((id, _)) = p.chain.rule_late.iter().find(|(_, d)| eq(d)) {
                Some(format!("rule:{id}"))
            } else if e.section == Section::Chain && chain::is_late(&p.ini, &e.path) {
                // a line another profile's rule wrote is that rule's (switching it off is undone by the next activation)
                Some(match rule_marks.iter().find(|(_, d)| eq(d)) {
                    Some((id, _)) => format!("rule:{id}"),
                    None => "player".to_string(),
                })
            } else {
                None
            };
            // the proxy loads EMF's loader at its place whatever chain.ini [late] says
            let loader = e.is_emf_loader();
            ChainViewEntry { entry: e.clone(), late: !loader && late_by.is_some() && off_by.is_none(), late_by: if loader { None } else { late_by }, off_by, loader,
                gated: is_gated_addon(install, e) }
        })
        .collect();
    let lib = crate::library::list(dirs, None).unwrap_or_default();
    let gated = |e: &PlanEntry| entries.iter().any(|x| x.gated && x.entry.path.eq_ignore_ascii_case(&e.path));
    let overlaps = p.hooks.as_deref().map(overlaps).unwrap_or_default().into_iter().map(|o| overlap_view(o, &entries, &lib, &gated)).collect();
    let session = p.hooks.as_ref().map(|h| SessionInfo { generated: h.generated.clone(), phase: h.phase.clone(), kiln: h.kiln.clone(), world_checked: h.world_checked });
    Ok(ChainView { entries, rules: p.rules, rule_errors: p.rule_errors, session, overlaps, active, current, error })
}

/// One chain entry of the profile page by its path (case ignored), without building the whole [`view`]: hooks.json and
/// the Library are not read, and only this entry's DLL is classified. `not_found` when the profile's chain has no such
/// entry. The late switch ([`set_entry_late`]) needs the entry, nothing else.
pub fn find_entry(dirs: &DataDirs, profile: &Profile, install: &GameInstall, path: &str) -> Result<PlanEntry> {
    let text = chain::read_raw(&install.path)?;
    let ini = chain::parse(text.as_bytes());
    if let Some(p) = chain_paths(&ini, &text).into_iter().find(|p| p.eq_ignore_ascii_case(path)) {
        return Ok(chain_entry(install, p));
    }
    let (dlls, _) = profile_dlls(dirs, profile, install)?;
    dlls.iter()
        .find(|(_, p)| p.eq_ignore_ascii_case(path))
        .map(|(id, p)| profile_entry(id, p))
        .ok_or_else(|| Error::not_found(format!("{path} is not in this profile's chain")))
}

/// The late switch of one chain entry of the view: a [profile] entry changes the profile (`late` list; the caller
/// saves it), a [chain] entry changes chain.ini [late] (game-wide; refused while the game runs). shroudlink's client
/// addon is never switched late ([`is_gated_addon`]): refused with the reason. Returns true when the profile changed.
pub fn set_entry_late(install: &GameInstall, profile: &mut Profile, entry: &PlanEntry, late: bool) -> Result<bool> {
    if late && is_gated_addon(install, entry) {
        return Err(Error::invalid(format!("{} is shroudlink's client addon: it must find the game's functions before Kiln \
            hooks them, and Kiln waits for it at the start. Loaded late, it switches its MODS tab off (\"native UI disabled\").", entry.file)));
    }
    match (entry.section, &entry.mod_id) {
        (Section::Profile, Some(id)) => {
            let has = profile.late.contains(id);
            if has == late {
                return Ok(false);
            }
            if late {
                profile.late.push(id.clone());
            } else {
                profile.late.retain(|x| x != id);
            }
            Ok(true)
        }
        (Section::Profile, None) => Err(Error::invalid("a [profile] entry without a mod id")),
        (Section::Chain, _) => {
            if entry.is_kiln_own() {
                return Err(Error::invalid(format!("{} is Kiln's own: it always loads first", entry.file)));
            }
            if late && entry.is_emf_loader() {
                return Err(Error::invalid(format!("{} is EMF's loader: it starts EMF itself, so it always loads first", entry.file)));
            }
            // chain.ini is the proxy's input for the next start: not while a game that reads it is running
            crate::profile::refuse_running(install)?;
            chain::set_late(&install.path, &entry.path, late)?;
            Ok(false)
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn entry(path: &str, section: Section, engine: Option<&str>, mod_id: Option<&str>) -> PlanEntry {
        PlanEntry { path: path.into(), file: file_name(path).into(), section, engine: engine.map(Into::into), mod_id: mod_id.map(Into::into) }
    }

    #[test]
    fn globs() {
        assert!(glob("xhl-*.dll", "XHL-Core.dll"));
        assert!(glob("*", "a"));
        assert!(glob("a?c.dll", "abc.dll"));
        assert!(!glob("a?c.dll", "ac.dll"));
        assert!(glob("*dinput8*", "chain-loader-dinput8.dll"));
        assert!(!glob("xhl.dll", "xhl.dll.bak"));
    }

    #[test]
    fn parses_rules_and_reports_bad_ones() {
        let text = br#"{ "format": 1, "rules": [
            { "id": "a", "when": [{"module": "xhl-*.dll"}, {"engine": "shroudforge"}], "action": "late", "subject": {"module": "xhl-*.dll"}, "message": "XHL after SF" },
            { "id": "b", "action": "warn", "message": "always" },
            { "id": "c", "action": "late" },
            { "id": "d", "when": [{"module": "x", "engine": "y"}], "action": "warn" },
            { "id": "e", "when": [{"mod": "nocolon"}], "action": "warn" },
            { "id": "a", "action": "warn" },
            { "id": "f", "action": "explode" }
        ] }"#;
        let (rules, errors) = parse_rules(text, RuleSource::Kiln);
        assert_eq!(rules.iter().map(|r| r.rule.id.as_str()).collect::<Vec<_>>(), ["a", "b"]);
        assert_eq!(errors.len(), 5, "{errors:?}");
        assert!(parse_rules(b"{\"format\":2}", RuleSource::User).1[0].contains("format"));
        assert!(parse_rules(b"nope", RuleSource::User).1[0].contains("JSON"));
        assert_eq!(parse_rules(b"{\"format\":1}", RuleSource::User), (Vec::new(), Vec::new()));
    }

    #[test]
    fn evaluates_and_applies() {
        let (rules, e) = parse_rules(br#"{ "format": 1, "rules": [
            { "id": "xhl-late", "when": [{"module": "xhl-*.dll"}, {"engine": "shroudforge"}], "action": "late", "subject": {"module": "xhl-*.dll"} },
            { "id": "chat", "when": [{"symbol": "ChatLogAdd"}], "action": "warn", "message": "two chat hooks" },
            { "id": "old-off", "when": [{"engine": "chain-loader"}], "action": "disable", "subject": {"engine": "chain-loader"} },
            { "id": "fps-off", "when": [{"mod": "native:fps"}], "action": "disable", "subject": {"mod": "kiln:fps"} },
            { "id": "emf-off", "action": "disable", "subject": {"mod": "emf:foo"} },
            { "id": "never", "when": [{"engine": "greeneye"}], "action": "warn" }
        ] }"#, RuleSource::Kiln);
        assert!(e.is_empty(), "{e:?}");
        let entries = vec![
            entry("chain\\xhl-core.dll", Section::Chain, None, None),
            entry("chain\\chain-loader-dinput8.dll", Section::Chain, Some("chain-loader"), None),
            entry("Z:\\lib\\fps\\fps.dll", Section::Profile, None, Some("fps")),
            entry("Z:\\lib\\other\\o.dll", Section::Profile, None, Some("other")),
        ];
        let facts = Facts {
            modules: vec!["xhl-core.dll".into(), "chain-loader-dinput8.dll".into()],
            engines: vec!["kiln".into(), "shroudforge".into(), "chain-loader".into()],
            mods: vec!["kiln:fps".into()],
            symbols: vec!["ChatLogAdd".into()],
        };
        let res = evaluate(&rules, &facts, &entries);
        let by = |id: &str| res.iter().find(|r| r.id == id).unwrap();
        assert!(by("xhl-late").applied && by("xhl-late").targets == ["chain\\xhl-core.dll"]);
        assert!(by("chat").fired && by("chat").applied);
        assert_eq!(by("old-off").targets, ["chain\\chain-loader-dinput8.dll"]);
        assert_eq!(by("fps-off").targets, ["Z:\\lib\\fps\\fps.dll"]);
        assert!(by("emf-off").fired && !by("emf-off").applied);
        assert_eq!(by("emf-off").note.as_deref(), Some("Kiln skips this mod when the game starts"));
        assert!(!by("never").fired);
        let plan = apply(&entries, &["Z:\\lib\\other\\o.dll".into()], &res);
        assert_eq!(plan.profile, ["Z:\\lib\\other\\o.dll"]);
        assert_eq!(plan.profile_late, ["Z:\\lib\\other\\o.dll"]);
        assert_eq!(plan.rule_late, [("xhl-late".to_string(), "chain\\xhl-core.dll".to_string())]);
        assert_eq!(plan.rule_off, [("old-off".to_string(), "chain\\chain-loader-dinput8.dll".to_string())]);
    }

    #[test]
    fn shipped_rules_are_replaced_by_the_players() {
        let d = std::env::temp_dir().join(format!("km-compat-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&d);
        std::fs::create_dir_all(&d).unwrap();
        std::fs::write(d.join("kiln.json"), r#"{"format":1,"rules":[{"id":"a","action":"warn","message":"kiln"},{"id":"b","action":"warn"}]}"#).unwrap();
        std::fs::write(d.join("user.json"), r#"{"format":1,"rules":[{"id":"a","action":"warn","message":"mine"},{"id":"c","action":"warn"}]}"#).unwrap();
        let (rules, errors) = load_rules(&[(d.join("kiln.json"), RuleSource::Kiln), (d.join("user.json"), RuleSource::User), (d.join("none.json"), RuleSource::User)]);
        assert!(errors.is_empty());
        assert_eq!(rules.iter().map(|r| (r.rule.id.as_str(), r.rule.message.as_str(), r.source)).collect::<Vec<_>>(),
            [("a", "mine", RuleSource::User), ("b", "", RuleSource::Kiln), ("c", "", RuleSource::User)]);
        let _ = std::fs::remove_dir_all(&d);
    }

    #[test]
    fn derives_overlaps_from_targets() {
        let s = parse_hooks(br#"{ "format": 1, "kiln": "0.2.0-dev.21", "phase": "world", "worldChecked": true,
            "targets": [
              { "rva": "0x1000", "symbol": "ChatLogAdd", "label": "chat/ChatLogAdd",
                "links": [ {"owner": "kiln.chat", "kind": "raw", "priority": 0, "label": "chat/ChatLogAdd"}, {"owner": "kiln-emf", "kind": "raw", "priority": 0, "label": "x"} ],
                "foreignBefore": {"module": "xhl-core.dll", "offset": "0x2a0", "own": false}, "foreignOver": null },
              { "rva": "0x2000", "symbol": null, "label": "tick", "links": [], "foreignBefore": {"module": "kiln-emf.dll", "offset": "0x10", "own": true}, "foreignOver": null }
            ],
            "foreign": [ {"symbol": "Other", "rva": "0x3000", "module": "reshade.dll", "offset": "0x5", "own": false} ],
            "unknownField": 1 }"#).unwrap();
        let o = overlaps(&s);
        assert_eq!(o.len(), 1);
        assert_eq!((o[0].symbol.as_str(), o[0].kind, o[0].owners.clone()), ("ChatLogAdd", OverlapKind::ForeignBefore, vec!["kiln.chat".to_string(), "kiln-emf".to_string()]));
        assert_eq!(s.foreign[0].dest.module, "reshade.dll");
        assert!(parse_hooks(br#"{"format": 2}"#).is_err());
    }

    #[test]
    fn hostile_rules_and_hooks_stay_bounded() {
        let t = std::time::Instant::now();
        // a glob built to backtrack against a name that almost matches
        let name = format!("{}b.dll", "a".repeat(240));
        assert!(!glob("*a*a*a*a*c.dll", &name));
        assert!(glob("*a*a*a*a*b.dll", &name));
        assert!(!glob("*", &"x".repeat(100_000)), "no file name is that long");
        assert!(!glob(&"*a".repeat(100_000), "a"));
        // rules: too many conditions, patterns with too many wildcards
        let many = vec![r#"{"engine":"kiln"}"#; MAX_WHEN + 1].join(",");
        let stars = "a*".repeat(MAX_STARS + 1);
        let text = format!(
            r#"{{"format":1,"rules":[{{"id":"w","when":[{many}],"action":"warn"}},{{"id":"s","when":[{{"module":"{stars}"}}],"action":"warn"}},{{"id":"ok","when":[{{"module":"a*b*c*d*e"}}],"action":"warn"}}]}}"#
        );
        let (rules, errors) = parse_rules(text.as_bytes(), RuleSource::User);
        assert_eq!(rules.iter().map(|r| r.rule.id.as_str()).collect::<Vec<_>>(), ["ok"], "{errors:?}");
        assert_eq!(errors.len(), 2);
        // deep nesting, an enormous id, garbage: errors, never a panic
        for bad in [format!("{}{}", "[".repeat(100_000), "]".repeat(100_000)), "\u{0}\u{0}".into(), format!(r#"{{"format":1,"rules":[{{"id":"{}","action":"warn"}}]}}"#, "x".repeat(1 << 16))] {
            let (r, e) = parse_rules(bad.as_bytes(), RuleSource::User);
            assert!(r.is_empty() && !e.is_empty());
        }
        // a hooks.json with a very long owner list on one target and a flood of names stays quick
        let links = (0..60_000).map(|i| format!(r#"{{"owner":"o{i}","kind":"raw"}}"#)).collect::<Vec<_>>().join(",");
        let json = format!(
            r#"{{"format":1,"targets":[{{"rva":"0x1","symbol":"S","links":[{links}],"foreignBefore":{{"module":"{}.dll","offset":"0x1"}}}}]}}"#,
            "m".repeat(5000)
        );
        let h = parse_hooks(json.as_bytes()).unwrap();
        let o = overlaps(&h);
        assert_eq!((o.len(), o[0].owners.len()), (1, MAX_OWNERS));
        let mut f = Facts::default();
        for i in 0..100_000 {
            f.add_module(&format!("m{i}.dll"));
            f.add_symbol(&format!("S{i}"));
        }
        f.add_module(&"x".repeat(5000));
        assert_eq!((f.modules.len(), f.symbols.len()), (MAX_FACT_MODULES, MAX_FACT_SYMBOLS));
        assert!(t.elapsed().as_secs() < 5, "{:?}", t.elapsed());
    }

    #[test]
    fn files_over_the_cap_are_not_read() {
        let d = std::env::temp_dir().join(format!("km-compat-cap-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&d);
        std::fs::create_dir_all(d.join("kiln/state")).unwrap();
        std::fs::write(d.join(HOOKS_JSON), vec![b' '; MAX_HOOKS_BYTES as usize + 1]).unwrap();
        assert!(read_hooks(&d).is_err());
        std::fs::write(d.join("r.json"), vec![b' '; MAX_RULES_BYTES as usize + 1]).unwrap();
        let (rules, errors) = load_rules(&[(d.join("r.json"), RuleSource::User)]);
        assert!(rules.is_empty() && errors.len() == 1, "{errors:?}");
        let _ = std::fs::remove_dir_all(&d);
    }

    #[test]
    fn rules_never_move_kiln_s_own_files() {
        let (rules, e) = parse_rules(br#"{ "format": 1, "rules": [
            { "id": "all-off", "action": "disable", "subject": {"module": "*"} },
            { "id": "all-late", "action": "late", "subject": {"module": "*"} },
            { "id": "own-off", "action": "disable", "subject": {"module": "kiln-chat.dll"} }
        ] }"#, RuleSource::User);
        assert!(e.is_empty());
        let entries = vec![
            entry("chain\\emf-dinput8.dll", Section::Chain, Some("emf"), None),
            entry("chain\\kiln-old-dinput8.dll", Section::Chain, Some("kiln"), None),
            entry("chain\\sf-dinput8.dll", Section::Chain, Some("shroudforge"), None),
            entry("Z:\\lib\\kiln-chat.dll", Section::Profile, None, Some("chat")),
        ];
        let res = evaluate(&rules, &Facts::default(), &entries);
        assert_eq!(res[0].targets, ["chain\\emf-dinput8.dll"]);
        assert!(res[1].targets.is_empty(), "EMF's loader starts EMF itself: no rule loads it late");
        assert!(!res[2].applied && res[2].note.as_deref().is_some_and(|n| n.contains("never")), "{:?}", res[2]);
        let install = GameInstall {
            id: "g".into(), kind: crate::game::GameKind::Client, path: std::env::temp_dir(), exe: "enshrouded.exe".into(),
            app_id: crate::CLIENT_APP_ID, library: None, platform: crate::game::Platform::Proton, prefix: None, build_id: None,
            exe_sha256: None, steam_flavor: None, path_mode: crate::game::PathMode::Copy, source: "manual".into(),
        };
        let mut p = Profile {
            format: 1, id: "p".into(), name: "p".into(), created: String::new(), updated: String::new(), generation: 1,
            notes: String::new(), mods: Default::default(), dropins: vec![], dropins_engines: false, data_dir: Default::default(), server: None, late: vec![],
        };
        assert!(set_entry_late(&install, &mut p, &entries[1], true).is_err(), "Kiln's own proxy always loads first");
    }

    /// What kiln-core writes (native/core/src/compat_overlaps.cpp, services/hooks_chain.cpp): `refused` rows carry null for
    /// symbol, rva, module and offset; an rva-only target has a null symbol. The whole file must still read.
    #[test]
    fn reads_the_nulls_the_core_writes() {
        let dir = Path::new(env!("CARGO_MANIFEST_DIR")).join("../../schema");
        let example = parse_hooks(&std::fs::read(dir.join("examples/hooks-state.json")).unwrap()).unwrap();
        let rows = overlaps(&example);
        assert_eq!(rows.iter().map(|o| o.kind).collect::<Vec<_>>(), [OverlapKind::ForeignBefore, OverlapKind::ForeignOver, OverlapKind::Refused]);
        assert_eq!((rows[2].symbol.as_str(), rows[2].rva.as_str()), ("", ""), "the refused row: null symbol and rva");
        assert_eq!(rows[2].foreign.as_ref().map(|f| (f.module.as_str(), f.offset.as_str())), Some(("xhl-core.dll", "")));
        assert_eq!(rows[2].hook.as_deref(), Some("game-thread"));
        assert_eq!(example.rules.len(), 2);

        let s = parse_hooks(&std::fs::read(dir.join("fixtures/hooks-state/refused-no-culprit.json")).unwrap()).unwrap();
        let rows = overlaps(&s);
        assert_eq!(rows.len(), 2);
        assert_eq!(rows[0].kind, OverlapKind::ForeignBefore);
        assert_eq!((rows[0].symbol.as_str(), rows[0].rva.as_str()), ("", "0x31c0a0"), "an rva-only target: null symbol");
        let refused = &rows[1];
        assert_eq!(refused.kind, OverlapKind::Refused);
        assert_eq!((refused.symbol.as_str(), refused.rva.as_str()), ("", ""));
        let f = refused.foreign.as_ref().unwrap();
        assert_eq!((f.module.as_str(), f.offset.as_str(), f.own), ("", "", false), "no culprit: null module and offset");
        assert_eq!((refused.hook.as_deref(), refused.owners.as_slice()), (Some("game-thread"), ["shroudforge".to_string()].as_slice()));
        assert_eq!(s.rules.len(), 1);
        // every member that can be null reads as empty, in a target, a foreign symbol, a link and a rule too
        let t = parse_hooks(br#"{ "format": 1,
            "targets": [{ "rva": null, "symbol": null, "label": null, "links": [{ "owner": null, "kind": null, "priority": 0, "label": null }],
                          "foreignBefore": { "module": null, "offset": null, "own": null }, "foreignOver": null }],
            "foreign": [{ "symbol": null, "rva": null, "module": null, "offset": null, "own": null }],
            "overlaps": [{ "symbol": null, "rva": null, "kind": null, "foreign": null, "owners": [] }],
            "rules": [{ "id": null, "action": null, "applied": null, "message": null }] }"#).unwrap();
        assert_eq!((t.targets[0].label.as_str(), t.targets[0].links[0].owner.as_str(), t.foreign[0].dest.module.as_str()), ("", "", ""));
        assert_eq!(overlaps(&t)[0].kind, OverlapKind::Other, "a null kind reads as other");
    }

    #[test]
    fn emf_s_loader_is_never_late() {
        let loader = entry("chain\\emf-dinput8.dll", Section::Chain, Some("emf"), None);
        let other = entry("chain\\xhl-core.dll", Section::Chain, None, None);
        assert!(loader.is_emf_loader() && !other.is_emf_loader());
        let (rules, e) = parse_rules(br#"{ "format": 1, "rules": [
            { "id": "all-late", "action": "late", "subject": {"module": "*"} },
            { "id": "emf-late", "action": "late", "subject": {"engine": "emf"} },
            { "id": "emf-off", "action": "disable", "subject": {"engine": "emf"} }
        ] }"#, RuleSource::User);
        assert!(e.is_empty());
        let entries = vec![loader.clone(), other.clone()];
        let res = evaluate(&rules, &Facts::default(), &entries);
        assert_eq!(res[0].targets, ["chain\\xhl-core.dll"], "a late rule moves the other entry, never EMF's loader");
        assert!(!res[1].applied && res[1].targets.is_empty() && res[1].note.as_deref().is_some_and(|n| n.contains("EMF's loader")), "{:?}", res[1]);
        assert_eq!(res[2].targets, ["chain\\emf-dinput8.dll"], "switching the loader off stays possible");
        let install = GameInstall {
            id: "g".into(), kind: crate::game::GameKind::Client, path: std::env::temp_dir(), exe: "enshrouded.exe".into(),
            app_id: crate::CLIENT_APP_ID, library: None, platform: crate::game::Platform::Proton, prefix: None, build_id: None,
            exe_sha256: None, steam_flavor: None, path_mode: crate::game::PathMode::Copy, source: "manual".into(),
        };
        let mut p = Profile {
            format: 1, id: "p".into(), name: "p".into(), created: String::new(), updated: String::new(), generation: 1,
            notes: String::new(), mods: Default::default(), dropins: vec![], dropins_engines: false, data_dir: Default::default(), server: None, late: vec![],
        };
        let err = set_entry_late(&install, &mut p, &loader, true).unwrap_err();
        assert!(err.message.contains("EMF's loader"), "{}", err.message);
        // the overlap hint for it: no one-click fix
        let v = |e: PlanEntry| ChainViewEntry { loader: e.is_emf_loader(), entry: e, late: false, late_by: None, off_by: None, gated: false };
        let overlap = Overlap {
            symbol: "S".into(), rva: "0x1".into(), kind: OverlapKind::ForeignBefore, owners: vec!["kiln.chat".into()], hook: None,
            foreign: Some(ForeignDest { module: "emf-dinput8.dll".into(), offset: "0x1".into(), ..Default::default() }),
        };
        let view = overlap_view(overlap, &[v(loader), v(other)], &[], &|_| false);
        assert_eq!(view.fix, "none");
        assert!(view.hint.contains("EMF's loader"), "{}", view.hint);
    }

    #[test]
    fn two_entries_with_one_file_name_get_no_one_click_fix() {
        let v = |path: &str, section, mod_id: Option<&str>| ChainViewEntry {
            entry: entry(path, section, None, mod_id), late: false, late_by: None, off_by: None, loader: false, gated: false,
        };
        let overlap = Overlap {
            symbol: "S".into(), rva: "0x1".into(), kind: OverlapKind::ForeignBefore, owners: vec!["kiln.chat".into()], hook: None,
            foreign: Some(ForeignDest { module: "Hook.dll".into(), offset: "0x1".into(), ..Default::default() }),
        };
        let one = overlap_view(overlap.clone(), &[v("chain\\hook.dll", Section::Chain, None)], &[], &|_| false);
        assert_eq!((one.fix.as_str(), one.entry.as_deref()), ("late", Some("chain\\hook.dll")));
        let two = overlap_view(overlap, &[v("chain\\hook.dll", Section::Chain, None), v("Z:\\a\\hook.dll", Section::Profile, Some("a"))], &[], &|_| false);
        assert_eq!((two.fix.as_str(), two.entry.as_deref()), ("none", None));
        assert!(two.hint.contains("2 chain entries"), "{}", two.hint);
    }

    fn view_entry(path: &str, section: Section, off_by: Option<&str>, late: bool) -> ChainViewEntry {
        ChainViewEntry { entry: entry(path, section, None, None), late, late_by: late.then(|| "player".to_string()), off_by: off_by.map(Into::into), loader: false, gated: false }
    }

    /// An entry a compat rule switches off does not load in this profile: the hint says so, and never "loads late already".
    #[test]
    fn a_rule_disabled_entry_is_not_said_to_load_late() {
        let overlap = |kind| Overlap {
            symbol: "S".into(), rva: "0x1".into(), kind, owners: vec!["kiln.chat".into()], hook: None,
            foreign: Some(ForeignDest { module: "hook.dll".into(), offset: "0x1".into(), ..Default::default() }),
        };
        let off = [view_entry("chain\\hook.dll", Section::Chain, Some("rule:xhl-off"), false)];
        for kind in [OverlapKind::ForeignBefore, OverlapKind::Refused, OverlapKind::ForeignOver] {
            let v = overlap_view(overlap(kind), &off, &[], &|_| false);
            assert_eq!(v.fix, "none", "{kind:?}");
            assert!(v.hint.contains("switched off by rule xhl-off"), "{kind:?}: {}", v.hint);
            assert!(!v.hint.contains("loads late already"), "{kind:?}: {}", v.hint);
        }
        // not switched off: the old hints stay
        let late = [view_entry("chain\\hook.dll", Section::Chain, None, true)];
        assert!(overlap_view(overlap(OverlapKind::ForeignBefore), &late, &[], &|_| false).hint.contains("loads late already"));
        let plain = [view_entry("chain\\hook.dll", Section::Chain, None, false)];
        assert_eq!(overlap_view(overlap(OverlapKind::ForeignBefore), &plain, &[], &|_| false).fix, "late");
    }

    /// shroudlink's client addon hooks before Kiln on purpose (Kiln's start waits for it to finish looking): an overlap
    /// never offers to load it late, which would switch its MODS tab off. Another DLL in that place keeps the fix.
    #[test]
    fn the_shroudlink_addon_is_never_offered_late() {
        let (_t, _dirs, install) = scratch_install("addon-late");
        let chain = install.path.join("kiln/chain");
        std::fs::create_dir_all(&chain).unwrap();
        // the addon as Kiln's start knows it: its two exports, whatever the file is called and whatever text it holds
        let addon_dll = crate::pe::build_test_dll(&["DirectInput8Create", "shroudlink_shutdown"], b"");
        for f in ["shroudlink-dinput8.dll", "shroudlink-dinput8-2.dll", "shroudlink-client.dll"] {
            std::fs::write(chain.join(f), &addon_dll).unwrap();
        }
        std::fs::write(chain.join("hook-dinput8.dll"), crate::pe::build_test_dll(&["DirectInput8Create"], b"some other mod")).unwrap();
        // a proxy that only mentions shroudlink is not the addon
        std::fs::write(chain.join("other-dinput8.dll"), crate::pe::build_test_dll(&["DirectInput8Create"], b"shroudlink addon")).unwrap();
        let gated = |e: &PlanEntry| is_gated_addon(&install, e);
        let overlap = |module: &str| Overlap {
            symbol: "S".into(), rva: "0x1".into(), kind: OverlapKind::ForeignBefore, owners: vec!["kiln-content".into()], hook: None,
            foreign: Some(ForeignDest { module: module.into(), offset: "0x1".into(), ..Default::default() }),
        };
        // the installer's first and second chain slots (install.rs chain_slot)
        for f in ["shroudlink-dinput8.dll", "shroudlink-dinput8-2.dll"] {
            let addon = [view_entry(&format!("chain\\{f}"), Section::Chain, None, false)];
            let v = overlap_view(overlap(f), &addon, &[], &gated);
            assert_eq!(v.fix, "none", "{f}: {}", v.hint);
            assert!(v.hint.contains("shroudlink's client addon") && v.hint.contains("native UI disabled"), "{}", v.hint);
        }
        // the same addon chained from a profile (a native mod at its game-side path), also renamed before import
        let game_side = format!("Z:{}", chain.join("shroudlink-client.dll").display()).replace('/', "\\");
        let native = [ChainViewEntry { entry: entry(&game_side, Section::Profile, None, Some("shroudlink-client")), late: false, late_by: None, off_by: None, loader: false, gated: true }];
        assert_eq!(overlap_view(overlap("shroudlink-client.dll"), &native, &[], &gated).fix, "none");
        for f in ["hook-dinput8.dll", "other-dinput8.dll"] {
            let other = [view_entry(&format!("chain\\{f}"), Section::Chain, None, false)];
            assert_eq!(overlap_view(overlap(f), &other, &[], &gated).fix, "late", "{f}");
        }
    }

    /// The manual late switch refuses it too, with the reason; switching late off stays possible.
    #[test]
    fn the_shroudlink_addon_cannot_be_switched_late() {
        let (_t, _dirs, install) = scratch_install("addon-switch");
        let chain = install.path.join("kiln/chain");
        std::fs::create_dir_all(&chain).unwrap();
        std::fs::write(chain.join("shroudlink-dinput8-2.dll"), crate::pe::build_test_dll(&["DirectInput8Create", "shroudlink_shutdown"], b"")).unwrap();
        std::fs::write(install.path.join("kiln/chain.ini"), "[chain]\nchain\\shroudlink-dinput8-2.dll\n").unwrap();
        let mut p = Profile {
            format: 1, id: "p".into(), name: "p".into(), created: String::new(), updated: String::new(), generation: 1,
            notes: String::new(), mods: Default::default(), dropins: vec![], dropins_engines: false, data_dir: Default::default(), server: None, late: vec![],
        };
        let chained = entry("chain\\shroudlink-dinput8-2.dll", Section::Chain, None, None);
        let err = set_entry_late(&install, &mut p, &chained, true).unwrap_err();
        assert!(err.message.contains("shroudlink's client addon") && err.message.contains("MODS tab"), "{}", err.message);
        assert!(!std::fs::read_to_string(install.path.join("kiln/chain.ini")).unwrap().contains("[late]"), "chain.ini unchanged");
        assert!(set_entry_late(&install, &mut p, &chained, false).is_ok(), "switching late off stays possible");
        let game_side = format!("Z:{}", chain.join("shroudlink-dinput8-2.dll").display()).replace('/', "\\");
        let native = entry(&game_side, Section::Profile, None, Some("shroudlink-client"));
        assert!(set_entry_late(&install, &mut p, &native, true).is_err() && p.late.is_empty(), "{:?}", p.late);
        std::fs::write(chain.join("hook-dinput8.dll"), crate::pe::build_test_dll(&["DirectInput8Create"], b"")).unwrap();
        let hook = entry(&format!("Z:{}", chain.join("hook-dinput8.dll").display()).replace('/', "\\"), Section::Profile, None, Some("hook"));
        assert!(set_entry_late(&install, &mut p, &hook, true).unwrap() && p.late == ["hook"]);
    }

    /// The view marks shroudlink's client addon in both shapes, a [chain] proxy and a profile's dllmain native, so the
    /// launcher offers no late switch for it; another proxy in the same place is not marked.
    #[cfg(unix)]
    #[test]
    fn the_view_marks_the_shroudlink_addon_as_a_chain_entry_and_as_a_profile_native() {
        let (t, dirs, mut install) = scratch_install("addon-view");
        // Z mode, so the profile native's game-side path leads back to its Library DLL
        let pfx = t.0.join("pfx");
        std::fs::create_dir_all(pfx.join("dosdevices")).unwrap();
        std::os::unix::fs::symlink("/", pfx.join("dosdevices/z:")).unwrap();
        install.prefix = Some(pfx);
        let addon = crate::pe::build_test_dll(&["DirectInput8Create", "shroudlink_shutdown"], b"");
        let chain = install.path.join("kiln/chain");
        std::fs::create_dir_all(&chain).unwrap();
        std::fs::write(chain.join("shroudlink-dinput8-2.dll"), &addon).unwrap();
        std::fs::write(chain.join("hook-dinput8.dll"), crate::pe::build_test_dll(&["DirectInput8Create"], b"shroudlink")).unwrap();
        std::fs::write(install.path.join("kiln/chain.ini"), "[chain]\r\nchain\\shroudlink-dinput8-2.dll\r\nchain\\hook-dinput8.dll\r\n").unwrap();
        // the addon as the player imports it: its dinput8.dll, a native mod (a bare proxy DLL: phase dllmain)
        let incoming = t.0.join("incoming");
        std::fs::create_dir_all(&incoming).unwrap();
        std::fs::write(incoming.join("dinput8.dll"), crate::pe::build_test_dll(&["DirectInput8Create", "shroudlink_shutdown"], b"shroudlink addon")).unwrap();
        let r = crate::library::import(&dirs, &[incoming.join("dinput8.dll")], &mut crate::task::NoProgress).unwrap();
        let m = r.imported.first().unwrap_or_else(|| panic!("not imported: {r:?}"));
        assert_eq!(m.kind, ModKind::Native, "{r:?}");
        let mut p = crate::profile::create(&dirs, "Addon", None).unwrap();
        p.mods.native.push(crate::profile::ProfileMod { id: m.id.clone(), version: m.version.clone(), hash: m.hash.clone(), enabled: true });
        let v = view(&dirs, &p, &install).unwrap();
        assert_eq!(v.error, None);
        let marks: Vec<(Section, &str, bool)> = v.entries.iter().map(|e| (e.entry.section, e.entry.file.as_str(), e.gated)).collect();
        assert_eq!(marks, [
            (Section::Chain, "shroudlink-dinput8-2.dll", true),
            (Section::Chain, "hook-dinput8.dll", false),
            (Section::Profile, "dinput8.dll", true),
        ]);
        drop(t);
    }

    struct Scratch(PathBuf);
    impl Drop for Scratch {
        fn drop(&mut self) {
            let _ = std::fs::remove_dir_all(&self.0);
        }
    }

    fn scratch_install(tag: &str) -> (Scratch, DataDirs, GameInstall) {
        let t = Scratch(std::env::temp_dir().join(format!("km-compat-{tag}-{}", std::process::id())));
        let _ = std::fs::remove_dir_all(&t.0);
        std::fs::create_dir_all(t.0.join("game/kiln/state")).unwrap();
        std::fs::write(t.0.join("game/enshrouded.exe"), b"MZ").unwrap();
        let install = GameInstall {
            id: "g".into(), kind: crate::game::GameKind::Client, path: t.0.join("game"), exe: "enshrouded.exe".into(),
            app_id: crate::CLIENT_APP_ID, library: None, platform: crate::game::Platform::Proton, prefix: None, build_id: None,
            exe_sha256: None, steam_flavor: None, path_mode: crate::game::PathMode::Copy, source: "manual".into(),
        };
        let dirs = DataDirs::at(t.0.join("data"));
        (t, dirs, install)
    }

    /// A late switch needs the entry only: the view reads hooks.json once (plan() hands over what it parsed), and
    /// find_entry reads none and finds the same entry the view lists.
    #[test]
    fn a_view_reads_hooks_json_once_and_find_entry_reads_none() {
        let (t, dirs, install) = scratch_install("reads");
        let game = &install.path;
        std::fs::create_dir_all(game.join("kiln/chain")).unwrap();
        for f in ["a.dll", "b.dll"] {
            std::fs::write(game.join("kiln/chain").join(f), crate::pe::build_test_dll(&[], f.as_bytes())).unwrap();
        }
        std::fs::write(game.join("kiln/chain.ini"), "[chain]\r\nchain\\a.dll\r\nchain\\b.dll\r\n[late]\r\nchain\\b.dll\r\n").unwrap();
        let schema = Path::new(env!("CARGO_MANIFEST_DIR")).join("../../schema");
        std::fs::copy(schema.join("examples/hooks-state.json"), game.join(HOOKS_JSON)).unwrap();
        let p = crate::profile::create(&dirs, "Reads", None).unwrap();

        HOOKS_READS.with(|c| c.set(0));
        let v = view(&dirs, &p, &install).unwrap();
        assert_eq!(HOOKS_READS.with(|c| c.get()), 1, "view() read hooks.json once");
        assert!(v.session.is_some() && !v.overlaps.is_empty(), "and still has the session and its overlaps: {v:?}");
        assert_eq!(v.entries.iter().map(|e| (e.entry.file.as_str(), e.late_by.as_deref())).collect::<Vec<_>>(), [("a.dll", None), ("b.dll", Some("player"))]);

        HOOKS_READS.with(|c| c.set(0));
        let e = find_entry(&dirs, &p, &install, "CHAIN\\B.dll").unwrap();
        assert_eq!(HOOKS_READS.with(|c| c.get()), 0, "find_entry() reads no hooks.json");
        assert_eq!(e, v.entries[1].entry);
        assert_eq!(find_entry(&dirs, &p, &install, "chain\\none.dll").unwrap_err().code, ErrorCode::NotFound);
        drop(t);
    }

    /// A [late] line another profile's rule wrote shows as that rule's, not as the player's game-wide choice.
    #[test]
    fn a_rule_made_late_line_shows_its_rule() {
        let (t, dirs, install) = scratch_install("rulemark");
        let game = &install.path;
        std::fs::write(
            game.join("kiln/chain.ini"),
            "[chain]\r\nchain\\xhl.dll\r\nchain\\mine.dll\r\n[late]\r\n; kiln rule other-profile\r\nchain\\xhl.dll\r\nchain\\mine.dll\r\n",
        )
        .unwrap();
        let p = crate::profile::create(&dirs, "Marks", None).unwrap();
        let v = view(&dirs, &p, &install).unwrap();
        let by = |f: &str| v.entries.iter().find(|e| e.entry.file == f).map(|e| (e.late, e.late_by.clone())).unwrap();
        assert_eq!(by("xhl.dll"), (true, Some("rule:other-profile".to_string())));
        assert_eq!(by("mine.dll"), (true, Some("player".to_string())));
        drop(t);
    }
}
