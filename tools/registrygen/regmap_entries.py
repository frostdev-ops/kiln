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
    finally:
        src.close()
    Path(a.out).write_text(json.dumps(out), encoding="utf-8")
    print(f"regmap_entries: {', '.join(f'{k} {len(v)}' for k, v in out['registries'].items())} -> {a.out}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
