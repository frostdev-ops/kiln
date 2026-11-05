#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""Renders a glTF model into an item icon PNG (no third-party deps): the default icon for a model override or a new
model item, when the mod has no painted one. The game's icons are baked textures (a UiTextureResource per item GUID),
not drawn from the model at run time, so a mod that changes a model ships the matching icon itself: this tool makes
one from the same glTF the model was cooked from, and kiln.assets serves it ("kind": "icon" in assets/assets.json; a
game item's own icon texture is retextured, a new item gets a new one).

    python3 tools/assets/render-icon.py MODEL.gltf OUT.png [--size 256] [--yaw 35] [--pitch 15] [--roll -45]
        [--color '#c8ccd2'] [--uv-colors 'u0,v0,u1,v1=#rrggbb;...'] [--outline '#1b1f26'] [--margin 0.07]

View: orthographic; the model is turned by yaw (about its +Y), then pitch (about X), then roll (about the view axis):
the defaults show a weapon modelled along +Y (Kiln's convention, docs/kiln/modding/models.md) diagonally, tip to the
top right, like the game's weapon icons. Colours: a triangle takes the colour of the --uv-colors rectangle its UV
centroid lies in (models that point into regions of a game texture), else its material's baseColorFactor, else
--color. Lit from the upper left, with a dark outline and a soft shadow on a transparent background, rendered at 3x
and filtered down. glTF 2.0 with embedded (data:) or side-by-side .bin buffers; triangles only.
"""
import argparse
import base64
import json
import math
import os
import struct
import sys
import zlib

COMP = {5120: ("b", 1), 5121: ("B", 1), 5122: ("h", 2), 5123: ("H", 2), 5125: ("I", 4), 5126: ("f", 4)}
NCOMP = {"SCALAR": 1, "VEC2": 2, "VEC3": 3, "VEC4": 4}


def hexcolor(s):
    s = s.strip().lstrip("#")
    return tuple(int(s[i:i + 2], 16) / 255.0 for i in (0, 2, 4))


def load_gltf(path):
    doc = json.load(open(path))
    bufs = []
    for b in doc.get("buffers", []):
        uri = b.get("uri", "")
        if uri.startswith("data:"):
            bufs.append(base64.b64decode(uri.split(",", 1)[1]))
        else:
            bufs.append(open(os.path.join(os.path.dirname(path), uri), "rb").read())

    def accessor(i):
        a = doc["accessors"][i]
        v = doc["bufferViews"][a["bufferView"]]
        fmt, size = COMP[a["componentType"]]
        n = NCOMP[a["type"]]
        stride = v.get("byteStride") or size * n
        base = v.get("byteOffset", 0) + a.get("byteOffset", 0)
        data = bufs[v["buffer"]]
        out = []
        for k in range(a["count"]):
            vals = struct.unpack_from("<" + fmt * n, data, base + k * stride)
            out.append(vals if n > 1 else vals[0])
        return out

    tris = []  # (p0, p1, p2, n, uv centroid or None, material colour or None)
    mats = doc.get("materials", [])

    def mat4_mul(a, b):
        return [[sum(a[i][k] * b[k][j] for k in range(4)) for j in range(4)] for i in range(4)]

    def node_matrix(n):
        if "matrix" in n:
            m = n["matrix"]
            return [[m[c * 4 + r] for c in range(4)] for r in range(4)]
        t = n.get("translation", [0, 0, 0])
        x, y, z, w = n.get("rotation", [0, 0, 0, 1])
        s = n.get("scale", [1, 1, 1])
        r = [[1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w)],
             [2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w)],
             [2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y)]]
        return [[r[i][0] * s[0], r[i][1] * s[1], r[i][2] * s[2], t[i]] for i in range(3)] + [[0, 0, 0, 1]]

    def walk(ni, parent):
        n = doc["nodes"][ni]
        m = mat4_mul(parent, node_matrix(n))
        if "mesh" in n:
            for prim in doc["meshes"][n["mesh"]]["primitives"]:
                if prim.get("mode", 4) != 4:
                    continue
                P = accessor(prim["attributes"]["POSITION"])
                UV = accessor(prim["attributes"]["TEXCOORD_0"]) if "TEXCOORD_0" in prim["attributes"] else None
                idx = accessor(prim["indices"]) if "indices" in prim else list(range(len(P)))
                col = None
                if "material" in prim and prim["material"] < len(mats):
                    f = mats[prim["material"]].get("pbrMetallicRoughness", {}).get("baseColorFactor")
                    if f:
                        col = tuple(f[:3])
                W = [tuple(sum(m[i][k] * (p[k] if k < 3 else 1.0) for k in range(4)) for i in range(3)) for p in P]
                for t in range(0, len(idx) - 2, 3):
                    a, b, c = W[idx[t]], W[idx[t + 1]], W[idx[t + 2]]
                    uvc = None
                    if UV:
                        uvc = tuple((UV[idx[t]][j] + UV[idx[t + 1]][j] + UV[idx[t + 2]][j]) / 3 for j in range(2))
                    tris.append((a, b, c, uvc, col))
        for c in n.get("children", []):
            walk(c, m)

    ident = [[1 if i == j else 0 for j in range(4)] for i in range(4)]
    scene = doc.get("scenes", [{"nodes": list(range(len(doc.get("nodes", []))))}])[doc.get("scene", 0)]
    for ni in scene["nodes"]:
        walk(ni, ident)
    return tris


def rot(p, yaw, pitch, roll):
    x, y, z = p
    c, s = math.cos(yaw), math.sin(yaw)
    x, z = c * x + s * z, -s * x + c * z
    c, s = math.cos(pitch), math.sin(pitch)
    y, z = c * y - s * z, s * y + c * z
    c, s = math.cos(roll), math.sin(roll)
    x, y = c * x - s * y, s * x + c * y
    return (x, y, z)


def render(tris, size, yaw, pitch, roll, default, uvcolors, outline, margin, ss=3):
    V = [tuple(rot(p, yaw, pitch, roll) for p in t[:3]) + t[3:] for t in tris]
    xs = [p[0] for t in V for p in t[:3]]
    ys = [p[1] for t in V for p in t[:3]]
    R = size * ss
    span = max(max(xs) - min(xs), max(ys) - min(ys)) or 1.0
    scale = R * (1 - 2 * margin) / span
    cx, cy = (max(xs) + min(xs)) / 2, (max(ys) + min(ys)) / 2
    light = (-0.45, 0.6, 0.66)
    ln = math.sqrt(sum(v * v for v in light))
    light = tuple(v / ln for v in light)
    zbuf = [-1e30] * (R * R)
    col = [None] * (R * R)
    for a, b, c, uvc, mcol in V:
        # flat normal in view space (z toward the viewer)
        ux, uy, uz = b[0] - a[0], b[1] - a[1], b[2] - a[2]
        vx, vy, vz = c[0] - a[0], c[1] - a[1], c[2] - a[2]
        nx, ny, nz = uy * vz - uz * vy, uz * vx - ux * vz, ux * vy - uy * vx
        nl = math.sqrt(nx * nx + ny * ny + nz * nz) or 1.0
        nx, ny, nz = nx / nl, ny / nl, nz / nl
        if nz < 0:
            nx, ny, nz = -nx, -ny, -nz
        base = default
        if uvc is not None:
            for (u0, v0, u1, v1), rgb in uvcolors:
                if u0 <= uvc[0] <= u1 and v0 <= uvc[1] <= v1:
                    base = rgb
                    break
            else:
                base = mcol or default
        elif mcol:
            base = mcol
        d = max(0.0, nx * light[0] + ny * light[1] + nz * light[2])
        hx, hy, hz = light[0], light[1], light[2] + 1
        hl = math.sqrt(hx * hx + hy * hy + hz * hz)
        spec = max(0.0, (nx * hx + ny * hy + nz * hz) / hl) ** 24 * 0.35
        k = 0.38 + 0.62 * d
        shade = tuple(min(1.0, ch * k + spec) for ch in base)
        P = [((p[0] - cx) * scale + R / 2, (cy - p[1]) * scale + R / 2, p[2]) for p in (a, b, c)]
        x0 = max(0, int(min(p[0] for p in P)))
        x1 = min(R - 1, int(max(p[0] for p in P)) + 1)
        y0 = max(0, int(min(p[1] for p in P)))
        y1 = min(R - 1, int(max(p[1] for p in P)) + 1)
        (ax, ay, az), (bx, by, bz), (qx, qy, qz) = P
        den = (by - qy) * (ax - qx) + (qx - bx) * (ay - qy)
        if abs(den) < 1e-12:
            continue
        bias = nz * span * 2e-3
        for y in range(y0, y1 + 1):
            py = y + 0.5
            for x in range(x0, x1 + 1):
                px = x + 0.5
                w0 = ((by - qy) * (px - qx) + (qx - bx) * (py - qy)) / den
                w1 = ((qy - ay) * (px - qx) + (ax - qx) * (py - qy)) / den
                w2 = 1 - w0 - w1
                if w0 < -1e-4 or w1 < -1e-4 or w2 < -1e-4:  # shared edges: no cracks
                    continue
                z = w0 * az + w1 * bz + w2 * qz + bias  # ties at shared edges go to the face that looks at the camera
                i = y * R + x
                if z > zbuf[i]:
                    zbuf[i] = z
                    col[i] = shade
    # down-filter to the icon size: colour (premultiplied) and coverage
    out = [(0.0, 0.0, 0.0, 0.0)] * (size * size)
    for y in range(size):
        for x in range(size):
            r = g = b = a = 0.0
            for sy in range(ss):
                row = (y * ss + sy) * R + x * ss
                for sx in range(ss):
                    c = col[row + sx]
                    if c is not None:
                        r += c[0]
                        g += c[1]
                        b += c[2]
                        a += 1
            n = ss * ss
            out[y * size + x] = (r / n, g / n, b / n, a / n)
    alpha = [p[3] for p in out]

    def dilate(m, rad):
        res = m[:]
        for y in range(size):
            for x in range(size):
                best = m[y * size + x]
                for dy in range(-rad, rad + 1):
                    yy = y + dy
                    if yy < 0 or yy >= size:
                        continue
                    for dx in range(-rad, rad + 1):
                        xx = x + dx
                        if 0 <= xx < size and dx * dx + dy * dy <= rad * rad + rad:
                            best = max(best, m[yy * size + xx])
                res[y * size + x] = best
        return res

    ring = dilate(alpha, max(1, size // 128))
    shadow = dilate(alpha, max(2, size // 64))
    off = max(2, size // 80)
    px = bytearray(size * size * 4)
    for y in range(size):
        for x in range(size):
            i = y * size + x
            r, g, b, a = out[i]
            # under the model: the outline ring, then the shadow (offset down-right)
            sy, sx = y - off, x - off
            sh = shadow[sy * size + sx] * 0.35 if 0 <= sy < size and 0 <= sx < size else 0.0
            o = ring[i]
            under_a = o + sh * (1 - o)
            ur = (outline[0] * o) / under_a if under_a > 0 else 0
            ug = (outline[1] * o) / under_a if under_a > 0 else 0
            ub = (outline[2] * o) / under_a if under_a > 0 else 0
            fa = a + under_a * (1 - a)
            if fa <= 0:
                continue
            fr = (r + ur * under_a * (1 - a)) / fa
            fg = (g + ug * under_a * (1 - a)) / fa
            fb = (b + ub * under_a * (1 - a)) / fa
            px[i * 4:i * 4 + 4] = bytes((min(255, int(fr * 255 + 0.5)), min(255, int(fg * 255 + 0.5)),
                                         min(255, int(fb * 255 + 0.5)), min(255, int(fa * 255 + 0.5))))
    return px


def write_png(path, w, h, rgba):
    def chunk(tag, data):
        return struct.pack(">I", len(data)) + tag + data + struct.pack(">I", zlib.crc32(tag + data) & 0xffffffff)
    raw = bytearray()
    for y in range(h):
        raw.append(0)
        raw += rgba[y * w * 4:(y + 1) * w * 4]
    png = b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", w, h, 8, 6, 0, 0, 0))
    png += chunk(b"IDAT", zlib.compress(bytes(raw), 9)) + chunk(b"IEND", b"")
    with open(path, "wb") as f:
        f.write(png)


def main():
    ap = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    ap.add_argument("gltf")
    ap.add_argument("out")
    ap.add_argument("--size", type=int, default=256)
    ap.add_argument("--yaw", type=float, default=35)
    ap.add_argument("--pitch", type=float, default=15)
    ap.add_argument("--roll", type=float, default=-45)
    ap.add_argument("--color", default="#c8ccd2")
    ap.add_argument("--uv-colors", default="")
    ap.add_argument("--outline", default="#1b1f26")
    ap.add_argument("--margin", type=float, default=0.07)
    a = ap.parse_args()
    uvc = []
    for part in filter(None, (p.strip() for p in a.uv_colors.split(";"))):
        rect, rgb = part.split("=")
        u0, v0, u1, v1 = (float(v) for v in rect.split(","))
        uvc.append(((min(u0, u1), min(v0, v1), max(u0, u1), max(v0, v1)), hexcolor(rgb)))
    tris = load_gltf(a.gltf)
    if not tris:
        sys.exit(f"{a.gltf}: no triangles")
    px = render(tris, a.size, math.radians(a.yaw), math.radians(a.pitch), math.radians(a.roll), hexcolor(a.color), uvc,
                hexcolor(a.outline), a.margin)
    write_png(a.out, a.size, a.size, px)
    print(f"{a.out}: {a.size}x{a.size} icon of {a.gltf} ({len(tris)} triangles)")


if __name__ == "__main__":
    main()
