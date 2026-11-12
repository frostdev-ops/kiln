#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""kiln.playeranim on a lab server, player free: mods' states in the player's animation graph, one server start per scenario.

    KILN_OUT=<build dir> python3 mods/tests/player-anim-test/run.py --lab NAME [--only S[,S]] [--out DIR] [--net-port 27193]
        [--game DIR] [--boots 10] [--join]

Before it: build Kiln into $KILN_OUT (kiln.playeranim, kiln.types and kiln.assets among the services) and this folder's
PlayerAnimTest.csproj into $KILN_OUT/mods/player-anim-test; make the lab with `tools/lab/lab.sh new`. The driver deploys Kiln into
the lab itself (`tools/deploy-server.sh --no-build`). KILN_LABSTART, when set, is the command that starts the lab (it gets the lab's
name and runs from the repository's root); else `tools/lab/lab.sh start`.

Nothing it makes from the game goes into the repository: the fixture mods (mods/) are copied to --out, their clips written there by
make_clips.py from the installed game's player skeleton and cooked there with animcook; the overlay scenario's carrier sequences are
decoded from the installed game into the lab's copy of the test mod. Every start's kiln.log and game log go to --out/<scenario>/.

Prints PASS/FAIL/SKIP lines and the numbers behind them, and exits non-zero on a FAIL. Scenarios, in this order:
  baseline     the game's own graph: the game's states (the reference for every comparison) and the mods' sequences doing nothing (control)
  unknown-id   a state id no state has, on the game's own graph: the pose stays, nothing in the game's log, the server up (condition 3)
  untouched    no mod with states: the game's graph, nothing put
  entered      mod A: each state entered, the pose follows its authored clip, PlayerAnimStates says Live with the graph's id
  game-states  mods A and B: the game's states still play, each within the noise of the baseline
  two-mods     mods A and B: all Live, each plays its own clip, the served graph's hash is animcook's for the same folders
  removed      mod A alone after A and B: B's states gone, the hash is A's alone, A's states still play
  broken       a wrong like, a clip of another skeleton, a manifest without clients: each refused with its reason, the rest live
  off          the lab switch testMissingField: the game's graph, every state Unavailable, the game's states play
  overlays     overlay states (staff, wand, bow): the clone route if kiln.gamedata takes an overlayName, else the carrier route
  join         (--join, with the Kiln.Network gate patch built in) a HELLO with another graph hash is refused, the same one accepted
  timing       --boots boots without a player-state mod and as many with A and B: the service's times and the time to the world
  reload       mod A unloaded and loaded again while a 1_Player exists: its state still plays
"""
import argparse
import hashlib
import json
import math
import os
import re
import shutil
import statistics
import subprocess
import sys
import time
import uuid
from pathlib import Path

HERE = Path(__file__).resolve().parent
REPO = HERE.parents[2]
sys.path[:0] = [str(REPO / "tools/kfc"), str(REPO / "tools/animcook"), str(REPO / "tools/modelcook/tests"), str(REPO / "tools/nettest")]

GAME_HASH = "0000000000000000"
PLAYER_GRAPH = "21319e83-5087-4fff-8795-22997843649e"
PLAYER_SKELETON = "87b29df6-6518-43ef-9298-e3f85a6204b9"
GRAPH_TYPE = "keen::anim_graph::runtime_graph::AnimationGraphResource2_0"
SEQ_TYPE = "keen::actor::ActorSequenceResource"
CLONE_NS = uuid.UUID("2b9f4c1e-5d3a-4e7b-9c8d-1a2b3c4d5e6f")
ZERO = "00000000-0000-0000-0000-000000000000"

# The game's sequences the scenarios play (names; kiln.gamedata finds them), and what they enter.
HEAVY = "Attack_Player_Melee_Heavy_2H_Axe_01"      # SetAnimationEvent at 0, 3.35 s: the base of every full-body copy
SWORD = "Attack_Player_1H_Single_LightChain1"      # Attack_Sword_Shield_01
BLOCK = "Ability_Block"                            # Block_FullBody
GREET = "Ability_Player_Emote_Greet"               # an emote
FIREBALL = "Attack_Player_Staff_FixedAnim_Fireball"  # Attack_Fireball_FullBody and the staff overlay Staff_Cast_F_Reload
CHAINHEAL = "Attack_Player_Staff_Overlay_ChainHeal"  # Staff_Cast_F_Reload, then Staff_Cast_U_Shoot at 0.4 s: the reference of pa-cast/cast
CHAINLIGHTNING = "Attack_Player_Staff_Overlay_ChainLightning"  # Staff_Cast_F_Reload, then Staff_Cast_F_Shoot at 0.4 s: the base of pa-cast/cast
WANDCHAIN = "Attack_Player_Wand_LightningChain_normal"  # Attack_Magic_Wand_01 and _02 at 0 (gated on entity tags): the base of pa-cast/zap
WANDHEAVY = "Attack_Player_Wand_AllElements_Heavy"   # Attack_Magic_Wand_Heavy at 0.13 s: the reference of pa-cast/zap
BOWSHOT = "Attack_Player_Ranged_Bow_Shoot"         # Bow_Reload_Fast, Bow_Reload (tagged) at 0, Bow_Shoot at 0.4 s: the base of pa-cast/loose
STRONGSHOT = "Attack_Player_SpecialAbility_Strong_Shot_Bow"  # Bow_Shoot_SpecialAbility at 0.13 s: the reference of pa-cast/loose
STAFF, WAND, BOW = "Weapon_T1_2H_Staff_01", "Weapon_T1_1H_Wand_01_Fire", "Weapon_T1_2H_Bow_01"
# Unused test sequences of the game whose resources carry the overlay copies in the carrier route (served as overrides of them).
CARRIERS = {"cast": "e3c08f06-464a-4da8-bfc4-8672cff34795", "zap": "fcbbb950-4eaa-4a40-af07-3a27d3bd09c7",
            "loose": "f7a1b82d-2d7b-46a8-a106-ecdc1aca0bd0", "chainlightning": "08b3e3d4-9f38-429c-9fea-c7520f55d055",
            "bowshot": "9436b151-8aeb-4d55-8130-7a94510daf26"}
SLOT_HAND_R, SLOT_HAND_L = 8, 9

STATES_A = ["slash", "guard", "guard-game-clip", "leap", "bolt"]
STATES_B = ["slash", "smash"]
# state -> (clip of make_clips.py, the full-body sequence copy's base)
AUTHORED = {("pa-a", "slash"): ("a_slash", HEAVY), ("pa-a", "leap"): ("a_leap", HEAVY), ("pa-a", "bolt"): ("a_bolt", HEAVY),
            ("pa-a", "guard"): ("a_guard", HEAVY), ("pa-b", "slash"): ("b_slash", HEAVY), ("pa-b", "smash"): ("b_smash", HEAVY)}

# Thresholds, in metres: the mean distance of HandR and HandL from where they should be.
FOLLOWS = 0.12       # the pose follows the authored clip
NOISE = 0.15         # the same game sequence on two boots (T0: 0.05 to 0.10 at the best of a few samples' shift)
APART = 0.30         # far from it: what a control has to show


def derive(mod, kind, name):
    from anicook.graphgen import derive as d
    return d(mod, kind, name)


def fnv(guid):
    from kfc.animgraph import fnv_guid
    return fnv_guid(guid)


def clone_guid(label):
    return str(uuid.uuid5(CLONE_NS, label))


# ---------------------------------------------------------------------------------------------------------------------------- the lab

class Lab:
    def __init__(self, a):
        self.name = a.lab
        self.dir = Path(os.environ.get("KILN_LABS", Path.home() / "srv/enshrouded/labs")) / a.lab
        self.kiln = self.dir / "kiln"
        self.build = Path(os.environ["KILN_OUT"])
        self.out = Path(a.out) if a.out else self.build / "player-anim-test"
        self.game = Path(a.game).expanduser()
        self.net_port = a.net_port
        self.cooked = self.out / "mods"
        self.deployed = False

    def tool(self, *args):
        return subprocess.run([str(REPO / "tools/lab/lab.sh"), *args], capture_output=True, text=True, cwd=REPO).stdout.strip()

    def start(self):
        start = os.environ.get("KILN_LABSTART")
        cmd = [start, self.name] if start else [str(REPO / "tools/lab/lab.sh"), "start", self.name]
        r = subprocess.run(cmd, capture_output=True, text=True, cwd=REPO)
        return (r.stdout + r.stderr).strip().splitlines()[-1:] or [""]

    def deploy(self):
        if self.deployed:
            return
        env = dict(os.environ, KILN_OUT=str(self.build))
        subprocess.run([str(REPO / "tools/deploy-server.sh"), str(self.dir), "--policy", "any", "--no-build"], check=True, env=env,
                       stdout=subprocess.DEVNULL, cwd=REPO)
        self.deployed = True

    def cook(self, mod):
        """The fixture mod, copied to --out once per run, its clips written and cooked there. -> its folder."""
        d = self.cooked / mod
        if (d / ".cooked").exists():
            return d
        shutil.rmtree(d, ignore_errors=True)
        shutil.copytree(HERE / "mods" / mod, d)
        if (d / "assets/cook.json").exists():
            rig = ["--rig"] if mod == "pa-bad-skeleton" else []
            subprocess.run([sys.executable, str(HERE / "make_clips.py"), str(d), "--game", str(self.game), *rig], check=True, stdout=subprocess.DEVNULL)
            r = subprocess.run([sys.executable, str(REPO / "tools/animcook/animcook.py"), "--game", str(self.game), "recipe", str(d / "assets/cook.json"),
                                "--update-manifest"], capture_output=True, text=True)
            (d / "cook.log").write_text(r.stdout + r.stderr)
            # pa-bad-skeleton's cook reports its state refused (exit 1) after writing its files: that is the point of the mod
            if r.returncode != 0 and mod != "pa-bad-skeleton":
                raise SystemExit(f"cook of {mod} failed:\n{r.stdout}{r.stderr}")
        (d / ".cooked").write_text("")
        return d

    def set_mods(self, mods, plan, config=None, carriers=None):
        self.deploy()
        mods_dir = self.kiln / "mods"
        for d in mods_dir.glob("pa-*"):
            shutil.rmtree(d)
        for m in mods:
            shutil.copytree(self.cook(m), mods_dir / m, ignore=shutil.ignore_patterns(".cooked", "cook.log", "src"))
        driver = mods_dir / "player-anim-test"
        shutil.rmtree(driver, ignore_errors=True)
        shutil.copytree(self.build / "mods/player-anim-test", driver, ignore=shutil.ignore_patterns("Kiln.*", "*.deps.json"))
        (driver / "plan.json").write_text(json.dumps(plan, indent=1))
        if carriers:
            (driver / "assets/seq").mkdir(parents=True)
            for guid, raw in carriers.items():
                (driver / "assets/seq" / f"{guid}.bin").write_bytes(raw)
            (driver / "assets/assets.json").write_text(json.dumps({"format": 1, "entries": [
                {"kind": "resource", "guid": g, "type": SEQ_TYPE, "part": 0, "file": f"seq/{g}.bin", "mode": "override"} for g in carriers]}, indent=1))
        cfg = self.kiln / "config"
        cfg.mkdir(parents=True, exist_ok=True)
        (cfg / "playeranim.json").write_text(json.dumps(config or {}))
        (cfg / "network.json").write_text(json.dumps({"labPort": self.net_port}))

    def run(self, scenario, seconds=900, during=None):
        """One server start: until "pat: done", then `during` (with the server still up), then stop. -> (kiln.log, game log, during's result, alive)."""
        log = self.kiln / "logs/kiln.log"
        log.unlink(missing_ok=True)
        t0 = time.time()
        said = self.start()
        print(f"== {scenario}: {said[0] if said else ''}", flush=True)
        end = time.time() + seconds
        text = ""
        result = None
        while time.time() < end:
            text = log.read_text(errors="replace") if log.exists() else ""
            if during and "pat: paused" in text and result is None:
                result = during(text)
            if "pat: done" in text:
                break
            if time.time() - t0 > 60 and not self.tool("pid", self.name):
                break
            time.sleep(1)
        alive = bool(self.tool("pid", self.name))
        if during and result is None:
            result = during(text)
        time.sleep(2)
        self.tool("stop", self.name)
        text = log.read_text(errors="replace") if log.exists() else ""
        game = (self.dir / "logs/enshrouded_server.log")
        game_text = game.read_text(errors="replace").replace("\r", "") if game.exists() else ""
        d = self.out / scenario
        d.mkdir(parents=True, exist_ok=True)
        (d / "kiln.log").write_text(text)
        (d / "enshrouded_server.log").write_text(game_text)
        status = self.kiln / "data/playeranim/status.json"
        if status.exists():
            shutil.copy(status, d / "status.json")
        return text, game_text, result, alive

    def control(self, command):
        """A Kiln.Host control command (kiln/control/<name>.cmd -> <name>.result.json)."""
        c = self.kiln / "control"
        c.mkdir(parents=True, exist_ok=True)
        res = c / "pat.result.json"
        res.unlink(missing_ok=True)
        (c / "pat.cmd").write_text(command)
        for _ in range(60):
            if res.exists():
                return res.read_text(errors="replace")
            time.sleep(0.5)
        return "(no result)"


# ---------------------------------------------------------------------------------------------------------------------------- reading

class Checks:
    def __init__(self):
        self.passed = self.failed = self.skipped = 0

    def __call__(self, name, ok, detail=""):
        self.passed += bool(ok)
        self.failed += not ok
        print(f"{'PASS' if ok else 'FAIL'} {name}{': ' + detail if detail else ''}", flush=True)
        return ok

    def skip(self, name, why):
        self.skipped += 1
        print(f"SKIP {name}: {why}", flush=True)


def pat(text, pattern):
    return [m for m in (re.search(pattern, l) for l in text.splitlines()) if m]


def plays(text):
    """label -> [(step, ms, state, HandR, HandL, FootL, FootR)] per play line; p2's plays as p2/<label>; a label played twice gets '+'."""
    out = {}
    for m in pat(text, r"pat: play (\S+) (\S+) seq \S+ \(0x[0-9a-f]+\) started (\S+), sequence seen in (\d+) samples \| (.*)"):
        key = m[2] if m[1] == "p1" else f"{m[1]}/{m[2]}"
        while key in out:
            key += "+"
        pts = []
        for tok in m[5].split():
            f = tok.split(":")
            pts.append((int(f[0]), int(f[1]), f[2], *(tuple(map(float, x.split(","))) for x in f[3:7])))
        out[key] = {"started": m[3] == "True", "seen": int(m[4]), "pts": pts}
    return out


def states_seen(p):
    s = []
    for x in p["pts"]:
        if not s or s[-1] != x[2]:
            s.append(x[2])
    return s


def moved(p, slot=3):
    return max(math.dist(p["pts"][0][slot], x[slot]) for x in p["pts"])


def near(a, b, lo=0, hi=None, shift=3, slots=(3, 4), offset=0):
    """Mean distance of the hands between two plays over samples lo..hi of a, at the best shift of b within +-shift samples
    (b's sample i + offset + k against a's sample i). -> (mean, shift)."""
    A, B = a["pts"], b["pts"]
    hi = len(A) if hi is None else min(hi, len(A))
    best = None
    for k in range(-shift, shift + 1):
        ds = [sum(math.dist(A[i][s], B[i + offset + k][s]) for s in slots) / len(slots) for i in range(lo, hi) if 0 <= i + offset + k < len(B)]
        if ds and (best is None or sum(ds) / len(ds) < best[0]):
            best = (sum(ds) / len(ds), k)
    return best or (float("inf"), 0)


def state_info(text, when="running"):
    """mod/name -> dict(status, id, guid, kind, machine, reason) from the test mod's report."""
    out = {}
    for m in pat(text, rf"pat: {when}: state (\S+): status (\w+), id 0x([0-9a-f]+), guid (\S+), kind (\w+), machine (\S*), like .*?, reason (.*)$"):
        out[m[1]] = {"status": m[2], "id": int(m[3], 16), "guid": m[4], "kind": m[5], "machine": m[6], "reason": m[7]}
    for m in pat(text, rf"pat: {when}: state (\S+): none"):
        out[m[1]] = {"status": "none"}
    return out


def graph_line(text, when="running"):
    m = pat(text, rf"pat: {when}: service available (\w+), graph (\w+), hash (\w+)")
    return (m[-1][1] == "True", m[-1][2], m[-1][3]) if m else (None, None, None)


def served(text):
    m = pat(text, r"pat: player graph as kiln.assets reads it: (\d+) B, sha256 (\w+)")
    return (int(m[-1][1]), m[-1][2]) if m else (None, None)


def service_lines(text):
    return [l.split("] ", 2)[-1] for l in text.splitlines() if "playeranim:" in l]


def merged_line(text):
    m = pat(text, r"playeranim: (\d+) state\(s\) of (\d+) mod\(s\) merged into the player's graph: (\d+) machine\(s\), (\d+) -> (\d+) bytes, ([\d.]+) ms \((.*?)\), hash (\w+)(?:, (\d+) refused)?")
    if not m:
        return None
    m = m[-1]
    return {"states": int(m[1]), "mods": int(m[2]), "machines": int(m[3]), "before": int(m[4]), "after": int(m[5]), "ms": float(m[6]),
            "split": m[7], "hash": m[8], "refused": int(m[9] or 0)}


def game_errors(game_text):
    """The game's own error and warning lines, without the ones every lab boot has."""
    usual = ("Could not find MatchmakingServers", "steam friends interface", "steam user interface", "template Benchmark")
    return [l for l in game_text.splitlines() if re.match(r"\[(E|W|X) ", l) and not any(u in l for u in usual)]


def world_seconds(game_text):
    m = re.search(r"\[I (\d+):(\d+):(\d+),(\d+)\] \[game_server\] Switching state from LoadEcsScene to Run", game_text)
    return int(m[1]) * 3600 + int(m[2]) * 60 + int(m[3]) + int(m[4]) / 1000 if m else None


# ---------------------------------------------------------------------------------------------------------------------------- the game's data

class Game:
    """What the driver reads from the installed game, at run time, into memory or --out only."""

    def __init__(self, lab):
        from kfc.archive import Kfc
        from kfc.blob import Decoder
        from kfc.blobwrite import Encoder
        from kfc.refl import TypeDb
        self.k, self.t = Kfc(lab.game), TypeDb.from_exe(lab.game / "enshrouded.exe")
        self.dec, self.enc = Decoder(self.t, max_array=1 << 26), Encoder(self.t)
        self.lab = lab
        self._hier = None

    def read(self, guid, type_name):
        from kfc.archive import ResourceKey
        tt = self.t.get(type_name)
        return self.k.read(ResourceKey(guid, tt["hash"], 0))

    def hierarchy(self):
        if self._hier is None:
            self._hier = self.dec.decode(self.t.get("keen::ModelHierarchyResource"), self.read(PLAYER_SKELETON, "keen::ModelHierarchyResource"))
            g = self.dec.decode(self.t.get(GRAPH_TYPE), self.read(PLAYER_GRAPH, GRAPH_TYPE))
            mapping = g["slotBoneIndexMapping"]
            self.slot_joint = {s: (mapping[s]["value"] if isinstance(mapping[s], dict) else mapping[s]) for s in (SLOT_HAND_R, SLOT_HAND_L)}
            self.raw_hier = self.read(PLAYER_SKELETON, "keen::ModelHierarchyResource")
        return self._hier

    def expect(self, mod_dir, clip):
        """Where the cooked clip puts HandR and HandL, model space, at 60 frames a second: [(t, HandR, HandL)]."""
        import skindeform as D
        from kfc import anim
        hier = self.hierarchy()
        guid = derive(json.loads((mod_dir / "kiln.mod.json").read_text())["id"], "clip", clip)
        entry = next(e for e in json.loads((mod_dir / "assets/assets.json").read_text())["entries"] if e.get("guid") == guid)
        c = anim.parse((mod_dir / "assets" / entry["file"]).read_bytes())
        dp = anim.parse_default_pose(self.raw_hier)
        out = []
        for f in range(c.frame_count):
            w = D.pose_of_clip(hier, anim.sample_frame(c, float(f), dp))
            jr, jl = self.slot_joint[SLOT_HAND_R], self.slot_joint[SLOT_HAND_L]
            out.append((f / 60.0, tuple(w[jr][r][3] for r in range(3)), tuple(w[jl][r][3] for r in range(3))))
        return out

    def carrier(self, base, carrier, overlay_events=(), state=None):
        """A copy of a game sequence served over an unused one: every event's consume off (PlaySequence refuses a sequence that consumes
        the used item), and the overlay events at `overlay_events` pointed at `state` with their requiredEntityTag cleared (the wand
        and bow casts gate them on tags a 1_Player does not have). -> (bytes, what was changed)."""
        from kfc.archive import ResourceKey
        st = self.t.get(SEQ_TYPE)
        guid = self._seq_guid(base)
        v = self.dec.decode(st, self.k.read(ResourceKey(guid, st["hash"], 0)))
        v["resourceId"] = carrier
        consume = changed = n = 0
        for e in v["subSequences"][0]["events"]:
            val = e["value"]
            if val.get("consume"):
                val["consume"] = False
                consume += 1
            if e["$type"].endswith("SetAnimationOverlayEvent"):
                if n in overlay_events:
                    val["overlayName"] = state
                    val["requiredEntityTag"] = ZERO
                    changed += 1
                n += 1
        return self.enc.encode(st, v), f"{base}: {consume} consume off, {changed} overlay event(s) pointed at the state"

    def _seq_guid(self, name):
        if not hasattr(self, "_names"):
            st = self.t.get(SEQ_TYPE)
            self._names = {}
            for key in self.k.resources_of_type(st["hash"]):
                try:
                    v = self.dec.decode(st, self.k.read(key))
                except Exception:
                    continue
                g = key[0] if isinstance(key[0], str) else str(uuid.UUID(bytes_le=key[0]))
                for s in v.get("subSequences", []):
                    self._names.setdefault(s.get("name"), g)
        return self._names[name]


def follows(p, expected, lo_ms=250, hi_ms=1900, shifts=(-0.2, 0.35)):
    """Mean distance of HandR and HandL from where the clip puts them, at the best start time of the clip within `shifts`
    seconds of the sequence's start; samples between lo_ms and hi_ms. -> (mean, start)."""
    best = None
    n = len(expected)
    for k in range(int(shifts[0] * 100), int(shifts[1] * 100) + 1, 2):
        d0 = k / 100
        ds = []
        for step, ms, st, hr, hl, *_ in p["pts"]:
            if not lo_ms <= ms <= hi_ms:
                continue
            f = int(round((ms / 1000 - d0) * 60))
            if 0 <= f < n:
                ds.append((math.dist(hr, expected[f][1]) + math.dist(hl, expected[f][2])) / 2)
        if len(ds) >= 10 and (best is None or sum(ds) / len(ds) < best[0]):
            best = (sum(ds) / len(ds), d0)
    return best or (float("inf"), 0)


# ---------------------------------------------------------------------------------------------------------------------------- scenarios

def full_body_clones(mods):
    """A copy of a full-body game sequence per state, its SetAnimationEvent naming the state."""
    out = []
    for (mod, name), (_, base) in AUTHORED.items():
        if mod in mods:
            out.append({"guid": clone_guid(f"{mod}/{name}"), "name": f"player-anim-test.{mod}.{name}", "base": base,
                        "set": {"events[SetAnimationEvent#0].animationName": derive(mod, "state", name)}})
    if "pa-a" in mods:
        out.append({"guid": clone_guid("pa-a/guard-game-clip"), "name": "player-anim-test.pa-a.guard-game-clip", "base": HEAVY,
                    "set": {"events[SetAnimationEvent#0].animationName": derive("pa-a", "state", "guard-game-clip")}})
    return out


def P(label, seq, steps=100, after=40, equip=None):
    return {"label": label, "seq": seq, "steps": steps, "after": after, "equip": equip}


GAME_PLAYS = [P("game-sword", SWORD, 60), P("game-heavy", HEAVY, 100), P("game-block", BLOCK, 60), P("game-greet", GREET, 100)]
STAFF_PLAYS = [P("game-fireball", FIREBALL, 80, equip=STAFF)]


def mod_plays(mods):
    return [P(f"{m}/{n}", clone_guid(f"{m}/{n}"), 100) for (m, n) in AUTHORED if m in mods] + \
           ([P("pa-a/guard-game-clip", clone_guid("pa-a/guard-game-clip"), 100)] if "pa-a" in mods else [])


def lookups(mods):
    names = {"pa-a": STATES_A, "pa-b": STATES_B, "pa-cast": ["cast", "zap", "loose"], "pa-bad-like": ["nowhere", "fine"],
             "pa-bad-skeleton": ["wave"], "pa-no-clients": ["slash"]}
    return [[m, n] for m in mods for n in names.get(m, [])]


def plan(scenario, mods_measured, extra_plays=(), lookups_of=(), up=0, **more):
    p = {"scenario": scenario, "lookups": lookups(lookups_of), "gameStates": ["Attack_Sword_Shield_02", "Block_FullBody", "Attack_Heavy"],
         "clones": full_body_clones(mods_measured), "plays": [*mod_plays(mods_measured), *extra_plays], "upSeconds": up}
    p.update(more)
    return p


def offline_hash(lab, mods):
    """animcook player-graph over the cooked copies: the hash the service should serve, from the first 8 bytes of SHA-256."""
    out = lab.out / ("merge-" + "+".join(mods))
    shutil.rmtree(out, ignore_errors=True)
    args = [sys.executable, str(REPO / "tools/animcook/animcook.py"), "--game", str(lab.game), "player-graph", "--out", str(out)]
    for m in mods:
        args += ["--mod", str(lab.cook(m))]
    r = subprocess.run(args, capture_output=True, text=True)
    g = out / "player-graph.bin"
    if not g.exists():
        return None, r.stdout + r.stderr
    rep = json.loads((out / "player-graph.report.json").read_text())
    return hashlib.sha256(g.read_bytes()).hexdigest()[:16], rep.get("graphHash")


def scenarios(lab, c, a):
    game = Game(lab)
    base = {}

    def expectations(mods):
        return {(m, n): game.expect(lab.cook(m), clip) for (m, n), (clip, _) in AUTHORED.items() if m in mods}

    def judge_states(text, mods, prefix, ref):
        """Each authored state entered and its pose following its clip; the control in `base` stays apart from the clip."""
        exp = expectations(mods)
        info = state_info(text)
        got = plays(text)
        for (m, n), e in exp.items():
            key = f"{m}/{n}"
            s = info.get(key, {})
            sid = fnv(derive(m, "state", n))
            c(f"{prefix}.{key}.live", s.get("status") == "Live" and s.get("id") == sid, f"{s}, id by the rule 0x{sid:08x}")
            p = got.get(key)
            if p is None:
                c(f"{prefix}.{key}.played", False, "no play line")
                continue
            c(f"{prefix}.{key}.entered", f"{sid:08x}" in states_seen(p), f"state ids {states_seen(p)}")
            if n == "guard":
                # under the block's bone mask: the arms are the clip's, the legs the block's: the hands are apart from the game's guard
                g = ref.get("game-block") or got.get("game-block")
                d = near(p, g, 10, 40)[0] if g else float("inf")
                c(f"{prefix}.{key}.not-the-games-guard", APART <= d < float("inf"), f"{d:.3f} m from the game's block pose")
                continue
            d, start = follows(p, e)
            ctl = base.get(key)
            dc = follows(ctl, e)[0] if ctl else float("inf")
            c(f"{prefix}.{key}.follows-its-clip", d <= FOLLOWS, f"{d:.3f} m from the clip at the best start ({start:+.2f} s); the same copy on the game's graph {dc:.3f} m")
            if ctl:
                c(f"{prefix}.{key}.control-apart", dc >= APART, f"{dc:.3f} m")

    # --------------------------------------------------------------------------------------------- baseline and controls
    def baseline():
        lab.set_mods([], plan("baseline", ["pa-a", "pa-b"], GAME_PLAYS + STAFF_PLAYS))
        text, gtext, _, alive = lab.run("baseline")
        got = plays(text)
        base.update(got)
        base["_errors"] = game_errors(gtext)
        _, graph, h = graph_line(text)
        c("baseline.game-graph", h == GAME_HASH and graph in ("Untouched", "Off"), f"graph {graph}, hash {h}")
        for k in ("game-sword", "game-heavy", "game-block", "game-greet", "game-fireball"):
            p = got.get(k)
            c(f"baseline.{k}.plays", p is not None and p["started"] and moved(p) > 0.3, f"HandR moved {moved(p):.3f} m" if p else "no play")
        for (m, n) in AUTHORED:
            p = got.get(f"{m}/{n}")
            if p is None:
                continue
            sid = f"{fnv(derive(m, 'state', n)):08x}"
            c(f"baseline.control.{m}/{n}", sid in states_seen(p) and moved(p) < APART,
              f"the id {sid} reaches the graph {sid in states_seen(p)}, HandR moves {moved(p):.3f} m (the game's graph has no such state)")
        c("baseline.server-up", alive)

    def unknown_id():
        # condition 3: what a client without the mod runs when the server sends it a mod state's id
        guid = derive("player-anim-test", "state", "no-such-state")
        clone = {"guid": clone_guid("unknown"), "name": "player-anim-test.unknown", "base": HEAVY, "set": {"events[SetAnimationEvent#0].animationName": guid}}
        lab.set_mods([], {"scenario": "unknown-id", "clones": [clone], "upSeconds": 60,
                          "plays": [P("rest", "Attack_Player_Unarmed_Fists_LightChain01", 2, 60), P("unknown", clone["guid"], 100), P("game-sword", SWORD, 60)]})
        text, gtext, _, alive = lab.run("unknown-id")
        got = plays(text)
        p = got.get("unknown")
        sid = f"{fnv(guid):08x}"
        c("unknown-id.reaches-the-graph", p is not None and sid in states_seen(p), f"state ids {states_seen(p) if p else None}")
        c("unknown-id.pose-stays", p is not None and moved(p) < APART, f"HandR moves {moved(p):.3f} m over the attack's length" if p else "")
        new = [l for l in game_errors(gtext) if l not in base.get("_errors", [])]
        c("unknown-id.game-log-quiet", not new, str(new[:3]))
        c("unknown-id.server-up", alive and "pat: up 60 s" in text)
        g = got.get("game-sword")
        c("unknown-id.game-states-after", g is not None and moved(g) > 0.3, f"HandR moved {moved(g):.3f} m" if g else "")

    def untouched():
        lab.set_mods([], {"scenario": "untouched", "plays": [P("game-sword", SWORD, 60)]})
        text, *_ = lab.run("untouched")
        _, graph, h = graph_line(text)
        c("untouched.state", graph == "Untouched" and h == GAME_HASH, f"graph {graph}, hash {h}")
        c("untouched.log-line", any("no mod defines a player state" in l for l in service_lines(text)), str(service_lines(text)[:3]))
        size, sha = served(text)
        game_sha = hashlib.sha256(game.read(PLAYER_GRAPH, GRAPH_TYPE)).hexdigest()[:16]
        c("untouched.the-games-bytes", sha == game_sha, f"served {size} B {sha}, the game's {game_sha}")

    # --------------------------------------------------------------------------------------------- with the service
    def entered():
        lab.set_mods(["pa-a"], plan("entered", ["pa-a"], [P("game-block", BLOCK, 60)], ["pa-a"], second=["pa-a/slash"], define=True))
        text, gtext, _, alive = lab.run("entered")
        m = merged_line(text)
        _, graph, h = graph_line(text)
        c("entered.merged", m is not None and graph == "Merged" and m["hash"] == h, f"{m}; PlayerAnimStates.Graph {graph}, GraphHash {h}")
        size, sha = served(text)
        c("entered.served-is-the-hash", sha == h, f"kiln.assets serves {size} B sha256 {sha}")
        judge_states(text, ["pa-a"], "entered", {})
        got = plays(text)
        gg, gb = got.get("pa-a/guard-game-clip"), got.get("game-block")
        d = near(gg, gb, 10, 40)[0] if gg and gb else float("inf")
        c("entered.guard-game-clip-is-the-games-guard", d <= 0.05, f"{d:.3f} m from the game's block pose over steps 20 to 80 (a shared cached pose)")
        p2 = got.get("p2/pa-a/slash")
        if p2:
            d2 = follows(p2, game.expect(lab.cook("pa-a"), "a_slash"))[0]
            c("entered.second-player", d2 <= FOLLOWS, f"{d2:.3f} m")
        fg = pat(text, r"pat: running: game state Attack_Heavy: (.*)")
        c("entered.find-game-states-bare-name", bool(fg) and fg[-1][1].count(";") >= 1, fg[-1][1] if fg else "no line")
        d = pat(text, r"pat: define: (.*)")
        c("entered.define-from-code", bool(d) and "Refused" in d[-1][1] and "not available" in d[-1][1], d[-1][1] if d else "no line")
        c("entered.server-up", alive)

    def game_states():
        lab.set_mods(["pa-a", "pa-b"], plan("game-states", [], GAME_PLAYS + STAFF_PLAYS, ["pa-a", "pa-b"]))
        text, gtext, _, alive = lab.run("game-states")
        got = plays(text)
        for k in ("game-sword", "game-heavy", "game-block", "game-greet", "game-fireball"):
            p, b = got.get(k), base.get(k)
            d = near(p, b)[0] if p and b else float("inf")
            c(f"game-states.{k}", d <= NOISE, f"{d:.3f} m from the same sequence on the game's graph")
        new = [l for l in game_errors(gtext) if l not in base.get("_errors", [])]
        c("game-states.game-log-as-without-mods", not new, str(new[:3]))

    def two_mods():
        lab.set_mods(["pa-a", "pa-b"], plan("two-mods", ["pa-a", "pa-b"], [P("game-block", BLOCK, 60)], ["pa-a", "pa-b"]))
        text, *_ = lab.run("two-mods")
        info = state_info(text)
        live = [k for k, v in info.items() if v.get("status") == "Live"]
        c("two-mods.all-live", len(live) == len(STATES_A) + len(STATES_B), f"{len(live)} live: {live}")
        judge_states(text, ["pa-a", "pa-b"], "two-mods", {})
        got = plays(text)
        a, b = got.get("pa-a/slash"), got.get("pa-b/slash")
        d = near(a, b)[0] if a and b else 0
        c("two-mods.same-neighbour-own-clips", d >= APART, f"A's and B's states beside Attack_Sword_Shield_02 are {d:.3f} m apart")
        _, _, h = graph_line(text)
        want, rep = offline_hash(lab, ["pa-a", "pa-b"])
        c("two-mods.hash-is-animcooks", h == want and (rep is None or rep == want), f"served {h}, animcook's bytes {want}, its report {rep}")
        base["two-mods-hash"] = h

    def removed():
        lab.set_mods(["pa-a"], plan("removed", ["pa-a"], [], ["pa-a", "pa-b"]))
        text, *_ = lab.run("removed")
        names = [m[1] for m in pat(text, r"pat: running: all \d+: (.*)")]
        c("removed.b-gone", bool(names) and "pa-b/" not in names[-1], names[-1] if names else "no line")
        _, _, h = graph_line(text)
        want, _ = offline_hash(lab, ["pa-a"])
        c("removed.hash-of-a-alone", h == want and h != base.get("two-mods-hash"), f"served {h}, animcook's for A alone {want}, with B it was {base.get('two-mods-hash')}")
        p = plays(text).get("pa-a/slash")
        d = follows(p, game.expect(lab.cook("pa-a"), "a_slash"))[0] if p else float("inf")
        c("removed.a-still-plays", d <= FOLLOWS, f"{d:.3f} m")

    def broken():
        mods = ["pa-a", "pa-bad-like", "pa-bad-skeleton", "pa-no-clients"]
        lab.set_mods(mods, plan("broken", [], [P("game-sword", SWORD, 60)], mods, up=60))
        text, _, _, alive = lab.run("broken")
        info = state_info(text)
        warn = "\n".join(l for l in text.splitlines() if "playeranim" in l and re.match(r"\[(W|E) ", l))
        for key, word in (("pa-bad-like/nowhere", "Attack_Sword_Shield_99"), ("pa-bad-skeleton/wave", "another skeleton"), ("pa-no-clients/slash", "clients")):
            s = info.get(key, {})
            c(f"broken.{key}.refused", s.get("status") == "Refused" and word in s.get("reason", ""), f"{s.get('status')}: {s.get('reason')}")
            c(f"broken.{key}.in-kiln-log", key.split("/")[1] in warn and word in warn, "")
        others = [k for k in ("pa-bad-like/fine", *(f"pa-a/{n}" for n in STATES_A)) if info.get(k, {}).get("status") == "Live"]
        c("broken.the-rest-live", len(others) == 1 + len(STATES_A), str(others))
        c("broken.server-up", alive and "pat: up 60 s" in text)

    def off():
        lab.set_mods(["pa-a"], plan("off", [], [P("game-sword", SWORD, 60)], ["pa-a"]), config={"testMissingField": "retriggerBlendDuration"})
        text, *_ = lab.run("off")
        _, graph, h = graph_line(text)
        c("off.graph", graph == "Off" and h == GAME_HASH, f"graph {graph}, hash {h}")
        info = state_info(text)
        un = [k for k, v in info.items() if v.get("status") == "Unavailable" and v.get("reason")]
        c("off.all-unavailable-with-reason", len(un) == len(STATES_A), f"{un}; {next(iter(info.values()), {}).get('reason')}")
        c("off.log-line", any(l.startswith("playeranim: off:") for l in service_lines(text)), str([l for l in service_lines(text) if "off" in l][:2]))
        size, sha = served(text)
        game_sha = hashlib.sha256(game.read(PLAYER_GRAPH, GRAPH_TYPE)).hexdigest()[:16]
        c("off.the-games-bytes", sha == game_sha, f"served {sha}, the game's {game_sha}")
        p, b = plays(text).get("game-sword"), base.get("game-sword")
        d = near(p, b)[0] if p and b else float("inf")
        c("off.game-states-play", d <= NOISE, f"{d:.3f} m")

    def overlays():
        st = {n: derive("pa-cast", "state", n) for n in ("cast", "zap", "loose")}
        ov = "events[SetAnimationOverlayEvent#{}].overlayName"
        clones = [{"guid": clone_guid("clone/cast"), "name": "player-anim-test.clone.cast", "base": CHAINLIGHTNING, "set": {ov.format(1): st["cast"]}}]
        carriers, what = {}, []
        for key, base_seq, events, state in (("cast", CHAINLIGHTNING, (1,), st["cast"]), ("zap", WANDCHAIN, (0, 1), st["zap"]),
                                             ("loose", BOWSHOT, (0, 1, 2), st["loose"]), ("chainlightning", CHAINLIGHTNING, (), None), ("bowshot", BOWSHOT, (), None)):
            raw, said = game.carrier(base_seq, CARRIERS[key], events, state)
            carriers[CARRIERS[key]] = raw
            what.append(f"{key}: {said}")
        print("INFO overlays: carrier sequences made from the installed game into the lab's copy of the test mod: " + "; ".join(what), flush=True)
        staff = [P("warm", CARRIERS["chainlightning"], 150, 50, STAFF), P("ref-cast", CHAINHEAL, 150, 50, STAFF), P("clone-cast", clone_guid("clone/cast"), 150, 50, STAFF),
                 P("mod-cast", CARRIERS["cast"], 150, 50, STAFF), P("ref-cast-2", CHAINHEAL, 150, 50, STAFF), P("mod-cast-2", CARRIERS["cast"], 150, 50, STAFF),
                 P("game-chainlightning", CARRIERS["chainlightning"], 150, 50, STAFF)]
        wand = [P("ref-zap", WANDHEAVY, 70, 50, WAND), P("mod-zap", CARRIERS["zap"], 70, 50, WAND), P("mod-zap-1st", CARRIERS["zap"], 16, 0, WAND),
                P("mod-zap-2nd-before-end", CARRIERS["zap"], 70, 50, WAND), P("mod-zap-3rd-after-end", CARRIERS["zap"], 70, 50, WAND)]
        bow = [P("ref-loose", STRONGSHOT, 100, 50, BOW), P("mod-loose", CARRIERS["loose"], 100, 50, BOW), P("ref-loose-2", STRONGSHOT, 100, 50, BOW),
               P("mod-loose-2", CARRIERS["loose"], 100, 50, BOW), P("game-bowshot", CARRIERS["bowshot"], 60, 50, BOW)]
        runs = {}
        for name, mods in (("overlays-control", []), ("overlays", ["pa-cast"])):
            lab.set_mods(mods, {"scenario": name, "lookups": lookups(["pa-cast"]), "clones": clones, "plays": staff + wand + bow, "upSeconds": 60}, carriers=carriers)
            runs[name] = lab.run(name)
        text, gtext, _, alive = runs["overlays"]
        ctext = runs["overlays-control"][0]
        got, ctl = plays(text), plays(ctext)
        refused = [l for l in text.splitlines() if "player-anim-test.clone.cast" in l and "OverlayId" in l]
        if refused:
            c.skip("overlays.clone-route", "kiln.gamedata refuses an overlayName in a sequence copy on this tree: " + refused[0].split("] ", 2)[-1][:200])
        else:
            p = got.get("clone-cast")
            d = near(p, got.get("ref-cast"), 8, 31, 2)[0] if p else float("inf")
            c("overlays.clone-route", d <= 0.25, f"{d:.3f} m from the reference")
        info = state_info(text)
        for n in ("cast", "zap", "loose"):
            s = info.get(f"pa-cast/{n}", {})
            c(f"overlays.{n}.live", s.get("status") == "Live" and s.get("kind") == "Overlay", str(s))
        # windows as in the T0b proof runs: the staff shoot from 0.4 s, the wand's whole cast, the bow's special shot (its overlay at 0.4 s, the reference's at 0.13 s)
        for n, mod, ref, lo, hi, off in (("cast", "mod-cast", "ref-cast", 8, 31, 0), ("cast-2", "mod-cast-2", "ref-cast-2", 8, 31, 0),
                                         ("zap", "mod-zap", "ref-zap", 0, 21, 0), ("loose", "mod-loose", "ref-loose", 6, 16, -3),
                                         ("loose-2", "mod-loose-2", "ref-loose-2", 6, 16, -3)):
            m, r, k = got.get(mod), got.get(ref), ctl.get(mod)
            d = near(m, r, lo, hi, 2, offset=off)[0] if m and r else float("inf")
            dc = near(k, ctl.get(ref) or r, lo, hi, 2, offset=off)[0] if k and r else float("inf")
            c(f"overlays.{n}.follows-the-reference", d <= 0.25 and dc >= d + 0.1, f"merged {d:.3f} m, the same on the game's graph {dc:.3f} m")
        one, two = got.get("mod-zap"), got.get("mod-zap-2nd-before-end")
        if one and two:
            restart = near(two, one, 0, 18, 1)[0]
            cont = min(near(two, one, 0, 18, 1, offset=o)[0] for o in range(6, 14))
            c("overlays.zap-second-cast-continues", cont < restart, f"a second cast before the first ended: {restart:.3f} m from the cast's start, {cont:.3f} m from where the first had got to (no twin state: it does not restart)")
        three = got.get("mod-zap-3rd-after-end")
        if one and three:
            d = near(three, one, 0, 21, 2)[0]
            c("overlays.zap-cast-after-the-end-restarts", d <= 0.2, f"{d:.3f} m from the first cast")
        g = got.get("game-chainlightning")
        c("overlays.game-cast-after", g is not None and moved(g) > 0.5, f"HandR moved {moved(g):.3f} m" if g else "")
        print("INFO overlays: the carrier copies have every event's consume off and the redirected overlay events' requiredEntityTag cleared", flush=True)
        c("overlays.server-up", alive and "pat: up 60 s" in text)

    def join():
        if not a.join:
            c.skip("join", "needs --join and a build with kiln-network-player-graph-gate.patch applied")
            return
        from nettest import Client
        lab.set_mods(["pa-a"], {"scenario": "join", "pause": "join", "upSeconds": 0, "plays": []})
        results = {}

        def clients(text):
            st = json.loads((lab.kiln / "data/playeranim/status.json").read_text())
            own = st.get("hash")
            mods = [("pa-a", "1.0.0", 2)]
            for name, props, want in (("same", {"kiln.playeranim": own}, "welcome"), ("other-hash", {"kiln.playeranim": "0123456789abcdef"}, "reject"),
                                      ("game-graph", {"kiln.playeranim": GAME_HASH}, "reject")):
                cl = Client(lab.net_port, name)
                cl.hello(mods, [], props)
                cl.wait(lambda: cl.welcome is not None or cl.reject is not None, 8)
                results[name] = (cl.welcome is not None, cl.reject, want)
            (lab.dir / "kiln/mods/player-anim-test/go-join").write_text("")
            return results

        text, *_ = lab.run("join", during=clients)
        for name, (welcomed, reject, want) in results.items():
            ok = welcomed if want == "welcome" else (not welcomed and reject is not None and "player animations" in reject[0])
            c(f"join.{name}", ok, f"welcome {welcomed}, reject {reject[0][:160] if reject else None}")

    def timing():
        rows = {}
        for label, mods in (("no-states", []), ("a-and-b", ["pa-a", "pa-b"])):
            rows[label] = []
            for i in range(a.boots):
                lab.set_mods(mods, {"scenario": f"timing-{label}-{i}", "plays": []})
                text, gtext, *_ = lab.run(f"timing/{label}-{i}")
                m = merged_line(text)
                rows[label].append((m["ms"] if m else None, m["split"] if m else "", world_seconds(gtext)))
        for label, r in rows.items():
            ms = [x[0] for x in r if x[0] is not None]
            ws = [x[2] for x in r if x[2] is not None]
            print(f"INFO timing {label}: merge ms mean {statistics.mean(ms) if ms else float('nan'):.1f} worst {max(ms) if ms else float('nan'):.1f}; "
                  f"world running after s mean {statistics.mean(ws) if ws else float('nan'):.2f} worst {max(ws) if ws else float('nan'):.2f}; per boot {r}", flush=True)
        c("timing.boots", all(len(v) == a.boots for v in rows.values()) and all(x[0] is not None for x in rows["a-and-b"]), f"{ {k: len(v) for k, v in rows.items()} }")

    def reload():
        lab.set_mods(["pa-a"], plan("reload", ["pa-a"], [], ["pa-a"], pause="reload", playsAfter=[P("pa-a/slash", clone_guid("pa-a/slash"), 100)]))
        said = {}

        def act(text):
            said["unload"] = lab.control("unload pa-a")
            time.sleep(3)
            said["load"] = lab.control(f"load {lab.kiln / 'mods/pa-a'}")
            time.sleep(3)
            said["reload"] = lab.control("reload pa-a")
            time.sleep(3)
            (lab.kiln / "mods/player-anim-test/go-reload").write_text("")
            return said

        text, _, _, alive = lab.run("reload", during=act)
        print(f"INFO reload: control answers {said}", flush=True)
        after = text.split("pat: go reload", 1)[-1]
        lines = [l for l in service_lines(text.split("pat: paused", 1)[-1])]
        c("reload.service-line", bool(lines), str(lines[:4]))
        p = plays(after).get("pa-a/slash")
        d = follows(p, game.expect(lab.cook("pa-a"), "a_slash"))[0] if p else float("inf")
        c("reload.still-plays", d <= FOLLOWS, f"{d:.3f} m from its clip after the unload, load and reload")
        c("reload.server-up", alive)

    return [("baseline", baseline), ("unknown-id", unknown_id), ("untouched", untouched), ("entered", entered), ("game-states", game_states),
            ("two-mods", two_mods), ("removed", removed), ("broken", broken), ("off", off), ("overlays", overlays), ("join", join),
            ("timing", timing), ("reload", reload)]


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--lab", required=True)
    ap.add_argument("--only", default="", help="scenarios to run (comma-separated); the baseline runs too whenever another needs it")
    ap.add_argument("--out", default=None, help="where the cooked copies and each start's logs go (default $KILN_OUT/player-anim-test)")
    ap.add_argument("--game", default=str(Path.home() / ".local/share/Steam/steamapps/common/Enshrouded"))
    ap.add_argument("--net-port", type=int, default=27193, help="the lab transport's UDP port for fake clients (join)")
    ap.add_argument("--boots", type=int, default=10)
    ap.add_argument("--join", action="store_true")
    a = ap.parse_args()
    lab = Lab(a)
    if REPO in lab.out.resolve().parents:
        sys.exit("run.py: --out is inside the repository; what it writes is made from the game's files")
    c = Checks()
    only = set(filter(None, a.only.split(",")))
    needs_base = {"unknown-id", "entered", "game-states", "two-mods", "off"}
    try:
        for name, fn in scenarios(lab, c, a):
            if only and name not in only and not (name == "baseline" and only & needs_base):
                continue
            fn()
    finally:
        lab.tool("stop", a.lab)
    print(f"player-anim-test: {c.passed} passed, {c.failed} failed, {c.skipped} skipped")
    return 1 if c.failed else 0


if __name__ == "__main__":
    sys.exit(main())
