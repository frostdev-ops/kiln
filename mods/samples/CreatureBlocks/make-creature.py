#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""Makes this sample's creature with no game data: a script writes a rigged figure (skeleton, skinned mesh, Idle, Walk, Run, Attack, Dead, Flinch) as
models/stoneling.glb, then the cook turns the animation and body of blocks/stoneling.json into the game's formats. `kiln build` runs the cook by itself;
the figure stands in for what you would export from Blender.

    python3 mods/samples/CreatureBlocks/make-creature.py
"""
import subprocess
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
ROOT = HERE.parent.parent.parent
(HERE / "models").mkdir(exist_ok=True)
subprocess.check_call([sys.executable, str(ROOT / "tools/modelcook/samples/rigged_figure.py"), str(HERE / "models/stoneling.glb")])
subprocess.check_call([sys.executable, str(ROOT / "tools/animcook/animcook.py"), "recipe", str(HERE / "assets/cook.json"), "--update-manifest"])
