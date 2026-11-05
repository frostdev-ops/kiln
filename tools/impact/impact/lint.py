# SPDX-License-Identifier: MIT
"""The verifier. Errors make a program unsafe to register: kiln.abilities applies the same rules (marked [rt]) before
it inserts a new program into the game's ImpactRegistry. Warnings and infos are advice.

Rules (docs/re/impact.md section 6 has the evidence for each):
  E_ID          [rt] id must be FNV-1a-32(GUID bytes); the game looks programs up by it
  E_SIZE        [rt] code and shutdown hold at most 65535 words (pc, sp and branch targets are u16)
  E_OPCODE      [rt] every opcode is 1..30
  E_TRUNC       [rt] every operand word lies inside its array
  E_TARGET      [rt] a branch target is an instruction start in the same array
  E_FRAME       [rt] CALL/RET/LOAD/STORE are rejected (unused by the 571 vanilla programs, unverified)
  E_FALLOFF     [rt] no path runs past the end (the interpreter has no bounds check)
  E_END         [rt] code and shutdown each end in HALT (every vanilla array does; kiln.abilities requires it)
  E_DEPTH       [rt] the stack depth at each instruction is the same on every path
  E_UNDERFLOW   [rt] no instruction pops more than the stack holds
  E_OVERFLOW    [rt] depth <= 31 after every push (32 slots, slot 31 is the 0xBAADF00D canary -> fatal)
  E_HANDLE      [rt] GLOAD/GSTORE operands are handles 0xFFFF0000|i with i < variable count; ICONST handles too
  E_LOADTYPE    [rt] GLOAD/GSTORE variables are uint32/sint32/float32/bool/Time (the interpreter errors otherwise)
  E_COPY        [rt] COPY operands name existing variables of the same type
  E_NATIVE      [rt] every ECALL hash is a known native (vanilla, Kiln built-in or declared by the mod)
  E_ARG         a native argument is a variable handle out of range, or a raw value where a handle is read
  E_VAR         a variable lies outside the data block
  E_QUERY       a query native's site is missing from queryCallIds (the instance would have no query state)
  E_UNCONNECTED [rt] an argument the native reads with no check is unconnected: it dereferences null and the process
                dies. hasAttackCategory's matchAll took a dedicated server down this way (natives.json: "unchecked")
  W_ARGTYPE     an argument's variable type differs from every type vanilla passes there
  W_UNCONNECTED an argument every vanilla call connects is unconnected: the native returns an error and the program stops
  W_TYPESIZE    a variable's size differs from its type's size
  W_ALIGN       a variable is not aligned to its type
  W_CONFIGTYPE  a config id is bound to another type in vanilla programs
  W_STREAM      onEvent listens to a stream the program does not allocate (usedStreams)
  W_SITE        an ECALL site id is also used by a vanilla program (per-node state could collide)
  W_LOOP        a loop with no rvm and no native call: the interpreter has no step limit, so a loop that never
                ends hangs the server's simulation step (forEach* loops end because their native returns 0)
  I_CONFIG      a config id that no vanilla program uses (the binding must supply it)
  I_UNREACHABLE code no path reaches
"""
import collections

from . import ops
from .program import program_id, STREAMS


class Finding:
    __slots__ = ("level", "code", "section", "pc", "msg")

    def __init__(self, level, code, msg, section=None, pc=None):
        self.level, self.code, self.msg, self.section, self.pc = level, code, msg, section, pc

    def __str__(self):
        loc = f"{self.section}@{self.pc}: " if self.section is not None and self.pc is not None else (
            f"{self.section}: " if self.section else "")
        return f"{self.level} {self.code} {loc}{self.msg}"

    def to_json(self):
        return {"level": self.level, "code": self.code, "section": self.section, "pc": self.pc, "msg": self.msg}


class Report:
    def __init__(self):
        self.findings = []
        self.max_depth = {}
        self.natives = {}     # hash -> (name, argc, origin) of every ECALL target

    def add(self, level, code, msg, section=None, pc=None):
        self.findings.append(Finding(level, code, msg, section, pc))

    @property
    def errors(self):
        return [f for f in self.findings if f.level == "error"]

    @property
    def ok(self):
        return not self.errors

    def to_json(self):
        return {"ok": self.ok, "maxDepth": self.max_depth,
                "errors": len(self.errors), "warnings": sum(f.level == "warning" for f in self.findings),
                "findings": [f.to_json() for f in self.findings],
                "natives": [{"hash": f"0x{h:08x}", "name": n, "argc": a, "origin": o}
                            for h, (n, a, o) in sorted(self.natives.items())]}


def stack_effect(i, cat):
    """(pop, push) of one instruction, or None when it depends on frames (CALL/RET) or an unknown native."""
    o = i.op
    if i.code == ops.ECALL:
        n = cat.native(i.args[1]) if cat else None
        return (n.argc, 1) if n else None
    if o.pop is None:
        return None
    return o.pop, o.push


def successors(i, starts_next):
    o = i.op
    if o.flow == ops.STOP:
        return []
    if o.flow == ops.JUMP:
        return [i.args[0]] if i.code == 8 else [i.args[0]]  # CALL is rejected before this matters
    if o.flow == ops.CJUMP:
        return [i.args[0], starts_next]
    return [starts_next]


def depth_check(insns, cat, rep, sec, words_len):
    """The worklist algorithm shared with kiln.abilities. Returns the max depth (or None on error)."""
    by_pc = {i.pc: k for k, i in enumerate(insns)}
    depth = {}
    work = [(0, 0)] if insns else []
    maxd = 0
    bad = False
    while work:
        pc, d = work.pop()
        if pc >= words_len:
            rep.add("error", "E_FALLOFF", "a path runs past the end of the code", sec, pc)
            bad = True
            continue
        k = by_pc.get(pc)
        if k is None:
            bad = True  # reported as E_TARGET
            continue
        if pc in depth:
            if depth[pc] != d:
                rep.add("error", "E_DEPTH", f"stack depth {d} here on one path, {depth[pc]} on another", sec, pc)
                bad = True
            continue
        depth[pc] = d
        i = insns[k]
        eff = stack_effect(i, cat)
        if eff is None:
            bad = True
            continue
        pop, push = eff
        if d < pop:
            rep.add("error", "E_UNDERFLOW", f"{i.op.name} pops {pop}, the stack holds {d}", sec, pc)
            bad = True
            continue
        nd = d - pop + push
        if nd > ops.MAX_STACK:
            rep.add("error", "E_OVERFLOW", f"stack depth {nd} > {ops.MAX_STACK} (32 slots, the last is the canary)",
                    sec, pc)
            bad = True
            continue
        maxd = max(maxd, nd, d)
        nxt = pc + i.size
        for s in successors(i, nxt):
            work.append((s, nd))
    for k, i in enumerate(insns):
        if i.pc not in depth and not bad:
            if k == len(insns) - 1 and i.code == 30:
                break  # the trailing halt every vanilla program has
            rep.add("info", "I_UNREACHABLE", f"{i.op.name if i.op else 'word'} is never reached", sec, i.pc)
            break
    return None if bad else maxd


def _push_args(insns, k, argc):
    """args[0..argc-1] as push Insns (None where not a plain push)."""
    out, j = [], k - 1
    for _ in range(argc):
        if j < 0 or insns[j].code not in (11, 12, 13):
            out.append(None)
            j = -1
            continue
        out.append(insns[j])
        j -= 1
    return out


def lint(p, cat, allow_frames=False, vanilla_sites=None):
    rep = Report()
    nv = len(p.variables)
    if p.id != program_id(p.guid):
        rep.add("error", "E_ID", f"id {p.id:#010x} != FNV-1a(GUID) {program_id(p.guid):#010x}")
    if p.streams >> len(STREAMS):
        rep.add("error", "E_STREAMS", f"usedStreams {p.streams:#x} has unknown bits")
    if p.stack_size != 256:
        rep.add("info", "I_STACKSIZE", f"stackSize {p.stack_size} (vanilla: 256; the instance stack is 32 slots anyway)")

    # ---- variables
    names = collections.Counter(v.name for v in p.variables)
    for idx, v in enumerate(p.variables):
        where = f"${v.dbg or idx}"
        if v.offset + v.size > len(p.data):
            rep.add("error", "E_VAR", f"{where}: [{v.offset}, {v.offset + v.size}) outside the data ({len(p.data)} B)")
        ti = cat.type_info(v.type)
        if ti is None:
            rep.add("warning", "W_TYPE", f"{where}: type {v.type:#010x} is not in the catalog")
        else:
            if ti.get("size") is not None and ti["size"] != v.size:
                rep.add("warning", "W_TYPESIZE", f"{where}: size {v.size}, {ti['name']} is {ti['size']}")
            al = ti.get("align") or 1
            if v.offset % al:
                rep.add("warning", "W_ALIGN", f"{where}: offset {v.offset} not aligned to {al} ({ti['name']})")
        if v.config:
            c = cat.configs.get(v.config)
            if c is None:
                rep.add("info", "I_CONFIG", f"{where}: config {v.config:#010x} is used by no vanilla program")
            elif ti and c.get("types") and ti["name"] not in c["types"]:
                rep.add("warning", "W_CONFIGTYPE", f"{where}: config {v.config:#010x} ({cat.config_name(v.config)}) "
                        f"is {'/'.join(c['types'])} in vanilla, here {ti['name']}")

    # ---- code
    query_natives = {n.hash for n in cat.natives.values() if n.extra.get("query")}
    sites = collections.defaultdict(set)
    for sec, words in (("code", p.code), ("shutdown", p.shutdown)):
        if len(words) > 0xFFFF:
            rep.add("error", "E_SIZE", f"{len(words)} words: pcs are u16 (at most 65535)", sec)
            rep.max_depth[sec] = None
            continue
        try:
            insns = ops.decode(words)
        except ops.DecodeError as e:
            msg = str(e)
            rep.add("error", "E_TRUNC" if "operand" in msg else "E_OPCODE", msg, sec)
            rep.max_depth[sec] = None
            continue
        if not insns:
            rep.add("error", "E_FALLOFF", "empty code (it must end in halt)", sec, 0)
            rep.max_depth[sec] = None
            continue
        if insns[-1].code != 30:
            rep.add("error", "E_END", f"the last instruction is {insns[-1].op.name if insns[-1].op else 'a raw word'}, "
                    "not halt (add a trailing halt)", sec, insns[-1].pc)
        starts = {i.pc for i in insns}
        frame_bad = False
        for k, i in enumerate(insns):
            c = i.code
            if c in ops.FRAME_OPS and not allow_frames:
                rep.add("error", "E_FRAME", f"{i.op.name} is not allowed (frames are unverified; vanilla never uses them)",
                        sec, i.pc)
                frame_bad = True
            if c in ops.BRANCHES or c == 18:
                if i.args[0] not in starts:
                    rep.add("error", "E_TARGET", f"{i.op.name} target {i.args[0]} is not an instruction start", sec, i.pc)
            if c == 11 and ops.is_handle(i.args[0]) and (i.args[0] & 0xFFFF) >= nv:
                rep.add("error", "E_HANDLE", f"handle {i.args[0]:#x}: no variable {i.args[0] & 0xFFFF}", sec, i.pc)
            if c in ops.HANDLE_OPS:
                a = i.args[0]
                if not ops.is_handle(a) or (a & 0xFFFF) >= nv:
                    rep.add("error", "E_HANDLE", f"{i.op.name} {a:#x} is not a variable handle", sec, i.pc)
                else:
                    t = p.variables[a & 0xFFFF].type
                    if t not in ops.LOADABLE_TYPES:
                        rep.add("error", "E_LOADTYPE", f"{i.op.name} ${p.variables[a & 0xFFFF].dbg}: type "
                                f"{cat.type_name(t) or hex(t)} is not uint32/sint32/float32/bool/Time", sec, i.pc)
            if c == 16:
                src = [v for v in p.variables if v.name == i.args[0]]
                dst = [v for v in p.variables if v.name == i.args[1]]
                if not src or not dst:
                    rep.add("error", "E_COPY", f"COPY {i.args[0]:#x} -> {i.args[1]:#x}: no such variable name", sec, i.pc)
                elif src[0].type != dst[0].type:
                    rep.add("error", "E_COPY", "COPY between different types", sec, i.pc)
                elif names[i.args[0]] > 1 or names[i.args[1]] > 1:
                    rep.add("warning", "W_COPYNAME", "COPY finds the first variable with that name; it is not unique",
                            sec, i.pc)
            if c == ops.ECALL:
                site, h = i.args
                n = cat.native(h)
                sites[site].add(h)
                if n is None:
                    rep.add("error", "E_NATIVE", f"native {h:#010x} is not known (declare it in impact/natives.json)",
                            sec, i.pc)
                    continue
                rep.natives[h] = (n.name, n.argc, n.origin)
                _check_args(p, insns, k, n, cat, rep, sec)
                if h == 0x45DBF538:  # onEvent(eventStream, eventType)
                    _check_stream(p, insns, k, rep, sec)
        if frame_bad:
            rep.max_depth[sec] = None
            continue
        if any(f.code in ("E_TARGET",) and f.section == sec for f in rep.findings):
            rep.max_depth[sec] = None
            continue
        if allow_frames and any(i.code in ops.FRAME_OPS for i in insns):
            rep.max_depth[sec] = None
            rep.add("warning", "W_FRAME", "frames present: stack depth not verified", sec)
            continue
        rep.max_depth[sec] = depth_check(insns, cat, rep, sec, len(words))
        _check_loops(insns, rep, sec)

    for site, hs in sites.items():
        if hs & query_natives and site not in p.query_ids:
            rep.add("error", "E_QUERY", f"site {site:#010x} calls a query native but is not in queryCallIds")
        if vanilla_sites and site in vanilla_sites and vanilla_sites[site] != p.guid:
            rep.add("warning", "W_SITE", f"site {site:#010x} is also used by vanilla program {vanilla_sites[site]}")
    for q in p.query_ids:
        if q not in sites:
            rep.add("warning", "W_QUERY", f"queryCallIds has {q:#010x}, no ECALL uses it")
    return rep


def _check_args(p, insns, k, n, cat, rep, sec):
    nv = len(p.variables)
    for ai, a in enumerate(_push_args(insns, k, n.argc)):
        if a is None:
            continue
        par = n.param(ai)
        if a.code == 12 and par:
            if par.unchecked:
                rep.add("error", "E_UNCONNECTED", f"{n.name} args[{ai}] ({par.name}) is unconnected: the native reads it with no check, "
                        "so this kills the process. Connect a variable", sec, a.pc)
            elif par.always:
                rep.add("warning", "W_UNCONNECTED", f"{n.name} args[{ai}] ({par.name}) is unconnected, and every vanilla call connects it",
                        sec, a.pc)
            continue
        raw_ok = bool(par and par.raw)
        if a.code == 13 or (a.code == 11 and not ops.is_handle(a.args[0])):
            if not raw_ok:
                val = 1 if a.code == 13 else a.args[0]
                rep.add("error", "E_ARG", f"{n.name} args[{ai}] ({par.name if par else '?'}) gets the raw value {val}: "
                        f"natives read arguments as variable handles (it would use variable {val & 0xFFFF})",
                        sec, a.pc)
            continue
        if a.code == 11:
            idx = a.args[0] & 0xFFFF
            if idx >= nv:
                continue  # E_HANDLE already
            v = p.variables[idx]
            tn = cat.type_name(v.type)
            if par and par.types and tn and tn not in par.types and "any" not in par.types:
                rep.add("warning", "W_ARGTYPE", f"{n.name} args[{ai}] ({par.name}): ${v.dbg} is {tn}, vanilla passes "
                        f"{'/'.join(par.types[:4])}", sec, a.pc)


def _check_stream(p, insns, k, rep, sec):
    args = _push_args(insns, k, 2)
    a = args[1] if len(args) > 1 else None
    if a is None or a.code != 11 or not ops.is_handle(a.args[0]) or (a.args[0] & 0xFFFF) >= len(p.variables):
        return
    v = p.variables[a.args[0] & 0xFFFF]
    if v.offset < len(p.data) and not v.config:
        t = p.data[v.offset]
        if t < 3 and not (p.streams >> t) & 1:
            rep.add("warning", "W_STREAM", f"onEvent listens to {STREAMS[t]} but usedStreams lacks it", sec, a.pc)


def _check_loops(insns, rep, sec):
    """Strongly connected components of the control-flow graph that contain neither RVM nor ECALL."""
    by_pc = {i.pc: i for i in insns}
    succ = {}
    for i in insns:
        if i.op is None:
            continue
        succ[i.pc] = [t for t in successors(i, i.pc + i.size) if t in by_pc]
    index, low, on, stack, out = {}, {}, set(), [], []
    counter = [0]

    def strong(v):
        # iterative Tarjan
        work = [(v, 0)]
        index[v] = low[v] = counter[0]
        counter[0] += 1
        stack.append(v)
        on.add(v)
        while work:
            node, k = work[-1]
            nxt = succ.get(node, [])
            if k < len(nxt):
                work[-1] = (node, k + 1)
                w = nxt[k]
                if w not in index:
                    index[w] = low[w] = counter[0]
                    counter[0] += 1
                    stack.append(w)
                    on.add(w)
                    work.append((w, 0))
                elif w in on:
                    low[node] = min(low[node], index[w])
                continue
            work.pop()
            if work:
                low[work[-1][0]] = min(low[work[-1][0]], low[node])
            if low[node] == index[node]:
                comp = []
                while True:
                    x = stack.pop()
                    on.discard(x)
                    comp.append(x)
                    if x == node:
                        break
                out.append(comp)

    for pc in succ:
        if pc not in index:
            strong(pc)
    for comp in out:
        cyc = len(comp) > 1 or comp[0] in succ.get(comp[0], [])
        if cyc and not any(by_pc[x].code in (28, ops.ECALL) for x in comp):
            rep.add("warning", "W_LOOP", f"a loop at pc {min(comp)}..{max(comp)} has no rvm and no native call: "
                    "make sure it ends (the interpreter has no step limit)", sec, min(comp))
