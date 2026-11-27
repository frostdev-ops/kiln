#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""Tests for tools/docs-site/api_index.py, the agent API index: the lines it reads (PublicAPI.Shipped/Unshipped, XML docs, the
since ledger, native headers and the ABI manifest), what it leaves out, since marks, extension members, enums, properties,
record members, Markdown safety and plain ASCII, the splits (Kiln.Blocks by family, the rest by name, native by header),
determinism, and --check (current, stale, missing and extra files, a broken input).

  python3 -I tools/docs-site/test_api_index.py
Builds small repositories under a temporary folder; nothing else is written. KILN_API_XML=<xml dir> also checks that the committed
index of this repository is current (the same check tools/check.sh runs).
"""
import contextlib
import importlib.util
import io
import json
import os
from pathlib import Path
import re
import tempfile
import unittest

HERE = Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location("api_index", HERE / "api_index.py")
ai = importlib.util.module_from_spec(spec)
spec.loader.exec_module(ai)

SERVICES = {
    "Kiln.Api": ("(always)", "The base", ["api-contract.md"], ["modding/contract.md"]),
    "Kiln.Items": ("Items", "Items in inventories", ["api-items.md", "api-gear.md"], ["services/items.md"]),
    "Kiln.Lab": (ai.OWN, "A lab", [], []),
}

API_SHIPPED = """#nullable enable
Kiln.Mods
const Kiln.Mods.ApiVersion = "0.2.15" -> string!
Kiln.EntityId
Kiln.EntityId.EntityId(uint Value) -> void
Kiln.EntityId.Value.get -> uint
Kiln.EntityId.Value.init -> void
static readonly Kiln.EntityId.None -> Kiln.EntityId
"""
ITEMS_SHIPPED = """#nullable enable
Kiln.GiveResult
Kiln.GiveResult.<Clone>$() -> Kiln.GiveResult!
Kiln.GiveResult.Deconstruct(out bool Ok, out string? Reason) -> void
Kiln.GiveResult.Equals(Kiln.GiveResult? other) -> bool
Kiln.GiveResult.GiveResult(bool Ok, string? Reason) -> void
Kiln.GiveResult.Ok.get -> bool
Kiln.GiveResult.Ok.init -> void
Kiln.GiveResult.Reason.get -> string?
Kiln.GiveResult.Reason.init -> void
override Kiln.GiveResult.Equals(object? obj) -> bool
override Kiln.GiveResult.GetHashCode() -> int
static Kiln.GiveResult.operator !=(Kiln.GiveResult? left, Kiln.GiveResult? right) -> bool
static Kiln.GiveResult.operator ==(Kiln.GiveResult? left, Kiln.GiveResult? right) -> bool
Kiln.ItemRarity
Kiln.ItemRarity.Common = 0 -> Kiln.ItemRarity
Kiln.ItemRarity.Epic = 3 -> Kiln.ItemRarity
Kiln.ItemRarity.Rare = 2 -> Kiln.ItemRarity
Kiln.ItemRarity.Uncommon = 1 -> Kiln.ItemRarity
Kiln.Items
Kiln.Items.Items() -> void
static Kiln.Items.Available.get -> bool
static Kiln.Items.Give(Kiln.EntityId player, Kiln.ItemId item, int count = 1) -> Kiln.GiveResult!
static Kiln.Items.SetRarity(Kiln.EntityId player, Kiln.ItemRarity rarity) -> bool
static Kiln.Items.Take<T>(System.Collections.Generic.IReadOnlyList<T>! things, System.Threading.Tasks.Task<int>? task = null) -> System.Threading.Tasks.Task<bool>!
[KILNX001]static Kiln.Items.LabReset() -> void
~[KILNX002]static Kiln.Items.Unproven() -> void
Kiln.OldName (forwarded, contained in Kiln.Api)
Kiln.Box<T>
Kiln.Box<T>.Box(T value) -> void
Kiln.Box<T>.Value.get -> T
Kiln.Box<T>.this[int index].get -> T
Kiln.Box<T>.this[int index].set -> void
Kiln.Is
Kiln.RulesApi
Kiln.RulesApi.extension(Kiln.Is!)
static Kiln.RulesApi.Chance(double chance) -> bool
static Kiln.RulesApi.extension(Kiln.Is!).Chance(double chance) -> bool
static Kiln.RulesApi.extension(Kiln.Is!).InCombat.get -> bool
static Kiln.RulesApi.get_InCombat() -> bool
"""
ITEMS_UNSHIPPED = """#nullable enable
*REMOVED*static Kiln.Items.SetRarity(Kiln.EntityId player, Kiln.ItemRarity rarity) -> bool
static Kiln.Items.SetRarity(Kiln.EntityId player, Kiln.ItemRarity rarity, bool force = false) -> bool
static Kiln.Items.UpgradeRarity(Kiln.EntityId player) -> Kiln.ItemRarity
Kiln.UpgradeInfo
Kiln.UpgradeInfo.Cost.get -> int
"""
LAB_SHIPPED = """#nullable enable
[KILNX001]Kiln.LabThing
"""
HEADER = """/* SPDX-License-Identifier: MIT
 * kiln.items v1: items. */
#define KILN_ITEMS_SERVICE "kiln.items"
typedef struct KilnItemsApi {
    KilnServiceHeader header; /* { sizeof(KilnItemsApi), KILN_ITEMS_VERSION } */

    /* ---- reads ---- */
    KilnResult(KILN_CALL* count)(uint32_t* out); /* trailing: how many */
    /* Gives items to a player.
     * Second line. */
    KilnResult(KILN_CALL* give)(KilnEntity player,
                                uint32_t count);
    // after a line comment
    KilnResult(KILN_CALL* take)(KilnEntity player);

    KilnResult(KILN_CALL* nocomment)(void);
    /* a < b */
    KilnResult(KILN_CALL* upgrade)(KilnEntity player);
} KilnItemsApi;
"""
LEDGER = {
    ("Kiln.Api", "Kiln.EntityId"): ("<=0.2.0-dev.26", "<=0.2.1"),
    ("Kiln.Api", "Kiln.EntityId.EntityId(uint Value) -> void"): ("<=0.2.0-dev.26", "<=0.2.1"),
    ("Kiln.Api", "Kiln.EntityId.Value.get -> uint"): ("<=0.2.0-dev.26", "<=0.2.1"),
    ("Kiln.Api", "Kiln.EntityId.Value.init -> void"): ("<=0.2.0-dev.26", "<=0.2.1"),
    ("Kiln.Api", "static readonly Kiln.EntityId.None -> Kiln.EntityId"): ("<=0.2.0-dev.26", "<=0.2.1"),
    ("Kiln.Api", "Kiln.Mods"): ("<=0.2.0-dev.26", "<=0.2.1"),
}


def member(did, summary=None, params=None, inherit=None):
    inner = ""
    if summary is not None:
        inner += "<summary>%s</summary>" % summary
    for k, v in (params or {}).items():
        inner += '<param name="%s">%s</param>' % (k, v)
    if inherit:
        inner += '<inheritdoc cref="%s"/>' % inherit
    return '<member name="%s">%s</member>' % (did, inner)


def xml_doc(name, *members):
    return '<?xml version="1.0"?><doc><assembly><name>%s</name></assembly><members>%s</members></doc>' % (name, "".join(members))


ITEMS_XML = xml_doc(
    "Kiln.Items",
    member("T:Kiln.Items", "Items in inventories. Give and take; <c>Items.Available</c> says whether it runs."),
    member("M:Kiln.Items.Give(Kiln.EntityId,Kiln.ItemId,System.Int32)",
           'Gives <paramref name="count"/> of an item (<see cref="T:Kiln.ItemId"/>, <see cref="M:Kiln.Items.Take``1(System.Collections.Generic.IReadOnlyList{``0},System.Threading.Tasks.Task{System.Int32})"/>, '
           'see <see langword="null"/>); 0 &lt; x. e.g. a stack—or more. Second sentence.'),
    member("P:Kiln.Items.Available", "True when the native service is loaded: <c>kiln.items</c>."),
    member("M:Kiln.Items.SetRarity(Kiln.EntityId,Kiln.ItemRarity)", "Old overload."),
    member("M:Kiln.Items.SetRarity(Kiln.EntityId,Kiln.ItemRarity,System.Boolean)", "Sets the rarity of the item a player holds."),
    member("M:Kiln.Items.UpgradeRarity(Kiln.EntityId)", "Moves the item one rarity up. Costs a station."),
    member("T:Kiln.GiveResult", "What a give did."),
    member("M:Kiln.GiveResult.#ctor(System.Boolean,System.String)", "What a give did.", {"Ok": "True when it went in.", "Reason": "Why not."}),
    member("T:Kiln.ItemRarity", "How rare an item is."),
    member("T:Kiln.Box`1", "A box of <typeparamref name=\"T\"/>."),
    member("P:Kiln.Box`1.Item(System.Int32)", "The value at <c>index</c>."),
    member("T:Kiln.UpgradeInfo", "What an upgrade costs."),
    member("P:Kiln.UpgradeInfo.Cost", "The cost."),
    member("T:Kiln.RulesApi", "What <see cref=\"T:Kiln.RulesApi\"/> adds to <see cref=\"T:Kiln.Is\"/>."),
    member("M:Kiln.RulesApi.get_InCombat", inherit="P:Kiln.RulesApi.&lt;G&gt;$ABC.InCombat"),
    member("M:Kiln.RulesApi.Chance(System.Double)", inherit="M:Kiln.RulesApi.&lt;G&gt;$ABC.Chance(System.Double)"),
    member("P:Kiln.RulesApi.&lt;G&gt;$ABC.InCombat", "It is in combat."),
    member("M:Kiln.RulesApi.&lt;G&gt;$ABC.Chance(System.Double)", "A roll that comes up this share of the times."),
)
API_XML = xml_doc("Kiln.Api", member("T:Kiln.EntityId", "An entity."), member("F:Kiln.EntityId.None", "No entity."),
                  member("T:Kiln.Mods", "Other mods."))


class Repo:
    """A small repository under a temporary folder, with its XML docs."""

    def __init__(self, tmp, ledger=None, items_unshipped=ITEMS_UNSHIPPED, items_shipped=ITEMS_SHIPPED, services=None):
        self.root = Path(tmp) / "repo"
        self.xml = Path(tmp) / "xml"
        self.services = services or SERVICES
        self.write("managed/Kiln.Api/Mods.cs", 'public const string ApiVersion = "0.2.15";\n')
        self.write("managed/Kiln.Api/Kiln.Api.csproj", "<Project><PropertyGroup><GenerateDocumentationFile>true</GenerateDocumentationFile></PropertyGroup></Project>\n")
        self.write("managed/Kiln.Api/PublicAPI.Shipped.txt", API_SHIPPED)
        self.write("managed/Kiln.Api/PublicAPI.Unshipped.txt", "#nullable enable\n")
        self.write("managed/services/Kiln.Items/Kiln.Items.csproj", "<Project><PropertyGroup><GenerateDocumentationFile>true</GenerateDocumentationFile></PropertyGroup></Project>\n")
        self.write("managed/services/Kiln.Items/PublicAPI.Shipped.txt", items_shipped)
        self.write("managed/services/Kiln.Items/PublicAPI.Unshipped.txt", items_unshipped)
        self.write("managed/services/Kiln.Lab/Kiln.Lab.csproj", "<Project/>\n")
        self.write("managed/services/Kiln.Lab/PublicAPI.Shipped.txt", LAB_SHIPPED)
        self.write("managed/services/Kiln.Lab/PublicAPI.Unshipped.txt", "#nullable enable\n")
        self.write("native/core/abi/kiln_api.h", "typedef struct KilnApi {\n    KilnResult(KILN_CALL* version)(void); /* the version */\n} KilnApi;\n")
        self.write("native/core/abi/services/kiln_items.h", HEADER)
        self.write("crates/sfi/kiln-abi/abi-manifest.json", json.dumps({"headers": {
            "native/core/abi/services/kiln_items.h": {"layouts": {"KilnItemsApi": {"size": 56, "fields": {"count": 8, "give": 16, "upgrade": 48}}}}}}))
        self.write_ledger(ledger)
        self.write_xml("Kiln.Api", API_XML)
        self.write_xml("Kiln.Items", ITEMS_XML)

    def write(self, rel, text):
        p = self.root / rel
        p.parent.mkdir(parents=True, exist_ok=True)
        p.write_text(text, encoding="utf-8")

    def write_xml(self, asm, text):
        self.xml.mkdir(parents=True, exist_ok=True)
        (self.xml / f"{asm}.xml").write_text(text, encoding="utf-8")

    def write_ledger(self, ledger=None):
        """A row for every shipped line (Kiln.Items: first shipped in 0.2.0-dev.30, mod API 0.2.12), the experimental, forwarded and
        extension marker lines have none, as in the real ledger."""
        rows = dict(LEDGER)
        for l in ai.read_lines(self.root / "managed/services/Kiln.Items/PublicAPI.Shipped.txt"):
            if ai.parse_api_line(l).kind not in ("marker", "forward") and not ai.parse_api_line(l).exp:
                rows[("Kiln.Items", l)] = ("0.2.0-dev.30", "0.2.12")
        rows.update(ledger or {})
        out = ["kind\tproject\tentry\trelease\tmod_api"]
        for (proj, entry), (rel, api) in sorted(rows.items()):
            out.append("\t".join(["managed", proj, entry, rel, api]))
        out.append("\t".join(["native", "services/kiln_items.h", "services/kiln_items.h:KilnItemsApi:count", "<=0.2.0-dev.10", "<=0.2.0"]))
        out.append("\t".join(["native", "services/kiln_items.h", "services/kiln_items.h:KilnItemsApi:give", "0.2.0-dev.28", "0.2.3"]))
        self.write("managed/api-history.tsv", "\n".join(out) + "\n")

    def generate(self, **kw):
        return ai.generate(self.root, self.xml, services=self.services, **kw)


class Base(unittest.TestCase):
    def setUp(self):
        tmp = tempfile.TemporaryDirectory()
        self.addCleanup(tmp.cleanup)
        self.tmp = tmp.name
        self.repo = Repo(self.tmp)
        self.files = self.repo.generate()
        self.items = self.files["Items.md"]

    def lines(self, name):
        return self.files[name].split("\n")

    def block(self, heading, name="Items.md"):
        """The lines of the type whose heading starts with `## <heading>`."""
        out, on = [], False
        for l in self.files[name].split("\n"):
            if l.startswith("## "):
                on = l.startswith("## " + heading)
            if on:
                out.append(l)
        return out


class Files(Base):
    def test_one_file_per_assembly_named_by_its_entry(self):
        self.assertEqual(sorted(self.files), ["Api.md", "INDEX.md", "Items.md", "native.md"])      # Lab: lab members only, no file

    def test_index_table(self):
        idx = self.files["INDEX.md"]
        self.assertIn("# Kiln mod API index (mod API 0.2.15)", idx)
        self.assertIn("| Kiln.Items | `Items` | Items in inventories | [api-items.md](../api-items.md), [api-gear.md](../api-gear.md) | "
                      "`services/items.md` |", idx)
        self.assertIn("| Kiln.Api | (always) | The base |", idx)
        self.assertIn("| Kiln.Lab | (Kiln's own) | A lab | - | - | 0 | 0 | no public API (lab members only) |", idx)
        self.assertRegex(idx, r"\| Native ABI \| C plugins \|.*\| \[native.md\]\(native.md\) \(\d+ KB\) \|")

    def test_experimental_members_are_left_out_and_counted(self):
        self.assertNotIn("LabReset", self.items)
        self.assertNotIn("Unproven", self.items)
        self.assertNotIn("LabThing", "".join(self.files.values()).replace("KILNX001 Kiln.Lab 1", ""))
        self.assertIn("KILNX001 Kiln.Items 1", self.files["INDEX.md"])
        self.assertIn("KILNX002 Kiln.Items 1", self.files["INDEX.md"])
        # KILNX003 (WorldConcurrent) is normal API since 0.2.0-dev.30.7: the note no longer calls it experimental
        note = [l for l in self.files["INDEX.md"].splitlines() if l.startswith("Left out as experimental:")][0]
        self.assertNotIn("KILNX003", note)
        self.assertNotIn("WorldConcurrent", note)
        self.assertIn("KILNX001 is lab and test API. KILNX002 (Kiln.Client SteamGuard) is real but unproven", note)

    def test_forwarded_lines_are_no_members(self):
        self.assertNotIn("OldName", self.items)


class Types(Base):
    def test_type_heading_has_the_first_sentence_of_the_summary(self):
        self.assertEqual(self.block("Items")[0], "## Items `0.2.12` — Items in inventories.")

    def test_members_have_signature_since_and_summary(self):
        b = "\n".join(self.block("Items"))
        self.assertIn("- `static Give(EntityId player, ItemId item, int count = 1) -> GiveResult` — Gives `count` of an item (`ItemId`, "
                      "`Items.Take`, see `null`); 0 \\< x.", b)
        self.assertIn("- `static Available { get; } -> bool` — True when the native service is loaded: `kiln.items`.", b)

    def test_summary_is_cut_at_a_sentence_not_at_e_g(self):
        b = "\n".join(self.block("Items"))
        self.assertNotIn("Second sentence", b)
        self.assertIn("e.g. a stack - or more.", b)

    def test_unshipped_lines_carry_the_upcoming_mod_api_and_shipped_ones_the_ledgers(self):
        b = "\n".join(self.block("UpgradeInfo"))
        self.assertIn("## UpgradeInfo `0.2.15` — What an upgrade costs.", b)
        self.assertIn("- `Cost { get; } -> int` — The cost.", b)               # same as its type: no mark
        up = [l for l in self.block("Items") if "UpgradeRarity" in l][0]
        self.assertTrue(up.startswith("- `static UpgradeRarity(EntityId player) -> ItemRarity` `0.2.15` — Moves the item one rarity up."))
        give = [l for l in self.block("Items") if " Give(" in l][0]
        self.assertIn("`0.2.12`", self.files["Items.md"].split("## Items")[1].split("\n")[0] + "0.2.12")
        self.assertNotIn("0.2.12`", give)                                       # a member as old as its type: no mark

    def test_removed_lines_are_gone_the_new_overload_is_there(self):
        sr = [l for l in self.block("Items") if "SetRarity" in l]
        self.assertEqual(len(sr), 1)
        self.assertIn("SetRarity(EntityId player, ItemRarity rarity, bool force = false) -> bool` `0.2.15` — Sets the rarity", sr[0])

    def test_a_type_of_the_oldest_record_has_no_mark(self):
        a = self.files["Api.md"]
        self.assertIn("## EntityId — An entity.\n", a)
        self.assertIn("- `static readonly None -> EntityId` — No entity.", a)

    def test_enum_values_on_one_line_in_value_order(self):
        self.assertEqual(self.block("ItemRarity")[1], "enum: Common, Uncommon, Rare, Epic")

    def test_properties_are_one_line_and_constructors_first(self):
        b = self.block("GiveResult")
        self.assertEqual(b[0], "## GiveResult `0.2.12` — What a give did.")
        self.assertEqual(b[1], "- `GiveResult(bool Ok, string? Reason)`")
        self.assertIn("- `Ok { get; init; } -> bool` — True when it went in.", b)            # a positional record's <param>
        self.assertIn("- `Reason { get; init; } -> string?` — Why not.", b)

    def test_record_plumbing_and_object_overrides_are_left_out(self):
        b = "\n".join(self.block("GiveResult"))
        for gone in ("Clone", "Deconstruct", "Equals", "GetHashCode", "operator"):
            self.assertNotIn(gone, b)

    def test_generic_names_are_in_code_and_indexers_are_one_line(self):
        self.assertTrue(self.block("`Box<T>`"))
        b = "\n".join(self.block("`Box<T>`"))
        self.assertIn("- `this[int index] { get; set; } -> T` — The value at `index`.", b)
        self.assertIn("- `Box(T value)`", b)

    def test_generic_method_and_namespaces(self):
        b = "\n".join(self.block("Items"))
        self.assertIn("- `static Take<T>(IReadOnlyList<T> things, Task<int>? task = null) -> Task<bool>`", b)

    def test_a_parameterless_constructor_without_a_doc_is_left_out(self):
        self.assertNotIn("Items()", self.items)


class Extension(Base):
    def test_extension_members_are_written_on_their_receiver(self):
        b = self.block("RulesApi")
        self.assertEqual(b[1], "Extension members (C# 14): call them on the receiver (`Is.X`), not on `RulesApi`.")
        self.assertIn("- `Is.Chance(double chance) -> bool` — A roll that comes up this share of the times.", b)
        self.assertIn("- `Is.InCombat { get; } -> bool` — It is in combat.", b)

    def test_the_implementation_methods_are_not_listed_again(self):
        b = "\n".join(self.block("RulesApi"))
        self.assertNotIn("get_InCombat", b)
        self.assertEqual(b.count("Chance"), 1)


class Safety(Base):
    def test_markdown_safe_and_ascii(self):
        for name, text in self.files.items():
            for n, l in enumerate(text.split("\n"), 1):
                plain = re.sub(r"`[^`]*`", "", l).replace("—", "-")
                self.assertNotRegex(plain, r"(?<!\\)<", f"{name}:{n} has a raw <: {l}")
                self.assertEqual(l.count("`") % 2, 0, f"{name}:{n} has an odd number of backticks: {l}")
                self.assertNotRegex(plain, r"[^\x20-\x7e]", f"{name}:{n}: {l}")

    def test_files_end_with_one_newline_and_have_no_carriage_return(self):
        for name, text in self.files.items():
            self.assertTrue(text.endswith("\n") and not text.endswith("\n\n"), name)
            self.assertNotIn("\r", text)

    def test_text_helpers(self):
        self.assertEqual(ai.ascii_text("a…b − é →"), "a...b - e ->")
        self.assertEqual(ai.esc("x <T> `y`"), "x \\<T> 'y'")
        self.assertEqual(ai.short("Kiln.Foo<System.Int32>! Bar(System.Collections.Generic.List<Kiln.X!>! a) != b"),
                         "Foo<Int32> Bar(List<X> a) != b")

    def test_first_sentence(self):
        f = ai.first_sentence
        self.assertEqual(f("Reads a cell. Then more.", 100), "Reads a cell.")
        self.assertEqual(f("Use e.g. this one. And that.", 100), "Use e.g. this one.")
        self.assertEqual(f("Since 0.2.0-dev.30.6. Next", 100), "Since 0.2.0-dev.30.6.")
        self.assertEqual(f("A value (`attr.Name ?? x`). More.", 100), "A value (`attr.Name ?? x`).")
        self.assertEqual(f("No stop at the end", 100), "No stop at the end")
        cut = f("word " * 40 + "`code part", 60)
        self.assertTrue(cut.endswith("...") and len(cut) <= 60 and cut.count("`") % 2 == 0, cut)


class RegistrySummary(unittest.TestCase):
    def test_kiln_registry_names_every_shipped_registry(self):
        """INDEX.md's purpose of Kiln.Registry names every registry schema Kiln ships (it listed the first five only)."""
        repo = HERE.parents[1]
        summary = ai.SERVICES["Kiln.Registry"][1]
        for f in sorted((repo / "schema" / "registry").glob("*.json")):
            if f.name == "ids.lock.json":
                continue
            name = json.loads(f.read_text(encoding="utf-8"))["registry"]
            self.assertIn(name, re.findall(r"[A-Za-z]+", summary), f"{name} is not in Kiln.Registry's purpose: {summary}")


class Determinism(unittest.TestCase):
    def test_same_bytes_whatever_the_order_of_the_record(self):
        with tempfile.TemporaryDirectory() as tmp:
            a = Repo(Path(tmp) / "a")
            b = Repo(Path(tmp) / "b", items_shipped="#nullable enable\n" + "\n".join(
                reversed([l for l in ITEMS_SHIPPED.split("\n") if l and l != "#nullable enable"])) + "\n")
            self.assertEqual(a.generate(), b.generate())
            self.assertEqual(a.generate(), a.generate())


class Splits(unittest.TestCase):
    def setUp(self):
        self.limit, self.whole = ai.LIMIT, ai.WHOLE_MAX
        self.addCleanup(lambda: (setattr(ai, "LIMIT", self.limit), setattr(ai, "WHOLE_MAX", self.whole)))

    def test_a_large_assembly_is_split_by_type_name_with_a_map(self):
        ai.LIMIT, ai.WHOLE_MAX = 700, 900
        with tempfile.TemporaryDirectory() as tmp:
            files = Repo(tmp).generate()
        self.assertIn("Items-1.md", files)
        self.assertIn("Items-2.md", files)
        m = files["Items.md"]
        self.assertTrue(m.startswith("# Items (Kiln.Items): map"))
        self.assertRegex(m, r"- `Items-1.md` \(\d+ KB\): Box<T>|- `Items-1.md` \(\d+ KB\): `Box<T>`")
        types = re.findall(r"^## (.*?)( `|$| —)", "".join(files[n] for n in files if n.startswith("Items-")), re.M)
        self.assertEqual(len(types), len(set(t[0] for t in types)))              # each type once
        self.assertRegex(files["Items-1.md"], r"^# Items \(Kiln.Items\): part 1 of \d+\n")
        self.assertRegex(files["INDEX.md"], r"\[Items.md\]\(Items.md\) \(map of \d+ parts\)")

    def test_kiln_blocks_is_split_by_the_folder_of_its_source(self):
        ai.LIMIT = 200
        services = dict(SERVICES)
        services["Kiln.Blocks"] = ("Blocks", "Blocks", [], [])
        with tempfile.TemporaryDirectory() as tmp:
            r = Repo(tmp, services=services)
            r.write("managed/services/Kiln.Blocks/Kiln.Blocks.csproj", "<Project/>\n")
            r.write("managed/services/Kiln.Blocks/PublicAPI.Unshipped.txt", "#nullable enable\n")
            r.write("managed/services/Kiln.Blocks/PublicAPI.Shipped.txt", "#nullable enable\n" + "".join(
                f"Kiln.{n}\nKiln.{n}.Name.get -> string!\nKiln.{n}.Name.init -> void\n" for n in ("ItemBlock", "NpcBlock", "CoreThing", "KindThing", "SpellBlock")))
            r.write("managed/services/Kiln.Blocks/Items/Items.cs", "public sealed record ItemBlock { }\npublic sealed class SpellBlock { }\n")
            r.write("managed/services/Kiln.Blocks/Npc/Npc.cs", "[Foo]\npublic sealed class NpcBlock { }\n")
            r.write("managed/services/Kiln.Blocks/Kinds/Kinds.cs", "public enum KindThing { }\n")
            r.write("managed/services/Kiln.Blocks/Core.cs", "public static partial class CoreThing { }\n")
            r.write_ledger({("Kiln.Blocks", l): ("<=0.2.0-dev.26", "<=0.2.1") for l in ai.read_lines(r.root / "managed/services/Kiln.Blocks/PublicAPI.Shipped.txt")})
            files = r.generate()
        self.assertEqual(sorted(n for n in files if n.startswith("Blocks")), ["Blocks-core.md", "Blocks-items.md", "Blocks-npc.md", "Blocks.md"])
        self.assertIn("## ItemBlock", files["Blocks-items.md"])
        self.assertIn("## SpellBlock", files["Blocks-items.md"])
        self.assertIn("## NpcBlock", files["Blocks-npc.md"])
        self.assertIn("## KindThing", files["Blocks-core.md"])                   # Kinds is core
        self.assertIn("## CoreThing", files["Blocks-core.md"])
        self.assertIn("by family", files["Blocks.md"])

    def test_balanced_parts(self):
        parts = ai.split_balanced(list(range(10)), lambda x: 10, 40)
        self.assertEqual(sorted(len(p) for p in parts), [3, 3, 4])               # 100 bytes in parts of at most about 40
        self.assertEqual(sum(parts, []), list(range(10)))
        self.assertEqual(len(ai.split_balanced([1], lambda x: 1000, 40)), 1)


class Parse(unittest.TestCase):
    """A PublicAPI line and its doc id name the same entry: the lookup of a summary."""

    def same(self, line, did):
        self.assertEqual(ai.entry_key(ai.parse_api_line(line)), ai.parse_doc_id(did), line)

    def test_lines_and_their_doc_ids(self):
        self.same("Kiln.Foo", "T:Kiln.Foo")
        self.same("Kiln.Foo<T>", "T:Kiln.Foo`1")
        self.same("Kiln.Foo<T>.Bar<U>(T a, System.Collections.Generic.List<U>! b) -> U", "M:Kiln.Foo`1.Bar``1(`0,System.Collections.Generic.List{``0})")
        self.same("static Kiln.Foo.Make(int? a, string![]! b, int[,] c, ref int d, out Kiln.Bar e, in System.Guid f = default) -> void",
                  "M:Kiln.Foo.Make(System.Nullable{System.Int32},System.String[],System.Int32[0:,0:],System.Int32@,Kiln.Bar@,System.Guid@)")
        self.same("Kiln.Foo.Foo(int a) -> void", "M:Kiln.Foo.#ctor(System.Int32)")
        self.same("Kiln.Foo.Count.get -> int", "P:Kiln.Foo.Count")
        self.same("Kiln.Foo.Count.set -> void", "P:Kiln.Foo.Count")
        self.same("Kiln.Foo.this[int i, string s].get -> bool", "P:Kiln.Foo.Item(System.Int32,System.String)")
        self.same("Kiln.Foo.Changed -> System.Action<int>?", "E:Kiln.Foo.Changed")
        self.same("const Kiln.Foo.Max = 3 -> int", "F:Kiln.Foo.Max")
        self.same("Kiln.Level.High = 2 -> Kiln.Level", "F:Kiln.Level.High")
        self.same("static Kiln.Foo.operator ==(Kiln.Foo a, Kiln.Foo b) -> bool", "M:Kiln.Foo.op_Equality(Kiln.Foo,Kiln.Foo)")
        self.same("static Kiln.Foo.operator <=(Kiln.Foo a, Kiln.Foo b) -> bool", "M:Kiln.Foo.op_LessThanOrEqual(Kiln.Foo,Kiln.Foo)")
        self.same("static Kiln.Foo.implicit operator Kiln.Bar!(Kiln.Foo x) -> Kiln.Bar!", "M:Kiln.Foo.op_Implicit(Kiln.Foo)~Kiln.Bar")
        self.same("static Kiln.Foo.Pair(( int a, string b) t) -> void", "M:Kiln.Foo.Pair(System.ValueTuple{System.Int32,System.String})")

    def test_kinds_and_flags(self):
        p = ai.parse_api_line
        self.assertEqual([p(x).kind for x in ("Kiln.Foo", "Kiln.Foo.Foo() -> void", "Kiln.Foo.Bar() -> void", "Kiln.Foo.P.get -> int",
                                             "Kiln.Foo.F -> int", "static Kiln.Foo.operator +(Kiln.Foo a) -> Kiln.Foo",
                                             "Kiln.A.extension(Kiln.B!)", "Kiln.Old (forwarded, contained in Kiln.Api)")],
                         ["type", "ctor", "method", "prop", "field", "op", "marker", "forward"])
        e = p("~[KILNX003]static Kiln.Foo.Bar() -> void")
        self.assertEqual((e.exp, e.mods, e.name), ("KILNX003", ["static"], "Bar"))
        e = p("static Kiln.A.extension(Kiln.B!).Go(int x) -> void")
        self.assertEqual((e.type, e.name, e.ext, e.params), ("Kiln.A", "Go", "Kiln.B!", ["Kiln.B", "System.Int32"]))
        self.assertEqual(p("Kiln.Foo<T>.Bar.get -> int").type, "Kiln.Foo`1")

    def test_xml_rendering(self):
        import xml.etree.ElementTree as ET
        n = ET.fromstring('<summary>See <see cref="M:Kiln.Build.PlaceOnce(System.String)"/> and <see cref="T:Kiln.Foo`1"/>, '
                          '<see cref="P:Kiln.Foo.Bar"/> (<c>a &lt; b</c>), <paramref name="x"/>, <see langword="true"/>.\n   Next\tline &amp; \u2192 &lt;T&gt;.</summary>')
        self.assertEqual(ai.render(n), "See `Build.PlaceOnce` and `Foo`, `Foo.Bar` (`a < b`), `x`, `true`. Next line & -> \\<T>.")


class Native(Base):
    def test_rows_with_header_table_offset_since_and_comment(self):
        n = self.files["native.md"]
        self.assertIn("## services/kiln_items.h — service `kiln.items`", n)
        self.assertIn("### KilnItemsApi — 56 bytes", n)
        self.assertIn("- kiln_items.h `KilnItemsApi.count` @8 — trailing: how many", n)                    # no mark: <=0.2.0
        self.assertIn("- kiln_items.h `KilnItemsApi.give` @16 `0.2.3` — Gives items to a player.", n)     # the comment above, first sentence
        self.assertIn("- kiln_items.h `KilnItemsApi.take` `0.2.15` — after a line comment", n)             # no offset in the manifest
        self.assertIn("- kiln_items.h `KilnItemsApi.nocomment` `0.2.15`\n", n)                             # a row the ledger lacks: upcoming
        self.assertIn("- kiln_items.h `KilnItemsApi.upgrade` @48 `0.2.15` — a \\< b", n)
        self.assertIn("- kiln_api.h `KilnApi.version` `0.2.15` — the version", n)

    def test_a_dashed_section_label_is_no_comment(self):
        self.assertEqual(ai.table_rows("/* ---- reads ---- */\nKilnResult(KILN_CALL* a)(void);"), [("a", "")])
        self.assertEqual(ai.table_rows("/* ---- reads ---- Does a thing. */\nKilnResult(KILN_CALL* a)(void);"), [("a", "Does a thing.")])

    def test_a_blank_line_cuts_a_comment_from_its_row(self):
        self.assertEqual(ai.table_rows("/* a group */\n\nKilnResult(KILN_CALL* a)(void);"), [("a", "")])
        self.assertEqual(ai.table_rows("// one\n// two\nKilnResult(KILN_CALL* a)(\n    int x); /* late */"), [("a", "one two")])

    def test_native_is_split_by_header_when_large(self):
        old = ai.LIMIT, ai.WHOLE_MAX
        self.addCleanup(lambda: (setattr(ai, "LIMIT", old[0]), setattr(ai, "WHOLE_MAX", old[1])))
        ai.LIMIT, ai.WHOLE_MAX = 300, 400
        files = self.repo.generate()
        self.assertIn("native-1.md", files)
        self.assertTrue(files["native.md"].startswith("# Native ABI rows (mod API 0.2.15): map"))
        self.assertIn("kiln_api.h", files["native.md"])


class Inputs(unittest.TestCase):
    def test_a_missing_ledger_row_stops_the_generator(self):
        with tempfile.TemporaryDirectory() as tmp:
            r = Repo(tmp)
            text = (r.root / "managed/api-history.tsv").read_text()
            r.write("managed/api-history.tsv", "\n".join(l for l in text.split("\n") if "Kiln.Items.Available" not in l))
            with self.assertRaisesRegex(ai.Problem, "has no row for Kiln.Items: static Kiln.Items.Available"):
                r.generate()

    def test_an_assembly_without_a_services_row_and_a_row_without_assembly(self):
        with tempfile.TemporaryDirectory() as tmp:
            r = Repo(tmp)
            with self.assertRaisesRegex(ai.Problem, "Kiln.Lab has a PublicAPI record but no row in SERVICES"):
                ai.generate(r.root, r.xml, services={k: v for k, v in SERVICES.items() if k != "Kiln.Lab"})
            with self.assertRaisesRegex(ai.Problem, "SERVICES names Kiln.Ghost"):
                ai.generate(r.root, r.xml, services=dict(SERVICES, **{"Kiln.Ghost": ("Ghost", "x", [], [])}))

    def test_a_missing_xml_doc_file_is_named(self):
        with tempfile.TemporaryDirectory() as tmp:
            r = Repo(tmp)
            (r.xml / "Kiln.Items.xml").unlink()
            with self.assertRaisesRegex(ai.Problem, "no XML doc file of Kiln.Items"):
                r.generate()

    def test_xml_is_found_in_a_stage_and_in_an_artifacts_bin_folder(self):
        with tempfile.TemporaryDirectory() as tmp:
            r = Repo(tmp)
            flat = r.generate()
            bin_dir = Path(tmp) / "art"
            for asm in ("Kiln.Api", "Kiln.Items"):
                (bin_dir / asm / "release").mkdir(parents=True)
                (bin_dir / asm / "release" / f"{asm}.xml").write_text((r.xml / f"{asm}.xml").read_text(encoding="utf-8"), encoding="utf-8")
            self.assertEqual(ai.generate(r.root, bin_dir, services=SERVICES), flat)


class Check(unittest.TestCase):
    def setUp(self):
        tmp = tempfile.TemporaryDirectory()
        self.addCleanup(tmp.cleanup)
        self.repo = Repo(tmp.name)
        self.out = self.repo.root / ai.INDEX_DIR
        # main() reads the real SERVICES: the small repository names its own
        old = ai.SERVICES
        ai.SERVICES = SERVICES
        self.addCleanup(lambda: setattr(ai, "SERVICES", old))

    def run_main(self, *args):
        out, err = io.StringIO(), io.StringIO()
        with contextlib.redirect_stdout(out), contextlib.redirect_stderr(err):
            rc = ai.main(["--tree", str(self.repo.root), "--xml", str(self.repo.xml), *args])
        return rc, out.getvalue(), err.getvalue()

    def test_write_then_check_is_current(self):
        rc, out, _ = self.run_main()
        self.assertEqual(rc, 0, out)
        self.assertTrue((self.out / "INDEX.md").is_file())
        rc, out, _ = self.run_main("--check")
        self.assertEqual((rc, out.strip().split(", ")[-1]), (0, "current"))

    def test_check_without_a_committed_index_fails(self):
        rc, out, _ = self.run_main("--check")
        self.assertEqual(rc, 1)
        self.assertIn("missing: Items.md", out)

    def test_a_changed_doc_a_changed_record_a_stale_and_a_missing_file_are_drift(self):
        self.run_main()
        (self.repo.xml / "Kiln.Items.xml").write_text(ITEMS_XML.replace("Sets the rarity of the item", "Sets the grade of the item"), encoding="utf-8")
        rc, out, _ = self.run_main("--check")
        self.assertEqual(rc, 1)
        self.assertIn("differs: Items.md", out)
        self.assertIn("api_index.py --xml", out)                              # how to regenerate
        self.run_main()
        self.assertEqual(self.run_main("--check")[0], 0)
        self.repo.write("managed/services/Kiln.Items/PublicAPI.Unshipped.txt", ITEMS_UNSHIPPED + "static Kiln.Items.Drop() -> void\n")
        self.assertEqual(self.run_main("--check")[0], 1)
        self.run_main()
        (self.out / "extra.md").write_text("x\n")
        rc, out, _ = self.run_main("--check")
        self.assertEqual(rc, 1)
        self.assertIn("stale file: extra.md", out)
        self.run_main()                                                          # writing removes it
        self.assertFalse((self.out / "extra.md").exists())
        (self.out / "Api.md").unlink()
        rc, out, _ = self.run_main("--check")
        self.assertEqual(rc, 1)
        self.assertIn("missing: Api.md", out)

    def test_bad_input_is_exit_two(self):
        (self.repo.xml / "Kiln.Items.xml").unlink()
        rc, _, err = self.run_main("--check")
        self.assertEqual(rc, 2)
        self.assertIn("no XML doc file of Kiln.Items", err)

    def test_check_never_writes(self):
        self.run_main("--check")
        self.assertFalse(self.out.exists())


class ServicesTable(unittest.TestCase):
    """The table of assemblies (SERVICES) points at pages that exist, and covers the assemblies of this repository."""

    root = HERE.parent.parent

    def test_every_gated_assembly_has_a_row_and_the_pages_exist(self):
        gated = {a for a, _ in ai.Tree(self.root).projects()}
        self.assertEqual(gated, set(ai.SERVICES))
        for asm, (entry, purpose, guides, pages) in ai.SERVICES.items():
            self.assertTrue(entry and purpose, asm)
            self.assertNotIn("|", purpose + entry)                                  # it goes in a Markdown table
            for g in guides:
                self.assertTrue((self.root / "agent/skills/kiln-modding/references" / g).is_file(), f"{asm}: skill guide {g}")
            for p in pages:
                self.assertTrue((self.root / "docs/kiln" / p).is_file(), f"{asm}: docs/kiln/{p}")

    def test_every_publicapi_record_has_a_services_row(self):
        # the generator stops on a missing row (exit 2); this names the gap in the suite, before anyone generates
        recorded = {a for a, _ in ai.Tree(self.root).projects()}
        missing = sorted(recorded - set(ai.SERVICES))
        self.assertEqual(missing, [], "these assemblies have a PublicAPI record but no row in SERVICES "
                                      "(tools/docs-site/api_index.py): add each one's KilnServices entry, purpose, "
                                      "skill guides and docs/kiln pages: " + ", ".join(missing))
        stale = sorted(set(ai.SERVICES) - recorded)
        self.assertEqual(stale, [], "SERVICES names assemblies without a PublicAPI record: remove their rows: "
                                    + ", ".join(stale))

    def test_the_entry_of_a_service_is_its_assembly_name(self):
        for asm, (entry, *_rest) in ai.SERVICES.items():
            if not entry.startswith("("):
                self.assertEqual(entry, asm[len("Kiln."):])


@unittest.skipUnless(os.environ.get("KILN_API_XML"), "KILN_API_XML (a folder of compiled XML docs) is not set")
class ThisRepository(unittest.TestCase):
    def test_the_committed_index_is_current(self):
        out = io.StringIO()
        with contextlib.redirect_stdout(out):
            rc = ai.main(["--check", "--xml", os.environ["KILN_API_XML"]])
        self.assertEqual(rc, 0, out.getvalue())


if __name__ == "__main__":
    unittest.main()
