#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""Builds the lab test's held models with moving parts: the sample's moving blade (mods/samples/WeaponMoves/make_assets.py, no game data) into
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
make.blade(src / "blade.gltf", skinned=True)
make.blade(src / "blade_rigid.gltf", skinned=False)
print("wrote", ", ".join(p.name for p in sorted(src.glob("*.gltf"))))
cmd = [sys.executable, str(ROOT / "tools/modelcook/modelcook.py")] + sys.argv[1:] + ["recipe", str(HERE / "assets/cook.json"), "--update-manifest"]
sys.exit(subprocess.call(cmd))
