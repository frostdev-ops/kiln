# SPDX-License-Identifier: MIT
"""Read-only access to the installed game: programs (tools/kfc), the impact type names in the exe's reflection
records, the native registration table, and the survey that builds tools/impact/data from evidence.

Nothing here writes into the game folder. Outputs go to $KILN_OUT (default <repo>/.out/impact).
"""
import collections
import json
import os
import re
import struct
from pathlib import Path

from . import ops, program as P

DEFAULT_GAME = Path.home() / ".local/share/Steam/steamapps/common/Enshrouded"
DEFAULT_SERVER = Path.home() / "srv/enshrouded/lab2"


def out_dir(sub=""):
    base = Path(os.environ.get("KILN_OUT") or (Path(__file__).resolve().parents[3] / ".out/impact"))
    p = base / sub if sub else base
    p.mkdir(parents=True, exist_ok=True)
    return p


def exe_in(game):
    g = Path(game).expanduser()
    for n in ("enshrouded.exe", "enshrouded_server.exe"):
        if (g / n).exists():
            return g / n
    raise FileNotFoundError(f"no enshrouded(.exe|_server.exe) in {g}; pass --exe")


class Game:
    """The installed game, read only."""

    def __init__(self, game=DEFAULT_GAME, exe=None):
        os.environ.setdefault("KILN_OUT", str(out_dir()))
        from kfc.archive import Kfc
        from kfc.refl import TypeDb
        self.dir = Path(game).expanduser()
        self.exe = Path(exe).expanduser() if exe else exe_in(self.dir)
        self.kfc = Kfc(self.dir)
        self.types = TypeDb.from_exe(self.exe)
        probs = P.layout_check(self.types)
        if probs:
            raise RuntimeError("this game build's ImpactProgram layout differs from tools/impact's codec: "
                               + "; ".join(probs))

    @property
    def version(self):
        return self.kfc.version

    def program_keys(self):
        return self.kfc.resources_of_type(P.TYPE_HASH)

    def has_program(self, guid):
        from kfc.archive import ResourceKey
        return self.kfc.has(ResourceKey(guid, P.TYPE_HASH, 0))

    def program_bytes(self, guid):
        from kfc.archive import ResourceKey
        key = ResourceKey(guid, P.TYPE_HASH, 0)
        if not self.kfc.has(key):
            raise KeyError(f"{guid} is not a keen::ImpactProgram in {self.kfc.path}")
        return self.kfc.read(key)

    def program(self, guid):
        return P.decode(self.program_bytes(guid))

    def programs(self):
        for key in self.program_keys():
            raw = self.kfc.read(key)
            yield key.guid, raw, P.decode(raw)

    def resource_bytes(self, guid, type_name, part=0):
        from kfc.archive import ResourceKey
        t = self.types.get(type_name)
        if t is None:
            raise KeyError(f"unknown type {type_name}")
        return self.kfc.read(ResourceKey(guid, t["hash"], part))


# ---------------------------------------------------------------- reflection: impact names
def impact_names(types, exe):
    """{FNV-1a(impact name): (impact name, qualified name)} from each reflection record's +0x10/+0x18 slice."""
    from kfc.pe import PeImage
    pe = PeImage(exe)
    out = {}
    for t in types.dump["types"]:
        p, n = struct.unpack_from("<QQ", pe.img, t["rva"] + 0x10)
        if not p or not 0 < n < 512:
            continue
        s = pe.text_at(p, n)
        if s:
            out.setdefault(P.fnv1a32(s), (s, t["name"]))
    # records without an impact name are found by their qualified name (keen::impact::KnowledgeIdContainer)
    for t in types.dump["types"]:
        out.setdefault(t["hash"], (t["name"], t["name"]))
    return out


# ---------------------------------------------------------------- native registrations
_MOV_EDX = re.compile(rb"\xBA(....)", re.S)
_ANCHORS = (b"applyDamage", b"spawnEntity", b"applyBuff")


def _rip_leas(img, va, vsz, target):
    """RVAs of `lea r64, [rip + disp32]` instructions in .text that point at `target`."""
    out = []
    for m in re.finditer(rb"[\x48\x4C]\x8D[\x05\x0D\x15\x1D\x25\x2D\x35\x3D]", img[va:va + vsz]):
        o = va + m.start()
        disp = struct.unpack_from("<i", img, o + 3)[0]
        if o + 7 + disp == target:
            out.append(o)
    return out


def scan_natives(exe):
    """The world setup's registerNative calls: [{site, hash, fn, name, argc}] (RVAs). registerNative is found from
    anchors: the first call after a `lea` of the native name "applyDamage" (and two more) is registerNative."""
    import capstone
    from kfc.pe import PeImage
    pe = PeImage(exe)
    va, vsz = pe.sections[".text"]
    img = pe.img
    md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)
    votes = collections.Counter()
    for a in _ANCHORS:
        for m in re.finditer(re.escape(a) + rb"(?![A-Za-z0-9_])", img):
            for lea in _rip_leas(img, va, vsz, m.start()):
                for ins in md.disasm(img[lea:lea + 96], pe.base + lea):
                    if ins.mnemonic == "call" and ins.op_str.startswith("0x"):
                        votes[int(ins.op_str, 16) - pe.base] += 1
                        break
    if not votes:
        return {"registerNative": None, "sha256": pe.sha256, "sites": []}
    reg_native = votes.most_common(1)[0][0]
    hits = []
    for m in re.finditer(rb"\xE8", img[va:va + vsz]):
        o = va + m.start()
        if o + 5 + struct.unpack_from("<i", img, o + 1)[0] == reg_native:
            hits.append(o)
    if not hits:
        return {"registerNative": reg_native, "sha256": pe.sha256, "sites": []}
    lo, hi = min(hits) - 0x80, max(hits) + 5
    regs, imms, out, cur = {}, {}, [], {}
    for ins in md.disasm(img[lo:hi], pe.base + lo):
        a = ins.address - pe.base
        mn, opnd = ins.mnemonic, ins.op_str
        mm = re.match(r"lea (\w+), \[rip ([+-]) (0x[0-9a-f]+)\]", f"{mn} {opnd}")
        if mm:
            tgt = ins.address + ins.size + (int(mm.group(3), 16) * (1 if mm.group(2) == "+" else -1)) - pe.base
            regs[mm.group(1)] = tgt
            if mm.group(1) == "r8":
                cur["fn"] = tgt
            continue
        mi = re.match(r"(\w+), (0x[0-9a-f]+|\d+)$", opnd) if mn == "mov" else None
        if mi and not opnd.startswith(("byte", "qword", "dword", "word")):
            imms[_reg64(mi.group(1))] = int(mi.group(2), 0)
        if mn == "xor":
            r1, _, r2 = opnd.partition(", ")
            if r1 == r2:
                imms[_reg64(r1)] = 0
        if mn == "xor" and opnd == "r8d, r8d":
            cur["fn"] = 0
        elif mn == "mov" and opnd.startswith("edx, 0x"):
            cur["hash"] = int(opnd.split(", ")[1], 16)
        elif mn == "mov" and opnd.startswith("byte ptr [rsp + 0x20], "):
            v = opnd.split(", ")[1]
            cur["argc"] = int(v, 0) if v[0].isdigit() else imms.get(_reg64(v))
        elif mn == "mov" and opnd.startswith("qword ptr [rsp + 0x38], "):
            v = opnd.split(", ")[1]
            cur["len"] = int(v, 0) if v[0].isdigit() else imms.get(_reg64(v))
        elif mn == "mov" and opnd.startswith("qword ptr [rsp + 0x30], "):
            r = opnd.split(", ")[1]
            if r in regs:
                cur["name_at"] = regs[r]
        elif mn == "call" and opnd.startswith("0x") and int(opnd, 16) - pe.base == reg_native:
            name = None
            if "name_at" in cur and "len" in cur:
                name = img[cur["name_at"]:cur["name_at"] + cur["len"]].decode("utf-8", "replace")
            out.append({"site": a, "hash": cur.get("hash"), "fn": cur.get("fn"), "argc": cur.get("argc"),
                        "name": name})
            keep = {k: cur[k] for k in ("name_at", "len") if k in cur}
            cur = keep
    return {"registerNative": reg_native, "sha256": pe.sha256, "sites": out}


def _reg64(r):
    """r12b / r12d / ecx / al -> the 64-bit register name (for tracking immediates)."""
    m = re.match(r"^r(\d+)[bwd]?$", r)
    if m:
        return "r" + m.group(1)
    base = {"al": "rax", "bl": "rbx", "cl": "rcx", "dl": "rdx", "sil": "rsi", "dil": "rdi"}
    if r in base:
        return base[r]
    if len(r) == 3 and r[0] == "e":
        return "r" + r[1:]
    return r


# ---------------------------------------------------------------- evidence for signatures
def call_sites(p):
    """For each ECALL in code and shutdown: (section, insn index, Insn, [arg Insn or None] (args[0] first), next Insn).
    An arg is the push instruction that produced it when the argc values right before the ECALL are plain pushes."""
    out = []
    for sec, words in (("code", p.code), ("shutdown", p.shutdown)):
        ins = ops.decode(words)
        for k, i in enumerate(ins):
            if i.code != ops.ECALL:
                continue
            out.append((sec, k, i, ins))
    return out


def node_of(dbg):
    head = dbg.split("::")[0]
    if head.startswith("impact_nodes."):
        head = head[len("impact_nodes."):]
    return head


def pin_of(dbg):
    return dbg.split("::")[-1] if "::" in dbg else dbg
