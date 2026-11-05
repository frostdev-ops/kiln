#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""Generate the AssetSample PNGs (no third-party deps). These are Kiln's own art, committed with the sample:
  emberseal_icon.png  256x256 RGBA  an ember-on-frost diamond sigil (the new item's inventory icon)
  ember_overlay.png   256x256 RGBA  a bold tintable panel used to retexture a vanilla UI image (B3)
Run: python3 tools/assets/make-sample-pngs.py mods/samples/AssetSample/assets
"""
import struct, zlib, sys, math, os

def write_png(path, w, h, rgba):
    def chunk(tag, data):
        return struct.pack(">I", len(data)) + tag + data + struct.pack(">I", zlib.crc32(tag + data) & 0xffffffff)
    raw = bytearray()
    for y in range(h):
        raw.append(0)
        raw += rgba[y * w * 4:(y + 1) * w * 4]
    png = b"\x89PNG\r\n\x1a\n"
    png += chunk(b"IHDR", struct.pack(">IIBBBBB", w, h, 8, 6, 0, 0, 0))
    png += chunk(b"IDAT", zlib.compress(bytes(raw), 9))
    png += chunk(b"IEND", b"")
    open(path, "wb").write(png)

def lerp(a, b, t): return tuple(int(a[i] + (b[i] - a[i]) * t) for i in range(4))

def icon(w, h):
    frost = (18, 28, 46, 255); frost2 = (40, 70, 120, 255)
    ember = (255, 120, 28, 255); gold = (255, 206, 92, 255)
    px = bytearray(w * h * 4)
    cx, cy = w / 2, h / 2
    for y in range(h):
        for x in range(w):
            # background radial frost
            r = math.hypot(x - cx, y - cy) / (w / 2)
            bg = lerp(frost2, frost, min(1, r))
            # diamond sigil |dx|+|dy| metric
            d = (abs(x - cx) + abs(y - cy)) / (w / 2)
            c = bg
            if d < 0.78:
                edge = abs(d - 0.62)
                if edge < 0.06:                       # the ring
                    c = gold
                elif d < 0.56:
                    c = lerp(ember, gold, max(0, (0.56 - d) / 0.56))  # ember core
            px[(y*w+x)*4:(y*w+x)*4+4] = bytes(c)
    return px

def overlay(w, h):
    # a bold diagonal ember band on a dark panel, clearly different from any vanilla image
    a = (14, 12, 11, 255); b = (255, 106, 26, 255)
    px = bytearray(w * h * 4)
    for y in range(h):
        for x in range(w):
            band = ((x + y) % 64) < 28
            c = b if band else a
            if x < 6 or y < 6 or x >= w-6 or y >= h-6: c = (249, 199, 79, 255)  # gold border
            px[(y*w+x)*4:(y*w+x)*4+4] = bytes(c)
    return px

out = sys.argv[1] if len(sys.argv) > 1 else "."
os.makedirs(out, exist_ok=True)
write_png(os.path.join(out, "emberseal_icon.png"), 256, 256, icon(256, 256))
write_png(os.path.join(out, "ember_overlay.png"), 256, 256, overlay(256, 256))
print("wrote emberseal_icon.png and ember_overlay.png to", out)
