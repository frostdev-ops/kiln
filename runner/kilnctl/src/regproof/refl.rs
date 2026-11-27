// SPDX-License-Identifier: MIT
//! Reflection member and enum diff (registry proof step 1, BRIEF 6.1): the game's reflection of the old and the new
//! exe, and for every regmap row the members its paths walk through, the leaf types and the enum values it maps. A row
//! whose members and values are all there with the same types carries over (offsets may move: kiln.content resolves
//! members by name at run time); anything else is a change for a human, with a proposal where it looks like a rename or
//! a move. Nothing here is applied: the proof only reports and refuses.
//!
//! The dumps are tools/kfc's reflection (`kfc.py --exe <copy> types --closure <roots> --json-out <file>`: name, kind,
//! size, layout, inner, fields {name, type, offset}, values {name, value}) or a committed sigdb/types dump (schema 2,
//! `qualifiedName`). They hold the game's type names: outputs stay outside the repository.
use super::regmap::{RegFile, RegMap, Row, list_of};
use anyhow::{Context, Result};
use serde::Serialize;
use serde_json::Value;
use std::collections::BTreeMap;

pub const K_ENUM: u32 = 12;
pub const K_TYPEDEF: u32 = 17;
pub const K_STRUCT: u32 = 18;
pub const K_STATIC_ARRAY: u32 = 19;
pub const K_BLOB_ARRAY: u32 = 24;

#[derive(Debug, Clone, Default, PartialEq)]
pub struct Member {
    pub name: String,
    pub ty: String,
    pub offset: u64,
}

#[derive(Debug, Clone, Default, PartialEq)]
pub struct Type {
    pub name: String,
    pub kind: u32,
    pub size: u64,
    pub layout: Option<u64>,
    /// typedef: the aliased type; struct: the base type; array: the element type; bitmask: the enum
    pub inner: Option<String>,
    pub fields: Vec<Member>,
    pub values: Vec<(String, i64)>,
}

#[derive(Debug, Clone, Default)]
pub struct TypeDb {
    pub exe_sha256: String,
    pub types: BTreeMap<String, Type>,
}

/// A number as JSON writes it, or a hex/decimal string ("0x07009d37").
fn num(v: &Value) -> Option<u64> {
    match v {
        Value::Number(n) => n.as_u64(),
        Value::String(s) => match s.trim().strip_prefix("0x").or_else(|| s.trim().strip_prefix("0X")) {
            Some(h) => u64::from_str_radix(h, 16).ok(),
            None => s.trim().parse().ok(),
        },
        _ => None,
    }
}

impl TypeDb {
    pub fn parse(text: &str) -> Result<TypeDb> {
        let v: Value = serde_json::from_str(text).context("not JSON")?;
        let list = v.get("types").and_then(Value::as_array).context("no \"types\" list")?;
        let mut types = BTreeMap::new();
        for t in list {
            let Some(name) = t.get("qualifiedName").or_else(|| t.get("name")).and_then(Value::as_str) else { continue };
            let fields = t.get("fields").and_then(Value::as_array).map(|a| {
                a.iter()
                    .filter_map(|f| {
                        Some(Member {
                            name: f.get("name")?.as_str()?.to_string(),
                            ty: f.get("type").and_then(Value::as_str).unwrap_or_default().to_string(),
                            offset: f.get("offset").and_then(num).unwrap_or(0),
                        })
                    })
                    .collect()
            });
            let values = t.get("values").and_then(Value::as_array).map(|a| {
                a.iter()
                    .filter_map(|x| {
                        let n = x.get("value")?;
                        Some((x.get("name")?.as_str()?.to_string(), n.as_i64().or_else(|| n.as_u64().map(|u| u as i64))?))
                    })
                    .collect()
            });
            types.insert(name.to_string(), Type {
                name: name.to_string(),
                kind: t.get("kind").and_then(num).and_then(|k| u32::try_from(k).ok()).unwrap_or(0),
                size: t.get("size").and_then(num).unwrap_or(0),
                layout: t.get("layout").and_then(num),
                inner: t.get("inner").and_then(Value::as_str).map(str::to_string),
                fields: fields.unwrap_or_default(),
                values: values.unwrap_or_default(),
            });
        }
        let exe_sha256 = v.get("exe_sha256").or_else(|| v.get("exeSha256")).and_then(Value::as_str).unwrap_or_default();
        Ok(TypeDb { exe_sha256: exe_sha256.to_string(), types })
    }

    pub fn get(&self, name: &str) -> Option<&Type> {
        self.types.get(name)
    }

    /// Follows typedefs to the final record.
    pub fn resolve<'a>(&'a self, mut t: &'a Type) -> &'a Type {
        for _ in 0..16 {
            if t.kind != K_TYPEDEF {
                break;
            }
            match t.inner.as_deref().and_then(|n| self.get(n)) {
                Some(x) => t = x,
                None => break,
            }
        }
        t
    }

    /// A struct's members with its bases' (the base's first).
    pub fn fields<'a>(&'a self, t: &'a Type) -> Vec<&'a Member> {
        let mut chain = Vec::new();
        let mut cur = Some(self.resolve(t));
        while let Some(c) = cur {
            if c.kind != K_STRUCT || chain.len() > 32 {
                break;
            }
            chain.push(c);
            cur = c.inner.as_deref().and_then(|n| self.get(n)).map(|x| self.resolve(x));
        }
        chain.iter().rev().flat_map(|c| c.fields.iter()).collect()
    }

    /// An array's element type.
    pub fn element(&self, name: &str) -> Option<&Type> {
        let t = self.resolve(self.get(name)?);
        if t.kind != K_STATIC_ARRAY && t.kind != K_BLOB_ARRAY {
            return None;
        }
        self.get(t.inner.as_deref()?)
    }
}

fn hex(v: Option<u64>) -> String {
    v.map(|x| format!("0x{x:08x}")).unwrap_or_else(|| "?".into())
}

/// How a row fares between the two builds.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub enum Status {
    /// every member, type and value as before
    Same,
    /// as before by name and type; offsets moved or the enum gained values (notes)
    Carried,
    /// does not resolve on the old build but does on the new (a row edited for this build): only the round-trip suite
    /// proves it
    New,
    /// something it uses is gone or different: a human reviews it (the row is refused until then)
    Changed,
}

#[derive(Debug, Clone, PartialEq, Serialize)]
#[serde(tag = "kind", rename_all = "camelCase")]
pub enum Change {
    /// the registry's root type (or its array's element type) is not in the new reflection
    RootMissing { ty: String },
    /// a member on the path is gone in the new reflection
    Unresolved { path: String, owner: String, member: String },
    /// the member at the end of the path has another type
    Retyped { path: String, was: String, now: String },
    /// a game value the row maps a Kiln name to is gone
    EnumMissing { ty: String, value: String },
    /// a game value the row maps has another number
    EnumRenumbered { ty: String, value: String, was: i64, now: i64 },
    /// the members the registry's entries are named and keyed by changed (ids depend on them)
    Entries { what: String },
    /// the row resolves on neither build
    Unresolvable { path: String, why: String },
}

#[derive(Debug, Clone, PartialEq, Serialize)]
#[serde(tag = "kind", rename_all = "camelCase")]
pub enum Note {
    Moved { owner: String, member: String, was: u64, now: u64 },
    EnumAdded { ty: String, values: Vec<String> },
}

/// A path the row could take on the new build: never applied, a hint for the human who edits regmap/common.
#[derive(Debug, Clone, PartialEq, Serialize)]
pub struct Proposal {
    pub was: String,
    pub now: String,
    pub why: String,
}

#[derive(Debug, Clone, PartialEq, Serialize)]
pub struct RowDiff {
    pub status: Status,
    pub changes: Vec<Change>,
    pub notes: Vec<Note>,
    pub proposals: Vec<Proposal>,
}

#[derive(Debug, Clone, Default, Serialize)]
pub struct ReflDiff {
    /// `<registry>.<field>`, and `<registry>.(entries)` for the members entries are named and keyed by
    pub rows: BTreeMap<String, RowDiff>,
    /// root type -> [old layout hash, new layout hash] where they differ
    pub layouts: BTreeMap<String, [String; 2]>,
}

impl ReflDiff {
    #[cfg(test)]
    pub fn status(&self, q: &str) -> Option<Status> {
        self.rows.get(q).map(|r| r.status)
    }
}

impl RowDiff {
    fn of(changes: Vec<Change>, notes: Vec<Note>, proposals: Vec<Proposal>) -> RowDiff {
        let status = if !changes.is_empty() {
            Status::Changed
        } else if !notes.is_empty() {
            Status::Carried
        } else {
            Status::Same
        };
        RowDiff { status, changes, notes, proposals }
    }
    fn new_row() -> RowDiff {
        RowDiff { status: Status::New, changes: Vec::new(), notes: Vec::new(), proposals: Vec::new() }
    }
}

/// One member a path walks through.
#[derive(Debug, Clone)]
struct Step {
    owner: String,
    member: String,
    ty: String,
    offset: u64,
}

/// Where a walk stopped: the step's index, the type that has no such member, the member.
#[derive(Debug, Clone)]
struct Stop {
    at: usize,
    owner: String,
    member: String,
    why: String,
}

/// Why a row's base type is not there.
#[derive(Debug, Clone)]
enum Miss {
    Root(String),
    At { path: String, stop: Stop },
}

impl Miss {
    fn why(&self) -> String {
        match self {
            Miss::Root(t) => format!("{t} is not in the reflection"),
            Miss::At { path, stop } => format!("{path}: {}", stop.why),
        }
    }
}

/// A template's root type: its paths start at one of its components by type name (`Level.dataStorage[0]`).
pub const TEMPLATE: &str = "keen::ecs::TemplateResource";

/// A template component's type by its name without namespaces (`Level` is `keen::ecs::Level`).
pub fn component_type(name: &str) -> String {
    format!("keen::ecs::{name}")
}

/// The members a dotted path walks through from a type. A part `name[i]` is element i of the array member `name` (a step of
/// its own, the element's type at i times its size); from a template root the first part names a component by its type (a
/// step from the template to that component type, offset 0: where a template holds it is the template's, not the build's).
fn walk(db: &TypeDb, base: &str, path: &str) -> std::result::Result<Vec<Step>, Stop> {
    let mut cur = db.get(base).ok_or_else(|| Stop { at: 0, owner: base.into(), member: String::new(), why: format!("{base} is not in the reflection") })?;
    let mut steps = Vec::new();
    let mut parts: Vec<&str> = path.split('.').collect();
    if base == TEMPLATE && !parts.is_empty() && !db.fields(cur).iter().any(|f| f.name == parts[0]) {
        let ty = component_type(parts[0]);
        let comp = db.get(&ty).filter(|t| db.resolve(t).kind == K_STRUCT).ok_or_else(|| Stop {
            at: 0, owner: base.into(), member: parts[0].into(), why: format!("{} is not a component type in the reflection", parts[0]) })?;
        steps.push(Step { owner: base.into(), member: parts[0].into(), ty: ty.clone(), offset: 0 });
        cur = comp;
        parts.remove(0);
    }
    for whole in parts {
        let (part, index) = match whole.strip_suffix(']').and_then(|x| x.split_once('[')) {
            Some((n, i)) => (n, Some(i)),
            None => (whole, None),
        };
        let owner = db.resolve(cur);
        let stop = |why: String| Stop { at: steps.len(), owner: owner.name.clone(), member: part.into(), why };
        if owner.kind != K_STRUCT {
            return Err(stop(format!("{} has no members", owner.name)));
        }
        let Some(m) = db.fields(owner).into_iter().find(|f| f.name == part) else {
            return Err(stop(format!("{} has no member {part}", owner.name)));
        };
        let next = db.get(&m.ty).ok_or_else(|| stop(format!("the type of {part} ({}) is not in the reflection", m.ty)))?;
        steps.push(Step { owner: owner.name.clone(), member: part.into(), ty: m.ty.clone(), offset: m.offset });
        cur = next;
        if let Some(i) = index {
            let at = steps.len();
            let arr = db.resolve(cur);
            let stop = |why: String| Stop { at, owner: arr.name.clone(), member: format!("[{i}]"), why };
            let i: u64 = i.parse().map_err(|_| stop(format!("[{i}] is not an index")))?;
            let el = db.element(&arr.name).ok_or_else(|| stop(format!("{part} is not an array")))?;
            if arr.kind == K_STATIC_ARRAY && el.size > 0 && i >= arr.size / el.size {
                return Err(stop(format!("{part} has {} elements, not {}", arr.size / el.size, i + 1)));
            }
            steps.push(Step { owner: arr.name.clone(), member: format!("[{i}]"), ty: el.name.clone(), offset: i * el.size });
            cur = el;
        }
    }
    Ok(steps)
}

fn leaf(steps: &[Step]) -> String {
    steps.last().map(|s| s.ty.clone()).unwrap_or_default()
}

/// The type a registry's entries are: the root, or its array's element.
fn entry_root(db: &TypeDb, f: &RegFile) -> std::result::Result<String, Miss> {
    let root = &f.entries.resource_type;
    if db.get(root).is_none() {
        return Err(Miss::Root(root.clone()));
    }
    if f.entries.kind != "array" {
        return Ok(root.clone());
    }
    let array = &f.entries.array;
    let steps = walk(db, root, array).map_err(|stop| Miss::At { path: array.clone(), stop })?;
    match db.element(&leaf(&steps)) {
        Some(t) => Ok(t.name.clone()),
        None => Err(Miss::At { path: array.clone(), stop: Stop { at: 0, owner: root.clone(), member: array.clone(), why: "not an array".into() } }),
    }
}

/// The type a field's paths start from: the entry, or (a field of list elements) its list's element.
fn base_of(db: &TypeDb, f: &RegFile, entry: &str, field: &str) -> std::result::Result<String, Miss> {
    let Some(list) = list_of(field) else { return Ok(entry.to_string()) };
    let none = |why: &str| Miss::At { path: list.into(), stop: Stop { at: 0, owner: entry.into(), member: list.into(), why: why.into() } };
    let row = f.rows.get(list).filter(|r| !r.paths.is_empty()).ok_or_else(|| none("its list has no row"))?;
    let path = &row.paths[0];
    let mut t = leaf(&walk(db, entry, path).map_err(|stop| Miss::At { path: path.clone(), stop })?);
    if row.conv == "inventoryWalk" {
        t = leaf(&walk(db, &t, "stacks").map_err(|stop| Miss::At { path: format!("{path}.stacks"), stop })?);
    }
    db.element(&t).map(|e| e.name.clone()).ok_or_else(|| none("not an array"))
}

/// The member's type is another one (kind or size, or another named struct, enum or array).
fn retyped(old: &TypeDb, new: &TypeDb, a: &str, b: &str) -> bool {
    let (Some(ta), Some(tb)) = (old.get(a), new.get(b)) else { return a != b };
    let (ra, rb) = (old.resolve(ta), new.resolve(tb));
    if ra.kind != rb.kind || ra.size != rb.size {
        return true;
    }
    let named = matches!(ra.kind, K_STRUCT | K_ENUM | 13..=16 | K_STATIC_ARRAY | K_BLOB_ARRAY);
    named && ra.name != rb.name
}

/// Every path of at most `depth` members from `base` ending in a member called `name`, with its type.
fn find_paths(db: &TypeDb, base: &str, name: &str, depth: usize, prefix: &str, out: &mut Vec<(String, String)>) {
    if depth == 0 {
        return;
    }
    let Some(t) = db.get(base) else { return };
    let t = db.resolve(t);
    if t.kind != K_STRUCT {
        return;
    }
    for m in db.fields(t) {
        let p = if prefix.is_empty() { m.name.clone() } else { format!("{prefix}.{}", m.name) };
        if m.name == name {
            out.push((p.clone(), m.ty.clone()));
        }
        find_paths(db, &m.ty, name, depth - 1, &p, out);
    }
}

/// A rename (a new member of the same type in the same place) or a move (the only member of that name and type
/// elsewhere) for a path the new build does not resolve.
fn propose(old: &TypeDb, new: &TypeDb, nbase: &str, path: &str, os: &[Step], stop: &Stop) -> Option<Proposal> {
    let was = os.get(stop.at)?;
    let old_names: Vec<String> = old.get(&was.owner).map(|t| old.fields(t).iter().map(|f| f.name.clone()).collect()).unwrap_or_default();
    let owner = new.get(&stop.owner)?;
    let cands: Vec<&Member> = new.fields(owner).into_iter().filter(|f| !old_names.contains(&f.name) && !retyped(old, new, &was.ty, &f.ty)).collect();
    let same_place: Vec<&&Member> = cands.iter().filter(|f| f.offset == was.offset).collect();
    let rename = match (same_place.len(), cands.len()) {
        (1, _) => Some((same_place[0].name.clone(), "renamed: a new member with the same offset and type")),
        (_, 1) => Some((cands[0].name.clone(), "renamed: the only new member of the same type")),
        _ => None,
    };
    let old_leaf = leaf(os);
    let fits = |p: &str| walk(new, nbase, p).is_ok_and(|s| !retyped(old, new, &old_leaf, &leaf(&s)));
    if let Some((name, why)) = rename {
        let mut parts: Vec<String> = path.split('.').map(str::to_string).collect();
        parts[stop.at] = name;
        let now = parts.join(".");
        if fits(&now) {
            return Some(Proposal { was: path.into(), now, why: why.into() });
        }
    }
    let last = path.rsplit('.').next().unwrap_or(path);
    let mut found = Vec::new();
    find_paths(new, nbase, last, 4, "", &mut found);
    found.retain(|(p, ty)| p != path && !retyped(old, new, &old_leaf, ty));
    match found.as_slice() {
        [(p, _)] => Some(Proposal { was: path.into(), now: p.clone(), why: "moved: the only member of this name and type".into() }),
        _ => None,
    }
}

/// The enum (or a bitmask's enum) a row's values map onto.
fn enum_of<'a>(db: &'a TypeDb, conv: &str, leaves: &[String]) -> Option<&'a Type> {
    let at = match conv {
        "enum" | "flags" => 0,
        "optionalEnum" => 1,
        _ => return None,
    };
    let t = db.resolve(db.get(leaves.get(at)?)?);
    if conv == "flags" {
        if !(13..=16).contains(&t.kind) {
            return None;
        }
        return db.get(t.inner.as_deref()?).map(|x| db.resolve(x));
    }
    (t.kind == K_ENUM).then_some(t)
}

fn enum_diff(row: &Row, oe: &Type, ne: &Type, changes: &mut Vec<Change>, notes: &mut Vec<Note>, proposals: &mut Vec<Proposal>) {
    let find = |t: &Type, n: &str| t.values.iter().find(|(v, _)| v == n).map(|(_, x)| *x);
    let added: Vec<String> = {
        let mut a: Vec<String> = ne.values.iter().filter(|(n, _)| find(oe, n).is_none()).map(|(n, _)| n.clone()).collect();
        a.sort();
        a
    };
    for (kiln, game) in &row.values {
        if row.conv == "optionalEnum" && kiln == "none" {
            continue;
        }
        match (find(oe, game), find(ne, game)) {
            (Some(o), None) => {
                changes.push(Change::EnumMissing { ty: ne.name.clone(), value: game.clone() });
                let same: Vec<&String> = added.iter().filter(|n| find(ne, n) == Some(o)).collect();
                if let [n] = same.as_slice() {
                    proposals.push(Proposal { was: game.clone(), now: (*n).clone(), why: "renamed: a new value with the same number".into() });
                }
            }
            (Some(o), Some(n)) if o != n => changes.push(Change::EnumRenumbered { ty: ne.name.clone(), value: game.clone(), was: o, now: n }),
            (None, None) => changes.push(Change::EnumMissing { ty: ne.name.clone(), value: game.clone() }),
            _ => {}
        }
    }
    if !added.is_empty() {
        notes.push(Note::EnumAdded { ty: ne.name.clone(), values: added });
    }
}

fn moved(os: &[Step], ns: &[Step], notes: &mut Vec<Note>) {
    for (a, b) in os.iter().zip(ns) {
        let n = Note::Moved { owner: b.owner.clone(), member: b.member.clone(), was: a.offset, now: b.offset };
        if a.offset != b.offset && !notes.contains(&n) {
            notes.push(n);
        }
    }
}

type Base = std::result::Result<String, Miss>;

fn row_diff(old: &TypeDb, new: &TypeDb, f: &RegFile, field: &str, row: &Row, oroot: &Base, nroot: &Base) -> RowDiff {
    let ob = oroot.clone().and_then(|r| base_of(old, f, &r, field));
    let nb = nroot.clone().and_then(|r| base_of(new, f, &r, field));
    let ob = match ob {
        Ok(b) => b,
        Err(m) => {
            let all_new = nb.as_ref().is_ok_and(|b| row.paths.iter().all(|p| walk(new, b, p).is_ok()));
            return if all_new {
                RowDiff::new_row()
            } else {
                RowDiff::of(vec![Change::Unresolvable { path: row.paths.join(", "), why: m.why() }], vec![], vec![])
            };
        }
    };
    let nb = match nb {
        Ok(b) => b,
        Err(Miss::Root(ty)) => return RowDiff::of(vec![Change::RootMissing { ty }], vec![], vec![]),
        Err(Miss::At { path, stop }) => {
            return RowDiff::of(vec![Change::Unresolved { path, owner: stop.owner, member: stop.member }], vec![], vec![]);
        }
    };
    let (mut changes, mut notes, mut proposals) = (Vec::new(), Vec::new(), Vec::new());
    let mut old_ok = true;
    let (mut oleaves, mut nleaves) = (Vec::new(), Vec::new());
    for p in &row.paths {
        match (walk(old, &ob, p), walk(new, &nb, p)) {
            (Err(_), Ok(ns)) => {
                old_ok = false;
                nleaves.push(leaf(&ns));
            }
            (Err(s), Err(_)) => changes.push(Change::Unresolvable { path: p.clone(), why: s.why }),
            (Ok(os), Err(s)) => {
                if let Some(pr) = propose(old, new, &nb, p, &os, &s) {
                    proposals.push(pr);
                }
                changes.push(Change::Unresolved { path: p.clone(), owner: s.owner, member: s.member });
            }
            (Ok(os), Ok(ns)) => {
                moved(&os, &ns, &mut notes);
                let (a, b) = (leaf(&os), leaf(&ns));
                if retyped(old, new, &a, &b) {
                    changes.push(Change::Retyped { path: p.clone(), was: a.clone(), now: b.clone() });
                }
                oleaves.push(a);
                nleaves.push(b);
            }
        }
    }
    if !old_ok {
        return if changes.is_empty() { RowDiff::new_row() } else { RowDiff::of(changes, notes, proposals) };
    }
    if oleaves.len() == row.paths.len() && nleaves.len() == row.paths.len() {
        if let (Some(oe), Some(ne)) = (enum_of(old, &row.conv, &oleaves), enum_of(new, &row.conv, &nleaves)) {
            enum_diff(row, oe, ne, &mut changes, &mut notes, &mut proposals);
        }
    }
    RowDiff::of(changes, notes, proposals)
}

/// The members a registry's entries are named and keyed by (and a drop table's naming component; a template registry's entry
/// components, `entries.withComponent`).
fn entries_diff(old: &TypeDb, new: &TypeDb, f: &RegFile, oroot: &Base, nroot: &Base) -> RowDiff {
    let (ob, nb) = match (oroot, nroot) {
        (Ok(o), Ok(n)) => (o, n),
        (Err(_), Ok(_)) => return RowDiff::new_row(),
        (Err(m), Err(_)) => return RowDiff::of(vec![Change::Unresolvable { path: "(entries)".into(), why: m.why() }], vec![], vec![]),
        (Ok(_), Err(Miss::Root(ty))) => return RowDiff::of(vec![Change::RootMissing { ty: ty.clone() }], vec![], vec![]),
        (Ok(_), Err(Miss::At { path, stop })) => {
            return RowDiff::of(vec![Change::Unresolved { path: path.clone(), owner: stop.owner.clone(), member: stop.member.clone() }], vec![], vec![]);
        }
    };
    let mut paths: Vec<(String, String, String)> = [&f.entries.name, &f.entries.guid, &f.entries.id]
        .into_iter()
        .filter(|m| !m.is_empty())
        .map(|m| (ob.clone(), nb.clone(), m.clone()))
        .collect();
    if let Some((ty, member)) = f.entries.name_from.rsplit_once('.') {
        paths.push((ty.to_string(), ty.to_string(), member.to_string()));
    }
    // a template registry's entries are the templates holding one of these components: each must still be a component type
    if f.entries.resource_type == TEMPLATE {
        paths.extend(f.entries.with_component.iter().map(|c| (ob.clone(), nb.clone(), c.clone())));
    }
    let (mut changes, mut notes) = (Vec::new(), Vec::new());
    for (ot, nt, p) in &paths {
        match (walk(old, ot, p), walk(new, nt, p)) {
            (Ok(os), Ok(ns)) => {
                moved(&os, &ns, &mut notes);
                let (a, b) = (leaf(&os), leaf(&ns));
                if retyped(old, new, &a, &b) {
                    changes.push(Change::Entries { what: format!("{p}: was {a}, now {b}") });
                }
            }
            (Ok(_), Err(s)) => changes.push(Change::Entries { what: format!("{p}: {}", s.why) }),
            (Err(_), _) => {}
        }
    }
    RowDiff::of(changes, notes, vec![])
}

/// The diff of every row with a mapping, and of each registry's entry members.
pub fn diff(map: &RegMap, old: &TypeDb, new: &TypeDb) -> ReflDiff {
    let mut out = ReflDiff::default();
    for (name, f) in map {
        let (oroot, nroot) = (entry_root(old, f), entry_root(new, f));
        let mut roots = vec![f.entries.resource_type.clone()];
        if let (Ok(o), Ok(n)) = (&oroot, &nroot) {
            if o == n && !roots.contains(o) {
                roots.push(o.clone());
            }
        }
        for r in roots {
            if let (Some(a), Some(b)) = (old.get(&r), new.get(&r)) {
                if a.layout != b.layout {
                    out.layouts.insert(r.clone(), [hex(a.layout), hex(b.layout)]);
                }
            }
        }
        let ent = entries_diff(old, new, f, &oroot, &nroot);
        let ent_changed = ent.changes.iter().any(|c| matches!(c, Change::Entries { .. }));
        for (field, row) in &f.rows {
            if !row.has_mapping() {
                continue;
            }
            let mut d = row_diff(old, new, f, field, row, &oroot, &nroot);
            if ent_changed {
                d.changes.push(Change::Entries { what: format!("the registry's entries changed (see {name}.(entries))") });
                d = RowDiff::of(d.changes, d.notes, d.proposals);
            }
            out.rows.insert(format!("{name}.{field}"), d);
        }
        out.rows.insert(format!("{name}.(entries)"), ent);
    }
    out
}

impl std::fmt::Display for Change {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            Change::RootMissing { ty } => write!(f, "{ty} is not in the new reflection"),
            Change::Unresolved { path, owner, member } => write!(f, "{path}: {owner} has no member {member} on the new build"),
            Change::Retyped { path, was, now } => write!(f, "{path}: was {was}, now {now}"),
            Change::EnumMissing { ty, value } => write!(f, "{ty} has no value {value} on the new build"),
            Change::EnumRenumbered { ty, value, was, now } => write!(f, "{ty}::{value} was {was}, now {now}"),
            Change::Entries { what } => write!(f, "entries: {what}"),
            Change::Unresolvable { path, why } => write!(f, "{path} resolves on neither build ({why})"),
        }
    }
}

#[cfg(test)]
pub(crate) mod tests {
    use super::*;
    use crate::regproof::regmap::tests::sample;
    use serde_json::{Value, json};

    fn t(name: &str, kind: u32, size: u64) -> Value {
        json!({"name": name, "kind": kind, "size": size, "layout": 0, "inner": null})
    }
    fn st(name: &str, size: u64, layout: u64, fields: &[(&str, &str, u64)]) -> Value {
        json!({"name": name, "kind": K_STRUCT, "size": size, "layout": layout, "inner": null,
               "fields": fields.iter().map(|(n, ty, o)| json!({"name": n, "type": ty, "offset": o})).collect::<Vec<_>>()})
    }
    fn en(name: &str, values: &[(&str, i64)]) -> Value {
        json!({"name": name, "kind": K_ENUM, "size": 4, "layout": 0, "inner": "demo::u32",
               "values": values.iter().map(|(n, v)| json!({"name": n, "value": v})).collect::<Vec<_>>()})
    }
    fn arr(name: &str, inner: &str) -> Value {
        json!({"name": name, "kind": K_BLOB_ARRAY, "size": 8, "layout": 0, "inner": inner})
    }

    /// The synthetic game of regmap::tests::sample (invented names), as tools/kfc's reflection writes it.
    pub(crate) fn old_types() -> Vec<Value> {
        vec![
            t("demo::bool", 1, 1), t("demo::u16", 4, 2), t("demo::u32", 6, 4), t("demo::f32", 10, 4), t("demo::Guid", 29, 16),
            t("demo::Text", 30, 8),
            st("demo::Gadget", 48, 0x1111, &[("stackLimit", "demo::u16", 0), ("grade", "demo::Grade", 4), ("hit", "demo::Hit", 8),
                ("range", "demo::Range", 20), ("label", "demo::Text", 28), ("uid", "demo::Guid", 32), ("num", "demo::u32", 44)]),
            st("demo::Hit", 12, 0x2222, &[("isSet", "demo::bool", 0), ("mod", "demo::f32", 4), ("pace", "demo::f32", 8)]),
            st("demo::Range", 8, 0x3333, &[("min", "demo::u32", 0), ("max", "demo::u32", 4)]),
            en("demo::Grade", &[("Low", 0), ("High", 1)]),
            st("demo::PlanBook", 8, 0x4444, &[("plans", "demo::PlanArray", 0)]),
            arr("demo::PlanArray", "demo::Plan"),
            st("demo::Plan", 16, 0x5555, &[("planId", "demo::u32", 0), ("parts", "demo::PartArray", 4), ("limit", "demo::u32", 12)]),
            arr("demo::PartArray", "demo::Part"),
            st("demo::Part", 2, 0x6666, &[("amount", "demo::u16", 0)]),
        ]
    }

    pub(crate) fn db(types: Vec<Value>, sha: &str) -> TypeDb {
        TypeDb::parse(&json!({"exe_sha256": sha, "types": types}).to_string()).unwrap()
    }

    fn replace(types: &mut [Value], v: Value) {
        let n = v["name"].clone();
        let at = types.iter().position(|x| x["name"] == n).unwrap();
        types[at] = v;
    }

    fn status(d: &ReflDiff, q: &str) -> Status {
        d.status(q).unwrap_or_else(|| panic!("{q} not in the diff: {:?}", d.rows.keys().collect::<Vec<_>>()))
    }

    #[test]
    fn parses_both_dump_forms() {
        let a = db(old_types(), "aa");
        assert_eq!(a.exe_sha256, "aa");
        assert_eq!(a.types["demo::Hit"].fields[2], Member { name: "pace".into(), ty: "demo::f32".into(), offset: 8 });
        assert_eq!(a.types["demo::Grade"].values, [("Low".to_string(), 0), ("High".to_string(), 1)]);
        assert_eq!(a.types["demo::Gadget"].layout, Some(0x1111));
        // a committed sigdb/types dump: qualifiedName, hex hashes, fields with their size and kind
        let s = r#"{"side": "server", "types": [
            {"name": "Hit", "qualifiedName": "demo::Hit", "hash": "0x1", "size": 12, "kind": 18,
             "fields": [{"name": "mod", "offset": 4, "type": "demo::f32", "size": 4, "kind": 10}]},
            {"name": "f32", "qualifiedName": "demo::f32", "hash": "0x2", "size": 4, "kind": 10, "layout": "0x0000abcd"}]}"#;
        let b = TypeDb::parse(s).unwrap();
        assert_eq!(b.types["demo::Hit"].fields[0].offset, 4);
        assert_eq!(b.types["demo::f32"].layout, Some(0xabcd));
        assert!(TypeDb::parse("{\"types\": 3}").is_err());
    }

    /// dev.32 creatures: a template's paths start at a component by its type name; an attribute's slot is an index into a
    /// static array. A moved slot is carried, a slot past the array's end and a component the build lacks change the row.
    fn creature_types(slots: u64) -> Vec<Value> {
        vec![
            t("demo::u32", 6, 4), t("demo::f32", 10, 4), t("demo::Guid", 29, 16), t("demo::Text", 30, 8),
            json!({"name": "demo::Slots", "kind": K_STATIC_ARRAY, "size": 4 * slots, "layout": 0, "inner": "demo::u32"}),
            st(TEMPLATE, 24, 0x7777, &[("name", "demo::Text", 0), ("components", "demo::PartArray", 16)]),
            arr("demo::PartArray", "demo::Part"), st("demo::Part", 2, 0x6666, &[]),
            st("keen::ecs::Level", 16, 0x8888, &[("dataStorage", "demo::Slots", 8)]),
            st("keen::ecs::Sight", 4, 0x9999, &[("range", "demo::f32", 0)]),
        ]
    }
    fn creature_map() -> RegMap {
        let creatures = r#"{"regmap": 1, "registry": "beasts",
            "entries": {"kind": "resource", "resourceType": "keen::ecs::TemplateResource", "name": "name", "key": "templates",
                        "withComponent": ["Level"]},
            "rows": {
              "level":    {"paths": ["Level.dataStorage[0]"], "conv": "int", "read": "now", "backend": "live"},
              "levelMax": {"paths": ["Level.dataStorage[1]"], "conv": "int", "read": "now", "backend": "live"},
              "sight":    {"paths": ["Sight.range"], "conv": "float", "read": "now", "backend": "live"}}}"#;
        let mut m = RegMap::new();
        m.insert("beasts".into(), crate::regproof::regmap::parse(creatures, "beasts").unwrap());
        m
    }

    #[test]
    fn template_components_and_attribute_slots_are_walked() {
        let (m, old) = (creature_map(), db(creature_types(2), "a"));
        let d = diff(&m, &old, &db(creature_types(2), "b"));
        for q in ["beasts.level", "beasts.levelMax", "beasts.sight", "beasts.(entries)"] {
            assert_eq!(status(&d, q), Status::Same, "{q}: {:?}", d.rows[q]);
        }
        // the slots array shrinks to one: slot 1 is past its end on the new build; slot 0 stays where it was
        let d = diff(&m, &old, &db(creature_types(1), "b"));
        assert_eq!(status(&d, "beasts.level"), Status::Same, "{:?}", d.rows["beasts.level"]);
        assert_eq!(status(&d, "beasts.levelMax"), Status::Changed, "{:?}", d.rows["beasts.levelMax"]);
        // the component's slots move within it: carried, with the move noted
        let mut moved = creature_types(2);
        replace(&mut moved, st("keen::ecs::Level", 20, 0x8888, &[("dataStorage", "demo::Slots", 12)]));
        let d = diff(&m, &old, &db(moved, "b"));
        assert_eq!(status(&d, "beasts.levelMax"), Status::Carried, "{:?}", d.rows["beasts.levelMax"]);
        // the build has no such component any more
        let mut gone = creature_types(2);
        gone.retain(|x| x["name"] != "keen::ecs::Sight");
        assert_eq!(status(&diff(&m, &old, &db(gone, "b")), "beasts.sight"), Status::Changed);
        // the closure kilnctl dumps names the components the rows start at
        let r = crate::regproof::roots(&m);
        assert!(r.contains(&"keen::ecs::Level".to_string()) && r.contains(&"keen::ecs::Sight".to_string()) && r.contains(&TEMPLATE.to_string()), "{r:?}");
    }

    /// dev.32 creatures: the components that make a template an entry (`entries.withComponent`) define the registry's entries
    /// like a name or GUID member: a build without one of them changes the registry's entries (every row), and the closure
    /// kilnctl dumps names them even when no row starts at them.
    #[test]
    fn entry_components_are_entry_members() {
        let creatures = r#"{"regmap": 1, "registry": "beasts",
            "entries": {"kind": "resource", "resourceType": "keen::ecs::TemplateResource", "name": "name", "key": "templates",
                        "withComponent": ["Level", "Brain"]},
            "rows": {"level": {"paths": ["Level.dataStorage[0]"], "conv": "int", "read": "now", "backend": "live"}}}"#;
        let mut m = RegMap::new();
        m.insert("beasts".into(), crate::regproof::regmap::parse(creatures, "beasts").unwrap());
        let with_brain = || {
            let mut v = creature_types(2);
            v.push(st("keen::ecs::Brain", 8, 0xbbbb, &[("state", "demo::u32", 0)]));
            v
        };
        let old = db(with_brain(), "a");
        assert_eq!(status(&diff(&m, &old, &db(with_brain(), "b")), "beasts.(entries)"), Status::Same);
        let gone = db(creature_types(2), "b");  // no Brain component type on the new build
        let d = diff(&m, &old, &gone);
        assert_eq!(status(&d, "beasts.(entries)"), Status::Changed, "{:?}", d.rows["beasts.(entries)"]);
        assert_eq!(status(&d, "beasts.level"), Status::Changed, "{:?}", d.rows["beasts.level"]);
        let r = crate::regproof::roots(&m);
        assert!(r.contains(&"keen::ecs::Brain".to_string()), "{r:?}");
    }

    #[test]
    fn the_same_reflection_carries_every_mapped_row() {
        let d = diff(&sample(), &db(old_types(), "a"), &db(old_types(), "b"));
        for q in ["gadgets.size", "gadgets.tier", "gadgets.power", "gadgets.label", "gadgets.(entries)", "plans.parts",
                  "plans.parts[].count", "plans.cap", "plans.(entries)"] {
            assert_eq!(status(&d, q), Status::Same, "{q}: {:?}", d.rows[q]);
        }
        assert!(!d.rows.contains_key("gadgets.glow"), "an unmapped row has nothing to diff");
        assert!(d.layouts.is_empty());
    }

    #[test]
    fn a_moved_member_carries_over_with_a_note() {
        let mut n = old_types();
        replace(&mut n, st("demo::Hit", 16, 0x2223, &[("isSet", "demo::bool", 0), ("spare", "demo::u32", 4), ("mod", "demo::f32", 8), ("pace", "demo::f32", 12)]));
        let d = diff(&sample(), &db(old_types(), "a"), &db(n, "b"));
        assert_eq!(status(&d, "gadgets.power"), Status::Carried);
        assert!(d.rows["gadgets.power"].notes.contains(&Note::Moved { owner: "demo::Hit".into(), member: "pace".into(), was: 8, now: 12 }));
        assert_eq!(status(&d, "gadgets.size"), Status::Same);
        assert_eq!(status(&d, "gadgets.label"), Status::Same, "isSet stayed at 0");
    }

    #[test]
    fn a_renamed_member_is_a_change_with_a_proposal() {
        let mut n = old_types();
        replace(&mut n, st("demo::Gadget", 48, 0x1112, &[("maxStack", "demo::u16", 0), ("grade", "demo::Grade", 4), ("hit", "demo::Hit", 8),
            ("range", "demo::Range", 20), ("label", "demo::Text", 28), ("uid", "demo::Guid", 32), ("num", "demo::u32", 44)]));
        let d = diff(&sample(), &db(old_types(), "a"), &db(n, "b"));
        let r = &d.rows["gadgets.size"];
        assert_eq!(r.status, Status::Changed);
        assert_eq!(r.changes, [Change::Unresolved { path: "stackLimit".into(), owner: "demo::Gadget".into(), member: "stackLimit".into() }]);
        assert_eq!(r.proposals, [Proposal { was: "stackLimit".into(), now: "maxStack".into(), why: "renamed: a new member with the same offset and type".into() }]);
        assert_eq!(status(&d, "gadgets.tier"), Status::Same);
        assert_eq!(d.layouts["demo::Gadget"], ["0x00001111".to_string(), "0x00001112".to_string()]);
    }

    #[test]
    fn a_member_moved_elsewhere_is_proposed_by_name_and_type() {
        let mut n = old_types();
        replace(&mut n, st("demo::Hit", 16, 0x2224, &[("isSet", "demo::bool", 0), ("mod", "demo::f32", 4), ("pace", "demo::f32", 8), ("min", "demo::u32", 12)]));
        replace(&mut n, st("demo::Range", 4, 0x3334, &[("max", "demo::u32", 0)]));
        let d = diff(&sample(), &db(old_types(), "a"), &db(n, "b"));
        let r = &d.rows["gadgets.power"];
        assert_eq!(r.status, Status::Changed);
        assert!(r.changes.contains(&Change::Unresolved { path: "range.min".into(), owner: "demo::Range".into(), member: "min".into() }), "{r:?}");
        assert_eq!(r.proposals, [Proposal { was: "range.min".into(), now: "hit.min".into(), why: "moved: the only member of this name and type".into() }]);
    }

    #[test]
    fn enum_values_missing_renumbered_and_added() {
        let mut n = old_types();
        replace(&mut n, en("demo::Grade", &[("Low", 5), ("Top", 1), ("Mid", 2)]));
        let d = diff(&sample(), &db(old_types(), "a"), &db(n, "b"));
        let r = &d.rows["gadgets.tier"];
        assert_eq!(r.status, Status::Changed);
        assert!(r.changes.contains(&Change::EnumMissing { ty: "demo::Grade".into(), value: "High".into() }), "{r:?}");
        assert!(r.changes.contains(&Change::EnumRenumbered { ty: "demo::Grade".into(), value: "Low".into(), was: 0, now: 5 }), "{r:?}");
        assert!(r.proposals.contains(&Proposal { was: "High".into(), now: "Top".into(), why: "renamed: a new value with the same number".into() }), "{r:?}");
        assert!(r.notes.contains(&Note::EnumAdded { ty: "demo::Grade".into(), values: vec!["Mid".into(), "Top".into()] }), "{r:?}");
        // only added values: carried
        let mut m = old_types();
        replace(&mut m, en("demo::Grade", &[("Low", 0), ("High", 1), ("Mid", 2)]));
        let d2 = diff(&sample(), &db(old_types(), "a"), &db(m, "b"));
        assert_eq!(status(&d2, "gadgets.tier"), Status::Carried);
    }

    #[test]
    fn a_retyped_leaf_of_a_list_element_is_a_change() {
        let mut n = old_types();
        replace(&mut n, st("demo::Part", 4, 0x6667, &[("amount", "demo::u32", 0)]));
        let d = diff(&sample(), &db(old_types(), "a"), &db(n, "b"));
        assert_eq!(d.rows["plans.parts[].count"].changes, [Change::Retyped { path: "amount".into(), was: "demo::u16".into(), now: "demo::u32".into() }]);
        assert_eq!(status(&d, "plans.parts"), Status::Same);
        assert_eq!(status(&d, "plans.cap"), Status::Same);
    }

    #[test]
    fn a_missing_root_or_entry_member_changes_every_row_of_the_registry() {
        let mut n = old_types();
        n.retain(|x| x["name"] != "demo::PlanBook");
        let d = diff(&sample(), &db(old_types(), "a"), &db(n, "b"));
        for q in ["plans.parts", "plans.parts[].count", "plans.cap"] {
            assert_eq!(d.rows[q].changes, [Change::RootMissing { ty: "demo::PlanBook".into() }], "{q}");
        }
        assert_eq!(status(&d, "gadgets.size"), Status::Same);
        let mut m = old_types();
        replace(&mut m, st("demo::Gadget", 48, 0x1111, &[("stackLimit", "demo::u16", 0), ("grade", "demo::Grade", 4), ("hit", "demo::Hit", 8),
            ("range", "demo::Range", 20), ("label", "demo::Text", 28), ("key", "demo::Guid", 32), ("num", "demo::u32", 44)]));
        let d2 = diff(&sample(), &db(old_types(), "a"), &db(m, "b"));
        assert_eq!(status(&d2, "gadgets.(entries)"), Status::Changed);
        for q in ["gadgets.size", "gadgets.tier", "gadgets.power", "gadgets.label"] {
            assert!(d2.rows[q].changes.iter().any(|c| matches!(c, Change::Entries { .. })), "{q}: {:?}", d2.rows[q]);
        }
        assert_eq!(status(&d2, "plans.cap"), Status::Same);
    }

    #[test]
    fn a_row_edited_for_the_new_build_is_new_and_one_resolving_nowhere_is_a_change() {
        let mut map = sample();
        map.get_mut("gadgets").unwrap().rows.get_mut("size").unwrap().paths = vec!["maxStack".into()];
        let mut n = old_types();
        replace(&mut n, st("demo::Gadget", 48, 0x1112, &[("maxStack", "demo::u16", 0), ("grade", "demo::Grade", 4), ("hit", "demo::Hit", 8),
            ("range", "demo::Range", 20), ("label", "demo::Text", 28), ("uid", "demo::Guid", 32), ("num", "demo::u32", 44)]));
        let d = diff(&map, &db(old_types(), "a"), &db(n, "b"));
        assert_eq!(status(&d, "gadgets.size"), Status::New);
        map.get_mut("gadgets").unwrap().rows.get_mut("size").unwrap().paths = vec!["nowhere".into()];
        let d2 = diff(&map, &db(old_types(), "a"), &db(old_types(), "b"));
        assert!(matches!(d2.rows["gadgets.size"].changes[..], [Change::Unresolvable { .. }]), "{:?}", d2.rows["gadgets.size"]);
    }
}
