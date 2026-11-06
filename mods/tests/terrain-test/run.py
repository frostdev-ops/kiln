#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""Terrain materials on a lab server, player free. One server start per scenario, in order: terrain blocks take kiln.worldtables' terrain ids (a room apart from
the building materials'), keep them across restarts, a block from C# lowers to what its JSON twin does, wrong blocks are refused by name, cells written through
Kiln.Build read back, the game's own explosion digs them by their strength, the served registry says what digging gives (decoded with tools/kfc), a world keeps
their cells when their mod is removed, and the 12th is refused by name. How they look is a client's: native/services/worldtables/tests checks those tables offline.

    KILN_OUT=<build dir> mods/tests/terrain-test/run.py --lab mylab [--out DIR] [--only NAME[,NAME]] [--low]

Before it: build this driver into $KILN_OUT/mods/terrain-test and mods/samples/TerrainSample into $KILN_OUT/mods/terrain-sample, make the lab with
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
SAMPLE, DATA, BAD, OVER = "terrain-sample", "tt-data", "tt-bad", "tt-over"
LEAN = shutil.ignore_patterns("Kiln.*", "*.deps.json", "*.runtimeconfig.json")  # what deploy-server.sh leaves out: kiln/host has them


def guid(mod, name, kind="terrain"):
    """BlockIds.Derive(mod, kind, name)."""
    h = bytearray(hashlib.sha1(NS.bytes + f"{mod}:{kind}:{name}".encode()).digest()[:16])
    h[6] = h[6] & 0x0f | 0x50
    h[8] = h[8] & 0x3f | 0x80
    return str(uuid.UUID(bytes=bytes(h)))


# Strengths the explosion tells apart, a like with buffs of its own (tar), and a building material in the same start.
GOOD = {
    "format": 1,
    "terrainMaterials": {
        "tt-hard": {"name": "TT Hard", "like": "Cell26", "strength": {"hardness": "unbreakable"}},
        "tt-soft": {"name": "TT Soft", "like": "TerrainStone", "strength": {"hardness": "soft", "health": 1}},
        "tt-vhard": {"name": "TT Very Hard", "like": "TerrainStone", "strength": {"hardness": "veryHard", "health": 1000}},
        "tt-tar": {"name": "TT Tar", "like": "Cell51", "flatten": True},
    },
    "buildingMaterials": {"tt-brick": {"name": "TT Brick"}},
}
# Wrong on purpose, so it's written here rather than kept as a fixture the repository's schema test reads. Each is refused by its field.
WRONG = {
    "format": 1,
    "terrainMaterials": {
        "bad-field": {"name": "B", "shine": 1},
        "bad-like": {"name": "B", "like": "WoodRough"},
        "no-such": {"name": "B", "look": "NoSuchMaterial"},
        "bad-recipe": {"name": "B", "recipe": {"needs": {"Material_T1_Twigs": 1}}},
        "bad-drops": {"name": "B", "strength": {"drops": "NoSuchItem"}},
    },
}
WRONG_FIELDS = {"bad-field": "shine", "bad-like": "like", "no-such": "look", "bad-recipe": "recipe", "bad-drops": "strength.drops"}


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

    def set_mods(self, mode, sample=True, extra=()):
        for d in list(self.kiln.glob("mods/tt-*")) + [self.kiln / "mods" / m for m in (SAMPLE, "terrain-test")]:
            shutil.rmtree(d, ignore_errors=True)
        shutil.copytree(self.build / "mods/terrain-test", self.kiln / "mods/terrain-test", ignore=LEAN)
        if sample:
            shutil.copytree(self.build / "mods" / SAMPLE, self.kiln / "mods" / SAMPLE, ignore=LEAN)
        for m, doc in extra:
            d = self.kiln / "mods" / m
            (d / "blocks").mkdir(parents=True)
            (d / "kiln.mod.json").write_text(json.dumps({"id": m, "name": m, "version": "1.0.0", "apiVersion": "0.1", "side": "both", "clients": "mod"}))
            (d / "blocks" / f"{m}.json").write_text(json.dumps(doc))
        (self.kiln / "data/terrain-test").mkdir(parents=True, exist_ok=True)
        (self.kiln / "data/terrain-test/mode").write_text(mode)

    def forget(self, prefixes):
        """Drop what the worlds remember of these mods' materials and what their code kept, so their ids are free again."""
        for f in self.kiln.glob("data/worldtables/worlds/*.json"):
            d = json.loads(f.read_text())
            d["materials"] = {g: m for g, m in d["materials"].items() if not any(m["mod"].startswith(p) for p in prefixes)}
            f.write_text(json.dumps(d))
        for p in prefixes:
            for d in self.kiln.glob(f"data/worldtables/pending/{p}*"):
                shutil.rmtree(d, ignore_errors=True)

    def run(self, scenario, seconds=480):
        log = self.kiln / "logs/kiln.log"
        log.unlink(missing_ok=True)
        print(f"== {scenario}: {self.tool('start', self.name)}", flush=True)
        end = time.time() + seconds
        text = ""
        while time.time() < end:
            text = log.read_text(errors="replace") if log.exists() else ""
            if re.search(r"tt: done ", text):
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
        print(f"{'PASS' if ok else 'FAIL'} {name}{': ' + str(detail) if detail else ''}", flush=True)


def tt(text, pattern):
    return [l.split("tt: ", 1)[1] for l in text.splitlines() if "tt: " in l and re.search(pattern, l.split("tt: ", 1)[1])]


def table(text):
    """block -> (cell id, live or kept, terrain or building) of every material in the tables."""
    return {m[2]: (int(m[0]), m[3], m[4]) for m in re.findall(r"tt: table: (\d+) (\S+) (\S+) \(\S+\) (live|kept) (terrain|building)", text)}


def block(text, name):
    m = re.search(rf"tt: block {re.escape(name)}: status (\w+), cell (-?\d+)", text)
    return (m[1], int(m[2])) if m else None


def blasts(text):
    return {m[1]: (int(m[2]), int(m[3]), int(m[4]), int(m[5])) for m in re.finditer(r"tt: blast \S+ on (.+?) \(cell (\d+)\): solid (\d+), removed (\d+), partly (\d+)", text)}


def decode_served(lab, names):
    """The tables the server served, as the game's reflection reads them (tools/kfc, with the lab's own exe)."""
    sys.path.insert(0, str(REPO / "tools/kfc"))
    from kfc.blob import Decoder
    from kfc.refl import TypeDb
    types = TypeDb.from_exe(lab.dir / "enshrouded_server.exe")
    dec = Decoder(types, max_array=1 << 26)
    t = {"registry": "keen::TerraformingEfficiencyRegistryResource", "vfx": "keen::VoxelMaterialVfxDataResource", "world": "keen::VoxelWorldResource",
         "pools": "keen::VoxelBlueprintMaterialPoolRegistryResource"}
    return {n: dec.decode(types.get(t[n]), (lab.kiln / "data/terrain-test" / f"served-{n}.bin").read_bytes()) for n in names}


def scenarios(lab, c):
    ids = {}

    def first():
        lab.forget(["tt-", SAMPLE])
        lab.set_mods("status+code-bad+dump", extra=[(DATA, GOOD), (BAD, WRONG)])
        text = lab.run("first")
        t = table(text)
        terrain = {n: v for n, v in t.items() if v[2] == "terrain"}
        want = ["glacier-ore-json", "packed-snow-json", *GOOD["terrainMaterials"]]
        c("first.json-blocks-take-terrain-ids", all(terrain.get(n, (0, ""))[1] == "live" and 117 <= terrain[n][0] <= 127 for n in want) and len(terrain) == len(want), str(terrain))
        c("first.building-room-apart", t.get("tt-brick", (0, "", ""))[0] >= 213 and t["tt-brick"][2] == "building", str(t.get("tt-brick")))
        c("first.rooms", "tt: room: building 49, terrain 11" in text, str(tt(text, "^room")))
        for n in ("glacier-ore-json", "packed-snow-json"):
            c(f"first.{n}-registered", block(text, n) == ("Registered", terrain.get(n, (-1,))[0]), str(block(text, n)))
        for n in ("glacier-ore", "packed-snow"):
            c(f"first.{n}-from-code-waits", (block(text, n) or ("",))[0] == "Pending", f"{block(text, n)}: the game read its tables before the mod's code ran")
        for n, field in WRONG_FIELDS.items():
            said = [l.split("] ", 1)[-1] for l in text.splitlines() if "worldtables:" in l and re.search(rf"block terrainMaterial {re.escape(n)}( \(mod {BAD}\))?, {re.escape(field)}\b", l)]
            c(f"first.refuses-{n}-by-name", bool(said) and n not in t, said[0] if said else "no line")
        for n, want_field in (("bad-like", "like"), ("bad-recipe", "recipe"), ("bad-look", "look")):
            said = tt(text, rf"^code-bad {n}: ")
            c(f"first.code-refuses-{n}", bool(said) and re.search(rf"refused: block terrainmaterial {n}, {want_field}: ", said[0]) is not None, said[0] if said else "no line")
        served = [l for l in tt(text, r"^table bytes: (registry|world|pools|particle)")]
        c("first.tables-served", any("registry" in l and "identical False" in l for l in served) and any("particle" in l and "identical False" in l for l in served), str(served))

        # what the server serves, through the game's own reflection
        d = decode_served(lab, ["registry", "vfx", "world"])
        configs = {x["terrainMaterial"]["value"]: x for x in d["registry"]["terrainConfigs"]}
        items = {m[1]: m[2] for m in re.finditer(r"tt: game item (\S+): (\S+)", text)}
        ore, snow = configs.get(terrain.get("glacier-ore-json", (0,))[0]), configs.get(terrain.get("packed-snow-json", (0,))[0])
        c("first.ore-loot", ore is not None and ore["terrainItem"] == items.get("Material_T7_Celesthium_Ore") and ore["terrainPerLootItemExchangeRate"] == 12 and ore["healthPoints"] == 12,
          {k: ore[k] for k in ("terrainItem", "terrainPerLootItemExchangeRate", "healthPoints", "hardness", "damageSusceptibility")} if ore else None)
        c("first.ore-like-copper", ore is not None and ore["hardness"] == configs[26]["hardness"] and ore["damageSusceptibility"] == "Metal" and ore["materialFeedbackId"] == configs[26]["materialFeedbackId"])
        own = guid(SAMPLE, "packed-snow-json", "item")
        c("first.snow-gives-itself", snow is not None and snow["terrainItem"] == own and snow["terrainPerLootItemExchangeRate"] == configs[56]["terrainPerLootItemExchangeRate"], f"{snow and snow['terrainItem']}, its item {own}")
        c("first.snow-flags", snow is not None and snow["isSlidingMaterial"] and snow["canBeFlattened"] == configs[56]["canBeFlattened"] and snow["hardness"] == "SlightlyHard" and snow["healthPoints"] == 6)
        hard, tar = configs.get(terrain.get("tt-hard", (0,))[0]), configs.get(terrain.get("tt-tar", (0,))[0])
        c("first.hard-unbreakable", hard is not None and hard["hardness"] == "Unbreakable" and hard["terrainItem"] == configs[26]["terrainItem"])
        c("first.tar-keeps-its-buffs", tar is not None and tar["conditionalBuffTypes"] == configs[51]["conditionalBuffTypes"] and len(tar["conditionalBuffTypes"]) > 0 and tar["canBeFlattened"],
          tar and tar["conditionalBuffTypes"])
        vfx = d["vfx"]["materialType"]
        c("first.snow-particles-of-ice", vfx[terrain.get("packed-snow-json", (0,))[0]] == vfx[57] != 0, f"{vfx[terrain.get('packed-snow-json', (0,))[0]]} against ice's {vfx[57]}")
        guids = d["world"]["materialGuids"]
        c("first.world-guids", all(guids[terrain[n][0]] == guid(SAMPLE if n.endswith("-json") else DATA, n) for n in want if n in terrain))

    def second():
        lab.set_mods("status+write+blast", extra=[(DATA, GOOD), (BAD, WRONG)])
        text = lab.run("second")
        t = table(text)
        for n in ("glacier-ore", "packed-snow"):
            c(f"second.{n}-from-code-registered", (block(text, n) or ("",))[0] == "Registered" and t.get(n, (0, "", ""))[1:] == ("live", "terrain"), f"{block(text, n)} {t.get(n)}")
        for n in ("glacier-ore", "packed-snow"):
            c(f"second.twin-{n}", f"tt: twin {n}: same True" in text, str(tt(text, f"twin {n}")))
        c("second.guids-follow-names", f" {guid(SAMPLE, 'glacier-ore')} glacier-ore " in text, guid(SAMPLE, "glacier-ore"))
        for n in ("packed-snow", "packed-snow-json"):
            m = re.search(rf"tt: item {n}: \S+ 0x\S+ guid (\S+), voxel id (\d+), voxel guid (\S+), building (\d), in the pools (\w+)", text)
            c(f"second.item-{n}", m is not None and int(m[2]) == t.get(n, (-1,))[0] and m[3] == guid(SAMPLE, n) and m[4] == "0" and m[5] == "True", m[0] if m else "no item line")
        c("second.ore-has-no-item", "tt: item glacier-ore-json: none" in text)
        wrote = tt(text, r"^wrote ")
        c("second.cells-written", len(wrote) >= 8 and all(re.search(r"at cell id (\d+): mat \1 fill 255", w) for w in wrote), str(wrote))
        b = blasts(text)
        stone, hard, soft, vhard = b.get("TerrainStone"), b.get("tt-hard"), b.get("tt-soft"), b.get("tt-vhard")
        c("second.tnt-digs-stone", stone is not None and stone[2] + stone[3] > 0, str(stone))
        c("second.tnt-unbreakable-untouched", hard is not None and hard[1] > 0 and hard[2] == 0 and hard[3] == 0, str(hard))
        c("second.tnt-soft-at-least-stone", soft is not None and stone is not None and soft[2] >= stone[2] and soft[2] > 0, f"soft {soft}, stone {stone}")
        c("second.tnt-very-hard-less", vhard is not None and stone is not None and vhard[2] < stone[2], f"very hard 1000 {vhard}, stone {stone}")
        for n in ("glacier-ore-json", "packed-snow-json", "glacier-ore", "packed-snow"):
            c(f"second.tnt-{n}", n in b and b[n][2] + b[n][3] > 0, str(b.get(n)))
        ids.update({n: v for n, v in t.items() if v[2] == "terrain"})

    def third():
        lab.set_mods("status+read", extra=[(DATA, GOOD), (BAD, WRONG)])
        text = lab.run("third")
        t = {n: v for n, v in table(text).items() if v[2] == "terrain"}
        c("third.ids-stable", t == ids and len(t) == 8, f"{t} against {ids}")
        cells = tt(text, r"^cells of ")
        c("third.cells-kept", len(cells) == 8 and all(re.search(r"the table says (\d+), the world has mat \1 fill 255 x\d+$", x) for x in cells), str(cells))

    def removed():
        lab.set_mods("status+read+blast", sample=False)
        text = lab.run("removed")
        t = table(text)
        kept = {n: v for n, v in t.items() if v[2] == "terrain"}
        c("removed.kept", len(kept) == 8 and all(v[1] == "kept" for v in kept.values()), str(kept))
        cells = tt(text, r"^cells of ")
        c("removed.cells-stay", len(cells) == 8 and all(re.search(r"the table says (\d+), the world has mat \1 fill 255 x\d+$", x) for x in cells), str(cells))
        b = blasts(text)
        c("removed.kept-digs-as-before", b.get("tt-hard (kept)", (0, 0, 1, 1))[2:] == (0, 0) and b.get("tt-soft (kept)", (0, 0, 0, 0))[2] > 0, f"{b.get('tt-hard (kept)')} {b.get('tt-soft (kept)')}")

    def back():
        lab.set_mods("status+read", extra=[(DATA, GOOD), (BAD, WRONG)])
        text = lab.run("back")
        t = {n: v for n, v in table(text).items() if v[2] == "terrain"}
        c("back.live-again", len(t) == 8 and all(v[1] == "live" for v in t.values()), str(t))
        cells = tt(text, r"^cells of ")
        c("back.cells-follow", len(cells) == 8 and all(re.search(r"the table says (\d+), the world has mat \1 fill 255 x\d+$", x) for x in cells), str(cells))

    def limit():
        lab.forget(["tt-", SAMPLE])
        over = {f"o{i:02}": {"name": f"Over {i}"} for i in range(12)}
        lab.set_mods("status", sample=False, extra=[(OVER, {"format": 1, "terrainMaterials": over})])
        text = lab.run("limit")
        t = {n: v for n, v in table(text).items() if re.fullmatch(r"o\d\d", n)}
        c("limit.11-placed", len(t) == 11, f"{len(t)} placed")
        refused = [l.split("] ", 1)[-1] for l in text.splitlines() if "worldtables:" in l and "refused: the game has room for 11 new terrain materials" in l]
        c("limit.refuses-by-name", len(refused) == 1 and re.search(rf"block terrainMaterial o\d\d \(mod {OVER}\): refused", refused[0]) is not None, str(refused))
        c("limit.server-runs", "tt: done " in text)
        lab.forget(["tt-over"])

    return [("first", first), ("second", second), ("third", third), ("removed", removed), ("back", back), ("limit", limit)]


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--lab", required=True)
    ap.add_argument("--out", default=None, help="where each start's kiln.log goes (default $KILN_OUT/terrain-test)")
    ap.add_argument("--only", default="")
    ap.add_argument("--low", action="store_true", help="run the lab server at the lowest CPU and IO priority")
    a = ap.parse_args()
    out = Path(a.out) if a.out else Path(os.environ["KILN_OUT"]) / "terrain-test"
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
