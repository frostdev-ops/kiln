# SPDX-License-Identifier: MIT
"""Retargeting: an animation authored on one rig played on another skeleton, a game creature's or a skeleton of the mod. docs/kiln/modding/animations.md,
"Retargeting".

Joints are paired by name (`map`, source joint -> target joint; joints of the same name pair by themselves). Each paired target joint turns in model space
as its source joint turns away from the source's reference pose; target joints with no pair keep their rest pose relative to their parent. So the two rigs
may differ in joint names, joint count, bone lengths and the rest orientation of every joint, as long as their reference poses show the same posture
(a T-pose against a T-pose). Translations carry over only on the `translate` joints, scaled by the rigs' heights. By default those are the topmost paired joints, and when such a
joint stands on the ground (a root bone at the origin), its paired children as well: the hips.

Everything is in the game's space (after the mirror on X), where both rigs face +Z with y up, as glTF files do.
"""
import math

from .gltfdoc import compose, qinv, qmul, qnorm, qrot


class RetargetError(Exception):
    pass


IDENTITY = ((0.0, 0.0, 0.0), (0.0, 0.0, 0.0, 1.0), (1.0, 1.0, 1.0))


def rest_frame(skel):
    """The skeleton's rest pose as one frame (rots, positions, scales)."""
    return [list(r[1]) for r in skel.rest], [list(r[0]) for r in skel.rest], [list(r[2]) for r in skel.rest]


def model_space(skel, frame):
    """(position, rotation, scale) per joint in model space, for one frame of (rots, positions, scales)."""
    rots, poss, scls = frame
    if any(len(v) != len(skel) for v in frame):
        raise RetargetError("retarget: pose and skeleton have different joint counts")
    out = [None] * len(skel)
    for j in _order(skel):
        for v, size in ((rots[j], 4), (poss[j], 3), (scls[j], 3)):
            if len(v) != size or any(not isinstance(x, (int, float)) or not math.isfinite(x) for x in v):
                raise RetargetError(f"retarget: joint {skel.names[j]!r} has a non-finite or malformed transform")
        norm = sum(x * x for x in rots[j])
        if not math.isfinite(norm) or norm < 1e-12 or any(abs(x) < 1e-12 for x in scls[j]):
            raise RetargetError(f"retarget: joint {skel.names[j]!r} has a zero rotation or scale")
        local = (tuple(poss[j]), qnorm(tuple(rots[j])), tuple(scls[j]))
        p = skel.parents[j]
        out[j] = local if p == 0xFFFF else compose(out[p], local)
        if any(not math.isfinite(x) for v in out[j] for x in v) or any(abs(x) < 1e-12 for x in out[j][2]):
            raise RetargetError(f"retarget: joint {skel.names[j]!r} has a degenerate model transform")
    return out


def _order(skel):
    """Joint indices parents first (the skeletons Kiln makes are already in this order; a game skeleton is checked, not assumed)."""
    if len(skel.parents) != len(skel) or len(skel.rest) != len(skel):
        raise RetargetError("retarget: skeleton arrays have different joint counts")
    if any(p != 0xFFFF and (not isinstance(p, int) or p < 0 or p >= len(skel) or p == j) for j, p in enumerate(skel.parents)):
        raise RetargetError("retarget: skeleton has an invalid parent joint")
    if all(p == 0xFFFF or p < j for j, p in enumerate(skel.parents)):
        return range(len(skel))
    kids = {}
    for j, p in enumerate(skel.parents):
        kids.setdefault(p, []).append(j)
    out, stack = [], list(reversed(kids.get(0xFFFF, [])))
    while stack:
        j = stack.pop()
        out.append(j)
        stack.extend(reversed(kids.get(j, [])))
    if len(out) != len(skel):
        raise RetargetError("the target skeleton has a joint that is not under its root")
    return out


class Plan:
    """What a retarget pairs and moves: `pairs` target joint -> source joint, `translate` target joints whose position follows the source's, `scale`."""

    def __init__(self, src, dst, cfg=None):
        cfg = {} if cfg is None or cfg is True else cfg
        if not isinstance(cfg, dict):
            raise RetargetError("retarget: expected an object of settings or true")
        self.src, self.dst = src, dst
        # Validate before walking ancestors to find the hips: a cyclic rig must fail, not hang.
        self.ref = model_space(src, cfg["referenceFrame"] if "referenceFrame" in cfg else rest_frame(src))
        self.dst_rest = model_space(dst, rest_frame(dst))
        m = cfg.get("map", "names")
        if m == "names":
            m = {}
        if not isinstance(m, dict):
            raise RetargetError('retarget.map is an object of source joint -> target joint, or "names"')
        pairs = {}
        for s, d in m.items():
            if s not in src.index:
                raise RetargetError(f"retarget.map: {s!r} is not a joint of the clips' rig (has {', '.join(src.names[:12])}{'...' if len(src) > 12 else ''})")
            if not isinstance(d, str) or d not in dst.index:
                raise RetargetError(f"retarget.map: {s!r} -> {d!r}: {d!r} is not a joint of the target skeleton (has {', '.join(dst.names[:12])}"
                                    f"{'...' if len(dst) > 12 else ''})")
            if dst.index[d] in pairs:
                raise RetargetError(f"retarget.map: target joint {d!r} is paired twice ({src.names[pairs[dst.index[d]]]!r} and {s!r})")
            pairs[dst.index[d]] = src.index[s]
        if cfg.get("byName", True):
            taken = set(pairs.values())
            for d, j in dst.index.items():
                if j not in pairs and d in src.index and src.index[d] not in taken and d != "kiln_root":
                    pairs[j] = src.index[d]
        if not pairs:
            raise RetargetError("retarget: no joint of the clips' rig pairs with the target skeleton: name them in \"map\" (source joint -> target joint)")
        self.pairs = pairs
        tr = cfg.get("translate")
        if tr is None:
            # the topmost paired joints: those with no paired joint above them
            paired = set(pairs)
            top = []
            for j in pairs:
                p = dst.parents[j]
                while p != 0xFFFF and p not in paired:
                    p = dst.parents[p]
                if p == 0xFFFF:
                    top.append(j)
            # a root on the ground (a game skeleton's j0, Blender's root bone) carries the hips below it: those move by translation too
            rest = model_space(dst, rest_frame(dst))
            more = [c for j in top if abs(rest[j][0][1]) < 0.05 for c in paired if dst.parents[c] == j]
            self.translate = sorted(set(top + more))
        elif tr == "all":
            self.translate = sorted(pairs)
        else:
            if not isinstance(tr, list):
                raise RetargetError('retarget.translate is a list of the clips\' rig\'s joints, or "all"')
            inv = {s: d for d, s in pairs.items()}
            self.translate = []
            for s in tr:
                if s not in src.index or src.index[s] not in inv:
                    raise RetargetError(f"retarget.translate: {s!r} is not a paired joint of the clips' rig")
                self.translate.append(inv[src.index[s]])
        sc = cfg.get("scale", "auto")
        if sc == "auto":
            sc = self._auto_scale()
        if isinstance(sc, bool) or not isinstance(sc, (int, float)) or not math.isfinite(sc) or sc <= 0:
            raise RetargetError('retarget.scale is a number above 0, or "auto"')
        self.scale = float(sc)

    def _auto_scale(self):
        """Target height over source height: at the first translate joint when it stands clear of the ground in both rigs, else the rigs' heights."""
        for d in self.translate:
            hs, hd = self.ref[self.pairs[d]][0][1], self.dst_rest[d][0][1]
            if hs > 0.05 and hd > 0.05:
                return hd / hs

        def height(ms):
            ys = [m[0][1] for m in ms]
            return max(ys) - min(ys)

        hs = height([self.ref[s] for s in self.pairs.values()])
        hd = height([self.dst_rest[d] for d in self.pairs])
        return hd / hs if hs > 1e-6 and hd > 1e-6 else 1.0

    def frame(self, src_frame):
        """One source frame -> one target frame (rots, positions, scales), parent-relative."""
        src, dst = self.src, self.dst
        gs = model_space(src, src_frame)
        n = len(dst)
        rots, poss, scls = [None] * n, [None] * n, [None] * n
        glob = [None] * n
        for j in _order(dst):
            rt, rq, rs = dst.rest[j]
            p = dst.parents[j]
            pt, pq, ps = IDENTITY if p == 0xFFFF else glob[p]
            if j in self.pairs:
                s = self.pairs[j]
                # the source joint's turn away from its reference, applied to the target joint's rest orientation, in model space
                gq = qnorm(qmul(qmul(gs[s][1], qinv(self.ref[s][1])), self.dst_rest[j][1]))
                lq = qnorm(qmul(qinv(pq), gq))
            else:
                lq = tuple(rq)
                gq = qnorm(qmul(pq, lq))
            if j in self.translate:
                s = self.pairs[j]
                k = self.scale
                rp, sp = self.dst_rest[j][0], self.ref[s][0]
                cur = gs[s][0]
                want = tuple(rp[i] + k * (cur[i] - sp[i]) for i in range(3))
                d = qrot(qinv(pq), tuple(want[i] - pt[i] for i in range(3)))
                if any(abs(v) < 1e-12 for v in ps):
                    raise RetargetError(f"retarget: joint {dst.names[j]!r} has a zero parent scale")
                lt = tuple(d[i] / ps[i] for i in range(3))
            else:
                lt = tuple(rt)
            rots[j], poss[j], scls[j] = list(lq), list(lt), list(rs)
            glob[j] = compose((pt, pq, ps), (lt, lq, tuple(rs)))
            if any(not math.isfinite(v) for part in glob[j] for v in part):
                raise RetargetError(f"retarget: joint {dst.names[j]!r} produced a non-finite transform")
        return rots, poss, scls

    def report(self):
        unpaired = [self.dst.names[j] for j in range(len(self.dst)) if j not in self.pairs]
        return {"paired": len(self.pairs), "targetJoints": len(self.dst), "unpaired": unpaired[:24] + (["..."] if len(unpaired) > 24 else []),
                "translate": [self.dst.names[j] for j in self.translate], "scale": round(self.scale, 6)}


def reference_frame(doc, src, spec, sample_animation):
    """The source pose `{"animation": name, "at": seconds}` names, as one frame: the pose that shows the target's rest posture."""
    from .clips import FPS
    if not isinstance(spec, dict) or not isinstance(spec.get("animation"), str) or not spec["animation"]:
        raise RetargetError("retarget.reference: name an animation")
    at = spec.get("at", 0)
    if isinstance(at, bool) or not isinstance(at, (int, float)) or not math.isfinite(at) or at < 0:
        raise RetargetError("retarget.reference.at: a finite time from 0 seconds")
    frames, n = sample_animation(doc, spec["animation"], src)
    f = min(n, round(min(at, n / FPS) * FPS))
    return frames[f]


__all__ = ["Plan", "RetargetError", "model_space", "reference_frame", "rest_frame"]
