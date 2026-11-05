# SPDX-License-Identifier: MIT
"""impact.py: Kiln's toolchain for Enshrouded Impact programs (keen::ImpactProgram).

    impact.py dis   <GUID|file.bin|file.kimp> [--raw] [--bare] [-o out.kimp]   disassemble (a GUID: from the game)
    impact.py asm   <file.kimp> [-o out.bin] [--natives F.json ...]             assemble + lint
    impact.py lint  <GUID|file.bin|file.kimp> [--json] [--allow-frames] [--natives F ...] [--vanilla-sites]
    impact.py clone <GUID|file.bin> --guid NEW [--edits E.json] [--keep-sites] [-o out.bin|out.kimp]
    impact.py natives [RX] [--show NAME] [--json]                               the native catalog
    impact.py hash  TEXT... | --guid GUID                                       FNV-1a-32 (natives, configs, types; program id)
    impact.py cook  <mod>/impact/cook.json [--update-manifest] [--check]       build a mod's programs (kiln.assets)
    impact.py roundtrip [--limit N]          every vanilla program: dis -> asm byte-identical, codec, lint (local check)
    impact.py survey [--out DIR]             rebuild the catalogs from the installed game (default $KILN_OUT/impact-data)
    impact.py vectors [--out DIR]            write the verifier test vectors (synthetic) for kiln.abilities

Common: --game DIR (default Steam's Enshrouded client), --exe EXE (default the game's exe; read only),
--server DIR (for the native table; default ~/srv/enshrouded/lab2). Outputs never go into the game folder; defaults
are under $KILN_OUT (<repo>/.out/impact).
"""
import argparse
import json
import re
import sys
from pathlib import Path

from . import asm, catalog, disasm, game as G, lint as L, patch, program as P, ops

GUID_RX = re.compile(r"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$")


class Ctx:
    def __init__(self, a):
        self.a = a
        self._g = None
        self.cat = catalog.Catalog.default(extra_natives=getattr(a, "natives", None) or ())

    @property
    def game(self):
        if self._g is None:
            self._g = G.Game(self.a.game, self.a.exe)
        return self._g

    def load(self, ref):
        """A Program from a GUID (the game), a .bin blob, or a .kimp source."""
        if GUID_RX.match(ref):
            return self.game.program(ref)
        p = Path(ref)
        if p.suffix == ".kimp":
            return asm.assemble(p.read_text(), self.cat, src=str(p))
        return P.decode(p.read_bytes())


def _print_lint(rep, as_json=False, quiet_info=True):
    if as_json:
        print(json.dumps(rep.to_json(), indent=1))
        return
    for f in rep.findings:
        if quiet_info and f.level == "info":
            continue
        print(f"  {f}")
    md = ", ".join(f"{k} {v}" for k, v in rep.max_depth.items())
    print(f"lint: {'ok' if rep.ok else 'FAILED'} ({len(rep.errors)} error(s), "
          f"{sum(f.level == 'warning' for f in rep.findings)} warning(s); max stack depth {md}; limit {ops.MAX_STACK})")


def cmd_dis(c):
    p = c.load(c.a.ref)
    txt = disasm.disassemble(p, c.cat, fold=not c.a.raw, comments=not c.a.bare)
    if c.a.o:
        Path(c.a.o).write_text(txt)
        print(c.a.o)
    else:
        sys.stdout.write(txt)


def cmd_asm(c):
    src = Path(c.a.src)
    p = asm.assemble(src.read_text(), c.cat, src=str(src))
    rep = L.lint(p, c.cat, allow_frames=c.a.allow_frames)
    blob = P.encode(p)
    out = Path(c.a.o) if c.a.o else G.out_dir("bin") / (src.stem + ".bin")
    if rep.ok or c.a.force:
        out.parent.mkdir(parents=True, exist_ok=True)
        out.write_bytes(blob)
        print(f"{out}: {len(blob)} bytes, program {p.guid} id {p.id:#010x}")
    _print_lint(rep)
    return 0 if rep.ok else 1


def cmd_lint(c):
    p = c.load(c.a.ref)
    vs = None
    if c.a.vanilla_sites:
        vs = vanilla_site_index(c.game)
    rep = L.lint(p, c.cat, allow_frames=c.a.allow_frames, vanilla_sites=vs)
    _print_lint(rep, c.a.json, quiet_info=not c.a.verbose)
    return 0 if rep.ok else 1


def vanilla_site_index(g):
    idx = {}
    for guid, raw, p in g.programs():
        for words in (p.code, p.shutdown):
            for i in ops.decode(words):
                if i.code == ops.ECALL:
                    idx.setdefault(i.args[0], guid)
    return idx


def cmd_clone(c):
    base = c.load(c.a.ref)
    edits = json.loads(Path(c.a.edits).read_text()) if c.a.edits else []
    if isinstance(edits, dict):
        edits = edits.get("edits", [])
    q = patch.clone(base, c.cat, c.a.guid, edits, keep_sites=c.a.keep_sites,
                    base_dir=Path(c.a.edits).parent if c.a.edits else None)
    rep = L.lint(q, c.cat)
    out = Path(c.a.o) if c.a.o else G.out_dir("bin") / f"{q.guid}.bin"
    out.parent.mkdir(parents=True, exist_ok=True)
    if out.suffix == ".kimp":
        out.write_text(disasm.disassemble(q, c.cat))
    else:
        out.write_bytes(P.encode(q))
    print(f"{out}: clone of {base.guid} as {q.guid} (id {q.id:#010x})")
    _print_lint(rep)
    return 0 if rep.ok else 1


def cmd_natives(c):
    ns = sorted(c.cat.natives.values(), key=lambda n: (n.origin != "vanilla", n.name.lower()))
    if c.a.show:
        n = c.cat.native(c.a.show)
        if not n:
            print(f"unknown native {c.a.show}")
            return 1
        print(f"{n.name}  node {n.node}  hash {n.hash:#010x}  argc {n.argc}  ({n.origin})")
        if n.returns:
            print(f"  returns: {n.returns}")
        if n.suspends:
            print(f"  suspends: {n.suspends}")
        if n.doc:
            print(f"  {n.doc}")
        for i, a in enumerate(n.args):
            print(f"  args[{i:2d}] {a.dir:3s} {a.name:28s} {'/'.join(a.types[:4]) or '-'}"
                  + (" (raw ok)" if a.raw else "") + (f"  {a.doc}" if a.doc else ""))
        return 0
    rx = re.compile(c.a.rx or ".", re.I)
    for n in ns:
        if rx.search(n.name) or rx.search(n.node):
            sig = ", ".join(f"{a.name}" + ("*" if a.dir == "out" else "") for a in n.args)
            print(f"{n.hash:#010x} {n.origin:8s} {n.name}({sig})")
    if c.a.json:
        print(json.dumps([{"hash": f"{n.hash:#010x}", "name": n.name, "argc": n.argc} for n in ns]))


def cmd_hash(c):
    if c.a.guid:
        print(f"{P.program_id(c.a.guid):#010x}  program id of {c.a.guid}")
    for t in c.a.text:
        print(f"{P.fnv1a32(t):#010x}  {t}")


def cmd_roundtrip(c):
    from . import roundtrip
    r = roundtrip.run(c.game, c.cat, limit=c.a.limit)
    out = G.out_dir() / "roundtrip.json"
    out.write_text(json.dumps(r, indent=1) + "\n")
    print(json.dumps(r["summary"], indent=1))
    print(f"report: {out}")
    return 0 if r["summary"]["ok"] else 1


def cmd_survey(c):
    from . import survey
    srv = Path(c.a.server).expanduser()
    sexe = srv / "enshrouded_server.exe" if (srv / "enshrouded_server.exe").exists() else None
    n, t, cf = survey.survey(c.game, exe_for_natives=sexe or c.game.exe, other_exe=c.game.exe if sexe else None)
    out = Path(c.a.out) if c.a.out else G.out_dir("impact-data")
    out.mkdir(parents=True, exist_ok=True)
    (out / "natives.json").write_text(json.dumps(n, indent=1) + "\n")
    (out / "types.json").write_text(json.dumps(t, separators=(",", ":")) + "\n")
    (out / "configs.json").write_text(json.dumps(cf, indent=1) + "\n")
    print(f"{out}: {len(n['natives'])} natives, {len(t['impact'])} types, {len(cf['configs'])} config ids")


def cmd_cook(c):
    from . import cook
    if c.a.check:
        need = cook.needs_cook(c.a.recipe)
        print("cook needed" if need else "up to date")
        return 1 if need else 0
    r = cook.run(c.a.recipe, c.game, update_manifest=c.a.update_manifest)
    for s in r["steps"]:
        print(f"step {s['step']} {s['op']}: {s.get('guid')} -> {s.get('file')} {'ok' if s['ok'] else 'FAILED'}")
        for f in s.get("lint", {}).get("findings", []):
            if f["level"] != "info":
                print(f"    {f['level']} {f['code']} {f['msg']}")
        if s.get("error"):
            print(f"    {s['error']}")
    m = r["manifest"]
    if m["missing"] or m["mismatched"]:
        print("assets.json does not match the cooked files (run with --update-manifest):")
        for e in m["missing"] + m["mismatched"]:
            print("  " + json.dumps(e))
    print(f"{'ok' if r['ok'] else 'FAILED'}: {len(r['outputs'])} program(s), report {r['report']}")
    return 0 if r["ok"] else 1


def cmd_vectors(c):
    from . import vectors
    out = Path(c.a.out) if c.a.out else Path(__file__).resolve().parent.parent / "tests" / "vectors"
    idx = vectors.write(out, c.cat)
    print(f"{out}: {len(idx)} vectors")


def main(argv=None):
    ap = argparse.ArgumentParser(prog="impact.py", description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--game", default=str(G.DEFAULT_GAME))
    ap.add_argument("--exe")
    ap.add_argument("--server", default=str(G.DEFAULT_SERVER))
    ap.add_argument("--natives", action="append", help="extra native declarations (a mod's impact/natives.json)")
    sub = ap.add_subparsers(dest="cmd", required=True)
    p = sub.add_parser("dis")
    p.add_argument("ref")
    p.add_argument("--raw", action="store_true", help="no ecall(args) folding")
    p.add_argument("--bare", action="store_true", help="no comments")
    p.add_argument("-o")
    p = sub.add_parser("asm")
    p.add_argument("src")
    p.add_argument("-o")
    p.add_argument("--allow-frames", action="store_true")
    p.add_argument("--force", action="store_true", help="write the blob even when lint fails")
    p = sub.add_parser("lint")
    p.add_argument("ref")
    p.add_argument("--json", action="store_true")
    p.add_argument("--verbose", "-v", action="store_true", help="show infos too")
    p.add_argument("--allow-frames", action="store_true")
    p.add_argument("--vanilla-sites", action="store_true", help="warn about site ids that vanilla programs use")
    p = sub.add_parser("clone")
    p.add_argument("ref")
    p.add_argument("--guid", required=True)
    p.add_argument("--edits")
    p.add_argument("--keep-sites", action="store_true")
    p.add_argument("-o")
    p = sub.add_parser("natives")
    p.add_argument("rx", nargs="?")
    p.add_argument("--show")
    p.add_argument("--json", action="store_true")
    p = sub.add_parser("hash")
    p.add_argument("text", nargs="*")
    p.add_argument("--guid")
    p = sub.add_parser("cook")
    p.add_argument("recipe")
    p.add_argument("--update-manifest", action="store_true")
    p.add_argument("--check", action="store_true")
    p = sub.add_parser("roundtrip")
    p.add_argument("--limit", type=int, default=0)
    p = sub.add_parser("survey")
    p.add_argument("--out")
    p = sub.add_parser("vectors")
    p.add_argument("--out")
    a = ap.parse_args(argv)
    c = Ctx(a)
    fn = {"dis": cmd_dis, "asm": cmd_asm, "lint": cmd_lint, "clone": cmd_clone, "natives": cmd_natives,
          "hash": cmd_hash, "cook": cmd_cook, "roundtrip": cmd_roundtrip, "survey": cmd_survey,
          "vectors": cmd_vectors}[a.cmd]
    try:
        return fn(c) or 0
    except (asm.AsmError, patch.PatchError, P.CodecError, catalog.CatalogError, KeyError, OSError) as e:
        print(f"impact: {e}", file=sys.stderr)
        return 2
    except Exception as e:  # cook/recipe errors and the like: one line, not a traceback
        from . import cook
        if isinstance(e, cook.RecipeError):
            print(f"impact: {e}", file=sys.stderr)
            return 2
        raise
