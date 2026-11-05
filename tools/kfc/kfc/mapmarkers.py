# SPDX-License-Identifier: MIT
"""The admin map's markers from the game's own data (reference implementation of crates/kiln-manage/src/gameart/map/
markers.rs; format notes in docs/re/map.md).

Sources, all read offline from a client's .kfc and exe (layouts from the exe's reflection, by field name):

* `MapMarkerRegistryResource`: the marker types (`MapMarkerType`: icon UiTexture, sorting category, fast travel, label
  flags), keyed by `MapMarkerTypeId` = FNV-1a-32 of the type's GUID bytes.
* `ecs::TemplateResource`: templates with an `ecs::MapMarker` component (marker type, name tag), `LoreReading` (lore
  note titles), plus the template names the world-object rules match.
* `SceneResource`: the world scene is the one with the largest `entityChunkCount` (40x40 in 1287369);
  `SceneEntityChunkResource` parts 0..x*y-1 are its chunks, part x*y its global chunk (where every map marker sits).
  Entities: `{u16 templateIndex, SceneEntityTransform}`, positions 16.16 fixed point in world metres.
* Spawn values per entity (`spawnData.entries[i] = {spawnMask, spawnIndexStart}` into `spawnData.indices`): u8 count of
  spawn components, u16 first component (when count > 0), then one index per mask group in bit order: TriggerRange
  (bits 0-2, u16), EnemySettings u8, RandomSpawner (bits 4-6) u8, EnemyTestFlags u8, Level u8 (into `levels`),
  EnemyMarkerTag u8, TeleporterId u8, TeleportTargetId u8, JumpParameters u8, Npc/AnimalTestFlags u8.
* `LocaTagCollectionResource.languages[]`: one `LocaTagCollectionResourceData` content blob per language (24-byte
  `LocaTagResource {id, text, arguments, genericArguments}` sorted by id); a tag's id = FNV-1a-32 of its GUID bytes.

Output (the launcher's map format, launcher/src/admin/map/engine.ts MarkerData): `markers.json` {format, source,
build, world, lang, langs, cats, points, labels} with English texts and `lang/<code>.json` {cats, points, labels} with
every other language's texts, index-aligned.
"""
import math
import re
import struct
from collections import Counter, OrderedDict

from .archive import ContentHash
from .blob import Decoder
from .util import fnv1a32, guid_bytes, guid_text

FORMAT = 1
WORLD = 10240

# group order on the map's side panel
GROUPS = ["Points of interest", "Settlements", "Crafting", "Shroud", "Enemies", "Quests", "Collectibles", "Lore",
          "Loot", "Secrets", "Fishing", "Traversal", "Other"]
POI, SET, CRA, SHR, ENE, QUE, COL, LOR, LOO, SEC, FIS, TRA, OTH = GROUPS

# map marker types by their icon texture: (category id, group, label, on, place-label tier or 0, label translation)
# (translation: "name" = the category's most common marker name in that language, "label" = the game's text spelled
# like the English label, "text:<English>" = the game's text spelled like that, None = English in every language;
# each checked by hand against the German and Japanese tables of 1287369)
MARKER_CATS = {
    "mapmarker_flame_essence": ("flame-shrine", POI, "Flame Shrines", True, 0, "name"),
    "mapmarker_tower": ("ancient-spire", POI, "Ancient Spires", True, 0, None),
    "mapmarker_tower_locked": ("ancient-spire", POI, "Ancient Spires", True, 0, None),
    "mapmarker_fastTravel": ("fast-travel", POI, "Fast Travel Points", True, 0, None),
    "mapmarker_obelisk": ("ancient-obelisk", POI, "Ancient Obelisks", True, 0, "name"),
    "mapmarker_challenge_chamber": ("ancient-vault", POI, "Ancient Vaults", True, 2, None),
    "mapmarker_nighttemple": ("night-sanctum", POI, "Night Sanctums", True, 0, "name"),
    "mapmarker_balefire": ("balefire", POI, "Balefires", True, 2, None),
    "mapmarker_bigDungeon": ("hollow-halls", POI, "Hollow Halls", True, 0, "name"),
    "mapmarker_dungeon": ("dungeon", POI, "Dungeons", True, 2, "text:Dungeon"),
    "mapmarker_tomb": ("tomb", POI, "Hidden Tombs", True, 2, None),
    "mapmarker_cave": ("cave", POI, "Caves", True, 3, "label"),
    "mapmarker_village": ("village", SET, "Villages", True, 1, None),
    "mapmarker_survivorcamp": ("camp", SET, "Survivor Camps", True, 2, None),
    "mapmarker_farm": ("farm", SET, "Farms", True, 2, "text:Farm"),
    "mapmarker_mine": ("mine", SET, "Mines", True, 2, None),  # "Mine" is the verb ("Abbauen") in German
    "mapmarker_shroudforge": ("gem-forge", CRA, "Gem Forges", True, 0, "name"),
    "mapmarker_fognest": ("shroud-lair", SHR, "Shroud Lairs", True, 0, "label"),
    "mapmarker_well_plant": ("elixir-well", SHR, "Elixir Wells", True, 0, "name"),
    "mapmarker_special_enemy_camp": ("enemy-camp", ENE, "Enemy Camps", True, 3, None),
    "mapmarker_investigation": ("investigation", QUE, "Search Areas", False, 0, None),
}
MARKER_RANK = {icon: i for i, icon in enumerate(MARKER_CATS)}
# markers left out because a world-object category places the real thing: the game's "Shroud Root" labels
# (FogWellPlant, 55) sit beside some of the 78 Shroud Root plants (Loot_Node_Well_Core_Object_SkillPointPlant_*)
MARKER_DUPLICATE = ("fogwellplant",)
# templates whose markers the game never shows (name contains, case-insensitive)
MARKER_SKIP = ("_noui", "placeholdermapmarker")

# world objects by template name, first match wins: (category id, group, label, singular name, on, icon, regex,
# label translation as above; their point names translate through the singular name when the game spells it so)
OBJECT_CATS = [
    ("pearl-clam", LOO, "Pearl Clams", "Pearl Clam", False, "icon_loot", ("LootChest_Random_", "shell"), None),
    ("chest-gold", LOO, "Gold Chests", "Gold Chest", True, "icon_chest-gold", ("LootChest_Random_", "gold"), None),
    ("chest-silver", LOO, "Silver Chests", "Silver Chest", False, "icon_chest-silver", ("LootChest_Random_", "silver"), "name"),
    ("chest-wood", LOO, "Wooden Chests", "Wooden Chest", False, "icon_chest-wood", ("LootChest_Random_", "wood"), "name"),
    ("lore", LOR, "Lore", "Lore", False, "journal_lorePageFound", ("LoreText_",), "label"),
    ("fossil", COL, "Fossils", "Fossil", False, "collectionslot_smallFossils", ("LootPickup_Collections_Fossils",), "label"),
    ("shroud-root", SHR, "Shroud Roots", "Shroud Root", True, "mapmarker_well_plant", ("Loot_Node_Well_Core_Object_SkillPointPlant",), "name"),
    ("dragon-brazier", ENE, "Dragon Braziers", "Dragon Brazier", False, "icon_combat", ("", "DragonBrazier"), None),
    ("puzzle", SEC, "Puzzles", "Puzzle", False, "icon_goal", ("=Prop_Logic_Puzzle_Solved_Notifier",), "name"),
    ("secret-door", SEC, "Secret Doors", "Secret Door", False, "logicGraph_entityCategories_icons_door", ("Door_Secret",), "label"),
    ("secret-door", SEC, "Secret Doors", "Secret Door", False, "logicGraph_entityCategories_icons_door", ("Shelf_Secret",), "label"),
    ("fishing", FIS, "Fishing Spots", "Fishing Spot", False, "icon_fish", ("_FishingLocation",), None),
    ("updraft", TRA, "Updrafts", "Updraft", False, "updraft_unlocked", ("Upstream_Zone",), "name"),
    ("jump-pad", TRA, "Jump Pads", "Jump Pad", False, "double_jump_unlocked", ("Zone_ThrowIntoAir_JumpPad",), None),
    ("grapple", TRA, "Grapple Points", "Grapple Point", False, "spatial_hookshotTargetIcons_base", ("Hookshot_Target",), None),
]


def rule_matches(rule, name):
    """(prefix[, contains]) case-insensitive; a prefix "=X" is an exact name; contains is searched after the prefix."""
    n = name.lower()
    pre = rule[0].lower()
    if pre.startswith("="):
        return n == pre[1:]
    if not n.startswith(pre):
        return False
    return len(rule) < 2 or rule[1].lower() in n[len(pre):]


def object_cat(name):
    return next((c for c in OBJECT_CATS if rule_matches(c[6], name)), None)
OBJECT_RANK = {}
for _i, _c in enumerate(OBJECT_CATS):
    OBJECT_RANK.setdefault(_c[0], len(MARKER_CATS) + _i)
# categories whose points carry a level (the map's chest-level filter applies to every point with one)
LEVELED = {"chest-gold", "chest-silver", "chest-wood", "pearl-clam"}
# extra textures the categories use as icons (besides every mapmarker_*), and the tinted chest variants
OBJECT_ICONS = sorted({c[5] for c in OBJECT_CATS if not c[5].startswith(("icon_chest-", "mapmarker_"))} | {"icon_chest"})
FALLBACK_ICON = "g-mapmarker_investigation.webp"
CHEST_TINTS = {"icon_chest-gold": (0xF2, 0xC1, 0x4E), "icon_chest-silver": (0xC9, 0xD1, 0xDB), "icon_chest-wood": (0xB0, 0x7D, 0x4A)}
# one category, same name within DEDUP_M or any name within DEDUP_ANY_M: one marker (the game places some twice, e.g.
# the locked and the unlocked spire); world objects stacked within DEDUP_OBJECT_M (horizontally): one
DEDUP_M = 25.0
DEDUP_ANY_M = 5.0
DEDUP_OBJECT_M = 1.0
# a marker name becomes a place label when it is used at most this often (generic names like "Cave Passage" are not)
LABEL_MAX_USES = 2

# keen::LanguageId name -> (BCP 47 code, native name)
LANGS = {
    "En_Us": ("en-US", "English"), "En_Gb": ("en-GB", "English (UK)"), "De_De": ("de-DE", "Deutsch"),
    "Fr_Fr": ("fr-FR", "Français"), "It_It": ("it-IT", "Italiano"), "Es_Es": ("es-ES", "Español"),
    "Es_Mx": ("es-MX", "Español (México)"), "Ru_Ru": ("ru-RU", "Русский"), "Pl_Pl": ("pl-PL", "Polski"),
    "Ja_Jp": ("ja-JP", "日本語"), "Ko_Kr": ("ko-KR", "한국어"), "Pt_Br": ("pt-BR", "Português (Brasil)"),
    "Pt_Pt": ("pt-PT", "Português"), "Th_Th": ("th-TH", "ไทย"), "Tr_Tr": ("tr-TR", "Türkçe"),
    "Zh_Cn": ("zh-CN", "简体中文"), "Zh_Tw": ("zh-TW", "繁體中文"), "Uk_Ua": ("uk-UA", "Українська"),
    "Nl_Nl": ("nl-NL", "Nederlands"), "Cs_Cz": ("cs-CZ", "Čeština"), "Hu_Hu": ("hu-HU", "Magyar"),
}

TAG_RX = re.compile(r"<[^<>]*>")


def r1(v):
    """One decimal, the same float as the Rust port's `((v * 10.0) + 0.5).floor() / 10.0`."""
    return math.floor(v * 10.0 + 0.5) / 10.0


def clean(s):
    """A loca text for a map: markup tags removed, whitespace collapsed."""
    return " ".join(TAG_RX.sub("", s or "").split())


def tag_id(guid):
    """A LocaTagReference / ObjectReference GUID (text) -> its 32-bit id (FNV-1a over the GUID bytes); 0 for null."""
    if not guid or guid.startswith("00000000-0000-0000-0000-000000000000"):
        return 0
    return fnv1a32(guid_bytes(guid))


class Offs:
    """Field offsets by dotted path, from reflection."""

    def __init__(self, types):
        self.t = types

    def __call__(self, type_name, path):
        t = self.t.get(type_name)
        off = 0
        for seg in path.split("."):
            t = self.t.resolve(t)
            f = next((f for f in self.t.all_fields(t) if f["name"] == seg), None)
            if f is None:
                raise KeyError(f"{type_name}.{path}: no field {seg}")
            off += f["offset"]
            t = self.t.get(f["type"])
        return off


def blob_array(b, o):
    rel, n = struct.unpack_from("<iI", b, o)
    return o + rel, n


def blob_string(b, o):
    rel, n = struct.unpack_from("<iI", b, o)
    return b[o + rel:o + rel + n].decode("utf-8", "replace") if n else ""


def loca_table(blob):
    """`LocaTagCollectionResourceData` bytes -> {tag id: text}."""
    base, n = blob_array(blob, 0)
    out = {}
    for i in range(n):
        o = base + 24 * i
        out[struct.unpack_from("<I", blob, o)[0]] = blob_string(blob, o + 4)
    return out


def spawn_groups(types):
    """keen::ecs::EntitySpawnValue -> (Level bit, [(group, index width, bits)] of the bits below it, in bit order):
    TriggerRange* share one u16 index, RandomSpawnerSetting_* one u8, every other value its own u8."""
    vals = {v["value"]: v["name"] for v in types.get("keen::ecs::EntitySpawnValue")["values"]}
    level = next(v for v, n in vals.items() if n == "Level")
    groups = OrderedDict()
    for bit in range(level):
        n = vals.get(bit)
        if n is None:
            raise KeyError(f"EntitySpawnValue bit {bit} has no name")
        g = "TriggerRange" if n.startswith("TriggerRange") else "RandomSpawner" if n.startswith("RandomSpawnerSetting_") else n
        groups.setdefault(g, [2 if g == "TriggerRange" else 1, 0])[1] |= 1 << bit
    return level, [(g, w, bits) for g, (w, bits) in groups.items()]


def spawn_level(ix, start, mask, levels, sg):
    """The Level spawn value of one entity (None without one): walks its spawn indices (see the module doc)."""
    level_bit, groups = sg
    if not mask >> level_bit & 1 or start >= len(ix):
        return None
    p = start
    count = ix[p]
    p += 1 + (2 if count else 0)
    for _, width, bits in groups:
        if mask & bits:
            p += width
    if p >= len(ix):
        return None
    li = ix[p]
    return levels[li] if li < len(levels) else None


def extract(kfc, types, langs=None):
    """Returns (markers dict, {code: lang dict}, icon names used, report dict)."""
    o = Offs(types)
    dec = Decoder(types)
    th = lambda n: fnv1a32(n.encode())

    # ---- loca
    lk = kfc.resources_of_type(th("keen::LocaTagCollectionResource"))
    coll = dec.decode("keen::LocaTagCollectionResource", kfc.read(lk[0]))
    tables = OrderedDict()
    for l in coll["languages"]:
        code = LANGS.get(l["language"], (l["language"].replace("_", "-"), l["language"]))
        if langs and code[0] not in langs:
            continue
        h = l["data"]["hash"]
        tables[code] = loca_table(kfc.read_content(ContentHash(h["size"], h["hash0"], h["hash1"], h["hash2"])))
    en_code = next((c for c in tables if c[0] == "en-US"), next(iter(tables)))
    EN = tables[en_code]

    # ---- texture names (icons)
    tex = {}
    tn = o("keen::UiTextureResource", "debugName")
    for key in kfc.resources_of_type(th("keen::UiTextureResource")):
        if key[2] == 0:
            tex[key.guid] = blob_string(kfc.read(key), tn)

    # ---- marker types
    rk = kfc.resources_of_type(th("keen::MapMarkerRegistryResource"))
    reg = dec.decode("keen::MapMarkerRegistryResource", kfc.read(rk[0]))["mapMarkers"]
    mtypes = {}
    for kk, v in zip(reg["keys"], reg["values"]):
        mtypes[kk["value"]] = {"icon": tex.get(v["icon"]["image"]) if tag_id(v["icon"]["image"]) else None,
                               "name": tag_id(v["typeName"]), "label": v["showLabel"], "custom": v["isCustomMarkerType"],
                               "fastTravel": v["isFastTravelDestination"], "sorting": v["sortingCategory"]}

    # ---- templates
    t_name = o("keen::ecs::TemplateResource", "name")
    t_comps = o("keen::ecs::TemplateResource", "components")
    mm_h, lore_h, lt_h = th("keen::ecs::MapMarker"), th("keen::ecs::LoreReading"), th("keen::ecs::LocaTagComponent")
    mm_id, mm_type = o("keen::ecs::MapMarker", "markerId"), o("keen::ecs::MapMarker", "markerType")
    lore_entry = o("keen::ecs::LoreReading", "entryName")
    lt_tag = o("keen::ecs::LocaTagComponent", "locaTag")
    templates = {}
    for key in kfc.resources_of_type(th("keen::ecs::TemplateResource")):
        b = kfc.read(key)
        name = blob_string(b, t_name)
        base, n = blob_array(b, t_comps)
        t = {"name": name}
        for i in range(n):
            h, rel, size = struct.unpack_from("<IiI", b, base + 12 * i)
            at = base + 12 * i + 4 + rel
            if h == mm_h:
                t["marker"] = (tag_id(guid_text(b[at + mm_id:at + mm_id + 16])),
                               tag_id(guid_text(b[at + mm_type:at + mm_type + 16])))
            elif h == lore_h:
                t["lore"] = tag_id(guid_text(b[at + lore_entry:at + lore_entry + 16]))
            elif h == lt_h:
                t["tag"] = tag_id(guid_text(b[at + lt_tag:at + lt_tag + 16]))
        templates[key.guid] = t

    # ---- the world scene and its chunks
    s_count = o("keen::SceneResource", "entityChunkCount")
    scenes = []
    for key in kfc.resources_of_type(th("keen::SceneResource")):
        cx, cy = struct.unpack_from("<II", kfc.read(key), s_count)
        scenes.append((cx * cy, key.guid))
    cells, world = max(scenes)
    c_tmpl = o("keen::SceneEntityChunkResource", "templates")
    c_ent = o("keen::SceneEntityChunkResource", "entities")
    sd = o("keen::SceneEntityChunkResource", "spawnData")
    sd_entries = sd + o("keen::SceneEntityChunkSpawnData", "entries")
    sd_levels = sd + o("keen::SceneEntityChunkSpawnData", "levels")
    sd_ix = sd + o("keen::SceneEntityChunkSpawnData", "indices")
    lv_level = o("keen::SceneEntitySpawnEntryLevel", "level")
    tr = {f: o("keen::SceneEntityTransform", f)
          for f in ("positionX0", "positionX1", "positionY0", "positionY1", "positionZ0", "positionZ1")}
    e_tr = o("keen::SceneEntityChunkSpawnEntry", "transform")
    e_size = types.get("keen::SceneEntityChunkSpawnEntry")["size"]
    lvl_size = types.get("keen::SceneEntitySpawnEntryLevel")["size"]
    sg = spawn_groups(types)
    placements = []  # (template guid, x, y, z, level)
    entities = 0
    for key in kfc.resources_of_type(th("keen::SceneEntityChunkResource")):
        if key.guid != world or key[2] > cells:
            continue
        b = kfc.read(key)
        tb, tn_ = blob_array(b, c_tmpl)
        tg = [guid_text(b[tb + 16 * i:tb + 16 * i + 16]) for i in range(tn_)]
        eb, en = blob_array(b, c_ent)
        entities += en
        nb, nn = blob_array(b, sd_entries)
        lb, ln = blob_array(b, sd_levels)
        levels = [struct.unpack_from("<i", b, lb + lvl_size * i + lv_level)[0] for i in range(ln)]
        ib, inn = blob_array(b, sd_ix)
        ix = b[ib:ib + inn]
        for i in range(en):
            e = eb + e_size * i
            ti = struct.unpack_from("<H", b, e)[0]
            g = tg[ti] if ti < len(tg) else None
            t = templates.get(g)
            if t is None or not ("marker" in t or object_cat(t["name"])):
                continue
            q = lambda f: struct.unpack_from("<H", b, e + e_tr + tr[f])[0]
            x = ((q("positionX1") << 16) | q("positionX0")) / 65536
            y = ((q("positionY1") << 16) | q("positionY0")) / 65536
            z = ((q("positionZ1") << 16) | q("positionZ0")) / 65536
            lv = None
            if i < nn:
                mask, start = struct.unpack_from("<HH", b, nb + 4 * i)
                lv = spawn_level(ix, start, mask, levels, sg)
            placements.append((g, x, y, z, lv))

    # ---- classify
    cats = OrderedDict()  # id -> {id, group, label, icon, on, tier, singular, names: Counter}
    pts = []  # [cat id, x, z, y, en name, name tag or None, level]
    labels = []  # [name tag or en text, x, z, tier]
    skipped = Counter()

    def cat(cid, group, label, icon, on, tier, singular, tr, rank):
        if cid not in cats:
            cats[cid] = {"id": cid, "group": group, "label": label, "icon": icon, "on": on, "tier": tier,
                         "singular": singular, "names": Counter(), "tr": tr, "rank": rank}
        cats[cid]["rank"] = min(cats[cid]["rank"], rank)
        return cats[cid]

    def marker_skip(t):
        """Why a template's map marker is not drawn (None: it is)."""
        name_tag, type_id = t["marker"]
        mt = mtypes.get(type_id)
        if any(k in t["name"].lower() for k in MARKER_SKIP):
            return "marker: template not shown in game (_noUI)"
        if any(k in t["name"].lower() for k in MARKER_DUPLICATE):
            return "marker: label of a world object"
        if mt is None:
            return "marker: no type"
        if mt["custom"]:
            return "marker: custom marker type"
        if mt["icon"] is None and not mt["label"]:
            return "marker: invisible type"
        return None

    for g, x, y, z, lv in placements:
        t = templates[g]
        obj = object_cat(t["name"])
        why = marker_skip(t) if "marker" in t else "no marker"
        if why is None:
            name_tag, type_id = t["marker"]
            mt = mtypes[type_id]
            tag = name_tag if name_tag in EN else (t.get("tag") if t.get("tag") in EN else None)
            if mt["icon"] is None:
                if tag:
                    labels.append([tag, x, z, 1])
                else:
                    skipped["marker: label without a name"] += 1
                continue
            spec = MARKER_CATS.get(mt["icon"])
            rank = MARKER_RANK.get(mt["icon"], len(MARKER_CATS) + len(OBJECT_CATS))
            if spec is None:  # a marker type this recipe does not know yet: its own category
                pretty = mt["icon"].removeprefix("mapmarker_").replace("_", " ").strip().title()
                spec = (mt["icon"].removeprefix("mapmarker_").lower(), OTH, pretty, True, 0, "name")
            c = cat(spec[0], spec[1], spec[2], f"g-{mt['icon']}.webp", spec[3], spec[4], None, spec[5], rank)
            en_name = clean(EN.get(tag, "")) if tag else ""
            c["names"][tag] += 1
            pts.append([spec[0], x, z, y, en_name, tag, None, rank])
        elif obj is not None:
            spec = obj
            rank = OBJECT_RANK[spec[0]]
            c = cat(spec[0], spec[1], spec[2], f"g-{spec[5]}.webp", spec[4], 0, spec[3], spec[7], rank)
            tag = t.get("lore") if spec[0] == "lore" and t.get("lore") in EN else None
            pts.append([spec[0], x, z, y, clean(EN.get(tag, "")) if tag else "", tag,
                        lv if spec[0] in LEVELED else None, rank])
        else:
            skipped[why] += 1

    # ---- order categories (group order, then first appearance), dedupe, name the unnamed
    order = sorted(cats.values(), key=lambda c: (GROUPS.index(c["group"]), c["rank"], c["id"]))
    cidx = {c["id"]: i for i, c in enumerate(order)}
    # deterministic: by category, then the type's rank (the unlocked spire before the locked one), then position
    pts.sort(key=lambda p: (cidx[p[0]], p[7], r1(p[1]), r1(p[2]), r1(p[3]), p[4]))
    keep = []
    dups = 0
    grid = {}  # (category, cell x, cell z) -> kept points; cells of DEDUP_M, so neighbours cover every radius
    for p in pts:
        objects = cats[p[0]]["singular"] is not None
        gx, gz = int(p[1] // DEDUP_M), int(p[2] // DEDUP_M)
        near = [q for dx in (-1, 0, 1) for dz in (-1, 0, 1) for q in grid.get((p[0], gx + dx, gz + dz), ())]
        if any(math.hypot(q[1] - p[1], q[2] - p[2]) < (DEDUP_OBJECT_M if objects else
               (DEDUP_M if q[4] == p[4] else DEDUP_ANY_M)) for q in near):
            dups += 1
            continue
        grid.setdefault((p[0], gx, gz), []).append(p)
        keep.append(p)
    pts = sorted(keep, key=lambda p: (cidx[p[0]], r1(p[1]), r1(p[2]), r1(p[3]), p[4]))
    uses = Counter(p[4] for p in pts if p[4])
    # place labels: unique-ish names of location markers
    for p in pts:
        tier = cats[p[0]]["tier"]
        if tier and p[4] and uses[p[4]] <= LABEL_MAX_USES and p[5] is not None:
            labels.append([p[5], p[1], p[2], tier])
    labels.sort(key=lambda l: (clean(EN.get(l[0], "")).lower(), l[1], l[2]))
    dedup_labels = []
    for l in labels:
        if not any(m[0] == l[0] and math.hypot(m[1] - l[1], m[2] - l[2]) < DEDUP_M for m in dedup_labels):
            dedup_labels.append(l)
    labels = dedup_labels

    def text(table, tag, fallback):
        return clean(table.get(tag, "")) if tag and table.get(tag) else fallback

    def en_lookup(s):
        """The tag id whose English text is `s` (case-insensitive), for labels the game spells the same."""
        want = s.lower()
        return next((k for k, v in sorted(EN.items()) if clean(v).lower() == want), None)

    singular_tags = {c["id"]: en_lookup(c["singular"]) for c in order if c["singular"]}
    # unnamed map markers: the category's most common marker name ("Shroud Root"), else its label
    top_tags = {c["id"]: next((t for t, _ in sorted(c["names"].items(), key=lambda kv: (-kv[1], kv[0] or 0)) if t), None)
                for c in order}

    def label_tag(c):
        tr = c["tr"]
        if tr == "name":
            return singular_tags.get(c["id"]) if c["singular"] else top_tags.get(c["id"])
        if tr == "label":
            return en_lookup(c["label"])
        if tr and tr.startswith("text:"):
            return en_lookup(tr[5:])
        return None
    cat_tags = {c["id"]: label_tag(c) for c in order}

    def build(table, english=False):
        pnames = []
        for p in pts:
            c = cats[p[0]]
            if c["singular"]:
                fallback = text(table, singular_tags.get(p[0]), c["singular"])
            else:
                fallback = text(table, top_tags.get(p[0]), text(table, cat_tags[p[0]], c["label"]))
            pnames.append(text(table, p[5], fallback) if p[5] else fallback)
        return {
            "cats": [c["label"] if english else text(table, cat_tags[c["id"]], c["label"]) for c in order],
            "points": pnames,
            "labels": [text(table, l[0], "") for l in labels],
        }

    en = build(EN, english=True)
    counts = Counter(p[0] for p in pts)
    markers = {
        "format": FORMAT, "source": "game", "build": kfc.version.split("|")[0], "world": WORLD,
        "lang": en_code[0], "langs": [{"code": c[0], "name": c[1]} for c in tables],
        "cats": [{"id": c["id"], "group": c["group"], "label": en["cats"][i], "icon": c["icon"], "on": c["on"],
                  "count": counts[c["id"]]} for i, c in enumerate(order)],
        "points": [[cidx[p[0]], r1(p[1]), r1(p[2]), r1(p[3]), en["points"][i], "", p[6] or 0, ""]
                   for i, p in enumerate(pts)],
        "labels": [[en["labels"][i], r1(l[1]), r1(l[2]), l[3]] for i, l in enumerate(labels)],
    }
    other = {c[0]: build(tb) for c, tb in tables.items() if c != en_code}
    have = set(tex.values())
    for c in markers["cats"]:
        name = c["icon"][2:-5]
        base = "icon_chest" if name in CHEST_TINTS else name
        if base not in have:
            c["icon"] = FALLBACK_ICON
    icons = sorted({c["icon"][2:-5] for c in markers["cats"]})
    report = {"world": world, "chunks": cells + 1, "entities": entities, "placements": len(placements),
              "points": len(pts), "labels": len(labels), "duplicates": dups, "skipped": dict(skipped),
              "templates": len(templates), "types": len(mtypes), "languages": len(tables)}
    return markers, other, icons, report


# Category ids of the other set that are one game category (for compare only)
EW_ALIAS = {"sun-temple": "dungeon"}


def compare(markers, ew, radius=25.0):
    """Per-category counts and position agreement with another MarkerData, by category id (its 13
    lore-* categories count as one "lore")."""
    def by_cat(d, alias):
        out = {}
        for p in d["points"]:
            cid = d["cats"][p[0]]["id"]
            if alias:
                cid = "lore" if cid.startswith("lore-") else EW_ALIAS.get(cid, cid)
            out.setdefault(cid, []).append(p)
        return out
    a, b = by_cat(markers, False), by_cat(ew, True)
    rows = []
    for cid in sorted(set(a) | set(b), key=lambda c: (c not in a, c)):
        pa, pb = a.get(cid, []), b.get(cid, [])
        near = []
        for p in pa:
            if pb:
                near.append(min(math.hypot(p[1] - q[1], p[2] - q[2]) for q in pb))
        near.sort()
        found_b = sum(1 for q in pb if pa and min(math.hypot(p[1] - q[1], p[2] - q[2]) for p in pa) <= radius)
        rows.append({"id": cid, "game": len(pa), "other": len(pb),
                     "median_m": round(near[len(near) // 2], 1) if near else None,
                     "game_within": sum(1 for d in near if d <= radius), "other_within": found_b})
    return rows
