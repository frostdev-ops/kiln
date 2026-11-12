#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""The generated Kiln.Ecs types leave the undocumented check; the hand-written part of Kiln.Ecs stays in it.

  python3 -I tools/docs-site/test_undocumented.py
"""
import importlib.util
from pathlib import Path
import tempfile
import unittest

HERE = Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location("undocumented", HERE / "undocumented.py")
undocumented = importlib.util.module_from_spec(spec)
spec.loader.exec_module(undocumented)


def item(uid, kind, parent, summary=None):
    lines = [f"- uid: {uid}", f"  commentId: X:{uid}", f"  parent: {parent}", f"  type: {kind}"]
    if summary is not None:
        lines.append(f"  summary: {summary}")
    return "\n".join(lines)


def page(*items):
    return "### YamlMime:ManagedReference\nitems:\n" + "\n".join(items) + "\nreferences:\n- uid: System.Object\n"


class GeneratedEcs(unittest.TestCase):
    def setUp(self):
        tmp = tempfile.TemporaryDirectory()
        self.addCleanup(tmp.cleanup)
        self.api = Path(tmp.name)

    def write(self, name, text):
        (self.api / f"{name}.yml").write_text(text, encoding="utf-8")

    def test_generated_types_and_their_members_are_left_out(self):
        self.write("Kiln.Ecs.Fuel", page(
            item("Kiln.Ecs.Fuel", "Struct", "Kiln",
                 "'<code>keen::ecs::Fuel</code>: an ECS component, 16 bytes, align 8. Base <code>keen::ecs::Component</code>.'"),
            item("Kiln.Ecs.Fuel.raw", "Field", "Kiln.Ecs.Fuel")))
        self.write("Kiln.Ecs.Mode", page(
            item("Kiln.Ecs.Mode", "Enum", "Kiln", "'Game enum <code>keen::Mode</code>: 1 byte(s) over <code>keen::uint8</code>.'"),
            item("Kiln.Ecs.Mode.None", "Field", "Kiln.Ecs.Mode")))
        self.write("Kiln.Ecs.Flags", page(
            item("Kiln.Ecs.Flags", "Struct", "Kiln", "'Game bitmask <code>keen::Flags</code>: 4 byte(s).'"),
            item("Kiln.Ecs.Flags.Bits", "Field", "Kiln.Ecs.Flags")))
        self.write("Kiln.Ecs.StaticArray8-1", page(
            item("Kiln.Ecs.StaticArray8`1", "Struct", "Kiln",
                 "\"A game <code>StaticArray</code> of 8 elements: 8 × the element's size, no padding.\""),
            item("Kiln.Ecs.StaticArray8`1.Item", "Property", "Kiln.Ecs.StaticArray8`1")))
        self.write("Kiln.Ecs.Spawn", page(
            item("Kiln.Ecs.Spawn", "Struct", "Kiln", ">-\n    <code>keen::ecs::Spawn</code>: an ECS event, 24 bytes, align 8."),
            item("Kiln.Ecs.Spawn.Equals(System.Object)", "Method", "Kiln.Ecs.Spawn")))
        self.assertEqual(undocumented.undocumented(self.api), [])

    def test_hand_written_part_of_ecs_stays_checked(self):
        self.write("Kiln.Ecs", page(
            item("Kiln.Ecs", "Class", "Kiln", "The game's ECS types, generated from its reflection table."),
            item("Kiln.Ecs.TryGet", "Method", "Kiln.Ecs")))
        self.write("Kiln.Ecs.GameGuid", page(
            item("Kiln.Ecs.GameGuid", "Struct", "Kiln", "'A game guid (reflection kind 29, <code>keen::Guid</code>): 16 bytes.'"),
            item("Kiln.Ecs.GameGuid.Equals(System.Object)", "Method", "Kiln.Ecs.GameGuid")))
        # a generated-looking type with no summary, or outside Kiln.Ecs, is not taken for a generated one
        self.write("Kiln.Ecs.Bare", page(
            item("Kiln.Ecs.Bare", "Struct", "Kiln"),
            item("Kiln.Ecs.Bare.raw", "Field", "Kiln.Ecs.Bare")))
        self.write("Kiln.Lookalike", page(
            item("Kiln.Lookalike", "Struct", "Kiln", "'Game enum <code>keen::Mode</code>: 1 byte(s).'"),
            item("Kiln.Lookalike.None", "Field", "Kiln.Lookalike")))
        self.assertEqual(undocumented.undocumented(self.api), [
            "Kiln.Ecs.Bare", "Kiln.Ecs.Bare.raw", "Kiln.Ecs.GameGuid.Equals(System.Object)", "Kiln.Ecs.TryGet",
            "Kiln.Lookalike.None"])


class InheritDoc(unittest.TestCase):
    def test_inheritdoc_counts_as_documented_only_with_the_xml(self):
        with tempfile.TemporaryDirectory() as tmp:
            api, xml = Path(tmp, "api"), Path(tmp, "xml")
            api.mkdir()
            xml.mkdir()
            (api / "Kiln.Guid.yml").write_text(page(
                item("Kiln.Guid", "Struct", "Kiln", "A guid."),
                item("Kiln.Guid.GetHashCode", "Method", "Kiln.Guid"),
                item("Kiln.Guid.ToString", "Method", "Kiln.Guid")), encoding="utf-8")
            (xml / "Kiln.Api.xml").write_text(
                '<doc><assembly><name>Kiln.Api</name></assembly><members>'
                '<member name="X:Kiln.Guid.GetHashCode"><inheritdoc/></member></members></doc>', encoding="utf-8")
            self.assertEqual(undocumented.undocumented(api), ["Kiln.Guid.GetHashCode", "Kiln.Guid.ToString"])
            self.assertEqual(undocumented.undocumented(api, xml), ["Kiln.Guid.ToString"])


if __name__ == "__main__":
    unittest.main()
