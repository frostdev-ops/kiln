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
OUTPUTS = ("Kiln.Game.Ecs.g.cs", "Kiln.Game.Arrays.g.cs", "Kiln.Game.Legacy.g.cs", "Kiln.Game.layouts")

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
    refused("in-base", lambda d: field(find(d, "keen::ecs::Warmth"), "store").update(offset=4),
            "keen::ecs::Warmth.store at 4 lies inside its base keen::ecs::FloatMeter")
    refused("repeat", lambda d: field(find(d, "keen::ecs::Warmth"), "store").update(name="key"),
            "field 'key' is declared by keen::ecs::MeterBase and by keen::ecs::Warmth")
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


# --- the legacy mode ------------------------------------------------------------------------------------------------

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
        print(f"blessed {EXPECTED}")
    tests = [v for k, v in sorted(globals().items()) if k.startswith("test_")]
    tests.sort(key=lambda f: f.__code__.co_firstlineno)
    for t in tests:
        case(t)
    print(f"{len(tests) - len(failures)} passed, {len(failures)} failed (work: {WORK})")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
