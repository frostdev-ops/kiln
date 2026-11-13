# SPDX-License-Identifier: MIT
"""tools/impact tests on synthetic programs only (no game files): codec (and agreement with tools/kfc's generic blob
encoder on a hand-written type db), assembler/disassembler round trips, ecall argument order, values, lint and the
committed verifier vectors, clone/patch relocation and site remapping, the catalog rules, and a cook against a
synthetic KFC4 game folder.

    python3 -m unittest discover -s tools/impact/tests -v
The check against the installed game is `python3 tools/impact/impact.py roundtrip` (local only).
"""
import json
import struct
import sys
import tempfile
import unittest
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent))
sys.path.insert(0, str(HERE.parent.parent / "kfc"))
sys.path.insert(0, str(HERE.parent.parent / "kfc" / "tests"))

import impact  # noqa: E402,F401
from impact import asm, catalog, cook, disasm, lint, ops, patch, program as P, values as V, vectors  # noqa: E402
from kfc import refl as R  # noqa: E402

CAT = catalog.Catalog.default()

SRC = """.kimp 1
program 0d1c2b3a-4f5e-4a6b-8c7d-9e0f1a2b3c4d
stack 256
streams OnCollision

var $stream    impact.EventStream
var $type      impact.EventStreamType = OnCollision
var $self      ecs.EntityId
var $where     float3 = (1.5, -2.0, 0.25)
var $range     float32 config NoiseRange = 12.5
var $wait      Time = 250ms
var $count     sint32
var $flag      bool = true
var $raw       uint32 = hex:78563412

code:
        ecall onEvent($stream, $type)
        pop
        ecall getSelf($self) site me
        pop
        iconst0
        gstore $count
loop:   gload $count
        inc
        dup
        gstore $count
        iconst 3
        ilt
        brf out
        ecall triggerNoise($where, $range)
        pop
        rvm
        br loop
out:    dself
        halt

shutdown:
        halt
"""


def mini_types():
    """A hand-written reflection db for keen::ImpactProgram (the layout of docs/re/impact.md), so tools/kfc's
    generic encoder can be compared with impact.program's codec without game files."""
    def t(name, kind, size, align, inner=None, fields=(), values=(), layout=0):
        return {"name": name, "hash": P.fnv1a32(name), "layout": layout, "size": size, "align": align, "kind": kind,
                "count": len(fields) or len(values), "inner": inner,
                "fields": [{"name": f, "type": ty, "offset": o} for f, ty, o in fields],
                "values": [{"name": n, "value": v} for n, v in values]}
    ty = [
        t("keen::uint8", R.K_U8, 1, 1), t("keen::uint16", R.K_U16, 2, 2), t("keen::uint32", R.K_U32, 4, 4),
        t("keen::HashKey32", R.K_STRUCT, 4, 4, fields=[("value", "keen::uint32", 0)]),
        t("keen::impact::ProgramId", R.K_TYPEDEF, 4, 4, inner="keen::HashKey32"),
        t("keen::Guid", R.K_GUID, 16, 4),
        t("keen::impact::EventStreamType", R.K_ENUM, 1, 1, inner="keen::uint8",
          values=[("OnCollision", 0), ("OnHit", 1), ("OnParry", 2), ("EntityInRange", 3)]),
        t("keen::Bitmask8<keen::impact::EventStreamType>", R.K_BM8, 1, 1, inner="keen::impact::EventStreamType"),
        t("keen::impact::EventStreamMask", R.K_TYPEDEF, 1, 1, inner="keen::Bitmask8<keen::impact::EventStreamType>"),
        t("keen::ImpactCommand", R.K_TYPEDEF, 4, 4, inner="keen::uint32"),
        t("keen::BlobArray<keen::ImpactCommand>", R.K_BLOB_ARRAY, 8, 4, inner="keen::ImpactCommand"),
        t("keen::BlobString", R.K_BLOB_STRING, 8, 4),
        t("keen::ImpactVariable", R.K_STRUCT, 24, 4, layout=P.VAR_LAYOUT_HASH, fields=[
            ("name", "keen::HashKey32", 0), ("configId", "keen::HashKey32", 4), ("type", "keen::HashKey32", 8),
            ("size", "keen::uint16", 12), ("offsetInBytes", "keen::uint16", 14), ("dbgName", "keen::BlobString", 16)]),
        t("keen::BlobArray<keen::ImpactVariable>", R.K_BLOB_ARRAY, 8, 4, inner="keen::ImpactVariable"),
        t("keen::BlobArray<keen::uint8>", R.K_BLOB_ARRAY, 8, 4, inner="keen::uint8"),
        t("keen::BlobArray<keen::uint32>", R.K_BLOB_ARRAY, 8, 4, inner="keen::uint32"),
        t("keen::ImpactProgram", R.K_STRUCT, 64, 4, layout=P.LAYOUT_HASH, fields=[
            ("id", "keen::impact::ProgramId", 0), ("programGuid", "keen::Guid", 4), ("stackSize", "keen::uint16", 20),
            ("usedStreams", "keen::impact::EventStreamMask", 22), ("code", "keen::BlobArray<keen::ImpactCommand>", 24),
            ("codeShutdown", "keen::BlobArray<keen::ImpactCommand>", 32),
            ("dataLayout", "keen::BlobArray<keen::ImpactVariable>", 40), ("data", "keen::BlobArray<keen::uint8>", 48),
            ("queryCallIds", "keen::BlobArray<keen::uint32>", 56)]),
    ]
    return R.TypeDb({"types": ty})


class CodecTests(unittest.TestCase):
    def setUp(self):
        self.p = asm.assemble(SRC, CAT)

    def test_roundtrip(self):
        b = P.encode(self.p)
        q = P.decode(b)
        self.assertEqual(q, self.p)
        self.assertEqual(P.encode(q), b)

    def test_layout(self):
        b = P.encode(self.p)
        self.assertEqual(struct.unpack_from("<I", b, 0)[0], P.program_id(self.p.guid))
        self.assertEqual(P.guid_text(b[4:20]), self.p.guid)
        self.assertEqual(struct.unpack_from("<HB", b, 0x14), (256, 1))
        rel, n = struct.unpack_from("<iI", b, 0x18)
        self.assertEqual((0x18 + rel, n), (64, len(self.p.code)))       # code right after the root
        rel, n = struct.unpack_from("<iI", b, 0x38)
        self.assertEqual((rel, n), (0, 0))                                # empty array = {0, 0}

    def test_agrees_with_kfc_encoder(self):
        from kfc.blob import Decoder
        from kfc.blobwrite import Encoder
        db = mini_types()
        self.assertEqual(P.layout_check(db), [])
        b = P.encode(self.p)
        v = Decoder(db).decode("keen::ImpactProgram", b)
        self.assertEqual(bytes(Encoder(db).encode("keen::ImpactProgram", v)), b)
        q = self.p.copy()
        q.query_ids = [0x11223344, 0x55667788]
        b2 = P.encode(q)
        self.assertEqual(bytes(Encoder(db).encode("keen::ImpactProgram", Decoder(db).decode("keen::ImpactProgram", b2))), b2)

    def test_layout_check_detects_change(self):
        db = mini_types()
        db.by_name["keen::ImpactProgram"]["layout"] = 0x1234
        self.assertTrue(P.layout_check(db))


class AsmTests(unittest.TestCase):
    def test_text_roundtrip(self):
        p = asm.assemble(SRC, CAT)
        for fold in (True, False):
            txt = disasm.disassemble(p, CAT, fold=fold)
            q = asm.assemble(txt, CAT)
            self.assertEqual(P.encode(q), P.encode(p), txt)
            self.assertEqual(disasm.disassemble(q, CAT, fold=fold), txt)

    def test_ecall_arg_order(self):
        """ecall name(a0, a1) pushes a1 first and a0 last: a0 is args[0], the top of the stack."""
        p = asm.assemble(SRC, CAT)
        ins = ops.decode(p.code)
        k = [j for j, i in enumerate(ins) if i.code == ops.ECALL and i.args[1] == 0xD471249F][0]  # triggerNoise
        where = p.var_index("where")
        rng = p.var_index("range")
        self.assertEqual(ins[k - 1].args[0], ops.handle(where))   # args[0] = position: pushed last
        self.assertEqual(ins[k - 2].args[0], ops.handle(rng))     # args[1] = range

    def test_ecall_forms(self):
        h = P.fnv1a32("ApplyDamage")
        for tok in ("applyDamage", "ApplyDamage", "0xb04d0e7a", "applydamage"):
            self.assertEqual(CAT.native(tok).hash, h)
        txt = ".kimp 1\nprogram 0d1c2b3a-4f5e-4a6b-8c7d-9e0f1a2b3c4e\ncode:\n        ecall Mod_X argc 0\n        pop\n        halt\n"
        p = asm.assemble(txt, CAT)
        self.assertEqual(ops.decode(p.code)[0].args[1], P.fnv1a32("Mod_X"))
        with self.assertRaises(asm.AsmError):
            asm.assemble(txt.replace(" argc 0", ""), CAT)   # unknown native without argc

    def test_args_short_forms(self):
        txt = (".kimp 1\nprogram 0d1c2b3a-4f5e-4a6b-8c7d-9e0f1a2b3c4f\nvar $v uint32\ncode:\n"
               "        ecall kiln.Signal($v, 0x1234)\n        pop\n        ecall kiln.Signal(_, 1)\n        pop\n        halt\n")
        p = asm.assemble(txt, CAT)
        w = p.code
        self.assertEqual(w[:5], [11, 0x1234, 11, ops.handle(0), 19])
        self.assertEqual(w[8:10], [13, 12])  # 1 -> ICONST1, _ -> ICONST0

    def test_sites(self):
        p = asm.assemble(SRC, CAT)
        gb = P.guid_bytes(p.guid)
        sites = [i.args[0] for i in ops.decode(p.code) if i.code == ops.ECALL]
        self.assertEqual(sites[1], P.fnv1a32(gb + b"me"))           # named site
        self.assertEqual(sites[0], P.fnv1a32(gb + b"code#0"))       # default: section#ordinal

    def test_auto_layout_and_alias(self):
        txt = (".kimp 1\nprogram 0d1c2b3a-4f5e-4a6b-8c7d-9e0f1a2b3c50\nvar $a bool = true\n"
               "var $dmg impact.DamageSetup = {magicalFireDamage: 3.0}\nvar $fire float32 @$dmg+12\ncode:\n        halt\n")
        p = asm.assemble(txt, CAT)
        a, d, f = p.variables
        self.assertEqual((a.offset, d.offset, f.offset), (0, 16, 28))
        self.assertEqual(struct.unpack_from("<f", p.data, 28)[0], 3.0)
        self.assertEqual(len(p.data), 16 + 52)

    def test_conflicting_init(self):
        txt = (".kimp 1\nprogram 0d1c2b3a-4f5e-4a6b-8c7d-9e0f1a2b3c51\nvar $a sint32 = 1\nvar $b sint32 @0 = 2\n"
               "code:\n        halt\n")
        with self.assertRaises(asm.AsmError):
            asm.assemble(txt, CAT)

    def test_errors_have_lines(self):
        with self.assertRaises(asm.AsmError) as cm:
            asm.assemble(SRC.replace("brf out", "brf nowhere"), CAT, src="x.kimp")
        self.assertIn("x.kimp:", str(cm.exception))


class ValueTests(unittest.TestCase):
    def test_floats(self):
        for x in (0.1, 20.0, 1500.0, 1.0 / 3.0, -0.0, 3.4e38, 1e-7):
            b = struct.pack("<f", x)
            s = V.f32_text(struct.unpack("<f", b)[0])
            self.assertEqual(struct.pack("<f", float(s)), b, s)
        self.assertEqual(V.f32_text(20.0), "20.0")

    def test_typed(self):
        db = CAT.records
        cases = [("keen::Time", "250ms"), ("keen::Time", "2s"), ("keen::Time", "7ns"), ("keen::float3", "(1.0, 2.0, 3.5)"),
                 ("keen::impact::HitReactionType", "Medium"), ("keen::FactionMask", "[Vukah, Player]"),
                 ("keen::BuffTypeReference", "3f1e0114-0000-4000-8000-000000000001"), ("keen::bool", "true")]
        for tn, txt in cases:
            raw = V.encode_value(db, tn, V.parse_ast(txt))
            self.assertEqual(V.exact_text(db, tn, raw), txt, tn)

    def test_hex_fallback(self):
        db = CAT.records
        raw = b"\x02"  # a bool byte that is not 0/1 has no exact typed form
        self.assertIsNone(V.exact_text(db, "keen::bool", raw))
        self.assertEqual(V.encode_value(db, "keen::bool", V.parse_ast("hex:02")), raw)


class LintTests(unittest.TestCase):
    def test_sample_clean(self):
        rep = lint.lint(asm.assemble(SRC, CAT), CAT)
        self.assertTrue(rep.ok, [str(f) for f in rep.findings])
        self.assertEqual(rep.max_depth["code"], 2)

    def test_vectors_match_committed(self):
        committed = json.loads((HERE / "vectors" / "index.json").read_text())["vectors"]
        with tempfile.TemporaryDirectory() as d:
            fresh = vectors.write(d, CAT)
            for a, b in zip(fresh, committed):
                self.assertEqual((a["file"], a["ok"], a["errors"], a["maxDepth"]),
                                 (b["file"], b["ok"], b["errors"], b["maxDepth"]))
                self.assertEqual((Path(d) / a["file"]).read_bytes(), (HERE / "vectors" / b["file"]).read_bytes())
        names = {v["file"]: v for v in committed}
        self.assertTrue(names["ok_depth31.bin"]["ok"])
        self.assertEqual(names["ok_depth31.bin"]["maxDepth"]["code"], 31)
        self.assertEqual(names["bad_overflow32.bin"]["errors"], ["E_OVERFLOW"])

    def test_unconnected_argument(self):
        head = ".kimp 1\nprogram 0d1c2b3a-4f5e-4a6b-8c7d-9e0f1a2b3c53\nvar $a impact.AttackCategoryMask\nvar $b impact.AttackCategoryMask\nvar $m bool\nvar $e ecs.EntityId\ncode:\n"
        rep = lint.lint(asm.assemble(head + "        ecall hasAttackCategory($a, _, $b)\n        pop\n        halt\n", CAT), CAT)
        self.assertIn("E_UNCONNECTED", [f.code for f in rep.errors])
        rep = lint.lint(asm.assemble(head + "        ecall hasAttackCategory($a, $m, $b)\n        pop\n        halt\n", CAT), CAT)
        self.assertNotIn("E_UNCONNECTED", [f.code for f in rep.findings])
        rep = lint.lint(asm.assemble(head + "        ecall isEnemy($e, _)\n        pop\n        halt\n", CAT), CAT)
        self.assertIn("E_UNCONNECTED", [f.code for f in rep.errors])
        rep = lint.lint(asm.assemble(head + "        ecall isInState(_, $e)\n        pop\n        halt\n", CAT), CAT)
        self.assertIn("W_UNCONNECTED", [f.code for f in rep.findings])

    def test_raw_arg_rejected(self):
        txt = (".kimp 1\nprogram 0d1c2b3a-4f5e-4a6b-8c7d-9e0f1a2b3c52\nvar $p float3\ncode:\n"
               "        ecall triggerNoise($p, 5)\n        pop\n        halt\n")
        rep = lint.lint(asm.assemble(txt, CAT), CAT)
        self.assertIn("E_ARG", [f.code for f in rep.errors])

    def test_stream_warning(self):
        rep = lint.lint(asm.assemble(SRC.replace("streams OnCollision", "streams none"), CAT), CAT)
        self.assertIn("W_STREAM", [f.code for f in rep.findings])


class PatchTests(unittest.TestCase):
    def setUp(self):
        self.p = asm.assemble(SRC, CAT)
        self.new = "1a2b3c4d-0000-4000-8000-00000000abcd"

    def test_clone_resites(self):
        q = patch.clone(self.p, CAT, self.new)
        self.assertEqual(q.id, P.program_id(self.new))
        old = [i.args[0] for i in ops.decode(self.p.code) if i.code == ops.ECALL]
        new = [i.args[0] for i in ops.decode(q.code) if i.code == ops.ECALL]
        gb = P.guid_bytes(self.new)
        self.assertEqual(new, [P.fnv1a32(gb + struct.pack("<I", s)) for s in old])
        k = patch.clone(self.p, CAT, self.new, keep_sites=True)
        self.assertEqual([i.args[0] for i in ops.decode(k.code) if i.code == ops.ECALL], old)

    def test_insert_relocates_branches(self):
        q = patch.clone(self.p, CAT, self.new, [
            {"var": "var $tag uint32 = 9"},
            {"insert": "ecall kiln.Signal($self, $tag)\npop", "after": {"native": "getSelf"}},
            {"insert": "ecall kiln.Log($count)\npop", "before": {"native": "triggerNoise"}},
        ])
        rep = lint.lint(q, CAT)
        self.assertTrue(rep.ok, [str(f) for f in rep.findings])
        txt = disasm.disassemble(q, CAT)
        # the loop still branches to its head and its exit; the Log call sits inside the loop
        self.assertIn("br L", txt)
        ins = ops.decode(q.code)
        names = [CAT.native(i.args[1]).name for i in ins if i.code == ops.ECALL]
        self.assertEqual(names, ["onEvent", "getSelf", "kiln.Signal", "kiln.Log", "triggerNoise"])
        br = [i for i in ins if i.code == 8][0]
        head = [i for i in ins if i.pc == br.args[0]][0]
        self.assertEqual(head.code, 22)  # gload $count: the loop head moved and the jump followed it
        self.assertEqual(len(q.data), len(self.p.data) + 4 + (-len(self.p.data)) % 16)

    def test_insert_with_end_label(self):
        q = patch.clone(self.p, CAT, self.new, [
            {"insert": "        gload $flag\n        brf skip\n        ecall kiln.Log($flag)\n        pop\nskip:",
             "before": {"native": "triggerNoise"}}])
        self.assertTrue(lint.lint(q, CAT).ok)

    def test_swap_replace_remove_set(self):
        q = patch.clone(self.p, CAT, self.new, [
            {"swap": "triggerNoise", "with": "kiln.Signal"},
            {"set": "$range", "value": "40.0"},
            {"config": "$range", "id": "0"},
        ])
        self.assertIn(P.fnv1a32("kiln.Signal"), [i.args[1] for i in ops.decode(q.code) if i.code == ops.ECALL])
        v = q.variables[q.var_index("range")]
        self.assertEqual(struct.unpack_from("<f", q.data, v.offset)[0], 40.0)
        self.assertEqual(v.config, 0)
        r = patch.clone(self.p, CAT, self.new, [{"remove": {"native": "getSelf"}}])
        self.assertTrue(lint.lint(r, CAT).ok)
        self.assertNotIn(0x01D09759, [i.args[1] for i in ops.decode(r.code) if i.code == ops.ECALL])
        with self.assertRaises(patch.PatchError):
            patch.clone(self.p, CAT, self.new, [{"swap": "triggerNoise", "with": "getSelf"}])  # argc differs


class CatalogTests(unittest.TestCase):
    def test_vanilla(self):
        self.assertEqual(len(CAT.natives), 107 + 2)
        n = CAT.native("applyDamage")
        self.assertEqual((n.argc, n.node, n.hash), (25, "ApplyDamage", 0xB04D0E7A))
        self.assertEqual(CAT.native("changeAttributeValue").node, "ChangeAttribute")
        self.assertTrue(CAT.native("forEachEntityWithFactionInRange").extra.get("query"))
        self.assertEqual(CAT.native("kiln.Signal").hash, P.fnv1a32("kiln.Signal"))
        self.assertEqual(CAT.problems, [])

    def test_declarations(self):
        c = catalog.Catalog.default()
        c.add_natives({"format": 1, "natives": [
            {"name": "mymod.Good", "argc": 1, "args": [{"name": "x", "type": "float32"}]},
            {"name": "bad name", "argc": 0},
            {"name": "kiln.Mine", "argc": 0},
            {"name": "mymod.Collide", "hash": "0xb04d0e7a", "argc": 25},
        ]}, "mod:test")
        probs = " | ".join(c.problems)
        self.assertIn("bad name", probs)
        self.assertIn("reserved", probs)
        self.assertIn("collides", probs)
        self.assertEqual(c.native("mymod.Good").hash, P.fnv1a32("mymod.Good"))
        self.assertEqual(c.native("applyDamage").origin, "vanilla")


class FakeGame:
    def __init__(self, kfc, types):
        self.kfc, self.types = kfc, types
        self.version = kfc.version

    def has_program(self, guid):
        from kfc.archive import ResourceKey
        return self.kfc.has(ResourceKey(guid, P.TYPE_HASH, 0))


@unittest.skipIf(sys.version_info < (3, 14), "the synthetic KFC needs Python 3.14 compression.zstd")
class CookTests(unittest.TestCase):
    def test_cook(self):
        from test_kfc import build_kfc
        from kfc.archive import Kfc
        base = asm.assemble(SRC, CAT)
        with tempfile.TemporaryDirectory() as d:
            d = Path(d)
            (d / "game").mkdir()
            build_kfc(d / "game", [(base.guid, P.TYPE_HASH, 0, P.encode(base))], [b"x"])
            k = Kfc(d / "game")
            k.bundles[P.TYPE_HASH]["layout"] = P.LAYOUT_HASH
            g = FakeGame(k, mini_types())
            g.types.dump["exe_sha256"] = "synthetic"
            mod = d / "Mod"
            (mod / "impact").mkdir(parents=True)
            (mod / "impact" / "new.kimp").write_text(SRC.replace("0d1c2b3a-4f5e-4a6b-8c7d-9e0f1a2b3c4d",
                                                                 "0d1c2b3a-4f5e-4a6b-8c7d-000000000001"))
            (mod / "impact" / "natives.json").write_text(json.dumps({"format": 1, "natives": [
                {"name": "mod.Ping", "argc": 1, "args": [{"name": "v", "types": ["any"]}]}]}))
            recipe = {"format": 1, "tool": "impact", "natives": ["natives.json"],
                      "inputs": {"b": {"guid": base.guid, "type": "keen::ImpactProgram"}},
                      "steps": [{"op": "assemble", "src": "new.kimp", "stem": "new"},
                                {"op": "clone", "base": "b", "guid": "0d1c2b3a-4f5e-4a6b-8c7d-000000000002", "stem": "c",
                                 "edits": [{"insert": "ecall mod.Ping($self)\npop", "after": {"native": "getSelf"}}]},
                                {"op": "clone", "base": "b", "guid": "0d1c2b3a-4f5e-4a6b-8c7d-000000000003", "stem": "bad",
                                 "edits": [{"insert": "pop", "after": {"native": "getSelf"}}]}]}
            rp = mod / "impact" / "cook.json"
            rp.write_text(json.dumps(recipe))
            r = cook.run(rp, g, update_manifest=True)
            st = {s["stem"]: s for s in r["steps"]}
            self.assertTrue(st["new"]["ok"] and st["c"]["ok"])
            self.assertFalse(st["bad"]["ok"])           # lint error (underflow): not written
            self.assertIn("E_UNDERFLOW", st["bad"]["error"])
            self.assertFalse(r["ok"])
            man = json.loads((mod / "assets" / "assets.json").read_text())
            files = sorted(e["file"] for e in man["entries"])
            self.assertEqual(files, ["cooked/impact/c.bin", "cooked/impact/new.bin"])
            self.assertTrue(all(e["mode"] == "new" and e["type"] == "keen::ImpactProgram" for e in man["entries"]))
            c = P.decode((mod / "assets" / "cooked" / "impact" / "c.bin").read_bytes())
            self.assertIn(P.fnv1a32("mod.Ping"), [i.args[1] for i in ops.decode(c.code) if i.code == ops.ECALL])
            self.assertTrue(cook.needs_cook(rp))       # the failed step keeps it "needs cook"
            # everything the cook wrote is in the mod's assets/cooked folder (the report too) and its manifest
            written = sorted(f.relative_to(mod).as_posix() for f in mod.rglob("*") if f.is_file() and f.relative_to(mod).parts[0] != "impact")
            self.assertEqual(written, ["assets/assets.json", "assets/cooked/impact/c.bin", "assets/cooked/impact/cook-report.json", "assets/cooked/impact/new.bin"])
            # a stem that would lead out of that folder is refused before anything is written
            rp.write_text(json.dumps(dict(recipe, steps=[dict(recipe["steps"][0], stem="../../escaped")])))
            before = sorted(str(f) for f in mod.rglob("*"))
            with self.assertRaises(cook.RecipeError) as e:
                cook.run(rp, g, update_manifest=True)
            self.assertIn("outside the mod's assets/cooked folder", str(e.exception))
            self.assertEqual(sorted(str(f) for f in mod.rglob("*")), before)
            self.assertFalse((mod / "assets" / "escaped.bin").exists())
            # another manifest than the mod's assets/assets.json is refused
            rp.write_text(json.dumps(dict(recipe, manifest="../elsewhere.json")))
            with self.assertRaises(cook.RecipeError) as e:
                cook.run(rp, g, update_manifest=True)
            self.assertIn("assets/assets.json only", str(e.exception))
            self.assertFalse((mod / "elsewhere.json").exists())
            # an "out" outside it is refused with what to change, and nothing is written
            for out in ("built", "../cooked/impact", "../assets", "../assets/cooked/../impact"):
                rp.write_text(json.dumps(dict(recipe, out=out)))
                before = sorted(str(f) for f in mod.rglob("*"))
                with self.assertRaises(cook.RecipeError, msg=out) as e:
                    cook.run(rp, g, update_manifest=True)
                self.assertIn("outside the mod's assets/cooked folder", str(e.exception))
                self.assertIn('"../assets/cooked/<name>"', str(e.exception))
                with self.assertRaises(cook.RecipeError):
                    cook.needs_cook(rp)
                self.assertEqual(sorted(str(f) for f in mod.rglob("*")), before)
            k.close()


if __name__ == "__main__":
    unittest.main()
