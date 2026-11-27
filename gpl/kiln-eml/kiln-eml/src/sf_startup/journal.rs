// SPDX-License-Identifier: GPL-3.0-or-later
//! kiln-eml: the startup change journal of the ShroudForge-dialect pass, kiln-eml's own
//! code. Per package of the committed attempt, which archive resources its entrypoint wrote, which it created (each
//! with a per-package ordinal) and which contents it created; never a value. A later Kiln registry stage diffs the base
//! against the published archive field by field and names each change's writer from this record; a later effect
//! replay can name a package's changes by the same keys after a game update.
//!
//! **What counts, exactly.** The pass runs one package's entrypoint at a time ([`Recorder::begin`] / [`Recorder::end`]).
//! Every typed value of a resource carries that resource as its owner (`ValueObjects::owned_by`: the decoded value of
//! `Resource.data`, a value validated into a resource by `create_resource` or the `data` setter, every wrapper read
//! from those, and every copy assigned into them, which belongs to the resource it is assigned into, never to its
//! source). Each write through such a wrapper (an assignment, `table.insert`, `table.remove`, `table.clear`, a
//! variant's `value`) and each `data` assignment is noted for the package whose entrypoint is running, so:
//! - `changed` lists the resources a package wrote, whoever changed them before (a resource two packages edit is
//!   listed for both), also through a reference kept from an earlier package (a shared module) or written by another
//!   package's module function the entrypoint called (the running entrypoint is the writer, as ShroudForge names the
//!   failing package by its entrypoint); a resource a package only read is never listed. A write into a value the
//!   package itself detached from its resource earlier (a removed element, a replaced field kept in a variable) still
//!   names that resource: the journal can over-name a writer, never miss one;
//! - `created` lists the resources a package created, in creation order: the ordinal (1, 2, ...) is the resource's
//!   name across runs (`<package id>:<ordinal>`), since a created resource's random GUID differs per publication; it
//!   holds while the package creates in the same order (the Lua runtime seeds its string hashing per run, so creations
//!   made while walking a string-keyed table can be numbered differently); an explicit GUID is marked as such; a
//!   package's writes into its own creations are not listed again;
//! - `contents` lists each content a package created (`create_content`), once per package, first call first;
//! - code that runs inside an entrypoint is that entrypoint's, whoever wrote it (another package's module function, a
//!   finalizer the collector happens to run then); a write made while no entrypoint runs goes to `unattributed`
//!   (normally empty: the pass runs no Lua between entrypoints).
//!
//! No value is walked or compared here: the cost is one cell comparison per write, plus one set lookup the first time a
//! resource is written in an entrypoint.
//!
//! **Bounds.** At most [`PACKAGE_ENTRIES`] entries (changed + created + contents) per package (and for the unattributed
//! bucket), [`TOTAL_ENTRIES`] in all; what is over is left out and marks the package and the journal `truncated`, never
//! a failure. Created resources keep their ordinals (the ones left out are the last).
//!
//! The record (startup-journal.schema.json v1) is written by the startup controller beside the ownership receipt, bound
//! to the published generation (startup.rs); this module only records and formats.
//!
//! **The EML dialect (format 2).** EML's own pass (`runner::run` outside kiln mode) records its writes in EML's runtime
//! (mod-loader-lua `env/writes.rs`: per mod, the resources its entrypoint wrote with the member paths it wrote, at their
//! published places, the ones it created and the contents it created); [`Journal::from_eml`] bounds that record as this
//! module bounds its own, and [`Journal::json`] writes it as format 2 (eml-journal-2.schema.json): the same record with a
//! `paths` list on each changed resource (`[""]`: the whole resource) and no plan digests (`null`: no launch plan froze
//! them). The runner writes it beside the receipt the publication wrote, bound to that receipt (`generation` is its
//! SHA-256, as kiln-eml's eml.json reports the live generation).
//!
//! **Member paths of the ShroudForge dialect (format 2, dialect `shroudforge`; 0.2.0-dev.32).** The asset session compares,
//! after each entrypoint, the resources it wrote with what they held before it ran (`journal_paths.rs`) and gives each
//! changed resource the member paths the package CHANGED there ([`Journal::member_paths`]): a resource a package wrote back
//! as it was is then left out of its `changed`. Such a journal is written as format 2 of dialect `shroudforge` (plan
//! digests as format 1's), with `notApplied` (the packages the committed attempt did not run, [`NotApplied`]).
use std::{
    cell::{Cell, RefCell},
    collections::{BTreeSet, HashMap, HashSet},
    rc::Rc,
};

use kfc::guid::{ContentHash, ResourceId};

use super::resource_values::WriteOwner;

/// The most entries one package (or the unattributed bucket) keeps: changed, created and contents together.
pub const PACKAGE_ENTRIES: usize = 4096;
/// The most entries a journal keeps in all.
pub const TOTAL_ENTRIES: usize = 32768;
/// Format 2: the most member paths one changed resource lists; over that it is the whole resource (`[""]`). Room for a
/// path per element of the game's largest shared resource (2,542 recipes in one `RecipeRegistryResource`) several times
/// over: below it a recipe or loot mod is named on the elements and fields it wrote, not on every one.
pub const RESOURCE_PATHS: usize = 16384;
/// Format 2: the longest member path (bytes); a longer one is cut back to the member that holds it.
pub const PATH_BYTES: usize = 256;
/// Format 2: the most bytes the member paths of one journal take as written (each path with its quotes and comma); a
/// changed resource whose paths would go past it is listed whole (`[""]`). With the entry bounds this keeps a journal
/// well below the 16 MiB a reader takes (`StartupJournal::read`, Kiln's reader): past that it would name nobody.
pub const JOURNAL_PATH_BYTES: usize = 8 << 20;

/// A resource a package created, the `ordinal`-th (1-based) of its creations.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Created {
    pub ordinal: u32,
    pub id: ResourceId,
    /// The package gave the GUID (and part) itself; else it was drawn at random.
    pub explicit: bool,
}

/// A resource a package wrote; `created_by`: the package (id, ordinal) that created it in this pass, if one did.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Changed {
    pub id: ResourceId,
    pub created_by: Option<(String, u32)>,
    /// The EML dialect (format 2): the member paths written, sorted, each once (`[""]`: the whole resource). None in the
    /// ShroudForge dialect, whose record is per resource (format 1).
    pub paths: Option<Vec<String>>,
}

/// What one package's entrypoint (or, without an id, everything outside an entrypoint) changed.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct Changes {
    /// None: the unattributed bucket.
    pub id: Option<String>,
    /// In resource-key order.
    pub changed: Vec<Changed>,
    /// In creation order.
    pub created: Vec<Created>,
    /// In first-creation order.
    pub contents: Vec<ContentHash>,
    pub truncated: bool,
}

/// The journal of one attempt: its packages in the order their entrypoints ran.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct Journal {
    pub packages: Vec<Changes>,
    pub unattributed: Changes,
    pub truncated: bool,
    /// Every changed resource has its member paths (format 2): the EML dialect's record, and (0.2.0-dev.32) the ShroudForge
    /// dialect's when the asset session compared each package's resources (`AssetSession::take_journal`).
    pub member_paths: bool,
}

/// A package of the plan whose pregame Lua the committed attempt did not run (format 2's `notApplied`): the package that
/// failed (the retry ran without it) and those left out with it.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct NotApplied {
    pub id: String,
    pub version: Option<String>,
    /// `shroudforge` or `eml`.
    pub kind: &'static str,
    /// The plan's content digest (all zero: none, written null).
    pub content_sha256: [u8; 32],
    /// One line (cut to 512 bytes, control characters as spaces).
    pub reason: String,
}

#[derive(Default)]
struct Bucket {
    id: Option<String>,
    written: BTreeSet<ResourceId>,
    seen: HashSet<ContentHash>,
    creations: u32,
    created: Vec<Created>,
    contents: Vec<ContentHash>,
    truncated: bool,
}
impl Bucket {
    fn entries(&self) -> usize {
        self.written.len() + self.created.len() + self.contents.len()
    }
}

struct State {
    /// Bucket 0 is the unattributed one; each entrypoint gets the next.
    buckets: Vec<Bucket>,
    current: usize,
    /// Every resource created in this pass: its creator's bucket and ordinal.
    creators: HashMap<ResourceId, (usize, u32)>,
    entries: usize,
    truncated: bool,
    closed: bool,
}

/// The pass's recorder: one per asset session (so one per attempt), shared by every resource owner it hands out.
/// Worker-local (`Rc`, `Cell`): the VM's thread only. It holds keys and ids, never a Lua value.
pub struct Recorder {
    state: RefCell<State>,
    /// Moves at every `begin`/`end`: an owner that already noted itself in this epoch skips the set lookup.
    epoch: Cell<u64>,
}

impl Default for Recorder {
    fn default() -> Self {
        Self {
            state: RefCell::new(State {
                buckets: vec![Bucket::default()],
                current: 0,
                creators: HashMap::new(),
                entries: 0,
                truncated: false,
                closed: false,
            }),
            epoch: Cell::new(0),
        }
    }
}

impl State {
    /// Room for one more entry in the current bucket; else it and the journal are marked truncated.
    fn room(&mut self) -> bool {
        let bucket = &mut self.buckets[self.current];
        if bucket.entries() < PACKAGE_ENTRIES && self.entries < TOTAL_ENTRIES {
            self.entries += 1;
            true
        } else {
            bucket.truncated = true;
            self.truncated = true;
            false
        }
    }
}

impl Recorder {
    /// `package`'s entrypoint begins: what is noted from now on is its own.
    pub fn begin(&self, package: &str) {
        let mut state = self.state.borrow_mut();
        if state.closed {
            return;
        }
        state.buckets.push(Bucket {
            id: Some(package.to_owned()),
            ..Bucket::default()
        });
        state.current = state.buckets.len() - 1;
        self.epoch.set(self.epoch.get() + 1);
    }

    /// kiln-eml (format 2 of the ShroudForge dialect): the running entrypoint's bucket (0: none runs; the packages' are 1, 2,
    /// ... in the order they began, as [`Recorder::take`] lists them), the resources it wrote and the ones it created.
    pub(crate) fn running(&self) -> (u32, Vec<ResourceId>, Vec<ResourceId>) {
        let state = self.state.borrow();
        let current = state.current;
        let created = state.creators.iter().filter(|(_, (by, _))| *by == current).map(|(id, _)| *id).collect();
        (current as u32, state.buckets[current].written.iter().copied().collect(), created)
    }

    /// The running entrypoint returned (or failed): what is noted from now on is unattributed.
    pub fn end(&self) {
        self.state.borrow_mut().current = 0;
        self.epoch.set(self.epoch.get() + 1);
    }

    /// The running entrypoint (or nobody) wrote into `id`'s value or replaced it.
    pub fn wrote(&self, id: ResourceId) {
        let mut state = self.state.borrow_mut();
        let current = state.current;
        if state.closed
            || state.creators.get(&id).is_some_and(|(creator, _)| *creator == current)
            || state.buckets[current].written.contains(&id)
        {
            return;
        }
        if state.room() {
            state.buckets[current].written.insert(id);
        }
    }

    /// The running entrypoint (or nobody) created `id`; `explicit`: with its own GUID.
    pub fn created(&self, id: ResourceId, explicit: bool) {
        let mut state = self.state.borrow_mut();
        if state.closed {
            return;
        }
        let current = state.current;
        let bucket = &mut state.buckets[current];
        bucket.creations = bucket.creations.saturating_add(1);
        let ordinal = bucket.creations;
        state.creators.insert(id, (current, ordinal));
        if state.room() {
            state.buckets[current].created.push(Created { ordinal, id, explicit });
        }
    }

    /// The running entrypoint (or nobody) created the content `hash` (or created it again).
    pub fn content(&self, hash: ContentHash) {
        let mut state = self.state.borrow_mut();
        let current = state.current;
        if state.closed || state.buckets[current].seen.contains(&hash) {
            return;
        }
        if state.room() {
            let bucket = &mut state.buckets[current];
            bucket.seen.insert(hash);
            bucket.contents.push(hash);
        }
    }

    /// The journal so far; the recorder notes nothing more (writes after the pass, from a finalizer, are not the
    /// generation's: the stage was taken back before its resources were serialized).
    pub fn take(&self) -> Journal {
        let mut state = self.state.borrow_mut();
        state.closed = true;
        let names: Vec<Option<String>> = state.buckets.iter().map(|b| b.id.clone()).collect();
        let creators = std::mem::take(&mut state.creators);
        let mut buckets = std::mem::take(&mut state.buckets).into_iter().map(|b| Changes {
            changed: b
                .written
                .iter()
                .map(|id| Changed {
                    id: *id,
                    created_by: creators
                        .get(id)
                        .and_then(|(creator, ordinal)| Some((names[*creator].clone()?, *ordinal))),
                    paths: None,
                })
                .collect(),
            id: b.id,
            created: b.created,
            contents: b.contents,
            truncated: b.truncated,
        });
        let unattributed = buckets.next().unwrap_or_default();
        Journal {
            packages: buckets.collect(),
            unattributed,
            truncated: state.truncated,
            member_paths: false,
        }
    }
}

/// One resource's owner for its typed values (`ValueObjects::owned_by`).
pub(crate) struct ResourceOwner {
    id: ResourceId,
    recorder: Rc<Recorder>,
    noted: Cell<u64>,
}

impl ResourceOwner {
    pub(crate) fn new(id: ResourceId, recorder: Rc<Recorder>) -> Self {
        Self {
            id,
            recorder,
            noted: Cell::new(u64::MAX),
        }
    }
}

impl WriteOwner for ResourceOwner {
    fn wrote(&self) {
        let epoch = self.recorder.epoch.get();
        if self.noted.get() != epoch {
            self.noted.set(epoch);
            self.recorder.wrote(self.id);
        }
    }
}

/// What the record says of a package besides its changes: its plan identity.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Identity {
    pub version: Option<String>,
    /// `shroudforge` or `eml` (the plan's kind).
    pub kind: &'static str,
    pub content_sha256: [u8; 32],
    pub settings_sha256: [u8; 32],
}

/// The record's header: the generation it belongs to.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Header<'a> {
    pub stem: &'a str,
    /// kiln-eml's generation digest (`startup::generation_digest`): the outcome's `archiveSha256`.
    pub generation: [u8; 32],
    pub receipt_sha256: [u8; 32],
    pub dialect: &'static str,
    pub attempt: &'static str,
}

/// Format 2's optional tail: who wrote the record (`writer`, left out: kiln-eml) and the packages not applied (left out:
/// none were given, as 0.2.0-dev.31.18's EML record).
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct Tail<'a> {
    pub writer: Option<&'static str>,
    pub not_applied: Option<&'a [NotApplied]>,
}

/// One line of reason as the record keeps it: control characters as spaces, at most 512 bytes (at a character boundary).
pub fn one_line(text: &str) -> String {
    let mut s: String = text.chars().map(|c| if c.is_control() { ' ' } else { c }).collect();
    let s2 = s.trim().to_owned();
    s = if s2.is_empty() { "failed".to_owned() } else { s2 };
    if s.len() > 512 {
        let mut end = 512;
        while !s.is_char_boundary(end) {
            end -= 1;
        }
        s.truncate(end);
    }
    s
}

fn hex(bytes: &[u8]) -> String {
    bytes.iter().map(|b| format!("{b:02x}")).collect()
}
fn text(s: &str) -> String {
    serde_json::to_string(s).unwrap_or_else(|_| "\"\"".into())
}
fn key(id: &ResourceId) -> String {
    format!(
        "\"guid\":\"{}\",\"typeHash\":\"{:08x}\",\"part\":{}",
        id.guid(),
        id.type_hash(),
        id.part_index()
    )
}
fn changes(c: &Changes) -> String {
    let changed: Vec<String> = c
        .changed
        .iter()
        .map(|e| {
            let by = match &e.created_by {
                Some((package, ordinal)) => format!("{{\"package\":{},\"ordinal\":{ordinal}}}", text(package)),
                None => "null".into(),
            };
            match &e.paths {
                // format 2 (the EML dialect)
                Some(paths) => {
                    let paths: Vec<String> = paths.iter().map(|p| text(p)).collect();
                    format!("{{{},\"createdBy\":{by},\"paths\":[{}]}}", key(&e.id), paths.join(","))
                }
                None => format!("{{{},\"createdBy\":{by}}}", key(&e.id)),
            }
        })
        .collect();
    let created: Vec<String> = c
        .created
        .iter()
        .map(|e| {
            format!(
                "{{\"ordinal\":{},{},\"explicit\":{}}}",
                e.ordinal,
                key(&e.id),
                e.explicit
            )
        })
        .collect();
    let contents: Vec<String> = c
        .contents
        .iter()
        .map(|h| format!("{{\"guid\":\"{h}\",\"size\":{}}}", h.size()))
        .collect();
    format!(
        "\"truncated\":{},\"changed\":[{}],\"created\":[{}],\"contents\":[{}]",
        c.truncated,
        changed.join(","),
        created.join(","),
        contents.join(",")
    )
}

/// A ShroudForge identifier, as the record names a package (sfi-state's sfId): 1 to 80 of A-Z a-z 0-9 . _ -
pub fn is_sf_id(id: &str) -> bool {
    (1..=80).contains(&id.len()) && id.bytes().all(|b| b.is_ascii_alphanumeric() || matches!(b, b'.' | b'_' | b'-'))
}

/// Format 2: a member path cut back to the member that holds it when longer than [`PATH_BYTES`] (at a `.` or `[`).
fn bounded_path(path: &str) -> String {
    if path.len() <= PATH_BYTES {
        return path.to_owned();
    }
    let mut end = PATH_BYTES + 1;
    while !path.is_char_boundary(end) {
        end -= 1;
    }
    path[..end].rfind(['.', '[']).map_or_else(String::new, |at| path[..at].to_owned())
}

/// Format 2: a changed resource's paths as the record lists them: bounded, sorted, each once; `[""]` for the whole.
fn bounded_paths(paths: &[String]) -> Vec<String> {
    let set: BTreeSet<String> = paths.iter().map(|p| bounded_path(p)).collect();
    if set.contains("") || set.len() > RESOURCE_PATHS { vec![String::new()] } else { set.into_iter().collect() }
}

impl Journal {
    /// kiln-eml (format 2 of the ShroudForge dialect, 0.2.0-dev.32): each changed resource's member paths from
    /// `of(bucket, resource)` (the recorder's bucket: 0 the unattributed one, the packages 1, 2, ... in run order).
    /// `Some(empty)`: the package changed nothing there (it wrote a value back as it was), so the row goes; `None`: not
    /// compared (a write outside every entrypoint), the whole resource. Bounded as [`Journal::from_eml`] bounds the EML
    /// record's paths (per path, per resource, the journal's path budget).
    pub fn with_member_paths(mut self, of: impl Fn(u32, &ResourceId) -> Option<Vec<String>>) -> Journal {
        let mut path_bytes = 0usize;
        let mut fill = |c: &mut Changes, bucket: u32| {
            c.changed.retain_mut(|e| {
                let mut paths = match of(bucket, &e.id) {
                    Some(p) if p.is_empty() => return false,
                    Some(p) => bounded_paths(&p),
                    None => vec![String::new()],
                };
                let cost: usize = paths.iter().map(|p| p.len() + 3).sum();
                if path_bytes + cost > JOURNAL_PATH_BYTES {
                    paths = vec![String::new()]; // past the journal's path budget: the whole resource
                }
                path_bytes += paths.iter().map(|p| p.len() + 3).sum::<usize>();
                e.paths = Some(paths);
                true
            });
        };
        fill(&mut self.unattributed, 0);
        for (i, p) in self.packages.iter_mut().enumerate() {
            fill(p, i as u32 + 1);
        }
        self.member_paths = true;
        self
    }

    /// The EML dialect's journal (format 2) from the record of EML's pass (mod-loader-lua `WriteRecord`), bounded as the
    /// ShroudForge dialect's: at most [`PACKAGE_ENTRIES`] entries per package and in the unattributed bucket (created
    /// first, then contents, then changed), [`TOTAL_ENTRIES`] in all, what is over left out and marked `truncated`. A mod
    /// whose id is no ShroudForge identifier cannot be named: its writes go to the unattributed bucket (its creations
    /// numbered after that bucket's own, writes into them named as no package's).
    pub fn from_eml(record: &mod_loader::lua::WriteRecord) -> Journal {
        let mut unattributed = record.unattributed.clone();
        let mut packages = Vec::new();
        for p in &record.packages {
            if p.id.as_deref().is_some_and(is_sf_id) {
                packages.push(p.clone());
                continue;
            }
            let base = unattributed.created.len() as u32;
            unattributed.created.extend(p.created.iter().map(|c| mod_loader::lua::CreatedResource { ordinal: base + c.ordinal, ..c.clone() }));
            for hash in &p.contents {
                if !unattributed.contents.contains(hash) {
                    unattributed.contents.push(*hash);
                }
            }
            for c in &p.changed {
                match unattributed.changed.iter_mut().find(|u| u.id == c.id) {
                    Some(u) => u.paths.extend(c.paths.iter().cloned()),
                    None => unattributed.changed.push(c.clone()),
                }
            }
        }
        let unnamed: Vec<&str> = record.packages.iter().filter_map(|p| p.id.as_deref()).filter(|id| !is_sf_id(id)).collect();
        let mut entries = 0usize;
        let mut truncated = false;
        let mut path_bytes = 0usize;
        let mut bound = |w: &mod_loader::lua::PackageWrites| -> Changes {
            let mut c = Changes { id: w.id.clone(), ..Changes::default() };
            let mut room = |c: &mut Changes| {
                if c.changed.len() + c.created.len() + c.contents.len() < PACKAGE_ENTRIES && entries < TOTAL_ENTRIES {
                    entries += 1;
                    true
                } else {
                    c.truncated = true;
                    truncated = true;
                    false
                }
            };
            for x in &w.created {
                if !room(&mut c) {
                    break;
                }
                c.created.push(Created { ordinal: x.ordinal, id: x.id, explicit: x.explicit });
            }
            for hash in &w.contents {
                if !room(&mut c) {
                    break;
                }
                c.contents.push(*hash);
            }
            let mut changed: Vec<_> = w.changed.iter().collect();
            changed.sort_by_key(|x| x.id); // resource-key order, as the record lists them
            for x in changed {
                if !room(&mut c) {
                    break;
                }
                let created_by = x.created_by.clone().filter(|(by, _)| !unnamed.contains(&by.as_str()));
                let mut paths = bounded_paths(&x.paths);
                let cost: usize = paths.iter().map(|p| p.len() + 3).sum();
                if path_bytes + cost > JOURNAL_PATH_BYTES {
                    paths = vec![String::new()]; // past the journal's path budget: the whole resource
                }
                path_bytes += paths.iter().map(|p| p.len() + 3).sum::<usize>();
                c.changed.push(Changed { id: x.id, created_by, paths: Some(paths) });
            }
            c
        };
        let unattributed = bound(&unattributed);
        let packages = packages.iter().map(&mut bound).collect();
        Journal { packages, unattributed, truncated, member_paths: true }
    }

    /// The record, compact, every field in the schema's order (a typed serializer): format 1
    /// (startup-journal.schema.json) for the ShroudForge dialect, format 2 (eml-journal-2.schema.json) for the EML
    /// dialect (`paths` on each changed resource; a plan digest that no launch plan froze, all zero, is null).
    /// `identity`: each package's plan identity (a package the plan does not name has none, which the startup controller never
    /// passes: every package of the pass is a plan member).
    pub fn json(&self, header: &Header<'_>, identity: &dyn Fn(&str) -> Option<Identity>) -> String {
        self.json_with(header, identity, &Tail::default())
    }

    /// [`Journal::json`] with format 2's optional tail (0.2.0-dev.32).
    pub fn json_with(&self, header: &Header<'_>, identity: &dyn Fn(&str) -> Option<Identity>, tail: &Tail<'_>) -> String {
        let eml = header.dialect == "eml";
        let format2 = eml || self.member_paths;
        let digest = |d: &[u8; 32]| if format2 && *d == [0; 32] { "null".to_owned() } else { format!("\"{}\"", hex(d)) };
        let packages: Vec<String> = self
            .packages
            .iter()
            .map(|c| {
                let id = c.id.as_deref().unwrap_or_default();
                let identity = identity(id).unwrap_or(Identity {
                    version: None,
                    kind: if eml { "eml" } else { "shroudforge" },
                    content_sha256: [0; 32],
                    settings_sha256: [0; 32],
                });
                format!(
                    "{{\"id\":{},\"version\":{},\"kind\":\"{}\",\"contentSha256\":{},\"settingsSha256\":{},{}}}",
                    text(id),
                    identity.version.as_deref().map_or_else(|| "null".into(), text),
                    identity.kind,
                    digest(&identity.content_sha256),
                    digest(&identity.settings_sha256),
                    changes(c)
                )
            })
            .collect();
        let mut extra = String::new();
        if format2 {
            if let Some(writer) = tail.writer {
                extra.push_str(&format!(",\"writer\":\"{writer}\""));
            }
            if let Some(rows) = tail.not_applied {
                let rows: Vec<String> = rows
                    .iter()
                    .filter(|n| is_sf_id(&n.id))
                    .map(|n| {
                        format!(
                            "{{\"id\":{},\"version\":{},\"kind\":\"{}\",\"contentSha256\":{},\"reason\":{}}}",
                            text(&n.id),
                            n.version.as_deref().map_or_else(|| "null".into(), text),
                            if eml { "eml" } else { n.kind },
                            digest(&n.content_sha256),
                            text(&one_line(&n.reason))
                        )
                    })
                    .collect();
                extra.push_str(&format!(",\"notApplied\":[{}]", rows.join(",")));
            }
        }
        format!(
            "{{\"format\":{},\"stem\":{},\"generation\":\"{}\",\"receiptSha256\":\"{}\",\"dialect\":\"{}\",\
             \"attempt\":\"{}\",\"truncated\":{},\"packages\":[{}],\"unattributed\":{{{}}}{extra}}}",
            if format2 { 2 } else { 1 },
            text(header.stem),
            hex(&header.generation),
            hex(&header.receipt_sha256),
            header.dialect,
            header.attempt,
            self.truncated,
            packages.join(","),
            changes(&self.unattributed)
        )
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use kfc::guid::Guid;

    fn id(n: u32) -> ResourceId {
        let mut bytes = [0u8; 16];
        bytes[..4].copy_from_slice(&n.to_le_bytes());
        ResourceId::new(Guid::from(bytes), 0x1234_5678, n % 3)
    }

    // Attribution by the running entrypoint: a write before any entrypoint is unattributed; two packages writing one
    // resource are both named; a package's writes into its own creation are not listed, another's name its creator
    // and ordinal; repeated writes and contents count once; ordinals count creations, explicit ones too; nothing is
    // noted after take.
    #[test]
    fn writes_are_named_for_the_running_entrypoint() {
        let r = Rc::new(Recorder::default());
        let owner = |n| ResourceOwner::new(id(n), r.clone());
        let early = owner(9);
        early.wrote();
        r.begin("a");
        let zero = owner(0);
        zero.wrote();
        zero.wrote();
        r.created(id(100), false);
        r.created(id(101), true);
        ResourceOwner::new(id(100), r.clone()).wrote();
        r.content(ContentHash::from_data(b"one"));
        r.content(ContentHash::from_data(b"one"));
        r.end();
        r.begin("b");
        zero.wrote();
        ResourceOwner::new(id(101), r.clone()).wrote();
        r.end();
        r.begin("c");
        r.end();
        let journal = r.take();
        zero.wrote();
        r.created(id(7), false);
        let names: Vec<_> = journal.packages.iter().map(|p| p.id.clone().unwrap()).collect();
        assert_eq!(names, ["a", "b", "c"]);
        let a = &journal.packages[0];
        assert_eq!(
            a.changed,
            [Changed {
                id: id(0),
                created_by: None,
                paths: None
            }]
        );
        assert_eq!(
            a.created,
            [
                Created {
                    ordinal: 1,
                    id: id(100),
                    explicit: false
                },
                Created {
                    ordinal: 2,
                    id: id(101),
                    explicit: true
                }
            ]
        );
        assert_eq!(a.contents, [ContentHash::from_data(b"one")]);
        let b = &journal.packages[1];
        assert_eq!(
            b.changed,
            [
                Changed {
                    id: id(0),
                    created_by: None,
                    paths: None
                },
                Changed {
                    id: id(101),
                    created_by: Some(("a".into(), 2)),
                    paths: None
                }
            ]
        );
        assert_eq!(
            journal.packages[2],
            Changes {
                id: Some("c".into()),
                ..Changes::default()
            }
        );
        assert_eq!(
            journal.unattributed.changed,
            [Changed {
                id: id(9),
                created_by: None,
                paths: None
            }]
        );
        assert!(!journal.truncated);
        assert_eq!(r.take().packages.len(), 0, "taken once");
    }

    // The EML dialect (format 2): a resource's paths are bounded (too many: the whole resource; too long: cut back to the
    // member holding it), a mod the record cannot name is unattributed (and a creation of its is nobody's), a bucket is
    // bounded as the ShroudForge dialect's; the record is format 2 with paths and null plan digests.
    #[test]
    fn the_eml_record_is_bounded_and_written_as_format_2() {
        use mod_loader::lua::{CreatedResource, PackageWrites, WriteRecord, WrittenResource};
        let long = format!("a.{}.b", "x".repeat(PATH_BYTES));
        let written = |n: u32, paths: Vec<String>| WrittenResource { id: id(n), created_by: None, paths };
        let many: Vec<String> = (0..=RESOURCE_PATHS).map(|i| format!("f{i:03}")).collect();
        let record = WriteRecord {
            packages: vec![
                PackageWrites {
                    id: Some("ok".into()),
                    changed: vec![
                        written(2, vec![long.clone()]),
                        written(1, many),
                        WrittenResource { id: id(5), created_by: Some(("not ok".into(), 1)), paths: vec!["x".into()] },
                    ],
                    ..PackageWrites::default()
                },
                PackageWrites {
                    id: Some("not ok".into()),
                    changed: vec![written(3, vec!["y".into()])],
                    created: vec![CreatedResource { ordinal: 1, id: id(5), explicit: false }],
                    ..PackageWrites::default()
                },
            ],
            unattributed: PackageWrites::default(),
        };
        let j = Journal::from_eml(&record);
        assert_eq!(j.packages.len(), 1, "only a nameable id is a package");
        let ok = &j.packages[0];
        assert_eq!(ok.changed.iter().map(|c| c.id).collect::<Vec<_>>(), [id(1), id(2), id(5)], "resource-key order");
        assert_eq!(ok.changed[0].paths.as_deref(), Some(&[String::new()][..]), "over the path bound: the whole resource");
        assert_eq!(ok.changed[1].paths.as_deref(), Some(&["a".to_string()][..]), "a long path: its holding member");
        assert_eq!(ok.changed[2].created_by, None, "a creation of an unnamed mod is nobody's");
        assert_eq!(j.unattributed.changed[0].id, id(3));
        assert_eq!(j.unattributed.created.len(), 1);
        let header = Header { stem: "enshrouded", generation: [1; 32], receipt_sha256: [1; 32], dialect: "eml", attempt: "first" };
        let text = j.json(&header, &|_| None);
        assert!(text.starts_with("{\"format\":2,") && text.contains("\"kind\":\"eml\",\"contentSha256\":null"), "{text}");
        assert!(text.contains("\"paths\":[\"\"]"), "{text}");
        // the bounds
        let big = WriteRecord {
            packages: vec![PackageWrites {
                id: Some("big".into()),
                changed: (0..PACKAGE_ENTRIES as u32 + 3).map(|n| written(n, vec!["x".into()])).collect(),
                ..PackageWrites::default()
            }],
            unattributed: PackageWrites::default(),
        };
        let b = Journal::from_eml(&big);
        assert!(b.truncated && b.packages[0].truncated && b.packages[0].changed.len() == PACKAGE_ENTRIES);
    }

    // Review (the path bounds): a mod that writes one field of a hundred recipes, elements of one shared resource, keeps
    // a path per recipe (64 made it the whole resource: named on every field of every recipe); and a record whose paths
    // would make the file larger than a reader takes (16 MiB) stays below it, the resources past its path budget listed
    // whole.
    #[test]
    fn the_eml_record_keeps_a_path_per_element_and_stays_readable() {
        use mod_loader::lua::{PackageWrites, WriteRecord, WrittenResource};
        let recipes: Vec<String> = (0..100).map(|i| format!("recipes[{i}].craftingDuration")).collect();
        let one = WriteRecord {
            packages: vec![PackageWrites {
                id: Some("faster".into()),
                changed: vec![WrittenResource { id: id(1), created_by: None, paths: recipes.clone() }],
                ..PackageWrites::default()
            }],
            unattributed: PackageWrites::default(),
        };
        let mut sorted = recipes.clone();
        sorted.sort();
        assert_eq!(Journal::from_eml(&one).packages[0].changed[0].paths.as_deref(), Some(&sorted[..]), "a path per recipe");
        let long = |r: u32, i: u32| format!("m{r:05}.{}.f{i:02}", "x".repeat(230));
        let big = WriteRecord {
            packages: (0..2u32)
                .map(|p| PackageWrites {
                    id: Some(format!("big{p}")),
                    changed: (0..1800u32)
                        .map(|r| WrittenResource { id: id(p * 10_000 + r), created_by: None, paths: (0..20).map(|i| long(r, i)).collect() })
                        .collect(),
                    ..PackageWrites::default()
                })
                .collect(),
            unattributed: PackageWrites::default(),
        };
        let j = Journal::from_eml(&big);
        let header = Header { stem: "enshrouded", generation: [1; 32], receipt_sha256: [1; 32], dialect: "eml", attempt: "first" };
        let text = j.json(&header, &|_| None);
        assert!(text.len() < 16 << 20, "the record is {} bytes: more than a reader takes", text.len());
        let first = &j.packages[0].changed[0];
        assert_eq!(first.paths.as_ref().map(Vec::len), Some(20), "the first resources keep their paths");
        let last = j.packages[1].changed.last().unwrap();
        assert_eq!(last.paths.as_deref(), Some(&[String::new()][..]), "past the budget: the whole resource");
        assert_eq!(j.packages.iter().map(|p| p.changed.len()).sum::<usize>(), 3600, "every resource is still listed");
    }

    // Over the bounds: a package keeps its first PACKAGE_ENTRIES entries and is truncated, ordinals keep counting, the
    // journal stops at TOTAL_ENTRIES; nothing fails.
    #[test]
    fn over_the_bounds_the_journal_is_truncated_never_failed() {
        let r = Recorder::default();
        r.begin("big");
        for n in 0..(PACKAGE_ENTRIES as u32 + 10) {
            r.created(id(n), false);
        }
        r.wrote(id(1_000_000));
        r.end();
        let mut n = 2_000_000u32;
        for p in 0..(TOTAL_ENTRIES / PACKAGE_ENTRIES + 1) {
            r.begin(&format!("p{p}"));
            for _ in 0..PACKAGE_ENTRIES {
                r.wrote(id(n));
                n += 1;
            }
            r.end();
        }
        let journal = r.take();
        let big = &journal.packages[0];
        assert!(big.truncated && journal.truncated);
        assert_eq!(big.created.len(), PACKAGE_ENTRIES);
        assert_eq!(big.created.last().unwrap().ordinal, PACKAGE_ENTRIES as u32);
        assert!(big.changed.is_empty());
        let total: usize = journal
            .packages
            .iter()
            .map(|p| p.changed.len() + p.created.len() + p.contents.len())
            .sum();
        assert_eq!(total, TOTAL_ENTRIES);
        assert!(journal.packages.last().unwrap().truncated);
        assert!(journal.packages.last().unwrap().changed.is_empty());
    }
}
