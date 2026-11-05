# SPDX-License-Identifier: MIT
""".kimp text -> Program (format 1; see disasm.py for the listing form and docs/kiln/modding/impact.md for the
language). Two passes per section: lay out the instructions, then resolve labels, variables and natives.

Lines:  [label:] instruction   ; comment
Directives (header):
    .kimp 1
    program <GUID> [id 0x..]           id defaults to FNV-1a(GUID bytes) (what the game computes)
    stack <n>                          default 256
    streams OnCollision, OnHit | none  which event streams the instance allocates
    data_size <n>                      only when the data block is larger than the variables need
    queries 0x.., 0x.. | auto          queryCallIds; auto (the default) = the sites of the query natives
    var $sym <type> [@off | @$other+n] [size n] [config <0x..|Alias|"text">] [name 0x..] [dbg "..."] [= value]
    code: / shutdown:                  sections (shutdown defaults to a single halt)
Instructions: the opcode names (iadd ... halt) in any case, `word 0x..` for raw words, and
    ecall <native>[(a0, a1, ...)] [site 0x..|<name>] [argc n]
"""
import re

from . import ops, values as V
from .catalog import CatalogError
from .program import Program, Variable, fnv1a32, guid_bytes, STREAMS

QUERY_NATIVES_DEFAULT = {0x19143416, 0x93E40AA9, 0xA8787E70, 0x22A62EA4, 0x61946152}


class AsmError(Exception):
    def __init__(self, msg, line=None, src="<kimp>"):
        self.line = line
        super().__init__(f"{src}:{line}: {msg}" if line else f"{src}: {msg}")


def _strip_comment(s):
    out, q = [], False
    i = 0
    while i < len(s):
        c = s[i]
        if c == '"' and (i == 0 or s[i - 1] != "\\"):
            q = not q
        if c == ";" and not q:
            break
        out.append(c)
        i += 1
    return "".join(out).rstrip()


def _split_args(s):
    """'a, b, c' at depth 0 -> [a, b, c]."""
    parts, depth, cur, q = [], 0, [], False
    for c in s:
        if c == '"':
            q = not q
        if not q and c in "([{":
            depth += 1
        if not q and c in ")]}":
            depth -= 1
        if c == "," and depth == 0 and not q:
            parts.append("".join(cur).strip())
            cur = []
        else:
            cur.append(c)
    if "".join(cur).strip():
        parts.append("".join(cur).strip())
    return parts


def _int(tok, what="number"):
    try:
        return int(tok, 0)
    except ValueError:
        raise ValueError(f"{what} expected, got {tok!r}")


_VAR_RX = re.compile(r"^var\s+\$(\S+)\s+(\S+)(.*)$")
_LABEL_RX = re.compile(r"^([A-Za-z_][A-Za-z0-9_.]*):\s*(.*)$")


class _Item:
    __slots__ = ("kind", "line", "op", "text", "args", "pc", "size", "native", "site", "words")

    def __init__(self, kind, line, **kw):
        self.kind, self.line = kind, line
        for k in ("op", "text", "args", "pc", "size", "native", "site", "words"):
            setattr(self, k, kw.get(k))


def assemble(text, cat, src="<kimp>", natives_files=()):
    """Returns a Program. Raises AsmError with the line number."""
    for f in natives_files:
        cat.add_natives_file(f)
    hdr = {"guid": None, "id": None, "stack": 256, "streams": 0, "data_size": None, "queries": "auto"}
    var_lines = []
    sections = {"code": [], "shutdown": []}
    cur = None
    for ln, raw in enumerate(text.splitlines(), 1):
        s = _strip_comment(raw).strip()
        if not s:
            continue
        if s in ("code:", "shutdown:"):
            cur = s[:-1]
            continue
        if cur is None:
            w = s.split(None, 1)
            kw, rest = w[0], (w[1] if len(w) > 1 else "")
            if kw == ".kimp":
                if rest.strip() != "1":
                    raise AsmError(f".kimp {rest}: this assembler reads format 1", ln, src)
            elif kw == "program":
                t = rest.split()
                if not t:
                    raise AsmError("program <GUID> expected", ln, src)
                try:
                    hdr["guid"] = str(t[0]).lower()
                    guid_bytes(hdr["guid"])
                except ValueError:
                    raise AsmError(f"bad GUID {t[0]!r}", ln, src)
                if len(t) >= 3 and t[1] == "id":
                    hdr["id"] = _int(t[2], "id")
            elif kw == "stack":
                hdr["stack"] = _int(rest.strip(), "stack size")
            elif kw == "streams":
                r = rest.strip()
                if r in ("none", "0", ""):
                    hdr["streams"] = 0
                elif r.lower().startswith("0x"):
                    hdr["streams"] = int(r, 16)
                else:
                    m = 0
                    for x in _split_args(r):
                        if x not in STREAMS:
                            raise AsmError(f"stream {x!r} (known: {', '.join(STREAMS)})", ln, src)
                        m |= 1 << STREAMS.index(x)
                    hdr["streams"] = m
            elif kw == "data_size":
                hdr["data_size"] = _int(rest.strip(), "data size")
            elif kw == "queries":
                r = rest.strip()
                hdr["queries"] = "auto" if r == "auto" else ([] if r == "none" else [_int(x, "query id") for x in _split_args(r)])
            elif kw == "var":
                var_lines.append((ln, s))
            else:
                raise AsmError(f"unknown directive {kw!r} (or an instruction before 'code:')", ln, src)
            continue
        # an instruction line in a section
        lab = None
        m = _LABEL_RX.match(s)
        if m and not s.startswith(("ecall", "word")):
            lab, s = m.group(1), m.group(2).strip()
        if lab:
            sections[cur].append(_Item("label", ln, text=lab))
        if s:
            sections[cur].append(_Item("insn", ln, text=s))
    if not hdr["guid"]:
        raise AsmError("no 'program <GUID>' line", None, src)
    p = Program(hdr["guid"], hdr["id"], hdr["stack"], hdr["streams"])
    gb = guid_bytes(p.guid)

    # ---- variables
    syms = {}
    vs = VarState()
    for ln, s in var_lines:
        declare_var(s, ln, syms, p, vs, cat, src)
    data = vs.data
    if hdr["data_size"] is not None:
        if hdr["data_size"] < len(data):
            raise AsmError(f"data_size {hdr['data_size']} < {len(data)} bytes the variables need", None, src)
        data.extend(b"\0" * (hdr["data_size"] - len(data)))
    p.data = bytes(data)

    # ---- code
    for sec in ("code", "shutdown"):
        items = sections[sec]
        if sec == "shutdown" and not items:
            items = [_Item("insn", None, text="halt")]
        p_words = _assemble_section(items, sec, syms, p, cat, gb, src)
        if sec == "code":
            p.code = p_words
        else:
            p.shutdown = p_words
    if hdr["queries"] == "auto":
        qn = {n.hash for n in cat.natives.values() if n.extra.get("query")} or QUERY_NATIVES_DEFAULT
        q = []
        for words in (p.code, p.shutdown):
            for i in ops.decode(words, strict=False):
                if i.code == ops.ECALL and i.args[1] in qn and i.args[0] not in q:
                    q.append(i.args[0])
        p.query_ids = q
    else:
        p.query_ids = list(hdr["queries"])
    return p


class VarState:
    """Data block under construction: bytes, the auto-layout cursor and which bytes initialisers wrote."""

    def __init__(self, data=b"", cursor=None):
        self.data = bytearray(data)
        self.cursor = len(data) if cursor is None else cursor
        self.written = {}


def declare_var(s, ln, syms, p, vs, cat, src="<kimp>"):
    """Parse one `var` line, append the Variable to p (and its bytes to vs.data). Returns its index."""
    init = None
    body = s
    eq = _find_eq(s)
    if eq is not None:
        body, init = s[:eq].rstrip(), s[eq + 1:].strip()
    m = _VAR_RX.match(body)
    if not m:
        raise AsmError("var $name <type> ... expected", ln, src)
    sym, ttok, rest = m.group(1), m.group(2), m.group(3)
    if sym in syms:
        raise AsmError(f"variable ${sym} declared twice", ln, src)
    th = cat.type_hash(ttok)
    ti = cat.type_info(th)
    opts = _opts(rest, ln, src)
    size = opts.get("size")
    if size is None:
        if not ti or ti.get("size") is None:
            raise AsmError(f"type {ttok}: unknown size, give `size n`", ln, src)
        size = ti["size"]
    if "at" in opts:
        off = _resolve_at(opts["at"], syms, p, ln, src)
    else:
        off = (vs.cursor + 15) // 16 * 16
    dbg = opts.get("dbg", sym.split("#")[0] if "#" in sym else sym)
    name = opts["name"] if "name" in opts else fnv1a32(dbg)
    cfg = 0
    if "config" in opts:
        try:
            cfg = cat.config_id(opts["config"])
        except CatalogError as e:
            raise AsmError(str(e), ln, src)
    v = Variable(name, cfg, th, size, off, dbg)
    syms[sym] = len(p.variables)
    p.variables.append(v)
    vs.cursor = max(vs.cursor, off + size)
    if len(vs.data) < off + size:
        vs.data.extend(b"\0" * (off + size - len(vs.data)))
    if init is not None:
        set_initial(p, len(p.variables) - 1, init, vs, cat, ln, src)
    return syms[sym]


def set_initial(p, idx, text, vs, cat, ln=None, src="<kimp>"):
    v = p.variables[idx]
    ti = cat.type_info(v.type)
    q = ti.get("qualified") if ti else None
    try:
        raw = V.encode_value(cat.records, q, V.parse_ast(text))
    except V.ValueError_ as e:
        raise AsmError(f"${v.dbg}: {e}", ln, src)
    if len(raw) != v.size:
        raise AsmError(f"${v.dbg}: value is {len(raw)} bytes, the variable {v.size}", ln, src)
    for j, b in enumerate(raw):
        o = v.offset + j
        if vs is not None:
            if o in vs.written and vs.written[o] != b:
                raise AsmError(f"${v.dbg}: initial value conflicts with an earlier variable at byte {o}", ln, src)
            vs.written[o] = b
            vs.data[o] = b
    return raw


def parse_section_lines(text, first_line=1):
    """Instruction lines of a snippet (no header) -> items for assemble_items."""
    items = []
    for k, raw in enumerate(text.splitlines()):
        ln = first_line + k
        s = _strip_comment(raw).strip()
        if not s:
            continue
        lab = None
        m = _LABEL_RX.match(s)
        if m and not s.startswith(("ecall", "word")):
            lab, s = m.group(1), m.group(2).strip()
        if lab:
            items.append(_Item("label", ln, text=lab))
        if s:
            items.append(_Item("insn", ln, text=s))
    return items


def assemble_snippet(text, syms, p, cat, site_prefix, src="<snippet>"):
    """Assemble code lines in the context of program p's variables (labels local to the snippet, pcs from 0).
    New ECALL sites default to FNV-1a(GUID ‖ "<site_prefix>#n")."""
    return _assemble_section(parse_section_lines(text), site_prefix, syms, p, cat, guid_bytes(p.guid), src)


def _find_eq(s):
    q = False
    for i, c in enumerate(s):
        if c == '"':
            q = not q
        if c == "=" and not q:
            return i
    return None


def _opts(rest, ln, src):
    toks = re.findall(r'"(?:[^"\\]|\\.)*"|\S+', rest)
    o = {}
    i = 0
    while i < len(toks):
        t = toks[i]
        if t.startswith("@"):
            o["at"] = t[1:]
            i += 1
            continue
        if t in ("size", "config", "name", "dbg") and i + 1 < len(toks):
            v = toks[i + 1]
            if t == "size":
                o["size"] = _int(v, "size")
            elif t == "name":
                o["name"] = _int(v, "name hash")
            elif t == "dbg":
                if not (v.startswith('"') and v.endswith('"')):
                    raise AsmError('dbg "text" expected', ln, src)
                o["dbg"] = v[1:-1].replace('\\"', '"').replace("\\\\", "\\")
            else:
                o["config"] = v
            i += 2
            continue
        raise AsmError(f"unexpected {t!r} in var line", ln, src)
    return o


def _resolve_at(at, syms, p, ln, src):
    m = re.match(r"^\$([^+\-]+)([+\-]\d+)?$", at)
    if m:
        if m.group(1) not in syms:
            raise AsmError(f"@${m.group(1)}: no such variable (declare it first)", ln, src)
        base = p.variables[syms[m.group(1)]].offset
        return base + (int(m.group(2)) if m.group(2) else 0)
    return _int(at, "offset")


def _arg_words(tok, syms, ln, src):
    tok = tok.strip()
    if tok in ("_",):
        return [12]
    if tok.startswith("$"):
        nm = tok[1:]
        if nm not in syms:
            raise AsmError(f"no variable {tok}", ln, src)
        return [11, ops.handle(syms[nm])]
    try:
        v = int(tok, 0)
    except ValueError:
        raise AsmError(f"ecall argument {tok!r}: $var, _, or an integer", ln, src)
    if v == 0:
        return [12]
    if v == 1:
        return [13]
    return [11, v & 0xFFFFFFFF]


def _parse_ecall(text, ln, src):
    m = re.match(r"^ecall\s+([^\s(]+)\s*(\((.*)\))?\s*(.*)$", text, re.I)
    if not m:
        raise AsmError("ecall <native>[(args)] [site ..] [argc n]", ln, src)
    name, args, tail = m.group(1), m.group(3), m.group(4)
    site = argc = None
    t = tail.split()
    i = 0
    while i < len(t):
        if t[i] == "site" and i + 1 < len(t):
            site = t[i + 1]
            i += 2
        elif t[i] == "argc" and i + 1 < len(t):
            argc = _int(t[i + 1], "argc")
            i += 2
        else:
            raise AsmError(f"ecall: unexpected {t[i]!r}", ln, src)
    return name, (_split_args(args) if args is not None else None), site, argc


def _assemble_section(items, sec, syms, p, cat, gb, src):
    labels = {}
    pc = 0
    ordinal = 0
    for it in items:
        if it.kind == "label":
            if it.text in labels:
                raise AsmError(f"label {it.text} defined twice", it.line, src)
            labels[it.text] = pc
            continue
        t = it.text
        head = t.split(None, 1)[0].lower()
        if head == "ecall":
            name, args, site, argc = _parse_ecall(t, it.line, src)
            n = cat.native(name)
            h = n.hash if n else (int(name, 16) if name.lower().startswith("0x") else fnv1a32(name))
            if argc is None:
                if n is None:
                    raise AsmError(f"unknown native {name!r}: declare it (impact/natives.json) or add `argc n`",
                                   it.line, src)
                argc = n.argc
            elif n is not None and n.argc != argc:
                raise AsmError(f"{name}: argc {argc} but the catalog says {n.argc}", it.line, src)
            words = []
            if args is not None:
                if len(args) != argc:
                    raise AsmError(f"{name} takes {argc} argument(s), {len(args)} given", it.line, src)
                for a in reversed(args):
                    words += _arg_words(a, syms, it.line, src)
            if site is None:
                sid = fnv1a32(gb + f"{sec}#{ordinal}".encode())
            elif site.lower().startswith("0x") or site.isdigit():
                sid = int(site, 0)
            else:
                sid = fnv1a32(gb + site.encode())
            ordinal += 1
            words += [ops.ECALL, sid, h]
            it.words, it.pc, it.size = words, pc, len(words)
            pc += len(words)
            continue
        if head == "word":
            ws = [_int(x, "word") & 0xFFFFFFFF for x in _split_args(t.split(None, 1)[1] if " " in t else "")]
            it.words, it.pc, it.size = ws, pc, len(ws)
            pc += len(ws)
            continue
        o = ops.op(head)
        if o is None:
            raise AsmError(f"unknown instruction {head!r}", it.line, src)
        it.op, it.pc, it.size = o, pc, 1 + o.nops
        rest = t.split(None, 1)[1] if " " in t else ""
        it.args = _split_args(rest) if rest else []
        if len(it.args) != o.nops:
            raise AsmError(f"{o.name.lower()} takes {o.nops} operand(s)", it.line, src)
        pc += it.size
    words = []
    for it in items:
        if it.kind == "label":
            continue
        if it.words is not None:
            words += it.words
            continue
        o = it.op
        w = [o.code]
        for k, a in enumerate(it.args):
            w.append(_operand(o, k, a, labels, syms, p, it.line, src))
        words += w
    return words


def _operand(o, k, a, labels, syms, p, ln, src):
    c = o.code
    if c in ops.BRANCHES or (c == 18 and k == 0):
        if a.startswith("@"):
            return _int(a[1:], "pc")
        if a not in labels:
            raise AsmError(f"no label {a!r}", ln, src)
        return labels[a]
    if c in (11, 22, 24):
        if a.startswith("$"):
            if a[1:] not in syms:
                raise AsmError(f"no variable {a}", ln, src)
            return ops.handle(syms[a[1:]])
        return _int(a, "value") & 0xFFFFFFFF
    if c == 16:
        if a.startswith("$"):
            if a[1:] not in syms:
                raise AsmError(f"no variable {a}", ln, src)
            return p.variables[syms[a[1:]]].name
        return _int(a, "variable name hash") & 0xFFFFFFFF
    return _int(a, "operand") & 0xFFFFFFFF
