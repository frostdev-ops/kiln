#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""regmap_entries: the R1 registries' game entries of one game build, read offline from the game's own archive and
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

        def array(type_name, member, name_field, guid_field, id_field):
            key, blob, el, at, n = src._collection(type_name, member)
            b = live.Blob(blob)
            nf = g.field(el, name_field)[2] if name_field else None
            gf = g.field(el, guid_field)[2] if guid_field else None
            idf = g.field(el, id_field)[2]
            rows = []
            for i in range(n):
                o = at + i * el.size
                guid = live.guid_text(b.bytes(o + gf, 16)) if gf is not None else ""
                rows.append({"guid": guid, "name": b.blob_string(o + nf) if nf is not None else "", "id": b.u32(o + idf)})
            return rows

        out["registries"]["recipes"] = array("keen::RecipeRegistryResource", "recipes", "debugName", "recipeGuid", "recipeId")
        out["registries"]["loot"] = array("keen::LootableItemsResource", "items", None, None, "itemId")
        # templates' stable names (the same rules), then the drop tables they use
        trows = list(src.templates())
        tids, _ = regmap_ids.derive("templates", {"unnamed": "template_%.8s"}, {}, [{"guid": r.key.guid, "name": r.name, "id": 0} for r in trows])
        users = {}
        for r, tid in zip(trows, tids):
            for comp in find(r.value(), "DefaultInventory"):
                ref = str(comp.get("defaultInventory") or "").lower()
                if ref:
                    users.setdefault(ref, []).append(tid.split(":", 1)[1])
        out["registries"]["templates"] = [{"guid": r.key.guid, "name": r.name, "id": 0} for r in trows]
        out["registries"]["drops"] = [{"guid": k.guid, "name": "", "id": 0, "namedBy": sorted(users.get(k.guid.lower(), []))}
                                      for k in src.keys("keen::ecs::DefaultInventoryResource")]
        out["registries"]["balance"] = [{"guid": k.guid, "name": "", "id": 0} for k in src.keys("keen::BalancingTable")]
        # one-resource registries of R3b (world rules): the one resource their regmap names (the client has five scenes)
        for reg, rtype, guid in (("world", "keen::SceneResource", "509feadb-4c60-425f-9c7c-deeefd9b6920"),
                                 ("difficulty", "keen::GameSettingsPresetsResource", "46e14df6-fb8f-4f66-9463-e23b9e19e48e")):
            out["registries"][reg] = [{"guid": k.guid, "name": "", "id": 0} for k in src.keys(rtype) if k.guid.lower() == guid]
        # a hooks registry (limits) has no resource: its one entry's GUID is the model's added_guid (UUIDv5, as an added entry's)
        import uuid
        out["registries"]["limits"] = [{"guid": str(uuid.uuid5(uuid.UUID("6f6d2a44-53c1-5c52-9d51-0a3c6e1f2b77"), "registry:limits:game:limits")),
                                        "name": "", "id": 0}]
    finally:
        src.close()
    Path(a.out).write_text(json.dumps(out), encoding="utf-8")
    print(f"regmap_entries: {', '.join(f'{k} {len(v)}' for k, v in out['registries'].items())} -> {a.out}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
