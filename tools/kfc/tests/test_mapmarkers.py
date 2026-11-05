# SPDX-License-Identifier: MIT
"""tools/kfc map-markers: the pure parts on synthetic bytes (the whole extraction runs against an installed client:
`kfc.py map-markers --compare <markers.json>`; the Rust port's real-client test
checks that both write the same JSON).

    python3 -m unittest discover -s tools/kfc/tests -v
"""
import struct
import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))

from kfc import mapmarkers as M  # noqa: E402

SPAWN_VALUES = ["TriggerRange", "TriggerRange2", "TriggerRange3", "EnemySettings",
                "RandomSpawnerSetting_AllowUpScaling", "RandomSpawnerSetting_AllowDownScaling",
                "RandomSpawnerSetting_MinSpawnerAmount", "EnemyTestFlags", "Level", "EnemyMarkerTag", "TeleporterId"]


class FakeTypes:
    def get(self, name):
        assert name == "keen::ecs::EntitySpawnValue"
        return {"values": [{"name": n, "value": i} for i, n in enumerate(SPAWN_VALUES)]}


class MapMarkersTest(unittest.TestCase):
    def test_clean(self):
        self.assertEqual(M.clean("<location>Thornhold</location>"), "Thornhold")
        self.assertEqual(M.clean("  a <b>bold</b>\n  line "), "a bold line")
        self.assertEqual(M.clean("a<b<c>d"), "a<bd")
        self.assertEqual(M.clean("x <unclosed"), "x <unclosed")

    def test_rounding_matches_the_rust_port(self):
        self.assertEqual(M.r1(0.25), 0.3)  # round() would give 0.2
        self.assertEqual(M.r1(3412.66), 3412.7)
        self.assertEqual(M.r1(-0.04), 0.0)

    def test_rules(self):
        cat = lambda n: (M.object_cat(n) or [None])[0]
        self.assertEqual(cat("LootChest_Random_gold"), "chest-gold")
        self.assertEqual(cat("LootChest_Random_HollowHall_Sarcophagus_Silver"), "chest-silver")
        self.assertEqual(cat("LootChest_Random_Cairn_Sarcophagus_Wood"), "chest-wood")
        self.assertEqual(cat("LootChest_Random_shell"), "pearl-clam")
        self.assertEqual(cat("Prop_Cold_Heights_DragonBrazier_01_On_BossActivation"), "dragon-brazier")
        self.assertEqual(cat("Prop_Logic_Puzzle_Solved_Notifier"), "puzzle")
        self.assertIsNone(cat("Prop_Logic_Puzzle_Solved_Notifier_2"))
        self.assertEqual(cat("Shelf_Secret_01"), "secret-door")
        self.assertEqual(cat("hookshot_target01_front"), "grapple")
        self.assertIsNone(cat("LootChest_Random_"))

    def test_spawn_level(self):
        sg = M.spawn_groups(FakeTypes())
        self.assertEqual(sg[0], 8)
        self.assertEqual([(w, b) for _, w, b in sg[1]], [(2, 0b111), (1, 0b1000), (1, 0b111_0000), (1, 0b1000_0000)])
        levels = [3, 43, 20]
        self.assertEqual(M.spawn_level(bytes([0, 1]), 0, 0x100, levels, sg), 43)
        self.assertEqual(M.spawn_level(bytes([2, 0, 0, 1, 0, 2]), 0, 0x101, levels, sg), 20)
        self.assertEqual(M.spawn_level(bytes([9, 9, 9, 1, 0x2c, 0, 1, 0, 0, 0]), 3, 0x177, levels, sg), 3)
        self.assertIsNone(M.spawn_level(bytes([0, 0, 0]), 0, 0x7, levels, sg))
        self.assertIsNone(M.spawn_level(bytes([0, 7]), 0, 0x100, levels, sg))

    def test_loca_table(self):
        b = bytearray(8 + 2 * 24)
        struct.pack_into("<iI", b, 0, 8, 2)
        for i, (tid, s) in enumerate([(7, "Thornhold"), (42, "<b>Cave</b>")]):
            o = 8 + 24 * i
            struct.pack_into("<I", b, o, tid)
            at = len(b)
            b += s.encode()
            struct.pack_into("<iI", b, o + 4, at - (o + 4), len(s))
        t = M.loca_table(bytes(b))
        self.assertEqual(t[7], "Thornhold")
        self.assertEqual(M.clean(t[42]), "Cave")

    def test_tag_id(self):
        self.assertEqual(M.tag_id("00000000-0000-0000-0000-000000000000"), 0)
        self.assertEqual(M.tag_id("704d8fe5-d817-4839-aee2-5ad8d26733ac"),
                         M.fnv1a32(bytes.fromhex("e58f4d7017d83948aee25ad8d26733ac")))

    def test_compare_counts_lore_as_one(self):
        game = {"cats": [{"id": "lore"}], "points": [[0, 10.0, 10.0, 0, "a", "", 0, ""]]}
        ew = {"cats": [{"id": "lore-general"}, {"id": "lore-drak"}], "points": [[0, 10.5, 10.0, 0, "", "", 0, ""], [1, 500, 500, 0, "", "", 0, ""]]}
        rows = {r["id"]: r for r in M.compare(game, ew)}
        self.assertEqual((rows["lore"]["game"], rows["lore"]["other"], rows["lore"]["other_within"]), (1, 2, 1))


if __name__ == "__main__":
    unittest.main()
