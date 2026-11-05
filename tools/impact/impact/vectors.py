# SPDX-License-Identifier: MIT
"""Verifier test vectors (synthetic programs, no game data) for kiln.abilities' C++ verifier and for tests.

tests/vectors/<name>.bin is a keen::ImpactProgram blob; tests/vectors/index.json lists, per vector:
    file, ok (the verdict of the runtime rules [rt]), errors (rt error codes), lintOk, lintErrors (all codes),
    maxDepth {code, shutdown} (null where the depth check fails), natives (hash, argc) the vector assumes.
Natives used: getSelf (0x01d09759, argc 1), applyDamage (0xb04d0e7a, argc 25), forEachEntityWithFactionInRange
(0x19143416, argc 6, a query native), kiln.Signal (0x38b05baf, argc 2), and the unknown 0xdeadbeef.
"""
import json
from pathlib import Path

from . import asm, lint as L, program as P

RT_CODES = {"E_ID", "E_SIZE", "E_END", "E_OPCODE", "E_TRUNC", "E_TARGET", "E_FRAME", "E_FALLOFF", "E_DEPTH", "E_UNDERFLOW",
            "E_OVERFLOW", "E_HANDLE", "E_LOADTYPE", "E_COPY", "E_NATIVE", "E_QUERY", "E_UNCONNECTED"}

G = "5a1e0000-0000-4000-8000-0000000000{:02x}"


def _hdr(n, extra=""):
    return f".kimp 1\nprogram {G.format(n)}\nstack 256\nstreams none\n{extra}\n"


def _push(k):
    return "\n".join(["        iconst0"] * k)


def _pops(k):
    return "\n".join(["        pop"] * k)


# what the native reads with no check is connected: the depth is what these two are about
DAMAGE_VARS = "var $e ecs.EntityId\nvar $p float3\nvar $d impact.MajorDirection"
DAMAGE_ARGS = ", ".join(["_", "$e", "$e", "$p", "$p", "$d"] + ["_"] * 19)

SOURCES = [
    ("ok_minimal", _hdr(1) + "code:\n        halt\n"),
    ("ok_wait_loop", _hdr(2, "var $wait Time = 250ms") + """code:
        gload $wait
        ltime
        iadd
L:      dup
        ltime
        ileq
        brt done
        rvm
        br L
done:   pop
        halt
"""),
    ("ok_depth31", _hdr(3, DAMAGE_VARS) + "code:\n" + _push(6) + f"\n        ecall applyDamage({DAMAGE_ARGS})\n" + _pops(7) + "\n        halt\n"),
    ("bad_overflow32", _hdr(4, DAMAGE_VARS) + "code:\n" + _push(7) + f"\n        ecall applyDamage({DAMAGE_ARGS})\n" + _pops(8) + "\n        halt\n"),
    ("bad_underflow", _hdr(5) + "code:\n        pop\n        halt\n"),
    ("bad_depth_mismatch", _hdr(6, "var $c bool = true") + """code:
        gload $c
        brt skip
        iconst1
skip:   halt
"""),
    ("bad_falloff", _hdr(7) + "code:\n        iconst0\n        pop\n"),
    ("bad_no_halt", _hdr(23) + "code:\nL:      rvm\n        br L\n"),
    ("bad_target", _hdr(8) + "code:\n        word 8, 3\n        word 11, 0\n        halt\n"),
    ("bad_opcode", _hdr(9) + "code:\n        word 0x1f\n        halt\n"),
    ("bad_trunc", _hdr(10) + "code:\n        halt\n        word 11\n"),
    ("bad_frame", _hdr(11) + "code:\n        call f, 0\n        pop\n        halt\nf:      iconst0\n        ret\n"),
    ("bad_handle", _hdr(12, "var $x sint32") + "code:\n        gload 0xffff0005\n        pop\n        halt\n"),
    ("bad_loadtype", _hdr(13, "var $v float3") + "code:\n        gload $v\n        pop\n        halt\n"),
    ("ok_copy", _hdr(14, "var $a sint32 = 5\nvar $b sint32") + "code:\n        copy $a, $b\n        halt\n"),
    ("bad_copy_type", _hdr(15, "var $a sint32 = 5\nvar $b float32") + "code:\n        copy $a, $b\n        halt\n"),
    ("bad_native", _hdr(16) + "code:\n        ecall 0xdeadbeef argc 0\n        pop\n        halt\n"),
    ("ok_kiln_signal", _hdr(18, "var $self ecs.EntityId") + """code:
        ecall getSelf($self)
        pop
        ecall kiln.Signal($self, 7)
        pop
        halt
"""),
    ("bad_query_missing", _hdr(19, "queries none\nvar $e ecs.EntityId\nvar $i uint32\nvar $f FactionMask\nvar $o WorldTransform\nvar $r float32 = 5.0") + """code:
        iconst0
        gstore $i
L:      ecall forEachEntityWithFactionInRange($e, _, $i, $f, $o, $r) site q
        brt L
        halt
"""),
    ("ok_query", _hdr(20, "var $e ecs.EntityId\nvar $i uint32\nvar $f FactionMask\nvar $o WorldTransform\nvar $r float32 = 5.0") + """code:
        iconst0
        gstore $i
L:      ecall forEachEntityWithFactionInRange($e, _, $i, $f, $o, $r) site q
        brt L
        halt
shutdown:
        ecall destroyFactionQuery($e, _, $i, $f, $o, $r) site q
        pop
        halt
"""),
    ("bad_shutdown_falloff", _hdr(21) + "code:\n        halt\nshutdown:\n        iconst0\n        pop\n"),
    ("ok_counter_loop", _hdr(22, "var $n sint32") + """code:
        iconst0
        gstore $n
L:      gload $n
        inc
        dup
        gstore $n
        iconst 10
        ilt
        brt L
        halt
"""),
    ("bad_unconnected", _hdr(24, "var $a impact.AttackCategoryMask\nvar $b impact.AttackCategoryMask") + """code:
        ecall hasAttackCategory($a, _, $b)
        pop
        halt
"""),
]


def build(cat):
    out = []
    for name, src in SOURCES:
        p = asm.assemble(src, cat, src=name)
        out.append((name, p))
    # a wrong id cannot be written in .kimp without saying so: build it directly
    p = asm.assemble(_hdr(17) + "code:\n        halt\n", cat, src="bad_id")
    p.id ^= 1
    out.append(("bad_id", p))
    return out


def write(outdir, cat):
    outdir = Path(outdir)
    outdir.mkdir(parents=True, exist_ok=True)
    idx = []
    for name, p in build(cat):
        blob = P.encode(p)
        (outdir / f"{name}.bin").write_bytes(blob)
        rep = L.lint(p, cat)
        codes = sorted({f.code for f in rep.errors})
        rt = sorted(c for c in codes if c in RT_CODES)
        idx.append({"file": f"{name}.bin", "ok": not rt, "errors": rt, "lintOk": rep.ok, "lintErrors": codes,
                    "maxDepth": rep.max_depth, "sha256": __import__("hashlib").sha256(blob).hexdigest(),
                    "natives": [{"hash": f"0x{h:08x}", "argc": a} for h, (n, a, o) in sorted(rep.natives.items())]})
    (outdir / "index.json").write_text(json.dumps({"format": 1, "comment": __doc__.strip().splitlines()[0],
                                                   "maxStack": 31, "vectors": idx}, indent=1) + "\n")
    return idx
