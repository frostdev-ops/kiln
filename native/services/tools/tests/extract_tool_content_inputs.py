#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""Write the game inputs of test-tool-content.sh into a scratch folder, read-only from a game folder (tools/kfc):
types.json (the exe's reflection), brush.bin (Building_Overgrowth_Shape_Small's ItemInfo, the brush base), shape-medium.bin and
shape-big.bin (the vanilla shapes whose cursor preview larger brushes take), pool-item.bin
(a game item naming the vanilla TerrainMaterials pool, so the pool is a loaded reference) and registry.bin (the
ItemRegistryResource, the item tags the game knows). The game's own archive is read when a mod loader patched it.

    python3 -I extract_tool_content_inputs.py <repo>/tools/kfc <game folder> <exe> <out folder>
"""
import json
import sys
from pathlib import Path

sys.path.insert(0, sys.argv[1])
from kfc.archive import Kfc, ResourceKey  # noqa: E402
from kfc.blob import Decoder  # noqa: E402
from kfc.refl import TypeDb  # noqa: E402

game, exe, out = Path(sys.argv[2]).expanduser(), Path(sys.argv[3]), Path(sys.argv[4])
out.mkdir(parents=True, exist_ok=True)
types = TypeDb.from_exe(exe)
(out / 'types.json').write_text(json.dumps(types.dump))
archive = Kfc.unpatched(game) or Kfc(game)
item = types.get('keen::ItemInfo')['hash']
BRUSH = '54adf19c-7f45-418a-b9bc-665fb24c8561'  # Building_Overgrowth_Shape_Small
POOL = '256c43ba-dd20-4369-b3bf-f49c671a59ec'   # TerrainMaterials
(out / 'brush.bin').write_bytes(archive.read(ResourceKey(BRUSH, item, 0)))
# The vanilla shapes a brush takes its cursor preview from (kiln#153): Building_Overgrowth_Shape_Medium and _Big.
(out / 'shape-medium.bin').write_bytes(archive.read(ResourceKey('07b0f7d5-aa90-4332-9fa9-61b9faba4e40', item, 0)))
(out / 'shape-big.bin').write_bytes(archive.read(ResourceKey('a38fd04c-3715-4b2f-9d2e-034cd0200e54', item, 0)))
dec = Decoder(types)
for key in archive.resources_of_type(item):
    raw = archive.read(key)
    if dec.decode(item, raw).get('equipment', {}).get('voxelBlueprintMaterialPool') == POOL:
        (out / 'pool-item.bin').write_bytes(raw)
        break
else:
    raise SystemExit('no game item names the TerrainMaterials pool')
registry = types.get('keen::ItemRegistryResource')['hash']
keys = list(archive.resources_of_type(registry))
if len(keys) != 1:
    raise SystemExit(f'expected one ItemRegistryResource, found {len(keys)}')
(out / 'registry.bin').write_bytes(archive.read(keys[0]))
print(f'tool content inputs in {out}')
