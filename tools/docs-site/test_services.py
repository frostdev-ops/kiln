#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""services.py (the table of contents by assembly, the overview and the service pages) and gen-services.py (services.json).

  python3 -I tools/docs-site/test_services.py
"""
import importlib.util
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest import mock

HERE = Path(__file__).resolve().parent
REPO = HERE.parent.parent


def load(name, filename):
    spec = importlib.util.spec_from_file_location(name, HERE / filename)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


services = load("services", "services.py")
gen = load("gen_services", "gen-services.py")


def entry(uid, cid, kind, assembly, name=None, namespace="Kiln", summary=False):
    out = [f"- uid: {uid}", f"  commentId: {cid}", f"  name: {name or uid.rsplit('.', 1)[-1]}", f"  type: {kind}",
           "  assemblies:", f"  - {assembly}", f"  namespace: {namespace}"]
    return "\n".join(out) + "\n"


class Pages(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        root = Path(self.tmp.name)
        self.api, self.stage = root / "api", root / "stage"
        self.api.mkdir()
        self.stage.mkdir()
        (self.api / "Kiln.yml").write_text("items:\n" + entry("Kiln", "N:Kiln", "Namespace", "Kiln.Api"))
        types = [("Kiln.Zebra", "Class", "Kiln.Api", None, "Kiln"), ("Kiln.Apple`1", "Struct", "Kiln.Api", "Apple<T>", "Kiln"),
                 ("Kiln.Build", "Class", "Kiln.Build", None, "Kiln"), ("Kiln.Abilities.Natives", "Class", "Kiln.Abilities", "Abilities.Natives", "Kiln"),
                 ("Kiln.Ecs.Position", "Struct", "Kiln.Game", "Ecs.Position", "Kiln"), ("Kiln.Ecs", "Class", "Kiln.Game", None, "Kiln"),
                 ("Kiln.TestKit.Harness", "Class", "Kiln.TestKit", None, "Kiln.TestKit"), ("Kiln.TestKit.Xunit.Fact", "Class", "Kiln.TestKit", None, "Kiln.TestKit.Xunit"),
                 ("Kiln.Mystery", "Enum", "Kiln.NotListed", None, "Kiln")]
        for uid, kind, asm, name, ns in types:
            (self.api / (uid.replace("`", "-") + ".yml")).write_text("items:\n" + entry(uid, "T:" + uid, kind, asm, name, ns))
        (self.api / "Kiln.TestKit.yml").write_text("items:\n" + entry("Kiln.TestKit", "N:Kiln.TestKit", "Namespace", "Kiln.TestKit"))
        (self.stage / "Kiln.Build.xml").write_text("<doc><members><member name='T:Kiln.Build'><summary>Builds <see cref='T:Kiln.Foo'/> cells | fast. Second.</summary></member></members></doc>")
        self.json = root / "services.json"
        self.json.write_text(json.dumps({"format": 1, "services": [
            {"assembly": "Kiln.Build", "name": "Build", "summary": "Voxels and props.", "guides": ["docs/kiln/services/build.md", "docs/kiln/modding/blocks.md"],
             "capabilities": [{"name": "kiln.build", "members": 40}], "samples": ["OreVein"]},
            {"assembly": "Kiln.Api", "name": None, "summary": "The API mods compile against (Kiln.Ecs.*, not Kiln.GameTypes.*).", "guides": [], "capabilities": [], "samples": []},
            {"assembly": "Kiln.TestKit", "name": None, "summary": "Tests.", "guides": [], "capabilities": [], "samples": []}]}))
        self.wiki = root / "wiki.json"
        self.wiki.write_text(json.dumps({"format": 1, "base": "https://frostdev.io/kiln/wiki/",
                                         "assemblies": {"Kiln.Build": [["world-edits", "World edits"], ["a|b", "Pipes | here"]]}}))
        self.by = services.build(self.api, self.stage, self.json, self.wiki)

    def toc(self):
        return (self.api / "toc.yml").read_text()

    def group(self, asm):
        return json.loads((self.api / "toc" / f"{asm}.json").read_text())

    def test_the_table_of_contents_has_a_group_for_each_assembly(self):
        names = [l[len("- name: "):].strip('"') for l in self.toc().splitlines() if l.startswith("- name: ")]
        # the overview, the core in its own order, the services A-Z, the namespaces
        self.assertEqual(names, ["Overview", "Kiln.Api", "Kiln.Game", "Kiln.TestKit", "Kiln.Abilities", "Kiln.Build", "Kiln.NotListed", "Namespaces"])
        self.assertIn("- name: \"Kiln.Build\"\n  href: services/Kiln.Build.md\n- name: \"Kiln.NotListed\"", self.toc())

    def test_the_table_of_contents_holds_no_types(self):
        # 4,000 of them were drawn on every page: they are in api/toc/<assembly>.json, loaded when a group is opened
        groups = self.toc().split("- name: Namespaces")[0]
        self.assertNotIn("uid:", groups)
        self.assertNotIn("items:", groups)

    def test_types_are_sorted_by_name_and_keep_their_docfx_names(self):
        api = self.group("Kiln.Api")
        self.assertEqual(api, {"v": 1, "assembly": "Kiln.Api", "types": [["Apple<T>", "Kiln.Apple-1.html", "s"], ["Zebra", "Kiln.Zebra.html", "c"]]})
        self.assertEqual(self.group("Kiln.Abilities")["types"], [["Abilities.Natives", "Kiln.Abilities.Natives.html", "c"]])
        self.assertEqual([t[2] for t in self.group("Kiln.NotListed")["types"]], ["e"])

    def test_a_type_of_a_sub_namespace_says_where_it_is(self):
        self.assertEqual([t[0] for t in self.group("Kiln.TestKit")["types"]], ["Harness", "Xunit.Fact"])

    def test_the_group_index_has_pages_counts_services_and_summaries(self):
        index = json.loads((self.api / "toc/index.json").read_text())
        self.assertEqual([g["name"] for g in index["groups"]], ["Kiln.Api", "Kiln.Game", "Kiln.TestKit", "Kiln.Abilities", "Kiln.Build", "Kiln.NotListed"])
        build = index["groups"][4]
        self.assertEqual(build, {"name": "Kiln.Build", "href": "services/Kiln.Build.html", "service": "Build", "summary": "Voxels and props", "types": 1})
        self.assertEqual(index["groups"][0]["service"], None)
        self.assertEqual(index["groups"][1]["types"], 2)

    def test_the_namespaces_follow_the_services(self):
        toc = self.toc()
        tail = toc[toc.index("- name: Namespaces"):]
        self.assertIn('- uid: "Kiln"', tail)
        self.assertIn('- uid: "Kiln.TestKit"', tail)

    def test_every_type_is_in_exactly_one_group(self):
        pages = [t[1] for f in (self.api / "toc").glob("Kiln.*.json") for t in json.loads(f.read_text())["types"]]
        self.assertEqual(sorted(pages), sorted(set(pages)))
        self.assertEqual(len(pages), 9)
        self.assertEqual(len(list((self.api / "toc").glob("*.json"))), 7)      # six groups and the index

    def test_a_service_page_has_facts_capabilities_samples_and_types(self):
        page = (self.api / "services/Kiln.Build.md").read_text()
        self.assertIn("# Kiln.Build\n\nVoxels and props.", page)
        self.assertIn("- **Assembly:** `Kiln.Build.dll`", page)
        self.assertIn("- **KilnServices name:** `Build`", page)
        self.assertIn("[build.md](https://github.com/frostdev-ops/kiln/blob/main/docs/kiln/services/build.md)", page)
        self.assertIn("[blocks.md](../../modding/blocks.md)", page)               # a modding guide is on the site itself
        self.assertIn("`<KilnServices>Build</KilnServices>`", page)
        self.assertIn("| `kiln.build` | 40 |", page)
        self.assertIn("- [OreVein](https://github.com/frostdev-ops/kiln/tree/main/mods/samples/OreVein)", page)
        self.assertIn("- [Build](../Kiln.Build.yml) (class): Builds Foo cells | fast.", page)
        self.assertIn("- [Apple&lt;T&gt;](../Kiln.Apple-1.yml) (struct)", (self.api / "services/Kiln.Api.md").read_text())

    def test_a_service_page_names_the_types_to_start_with(self):
        doc = json.loads(self.json.read_text())
        doc["services"][0]["start"] = ["Gone", "Build"]
        self.json.write_text(json.dumps(doc))
        services.build(self.api, self.stage, self.json, self.wiki)
        page = (self.api / "services/Kiln.Build.md").read_text()
        self.assertIn("- **Start with:** [`Build`](../Kiln.Build.yml)\n", page)       # a name no type has is left out
        self.assertNotIn("Gone", page)
        self.assertNotIn("**Start with:**", (self.api / "services/Kiln.Api.md").read_text())

    def test_a_service_page_links_its_wiki_pages_and_opens_its_group(self):
        page = (self.api / "services/Kiln.Build.md").read_text()
        self.assertIn('---\ntitle: "Kiln.Build"\nkilnAsm: "Kiln.Build"\n---\n', page)
        self.assertIn("- **Wiki:** [World edits](https://frostdev.io/kiln/wiki/world-edits), [Pipes \\| here](https://frostdev.io/kiln/wiki/a|b)", page)
        self.assertNotIn("**Wiki:**", (self.api / "services/Kiln.Api.md").read_text())

    def test_the_core_has_no_service_name_and_the_test_kit_is_a_package(self):
        self.assertIn("none: every mod has it", (self.api / "services/Kiln.Api.md").read_text())
        self.assertIn("`Kiln.TestKit` (NuGet)", (self.api / "services/Kiln.TestKit.md").read_text())

    def test_an_assembly_services_json_does_not_know_still_has_a_page(self):
        page = (self.api / "services/Kiln.NotListed.md").read_text()
        self.assertIn("# Kiln.NotListed", page)
        self.assertIn("- [Mystery](../Kiln.Mystery.yml) (enum)", page)
        self.assertNotIn("## Samples", page)

    def test_the_game_types_generated_into_ecs_stay_off_the_page(self):
        page = (self.api / "services/Kiln.Game.md").read_text()
        self.assertNotIn("Position", page.split("## Types")[1].split("The 1 game types")[0])
        self.assertIn("The 1 game types generated into `Ecs`", page)

    def test_the_overview_lists_every_assembly(self):
        index = (self.api / "index.md").read_text()
        for asm in ("Kiln.Api", "Kiln.Build", "Kiln.Game", "Kiln.NotListed"):
            self.assertIn(f"[{asm}](services/{asm}.md)", index)
        self.assertIn("| [Kiln.Build](services/Kiln.Build.md) | `Build` | Voxels and props | [build.md](https://", index)
        self.assertIn("[blocks.md](../modding/blocks.md)", index)
        self.assertIn("| The API mods compile against (Kiln.Ecs.\\*, not Kiln.GameTypes.\\*) |", index)    # no emphasis between the two

    def test_a_list_not_a_table_holds_the_types(self):
        # DocFX's Markdown parser fails on a table of a few hundred rows ("too deeply nested")
        self.assertNotIn("| Type |", (self.api / "services/Kiln.Api.md").read_text())

    def test_the_command_line(self):
        r = subprocess.run([sys.executable, "-I", str(HERE / "services.py"), "--services", str(self.json), str(self.api), str(self.stage)],
                           capture_output=True, text=True)
        self.assertEqual(r.returncode, 0, r.stderr)
        self.assertIn("services: 6 assemblies, 9 types", r.stdout)


class Generator(unittest.TestCase):
    def make(self):
        root = Path(self.tmp.name)
        files = {
            "managed/Kiln.Api/Kiln.Api.csproj": "<Project><!-- The API mods compile against. More. --></Project>",
            "managed/services/Kiln.Build/Kiln.Build.csproj": "<Project><!-- Managed face of native/services/build (kiln.build): voxels and props. Deployed. --></Project>",
            "managed/services/Kiln.Build/Build.cs": 'public static class Build { const string Cap = "kiln.build"; [RequiresCapability(Cap)] void A() {}\n'
                                                     '[RequiresCapability("build.find_wall")] void B() {} [RequiresCapability(Cap)] void C() {} }',
            "managed/services/Kiln.Chat/Kiln.Chat.csproj": "<Project><!-- kiln.chat: the managed face of native/services/chat (commands, tell). Deployed. --></Project>",
            "managed/services/Kiln.Chat/obj/Skip.cs": '[RequiresCapability("never")]',
            "docs/kiln/services/build.md": "# Build\n",
            "docs/kiln/modding/chat.md": "# Chat\n",
            "mods/samples/OreVein/OreVein.csproj": '<Project><PropertyGroup><KilnServices>Build;Chat</KilnServices></PropertyGroup></Project>',
            "mods/samples/Plain/Plain.csproj": '<Project><ItemGroup><ProjectReference Include="../../../managed/services/Kiln.Chat/Kiln.Chat.csproj" /></ItemGroup></Project>',
        }
        for rel, text in files.items():
            (root / rel).parent.mkdir(parents=True, exist_ok=True)
            (root / rel).write_text(text)
        subprocess.run(["git", "init", "-q", str(root)], check=True)
        subprocess.run(["git", "-C", str(root), "add", "-A", "-f"], check=True)
        return root

    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        mock.patch.dict(os.environ, {"KILN_DOCS_TRACKED": ""}).start()
        self.addCleanup(mock.patch.stopall)
        self.root = self.make()

    def test_it_finds_services_guides_capabilities_and_samples(self):
        doc = gen.generate(self.root)
        by = {s["assembly"]: s for s in doc["services"]}
        self.assertEqual([s["assembly"] for s in doc["services"]], ["Kiln.Api", "Kiln.Build", "Kiln.Chat"])
        build = by["Kiln.Build"]
        self.assertEqual(build["name"], "Build")
        self.assertEqual(build["summary"], "Voxels and props")
        self.assertEqual(build["guides"], ["docs/kiln/services/build.md"])
        self.assertEqual(by["Kiln.Chat"]["guides"], ["docs/kiln/modding/chat.md"])
        self.assertEqual(build["capabilities"], [{"name": "build.find_wall", "members": 1}, {"name": "kiln.build", "members": 2}])
        self.assertEqual(build["samples"], ["OreVein"])
        self.assertEqual(by["Kiln.Chat"]["samples"], ["OreVein", "Plain"])      # a <ProjectReference> counts like <KilnServices>
        self.assertEqual(by["Kiln.Chat"]["summary"], "Commands, tell")
        self.assertEqual(by["Kiln.Chat"]["capabilities"], [])                  # obj/ is no source
        self.assertEqual(by["Kiln.Api"]["name"], None)

    def test_a_reworded_summary_is_kept(self):
        before = gen.generate(self.root)
        before["services"][1]["summary"] = "Reworded by hand."
        self.assertEqual(gen.generate(self.root, before)["services"][1]["summary"], "Reworded by hand.")

    def test_a_start_list_is_kept_and_left_out_when_there_is_none(self):
        before = gen.generate(self.root)
        self.assertNotIn("start", before["services"][1])
        before["services"][1]["start"] = ["Build"]
        self.assertEqual(gen.generate(self.root, before)["services"][1]["start"], ["Build"])

    def test_check_says_when_the_file_is_out_of_date(self):
        target = self.root / "tools/docs-site/services.json"
        target.parent.mkdir(parents=True)
        args = [sys.executable, "-I", str(HERE / "gen-services.py"), str(self.root)]
        r = subprocess.run(args + ["--check"], capture_output=True, text=True)
        self.assertNotEqual(r.returncode, 0)
        subprocess.run(args, check=True, capture_output=True)
        subprocess.run(["git", "-C", str(self.root), "add", "-A", "-f"], check=True)
        r = subprocess.run(args + ["--check"], capture_output=True, text=True)
        self.assertEqual(r.returncode, 0, r.stderr)
        (self.root / "mods/samples/Late").mkdir()
        (self.root / "mods/samples/Late/Late.csproj").write_text("<Project><PropertyGroup><KilnServices>Build</KilnServices></PropertyGroup></Project>")
        subprocess.run(["git", "-C", str(self.root), "add", "-A", "-f"], check=True)
        r = subprocess.run(args + ["--check"], capture_output=True, text=True)
        self.assertNotEqual(r.returncode, 0)
        self.assertIn("out of date", r.stderr)


@unittest.skipUnless((REPO / ".git").exists() or os.environ.get("KILN_DOCS_TRACKED"), "needs a git checkout of the repository")
class Checked(unittest.TestCase):
    def test_the_checked_in_services_json_is_what_the_repository_gives(self):
        r = subprocess.run([sys.executable, "-I", str(HERE / "gen-services.py"), "--check", str(REPO)], capture_output=True, text=True)
        self.assertEqual(r.returncode, 0, r.stderr)

    def test_the_wiki_pages_name_known_assemblies(self):
        wiki = json.loads((HERE / "wiki.json").read_text())
        names = {s["assembly"] for s in json.loads((HERE / "services.json").read_text())["services"]}
        self.assertEqual(wiki["base"], "https://frostdev.io/kiln/wiki/")
        for asm, pages in wiki["assemblies"].items():
            self.assertIn(asm, names)
            for slug, title in pages:
                self.assertRegex(slug, r"^[a-z0-9]+(-[a-z0-9]+)*$")
                self.assertTrue(title.strip(), slug)

    def test_every_assembly_of_the_reference_has_an_entry_with_a_summary(self):
        doc = json.loads((HERE / "services.json").read_text())
        names = {s["assembly"] for s in doc["services"]}
        for folder in (REPO / "managed/services").iterdir():
            if (folder / f"{folder.name}.csproj").exists():
                self.assertIn(folder.name, names)
        for s in doc["services"]:
            if s["assembly"] not in ("Kiln.Tools",):
                self.assertTrue(s["summary"], s["assembly"])


if __name__ == "__main__":
    unittest.main()
