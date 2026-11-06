//! World things with stable ids: Ancient Spires, Flame Shrines and Sanctums, Shroud Roots, Ancient Obelisks and bosses
//! (docs/kiln/modding/catalogs.md, "World things"). The ids are made by fixed rules from the game's own data, so a
//! runtime that reads the same data (the server's) derives the same ids without a catalog.
//!
//! Sources (layouts from the exe's reflection, by field name):
//! - `GameKnowledgeQueryResourceDb.queries`: the achievement queries name the shrines, roots and obelisks of each
//!   region; their actions are the knowledge ids.
//! - `GameKnowledgeResource`: world and player knowledge objects (the scope of an id), `skillPoints`.
//! - `ecs::TemplateResource` components: `MapMarker`, `BossHealthBar`, `TriggerSpawner`, `RandomSpawner`,
//!   `KnowledgeTriggerOnKill`.
//! - The world scene: the `SceneEntityChunkResource` GUID with the most parts (the highest part is the global chunk,
//!   the others the regular grid). Entities as in gameart's map markers (`gameart::map::markers`); per entity the spawn
//!   components `SceneKnowledgeTrigger` (what the placement unlocks) and `KnowledgeQuery` (what it waits for).

use super::decode::{Offsets, blob_array_at, blob_string_at, guid_bytes_at, u16_at, u32_at, u8_at};
use super::extract::{Loca, Src, e, opt, put};
use super::{Entry, hex_id};
use crate::gameart::kfc::fnv1a32;
use crate::gameart::map::markers::r1;
use crate::{Error, ErrorCode, Result};
use serde_json::{Map, Value, json};
use std::collections::{HashMap, HashSet};

/// The sections this module makes.
pub(super) const SECTIONS: [&str; 5] = ["spires", "shrines", "shroudRoots", "obelisks", "bosses"];

/// The achievement queries that list a region's things: (query name prefix, section).
const ACHIEVEMENTS: [(&str, &str); 3] = [
    ("achievement_all_flametemplesandshrines_found_", "shrines"),
    ("achievement_all_shroudroots_destroyed_", "shroudRoots"),
    ("achievement_all_obelisks_read_", "obelisks"),
];
/// A beacon belongs to the spire it stands within this many metres of (horizontally).
const BEACON_M: f64 = 64.0;

fn unsupported(what: impl Into<String>) -> Error {
    Error::new(ErrorCode::Unsupported, what.into())
}

fn size(s: &Src, name: &str) -> Result<usize> {
    s.types.get(name).map(|t| t.size as usize).filter(|n| *n > 0).ok_or_else(|| unsupported(format!("{name} is not in this game's reflection")))
}

fn hash(s: &Src, name: &str) -> Result<u32> {
    s.types.get(name).map(|t| t.hash).ok_or_else(|| unsupported(format!("{name} is not in this game's reflection")))
}

fn enum_value(s: &Src, ty: &str, name: &str) -> Result<u8> {
    s.types
        .enum_values(ty)
        .and_then(|v| v.into_iter().find(|(n, _)| n == name))
        .map(|(_, v)| v as u8)
        .ok_or_else(|| unsupported(format!("{ty}::{name} is not in this game's reflection")))
}

// ---- id rules (pure: the unit tests and a runtime that reads the server's data follow them)

/// `achievement_all_<what>_<NN>_<Region>` (case-insensitive) -> (section, NN, region lower-cased). Names starting
/// `DEPRECATED` are not.
pub(super) fn achievement(query: &str) -> Option<(&'static str, u32, String)> {
    let n = query.to_lowercase();
    if n.starts_with("deprecated") {
        return None;
    }
    let (rest, section) = ACHIEVEMENTS.iter().find_map(|(p, s)| n.strip_prefix(p).map(|r| (r, *s)))?;
    let (nn, region) = rest.split_once('_')?;
    if nn.is_empty() || !nn.bytes().all(|c| c.is_ascii_digit()) || region.is_empty() {
        return None;
    }
    Some((section, nn.parse().ok()?, region.to_string()))
}

/// A thing's id: the action name lower-cased, with `<region>_` in front unless it starts with that already.
pub(super) fn thing_id(region: &str, action: &str) -> String {
    let a = action.to_lowercase();
    if a.starts_with(&format!("{region}_")) { a } else { format!("{region}_{a}") }
}

/// Repeats of an id within a section get `_2`, `_3`, ... in the order they come.
#[derive(Default)]
pub(super) struct Unique(HashMap<String, u32>);

impl Unique {
    pub fn take(&mut self, id: String) -> String {
        let n = self.0.entry(id.clone()).or_insert(0);
        *n += 1;
        if *n == 1 { id } else { format!("{id}_{n}") }
    }
}

/// `sanctum` (a Flame Temple) or `shrine`.
pub(super) fn shrine_kind(action: &str) -> &'static str {
    if action.to_lowercase().contains("flametemple") { "sanctum" } else { "shrine" }
}

/// `MapMarker_Tower_<a>_FastTravel<b>` -> `<a><b>` lower-cased (`MapMarker_Tower_Desert_FastTravel` -> `desert`).
pub(super) fn spire_id(template: &str) -> Option<String> {
    let rest = template.strip_prefix("MapMarker_Tower_")?;
    let at = rest.rfind("_FastTravel")?;
    (at > 0).then(|| format!("{}{}", &rest[..at], &rest[at + "_FastTravel".len()..]).to_lowercase())
}

/// Boss ids for placements in scene order, given each one's boss template name: the name lower-cased, and `_<n>`
/// (from 1) on every placement of a boss placed more than once.
pub(super) fn boss_ids(bosses: &[&str]) -> Vec<String> {
    let mut total: HashMap<String, u32> = HashMap::new();
    for b in bosses {
        *total.entry(b.to_lowercase()).or_default() += 1;
    }
    let mut seen: HashMap<String, u32> = HashMap::new();
    bosses
        .iter()
        .map(|b| {
            let id = b.to_lowercase();
            if total[&id] == 1 {
                return id;
            }
            let n = seen.entry(id.clone()).or_default();
            *n += 1;
            format!("{id}_{n}")
        })
        .collect()
}

/// The spawn components of one entity: `indices[start]` is their count, then (when not 0) the index of the first in
/// `spawnData.components` as an unaligned u16.
pub(super) fn spawn_range(indices: &[u8], start: usize) -> Option<std::ops::Range<usize>> {
    let n = *indices.get(start)? as usize;
    if n == 0 {
        return Some(0..0);
    }
    let first = u16::from_le_bytes([*indices.get(start + 1)?, *indices.get(start + 2)?]) as usize;
    Some(first..first + n)
}

// ---- the game's data

/// What `GameKnowledgeResource` says about knowledge ids.
#[derive(Default)]
struct Knowledge {
    /// World objects: id -> hasOptionalPlayerProgression (a player twin of the same id).
    world: HashMap<u32, bool>,
    player: HashSet<u32>,
    skill_points: HashMap<u32, u64>,
}

impl Knowledge {
    /// `character` (a player object only), `world` (a world object), `both` (a world object with a player twin).
    fn scope(&self, id: u32) -> Option<&'static str> {
        match self.world.get(&id) {
            Some(true) => Some("both"),
            Some(false) => Some("world"),
            None => self.player.contains(&id).then_some("character"),
        }
    }
}

fn knowledge(s: &Src) -> Result<Knowledge> {
    const R: &str = "keen::GameKnowledgeResource";
    const W: &str = "keen::WorldKnowledgeObjectResource";
    const P: &str = "keen::PlayerKnowledgeObjectResource";
    const S: &str = "keen::SkillPointUnlock";
    let b = s.one(R)?;
    let o = Offsets::new(&s.types);
    let mut k = Knowledge::default();
    let bad = |f: &str| Error::new(ErrorCode::Parse, format!("{R}.{f}"));
    let (base, n) = blob_array_at(&b, o.need(R, "worldKnowledge.values")?).ok_or_else(|| bad("worldKnowledge"))?;
    let (id, twin, stride) = (o.need(W, "id")?, o.need(W, "hasOptionalPlayerProgression")?, size(s, W)?);
    for i in 0..n {
        let at = base + i * stride;
        k.world.insert(u32_at(&b, at + id).ok_or_else(|| bad("worldKnowledge"))?, u8_at(&b, at + twin).unwrap_or(0) != 0);
    }
    let (base, n) = blob_array_at(&b, o.need(R, "playerKnowledge.values")?).ok_or_else(|| bad("playerKnowledge"))?;
    let (id, stride) = (o.need(P, "id")?, size(s, P)?);
    for i in 0..n {
        k.player.insert(u32_at(&b, base + i * stride + id).ok_or_else(|| bad("playerKnowledge"))?);
    }
    let (base, n) = blob_array_at(&b, o.need(R, "skillPoints")?).ok_or_else(|| bad("skillPoints"))?;
    let (id, amount, stride) = (o.need(S, "knowledgeId")?, o.need(S, "amount")?, size(s, S)?);
    for i in 0..n {
        let at = base + i * stride;
        if let (Some(kid), Some(a)) = (u32_at(&b, at + id), u8_at(&b, at + amount)) {
            k.skill_points.insert(kid, a as u64);
        }
    }
    Ok(k)
}

/// A `KnowledgeQueryData`: what a query action or a placement compares.
#[derive(Debug, Clone, Copy)]
struct QueryData {
    id: u32,
    compare: u32,
    ty: u8,
}

struct QueryLay {
    id: usize,
    compare: usize,
    ty: usize,
}

impl QueryLay {
    fn new(o: &Offsets) -> Result<QueryLay> {
        const D: &str = "keen::KnowledgeQueryData";
        Ok(QueryLay { id: o.need(D, "knowledgeOrQueryId")?, compare: o.need(D, "compareValue")?, ty: o.need(D, "type")? })
    }
    fn read(&self, b: &[u8], at: usize) -> Option<QueryData> {
        Some(QueryData { id: u32_at(b, at + self.id)?, compare: u32_at(b, at + self.compare)?, ty: u8_at(b, at + self.ty)? })
    }
}

struct Action {
    ty: u8,
    name: String,
    query: QueryData,
}

struct Query {
    name: String,
    actions: Vec<Action>,
}

/// Every query of `GameKnowledgeQueryResourceDb`, with its actions, in the data's order.
fn queries(s: &Src) -> Result<Vec<Query>> {
    const DB: &str = "keen::GameKnowledgeQueryResourceDb";
    const Q: &str = "keen::GameKnowledgeQueryResource";
    const A: &str = "keen::KnowledgeQueryActionInfo";
    let b = s.one(DB)?;
    let o = Offsets::new(&s.types);
    let ql = QueryLay::new(&o)?;
    let (q_name, q_actions, q_size) = (o.need(Q, "name")?, o.need(Q, "actions")?, size(s, Q)?);
    let (a_type, a_name, a_query, a_size) = (o.need(A, "type")?, o.need(A, "name")?, o.need(A, "query")?, size(s, A)?);
    let (base, n) = blob_array_at(&b, o.need(DB, "queries")?).ok_or_else(|| Error::new(ErrorCode::Parse, format!("{DB}.queries")))?;
    let mut out = Vec::with_capacity(n);
    for i in 0..n {
        let at = base + i * q_size;
        let mut q = Query { name: blob_string_at(&b, at + q_name), actions: Vec::new() };
        if let Some((ab, an)) = blob_array_at(&b, at + q_actions) {
            for j in 0..an.min(4096) {
                let x = ab + j * a_size;
                let (Some(ty), Some(query)) = (u8_at(&b, x + a_type), ql.read(&b, x + a_query)) else { break };
                q.actions.push(Action { ty, name: blob_string_at(&b, x + a_name), query });
            }
        }
        out.push(q);
    }
    Ok(out)
}

/// What the world things need of a template.
#[derive(Default)]
struct Tmpl {
    name: String,
    /// `MapMarker.markerId` (loca tag id; 0 when none).
    marker: Option<u32>,
    /// `BossHealthBar.displayName` (loca tag id): Some = a boss.
    boss: Option<u32>,
    /// What its `TriggerSpawner` and `RandomSpawner` spawn, in component and entry order.
    targets: Vec<[u8; 16]>,
    /// `KnowledgeTriggerOnKill`: (useSceneKnowledgeTrigger, knowledgeUnlockOperation's id).
    on_kill: Option<(bool, u32)>,
}

/// The loca tag id of a `LocaTagReference` at `o` (FNV-1a of the GUID; 0 when null).
fn tag_at(b: &[u8], o: usize) -> u32 {
    guid_bytes_at(b, o).map_or(0, |g| fnv1a32(&g))
}

fn templates(s: &Src) -> Result<HashMap<[u8; 16], Tmpl>> {
    const T: &str = "keen::ecs::TemplateResource";
    const R: &str = "keen::ecs::RuntimeSpawnerEntry";
    const K: &str = "keen::ecs::KnowledgeTriggerOnKill";
    let o = Offsets::new(&s.types);
    let (name_off, comp_off) = (o.need(T, "name")?, o.need(T, "components")?);
    let var = size(s, "keen::BlobVariant<keen::ecs::Component>")?;
    let (mm, bh, ts, rs, ok) = (
        hash(s, "keen::ecs::MapMarker")?,
        hash(s, "keen::ecs::BossHealthBar")?,
        hash(s, "keen::ecs::TriggerSpawner")?,
        hash(s, "keen::ecs::RandomSpawner")?,
        hash(s, K)?,
    );
    let (mm_id, bh_name, ts_tmpl, rs_entries) = (
        o.need("keen::ecs::MapMarker", "markerId")?,
        o.need("keen::ecs::BossHealthBar", "displayName")?,
        o.need("keen::ecs::TriggerSpawner", "entityTemplate")?,
        o.need("keen::ecs::RandomSpawner", "spawnEntries")?,
    );
    let (re_tmpls, re_size) = (o.need(R, "entityTemplates")?, size(s, R)?);
    let (ok_use, ok_id) = (o.need(K, "useSceneKnowledgeTrigger")?, o.need(K, "knowledgeUnlockOperation.knowledgeId")?);
    let mut out = HashMap::new();
    for (ix, key) in s.of_type(T)? {
        if key.part != 0 {
            continue;
        }
        let b = s.kfc.read(ix)?;
        let n = blob_string_at(&b, name_off);
        let mut t = Tmpl { name: if n.is_empty() { key.guid_text() } else { n }, ..Default::default() };
        if let Some((base, cn)) = blob_array_at(&b, comp_off) {
            for i in 0..cn.min(512) {
                let at = base + var * i;
                let (Some(h), Some(rel)) = (u32_at(&b, at), u32_at(&b, at + 4)) else { break };
                let Ok(body) = usize::try_from(at as i64 + 4 + rel as i32 as i64) else { continue };
                if h == mm {
                    t.marker = Some(tag_at(&b, body + mm_id));
                } else if h == bh {
                    t.boss = Some(tag_at(&b, body + bh_name));
                } else if h == ts {
                    t.targets.extend(guid_bytes_at(&b, body + ts_tmpl));
                } else if h == rs
                    && let Some((eb, en)) = blob_array_at(&b, body + rs_entries)
                {
                    for j in 0..en.min(256) {
                        if let Some((gb, gn)) = blob_array_at(&b, eb + j * re_size + re_tmpls) {
                            t.targets.extend((0..gn.min(256)).filter_map(|k| guid_bytes_at(&b, gb + 16 * k)));
                        }
                    }
                } else if h == ok {
                    t.on_kill = Some((u8_at(&b, body + ok_use).unwrap_or(0) != 0, u32_at(&b, body + ok_id).unwrap_or(0)));
                }
            }
        }
        out.insert(key.guid, t);
    }
    Ok(out)
}

/// One `KnowledgeTriggerOperation` of a placement's `SceneKnowledgeTrigger` (empty slots left out).
#[derive(Debug, Clone, Copy)]
struct Op {
    context: u8,
    id: u32,
    ty: u8,
}

/// An entity of the world scene that matters here.
struct Placement {
    global: bool,
    template: [u8; 16],
    /// World metres, y up.
    pos: [f64; 3],
    ops: Vec<Op>,
    query: Option<QueryData>,
}

/// The world scene's placements: every one in the global chunk, and elsewhere those with knowledge operations or
/// whose template `keep` wants; in part order, then entity order.
fn scene(s: &Src, keep: &dyn Fn(&[u8; 16]) -> bool) -> Result<Vec<Placement>> {
    const C: &str = "keen::SceneEntityChunkResource";
    const E: &str = "keen::SceneEntityChunkSpawnEntry";
    const SKT: &str = "keen::ecs::SceneKnowledgeTrigger";
    const OP: &str = "keen::KnowledgeTriggerOperation";
    let o = Offsets::new(&s.types);
    let (c_tmpl, c_ent) = (o.need(C, "templates")?, o.need(C, "entities")?);
    let (sd_entries, sd_comps, sd_ix) = (o.need(C, "spawnData.entries")?, o.need(C, "spawnData.components")?, o.need(C, "spawnData.indices")?);
    let (e_size, e_tmpl, tr) = (size(s, E)?, o.need(E, "templateIndex")?, o.need(E, "transform")?);
    let mut e_pos = [0usize; 6];
    for (i, f) in ["positionX0", "positionX1", "positionY0", "positionY1", "positionZ0", "positionZ1"].iter().enumerate() {
        e_pos[i] = tr + o.need("keen::SceneEntityTransform", f)?;
    }
    let (se_size, se_start) = (size(s, "keen::SceneEntityChunkSpawnDataEntry")?, o.need("keen::SceneEntityChunkSpawnDataEntry", "spawnIndexStart")?);
    let var = size(s, "keen::BlobVariant<keen::ecs::SpawnComponent>")?;
    let (skt, kq) = (hash(s, SKT)?, hash(s, "keen::ecs::KnowledgeQuery")?);
    let (ops_off, ops_ty) = s.types.path(SKT, "knowledgeUnlockOperations").ok_or_else(|| unsupported(format!("{SKT}.knowledgeUnlockOperations")))?;
    let ops_n = ops_ty.map_or(0, |t| s.types.resolve(t).count as usize);
    let (op_size, op_ctx, op_id, op_ty) =
        (size(s, OP)?, o.need(OP, "context")?, o.need(OP, "unlockOperation.knowledgeId")?, o.need(OP, "unlockOperation.type")?);
    let kq_query = o.need("keen::ecs::KnowledgeQuery", "query")?;
    let ql = QueryLay::new(&o)?;

    // the world: the chunk GUID with the most parts; its highest part is the global chunk
    let mut by_guid: HashMap<[u8; 16], Vec<(u32, usize)>> = HashMap::new();
    for (ix, key) in s.of_type(C)? {
        by_guid.entry(key.guid).or_default().push((key.part, ix));
    }
    let mut chunks = by_guid.into_iter().max_by_key(|(g, v)| (v.len(), *g)).map(|(_, v)| v).ok_or_else(|| Error::not_found("no SceneEntityChunkResource in the data"))?;
    chunks.sort();
    let global = chunks.last().map_or(0, |c| c.0);

    let mut out = Vec::new();
    for (part, ix) in chunks {
        let b = s.kfc.read(ix)?;
        let bad = || Error::new(ErrorCode::Parse, format!("{C} part {part}"));
        let (tb, tn) = blob_array_at(&b, c_tmpl).ok_or_else(bad)?;
        let tg: Vec<[u8; 16]> = (0..tn).map(|i| b.get(tb + 16 * i..tb + 16 * i + 16).and_then(|x| x.try_into().ok()).unwrap_or([0; 16])).collect();
        let (eb, en) = blob_array_at(&b, c_ent).ok_or_else(bad)?;
        let (nb, nn) = blob_array_at(&b, sd_entries).ok_or_else(bad)?;
        let (cb, cn) = blob_array_at(&b, sd_comps).ok_or_else(bad)?;
        let (ib, inn) = blob_array_at(&b, sd_ix).ok_or_else(bad)?;
        let ixs = if inn > 0 { b.get(ib..ib + inn).ok_or_else(bad)? } else { &[][..] };
        for i in 0..en {
            let at = eb + e_size * i;
            let Some(t) = u16_at(&b, at + e_tmpl).and_then(|t| tg.get(t as usize)) else { continue };
            let mut ops = Vec::new();
            let mut query = None;
            let range = if i < nn { u16_at(&b, nb + se_size * i + se_start).and_then(|st| spawn_range(ixs, st as usize)) } else { None };
            for c in range.unwrap_or(0..0).filter(|c| *c < cn) {
                let v = cb + var * c;
                let (Some(h), Some(rel)) = (u32_at(&b, v), u32_at(&b, v + 4)) else { continue };
                let Ok(body) = usize::try_from(v as i64 + 4 + rel as i32 as i64) else { continue };
                if h == skt {
                    for k in 0..ops_n {
                        let x = body + ops_off as usize + k * op_size;
                        if let (Some(id), Some(context), Some(ty)) = (u32_at(&b, x + op_id), u8_at(&b, x + op_ctx), u8_at(&b, x + op_ty))
                            && id != 0
                        {
                            ops.push(Op { context, id, ty });
                        }
                    }
                } else if h == kq && query.is_none() {
                    query = ql.read(&b, body + kq_query).filter(|q| q.id != 0);
                }
            }
            if part != global && ops.is_empty() && !keep(t) {
                continue;
            }
            let q = |k: usize| u16_at(&b, at + e_pos[k]).unwrap_or(0) as u32;
            let pos = |a: usize| ((q(a + 1) << 16) | q(a)) as f64 / 65536.0;
            out.push(Placement { global: part == global, template: *t, pos: [pos(0), pos(2), pos(4)], ops, query });
        }
    }
    Ok(out)
}

// ---- the sections

/// The enum values the rules compare.
struct Names {
    knowledge_action: u8,
    simple: [u8; 3],
    bool_op: u8,
    kill: u8,
    invalid: u8,
}

impl Names {
    fn new(s: &Src) -> Result<Names> {
        const DT: &str = "keen::KnowledgeQueryDataType";
        const CTX: &str = "keen::GameKnowledgeTriggerContext";
        Ok(Names {
            knowledge_action: enum_value(s, "keen::KnowledgeQueryActionType", "Knowledge")?,
            simple: [enum_value(s, DT, "SimpleBool")?, enum_value(s, DT, "SimpleCount")?, enum_value(s, DT, "SimpleFlag")?],
            bool_op: enum_value(s, "keen::KnowledgeType", "Bool")?,
            kill: enum_value(s, CTX, "Kill")?,
            invalid: enum_value(s, CTX, "Invalid")?,
        })
    }
}

struct Ctx<'a> {
    s: &'a Src,
    loca: &'a Loca,
    know: Option<Knowledge>,
    names: Names,
    tmpls: HashMap<[u8; 16], Tmpl>,
    places: Vec<Placement>,
}

impl Ctx<'_> {
    fn scope(&self, id: u32) -> Option<&'static str> {
        self.know.as_ref().and_then(|k| k.scope(id))
    }

    fn tmpl(&self, g: &[u8; 16]) -> Option<&Tmpl> {
        self.tmpls.get(g)
    }

    /// `{id, type, compareValue, scope}` of a query (scope for the ids of knowledge objects: the Simple types).
    fn query_json(&self, q: &QueryData) -> Value {
        let mut m = Map::new();
        m.insert("id".into(), hex_id(q.id).into());
        m.insert("type".into(), self.s.enum_name("keen::KnowledgeQueryDataType", q.ty as u64).map_or_else(|| Value::from(q.ty), Value::from));
        m.insert("compareValue".into(), q.compare.into());
        if self.names.simple.contains(&q.ty)
            && let Some(sc) = self.scope(q.id)
        {
            m.insert("scope".into(), sc.into());
        }
        Value::Object(m)
    }

    /// `template`, `displayName` (its map marker's text), `position` of a placement.
    fn place(&self, en: &mut Entry, p: &Placement) {
        if let Some(t) = self.tmpl(&p.template) {
            put(en, "template", t.name.clone());
            put(en, "displayName", opt(t.marker.and_then(|m| self.loca.text(m))));
        }
        put(en, "position", json!([r1(p.pos[0]), r1(p.pos[1]), r1(p.pos[2])]));
    }

    fn template_named(&self, p: &Placement, f: impl Fn(&Tmpl) -> bool) -> bool {
        self.tmpl(&p.template).is_some_and(f)
    }
}

/// Where an achievement entry goes: (section, NN, region, query order, action order).
type Order = (&'static str, u32, String, usize, usize);

/// Shrines, roots and obelisks from the achievement queries.
fn achievements(c: &Ctx, queries: &[Query]) -> HashMap<&'static str, Vec<Entry>> {
    // knowledge id -> the placements that unlock it
    let mut by_op: HashMap<u32, Vec<usize>> = HashMap::new();
    for (i, p) in c.places.iter().enumerate() {
        for op in &p.ops {
            let v = by_op.entry(op.id).or_default();
            if v.last() != Some(&i) {
                v.push(i);
            }
        }
    }
    let mut taken: HashMap<&str, Unique> = HashMap::new();
    let mut rows: Vec<(Order, Entry)> = Vec::new();
    for (qi, q) in queries.iter().enumerate() {
        let Some((section, nn, region)) = achievement(&q.name) else { continue };
        for (ai, a) in q.actions.iter().enumerate() {
            if a.ty != c.names.knowledge_action || !c.names.simple.contains(&a.query.ty) {
                continue;
            }
            let id = taken.entry(section).or_default().take(thing_id(&region, &a.name));
            let k = a.query.id;
            let mut en = e(Some(id), None, None);
            put(&mut en, "knowledge", hex_id(k));
            put(&mut en, "scope", opt(c.scope(k)));
            let cands = by_op.get(&k).map(Vec::as_slice).unwrap_or(&[]);
            let pick = |ok: &dyn Fn(&Placement) -> bool| cands.iter().map(|i| &c.places[*i]).find(|p| ok(p));
            let at = match section {
                "shrines" => {
                    put(&mut en, "kind", shrine_kind(&a.name));
                    // its map marker in the global chunk: a FlameShrine / FlameTemple one first (some sanctums have
                    // markers named otherwise)
                    let marker = |p: &Placement, named: bool| {
                        p.global
                            && c.template_named(p, |t| {
                                let n = t.name.to_lowercase();
                                t.marker.is_some() && (!named || n.contains("flameshrine") || n.contains("flametemple"))
                            })
                    };
                    pick(&|p| marker(p, true)).or_else(|| pick(&|p| marker(p, false)))
                }
                "shroudRoots" => {
                    if a.name.to_lowercase().starts_with("well") {
                        put(&mut en, "elixirWell", true);
                    }
                    put(&mut en, "skillPoints", opt(c.know.as_ref().and_then(|kn| kn.skill_points.get(&k).copied())));
                    pick(&|p| !p.global && c.template_named(p, |t| t.name.starts_with("Loot_Node_Well_Core_Object_SkillPointPlant")))
                }
                _ => pick(&|_| true),
            };
            if let Some(p) = at {
                c.place(&mut en, p);
            }
            rows.push(((section, nn, region.clone(), qi, ai), en));
        }
    }
    rows.sort_by(|a, b| (a.0.1, &a.0.2, a.0.3, a.0.4).cmp(&(b.0.1, &b.0.2, b.0.3, b.0.4)));
    let mut out: HashMap<&'static str, Vec<Entry>> = HashMap::new();
    for ((section, ..), en) in rows {
        out.entry(section).or_default().push(en);
    }
    out
}

fn spires(c: &Ctx, queries: Option<&[Query]>) -> Vec<Entry> {
    // the knowledge a spire's top sets: actions named *topReached* of the Achievement_Tower_* queries
    let top: HashSet<u32> = queries
        .unwrap_or(&[])
        .iter()
        .filter(|q| q.name.to_lowercase().starts_with("achievement_tower_"))
        .flat_map(|q| q.actions.iter())
        .filter(|a| a.name.to_lowercase().contains("topreached"))
        .map(|a| a.query.id)
        .collect();
    let beacons: Vec<&Placement> = c.places.iter().filter(|p| p.global && c.template_named(p, |t| t.name == "Prop_Tower_Beacon")).collect();
    let mut taken = Unique::default();
    let mut out = Vec::new();
    for p in c.places.iter().filter(|p| p.global) {
        let Some(t) = c.tmpl(&p.template) else { continue };
        let Some(id) = spire_id(&t.name) else { continue };
        let mut en = e(Some(taken.take(id)), None, None);
        c.place(&mut en, p);
        if let Some(q) = &p.query {
            put(&mut en, "unlock", c.query_json(q));
        }
        if let Some(op) = p.ops.iter().find(|o| o.ty == c.names.bool_op && top.contains(&o.id)) {
            let mut m = json!({ "id": hex_id(op.id) });
            if let Some(sc) = c.scope(op.id) {
                m["scope"] = sc.into();
            }
            put(&mut en, "topReached", m);
        }
        let near = beacons
            .iter()
            .map(|b| ((b.pos[0] - p.pos[0]).hypot(b.pos[2] - p.pos[2]), *b))
            .filter(|(d, _)| *d <= BEACON_M)
            .min_by(|a, b| a.0.total_cmp(&b.0));
        if let Some(q) = near.and_then(|(_, b)| b.query.as_ref()) {
            put(&mut en, "beacon", c.query_json(q));
        }
        out.push(en);
    }
    out
}

/// Template -> the boss it is or spawns (the first boss target in entry order).
fn boss_of(tmpls: &HashMap<[u8; 16], Tmpl>) -> HashMap<[u8; 16], [u8; 16]> {
    let is_boss = |g: &[u8; 16]| tmpls.get(g).is_some_and(|t| t.boss.is_some());
    tmpls
        .iter()
        .filter_map(|(g, t)| if t.boss.is_some() { Some((*g, *g)) } else { t.targets.iter().find(|x| is_boss(x)).map(|b| (*g, *b)) })
        .collect()
}

fn bosses(c: &Ctx, boss_of: &HashMap<[u8; 16], [u8; 16]>) -> Vec<Entry> {
    // (placement, boss template, the spawner placed when it is not the boss itself)
    let placed: Vec<(&Placement, &Tmpl, Option<&Tmpl>)> = c
        .places
        .iter()
        .filter_map(|p| {
            let b = boss_of.get(&p.template)?;
            let spawner = if *b == p.template { None } else { Some(c.tmpl(&p.template)?) };
            Some((p, c.tmpl(b)?, spawner))
        })
        .collect();
    let ids = boss_ids(&placed.iter().map(|(_, b, _)| b.name.as_str()).collect::<Vec<_>>());
    let n = &c.names;
    let mut out = Vec::new();
    for ((p, boss, spawner), id) in placed.into_iter().zip(ids) {
        let mut en = e(Some(id), None, None);
        put(&mut en, "template", boss.name.clone());
        put(&mut en, "spawner", opt(spawner.map(|t| t.name.clone())));
        put(&mut en, "displayName", opt(boss.boss.and_then(|t| c.loca.text(t))));
        put(&mut en, "position", json!([r1(p.pos[0]), r1(p.pos[1]), r1(p.pos[2])]));
        // the placement's Bool ops with context Kill or Invalid: a world object's first, else a Kill one, else the first;
        // without any, the boss template's own KnowledgeTriggerOnKill
        let cands: Vec<&Op> = p.ops.iter().filter(|o| o.ty == n.bool_op && (o.context == n.kill || o.context == n.invalid)).collect();
        let kill = cands
            .iter()
            .find(|o| matches!(c.scope(o.id), Some("world" | "both")))
            .or_else(|| cands.iter().find(|o| o.context == n.kill))
            .or_else(|| cands.first())
            .map(|o| o.id)
            .or_else(|| boss.on_kill.filter(|(scene, id)| !scene && *id != 0).map(|(_, id)| id));
        if let Some(k) = kill {
            put(&mut en, "kill", hex_id(k));
            put(&mut en, "scope", opt(c.scope(k)));
        }
        out.push(en);
    }
    out
}

/// The world sections `want` asks for, each Ok or the reason it could not be made. `problems` gets what is missing
/// but only leaves fields out (no knowledge objects: no `scope`, no world scene: no positions).
pub(super) fn sections(s: &Src, loca: &Loca, want: &dyn Fn(&str) -> bool, problems: &mut Vec<String>) -> Vec<(&'static str, Result<Vec<Entry>>)> {
    let wanted: Vec<&'static str> = SECTIONS.into_iter().filter(|n| want(n)).collect();
    if wanted.is_empty() {
        return Vec::new();
    }
    let fail = |e: &Error| wanted.iter().map(|n| (*n, Err(Error::new(e.code, e.message.clone())))).collect();
    let names = match Names::new(s) {
        Ok(n) => n,
        Err(e) => return fail(&e),
    };
    let know = knowledge(s).map_err(|e| problems.push(format!("world knowledge: {}", e.message))).ok();
    let queries = queries(s);
    let tmpls = templates(s);
    let places = match &tmpls {
        Ok(t) => {
            let bo = boss_of(t);
            scene(s, &|g| bo.contains_key(g)).map(|p| (p, bo))
        }
        Err(e) => Err(Error::new(e.code, e.message.clone())),
    };
    let (places, boss_map, world_err) = match places {
        Ok((p, b)) => (p, b, None),
        Err(e) => (Vec::new(), HashMap::new(), Some(e)),
    };
    let c = Ctx { s, loca, know, names, tmpls: tmpls.unwrap_or_default(), places };
    let err = |e: &Error| Err(Error::new(e.code, e.message.clone()));
    let mut out = Vec::new();
    let mut achieved = match &queries {
        Ok(q) if wanted.iter().any(|n| ["shrines", "shroudRoots", "obelisks"].contains(n)) => {
            if let Some(e) = &world_err {
                problems.push(format!("world scene: {} (shrines, roots and obelisks have no positions)", e.message));
            }
            achievements(&c, q)
        }
        _ => HashMap::new(),
    };
    for name in wanted {
        let r = match (name, &queries, &world_err) {
            ("shrines" | "shroudRoots" | "obelisks", Err(e), _) => err(e),
            ("shrines" | "shroudRoots" | "obelisks", Ok(_), _) => Ok(achieved.remove(name).unwrap_or_default()),
            ("spires" | "bosses", _, Some(e)) => err(e),
            ("spires", q, None) => Ok(spires(&c, q.as_ref().ok().map(Vec::as_slice))),
            _ => Ok(bosses(&c, &boss_map)),
        };
        out.push((name, r));
    }
    out
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn achievement_names() {
        assert_eq!(achievement("achievement_all_FlameTemplesAndShrines_found_01_Meadow"), Some(("shrines", 1, "meadow".into())));
        assert_eq!(achievement("ACHIEVEMENT_ALL_shroudRoots_destroyed_12_FarHills"), Some(("shroudRoots", 12, "farhills".into())));
        assert_eq!(achievement("achievement_all_Obelisks_read_03_Salt_Flats"), Some(("obelisks", 3, "salt_flats".into())));
        assert_eq!(achievement("DEPRECATED_achievement_all_Obelisks_read_03_SaltFlats"), None);
        assert_eq!(achievement("achievement_all_Obelisks_read_x3_SaltFlats"), None, "NN is digits");
        assert_eq!(achievement("achievement_all_Obelisks_read_03_"), None, "a region");
        assert_eq!(achievement("achievement_all_Obelisks_read_03"), None);
        assert_eq!(achievement("achievement_some_other_03_Meadow"), None);
    }

    #[test]
    fn ids_get_the_region_once() {
        assert_eq!(thing_id("meadow", "Well_04"), "meadow_well_04");
        assert_eq!(thing_id("meadow", "Meadow_FlameShrine_02"), "meadow_flameshrine_02", "already prefixed");
        assert_eq!(thing_id("meadow", "meadowlark_stone"), "meadow_meadowlark_stone", "the prefix ends with _");
        assert_eq!(thing_id("salt_flats", "Salt_Flats_Obelisk"), "salt_flats_obelisk");
    }

    #[test]
    fn repeats_are_numbered_from_two() {
        let mut u = Unique::default();
        assert_eq!(u.take("meadow_stone".into()), "meadow_stone");
        assert_eq!(u.take("meadow_stone".into()), "meadow_stone_2");
        assert_eq!(u.take("meadow_other".into()), "meadow_other");
        assert_eq!(u.take("meadow_stone".into()), "meadow_stone_3");
    }

    #[test]
    fn sanctums_and_shrines() {
        assert_eq!(shrine_kind("Meadow_FlameTemple_01"), "sanctum");
        assert_eq!(shrine_kind("flametemple_hidden"), "sanctum");
        assert_eq!(shrine_kind("Meadow_FlameShrine_01"), "shrine");
        assert_eq!(shrine_kind("hearth_03"), "shrine");
    }

    #[test]
    fn spire_ids() {
        assert_eq!(spire_id("MapMarker_Tower_Meadow_FastTravel").as_deref(), Some("meadow"));
        assert_eq!(spire_id("MapMarker_Tower_FarHills_FastTravel_02_northCliff").as_deref(), Some("farhills_02_northcliff"));
        assert_eq!(spire_id("MapMarker_Tower_A_FastTravel_B_FastTravel_C").as_deref(), Some("a_fasttravel_b_c"), "the last _FastTravel");
        assert_eq!(spire_id("MapMarker_Tower__FastTravel"), None);
        assert_eq!(spire_id("MapMarker_Tower_Meadow"), None);
        assert_eq!(spire_id("Prop_Tower_Meadow_FastTravel"), None);
    }

    #[test]
    fn bosses_placed_twice_are_numbered() {
        let ids = boss_ids(&["Enemy_Boss_Test_Golem", "Enemy_Boss_Test_Wyrm", "Enemy_Boss_Test_Golem", "Enemy_Boss_Test_Golem"]);
        assert_eq!(ids, ["enemy_boss_test_golem_1", "enemy_boss_test_wyrm", "enemy_boss_test_golem_2", "enemy_boss_test_golem_3"]);
        assert!(boss_ids(&[]).is_empty());
    }

    #[test]
    fn spawn_component_ranges() {
        // count 0; count 2 from component 0x0105 (unaligned, at an odd offset)
        assert_eq!(spawn_range(&[0], 0), Some(0..0));
        assert_eq!(spawn_range(&[9, 2, 0x05, 0x01], 1), Some(0x105..0x107));
        assert_eq!(spawn_range(&[1, 7], 0), None, "the u16 is cut off");
        assert_eq!(spawn_range(&[], 0), None);
    }
}

