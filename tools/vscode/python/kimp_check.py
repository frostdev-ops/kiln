# SPDX-License-Identifier: MIT
"""Checks one .kimp / .kpatch buffer with tools/impact and prints JSON for the VS Code extension.

    python3 kimp_check.py --impact <tools/impact dir> --path <file> [--natives F.json ...] < source

Output (stdout, one object):
    {"ok": bool, "kind": "kimp"|"kpatch",
     "error": {"line": n|null, "message": "..."} | null,          # assembler error (stops at the first one)
     "findings": [{"level", "code", "section", "pc", "line", "msg"}],   # lint findings, mapped back to source lines
     "maxDepth": {...}, "program": {"guid", "id"} | null}

A .kimp is assembled and linted like `impact.py asm` does. A .kpatch (code inserted into a clone) has no program of its
own: its lines are assembled against any variable name (unknown instructions, natives, argc and labels are checked;
variables are checked when the cook applies the patch).
The pc -> line map comes from the assembler's own items (their pc and size), so a finding points at the line that
produced the instruction (an ecall line covers its argument pushes too).
"""
import argparse
import json
import re
import sys
from pathlib import Path


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--impact", required=True, help="the tools/impact folder (holds the impact package)")
    ap.add_argument("--path", required=True, help="the file the buffer belongs to (its suffix picks kimp/kpatch)")
    ap.add_argument("--natives", action="append", default=[])
    a = ap.parse_args()
    sys.path.insert(0, str(Path(a.impact).resolve()))
    from impact import asm, catalog, lint as L, program as P  # noqa: E402

    text = sys.stdin.read()
    kind = "kpatch" if a.path.endswith(".kpatch") else "kimp"
    out = {"ok": False, "kind": kind, "error": None, "findings": [], "maxDepth": {}, "program": None}
    try:
        cat = catalog.Catalog.default(extra_natives=[f for f in a.natives if Path(f).is_file()])
    except Exception as e:  # a broken natives.json is the most likely cause
        out["error"] = {"line": None, "message": f"natives: {e}"}
        print(json.dumps(out))
        return 0

    lines = {}  # section -> [(pc, size, line)]
    orig = asm._assemble_section

    def capture(items, sec, *rest, **kw):
        words = orig(items, sec, *rest, **kw)
        lines[sec] = [(it.pc, it.size, it.line) for it in items
                      if it.kind == "insn" and it.pc is not None and it.line is not None]
        return words

    asm._assemble_section = capture
    try:
        if kind == "kimp":
            p = asm.assemble(text, cat, src=Path(a.path).name)
            rep = L.lint(p, cat)
            out["program"] = {"guid": p.guid, "id": f"{p.id:#010x}"}
            out["maxDepth"] = rep.max_depth
            var_lines = {m.group(1): k for k, raw in enumerate(text.splitlines(), 1)
                         if (m := VAR_RX.match(raw))}
            for f in rep.findings:
                j = f.to_json()
                j["line"] = line_of(lines.get(f.section, []), f.pc)
                if j["line"] is None and (m := VAR_REF_RX.match(f.msg or "")):
                    j["line"] = var_lines.get(m.group(1))   # "$name: ..." findings point at the var line
                out["findings"].append(j)
            out["ok"] = rep.ok
        else:
            check_patch(text, cat, asm, P, Path(a.path).name)
            out["ok"] = True
    except asm.AsmError as e:
        msg = str(e)
        prefix = f"{Path(a.path).name}:{e.line}: " if e.line else f"{Path(a.path).name}: "
        out["error"] = {"line": e.line, "message": msg[len(prefix):] if msg.startswith(prefix) else msg}
    except Exception as e:  # codec or catalog errors: one message, no traceback
        out["error"] = {"line": None, "message": f"{type(e).__name__}: {e}"}
    print(json.dumps(out))
    return 0


VAR_RX = re.compile(r"^\s*var\s+\$(\S+)")
VAR_REF_RX = re.compile(r"^\$([^\s:]+):")


def line_of(spans, pc):
    if pc is None:
        return None
    for start, size, ln in spans:
        if start <= pc < start + max(size or 1, 1):
            return ln
    return None


class _AnySyms(dict):
    """Every $name exists (index 0): a patch's variables belong to the program it is inserted into."""

    def __contains__(self, k):
        return True

    def __getitem__(self, k):
        return 0


class _Var:
    name = 0
    offset = 0


class _Prog:
    variables = [_Var()]
    guid = "00000000-0000-0000-0000-000000000000"


def check_patch(text, cat, asm, P, src):
    items = asm.parse_section_lines(text)
    # a label on the last line means "the code after the insert": give it a target
    items.append(asm._Item("insn", None, text="halt"))
    asm._assemble_section(items, "patch", _AnySyms(), _Prog(), cat, b"\0" * 16, src)


if __name__ == "__main__":
    sys.exit(main())
