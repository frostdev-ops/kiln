#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""Keep startup reading every section read_blocks reads.

The Windows plugin cannot run in the host harness, so this checks its source: read_mod hands every
JSON object document to read_blocks, which leaves alone what is not one of its sections
(test_tool_pools.cpp checks that). A second list of section names in front of read_blocks drifted
once: documents holding only toolBrushShapes or toolMaterialPools were skipped (GitHub #129).
"""
from pathlib import Path
import re
import unittest


root = Path(__file__).resolve().parent.parent
merge = (root / "merge.cpp").read_text()
terrain = (root / "terrain.cpp").read_text()
startup = (root / "worldtables.cpp").read_text()
reader = startup.split("void read_mod(", 1)[1].split("\n// The per-world record:", 1)[0]
# What read_mod decides between knowing a document is JSON and reading it.
gate = reader.split('": not valid JSON");', 1)[1].split("read_blocks(doc, &mf);", 1)[0]
handled = set(re.findall(r'doc\.find\("([^"]+)"\)', merge + terrain))
# read_crafting_props also iterates the prop-kind sections through kSections.
for sections in re.findall(r'kSections\[\]\s*=\s*\{([^}]+)\}', merge):
    handled.update(re.findall(r'"([^"]+)"', sections))


class StartupSections(unittest.TestCase):
    def test_reader_sections_found(self):
        self.assertTrue({"toolBrushShapes", "toolMaterialPools", "blueprints", "craftingProps"} <= handled, handled)

    def test_startup_reads_every_object_document(self):
        self.assertIn("doc.is_object()", gate)
        self.assertNotRegex(gate, r"\bcontains\s*\(", "read_mod picks documents by section name again: read_blocks would miss a section the list lacks")
        for section in sorted(handled):
            with self.subTest(section=section):
                self.assertNotIn(f'"{section}"', gate)

    def test_replayed_pools_keep_their_section(self):
        # kiln#152: a pool kept for the next start is read back through pool_replay_document (test_pool_store.cpp runs it),
        # never wrapped in one fixed section here: a tool pool as buildingMaterialPools never registered.
        replay = reader.split("for(auto definition:g_pool_replayed)", 1)[1].split("for(const auto& frozen", 1)[0]
        self.assertIn("pool_replay_document(definition)", replay)
        self.assertNotIn("MaterialPools", replay)


if __name__ == "__main__":
    unittest.main()
