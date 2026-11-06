#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""kiln.worldtables' terrain tables through tools/kfc, offline: what the C++ writer made (run.sh with KILN_TERRAIN_RES) decodes with the game's own
reflection, says exactly what was asked and nothing else, and encodes back to the same values.

    python3 -I native/services/worldtables/tests/terrain_roundtrip.py RES_DIR [--game DIR]

RES_DIR holds the game's registry.bin pools.bin world.bin vfx.bin blend.bin simple.bin and the writer's merged-*.bin (local data, never in the repo).
For each table: decode(game) with the expected edit applied in Python == decode(merged); encode(decode(merged)) decodes to the same value and encodes
to the same bytes again. Prints PASS/FAIL lines; exit status 1 on a FAIL.
"""
import argparse
import copy
import struct
import sys
from pathlib import Path

REPO = Path(__file__).resolve().parents[4]
sys.path.insert(0, str(REPO / "tools/kfc"))

from kfc.blob import Decoder  # noqa: E402
from kfc.blobwrite import Encoder  # noqa: E402
from kfc.refl import TypeDb  # noqa: E402

TYPES = {
    "registry": "keen::TerraformingEfficiencyRegistryResource",
    "pools": "keen::VoxelBlueprintMaterialPoolRegistryResource",
    "world": "keen::VoxelWorldResource",
    "vfx": "keen::VoxelMaterialVfxDataResource",
    "blend": "keen::WorldMaterialBlending2Resource",
    "simple": "keen::SimpleWorldMaterialResource",
}
# what test_terrain.cpp merges: (block, like, look); its ids come from the merged registry
ORE, SOIL = ("frost-ore", 26, 115), ("frost-soil", 1, 1)


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("res")
    ap.add_argument("--game", default=str(Path.home() / ".local/share/Steam/steamapps/common/Enshrouded"))
    a = ap.parse_args()
    res = Path(a.res)
    types = TypeDb.from_exe(Path(a.game) / "enshrouded.exe")
    dec, enc = Decoder(types, max_array=1 << 26), Encoder(types)
    failed = 0

    def check(name, ok, detail=""):
        nonlocal failed
        failed += not ok
        print(f"{'PASS' if ok else 'FAIL'} {name}{': ' + str(detail) if detail else ''}", flush=True)

    game, merged = {}, {}
    for k, t in TYPES.items():
        game[k] = dec.decode(types.get(t), (res / f"{k}.bin").read_bytes())
        merged[k] = dec.decode(types.get(t), (res / f"merged-{k}.bin").read_bytes())

    # the ids the writer gave: the terrain configs past the game's
    old_ids = {c["terrainMaterial"]["value"] for c in game["registry"]["terrainConfigs"]}
    new = [c for c in merged["registry"]["terrainConfigs"] if c["terrainMaterial"]["value"] not in old_ids]
    check("registry.two-new-configs", len(new) == 2 and len(merged["registry"]["terrainConfigs"]) == len(old_ids) + 2, [c["terrainMaterial"]["value"] for c in new])
    if len(new) != 2:
        return 1
    by_like = {}
    for c in new:
        by_like[c["terrainMaterial"]["value"]] = c
    # the soil is the one that slides
    soil_id = next(i for i, c in by_like.items() if c["isSlidingMaterial"])
    ore_id = next(i for i in by_like if i != soil_id)
    like_of = {c["terrainMaterial"]["value"]: c for c in game["registry"]["terrainConfigs"]}

    # registry: the game's, plus a copy of each like with its id and what the test set
    want = copy.deepcopy(game["registry"])
    ore = copy.deepcopy(like_of[ORE[1]])
    ore["terrainMaterial"]["value"] = ore_id
    ore["healthPoints"] = 12
    soil = copy.deepcopy(like_of[SOIL[1]])
    soil["terrainMaterial"]["value"] = soil_id
    soil["isSlidingMaterial"] = True
    soil["terrainItem"] = by_like[soil_id]["terrainItem"]  # its own item's GUID
    want["terrainConfigs"] += sorted([ore, soil], key=lambda c: [x["terrainMaterial"]["value"] for x in new].index(c["terrainMaterial"]["value"]))
    check("registry.as-asked", want == merged["registry"])
    check("registry.soil-drops-own-item", soil["terrainItem"] not in {c["terrainItem"] for c in game["registry"]["terrainConfigs"]}, soil["terrainItem"])

    # world: a GUID at each new id, nothing else
    w = copy.deepcopy(game["world"])
    zero = "00000000-0000-0000-0000-000000000000"
    for i in (ore_id, soil_id):
        w["materialGuids"][i] = merged["world"]["materialGuids"][i]
    check("world.as-asked", w == merged["world"] and all(merged["world"]["materialGuids"][i] != zero for i in (ore_id, soil_id)))

    # pools: the soil's item after the terrain pool's own
    p = copy.deepcopy(game["pools"])
    tp = next(x for x in p["materialPools"] if x["debugName"] == "TerrainMaterials")
    mp = next(x for x in merged["pools"]["materialPools"] if x["debugName"] == "TerrainMaterials")
    tp["materials"].append(mp["materials"][-1])
    check("pools.as-asked", p == merged["pools"], f"{len(mp['materials'])} items in TerrainMaterials")

    # per-id tables: each new id takes its look's entries
    v = copy.deepcopy(game["vfx"])
    v["materialType"][ore_id] = v["materialType"][ORE[2]]
    v["materialType"][soil_id] = v["materialType"][SOIL[2]]
    check("vfx.as-asked", v == merged["vfx"])

    def u32s(block):
        b = bytes.fromhex(block["data"]["bytes"])
        return list(struct.unpack(f"<{len(b) // 4}I", b))

    b = copy.deepcopy(game["blend"])
    e = u32s(b["bakingVoxelMaterialDataBlock"])
    e[ore_id], e[soil_id] = e[ORE[2]], e[SOIL[2]]
    b["bakingVoxelMaterialDataBlock"]["data"]["bytes"] = struct.pack(f"<{len(e)}I", *e).hex()
    check("blend.as-asked", b == merged["blend"], f"id {ore_id}: {e[ore_id]:#x} (first layer {e[ore_id] & 0xffff}, {e[ore_id] >> 16} layers), id {soil_id}: {e[soil_id]:#x}")
    s = copy.deepcopy(game["simple"])
    e = u32s(s["layerInfosDataBlock"])
    for new_id, look in ((ore_id, ORE[2]), (soil_id, SOIL[2])):
        e[2 * new_id], e[2 * new_id + 1] = e[2 * look], e[2 * look + 1]
    s["layerInfosDataBlock"]["data"]["bytes"] = struct.pack(f"<{len(e)}I", *e).hex()
    check("simple.as-asked", s == merged["simple"])

    # every merged table through kfc's encoder: the same value back, and a stable encoding
    for k, t in TYPES.items():
        ty = types.get(t)
        once = enc.encode(ty, merged[k])
        again = dec.decode(ty, once)
        check(f"roundtrip.{k}", again == merged[k] and enc.encode(ty, again) == once, f"{len(once)} bytes")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
