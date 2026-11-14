# SPDX-License-Identifier: MIT
"""The four kinds of the boot game-data snapshot (contract c-08) from the game's archive: what each entry is named, which
resource backs it, and where its value sits.

- items: every keen::ItemInfo resource, named by its debugName (kiln.content's item registry reads that name).
- templates: every keen::ecs::TemplateResource, named by its `name`, or its GUID when it has none (gamedata_res.cpp
  build_resources, as the live reader names templates).
- skills: the nodes of the first keen::SkillTreeResource (build_collection), named "0x" + 8 hex digits of `id`.
- materials: the rows of keen::TerraformingEfficiencyRegistryResource, building configs then terrain configs, named as
  the GameMaterials catalog names them (crates/kiln-manage/src/catalog/extract.rs `materials`, the same rule as
  native/services/worldtables `material_name`): from the debug name of the first item that places the cell, else
  "Cell<id>" (also when that name is taken already).

A row is (name, order, key, sort, value): `order` is the archive's resource order (the first of two equal names is the
one kept), `sort` the place of its bytes in the resource stream (values are decoded in that order, so every chunk is
decompressed once), `value()` the live reader's JSON of it (live.Walker, depth 12).
"""
from collections import namedtuple

from .live import Blob

ITEM, TEMPLATE = "keen::ItemInfo", "keen::ecs::TemplateResource"
SKILL_TREE, SKILL_NODE = "keen::SkillTreeResource", "keen::SkillTreeNode"
TERRAFORMING = "keen::TerraformingEfficiencyRegistryResource"
KINDS = ("items", "templates", "skills", "materials")

Row = namedtuple("Row", "name order key sort value")


class KindError(Exception):
    """A kind cannot be read from this archive (a type or member the rule needs is missing)."""


def fnv1a32(s):
    h = 0x811C9DC5
    for c in s.encode():
        h = ((h ^ c) * 0x01000193) & 0xFFFFFFFF
    return h


def material_name(item):
    """extract.rs material_name: "Block_T2_Stone_Rough" -> "StoneRough", "Build_TerrainMaterial_Sand" -> "TerrainSand"."""
    s = item.lstrip("_")
    if s.startswith("Build_TerrainMaterial_"):
        prefix, rest = "Terrain", s[len("Build_TerrainMaterial_"):]
    elif s.startswith("Block_"):
        prefix, rest = "", s[len("Block_"):]
    else:
        return None
    out = prefix
    for tok in rest.split("_"):
        tier = len(tok) >= 2 and tok[0] == "T" and all("0" <= c <= "9" for c in tok[1:])
        if not tok or tier or tok.upper() in ("CRAFTING", "REWARD"):
            continue
        out += tok[0].upper() + tok[1:]
    return out


class Source:
    """An archive (tools/kfc Kfc) and the executable's type graph, with the walker that makes values."""

    def __init__(self, kfc, graph, walker):
        self.kfc, self.g, self.walker = kfc, graph, walker

    def keys(self, type_name):
        return self.kfc.resources_of_type(fnv1a32(type_name))

    def stream_offset(self, key):
        return self.kfc.values[self.kfc.index[key]][0]

    def need(self, type_name):
        t = self.g.named(type_name)
        if t is None:
            raise KindError(f"{type_name} is not in the game's reflection")
        return t

    def need_field(self, t, name):
        f = self.g.field(t, name)
        if f is None:
            raise KindError(f"{t.name} has no member {name}")
        return f

    def path(self, t, dotted):
        """Offset and type of a member path ("equipment.voxelData.placeVoxelMaterial")."""
        off = 0
        for part in dotted.split("."):
            r = self.g.resolved(t)
            f = self.g.field(r, part) if r is not None else None
            if f is None:
                raise KindError(f"{t.name if t else '?'} has no member {part} ({dotted})")
            off += f[2]
            t = self.g.get(f[1])
        return off, t

    def value_of_resource(self, type_name, key):
        return lambda: self.walker.json(type_name, self.kfc.read(key))

    def value_of_row(self, row_type, blob, offset):
        return lambda: self.walker.json(row_type, blob, offset)

    # ---- the kinds ----
    def rows(self, kind):
        return {"items": self.items, "templates": self.templates, "skills": self.skills, "materials": self.materials}[kind]()

    def items(self):
        t = self.need(ITEM)
        name_at = self.need_field(t, "debugName")[2]
        for order, key in enumerate(self.keys(ITEM)):
            name = Blob(self.kfc.read(key)).blob_string(name_at)
            yield Row(name, order, key, self.stream_offset(key), self.value_of_resource(ITEM, key))

    def templates(self):
        t = self.need(TEMPLATE)
        name_at = self.need_field(t, "name")[2]
        for order, key in enumerate(self.keys(TEMPLATE)):
            name = Blob(self.kfc.read(key)).blob_string(name_at) or key.guid
            yield Row(name, order, key, self.stream_offset(key), self.value_of_resource(TEMPLATE, key))

    def _collection(self, type_name, array):
        """The first resource of `type_name` and the element type, data offset and count of its BlobArray `array`."""
        t = self.need(type_name)
        keys = self.keys(type_name)
        if not keys:
            raise KindError(f"the archive has no {type_name}")
        key = keys[0]
        blob = self.kfc.read(key)
        f = self.need_field(t, array)
        at_t = self.g.resolved(self.g.get(f[1]))
        el = self.g.resolved(self.g.inner(at_t)) if at_t is not None else None
        if el is None:
            raise KindError(f"{type_name}.{array} is not an array of a known type")
        b = Blob(blob)
        rel, n = b.i32(f[2]), b.u32(f[2] + 4)
        if rel is None or n is None or n > 1000000:
            raise KindError(f"{type_name}.{array} cannot be read")
        count = n if rel else 0
        return key, blob, el, f[2] + rel, count

    def skills(self):
        node_t = self.need(SKILL_NODE)
        key, blob, _el, data, count = self._collection(SKILL_TREE, "nodes")
        id_at = self.need_field(node_t, "id")[2]
        b = Blob(blob)
        for i in range(min(count, 100000)):
            at = data + i * node_t.size  # gamedata_res.cpp: the stride is SkillTreeNode's size
            nid = b.u32(at + id_at)
            name = f"0x{nid:08x}" if nid is not None else ""
            yield Row(name, i, key, self.stream_offset(key) + at, self.value_of_row(node_t, blob, at))

    def materials(self):
        item_t = self.need(ITEM)
        dbg = self.need_field(item_t, "debugName")[2]
        voxel_guid, _ = self.path(item_t, "equipment.voxelData.placeVoxelMaterial")
        voxel_id, _ = self.path(item_t, "equipment.voxelData.placeVoxelMaterialId")
        placed = {}
        for key in self.keys(ITEM):
            if key[2] != 0:
                continue
            b = Blob(self.kfc.read(key))
            g = b.bytes(voxel_guid, 16)
            if g is None or not any(g):
                continue
            cell = b.bytes(voxel_id, 1)
            if cell is None:
                continue
            placed.setdefault(cell[0], b.blob_string(dbg))
        reg_t = self.need(TERRAFORMING)
        keys = self.keys(TERRAFORMING)
        if not keys:
            raise KindError(f"the archive has no {TERRAFORMING}")
        key = keys[0]
        blob = self.kfc.read(key)
        b = Blob(blob)
        taken = set()
        order = 0
        for array, cell_member, base in (("buildingConfigs", "buildingMaterial", 128), ("terrainConfigs", "terrainMaterial", 0)):
            f = self.need_field(reg_t, array)
            at_t = self.g.resolved(self.g.get(f[1]))
            el = self.g.resolved(self.g.inner(at_t)) if at_t is not None else None
            if el is None:
                raise KindError(f"{TERRAFORMING}.{array} is not an array of a known type")
            cf = self.need_field(el, cell_member)
            leaf = self.g.leaf(self.g.get(cf[1]))
            if leaf is None or leaf[0] not in ("int", "enum"):
                raise KindError(f"{el.name}.{cell_member} is not a number")
            rel, n = b.i32(f[2]), b.u32(f[2] + 4)
            if rel is None or n is None or n > 1000000:
                raise KindError(f"{TERRAFORMING}.{array} cannot be read")
            data = f[2] + rel
            for i in range(n if rel else 0):
                at = data + i * el.size
                raw = b.bytes(at + cf[2] + leaf[3], leaf[1])
                cell = base + (int.from_bytes(raw, "little") if raw is not None else 0)
                item = placed.get(cell)
                named = material_name(item) if item else None
                name = named if named and named not in taken else f"Cell{cell}"
                taken.add(name)
                yield Row(name, order, key, self.stream_offset(key) + at, self.value_of_row(el, blob, at))
                order += 1
