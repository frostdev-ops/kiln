//! The admin map's markers from the client's own data: the game's map markers (POIs, place names), world objects the
//! map shows as categories (chests, grapple points, lore notes, …) and their names in every game language. Rust port of
//! Kiln's MIT reference `tools/kfc/kfc/mapmarkers.py` (`kfc.py map-markers` writes the same JSON; the real-client test
//! compares them). Formats and evidence: docs/re/map.md; recipe: docs/kiln/launcher/gameart.md.
//!
//! Sources (layouts from the exe's reflection, by field name; no built-in fallback: without reflection there are no
//! markers and the map keeps a local copy):
//! - `MapMarkerRegistryResource.mapMarkers`: `MapMarkerType`s (icon `UiTexture`, label/custom flags) keyed by
//!   `MapMarkerTypeId` = FNV-1a-32 of the type GUID's bytes.
//! - `ecs::TemplateResource`: `name`, `components[]` (`BlobVariant {u32 type hash, i32 rel (from +4), u32 size}`):
//!   `ecs::MapMarker {markerId: LocaTagReference, markerType}`, `ecs::LoreReading.entryName`, `ecs::LocaTagComponent`.
//! - The world `SceneResource` (largest `entityChunkCount`, 40x40) and its `SceneEntityChunkResource` parts 0..=x*y
//!   (the last is the global chunk with every map marker). Entities `{u16 templateIndex, SceneEntityTransform}`,
//!   positions 16.16 fixed point (`X1 << 16 | X0`) in world metres. Spawn values: see [`SpawnGroups`].
//! - `LocaTagCollectionResource.languages[]`: per language a `LocaTagCollectionResourceData` content blob (sorted
//!   24-byte `LocaTagResource {id, text}`); a tag's id = FNV-1a-32 of its GUID bytes.

use crate::gameart::kfc::{ContentHash, Kfc, fnv1a32, guid_text, i32_at, u16_at, u32_at};
use crate::gameart::refl::TypeDb;
use crate::gameart::res::UiTexture;
use crate::{Error, ErrorCode, Result};
use serde_json::{Value, json};
use std::collections::{BTreeMap, BTreeSet, HashMap, HashSet};

/// `markers.json` `format`.
pub const FORMAT: u32 = 1;
pub const WORLD: u32 = 10240;
/// A category whose icon texture this game build lacks.
pub const FALLBACK_ICON: &str = "g-mapmarker_investigation.webp";
/// World-object icons the map extraction writes besides every `mapmarker_*`: (texture, tint) -> `g-<name>.webp`.
pub const CHEST_TINTS: [(&str, [u8; 3]); 3] =
    [("icon_chest-gold", [0xF2, 0xC1, 0x4E]), ("icon_chest-silver", [0xC9, 0xD1, 0xDB]), ("icon_chest-wood", [0xB0, 0x7D, 0x4A])];

const POI: &str = "Points of interest";
const SET: &str = "Settlements";
const CRA: &str = "Crafting";
const SHR: &str = "Shroud";
const ENE: &str = "Enemies";
const QUE: &str = "Quests";
const COL: &str = "Collectibles";
const LOR: &str = "Lore";
const LOO: &str = "Loot";
const SEC: &str = "Secrets";
const FIS: &str = "Fishing";
const TRA: &str = "Traversal";
const OTH: &str = "Other";
/// Group order on the map's side panel.
const GROUPS: [&str; 13] = [POI, SET, CRA, SHR, ENE, QUE, COL, LOR, LOO, SEC, FIS, TRA, OTH];

/// How a category label is translated: the category's most common marker name (or an object's singular name), the
/// game's text spelled like the English label, the game's text spelled like the given English, or English everywhere.
#[derive(Debug, Clone, Copy, PartialEq)]
enum Tr {
    Name,
    Label,
    Text(&'static str),
    English,
}

/// A map marker type by its icon texture: (icon, category id, group, label, on, place-label tier, label translation).
type MarkerCat = (&'static str, &'static str, &'static str, &'static str, bool, u8, Tr);
const MARKER_CATS: [MarkerCat; 21] = [
    ("mapmarker_flame_essence", "flame-shrine", POI, "Flame Shrines", true, 0, Tr::Name),
    ("mapmarker_tower", "ancient-spire", POI, "Ancient Spires", true, 0, Tr::English),
    ("mapmarker_tower_locked", "ancient-spire", POI, "Ancient Spires", true, 0, Tr::English),
    ("mapmarker_fastTravel", "fast-travel", POI, "Fast Travel Points", true, 0, Tr::English),
    ("mapmarker_obelisk", "ancient-obelisk", POI, "Ancient Obelisks", true, 0, Tr::Name),
    ("mapmarker_challenge_chamber", "ancient-vault", POI, "Ancient Vaults", true, 2, Tr::English),
    ("mapmarker_nighttemple", "night-sanctum", POI, "Night Sanctums", true, 0, Tr::Name),
    ("mapmarker_balefire", "balefire", POI, "Balefires", true, 2, Tr::English),
    ("mapmarker_bigDungeon", "hollow-halls", POI, "Hollow Halls", true, 0, Tr::Name),
    ("mapmarker_dungeon", "dungeon", POI, "Dungeons", true, 2, Tr::Text("Dungeon")),
    ("mapmarker_tomb", "tomb", POI, "Hidden Tombs", true, 2, Tr::English),
    ("mapmarker_cave", "cave", POI, "Caves", true, 3, Tr::Label),
    ("mapmarker_village", "village", SET, "Villages", true, 1, Tr::English),
    ("mapmarker_survivorcamp", "camp", SET, "Survivor Camps", true, 2, Tr::English),
    ("mapmarker_farm", "farm", SET, "Farms", true, 2, Tr::Text("Farm")),
    // "Mine" is the verb in German ("Abbauen")
    ("mapmarker_mine", "mine", SET, "Mines", true, 2, Tr::English),
    ("mapmarker_shroudforge", "gem-forge", CRA, "Gem Forges", true, 0, Tr::Name),
    ("mapmarker_fognest", "shroud-lair", SHR, "Shroud Lairs", true, 0, Tr::Label),
    ("mapmarker_well_plant", "elixir-well", SHR, "Elixir Wells", true, 0, Tr::Name),
    ("mapmarker_special_enemy_camp", "enemy-camp", ENE, "Enemy Camps", true, 3, Tr::English),
    ("mapmarker_investigation", "investigation", QUE, "Search Areas", false, 0, Tr::English),
];

/// A template-name rule, case-insensitive: `prefix` (a leading `=` makes it the exact name), then optionally
/// `contains` somewhere after the prefix.
type Rule = (&'static str, Option<&'static str>);
/// A world object category, first match wins: (id, group, label, singular name, on, icon, rule, label translation).
type ObjectCat = (&'static str, &'static str, &'static str, &'static str, bool, &'static str, Rule, Tr);
const OBJECT_CATS: [ObjectCat; 15] = [
    ("pearl-clam", LOO, "Pearl Clams", "Pearl Clam", false, "icon_loot", ("LootChest_Random_", Some("shell")), Tr::English),
    ("chest-gold", LOO, "Gold Chests", "Gold Chest", true, "icon_chest-gold", ("LootChest_Random_", Some("gold")), Tr::English),
    ("chest-silver", LOO, "Silver Chests", "Silver Chest", false, "icon_chest-silver", ("LootChest_Random_", Some("silver")), Tr::Name),
    ("chest-wood", LOO, "Wooden Chests", "Wooden Chest", false, "icon_chest-wood", ("LootChest_Random_", Some("wood")), Tr::Name),
    ("lore", LOR, "Lore", "Lore", false, "journal_lorePageFound", ("LoreText_", None), Tr::Label),
    ("fossil", COL, "Fossils", "Fossil", false, "collectionslot_smallFossils", ("LootPickup_Collections_Fossils", None), Tr::Label),
    ("shroud-root", SHR, "Shroud Roots", "Shroud Root", true, "mapmarker_well_plant", ("Loot_Node_Well_Core_Object_SkillPointPlant", None), Tr::Name),
    ("dragon-brazier", ENE, "Dragon Braziers", "Dragon Brazier", false, "icon_combat", ("", Some("DragonBrazier")), Tr::English),
    ("puzzle", SEC, "Puzzles", "Puzzle", false, "icon_goal", ("=Prop_Logic_Puzzle_Solved_Notifier", None), Tr::Name),
    ("secret-door", SEC, "Secret Doors", "Secret Door", false, "logicGraph_entityCategories_icons_door", ("Door_Secret", None), Tr::Label),
    ("secret-door", SEC, "Secret Doors", "Secret Door", false, "logicGraph_entityCategories_icons_door", ("Shelf_Secret", None), Tr::Label),
    ("fishing", FIS, "Fishing Spots", "Fishing Spot", false, "icon_fish", ("_FishingLocation", None), Tr::English),
    ("updraft", TRA, "Updrafts", "Updraft", false, "updraft_unlocked", ("Upstream_Zone", None), Tr::Name),
    ("jump-pad", TRA, "Jump Pads", "Jump Pad", false, "double_jump_unlocked", ("Zone_ThrowIntoAir_JumpPad", None), Tr::English),
    ("grapple", TRA, "Grapple Points", "Grapple Point", false, "spatial_hookshotTargetIcons_base", ("Hookshot_Target", None), Tr::English),
];
/// Categories whose points carry a level (the map's chest-level filter applies to every point with one).
const LEVELED: [&str; 4] = ["chest-gold", "chest-silver", "chest-wood", "pearl-clam"];
/// Templates whose markers the game never shows (name contains, lower case).
const MARKER_SKIP: [&str; 2] = ["_noui", "placeholdermapmarker"];
/// Markers left out because a world-object category places the real thing: the "Shroud Root" labels (FogWellPlant)
/// beside some of the Shroud Root plants.
const MARKER_DUPLICATE: [&str; 1] = ["fogwellplant"];
/// One category, same name within `DEDUP_M` or any name within `DEDUP_ANY_M`: one marker; world objects stacked within
/// `DEDUP_OBJECT_M` (horizontally): one.
const DEDUP_M: f64 = 25.0;
const DEDUP_ANY_M: f64 = 5.0;
const DEDUP_OBJECT_M: f64 = 1.0;
/// A marker name becomes a place label when used at most this often.
const LABEL_MAX_USES: usize = 2;

/// `keen::LanguageId` name -> (BCP 47 code, native name).
const LANGS: [(&str, &str, &str); 21] = [
    ("En_Us", "en-US", "English"),
    ("En_Gb", "en-GB", "English (UK)"),
    ("De_De", "de-DE", "Deutsch"),
    ("Fr_Fr", "fr-FR", "Français"),
    ("It_It", "it-IT", "Italiano"),
    ("Es_Es", "es-ES", "Español"),
    ("Es_Mx", "es-MX", "Español (México)"),
    ("Ru_Ru", "ru-RU", "Русский"),
    ("Pl_Pl", "pl-PL", "Polski"),
    ("Ja_Jp", "ja-JP", "日本語"),
    ("Ko_Kr", "ko-KR", "한국어"),
    ("Pt_Br", "pt-BR", "Português (Brasil)"),
    ("Pt_Pt", "pt-PT", "Português"),
    ("Th_Th", "th-TH", "ไทย"),
    ("Tr_Tr", "tr-TR", "Türkçe"),
    ("Zh_Cn", "zh-CN", "简体中文"),
    ("Zh_Tw", "zh-TW", "繁體中文"),
    ("Uk_Ua", "uk-UA", "Українська"),
    ("Nl_Nl", "nl-NL", "Nederlands"),
    ("Cs_Cz", "cs-CZ", "Čeština"),
    ("Hu_Hu", "hu-HU", "Magyar"),
];

/// What [`extract`] made.
#[derive(Debug)]
pub struct Markers {
    /// `markers.json` (English).
    pub markers: Value,
    /// `lang/<code>.json` for every other language: `{cats, points, labels}`, index-aligned with `markers`.
    pub langs: Vec<(String, Value)>,
    /// Texture names the categories use as icons (`g-<name>.webp`; `icon_chest-*` are tints of `icon_chest`).
    pub icons: Vec<String>,
    pub report: Report,
}

#[derive(Debug, Default, Clone, serde::Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Report {
    pub world: String,
    pub chunks: u32,
    pub entities: u64,
    pub placements: u64,
    pub points: u64,
    pub categories: u64,
    pub labels: u64,
    pub duplicates: u64,
    pub skipped: BTreeMap<String, u64>,
    pub templates: u64,
    pub types: u64,
    pub languages: u64,
}

fn unsupported(what: impl Into<String>) -> Error {
    Error::new(ErrorCode::Unsupported, "This game build's map marker data is not in the expected form.").with_detail(what.into())
}

/// One decimal, the same float as the reference's `floor(v * 10 + 0.5) / 10`.
pub fn r1(v: f64) -> f64 {
    (v * 10.0 + 0.5).floor() / 10.0
}

/// A loca text for a map: markup tags (`<location>…</location>`) removed, whitespace collapsed.
pub fn clean(s: &str) -> String {
    let c: Vec<char> = s.chars().collect();
    let mut out = String::with_capacity(s.len());
    let mut i = 0;
    while i < c.len() {
        if c[i] == '<'
            && let Some(j) = c[i + 1..].iter().position(|&x| x == '<' || x == '>')
            && c[i + 1 + j] == '>'
        {
            i += j + 2;
            continue;
        }
        out.push(c[i]);
        i += 1;
    }
    out.split_whitespace().collect::<Vec<_>>().join(" ")
}

/// A template-name rule (see [`Rule`]).
fn rule_matches(rule: &Rule, name: &str) -> bool {
    let n = name.to_lowercase();
    let pre = rule.0.to_lowercase();
    if let Some(exact) = pre.strip_prefix('=') {
        return n == exact;
    }
    if !n.starts_with(&pre) {
        return false;
    }
    rule.1.is_none_or(|c| n[pre.len()..].contains(&c.to_lowercase()))
}

fn object_cat(name: &str) -> Option<&'static ObjectCat> {
    OBJECT_CATS.iter().find(|c| rule_matches(&c.6, name))
}

/// FNV-1a-32 of a 16-byte reference; 0 for the null GUID.
fn tag_id(g: &[u8]) -> u32 {
    if g.iter().all(|&b| b == 0) { 0 } else { fnv1a32(g) }
}

/// `{i32 rel, u32 count}` at `o` -> (absolute start, count), checked against the buffer for `stride`-byte elements.
fn blob_array(b: &[u8], o: usize, stride: usize) -> Option<(usize, usize)> {
    let rel = i32_at(b, o)? as i64;
    let n = u32_at(b, o + 4)? as usize;
    if n == 0 {
        return Some((0, 0));
    }
    let base = usize::try_from(o as i64 + rel).ok()?;
    (base.checked_add(n.checked_mul(stride)?)? <= b.len()).then_some((base, n))
}

fn blob_string(b: &[u8], o: usize) -> Option<String> {
    let (base, n) = blob_array(b, o, 1)?;
    Some(if n == 0 { String::new() } else { String::from_utf8_lossy(&b[base..base + n]).into_owned() })
}

/// A `LocaTagCollectionResourceData` blob -> tag id -> text (the last of a repeated id wins).
pub fn loca_table(blob: &[u8], id_off: usize, text_off: usize, stride: usize) -> Result<BTreeMap<u32, String>> {
    let (base, n) = blob_array(blob, 0, stride).ok_or_else(|| unsupported("loca table header"))?;
    let mut out = BTreeMap::new();
    for i in 0..n {
        let o = base + stride * i;
        let id = u32_at(blob, o + id_off).ok_or_else(|| unsupported("loca record"))?;
        out.insert(id, blob_string(blob, o + text_off).ok_or_else(|| unsupported("loca text"))?);
    }
    Ok(out)
}

/// How an entity's spawn values sit in `spawnData.indices` (checked on all 686,371 entities of 1287369): a u8 count of
/// spawn components, a u16 first component when the count is not 0, then one index per `EntitySpawnValue` group set
/// in `spawnMask`, in bit order: the TriggerRange bits share one u16, the RandomSpawnerSetting_* bits one u8, every
/// other value its own u8. The Level index points into `spawnData.levels`.
#[derive(Debug, Clone, PartialEq)]
pub struct SpawnGroups {
    pub level_bit: u32,
    /// (index width, mask bits) of the groups below the Level bit, in bit order.
    pub groups: Vec<(usize, u16)>,
}

impl SpawnGroups {
    pub fn from_enum(values: &[(String, u64)]) -> Result<SpawnGroups> {
        let level_bit = values.iter().find(|(n, _)| n == "Level").map(|(_, v)| *v as u32).ok_or_else(|| unsupported("EntitySpawnValue has no Level"))?;
        if level_bit >= 16 {
            return Err(unsupported("EntitySpawnValue Level above bit 15"));
        }
        let mut groups: Vec<(String, usize, u16)> = Vec::new();
        for bit in 0..level_bit {
            let n = values.iter().find(|(_, v)| *v == bit as u64).map(|(n, _)| n.as_str()).ok_or_else(|| unsupported(format!("EntitySpawnValue bit {bit} has no name")))?;
            let g = if n.starts_with("TriggerRange") {
                "TriggerRange"
            } else if n.starts_with("RandomSpawnerSetting_") {
                "RandomSpawner"
            } else {
                n
            };
            match groups.iter_mut().find(|x| x.0 == g) {
                Some(x) => x.2 |= 1 << bit,
                None => groups.push((g.to_string(), if g == "TriggerRange" { 2 } else { 1 }, 1 << bit)),
            }
        }
        Ok(SpawnGroups { level_bit, groups: groups.into_iter().map(|(_, w, b)| (w, b)).collect() })
    }

    /// The Level spawn value of one entity, None without one.
    pub fn level(&self, ix: &[u8], start: usize, mask: u16, levels: &[i32]) -> Option<i32> {
        if mask >> self.level_bit & 1 == 0 || start >= ix.len() {
            return None;
        }
        let mut p = start + 1 + if ix[start] != 0 { 2 } else { 0 };
        for &(w, bits) in &self.groups {
            if mask & bits != 0 {
                p += w;
            }
        }
        levels.get(*ix.get(p)? as usize).copied()
    }
}

/// Field offsets, by name from reflection.
struct Lay {
    tex_name: usize,
    reg_keys: usize,
    reg_values: usize,
    mt_size: usize,
    mt_icon: usize,
    mt_label: usize,
    mt_custom: usize,
    t_name: usize,
    t_comps: usize,
    var_size: usize,
    mm_id: usize,
    mm_type: usize,
    lore_entry: usize,
    lt_tag: usize,
    s_count: usize,
    c_tmpl: usize,
    c_ent: usize,
    sd_entries: usize,
    sd_levels: usize,
    sd_ix: usize,
    e_size: usize,
    e_tmpl: usize,
    e_pos: [usize; 6],
    lv_size: usize,
    lv_level: usize,
    langs: usize,
    lang_size: usize,
    lang_id: usize,
    lang_hash: usize,
    loca_id: usize,
    loca_text: usize,
    loca_size: usize,
    language_names: HashMap<i64, String>,
    spawn: SpawnGroups,
}

const RESOURCE_TYPES: [&str; 5] = [
    "keen::MapMarkerRegistryResource",
    "keen::ecs::TemplateResource",
    "keen::SceneResource",
    "keen::SceneEntityChunkResource",
    "keen::LocaTagCollectionResource",
];

fn layout(t: &TypeDb, kfc: &Kfc) -> Result<Lay> {
    // the exe and the data must be the same build (the extraction already refuses mixed builds for the UI textures)
    for n in RESOURCE_TYPES {
        let exe = t.get(n).map(|r| r.layout);
        let data = kfc.bundle(fnv1a32(n.as_bytes())).map(|b| b.layout);
        if data.is_none() {
            return Err(unsupported(format!("no {n} in the data")));
        }
        if exe != data {
            return Err(Error::new(ErrorCode::Conflict, "The game's exe and data files are from different builds.")
                .with_detail(json!({"type": n, "exeLayout": exe, "kfcLayout": data})));
        }
    }
    let off = |ty: &str, path: &str| -> Result<usize> { t.path(ty, path).map(|(o, _)| o as usize).ok_or_else(|| unsupported(format!("{ty}.{path}"))) };
    let size = |ty: &str| -> Result<usize> { t.get(ty).map(|r| r.size as usize).filter(|&s| s > 0).ok_or_else(|| unsupported(format!("{ty} size"))) };
    let sd = off("keen::SceneEntityChunkResource", "spawnData")?;
    let tr = off("keen::SceneEntityChunkSpawnEntry", "transform")?;
    let mut e_pos = [0usize; 6];
    for (i, f) in ["positionX0", "positionX1", "positionY0", "positionY1", "positionZ0", "positionZ1"].iter().enumerate() {
        e_pos[i] = tr + off("keen::SceneEntityTransform", f)?;
    }
    let language_names = t
        .enum_values("keen::LanguageId")
        .ok_or_else(|| unsupported("keen::LanguageId"))?
        .into_iter()
        .map(|(n, v)| (v as i64, n))
        .collect();
    let spawn = SpawnGroups::from_enum(&t.enum_values("keen::ecs::EntitySpawnValue").ok_or_else(|| unsupported("keen::ecs::EntitySpawnValue"))?)?;
    Ok(Lay {
        tex_name: off("keen::UiTextureResource", "debugName")?,
        reg_keys: off("keen::MapMarkerRegistryResource", "mapMarkers.keys")?,
        reg_values: off("keen::MapMarkerRegistryResource", "mapMarkers.values")?,
        mt_size: size("keen::MapMarkerType")?,
        mt_icon: off("keen::MapMarkerType", "icon.image")?,
        mt_label: off("keen::MapMarkerType", "showLabel")?,
        mt_custom: off("keen::MapMarkerType", "isCustomMarkerType")?,
        t_name: off("keen::ecs::TemplateResource", "name")?,
        t_comps: off("keen::ecs::TemplateResource", "components")?,
        var_size: size("keen::BlobVariant<keen::ecs::Component>")?,
        mm_id: off("keen::ecs::MapMarker", "markerId")?,
        mm_type: off("keen::ecs::MapMarker", "markerType")?,
        lore_entry: off("keen::ecs::LoreReading", "entryName")?,
        lt_tag: off("keen::ecs::LocaTagComponent", "locaTag")?,
        s_count: off("keen::SceneResource", "entityChunkCount")?,
        c_tmpl: off("keen::SceneEntityChunkResource", "templates")?,
        c_ent: off("keen::SceneEntityChunkResource", "entities")?,
        sd_entries: sd + off("keen::SceneEntityChunkSpawnData", "entries")?,
        sd_levels: sd + off("keen::SceneEntityChunkSpawnData", "levels")?,
        sd_ix: sd + off("keen::SceneEntityChunkSpawnData", "indices")?,
        e_size: size("keen::SceneEntityChunkSpawnEntry")?,
        e_tmpl: off("keen::SceneEntityChunkSpawnEntry", "templateIndex")?,
        e_pos,
        lv_size: size("keen::SceneEntitySpawnEntryLevel")?,
        lv_level: off("keen::SceneEntitySpawnEntryLevel", "level")?,
        langs: off("keen::LocaTagCollectionResource", "languages")?,
        lang_size: size("keen::LocaTagCollectionResourceLanguage")?,
        lang_id: off("keen::LocaTagCollectionResourceLanguage", "language")?,
        lang_hash: off("keen::LocaTagCollectionResourceLanguage", "data.hash")?,
        loca_id: off("keen::LocaTagResource", "id")?,
        loca_text: off("keen::LocaTagResource", "text")?,
        loca_size: size("keen::LocaTagResource")?,
        language_names,
        spawn,
    })
}

#[derive(Debug, Default, Clone)]
struct Template {
    name: String,
    /// (name tag, type id)
    marker: Option<(u32, u32)>,
    lore: Option<u32>,
    tag: Option<u32>,
}

struct MarkerType {
    icon: Option<String>,
    label: bool,
    custom: bool,
}

struct Cat {
    id: String,
    group: &'static str,
    label: String,
    icon: String,
    on: bool,
    tier: u8,
    singular: Option<&'static str>,
    names: HashMap<Option<u32>, usize>,
    tr: Tr,
    rank: usize,
}

#[derive(Debug, Clone)]
struct Point {
    cat: String,
    x: f64,
    z: f64,
    y: f64,
    en: String,
    tag: Option<u32>,
    lv: Option<i32>,
    rank: usize,
}

#[allow(clippy::too_many_arguments)]
fn cat_of(cats: &mut Vec<Cat>, id: &str, group: &'static str, label: &str, icon: String, on: bool, tier: u8, singular: Option<&'static str>, tr: Tr, rank: usize) -> usize {
    match cats.iter().position(|c| c.id == id) {
        Some(i) => {
            cats[i].rank = cats[i].rank.min(rank);
            i
        }
        None => {
            cats.push(Cat { id: id.to_string(), group, label: label.to_string(), icon, on, tier, singular, names: HashMap::new(), tr, rank });
            cats.len() - 1
        }
    }
}

/// "player_waterSource" -> "Player Watersource" (a category for a marker type this recipe does not know).
fn title(s: &str) -> String {
    s.replace('_', " ")
        .split_whitespace()
        .map(|w| {
            let mut c = w.chars();
            c.next().map(|f| f.to_uppercase().collect::<String>() + &c.as_str().to_lowercase()).unwrap_or_default()
        })
        .collect::<Vec<_>>()
        .join(" ")
}

fn cmp_f(a: f64, b: f64) -> std::cmp::Ordering {
    a.partial_cmp(&b).unwrap_or(std::cmp::Ordering::Equal)
}

/// Every marker and world object of the client's world, names in every language. `textures`: the client's
/// `UiTextureResource`s (for icon names). Reads the game read-only; writes nothing.
pub fn extract(kfc: &Kfc, types: &TypeDb, textures: &[UiTexture]) -> Result<Markers> {
    let l = layout(types, kfc)?;
    let th = |n: &str| fnv1a32(n.as_bytes());
    let read_one = |n: &str| -> Result<Vec<u8>> {
        let r = kfc.resources_of_type(th(n));
        let (ix, _) = r.into_iter().find(|(_, k)| k.part == 0).ok_or_else(|| unsupported(format!("no {n}")))?;
        kfc.read(ix)
    };

    // ---- loca, per language
    let coll = read_one("keen::LocaTagCollectionResource")?;
    let (lb, ln) = blob_array(&coll, l.langs, l.lang_size).ok_or_else(|| unsupported("LocaTagCollectionResource.languages"))?;
    let mut tables: Vec<((String, String), BTreeMap<u32, String>)> = Vec::new();
    for i in 0..ln {
        let e = lb + l.lang_size * i;
        let id = i32_at(&coll, e + l.lang_id).ok_or_else(|| unsupported("language id"))? as i64;
        let h = ContentHash::from_bytes(coll.get(e + l.lang_hash..).unwrap_or(&[])).ok_or_else(|| unsupported("language data"))?;
        let name = l.language_names.get(&id).cloned().unwrap_or_else(|| format!("#{id}"));
        let code = LANGS
            .iter()
            .find(|x| x.0 == name)
            .map(|x| (x.1.to_string(), x.2.to_string()))
            .unwrap_or_else(|| (name.replace('_', "-"), name.clone()));
        tables.push((code, loca_table(&kfc.read_content(&h)?, l.loca_id, l.loca_text, l.loca_size)?));
    }
    let en_ix = tables.iter().position(|(c, _)| c.0 == "en-US").unwrap_or(0);
    let en: &BTreeMap<u32, String> = &tables.get(en_ix).ok_or_else(|| unsupported("no languages"))?.1;

    // ---- icons: texture GUID -> name (part 0)
    let mut tex: HashMap<String, String> = HashMap::new();
    for t in textures.iter().filter(|t| t.key.part == 0) {
        tex.entry(t.key.guid_text()).or_insert_with(|| t.name.clone());
    }
    let _ = l.tex_name; // names come from res::ui_textures (same field)

    // ---- marker types
    let reg = read_one("keen::MapMarkerRegistryResource")?;
    let (kb, kn) = blob_array(&reg, l.reg_keys, 4).ok_or_else(|| unsupported("MapMarkerMap.keys"))?;
    let (vb, vn) = blob_array(&reg, l.reg_values, l.mt_size).ok_or_else(|| unsupported("MapMarkerMap.values"))?;
    if kn != vn {
        return Err(unsupported("MapMarkerMap keys and values differ"));
    }
    let mut mtypes: HashMap<u32, MarkerType> = HashMap::new();
    for i in 0..kn {
        let v = vb + l.mt_size * i;
        let icon = &reg[v + l.mt_icon..v + l.mt_icon + 16];
        mtypes.insert(
            u32_at(&reg, kb + 4 * i).unwrap_or(0),
            MarkerType {
                icon: if tag_id(icon) != 0 { tex.get(&guid_text(icon.try_into().unwrap())).cloned() } else { None },
                label: reg[v + l.mt_label] != 0,
                custom: reg[v + l.mt_custom] != 0,
            },
        );
    }

    // ---- templates
    let (mm_h, lore_h, lt_h) = (th("keen::ecs::MapMarker"), th("keen::ecs::LoreReading"), th("keen::ecs::LocaTagComponent"));
    let mut templates: HashMap<String, Template> = HashMap::new();
    for (ix, key) in kfc.resources_of_type(th("keen::ecs::TemplateResource")) {
        let b = kfc.read(ix)?;
        let mut t = Template { name: blob_string(&b, l.t_name).unwrap_or_default(), ..Default::default() };
        if let Some((base, n)) = blob_array(&b, l.t_comps, l.var_size) {
            for i in 0..n {
                let o = base + l.var_size * i;
                let (Some(h), Some(rel)) = (u32_at(&b, o), i32_at(&b, o + 4)) else { continue };
                let Ok(at) = usize::try_from(o as i64 + 4 + rel as i64) else { continue };
                let r16 = |f: usize| b.get(at + f..at + f + 16).map(tag_id);
                if h == mm_h {
                    if let (Some(a), Some(c)) = (r16(l.mm_id), r16(l.mm_type)) {
                        t.marker = Some((a, c));
                    }
                } else if h == lore_h {
                    t.lore = r16(l.lore_entry);
                } else if h == lt_h {
                    t.tag = r16(l.lt_tag);
                }
            }
        }
        templates.insert(key.guid_text(), t);
    }

    // ---- the world scene and its chunks
    let mut world: Option<(u64, String)> = None;
    for (ix, key) in kfc.resources_of_type(th("keen::SceneResource")) {
        let b = kfc.read(ix)?;
        let cells = u32_at(&b, l.s_count).unwrap_or(0) as u64 * u32_at(&b, l.s_count + 4).unwrap_or(0) as u64;
        let cand = (cells, key.guid_text());
        if world.as_ref().is_none_or(|w| cand > *w) {
            world = Some(cand);
        }
    }
    let (cells, world) = world.ok_or_else(|| unsupported("no SceneResource"))?;
    let mut rep = Report { world: world.clone(), chunks: cells as u32 + 1, templates: templates.len() as u64, types: mtypes.len() as u64, languages: tables.len() as u64, ..Default::default() };
    // (template guid, x, y, z, level)
    let mut placements: Vec<(String, f64, f64, f64, Option<i32>)> = Vec::new();
    for (ix, key) in kfc.resources_of_type(th("keen::SceneEntityChunkResource")) {
        if key.part as u64 > cells || key.guid_text() != world {
            continue;
        }
        let b = kfc.read(ix)?;
        let bad = || unsupported(format!("SceneEntityChunkResource part {}", key.part));
        let (tb, tn) = blob_array(&b, l.c_tmpl, 16).ok_or_else(bad)?;
        let tg: Vec<String> = (0..tn).map(|i| guid_text(b[tb + 16 * i..tb + 16 * i + 16].try_into().unwrap())).collect();
        let (eb, en_) = blob_array(&b, l.c_ent, l.e_size).ok_or_else(bad)?;
        rep.entities += en_ as u64;
        let (nb, nn) = blob_array(&b, l.sd_entries, 4).ok_or_else(bad)?;
        let (lvb, lvn) = blob_array(&b, l.sd_levels, l.lv_size).ok_or_else(bad)?;
        let levels: Vec<i32> = (0..lvn).map(|i| i32_at(&b, lvb + l.lv_size * i + l.lv_level).unwrap_or(0)).collect();
        let (ib, inn) = blob_array(&b, l.sd_ix, 1).ok_or_else(bad)?;
        let ixs = if inn > 0 { &b[ib..ib + inn] } else { &[][..] };
        for i in 0..en_ {
            let e = eb + l.e_size * i;
            let ti = u16_at(&b, e + l.e_tmpl).unwrap_or(u16::MAX) as usize;
            let Some(g) = tg.get(ti) else { continue };
            let Some(t) = templates.get(g) else { continue };
            if t.marker.is_none() && object_cat(&t.name).is_none() {
                continue;
            }
            let q = |k: usize| u16_at(&b, e + l.e_pos[k]).unwrap_or(0) as u32;
            let pos = |a: usize| ((q(a + 1) << 16) | q(a)) as f64 / 65536.0;
            let lv = if i < nn {
                let (mask, start) = (u16_at(&b, nb + 4 * i).unwrap_or(0), u16_at(&b, nb + 4 * i + 2).unwrap_or(0) as usize);
                l.spawn.level(ixs, start, mask, &levels)
            } else {
                None
            };
            placements.push((g.clone(), pos(0), pos(2), pos(4), lv));
        }
    }
    rep.placements = placements.len() as u64;

    // ---- classify
    let mut cats: Vec<Cat> = Vec::new();
    let mut pts: Vec<Point> = Vec::new();
    let mut labels: Vec<(u32, f64, f64, u8)> = Vec::new();
    let marker_skip = |t: &Template| -> Option<&'static str> {
        let (_, type_id) = t.marker?;
        let n = t.name.to_lowercase();
        if MARKER_SKIP.iter().any(|k| n.contains(k)) {
            return Some("marker: template not shown in game (_noUI)");
        }
        if MARKER_DUPLICATE.iter().any(|k| n.contains(k)) {
            return Some("marker: label of a world object");
        }
        let Some(mt) = mtypes.get(&type_id) else { return Some("marker: no type") };
        if mt.custom {
            return Some("marker: custom marker type");
        }
        if mt.icon.is_none() && !mt.label {
            return Some("marker: invisible type");
        }
        None
    };
    for (g, x, y, z, lv) in &placements {
        let t = &templates[g];
        let obj = object_cat(&t.name);
        let why = if t.marker.is_some() { marker_skip(t) } else { Some("no marker") };
        if let (None, Some((name_tag, type_id))) = (why, t.marker) {
            let mt = &mtypes[&type_id];
            let tag = if en.contains_key(&name_tag) {
                Some(name_tag)
            } else {
                t.tag.filter(|x| en.contains_key(x))
            };
            let Some(icon) = &mt.icon else {
                match tag {
                    Some(tag) => labels.push((tag, *x, *z, 1)),
                    None => *rep.skipped.entry("marker: label without a name".into()).or_default() += 1,
                }
                continue;
            };
            let rank = MARKER_CATS.iter().position(|c| c.0 == icon).unwrap_or(MARKER_CATS.len() + OBJECT_CATS.len());
            let (id, group, label, on, tier, tr): (String, &'static str, String, bool, u8, Tr) = match MARKER_CATS.iter().find(|c| c.0 == icon) {
                Some(c) => (c.1.to_string(), c.2, c.3.to_string(), c.4, c.5, c.6),
                None => {
                    // a marker type this recipe does not know yet: its own category
                    let short = icon.strip_prefix("mapmarker_").unwrap_or(icon);
                    (short.to_lowercase(), OTH, title(short), true, 0, Tr::Name)
                }
            };
            let ci = cat_of(&mut cats, &id, group, &label, format!("g-{icon}.webp"), on, tier, None, tr, rank);
            *cats[ci].names.entry(tag).or_default() += 1;
            let en_name = tag.map(|t| clean(en.get(&t).map(String::as_str).unwrap_or(""))).unwrap_or_default();
            pts.push(Point { cat: id, x: *x, z: *z, y: *y, en: en_name, tag, lv: None, rank });
        } else if let Some(c) = obj {
            let rank = MARKER_CATS.len() + OBJECT_CATS.iter().position(|o| o.0 == c.0).unwrap();
            cat_of(&mut cats, c.0, c.1, c.2, format!("g-{}.webp", c.5), c.4, 0, Some(c.3), c.7, rank);
            let tag = if c.0 == "lore" { t.lore.filter(|x| en.contains_key(x)) } else { None };
            let en_name = tag.map(|t| clean(en.get(&t).map(String::as_str).unwrap_or(""))).unwrap_or_default();
            pts.push(Point { cat: c.0.to_string(), x: *x, z: *z, y: *y, en: en_name, tag, lv: if LEVELED.contains(&c.0) { *lv } else { None }, rank });
        } else if let Some(why) = why {
            *rep.skipped.entry(why.into()).or_default() += 1;
        }
    }

    // ---- order categories, dedupe, place labels
    let group_ix = |g: &str| GROUPS.iter().position(|x| *x == g).unwrap_or(GROUPS.len());
    cats.sort_by(|a, b| (group_ix(a.group), a.rank, &a.id).cmp(&(group_ix(b.group), b.rank, &b.id)));
    let cidx: HashMap<&str, usize> = cats.iter().enumerate().map(|(i, c)| (c.id.as_str(), i)).collect();
    pts.sort_by(|a, b| {
        (cidx[a.cat.as_str()], a.rank)
            .cmp(&(cidx[b.cat.as_str()], b.rank))
            .then(cmp_f(r1(a.x), r1(b.x)))
            .then(cmp_f(r1(a.z), r1(b.z)))
            .then(cmp_f(r1(a.y), r1(b.y)))
            .then(a.en.cmp(&b.en))
    });
    let mut keep: Vec<Point> = Vec::with_capacity(pts.len());
    let mut grid: HashMap<(usize, i64, i64), Vec<usize>> = HashMap::new();
    for p in pts {
        let ci = cidx[p.cat.as_str()];
        let (gx, gz) = ((p.x / DEDUP_M).floor() as i64, (p.z / DEDUP_M).floor() as i64);
        let objects = cats[ci].singular.is_some();
        let mut dup = false;
        'n: for dx in -1..=1 {
            for dz in -1..=1 {
                for &qi in grid.get(&(ci, gx + dx, gz + dz)).map(Vec::as_slice).unwrap_or(&[]) {
                    let q = &keep[qi];
                    let lim = if objects {
                        DEDUP_OBJECT_M
                    } else if q.en == p.en {
                        DEDUP_M
                    } else {
                        DEDUP_ANY_M
                    };
                    if (q.x - p.x).hypot(q.z - p.z) < lim {
                        dup = true;
                        break 'n;
                    }
                }
            }
        }
        if dup {
            rep.duplicates += 1;
            continue;
        }
        grid.entry((ci, gx, gz)).or_default().push(keep.len());
        keep.push(p);
    }
    let mut pts = keep;
    pts.sort_by(|a, b| {
        cidx[a.cat.as_str()]
            .cmp(&cidx[b.cat.as_str()])
            .then(cmp_f(r1(a.x), r1(b.x)))
            .then(cmp_f(r1(a.z), r1(b.z)))
            .then(cmp_f(r1(a.y), r1(b.y)))
            .then(a.en.cmp(&b.en))
    });
    let mut uses: HashMap<&str, usize> = HashMap::new();
    for p in pts.iter().filter(|p| !p.en.is_empty()) {
        *uses.entry(p.en.as_str()).or_default() += 1;
    }
    for p in &pts {
        let tier = cats[cidx[p.cat.as_str()]].tier;
        if tier > 0 && !p.en.is_empty() && uses[p.en.as_str()] <= LABEL_MAX_USES
            && let Some(tag) = p.tag
        {
            labels.push((tag, p.x, p.z, tier));
        }
    }
    let label_key = |t: u32| clean(en.get(&t).map(String::as_str).unwrap_or("")).to_lowercase();
    labels.sort_by(|a, b| label_key(a.0).cmp(&label_key(b.0)).then(cmp_f(a.1, b.1)).then(cmp_f(a.2, b.2)));
    let mut dl: Vec<(u32, f64, f64, u8)> = Vec::new();
    for lb in labels {
        if !dl.iter().any(|m| m.0 == lb.0 && (m.1 - lb.1).hypot(m.2 - lb.2) < DEDUP_M) {
            dl.push(lb);
        }
    }
    let labels = dl;

    // ---- texts per language
    // English text (cleaned, lower case) -> the lowest tag id spelling it
    let mut en_index: HashMap<String, u32> = HashMap::new();
    for (k, v) in en {
        en_index.entry(clean(v).to_lowercase()).or_insert(*k);
    }
    let en_lookup = |s: &str| -> Option<u32> { en_index.get(&s.to_lowercase()).copied() };
    let singular_tags: HashMap<&str, Option<u32>> = cats.iter().filter_map(|c| c.singular.map(|s| (c.id.as_str(), en_lookup(s)))).collect();
    let top_tags: HashMap<&str, Option<u32>> = cats
        .iter()
        .map(|c| {
            let mut v: Vec<(&Option<u32>, &usize)> = c.names.iter().collect();
            v.sort_by(|a, b| b.1.cmp(a.1).then(a.0.unwrap_or(0).cmp(&b.0.unwrap_or(0))));
            (c.id.as_str(), v.into_iter().find_map(|(t, _)| t.filter(|&x| x != 0)))
        })
        .collect();
    let cat_tags: HashMap<&str, Option<u32>> = cats
        .iter()
        .map(|c| {
            let t = match c.tr {
                Tr::Name => {
                    if c.singular.is_some() {
                        singular_tags.get(c.id.as_str()).copied().flatten()
                    } else {
                        top_tags.get(c.id.as_str()).copied().flatten()
                    }
                }
                Tr::Label => en_lookup(&c.label),
                Tr::Text(s) => en_lookup(s),
                Tr::English => None,
            };
            (c.id.as_str(), t)
        })
        .collect();
    let text = |table: &BTreeMap<u32, String>, tag: Option<u32>, fallback: &str| -> String {
        match tag.and_then(|t| table.get(&t)).filter(|s| !s.is_empty()) {
            Some(s) => clean(s),
            None => fallback.to_string(),
        }
    };
    let build = |table: &BTreeMap<u32, String>, english: bool| -> (Vec<String>, Vec<String>, Vec<String>) {
        let cat_names = cats.iter().map(|c| if english { c.label.clone() } else { text(table, cat_tags[c.id.as_str()], &c.label) }).collect();
        let pnames = pts
            .iter()
            .map(|p| {
                let c = &cats[cidx[p.cat.as_str()]];
                let id = c.id.as_str();
                let fallback = match c.singular {
                    Some(s) => text(table, singular_tags.get(id).copied().flatten(), s),
                    None => text(table, top_tags.get(id).copied().flatten(), &text(table, cat_tags[id], &c.label)),
                };
                if p.tag.is_some() { text(table, p.tag, &fallback) } else { fallback }
            })
            .collect();
        let lnames = labels.iter().map(|lb| text(table, Some(lb.0), "")).collect();
        (cat_names, pnames, lnames)
    };
    let (en_cats, en_points, en_labels) = build(en, true);
    let mut counts: HashMap<&str, u64> = HashMap::new();
    for p in &pts {
        *counts.entry(p.cat.as_str()).or_default() += 1;
    }
    let have: HashSet<&str> = tex.values().map(String::as_str).collect();
    let mut cat_json = Vec::new();
    let mut icons = BTreeSet::new();
    for (i, c) in cats.iter().enumerate() {
        let name = &c.icon[2..c.icon.len() - 5];
        let base = if CHEST_TINTS.iter().any(|t| t.0 == name) { "icon_chest" } else { name };
        let icon = if have.contains(base) { c.icon.clone() } else { FALLBACK_ICON.to_string() };
        icons.insert(icon[2..icon.len() - 5].to_string());
        cat_json.push(json!({"id": c.id, "group": c.group, "label": en_cats[i], "icon": icon, "on": c.on, "count": counts.get(c.id.as_str()).copied().unwrap_or(0)}));
    }
    let markers = json!({
        "format": FORMAT,
        "source": "game",
        "build": kfc.version.split('|').next().unwrap_or(""),
        "world": WORLD,
        "lang": tables[en_ix].0.0,
        "langs": tables.iter().map(|(c, _)| json!({"code": c.0, "name": c.1})).collect::<Vec<_>>(),
        "cats": cat_json,
        "points": pts.iter().enumerate().map(|(i, p)| json!([cidx[p.cat.as_str()], r1(p.x), r1(p.z), r1(p.y), en_points[i], "", p.lv.unwrap_or(0), ""])).collect::<Vec<_>>(),
        "labels": labels.iter().enumerate().map(|(i, lb)| json!([en_labels[i], r1(lb.1), r1(lb.2), lb.3])).collect::<Vec<_>>(),
    });
    let langs = tables
        .iter()
        .enumerate()
        .filter(|(i, _)| *i != en_ix)
        .map(|(_, (code, table))| {
            let (c, p, lb) = build(table, false);
            (code.0.clone(), json!({"cats": c, "points": p, "labels": lb}))
        })
        .collect();
    rep.points = pts.len() as u64;
    rep.categories = cats.len() as u64;
    rep.labels = labels.len() as u64;
    Ok(Markers { markers, langs, icons: icons.into_iter().collect(), report: rep })
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn clean_removes_markup() {
        assert_eq!(clean("<location>Thornhold</location>"), "Thornhold");
        assert_eq!(clean("  a <b>bold</b>\n  line "), "a bold line");
        assert_eq!(clean("a<b<c>d"), "a<bd");
        assert_eq!(clean("1 < 2 and 3 > 2"), "1 2");
        assert_eq!(clean("x <unclosed"), "x <unclosed");
    }

    #[test]
    fn rounding_is_half_up() {
        assert_eq!(r1(0.25), 0.3);
        assert_eq!(r1(3412.66), 3412.7);
        assert_eq!(r1(1333.0), 1333.0);
        assert_eq!(r1(-0.04), 0.0);
    }

    #[test]
    fn template_rules() {
        let cat = |n: &str| object_cat(n).map(|c| c.0);
        assert_eq!(cat("LootChest_Random_gold"), Some("chest-gold"));
        assert_eq!(cat("LootChest_Random_HollowHall_Sarcophagus_Silver"), Some("chest-silver"));
        assert_eq!(cat("LootChest_Random_Cairn_Sarcophagus_Wood"), Some("chest-wood"));
        assert_eq!(cat("LootChest_Random_shell"), Some("pearl-clam"));
        assert_eq!(cat("Prop_Cold_Heights_DragonBrazier_01_On_BossActivation"), Some("dragon-brazier"));
        assert_eq!(cat("Prop_Logic_Puzzle_Solved_Notifier"), Some("puzzle"));
        assert_eq!(cat("Prop_Logic_Puzzle_Solved_Notifier_2"), None);
        assert_eq!(cat("Shelf_Secret_01"), Some("secret-door"));
        assert_eq!(cat("hookshot_target01_front"), Some("grapple"));
        assert_eq!(cat("Prop_Furniture_Book_Pile_01_A_Lootpickup"), None);
        assert_eq!(cat("LootChest_Random_"), None);
    }

    #[test]
    fn spawn_indices() {
        // the enum of 1287369
        let names = [
            "TriggerRange", "TriggerRange2", "TriggerRange3", "EnemySettings", "RandomSpawnerSetting_AllowUpScaling",
            "RandomSpawnerSetting_AllowDownScaling", "RandomSpawnerSetting_MinSpawnerAmount", "EnemyTestFlags", "Level",
            "EnemyMarkerTag", "TeleporterId",
        ];
        let values: Vec<(String, u64)> = names.iter().enumerate().map(|(i, n)| (n.to_string(), i as u64)).collect();
        let g = SpawnGroups::from_enum(&values).unwrap();
        assert_eq!(g.level_bit, 8);
        assert_eq!(g.groups, vec![(2, 0b111), (1, 0b1000), (1, 0b111_0000), (1, 0b1000_0000)]);
        let levels = [3, 43, 20];
        // [Level]: count 0, level index 1
        assert_eq!(g.level(&[0, 1], 0, 0x100, &levels), Some(43));
        // [TriggerRange, Level], 2 components from 0: count, u16 first, u16 trigger, u8 level
        assert_eq!(g.level(&[2, 0, 0, 1, 0, 2], 0, 0x101, &levels), Some(20));
        // TriggerRange x3 + RandomSpawner x3 + Level at offset 3 of a longer buffer
        assert_eq!(g.level(&[9, 9, 9, 1, 0x2c, 0, 1, 0, 0, 0], 3, 0x177, &levels), Some(3));
        // no Level bit, an index past the levels, a start past the buffer
        assert_eq!(g.level(&[0, 0, 0], 0, 0x7, &levels), None);
        assert_eq!(g.level(&[0, 7], 0, 0x100, &levels), None);
        assert_eq!(g.level(&[0, 1], 5, 0x100, &levels), None);
        // an unnamed bit below Level cannot be walked
        let mut gap = values.clone();
        gap.remove(3);
        assert!(SpawnGroups::from_enum(&gap).is_err());
    }

    #[test]
    fn loca_table_parses_records() {
        // LocaTagCollectionResourceData {tags: BlobArray<LocaTagResource 24 B>} + the strings after it
        let mut b = vec![0u8; 8 + 2 * 24];
        b[0..4].copy_from_slice(&8i32.to_le_bytes());
        b[4..8].copy_from_slice(&2u32.to_le_bytes());
        let mut strings = Vec::new();
        for (i, (id, s)) in [(7u32, "Thornhold"), (42, "<b>Cave</b>")].iter().enumerate() {
            let o = 8 + 24 * i;
            b[o..o + 4].copy_from_slice(&id.to_le_bytes());
            strings.push((o + 4, s.as_bytes().to_vec()));
        }
        for (field, s) in strings {
            let at = b.len();
            b.extend_from_slice(&s);
            b[field..field + 4].copy_from_slice(&((at - field) as i32).to_le_bytes());
            b[field + 4..field + 8].copy_from_slice(&(s.len() as u32).to_le_bytes());
        }
        let t = loca_table(&b, 0, 4, 24).unwrap();
        assert_eq!(t.get(&7).map(String::as_str), Some("Thornhold"));
        assert_eq!(t.get(&42).map(|s| clean(s)), Some("Cave".into()));
        assert!(loca_table(&b[..20], 0, 4, 24).is_err());
    }

    /// The installed client (read-only): `KILN_GAMEART_GAME=<client> KILN_GAMEART_OUT=<scratch> [KILN_MARKERS_REF=<dir of
    /// kfc.py map-markers>] cargo test -p kiln-manage --features gameart --release --lib -- --ignored real_client_markers
    /// --nocapture`. With the reference: the Rust output must equal it (same JSON values).
    #[test]
    #[ignore]
    fn real_client_markers() {
        let game = std::path::PathBuf::from(std::env::var_os("KILN_GAMEART_GAME").expect("KILN_GAMEART_GAME"));
        let out = std::path::PathBuf::from(std::env::var_os("KILN_GAMEART_OUT").expect("KILN_GAMEART_OUT"));
        let t0 = std::time::Instant::now();
        let g = crate::gameart::GameData::open(&game).unwrap();
        let textures = crate::gameart::res::ui_textures(&g.kfc, &g.layouts).unwrap();
        let types = TypeDb::from_exe(&game.join("enshrouded.exe")).unwrap();
        let t1 = t0.elapsed();
        let m = extract(&g.kfc, &types, &textures).unwrap();
        println!("open {:.2?}, markers {:.2?}", t1, t0.elapsed() - t1);
        println!("{}", serde_json::to_string_pretty(&m.report).unwrap());
        std::fs::create_dir_all(out.join("lang")).unwrap();
        std::fs::write(out.join("markers.json"), serde_json::to_vec(&m.markers).unwrap()).unwrap();
        for (code, v) in &m.langs {
            std::fs::write(out.join("lang").join(format!("{code}.json")), serde_json::to_vec(v).unwrap()).unwrap();
        }
        println!("icons {:?}", m.icons);
        if let Some(r) = std::env::var_os("KILN_MARKERS_REF").map(std::path::PathBuf::from) {
            let read = |p: std::path::PathBuf| serde_json::from_slice::<Value>(&std::fs::read(&p).unwrap()).unwrap();
            let want = read(r.join("markers.json"));
            for k in ["cats", "points", "labels", "langs", "lang", "build", "world", "format"] {
                assert_eq!(m.markers[k], want[k], "markers.json {k}");
            }
            for (code, v) in &m.langs {
                assert_eq!(*v, read(r.join("lang").join(format!("{code}.json"))), "lang/{code}.json");
            }
            println!("identical to the reference in {}", r.display());
        }
    }

    #[test]
    fn blob_arrays_are_bounds_checked() {
        let b = [8u8, 0, 0, 0, 2, 0, 0, 0, 1, 2, 3, 4];
        assert_eq!(blob_array(&b, 0, 2), Some((8, 2)));
        assert_eq!(blob_array(&b, 0, 4), None);
        assert_eq!(blob_array(&[0xF0, 0xFF, 0xFF, 0xFF, 1, 0, 0, 0], 0, 1), None);
        assert_eq!(tag_id(&[0; 16]), 0);
        assert_eq!(tag_id(&[1; 16]), fnv1a32(&[1; 16]));
    }
}
