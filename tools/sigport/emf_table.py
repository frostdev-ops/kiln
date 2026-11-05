"""Extract EMF 0.3.3's signature table from emf_core.dll and test it against server builds."""
import json
import re
import struct
import sys
from pathlib import Path

from peimg import Image

EMF = str(Path(__file__).resolve().parents[2] / "re/inputs/emf_core-0.3.3.dll")
FIRST = 0x18029DB50  # entry 0 (SimStep)
STRIDE = 0x38


def extract(path=EMF):
    im = Image(path)
    base = im.base

    def q(va):
        return struct.unpack_from("<Q", im.img, va - base)[0]

    def d(va):
        return struct.unpack_from("<I", im.img, va - base)[0]

    def cstr(va):
        o = va - base
        return im.img[o:im.img.index(b"\0", o)].decode()

    out = []
    va = FIRST
    while True:
        idx, name_p, pat_p = q(va), q(va + 8), q(va + 16)
        if idx != len(out) or not (base <= name_p < base + len(im.img)):
            break
        out.append({"index": idx, "name": cstr(name_p), "pattern": cstr(pat_p),
                    "client_1269579": d(va + 24), "client_1273043": d(va + 28),
                    "server_1273043": d(va + 32), "flags": d(va + 36), "extra": d(va + 40)})
        va += STRIDE
    return out


SHIFT = 0x1C00  # world struct growth (program table stride 0x1F20 -> 0x1F28, 0x380 slots)
LO, HI = 0x746300, 0x1D00000  # displacements inside the world struct past the program table


def rebase(pattern):
    """Shift little-endian disp32 constants that point past the program table by SHIFT."""
    toks = pattern.split()
    out = list(toks)
    for i in range(len(toks) - 3):
        if any(t.startswith("?") for t in toks[i:i + 4]):
            continue
        v = int("".join(reversed(toks[i:i + 4])), 16)
        if LO <= v < HI and toks[i + 3] == "00":
            nv = v + SHIFT
            out[i:i + 4] = [f"{(nv >> (8 * k)) & 0xFF:02X}" for k in range(4)]
    return " ".join(out)


if __name__ == "__main__":
    old = Image(sys.argv[1])
    new = Image(sys.argv[2])
    rows = extract()
    ok = 0
    for r in rows:
        o = old.find(r["pattern"], 3)
        n = new.find(r["pattern"], 3)
        rb = rebase(r["pattern"])
        nr = new.find(rb, 3) if rb != r["pattern"] else []
        r["old"] = [hex(x) for x in o]
        r["new"] = [hex(x) for x in n]
        r["new_rebased"] = [hex(x) for x in nr]
        r["rebased_pattern"] = rb if rb != r["pattern"] else None
        good = len(n) == 1 or len(nr) == 1
        ok += good
        print(f'{r["index"]:3} {r["name"]:28} req={r["flags"]} old={r["old"][:2]} '
              f'(doc {hex(r["server_1273043"])}) new={r["new"][:2]} rebased={r["new_rebased"][:2]} {"OK" if good else "--"}')
    print(f"{ok}/{len(rows)} resolve uniquely on the new build")
    json.dump(rows, open("emf-0.3.3-signatures.json", "w"), indent=1)
