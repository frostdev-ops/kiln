# SPDX-License-Identifier: MIT
"""The local check over the installed game (read only; outputs under $KILN_OUT/impact):

for every keen::ImpactProgram:
  1. codec       decode -> encode is byte-identical (tools/impact's own codec)
  2. kfc         tools/kfc's generic reflected decode -> encode is byte-identical too (two independent readings agree)
  3. asm         disassemble (folded and raw) -> assemble is byte-identical
  4. lint        no errors; max stack depth
  5. rules       id = FNV-1a(GUID), queryCallIds = the query natives' sites, every ECALL hash is registered
Listings go to $KILN_OUT/impact/dis/<guid>.kimp (for reading; never commit them: they are game data).
"""
import collections
import time

from . import asm, disasm, game as G, lint as L, ops, program as P


def run(g, cat, limit=0, write_listings=True):
    from kfc.blob import Decoder
    from kfc.blobwrite import Encoder
    dec, enc = Decoder(g.types), Encoder(g.types)
    t0 = time.time()
    res = collections.Counter()
    fails = []
    depth = collections.Counter()
    natives_used = collections.Counter()
    disdir = G.out_dir("dis") if write_listings else None
    n = 0
    for guid, raw, p in g.programs():
        if limit and n >= limit:
            break
        n += 1
        ok = True

        def fail(what, detail=""):
            nonlocal ok
            ok = False
            res[f"{what}_fail"] += 1
            if len(fails) < 50:
                fails.append({"guid": guid, "check": what, "detail": str(detail)[:300]})

        if P.encode(p) == raw:
            res["codec_ok"] += 1
        else:
            fail("codec")
        try:
            if bytes(enc.encode(P.TYPE_HASH, dec.decode(P.TYPE_HASH, raw))) == raw:
                res["kfc_ok"] += 1
            else:
                fail("kfc")
        except Exception as e:  # noqa: BLE001
            fail("kfc", e)
        for fold in (True, False):
            txt = disasm.disassemble(p, cat, fold=fold)
            try:
                q = asm.assemble(txt, cat, src=guid)
                if P.encode(q) == raw:
                    res["asm_fold_ok" if fold else "asm_raw_ok"] += 1
                else:
                    fail("asm_fold" if fold else "asm_raw", "bytes differ")
            except asm.AsmError as e:
                fail("asm_fold" if fold else "asm_raw", e)
            if fold and disdir is not None:
                (disdir / f"{guid}.kimp").write_text(txt)
        rep = L.lint(p, cat)
        if rep.ok:
            res["lint_ok"] += 1
        else:
            fail("lint", "; ".join(str(f) for f in rep.errors[:3]))
        for s, d in rep.max_depth.items():
            if d is not None:
                depth[d] += 1
        for h in rep.natives:
            natives_used[h] += 1
        if p.id == P.program_id(p.guid):
            res["id_ok"] += 1
        else:
            fail("id")
        qn = {x.hash for x in cat.natives.values() if x.extra.get("query")}
        want = []
        for words in (p.code, p.shutdown):
            for i in ops.decode(words):
                if i.code == ops.ECALL and i.args[1] in qn and i.args[0] not in want:
                    want.append(i.args[0])
        if want == p.query_ids:
            res["queries_ok"] += 1
        else:
            fail("queries", f"{p.query_ids} vs {want}")
        if ok:
            res["all_ok"] += 1
    summary = {
        "programs": n, "ok": res["all_ok"] == n, "all_checks_ok": res["all_ok"],
        "codec_identical": res["codec_ok"], "kfc_identical": res["kfc_ok"],
        "asm_identical_folded": res["asm_fold_ok"], "asm_identical_raw": res["asm_raw_ok"],
        "lint_clean": res["lint_ok"], "id_rule": res["id_ok"], "query_rule": res["queries_ok"],
        "max_stack_depth": max(depth) if depth else None, "natives_used": len(natives_used),
        "natives_known": len(cat.natives), "seconds": round(time.time() - t0, 1),
        "game": {"kfc": g.version, "exe": g.types.dump.get("exe_sha256")},
    }
    return {"summary": summary, "failures": fails, "depth_histogram": dict(sorted(depth.items())),
            "listings": str(disdir) if disdir else None}
