#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""regmap_entries: the registries' game entries of one game build, read offline from the game's own archive and
executable (tools/bootsnap's reader: the files are read as data, the exe is never run), for regmap_check.py --record.

    regmap_entries.py --exe <a copy of the game exe, its .kfc pair beside it> --out <file outside the repository>

The output holds the entries' GUIDs, debug names and 32-bit ids (and, for drop tables, the stable names of the templates
that use them): game data. It is an input of the record step, written to the build or output folder, never committed.
"""
import argparse
import json
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
sys.path.insert(0, str(HERE.parent / "bootsnap"))
sys.path.insert(0, str(HERE.parent / "kfc"))

import regmap_ids  # noqa: E402


def find(value, kind):
    """Every object {"$type": kind, ...} inside a walked value."""
    stack = [value]
    while stack:
        v = stack.pop()
        if isinstance(v, dict):
            if v.get("$type") == kind:
                yield v
            stack.extend(v.values())
        elif isinstance(v, list):
            stack.extend(v)


def creature_components(root=HERE.parent.parent):
    """The behaviour components that make a template a creature: the mapping's own list (regmap/common/creatures.json
    entries.withComponent, of the tree this script is in: the one kilnctl regproof proves), by short type name."""
    doc = json.loads((root / "regmap" / "common" / "creatures.json").read_text(encoding="utf-8"))
    return tuple(str(c).rsplit("::", 1)[-1] for c in doc["entries"]["withComponent"])


def element_num(guid, key):
    """kiln.content's 32-bit number of an element without an id member (registry/element_num.h): FNV-1a over its resource's
    16 GUID bytes in the game's order, mixed with its key (the usedBy id that names it, never its index)."""
    import uuid
    h = 2166136261
    for c in uuid.UUID(guid).bytes_le:
        h = ((h ^ c) * 16777619) & 0xFFFFFFFF
    return h ^ (((key + 1) * 0x9E3779B1) & 0xFFFFFFFF)


def element_name(pattern, key, guid):
    """regmap.cpp element_name: "%08x" the key (the element's id, or the usedBy id that names it), "%.8s" the resource GUID
    text's first 8 characters."""
    return pattern.replace("%08x", "%08x" % key).replace("%.8s", guid.lower()[:8])


def _blob_elements(b, at):
    """(first element's offset, count) of the BlobArray at offset `at` of blob b."""
    rel, n = b.i32(at), b.u32(at + 4)
    return (at + rel, n) if rel else (at, 0)


def random_loot(src, g):
    """randomLoot's entries (kind arrays): every element of each source's array, in every resource of its type, named by
    the source's pattern over its id member, or over the smallest usedBy id of the elements that use it (a world table:
    the scene node of its first chest); an element nothing uses is no entry (regmap/common/randomLoot.json). No GUID of
    their own (id tables key them "#<number>")."""
    from bootdata import live
    regmap = json.loads((HERE.parents[1] / "regmap" / "common" / "randomLoot.json").read_text(encoding="utf-8"))
    rows = []
    for source in regmap["entries"]["sources"]:
        t = src.need(source["resourceType"])
        f = src.need_field(t, source["array"])
        at_t = g.resolved(g.get(f[1]))
        el = g.resolved(g.inner(at_t)) if at_t is not None else None
        idf = g.field(el, source["id"])[2] if source.get("id") else None
        used_by = source.get("usedBy")
        if used_by:
            uf = src.need_field(t, used_by["array"])
            ut = g.resolved(g.get(uf[1]))
            uel = g.resolved(g.inner(ut)) if ut is not None else None
            u_index, u_id = g.field(uel, used_by["index"])[2], g.field(uel, used_by["id"])[2]
        for key in src.keys(source["resourceType"]):
            b = live.Blob(src.kfc.read(key))
            first, count = _blob_elements(b, f[2])
            keys = {}
            if used_by:
                ufirst, ucount = _blob_elements(b, uf[2])
                for u in range(ucount):
                    o = ufirst + u * uel.size
                    at, node = b.u32(o + u_index), b.u32(o + u_id)
                    if at < count and (at not in keys or node < keys[at]):
                        keys[at] = node
            for i in range(count):
                o = first + i * el.size
                if idf is not None:
                    num = b.u32(o + idf)
                    rows.append({"guid": "", "name": element_name(source["unnamed"], num, key.guid), "id": num})
                elif i in keys:
                    rows.append({"guid": "", "name": element_name(source["unnamed"], keys[i], key.guid), "id": element_num(key.guid, keys[i])})
    return rows


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    ap.add_argument("--exe", required=True)
    ap.add_argument("--out", required=True)
    a = ap.parse_args(argv)
    import bootsnap
    from bootdata import live
    src = bootsnap.open_source(Path(a.exe))
    g = src.g
    out = {"format": 1, "registries": {}}
    try:
        items = [{"guid": r.key.guid, "name": r.name, "id": live.Blob(src.kfc.read(r.key)).u32(0) or 0} for r in src.items()]
        out["registries"]["items"] = items

        def array(type_name, member, name_field, guid_field, id_field, key_field=None):
            key, blob, el, at, n = src._collection(type_name, member)
            b = live.Blob(blob)
            nf = g.field(el, name_field)[2] if name_field else None
            gf = g.field(el, guid_field)[2] if guid_field else None
            idf = g.field(el, id_field)[2]
            kf = g.field(el, key_field)[2] if key_field else None
            rows = []
            for i in range(n):
                o = at + i * el.size
                guid = live.guid_text(b.bytes(o + gf, 16)) if gf is not None else ""
                row = {"guid": guid, "name": b.blob_string(o + nf) if nf is not None else "", "id": b.u32(o + idf)}
                if kf is not None:
                    row["keyId"] = b.u32(o + kf)  # stations: the item that builds it names it (regmap entries keyMember)
                rows.append(row)
            return rows

        out["registries"]["recipes"] = array("keen::RecipeRegistryResource", "recipes", "debugName", "recipeGuid", "recipeId")
        out["registries"]["loot"] = array("keen::LootableItemsResource", "items", None, None, "itemId")
        out["registries"]["stations"] = array("keen::WorkshopRegistryResource", "workshops", None, "workshopGuid", "workshopId", "item")
        out["registries"]["skills"] = array("keen::SkillTreeResource", "nodes", None, None, "id")
        # templates' stable names (the same rules), then the drop tables they use
        trows = list(src.templates())
        tids, _ = regmap_ids.derive("templates", {"unnamed": "template_%.8s"}, {}, [{"guid": r.key.guid, "name": r.name, "id": 0} for r in trows])
        # one decode of each template: the drop tables and arsenals it names (they are named after it), and whether it is a
        # creature (regmap/common/creatures.json entries.withComponent: an enemy's, a wild or farm animal's, a villager's behaviour)
        creature_kinds = creature_components()
        users, arsenal_users, creatures = {}, {}, []
        for r, tid in zip(trows, tids):
            v = r.value()
            for comp in find(v, "DefaultInventory"):
                ref = str(comp.get("defaultInventory") or "").lower()
                if ref:
                    users.setdefault(ref, []).append(tid.split(":", 1)[1])
            for comp in find(v, "EnemyArsenalReference"):
                ref = str(comp.get("arsenal") or "").lower()
                if ref:
                    arsenal_users.setdefault(ref, []).append(tid.split(":", 1)[1])
            comps = v.get("components") if isinstance(v, dict) else None
            types = {c.get("$type") for c in comps if isinstance(c, dict)} if isinstance(comps, list) else set()
            if any(k in types for k in creature_kinds):
                creatures.append({"guid": r.key.guid, "name": r.name, "id": 0})
        out["registries"]["templates"] = [{"guid": r.key.guid, "name": r.name, "id": 0} for r in trows]
        out["registries"]["creatures"] = creatures
        out["registries"]["drops"] = [{"guid": k.guid, "name": "", "id": 0, "namedBy": sorted(users.get(k.guid.lower(), []))}
                                      for k in src.keys("keen::ecs::DefaultInventoryResource")]
        out["registries"]["balance"] = [{"guid": k.guid, "name": "", "id": 0} for k in src.keys("keen::BalancingTable")]
        # arsenals: the elements of the arsenal registry, named after the creatures that use them
        _key, blob, el, at, n = src._collection("keen::enemy::EnemyArsenalRegistryResource", "arsenals")
        b, gf = live.Blob(blob), g.field(el, "guid")[2]
        out["registries"]["arsenals"] = [
            {"guid": (gid := live.guid_text(b.bytes(at + i * el.size + gf, 16))), "name": "", "id": 0,
             "namedBy": sorted(arsenal_users.get(gid.lower(), []))} for i in range(n)]
        perk_t = src.need("keen::Perk")
        perk_name = src.need_field(perk_t, "debugName")[2]
        out["registries"]["perks"] = [{"guid": k.guid, "name": live.Blob(src.kfc.read(k)).blob_string(perk_name), "id": 0}
                                      for k in src.keys("keen::Perk")]
        # dev.32: buffs (the one collection's records, named by their 32-bit id) and abilities (one action sequence each,
        # named by its first sub-sequence: regmap/common/abilities.json's "subSequences[0].name")
        out["registries"]["buffs"] = array("keen::BuffTypeCollectionResource", "buffs", None, None, "buffTypeId")
        seq_t = src.need("keen::actor::ActorSequenceResource")
        subs_at = src.need_field(seq_t, "subSequences")[2]
        sub_t = src.need("keen::actor::ActorSubSequenceResource")
        sub_name = src.need_field(sub_t, "name")[2]
        abilities = []
        for k in src.keys("keen::actor::ActorSequenceResource"):
            b = live.Blob(src.kfc.read(k))
            rel, n = b.i32(subs_at), b.u32(subs_at + 4)
            name = b.blob_string(subs_at + rel + sub_name) if rel and n else ""
            abilities.append({"guid": k.guid, "name": name, "id": 0})
        out["registries"]["abilities"] = abilities
        # one-resource registries of R3b (world rules): the one resource their regmap names (the client has five scenes)
        for reg, rtype, guid in (("world", "keen::SceneResource", "509feadb-4c60-425f-9c7c-deeefd9b6920"),
                                 ("difficulty", "keen::GameSettingsPresetsResource", "46e14df6-fb8f-4f66-9463-e23b9e19e48e")):
            out["registries"][reg] = [{"guid": k.guid, "name": "", "id": 0} for k in src.keys(rtype) if k.guid.lower() == guid]
        # a hooks registry (limits) has no resource: its one entry's GUID is the model's added_guid (UUIDv5, as an added entry's)
        import uuid
        out["registries"]["limits"] = [{"guid": str(uuid.uuid5(uuid.UUID("6f6d2a44-53c1-5c52-9d51-0a3c6e1f2b77"), "registry:limits:game:limits")),
                                        "name": "", "id": 0}]
        out["registries"]["lootRules"] = [{"guid": k.guid, "name": "", "id": 0} for k in src.keys("keen::LootableItemsResource")]
        out["registries"]["randomLoot"] = random_loot(src, g)
    finally:
        src.close()
    Path(a.out).write_text(json.dumps(out), encoding="utf-8")
    print(f"regmap_entries: {', '.join(f'{k} {len(v)}' for k, v in out['registries'].items())} -> {a.out}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
