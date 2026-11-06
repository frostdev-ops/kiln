#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""Building surfaces on a lab server, player free. One server start per scenario, in order. A server has no surface tables, so this is what every side
shares: material blocks with a surface take kiln.worldtables' slots, keep their ids across restarts, a block from C# reads the same as its JSON twin,
and a wrong surface is refused by name. The pictures are a client's: tools/surfacecook/tests checks their bytes offline.

    KILN_OUT=<build dir> mods/tests/surfaces-test/run.py --lab mylab [--out DIR] [--only NAME[,NAME]] [--low]

Before it: build this driver into $KILN_OUT/mods/surfaces-test and mods/samples/SurfaceSample into $KILN_OUT/mods/surface-sample, make the lab with
`tools/lab/lab.sh new` and a save, and deploy with `tools/deploy-server.sh`. Prints PASS/FAIL lines, writes each start's kiln.log to --out, and exits
non-zero on a FAIL.
"""
import argparse
import hashlib
import json
import os
import re
import shutil
import subprocess
import sys
import time
import uuid
from pathlib import Path

HERE = Path(__file__).resolve().parent
REPO = HERE.parents[2]
NS = uuid.UUID("6f6d2a44-53c1-5c52-9d51-0a3c6e1f2b77")
SAMPLE = "surface-sample"


def material_guid(mod, name, kind="material"):
    """BlockIds.Derive(mod, kind, name)."""
    h = bytearray(hashlib.sha1(NS.bytes + f"{mod}:{kind}:{name}".encode()).digest()[:16])
    h[6] = h[6] & 0x0f | 0x50
    h[8] = h[8] & 0x3f | 0x80
    return str(uuid.UUID(bytes=bytes(h)))


# Wrong on purpose, so it's written here rather than kept as a fixture the repository's schema test reads. Every block but "fine" is refused.
BAD = {
    "format": 1,
    "buildingMaterials": {
        "bad-tint": {"name": "Bad Tint", "surface": {"side": {"albedo": "textures/a.png", "tint": "#12345"}}},
        "bad-field": {"name": "Bad Field", "surface": {"side": {"albedo": "textures/a.png", "shine": 1}}},
        "no-side": {"name": "No Side", "surface": {"top": {"albedo": "textures/a.png"}}},
        "outside": {"name": "Outside", "surface": {"side": {"albedo": "../other-mod/a.png"}}},
        "bad-range": {"name": "Bad Range", "surface": {"side": {"albedo": "textures/a.png", "roughness": 1.5}}},
        "fine": {"name": "Fine", "surface": {"side": {"albedo": "textures/a.png", "roughness": 0.5}}},
        "own-block": {"name": "Own Block", "strength": {"drops": "block"}, "recipe": {"makes": 10, "needs": {"Build_TerrainMaterial_T1_Stone": 1}}},
        "no-such-look": {"name": "No Such Look", "surface": "nowhere"},
    },
    "materialTweaks": {
        "bad-tweak": {"material": "WoodRough", "surface": {"bevel": 0.1}},
        "bad-drops": {"material": "WoodRough", "strength": {"drops": "block"}},
    },
}
STONE = "8870fa83-04e6-4b5a-ac2a-ea13205ce469"  # Build_TerrainMaterial_T1_Stone, what digging Rough Stone gives
BAD_NAMES = {
    "bad-tint": "surface.side.tint",
    "bad-field": "surface.side.shine",
    "no-side": "surface.side",
    "outside": "surface.side.albedo",
    "bad-range": "surface.side.roughness",
    "bad-tweak": "surface",
    "bad-drops": "strength.drops",
    "no-such-look": "surface",
}
LEAN = __import__("shutil").ignore_patterns("Kiln.*", "*.deps.json", "*.runtimeconfig.json")  # what deploy-server.sh leaves out: kiln/host has them


class Lab:
    def __init__(self, name, out):
        self.name = name
        self.dir = Path(os.environ.get("KILN_LABS", Path.home() / "srv/enshrouded/labs")) / name
        self.kiln = self.dir / "kiln"
        self.out = out
        self.build = Path(os.environ["KILN_OUT"])

    low = False

    def tool(self, *args):
        cmd = [str(REPO / "tools/lab/lab.sh"), *args]
        if self.low and args[0] == "start":
            # Wine raises its threads' priority again, so the nice limit has to be set too
            cmd = ["bash", "-c", 'ulimit -e 1; exec nice -n 19 ionice -c 3 "$@"', "lab", *cmd]
        return subprocess.run(cmd, capture_output=True, text=True).stdout.strip()

    def set_mods(self, sample=True, extra=(), dig=None):
        for d in list(self.kiln.glob("mods/sf-*")) + [self.kiln / "mods" / m for m in (SAMPLE, "surfaces-test", "worldtables-test")]:
            shutil.rmtree(d, ignore_errors=True)
        shutil.copytree(self.build / "mods/surfaces-test", self.kiln / "mods/surfaces-test", ignore=LEAN)
        if sample:
            shutil.copytree(self.build / "mods" / SAMPLE, self.kiln / "mods" / SAMPLE, ignore=LEAN)
        if dig:
            # kiln.worldtables' own lab driver: the game's TNT on a cube of every material in the tables
            shutil.copytree(self.build / "mods/worldtables-test", self.kiln / "mods/worldtables-test", ignore=LEAN)
            (self.kiln / "data/worldtables-test").mkdir(parents=True, exist_ok=True)
            (self.kiln / "data/worldtables-test/mode").write_text(dig)
        for m, doc in extra:
            d = self.kiln / "mods" / m
            (d / "blocks").mkdir(parents=True)
            (d / "kiln.mod.json").write_text(json.dumps({"id": m, "name": m, "version": "1.0.0", "apiVersion": "0.1", "side": "both", "clients": "mod"}))
            (d / "blocks" / f"{m}.json").write_text(json.dumps(doc))

    def forget_mods(self, prefixes):
        """Drop what the worlds remember of these mods' materials, so their slots are free again."""
        for f in self.kiln.glob("data/worldtables/worlds/*.json"):
            d = json.loads(f.read_text())
            d["materials"] = {g: m for g, m in d["materials"].items() if not any(m["mod"].startswith(p) for p in prefixes)}
            f.write_text(json.dumps(d))

    def run(self, scenario, seconds=420, until=("sf: done",)):
        log = self.kiln / "logs/kiln.log"
        log.unlink(missing_ok=True)
        print(f"== {scenario}: {self.tool('start', self.name)}", flush=True)
        end = time.time() + seconds
        text = ""
        while time.time() < end:
            text = log.read_text(errors="replace") if log.exists() else ""
            if all(u in text for u in until):
                break
            time.sleep(1)
        time.sleep(2)
        self.tool("stop", self.name)
        text = log.read_text(errors="replace") if log.exists() else ""
        self.out.mkdir(parents=True, exist_ok=True)
        (self.out / f"kiln-{scenario}.log").write_text(text)
        return text


class Checks:
    def __init__(self):
        self.failed = self.passed = 0

    def __call__(self, name, ok, detail=""):
        self.passed += bool(ok)
        self.failed += not ok
        print(f"{'PASS' if ok else 'FAIL'} {name}{': ' + detail if detail else ''}", flush=True)


def sf(text, pattern):
    return [l.split("sf: ", 1)[1] for l in text.splitlines() if "sf: " in l and re.search(pattern, l.split("sf: ", 1)[1])]


def table(text):
    """block -> (cell id, live or kept) of every material in the tables."""
    return {m[2]: (int(m[0]), m[3]) for m in re.findall(r"sf: table: (\d+) (\S+) (\S+) \(\S+\) (live|kept)", text)}


def block(text, name):
    m = re.search(rf"sf: block {re.escape(name)}: status (\w+), cell (-?\d+)", text)
    return (m[1], int(m[2])) if m else None


def scenarios(lab, c):
    ids = {}

    def first():
        # a clean start: nothing kept from code, no world remembering these mods' materials
        for d in [lab.kiln / "data/worldtables/pending" / SAMPLE, *lab.kiln.glob("data/worldtables/pending/sf-*")]:
            shutil.rmtree(d, ignore_errors=True)
        lab.forget_mods(["sf-", SAMPLE])
        lab.set_mods(extra=[("sf-bad", BAD)])
        text = lab.run("first")
        t = table(text)
        c("first.json-blocks-take-slots", all(t.get(n, (0, ""))[1] == "live" and 213 <= t[n][0] <= 255 for n in ("moss-brick-json", "rune-stone-json", "fine")), str(t))
        for n in ("moss-brick-json", "rune-stone-json"):
            c(f"first.{n}-registered", block(text, n) == ("Registered", t.get(n, (-1,))[0]), str(block(text, n)))
        c("first.retexture-listed", "sf: tweak listed: 143 snowy-logs-json (surface-sample)" in text, str(sf(text, "tweak listed")))
        c("first.retexture-registered", (block(text, "snowy-logs-json") or ("",))[0] == "Registered", str(block(text, "snowy-logs-json")))
        for n in ("moss-brick", "rune-stone"):
            c(f"first.{n}-from-code-waits", (block(text, n) or ("",))[0] == "Pending", f"{block(text, n)}: the game read its tables before the mod's code ran")
        for n, field in BAD_NAMES.items():
            kind = "materialTweak" if n in BAD["materialTweaks"] else "buildingMaterial"
            said = [l for l in text.splitlines() if "worldtables:" in l and re.search(rf"block {kind} {re.escape(n)}( \(mod sf-bad\))?, {re.escape(field)}\b", l)]
            c(f"first.refuses-{n}-by-name", bool(said) and n not in t, said[0].split("] ", 1)[-1] if said else "no line")
        writer = [l for l in text.splitlines() if re.search(r"surfaces: .*slot \d+ \(cell \d+\)", l)]
        c("first.no-surfaces-on-a-server", not writer, "kiln.surfaces writes nothing on a dedicated server" if not writer else writer[0])

    def second():
        lab.set_mods(extra=[("sf-bad", BAD)])
        text = lab.run("second")
        t = table(text)
        for n in ("moss-brick", "rune-stone"):
            c(f"second.{n}-from-code-registered", (block(text, n) or ("",))[0] == "Registered" and t.get(n, (0, ""))[1] == "live", f"{block(text, n)} {t.get(n)}")
        for n in ("moss", "moss-brick", "rune-stone", "snowy-logs"):
            c(f"second.twin-{n}", f"sf: twin {n}: same True" in text, str(sf(text, f"twin {n}")))
        ids.update(t)
        c("second.guids-follow-names", f" {material_guid(SAMPLE, 'moss-brick')} moss-brick " in text, material_guid(SAMPLE, "moss-brick"))

    def third():
        lab.set_mods(extra=[("sf-bad", BAD)])
        text = lab.run("third")
        t = table(text)
        c("third.ids-stable", t == ids and len(t) >= 5, f"{t} against {ids}")
        for n in ("moss-brick", "rune-stone", "moss-brick-json", "rune-stone-json"):
            c(f"third.{n}-registered", block(text, n) == ("Registered", ids.get(n, (-1,))[0]), str(block(text, n)))

    def dig():
        lab.set_mods(extra=[("sf-bad", BAD)], dig="status+blast-new")
        text = lab.run("dig", until=("sf: done", "wt: done"))
        loot = {m[1]: (m[2], int(m[3])) for m in re.findall(r"worldtables: material (\d+) \S+ (\S+) \(mod [^,]+, live\): .*drops (\S+) per (\d+) blocks", text)}
        c("dig.moss-brick-gives-stone", loot.get("moss-brick-json") == (STONE, 2), f"{loot.get('moss-brick-json')}: its recipe makes 20 from 10 stone")
        c("dig.rune-stone-gives-stone", loot.get("rune-stone-json") == (STONE, 1), f"{loot.get('rune-stone-json')}: 10 from 10")
        own = material_guid("sf-bad", "own-block", "item")
        c("dig.own-block-when-asked", loot.get("own-block") == (own, 1), f"{loot.get('own-block')}, its block item {own}")
        c("dig.loot-from-code-too", loot.get("moss-brick") == (STONE, 2), str(loot.get("moss-brick")))
        # the game's TNT does 1500 to hard (Moss Brick, health 1200) and 2000 to moderately hard (Rough Stone's 250, what Rune Stone starts from)
        for n, want in (("moss-brick-json", lambda r: r > 0), ("rune-stone-json", lambda r: r == 504), ("moss-brick", lambda r: r > 0)):
            m = re.search(rf"blast Terraform_Explosion_TNT_T1 on {re.escape(n)} \(cell (\d+)\): solid (\d+), removed (\d+)", text)
            c(f"dig.tnt-{n}", m is not None and want(int(m[3])), m[0] if m else "no blast line")

    def limit():
        lab.forget_mods(["sf-", SAMPLE])
        over = {f"s{i:02}": {"name": f"Surface {i}", "surface": {"side": {"game": "StoneRough"}, "bevel": 0.1}} for i in range(50)}
        lab.set_mods(sample=False, extra=[("sf-over", {"format": 1, "buildingMaterials": over})])
        text = lab.run("limit")
        t = {n: v for n, v in table(text).items() if re.fullmatch(r"s\d\d", n)}
        c("limit.49-placed", len(t) == 49, f"{len(t)} placed")
        refused = [l.split("] ", 1)[-1] for l in text.splitlines() if "worldtables:" in l and "refused: the game has room for 49" in l]
        c("limit.refuses-by-name", len(refused) == 1 and re.search(r"block buildingMaterial s\d\d \(mod sf-over\): refused", refused[0]) is not None, str(refused))
        c("limit.server-runs", "sf: done" in text)
        lab.forget_mods(["sf-over"])

    return [("first", first), ("second", second), ("third", third), ("dig", dig), ("limit", limit)]


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--lab", required=True)
    ap.add_argument("--out", default=None, help="where each start's kiln.log goes (default $KILN_OUT/surfaces-test)")
    ap.add_argument("--only", default="")
    ap.add_argument("--low", action="store_true", help="run the lab server at the lowest CPU and IO priority")
    a = ap.parse_args()
    out = Path(a.out) if a.out else Path(os.environ["KILN_OUT"]) / "surfaces-test"
    lab = Lab(a.lab, out)
    lab.low = a.low
    c = Checks()
    only = set(filter(None, a.only.split(",")))
    try:
        for name, fn in scenarios(lab, c):
            if only and name not in only:
                continue
            fn()
    finally:
        lab.tool("stop", a.lab)
    print(f"{c.passed} passed, {c.failed} failed")
    return 1 if c.failed else 0


if __name__ == "__main__":
    sys.exit(main())
