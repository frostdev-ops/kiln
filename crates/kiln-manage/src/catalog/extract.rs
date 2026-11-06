//! Makes a catalog from an installed game, read-only: the `.kfc` archive (gameart's [`Kfc`]) and the exe's reflection
//! (gameart's [`TypeDb`]) for field offsets by name. What each section is made from: docs/kiln/modding/catalogs.md.
//! Names and ids follow kiln.content / kiln.gamedata exactly (native/services/content/gamedata_res.cpp): templates and
//! perks by their name field with `id` = FNV-1a of the GUID, buffs `buff_<id>`, skill nodes `skill_<id>`, sequences
//! per sub-sequence (`<guid>#k` when unnamed or a duplicate, id = FNV-1a(GUID) ^ k * 0x9E3779B1).
//!
//! Fail soft: a section the game changed (a missing type or field) is left out and named in `problems`; the rest of
//! the catalog is written. Exe and data from different builds (Steam updating) refuse the whole build (`conflict`).

use super::decode::{self, Decoder, Offsets, blob_array_at, blob_string_at, guid_at, guid_bytes_at, guid_value, hex_scalar, scalar, u16_at, u32_at, u8_at};
use super::{Catalog, Entry, FieldInfo, GameInfo, FORMAT, KIND, RECIPE, file_for, hex_id};
use crate::gameart::kfc::{ContentHash, Kfc, fnv1a32};
use crate::gameart::map::markers::{clean, loca_table};
use crate::gameart::refl::{PeFile, TypeDb, TypeRec};
use crate::task::Progress;
use crate::{Error, ErrorCode, Result};
use serde::{Deserialize, Serialize};
use serde_json::{Map, Value, json};
use std::collections::{BTreeMap, HashMap, HashSet};
use std::path::{Path, PathBuf};

/// What to make.
#[derive(Debug, Clone, Default)]
pub struct BuildOptions {
    /// Rebuild even when the catalog is current.
    pub force: bool,
    /// Steam's build id for the record (`build_default` reads it from the app manifest).
    pub steam_build: Option<String>,
    /// Only these sections (None: all).
    pub sections: Option<Vec<String>>,
    /// The item icons folder to name in the catalog (None: `<data>/editor-assets/icons` when it exists).
    pub icons_dir: Option<PathBuf>,
    /// Also write the reflection dump for local Kiln.Game types (`<dest>/types/<side>-<exe sha>.json`, see
    /// [`super::types`]).
    pub types_dump: bool,
}

/// What [`build`] did.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct BuildInfo {
    pub side: String,
    pub path: PathBuf,
    /// false: the catalog was current and left alone.
    pub built: bool,
    pub game: GameInfo,
    pub counts: BTreeMap<String, u64>,
    pub bytes: u64,
    pub seconds: f64,
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub problems: Vec<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub types_dump: Option<PathBuf>,
}

/// The opened game: archive + reflection.
pub(super) struct Src {
    pub kfc: Kfc,
    pub types: TypeDb,
    pub exe_sha256: String,
    pub exe: PathBuf,
}

impl Src {
    pub fn open(game_dir: &Path) -> Result<Src> {
        let side = super::side_of(game_dir)
            .ok_or_else(|| Error::not_found(format!("{} has no enshrouded.kfc or enshrouded_server.kfc", game_dir.display())))?;
        let (kfc_name, exe_name) =
            if side == "client" { ("enshrouded.kfc", "enshrouded.exe") } else { ("enshrouded_server.kfc", "enshrouded_server.exe") };
        let kfc = Kfc::open(&game_dir.join(kfc_name))?;
        let exe = game_dir.join(exe_name);
        let raw = std::fs::read(&exe).map_err(crate::util::io_at(&exe, "reading"))?;
        let exe_sha256 = crate::hash::sha256_hex(&raw);
        let types = TypeDb::from_pe(&PeFile::parse(raw)?)?;
        let src = Src { kfc, types, exe_sha256, exe };
        // exe and data of one build: ItemInfo's layout hash must be the .kfc bundle's (else Steam is mid-update)
        src.check_layout("keen::ItemInfo")
            .map_err(|e| Error::new(ErrorCode::Conflict, format!("{} (are the exe and the data from different builds? Steam may be updating the game)", e.message)))?;
        Ok(src)
    }

    pub fn hash(name: &str) -> u32 {
        fnv1a32(name.as_bytes())
    }

    /// The reflection record of `name`, checked against the `.kfc` bundle's layout hash when the archive has
    /// resources of that type.
    fn check_layout(&self, name: &str) -> Result<&TypeRec> {
        let t = self.types.get(name).ok_or_else(|| Error::new(ErrorCode::Unsupported, format!("{name} is not in the exe's reflection")))?;
        if let Some(b) = self.kfc.bundle(t.hash)
            && b.layout != t.layout
        {
            return Err(Error::new(
                ErrorCode::Conflict,
                format!("{name}: layout 0x{:08x} in the exe, 0x{:08x} in the data", t.layout, b.layout),
            ));
        }
        Ok(t)
    }

    /// Every resource of a type: (index, key).
    pub(super) fn of_type(&self, name: &str) -> Result<Vec<(usize, crate::gameart::kfc::ResourceKey)>> {
        self.check_layout(name)?;
        Ok(self.kfc.resources_of_type(Self::hash(name)))
    }

    /// The bytes of the first resource of a type.
    pub(super) fn one(&self, name: &str) -> Result<Vec<u8>> {
        let r = self.of_type(name)?;
        let (ix, _) = r.first().ok_or_else(|| Error::not_found(format!("no {name} in the data")))?;
        self.kfc.read(*ix)
    }

    pub(super) fn enum_name(&self, type_name: &str, v: u64) -> Option<String> {
        self.types.enum_values(type_name)?.into_iter().find(|(_, x)| *x == v).map(|(n, _)| n)
    }
}

/// Loca: tag id -> en-us text; and the language codes.
pub(super) struct Loca {
    en: BTreeMap<u32, String>,
    languages: Vec<String>,
}

impl Loca {
    pub(super) fn text(&self, id: u32) -> Option<String> {
        if id == 0 {
            return None;
        }
        self.en.get(&id).map(|s| clean(s)).filter(|s| !s.is_empty())
    }
    /// A `LocaTagReference` (16 bytes) at `o` -> its text.
    fn text_ref(&self, b: &[u8], o: usize) -> Option<String> {
        guid_bytes_at(b, o).and_then(|g| self.text(fnv1a32(&g)))
    }
    /// A LocaTagReference GUID string (from the decoder) -> its text.
    fn text_guid(&self, v: &Value) -> Option<String> {
        guid_value(v).and_then(|g| crate::gameart::modded::guid_bytes(&g)).and_then(|g| self.text(fnv1a32(&g)))
    }
}

/// `keen::LanguageId` value name (`En_Us`) -> `en-us`.
fn lang_code(name: &str) -> String {
    name.replace('_', "-").to_lowercase()
}

fn read_loca(s: &Src) -> Result<Loca> {
    let coll = s.one("keen::LocaTagCollectionResource")?;
    let dec = Decoder::new(&s.types);
    let v = dec.decode("keen::LocaTagCollectionResource", &coll, 0)?;
    let o = Offsets::new(&s.types);
    let (id_off, text_off) = (o.get("keen::LocaTagResource", "id").unwrap_or(0), o.get("keen::LocaTagResource", "text").unwrap_or(4));
    let stride = s.types.get("keen::LocaTagResource").map_or(24, |t| t.size as usize);
    let mut languages = Vec::new();
    let mut en = BTreeMap::new();
    for (i, l) in v["languages"].as_array().cloned().unwrap_or_default().iter().enumerate() {
        let name = l["language"].as_str().map(str::to_string).unwrap_or_else(|| format!("lang{i}"));
        let code = lang_code(&name);
        if code == "en-us" || (en.is_empty() && i + 1 == v["languages"].as_array().map_or(0, Vec::len)) {
            let h = &l["data"]["hash"];
            let ch = ContentHash {
                size: h["size"].as_u64().unwrap_or(0) as u32,
                h: [h["hash0"].as_u64().unwrap_or(0) as u32, h["hash1"].as_u64().unwrap_or(0) as u32, h["hash2"].as_u64().unwrap_or(0) as u32],
            };
            if en.is_empty() || code == "en-us" {
                en = loca_table(&s.kfc.read_content(&ch)?, id_off, text_off, stride)?;
            }
        }
        languages.push(code);
    }
    Ok(Loca { en, languages })
}

pub(super) fn e(name: Option<String>, id: Option<String>, guid: Option<String>) -> Entry {
    Entry { name, id, guid, extra: Map::new() }
}

pub(super) fn put(e: &mut Entry, k: &str, v: impl Into<Value>) {
    let v = v.into();
    if !v.is_null() {
        e.extra.insert(k.to_string(), v);
    }
}

pub(super) fn opt<T: Into<Value>>(v: Option<T>) -> Value {
    v.map_or(Value::Null, Into::into)
}

/// The `T<n>` tier token of a debug name (`Weapon_T1_1H_Sword_01` -> 1); a naming convention, not a game field.
fn tier_of(name: &str) -> Option<u32> {
    name.split('_').find_map(|p| p.strip_prefix('T').filter(|d| !d.is_empty() && d.len() <= 2).and_then(|d| d.parse().ok()))
}

/// Item ids and GUIDs -> names (for recipes, loot, skills, workshops).
#[derive(Default)]
struct ItemNames {
    by_id: HashMap<u32, String>,
    by_guid: HashMap<String, String>,
}

impl ItemNames {
    fn of(&self, id: Option<u64>, guid: Option<&str>) -> Option<String> {
        id.filter(|i| *i != 0).and_then(|i| self.by_id.get(&(i as u32)).cloned()).or_else(|| guid.and_then(|g| self.by_guid.get(g).cloned()))
    }
}

fn items(s: &Src, loca: &Loca, icons: &HashSet<String>, names: &mut ItemNames) -> Result<Vec<Entry>> {
    const T: &str = "keen::ItemInfo";
    let o = Offsets::new(&s.types);
    let (generate, flags) = (o.get(T, "generateRarity"), o.get(T, "flags"));
    let (id, guid, stack, rarity, cat, name, desc, dbg, icon) = (
        o.need(T, "itemId")?,
        o.need(T, "objectId")?,
        o.get(T, "maxStackSize"),
        o.get(T, "rarity"),
        o.get(T, "category"),
        o.get(T, "name"),
        o.get(T, "description"),
        o.need(T, "debugName")?,
        o.get(T, "iconImage"),
    );
    // registered items: the ItemRegistry's list (the rest are ItemInfo resources the game does not register)
    let registered: Option<HashSet<String>> = s.one("keen::ItemRegistryResource").ok().and_then(|reg| {
        let at = o.get("keen::ItemRegistryResource", "itemRefs")?;
        let (base, n) = blob_array_at(&reg, at)?;
        Some((0..n).filter_map(|i| guid_at(&reg, base + 16 * i)).collect())
    });
    let textures = texture_names(s);
    let texture_set: HashSet<&str> = textures.values().map(String::as_str).collect();
    let mut out = Vec::new();
    for (ix, key) in s.of_type(T)? {
        if key.part != 0 {
            continue;
        }
        let b = s.kfc.read(ix)?;
        let item_id = u32_at(&b, id).unwrap_or(0);
        let g = guid_at(&b, guid).unwrap_or_else(|| key.guid_text());
        let debug = blob_string_at(&b, dbg);
        let n = if debug.is_empty() { g.clone() } else { debug.clone() };
        let mut en = e(Some(n.clone()), Some(hex_id(item_id)), Some(g.clone()));
        put(&mut en, "displayName", opt(name.and_then(|o| loca.text_ref(&b, o))));
        put(&mut en, "description", opt(desc.and_then(|o| loca.text_ref(&b, o))));
        put(&mut en, "kind", opt(cat.and_then(|o| u8_at(&b, o)).and_then(|v| s.enum_name("keen::ItemCategory", v as u64))));
        put(&mut en, "rarity", opt(rarity.and_then(|o| u8_at(&b, o)).and_then(|v| s.enum_name("keen::ItemRarity", v as u64))));
        put(&mut en, "tier", opt(tier_of(&debug)));
        // gear (ItemFlag HasPIDE) that a craft would make the lowest allowed rarity: kiln lint KL406
        if generate.and_then(|o| u8_at(&b, o)).is_some_and(|v| v != 0) && flags.and_then(|o| u32_at(&b, o)).is_some_and(|v| v & 2 != 0) {
            put(&mut en, "generateRarity", true);
        }
        put(&mut en, "maxStack", opt(stack.and_then(|o| u16_at(&b, o))));
        // the editor's icon file: `<texture debugName>.webp` (gameart's editor recipe); items use `<debugName>_icon`
        let candidates = [Some(format!("{debug}_icon")), icon.and_then(|o| guid_at(&b, o)).and_then(|g| textures.get(&g).cloned())];
        let icon_name = candidates
            .into_iter()
            .flatten()
            .find(|n| if icons.is_empty() { texture_set.contains(n.as_str()) } else { icons.contains(n) });
        put(&mut en, "icon", opt(icon_name.map(|n| format!("{n}.webp"))));
        if let Some(r) = &registered
            && !r.contains(&g)
        {
            put(&mut en, "registered", false);
        }
        names.by_id.insert(item_id, n.clone());
        names.by_guid.insert(g, n);
        out.push(en);
    }
    Ok(out)
}

/// UiTextureResource GUID -> debugName (part 0), client only; empty when unreadable.
fn texture_names(s: &Src) -> HashMap<String, String> {
    let mut m = HashMap::new();
    let o = Offsets::new(&s.types);
    let Some(off) = o.get("keen::UiTextureResource", "debugName") else { return m };
    let Ok(list) = s.of_type("keen::UiTextureResource") else { return m };
    for (ix, key) in list {
        if key.part != 0 {
            continue;
        }
        if let Ok(b) = s.kfc.read(ix) {
            m.entry(key.guid_text()).or_insert_with(|| blob_string_at(&b, off));
        }
    }
    m
}

fn recipes(s: &Src, loca: &Loca, items: &ItemNames, workshops: &HashMap<u32, String>) -> Result<(Vec<Entry>, Vec<Entry>)> {
    let reg = s.one("keen::RecipeRegistryResource")?;
    let v = Decoder::new(&s.types).decode("keen::RecipeRegistryResource", &reg, 0)?;
    let mut out = Vec::new();
    for r in v["recipes"].as_array().cloned().unwrap_or_default() {
        let name = r["debugName"].as_str().filter(|n| !n.is_empty()).map(str::to_string).or_else(|| guid_value(&r["recipeGuid"]));
        let mut en = e(name, hex_scalar(&r["recipeId"]), guid_value(&r["recipeGuid"]));
        put(&mut en, "displayName", opt(loca.text_guid(&r["recipeName"])));
        let ws = scalar(&r["workshopId"]).unwrap_or(0) as u32;
        if ws != 0 {
            put(&mut en, "workshop", hex_id(ws));
            put(&mut en, "workshopName", opt(workshops.get(&ws).cloned()));
        }
        let stack = |x: &Value| -> Value {
            let it = items.of(scalar(&x["item"]), x["itemRef"].as_str());
            json!({ "item": it, "count": x["count"] })
        };
        let outs: Vec<Value> = r["output"].as_array().map(|a| a.iter().map(stack).collect()).unwrap_or_default();
        let ins: Vec<Value> = r["input"]
            .as_array()
            .map(|a| {
                a.iter()
                    .map(|x| {
                        let cat = scalar(&x["inputItemCategory"]["category"]).unwrap_or(0) as u32;
                        if cat != 0 {
                            json!({ "category": hex_id(cat), "count": x["inputItemCategory"]["count"] })
                        } else {
                            stack(&x["itemStack"])
                        }
                    })
                    .collect()
            })
            .unwrap_or_default();
        put(&mut en, "inputs", Value::Array(ins));
        put(&mut en, "outputs", Value::Array(outs));
        out.push(en);
    }
    let mut cats = Vec::new();
    for c in v["inputCategories"].as_array().cloned().unwrap_or_default() {
        let mut en = e(None, hex_scalar(&c["inputCategoryId"]), None);
        let members: Vec<Value> =
            c["items"].as_array().map(|a| a.iter().filter_map(|x| items.of(scalar(&x["itemId"]), None)).map(Value::from).collect()).unwrap_or_default();
        put(&mut en, "items", Value::Array(members));
        cats.push(en);
    }
    Ok((out, cats))
}

fn workshops(s: &Src, loca: &Loca, items: &ItemNames) -> Result<(Vec<Entry>, HashMap<u32, String>)> {
    let reg = s.one("keen::WorkshopRegistryResource")?;
    let v = Decoder::new(&s.types).decode("keen::WorkshopRegistryResource", &reg, 0)?;
    let mut out = Vec::new();
    let mut names = HashMap::new();
    for (kind, list) in [("npc", &v["npcs"]), ("workshop", &v["workshops"])] {
        for w in list.as_array().cloned().unwrap_or_default() {
            let id = scalar(&w["workshopId"]).unwrap_or(0) as u32;
            let station = items.of(scalar(&w["item"]), w["itemRef"].as_str());
            let mut en = e(station.clone(), Some(hex_id(id)), guid_value(&w["workshopGuid"]));
            let dn = scalar(&w["name"]).and_then(|t| loca.text(t as u32));
            put(&mut en, "displayName", opt(dn.clone()));
            put(&mut en, "kind", kind);
            if let Some(n) = station.or(dn) {
                names.insert(id, n);
            }
            out.push(en);
        }
    }
    Ok((out, names))
}

fn loot(s: &Src, items: &ItemNames) -> Result<Vec<Entry>> {
    let reg = s.one("keen::LootableItemsResource")?;
    let o = Offsets::new(&s.types);
    let at = o.need("keen::LootableItemsResource", "items")?;
    let stride = s.types.get("keen::LootableItemInfo").map_or(36, |t| t.size as usize);
    let idf = o.get("keen::LootableItemInfo", "itemId").unwrap_or(0);
    let (base, n) = blob_array_at(&reg, at).ok_or_else(|| Error::new(ErrorCode::Parse, "LootableItemsResource.items"))?;
    let mut out = Vec::new();
    for i in 0..n {
        let id = u32_at(&reg, base + i * stride + idf).unwrap_or(0);
        out.push(e(items.of(Some(id as u64), None), Some(hex_id(id)), None));
    }
    Ok(out)
}

fn drops(s: &Src) -> Result<Vec<Entry>> {
    Ok(s.of_type("keen::ecs::DefaultInventoryResource")?.into_iter().filter(|(_, k)| k.part == 0).map(|(_, k)| e(None, None, Some(k.guid_text()))).collect())
}

/// Templates (names read by offset: 12k resources) + the arsenals they reference.
/// arsenal GUID -> the templates naming it.
type ArsenalUsers = HashMap<String, Vec<String>>;

/// Component short name -> reflection type name, for every component some template has.
type ComponentTypes = BTreeMap<String, String>;

fn templates(s: &Src) -> Result<(Vec<Entry>, ArsenalUsers, ArsenalUsers, ComponentTypes, VfxUsers)> {
    const T: &str = "keen::ecs::TemplateResource";
    let o = Offsets::new(&s.types);
    let name_off = o.need(T, "name")?;
    let fam_off = o.get(T, "family");
    let comp_off = o.get(T, "components");
    let arsenal_ref = s.types.get("keen::ecs::EnemyArsenalReference").map(|t| t.hash);
    let arsenal_field = o.get("keen::ecs::EnemyArsenalReference", "arsenal").unwrap_or(0);
    let graph_ref = s.types.get("keen::ecs::AnimationGraphResourceComponent2").map(|t| t.hash);
    let graph_field = o.get("keen::ecs::AnimationGraphResourceComponent2", "animationGraph").unwrap_or(0);
    let mut short: HashMap<u32, String> = HashMap::new();
    let mut out = Vec::new();
    let mut by_arsenal: ArsenalUsers = HashMap::new();
    let mut by_graph: ArsenalUsers = HashMap::new();
    let mut components = ComponentTypes::new();
    let carrier = s.types.get("keen::ecs::VfxComponentResource").map(|t| t.hash);
    let mut vfx_field: HashMap<u32, Option<usize>> = HashMap::new();
    let mut by_vfx = VfxUsers::new();
    for (ix, key) in s.of_type(T)? {
        if key.part != 0 {
            continue;
        }
        let b = s.kfc.read(ix)?;
        let g = key.guid_text();
        let n = blob_string_at(&b, name_off);
        let named = !n.is_empty();
        let mut carried = false;
        let n = if n.is_empty() { g.clone() } else { n };
        let mut en = e(Some(n.clone()), Some(hex_id(fnv1a32(&key.guid))), Some(g));
        put(&mut en, "family", opt(fam_off.and_then(|o| u8_at(&b, o)).and_then(|v| s.enum_name("keen::ecs::TemplateFamily", v as u64)).filter(|f| f != "None")));
        if let Some(co) = comp_off
            && let Some((base, cn)) = blob_array_at(&b, co)
        {
            let mut comps = Vec::with_capacity(cn);
            for i in 0..cn.min(512) {
                let at = base + 12 * i;
                let Some(h) = u32_at(&b, at) else { break };
                if h == 0 {
                    continue;
                }
                let nm = short.entry(h).or_insert_with(|| {
                    let rec = s.types.by_hash(h);
                    let name = rec.map_or_else(|| format!("0x{h:08x}"), |t| t.name.rsplit("::").next().unwrap_or(&t.name).to_string());
                    if let Some(t) = rec {
                        components.entry(name.clone()).or_insert_with(|| t.name.clone());
                    }
                    name
                });
                comps.push(Value::from(nm.clone()));
                if Some(h) == arsenal_ref
                    && let Some(rel) = decode::i32_at(&b, at + 4)
                    && let Some(ag) = guid_at(&b, (at as i64 + 4 + rel as i64) as usize + arsenal_field)
                {
                    by_arsenal.entry(ag).or_default().push(n.clone());
                }
                if Some(h) == graph_ref
                    && let Some(rel) = decode::i32_at(&b, at + 4)
                    && let Some(gg) = guid_at(&b, (at as i64 + 4 + rel as i64) as usize + graph_field)
                {
                    by_graph.entry(gg).or_default().push(n.clone());
                }
                // any component with a `vfx` field says what it plays; only VfxComponentResource (the first) is what a name finds at run time
                let field = *vfx_field.entry(h).or_insert_with(|| {
                    let rec = s.types.by_hash(h)?;
                    let (off, ty) = s.types.path(&rec.name, "vfx")?;
                    (ty?.name == "keen::VfxReference").then_some(off as usize)
                });
                if named
                    && let Some(field) = field
                    && let Some(rel) = decode::i32_at(&b, at + 4)
                    && let Some(vg) = guid_at(&b, (at as i64 + 4 + rel as i64) as usize + field)
                {
                    let user = by_vfx.entry(vg).or_default();
                    if Some(h) == carrier && !carried {
                        carried = true;
                        user.templates.push(n.clone());
                    } else {
                        user.labels.push(format!("{n}_{nm}"));
                    }
                }
            }
            put(&mut en, "components", Value::Array(comps));
        }
        out.push(en);
    }
    Ok((out, by_arsenal, by_graph, components, by_vfx))
}

/// A GUID in `guid_text` form back to its 16 bytes.
fn guid_text_bytes(g: &str) -> Option<[u8; 16]> {
    let hex: String = g.chars().filter(|c| *c != '-').collect();
    if hex.len() != 32 {
        return None;
    }
    let mut b = [0u8; 16];
    for (i, o) in b.iter_mut().enumerate() {
        *o = u8::from_str_radix(hex.get(i * 2..i * 2 + 2)?, 16).ok()?;
    }
    b[..4].reverse();
    b[4..6].reverse();
    b[6..8].reverse();
    Some(b)
}

/// FNV-1a of a state's GUID -> the GUID, from the sequences that set it: a graph holds only the hash, and a template or a mod's sequence names a state by GUID.
fn state_guids(s: &Src) -> Result<HashMap<u32, String>> {
    const T: &str = "keen::actor::ActorSequenceResource";
    let dec = Decoder::new(&s.types);
    let mut out = HashMap::new();
    let mut add = |g: Option<String>| {
        if let Some(g) = g
            && let Some(gb) = guid_text_bytes(&g)
        {
            out.entry(fnv1a32(&gb)).or_insert(g);
        }
    };
    for (ix, key) in s.of_type(T)? {
        if key.part != 0 {
            continue;
        }
        let b = s.kfc.read(ix)?;
        let Ok(v) = dec.decode(T, &b, 0) else { continue };
        for sub in v["subSequences"].as_array().into_iter().flatten() {
            for ev in sub["events"].as_array().into_iter().flatten() {
                if ev["$type"].as_str() == Some("keen::actor::SetAnimationEvent") {
                    add(guid_value(&ev["value"]["animationName"]));
                }
            }
        }
    }
    Ok(out)
}

/// The graph input that holds the current state (docs/re/npc-internals.md 2.5).
const STATE_INPUT: u64 = 0x5D2B_FB0A;

/// One entry per animation graph: the states it has and what enters each, so a premade animation can list its states and a mod can check it names real ones.
fn graphs(s: &Src, by_graph: &HashMap<String, Vec<String>>) -> Result<Vec<Entry>> {
    const G: &str = "keen::anim_graph::runtime_graph::AnimationGraphResource2_0";
    let state_guid = state_guids(s).unwrap_or_default();
    let dec = Decoder::new(&s.types);
    let mut out = Vec::new();
    for (ix, key) in s.of_type(G)? {
        if key.part != 0 {
            continue;
        }
        let b = s.kfc.read(ix)?;
        let v = dec.decode(G, &b, 0)?;
        let nodes: HashMap<u64, &Value> = v["nodeDefinitions"].as_array().into_iter().flatten().filter_map(|n| Some((scalar(&n["value"]["id"])?, &n["value"]))).collect();
        let g = key.guid_text();
        let mut users = by_graph.get(&g).cloned().unwrap_or_default();
        users.sort();
        users.dedup();
        let name = users.iter().min_by_key(|t| (t.len(), t.as_str())).cloned().unwrap_or_else(|| g.clone());
        let mut en = e(Some(name), Some(hex_id(fnv1a32(&key.guid))), Some(g));
        put(&mut en, "skeleton", opt(guid_value(&v["hierarchy"])));
        // a state's id: the id compared by the transition into it
        let mut states: Vec<Value> = Vec::new();
        let mut seen = HashSet::new();
        let mut add = |to: &Value, cond: &Value| {
            let (Some(to), Some(c)) = (scalar(to).and_then(|i| nodes.get(&i)), scalar(cond).and_then(|i| nodes.get(&i))) else { return };
            if c["type"].as_str() != Some("IdParameterEqualsConstant") || scalar(&c["parameterHash"]) != Some(STATE_INPUT) {
                return;
            }
            let Some(id) = scalar(&c["value"]).map(|x| x as u32) else { return };
            if !seen.insert(id) {
                return;
            }
            let mut st = json!({ "name": to["debugName"], "id": format!("0x{id:08x}") });
            if let Some(gu) = state_guid.get(&id) {
                st["guid"] = json!(gu);
            }
            let source = scalar(&to["poseResult"]).and_then(|i| nodes.get(&i)).and_then(|p| scalar(&p["input"])).and_then(|i| nodes.get(&i));
            if let Some(src) = source {
                match src["type"].as_str() {
                    Some("SampleAnimationClip") => {
                        st["clip"] = src["animation"].clone();
                        st["seconds"] = json!((src["animationDuration"].as_f64().unwrap_or(0.0) * 1000.0).round() / 1000.0);
                        st["loop"] = src["loop"].clone();
                    }
                    Some("BlendSpace_1D") => st["blend"] = json!(format!("0x{:08x}", scalar(&src["parameterHash"]).unwrap_or(0) as u32)),
                    _ => {}
                }
            }
            states.push(st);
        };
        for n in nodes.values() {
            match n["type"].as_str() {
                Some("StateMachine") => {
                    let count = n["numGlobalTransitions"].as_u64().unwrap_or(0) as usize;
                    for t in n["globalTransitions"].as_array().into_iter().flatten().take(count) {
                        add(&t["toState"], &t["conditionNode"]);
                    }
                }
                Some("StateMachineTransition") => add(&n["toState"], &n["conditionNode"]),
                _ => {}
            }
        }
        states.sort_by(|a, b| a["name"].as_str().cmp(&b["name"].as_str()).then_with(|| a["id"].as_str().cmp(&b["id"].as_str())));
        put(&mut en, "states", Value::Array(states));
        let floats: Vec<Value> = v["floatParameters"]["parameterIds"].as_array().into_iter().flatten().filter_map(|p| scalar(p)).map(|h| json!(format!("0x{:08x}", h as u32))).collect();
        put(&mut en, "floatInputs", Value::Array(floats));
        let slot_names = s.types.get("keen::AttachmentSlot").map(|t| t.values.clone()).unwrap_or_default();
        let mut slots = Map::new();
        for (i, j) in v["slotBoneIndexMapping"].as_array().into_iter().flatten().enumerate() {
            if let Some(j) = scalar(j).filter(|j| *j != 0)
                && let Some((n, _)) = slot_names.iter().find(|(_, val)| *val == i as u64)
            {
                slots.insert(n.clone(), json!(j));
            }
        }
        put(&mut en, "slots", Value::Object(slots));
        put(&mut en, "nodes", nodes.len() as u64);
        put(&mut en, "templates", Value::Array(users.into_iter().map(Value::from).collect()));
        out.push(en);
    }
    Ok(out)
}

fn arsenals(s: &Src, by_arsenal: &HashMap<String, Vec<String>>) -> Result<Vec<Entry>> {
    const R: &str = "keen::enemy::EnemyArsenalRegistryResource";
    let reg = s.one(R)?;
    let o = Offsets::new(&s.types);
    let at = o.need(R, "arsenals")?;
    let stride = s.types.get("keen::enemy::EnemyArsenalResource").map_or(32, |t| t.size as usize);
    let gf = o.get("keen::enemy::EnemyArsenalResource", "guid").unwrap_or(0);
    let (base, n) = blob_array_at(&reg, at).ok_or_else(|| Error::new(ErrorCode::Parse, "arsenals"))?;
    let mut out = Vec::new();
    for i in 0..n {
        let Some(gb) = guid_bytes_at(&reg, base + i * stride + gf) else { continue };
        let g = crate::gameart::kfc::guid_text(&gb);
        let mut en = e(Some(g.clone()), Some(hex_id(fnv1a32(&gb))), Some(g.clone()));
        let mut t = by_arsenal.get(&g).cloned().unwrap_or_default();
        t.sort();
        t.dedup();
        put(&mut en, "templates", Value::Array(t.into_iter().map(Value::from).collect()));
        out.push(en);
    }
    Ok(out)
}

/// kiln.gamedata's sub-sequence id: FNV-1a(GUID), xor k * 0x9E3779B1 for k > 0.
pub fn sub_id(g: &[u8; 16], k: usize) -> u32 {
    let h = fnv1a32(g);
    if k == 0 { h } else { h ^ (k as u32).wrapping_mul(0x9E37_79B1) }
}

/// Sequences, and which VFX they play: `events[VfxNotifierEvent#0].vfx` is what a name finds at run time.
fn sequences(s: &Src, by_vfx: &mut VfxUsers) -> Result<Vec<Entry>> {
    const T: &str = "keen::actor::ActorSequenceResource";
    const S: &str = "keen::actor::ActorSubSequenceResource";
    let o = Offsets::new(&s.types);
    let events_off = o.get(S, "events");
    let notifier = s.types.get("keen::VfxNotifierEvent").map(|t| t.hash);
    let notifier_vfx = o.get("keen::VfxNotifierEvent", "vfx");
    let spawn = s.types.get("keen::SpawnEntityEvent").map(|t| t.hash);
    let spawn_vfx = o.get("keen::SpawnEntityEvent", "vfxOnSpawn");
    let subs = o.need(T, "subSequences")?;
    let stride = s.types.get(S).map_or(72, |t| t.size as usize);
    let name_off = o.need(S, "name")?;
    let len_off = o.get(S, "length");
    let mut seen = HashSet::new();
    let mut out = Vec::new();
    for (ix, key) in s.of_type(T)? {
        if key.part != 0 {
            continue;
        }
        let b = s.kfc.read(ix)?;
        let Some((base, n)) = blob_array_at(&b, subs) else { continue };
        let g = key.guid_text();
        for k in 0..n.min(64) {
            let at = base + k * stride;
            let mut name = blob_string_at(&b, at + name_off);
            if name.is_empty() || seen.contains(&name) {
                name = if k == 0 { g.clone() } else { format!("{g}#{k}") };
            }
            seen.insert(name.clone());
            if let Some(eo) = events_off
                && !name.starts_with(g.as_str())
                && let Some((ebase, en)) = blob_array_at(&b, at + eo)
            {
                let mut first = true;
                for i in 0..en.min(4096) {
                    let va = ebase + 12 * i;
                    let (Some(h), Some(rel)) = (u32_at(&b, va), decode::i32_at(&b, va + 4)) else { break };
                    let body = (va as i64 + 4 + rel as i64) as usize;
                    if Some(h) == notifier
                        && let Some(vg) = notifier_vfx.and_then(|f| guid_at(&b, body + f))
                    {
                        let user = by_vfx.entry(vg).or_default();
                        if first {
                            user.sequences.push(name.clone());
                        } else {
                            user.labels.push(name.clone());
                        }
                        first = false;
                    } else if Some(h) == spawn
                        && let Some(vg) = spawn_vfx.and_then(|f| guid_at(&b, body + f))
                    {
                        by_vfx.entry(vg).or_default().labels.push(format!("{name}_Spawn"));
                    }
                }
            }
            let mut en = e(Some(name), Some(hex_id(sub_id(&key.guid, k))), Some(g.clone()));
            put(&mut en, "sequence", g.clone());
            if k > 0 {
                put(&mut en, "index", k as u64);
            }
            if let Some(lo) = len_off
                && let Some(ns) = decode::u64_at(&b, at + lo)
            {
                put(&mut en, "length", (ns as f64 / 1e9 * 1000.0).round() / 1000.0);
            }
            out.push(en);
        }
    }
    Ok(out)
}

fn buffs(s: &Src, loca: &Loca) -> Result<Vec<Entry>> {
    let coll = s.one("keen::BuffTypeCollectionResource")?;
    let v = Decoder::new(&s.types).decode("keen::BuffTypeCollectionResource", &coll, 0)?;
    let mut out = Vec::new();
    for b in v["buffs"].as_array().cloned().unwrap_or_default() {
        let id = scalar(&b["buffTypeId"]).unwrap_or(0) as u32;
        let mut en = e(Some(format!("buff_{id:08x}")), Some(hex_id(id)), None);
        put(&mut en, "displayName", opt(loca.text_guid(&b["name"])));
        put(&mut en, "description", opt(loca.text_guid(&b["description"])));
        put(&mut en, "slot", b["slot"].clone());
        if b["hideInUi"].as_bool() == Some(true) {
            put(&mut en, "hidden", true);
        }
        out.push(en);
    }
    Ok(out)
}

fn perks(s: &Src, loca: &Loca) -> Result<Vec<Entry>> {
    const T: &str = "keen::Perk";
    let o = Offsets::new(&s.types);
    let dbg = o.need(T, "debugName")?;
    let (name, desc) = (o.get(T, "name"), o.get(T, "description"));
    let mut out = Vec::new();
    for (ix, key) in s.of_type(T)? {
        if key.part != 0 {
            continue;
        }
        let b = s.kfc.read(ix)?;
        let g = key.guid_text();
        let n = blob_string_at(&b, dbg);
        let mut en = e(Some(if n.is_empty() { g.clone() } else { n }), Some(hex_id(fnv1a32(&key.guid))), Some(g));
        put(&mut en, "displayName", opt(name.and_then(|o| loca.text_ref(&b, o))));
        put(&mut en, "description", opt(desc.and_then(|o| loca.text_ref(&b, o))));
        out.push(en);
    }
    Ok(out)
}

fn skills(s: &Src, loca: &Loca, items: &ItemNames) -> Result<Vec<Entry>> {
    let tree = s.one("keen::SkillTreeResource")?;
    let v = Decoder::new(&s.types).decode("keen::SkillTreeResource", &tree, 0)?;
    let mut out = Vec::new();
    for n in v["nodes"].as_array().cloned().unwrap_or_default() {
        let id = scalar(&n["id"]).unwrap_or(0) as u32;
        let mut en = e(Some(format!("skill_{id:08x}")), Some(hex_id(id)), None);
        put(&mut en, "displayName", opt(scalar(&n["name"]).and_then(|t| loca.text(t as u32))));
        put(&mut en, "description", opt(scalar(&n["description"]).and_then(|t| loca.text(t as u32))));
        put(&mut en, "item", opt(items.of(scalar(&n["activeSkillUnlock"]), n["activeSkillUnlockRef"].as_str())));
        put(&mut en, "type", n["type"].clone());
        put(&mut en, "maxLevel", n["maxLevel"].clone());
        put(&mut en, "costs", n["costs"].clone());
        out.push(en);
    }
    Ok(out)
}

/// Sound cues (`SoundContainerResource`, client only): GUID, a name (its first sound's debugName), scene, positional.
fn sounds(s: &Src) -> Result<(Vec<Entry>, HashMap<String, String>)> {
    let o = Offsets::new(&s.types);
    let mut sound_names: HashMap<String, String> = HashMap::new();
    if let Some(off) = o.get("keen::SoundResource", "debugName") {
        for (ix, key) in s.of_type("keen::SoundResource")? {
            if key.part == 0
                && let Ok(b) = s.kfc.read(ix)
            {
                sound_names.insert(key.guid_text(), blob_string_at(&b, off));
            }
        }
    }
    const C: &str = "keen::SoundContainerResource";
    let entries_off = o.need(C, "entries")?;
    let (scene_off, pos_off) = (o.get(C, "scene"), o.get(C, "hasPosition"));
    let entry_hash = s.types.get("keen::SoundContainerResourceSoundEntry").map(|t| t.hash);
    let sound_field = o.get("keen::SoundContainerResourceSoundEntry", "sound").unwrap_or(4);
    let mut out = Vec::new();
    for (ix, key) in s.of_type(C)? {
        if key.part != 0 {
            continue;
        }
        let b = s.kfc.read(ix)?;
        let g = key.guid_text();
        let mut first = None;
        let mut count = 0u64;
        if let Some((base, n)) = blob_array_at(&b, entries_off) {
            for i in 0..n.min(4096) {
                let at = base + 12 * i;
                let Some(h) = u32_at(&b, at) else { break };
                count += 1;
                if first.is_none()
                    && Some(h) == entry_hash
                    && let Some(rel) = decode::i32_at(&b, at + 4)
                {
                    first = guid_at(&b, (at as i64 + 4 + rel as i64) as usize + sound_field).and_then(|sg| sound_names.get(&sg).cloned());
                }
            }
        }
        let mut en = e(first.filter(|n| !n.is_empty()).or_else(|| Some(g.clone())), Some(hex_id(fnv1a32(&key.guid))), Some(g));
        put(&mut en, "scene", opt(scene_off.and_then(|o| u8_at(&b, o)).and_then(|v| s.enum_name("keen::SoundContainerScene", v as u64))));
        if pos_off.and_then(|o| u8_at(&b, o)) == Some(1) {
            put(&mut en, "positional", true);
        }
        put(&mut en, "entries", count);
        out.push(en);
    }
    Ok((out, sound_names))
}

/// What names a VFX. VFX have no names of their own, so the catalog derives one from what uses it.
#[derive(Default)]
struct VfxUser {
    /// Templates whose first `VfxComponentResource` is this VFX: `VfxId.Game` finds it by these names.
    templates: Vec<String>,
    /// Sequences whose first `VfxNotifierEvent` plays it: `VfxId.Game` finds it by these too.
    sequences: Vec<String>,
    /// Everything else that names it (`DestroyOnLoot`, a later event, ...): a label, which nothing finds at run time.
    labels: Vec<String>,
}
type VfxUsers = HashMap<String, VfxUser>;

/// The best of several names: not a `Base_` template, then the shortest, then by text.
fn best_name(list: &[String]) -> Option<&String> {
    list.iter().min_by_key(|n| (n.starts_with("Base_"), n.len(), n.as_str()))
}

/// A parameter's type, from its DsTypeId: the FNV-1a of `VfxEffectParameter<Name>`. Lower case first, as `Vfx.Set` documents them.
fn vfx_parameter_types() -> HashMap<u32, String> {
    let mut m = HashMap::new();
    let scalar = |n: &str| [n.to_string(), format!("{n}2"), format!("{n}3"), format!("{n}4")];
    let names = [scalar("Float"), scalar("Int"), scalar("Uint"), scalar("Bool")].concat();
    for n in names.iter().map(String::as_str).chain(["Color", "Quaternion", "Mesh", "Model", "JointCollection"]) {
        let mut lower = n.to_string();
        lower[..1].make_ascii_lowercase();
        m.insert(fnv1a32(format!("VfxEffectParameter{n}").as_bytes()), lower);
    }
    m
}

/// Parameter names are stored only as hashes. Recovered by hashing what the player's own game has: the exe's strings and the names of particle state members.
fn vfx_parameter_names(s: &Src, wanted: &HashSet<u32>, state_names: &HashSet<String>) -> HashMap<u32, String> {
    let mut out = HashMap::new();
    for n in state_names {
        let h = fnv1a32(n.as_bytes());
        if wanted.contains(&h) {
            out.insert(h, n.clone());
        }
    }
    if let Ok(raw) = std::fs::read(&s.exe) {
        for run in raw.split(|c| !(c.is_ascii_alphanumeric() || *c == b'_')) {
            if (2..=48).contains(&run.len()) && !run[0].is_ascii_digit() {
                let h = fnv1a32(run);
                if wanted.contains(&h)
                    && !out.contains_key(&h)
                    && let Ok(t) = std::str::from_utf8(run)
                {
                    out.insert(h, t.to_string());
                }
            }
        }
    }
    out
}

/// The default of a parameter, from the 64-byte block: only the kinds an author can say back through `Vfx.Set`.
fn vfx_parameter_default(block: &[u8], ty: &str, start: usize, count: usize) -> Option<Value> {
    let bytes = block.get(start..start + count)?;
    let f = |i: usize| bytes.get(4 * i..4 * i + 4).map(|x| f32::from_le_bytes(x.try_into().unwrap())).filter(|v| v.is_finite()).map(|v| Value::from((v as f64 * 1e4).round() / 1e4));
    Some(match ty {
        "float" => f(0)?,
        "float2" | "float3" | "float4" => Value::Array((0..count / 4).map(f).collect::<Option<Vec<_>>>()?),
        "int" => Value::from(i32::from_le_bytes(bytes.get(..4)?.try_into().ok()?)),
        "uint" => Value::from(u32::from_le_bytes(bytes.get(..4)?.try_into().ok()?)),
        "bool" => Value::from(u32::from_le_bytes(bytes.get(..4)?.try_into().ok()?) != 0),
        "color" => {
            let c = bytes.get(..4)?;
            Value::from(format!("#{:02x}{:02x}{:02x}{:02x}", c[0], c[1], c[2], c[3]))
        }
        _ => return None,
    })
}

/// How long Kiln runs a one-shot of this VFX: the larger of its kill time and its `duration` + `lifetime` parameters, 10 s if it says nothing,
/// 1 to 60 s. The same rule as `one_shot_ns` in native/services/vfx/core.cpp.
fn vfx_length(kill: u32, duration: Option<f64>, lifetime: Option<f64>) -> f64 {
    let exposed = duration.filter(|v| *v > 0.0).unwrap_or(0.0) + lifetime.filter(|v| *v > 0.0).unwrap_or(0.0);
    let s = (kill as f64).max(exposed);
    if s <= 0.0 { 10.0 } else { (s.clamp(1.0, 60.0) * 1000.0).round() / 1000.0 }
}

/// Every `keen::VfxResource`, named from what uses it.
fn vfx(s: &Src, users: &VfxUsers) -> Result<Vec<Entry>> {
    const V: &str = "keen::VfxResource";
    const P: &str = "keen::VfxUserEffectParameterDescription";
    let o = Offsets::new(&s.types);
    let (kill_off, particles_off, emitters_off) = (o.need(V, "killTimeInSeconds")?, o.need(V, "particles")?, o.need(V, "emitterCount")?);
    let (params_off, defaults_off, images_off, meshes_off) = (o.need(V, "parameters")?, o.need(V, "defaultParameterData")?, o.need(V, "images")?, o.need(V, "meshes")?);
    let (cull_min, cull_max) = (o.need(V, "cullingDistanceMin")?, o.need(V, "cullingDistanceMax")?);
    let states_off = o.get(V, "stateInfos");
    let members_off = o.get("keen::VfxParticleStateBufferInfo", "members").unwrap_or(0);
    let member_name = o.get("keen::VfxBufferMember", "name");
    let stride = |n: &str, d: usize| s.types.get(n).map_or(d, |t| t.size as usize);
    let (state_stride, member_stride, image_stride, param_stride) = (stride("keen::VfxParticleStateBufferInfo", 12), stride("keen::VfxBufferMember", 16), stride("keen::VfxImagePatchPoint", 24), stride(P, 12));
    let image_ref = o.get("keen::VfxImagePatchPoint", "image");
    let (p_name, p_type, p_range) = (o.need(P, "nameHash")?, o.need(P, "typeId")?, o.need(P, "dataRange")?);
    let (range_start, range_count) = (o.get("keen::Range8", "start").unwrap_or(0), o.get("keen::Range8", "count").unwrap_or(1));

    let list: Vec<_> = s.of_type(V)?.into_iter().filter(|(_, k)| k.part == 0).collect();
    let mut blobs = Vec::with_capacity(list.len());
    let mut wanted = HashSet::new();
    let mut state_names = HashSet::new();
    for (ix, key) in &list {
        let b = s.kfc.read(*ix)?;
        if let Some((base, n)) = blob_array_at(&b, params_off) {
            for i in 0..n.min(64) {
                if let Some(h) = u32_at(&b, base + i * param_stride + p_name) {
                    wanted.insert(h);
                }
            }
        }
        if let (Some(so), Some(mn)) = (states_off, member_name)
            && let Some((base, n)) = blob_array_at(&b, so)
        {
            for i in 0..n.min(256) {
                if let Some((mb, mc)) = blob_array_at(&b, base + i * state_stride + members_off) {
                    for j in 0..mc.min(256) {
                        let t = blob_string_at(&b, mb + j * member_stride + mn);
                        if !t.is_empty() {
                            state_names.insert(t);
                        }
                    }
                }
            }
        }
        blobs.push((key.guid_text(), key.guid, b));
    }
    let names = vfx_parameter_names(s, &wanted, &state_names);
    let types = vfx_parameter_types();
    // a name that two VFX could claim stays with the first (a template's wins: the run-time lookup tries templates first)
    let mut holder: HashMap<&str, &str> = HashMap::new();
    let mut guids: Vec<&String> = users.keys().collect();
    guids.sort();
    for g in &guids {
        for t in &users[*g].templates {
            holder.entry(t).or_insert(g);
        }
    }
    for g in &guids {
        for t in &users[*g].sequences {
            holder.entry(t).or_insert(g);
        }
    }
    let empty = VfxUser::default();
    // run-time names first, so that a label never takes one
    let mut taken: HashSet<String> = HashSet::new();
    let mut run_time: HashMap<&str, (String, &str)> = HashMap::new();
    let mut all_names: HashMap<&str, Vec<String>> = HashMap::new();
    for (g, _, _) in &blobs {
        let user = users.get(g).unwrap_or(&empty);
        let finds = |l: &[String]| l.iter().filter(|n| holder.get(n.as_str()) == Some(&g.as_str())).cloned().collect::<Vec<_>>();
        let (by_t, by_s) = (finds(&user.templates), finds(&user.sequences));
        let chosen = best_name(&by_t).map(|n| (n.clone(), "template")).or_else(|| best_name(&by_s).map(|n| (n.clone(), "sequence")));
        if let Some(c) = chosen {
            taken.insert(c.0.clone());
            run_time.insert(g, c);
            let mut every: Vec<String> = by_t.into_iter().chain(by_s).collect();
            every.sort_by_key(|n| (n.starts_with("Base_"), n.len(), n.clone()));
            every.dedup();
            all_names.insert(g, every);
        }
    }
    let mut out = Vec::with_capacity(blobs.len());
    for (g, raw, b) in &blobs {
        let user = users.get(g).unwrap_or(&empty);
        let by_name = run_time.contains_key(g.as_str());
        let (name, from) = if let Some((n, from)) = run_time.get(g.as_str()) {
            (n.clone(), Some(*from))
        } else if user.labels.is_empty() {
            (format!("Vfx_{}", &g[..8]), None)
        } else {
            // the best label that's free. Labels repeat (two uses by one template, or a name another VFX has), and names stay unique.
            let mut labels: Vec<&String> = user.labels.iter().collect();
            labels.sort_by_key(|n| (n.starts_with("Base_"), n.len(), n.as_str()));
            let free = labels.iter().find(|n| !taken.contains(n.as_str())).map(|n| n.to_string());
            let name = free.unwrap_or_else(|| (2..).map(|k| format!("{}_{k}", labels[0])).find(|n| !taken.contains(n)).unwrap());
            taken.insert(name.clone());
            (name, Some("use"))
        };
        let mut en = e(Some(name.clone()), Some(hex_id(fnv1a32(raw))), Some(g.clone()));
        if let Some(from) = from {
            put(&mut en, "namedFrom", from);
        }
        if by_name {
            put(&mut en, "byName", true);
        }
        // every name a run-time lookup takes, then a few of what else uses it
        let every = all_names.get(g.as_str());
        if let Some(every) = every.filter(|e| e.len() > 1) {
            put(&mut en, "names", Value::Array(every.iter().map(|n| Value::from(n.as_str())).collect()));
        }
        let mut others: Vec<&String> = user.labels.iter().filter(|n| **n != name && every.is_none_or(|e| !e.contains(n))).collect();
        others.sort_by_key(|n| (n.starts_with("Base_"), n.len(), n.as_str()));
        others.dedup();
        if !others.is_empty() {
            put(&mut en, "usedBy", Value::Array(others.iter().take(8).map(|n| Value::from(n.as_str())).collect()));
        }
        let count = |off: usize| blob_array_at(b, off).map_or(0, |(_, n)| n as u64);
        put(&mut en, "particles", count(particles_off));
        put(&mut en, "emitters", u32_at(b, emitters_off).unwrap_or(0));
        put(&mut en, "meshes", count(meshes_off));
        let mut tex = HashSet::new();
        if let (Some(ir), Some((base, n))) = (image_ref, blob_array_at(b, images_off)) {
            for i in 0..n.min(4096) {
                if let Some(t) = guid_at(b, base + i * image_stride + ir) {
                    tex.insert(t);
                }
            }
        }
        put(&mut en, "textures", tex.len() as u64);
        let kill = u32_at(b, kill_off).unwrap_or(0);
        let f32_at = |off: usize| u32_at(b, off).map(|v| f32::from_bits(v) as f64);
        put(&mut en, "cullDistance", json!([f32_at(cull_min), f32_at(cull_max)]));
        put(&mut en, "killTime", kill);
        let block = b.get(defaults_off..defaults_off + 64).unwrap_or(&[]);
        let (mut duration, mut lifetime) = (None, None);
        let mut params = Vec::new();
        if let Some((base, n)) = blob_array_at(b, params_off) {
            for i in 0..n.min(64) {
                let at = base + i * param_stride;
                let (Some(h), Some(t)) = (u32_at(b, at + p_name), u32_at(b, at + p_type)) else { break };
                let start = u8_at(b, at + p_range + range_start).unwrap_or(0) as usize;
                let size = u8_at(b, at + p_range + range_count).unwrap_or(0) as usize;
                let ty = types.get(&t).cloned().unwrap_or_else(|| hex_id(t));
                let def = vfx_parameter_default(block, &ty, start, size);
                if ty == "float"
                    && let Some(v) = def.as_ref().and_then(Value::as_f64)
                {
                    match names.get(&h).map(String::as_str) {
                        Some("duration") => duration = Some(v),
                        Some("lifetime") => lifetime = Some(v),
                        _ => {}
                    }
                }
                let mut p = Map::new();
                match names.get(&h) {
                    Some(n) => drop(p.insert("name".into(), n.as_str().into())),
                    None => drop(p.insert("hash".into(), hex_id(h).into())),
                }
                p.insert("type".into(), ty.into());
                if let Some(v) = def {
                    p.insert("default".into(), v);
                }
                params.push(Value::Object(p));
            }
        }
        put(&mut en, "length", vfx_length(kill, duration, lifetime));
        if !params.is_empty() {
            put(&mut en, "parameters", Value::Array(params));
        }
        out.push(en);
    }
    Ok(out)
}

/// Resources mods name by GUID + type in cook recipes and asset entries (models, materials, hierarchies, UI
/// textures, programs, sounds, voice-overs).
const RESOURCE_TYPES: [&str; 8] = [
    "keen::RenderModel",
    "keen::RenderMaterialResource",
    "keen::ModelHierarchyResource",
    "keen::UiTextureResource",
    "keen::ImpactProgram",
    "keen::SoundContainerResource",
    "keen::SoundResource",
    "keen::VoiceoverResource",
];

fn resources(s: &Src, textures: &HashMap<String, String>, sounds: &HashMap<String, String>) -> Result<Vec<Entry>> {
    let mut out = Vec::new();
    for t in RESOURCE_TYPES {
        let Ok(list) = s.of_type(t) else { continue };
        for (_, key) in list {
            let g = key.guid_text();
            let name = match t {
                "keen::UiTextureResource" if key.part == 0 => textures.get(&g).cloned(),
                "keen::SoundResource" => sounds.get(&g).cloned(),
                _ => None,
            };
            let mut en = e(name.filter(|n| !n.is_empty()), None, Some(g));
            put(&mut en, "type", t);
            if key.part != 0 {
                put(&mut en, "part", key.part as u64);
            }
            out.push(en);
        }
    }
    Ok(out)
}

/// The stable name of a material, from the debug name of the item that places it. The same rule as `material_name` in native/services/worldtables/merge.cpp,
/// which reads the running game: a mod's file means the same on a machine with no catalog.
fn material_name(item: &str) -> Option<String> {
    let s = item.trim_start_matches('_');
    let (prefix, rest) = if let Some(r) = s.strip_prefix("Build_TerrainMaterial_") {
        ("Terrain", r)
    } else if let Some(r) = s.strip_prefix("Block_") {
        ("", r)
    } else {
        return None;
    };
    let mut out = prefix.to_string();
    for tok in rest.split('_') {
        let tier = tok.len() >= 2 && tok.starts_with('T') && tok[1..].bytes().all(|b| b.is_ascii_digit());
        let up = tok.to_ascii_uppercase();
        if tok.is_empty() || tier || up == "CRAFTING" || up == "REWARD" {
            continue;
        }
        let mut c = tok.chars();
        if let Some(first) = c.next() {
            out.extend(first.to_uppercase());
            out.push_str(c.as_str());
        }
    }
    Some(out)
}

/// The game's materials, from its terraforming registry and the world's GUID table: what a material block names, what it starts from, and what a tool does to it.
/// An id no item places has no name of its own: it answers to `Cell<id>`.
fn materials(s: &Src, loca: &Loca) -> Result<Vec<Entry>> {
    const T: &str = "keen::ItemInfo";
    let o = Offsets::new(&s.types);
    let (dbg, name) = (o.need(T, "debugName")?, o.get(T, "name"));
    let voxel_guid = s.types.path(T, "equipment.voxelData.placeVoxelMaterial").map(|(a, _)| a as usize);
    let voxel_id = s.types.path(T, "equipment.voxelData.placeVoxelMaterialId").map(|(a, _)| a as usize);
    let (Some(voxel_guid), Some(voxel_id)) = (voxel_guid, voxel_id) else {
        return Err(Error::new(ErrorCode::Parse, "ItemInfo.equipment.voxelData"));
    };
    let mut placed: HashMap<u64, (String, Option<String>)> = HashMap::new();
    for (ix, key) in s.of_type(T)? {
        if key.part != 0 {
            continue;
        }
        let b = s.kfc.read(ix)?;
        if guid_at(&b, voxel_guid).is_none_or(|g| g.bytes().all(|c| c == b'0' || c == b'-')) {
            continue;
        }
        let Some(id) = u8_at(&b, voxel_id) else { continue };
        placed.entry(id as u64).or_insert_with(|| (blob_string_at(&b, dbg), name.and_then(|n| loca.text_ref(&b, n))));
    }
    // The world resource with a material table: a GUID per cell id.
    let guids_at = o.need("keen::VoxelWorldResource", "materialGuids")?;
    let mut guids: HashMap<u64, String> = HashMap::new();
    for (ix, key) in s.of_type("keen::VoxelWorldResource")? {
        if key.part != 0 {
            continue;
        }
        let b = s.kfc.read(ix)?;
        for id in 1..256usize {
            if let Some(g) = guid_at(&b, guids_at + id * 16)
                && !g.bytes().all(|c| c == b'0' || c == b'-')
            {
                guids.insert(id as u64, g);
            }
        }
        if !guids.is_empty() {
            break;
        }
    }
    let reg = s.one("keen::TerraformingEfficiencyRegistryResource")?;
    let v = Decoder::new(&s.types).decode("keen::TerraformingEfficiencyRegistryResource", &reg, 0)?;
    let text = |x: &Value, ty: &str| x.as_str().map(str::to_string).or_else(|| scalar(x).and_then(|n| s.enum_name(ty, n)));
    let mut out = Vec::new();
    let mut taken: HashSet<String> = HashSet::new();
    let mut add = |c: &Value, cell: u64, kind: &str, roof: Option<bool>| {
        let item = placed.get(&cell);
        let named = item.and_then(|(n, _)| material_name(n)).filter(|n| !taken.contains(n));
        let n = named.unwrap_or_else(|| format!("Cell{cell}"));
        taken.insert(n.clone());
        let mut en = e(Some(n), None, guids.get(&cell).cloned());
        put(&mut en, "kind", kind);
        put(&mut en, "cell", cell);
        put(&mut en, "displayName", opt(item.and_then(|(_, d)| d.clone())));
        put(&mut en, "item", opt(item.map(|(n, _)| n.clone())));
        put(&mut en, "hardness", opt(text(&c["hardness"], "keen::HardnessCategory")));
        put(&mut en, "health", opt(scalar(&c["healthPoints"])));
        put(&mut en, "breaks", opt(text(&c["damageSusceptibility"], "keen::PropDamageSusceptibility")));
        if roof == Some(true) {
            put(&mut en, "roof", true);
        }
        out.push(en);
    };
    for c in v["buildingConfigs"].as_array().into_iter().flatten() {
        let cell = 128 + scalar(&c["buildingMaterial"]).unwrap_or(0);
        add(c, cell, "building", Some(c["isRoofBlock"].as_bool().unwrap_or(false)));
    }
    for c in v["terrainConfigs"].as_array().into_iter().flatten() {
        add(c, scalar(&c["terrainMaterial"]).unwrap_or(0), "terrain", None);
    }
    out.sort_by_key(|e| e.get("cell").and_then(Value::as_u64));
    Ok(out)
}

fn loca_section(loca: &Loca) -> Vec<Entry> {
    loca.en
        .iter()
        .filter(|(_, t)| !t.is_empty())
        .map(|(id, t)| {
            let mut en = e(None, Some(hex_id(*id)), None);
            put(&mut en, "text", t.clone());
            en
        })
        .collect()
}

/// Identity fields data packs may not change (docs/kiln/modding/catalogs.md): by section, and anywhere the names
/// kiln.gamedata refuses to write (`identity_name` in native/services/content/gamedata_walk.cpp).
fn read_only(section: &str, path: &str) -> bool {
    let last = path.rsplit(['.', ']']).next().unwrap_or(path);
    matches!(last, "configId" | "configGuid" | "objectId" | "resourceId" | "buffTypeId" | "guid" | "programId" | "programGuid" | "itemId")
        || matches!(
            (section, path),
            ("items", "itemId" | "objectId" | "flags" | "debugName")
                | ("recipes", "recipeGuid" | "recipeId" | "workshopGuid" | "workshopId" | "requiredPropsMask" | "debugName")
                | ("loot", "itemId")
                | ("buffs", "buffTypeId")
                | ("skills", "id")
                | ("perks", "debugName")
                | ("sequences", "name" | "sourceSubSequenceId")
        )
}

/// The flattened field list of a section's root struct, as data packs write paths.
fn fields_of(t: &TypeDb, section: &str, root: &str) -> Vec<FieldInfo> {
    let mut out = Vec::new();
    if let Some(r) = t.get(root) {
        walk_fields(t, section, r, "", 0, &mut out);
    }
    out
}

/// The `templates` list: the template's own fields, then every component's with the component's short name as the
/// first segment (`LifeTime.lifeTime`). A component without fields of its own is still listed, so that its name is known.
fn template_fields(t: &TypeDb, components: &ComponentTypes) -> Vec<FieldInfo> {
    let mut out = fields_of(t, "templates", "keen::ecs::TemplateResource");
    out.retain(|f| !f.path.starts_with("components"));
    for (short, qualified) in components {
        let Some(rec) = t.get(qualified) else { continue };
        let mut list = Vec::new();
        walk_fields(t, "templates", rec, short, 0, &mut list);
        if list.is_empty() {
            list.push(FieldInfo { path: short.clone(), type_name: short_type(qualified), kind: Some("struct".into()), ..Default::default() });
        }
        out.append(&mut list);
    }
    out
}

fn short_type(n: &str) -> String {
    n.strip_prefix("keen::").unwrap_or(n).replace("keen::", "")
}

/// Struct nesting the field lists follow. What lies deeper is listed as one `struct` leaf: paths below it stay valid.
const MAX_FIELD_DEPTH: u32 = 6;

/// What a value is, for tools that give fields a type: the scalar's name (`float32`, `uint16`, `bool`), `time`, `enum`,
/// `flags`, `ref`, `guid`, `string`, `list`, `variant`, `optional`, `configs`, or `struct`.
fn value_kind(t: &TypeDb, r: &TypeRec) -> &'static str {
    match r.kind {
        decode::K_BOOL => "bool",
        decode::K_U8 => "uint8",
        decode::K_S8 => "sint8",
        decode::K_U16 => "uint16",
        decode::K_S16 => "sint16",
        decode::K_U32 => "uint32",
        decode::K_S32 => "sint32",
        decode::K_U64 => "uint64",
        decode::K_S64 => "sint64",
        decode::K_F32 => "float32",
        decode::K_F64 => "float64",
        decode::K_ENUM => "enum",
        decode::K_BM8..=decode::K_BM64 => "flags",
        decode::K_OBJECT_REF => "ref",
        decode::K_GUID => "guid",
        decode::K_BLOB_STRING => "string",
        decode::K_BLOB_ARRAY | decode::K_STATIC_ARRAY => "list",
        decode::K_BLOB_VARIANT => "variant",
        decode::K_BLOB_OPTIONAL => "optional",
        decode::K_STRUCT => {
            if r.name == "keen::Time" {
                return "time";
            }
            if r.name == "keen::impact::Configurations" {
                return "configs";
            }
            // one-field id wrappers (HashKey32 {value}) are scalars to data packs
            let fs = decode::all_fields(t, r);
            if fs.len() == 1
                && let Some(inner) = fs[0].type_name.as_deref().and_then(|n| t.get(n)).map(|x| t.resolve(x))
                && inner.kind != decode::K_STRUCT
            {
                return value_kind(t, inner);
            }
            "struct"
        }
        _ => "struct",
    }
}

/// The value names of an enum or flag type, and their numbers when those are not simply 0, 1, 2, ...
fn enum_of(t: &TypeDb, r: &TypeRec) -> (Option<Vec<String>>, Option<Vec<u64>>) {
    let values = match r.kind {
        decode::K_ENUM => Some(r.values.clone()),
        decode::K_BM8..=decode::K_BM64 => r.inner.as_deref().and_then(|n| t.enum_values(n)),
        _ => None,
    };
    let Some(values) = values else { return (None, None) };
    let numbers = (!values.iter().enumerate().all(|(i, (_, v))| *v == i as u64)).then(|| values.iter().map(|(_, v)| *v).collect());
    (Some(values.into_iter().map(|(n, _)| n).collect()), numbers)
}

fn walk_fields(t: &TypeDb, section: &str, rec: &TypeRec, prefix: &str, depth: u32, out: &mut Vec<FieldInfo>) {
    if depth > MAX_FIELD_DEPTH || out.len() > 4000 {
        return;
    }
    for f in decode::all_fields(t, rec) {
        if f.name.is_empty() {
            continue;
        }
        let path = if prefix.is_empty() { f.name.clone() } else { format!("{prefix}.{}", f.name) };
        let Some(ft) = f.type_name.as_deref().and_then(|n| t.get(n)) else { continue };
        leaf_or_walk(t, section, ft, &path, depth, out);
    }
}

fn leaf_or_walk(t: &TypeDb, section: &str, ft: &TypeRec, path: &str, depth: u32, out: &mut Vec<FieldInfo>) {
    let r = t.resolve(ft);
    let kind = value_kind(t, r);
    let leaf = || {
        let (enum_values, enum_numbers) = enum_of(t, r);
        FieldInfo {
            path: path.to_string(),
            type_name: short_type(&ft.name),
            writable: (read_only(section, path) || kind == "string").then_some(false),
            enum_values,
            enum_numbers,
            kind: Some(kind.to_string()),
        }
    };
    match r.kind {
        decode::K_STRUCT if kind == "configs" => {
            // config values go by alias below this path; `simple` and `scaled` are the lists that hold them
            out.push(leaf());
            walk_fields(t, section, r, path, depth + 1, out);
        }
        decode::K_STRUCT if kind != "struct" => out.push(leaf()),
        decode::K_STRUCT if depth >= MAX_FIELD_DEPTH || decode::all_fields(t, r).is_empty() => out.push(leaf()),
        decode::K_STRUCT => walk_fields(t, section, r, path, depth + 1, out),
        decode::K_STATIC_ARRAY => {
            let el = r.inner.as_deref().and_then(|n| t.get(n));
            match el {
                Some(el) if r.count <= 8 => {
                    for i in 0..r.count {
                        leaf_or_walk(t, section, el, &format!("{path}[{i}]"), depth + 1, out);
                    }
                }
                Some(el) => leaf_or_walk(t, section, el, &format!("{path}[*]"), depth + 1, out),
                None => out.push(leaf()),
            }
        }
        decode::K_BLOB_ARRAY => {
            out.push(leaf());
            if let Some(el) = r.inner.as_deref().and_then(|n| t.get(n)) {
                leaf_or_walk(t, section, el, &format!("{path}[*]"), depth + 1, out);
            }
        }
        // kiln.gamedata reads through an optional: its fields have the optional's own path
        decode::K_BLOB_OPTIONAL => match r.inner.as_deref().and_then(|n| t.get(n)) {
            Some(el) => leaf_or_walk(t, section, el, path, depth + 1, out),
            None => out.push(leaf()),
        },
        _ => out.push(leaf()),
    }
}

/// The section roots `fields` describes (`templates` has its own list, [`template_fields`]).
const FIELD_ROOTS: [(&str, &str); 7] = [
    ("items", "keen::ItemInfo"),
    ("recipes", "keen::RecipeInfo"),
    ("loot", "keen::LootableItemInfo"),
    ("buffs", "keen::BuffType"),
    ("perks", "keen::Perk"),
    ("skills", "keen::SkillTreeNode"),
    ("sequences", "keen::actor::ActorSubSequenceResource"),
];

/// Configs C++ reads with no program naming them (the table in native/services/content/gamedata_walk.cpp) and, for the
/// ones the game data never gives a program variable, the type of their values.
const BUILTIN_CONFIGS: [(u32, &str, Option<&str>); 6] = [
    (0xaf23_6ece, "ManaCost", Some("keen::sint32")),
    (0x727d_7f78, "StaminaCost", Some("keen::sint32")),
    (0x2de5_f1f5, "SpecialAbilityCost", Some("keen::sint32")),
    (0xc849_2400, "FuelAmount", None),
    (0xe99a_aff8, "FuelCharges", None),
    (0x220e_6b10, "UsedItem", Some("keen::ItemId")),
];

/// One config id seen in the programs.
#[derive(Default)]
struct ConfigSeen {
    names: BTreeMap<String, u32>,
    types: BTreeMap<u32, u32>,
    programs: u32,
    cpp: Option<Option<&'static str>>,
}

/// A program variable's `type` is FNV-1a of the type's impact name: `keen::float32` is `float32`,
/// `keen::ds::impact::PushBackForce` is `impact.PushBackForce`.
fn impact_types(t: &TypeDb) -> HashMap<u32, &TypeRec> {
    let mut m = HashMap::new();
    for prefix in ["keen::ds::", "keen::"] {
        for rec in t.by_name.values() {
            if let Some(rest) = rec.name.strip_prefix(prefix) {
                m.insert(fnv1a32(rest.replace("::", ".").as_bytes()), rec);
            }
        }
    }
    m
}

/// The config vocabulary: every config id the Impact programs bind, with the names they give it, the same names
/// kiln.gamedata accepts in paths (`impactValues.ManaCost`). `name` is the most used one; `shared` says that it also names
/// another id, which kiln.gamedata tells apart by what the entry holds. Plus the type of the values, for typed access.
fn configs(s: &Src) -> Result<Vec<Entry>> {
    const P: &str = "keen::ImpactProgram";
    const V: &str = "keen::ImpactVariable";
    let o = Offsets::new(&s.types);
    let layout = o.need(P, "dataLayout")?;
    let (cid, dbg) = (o.need(V, "configId")?, o.need(V, "dbgName")?);
    let ty = o.get(V, "type");
    let stride = s.types.get(V).map_or(24, |t| t.size as usize);
    let mut seen: BTreeMap<u32, ConfigSeen> = BTreeMap::new();
    for (id, name, value) in BUILTIN_CONFIGS {
        let c = seen.entry(id).or_default();
        c.names.insert(name.to_string(), 1_000_000);
        c.cpp = Some(value);
    }
    for (ix, key) in s.of_type(P)? {
        if key.part != 0 {
            continue;
        }
        let b = s.kfc.read(ix)?;
        let Some((base, n)) = blob_array_at(&b, layout) else { continue };
        let mut mine = HashSet::new();
        for i in 0..n.min(4096) {
            let at = base + i * stride;
            let id = u32_at(&b, at + cid).unwrap_or(0);
            if id == 0 {
                continue;
            }
            let c = seen.entry(id).or_default();
            if let Some(t) = ty.and_then(|o| u32_at(&b, at + o)).filter(|t| *t != 0) {
                *c.types.entry(t).or_default() += 1;
            }
            let full = blob_string_at(&b, at + dbg);
            let nm = full.split("::").next().unwrap_or("");
            if !nm.is_empty() && !nm.starts_with("impact_nodes.") {
                *c.names.entry(nm.to_string()).or_default() += 1;
            }
            mine.insert(id);
        }
        for id in mine {
            seen.entry(id).or_default().programs += 1;
        }
    }
    let mut owners: HashMap<String, HashSet<u32>> = HashMap::new();
    for (id, c) in &seen {
        for n in c.names.keys() {
            owners.entry(n.to_lowercase()).or_default().insert(*id);
        }
    }
    let by_impact_name = impact_types(&s.types);
    let mut out = Vec::with_capacity(seen.len());
    for (id, c) in &seen {
        // most used first: what authors know a config by
        let mut names: Vec<(&String, &u32)> = c.names.iter().collect();
        names.sort_by(|a, b| b.1.cmp(a.1).then_with(|| a.0.cmp(b.0)));
        let main = names.first().map(|(n, _)| (*n).clone());
        let mut en = e(main.clone(), Some(hex_id(*id)), None);
        if main.is_some_and(|n| owners[&n.to_lowercase()].len() > 1) {
            put(&mut en, "shared", true);
        }
        put(&mut en, "names", Value::Array(names.into_iter().map(|(n, _)| Value::from(n.clone())).collect()));
        put(&mut en, "programs", c.programs as u64);
        if c.cpp.is_some() {
            put(&mut en, "readBy", "C++");
        }
        let mut types: Vec<(u32, &TypeRec)> = c.types.iter().filter_map(|(h, n)| by_impact_name.get(h).map(|r| (*n, *r))).collect();
        types.sort_by(|a, b| b.0.cmp(&a.0).then_with(|| a.1.name.cmp(&b.1.name)));
        let builtin = c.cpp.flatten().and_then(|n| s.types.get(n));
        let main = types.first().map(|(_, r)| *r).or(builtin);
        if let Some(rec) = main {
            let r = s.types.resolve(rec);
            put(&mut en, "type", short_type(&rec.name));
            let kinds: HashSet<&str> = types.iter().map(|(_, t)| value_kind(&s.types, s.types.resolve(t))).collect();
            if kinds.len() > 1 {
                const NUMBERS: [&str; 7] = ["uint8", "uint16", "uint32", "sint8", "sint16", "sint32", "float32"];
                put(&mut en, "kind", if kinds.iter().all(|k| NUMBERS.contains(k)) { "number" } else { "mixed" });
            } else {
                put(&mut en, "kind", value_kind(&s.types, r));
                let (names, numbers) = enum_of(&s.types, r);
                put(&mut en, "enum", opt(names));
                put(&mut en, "enumNumbers", opt(numbers));
            }
        } else {
            put(&mut en, "kind", "number");
        }
        out.push(en);
    }
    Ok(out)
}

/// The icon names (without `.webp`) in an icons folder.
fn icon_set(dir: Option<&Path>) -> HashSet<String> {
    let Some(d) = dir else { return HashSet::new() };
    std::fs::read_dir(d)
        .map(|rd| rd.flatten().filter_map(|e| e.file_name().to_str().and_then(|n| n.strip_suffix(".webp")).map(str::to_string)).collect())
        .unwrap_or_default()
}

/// Steam's app manifest says the game is being updated (`<lib>/steamapps/common/<game>` layout only).
fn steam_updating(game_dir: &Path, side: &str) -> Option<u32> {
    let lib = game_dir.parent()?.parent()?.parent()?;
    let app = if side == "client" { crate::CLIENT_APP_ID } else { crate::SERVER_APP_ID };
    let m = crate::steam::app_manifest(lib, app).ok().flatten()?;
    m.updating.then_some(m.state_flags)
}

/// Makes `<dest>/<side>.json` from the game at `game_dir` (the side follows the game: `enshrouded.kfc` = client,
/// `enshrouded_server.kfc` = server). Skips the work when the catalog there is current, unless `opts.force`.
pub fn build(game_dir: &Path, dest: &Path, opts: &BuildOptions, p: &mut dyn Progress) -> Result<BuildInfo> {
    let t0 = std::time::Instant::now();
    crate::gameart::refuse_inside_game(dest, game_dir)?;
    let side = super::side_of(game_dir)
        .ok_or_else(|| Error::not_found(format!("{} is not an Enshrouded client or server folder", game_dir.display())))?;
    if let Some(flags) = steam_updating(game_dir, side) {
        return Err(Error::new(ErrorCode::SteamUpdating, format!("Steam is updating or validating the game (StateFlags {flags}); try again when it is done")));
    }
    let path = file_for(dest, side);
    if !opts.force
        && let Some(st) = super::status(dest, Some(game_dir))?.into_iter().find(|s| s.side == side)
        && st.current == Some(true)
        && (!opts.types_dump || super::types::dump_path_existing(dest, side).is_some())
    {
        return Ok(BuildInfo {
            side: side.into(),
            path,
            built: false,
            game: st.game.unwrap_or_default(),
            counts: st.counts,
            bytes: 0,
            seconds: 0.0,
            problems: vec![],
            types_dump: super::types::dump_path_existing(dest, side),
        });
    }
    p.report(Some(0.0), "Reading the game's data and reflection");
    let s = Src::open(game_dir)?;
    let want = |n: &str| opts.sections.as_ref().is_none_or(|l| l.iter().any(|x| x == n));
    let mut problems = Vec::new();
    let mut sections: BTreeMap<String, Vec<Entry>> = BTreeMap::new();
    let note = |what: &str, e: Error, problems: &mut Vec<String>| problems.push(format!("{what}: {}", e.message));
    let canceled = |p: &dyn Progress| if p.canceled() { Err(Error::new(ErrorCode::Canceled, "Canceled.")) } else { Ok(()) };

    let loca = match read_loca(&s) {
        Ok(l) => l,
        Err(e) => {
            // a dedicated server has no texts: not a problem there
            if side == "client" || e.code != ErrorCode::NotFound {
                note("loca", e, &mut problems);
            }
            Loca { en: BTreeMap::new(), languages: vec![] }
        }
    };
    let icons_dir = opts
        .icons_dir
        .clone()
        .or_else(|| crate::paths::DataDirs::locate().ok().map(|d| d.root.join("editor-assets").join("icons")))
        .filter(|d| d.is_dir() && side == "client");
    let icons = icon_set(icons_dir.as_deref());
    p.report(Some(0.1), "Items");
    let mut item_names = ItemNames::default();
    match items(&s, &loca, &icons, &mut item_names) {
        Ok(v) => {
            if want("items") {
                sections.insert("items".into(), v);
            }
        }
        Err(e) => note("items", e, &mut problems),
    }
    canceled(p)?;
    let mut ws_names = HashMap::new();
    match workshops(&s, &loca, &item_names) {
        Ok((v, n)) => {
            ws_names = n;
            if want("workshops") {
                sections.insert("workshops".into(), v);
            }
        }
        Err(e) => note("workshops", e, &mut problems),
    }
    p.report(Some(0.2), "Recipes, loot, drops");
    if want("recipes") || want("itemCategories") {
        match recipes(&s, &loca, &item_names, &ws_names) {
            Ok((r, c)) => {
                if want("recipes") {
                    sections.insert("recipes".into(), r);
                }
                if want("itemCategories") {
                    sections.insert("itemCategories".into(), c);
                }
            }
            Err(e) => note("recipes", e, &mut problems),
        }
    }
    if want("loot") {
        match loot(&s, &item_names) {
            Ok(v) => drop(sections.insert("loot".into(), v)),
            Err(e) => note("loot", e, &mut problems),
        }
    }
    if want("drops") {
        match drops(&s) {
            Ok(v) => drop(sections.insert("drops".into(), v)),
            Err(e) => note("drops", e, &mut problems),
        }
    }
    canceled(p)?;
    p.report(Some(0.3), "Templates");
    let mut by_arsenal = HashMap::new();
    let mut by_graph = HashMap::new();
    let mut components = ComponentTypes::new();
    let mut by_vfx = VfxUsers::new();
    let want_vfx = side == "client" && want("vfx");
    if want("templates") || want("arsenals") || want("graphs") || want_vfx {
        match templates(&s) {
            Ok((v, a, g, c, x)) => {
                by_arsenal = a;
                by_graph = g;
                components = c;
                by_vfx = x;
                if want("templates") {
                    sections.insert("templates".into(), v);
                }
            }
            Err(e) => note("templates", e, &mut problems),
        }
    }
    if want("arsenals") {
        match arsenals(&s, &by_arsenal) {
            Ok(v) => drop(sections.insert("arsenals".into(), v)),
            Err(e) => note("arsenals", e, &mut problems),
        }
    }
    if want("graphs") {
        match graphs(&s, &by_graph) {
            Ok(v) => drop(sections.insert("graphs".into(), v)),
            Err(e) => note("graphs", e, &mut problems),
        }
    }
    canceled(p)?;
    p.report(Some(0.6), "Sequences, buffs, perks, skills");
    if want("sequences") || want_vfx {
        match sequences(&s, &mut by_vfx) {
            Ok(v) => {
                if want("sequences") {
                    sections.insert("sequences".into(), v);
                }
            }
            Err(e) => note("sequences", e, &mut problems),
        }
    }
    // the client has VFX, a dedicated server none
    if want_vfx {
        match vfx(&s, &by_vfx) {
            Ok(v) => drop(sections.insert("vfx".into(), v)),
            Err(e) => note("vfx", e, &mut problems),
        }
    }
    for (name, f) in [("buffs", buffs as fn(&Src, &Loca) -> Result<Vec<Entry>>), ("perks", perks)] {
        if want(name) {
            match f(&s, &loca) {
                Ok(v) => drop(sections.insert(name.into(), v)),
                Err(e) => note(name, e, &mut problems),
            }
        }
    }
    if want("skills") {
        match skills(&s, &loca, &item_names) {
            Ok(v) => drop(sections.insert("skills".into(), v)),
            Err(e) => note("skills", e, &mut problems),
        }
    }
    if want("configs") {
        match configs(&s) {
            Ok(v) => drop(sections.insert("configs".into(), v)),
            Err(e) => note("configs", e, &mut problems),
        }
    }
    canceled(p)?;
    p.report(Some(0.7), "Sounds and resources");
    let mut sound_names = HashMap::new();
    if side == "client" && (want("sounds") || want("resources")) {
        match sounds(&s) {
            Ok((v, n)) => {
                sound_names = n;
                if want("sounds") {
                    sections.insert("sounds".into(), v);
                }
            }
            Err(e) => note("sounds", e, &mut problems),
        }
    }
    if want("resources") {
        let tex = if side == "client" { texture_names(&s) } else { HashMap::new() };
        match resources(&s, &tex, &sound_names) {
            Ok(v) => drop(sections.insert("resources".into(), v)),
            Err(e) => note("resources", e, &mut problems),
        }
    }
    if want("materials") {
        match materials(&s, &loca) {
            Ok(v) => drop(sections.insert("materials".into(), v)),
            Err(e) => note("materials", e, &mut problems),
        }
    }
    canceled(p)?;
    if super::world::SECTIONS.iter().any(|n| want(n)) {
        p.report(Some(0.8), "World things: spires, shrines, roots, obelisks, bosses");
        for (name, r) in super::world::sections(&s, &loca, &want, &mut problems) {
            match r {
                Ok(v) => drop(sections.insert(name.into(), v)),
                Err(e) => note(name, e, &mut problems),
            }
        }
    }
    if want("loca") && !loca.en.is_empty() {
        sections.insert("loca".into(), loca_section(&loca));
    }
    canceled(p)?;
    let mut fields = BTreeMap::new();
    for (sec, root) in FIELD_ROOTS {
        if sections.contains_key(sec) {
            let f = fields_of(&s.types, sec, root);
            if !f.is_empty() {
                fields.insert(sec.to_string(), f);
            }
        }
    }
    if sections.contains_key("templates") {
        let f = template_fields(&s.types, &components);
        if !f.is_empty() {
            fields.insert("templates".to_string(), f);
        }
    }
    let kfc_version = s.kfc.version.clone();
    let game = GameInfo {
        side: side.into(),
        dir: Some(game_dir.display().to_string()),
        steam_build: opts.steam_build.clone(),
        build: opts.steam_build.clone(),
        svn: Some(kfc_version.split('|').next().unwrap_or("").to_string()),
        kfc_version: Some(kfc_version),
        exe_sha256: Some(s.exe_sha256.clone()),
    };
    let mut cat = Catalog {
        format: FORMAT,
        kind: KIND.into(),
        generator: Some(format!("kiln-manage {} catalog", crate::VERSION)),
        generated: Some(crate::util::now_rfc3339()),
        recipe: Some(RECIPE),
        game: game.clone(),
        languages: loca.languages.clone(),
        icons: icons_dir.map(|d| d.display().to_string()),
        sections,
        fields,
        counts: BTreeMap::new(),
        problems: problems.clone(),
        seconds: None,
        extra: Map::new(),
    };
    cat.counts = cat.counts();
    let mut types_dump = None;
    if opts.types_dump {
        p.report(Some(0.9), "Reflection dump for Kiln.Game");
        match super::types::write_dump(&s.types, dest, side, &s.exe_sha256, &super::types::DEFAULT_COMPONENTS) {
            Ok(path) => types_dump = Some(path),
            Err(e) => {
                problems.push(format!("types: {}", e.message));
                cat.problems = problems.clone();
            }
        }
    }
    cat.seconds = Some((t0.elapsed().as_secs_f64() * 10.0).round() / 10.0);
    p.report(Some(0.95), "Writing the catalog");
    std::fs::create_dir_all(dest).map_err(crate::util::io_at(dest, "creating"))?;
    let bytes = super::write(&path, &cat)?;
    p.report(Some(1.0), "Done");
    Ok(BuildInfo {
        side: side.into(),
        path,
        built: true,
        game,
        counts: cat.counts.clone(),
        bytes,
        seconds: cat.seconds.unwrap_or(0.0),
        problems,
        types_dump,
    })
}

/// The installed games a catalog can be made from, per side: `$KILN_CATALOG_GAME` (one folder) when set, else the
/// launcher's known games, then the Steam libraries (client app 1203620, server app 2278520). Each with Steam's build
/// id when known.
pub fn game_dirs() -> Vec<(PathBuf, Option<String>)> {
    if let Some(g) = std::env::var_os("KILN_CATALOG_GAME").filter(|g| !g.is_empty()) {
        let p = PathBuf::from(g);
        let build = crate::game::inspect(&p).ok().and_then(|i| i.build_id);
        return vec![(p, build)];
    }
    let mut out: Vec<(PathBuf, Option<String>)> = Vec::new();
    let mut add = |g: crate::game::GameInstall| {
        if super::side_of(&g.path).is_some() && !out.iter().any(|(p, _)| crate::steam::same_path(p, &g.path)) {
            out.push((g.path.clone(), g.build_id.clone()));
        }
    };
    if let Ok(d) = crate::paths::DataDirs::locate() {
        for g in crate::game::known(&d).unwrap_or_default() {
            add(g);
        }
    }
    for g in crate::game::detect().unwrap_or_default() {
        add(g);
    }
    out
}

/// Makes the catalogs into [`super::catalog_dir`]: for `side` Some, that side's first installed game; None, every
/// side that has one (client and server). Current catalogs are left alone (unless `force`).
pub fn build_default(side: Option<&str>, opts: &BuildOptions, p: &mut dyn Progress) -> Result<Vec<BuildInfo>> {
    let dest = super::catalog_dir()?;
    let mut done = Vec::new();
    let games = game_dirs();
    for want in super::SIDES {
        if side.is_some_and(|s| s != want) {
            continue;
        }
        let Some((dir, build_id)) = games.iter().find(|(d, _)| super::side_of(d) == Some(want)) else { continue };
        let o = BuildOptions { steam_build: opts.steam_build.clone().or_else(|| build_id.clone()), ..opts.clone() };
        done.push(build(dir, &dest, &o, p)?);
    }
    if done.is_empty() {
        return Err(Error::not_found(match side {
            Some(s) => format!("no Enshrouded {s} installed (set KILN_CATALOG_GAME to its folder)"),
            None => "no Enshrouded client or server installed (set KILN_CATALOG_GAME to a game folder)".into(),
        }));
    }
    Ok(done)
}
