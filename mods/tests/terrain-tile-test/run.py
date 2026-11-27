#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""Baked terrain on a lab server, player free: cook plan.json against the lab's own game files (tools/kfc terrain
cook), put the pack into the mod's folder, one server start, read the mod's PASS/FAIL lines, stop.

    KILN_OUT=<build dir> mods/tests/terrain-tile-test/run.py --lab <lab> [--out DIR] [--start "<command>"]

Before it: build the mod into $KILN_OUT/mods/terrain-tile-test, make a fresh lab (`tools/lab/lab.sh new <lab> <port>`,
no save: a fresh world has no stored edits over the tiles) and deploy Kiln with `tools/deploy-server.sh`. --start
replaces `tools/lab/lab.sh start` (a wrapper that queues lab starts takes the lab's name as its one argument).
Proves on the server: an explicit-hash blob under the game's own ContentHash is served, the server reads tiles Kiln's
writer made exactly as the pack holds them (changed, re-encoded and untouched columns, across a tile seam), a shipped
blob is reused by hash from another resource's tiles, and an untouched tile is unchanged. Exits non-zero on a FAIL, a
missing line, or a "Could not find content" line in the game's log. Dedicated lab servers only (the pack's coarser
levels and companions are stale; no client may join).
"""
import argparse
import hashlib
import json
import os
import re
import shlex
import shutil
import subprocess
import sys
import time
from pathlib import Path

HERE = Path(__file__).resolve().parent
REPO = HERE.parents[2]
MOD = "terrain-tile-test"
LEAN = shutil.ignore_patterns("Kiln.*", "*.deps.json", "*.runtimeconfig.json")  # kiln/host has them
CHECKS = ["random", "pillar", "pillar-base", "untouched-983", "seam-983-984", "air-tile-1063", "control-984"]

sys.path.insert(0, str(REPO / "tools/kfc"))
from kfc import terraincook  # noqa: E402
from kfc.archive import Kfc  # noqa: E402


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--lab", required=True)
    ap.add_argument("--out", default=None)
    ap.add_argument("--start", default=None, help="command that starts the lab instead of tools/lab/lab.sh start")
    ap.add_argument("--seconds", type=int, default=900, help="how long the start may take")
    a = ap.parse_args()
    build = Path(os.environ["KILN_OUT"])
    lab = Path(os.environ.get("KILN_LABS", Path.home() / "srv/enshrouded/labs")) / a.lab
    kiln = lab / "kiln"
    out = Path(a.out) if a.out else build / MOD
    out.mkdir(parents=True, exist_ok=True)
    tool = str(REPO / "tools/lab/lab.sh")
    start = shlex.split(a.start) + [a.lab] if a.start else [tool, "start", a.lab]
    if any((lab / "savegame").glob("*")):
        sys.exit(f"{lab}/savegame is not empty: this test needs a fresh world (no stored edits over its tiles)")

    subprocess.run([tool, "stop", a.lab], capture_output=True)
    mod = kiln / "mods" / MOD
    shutil.rmtree(mod, ignore_errors=True)
    shutil.copytree(build / "mods" / MOD, mod, ignore=LEAN)
    shutil.rmtree(kiln / "data" / MOD, ignore_errors=True)
    # the pack, from this lab's own game files (a fresh Kfc: the lab's archive as the server will read it)
    exe = lab / "enshrouded_server.exe"
    sha = hashlib.sha256(exe.read_bytes()).hexdigest()
    pack = out / "pack"
    shutil.rmtree(pack, ignore_errors=True)
    k = Kfc.unpatched(lab) or Kfc(lab)
    receipt = terraincook.cook(k, json.loads((HERE / "plan.json").read_text()), pack, exe_sha256=sha)
    k.close()
    shutil.copytree(pack / "assets", mod / "assets")
    shutil.copy2(pack / "receipt.json", mod / "receipt.json")
    print(f"== pack: {[(t['index'], t['new'], t['reusesShippedBlob']) for t in receipt['tiles']]}", flush=True)

    log = kiln / "logs/kiln.log"
    mark = log.stat().st_size if log.exists() else 0
    game_log = lab / "logs/enshrouded_server.log"  # the server writes it anew at each start (old ones to logs/backup)
    started = time.time()
    failed = 0
    try:
        r = subprocess.run(start, cwd=REPO)
        if r.returncode:
            sys.exit(f"start failed ({r.returncode})")
        t0 = time.time()
        text = ""
        while time.time() - t0 < a.seconds:
            text = log.read_bytes()[mark:].decode("utf-8", "replace") if log.exists() else ""
            if f"{MOD}: READY" in text:
                break
            if not subprocess.run([tool, "pid", a.lab], capture_output=True, text=True).stdout.strip():
                print("== the server ended", flush=True)
                break
            time.sleep(2)
    finally:
        subprocess.run([tool, "stop", a.lab])
    text = log.read_bytes()[mark:].decode("utf-8", "replace") if log.exists() else ""
    (out / "kiln.log").write_text(text)
    for f in (kiln / "data" / MOD).glob("*.bin") if (kiln / "data" / MOD).exists() else []:
        shutil.copy2(f, out / f.name)
    shown = re.compile(r"assets: .*terrain-tile-test|assets: (ready|bound|content map)")
    lines = [ln for ln in text.splitlines() if f"{MOD}:" in ln or shown.search(ln)]
    for ln in lines:
        print(ln, flush=True)
    assets = re.search(r"terrain-tile-test: icons 0, textures 0, resources 1, blobs 1, errors 0", text)
    if not assets:
        print("FAIL kiln.assets did not report the pack as 1 resource, 1 blob, 0 errors", flush=True)
        failed += 1
    for name in CHECKS:
        if not re.search(rf"{MOD}: PASS {re.escape(name)} ", text):
            print(f"FAIL check {name}: no PASS line", flush=True)
            failed += 1
    if re.search(rf"{MOD}: FAIL", text) or f"{MOD}: READY" not in text:
        failed += 1
    # the game's own word that a reference had no bytes (afa1bece's lab failure: a blob under a key it did not ask for)
    fresh = game_log.exists() and game_log.stat().st_mtime >= started
    game = game_log.read_bytes().decode("utf-8", "replace") if fresh else ""
    if not game:
        print(f"FAIL no game log of this start at {game_log}", flush=True)
        failed += 1
    for ln in [ln for ln in game.splitlines() if "Could not find content" in ln][:5]:
        print(f"FAIL game log: {ln.strip()}", flush=True)
        failed += 1
    print(f"== {'FAILED' if failed else 'PASSED'} (kiln.log in {out})", flush=True)
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
