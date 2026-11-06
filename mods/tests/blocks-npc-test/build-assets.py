#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""Builds the test's assets on your machine, with no game data: a rigged figure written by a script (tools/modelcook/samples/rigged_figure.py) as
models/stoneling.glb, then the cook of the mod's blocks (its animations and bodies) into assets/cooked.

    python3 mods/tests/blocks-npc-test/build-assets.py
"""
import subprocess
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
ROOT = HERE.parent.parent.parent
(HERE / "models").mkdir(exist_ok=True)
subprocess.check_call([sys.executable, str(ROOT / "tools/modelcook/samples/rigged_figure.py"), str(HERE / "models/stoneling.glb")])
subprocess.check_call([sys.executable, str(ROOT / "tools/animcook/animcook.py"), "recipe", str(HERE / "assets/cook.json"), "--update-manifest"])
