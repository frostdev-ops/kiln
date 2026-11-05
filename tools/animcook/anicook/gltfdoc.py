# SPDX-License-Identifier: MIT
"""glTF 2.0 reading for animation: the node tree, skins, and animation channels sampled at any time. Files are read with
tools/modelcook's loader (.gltf with data URIs or a .bin, .glb). Everything here is glTF space: right-handed, y up, metres.
`to_game` converts a local transform to the game's left-handed space, the inverse of what tools/kfc exports."""
import bisect
import math
from pathlib import Path

from cook.gltf import GltfError, _accessor, _load  # tools/modelcook


def to_game(t, q, s):
    """Mirror x: position (-x, y, z), rotation (x, -y, -z, w). docs/re/models.md 3."""
    return (-t[0], t[1], t[2]), (q[0], -q[1], -q[2], q[3]), tuple(s)


def qmul(a, b):
    ax, ay, az, aw = a
    bx, by, bz, bw = b
    return (aw * bx + ax * bw + ay * bz - az * by, aw * by - ax * bz + ay * bw + az * bx,
            aw * bz + ax * by - ay * bx + az * bw, aw * bw - ax * bx - ay * by - az * bz)


def qnorm(q):
    m = math.sqrt(sum(c * c for c in q)) or 1.0
    return tuple(c / m for c in q)


def qinv(q):
    return (-q[0], -q[1], -q[2], q[3])


def qrot(q, v):
    x, y, z, w = q
    tx, ty, tz = 2 * (y * v[2] - z * v[1]), 2 * (z * v[0] - x * v[2]), 2 * (x * v[1] - y * v[0])
    return (v[0] + w * tx + y * tz - z * ty, v[1] + w * ty + z * tx - x * tz, v[2] + w * tz + x * ty - y * tx)


def slerp(a, b, u):
    d = sum(x * y for x, y in zip(a, b))
    if d < 0:
        b, d = tuple(-x for x in b), -d
    if d > 0.9995:
        return qnorm(tuple(x + (y - x) * u for x, y in zip(a, b)))
    th = math.acos(min(1.0, d))
    s = math.sin(th)
    ka, kb = math.sin((1 - u) * th) / s, math.sin(u * th) / s
    return qnorm(tuple(x * ka + y * kb for x, y in zip(a, b)))


def matrix_to_trs(m):
    """A column-major glTF node matrix -> translation, rotation, scale (no shear; a mirror goes to scale x)."""
    cols = [m[i * 4:i * 4 + 3] for i in range(3)]
    sc = [math.sqrt(sum(c * c for c in col)) or 1.0 for col in cols]
    det = (cols[0][0] * (cols[1][1] * cols[2][2] - cols[1][2] * cols[2][1]) - cols[1][0] * (cols[0][1] * cols[2][2] - cols[0][2] * cols[2][1])
           + cols[2][0] * (cols[0][1] * cols[1][2] - cols[0][2] * cols[1][1]))
    if det < 0:
        sc[0] = -sc[0]
    r = [[cols[c][row] / sc[c] for c in range(3)] for row in range(3)]
    return tuple(m[12:15]), mat_to_quat(r), tuple(sc)


def mat_to_quat(r):
    tr = r[0][0] + r[1][1] + r[2][2]
    if tr > 0:
        s = math.sqrt(tr + 1.0) * 2
        q = ((r[2][1] - r[1][2]) / s, (r[0][2] - r[2][0]) / s, (r[1][0] - r[0][1]) / s, s / 4)
    elif r[0][0] > r[1][1] and r[0][0] > r[2][2]:
        s = math.sqrt(1.0 + r[0][0] - r[1][1] - r[2][2]) * 2
        q = (s / 4, (r[0][1] + r[1][0]) / s, (r[0][2] + r[2][0]) / s, (r[2][1] - r[1][2]) / s)
    elif r[1][1] > r[2][2]:
        s = math.sqrt(1.0 + r[1][1] - r[0][0] - r[2][2]) * 2
        q = ((r[0][1] + r[1][0]) / s, s / 4, (r[1][2] + r[2][1]) / s, (r[0][2] - r[2][0]) / s)
    else:
        s = math.sqrt(1.0 + r[2][2] - r[0][0] - r[1][1]) * 2
        q = ((r[0][2] + r[2][0]) / s, (r[1][2] + r[2][1]) / s, s / 4, (r[1][0] - r[0][1]) / s)
    return qnorm(q)


def compose(parent, child):
    """Two (t, q, s) -> the child's transform in the parent's parent, for uniform or axis-free scale (armature nodes)."""
    pt, pq, ps = parent
    ct, cq, cs = child
    st = (ct[0] * ps[0], ct[1] * ps[1], ct[2] * ps[2])
    rt = qrot(pq, st)
    return (rt[0] + pt[0], rt[1] + pt[1], rt[2] + pt[2]), qnorm(qmul(pq, cq)), (cs[0] * ps[0], cs[1] * ps[1], cs[2] * ps[2])


class Channel:
    """One sampler: times, values, interpolation. `eval(t)` per glTF 2.0 (LINEAR with slerp, STEP, CUBICSPLINE)."""

    def __init__(self, path, times, values, interpolation):
        self.path, self.times, self.values, self.interp = path, times, values, interpolation
        if interpolation not in ("LINEAR", "STEP", "CUBICSPLINE"):
            raise GltfError(f"interpolation {interpolation!r}")
        if interpolation == "CUBICSPLINE" and len(values) != 3 * len(times):
            raise GltfError("a CUBICSPLINE sampler needs 3 values per key")
        if interpolation != "CUBICSPLINE" and len(values) != len(times):
            raise GltfError("sampler output count differs from its input count")

    def _v(self, i, part=1):
        return self.values[3 * i + part] if self.interp == "CUBICSPLINE" else self.values[i]

    def eval(self, t):
        ts = self.times
        rot = self.path == "rotation"
        if t <= ts[0]:
            v = self._v(0)
        elif t >= ts[-1]:
            v = self._v(len(ts) - 1)
        else:
            k = bisect.bisect_right(ts, t) - 1
            d = ts[k + 1] - ts[k]
            u = (t - ts[k]) / d if d > 0 else 0.0
            if self.interp == "STEP":
                v = self._v(k)
            elif self.interp == "LINEAR":
                a, b = self._v(k), self._v(k + 1)
                v = slerp(a, b, u) if rot else tuple(x + (y - x) * u for x, y in zip(a, b))
            else:
                u2, u3 = u * u, u * u * u
                p0, m0 = self._v(k), self._v(k, 2)
                p1, m1 = self._v(k + 1), self._v(k + 1, 0)
                v = tuple((2 * u3 - 3 * u2 + 1) * p0[c] + (u3 - 2 * u2 + u) * d * m0[c] + (-2 * u3 + 3 * u2) * p1[c] + (u3 - u2) * d * m1[c]
                          for c in range(len(p0)))
        return qnorm(v) if rot else tuple(v)


class Doc:
    def __init__(self, path):
        self.path = Path(path)
        self.doc, self.buffers = _load(path)
        self.nodes = self.doc.get("nodes", [])
        self.names = [n.get("name") or f"node{i}" for i, n in enumerate(self.nodes)]
        self.parent = [None] * len(self.nodes)
        for i, n in enumerate(self.nodes):
            for c in n.get("children", []):
                self.parent[c] = i
        self.by_name = {}
        for i, nm in enumerate(self.names):
            self.by_name.setdefault(nm, i)

    def rest(self, i):
        n = self.nodes[i]
        if "matrix" in n:
            return matrix_to_trs(n["matrix"])
        return (tuple(n.get("translation", (0.0, 0.0, 0.0))), qnorm(tuple(n.get("rotation", (0.0, 0.0, 0.0, 1.0)))),
                tuple(n.get("scale", (1.0, 1.0, 1.0))))

    def world_of_parent(self, i):
        """The rest transform of everything above node i, or None when there is nothing."""
        chain = []
        p = self.parent[i]
        while p is not None:
            chain.append(p)
            p = self.parent[p]
        out = None
        for a in reversed(chain):
            out = self.rest(a) if out is None else compose(out, self.rest(a))
        return out

    def animations(self):
        """{name: {node index: {path: Channel}}, ...} in file order; unnamed animations are animation<i>."""
        out = {}
        for ai, a in enumerate(self.doc.get("animations", [])):
            nm = a.get("name") or f"animation{ai}"
            chans = {}
            for ch in a.get("channels", []):
                target = ch.get("target", {})
                node, path = target.get("node"), target.get("path")
                if node is None or path not in ("translation", "rotation", "scale"):
                    continue  # morph weights and pointer channels are not skeletal
                s = a["samplers"][ch["sampler"]]
                times = [x[0] for x in _accessor(self.doc, self.buffers, s["input"])]
                vals = [tuple(x) for x in _accessor(self.doc, self.buffers, s["output"])]
                chans.setdefault(node, {})[path] = Channel(path, times, vals, s.get("interpolation", "LINEAR"))
            out[nm] = chans
        return out

    def extras_joint(self, i):
        return (self.nodes[i].get("extras") or {}).get("keen_joint")
