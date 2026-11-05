# SPDX-License-Identifier: MIT
"""Clone a program under a new GUID and edit it. The edits work on instructions with symbolic branch targets, so
inserting or removing code relocates every jump.

Edits (a JSON list; the same in cook.json `clone` steps and `impact.py clone --edits`):
    {"set": "$NoiseRange::value", "value": "40.0"}          initial value (any .kimp value text)
    {"config": "$NoiseRange::value", "id": "0x..|Alias|\\"text\\"|0"}   rebind (0 = unbind)
    {"var": "var $tag uint32 = 7"}                           add a variable (a .kimp var line)
    {"swap": "triggerNoise", "with": "impactsample.Chain", "nth": 0|"all"}  same-argc native swap
    {"insert": "<code lines>" | {"file": "x.kpatch"}, "before"|"after": <anchor>}
    {"replace": <anchor>, "code": "<code lines>" | {"file": ...}}           the whole call statement
    {"remove": <anchor>}                                     a call statement whose result is popped
    {"streams": "OnCollision, OnHit"} / {"stack": 256}
Anchors: {"native": "getSelf", "nth": 0, "section": "code"} (a call statement: its argument pushes, the ECALL and a
following POP) or {"pc": 13, "section": "code"} (the instruction at that pc of the base program) or
{"label": "start"|"end", "section": "code"}.
Site ids: a clone gets fresh ones, FNV-1a(new GUID ‖ old site as 4 LE bytes), so apply/revert pairs stay paired and
vanilla per-node state cannot collide; queryCallIds follow. keep_sites=True keeps them.
"""
import json
import struct
from pathlib import Path

from . import asm, disasm, ops
from .program import fnv1a32, guid_bytes, guid_text, program_id, STREAMS


class PatchError(Exception):
    pass


class Node:
    __slots__ = ("code", "args", "target", "base_pc")

    def __init__(self, code, args, base_pc=None):
        self.code, self.args, self.target, self.base_pc = code, list(args), None, base_pc


END = Node(0, ())  # a snippet label after its last instruction: "the code that follows the insert"


def _nodes(words, base=True):
    insns = ops.decode(words)
    by_pc = {}
    nodes = []
    for i in insns:
        n = Node(i.code, i.args, i.pc if base else None)
        by_pc[i.pc] = n
        nodes.append(n)
    for n in nodes:
        if n.code in ops.BRANCHES or n.code == 18:
            t = by_pc.get(n.args[0])
            if t is None and not base and n.args[0] == len(words):
                t = END
            if t is None:
                raise PatchError(f"branch to {n.args[0]} is not an instruction start")
            n.target = t
    return nodes


def _link_end(new, follower):
    for n in new:
        if n.target is END:
            if follower is None:
                raise PatchError("a label at the end of the inserted code, but nothing follows it")
            n.target = follower


def _words(nodes):
    pc = 0
    at = {}
    for n in nodes:
        at[id(n)] = pc
        pc += 1 + len(n.args)
    out = []
    for n in nodes:
        a = list(n.args)
        if n.target is not None:
            if id(n.target) not in at:
                raise PatchError("a branch targets removed code")
            a[0] = at[id(n.target)]
        out.append(n.code)
        out.extend(a)
    return out


class Editor:
    def __init__(self, prog, cat, new_guid=None, keep_sites=False, base_dir=None):
        self.cat = cat
        self.p = prog.copy()
        self.base_dir = Path(base_dir) if base_dir else None
        self.sec = {"code": _nodes(self.p.code), "shutdown": _nodes(self.p.shutdown)}
        self.syms = {s: i for i, s in enumerate(disasm.symbols(self.p))}
        self.vs = asm.VarState(self.p.data)
        self.vs.cursor = len(self.p.data)
        self.patches = 0
        if new_guid:
            old = guid_bytes(self.p.guid)
            self.p.guid = guid_text(guid_bytes(new_guid))
            self.p.id = program_id(self.p.guid)
            if not keep_sites:
                gb = guid_bytes(self.p.guid)
                remap = lambda s: fnv1a32(gb + struct.pack("<I", s))
                for nodes in self.sec.values():
                    for n in nodes:
                        if n.code == ops.ECALL:
                            n.args[0] = remap(n.args[0])
                self.p.query_ids = [remap(q) for q in self.p.query_ids]

    # ---- helpers
    def _var(self, tok):
        t = tok[1:] if tok.startswith("$") else tok
        if t in self.syms:
            return self.syms[t]
        for i, v in enumerate(self.p.variables):
            if v.dbg == t:
                return i
        raise PatchError(f"no variable {tok}")

    def _calls(self, section, native):
        n = self.cat.native(native)
        if n is None:
            raise PatchError(f"unknown native {native!r}")
        nodes = self.sec[section]
        return n, [k for k, x in enumerate(nodes) if x.code == ops.ECALL and x.args[1] == n.hash]

    def _statement(self, anchor):
        """(section, first index, last index inclusive) of the anchored call statement / instruction."""
        section = anchor.get("section", "code")
        nodes = self.sec[section]
        if "native" in anchor:
            n, ks = self._calls(section, anchor["native"])
            nth = anchor.get("nth", 0)
            if not ks or nth >= len(ks):
                raise PatchError(f"{anchor['native']}: call #{nth} not found in {section} ({len(ks)} calls)")
            k = ks[nth]
            first = k
            for _ in range(n.argc):
                if first - 1 >= 0 and nodes[first - 1].code in (11, 12, 13):
                    first -= 1
                else:
                    break
            last = k
            if k + 1 < len(nodes) and nodes[k + 1].code == 27:
                last = k + 1
            return section, first, last
        if "pc" in anchor:
            for k, x in enumerate(nodes):
                if x.base_pc == anchor["pc"]:
                    return section, k, k
            raise PatchError(f"pc {anchor['pc']}: no instruction of the base program starts there")
        if "label" in anchor:
            if anchor["label"] == "start":
                return section, 0, -1
            if anchor["label"] == "end":
                # before the trailing halt
                k = len(nodes) - 1
                return section, k, k - 1
        raise PatchError(f"bad anchor {anchor!r}")

    def _snippet(self, code, section):
        if isinstance(code, dict) and "file" in code:
            f = Path(code["file"])
            if self.base_dir and not f.is_absolute():
                f = self.base_dir / f
            code = f.read_text()
        self.patches += 1
        words = asm.assemble_snippet(code, self.syms, self.p, self.cat, f"patch{self.patches}:{section}",
                                     src=f"patch #{self.patches}")
        return _nodes(words, base=False)

    def _retarget(self, section, old_nodes, new_first):
        """Branches into removed/replaced nodes now go to new_first."""
        ids = {id(n) for n in old_nodes}
        for nodes in (self.sec[section],):
            for n in nodes:
                if n.target is not None and id(n.target) in ids:
                    n.target = new_first

    # ---- edits
    def apply(self, e):
        if "set" in e:
            i = self._var(e["set"])
            asm.set_initial(self.p, i, str(e["value"]), self.vs, self.cat)
            self.vs.written.clear()
        elif "config" in e:
            i = self._var(e["config"])
            tok = str(e.get("id", "0"))
            self.p.variables[i].config = 0 if tok in ("0", "none") else self.cat.config_id(tok)
        elif "var" in e:
            line = e["var"].strip()
            if not line.startswith("var "):
                line = "var " + line
            asm.declare_var(line, None, self.syms, self.p, self.vs, self.cat, "edit")
        elif "swap" in e:
            section = e.get("section", "code")
            n_old, ks = self._calls(section, e["swap"])
            n_new = self.cat.native(e["with"])
            if n_new is None:
                raise PatchError(f"unknown native {e['with']!r} (declare it in the mod's impact/natives.json)")
            if n_new.argc != n_old.argc:
                raise PatchError(f"swap {n_old.name} (argc {n_old.argc}) -> {n_new.name} (argc {n_new.argc}): "
                                 "argc must match (use replace)")
            nth = e.get("nth", "all")
            pick = ks if nth == "all" else [ks[nth]] if nth < len(ks) else []
            if not pick:
                raise PatchError(f"{n_old.name}: call #{nth} not found")
            for k in pick:
                self.sec[section][k].args[1] = n_new.hash
        elif "insert" in e:
            anchor = e.get("before") or e.get("after")
            if not anchor:
                raise PatchError("insert needs before/after")
            section, first, last = self._statement(anchor)
            new = self._snippet(e["insert"], section)
            nodes = self.sec[section]
            at = first if "before" in e else last + 1
            if "before" in e and new:
                # jumps to the anchored statement now enter the inserted code first
                target = nodes[first] if first < len(nodes) else None
                if target is not None:
                    for n in nodes:
                        if n.target is target:
                            n.target = new[0]
            nodes[at:at] = new
            _link_end(new, nodes[at + len(new)] if at + len(new) < len(nodes) else None)
        elif "replace" in e:
            section, first, last = self._statement(e["replace"])
            new = self._snippet(e["code"], section)
            nodes = self.sec[section]
            old = nodes[first:last + 1]
            if not new:
                raise PatchError("replace with empty code: use remove")
            self._retarget(section, old, new[0])
            nodes[first:last + 1] = new
            k = first + len(new)
            _link_end(new, nodes[k] if k < len(nodes) else None)
        elif "remove" in e:
            section, first, last = self._statement(e["remove"])
            nodes = self.sec[section]
            if nodes[last].code != 27:
                raise PatchError("remove: the call's result is used (no POP after it); use replace")
            old = nodes[first:last + 1]
            if last + 1 >= len(nodes):
                raise PatchError("remove: nothing follows the statement")
            self._retarget(section, old, nodes[last + 1])
            del nodes[first:last + 1]
        elif "streams" in e:
            m = 0
            for x in [s.strip() for s in str(e["streams"]).split(",") if s.strip() and s.strip() != "none"]:
                if x not in STREAMS:
                    raise PatchError(f"stream {x!r}")
                m |= 1 << STREAMS.index(x)
            self.p.streams = m
        elif "stack" in e:
            self.p.stack_size = int(e["stack"])
        else:
            raise PatchError(f"unknown edit {e!r}")

    def result(self):
        self.p.code = _words(self.sec["code"])
        self.p.shutdown = _words(self.sec["shutdown"])
        self.p.data = bytes(self.vs.data)
        # queryCallIds: keep the base's (remapped) entries and add sites of new query calls
        qn = {n.hash for n in self.cat.natives.values() if n.extra.get("query")}
        for nodes in self.sec.values():
            for n in nodes:
                if n.code == ops.ECALL and n.args[1] in qn and n.args[0] not in self.p.query_ids:
                    self.p.query_ids.append(n.args[0])
        return self.p


def clone(prog, cat, new_guid, edits=(), keep_sites=False, base_dir=None):
    ed = Editor(prog, cat, new_guid, keep_sites, base_dir)
    for k, e in enumerate(edits):
        try:
            ed.apply(e)
        except (PatchError, asm.AsmError) as ex:
            raise PatchError(f"edit #{k} {json.dumps(e)[:80]}: {ex}")
    return ed.result()
