#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""The program catalog: every ECS program the game exe registers, read from the exe file (never run), structure only.

    python3 tools/typegen/catalog.py dump <server|client> <exe> [--out sigdb/programs]
        writes sigdb/programs/<side>/<sha256>.json
    python3 tools/typegen/catalog.py check [file...]
        checks catalogs (default: every file under sigdb/programs/): the allowed keys and shapes only, unique names,
        every contract recomputed; a server catalog of build 26430253 holds 817 programs, 197 of them client programs
        (flag 0x1, no update in the server exe), as the lab's directory row "find" counts them

How a dump finds the programs: world/RegisterProgram resolves from sigdb (tools/sigport); every call (E8) and tail
jump (E9) to it in .text is a registration; the descriptor is the rdx of that call (a `lea rdx,[rip+X]` before it, or
the value a `lea rax,[rip+X]; ret` getter returned into rdx). The descriptor layout is kiln.world's EngineDesc
(native/services/world/world_internal.h): name {char*, u64} at 0, update at 0x10, 21 name lists {ptr, count} from
0x18, the three masks, flags at 0x180, interval at 0x184.

What a catalog holds (the agreed exception for 06a, like sigdb/types/: structure only): program and list entry names,
flags, interval, whether the program has an update in this exe, the three masks, and the contract. Never an address,
an RVA, a call site, default-value bytes or anything read from the game's data files.

The contract (chapter 06a section 9.7; kiln.world checks a live descriptor the same way): FNV-1a-32 over the UTF-8
text: for each of the 21 lists in descriptor order, `<list>=` + its names in descriptor order joined by `,` + `\\n`;
then `sys_read_opt=%016x\\n`, `sys_write_opt=%016x\\n`, `sys_write_parallel=%016x\\n`, `flags=%08x\\n`,
`interval=%u\\n`.
"""
import hashlib
import json
import os
import re
import struct
import sys

sys.dont_write_bytecode = True
HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.normpath(os.path.join(HERE, "..", ".."))
OUT = os.path.join(ROOT, "sigdb", "programs")

# The descriptor's name lists, in descriptor order, with their offsets (EngineDesc).
LISTS = [("tpl", 0x18), ("tpl_opt", 0x28), ("read", 0x38), ("read_opt", 0x48), ("write", 0x58), ("write_opt", 0x68),
         ("add", 0x78), ("remove", 0x88), ("sys_read", 0x98), ("sys_write", 0xB0), ("other_tpl", 0xD0),
         ("other_read", 0xE0), ("other_write", 0xF0), ("other_write_unser", 0x100), ("with", 0x110),
         ("without", 0x120), ("any_of", 0x130), ("all_write", 0x140), ("all_read", 0x150), ("events_in", 0x160),
         ("events_out", 0x170)]
MASKS = [("sys_read_opt", 0xA8), ("sys_write_opt", 0xC0), ("sys_write_parallel", 0xC8)]
FLAGS, INTERVAL = 0x180, 0x184
CLIENT_FLAG = 0x1

PROGRAM_KEYS = {"name", "flags", "interval", "update", "lists", "sys_read_opt", "sys_write_opt", "sys_write_parallel",
                "contract"}
TOP_KEYS = {"comment", "format", "side", "exe_sha256", "programs"}
NAME = re.compile(r"^[A-Za-z][A-Za-z0-9_]{0,63}$")
ENTRY = re.compile(r"^[A-Za-z_][A-Za-z0-9_]*(::[A-Za-z_][A-Za-z0-9_]*)*$")  # a component, event or singleton name
MASK = re.compile(r"^0x[0-9a-f]{16}$")
HASH = re.compile(r"^0x[0-9a-f]{8}$")
# Counts the lab's directory measured on these builds (06a lab row "find"); a check of their catalogs compares them.
KNOWN = {"f0d824be7fcd48ebfe2458ff5c28da26060c2074e3ec1fcac587e5a1d508aeb2": (817, 197),
         "5d9ef2f140b8ed49e30dbee655841b89d08baec20f799a1817e171a18a850c77": (817, 0)}


def fnv1a32(text):
    h = 0x811C9DC5
    for b in text.encode("utf-8"):
        h = ((h ^ b) * 0x01000193) & 0xFFFFFFFF
    return h


def contract_text(p):
    lists = p.get("lists", {})
    s = "".join(f"{n}={','.join(lists.get(n, []))}\n" for n, _ in LISTS)
    for n, _ in MASKS:
        s += f"{n}={int(p[n], 16):016x}\n"
    return s + f"flags={p['flags']:08x}\ninterval={p['interval']}\n"


def contract(p):
    return f"0x{fnv1a32(contract_text(p)):08x}"


# ---- dump ----

def resolve_register_program(exe, side):
    sys.path.insert(0, os.path.join(ROOT, "tools", "sigport"))
    from peimg import Image  # noqa: E402
    from sides import merged  # noqa: E402
    from sigdb_check import Resolver  # noqa: E402
    img = Image(exe)
    doc = merged(os.path.join(ROOT, "sigdb", "symbols", f"{side}.json"))
    for name in [n for n, d in doc.get("symbols", {}).items() if isinstance(d, dict) and "sig" in d and "find" not in d]:
        doc.setdefault("sigs", {})[name] = doc["symbols"].pop(name)["sig"]
    syms, _, _ = Resolver(img, doc).run()
    r = syms.get("world/RegisterProgram", {})
    if not r.get("ok"):
        sys.exit(f"catalog: world/RegisterProgram does not resolve in {exe}: {r.get('error', 'no definition')}")
    return img, r["rva"]


def registrations(img, target):
    """(site rva, how) of every E8 call and E9 jump to target in .text."""
    t, tva = img.text, img.text_va
    out = []
    for op in (b"\xe8", b"\xe9"):
        i = t.find(op)
        while i != -1:
            if i + 5 <= len(t) and tva + i + 5 + struct.unpack_from("<i", t, i + 1)[0] == target:
                out.append((tva + i, "call" if op == b"\xe8" else "jmp"))
            i = t.find(op, i + 1)
    return sorted(out)


def descriptor_at(img, site):
    """The rva of the descriptor a registration passes in rdx, or None."""
    import capstone
    md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)
    base, b = img.base, img.img
    for back in range(5, 48):
        start = site - back
        ins = list(md.disasm(b[start:site + 5], base + start))
        if not ins or ins[-1].address != base + site:
            continue
        found = None
        for k, x in enumerate(ins[:-1]):
            if x.mnemonic == "lea" and x.op_str.startswith("rdx, [rip"):
                found = x.address + x.size + struct.unpack_from("<i", b, x.address - base + x.size - 4)[0] - base
            if x.mnemonic == "call" and k + 1 < len(ins) and ins[k + 1].mnemonic == "mov" and ins[k + 1].op_str == "rdx, rax":
                try:
                    tgt = int(x.op_str, 16) - base
                except ValueError:
                    continue
                g = list(md.disasm(b[tgt:tgt + 16], base + tgt))
                if len(g) >= 2 and g[0].mnemonic == "lea" and g[0].op_str.startswith("rax, [rip") and g[1].mnemonic == "ret":
                    found = g[0].address + g[0].size + struct.unpack_from("<i", b, tgt + g[0].size - 4)[0] - base
        if found:
            return found
    return None


def read_descriptor(img, d):
    b, base = img.img, img.base

    def u64(rva):
        return struct.unpack_from("<Q", b, rva)[0]

    def u32(rva):
        return struct.unpack_from("<I", b, rva)[0]

    def text(ptr, n):
        if not ptr or n > 256:
            raise ValueError("a name out of bounds")
        s = b[ptr - base:ptr - base + n].decode("ascii")
        return s

    def names(at):
        p, n = u64(at), u64(at + 8)
        if not p or not n:
            return []
        if n > 256:
            raise ValueError("a list out of bounds")
        return [text(u64(p - base + 16 * k), u64(p - base + 16 * k + 8)) for k in range(n)]

    p = {"name": text(u64(d), u64(d + 8)), "flags": u32(d + FLAGS), "interval": u32(d + INTERVAL),
         "update": u64(d + 0x10) != 0}
    lists = {}
    for n, off in LISTS:
        v = names(d + off)
        if v:
            lists[n] = v
    p["lists"] = lists
    for n, off in MASKS:
        p[n] = f"0x{u64(d + off):016x}"
    p["contract"] = contract(p)
    return p


def dump(side, exe, out):
    if side not in ("server", "client"):
        sys.exit("catalog: side is server or client")
    sha = hashlib.sha256(open(exe, "rb").read()).hexdigest()
    img, reg = resolve_register_program(exe, side)
    progs, bad = [], []
    for site, how in registrations(img, reg):
        d = descriptor_at(img, site)
        if d is None:
            bad.append(f"no descriptor found for the {how} at a registration site")
            continue
        try:
            progs.append(read_descriptor(img, d))
        except (ValueError, UnicodeDecodeError, struct.error) as e:
            bad.append(f"a descriptor could not be read: {e}")
    if bad:
        sys.exit("catalog: " + "; ".join(bad))
    seen = {}
    for p in progs:
        if p["name"] in seen and seen[p["name"]] != p:
            sys.exit(f"catalog: two descriptors named {p['name']}")
        seen[p["name"]] = p
    progs = sorted(seen.values(), key=lambda p: p["name"])
    doc = {"comment": "ECS program catalog read from the exe by tools/typegen/catalog.py: structure only (names, flags, "
                      "interval, whether an update exists, the access lists' entry names, masks, contract); no addresses. "
                      "docs/kiln/typegen.md section 4.5; sigdb/builds/README.md.",
           "format": 1, "side": side, "exe_sha256": sha, "programs": progs}
    errs = check_doc(doc, f"{side}/{sha}.json")
    if errs:
        sys.exit("catalog: the dump fails its own check: " + "; ".join(errs[:10]))
    os.makedirs(os.path.join(out, side), exist_ok=True)
    path = os.path.join(out, side, f"{sha}.json")
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        f.write(json.dumps(doc, indent=1) + "\n")
    print(f"catalog: {path}: {len(progs)} programs, {sum(1 for p in progs if p['flags'] & CLIENT_FLAG)} with flag 0x1, "
          f"{sum(1 for p in progs if not p['update'])} with no update")
    return 0


# ---- check ----

def check_doc(doc, where):
    e = []
    if not isinstance(doc, dict) or set(doc) != TOP_KEYS:
        return [f"{where}: the keys are {sorted(TOP_KEYS)}"]
    if doc["format"] != 1 or doc["side"] not in ("server", "client") or not re.match(r"^[0-9a-f]{64}$", str(doc["exe_sha256"])):
        e.append(f"{where}: format 1, a side, an exe_sha256")
    if not isinstance(doc["comment"], str):
        e.append(f"{where}: comment is text")
    progs = doc["programs"]
    if not isinstance(progs, list):
        return e + [f"{where}: programs is a list"]
    names = []
    for i, p in enumerate(progs):
        at = f"{where}: programs[{i}]"
        if not isinstance(p, dict) or set(p) != PROGRAM_KEYS:
            e.append(f"{at}: the keys are {sorted(PROGRAM_KEYS)}")
            continue
        if not isinstance(p["name"], str) or not NAME.match(p["name"]):
            e.append(f"{at}: name {p['name']!r} is not a program name")
            continue
        at = f"{where}: {p['name']}"
        names.append(p["name"])
        if type(p["flags"]) is not int or not 0 <= p["flags"] < 2**32 or type(p["interval"]) is not int or not 0 <= p["interval"] < 2**32:
            e.append(f"{at}: flags and interval are integers")
            continue
        if type(p["update"]) is not bool:
            e.append(f"{at}: update is true or false")
        if not all(isinstance(p[n], str) and MASK.match(p[n]) for n, _ in MASKS):
            e.append(f"{at}: masks are 0x + 16 lower-case hex digits")
            continue
        lists = p["lists"]
        if not isinstance(lists, dict) or set(lists) - {n for n, _ in LISTS}:
            e.append(f"{at}: lists names only the descriptor's 21 lists")
            continue
        order = [n for n, _ in LISTS if n in lists]
        if list(lists) != order:
            e.append(f"{at}: lists in descriptor order")
        for n, v in lists.items():
            if not isinstance(v, list) or not v or not all(isinstance(x, str) and ENTRY.match(x) and len(x) <= 120 for x in v):
                e.append(f"{at}.{n}: a non-empty list of names")
        if not isinstance(p["contract"], str) or not HASH.match(p["contract"]) or p["contract"] != contract(p):
            e.append(f"{at}: the contract is not FNV-1a-32 of the contract text")
    if names != sorted(names) or len(set(names)) != len(names):
        e.append(f"{where}: programs sorted by name, each once")
    known = KNOWN.get(doc.get("exe_sha256"))
    if known and not e:
        n, client = known
        got_client = sum(1 for p in progs if not p["update"])
        if len(progs) != n or (doc["side"] == "server" and (got_client != client or
                               got_client != sum(1 for p in progs if p["flags"] & CLIENT_FLAG))):
            e.append(f"{where}: {len(progs)} programs, {got_client} with no update; the lab's directory counted {n} "
                     f"and {client}")
    return e


def check(paths):
    if not paths:
        paths = sorted(os.path.join(dp, f) for dp, _, fs in os.walk(OUT) for f in fs if f.endswith(".json"))
    errs, by_sha = [], {}
    for path in paths:
        where = os.path.relpath(path, ROOT) if os.path.isabs(path) else path
        try:
            with open(path, "rb") as f:
                raw = f.read()
            doc = json.loads(raw.decode("utf-8"))
        except (OSError, ValueError) as ex:
            errs.append(f"{where}: not readable JSON: {ex}")
            continue
        e = check_doc(doc, where)
        if not e and os.path.basename(path) != f"{doc['exe_sha256']}.json":
            e.append(f"{where}: the file is named after its exe_sha256")
        if not e and os.path.basename(os.path.dirname(path)) != doc["side"]:
            e.append(f"{where}: the folder is its side")
        if not e and raw.decode("utf-8") != json.dumps(doc, indent=1) + "\n":
            e.append(f"{where}: not written as the tool writes it (indent 1, a final newline)")
        errs += e
        if not e:
            print(f"catalog: {where}: {len(doc['programs'])} programs, "
                  f"{sum(1 for p in doc['programs'] if p['flags'] & CLIENT_FLAG)} with flag 0x1, "
                  f"{sum(1 for p in doc['programs'] if not p['update'])} with no update: ok")
    for e in errs:
        print(f"catalog: FAIL {e}")
    return 1 if errs else 0


def main(argv):
    if len(argv) >= 3 and argv[0] == "dump":
        out = OUT
        if "--out" in argv:
            out = argv[argv.index("--out") + 1]
        return dump(argv[1], argv[2], out)
    if argv and argv[0] == "check":
        return check(argv[1:])
    print(__doc__.split("\n\n")[1], file=sys.stderr)
    return 2


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
