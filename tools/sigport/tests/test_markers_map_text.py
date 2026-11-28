# SPDX-License-Identifier: MIT
"""Read-only exact-build checks for kiln.markers' map tooltip text (GitHub issue 160; sigdb feature
markers.map_tooltip_text in sigdb/symbols/client.d/dev31-markers-map-text.json; docs/re/map.md "The tooltip's name").
These tests never execute a game binary.

MapLabel.NativeTooltip puts a marker's Text in the game's own map tooltip. kiln.markers writes the text view of its own
map entries (+0x50 pointer, +0x58 length, +0x60 hash), the field the game fills from an entity's CustomPlayerString,
and the game's name resolver takes it before anything else. The checks pin what that rests on in client 26430625: the
resolver's order, the player-text path the view goes through (the platform's text filter when there is one, '\\' and
'<' escaped, a copy in the UI's string buffer), the tooltip that starts from the resolver and draws nothing for an
empty name, the hover reset of each frame, and the game's own writers (its append clears the views; replicate fills the
view only from a CustomPlayerString; custom pins carry their own text in the other view).

    KILN_MARKERS_CLIENT_EXE=<read-only copy of enshrouded.exe 26430625> \\
        python3 -I -m unittest discover -s tools/sigport/tests -p test_markers_map_text.py
"""
import hashlib
import json
import os
from pathlib import Path
import struct
import sys
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from peimg import Image
from sides import merged
from sigdb_check import Resolver

ROOT = Path(__file__).resolve().parents[3]
ENV = "KILN_MARKERS_CLIENT_EXE"
SHA = "5d9ef2f140b8ed49e30dbee655841b89d08baec20f799a1817e171a18a850c77"  # client 26430625
FRAGMENT = ROOT / "sigdb/symbols/client.d/dev31-markers-map-text.json"
FEATURE = "markers.map_tooltip_text"
# client 26430625
MAP_NAME = 0xD3D350       # void(Name* out, const MapEntry*): the map entry name resolver
PLAYER_TEXT = 0xD28AF0    # Result*(Result* out, const StringView*): filter, escape, copy into the UI's string buffer
TOOLTIP_TEXTS = 0xD1DF80  # the hovered entry's title and the line under it
CROSSHAIR = 0xC2EEA0      # the map's crosshair window (CrosshairWindow): the hover tooltip
CANVAS = 0xC36070         # the map canvas
MARKER_LAYER = 0xDA4770   # the canvas's marker layer (entry pointers, rebuilt when a list count changes)
HIT_TEST = 0xD9EE20       # the hover hit test over the layer's items
REPLICATE = 0x3161B0      # replicate_map_markers_for_ui
APPEND = 0x302970         # MapEntry* append(MapList*)
CUSTOM_PINS = 0x3087F0    # custom_map_markers_ui
TEXT_FILTER = 0xA49B40    # PlayerText's filter step (the platform's text filter when there is one, else a copy)
UI_FORMAT = 0x11B0DA0     # format into the UI's string buffer (UiApp + 0x18, + 0x6b10)
ESCAPE_FORMAT = 0x89A9E0  # the "%k" argument: escape-char before every byte of the set or equal to it
TYPE_LOOKUP = 0xD3CF90    # const MapMarkerType* (u32 type id)
CSTRING_LOOKUP = 0xE8ECD0 # the string system's lookup of a CustomPlayerString id


class MapTextDefinitions(unittest.TestCase):
    """What needs no exe: the fragment's shape and the composition."""

    def test_the_feature_names_its_guards_and_the_entry_text_constants(self):
        doc = json.loads(FRAGMENT.read_text())
        self.assertEqual("client", doc["side"])
        self.assertNotIn("extends", doc)  # a client-only definition: no server twin
        feature = doc["features"][FEATURE]
        self.assertEqual(
            sorted(["markers/MapName", "@markers/MapName.order", "markers/PlayerText", "@markers/PlayerText.escape",
                    "markers/TooltipTexts", "@markers/Crosshair.title", "@markers/Append.label"]), sorted(feature["symbols"]))
        self.assertEqual(["markers/entry.label", "markers/entry.label_hash", "markers/entry.label_len"],
                         sorted(feature["constants"]))
        self.assertEqual(88, doc["constants"]["markers/entry.label_len"]["value"])

    def test_the_text_offsets_are_the_ones_the_map_list_already_guards(self):
        doc = merged(ROOT / "sigdb/symbols/client.json")
        self.assertIn(FEATURE, doc["features"])
        for name in ("markers/entry.label", "markers/entry.label_hash"):
            self.assertIn(name, doc["features"]["markers.client_map"]["constants"])
        self.assertEqual(80, doc["constants"]["markers/entry.label"]["value"])
        self.assertEqual(96, doc["constants"]["markers/entry.label_hash"]["value"])
        self.assertEqual(doc["constants"]["markers/entry.label"]["value"] + 8, doc["constants"]["markers/entry.label_len"]["value"])

    def test_the_server_composition_has_no_tooltip_text(self):
        doc = merged(ROOT / "sigdb/symbols/server.json")
        self.assertNotIn(FEATURE, doc.get("features", {}))


class MapTextSignatures(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        exe = os.environ.get(ENV)
        if not exe:
            raise unittest.SkipTest(f"set {ENV} to a read-only copy of enshrouded.exe 26430625")
        cls.exe = Path(exe)
        cls.image = Image(cls.exe)
        cls.resolver = Resolver(cls.image, merged(ROOT / "sigdb/symbols/client.json"))
        cls.syms, cls.consts, cls.features = cls.resolver.run()

    def at(self, rva, n):
        return bytes(self.image.img[rva:rva + n])

    def target(self, call):
        self.assertIn(self.image.img[call], (0xE8, 0xE9))
        return call + 5 + struct.unpack_from("<i", self.image.img, call + 1)[0]

    def rip(self, ins, size, disp_at):
        return ins + size + struct.unpack_from("<i", self.image.img, ins + disp_at)[0]

    def test_exact_build(self):
        self.assertEqual(SHA, hashlib.sha256(self.exe.read_bytes()).hexdigest())

    def test_the_feature_resolves(self):
        self.assertTrue(self.features.get(FEATURE))
        self.assertTrue(self.features.get("markers.client_map"))
        self.assertEqual(MAP_NAME, self.syms["markers/MapName"]["rva"])
        self.assertEqual(PLAYER_TEXT, self.syms["markers/PlayerText"]["rva"])
        self.assertEqual(TOOLTIP_TEXTS, self.syms["markers/TooltipTexts"]["rva"])
        self.assertEqual((0x50, 0x58, 0x60), tuple(self.consts[f"markers/entry.{n}"]["value"] for n in ("label", "label_len", "label_hash")))

    def test_the_text_view_is_the_name_before_anything_else(self):
        # null entry: an empty name; then [entry+0x58] != 0 -> PlayerText(&tmp, entry + 0x50), taken when its status is 0
        self.assertEqual(bytes.fromhex("48 83 7a 58 00 74 55 48 83 c2 50 48 8d 4d e0"), self.at(MAP_NAME + 0x44, 15))
        self.assertEqual(PLAYER_TEXT, self.target(MAP_NAME + 0x53))
        self.assertEqual(bytes.fromhex("80 39 00 48 8d 41 08"), self.at(0xD3D3C4, 7))  # status byte 0: the result's view
        # second: the custom-pin text view (+0x68 view, +0x78 hash), as it is
        self.assertEqual(bytes.fromhex("48 83 7a 70 00 74 16 0f 10 42 68 f2 0f 10 4a 78"), self.at(0xD3D3F0, 16))
        # last: the type; a custom-pin type ([type+0x19]) is the loca text at +0xaea0 ("Custom marker"); then the
        # entry's LocaTagComponent name (+0x38), empty when 0
        self.assertEqual(TYPE_LOOKUP, self.target(0xD3D4DE))
        self.assertEqual(bytes.fromhex("80 78 19 00 74 3b"), self.at(0xD3D4E8, 6))
        self.assertEqual(bytes.fromhex("48 81 c1 a0 ae 00 00"), self.at(0xD3D4FC, 7))
        self.assertEqual(bytes.fromhex("83 7f 38 00 0f 84"), self.at(0xD3D529, 6))

    def test_the_player_text_path_filters_and_escapes_a_copy(self):
        self.assertEqual(TEXT_FILTER, self.target(0xD28B47))
        # the format's argument: the escape set at rip+..., one character '<', escaped with '\'
        self.assertEqual(b"<\0", self.at(self.rip(0xD28B85, 7, 3), 2))
        self.assertEqual(bytes.fromhex("c6 44 24 60 5c"), self.at(0xD28BC9, 5))
        self.assertEqual(ESCAPE_FORMAT, self.target(self.rip(0xD28B91, 7, 3)))  # the argument's formatter (a thunk)
        self.assertEqual(b"%k\0", self.at(self.rip(0xD28BF7, 7, 3), 3))
        self.assertEqual(UI_FORMAT, self.target(0xD28BFE))
        # the formatter writes the escape byte before a byte equal to it or in the set, then the byte
        self.assertEqual(bytes.fromhex("48 8b 07 0f b6 1c 06 40 3a dd 74 23"), self.at(ESCAPE_FORMAT + 0x30, 12))
        self.assertEqual(bytes.fromhex("3a 1c 02 74 0a"), self.at(0x89AA30, 5))
        # the UI's string buffer: UiApp + 0x18 (0xa4be00), its array at + 0x6b10, the result hashed with sign-extended bytes
        self.assertEqual(bytes.fromhex("48 8b 41 18 c3"), self.at(0xA4BE00, 5))
        self.assertEqual(bytes.fromhex("4c 8b b2 18 6b 00 00"), self.at(UI_FORMAT + 0x1E, 7))

    def test_the_tooltip_starts_from_the_name_and_skips_an_empty_one(self):
        self.assertEqual(MAP_NAME, self.target(0xD1DFB4))
        self.assertEqual(TOOLTIP_TEXTS, self.target(0xC2F4F7))
        self.assertEqual(bytes.fromhex("48 39 5d 88 0f 84"), self.at(0xC2F4FC, 6))  # title length 0: no tooltip
        self.assertEqual(0xC2FABC, 0xC2F502 + 4 + struct.unpack_from("<i", self.image.img, 0xC2F502)[0])
        # the title is drawn as the game's native text line (ui/NativeTextLine 0xb1da10) from the view at rbp-0x80
        self.assertEqual(bytes.fromhex("0f 10 45 80"), self.at(0xC2F71D, 4))
        self.assertEqual(0xB1DA10, self.target(0xC2F74C))
        self.assertEqual(b"CrosshairWindow", self.at(self.rip(0xC2EF07, 7, 3), 15))
        self.assertEqual(CROSSHAIR, self.target(0xC37B6B))

    def test_two_types_name_themselves(self):
        # Ancient Spire (0xa17072be fast travel, 0x98b2d65d locked): the type's own name is the title, the entry's name
        # goes to the line under it; Water Source (0x4293eae5): the type's name, the entry's name is not shown
        self.assertEqual(bytes.fromhex("8b 50 10 81 fa be 72 70 a1 74 08 81 fa 5d d6 b2 98 75 48"), self.at(0xD1E007, 19))
        self.assertEqual(bytes.fromhex("0f 10 06 8b d0 48 8d 4c 24 20 0f 11 03"), self.at(0xD1E034, 13))
        self.assertEqual(bytes.fromhex("81 fa e5 ea 93 42 75 24"), self.at(0xD1E062, 8))

    def test_each_frame_hovers_an_entry_of_the_current_list(self):
        # the canvas rebuilds the marker layer when a list count changed, then clears the hovered entry and hit-tests
        # the layer's items again before the crosshair window reads it
        self.assertEqual(MARKER_LAYER, self.target(0xC362DB))
        self.assertEqual(bytes.fromhex("48 89 73 28 48 89 73 20"), self.at(0xC36566, 8))
        self.assertEqual(bytes.fromhex("33 f6"), self.at(0xC36269, 2))  # rsi = 0 (callee-saved up to the reset)
        self.assertEqual(HIT_TEST, self.target(0xC3656E))
        self.assertLess(0xC3656E, 0xC37B6B)

    def test_the_games_writers_never_hand_a_game_row_kilns_text(self):
        # the append clears the text view, its hash and the custom-pin view of every entry it adds
        self.assertEqual(bytes.fromhex("48 89 50 50 48 89 50 58 48 89 48 60 48 89 50 68 48 89 50 70 48 89 48 78"),
                         self.at(APPEND + 0x55, 24))
        # replicate fills the view only from the entity's CustomPlayerString (no string: the write is skipped)
        self.assertEqual(bytes.fromhex("4c 8b 00 4d 85 c0 74 77"), self.at(0x3167D7, 8))
        self.assertEqual(0x316856, 0x3167DD + 2 + 0x77)
        self.assertEqual(CSTRING_LOOKUP, self.target(0x3167EA))
        self.assertEqual(bytes.fromhex("0f 11 4e 50 f2 0f 11 46 60"), self.at(0x31684D, 9))
        # its hash: wrapping 64-bit FNV-1a over each byte sign-extended (movsx)
        self.assertEqual(bytes.fromhex("4a 0f be 0c 0a 48 ff c2 48 33 c1 49 0f af c6"), self.at(0x316830, 15))
        # custom pins carry their own text in the other view: the registry node's inline text (+0x20), length byte +0xa0
        self.assertEqual(bytes.fromhex("4c 8d 4b 20"), self.at(0x308947, 4))
        self.assertEqual(bytes.fromhex("45 0f b6 91 80 00 00 00"), self.at(0x308953, 8))
        self.assertEqual(bytes.fromhex("41 0f 11 40 68"), self.at(0x308994, 5))


if __name__ == "__main__":
    unittest.main()
