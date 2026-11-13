# SPDX-License-Identifier: MIT
"""sigport tests: definition trees and text edits, operand roles, and end-to-end ports between synthetic PE files.

    python3 -m unittest discover -s tools/sigport/tests -v
The real-exe benchmarks (bench.py replay/s2c/emf) run when the game exes are present: set SIGPORT_OLD/SIGPORT_NEW.
"""
import json
import os
import struct
import sys
import tempfile
import unittest
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent))

import port  # noqa: E402
from defs import Editor, Tree, _Spans  # noqa: E402
from exeinfo import Exe  # noqa: E402
from sides import merged  # noqa: E402
from sigdb_check import Resolver  # noqa: E402
from peimg import Image  # noqa: E402

TEXT, RDATA, PDATA = 0x1000, 0x4000, 0x5000


def build_pe(path, funcs, strings=()):
    """funcs: [(bytes, needs_pdata)] laid out from .text with int3 padding to 16; call targets patched via
    ('call', index) markers: a func may be a list of bytes/tuples. Returns the rva of each function."""
    code = bytearray()
    rvas, fixups = [], []
    for body, _pd in funcs:
        while len(code) % 16:
            code.append(0xCC)
        rvas.append(TEXT + len(code))
        for part in body:
            if isinstance(part, tuple):  # ('call'|'jmp', target index)
                op = 0xE8 if part[0] == "call" else 0xE9
                code.append(op)
                fixups.append((len(code), part[1]))
                code += b"\0\0\0\0"
            else:
                code += part
        code += b"\xCC" * 16
    ends = []
    for i, (body, _pd) in enumerate(funcs):
        start = rvas[i] - TEXT
        n = sum(len(p) if not isinstance(p, tuple) else 5 for p in body)
        ends.append(rvas[i] + n)
    for at, idx in fixups:
        rel = rvas[idx] - (TEXT + at + 4)
        code[at:at + 4] = struct.pack("<i", rel)
    rdata = bytearray(b"\x01\x00\x00\x00" + b"\0" * 12)  # one UNWIND_INFO (no codes) at RDATA
    for s in strings:
        rdata += s.encode() + b"\0"
    pdata = bytearray()
    for i, (_b, pd) in enumerate(funcs):
        if pd:
            pdata += struct.pack("<III", rvas[i], ends[i], RDATA)
    pdata += b"\0" * 12
    secs = [(b".text", TEXT, bytes(code)), (b".rdata", RDATA, bytes(rdata)), (b".pdata", PDATA, bytes(pdata))]
    hdr = bytearray(0x400)
    hdr[0:2] = b"MZ"
    struct.pack_into("<I", hdr, 0x3C, 0x40)
    hdr[0x40:0x44] = b"PE\0\0"
    struct.pack_into("<HHIIIHH", hdr, 0x44, 0x8664, len(secs), 0, 0, 0, 0xF0, 0x22)
    opt = 0x58
    struct.pack_into("<H", hdr, opt, 0x20B)
    struct.pack_into("<Q", hdr, opt + 24, 0x140000000)
    struct.pack_into("<II", hdr, opt + 32, 0x1000, 0x200)
    struct.pack_into("<I", hdr, opt + 56, 0x8000)
    struct.pack_into("<I", hdr, opt + 60, 0x400)
    struct.pack_into("<I", hdr, opt + 108, 16)
    off = opt + 0xF0
    raw = 0x400
    blobs = []
    for name, va, data in secs:
        size = (len(data) + 0x1FF) & ~0x1FF
        hdr[off:off + 8] = name.ljust(8, b"\0")
        struct.pack_into("<IIII", hdr, off + 8, len(data), va, size, raw)
        blobs.append(data.ljust(size, b"\0"))
        raw += size
        off += 40
    Path(path).write_bytes(bytes(hdr) + b"".join(blobs))
    return rvas


def h(s):
    return bytes.fromhex(s.replace(" ", ""))


def filler(k):
    """A distinct function with a frame (unique bodies, so identical-body matching has anchors)."""
    return [h("48 83 EC 28"), h("B8") + struct.pack("<I", 0x1000 + k * 7), h("05") + struct.pack("<I", k * 13 + 5),
            h("89 44 24 20 8B 44 24 20 48 83 C4 28 C3")]


def target(storage, frame, field_disp8=None, field_disp32=None, reg_rdi=True):
    """GetThing: lea rdi,[rcx+storage]; mov eax,[rdi+field] ..."""
    lea = h("48 8D B9") if reg_rdi else h("48 8D B1")
    body = [h("48 89 5C 24 08 57 48 83 EC") + bytes([frame]), lea + struct.pack("<I", storage)]
    if field_disp8 is not None:
        body.append((h("8B 47") if reg_rdi else h("8B 46")) + bytes([field_disp8]))
    else:
        body.append((h("8B 87") if reg_rdi else h("8B 86")) + struct.pack("<I", field_disp32))
    body.append(h("48 8B 5C 24 30 48 83 C4") + bytes([frame]) + h("5F C3"))
    return body


def caller(t):
    return [h("48 83 EC 28 48 8B 0D 00 00 00 00 B8 2A 00 00 00 89 44 24 20"), ("call", t),
            h("8B 4C 24 20 03 C1 89 44 24 20 48 83 C4 28 C3")]


DEFS = {
    "format": 1, "side": "server", "comment": "test",
    "symbols": {
        "GetThing": {"find": [{"pattern": "48 89 5C 24 08 57 48 83 EC 20 48 8D B9 {world.storage:d32} 8B 47"}]},
        "@GetThing.fields": {"find": [{"scope": {"symbol": "GetThing", "size": 64},
                                       "pattern": "8B 47 {thing.field:d8} 48 8B 5C 24 30"}]},
        "Caller": {"find": [{"pattern": "48 83 EC 28 48 8B 0D ?? ?? ?? ?? B8 2A 00 00 00 89 44 24 20 E8 {GetThing:rel32}"}]},
    },
    "constants": {"world.storage": {"min_votes": 1}, "thing.field": {"min_votes": 1}},
    "features": {"thing": {"symbols": ["GetThing"], "constants": ["world.storage", "thing.field"]}},
}


class SyntheticPort(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.d = Path(self.tmp.name)
        (self.d / "tree" / "server.d").mkdir(parents=True)
        (self.d / "tree" / "server.json").write_text(json.dumps(DEFS, indent=2) + "\n")
        self.cache = str(self.d / "cache")

    def tearDown(self):
        self.tmp.cleanup()

    def exes(self, new_target, new_fillers=3):
        old = self.d / "old.exe"
        new = self.d / "new.exe"
        o = [(filler(1), True), (filler(2), True), (target(0xE15DC0, 0x20, field_disp8=0x10), True), (filler(3), True)]
        o.append((caller(2), True))
        build_pe(old, o)
        n = [(filler(k), True) for k in range(1, 1 + new_fillers)]
        ti = len(n)
        n += [(new_target, True), (filler(9), True), (caller(ti), True)]
        rv = build_pe(new, n)
        return old, new, rv[ti]

    def run_port(self, old, new, *extra):
        out = self.d / "out"
        rc = port.main(["--old", str(old), "--new", str(new), "--defs", str(self.d / "tree/server.json"),
                        "--out", str(out), "--cache", self.cache, "--quiet"] + list(extra))
        return rc, json.loads((out / "report.json").read_text()), out

    def test_old_defs_resolve_on_old(self):
        old, _new, _ = self.exes(target(0xE179C0, 0x20, field_disp8=0x10))
        syms, consts, _ = Resolver(Image(old), merged(self.d / "tree/server.json")).run()
        self.assertTrue(syms["GetThing"]["ok"] and syms["Caller"]["ok"])
        self.assertEqual(consts["world.storage"]["value"], 0xE15DC0)

    def test_moved_layout_constant_needs_no_pattern_change(self):
        old, new, t = self.exes(target(0xE179C0, 0x20, field_disp8=0x10))
        rc, r, out = self.run_port(old, new)
        self.assertEqual(rc, 0, r["summary"])
        self.assertEqual(int(r["symbols"]["GetThing"]["new"], 16), t)
        self.assertEqual(r["constants"]["world.storage"]["new"], hex(0xE179C0))
        self.assertEqual(r["constants"]["world.storage"]["delta"], "+0x1c00")
        self.assertTrue(r["summary"]["defs_unchanged"])
        self.assertEqual((out / "symbols.patch").read_text(), "")

    def test_frame_change_regenerates_a_generalised_pattern(self):
        old, new, t = self.exes(target(0xE179C0, 0x30, field_disp8=0x10))
        for extra in ([], ["--no-exact"]):
            rc, r, out = self.run_port(old, new, *extra)
            self.assertEqual(rc, 0, (extra, r["summary"]))
            g = r["symbols"]["GetThing"]
            self.assertEqual(int(g["new"], 16), t)
            st = g["strategies"][0]
            self.assertTrue(st["pattern_changed"])
            self.assertIn("{world.storage:d32}", st["new_pattern"])
            self.assertTrue(st["generalized"], st)
            self.assertIn("48 83 EC ??", st["new_pattern"])
            # the patched tree resolves on both exes
            for exe, want in ((new, t), (old, None)):
                syms, consts, _ = Resolver(Image(exe), merged(out / "tree/server.json")).run()
                self.assertTrue(syms["GetThing"]["ok"], exe)
                if want:
                    self.assertEqual(syms["GetThing"]["rva"], want)
            self.assertIn("+++ b/", (out / "symbols.patch").read_text())
            rec = json.loads(next((out / "builds/server").glob("*.json")).read_text())
            self.assertFalse(rec["verified"])
            self.assertEqual(rec["symbols"]["GetThing"], hex(t))

    def test_register_and_encoding_change(self):
        # rdi -> rsi and the field grows past disp8: the probe's d8 hole becomes d32
        old, new, t = self.exes(target(0xE179C0, 0x20, field_disp32=0x90, reg_rdi=False))
        rc, r, out = self.run_port(old, new, "--min-confidence", "0.5")
        self.assertEqual(int(r["symbols"]["GetThing"]["new"], 16), t)
        self.assertEqual(r["constants"]["thing.field"]["new"], "0x90")
        st = r["symbols"]["@GetThing.fields"]["strategies"][0]
        self.assertIn("{thing.field:d32}", st["new_pattern"])
        self.assertTrue(any("d8 -> d32" in n for n in st["notes"]), st["notes"])

    def test_unrelated_new_exe_ports_nothing_confidently(self):
        old, _n, _ = self.exes(target(0xE179C0, 0x20, field_disp8=0x10))
        other = self.d / "other.exe"
        build_pe(other, [(filler(k), True) for k in range(20, 30)])
        rc, r, _ = self.run_port(old, other)
        self.assertEqual(rc, 1)
        self.assertFalse(r["symbols"]["GetThing"]["ok_new"])


class Strategies(unittest.TestCase):
    """A symbol with a second strategy anchored in the function's body: the one in use when another engine has hooked
    the function's first bytes (GitHub PR 27). The offline resolver asks more than the core: both must agree."""
    ENTRY = "48 89 5C 24 08 57 48 83 EC 20 48 8D B9"
    BODY = "8B 47 10 48 8B 5C 24 30 48 83 C4 20 5F C3"  # 17 bytes after the entry

    CALLER = "48 83 EC 28 48 8B 0D ?? ?? ?? ?? B8 2A 00 00 00 89 44 24 20 E8"  # the call's operand follows

    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.exe = Path(self.tmp.name) / "game.exe"
        funcs = [(filler(1), True), (target(0xE15DC0, 0x20, field_disp8=0x10), True), (filler(2), True),
                 (caller(1), True)]
        rvas = build_pe(self.exe, funcs)
        self.entry, self.caller = rvas[1], rvas[3]

    def tearDown(self):
        self.tmp.cleanup()

    def resolve(self, second, more=None):
        doc = {"symbols": {"GetThing": {"find": [{"pattern": self.ENTRY}, second]}}}
        doc["symbols"].update(more or {})
        return Resolver(Image(self.exe), doc).run()[0]["GetThing"]

    def test_both_strategies_name_the_entry(self):
        r = self.resolve({"pattern": self.BODY, "offset": -17})
        self.assertTrue(r["ok"], r)
        self.assertEqual((r["rva"], r["source"]), (self.entry, "GetThing.find[0]"))

    def test_a_hooked_entry_resolves_through_the_body(self):
        raw = bytearray(self.exe.read_bytes())
        at = 0x400 + self.entry - TEXT
        raw[at:at + 5] = h("E9 78 56 34 12")
        self.exe.write_bytes(bytes(raw))
        r = self.resolve({"pattern": self.BODY, "offset": -17})
        self.assertTrue(r["ok"], r)
        self.assertEqual((r["rva"], r["source"]), (self.entry, "GetThing.find[1]"))

    def test_a_second_strategy_with_a_stale_offset_fails_the_symbol(self):
        # what an update that makes the code before the anchor 3 bytes longer leaves behind
        r = self.resolve({"pattern": self.BODY, "offset": -14})
        self.assertFalse(r["ok"])
        self.assertIn("strategies disagree", r["error"])
        self.assertIn(f"{self.entry:#x}", r["error"])
        self.assertIn(f"{self.entry + 3:#x}", r["error"])

    def test_a_capture_that_agrees_does_not_bring_the_symbol_back(self):
        # the call in Caller votes for the entry, as the first strategy does: the stale second one still fails it
        more = {"Caller": {"find": [{"pattern": self.CALLER + " {GetThing:rel32}"}]}}
        self.assertTrue(self.resolve({"pattern": self.BODY, "offset": -17}, more)["ok"])
        r = self.resolve({"pattern": self.BODY, "offset": -14}, more)
        self.assertFalse(r["ok"])
        self.assertIn("strategies disagree", r["error"])

    def test_a_scoped_second_strategy_is_judged_whatever_the_order_of_the_definitions(self):
        # GetThing's second strategy looks inside Caller, which the definitions name after it
        call_at = self.caller + 20
        scoped = {"scope": {"symbol": "Caller", "size": 64}, "pattern": "E8 ?? ?? ?? ?? 8B 4C 24 20"}
        more = {"Caller": {"find": [{"pattern": self.CALLER}]}}
        self.assertTrue(self.resolve({**scoped, "offset": self.entry - call_at}, more)["ok"])
        r = self.resolve({**scoped, "offset": self.entry - call_at + 3}, more)
        self.assertFalse(r["ok"])
        self.assertIn("strategies disagree", r["error"])

    def test_a_second_strategy_that_matches_nowhere_or_twice_is_not_judged(self):
        self.assertTrue(self.resolve({"pattern": "0F 0B 0F 0B 0F 0B 0F 0B", "offset": -3})["ok"])
        self.assertTrue(self.resolve({"pattern": "48 83 EC 28 B8", "offset": -3})["ok"])  # both fillers start so


class Defs(unittest.TestCase):
    def test_spans_and_edit_keep_layout(self):
        text = '{\n  "symbols": {\n    "A": {\n      "find": [\n        { "pattern": "48 {x:d32}" },\n' \
               '        { "scope": { "symbol": "A", "size": 64 }, "pattern": "8B {y:d8}" }\n      ]\n    }\n  }\n}\n'
        sp = _Spans(text).spans
        a, b = sp[("symbols", "A", "find", 1, "pattern")]
        self.assertEqual(text[a:b], '"8B {y:d8}"')
        ed = Editor(text)
        ed.set(("symbols", "A", "find", 1, "pattern"), "8B 47 {y:d8}")
        ed.set(("symbols", "A", "find", 1, "scope", "size"), 128)
        ed.set(("symbols", "A", "find", 0, "offset"), 1)
        out = ed.text
        d = json.loads(out)
        self.assertEqual(d["symbols"]["A"]["find"][1]["pattern"], "8B 47 {y:d8}")
        self.assertEqual(d["symbols"]["A"]["find"][1]["scope"]["size"], 128)
        self.assertEqual(d["symbols"]["A"]["find"][0]["offset"], 1)
        self.assertEqual(out.count("\n"), text.count("\n"))  # no reformatting

    def test_add_symbol(self):
        for text in ('{\n  "format": 1,\n  "symbols": {}\n}\n',
                     '{\n  "format": 1,\n  "symbols": {\n    "A": {"find": [{"pattern": "90"}]}\n  }\n}\n',
                     '{\n  "format": 1\n}\n'):
            ed = Editor(text)
            ed.add_symbol("B", {"find": [{"pattern": "CC"}, {"pattern": "C3", "offset": 1}]})
            d = json.loads(ed.text)
            self.assertEqual(d["symbols"]["B"]["find"][1]["offset"], 1)

    def test_client_tree_writes_shared_symbols_to_an_overlay(self):
        with tempfile.TemporaryDirectory() as t:
            t = Path(t)
            (t / "server.d").mkdir()
            (t / "client.d").mkdir()
            (t / "server.json").write_text('{"format": 1, "side": "server", "symbols": {}}')
            (t / "client.json").write_text('{"format": 1, "side": "client", "symbols": {"Own": {"find": [{"pattern": "90"}]}}}')
            (t / "server.d/x.json").write_text('{"symbols": {"S": {"find": [{"pattern": "C3"}]}}, "constants": {"c": {"value": 1}}}')
            (t / "server.d/y.json").write_text('{"symbols": {"Y": {"find": [{"pattern": "CC"}]}, "Z": {"find": [{"pattern": "CD"}]}}}')
            (t / "client.d/y.json").write_text('{"extends": "server.d/y.json", "symbols": {"Z": {"find": [{"pattern": "CE"}]}}}')
            tr = Tree(t / "client.json")
            self.assertEqual(tr.dest["Own"], "client.json")
            self.assertEqual((tr.origin["S"], tr.dest["S"]), ("server.d/x.json", "client.d/x.json"))
            self.assertEqual((tr.origin["Y"], tr.dest["Y"]), ("server.d/y.json", "client.d/y.json"))
            self.assertEqual(tr.dest["Z"], "client.d/y.json")
            self.assertEqual(tr.doc["symbols"]["Z"]["find"][0]["pattern"], "CE")
            self.assertEqual(tr.cdest["c"], "client.d/x.json")
            st = Tree(t / "server.json")
            self.assertEqual(st.dest["S"], "server.d/x.json")


class Operands(unittest.TestCase):
    def ins(self, hexs):
        with tempfile.TemporaryDirectory() as t:
            p = Path(t) / "a.exe"
            build_pe(p, [([h(hexs + " C3")], True)])
            e = Exe(p, cache=None, quiet=True)
            return e.disasm(TEXT, TEXT + len(h(hexs)))[0]

    def test_disp_size_under_operand_size_prefix(self):
        i = self.ins("66 3B B9 50 53 E1 00")  # cmp di, word ptr [rcx + 0xe15350]: capstone says disp_size 2
        self.assertEqual((i["disp_off"], i["disp_size"]), (3, 4))
        self.assertEqual(port.role_of(i, 3, 4), ("disp", 0))

    def test_imm64_halves(self):
        i = self.ins("48 B9 00 AC 23 FC 06 00 00 00")  # movabs rcx, imm64 split into two d32 holes
        self.assertEqual(port.role_of(i, 2, 4), ("imm", 0))
        self.assertEqual(port.role_of(i, 6, 4), ("imm", 4))

    def test_branch(self):
        i = self.ins("E8 10 00 00 00")
        self.assertEqual(port.role_of(i, 1, 4), ("rel", 0))


@unittest.skipUnless(os.environ.get("SIGPORT_OLD") and os.environ.get("SIGPORT_NEW"), "SIGPORT_OLD/SIGPORT_NEW unset")
class RealExes(unittest.TestCase):
    def test_today_sigdb_ports_without_changes(self):
        with tempfile.TemporaryDirectory() as t:
            rc = port.main(["--old", os.environ["SIGPORT_OLD"], "--new", os.environ["SIGPORT_NEW"], "--out", t,
                            "--quiet"])
            r = json.loads((Path(t) / "report.json").read_text())
            self.assertEqual(r["summary"]["symbols"]["lost"], [])


if __name__ == "__main__":
    unittest.main()
