# SPDX-License-Identifier: MIT
"""Marks the link points of kiln_sprite_hooked (functions named kiln_hook_*) DontInline, so spirv-opt -O keeps them apart.

    python3 noinline.py in.spv out.spv
"""
import struct
import sys

data = open(sys.argv[1], "rb").read()
w = list(struct.unpack(f"<{len(data) // 4}I", data))
names, i = {}, 5
while i < len(w):
    op, wc = w[i] & 0xFFFF, w[i] >> 16
    if op == 5:  # OpName
        names[w[i + 1]] = bytes(struct.pack(f"<{wc - 2}I", *w[i + 2:i + wc])).split(b"\0")[0].decode()
    i += wc
marked, i = 0, 5
while i < len(w):
    op, wc = w[i] & 0xFFFF, w[i] >> 16
    if op == 54 and names.get(w[i + 2], "").startswith("kiln_hook_"):  # OpFunction
        w[i + 3] = (w[i + 3] & ~1) | 2  # DontInline, not Inline
        marked += 1
    i += wc
if marked != 3:
    sys.exit(f"expected 3 kiln_hook_* functions, found {marked}")
open(sys.argv[2], "wb").write(struct.pack(f"<{len(w)}I", *w))
