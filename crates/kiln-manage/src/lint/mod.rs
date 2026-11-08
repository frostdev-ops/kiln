//! kiln lint: checks the files a mod writes. Shared by `kiln lint` (kilnctl / the SDK CLI), the
//! launcher's library page (Tauri `library_lint`) and the VS Code extension (tools/vscode, through `kiln lint --json`).
//!
//! What it checks (every rule: docs/kiln/modding/lint.md):
//! - each file against its schema (schema/schemas.json maps files to schema/*.schema.json), in the JSON dialect the
//!   real Kiln reader accepts (comments, trailing commas), with exact byte spans;
//! - unknown keys with "did you mean", duplicate GUIDs / ids / names in a mod and across the mods linted together
//!   (a profile), references to game ids against the local game-data catalog (`kiln catalog` writes it; without one the
//!   reference checks are skipped with KL901), the manifest against the content (`clients`, `side`), cook recipes,
//!   assets, schematics, licences and files that must not be shipped.
//!
//! The JSON output (`LintReport::to_json`) is a contract: schema/lint-report.schema.json.

mod catalog;
pub mod codes;
pub mod docs;
pub mod json;
mod regex;
mod rules;
pub mod schema;
mod suggest;
#[cfg(test)]
mod sections_test;

pub use catalog::Catalog;

use serde::Serialize;
use std::collections::HashMap;
use std::path::{Path, PathBuf};
use std::sync::OnceLock;

// ---------------------------------------------------------------------------------------------------- types ----

#[derive(Debug, Clone, Copy, PartialEq, Eq, PartialOrd, Ord, Hash, Serialize, serde::Deserialize, Default)]
#[serde(rename_all = "lowercase")]
pub enum Severity {
    #[default]
    Hint,
    Info,
    Warning,
    Error,
}

impl Severity {
    pub fn as_str(self) -> &'static str {
        match self {
            Severity::Hint => "hint",
            Severity::Info => "info",
            Severity::Warning => "warning",
            Severity::Error => "error",
        }
    }
    pub fn parse(s: &str) -> Option<Severity> {
        match s {
            "hint" => Some(Severity::Hint),
            "info" => Some(Severity::Info),
            "warning" | "warn" => Some(Severity::Warning),
            "error" => Some(Severity::Error),
            _ => None,
        }
    }
}

/// UTF-8 byte offsets into the file, `end` exclusive.
#[derive(Debug, Clone, Copy, PartialEq, Eq, PartialOrd, Ord, Hash, Default, Serialize)]
pub struct Span {
    pub start: usize,
    pub end: usize,
}

impl Span {
    pub fn new(start: usize, end: usize) -> Span {
        Span { start, end: end.max(start) }
    }
    pub fn union(self, o: Span) -> Span {
        Span::new(self.start.min(o.start), self.end.max(o.end))
    }
}

#[derive(Debug, Clone, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct TextEdit {
    pub file: String,
    pub span: Span,
    pub new_text: String,
}

impl TextEdit {
    pub fn new(file: &str, span: Span, text: &str) -> TextEdit {
        TextEdit { file: file.to_string(), span, new_text: text.to_string() }
    }
}

#[derive(Debug, Clone, PartialEq, Serialize)]
pub struct Fix {
    pub title: String,
    pub edits: Vec<TextEdit>,
}

impl Fix {
    pub fn new(title: &str, edits: Vec<TextEdit>) -> Fix {
        Fix { title: title.to_string(), edits }
    }
}

#[derive(Debug, Clone, PartialEq, Serialize)]
pub struct Related {
    pub file: String,
    pub span: Span,
    pub message: String,
}

/// One finding. Positions: `span` (bytes, exact) and 1-based `line` / `col` / `endLine` / `endCol` (columns in
/// Unicode scalar values).
#[derive(Debug, Clone, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Diagnostic {
    pub file: String,
    pub code: String,
    pub severity: Severity,
    pub rule: String,
    pub message: String,
    pub span: Span,
    pub line: usize,
    pub col: usize,
    pub end_line: usize,
    pub end_col: usize,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub pointer: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub schema: Option<String>,
    pub fixes: Vec<Fix>,
    pub related: Vec<Related>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub docs: Option<String>,
}

impl Diagnostic {
    pub fn new(file: &str, code: &str, span: Span, message: &str) -> Diagnostic {
        let r = codes::rule(code);
        Diagnostic {
            file: file.to_string(),
            code: code.to_string(),
            severity: r.map(|r| r.severity).unwrap_or(Severity::Error),
            rule: r.map(|r| r.name.to_string()).unwrap_or_default(),
            message: message.to_string(),
            span,
            line: 0,
            col: 0,
            end_line: 0,
            end_col: 0,
            pointer: None,
            schema: None,
            fixes: vec![],
            related: vec![],
            docs: None,
        }
    }
    pub fn with_fix(mut self, f: Fix) -> Self {
        self.fixes.push(f);
        self
    }
    pub fn with_related(mut self, file: &str, span: Span, message: &str) -> Self {
        self.related.push(Related { file: file.to_string(), span, message: message.to_string() });
        self
    }
    pub fn severity(mut self, s: Severity) -> Self {
        self.severity = s;
        self
    }
    pub fn pointer(mut self, p: &str) -> Self {
        self.pointer = Some(p.to_string());
        self
    }
}

/// How to lint.
#[derive(Debug, Clone, Default)]
pub struct LintOptions {
    /// The game-data catalog (a file, or a folder holding client.json / server.json). Default: env KILN_CATALOG,
    /// then `<launcher data dir>/catalog/`.
    pub catalog: Option<PathBuf>,
    /// Do not look for a catalog at all (reproducible runs, tests).
    pub no_catalog: bool,
    /// Which catalog side to prefer when a folder holds both (default: client, then server).
    pub side: Option<String>,
    /// Other mods linted alongside for the cross-mod checks: a launcher profile folder (profiles/<id>, meta.json),
    /// a folder of mods (kiln/mods), or one mod folder. Their own findings are not reported.
    pub profile: Option<PathBuf>,
    /// Drop findings below this severity (default: keep everything).
    pub min_severity: Severity,
    /// Codes (KL103) or rule names (unknown-key) to turn off.
    pub rules_off: Vec<String>,
}

#[derive(Debug, Clone, Default, PartialEq, Serialize)]
pub struct Summary {
    pub errors: usize,
    pub warnings: usize,
    pub infos: usize,
    pub hints: usize,
}

#[derive(Debug, Clone, PartialEq, Serialize)]
pub struct CatalogInfo {
    pub path: String,
    pub side: Option<String>,
    pub build: Option<String>,
}

/// The result: `to_json()` is the stable machine form (schema/lint-report.schema.json).
#[derive(Debug, Clone, PartialEq, Serialize)]
pub struct LintReport {
    pub format: u32,
    pub tool: &'static str,
    pub version: &'static str,
    pub ok: bool,
    pub checked: Vec<String>,
    pub summary: Summary,
    pub catalog: Option<CatalogInfo>,
    pub diagnostics: Vec<Diagnostic>,
}

impl LintReport {
    pub fn to_json(&self) -> String {
        serde_json::to_string_pretty(self).unwrap_or_else(|_| "{}".into())
    }
    /// One line per finding (`<path>:<line>:<col>: <severity> <code>: <message>`), then a summary line.
    pub fn to_text(&self) -> String {
        let mut s = String::new();
        for d in &self.diagnostics {
            s.push_str(&format!("{}:{}:{}: {} {}: {}\n", d.file, d.line, d.col, d.severity.as_str(), d.code, d.message));
            for f in d.fixes.iter().take(1) {
                s.push_str(&format!("    fix: {}\n", f.title));
            }
        }
        let m = &self.summary;
        s.push_str(&format!(
            "{} error(s), {} warning(s), {} info, {} hint(s) in {} file(s){}\n",
            m.errors,
            m.warnings,
            m.infos,
            m.hints,
            self.checked.len(),
            match &self.catalog {
                Some(c) => format!("; catalog {}", c.path),
                None => String::new(),
            }
        ));
        s
    }
    /// 0: no errors, 1: errors.
    pub fn exit_code(&self) -> i32 {
        if self.summary.errors > 0 { 1 } else { 0 }
    }
}

/// Every rule (code, name, default severity, title).
pub fn rules() -> &'static [codes::Rule] {
    codes::ALL
}

// ---------------------------------------------------------------------------------------------------- schemas ----

/// One row of schema/schemas.json.
#[derive(Debug, Clone)]
pub struct SchemaEntry {
    pub schema: String,
    pub name: String,
    pub kind: String,
    pub file_match: Vec<String>,
    pub dialect: json::Dialect,
    pub container: Option<String>,
    pub docs: Option<String>,
}

pub(crate) struct Embedded {
    pub schemas: schema::Schemas,
    pub index: Vec<SchemaEntry>,
}

// The schemas the linter uses, compiled in (schema/ is the source; tests check the list is complete).
const EMBEDDED: &[(&str, &str)] = &[
    ("common.schema.json", include_str!("../../../../schema/common.schema.json")),
    ("kiln.mod.schema.json", include_str!("../../../../schema/kiln.mod.schema.json")),
    ("native.schema.json", include_str!("../../../../schema/native.schema.json")),
    ("kiln-modpack.schema.json", include_str!("../../../../schema/kiln-modpack.schema.json")),
    ("content-pack.schema.json", include_str!("../../../../schema/content-pack.schema.json")),
    ("assets.schema.json", include_str!("../../../../schema/assets.schema.json")),
    ("cook.schema.json", include_str!("../../../../schema/cook.schema.json")),
    ("impact-natives.schema.json", include_str!("../../../../schema/impact-natives.schema.json")),
    ("kschem-header.schema.json", include_str!("../../../../schema/kschem-header.schema.json")),
    ("skills.schema.json", include_str!("../../../../schema/skills.schema.json")),
    ("kiln-quests.schema.json", include_str!("../../../../schema/kiln-quests.schema.json")),
    ("kiln-blocks.schema.json", include_str!("../../../../schema/kiln-blocks.schema.json")),
    ("kiln-vfx.schema.json", include_str!("../../../../schema/kiln-vfx.schema.json")),
];
const INDEX: &str = include_str!("../../../../schema/schemas.json");

pub(crate) fn embedded() -> &'static Embedded {
    static E: OnceLock<Embedded> = OnceLock::new();
    E.get_or_init(|| {
        let docs = EMBEDDED.iter().map(|(id, text)| {
            let p = json::parse(id, text, 0, json::Dialect::JSONC);
            (id.to_string(), p.root.map(|n| n.to_json()).unwrap_or(serde_json::Value::Null))
        });
        let idx: serde_json::Value = serde_json::from_str(INDEX).unwrap_or_default();
        let index = idx["schemas"]
            .as_array()
            .into_iter()
            .flatten()
            .map(|e| SchemaEntry {
                schema: e["schema"].as_str().unwrap_or_default().to_string(),
                name: e["name"].as_str().unwrap_or_default().to_string(),
                kind: e["kind"].as_str().unwrap_or_default().to_string(),
                file_match: e["fileMatch"].as_array().into_iter().flatten().filter_map(|x| x.as_str().map(str::to_string)).collect(),
                dialect: json::Dialect {
                    comments: e["dialect"]["comments"].as_bool().unwrap_or(false),
                    trailing_commas: e["dialect"]["trailingCommas"].as_bool().unwrap_or(false),
                    bom: e["dialect"]["bom"].as_bool().unwrap_or_else(|| e["dialect"]["comments"].as_bool().unwrap_or(false)),
                },
                container: e["container"].as_str().map(str::to_string),
                docs: e["docs"].as_str().map(str::to_string),
            })
            .collect();
        Embedded { schemas: schema::Schemas::new(docs), index }
    })
}

/// The schema/schemas.json row for a path (relative or absolute), if any.
pub fn schema_for(path: &Path) -> Option<SchemaEntry> {
    let s = path.to_string_lossy().replace('\\', "/");
    embedded().index.iter().find(|e| suggest::file_match(&e.file_match, &s)).cloned()
}

// --------------------------------------------------------------------------------------------------- driver ----

/// Lints files and folders. A folder with kiln.mod.json or native.json is a mod; any other folder is searched for
/// mods (a kiln/mods folder, a repository) and for files schema/schemas.json knows. A file is linted with the rest of
/// its mod (only its own findings are reported).
pub fn lint_paths(paths: &[PathBuf], opts: &LintOptions) -> LintReport {
    let mut l = Linter::new(opts, HashMap::new());
    let paths: Vec<PathBuf> = if paths.is_empty() { vec![PathBuf::from(".")] } else { paths.to_vec() };
    let mut only: Option<Vec<String>> = Some(Vec::new());
    for p in &paths {
        let abs = absolute(p);
        if abs.is_dir() {
            only = None;
            l.add_dir(&abs);
        } else if abs.is_file() {
            if let Some(o) = only.as_mut() {
                o.push(display(&abs));
            }
            l.add_file(&abs);
        } else {
            only = None;
            l.diag(Diagnostic::new(&display(&abs), codes::UNREADABLE, Span::default(), "no such file or folder"));
        }
    }
    l.finish(only.as_deref())
}

/// Lints one file whose current text is `text` (an unsaved editor buffer), with the rest of its mod as on disk.
/// Only the findings of this file are reported.
pub fn lint_text(path: &Path, text: &str, opts: &LintOptions) -> LintReport {
    let abs = absolute(path);
    let mut overlay = HashMap::new();
    overlay.insert(abs.clone(), text.to_string());
    let mut l = Linter::new(opts, overlay);
    l.add_file(&abs);
    let me = display(&abs);
    l.finish(Some(&[me]))
}

/// Lints every enabled Kiln and native mod of a launcher profile together (cross-mod checks included).
pub fn lint_profile(dirs: &crate::paths::DataDirs, profile_id: &str, opts: &LintOptions) -> crate::Result<LintReport> {
    let dirs_of = profile_mod_dirs(dirs, profile_id)?;
    let mut l = Linter::new(opts, HashMap::new());
    for d in &dirs_of {
        l.add_dir(d);
    }
    Ok(l.finish(None))
}

/// Lints one library entry; with `profile_id`, the profile's other mods take part in the cross-mod checks.
pub fn lint_library_entry(dirs: &crate::paths::DataDirs, entry: &crate::library::LibraryRef, profile_id: Option<&str>, opts: &LintOptions) -> crate::Result<LintReport> {
    let dir = crate::library::path_of(dirs, entry)?;
    let mut l = Linter::new(opts, HashMap::new());
    if let Some(p) = profile_id {
        for d in profile_mod_dirs(dirs, p)? {
            if absolute(&d) != absolute(&dir) {
                l.add_context_dir(&d);
            }
        }
    }
    if dir.is_dir() {
        l.add_dir(&dir);
    } else {
        l.add_file(&dir);
    }
    Ok(l.finish(None))
}

/// The folders of a profile's enabled Kiln and native mods, in profile order.
pub fn profile_mod_dirs(dirs: &crate::paths::DataDirs, profile_id: &str) -> crate::Result<Vec<PathBuf>> {
    let p = crate::profile::get(dirs, profile_id)?;
    let mut out = Vec::new();
    for (kind, list) in [(crate::manifest::ModKind::Kiln, &p.mods.kiln), (crate::manifest::ModKind::Native, &p.mods.native)] {
        for m in list.iter().filter(|m| m.enabled) {
            let r = crate::library::LibraryRef { kind, id: m.id.clone(), version: m.version.clone(), hash: m.hash.clone() };
            if let Ok(d) = crate::library::path_of(dirs, &r) {
                out.push(d);
            }
        }
    }
    Ok(out)
}

pub(crate) fn absolute(p: &Path) -> PathBuf {
    std::path::absolute(p).unwrap_or_else(|_| p.to_path_buf())
}

pub(crate) fn display(p: &Path) -> String {
    p.to_string_lossy().into_owned()
}

pub(crate) const SKIP_DIRS: &[&str] = &["bin", "obj", "build", "node_modules", ".git", "target", ".out", ".vs", ".idea"];
const MAX_DEPTH: usize = 8;

/// A file read for linting.
pub(crate) struct LFile {
    pub path: PathBuf,
    pub display: String,
    pub kind: String,
    pub bytes: Vec<u8>,
    pub root: Option<json::Node>,
    pub annots: Vec<schema::Annot>,
    /// Byte offset of the JSON text in the file (a .kschem header).
    pub base: usize,
    /// A kiln.mod.json Kiln.Sdk generated into obj/ from this project file: findings are reported there.
    pub generated_from: Option<PathBuf>,
}

/// A mod being linted (or a context mod of the profile: `report` false).
pub(crate) struct LMod {
    pub root: PathBuf,
    pub display: String,
    pub id: Option<String>,
    pub native: bool,
    pub report: bool,
    pub files: Vec<LFile>,
}

impl LMod {
    pub fn file(&self, kind: &str) -> Option<&LFile> {
        self.files.iter().find(|f| f.kind == kind)
    }
    pub fn manifest(&self) -> Option<&LFile> {
        self.file("manifest").or_else(|| self.file("native"))
    }
}

pub(crate) struct Linter<'o> {
    pub opts: &'o LintOptions,
    pub overlay: HashMap<PathBuf, String>,
    pub mods: Vec<LMod>,
    /// Files outside any mod.
    pub loose: Vec<LFile>,
    pub diags: Vec<Diagnostic>,
    pub texts: HashMap<String, Vec<u8>>,
    pub catalog: Option<Catalog>,
    pub catalog_problem: Option<String>,
}

impl<'o> Linter<'o> {
    fn new(opts: &'o LintOptions, overlay: HashMap<PathBuf, String>) -> Self {
        let (catalog, problem) = if opts.no_catalog { (None, None) } else { catalog::locate(opts) };
        let mut l = Linter { opts, overlay, mods: vec![], loose: vec![], diags: vec![], texts: HashMap::new(), catalog, catalog_problem: problem };
        if let Some(p) = &opts.profile {
            let p = absolute(p);
            let meta = p.join("meta.json");
            if meta.is_file()
                && let (Some(id), Some(root)) = (p.file_name().map(|f| f.to_string_lossy().into_owned()), p.parent().and_then(Path::parent))
            {
                if let Ok(ds) = profile_mod_dirs(&crate::paths::DataDirs::at(root), &id) {
                    for d in ds {
                        l.add_context_dir(&d);
                    }
                }
            } else if p.is_dir() {
                l.add_context_dir(&p);
            }
        }
        l
    }

    pub fn diag(&mut self, d: Diagnostic) {
        self.diags.push(d);
    }

    fn read(&self, p: &Path) -> std::io::Result<Vec<u8>> {
        match self.overlay.get(p) {
            Some(t) => Ok(t.clone().into_bytes()),
            None => std::fs::read(p),
        }
    }

    pub(crate) fn is_mod_root(d: &Path) -> bool {
        d.join("kiln.mod.json").is_file() || d.join("native.json").is_file() || sdk_project(d).is_some()
    }

    fn find_mod_root(file: &Path) -> Option<PathBuf> {
        let mut d = file.parent();
        for _ in 0..5 {
            let dir = d?;
            if Self::is_mod_root(dir) {
                return Some(dir.to_path_buf());
            }
            d = dir.parent();
        }
        None
    }

    fn add_file(&mut self, file: &Path) {
        if let Some(root) = Self::find_mod_root(file) {
            match self.mods.iter_mut().find(|m| m.root == root) {
                Some(m) => m.report = true,
                None => self.add_mod(&root, true),
            }
            if let Some(m) = self.mods.iter().find(|m| m.root == root)
                && m.files.iter().any(|f| f.path == file)
            {
                return;
            }
        }
        // not a file its mod reads: lint it on its own, by its name
        let d = display(file);
        match schema_for(file) {
            Some(e) => {
                if let Some(f) = self.load(file, &e) {
                    self.loose.push(f);
                }
            }
            None => self.diag(Diagnostic::new(&d, codes::NOTHING, Span::default(), "kiln lint does not know this file (schema/schemas.json maps names to schemas)")),
        }
    }

    fn add_context_dir(&mut self, dir: &Path) {
        let before = self.mods.len();
        self.walk(dir, 0, false);
        for m in &mut self.mods[before..] {
            m.report = false;
        }
    }

    fn add_dir(&mut self, dir: &Path) {
        let before = self.mods.len() + self.loose.len();
        self.walk(dir, 0, true);
        if self.mods.len() + self.loose.len() == before {
            self.diag(Diagnostic::new(&display(dir), codes::NOTHING, Span::default(), "no mod (kiln.mod.json, native.json) and no file kiln lint knows in this folder"));
        }
    }

    fn walk(&mut self, dir: &Path, depth: usize, report: bool) {
        if depth > MAX_DEPTH {
            return;
        }
        if Self::is_mod_root(dir) {
            match self.mods.iter_mut().find(|m| m.root == dir) {
                Some(m) => m.report |= report,
                None => self.add_mod(dir, report),
            }
        } else if depth == 0 || !self.mods.iter().any(|m| dir.starts_with(&m.root)) {
            // loose files the schemas know (a modpack manifest, a stray schematic)
            if let Ok(rd) = std::fs::read_dir(dir) {
                let mut files: Vec<PathBuf> = rd.flatten().map(|e| e.path()).filter(|p| p.is_file()).collect();
                files.sort();
                for f in files {
                    if self.mods.iter().any(|m| f.starts_with(&m.root)) {
                        continue;
                    }
                    if let Some(e) = schema_for(&f)
                        && (e.kind == "modpack" || e.kind == "schematic")
                        && let Some(lf) = self.load(&f, &e)
                    {
                        self.loose.push(lf);
                    }
                }
            }
        }
        let Ok(rd) = std::fs::read_dir(dir) else { return };
        let mut subs: Vec<PathBuf> = rd.flatten().filter(|e| e.file_type().is_ok_and(|t| t.is_dir())).map(|e| e.path()).collect();
        subs.sort();
        for s in subs {
            let name = s.file_name().map(|n| n.to_string_lossy().into_owned()).unwrap_or_default();
            if SKIP_DIRS.contains(&name.as_str()) {
                continue;
            }
            self.walk(&s, depth + 1, report);
        }
    }

    fn add_mod(&mut self, root: &Path, report: bool) {
        let mut m = LMod { root: root.to_path_buf(), display: display(root), id: None, native: false, report, files: vec![] };
        let idx = embedded();
        let entry = |kind: &str| idx.index.iter().find(|e| e.kind == kind).cloned();
        let mut want: Vec<(PathBuf, String)> = vec![];
        if root.join("kiln.mod.json").is_file() {
            want.push((root.join("kiln.mod.json"), "manifest".into()));
        }
        if !root.join("kiln.mod.json").is_file()
            && let Some(proj) = sdk_project(root)
        {
            // Kiln.Sdk generates kiln.mod.json into obj/ at build time: lint the newest one, report on the project
            match generated_manifest(root) {
                Some(g) => {
                    if let Some(e) = entry("manifest")
                        && let Some(mut f) = self.load(&g, &e)
                    {
                        f.generated_from = Some(proj.clone());
                        m.files.push(f);
                    }
                }
                None => self.diag(Diagnostic::new(&display(&proj), codes::NOT_BUILT, Span::default(), "a Kiln.Sdk project that was not built yet: build it once (dotnet build) so kiln lint can check the kiln.mod.json it generates")),
            }
        }
        if root.join("native.json").is_file() {
            want.push((root.join("native.json"), "native".into()));
            m.native = !root.join("kiln.mod.json").is_file();
        }
        // content/*.json, as kiln.content discovers them (one folder, any case of .json)
        if let Ok(rd) = std::fs::read_dir(root.join("content")) {
            let mut v: Vec<PathBuf> = rd.flatten().map(|e| e.path()).filter(|p| p.is_file() && p.extension().is_some_and(|x| x.eq_ignore_ascii_case("json"))).collect();
            v.sort();
            want.extend(v.into_iter().map(|p| (p, "content".to_string())));
        }
        // skills/*.json (Kiln.Skills, native/services/skills: every file of the folder, by name)
        if let Ok(rd) = std::fs::read_dir(root.join("skills")) {
            let mut v: Vec<PathBuf> = rd.flatten().map(|e| e.path()).filter(|p| p.is_file() && p.extension().is_some_and(|x| x.eq_ignore_ascii_case("json"))).collect();
            v.sort();
            want.extend(v.into_iter().map(|p| (p, "skills".to_string())));
        }
        // quests/*.json (Kiln.Quests: every file of the folder, by name), and quests/generated/*.json: what the mod's code defines, written
        // down for the boot build (Kiln.Quests/Native/NativeExport.cs)
        if let Ok(rd) = std::fs::read_dir(root.join("quests")) {
            let mut v: Vec<PathBuf> = rd.flatten().map(|e| e.path()).filter(|p| p.is_file() && p.extension().is_some_and(|x| x.eq_ignore_ascii_case("json"))).collect();
            v.sort();
            want.extend(v.into_iter().map(|p| (p, "quests".to_string())));
        }
        if let Ok(rd) = std::fs::read_dir(root.join("quests").join("generated")) {
            let mut v: Vec<PathBuf> = rd.flatten().map(|e| e.path()).filter(|p| p.is_file() && p.extension().is_some_and(|x| x.eq_ignore_ascii_case("json"))).collect();
            v.sort();
            want.extend(v.into_iter().map(|p| (p, "quests".to_string())));
        }
        // blocks/*.json (Kiln.Blocks: every file of the folder, by name)
        if let Ok(rd) = std::fs::read_dir(root.join("blocks")) {
            let mut v: Vec<PathBuf> = rd.flatten().map(|e| e.path()).filter(|p| p.is_file() && p.extension().is_some_and(|x| x.eq_ignore_ascii_case("json"))).collect();
            v.sort();
            want.extend(v.into_iter().map(|p| (p, "blocks".to_string())));
        }
        for (rel, kind) in [("assets/assets.json", "assets"), ("assets/cook.json", "cook"), ("impact/cook.json", "cook"), ("impact/natives.json", "natives"), ("kiln-modpack.json", "modpack")] {
            if root.join(rel).is_file() {
                want.push((root.join(rel), kind.into()));
            }
        }
        for k in find_files(root, "kschem") {
            want.push((k, "schematic".into()));
        }
        for (path, kind) in want {
            if let Some(e) = entry(&kind)
                && let Some(f) = self.load(&path, &e)
            {
                m.files.push(f);
            }
        }
        if let Some(mf) = m.manifest()
            && let Some(id) = mf.root.as_ref().and_then(|r| r.str_of("id"))
        {
            m.id = Some(id.to_string());
        }
        self.mods.push(m);
    }

    /// Reads, parses and schema-checks one file.
    fn load(&mut self, path: &Path, e: &SchemaEntry) -> Option<LFile> {
        let d = display(path);
        let bytes = match self.read(path) {
            Ok(b) => b,
            Err(err) => {
                self.diag(Diagnostic::new(&d, codes::UNREADABLE, Span::default(), &format!("cannot read: {err}")));
                return None;
            }
        };
        let mut f = LFile { path: path.to_path_buf(), display: d.clone(), kind: e.kind.clone(), bytes, root: None, annots: vec![], base: 0, generated_from: None };
        let (text, base) = if e.container.as_deref() == Some("kschem") {
            match rules::kschem_header(&f.bytes) {
                Ok((start, end)) => (std::str::from_utf8(&f.bytes[start..end]).ok().map(str::to_string), start),
                Err((span, msg)) => {
                    self.diag(Diagnostic::new(&d, codes::CONTAINER, span, &msg));
                    self.texts.insert(d, f.bytes.clone());
                    return Some(f);
                }
            }
        } else {
            (std::str::from_utf8(&f.bytes).ok().map(str::to_string), 0)
        };
        f.base = base;
        self.texts.insert(d.clone(), f.bytes.clone());
        let Some(text) = text else {
            let at = std::str::from_utf8(&f.bytes[base..]).err().map(|e| base + e.valid_up_to()).unwrap_or(0);
            self.diag(Diagnostic::new(&d, codes::NOT_UTF8, Span::new(at, at + 1), "not valid UTF-8 (Kiln's readers take UTF-8)"));
            return Some(f);
        };
        let parsed = json::parse(&d, &text, base, e.dialect);
        let syntax_ok = parsed.root.is_some();
        for mut x in parsed.diagnostics {
            x.docs = e.docs.clone();
            if !e.dialect.comments && x.code == codes::COMMENT {
                x.message = format!("comments are not allowed: {}", reader_note(e));
            }
            if !e.dialect.trailing_commas && x.code == codes::TRAILING_COMMA {
                x.message = format!("trailing comma: {}", reader_note(e));
            }
            self.diag(x);
        }
        if let Some(root) = parsed.root {
            if syntax_ok {
                let out = embedded().schemas.validate(&e.schema, &root);
                for i in out.issues {
                    let mut dg = Diagnostic::new(&d, i.code, i.span, &i.message).severity(i.severity).pointer(&i.pointer);
                    dg.schema = Some(e.schema.clone());
                    dg.docs = i.docs.or_else(|| e.docs.clone());
                    for mut fx in i.fixes {
                        for ed in &mut fx.edits {
                            if ed.file.is_empty() {
                                ed.file = d.clone();
                            }
                        }
                        dg.fixes.push(fx);
                    }
                    self.diag(dg);
                }
                f.annots = out.annots;
            }
            f.root = Some(root);
        }
        Some(f)
    }

    fn finish(mut self, only: Option<&[String]>) -> LintReport {
        rules::run(&mut self);
        let reported: Vec<String> = {
            let mut v: Vec<String> = Vec::new();
            for m in self.mods.iter().filter(|m| m.report) {
                v.push(m.display.clone());
                v.extend(m.files.iter().map(|f| f.display.clone()));
            }
            v.extend(self.loose.iter().map(|f| f.display.clone()));
            v
        };
        let context: Vec<String> = self.mods.iter().filter(|m| !m.report).map(|m| m.display.clone()).collect();
        let checked: Vec<String> = {
            let mut c: Vec<String> = self.mods.iter().filter(|m| m.report).flat_map(|m| m.files.iter().map(|f| f.display.clone())).collect();
            c.extend(self.loose.iter().map(|f| f.display.clone()));
            if let Some(o) = only {
                c.retain(|x| o.contains(x));
            }
            c
        };
        let off: Vec<String> = self.opts.rules_off.iter().map(|s| s.to_string()).collect();
        let min = self.opts.min_severity;
        let texts = std::mem::take(&mut self.texts);
        let mut diags: Vec<Diagnostic> = std::mem::take(&mut self.diags)
            .into_iter()
            .filter(|d| d.severity >= min && !off.iter().any(|o| o == &d.code || o == &d.rule))
            .filter(|d| match only {
                Some(o) => o.contains(&d.file),
                None => !context.iter().any(|c| d.file.starts_with(&format!("{c}{}", std::path::MAIN_SEPARATOR)) || &d.file == c) || reported.contains(&d.file),
            })
            .collect();
        // findings in a generated manifest belong to its project file
        let generated: HashMap<String, String> = self.mods.iter().flat_map(|m| m.files.iter()).filter_map(|f| f.generated_from.as_ref().map(|p| (f.display.clone(), display(p)))).collect();
        for d in &mut diags {
            if let Some(proj) = generated.get(&d.file) {
                d.message = format!("{} (in the kiln.mod.json Kiln.Sdk generates from this project: set it with the project's properties)", d.message);
                d.file = proj.clone();
                d.span = Span::default();
                d.fixes.clear();
            }
            for r in &mut d.related {
                if let Some(proj) = generated.get(&r.file) {
                    r.file = proj.clone();
                    r.span = Span::default();
                }
            }
        }
        for d in &mut diags {
            if let Some(t) = texts.get(&d.file) {
                let (l, c) = line_col(t, d.span.start);
                let (el, ec) = line_col(t, d.span.end);
                (d.line, d.col, d.end_line, d.end_col) = (l, c, el, ec);
            } else {
                (d.line, d.col, d.end_line, d.end_col) = (1, 1, 1, 1);
            }
            if d.docs.is_none() {
                d.docs = Some(format!("docs/kiln/modding/lint.md#{}-{}", d.code.to_lowercase(), d.rule));
            }
        }
        diags.sort_by(|a, b| (a.file.as_str(), a.span.start, a.code.as_str(), a.message.as_str()).cmp(&(b.file.as_str(), b.span.start, b.code.as_str(), b.message.as_str())));
        diags.dedup_by(|a, b| a.file == b.file && a.span == b.span && a.code == b.code && a.message == b.message);
        let mut s = Summary::default();
        for d in &diags {
            match d.severity {
                Severity::Error => s.errors += 1,
                Severity::Warning => s.warnings += 1,
                Severity::Info => s.infos += 1,
                Severity::Hint => s.hints += 1,
            }
        }
        LintReport {
            format: 1,
            tool: "kiln-lint",
            version: crate::VERSION,
            ok: s.errors == 0,
            checked,
            summary: s,
            catalog: self.catalog.as_ref().map(|c| CatalogInfo { path: display(&c.path), side: c.side.clone(), build: c.build.clone() }),
            diagnostics: diags,
        }
    }
}

/// The project file of a Kiln.Sdk mod project (`<Project Sdk="Kiln.Sdk/...">`, or one that imports Kiln.Sdk).
pub(crate) fn sdk_project(d: &Path) -> Option<PathBuf> {
    let rd = std::fs::read_dir(d).ok()?;
    let mut v: Vec<PathBuf> = rd.flatten().map(|e| e.path()).filter(|p| p.extension().is_some_and(|x| x.eq_ignore_ascii_case("csproj"))).collect();
    v.sort();
    v.into_iter().find(|p| {
        std::fs::read_to_string(p).is_ok_and(|t| {
            let head: String = t.chars().take(4096).collect();
            head.contains("Sdk=\"Kiln.Sdk") || head.contains("Project=\"Sdk.props\" Sdk=\"Kiln.Sdk") || head.contains("Sdk Name=\"Kiln.Sdk")
        })
    })
}

/// The newest obj/**/kiln.mod.json of a Kiln.Sdk project.
fn generated_manifest(d: &Path) -> Option<PathBuf> {
    fn go(dir: &Path, depth: usize, best: &mut Option<(std::time::SystemTime, PathBuf)>) {
        if depth > 5 {
            return;
        }
        let Ok(rd) = std::fs::read_dir(dir) else { return };
        for e in rd.flatten() {
            let p = e.path();
            if p.is_dir() {
                go(&p, depth + 1, best);
            } else if p.file_name().is_some_and(|n| n == "kiln.mod.json")
                && let Ok(t) = p.metadata().and_then(|m| m.modified())
                && best.as_ref().is_none_or(|(bt, _)| t > *bt)
            {
                *best = Some((t, p));
            }
        }
    }
    let mut best = None;
    go(&d.join("obj"), 0, &mut best);
    best.map(|(_, p)| p)
}

fn reader_note(e: &SchemaEntry) -> String {
    match e.kind.as_str() {
        "cook" | "natives" => "the cook tools read this file with Python's json module (plain JSON)".into(),
        "modpack" => "the launcher reads this file as plain JSON".into(),
        "schematic" => "the schematic header is plain JSON".into(),
        "content" | "assets" => "kiln.content / kiln.assets read this file with nlohmann::json, which allows comments but not trailing commas".into(),
        _ => "its reader refuses it".into(),
    }
}

fn find_files(root: &Path, ext: &str) -> Vec<PathBuf> {
    fn go(d: &Path, ext: &str, depth: usize, root: &Path, out: &mut Vec<PathBuf>) {
        if depth > MAX_DEPTH {
            return;
        }
        let Ok(rd) = std::fs::read_dir(d) else { return };
        let mut v: Vec<PathBuf> = rd.flatten().map(|e| e.path()).collect();
        v.sort();
        for p in v {
            let name = p.file_name().map(|n| n.to_string_lossy().into_owned()).unwrap_or_default();
            if p.is_dir() {
                if !SKIP_DIRS.contains(&name.as_str()) && (p == root || !Linter::is_mod_root(&p)) {
                    go(&p, ext, depth + 1, root, out);
                }
            } else if p.extension().is_some_and(|x| x.eq_ignore_ascii_case(ext)) {
                out.push(p);
            }
        }
    }
    let mut out = Vec::new();
    go(root, ext, 0, root, &mut out);
    out
}

/// 1-based line and column (columns in Unicode scalar values; invalid UTF-8 counts bytes).
pub(crate) fn line_col(t: &[u8], at: usize) -> (usize, usize) {
    let at = at.min(t.len());
    let line_start = t[..at].iter().rposition(|b| *b == b'\n').map_or(0, |p| p + 1);
    let line = t[..at].iter().filter(|b| **b == b'\n').count() + 1;
    let seg = &t[line_start..at];
    let col = match std::str::from_utf8(seg) {
        Ok(s) => s.chars().count(),
        Err(_) => seg.iter().filter(|b| (**b & 0xC0) != 0x80).count(),
    };
    (line, col + 1)
}

#[cfg(test)]
mod tests;
