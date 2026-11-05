# SPDX-License-Identifier: MIT
"""Geometry helpers: normals, tangents, bounds, handedness conversion."""
import math


def _sub(a, b):
    return (a[0] - b[0], a[1] - b[1], a[2] - b[2])


def _cross(a, b):
    return (a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0])


def _dot(a, b):
    return a[0] * b[0] + a[1] * b[1] + a[2] * b[2]


def _norm(v, fallback=(0.0, 1.0, 0.0)):
    ln = math.sqrt(_dot(v, v))
    return (v[0] / ln, v[1] / ln, v[2] / ln) if ln > 1e-12 else fallback


def compute_normals(P, I):
    """Area-weighted vertex normals for counter-clockwise (right-handed, glTF) triangles."""
    acc = [[0.0, 0.0, 0.0] for _ in P]
    for t in range(0, len(I) - 2, 3):
        a, b, c = I[t], I[t + 1], I[t + 2]
        fn = _cross(_sub(P[b], P[a]), _sub(P[c], P[a]))
        for i in (a, b, c):
            acc[i][0] += fn[0]
            acc[i][1] += fn[1]
            acc[i][2] += fn[2]
    return [_norm(tuple(v)) for v in acc]


def compute_tangents(P, N, UV, I):
    """Per-vertex tangents (xyz + handedness w) from UV derivatives, Gram-Schmidt against the normal (glTF space)."""
    tan = [[0.0, 0.0, 0.0] for _ in P]
    bit = [[0.0, 0.0, 0.0] for _ in P]
    for t in range(0, len(I) - 2, 3):
        a, b, c = I[t], I[t + 1], I[t + 2]
        e1, e2 = _sub(P[b], P[a]), _sub(P[c], P[a])
        du1, dv1 = UV[b][0] - UV[a][0], UV[b][1] - UV[a][1]
        du2, dv2 = UV[c][0] - UV[a][0], UV[c][1] - UV[a][1]
        r = du1 * dv2 - du2 * dv1
        if abs(r) < 1e-20:
            continue
        r = 1.0 / r
        sd = tuple((e1[k] * dv2 - e2[k] * dv1) * r for k in range(3))
        td = tuple((e2[k] * du1 - e1[k] * du2) * r for k in range(3))
        for i in (a, b, c):
            for k in range(3):
                tan[i][k] += sd[k]
                bit[i][k] += td[k]
    out = []
    for i, n in enumerate(N):
        t = tuple(tan[i])
        t = _sub(t, tuple(n[k] * _dot(n, t) for k in range(3)))
        if _dot(t, t) < 1e-20:  # no UV gradient: any vector perpendicular to n
            t = _cross(n, (0.0, 0.0, 1.0) if abs(n[2]) < 0.9 else (1.0, 0.0, 0.0))
        t = _norm(t, (1.0, 0.0, 0.0))
        w = -1.0 if _dot(_cross(n, t), tuple(bit[i])) < 0 else 1.0
        out.append(t + (w,))
    return out


def bounds(P):
    mn = tuple(min(p[i] for p in P) for i in range(3))
    mx = tuple(max(p[i] for p in P) for i in range(3))
    return mn, mx


def bounding_sphere(P):
    """Centre of the box, radius to the farthest vertex (conservative)."""
    mn, mx = bounds(P)
    c = tuple((mn[i] + mx[i]) / 2 for i in range(3))
    r = max(math.sqrt(_dot(_sub(p, c), _sub(p, c))) for p in P)
    return c, r


def to_game(prim):
    """glTF right-handed -> game left-handed: x -> -x, tangent w -> -w; triangle order unchanged
    (the inverse of tools/kfc mesh.to_rh)."""
    out = dict(prim)
    out["positions"] = [(-p[0], p[1], p[2]) for p in prim["positions"]]
    out["normals"] = [(-n[0], n[1], n[2]) for n in prim["normals"]]
    out["tangents"] = [(-t[0], t[1], t[2], -t[3]) for t in prim["tangents"]]
    return out
