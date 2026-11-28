# SPDX-License-Identifier: MIT
"""Read-only exact-build checks for kiln.client.game_input (sigdb feature client.game_input_sites,
sigdb/symbols/client.d/client-game-input.json, docs/re/game-input.md); these tests never execute a game binary.

The game client's local input writer (0x3af340) fills the local player's ClientPlayerInput once per client world step;
kiln.client observes it at client/InputObserved (client.input_observation) and kiln.client.game_input reads the action
bits there, may press or release actions, and clears its own stale presses in the input message (0x3a8970) right before
that message copies the record. Also checks the Combat And Attack Tweaks author's sites 19 and 20 (beta.6 notes) on the
same build: Kiln's two links cover them.

    KILN_GAME_INPUT_CLIENT_EXE=<read-only copy of enshrouded.exe 26430625> \\
        python3 -I -m unittest tools/sigport/tests/test_client_game_input.py
"""
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
ENV = "KILN_GAME_INPUT_CLIENT_EXE"
SHA = "5d9ef2f140b8ed49e30dbee655841b89d08baec20f799a1817e171a18a850c77"  # client 26430625
FRAGMENT = ROOT / "sigdb/symbols/client.d/client-game-input.json"
TYPES = ROOT / f"sigdb/types/client/{SHA}.json"
# client 26430625
WRITER = 0x3AF340          # the local input writer
OBSERVED = 0x3AF55F        # kiln.client's observation point (client/InputObserved)
WRITER_RET = 0x3AF66D      # its ret
MESSAGE = 0x3A8970         # the input message
COPY = 0x3A8A03            # the guard's link: right before the record's copy
WORLD_TIME = 0x9BD9A0      # the client world's time getter
ITEM_FIND = 0xE94470       # the item registry's find (ItemRegistryFind)
GET_COMPONENT = 0x9B2EC0
DIGITAL_LOOP = 0x2477D0    # the digital input writer (sets or clears each of the 56 bits)
CHARGE_CHECK = 0x478260    # the ranged weapon update's charge test
CATT_19 = (0x3AF639, 0x3AF649)  # the author's InputWriterCode: match, selected address
CATT_20 = (0x3A89F9, 0x3A8A03)  # the author's InputMessageCode
# the action bits Kiln names: constant -> keen::ecs::PlayerInputType member
BITS = {
    "client/pit.mainhand": "MainhandAction", "client/pit.mainhand_tap": "MainhandAction_Tap",
    "client/pit.mainhand_hold": "MainhandAction_Hold", "client/pit.mainhand_release": "MainhandAction_Release",
    "client/pit.contextual": "ContextualAction", "client/pit.contextual_tap": "ContextualAction_Tap",
    "client/pit.contextual_hold": "ContextualAction_Hold", "client/pit.block": "Block", "client/pit.evade": "Evade",
    "client/pit.jump": "Jump", "client/pit.ranged_arm": "RangedCombatArm", "client/pit.ranged_shoot": "RangedCombatShoot",
    "client/pit.ranged_shoot_tap": "RangedCombatShoot_Tap",
}


_TYPES = None


def reflected(qualified):
    """The reflected type (fields or enum values) of one qualified name, from sigdb/types."""
    global _TYPES
    if _TYPES is None:
        doc = json.loads(TYPES.read_text())
        _TYPES = {t.get("qualifiedName"): t for t in (doc["types"] if isinstance(doc, dict) else doc)}
    return _TYPES[qualified]


class GameInputDefinitions(unittest.TestCase):
    """What needs no exe: the fragment's shape and its constants against the game's reflection."""

    @classmethod
    def setUpClass(cls):
        cls.doc = json.loads(FRAGMENT.read_text())
        cls.consts = merged(ROOT / "sigdb/symbols/client.json")["constants"]

    def test_the_feature_names_every_symbol_and_constant_it_reads(self):
        self.assertEqual("client", self.doc["side"])
        feature = self.doc["features"]["client.game_input_sites"]
        self.assertEqual(sorted([*self.doc["symbols"], "client/InputObserved", "GetComponent"]), sorted(feature["symbols"]))
        self.assertTrue(set(self.doc["constants"]) <= set(feature["constants"]))
        for name in feature["constants"]:
            self.assertIn(name, self.consts, name)

    def test_the_action_bits_are_the_reflected_player_input_types(self):
        enum = {v["name"]: v["value"] for v in reflected("keen::ecs::PlayerInputType")["values"]}
        for const, member in BITS.items():
            self.assertEqual(enum[member], self.consts[const]["value"], const)

    def test_the_ranged_weapon_fields_are_the_reflected_ones(self):
        nrw = reflected("keen::ecs::NetworkRangedWeapon")
        fields = {f["name"]: f["offset"] for f in nrw["fields"]}
        params = {f["name"]: f["offset"] for f in reflected("keen::ranged_weapon::WeaponParameters")["fields"]}
        instance = {f["name"]: f["offset"] for f in reflected("keen::ItemInstanceInfo")["fields"]}
        self.assertEqual(int(nrw["hash"], 16), self.consts["client/nrw.hash"]["value"])
        self.assertEqual(nrw["size"], self.consts["client/nrw.size"]["value"])
        self.assertEqual(fields["currentState"], self.consts["client/nrw.state"]["value"])
        self.assertEqual(fields["weaponParameters"] + params["weaponType"], self.consts["client/nrw.type"]["value"])
        self.assertEqual(fields["weaponParameters"] + params["chargeDuration"], self.consts["client/nrw.charge"]["value"])
        self.assertEqual(fields["stateStartTime"], self.consts["client/nrw.start"]["value"])
        self.assertEqual(fields["weaponInstance"] + instance["itemId"], self.consts["client/nrw.item"]["value"])
        states = {v["name"]: v["value"] for v in reflected("keen::ranged_weapon::State")["values"]}
        types = {v["name"]: v["value"] for v in reflected("keen::ranged_weapon::WeaponType")["values"]}
        self.assertEqual((2, 0), (states["AimAndCharge"], types["Bow"]))  # client_game_input.h's kRangedAimAndCharge, kWeaponBow

    def test_the_digital_field_and_the_slot_agree_with_the_input_object(self):
        cpi = {f["name"]: f["offset"] for f in reflected("keen::ecs::ClientPlayerInputData")["fields"]}
        self.assertEqual(cpi["digitalInput"], self.consts["client/cpi.digital"]["value"])
        self.assertEqual(self.consts["client/input.entity"]["value"], self.consts["client/input.slot"]["value"] + 4)

    def test_the_server_composition_has_no_game_input(self):
        doc = merged(ROOT / "sigdb/symbols/server.json")
        self.assertNotIn("client.game_input_sites", doc.get("features", {}))


class GameInputSignatures(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        exe = os.environ.get(ENV)
        if not exe:
            raise unittest.SkipTest(f"set {ENV} to a read-only copy of enshrouded.exe 26430625")
        cls.exe = Path(exe)
        cls.image = Image(cls.exe)
        cls.resolver = Resolver(cls.image, merged(ROOT / "sigdb/symbols/client.json"))
        cls.syms, cls.consts, cls.features = cls.resolver.run()

    def rel32(self, at):
        """The target of the call or jump at `at` (E8/E9 rel32)."""
        img = self.image.img
        self.assertIn(img[at], (0xE8, 0xE9))
        return at + 5 + struct.unpack_from("<i", img, at + 1)[0]

    def test_exact_build(self):
        self.assertEqual(SHA, hashlib.sha256(self.exe.read_bytes()).hexdigest())

    def test_the_feature_resolves_at_the_reviewed_places(self):
        self.assertTrue(self.features.get("client.input_observation"))
        self.assertTrue(self.features.get("client.game_input_sites"))
        self.assertEqual(OBSERVED, self.syms["client/InputObserved"]["rva"])
        self.assertEqual(MESSAGE, self.syms["client/InputMessage"]["rva"])
        self.assertEqual(COPY, self.syms["client/InputMessageCopy"]["rva"])
        self.assertEqual(WORLD_TIME, self.syms["client/WorldTimeOf"]["rva"])
        self.assertEqual(GET_COMPONENT, self.syms["GetComponent"]["rva"])
        self.assertTrue(self.syms["@client/InputObserved.held"]["ok"])
        expect = {"client/input.play": 0x3DA8, "client/play.held_item": 0x14F68, "client/input.slot": 0x658,
                  "client/world.period": 0xE17E80, "client/input.step": 0xE17E9C, "client/cpi.digital": 0x3A8}
        for name, value in expect.items():
            self.assertEqual(value, self.consts[name]["value"], name)
            self.assertTrue(self.consts[name]["ok"], name)

    def test_every_pattern_matches_once_in_its_scope(self):
        doc = json.loads(FRAGMENT.read_text())
        for name, definition in doc["symbols"].items():
            for strategy in definition["find"]:
                lo, hi = self.resolver.text_lo, self.resolver.text_hi
                if "scope" in strategy:
                    lo = self.syms[strategy["scope"]["symbol"]]["rva"]
                    hi = min(hi, lo + strategy["scope"]["size"])
                self.assertEqual(1, len(self.resolver.scan(parse(strategy["pattern"])[0], lo, hi)), name)

    def test_the_held_item_is_the_key_of_the_writers_item_lookup(self):
        # at the observation point: mov rcx, [rbp + input.play]; mov rdx, [rbp + ...]; mov ecx, [rcx + held]; call find
        self.assertEqual(ITEM_FIND, self.rel32(OBSERVED + 20))
        merged_syms = merged(ROOT / "sigdb/symbols/client.json")["symbols"]
        self.assertIn("items/ItemRegistryFind", merged_syms)  # Kiln already names it (build record: 0xe94470)
        self.assertEqual(ITEM_FIND, self.syms["items/ItemRegistryFind"]["rva"])

    def test_no_store_to_the_digital_word_after_the_observation(self):
        # between the observation and the writer's ret only the mask (+0x3a0) is stored into r14: a word Kiln writes at
        # the observation is the one the message and the simulation see
        import capstone
        md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)
        img = self.image.img
        stores = []
        for ins in md.disasm(bytes(img[OBSERVED:WRITER_RET + 1]), OBSERVED):
            if ins.mnemonic.startswith("mov") and ins.op_str.split(",")[0].find("[r14") >= 0:
                stores.append((ins.address, ins.op_str))
        self.assertTrue(stores)
        self.assertTrue(all("[r14 + 0x3a0]" in op for _, op in stores), stores)
        self.assertEqual("mov", next(md.disasm(bytes(img[CATT_19[1]:CATT_19[1] + 8]), CATT_19[1])).mnemonic)  # restores r14

    def test_the_digital_writer_sets_or_clears_all_56_bits_at_the_record(self):
        img = bytes(self.image.img)
        # cmp rdi, 0x38 (56 PlayerInputType bits) and the bts/btr store to [rsi + 0x3a8]
        body = img[DIGITAL_LOOP:DIGITAL_LOOP + 0x260]
        self.assertIn(bytes.fromhex("48 83 ff 38"), body)
        self.assertIn(bytes.fromhex("48 0f ab c1"), body)
        self.assertIn(bytes.fromhex("48 0f b3 c1"), body)
        self.assertIn(bytes.fromhex("48 89 8e a8 03 00 00"), body)

    def test_the_message_reads_the_local_players_record_and_its_step(self):
        img = self.image.img
        # the record: GetComponent(out, [r9 + 8] world, the ClientPlayerInput type id, [slot + 4] entity), the same type id
        # global the writer's own lookup loads
        call = 0x3A89E3
        self.assertEqual(GET_COMPONENT, self.rel32(call))
        message_type = 0x3A89DC + struct.unpack_from("<i", img, 0x3A89D5 + 3)[0]
        writer_type = 0x3AF383 + struct.unpack_from("<i", img, 0x3AF37C + 3)[0]
        self.assertEqual(writer_type, message_type)
        self.assertEqual(bytes.fromhex("4c 8b 28"), bytes(img[call + 5:call + 8]))  # mov r13, [rax]
        # the early return for a step already sent, then the guard's instruction
        self.assertEqual(bytes.fromhex("45 3b 74 24 0c 75 08"), bytes(img[0x3A89F4:0x3A89FB]))
        self.assertEqual(COPY, 0x3A89FB + 8)
        # the message's step: the low dword of the clock's {counter, time} (0x87a63a; movd r8d, xmm6 at its call)
        self.assertEqual(0x3AF340, self.rel32(0x87A778))
        self.assertEqual(MESSAGE, self.rel32(0x87A810))
        self.assertEqual(bytes.fromhex("66 41 0f 7e f0"), bytes(img[0x87A7FE:0x87A803]))
        self.assertEqual(bytes.fromhex("0f 10 30"), bytes(img[0x87A642:0x87A645]))

    def test_no_branch_lands_inside_the_guards_relocated_bytes(self):
        import capstone
        md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)
        md.detail = True
        img = self.image.img
        first = list(md.disasm(bytes(img[COPY:COPY + 16]), COPY))[:2]
        self.assertEqual(["lea", "movaps"], [i.mnemonic for i in first])
        self.assertTrue(all("rip" not in i.op_str for i in first))
        end = first[-1].address + first[-1].size
        targets = set()
        for ins in md.disasm(bytes(img[MESSAGE:MESSAGE + 0x1000]), MESSAGE):
            if ins.group(capstone.CS_GRP_JUMP) and ins.op_str.startswith("0x"):
                targets.add(int(ins.op_str, 16))
        self.assertIn(COPY, targets)  # the jne from the early return
        self.assertFalse([t for t in targets if COPY < t < end])

    def test_the_bows_draw_is_the_games_own_test(self):
        img = self.image.img
        # AimAndCharge (state 2): elapsed(now, stateStartTime) >= chargeDuration (cmp rax, [rbx + 0x20]; setge)
        self.assertEqual(bytes.fromhex("48 3b 43 20 0f 9d c0"), bytes(img[0x4782D8:0x4782DF]))
        # the world time: step x period with the infinities (Kiln's world_time)
        self.assertEqual(bytes.fromhex("48 0f af c2"), bytes(img[0x9BD9C8:0x9BD9CC]))

    def test_the_combat_and_attack_tweaks_sites_are_covered(self):
        # beta.6's patterns match once at the author's RVAs; site 20's selected address is Kiln's guard link, and site
        # 19's (after the mask stores) sees the same digitalInput as Kiln's observation point
        catt19 = "B8 FB FF 00 00 66 23 D0 66 41 89 96 A0 03 00 00 4C 8B B4 24 D0 00 00 00 4C 8D 9C 24 B0 00 00 00"
        catt20 = "75 ?? C6 06 01 E9 ?? ?? ?? ?? 48 8D 4D B8 0F 29 B4 24 10 0F 00 00"
        self.assertEqual([CATT_19[0]], self.image.find(catt19))
        self.assertEqual([CATT_20[0]], self.image.find(catt20))
        self.assertEqual(CATT_19[1], CATT_19[0] + 0x10)
        self.assertEqual(COPY, CATT_20[0] + 0xA)
        self.assertTrue(WRITER < OBSERVED < CATT_19[1] < WRITER_RET)


if __name__ == "__main__":
    unittest.main()
