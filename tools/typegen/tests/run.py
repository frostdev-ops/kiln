#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""Tests of tools/typegen/typegen.py with no game data (made-up fixture dumps only).

    python3 tools/typegen/tests/run.py [--work DIR] [--bless]

--work: where to write (default: $KILN_OUT/typegen-tests when KILN_OUT is set, else a new temporary folder).
--bless: rewrite the expected outputs (managed/tests/Kiln.Game.Tests/Fixtures/gen/expected) from the fixture dump.
The Kiln.Game tests run this file too (GeneratorTests).
"""
import copy
import json
import os
import shutil
import subprocess
import sys
import tempfile

sys.dont_write_bytecode = True
HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.normpath(os.path.join(HERE, "..", "..", ".."))
TYPEGEN = os.path.join(ROOT, "tools", "typegen", "typegen.py")
FIX = os.path.join(ROOT, "managed", "tests", "Kiln.Game.Tests", "Fixtures", "gen")
DUMP = os.path.join(FIX, "lanterns.json")
EXPECTED = os.path.join(FIX, "expected")
OLD_DUMP = os.path.join(ROOT, "sigdb", "types", "server",
                        "c5c9fe50c6398d2adedd98923c2d04bf9d9b4af64c1447ac167cdf8f874ff84f.json")
GENERATED_CS = os.path.join(ROOT, "managed", "Kiln.Game", "Generated.cs")
OUTPUTS = ("Kiln.Game.Ecs.g.cs", "Kiln.Game.Arrays.g.cs", "Kiln.Game.Legacy.g.cs", "Kiln.Game.layouts",
           "Kiln.Game.types")

sys.path.insert(0, os.path.dirname(TYPEGEN))
import typegen  # noqa: E402

WORK = None
failures = []


def run(*args):
    p = subprocess.run([sys.executable, TYPEGEN] + list(args), capture_output=True, text=True)
    return p.returncode, p.stdout, p.stderr


def work(name):
    p = os.path.join(WORK, name)
    if os.path.isdir(p):
        shutil.rmtree(p)
    return p


def load_fixture():
    with open(DUMP, encoding="utf-8") as f:
        return json.load(f)


def write_dump(name, d):
    p = os.path.join(WORK, name + ".json")
    with open(p, "w", encoding="utf-8", newline="\n") as f:
        json.dump(d, f, indent=1)
    return p


def find(d, qn):
    return next(t for t in d["types"] if t["qualifiedName"] == qn)


def field(t, name):
    return next(f for f in t["fields"] if f["name"] == name)


def add_type(d, t):
    t.setdefault("hash", f"0x{typegen.fnv1a32(t['qualifiedName']):08X}")
    d["types"].append(t)
    d["types"].sort(key=lambda e: e["qualifiedName"])


def case(fn):
    try:
        fn()
        print(f"ok    {fn.__name__}")
    except AssertionError as e:
        failures.append(fn.__name__)
        print(f"FAIL  {fn.__name__}: {e}")
    return fn


def read(p):
    with open(p, "rb") as f:
        return f.read()


# --- the fixture against its expected output ------------------------------------------------------------------------

def test_fixture_matches_expected_output():
    out = work("fixture")
    rc, so, se = run("generate", "--dump", DUMP, "--out", out, "--no-legacy")
    assert rc == 0, se
    for n in OUTPUTS:
        assert read(os.path.join(out, n)) == read(os.path.join(EXPECTED, n)), \
            f"{n} differs from Fixtures/gen/expected/{n} (diff them; run.py --bless if the change is meant)"
    assert so == read(os.path.join(EXPECTED, "summary.txt")).decode(), f"summary line: {so!r}"


def test_every_generated_file_starts_with_the_marker():
    out = os.path.join(WORK, "fixture")
    for n in OUTPUTS:
        with open(os.path.join(out, n), encoding="utf-8") as f:
            assert f.readline().rstrip("\n") == typegen.MARKER, n


def test_the_output_is_the_same_twice():
    a, b = work("twice-a"), work("twice-b")
    assert run("generate", "--dump", DUMP, "--out", a, "--no-legacy")[0] == 0
    assert run("generate", "--dump", DUMP, "--out", b, "--no-legacy")[0] == 0
    for n in OUTPUTS:
        assert read(os.path.join(a, n)) == read(os.path.join(b, n)), n


def test_the_root_option_names_the_class_and_every_reference():
    out = work("root")
    rc, so, se = run("generate", "--dump", DUMP, "--out", out, "--no-legacy", "--root", "Lamps")
    assert rc == 0, se
    with open(os.path.join(out, "Kiln.Game.Ecs.g.cs"), encoding="utf-8") as f:
        text = f.read()
    assert "public static partial class Lamps" in text and "Ecs." not in text and "class Ecs" not in text
    with open(os.path.join(out, "Kiln.Game.layouts"), encoding="utf-8") as f:
        table = f.read()
    assert "\tKiln.Lamps+Lantern\t" in table and "root=Lamps" in table and "Ecs" not in table


def test_the_fixture_has_every_kind_and_rule():
    d = load_fixture()
    kinds = {t["kind"] for t in d["types"]}
    for k in list(range(1, 20)) + [22, 24, 25, 28, 29]:
        if k in (15,):
            continue  # a 32-bit mask is the same rule as 8, 16 and 64
        assert k in kinds, f"kind {k} missing from the fixture"
    with open(os.path.join(EXPECTED, "Kiln.Game.Ecs.g.cs"), encoding="utf-8") as f:
        text = f.read()
    for needle in ["public uint @params;", "Glimmer_Trail", "Glimmer_KindMask", "@event = 1 << 2", "Left = -1",
                   "public static implicit operator uint(LampId value)", "Ecs.StaticArray3<Ecs.Spark> sparks",
                   "[FieldOffset(0)] public Ecs.HashKey32 key;", "public fixed byte notes[8];",
                   "public Ecs.ObjectReference model;", "public Ecs.GameGuid guid;", "public float glow;",
                   "IGameComponent<Lantern>", "IGameEvent<LanternLit>", "IGameStruct<Spark>", "Hooded = 1 << 15"]:
        assert needle in text, needle


def test_a_big_unsigned_and_a_negative_enum_value_get_the_right_literal():
    d = load_fixture()
    add_type(d, {"name": "Wide", "qualifiedName": "keen::Wide", "kind": 12, "size": 8, "align": 8,
                 "inner": "keen::uint64", "values": [{"name": "Top", "value": (1 << 64) - 1}, {"name": "Zero", "value": 0}]})
    add_type(d, {"name": "Deep", "qualifiedName": "keen::Deep", "kind": 12, "size": 8, "align": 8,
                 "inner": "keen::sint64", "values": [{"name": "Bottom", "value": -(1 << 63)}]})
    find(d, "keen::ecs::Gauges")["fields"].append({"name": "wide", "offset": 48, "type": "keen::Wide"})
    find(d, "keen::ecs::Gauges")["fields"].remove(field(find(d, "keen::ecs::Gauges"), "modes"))
    out = work("bigenum")
    rc, so, se = run("generate", "--dump", write_dump("bigenum", d), "--out", out, "--no-legacy")
    assert rc == 0, se
    with open(os.path.join(out, "Kiln.Game.Ecs.g.cs"), encoding="utf-8") as f:
        text = f.read()
    assert "public enum Wide : ulong" in text and "Top = 18446744073709551615," in text
    assert "public enum Deep : long" in text and "Bottom = -9223372036854775808," in text


# --- refusals: each stops before anything is written ----------------------------------------------------------------

def refused(name, mutate, *needles, args=()):
    d = load_fixture()
    mutate(d)
    out = work("bad-" + name)
    rc, so, se = run("generate", "--dump", write_dump("bad-" + name, d), "--out", out, "--no-legacy", *args)
    assert rc == 1, f"exit {rc}, stderr {se!r}"
    assert se.startswith("typegen: error: "), se
    for n in needles:
        assert n in se, f"{n!r} not in {se!r}"
    assert not os.path.exists(out), "wrote output although it refused"


def test_refuses_a_key_outside_the_schema():
    refused("top-key", lambda d: d.update(defaults={}), "defaults: key outside schema 2")
    refused("type-key", lambda d: find(d, "keen::ecs::Lantern").update(rva=4096),
            "(keen::ecs::Lantern).rva: key outside schema 2")
    refused("field-key", lambda d: field(find(d, "keen::ecs::Lantern"), "glow").update(defaultValue=0),
            "fields[0].defaultValue: key outside schema 2")
    refused("value-key", lambda d: find(d, "keen::LanternMode")["values"][0].update(flags=1),
            "values[0].flags: key outside schema 2")


def test_refuses_an_unresolved_type_name():
    refused("field-type", lambda d: field(find(d, "keen::ecs::Lantern"), "glow").update(type="keen::Candle"),
            "'keen::Candle' is not a type in this dump")
    refused("inner", lambda d: find(d, "keen::Lumen").update(inner="keen::float16"),
            "'keen::float16' is not a type in this dump")
    refused("component", lambda d: d["components"].append("keen::ecs::Torch"),
            "'keen::ecs::Torch' is not a type in this dump")


def test_refuses_a_kind_it_does_not_know():
    refused("kind", lambda d: find(d, "keen::DsHandle").update(kind=30), "30 is a kind this generator does not know")


def test_refuses_a_name_collision_naming_both_types():
    def twin(d):
        add_type(d, {"name": "Spark", "qualifiedName": "keen::Spark", "kind": 18, "size": 4, "align": 4, "fields": []})
    refused("collision", twin, "name collision", "keen::Spark", "keen::ecs::Spark", "Ecs.Spark")

    def prefixed(d):
        add_type(d, {"name": "Glimmer_Trail", "qualifiedName": "keen::Glimmer_Trail", "kind": 18, "size": 4,
                     "align": 4, "fields": []})
    refused("collision-ns", prefixed, "keen::Glimmer_Trail", "keen::glimmer::Trail", "Ecs.Glimmer_Trail")

    def handwritten(d):
        add_type(d, {"name": "GameGuid", "qualifiedName": "keen::GameGuid", "kind": 18, "size": 4, "align": 4,
                     "fields": []})
    refused("collision-root", handwritten, "keen::GameGuid", "Ecs.GameGuid")


def test_refuses_a_field_with_a_reserved_member_name():
    refused("reserved", lambda d: field(find(d, "keen::ecs::Lantern"), "glow").update(name="Size"),
            "keen::ecs::Lantern.Size", "members every generated struct has")


def test_refuses_a_layout_that_does_not_add_up():
    refused("overlap", lambda d: field(find(d, "keen::ecs::Lantern"), "wick").update(offset=2),
            "keen::ecs::Lantern: fields glow and wick overlap")
    refused("past-end", lambda d: field(find(d, "keen::ecs::Lantern"), "labels").update(offset=128),
            "ends past the struct's 136 bytes")
    refused("in-base", lambda d: field(find(d, "keen::ecs::Warmth"), "dataStorage").update(offset=4),
            "keen::ecs::Warmth.dataStorage at 4 lies inside its base keen::ecs::FloatMeter")
    refused("repeat", lambda d: field(find(d, "keen::ecs::Warmth"), "dataStorage").update(name="key"),
            "field 'key' is declared by keen::ecs::AttributeComponent and by keen::ecs::Warmth")
    refused("array", lambda d: find(d, "keen::StaticArray<keen::sint16,4>").update(size=10),
            "is not 4 elements of 2 bytes")


def test_refuses_values_that_do_not_fit():
    refused("bit", lambda d: find(d, "keen::LanternTag")["values"].append({"name": "Huge", "value": 16}),
            "keen::LanternTag.Huge = 16 is not a bit of a 16-bit mask")
    refused("enum", lambda d: find(d, "keen::LanternMode")["values"].append({"name": "Huge", "value": 256}),
            "keen::LanternMode.Huge = 256 does not fit byte")
    refused("signed", lambda d: find(d, "keen::Tilt")["values"].append({"name": "Far", "value": 1 << 15}),
            "keen::Tilt.Far = 32768 does not fit short")


def test_refuses_a_wrong_hash_and_the_wrong_schema():
    refused("hash", lambda d: find(d, "keen::ecs::Lantern").update(hash="0x00000000"),
            "is not FNV-1a-32 of the qualified name")
    refused("schema", lambda d: d.update(schema=1), "this generator reads schema 2")


def test_refuses_a_type_name_that_is_not_its_qualified_names_tail():
    refused("name-literal", lambda d: find(d, "keen::ecs::Lantern").update(name='Lantern\\"; x'),
            "(keen::ecs::Lantern).name", "is not the last part of the qualified name ('Lantern')")
    refused("name-other", lambda d: find(d, "keen::ecs::Lantern").update(name="Lamp"),
            "'Lamp' is not the last part of the qualified name ('Lantern')")
    refused("name-template", lambda d: find(d, "keen::StaticArray<keen::sint16,4>").update(name="StaticArray"),
            "('StaticArray<keen::sint16,4>')")


def test_refuses_a_component_kiln_world_cannot_find_by_name():
    refused("component-ns", lambda d: d["components"].append("keen::glimmer::Trail"),
            "component keen::glimmer::Trail", "keen::ecs::Trail", "typed access by name cannot reach it")


def test_the_table_lists_the_generated_methods_of_a_struct():
    with open(os.path.join(EXPECTED, "Kiln.Game.layouts"), encoding="utf-8") as f:
        lines = {l.split("\t")[1]: l.split("\t") for l in f.read().split("\n") if l[1:2] == "\t"}
    methods = {n: [m for m in p[6:] if "@method:" in m] for n, p in lines.items()}
    # wrappers: the conversions to and from the wrapped type; the attribute component: its value view
    assert methods["Kiln.Ecs+LampId"] == ["op_Implicit@method:uint"]
    assert methods["Kiln.Ecs+HashKey32"] == ["op_Implicit@method:uint"]
    assert methods["Kiln.Ecs+Moment"] == ["op_Implicit@method:long"]
    assert methods["Kiln.Ecs+Warmth"] == ["get_Values@method:Span<float>"]
    # nothing else: not a wrapper (several fields, an empty struct, a single field smaller than its struct), no view
    for n in ("Kiln.Ecs+Lantern", "Kiln.Ecs+Spark", "Kiln.Ecs+Component", "Kiln.Ecs+Unlit", "Kiln.Ecs+FloatMeter"):
        assert methods[n] == [], (n, methods[n])
    assert sum(len(v) for v in methods.values()) == 4
    # the method members come after the fields, and their "offset" is never a number
    for p in lines.values():
        ms = p[6:]
        k = [i for i, m in enumerate(ms) if "@method:" in m]
        assert k == list(range(len(ms) - len(k), len(ms))), p[1]


def test_refuses_a_field_named_like_a_table_method():
    refused("method-name", lambda d: field(find(d, "keen::ecs::Lantern"), "glow").update(name="op_Implicit"),
            "keen::ecs::Lantern.op_Implicit", "a generated method's name")


def test_an_attribute_component_gets_its_value_view():
    with open(os.path.join(EXPECTED, "Kiln.Game.Ecs.g.cs"), encoding="utf-8") as f:
        text = f.read()
    assert "public partial struct Warmth : IGameAttribute<Warmth, float>" in text
    assert "public Span<float> Values => dataStorage;" in text
    assert "IGameAttribute<Warmth, float>.ValuesOf(ref Warmth self) => self.dataStorage;" in text
    assert text.count("IGameAttribute<") == 2  # Warmth only (its declaration and ValuesOf)


def test_the_types_resource_lists_every_type_of_the_dump():
    d = load_fixture()
    with open(os.path.join(EXPECTED, "Kiln.Game.types"), encoding="utf-8") as f:
        lines = f.read().split("\n")
    assert lines[0] == typegen.MARKER and lines[1].startswith("kiln.game.types\t1\tside=server\texe_sha256=")
    rows = [l for l in lines[2:] if l]
    assert [r.split("\t")[0] for r in rows] == sorted(t["qualifiedName"] for t in d["types"])
    assert "keen::ecs::Spark\t18\t8\t0x" in "\n".join(rows)
    assert any(r.startswith("keen::Tilt\t12\t2\t") and r.endswith("\tLeft=-1\tLevel=0\tRight=1") for r in rows)


# --- programs -------------------------------------------------------------------------------------------------------

CATALOG = os.path.join(FIX, "programs.json")


def test_programs_match_their_expected_output():
    out = work("programs")
    rc, so, se = run("programs", "--catalog", CATALOG, "--dump", DUMP, "--out", out)
    assert rc == 0, se
    assert so == "typegen: programs.json: 3 program descriptors\n", so
    assert read(os.path.join(out, "Kiln.Game.Programs.g.cs")) == read(os.path.join(EXPECTED, "Kiln.Game.Programs.g.cs"))
    text = read(os.path.join(out, "Kiln.Game.Programs.g.cs")).decode()
    contract = ("program lantern_glow\ntpl keen::ecs::Lantern\nread keen::ecs::Comet,keen::ecs::Gauges\n"
                "write keen::ecs::Warmth\nsys_read light_cache\nevents_out keen::ecs::LanternLit\n")
    assert f"Contract = 0x{typegen.fnv1a64(contract):016X}ul;" in text


def test_the_programs_file_declares_no_type_directly_in_namespace_kiln():
    text = read(os.path.join(EXPECTED, "Kiln.Game.Programs.g.cs")).decode()
    body = text[text.index("namespace Kiln;"):]
    top = [l for l in body.split("\n") if l and not l.startswith((" ", "{", "}", "//", "namespace "))]
    assert top == ["public static partial class Ecs"], top   # only the root class itself, as a partial
    assert "    public static class Programs" in text and "class EcsPrograms" not in text


def programs_refused(name, catalog, *needles):
    p = os.path.join(WORK, "cat-" + name + ".json")
    with open(p, "w", encoding="utf-8") as f:
        json.dump(catalog, f)
    out = work("cat-" + name)
    rc, so, se = run("programs", "--catalog", p, "--dump", DUMP, "--out", out)
    assert rc == 1 and se.startswith("typegen: error: "), (rc, se)
    for n in needles:
        assert n in se, f"{n!r} not in {se!r}"
    assert not os.path.exists(out)


def test_programs_refuse_what_the_dump_cannot_name():
    programs_refused("missing", [{"name": "p", "lists": {"0x58": ["Torch"]}}], "(p).write: Torch is not among the components of lanterns.json")
    programs_refused("event", [{"name": "p", "lists": {"0x160": ["Lantern"]}}], "(p).events_in: Lantern is not among the events")
    programs_refused("offset", [{"name": "p", "lists": {"0x20": ["Lantern"]}}], "'0x20'", "not a known access list offset")
    programs_refused("name", [{"name": "p q", "lists": {}}], "'p q' is not a program name")
    programs_refused("key", [{"name": "p", "lists": {}, "fn": 1}], "a program is {name, lists}")
    programs_refused("twice", [{"name": "a_b", "lists": {}}, {"name": "a__b", "lists": {}}], "name collision", "a_b", "a__b")


# --- the legacy mode ------------------------------------------------------------------------------------------------

# ---- the program catalog (tools/typegen/catalog.py; sigdb/programs/) ----
CATALOG_PY = os.path.join(ROOT, "tools", "typegen", "catalog.py")
CATALOGS = os.path.join(ROOT, "sigdb", "programs")


def catalog_run(*args):
    p = subprocess.run([sys.executable, "-I", CATALOG_PY] + list(args), capture_output=True, text=True)
    return p.returncode, p.stdout, p.stderr


def a_catalog():
    """A made-up catalog of two programs the check accepts (the made-up exe hash is not one of the known builds)."""
    sys.path.insert(0, os.path.dirname(CATALOG_PY))
    import catalog  # noqa: E402
    progs = []
    for name, lists, flags in (("alpha_update", {"read": ["Lantern"], "write": ["Glow", "Wick"]}, 0x10),
                               ("beta_client", {"sys_read": ["g38_test::Registry"], "events_in": ["LitEvent"]}, 0x1)):
        p = {"name": name, "flags": flags, "interval": 1, "update": not flags & 1, "lists": lists,
             "sys_read_opt": "0x0000000000000001", "sys_write_opt": "0x0000000000000000",
             "sys_write_parallel": "0x0000000000000000"}
        p["contract"] = catalog.contract(p)
        progs.append(p)
    return {"comment": "made up", "format": 1, "side": "server", "exe_sha256": "ab" * 32, "programs": progs}


def catalog_refused(name, mutate, needle):
    d = a_catalog()
    mutate(d)
    path = os.path.join(work(name), "server", d["exe_sha256"] + ".json")
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        f.write(json.dumps(d, indent=1) + "\n")
    rc, so, se = catalog_run("check", path)
    assert rc == 1 and needle in so, f"expected a refusal naming {needle!r}, got rc {rc}: {so}{se}"


def test_catalog_the_committed_catalogs_pass_their_check():
    rc, so, se = catalog_run("check")
    assert rc == 0, so + se
    files = sorted(f for _, _, fs in os.walk(CATALOGS) for f in fs)
    assert so.count(": ok") == len(files) >= 2, so
    assert "817 programs, 197 with flag 0x1, 197 with no update" in so, so


def test_catalog_a_made_up_catalog_passes():
    d = a_catalog()
    path = os.path.join(work("catalog-good"), "server", d["exe_sha256"] + ".json")
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        f.write(json.dumps(d, indent=1) + "\n")
    rc, so, se = catalog_run("check", path)
    assert rc == 0, so + se


def test_catalog_refuses_an_address_or_any_other_key():
    catalog_refused("catalog-site", lambda d: d["programs"][0].update(site="0x2464c5"), "the keys are")
    catalog_refused("catalog-top", lambda d: d.update(image_base="0x140000000"), "the keys are")


def test_catalog_refuses_a_wrong_contract():
    catalog_refused("catalog-contract", lambda d: d["programs"][0]["lists"]["write"].reverse(), "contract")


def test_catalog_refuses_a_value_that_is_not_a_name():
    catalog_refused("catalog-name", lambda d: d["programs"][0]["lists"]["read"].append("0x1363970"), "a non-empty list of names")
    catalog_refused("catalog-list", lambda d: d["programs"][0]["lists"].update(defaults=["Glow"]), "21 lists")


def test_catalog_contract_text_is_the_chapter_s():
    sys.path.insert(0, os.path.dirname(CATALOG_PY))
    import catalog  # noqa: E402
    p = a_catalog()["programs"][0]
    text = catalog.contract_text(p)
    assert text.startswith("tpl=\ntpl_opt=\nread=Lantern\nread_opt=\nwrite=Glow,Wick\n"), text
    assert text.endswith("sys_read_opt=0000000000000001\nsys_write_opt=0000000000000000\n"
                         "sys_write_parallel=0000000000000000\nflags=00000010\ninterval=1\n"), text
    assert text.count("\n") == 21 + 5 and catalog.fnv1a32("a") == 0xE40C292C


def test_legacy_mode_on_the_old_dump_is_generated_cs():
    out = os.path.join(work("legacy"), "Generated.cs")
    os.makedirs(os.path.dirname(out))
    rc, so, se = run(OLD_DUMP, out)
    assert rc == 0, se
    a, b = read(out).split(b"\n"), read(GENERATED_CS).split(b"\n")
    assert a[2].startswith(b"// Source: ") and b[2].startswith(b"// Source: ")
    del a[2], b[2]
    assert a == b, "the legacy output differs from managed/Kiln.Game/Generated.cs"


def test_legacy_mode_needs_two_arguments():
    rc, so, se = run(OLD_DUMP)
    assert rc == 2 and "typegen.py" in so


# --- verified-build inventory failures must not publish a partial set of variants ------------------------------------

def build_game_case(name, corrupt=None, verified=True):
    repo = work("build-game-" + name)
    script = os.path.join(repo, "tools", "typegen", "build-game.sh")
    os.makedirs(os.path.dirname(script))
    shutil.copyfile(os.path.join(ROOT, "tools", "typegen", "build-game.sh"), script)
    for digit in "01":
        sha = digit * 64
        for kind in ("types", "builds"):
            path = os.path.join(repo, "sigdb", kind, "server", sha + ".json")
            os.makedirs(os.path.dirname(path), exist_ok=True)
            data = {"schema": 2, "side": "server", "exe_sha256": sha} if kind == "types" else {"verified": verified}
            with open(path, "w", encoding="utf-8") as f:
                f.write("{" if digit == "1" and kind == corrupt else json.dumps(data))
    host = os.path.join(repo, "host")
    previous = os.path.join(host, "game", "previous")
    os.makedirs(previous)
    with open(os.path.join(previous, "Kiln.Game.dll"), "wb") as f:
        f.write(b"previous variant")
    # No managed build: the stub records whether inventory errors reached the build/publish stage at all.
    bin_dir = os.path.join(repo, "bin")
    os.makedirs(bin_dir)
    dotnet = os.path.join(bin_dir, "dotnet")
    with open(dotnet, "w", encoding="utf-8", newline="\n") as f:
        f.write('''#!/usr/bin/env bash
set -eu
printf 'build\\n' >> "$KILN_OUT/invocations"
for a in "$@"; do
  case $a in -p:KilnGameVariant=*) variant=${a#*=} ;; esac
done
dir=$KILN_OUT/dotnet/bin/Kiln.Game/release_$variant
mkdir -p "$dir"
printf 'test assembly' > "$dir/Kiln.Game.dll"
''')
    os.chmod(dotnet, 0o755)
    out = os.path.join(repo, "build")
    env = dict(os.environ, KILN_OUT=out, PATH=bin_dir + os.pathsep + os.environ["PATH"])
    result = subprocess.run(["bash", script, host], capture_output=True, text=True, env=env)
    return result, host, out


def inventory_refused(kind):
    p, host, out = build_game_case("bad-" + kind, corrupt=kind)
    assert p.returncode != 0, f"build-game accepted an unreadable {kind} file and published a partial set: {p.stdout}"
    assert "JSONDecodeError" in p.stderr, p.stderr
    assert read(os.path.join(host, "game", "previous", "Kiln.Game.dll")) == b"previous variant"
    assert not os.path.exists(os.path.join(out, "invocations")), "started builds with an incomplete inventory"


def test_build_game_refuses_an_unreadable_dump_after_a_valid_build():
    inventory_refused("types")


def test_build_game_refuses_an_unreadable_build_record_after_a_valid_build():
    inventory_refused("builds")


def test_build_game_publishes_the_complete_verified_inventory():
    p, host, out = build_game_case("valid")
    assert p.returncode == 0, p.stdout + p.stderr
    assert sorted(os.listdir(os.path.join(host, "game"))) == ["server-" + d * 12 for d in "01"]
    for digit in "01":
        assert read(os.path.join(host, "game", "server-" + digit * 12, "Kiln.Game.dll")) == b"test assembly"
    assert read(os.path.join(out, "invocations")) == b"build\nbuild\n"


def test_build_game_refuses_an_empty_verified_inventory_without_replacing_variants():
    p, host, out = build_game_case("unverified", verified=False)
    assert p.returncode != 0 and "no verified schema-2 dump" in p.stderr, p.stdout + p.stderr
    assert read(os.path.join(host, "game", "previous", "Kiln.Game.dll")) == b"previous variant"
    assert not os.path.exists(os.path.join(out, "invocations"))


def main():
    global WORK
    args = sys.argv[1:]
    bless = "--bless" in args
    if "--work" in args:
        WORK = os.path.abspath(args[args.index("--work") + 1])
    elif os.environ.get("KILN_OUT"):
        WORK = os.path.join(os.environ["KILN_OUT"], "typegen-tests")
    else:
        WORK = tempfile.mkdtemp(prefix="typegen-tests-")
    os.makedirs(WORK, exist_ok=True)
    if bless:
        out = work("bless")
        rc, so, se = run("generate", "--dump", DUMP, "--out", out, "--no-legacy")
        if rc != 0:
            print(se, end="")
            return 1
        os.makedirs(EXPECTED, exist_ok=True)
        for n in OUTPUTS:
            shutil.copyfile(os.path.join(out, n), os.path.join(EXPECTED, n))
        with open(os.path.join(EXPECTED, "summary.txt"), "w", encoding="utf-8", newline="\n") as f:
            f.write(so)
        rc, so, se = run("programs", "--catalog", os.path.join(FIX, "programs.json"), "--dump", DUMP, "--out", out)
        if rc != 0:
            print(se, end="")
            return 1
        shutil.copyfile(os.path.join(out, "Kiln.Game.Programs.g.cs"), os.path.join(EXPECTED, "Kiln.Game.Programs.g.cs"))
        print(f"blessed {EXPECTED}")
    tests = [v for k, v in sorted(globals().items()) if k.startswith("test_")]
    tests.sort(key=lambda f: f.__code__.co_firstlineno)
    for t in tests:
        case(t)
    print(f"{len(tests) - len(failures)} passed, {len(failures)} failed (work: {WORK})")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
