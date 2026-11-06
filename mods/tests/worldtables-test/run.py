#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""kiln.worldtables on a lab server, player free. One server start per scenario, in order: the save carries over, so a material's cells are written
once and then have to survive every later mod list.

    KILN_OUT=<build dir> mods/tests/worldtables-test/run.py --lab mylab [--port 26641] [--out DIR] [--only NAME[,NAME]] [--from NAME] [--low]

Before it: build the driver into $KILN_OUT/mods/worldtables-test and fixture/ into $KILN_OUT/wt-fixture, make the lab with `tools/lab/lab.sh new` and a save, and
deploy with `tools/deploy-server.sh`. Prints PASS/FAIL lines, writes each start's kiln.log to --out, and exits non-zero on a FAIL.
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
sys.path.insert(0, str(REPO / "tools" / "nettest"))
sys.path.insert(0, str(REPO / "tools" / "kfc"))

NS = uuid.UUID("6f6d2a44-53c1-5c52-9d51-0a3c6e1f2b77")
EMPTY_HASH = "0000000000000000"


def material_guid(mod, name):
    """BlockIds.Derive(mod, "material", name)."""
    h = bytearray(hashlib.sha1(NS.bytes + f"{mod}:material:{name}".encode()).digest()[:16])
    h[6] = h[6] & 0x0f | 0x50
    h[8] = h[8] & 0x3f | 0x80
    return str(uuid.UUID(bytes=bytes(h)))


def first_name():
    d = json.loads((HERE / "fixtures/wt-mat-first/blocks/wt-mat-first.json").read_text())
    return next(iter(d["buildingMaterials"]))


A = material_guid("wt-mat-a", "brick-a")
FIRST = material_guid("wt-mat-first", first_name())


BAD = {
 "format": 1,
 "materialTweaks": {
  "typo": {
   "material": "NoSuchMaterial",
   "strength": {
    "hardness": "hard"
   }
  },
  "bad-hardness": {
   "material": "StoneRough",
   "strength": {
    "hardness": "steely"
   }
  },
  "bad-field": {
   "material": "StoneRough",
   "strength": {
    "hardnes": "hard"
   }
  },
  "still-good": {
   "material": "WoodRough",
   "strength": {
    "health": 151
   }
  }
 }
}


class Lab:
    def __init__(self, name, out, port):
        self.name = name
        self.dir = Path(os.environ.get("KILN_LABS", Path.home() / "srv/enshrouded/labs")) / name
        self.kiln = self.dir / "kiln"
        self.out = out
        self.port = port
        self.build = Path(os.environ["KILN_OUT"])

    low = False

    def tool(self, *args):
        cmd = [str(REPO / "tools/lab/lab.sh"), *args]
        if self.low and args[0] == "start":
            # Wine raises its threads' priority again, so the nice limit has to be set too
            cmd = ["bash", "-c", 'ulimit -e 1; exec nice -n 19 ionice -c 3 "$@"', "lab", *cmd]
        return subprocess.run(cmd, capture_output=True, text=True).stdout.strip()

    def set_mods(self, mods, extra=()):
        for d in self.kiln.glob("mods/wt-*"):
            shutil.rmtree(d)
        driver = self.kiln / "mods/worldtables-test"
        shutil.rmtree(driver, ignore_errors=True)
        driver.mkdir(parents=True)
        for f in ("WorldTablesTest.dll", "WorldTablesTest.pdb", "kiln.mod.json"):
            shutil.copy(self.build / "mods/worldtables-test" / f, driver)
        for m in mods:
            d = self.kiln / "mods" / m
            shutil.copytree(HERE / "fixtures" / m, d)
            shutil.copy(self.build / "wt-fixture/WtFixture.dll", d)
        for m, doc, *how in extra:
            side, clients = how if how else ("both", "mod")
            d = self.kiln / "mods" / m
            (d / "blocks").mkdir(parents=True)
            (d / "kiln.mod.json").write_text(json.dumps({"id": m, "name": m, "version": "1.0.0", "apiVersion": "0.1", "entryAssembly": "WtFixture.dll", "side": side, "clients": clients}))
            (d / "blocks" / f"{m}.json").write_text(json.dumps(doc))
            shutil.copy(self.build / "wt-fixture/WtFixture.dll", d)

    def set_config(self, tables=None, modsync=None):
        cfg = self.kiln / "config"
        cfg.mkdir(parents=True, exist_ok=True)
        (cfg / "worldtables.json").write_text(json.dumps(tables or {}))
        net = {"labPort": self.port}
        if modsync:
            net["modSync"] = modsync
        (cfg / "network.json").write_text(json.dumps(net))

    def forget(self, guid):
        for f in self.kiln.glob("data/worldtables/worlds/*.json"):
            d = json.loads(f.read_text())
            d["materials"].pop(guid, None)
            f.write_text(json.dumps(d))

    def run(self, scenario, mode, during=None, seconds=420):
        data = self.kiln / "data/worldtables-test"
        data.mkdir(parents=True, exist_ok=True)
        (data / "mode").write_text(mode)
        log = self.kiln / "logs/kiln.log"
        log.unlink(missing_ok=True)
        print(f"== {scenario}: {self.tool('start', self.name)}", flush=True)
        end = time.time() + seconds
        text = ""
        while time.time() < end:
            text = log.read_text(errors="replace") if log.exists() else ""
            if f"wt: done {mode}" in text:
                break
            time.sleep(1)
        result = during() if during else None
        time.sleep(2)
        self.tool("stop", self.name)
        text = log.read_text(errors="replace") if log.exists() else ""
        self.out.mkdir(parents=True, exist_ok=True)
        (self.out / f"kiln-{scenario}.log").write_text(text)
        return text, result


class Checks:
    def __init__(self):
        self.failed = self.passed = 0

    def __call__(self, name, ok, detail=""):
        self.passed += bool(ok)
        self.failed += not ok
        print(f"{'PASS' if ok else 'FAIL'} {name}{': ' + detail if detail else ''}", flush=True)


def lines(text, pattern):
    return [l.split("] ", 1)[-1].split("wt: ", 1)[-1] for l in text.splitlines() if re.search(pattern, l)]


def blast(text, label, template="Terraform_Explosion_TNT_T1"):
    m = re.search(rf"blast {template} on {re.escape(label)} \(cell (\d+)\): solid (\d+), removed (\d+), partly (\d+)", text)
    return (int(m[1]), int(m[2]), int(m[3]), int(m[4])) if m else None


def cells(text, guid):
    """(what the table says, the first run of cells) for one cube."""
    m = re.search(rf"cells of {guid}: the table says (-?\d+), the world has mat (\d+) fill (\d+) x(\d+)", text)
    return tuple(int(x) for x in m.groups()) if m else None


def table_id(text, guid):
    m = re.search(rf"table: (\d+) {guid} \S+ \(\S+\) (live|kept)", text)
    return (int(m[1]), m[2]) if m else None


def state_hash(text):
    m = re.search(r"wt: mode \S+: state (\w+), hash (\w+)", text)
    return (m[1], m[2]) if m else (None, None)


def identical(text):
    return dict(re.findall(r"table bytes: (\w+): game .*identical (True|False)", text))


def scenarios(lab, c, port):
    # Each entry: name, function. They run in this order.
    def no_mods():
        lab.set_mods([])
        lab.set_config()
        text, _ = lab.run("no-mods", "status+dump+blast")
        state, h = state_hash(text)
        c("no-mods.state", state == "untouched" and h == EMPTY_HASH, f"{state} {h}")
        c("no-mods.nothing-served", set(identical(text).values()) == {"True"}, str(identical(text)))
        # The bytes the game gets, against the bytes in the game's own files.
        from kfc.archive import Kfc
        k = Kfc(lab.dir)
        ok = True
        for name, type_hash in (("registry", 0xc9ce5b84), ("pools", 0x9a2e4101), ("blueprints", 0x0dfcca99)):
            files = k.resources_of_type(type_hash)
            same = k.read(files[0]) == (lab.kiln / "data/worldtables-test" / f"{name}.bin").read_bytes()
            c(f"no-mods.bytes.{name}", same)
            ok = ok and same
        for label, removed in (("StoneRough", 504), ("WoodRough", 504), ("MetalIron", 0)):
            b = blast(text, label)
            c(f"no-mods.blast.{label}", b is not None and b[2] == removed, str(b))
        return text

    def noop():
        lab.set_mods(["wt-noop"])
        text, _ = lab.run("noop", "status")
        state, h = state_hash(text)
        c("noop.state", state == "untouched", f"{state} {h}")
        c("noop.bytes-identical", set(identical(text).values()) == {"True"}, str(identical(text)))

    def tweaks():
        lab.set_mods(["wt-tweaks"])
        text, _ = lab.run("tweaks", "status+blast")
        state, h = state_hash(text)
        c("tweaks.state", state == "merged" and h == EMPTY_HASH, f"{state} {h}: a tweak isn't in the handshake")
        ids = identical(text)
        c("tweaks.only-the-registry", ids.get("registry") == "False" and ids.get("world") == "True" and ids.get("pools") == "True", str(ids))
        b = blast(text, "StoneRough")
        c("tweaks.unbreakable", b is not None and b[1] == 729 and b[2] == 0 and b[3] == 0, f"{b}; with no mods it's 504 of 729")
        b = blast(text, "MetalIron")
        c("tweaks.iron-made-soft", b is not None and b[2] == 504, f"{b}; with no mods it's 0")
        b = blast(text, "WoodRough", "Terraform_Fogger_Heavy_Strike")
        c("tweaks.thin-digs-under-the-strike", b is not None and b[2] > 0, f"{b}; 45 damage against slightly hard, health 1")
        b = blast(text, "WoodDeadwood", "Terraform_Fogger_Heavy_Strike")
        c("tweaks.harder-category", b is not None and b[2] == 0 and b[3] == 0, f"{b}; the same health, and the strike has 0 against moderately hard")
        with_health = blast(text, "WoodFancy")
        c("tweaks.health", with_health is not None and with_health[2] < 504, f"{with_health}; with no mods it's 504")

    def bad():
        # Wrong on purpose, so it's written here rather than kept with the other fixtures, which the repository's schema test reads.
        lab.set_mods([], extra=[("wt-bad", BAD, "server", "none")])
        text, _ = lab.run("bad", "status")
        for name, what in (("typo", "NoSuchMaterial"), ("bad-hardness", '"steely" is not a hardness'), ("bad-field", "strength.hardnes")):
            c(f"bad.names-{name}", name in text and what in text and re.search(rf"error: .*{name}", text) is not None)
        c("bad.others-stay", "tweak still-good (mod wt-bad): WoodRough (143): health 150 -> 151" in text)

    def failsafe():
        lab.set_mods(["wt-tweaks", "wt-mat-a"])
        lab.set_config(tables={"testMissingField": "healthPoints"})
        text, _ = lab.run("failsafe", "status+blast")
        state, h = state_hash(text)
        c("failsafe.off", state == "off" and h == EMPTY_HASH, f"{state} {h}")
        c("failsafe.says-why", "doesn't resolve" in text and "healthPoints" in text and "untouched" in text)
        b = blast(text, "StoneRough")
        c("failsafe.game-rules", b is not None and b[2] == 504, str(b))
        lab.set_config()

    def material():
        lab.set_mods(["wt-mat-a"])
        text, _ = lab.run("material", "status+write+blast-new+item")
        c("material.id", table_id(text, A) == (213, "live"), str(table_id(text, A)))
        state, h = state_hash(text)
        c("material.hash", state == "merged" and h != EMPTY_HASH, f"{state} {h}")
        b = blast(text, "brick-a")
        c("material.digs-by-its-config", b is not None and b[0] == 213 and b[2] == 504, str(b))
        c("material.item", re.search(r"item wt-mat-a\.brick-a: id 0x\w+, voxel id 213, table says 213", text) is not None, str(lines(text, r"wt: item ")))
        c("material.pools", re.search(r"item wt-mat-a\.brick-a: in the pools True, in the blueprint items True", text) is not None)
        return h

    def restart():
        lab.set_mods(["wt-mat-a"])
        text, _ = lab.run("restart", "status+read")
        got = cells(text, A)
        c("restart.same-id", table_id(text, A) == (213, "live"), str(table_id(text, A)))
        c("restart.cells", got is not None and got[0] == 213 and got[1] == 213 and got[2] == 255, str(got))

    def moved():
        lab.set_mods(["wt-mat-a", "wt-mat-first"])
        text, _ = lab.run("moved", "status+read+write")
        c("moved.ids", table_id(text, FIRST) == (213, "live") and table_id(text, A) == (214, "live"), f"first {table_id(text, FIRST)}, a {table_id(text, A)}")
        got = cells(text, A)
        c("moved.cells-follow-the-guid", got is not None and got[0] == 214 and got[1] == 214 and got[2] == 255, str(got))

    def removed():
        lab.set_mods(["wt-mat-first"])
        text, _ = lab.run("removed", "status+read+blast-new")
        c("removed.kept", table_id(text, A) == (214, "kept") and table_id(text, FIRST) == (213, "live"), f"a {table_id(text, A)}, first {table_id(text, FIRST)}")
        got = cells(text, A)
        c("removed.walls-stay", got is not None and got[1] == 214 and got[2] == 255 and got[3] > 0, str(got))
        c("removed.kept-digs-as-it-did", (blast(text, "brick-a (kept)") or (0, 0, 0, 0))[2] == 504, str(blast(text, "brick-a (kept)")))
        return state_hash(text)[1]

    def removed_again():
        lab.set_mods(["wt-mat-first"])
        text, _ = lab.run("removed-again", "status+read")
        got = cells(text, A)
        c("removed-again.walls-stay", got is not None and got[1] == 214 and got[2] == 255, str(got))

    def back():
        lab.set_mods(["wt-mat-a", "wt-mat-first"])
        text, _ = lab.run("back", "status+read")
        c("back.live-again", table_id(text, A) == (214, "live"), str(table_id(text, A)))
        got = cells(text, A)
        c("back.finds-its-walls", got is not None and got[1] == 214 and got[2] == 255, str(got))

    def handshake():
        from nettest import Client, FULL_MODS, W, frame  # noqa: F401
        lab.set_mods(["wt-mat-a", "wt-mat-first"])
        lab.set_config()
        results = {}

        def clients():
            status = json.loads((lab.kiln / "data/worldtables/status.json").read_text())
            own = status["hash"]
            ids = ",".join(f"{m['id']}={uuid.UUID(m['guid']).hex}" for m in status["materials"] if m["state"] == "live")
            mods = [("wt-mat-a", "1.0.0", 2), ("wt-mat-first", "1.0.0", 2)]
            for name, props, want in (
                ("same", {"kiln.worldtables": own, "kiln.worldtables.ids": ids}, "welcome"),
                ("wrong-hash", {"kiln.worldtables": "1234567890abcdef", "kiln.worldtables.ids": "213=" + uuid.UUID(A).hex}, "reject"),
                ("no-property", {"nettest.client": "older"}, "reject"),
                ("empty-table", {"kiln.worldtables": EMPTY_HASH}, "reject"),
            ):
                cl = Client(port, name)
                cl.hello(mods, [], props)
                cl.wait(lambda: cl.welcome is not None or cl.reject is not None, 8)
                results[name] = (cl.welcome is not None, cl.reject, want)
            return results

        text, _ = lab.run("handshake", "status", during=clients)
        for name, (welcomed, reject, want) in results.items():
            ok = welcomed if want == "welcome" else (reject is not None and "building materials" in reject[0])
            c(f"handshake.{name}", ok, f"welcome {welcomed}, reject {reject[0] if reject else None}")
        c("handshake.server-lived", "wt: done status" in text)
        said = [l for l in lines(text, "net: refusing") if "building materials" in l]
        c("handshake.server-says", len(said) == 3, f"{len(said)} refusal line(s) naming the materials: {said[:1]}")

        # A server that lost a mod: its kept material isn't in what a client must have.
        lab.set_mods(["wt-mat-first"])
        fresh = {}

        def clients2():
            status = json.loads((lab.kiln / "data/worldtables/status.json").read_text())
            cl = Client(port, "no-ledger")
            cl.hello([("wt-mat-first", "1.0.0", 2)], [], {"kiln.worldtables": status["hash"]})
            cl.wait(lambda: cl.welcome is not None or cl.reject is not None, 8)
            fresh["welcome"] = cl.welcome is not None
            fresh["kept"] = [m for m in status["materials"] if m["state"] == "kept"]
            return fresh

        lab.run("handshake-kept", "status", during=clients2)
        c("handshake.kept-isnt-required", fresh.get("welcome") is True and len(fresh.get("kept", [])) == 1, str(fresh))

    def forgotten():
        lab.set_mods(["wt-mat-first"])
        lab.forget(A)
        text, _ = lab.run("forgotten", "status+read")
        c("forgotten.no-slot", table_id(text, A) is None, str(table_id(text, A)))
        got = cells(text, A)
        c("forgotten.walls-gone", got is not None and got[0] == -1 and got[1] == 0 and got[2] == 0, f"{got}: what the game does without the record")

    def limit():
        # Every new id, one by one, and one over: a clean refusal by name.
        lab.forget(FIRST)
        over = {f"m{i:02}": {"name": f"Material {i}", "strength": {"hardness": "soft"}} for i in range(50)}
        lab.set_mods([], extra=[("wt-over", {"format": 1, "buildingMaterials": over})])
        text, _ = lab.run("limit", "status")
        placed = re.findall(r"table: (\d+) \S+ m\d\d \(wt-over\) live( reused)?", text)
        c("limit.49-placed", len(placed) == 49, f"{len(placed)} placed")
        c("limit.reused-are-the-last-6", sum(1 for _, r in placed if r) == 6, str([p for p, r in placed if r]))
        refused = lines(text, r"wt: refused:")
        c("limit.refuses-by-name", len(refused) == 1 and re.search(r"block buildingMaterial m\d\d \(mod wt-over\)", refused[0]) is not None, str(refused))
        c("limit.server-runs", "wt: done status" in text)
        for f in lab.kiln.glob("data/worldtables/worlds/*.json"):
            d = json.loads(f.read_text())
            d["materials"] = {g: m for g, m in d["materials"].items() if m["mod"] != "wt-over"}
            f.write_text(json.dumps(d))

    def code():
        # A tweak from C#: the game read its tables before the mod's code ran, so it's there from the next start.
        lab.set_mods([])
        text, _ = lab.run("code-1", "code")
        c("code.first-start-pending", "code tweak: status Pending" in text, str(lines(text, "code tweak")))
        b = blast(text, "StoneFancy")
        c("code.first-start-no-change", b is not None and b[2] == 504, str(b))
        text, _ = lab.run("code-2", "code")
        c("code.second-start-registered", "code tweak: status Registered" in text, str(lines(text, "code tweak")))
        b = blast(text, "StoneFancy")
        c("code.second-start-applies", b is not None and b[2] == 0, str(b))
        text, _ = lab.run("code-3", "status")
        c("code.dropped-from-the-code", "no longer defines code-tweak" in text, str(lines(text, "no longer defines")))
        text, _ = lab.run("code-4", "status")
        c("code.gone-next-start", state_hash(text)[0] == "untouched", str(state_hash(text)))

    def shapes():
        # A shape is made, placeable data and all, and keeps its ids across a restart.
        lab.set_mods(["wt-shapes"])
        first, _ = lab.run("shapes-1", "status+shapes")
        again, _ = lab.run("shapes-2", "status+shapes")
        door = r"shape door-wall: item wt-shapes\.door-wall 0x(\w+) guid (\S+), table entry 8x8x1 compressed 1 cells c3c3c3c3c3ffffff"
        m1, m2 = re.search(door, first), re.search(door, again)
        c("shapes.item-and-entry", m1 is not None, str(lines(first, "wt: shape door-wall")[:1]))
        c("shapes.ids-stable", m1 is not None and m2 is not None and m1.groups() == m2.groups(), f"{m1 and m1.groups()} then {m2 and m2.groups()}")
        game = re.search(r"game door wall: table entry 8x8x1 compressed 1 cells (\w+), pool (\S+), preview model 88 bytes sha256 (\w+)", first)
        mine = re.search(r"shape door-wall: voxelObject (\S+), pool (\S+), preview model 88 bytes sha256 (\w+)", first)
        c("shapes.same-bytes-as-the-game", game is not None and mine is not None and game[1] == "c3c3c3c3c3ffffff" and game[3] == mine[3], f"game {game and game.groups()}, ours {mine and mine.groups()}")
        c("shapes.preview-is-its-own", mine is not None and m1 is not None and mine[1] == m1[2], f"voxelObject {mine and mine[1]}")
        c("shapes.wall-pool", game is not None and mine is not None and game[2] == mine[2], f"{game and game[2]} / {mine and mine[2]}")
        c("shapes.roof", re.search(r"shape ridge: item wt-shapes\.ridge \S+ guid \S+, table entry 4x2x2 compressed 1 cells (\w+)", first) is not None, str(lines(first, "wt: shape ridge")[:2]))
        bad = [l for l in first.splitlines() if re.match(r"\[(E|W) ", l) and "wt-shapes" in l]
        c("shapes.no-errors", not bad and "wt: error" not in first, str(bad[:2]))

    return [("no-mods", no_mods), ("noop", noop), ("tweaks", tweaks), ("bad", bad), ("failsafe", failsafe), ("material", material), ("restart", restart), ("moved", moved), ("removed", removed),
            ("removed-again", removed_again), ("back", back), ("handshake", handshake), ("forgotten", forgotten), ("limit", limit), ("code", code), ("shapes", shapes)]


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--lab", required=True)
    ap.add_argument("--port", type=int, default=26641, help="the lab transport's UDP port for fake clients")
    ap.add_argument("--out", default=None, help="where each start's kiln.log goes (default $KILN_OUT/worldtables-test)")
    ap.add_argument("--only", default="")
    ap.add_argument("--from", dest="start", default="")
    ap.add_argument("--low", action="store_true", help="run the lab server at the lowest CPU and IO priority")
    a = ap.parse_args()
    out = Path(a.out) if a.out else Path(os.environ["KILN_OUT"]) / "worldtables-test"
    lab = Lab(a.lab, out, a.port)
    lab.low = a.low
    c = Checks()
    only = set(filter(None, a.only.split(",")))
    started = not a.start
    try:
        for name, fn in scenarios(lab, c, a.port):
            started = started or name == a.start
            if not started or (only and name not in only):
                continue
            fn()
    finally:
        lab.tool("stop", a.lab)
    print(f"{c.passed} passed, {c.failed} failed")
    return 1 if c.failed else 0


if __name__ == "__main__":
    sys.exit(main())
