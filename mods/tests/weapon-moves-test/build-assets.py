#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""Builds the lab test's assets: the sample's motions for its player states (assets/player-states.json) and the sample's moving blade (mods/samples/WeaponMoves/make_assets.py, no game data) into
assets/src/, then this mod's own recipe (assets/cook.json), cooked from the installed game into assets/cooked/ (gitignored, never committed).

    python3 mods/tests/weapon-moves-test/build-assets.py [--game DIR]
"""
import importlib.util
import subprocess
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
ROOT = HERE.parent.parent.parent
SAMPLE = ROOT / "mods/samples/WeaponMoves/make_assets.py"
src = HERE / "assets" / "src"
src.mkdir(parents=True, exist_ok=True)
# the sample's own writer, into this mod's folder: the blade skinned to its three joints with its Spin loop, and the same blade in one piece
spec = importlib.util.spec_from_file_location("weapon_moves_make_assets", SAMPLE)
make = importlib.util.module_from_spec(spec)
spec.loader.exec_module(make)
make.fighter(src / "fighter.gltf")   # the rig and motions the test's player states play (retargeted by assets/cook.json)
make.blade(src / "blade.gltf", skinned=True)
make.blade(src / "blade_rigid.gltf", skinned=False)
print("wrote", ", ".join(p.name for p in sorted(src.glob("*.gltf"))))
cmd = [sys.executable, str(ROOT / "tools/modelcook/modelcook.py")] + sys.argv[1:] + ["recipe", str(HERE / "assets/cook.json"), "--update-manifest"]
rc = subprocess.call(cmd)
# assets/player-states.json has one state that must be refused (wm-nowhere: its like is no state of the game's), so the cook's check of
# the file exits 1 on purpose. Every other state has to be live, and the cooked clips and the manifest have to be there.
report = HERE / "assets/cooked/player-states.json"
if rc != 0 and report.exists():
    import json
    states = json.loads(report.read_text()).get("states", {})
    bad = {k: v.get("status") for k, v in states.items() if (v.get("status") == "live") == (k == "wm-nowhere")}
    print("player states:", ", ".join(f"{k} {v.get('status')}" for k, v in states.items()))
    rc = 0 if not bad and (HERE / "assets/assets.json").exists() else rc
sys.exit(rc)
