# SPDX-License-Identifier: MIT
"""Program -> .kimp text (format 1). The listing re-assembles to the same bytes (asm.py); `impact.py roundtrip`
checks that for every vanilla program.

    .kimp 1
    program 80a2448f-945c-45f6-8fce-9b09ed61620c         ; id 0x7a120ee8 = FNV-1a(GUID)
    stack 256
    streams OnCollision

    ; variables: $symbol type @offset [size n] [config id] [name 0x..] [dbg "..."] [= initial value]
    var $WaitTime::value               Time        @544  config 0xe21b4b72 name 0x1b2c3d4e = 250ms ; WaitTime

    code:
            gload $WaitTime::value
            ltime
            iadd
    L4:     dup
            ...
            ecall getTransform($impact_nodes.GetTransform::transform, _, $impact_nodes.GetSelf::entityId) site 0xfcce4e0a

    shutdown:
            halt

`ecall name(a0, a1, ...)` lists the arguments in args[] order: a0 is pushed LAST (it is args[0], the top of stack).
`$var` = ICONST handle, `_` = ICONST0 (an unconnected input), `1` = ICONST1, other integers = ICONST n.
"""
import re

from . import FORMAT, ops, values as V
from .program import fnv1a32, program_id

SAFE = re.compile(r"^[A-Za-z0-9_.:]+$")


def symbols(p):
    """$symbol for each variable: the dbgName when it is unique and plain, else dbgName#index (or v<index>)."""
    seen = {}
    for v in p.variables:
        seen[v.dbg] = seen.get(v.dbg, 0) + 1
    out = []
    for i, v in enumerate(p.variables):
        if v.dbg and SAFE.match(v.dbg) and seen[v.dbg] == 1 and "#" not in v.dbg:
            out.append(v.dbg)
        elif v.dbg and SAFE.match(v.dbg):
            out.append(f"{v.dbg}#{i}")
        else:
            out.append(f"v{i}")
    return out


def _labels(insns):
    t = set()
    for i in insns:
        if i.code in ops.BRANCHES or i.code == 18:
            t.add(i.args[0])
    return t


def _push_arg(i, syms, nvars):
    if i.code == 12:
        return "_"
    if i.code == 13:
        return "1"
    if i.code == 11:
        a = i.args[0]
        if ops.is_handle(a) and (a & 0xFFFF) < nvars:
            return "$" + syms[a & 0xFFFF]
        return None  # a raw ICONST is not folded (it would read back as ICONST0/ICONST1/handle forms)
    return None


def _operand(i, syms, nvars, cat, labels):
    o = i.op
    c = i.code
    if c in ops.BRANCHES:
        return f"L{i.args[0]}" if i.args[0] in labels else f"@{i.args[0]}"
    if c == 18:
        return f"L{i.args[0]}, {i.args[1]}"
    if c in (11, 22, 24):
        a = i.args[0]
        if ops.is_handle(a) and (a & 0xFFFF) < nvars:
            return "$" + syms[a & 0xFFFF]
        return f"{a:#x}" if a > 9 else str(a)
    if c == 16:
        return ", ".join(f"{a:#010x}" for a in i.args)
    if c in (21, 23):
        a = i.args[0]
        return str(a - (1 << 32) if a & 0x80000000 else a)
    return ""


def native_token(cat, h):
    n = cat.native(h) if cat else None
    return n.name if n else f"{h:#010x}"


def disassemble(p, cat=None, fold=True, comments=True):
    syms = symbols(p)
    nv = len(p.variables)
    L = [f".kimp {FORMAT}", f"program {p.guid}" + ("" if p.id == program_id(p.guid) else f" id {p.id:#010x}")]
    if comments:
        L[-1] += f"    ; id {p.id:#010x}"
    L.append(f"stack {p.stack_size}")
    L.append("streams " + (", ".join(p.stream_names) if p.streams else "none"))
    if len(p.data) != p.data_extent():
        L.append(f"data_size {len(p.data)}")
    if p.query_ids:
        L.append("queries " + ", ".join(f"{q:#010x}" for q in p.query_ids))
    L.append("")
    if p.variables:
        L.append("; variables: $symbol type @offset [size n] [config id] [name hash] [dbg \"...\"] [= initial value]")
    db = cat.records if cat else None
    covered = bytearray(len(p.data))
    w = max([len(s) for s in syms] + [8])
    for i, v in enumerate(p.variables):
        ti = cat.type_info(v.type) if cat else None
        tname = ti["name"] if ti and fnv1a32(ti["name"]) == v.type else f"{v.type:#010x}"
        parts = [f"var ${syms[i]:<{w}}", f"{tname:<24}", f"@{v.offset:<5}"]
        if not ti or ti.get("size") != v.size:
            parts.append(f"size {v.size}")
        if v.config:
            parts.append(f"config {v.config:#010x}")
        parts.append(f"name {v.name:#010x}")
        sym_dbg = syms[i].split("#")[0] if "#" in syms[i] else syms[i]
        if sym_dbg != v.dbg:
            parts.append('dbg "' + v.dbg.replace("\\", "\\\\").replace('"', '\\"') + '"')
        raw = p.data[v.offset:v.offset + v.size]
        need = any(raw[j] and not covered[v.offset + j] for j in range(len(raw)))
        if need:
            txt = V.exact_text(db, ti.get("qualified") if ti else None, raw) if (ti and len(raw) == ti.get("size")) else None
            parts.append("= " + (txt if txt is not None else "hex:" + raw.hex()))
            for j in range(len(raw)):
                covered[v.offset + j] = 1
        line = " ".join(parts)
        if comments and v.config and cat:
            nm = cat.config_name(v.config)
            if nm:
                line += f"    ; {nm}"
        L.append(line.rstrip())
    for sec, words in (("code", p.code), ("shutdown", p.shutdown)):
        L.append("")
        L.append(f"{sec}:")
        try:
            insns = ops.decode(words)
        except ops.DecodeError:
            insns = ops.decode(words, strict=False)
        labels = _labels(insns)
        k = 0
        out = []
        while k < len(insns):
            i = insns[k]
            o = i.op
            lab = f"L{i.pc}:" if i.pc in labels else ""
            if o is None:
                out.append((lab, f"word {i.code:#x}" + "".join(f", {a:#x}" for a in i.args), ""))
                k += 1
                continue
            if i.code == ops.ECALL:
                site, h = i.args
                n = cat.native(h) if cat else None
                tok = native_token(cat, h)
                cm = ""
                if comments and n and n.origin != "vanilla":
                    cm = f"; {n.origin}"
                out.append((lab, f"ecall {tok} site {site:#010x}", cm))
                k += 1
                continue
            out.append((lab, o.name.lower() + (" " + _operand(i, syms, nv, cat, labels) if o.nops else ""), ""))
            k += 1
        # fold pushes into ecall name(args)
        if fold and cat:
            out = _fold(insns, out, cat, syms, nv, labels)
        for lab, txt, cm in out:
            line = f"{lab:<8}{txt}"
            if cm:
                line += "    " + cm
            L.append(line.rstrip())
    return "\n".join(L) + "\n"


def _fold(insns, out, cat, syms, nv, labels):
    res = []
    pending = []  # indexes into out that are foldable pushes, in order
    for idx, i in enumerate(insns):
        if i.code == ops.ECALL:
            n = cat.native(i.args[1])
            argc = n.argc if n else None
            if argc is not None and argc <= len(pending):
                take = pending[len(pending) - argc:] if argc else []
                # no label inside the push run except on its first push
                ok = all(insns[j].pc not in labels for j in take[1:])
                if ok and all(_push_arg(insns[j], syms, nv) is not None for j in take):
                    args = [_push_arg(insns[j], syms, nv) for j in reversed(take)]
                    first_lab = out[take[0]][0] if take else out[idx][0]
                    keep = pending[:len(pending) - argc]
                    # emit the pushes before the run unchanged
                    for j in keep:
                        res.append(out[j])
                    lab_e = out[idx][0]
                    if take and lab_e:
                        ok = False
                    if ok:
                        site, h = i.args
                        txt = f"ecall {native_token(cat, h)}(" + ", ".join(args) + f") site {site:#010x}"
                        res.append((first_lab, txt, out[idx][2]))
                        pending = []
                        continue
                    for j in take:
                        res.append(out[j])
                    pending = []
                    res.append(out[idx])
                    continue
            for j in pending:
                res.append(out[j])
            pending = []
            res.append(out[idx])
            continue
        if i.code in (11, 12, 13) and _push_arg(i, syms, nv) is not None:
            if i.pc in labels and pending:
                for j in pending:
                    res.append(out[j])
                pending = []
            pending.append(idx)
            continue
        for j in pending:
            res.append(out[j])
        pending = []
        res.append(out[idx])
    for j in pending:
        res.append(out[j])
    return res
