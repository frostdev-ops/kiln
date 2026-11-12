# SPDX-License-Identifier: MIT
"""A clip reworked from another one at cook time: a `clips` entry with `"from"` (docs/kiln/modding/animations.md, "`clips`").

    "cinder_guard": { "from": "@block_idle", "mirror": true, "speed": 0.8, "trim": [0.1, 1.4] }

`from` names one of the game's clips (an input of type keen::JointAnimation) or a clip an earlier entry or step of the recipe made. The
operations run in one fixed order, whatever the order of the keys: trim, reverse, speed, mirror, blend, offset. The source is the decoded clip,
sampled with the game's own arithmetic (tools/kfc/kfc/anim.py) at any time, so a trim or a speed reads between its frames without a second
resampling. The result is sampled at 60 frames a second and encoded like every cooked clip.

Siblings under the clip's GUID (docs/re/npc-internals.md 2.2, 2.3) follow each operation or are dropped with a warning:
- root motion (RootMotionAnimation, and the LveAnimation written again from it): trim cuts the path and starts it again at the new first frame
  (placed where the source's path starts: the game's paths do not all start at zero),
  reverse walks it backwards, speed keeps the path over the new length, mirror reflects it across the plane, blend mixes the two clips' paths by
  the weight of the skeleton's root joint (a clip without root motion counts as standing still), offset leaves it alone;
- events (AnimationSequenceContainer + SequenceResource): trim keeps the events inside the span and drops the others, reverse and speed move
  their times (an event with a duration keeps covering the same motion), mirror swaps left and right foot plants and left and right slots,
  blend keeps the source's and drops the `with` clip's, offset leaves them alone.

Joints are named as everywhere in animcook: `j<index>` on a game skeleton, their names on a skeleton of the recipe.
"""
import copy
import math

from kfc import anim

from .gltfdoc import qinv, qmul, qnorm, qrot, slerp
from .retarget import RetargetError, model_space, rest_frame, _order

FPS = 60
ROOT = 0xFFFF
OPS = ("trim", "reverse", "speed", "mirror", "blend", "offset")
KEYS = frozenset(OPS) | {"from"}
AXES = "xyz"
FOOT = {"keen::LeftFootPlantingEvent": "keen::RightFootPlantingEvent", "keen::RightFootPlantingEvent": "keen::LeftFootPlantingEvent"}
IDENTITY_Q = (0.0, 0.0, 0.0, 1.0)
AMBIGUOUS_DEG = 5.0  # two candidates for a mirror pair whose rest frames fit within this of each other are ambiguous


class OpError(Exception):
    pass


def _num(x):
    return isinstance(x, (int, float)) and not isinstance(x, bool) and math.isfinite(x)


def _vec3(x):
    return isinstance(x, list) and len(x) == 3 and all(_num(v) for v in x)


def check(name, c):
    """The checks a recipe can have without the game or the source clip: shapes and ranges of a `from` entry's keys."""
    where = f"clip {name}"
    if "from" not in c:
        stray = [k for k in OPS if k in c]
        if stray:
            raise OpError(f"{where}: {', '.join(stray)} rework a clip named by \"from\", and it has none")
        return
    if not isinstance(c["from"], str) or not c["from"]:
        raise OpError(f"{where}: \"from\" names a clip: \"@input\" or the key of an earlier clip")
    if "animation" in c:
        raise OpError(f"{where}: \"from\" or \"animation\", not both: a clip is either reworked from another clip or sampled from glTF")
    if "trim" in c:
        t = c["trim"]
        if not (isinstance(t, list) and len(t) == 2 and all(_num(v) for v in t)) or t[0] < 0 or t[1] <= t[0]:
            raise OpError(f"{where}: trim is [start, end] in seconds of the source, 0 <= start < end")
    if "reverse" in c and not isinstance(c["reverse"], bool):
        raise OpError(f"{where}: reverse is true or false")
    if "speed" in c and (not _num(c["speed"]) or c["speed"] <= 0):
        raise OpError(f"{where}: speed is a number above 0 (2 plays twice as fast), not {c['speed']!r}")
    if "mirror" in c:
        m = c["mirror"]
        if isinstance(m, dict):
            bad = set(m) - {"pairs", "axis", "tolerance"}
            if bad:
                raise OpError(f"{where}: mirror: unknown keys {sorted(bad)} (pairs, axis, tolerance)")
            if "axis" in m and m["axis"] not in AXES:
                raise OpError(f"{where}: mirror.axis is x, y or z")
            if "tolerance" in m and (not _num(m["tolerance"]) or m["tolerance"] <= 0):
                raise OpError(f"{where}: mirror.tolerance is metres above 0")
            if "pairs" in m and not (isinstance(m["pairs"], dict) and all(isinstance(a, str) and isinstance(b, str) for a, b in m["pairs"].items())):
                raise OpError(f"{where}: mirror.pairs is {{ joint: joint }}")
        elif not isinstance(m, bool):
            raise OpError(f"{where}: mirror is true, false or {{ pairs, axis, tolerance }}")
    if "blend" in c:
        b = c["blend"]
        if not isinstance(b, dict) or not isinstance(b.get("with"), str) or not b["with"]:
            raise OpError(f"{where}: blend is {{ \"with\": clip, \"weight\": w }} or {{ \"with\": clip, \"bones\": [...] | {{ joint: weight }} }}")
        bad = set(b) - {"with", "weight", "bones", "children", "comment"}
        if bad:
            raise OpError(f"{where}: blend: unknown keys {sorted(bad)}")
        if ("weight" in b) == ("bones" in b):
            raise OpError(f"{where}: blend takes a weight or bones, one of the two")
        if "weight" in b and (not _num(b["weight"]) or not 0 <= b["weight"] <= 1):
            raise OpError(f"{where}: blend.weight is 0 to 1")
        if "bones" in b:
            bs = b["bones"]
            ok = (isinstance(bs, list) and bs and all(isinstance(x, str) for x in bs)) or \
                 (isinstance(bs, dict) and bs and all(_num(w) and 0 <= w <= 1 for w in bs.values()))
            if not ok:
                raise OpError(f"{where}: blend.bones is a list of joints or {{ joint: weight 0 to 1 }}")
        if "children" in b and not isinstance(b["children"], bool):
            raise OpError(f"{where}: blend.children is true or false")
    if "offset" in c:
        o = c["offset"]
        if not isinstance(o, dict) or not o:
            raise OpError(f"{where}: offset is {{ joint: {{ \"rotate\": [x, y, z degrees], \"translate\": [x, y, z] }} }}")
        for j, v in o.items():
            if not isinstance(v, dict) or not v or set(v) - {"rotate", "translate"} or not all(_vec3(x) for x in v.values()):
                raise OpError(f"{where}: offset.{j} is {{ \"rotate\": [x, y, z degrees], \"translate\": [x, y, z metres] }}")


# ---- the source ----

def path_of(rm_value):
    """A RootMotionAnimation value -> (positions, orientations) per frame, as tuples."""
    return ([(p["x"], p["y"], p["z"]) for p in rm_value["positions"]],
            [(q["x"], q["y"], q["z"], q["w"]) for q in rm_value["orientations"]])


def hierarchy_of(skel):
    return getattr(skel, "game_hash", None) or skel.hierarchy_hash


class Source:
    """A clip to rework: the decoded clip, its skeleton, its root motion path (or None) and its SequenceResource values."""

    def __init__(self, label, clip, skel, rm=None, sequences=()):
        self.label, self.clip, self.skel = label, clip, skel
        self.n = clip.frame_count
        self.dp = skel.default_pose()
        self.loop = clip.flags == 1
        self.rm = rm
        self.sequences = list(sequences)

    @property
    def duration(self):
        return self.n / FPS

    def fits(self, skel):
        return self.clip.hierarchy_hash == hierarchy_of(skel) and self.clip.joint_count == len(skel)

    def pose(self, f):
        """The pose at frame f (a float, 0 to n), with the game's sampler: (rots, positions, scales) per joint, parent-relative."""
        f = min(max(float(f), 0.0), float(self.n))
        r, p, s = anim.sample_frame(self.clip, f, self.dp)
        return [list(qnorm(q)) for q in r], [list(x) for x in p], [list(x) for x in s]

    def rm_at(self, f):
        """The root motion path at frame f of the clip, between its per-frame samples: (position, orientation)."""
        ps, qs = self.rm
        m = len(ps) - 1
        x = min(max(f * m / self.n if self.n else 0.0, 0.0), float(m))
        i = min(int(x), max(m - 1, 0))
        u = x - i if m else 0.0
        j = min(i + 1, m)
        return tuple(a + (b - a) * u for a, b in zip(ps[i], ps[j])), slerp(qs[i], qs[j], u)


# ---- mirror ----

def reflect_v(v, a):
    return tuple(-c if i == a else c for i, c in enumerate(v))


def reflect_q(q, a):
    """The rotation reflected across the plane whose normal is axis a: S R S. The component along the normal stays, the other two flip."""
    return tuple(c if i == a or i == 3 else -c for i, c in enumerate(q))


def qangle(a, b):
    d = abs(sum(x * y for x, y in zip(qnorm(a), qnorm(b))))
    return math.degrees(2 * math.acos(min(1.0, d)))


class Mirror:
    """Left and right of a skeleton, found from its rest pose, and a pose reflected across the plane of symmetry.

    Pairing: the plane goes through the model origin, normal to `axis` (default x, the side axis of the game's and glTF's space). Parents
    first, each joint looks for a joint at the mirror image of its model-space rest position, within `tolerance` metres (default 0.3 % of
    the skeleton's size, at least 1 mm), among the children of its parent's mirror. A joint at its own mirror image is a centre joint. Of
    several candidates the one whose rest frame mirrors the way its parent's does wins; when two fit within 5 degrees of each other the
    pairing is ambiguous and refused: name them in `pairs`. A joint with no candidate is unplaced (reported): under a centre joint it is
    mirrored in place, under a paired joint it keeps its own local values and follows its parent to the other side's motion.

    Reflection: for output joint k taking its motion from joint j = M[k], the model-space turn of j away from its rest, reflected, is applied to
    k's rest: R'k = refl(Rj Rj0^-1) Rk0. In parent-relative terms L'k = Cp^-1 refl(Lj) Ck with Ck = refl(Rj0)^-1 Rk0, and the local
    translation t'k = Cp^-1 S tj plus the rest difference, so a pair whose rest frames are not mirror images still lands on its own rest pose,
    and mirroring twice gives the source back exactly. Scales are swapped between a pair, not reflected."""

    def __init__(self, skel, cfg=None):
        cfg = cfg if isinstance(cfg, dict) else {}
        self.skel = skel
        self.axis_name = cfg.get("axis", "x")
        a = self.axis = AXES.index(self.axis_name)
        try:
            ms = model_space(skel, rest_frame(skel))
            order = list(_order(skel))
        except RetargetError as e:
            raise OpError(f"mirror: {e}") from e
        pos = [m[0] for m in ms]
        rq = [m[1] for m in ms]
        size = max(max(p[i] for p in pos) - min(p[i] for p in pos) for i in range(3)) if pos else 0.0
        self.tol = float(cfg.get("tolerance", max(0.001, 0.003 * size)))
        n = len(skel)
        M = [None] * n
        given = cfg.get("pairs") or {}
        for x, y in given.items():
            for nm in (x, y):
                if nm not in skel.index:
                    raise OpError(f"mirror.pairs: {nm!r} is not a joint of the skeleton (joints are {_some(skel.names)})")
            i, j = skel.index[x], skel.index[y]
            for q in (i, j):
                if M[q] is not None and M[q] not in (i, j):
                    raise OpError(f"mirror.pairs: {skel.names[q]!r} is paired twice")
            M[i], M[j] = j, i
        kids = {}
        for j, p in enumerate(skel.parents):
            kids.setdefault(p, []).append(j)

        def corr(j, k):  # the frame correction of output joint k taking the motion of j
            return qnorm(qmul(qinv(reflect_q(rq[j], a)), rq[k]))

        self.unplaced = []
        for j in order:
            if M[j] is not None:
                continue
            p = skel.parents[j]
            mp = ROOT if p == ROOT else M[p]
            want = reflect_v(pos[j], a)
            cands = [k for k in kids.get(mp, []) if (M[k] is None or k == j) and max(abs(u - v) for u, v in zip(pos[k], want)) <= self.tol]
            if j in cands:
                M[j] = j
                continue
            if not cands:
                M[j] = j
                self.unplaced.append(j)
                continue
            if len(cands) > 1:
                cp = IDENTITY_Q if p == ROOT else corr(M[p], p)
                ranked = sorted((qangle(corr(j, k), cp), k) for k in cands)
                if ranked[1][0] - ranked[0][0] < AMBIGUOUS_DEG:
                    names = ", ".join(skel.names[k] for _, k in ranked[:4])
                    raise OpError(f"mirror: {skel.names[j]!r} has {len(cands)} joints at its mirror image ({names}) that fit equally well: "
                                  f"name its partner in \"mirror\": {{ \"pairs\": {{ \"{skel.names[j]}\": ... }} }}")
                cands = [ranked[0][1]]
            k = cands[0]
            M[j], M[k] = k, j
        # an unplaced joint under a paired joint (or under such a joint) keeps its own local values and follows its parent: the reflection of
        # a local frame needs a parent that is its own mirror image
        self.kept = set()
        for k in order:
            p = skel.parents[k]
            if M[k] == k and p != ROOT and (M[p] != p or p in self.kept):
                self.kept.add(k)
        for k in range(n):
            j, p = M[k], skel.parents[k]
            if k in self.kept:
                continue
            if p != ROOT and skel.parents[j] != M[p] or p == ROOT and skel.parents[j] != ROOT:
                raise OpError(f"mirror: {skel.names[k]!r} and {skel.names[j]!r} are paired but their parents are not "
                              f"({skel.names[p] if p != ROOT else 'none'} and {skel.names[skel.parents[j]] if skel.parents[j] != ROOT else 'none'})")
        self.M = M
        self.C = [corr(M[k], k) for k in range(n)]
        self.rest = skel.rest
        self.delta = []
        for k in range(n):
            j, p = M[k], skel.parents[k]
            cpi = IDENTITY_Q if p == ROOT else qinv(self.C[p])
            moved = qrot(cpi, reflect_v(skel.rest[j][0], a))
            self.delta.append(tuple(t - m for t, m in zip(skel.rest[k][0], moved)))
        self.order = order
        self.given = len(given)

    def report(self):
        names = self.skel.names
        pairs = sorted((names[j], names[k]) for j, k in enumerate(self.M) if j < k)
        centre = [names[j] for j, k in enumerate(self.M) if j == k and j not in self.unplaced]
        return {"axis": self.axis_name, "tolerance": round(self.tol, 5), "pairs": [list(p) for p in pairs], "centre": len(centre),
                "unplaced": [names[j] for j in self.unplaced], "keptOnParent": [names[j] for j in sorted(self.kept)], "given": self.given}

    def frame(self, frame):
        rots, poss, scls = frame
        a = self.axis
        out_r, out_p, out_s = [None] * len(rots), [None] * len(rots), [None] * len(rots)
        for k, j in enumerate(self.M):
            if k in self.kept:
                out_r[k], out_p[k], out_s[k] = list(rots[k]), list(poss[k]), list(scls[k])
                continue
            p = self.skel.parents[k]
            cpi = IDENTITY_Q if p == ROOT else qinv(self.C[p])
            out_r[k] = list(qnorm(qmul(qmul(cpi, reflect_q(qnorm(tuple(rots[j])), a)), self.C[k])))
            moved = qrot(cpi, reflect_v(tuple(poss[j]), a))
            out_p[k] = [m + d for m, d in zip(moved, self.delta[k])]
            rs_k, rs_j = self.rest[k][2], self.rest[j][2]
            out_s[k] = [s * (x / y if y else 1.0) for s, x, y in zip(scls[j], rs_k, rs_j)]
        return out_r, out_p, out_s


def _some(names, n=12):
    return ", ".join(names[:n]) + ("..." if len(names) > n else "")


# ---- the rework ----

def joint_index(skel, name, where):
    if not isinstance(name, str) or name not in skel.index:
        raise OpError(f"{where}: {name!r} is not a joint of the skeleton (joints are {_some(skel.names)})")
    return skel.index[name]


def euler_q(deg):
    """[x, y, z] degrees -> a quaternion turning about x, then y, then z (the axes of the parent)."""
    q = IDENTITY_Q
    for i, d in enumerate(deg):
        h = math.radians(d) / 2
        r = [0.0, 0.0, 0.0, math.cos(h)]
        r[i] = math.sin(h)
        q = qmul(tuple(r), q)
    return qnorm(q)


def mask_weights(skel, spec, where):
    """Per joint, how much comes from the `with` clip."""
    n = len(skel)
    if "weight" in spec:
        return [float(spec["weight"])] * n
    bones = spec["bones"]
    named = {joint_index(skel, b, f"{where}: blend.bones"): 1.0 for b in bones} if isinstance(bones, list) else \
            {joint_index(skel, b, f"{where}: blend.bones"): float(w) for b, w in bones.items()}
    w = [named.get(j, 0.0) for j in range(n)]
    if spec.get("children"):
        for j in _order(skel):
            p = skel.parents[j]
            if j not in named and p != ROOT:
                w[j] = w[p]
    return w


class Result:
    def __init__(self, frames, n, rm, sequences, report):
        self.frames, self.n, self.rm, self.sequences, self.report = frames, n, rm, sequences, report


def _rebase(path, origin):
    """A root motion path started again at its first sample, then placed at `origin` (the source's own first sample: the game's paths
    do not all start at zero, and startToEnd is their end minus their start): positions turned into the space of the first orientation."""
    ps, qs = path
    p0, q0 = ps[0], qs[0]
    inv = qinv(q0)
    op, oq = origin
    outp = [tuple(a + b for a, b in zip(op, qrot(oq, qrot(inv, tuple(a - b for a, b in zip(p, p0)))))) for p in ps]
    outq = []
    for q in qs:
        r = qnorm(qmul(oq, qmul(inv, q)))
        outq.append(tuple(-c for c in r) if sum(x * y for x, y in zip(r, oq)) < 0 else r)  # the source's sign: the game writes identity as w = -1
    return outp, outq


def rework(name, spec, src, skel, resolve, slots=None):
    """Applies the entry's operations to `src` on `skel`. `resolve(ref)` -> the Source of a `blend.with`. `slots`: the AttachmentSlot names,
    for swapping left and right slots of events. -> Result: frames (n + 1), n, root motion path or None, SequenceResource values, report."""
    where = f"clip {name}"
    warn = []
    ops = [op for op in OPS if op in spec and spec[op] is not False]
    if not src.fits(skel):
        raise OpError(f"{where}: {spec['from']} is a clip of another skeleton ({src.clip.joint_count} joints, hierarchy 0x{src.clip.hierarchy_hash:016x}), "
                      f"not of this step's ({len(skel)} joints, 0x{hierarchy_of(skel):016x})")
    a, b, rev = 0.0, float(src.n), False
    if "trim" in spec:
        s, e = spec["trim"]
        if s < 0 or e > src.duration + 0.5 / FPS or s >= e:
            raise OpError(f"{where}: trim [{s}, {e}] is outside {spec['from']}, which lasts {src.duration:.3f} s")
        a, b = s * FPS, min(e * FPS, float(src.n))
    rev = bool(spec.get("reverse"))
    span = b - a
    speed = float(spec.get("speed", 1.0))
    n = max(1, round(span / speed))

    def src_f(k):
        u = k / n
        return b - span * u if rev else a + span * u

    mirror = Mirror(skel, spec["mirror"]) if spec.get("mirror") else None
    blend = wsrc = weights = None
    if "blend" in spec:
        blend = spec["blend"]
        wsrc = resolve(blend["with"])
        if not wsrc.fits(skel):
            raise OpError(f"{where}: blend.with {blend['with']} is a clip of another skeleton ({wsrc.clip.joint_count} joints), not of this step's "
                          f"({len(skel)} joints)")
        weights = mask_weights(skel, blend, where)
        if abs(wsrc.duration - n / FPS) > 0.5 / FPS:
            warn.append(f"{where}: blend.with {blend['with']} lasts {wsrc.duration:.3f} s, the clip {n / FPS:.3f} s: it is stretched to the clip's length")
    offsets = {}
    for jn, o in (spec.get("offset") or {}).items():
        j = joint_index(skel, jn, f"{where}: offset")
        q = euler_q(o.get("rotate", [0.0, 0.0, 0.0]))
        t = o.get("translate", [0.0, 0.0, 0.0])
        # given in glTF's axes, as Blender shows the joint's parent; the game's space mirrors x (gltfdoc.to_game)
        offsets[j] = ((q[0], -q[1], -q[2], q[3]), (-t[0], t[1], t[2]))
    frames = []
    for k in range(n + 1):
        fr = src.pose(src_f(k))
        if mirror:
            fr = mirror.frame(fr)
        if blend:
            other = wsrc.pose(k / n * wsrc.n)
            rots, poss, scls = fr
            for j, w in enumerate(weights):
                if w <= 0.0:
                    continue
                if w >= 1.0:
                    rots[j], poss[j], scls[j] = other[0][j], other[1][j], other[2][j]
                    continue
                rots[j] = list(slerp(tuple(rots[j]), tuple(other[0][j]), w))
                poss[j] = [x + (y - x) * w for x, y in zip(poss[j], other[1][j])]
                scls[j] = [x + (y - x) * w for x, y in zip(scls[j], other[2][j])]
        for j, (q, t) in offsets.items():
            fr[0][j] = list(qnorm(qmul(q, tuple(fr[0][j]))))
            fr[1][j] = [x + d for x, d in zip(fr[1][j], t)]
        frames.append(fr)

    # root motion
    rm_note = "none"
    rm = None
    if src.rm is not None:
        rm = _rebase(tuple(map(list, zip(*[src.rm_at(src_f(k)) for k in range(n + 1)]))), (src.rm[0][0], src.rm[1][0]))
        moved = [op for op in ops if op != "blend"]
        rm_note = "follows " + ", ".join(moved) if moved else "kept"
        if mirror:
            rm = ([reflect_v(p, mirror.axis) for p in rm[0]], [reflect_q(q, mirror.axis) for q in rm[1]])
    if blend:
        root = next((j for j, p in enumerate(skel.parents) if p == ROOT), 0)
        wr = weights[root]
        if wr > 0 and (rm is not None or wsrc.rm is not None):
            theirs = tuple(map(list, zip(*[wsrc.rm_at(k / n * wsrc.n) for k in range(n + 1)]))) if wsrc.rm is not None else None
            # a clip without root motion stands still, where the other clip's path starts
            mine = rm if rm is not None else ([theirs[0][0]] * (n + 1), [theirs[1][0]] * (n + 1))
            if theirs is None:
                theirs = ([mine[0][0]] * (n + 1), [mine[1][0]] * (n + 1))
            rm = ([tuple(x + (y - x) * wr for x, y in zip(p, q)) for p, q in zip(mine[0], theirs[0])],
                  [slerp(p, q, wr) for p, q in zip(mine[1], theirs[1])])
            rm_note = f"blended by the root joint's weight {wr:g} with {blend['with']}" + ("" if wsrc.rm is not None else " (it has none: standing still)") + \
                      ("" if src.rm is not None else f" ({spec['from']} has none: standing still)")
        elif wsrc.rm is not None:
            warn.append(f"{where}: blend: the root motion of {blend['with']} is dropped: the root joint takes no weight from it")

    # events
    d_out = n / FPS
    seqs = []
    dropped = unknown = offset_kept = 0
    swap_slots = {}
    if mirror and slots:
        for s in slots:
            for x, y in (("L", "R"), ("R", "L")):
                if s.endswith(x) and s[:-1] + y in slots:
                    swap_slots[s] = s[:-1] + y
    one_sided = set()
    for seq in src.sequences:
        evs = []
        for e in seq.get("events", []):
            if not isinstance(e, dict) or "value" not in e:
                unknown += 1
                continue
            v = copy.deepcopy(e["value"])
            typ = e["$type"]
            t0 = v["time"]["value"] / 1e9
            dur = v["duration"]["value"] / 1e9 if isinstance(v.get("duration"), dict) else 0.0
            f = (t0 + dur if rev else t0) * FPS
            start = ((b - f) if rev else (f - a)) / span * d_out
            if start < -0.5 / FPS or start > d_out + 0.5 / FPS:
                dropped += 1
                continue
            v["time"]["value"] = int(round(min(max(start, 0.0), d_out) * 1e9))
            if isinstance(v.get("duration"), dict):
                v["duration"]["value"] = int(round(dur * n / span * 1e9))
            if mirror:
                typ = FOOT.get(typ, typ)
                sl = v.get("slot")
                if isinstance(sl, str):
                    if sl in swap_slots:
                        v["slot"] = swap_slots[sl]
                    elif sl[-1:] in ("L", "R") and sl not in ("Root",):
                        one_sided.add(sl)
                no = v.get("notifierOffset")
                if isinstance(no, dict):
                    if isinstance(no.get("worldOffset"), dict):
                        w = no["worldOffset"]
                        w[AXES[mirror.axis]] = -w[AXES[mirror.axis]]
                    lo, oo = no.get("localOffset") or {}, no.get("orientationOffset") or {}
                    if any(abs(lo.get(c, 0.0)) > 1e-6 for c in "xyz") or any(abs(oo.get(c, 0.0)) > 1e-6 for c in "xyz"):
                        offset_kept += 1
            evs.append({"$type": typ, "value": v})
        evs.sort(key=lambda x: x["value"]["time"]["value"])
        s2 = dict(seq, events=evs)
        s2["length"] = {"value": int(round(d_out * 1e9))}
        seqs.append(s2)
    kept = sum(len(s["events"]) for s in seqs)
    if dropped:
        warn.append(f"{where}: trim drops {dropped} event(s) of {spec['from']}'s sequence outside the span")
    if unknown:
        warn.append(f"{where}: {unknown} event(s) of {spec['from']}'s sequence are of a type this reflection cannot write: dropped")
    if one_sided:
        warn.append(f"{where}: mirror: events on the slot(s) {', '.join(sorted(one_sided))}, which have no other side, stay where they are")
    if offset_kept:
        warn.append(f"{where}: mirror: {offset_kept} event(s) have a local offset, kept as it is in the mirrored joint's frame")
    if blend and wsrc.sequences and any(w > 0 for w in weights):
        warn.append(f"{where}: blend: the events of {blend['with']} are dropped, the clip keeps those of {spec['from']}")
    if mirror and mirror.unplaced:
        warn.append(f"{where}: mirror: {len(mirror.unplaced)} joint(s) have no mirror image (mirrored in place under a centre joint, else kept "
                    f"on their parent): "
                    f"{_some([skel.names[j] for j in mirror.unplaced], 8)}")
    report = {"from": spec["from"], "ops": ops, "sourceDuration": round(src.duration, 6), "warnings": warn,
              "siblings": {"rootMotion": rm_note, "events": {"sequences": len(seqs), "kept": kept, "dropped": dropped + unknown}}}
    if mirror:
        report["mirror"] = mirror.report()
    return Result(frames, n, rm, seqs, report)


__all__ = ["KEYS", "Mirror", "OPS", "OpError", "Result", "Source", "check", "path_of", "rework"]
