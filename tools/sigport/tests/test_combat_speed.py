# SPDX-License-Identifier: MIT
"""Read-only exact-build checks for kiln.combat.speed (sigdb features combat.action_speed, combat.action_speed.client,
combat.bow_timing and combat.melee_sampling; sigdb/symbols/{server,client}.d/combat-speed.json; docs/re/action-speed.md).
These tests never execute a game binary.

The sites are the ones the Combat And Attack Tweaks author documented for beta.6 (their sites 01-18). Kiln hooks the
sequence clock, the graph binding and evaluation, and the bow parameters; the sampler's sites resolve and are tracked.

    KILN_SPEED_CLIENT_EXE=<read-only copy of enshrouded.exe 26430625> \\
    KILN_SPEED_SERVER_EXE=<read-only copy of enshrouded_server.exe 26430253> \\
        python3 -I -m unittest tools/sigport/tests/test_combat_speed.py
"""
import bisect
import hashlib
import json
import os
from pathlib import Path
import re
import struct
import sys
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from peimg import Image
from sides import merged
from sigdb_check import Resolver, parse

ROOT = Path(__file__).resolve().parents[3]
FRAGMENT = {side: ROOT / f"sigdb/symbols/{side}.d/combat-speed.json" for side in ("server", "client")}
SHA = {"client": "5d9ef2f140b8ed49e30dbee655841b89d08baec20f799a1817e171a18a850c77",   # client 26430625
       "server": "f0d824be7fcd48ebfe2458ff5c28da26060c2074e3ec1fcac587e5a1d508aeb2"}   # server 26430253
ENV = {"client": "KILN_SPEED_CLIENT_EXE", "server": "KILN_SPEED_SERVER_EXE"}

# The reviewed places (docs/re/action-speed.md): symbol -> rva, per side. None: the side has no such site.
SITES = {
    "speed/SeqAdvance": (0x21A800, 0x377A0),
    "speed/SeqClock": (0x21A83F, 0x377DC),          # CATT 01 (client) / 02 (server)
    "speed/SeqServerRet": (0x21A6EE, 0x3768E),      # CATT 03
    "speed/SeqClientRet": (0x2656C6, None),         # CATT 04
    "world/CurrentEntityId": (0x9BF9E0, 0x65B870),  # CATT 05 (Kiln's own symbol)
    "speed/GraphWrap": (0x286040, 0x97D30),
    "speed/GraphBind": (0x286080, 0x97D70),         # CATT 06
    "speed/GraphServerRet": (0x34AC1F, 0x10A27F),   # CATT 07
    "speed/GraphClientRet": (0x307BAA, None),       # CATT 08
    "speed/GraphEval": (0x92ED10, 0x7673F0),        # CATT 09
    "speed/BowTiming": (0x2EBE15, 0xD7DB5),         # CATT 10
    "speed/BowParams": (0x476700, 0x20EF80),
    "speed/HandSlots": (0x34AF4B, 0x10A5AB),        # CATT 11
    "speed/SlotToWorld": (0xF80010, 0x81C080),      # CATT 12
    "speed/ColliderCreate": (0x3F741B, 0x1986BB),   # CATT 13
    "speed/ColliderMove": (0x3F74B0, 0x198750),     # CATT 14
    "speed/CastShape": (0x9A6CC0, 0x642B50),        # CATT 15
    "speed/ContactWriter": (0x9A7B30, 0x6439C0),    # CATT 16
    "speed/ImpactContacts": (0x123ED53, 0x995C33),  # CATT 17
    "speed/CollisionBackend": (0x9CFFA0, 0x66BEF0), # CATT 18
}
# program -> update rva (the game's program descriptors), per side
PROGRAMS = {
    "actor_simulation_start": (0x274620, 0x86720),
    "actor_client_play": (0x2653B0, None),
    "server_update_animation_graph_2": (0x34A6E0, 0x109D40),
    "client_update_animation_graph_2": (0x307580, None),
    "server_player_ranged_weapon": (0x2EBC70, 0xD7C10),
}
FEATURES = {"server": ["combat.action_speed", "combat.bow_timing", "combat.melee_sampling"],
            "client": ["combat.action_speed", "combat.action_speed.client", "combat.bow_timing", "combat.melee_sampling"]}


def at(side, name):
    return SITES[name][0 if side == "client" else 1]


class SpeedDefinitions(unittest.TestCase):
    """What needs no exe: the fragments' shape."""

    def test_each_side_has_its_fragment_and_the_client_one_replaces_the_server_one(self):
        for side in ("server", "client"):
            doc = json.loads(FRAGMENT[side].read_text())
            self.assertEqual(side, doc["side"])
            self.assertNotIn("extends", doc)  # standalone: the client's register contract differs (rdi, not rsi)
            self.assertEqual(sorted(FEATURES[side]), sorted(doc["features"]))

    def test_every_symbol_and_constant_belongs_to_a_feature(self):
        for side in ("server", "client"):
            doc = json.loads(FRAGMENT[side].read_text())
            named = {n for f in doc["features"].values() for n in f.get("symbols", [])}
            consts = {n for f in doc["features"].values() for n in f.get("constants", [])}
            self.assertLessEqual(set(doc["symbols"]), named, side)
            self.assertEqual(set(doc["constants"]), consts, side)
            for name, c in doc["constants"].items():
                self.assertIn("value", c, f"{side} {name}: frame constants are fixed values the captures must equal")

    def test_the_client_only_sites_are_not_in_the_server_composition(self):
        doc = merged(ROOT / "sigdb/symbols/server.json")
        for name in ("speed/SeqClientRet", "speed/GraphClientRet", "@speed/GraphClientActor"):
            self.assertNotIn(name, doc["symbols"])
        self.assertNotIn("combat.action_speed.client", doc["features"])
        client = merged(ROOT / "sigdb/symbols/client.json")
        self.assertEqual(0xA7, client["constants"]["speed/seq.modrm"]["value"])  # lea r12, [rdi + 0x180]: rdi
        self.assertEqual(0x86, doc["constants"]["speed/seq.modrm"]["value"])     # mov rax, [rsi + 0x180]: rsi


class _Side(unittest.TestCase):
    side = None

    @classmethod
    def setUpClass(cls):
        if cls.side is None:
            raise unittest.SkipTest("base class")
        exe = os.environ.get(ENV[cls.side])
        if not exe:
            raise unittest.SkipTest(f"set {ENV[cls.side]} to a read-only copy of the {cls.side} exe")
        cls.exe = Path(exe)
        cls.image = Image(cls.exe)
        cls.resolver = Resolver(cls.image, merged(ROOT / f"sigdb/symbols/{cls.side}.json"))
        cls.syms, cls.consts, cls.features = cls.resolver.run()
        raw = cls.exe.read_bytes()
        pe = struct.unpack_from("<I", raw, 0x3C)[0]
        va, size = struct.unpack_from("<II", raw, pe + 24 + 112 + 3 * 8)  # PE32+ data directory 3: exceptions
        img = cls.image.img
        cls.rows = sorted(struct.unpack_from("<III", img, va + i) for i in range(0, size - 11, 12))
        cls.starts = [r[0] for r in cls.rows]
        # every direct call or jump to the functions the tests ask about, in one pass over .text
        side = cls.side
        interest = {SITES["speed/SeqAdvance"][0 if side == "client" else 1], SITES["speed/GraphWrap"][0 if side == "client" else 1]}
        interest.add(cls.function_of(SITES["speed/SeqServerRet"][0 if side == "client" else 1]))
        lo, hi = cls.resolver.text_lo, cls.resolver.text_hi
        text = bytes(img[lo:hi])
        cls.calls = {t: [] for t in interest}
        for m in re.finditer(rb"[\xe8\xe9]", text):
            s = m.start()
            if s + 5 <= len(text):
                t = lo + s + 5 + struct.unpack_from("<i", text, s + 1)[0]
                if t in cls.calls:
                    cls.calls[t].append(lo + s)

    @classmethod
    def row_at(self, rva):
        i = bisect.bisect_right(self.starts, rva) - 1
        return self.rows[i] if i >= 0 and self.rows[i][0] <= rva < self.rows[i][1] else None

    @classmethod
    def function_of(self, rva):
        img = self.image.img
        chunk = self.row_at(rva)
        if chunk is None:
            return None
        for _ in range(8):  # UNW_FLAG_CHAININFO: the parent RUNTIME_FUNCTION follows the unwind codes
            info = chunk[2]
            if not (img[info] >> 3) & 4:
                return chunk[0]
            n = img[info + 2]
            parent = struct.unpack_from("<I", img, info + 4 + ((n + 1) & ~1) * 2)[0]
            chunk = self.row_at(parent)
        return None

    def rel32(self, site):
        return site + 5 + struct.unpack_from("<i", self.image.img, site + 1)[0]

    def callers(self, target):
        return sorted(self.calls[target])

    def program_update(self, name):
        img = bytes(self.image.img)
        text = img.find(b"\0" + name.encode() + b"\0") + 1
        self.assertGreater(text, 0, name)
        ref = img.find(struct.pack("<QQ", self.image.base + text, len(name)))
        self.assertGreater(ref, 0, name)
        upd = struct.unpack_from("<Q", img, ref + 16)[0]
        return upd - self.image.base if upd else None

    def test_exact_build(self):
        self.assertEqual(SHA[self.side], hashlib.sha256(self.exe.read_bytes()).hexdigest())

    def test_every_feature_resolves_at_the_reviewed_places(self):
        for f in FEATURES[self.side]:
            self.assertTrue(self.features.get(f), f)
        for name in SITES:
            want = at(self.side, name)
            if want is None:
                self.assertNotIn(name, self.syms, name)
            else:
                self.assertEqual(want, self.syms[name]["rva"], name)

    def test_every_pattern_matches_once_in_its_scope(self):
        doc = json.loads(FRAGMENT[self.side].read_text())
        for name, definition in doc["symbols"].items():
            for strategy in definition["find"]:
                lo, hi = self.resolver.text_lo, self.resolver.text_hi
                if "scope" in strategy:
                    lo = self.syms[strategy["scope"]["symbol"]]["rva"]
                    hi = min(hi, lo + strategy["scope"]["size"])
                self.assertEqual(1, len(self.resolver.scan(parse(strategy["pattern"])[0], lo, hi)), name)

    def test_the_programs_are_the_reviewed_updates(self):
        for name, rvas in PROGRAMS.items():
            want = rvas[0 if self.side == "client" else 1]
            self.assertEqual(want, self.program_update(name), name)

    def test_the_sequence_helper_has_only_the_two_anchored_callers(self):
        helper = at(self.side, "speed/SeqAdvance")
        rets = [at(self.side, "speed/SeqServerRet")] + ([at(self.side, "speed/SeqClientRet")] if self.side == "client" else [])
        self.assertEqual(sorted(r - 5 for r in rets), self.callers(helper))
        # the server path: actor_simulation_start's update -> one routine -> the helper
        mid = self.function_of(rets[0])
        self.assertEqual([self.program_update("actor_simulation_start")], [self.function_of(c) for c in self.callers(mid)])
        if self.side == "client":
            self.assertEqual(self.program_update("actor_client_play"), self.function_of(rets[1]))
        # the clock site is inside the helper
        self.assertEqual(helper, self.function_of(at(self.side, "speed/SeqClock")))

    def test_the_graph_wrapper_has_only_the_two_anchored_callers(self):
        wrap = at(self.side, "speed/GraphWrap")
        rets = [at(self.side, "speed/GraphServerRet")] + ([at(self.side, "speed/GraphClientRet")] if self.side == "client" else [])
        self.assertEqual(sorted(r - 5 for r in rets), self.callers(wrap))
        self.assertEqual(self.program_update("server_update_animation_graph_2"), self.function_of(rets[0]))
        if self.side == "client":
            self.assertEqual(self.program_update("client_update_animation_graph_2"), self.function_of(rets[1]))
        self.assertEqual(wrap, self.function_of(at(self.side, "speed/GraphBind")))

    def test_the_mid_sites_relocate_no_branch_target_and_nothing_rip_relative(self):
        import capstone
        md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)
        img = bytes(self.image.img)
        for name in ("speed/SeqClock", "speed/GraphBind", "speed/GraphEval", "speed/BowTiming"):
            site = at(self.side, name)
            n, ins = 0, []
            for i in md.disasm(img[site:site + 32], site):
                ins.append(i)
                n += i.size
                if n >= 5:
                    break
            self.assertFalse([i for i in ins if "rip" in i.op_str], name)
            f = self.function_of(site)
            near = self.rows[max(0, bisect.bisect_left(self.starts, f) - 64):bisect.bisect_left(self.starts, f + 0x20000)]
            chunks = [r for r in near if self.function_of(r[0]) == f]
            for a, b, _ in chunks:
                for i in md.disasm(img[a:b], a):
                    if i.mnemonic.startswith("j") or i.mnemonic == "call":
                        try:
                            t = int(i.op_str, 16)
                        except ValueError:
                            continue
                        self.assertFalse(site < t < site + n, f"{name}: {i.address:#x} lands inside the relocated bytes")

    def test_the_bow_parameters_are_built_in_the_frame_the_site_reads(self):
        img = self.image.img
        site = at(self.side, "speed/BowTiming")
        self.assertEqual(at(self.side, "speed/BowParams"), self.rel32(site - 5))
        self.assertEqual(bytes.fromhex("48 8d 8d") + struct.pack("<I", 0x1B0), bytes(img[site - 12:site - 5]))
        # the stock code copies the 32-byte WeaponParameters from [rax] right after the site
        self.assertEqual(bytes.fromhex("48 8b 4d 00 0f 10 00"), bytes(img[site:site + 7]))
        self.assertEqual(self.program_update("server_player_ranged_weapon"), self.function_of(site))

    def test_the_collision_backend_flag_is_at_the_compare_end_plus_its_displacement(self):
        img = self.image.img
        getter = at(self.side, "speed/CollisionBackend")
        disp = struct.unpack_from("<i", img, getter + 2)[0]
        want = 0x283F334 if self.side == "client" else 0x14DB314
        self.assertEqual(want, getter + 7 + disp)       # cmp byte [rip + disp32], 0 is 7 bytes long
        self.assertNotEqual(want, getter + 6 + disp)    # beta.6 read the neighbouring byte


class ServerSpeed(_Side):
    side = "server"


class ClientSpeed(_Side):
    side = "client"


del _Side

if __name__ == "__main__":
    unittest.main()
