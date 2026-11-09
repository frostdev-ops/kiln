# SPDX-License-Identifier: MIT
"""Offline round trips of the native quest builders (native/services/quests/questbuild.cpp, questpack.cpp), the code
kiln-quests.dll runs at boot, compiled here for Linux and run on the client's own resources: rebuild -> decode with the
game's reflection -> every id found by the game's bucket rule (docs/re/quests.md 2.1). No game data is written outside
$KILN_OUT.

    python3 -m unittest tools.kfc.tests.test_questbuild -v      (or discover -s tools/kfc/tests)

Needs the installed client (skips without it) and a C++20 compiler.
"""
import json
import os
import shutil
import struct
import subprocess
import sys
import tempfile
import unittest
import uuid
from pathlib import Path

HERE = Path(__file__).resolve().parent
ROOT = HERE.parent.parent.parent
sys.path.insert(0, str(HERE.parent))

from kfc.archive import Kfc, ResourceKey  # noqa: E402
from kfc.blob import Decoder  # noqa: E402
from kfc.refl import TypeDb  # noqa: E402
from kfc.util import fnv1a32, guid_bytes  # noqa: E402

GAME = Path.home() / ".local/share/Steam/steamapps/common/Enshrouded"
OUT = Path(os.environ.get("KILN_OUT", ROOT / ".out/models")) / "questbuild"
GUID_KNOWLEDGE = "cb1eeb8b-f58a-4785-bf0f-6dd5437e9a5b"
GUID_JOURNAL = "33701b26-ec1d-423f-8e06-49f023b91b7f"
GUID_NPCS = "377f61dc-c7b8-4e4f-830f-7d2c3e8b7fd8"
BALTHAZAR = "7456862b-53fe-4b94-8a75-d3079afc9323"
MODS = [ROOT / "mods/tests/quest-native-test", ROOT / "mods/samples/NpcSample", ROOT / "mods/samples/quests-sample"]


def compile_cli():
    OUT.mkdir(parents=True, exist_ok=True)
    exe = OUT / "questbuild_cli"
    src = [HERE / "questbuild_cli.cpp", ROOT / "native/services/quests/questpack.cpp", ROOT / "native/services/quests/questbuild.cpp"]
    headers = [ROOT / "native/services/quests/questpack.h", ROOT / "native/services/quests/questbuild.h"]
    if not exe.exists() or any(s.stat().st_mtime > exe.stat().st_mtime for s in src + headers):
        subprocess.run(["c++", "-std=c++20", "-O1", f"-I{ROOT}/native/third_party", f"-I{ROOT}/native/services/quests",
                        *map(str, src), "-o", str(exe)], check=True)
    return exe


def bucket_find(b, base, stride, id_):
    def arr(o):
        r, n = struct.unpack_from("<iI", b, o)
        return o + r, n
    ebase, nb = arr(base)
    kbase, nk = arr(base + 8)
    vbase, nv = arr(base + 16)
    assert nk == nv and nb and nb & (nb - 1) == 0
    first, count = struct.unpack_from("<II", b, ebase + 8 * (id_ & (nb - 1)))
    for k in range(first, first + count):
        if struct.unpack_from("<I", b, kbase + 4 * k)[0] == id_:
            return b[vbase + stride * k:vbase + stride * (k + 1)]
    return None


def all_keys(b, base):
    r, n = struct.unpack_from("<iI", b, base + 8)
    return list(struct.unpack_from(f"<{n}I", b, base + 8 + r))


class BlockInput(unittest.TestCase):
    """Portable input and error isolation checks; no installed game or game data."""

    @classmethod
    def setUpClass(cls):
        cls.exe = compile_cli()

    def parse(self, text):
        r = subprocess.run([str(self.exe), "--parse-blocks"], input=text, capture_output=True, text=True, check=True)
        return json.loads(r.stdout)

    def test_comments_do_not_count_as_nesting(self):
        comment = "{" * 100 + "\"[}"
        self.assertEqual(self.parse('{/*' + comment + '*/"x": 1,//' + comment + '\n}'),
                         {"valid": True, "value": {"x": 1}})

    def test_trailing_commas_before_comments(self):
        self.assertEqual(self.parse('{"x": [1, /* [, */], // }\n}'), {"valid": True, "value": {"x": [1]}})
        for text in ('[,]', '{,}', '[1,,]', '{"x":,}', '{}/*', '{}/*never closed'):
            with self.subTest(text=text):
                self.assertFalse(self.parse(text)["valid"])

    def test_strings_preserve_comments_brackets_and_escapes(self):
        value = {"x": 'http://a/*[{},]*/\\\"quoted"'}
        self.assertEqual(self.parse(json.dumps(value)), {"valid": True, "value": value})
        # Removing comments must not merge two invalid adjacent tokens into one valid number.
        self.assertFalse(self.parse('[1/**/2]')["valid"])

    def test_depth_and_size_limits(self):
        self.assertTrue(self.parse('[' * 64 + '0' + ']' * 64)["valid"])
        self.assertFalse(self.parse('[' * 65 + '0' + ']' * 65)["valid"])
        self.assertFalse(self.parse('{}' + ' ' * (16 << 20))["valid"])

    def test_each_broken_entry_names_its_file_and_rolls_back_the_mod(self):
        with tempfile.TemporaryDirectory(prefix="block-input-", dir=OUT) as td:
            work = Path(td)
            mod = work / "badmod"
            (mod / "quests").mkdir(parents=True)
            (mod / "kiln.mod.json").write_text(json.dumps({"id": "badmod"}))
            (mod / "quests/first.json").write_text(json.dumps({"quests": {
                "a_bad": {"name": "Bad", "priority": "oops", "steps": [{"id": "s", "text": "T"}]},
                "b_good": {"name": "Good", "steps": [{"id": "s", "text": "T"}]},
            }}))
            (mod / "quests/second.json").write_text(json.dumps({"quests": {"missing_name": {"steps": [{"id": "s", "text": "T"}]}},
                                                                "npcs": {"n": {"template": "No_Such_Template", "clone": True}}}))
            out = work / "out"
            r = subprocess.run([str(self.exe), "--out", str(out), "--mod", str(mod)], capture_output=True, text=True)
            self.assertEqual(r.returncode, 1, r.stdout + r.stderr)
            s = json.loads((out / "summary.json").read_text())
            errors = s["errors"]
            self.assertTrue(any("a_bad" in e and "first.json" in e for e in errors), errors)
            self.assertTrue(any("missing_name" in e and "second.json" in e for e in errors), errors)
            self.assertTrue(any("No_Such_Template" in e and "second.json" in e for e in errors), errors)
            self.assertFalse(s["any"])
            self.assertFalse(s["ok"])
            self.assertEqual(s["knowledge"], [])
            self.assertFalse(s["manifest"]["mods"][0]["ok"])
            self.assertEqual((out / "knowledge.bin").stat().st_size, 0)

    def test_localized_block_farewell_and_transition_allocate_text_tags(self):
        # The boot collector copies block dialogues unchanged. Run that same shape through the real native compiler;
        # only its final resource rebuild is unavailable here, because this test supplies no game data.
        with tempfile.TemporaryDirectory(prefix="block-lines-", dir=OUT) as td:
            work = Path(td)
            mod = work / "lines"
            (mod / "quests").mkdir(parents=True)
            (mod / "kiln.mod.json").write_text('{"id":"lines"}')
            farewell = {"en": "Farewell", "de": "Auf Wiedersehen"}
            transition = {"en": "Something else?", "de": "Noch etwas?"}
            (mod / "quests/blocks.json").write_text(json.dumps({
                "npcs": {"n": {"template": "SyntheticNpc", "clone": True, "dialogue": "d"}},
                "dialogues": {"d": {"nodes": {"start": {"text": "Hello"}}, "farewell": [farewell], "transition": [transition]}}
            }))
            names = work / "names.json"
            names.write_text('{"SyntheticNpc":"11111111-2222-4333-8444-555555555555"}')
            out = work / "out"
            subprocess.run([str(self.exe), "--names", str(names), "--out", str(out), "--mod", str(mod)], capture_output=True, text=True)
            summary = json.loads((out / "summary.json").read_text())
            for expected in (farewell, transition):
                tags = [int(tag) for tag, text in summary["texts"].items() if json.loads(text) == expected]
                self.assertEqual(len(tags), 1, summary)
                self.assertNotEqual(tags[0], 0)

    def test_external_native_dialogue_reference_is_refused_explicitly(self):
        with tempfile.TemporaryDirectory(prefix="block-external-", dir=OUT) as td:
            work = Path(td)
            mod = work / "npc"
            (mod / "quests").mkdir(parents=True)
            (mod / "kiln.mod.json").write_text('{"id":"npc"}')
            (mod / "quests/npc.json").write_text('{"npcs":{"n":{"template":"SyntheticNpc","dialogue":"other:talk"}}}')
            out = work / "out"
            result = subprocess.run([str(self.exe), "--out", str(out), "--mod", str(mod)], capture_output=True, text=True)
            self.assertEqual(result.returncode, 1)
            summary = json.loads((out / "summary.json").read_text())
            self.assertTrue(any('native dialogue "other:talk"' in error and "not supported" in error and "npc.json" in error for error in summary["errors"]), summary)
            self.assertFalse(summary["any"])


@unittest.skipUnless((GAME / "enshrouded.kfc").exists() or list(GAME.glob("*.kfc")), "no client installed")
class RoundTrip(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.kfc = Kfc(GAME)
        exe = os.environ.get("KILN_TEST_EXE") or (GAME / "enshrouded.exe")
        cls.types = TypeDb.from_exe(Path(exe))
        cls.dec = Decoder(cls.types)
        cls.work = Path(tempfile.mkdtemp(prefix="questbuild", dir=OUT if OUT.exists() else None))
        res = cls.work / "res"
        res.mkdir()
        cls.vanilla = {}
        def dump(guid, type_name):
            t = cls.types.get(type_name)
            data = cls.kfc.read(ResourceKey(guid, t["hash"], 0))
            (res / f"{guid}.{t['hash']:08x}.bin").write_bytes(data)
            cls.vanilla[guid] = data
            return data
        dump(GUID_KNOWLEDGE, "keen::GameKnowledgeResource")
        dump(GUID_JOURNAL, "keen::JournalRegistryResource")
        dump(GUID_NPCS, "keen::NpcCollection")
        # every template by name (the same name -> GUID the service builds at boot)
        tt = cls.types.get("keen::ecs::TemplateResource")
        names = {}
        for key in cls.kfc.resources_of_type(tt["hash"]):
            data = cls.kfc.read(key)
            r, n = struct.unpack_from("<iI", data, 0)
            name = data[r:r + n].decode("utf-8", "replace") if n else ""
            names[name] = key.guid
            if name in ("Townsfolk_Female_villager_01_HannahReed", "Townsfolk_Male_villager_01_GuntherGaleforth",
                        "NPC_Workshop_Alchemist01", "NPC_Workshop_Fisher_Trader", "Townsfolk_Male_villager_04_AntonGodwin",
                        "Townsfolk_Male_villager_02_RamoneJones",
                        "8kMapLabel_townsfolk_female_03_GabrielleFairburn_searchArea_01_noUI"):
                (res / f"{key.guid}.{tt['hash']:08x}.bin").write_bytes(data)
                cls.vanilla[key.guid] = data
        cls.names = names
        (cls.work / "names.json").write_text(json.dumps(names))
        cls.exe = compile_cli()
        mods = [m for m in MODS if (m / "quests").is_dir()]
        cmd = [str(cls.exe), "--res", str(res), "--names", str(cls.work / "names.json"), "--out", str(cls.work / "out")]
        for m in mods:
            cmd += ["--mod", str(m)]
        cls.run_out = subprocess.run(cmd, capture_output=True, text=True)
        cls.out = cls.work / "out"
        cls.summary = json.loads((cls.out / "summary.json").read_text())

    @classmethod
    def tearDownClass(cls):
        shutil.rmtree(cls.work, ignore_errors=True)

    def test_build_ok(self):
        self.assertEqual(self.summary["errors"], [], self.run_out.stdout + self.run_out.stderr)
        self.assertTrue(self.summary["ok"])

    def test_uuid5_is_pythons(self):
        out = subprocess.run([str(self.exe), "--hash", "voiceover:greeting"], capture_output=True, text=True).stdout.split()
        expected = uuid.uuid5(uuid.UUID("928632ef-3270-4bc2-8657-0b6bcdeb2976"), "voiceover:greeting")
        self.assertEqual(out[0], str(expected))
        self.assertEqual(int(out[1], 16), fnv1a32(expected.bytes_le))

    def test_type_hashes(self):
        for name, h in (("keen::GameKnowledgeResource", 0xbc47511c), ("keen::JournalRegistryResource", 0x60b5ed8a),
                        ("keen::NpcCollection", 0x43ae2af5), ("keen::ecs::TemplateResource", 0x39768775),
                        ("keen::ecs::NpcDialog", 0xa9ad90e6), ("keen::ecs::ActiveNpcState", 0x1a8b5909),
                        ("keen::ecs::LocaTagComponent", 0x59dc15fd), ("keen::ecs::Crafting", 0xad776575),
                        ("keen::ecs::MapMarker", 0x8af070c5), ("keen::ecs::RuntimeMapMarkerUnlockRequirements", 0x3f65cd0b)):
            self.assertEqual(self.types.get(name)["hash"], h, name)

    def test_layouts(self):
        # the constants of questbuild.cpp against the game's reflection: a game update that moves one fails here
        def off(t, f):
            return next(x["offset"] for x in self.types.all_fields(self.types.get(t)) if x["name"] == f)
        for t, size in (("keen::WorldKnowledgeObjectResource", 12), ("keen::PlayerKnowledgeObjectResource", 24),
                        ("keen::JournalQuestResource", 44), ("keen::JournalEntryResource", 72), ("keen::NpcResource", 136),
                        ("keen::NpcDialogInitLineResource", 68), ("keen::NpcDialogTransitionLineResource", 64),
                        ("keen::NpcDialogFarewellResource", 64), ("keen::NpcDialogOptionResource", 68),
                        ("keen::KnowledgeUnlockOperation", 12), ("keen::KnowledgeQueryData", 12), ("keen::ecs::NpcDialog", 120),
                        ("keen::NpcDialogResource", 84), ("keen::GameKnowledgeResource", 72), ("keen::ecs::RuntimeMapMarkerUnlockRequirements", 36)):
            self.assertEqual(self.types.get(t)["size"], size, t)
        for t, f, o in (("keen::GameKnowledgeResource", "playerKnowledge", 0x18),
                        ("keen::WorldKnowledgeObjectResource", "hasOptionalPlayerProgression", 5), ("keen::WorldKnowledgeObjectResource", "isPlayerKnowledge", 6),
                        ("keen::WorldKnowledgeObjectResource", "type", 7), ("keen::WorldKnowledgeObjectResource", "expectedFlags", 8),
                        ("keen::PlayerKnowledgeObjectResource", "experienceGainId", 0x10), ("keen::PlayerKnowledgeObjectResource", "unlockedSkillPoints", 0x14),
                        ("keen::JournalQuestResource", "referencedDocumentName", 0xc), ("keen::JournalQuestResource", "priority", 0x10),
                        ("keen::JournalQuestResource", "entries", 0x18), ("keen::JournalQuestResource", "source", 0x20),
                        ("keen::JournalQuestResource", "type", 0x21), ("keen::JournalQuestResource", "rewards", 0x24),
                        ("keen::JournalEntryResource", "mapMarkerReference", 0xc), ("keen::JournalEntryResource", "knowledgeRequirement", 0x10),
                        ("keen::JournalEntryResource", "completionRequirement", 0x1c), ("keen::JournalEntryResource", "voiceover", 0x30),
                        ("keen::JournalEntryResource", "itemIconId", 0x40), ("keen::JournalEntryResource", "recommendedLevel", 0x44),
                        ("keen::NpcResource", "npcRoamingRange", 0xc), ("keen::NpcResource", "unlockRequirement", 0x10), ("keen::NpcResource", "workshopId", 0x1c),
                        ("keen::NpcResource", "workshopLabel", 0x20), ("keen::NpcResource", "itemId", 0x24), ("keen::NpcResource", "itemRef", 0x28),
                        ("keen::NpcResource", "debugName", 0x80), ("keen::ecs::NpcDialog", "dialogResource", 0x14), ("keen::NpcDialogResource", "initLines", 0x34),
                        ("keen::NpcDialogResource", "transitionLines", 0x3c), ("keen::NpcDialogResource", "farewellLines", 0x44),
                        ("keen::NpcDialogResource", "dialogOptions", 0x4c), ("keen::NpcDialogOptionResourceBase", "sequence", 8),
                        ("keen::NpcDialogOptionResourceBase", "knowledgeUnlockOperations", 0x18), ("keen::NpcDialogOptionResourceBase", "knowledgeRequirement", 0x20),
                        ("keen::NpcDialogOptionResourceBase", "bodyTexts", 0x2c), ("keen::NpcDialogOptionResourceBase", "voiceovers", 0x34),
                        ("keen::NpcDialogOptionResourceBase", "uiHintType", 0x3c), ("keen::NpcDialogOptionResourceBase", "dialogEndAction", 0x3e),
                        ("keen::NpcDialogOptionResource", "optionText", 0x40), ("keen::NpcDialogInitLineResource", "isWakeup", 0x40),
                        ("keen::ecs::Crafting", "workshopId", 0x10), ("keen::ecs::RuntimeMapMarkerUnlockRequirements", "visibleStateQuery", 0xc),
                        ("keen::ecs::RuntimeMapMarkerUnlockRequirements", "completedStateQuery", 0x18), ("keen::ecs::NpcCommandEvent", "npcId", 0xc),
                        ("keen::ecs::NpcCommandEvent", "targetTransform", 0x10), ("keen::ecs::NpcCommandEvent", "type", 8)):
            self.assertEqual(off(t, f), o, f"{t}.{f}")

    def test_knowledge(self):
        b = (self.out / "knowledge.bin").read_bytes()
        v = self.vanilla[GUID_KNOWLEDGE]
        d = self.dec.decode("keen::GameKnowledgeResource", b)
        self.assertGreater(len(d["worldKnowledge"]["keys"]), 5825)
        self.assertGreater(len(d["playerKnowledge"]["keys"]), 10744)
        # the other arrays are untouched
        vd = self.dec.decode("keen::GameKnowledgeResource", v)
        for k in ("achievements", "skillPoints", "flagKnowledgePatch"):
            self.assertEqual(d[k], vd[k], k)
        # every vanilla id still found by the game's bucket rule, with the same object
        for base, stride in ((0, 12), (0x18, 24)):
            for key in all_keys(v, base):
                self.assertEqual(bucket_find(b, base, stride, key), bucket_find(v, base, stride, key), hex(key))
        # every new id is found, with its type and scope, and only where it belongs
        self.assertGreater(len(self.summary["knowledge"]), 20)
        for o in self.summary["knowledge"]:
            w = bucket_find(b, 0, 12, o["id"])
            p = bucket_find(b, 0x18, 24, o["id"])
            self.assertEqual(w is not None, o["world"], o["name"])
            self.assertEqual(p is not None, o["player"], o["name"])
            if w:
                self.assertEqual(w[7], o["type"], o["name"])
                self.assertEqual(w[6], 0, o["name"])
                self.assertEqual(w[5], 1 if o["player"] else 0, o["name"])
            if p:
                self.assertEqual(p[7], o["type"], o["name"])
                self.assertEqual(p[6], 1, o["name"])
            self.assertIsNone(bucket_find(v, 0, 12, o["id"]), o["name"])
            self.assertIsNone(bucket_find(v, 0x18, 24, o["id"]), o["name"])
        # no key twice
        for base in (0, 0x18):
            keys = all_keys(b, base)
            self.assertEqual(len(keys), len(set(keys)))

    def manifest(self, mod="quest-native-test"):
        for m in self.summary["manifest"]["mods"]:
            if m["id"] == mod:
                return m
        self.fail(f"no {mod} in the manifest")

    def test_journal(self):
        b = (self.out / "journal.bin").read_bytes()
        d = self.dec.decode("keen::JournalRegistryResource", b)
        vd = self.dec.decode("keen::JournalRegistryResource", self.vanilla[GUID_JOURNAL])
        self.assertEqual(d["quests"][:188], vd["quests"])       # inner offsets rebased: decoded values equal
        for k in ("collections", "itemSetCategories", "itemSetRewards"):
            self.assertEqual(d[k], vd[k])
        m = self.manifest()
        know = bytes((self.out / "knowledge.bin").read_bytes())
        added = d["quests"][188:]
        self.assertGreaterEqual(len(added), 2)
        byid = {q["entryId"]["value"]: q for q in added}
        for q in m["quests"]:
            jq = byid[int(q["entryId"], 16)]
            # game rule (trigger_quest_rewards): the quest's entryId is a player Bool
            self.assertIsNotNone(bucket_find(know, 0x18, 24, int(q["entryId"], 16)))
            self.assertEqual(jq["type"], "WorldQuest" if q["scope"] == "world" else "PlayerQuest")
            self.assertEqual([s["knowledgeRequirement"]["knowledgeOrQueryId"]["value"] for s in jq["entries"]],
                             [int(s["visible"], 16) for s in q["steps"]])
            self.assertEqual([s["completionRequirement"]["knowledgeOrQueryId"]["value"] for s in jq["entries"]],
                             [int(s["done"], 16) for s in q["steps"]])
            # what every quest and step of the game has
            self.assertNotEqual(jq["referencedDocumentName"]["value"], 0)
            self.assertGreaterEqual(jq["priority"], 40)
            for s in jq["entries"]:
                self.assertEqual(s["knowledgeRequirement"]["type"], "SimpleBool")
                self.assertIn(str(s["name"]["value"]), self.summary["texts"])
                self.assertIn(str(s["text"]["value"]), self.summary["texts"])

    def test_npcs(self):
        b = (self.out / "npcs.bin").read_bytes()
        d = self.dec.decode("keen::NpcCollection", b)["npcs"]
        vd = self.dec.decode("keen::NpcCollection", self.vanilla[GUID_NPCS])["npcs"]
        self.assertEqual(d[:len(vd)], vd)
        self.assertLessEqual(len(d), 64)
        new = {n["debugName"]: n for n in d[len(vd):]}
        n = new["Kiln_Test_LabClone"]
        self.assertEqual(n["id"]["value"], fnv1a32(guid_bytes("5d2c0a1e-6b57-4c3f-9c11-7f1a2e3b4c5d")))
        self.assertEqual(n["unlockRequirement"]["type"], "SimpleBool")
        self.assertEqual(n["npcRoamingRange"], 8.0)
        base = next(x for x in vd if x["debugName"] == "Townsfolk_Female_villager_01_HannahReed")
        self.assertEqual(n["characterPresetReference"], base["characterPresetReference"])
        self.assertEqual(n["uiRendering"], base["uiRendering"])

    def template(self, guid):
        return self.dec.decode("keen::ecs::TemplateResource", (self.out / f"template.{guid}.bin").read_bytes())

    def comp(self, t, name):
        return next(c["value"] for c in t["components"] if c["$type"] == name)

    def test_clone(self):
        guid = "5d2c0a1e-6b57-4c3f-9c11-7f1a2e3b4c5d"
        t = self.template(guid)
        self.assertEqual(t["name"], "Kiln_Test_LabClone")
        self.assertEqual(self.comp(t, "keen::ecs::ActiveNpcState")["npcId"]["value"], fnv1a32(guid_bytes(guid)))
        r = self.comp(t, "keen::ecs::NpcDialog")["dialogResource"]
        self.assertEqual(len(r["initLines"]), 1)
        self.assertEqual(len(r["dialogOptions"]), 4)
        self.assertEqual(len(r["farewellLines"]), 1)
        self.assertEqual(len(r["transitionLines"]), 1)
        self.assertEqual(str(r["headline"]["value"]) in self.summary["texts"], True)
        # the lines the manifest lists are the ones in the blob; each has as many voiceovers as texts; ids are player objects
        know = (self.out / "knowledge.bin").read_bytes()
        m = self.manifest()
        ids = {int(x["line"], 16) for x in m["lines"] if x["dialogue"].endswith(":lab_clone")}
        self.assertEqual(ids, {r["initLines"][0]["optionId"]["value"]} | {o["optionId"]["value"] for o in r["dialogOptions"]})
        for o in r["dialogOptions"] + r["initLines"]:
            self.assertEqual(len(o["bodyTexts"]), len(o["voiceovers"]))
            self.assertIsNotNone(bucket_find(know, 0x18, 24, o["optionId"]["value"]))
        # a node's gate: the option that leads to it sets it, its options require it
        start, _, _, more = r["dialogOptions"]
        gate = start["knowledgeUnlockOperations"][0]["knowledgeId"]["id"]["value"]
        self.assertEqual(more["knowledgeRequirement"]["knowledgeOrQueryId"]["value"], gate)
        self.assertEqual(more["knowledgeRequirement"]["type"], "SimpleBool")
        self.assertIsNotNone(bucket_find(know, 0x18, 24, gate))
        # "available" = the quest's started object is unset
        self.assertEqual(start["knowledgeRequirement"]["type"], "SimpleCount")
        self.assertEqual(start["knowledgeRequirement"]["compareOperator"], "Equals")
        self.assertEqual(start["knowledgeRequirement"]["compareValue"], 0)

    def test_append_keeps_balthazar(self):
        t = self.template(BALTHAZAR)
        v = self.dec.decode("keen::ecs::TemplateResource", self.vanilla[BALTHAZAR])
        self.assertEqual(t["name"], v["name"])
        r, vr = self.comp(t, "keen::ecs::NpcDialog")["dialogResource"], self.comp(v, "keen::ecs::NpcDialog")["dialogResource"]
        self.assertEqual(r["dialogOptions"][:len(vr["dialogOptions"])], vr["dialogOptions"])
        self.assertEqual(r["initLines"][:len(vr["initLines"])], vr["initLines"])
        # two mods add to him (the test mod and the sample): one override, in load order; only the test mod asks for a greeting
        self.assertEqual(len(r["dialogOptions"]), len(vr["dialogOptions"]) + 2)
        self.assertEqual(len(r["initLines"]), len(vr["initLines"]) + 1)
        added = [o["optionId"]["value"] for o in r["dialogOptions"][len(vr["dialogOptions"]):]]
        lines = [int(x["line"], 16) for m in self.summary["manifest"]["mods"] for x in m["lines"] if x["npc"] == "balthazar" and x["option"] >= 0]
        self.assertEqual(sorted(added), sorted(lines))
        self.assertEqual(r["farewellLines"], vr["farewellLines"])
        self.assertNotEqual(r["headline"], vr["headline"])   # the test data renames him: the check of the template slot in the lab
        self.assertIn(str(r["headline"]["value"]), self.summary["texts"])
        # nothing else in the template moved
        for c, vc in zip(t["components"], v["components"]):
            if c["$type"] != "keen::ecs::NpcDialog":
                self.assertEqual(c, vc, c["$type"])

    def test_marker(self):
        m = self.manifest()
        byname = {(k["quest"], k["step"]): k for k in m["markers"]}
        self.assertIn(("lab_quest", "first"), byname)
        # a step with a place gets one by itself; a step with nowhere to point at does not
        self.assertIn(("lab_hunt", "kill"), byname)
        self.assertNotIn(("lab_quest", "second"), byname)
        self.assertNotIn(("lab_count_quest", "three"), byname)
        k0 = byname[("lab_quest", "first")]
        guid = k0["guid"]
        t = self.template(guid)
        self.assertEqual(t["name"], k0["template"])
        k = next(s for q in m["quests"] if q["id"] == "lab_quest" for s in q["steps"] if s["id"] == "first")
        req = self.comp(t, "keen::ecs::RuntimeMapMarkerUnlockRequirements")
        self.assertEqual(req["visibleStateQuery"]["knowledgeOrQueryId"]["value"], int(k["active"], 16))
        self.assertEqual(req["completedStateQuery"]["knowledgeOrQueryId"]["value"], 0)  # gone when the step is over, as the game's search areas
        marker = self.comp(t, "keen::ecs::MapMarker")
        tag = fnv1a32(guid_bytes(marker["markerId"]))
        self.assertIn(str(tag), self.summary["texts"])
        # the investigation type: an icon on the map and the compass, what 63 of the game's own quest steps point at
        self.assertEqual(marker["markerType"], "fffa09d9-623a-4daa-b202-c7b35bfb178b")
        jd = self.dec.decode("keen::JournalRegistryResource", (self.out / "journal.bin").read_bytes())
        step = next(s for q in jd["quests"][188:] for s in q["entries"] if s["mapMarkerReference"]["value"] == tag)
        self.assertEqual(step["completionRequirement"]["knowledgeOrQueryId"]["value"], int(k["done"], 16))

    def test_counter(self):
        # journal_ui (client 0x30e470): one SimpleCount query shows value / compareValue + 1
        m = self.manifest()
        q = next(x for x in m["quests"] if x["id"] == "lab_count_quest")
        s = q["steps"][0]
        self.assertEqual(s["total"], 3)
        self.assertNotIn("count", q["steps"][1])
        jd = self.dec.decode("keen::JournalRegistryResource", (self.out / "journal.bin").read_bytes())
        jq = next(x for x in jd["quests"][188:] if x["entryId"]["value"] == int(q["entryId"], 16))
        p = jq["entries"][0]["progressStepsRequirement"]
        self.assertEqual(len(p), 1)
        self.assertEqual(p[0]["type"], "SimpleCount")
        self.assertEqual(p[0]["compareValue"], 2)
        self.assertEqual(p[0]["compareOperator"], "GreaterThan")
        self.assertEqual(p[0]["knowledgeOrQueryId"]["value"], int(s["count"], 16))
        self.assertEqual(jq["entries"][1]["progressStepsRequirement"], [])
        # the object is a world counter of a world quest
        know = (self.out / "knowledge.bin").read_bytes()
        self.assertEqual(bucket_find(know, 0, 12, int(s["count"], 16))[7], 1)
        # the game's own steps use the same shape where they count
        vd = self.dec.decode("keen::JournalRegistryResource", self.vanilla[GUID_JOURNAL])
        single = [e["progressStepsRequirement"][0] for g in vd["quests"] for e in g["entries"] if len(e["progressStepsRequirement"]) == 1]
        self.assertTrue(any(x["type"] == "SimpleCount" for x in single) or len(single) == 0)

    def test_one_query_per_line(self):
        # every line has exactly one query; what one cannot say is an object the engine keeps
        m = self.manifest()
        t = self.template("8e4f1b2a-3c6d-4e7f-a0b1-2c3d4e5f6a7b")
        o = self.comp(t, "keen::ecs::NpcDialog")["dialogResource"]["dialogOptions"]
        step = lambda q, s: next(x for g in m["quests"] if g["id"] == q for x in g["steps"] if x["id"] == s)
        req = lambda i: o[i]["knowledgeRequirement"]["knowledgeOrQueryId"]["value"]
        self.assertEqual(req(0), int(step("lab_option_quest", "only")["active"], 16))
        self.assertEqual(req(1), int(step("lab_handin", "give")["ready"], 16))
        computed = {int(c["id"], 16): c for c in m["computed"]}
        self.assertEqual(computed[req(1)]["kind"], "ready")
        self.assertEqual(computed[req(1)]["target"], "quest-native-test:lab_handin.give")
        self.assertEqual((computed[req(2)]["kind"], computed[req(2)]["index"]), ("if", 2))      # all, level, not, item
        self.assertEqual((computed[req(3)]["kind"], computed[req(3)]["index"]), ("if", 3))      # available with a requirement
        gated = computed[req(5)]                                                                 # a later node's option with an "if"
        self.assertEqual((gated["kind"], gated["node"]), ("if", "more"))
        self.assertEqual(int(gated["gate"], 16), o[4]["knowledgeUnlockOperations"][0]["knowledgeId"]["id"]["value"])
        know = (self.out / "knowledge.bin").read_bytes()
        for k in computed:
            self.assertIsNotNone(bucket_find(know, 0x18, 24, k), hex(k))

    def test_quests_sample(self):
        # the Warden's chain: no journal key, no native key, and the C# quest from the shipped file
        m = self.manifest("quests-sample")
        self.assertEqual({q["id"] for q in m["quests"]}, {"ember_errand", "second_errand", "world_watch", "warden_tally"})
        guid = "d8512f2e-21b5-42c2-aff7-0ae5f3d63ed9"
        t = self.template(guid)
        self.assertEqual(t["name"], "Kiln_QuestsSample_Warden")
        self.assertEqual(self.comp(t, "keen::ecs::ActiveNpcState")["npcId"]["value"], fnv1a32(guid_bytes(guid)))
        r = self.comp(t, "keen::ecs::NpcDialog")["dialogResource"]
        self.assertEqual(len(r["dialogOptions"]), 8)
        self.assertEqual(len(r["initLines"]), 2)
        self.assertEqual(r["initLines"][0]["voiceovers"], ["00a3c13d-c08e-403b-8ef6-1925007416f7"])
        steps = {s["id"]: s for q in m["quests"] for s in q["steps"]}
        by = lambda i: r["dialogOptions"][i]["knowledgeRequirement"]["knowledgeOrQueryId"]["value"]
        self.assertEqual(by(0), int(steps["hunt"]["active"], 16))
        self.assertEqual(by(1), int(steps["turnin"]["ready"], 16))   # the hand-in: with the logs in hand only
        computed = {int(c["id"], 16): c for c in m["computed"]}
        self.assertEqual(computed[by(2)]["kind"], "if")              # available with a requirement
        self.assertEqual(computed[by(4)]["kind"], "if")              # a C# quest's requirement
        self.assertEqual(computed[by(6)]["kind"], "if")              # level 50
        jd = self.dec.decode("keen::JournalRegistryResource", (self.out / "journal.bin").read_bytes())
        added = {q["entryId"]["value"]: q for q in jd["quests"][188:]}
        ee = next(q for q in m["quests"] if q["id"] == "ember_errand")
        j = added[int(ee["entryId"], 16)]
        self.assertTrue(j["isMainQuest"])
        self.assertEqual(j["source"], "Grassland")
        markers = {k["step"] for k in m["markers"]}
        self.assertEqual(markers, {"meet", "hunt", "turnin", "walk"})      # every step with a place
        self.assertEqual(j["entries"][1]["progressStepsRequirement"][0]["compareValue"], 1)  # 2 Vukah
        self.assertEqual(j["entries"][2]["progressStepsRequirement"][0]["compareValue"], 2)  # 3 logs
        self.assertEqual(j["entries"][3]["progressStepsRequirement"], [])                     # a hand-in is one thing to do
        for i in (0, 1, 3):
            self.assertNotEqual(j["entries"][i]["mapMarkerReference"]["value"], 0)
        self.assertEqual(j["entries"][2]["mapMarkerReference"]["value"], 0)
        tally = next(q for q in m["quests"] if q["id"] == "warden_tally")
        self.assertEqual(added[int(tally["entryId"], 16)]["entries"][0]["progressStepsRequirement"][0]["compareValue"], 9)

    def test_chat_and_defaults(self):
        # "journal": "chat" and "native": false stay out of the game's data; everything else needs no key
        chat = self.work / "chatmod"
        (chat / "quests").mkdir(parents=True)
        (chat / "kiln.mod.json").write_text(json.dumps({"id": "chatmod"}))
        (chat / "quests/x.json").write_text(json.dumps({
            "format": 1,
            "npcs": {"n": {"template": "NPC_Workshop_Alchemist01", "dialogue": "d"}},
            "quests": {"q": {"name": "Q", "journal": "chat", "steps": [{"id": "s", "text": "t"}]}},
            "dialogues": {"d": {"native": False, "start": "a", "nodes": {"a": {"text": "x", "options": [{"text": "y"}]}}}}}))
        out = self.work / "chatout"
        r = subprocess.run([str(self.exe), "--res", str(self.work / "res"), "--names", str(self.work / "names.json"),
                            "--out", str(out), "--mod", str(chat)], capture_output=True, text=True)
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)
        s = json.loads((out / "summary.json").read_text())
        self.assertFalse(s["any"])
        self.assertEqual(s["templates"], [])

        auto = self.work / "automod"
        (auto / "quests").mkdir(parents=True)
        (auto / "kiln.mod.json").write_text(json.dumps({"id": "automod"}))
        (auto / "quests/x.json").write_text(json.dumps({
            "format": 1,
            "npcs": {"ilse": {"template": "NPC_Workshop_Alchemist01"}},
            "quests": {"q": {"name": "Q", "giver": "ilse", "steps": [{"id": "s", "text": "Bring logs", "objective": {"deliver": {"item": "x", "count": 2, "to": "ilse"}}}]}}}))
        out = self.work / "autoout"
        r = subprocess.run([str(self.exe), "--res", str(self.work / "res"), "--names", str(self.work / "names.json"),
                            "--out", str(out), "--mod", str(auto)], capture_output=True, text=True)
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)
        s = json.loads((out / "summary.json").read_text())
        m = s["manifest"]["mods"][0]
        # an NPC with no dialogue: a topic to take the quest, a topic to hand in
        self.assertEqual(sorted(l["auto"] for l in m["lines"]), ["advance:q.s", "start:q"])
        self.assertEqual([k["step"] for k in m["markers"]], ["s"])   # the NPC to go to is a place

    def test_sample(self):
        m = self.manifest("npcsample")
        guid = "299d68c5-10b9-4af9-91e0-194b308ee461"
        t = self.template(guid)
        self.assertEqual(t["name"], "Kiln_NpcSample_Tobin")
        self.assertEqual(self.comp(t, "keen::ecs::ActiveNpcState")["npcId"]["value"], fnv1a32(guid_bytes(guid)))
        r = self.comp(t, "keen::ecs::NpcDialog")["dialogResource"]
        # voice-overs: the cook's GUIDs (UUIDv5 of the recipe's namespace), parallel to the texts
        cook = json.loads((ROOT / "mods/samples/NpcSample/assets/cook.json").read_text())
        ns = uuid.UUID(cook["guidNamespace"])
        hello, thanks = (str(uuid.uuid5(ns, f"voiceover:{n}")) for n in ("tobin_hello", "tobin_thanks"))
        self.assertEqual(r["initLines"][0]["voiceovers"], [hello])
        self.assertEqual(r["dialogOptions"][0]["voiceovers"], [thanks])
        self.assertEqual(r["dialogOptions"][1]["voiceovers"], [thanks])
        self.assertEqual(r["dialogOptions"][0]["uiHintType"], "Attention")
        # per-language texts, with the tag in the line
        text = json.loads(self.summary["texts"][str(r["dialogOptions"][0]["optionText"]["value"])])
        self.assertEqual(set(text), {"en", "de"})
        self.assertIn(str(r["headline"]["value"]), self.summary["texts"])
        self.assertEqual(self.comp(t, "keen::ecs::Crafting")["workshopId"]["value"], 0xc4acc104)
        # the roster entry and its summon tool
        d = self.dec.decode("keen::NpcCollection", (self.out / "npcs.bin").read_bytes())["npcs"]
        n = next(x for x in d if x["debugName"] == "Kiln_NpcSample_Tobin")
        tool = "83ff4ce1-f84a-4ac7-8ee9-886ab16f280e"
        self.assertEqual(n["itemRef"], tool)
        self.assertEqual(n["itemId"]["value"], fnv1a32(guid_bytes(tool)))
        self.assertEqual(n["workshopId"]["value"], 0xc4acc104)
        self.assertEqual(n["unlockRequirement"]["knowledgeOrQueryId"]["value"], int(m["knowledge"]["tobin_met"], 16))
        items = [json.loads(j) for j in self.summary["contentItems"]]
        self.assertTrue(any(i["guid"] == tool for i in items))
        # the game's Survivors list shows the tool's name and description for the NPC: they are the NPC's, per language
        ti = next(i for i in items if i["guid"] == tool)
        self.assertNotIn("displayName", ti)
        self.assertNotIn("description", ti)
        self.assertEqual(ti["texts"]["en"]["name"], "Tobin the Wanderer")
        self.assertEqual(ti["texts"]["de"]["name"], "Tobin der Wanderer")
        self.assertTrue(ti["texts"]["en"]["description"].startswith("Tobin the Wanderer "))
        self.assertTrue(ti["texts"]["de"]["description"].startswith("Tobin der Wanderer "))
        self.assertEqual(ti["texts"]["en"]["caption"], "Tobin's Whistle")
        self.assertEqual(ti["texts"]["de"]["caption"], "Tobins Pfeife")
        self.assertTrue(any(json.loads(j)["name"] == "Kiln_NpcSample_TobinTrade" for j in self.summary["contentRecipes"]))
        # the option text and the reply are different tags; the quest's journal entry names its source
        self.assertNotEqual(r["dialogOptions"][0]["optionText"], r["dialogOptions"][0]["bodyTexts"][0])
        jd = self.dec.decode("keen::JournalRegistryResource", (self.out / "journal.bin").read_bytes())
        q = next(x for x in jd["quests"][188:] if x["entryId"]["value"] == int(m["quests"][0]["entryId"], 16))
        self.assertEqual(q["source"], "Fisher")
        self.assertEqual(len(q["entries"]), 2)
        self.assertNotEqual(q["entries"][0]["mapMarkerReference"]["value"], 0)
        # the hand-in goes by the items, and "Bring the Twigs to Tobin" points at him
        ret = next(s for g in m["quests"] for s in g["steps"] if s["id"] == "return")
        self.assertEqual(r["dialogOptions"][1]["knowledgeRequirement"]["knowledgeOrQueryId"]["value"], int(ret["ready"], 16))
        self.assertNotEqual(q["entries"][1]["mapMarkerReference"]["value"], 0)

    def test_errors_are_reported_not_built(self):
        bad = self.work / "badmod"
        (bad / "quests").mkdir(parents=True)
        (bad / "kiln.mod.json").write_text(json.dumps({"id": "badmod"}))
        (bad / "quests/x.json").write_text(json.dumps({
            "format": 1,
            "quests": {"q": {"journal": "native", "steps": [{"id": "s", "text": "t"}]}},  # no name
            "npcs": {"n": {"template": "No_Such_Template", "clone": True}},
            "dialogues": {"d": {"native": True, "start": "gone", "nodes": {}}}}))
        out = self.work / "badout"
        r = subprocess.run([str(self.exe), "--res", str(self.work / "res"), "--names", str(self.work / "names.json"),
                            "--out", str(out), "--mod", str(bad)], capture_output=True, text=True)
        self.assertEqual(r.returncode, 1, r.stdout + r.stderr)
        s = json.loads((out / "summary.json").read_text())
        self.assertFalse(s["any"])
        self.assertEqual((out / "knowledge.bin").stat().st_size, 0)
        text = " ".join(s["errors"])
        self.assertIn("\"name\" is required", text)
        self.assertIn("No_Such_Template", text)


if __name__ == "__main__":
    unittest.main()
