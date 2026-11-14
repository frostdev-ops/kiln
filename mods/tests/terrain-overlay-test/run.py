#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""Kiln.Terrain overlays on a lab server, player free: three server starts (fresh world, the same world after a restart,
another world), driven by the mod's READY and DONE lines.

    KILN_OUT=<build dir> mods/tests/terrain-overlay-test/run.py --lab <lab> [--out DIR] [--start "<command>"]

Before it: build the mod into $KILN_OUT/mods/terrain-overlay-test, make the lab with `tools/lab/lab.sh new <lab> <port>
--save <a save with a base>` and deploy Kiln with `tools/deploy-server.sh`. --start replaces `tools/lab/lab.sh start`
(a wrapper that queues lab starts takes the lab's name as its one argument). Prints the mod's PASS/FAIL lines, writes
each start's kiln.log to --out, and exits non-zero on a FAIL or a missing line.
"""
import argparse
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
MOD = "terrain-overlay-test"
LEAN = shutil.ignore_patterns("Kiln.*", "*.deps.json", "*.runtimeconfig.json")  # kiln/host has them
WANT = {
    1: ["overlay.inspect", "overlay.applied", "overlay.transient-not-recorded", "overlay.base-refused",
        "overlay.editing-handler-edit-kept"],
    2: ["overlay.kept-after-restart", "overlay.rerun-noop", "overlay.upgrade-conflict"],
    3: ["overlay.world-b-clean"],
}


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--lab", required=True)
    ap.add_argument("--out", default=None)
    ap.add_argument("--start", default=None, help="command that starts the lab instead of tools/lab/lab.sh start")
    ap.add_argument("--seconds", type=int, default=600, help="how long one start may take")
    a = ap.parse_args()
    build = Path(os.environ["KILN_OUT"])
    lab = Path(os.environ.get("KILN_LABS", Path.home() / "srv/enshrouded/labs")) / a.lab
    kiln = lab / "kiln"
    out = Path(a.out) if a.out else build / MOD
    out.mkdir(parents=True, exist_ok=True)
    tool = str(REPO / "tools/lab/lab.sh")
    start = shlex.split(a.start) + [a.lab] if a.start else [tool, "start", a.lab]

    subprocess.run([tool, "stop", a.lab], capture_output=True)
    shutil.rmtree(kiln / "mods" / MOD, ignore_errors=True)
    shutil.copytree(build / "mods" / MOD, kiln / "mods" / MOD, ignore=LEAN)
    shutil.rmtree(kiln / "data" / MOD, ignore_errors=True)
    save, aside = lab / "savegame", lab / "savegame-world-a"
    if aside.exists():
        sys.exit(f"{aside} exists: an earlier run stopped half way; put the save back by hand first")

    failed = 0
    try:
        for boot in (1, 2, 3):
            if boot == 3:
                save.rename(aside)          # the server makes a new world
            log = kiln / "logs/kiln.log"
            log.unlink(missing_ok=True)
            print(f"== boot {boot}: {subprocess.run(start, capture_output=True, text=True, cwd=REPO).stdout.strip()}", flush=True)
            end = time.time() + a.seconds
            text = ""
            while time.time() < end:
                text = log.read_text(errors="replace") if log.exists() else ""
                if re.search(rf"{MOD}: (READY stop|DONE)", text):
                    break
                time.sleep(1)
            time.sleep(2)
            subprocess.run([tool, "stop", a.lab], capture_output=True)
            text = log.read_text(errors="replace") if log.exists() else ""
            (out / f"kiln-boot{boot}.log").write_text(text)
            lines = [l.split(f"{MOD}: ", 1)[1] for l in text.splitlines() if f"{MOD}: " in l]
            for l in lines:
                if l.startswith(("PASS", "FAIL", "INFO overlay.budget")):
                    print(l, flush=True)
            failed += sum(l.startswith("FAIL") for l in lines)
            for name in WANT[boot]:
                if not any(re.match(rf"PASS {re.escape(name)}( |$)", l) for l in lines):
                    failed += 1
                    print(f"MISSING {name} (boot {boot})", flush=True)
            if not any(l.startswith(("READY stop", "DONE")) for l in lines):
                print(f"boot {boot} did not finish in {a.seconds} s", flush=True)
                failed += 1
                break
    finally:
        subprocess.run([tool, "stop", a.lab], capture_output=True)
        if aside.exists():
            shutil.rmtree(lab / "savegame-world-b", ignore_errors=True)
            if save.exists():
                save.rename(lab / "savegame-world-b")
            aside.rename(save)
    print(f"{'FAILED' if failed else 'ok'}: logs in {out}", flush=True)
    sys.exit(1 if failed else 0)


if __name__ == "__main__":
    main()
