#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""Turns pet clearance lab runs into clearance.json profiles (dossier 19d, gate C6).

  python3 -I tools/pets-clearance/profiles.py <runs.jsonl>... --out clearance.json [--report report.md] [--merge old.json]

Input: the lab mod's evidence (mods/tests/pets-clearance-lab: session, fixture, body, query, walk and native lines).
For each creature, scale and game build, every route pair (HorizontalSpace/VerticalSpace) is judged on every fixture:
  refused       kiln.queries gave no walk-only route that reaches the target (the pet would not walk it): safe;
  pass          the route was accepted, it crosses the measured opening, and Kiln.Pets' follower walked the body past the
                exit by at least its body length without clipping (its movement collider overlapping a wall or a ceiling),
                a stall, a push through a wall or off the floor, or a teleport;
  fail          the route was accepted and the walk was not a pass: the pet would take a route its body cannot walk;
  inconclusive  the answer is not known (unloaded/unknown query, an accepted route not walked, a route beside or over the
                opening, a teleported crossing).
A pair passes the gate when no fixture failed or stayed inconclusive and the wide/high control passed. A passing pair
vouches for the body only where the fixtures measured it: a passage in the world is a whole number of 0.5 m cells, so
when one cell narrower (or lower) than the body lies between the widest gap (highest headroom) the pair refused and the
narrowest (lowest) it was walked through, that passage was never measured and the pair might take it; such a pair is not
chosen. The chosen pair is the passing, vouching pair that walks the most fixtures; on a tie the larger vertical, then
the larger horizontal requirement (the one that refuses more of what the fixtures did not measure). A body wider than
the widest measured gap or taller than the highest measured headroom gets no profile: every pair it passes walks the
control only, which says nothing about the gaps between the two (calibrate it with wider or higher fixtures). Nor does a
body whose walker and collider Kiln.Pets did not read in the lab (its profile could never be checked).
Every session's evidence counts: one fail anywhere fails the pair. The profile is keyed by the game build the session
ran on: a game update invalidates it (Kiln.Pets checks the build and the body it was measured on).

Body profiles ("match": "body"): the routes kiln.queries finds do not depend on the creature, and the walk is the
movement collider's, so creatures with the same body (walker radius and height, collider width and height, the
template's own scale) walk the same. When at least 1 + SPOT walking creatures of one body were calibrated at a scale on
a build and every one of them chose the same pair, the file also gets a body profile with that pair: Kiln.Pets applies
it to every walking creature with that body that has no creature profile. One creature of the body without a profile,
or with another pair, and the body gets none.
"""
import argparse
import datetime
import json
import math
import sys
from dataclasses import dataclass, field

TOL_WALL = 0.10     # m the movement collider may meet a wall (contact, float noise) before it counts as clipping
TOL_HEAD = 0.10     # m the same for a ceiling
TOL_FLOOR = 0.6     # m off the floor that counts as pushed up or down
TOL_ROUTE = 0.30    # m a route point may stray outside the free space (path nodes sit on cell centres)
SAMPLE = 0.05       # m between samples of the free space's boundary and along a route
STEP = 0.5          # m: the build grid's cell; a passage in the world is a whole number of cells wide and high
SPOT = 2            # creatures of one body besides the first that a body profile needs
# answers that are not "no route": the game did not say (load the area, ask again); a refusal stands only on a real answer
UNKNOWN = {"Pending", "NotLoadedOrUnknown", "TimedOut", "NotCompleted", "QueueFull", "OwnerIdentityUnavailable", "OwnerGone",
           "Cancelled", "WorldClosed", "OwnerRetired", "Refused", "Truncated", "MalformedResult", "OutOfGrid"}
STALLS = {"Stuck", "LostAuthority", "NoRoute", "Timeout", "PlaceFailed"}


# ---- the body ----

@dataclass
class Body:
    walker_radius: float
    walker_height: float
    collider_width: float
    collider_height: float
    scale: float
    length: float
    kiln: dict = field(default_factory=dict)
    template_scale: float = 1.0
    walker: bool = True
    read: bool = True     # Kiln.Pets read the walker and collider in the lab (the numbers its check compares)

    @property
    def size(self):
        """The body's scale as a player sees it: the pet's scale times the template's own."""
        return self.scale * self.template_scale

    @property
    def width(self):
        return self.collider_width * self.size

    @property
    def radius(self):
        return self.width / 2

    @property
    def height(self):
        return self.collider_height * self.size

    @staticmethod
    def of(rec):
        k = rec.get("kilnBody") or {}
        names = ("walkerRadius", "walkerHeight", "colliderWidth", "colliderHeight")
        def finite(v):
            return isinstance(v, (int, float)) and not isinstance(v, bool) and math.isfinite(v)
        def num(name):
            v = k.get(name, rec.get(name))
            return float(v) if finite(v) else 0.0
        scale = float(rec.get("scale") or 1)
        lo, hi = rec.get("boundsMin") or [], rec.get("boundsMax") or []
        extents = [abs(b - a) for a, b in zip(lo, hi) if isinstance(a, (int, float)) and isinstance(b, (int, float))]
        horizontal = [extents[i] for i in (0, 2) if i < len(extents)]
        length = max(horizontal) * scale if horizontal else 0.0
        length = max(length, num("colliderWidth") * scale, 0.5)
        ts = k.get("templateScale", rec.get("templateScale"))
        ts = float(ts) if finite(ts) and ts > 0 else 1.0      # no ScaleComponent: 1
        walker = k.get("walker")
        if not isinstance(walker, bool):
            walker = rec.get("usePathFinding") not in (None, 0) and not rec.get("flying") and not rec.get("flyingEnemy")
        kiln = {n: num(n) for n in names}
        kiln["templateScale"] = ts
        read = isinstance(rec.get("kilnBody"), dict) and all(finite(k.get(n)) for n in names)
        return Body(kiln["walkerRadius"], kiln["walkerHeight"], kiln["colliderWidth"], kiln["colliderHeight"], scale, length, kiln,
                    ts, walker, read)

    @property
    def signature(self):
        """The body as Kiln.Pets compares it (its Same(): walker, collider, the template's own scale)."""
        return tuple(round(x, 3) for x in (self.walker_radius, self.walker_height, self.collider_width, self.collider_height,
                                           self.template_scale))


# ---- the fixture's free space ----

class Space:
    """A fixture's free space in XZ (the union of its rooms and passage boxes) and its boundary, sampled."""

    def __init__(self, f):
        self.f = f
        self.floor = float(f["floorY"])
        self.wall_top = float(f.get("wallTop") or self.floor + 5)
        self.boxes = [f["startRoom"], *f["passage"], f["exitRoom"]]
        self.passage = f["passage"]
        self.z0, self.z1 = f["passageZ"]
        self.buckets = {}
        for x0, x1, _, _, z0, z1 in self.boxes:
            for (ax, az, bx, bz, nx, nz) in ((x0, z0, x1, z0, 0, -1), (x0, z1, x1, z1, 0, 1), (x0, z0, x0, z1, -1, 0), (x1, z0, x1, z1, 1, 0)):
                n = max(1, int(round(math.hypot(bx - ax, bz - az) / SAMPLE)))
                for i in range(n + 1):
                    x, z = ax + (bx - ax) * i / n, az + (bz - az) * i / n
                    if not self.inside(x + nx * 1e-3, z + nz * 1e-3):
                        self.buckets.setdefault((math.floor(x), math.floor(z)), []).append((x, z))

    def inside(self, x, z, tol=0.0):
        return any(b[0] - tol <= x <= b[1] + tol and b[4] - tol <= z <= b[5] + tol for b in self.boxes)

    def clearance(self, x, z):
        """Metres from (x, z) to the nearest wall (up to 2 m; more is 2); negative when (x, z) is inside a wall."""
        if not self.inside(x, z, 0.02):
            return -1.0
        best = 2.0
        for i in range(math.floor(x) - 2, math.floor(x) + 3):
            for j in range(math.floor(z) - 2, math.floor(z) + 3):
                for (bx, bz) in self.buckets.get((i, j), ()):
                    best = min(best, math.hypot(bx - x, bz - z))
        return best

    def headroom(self, x, z):
        """The headroom over (x, z) when it is under a passage ceiling, else None."""
        for b in self.passage:
            if b[0] <= x <= b[1] and b[4] <= z <= b[5] and b[3] < self.wall_top - 1e-6:
                return b[3] - self.floor
        return None

    def in_passage(self, x, z, tol=0.0):
        return any(b[0] - tol <= x <= b[1] + tol and b[4] - tol <= z <= b[5] + tol for b in self.passage)


_spaces = {}


def space(f):
    key = id(f)
    if key not in _spaces:
        _spaces[key] = Space(f)
    return _spaces[key]


def route_crossing(f, points):
    """Whether a route goes through the measured opening: every sample of it inside the passage's z range lies in the
    passage (not beside or over it), on the floor, and it starts before and ends after it. (True, None) or (False, why)."""
    s = space(f)
    if len(points) < 2:
        return False, "the route has fewer than 2 points"
    if not (points[0][2] < s.z0 and points[-1][2] > s.z1):
        return False, "the route does not run from before the passage to after it"
    for a, b in zip(points, points[1:]):
        d = math.dist(a[:3], b[:3])
        n = max(1, int(d / 0.1))
        for i in range(n + 1):
            x, y, z = (a[k] + (b[k] - a[k]) * i / n for k in range(3))
            if y > s.floor + 1.0:
                return False, f"the route runs above the floor (y {y - s.floor:.2f} m at z {z:.2f}): over a wall, not through the opening"
            if not s.inside(x, z, TOL_ROUTE):
                return False, f"the route leaves the fixture's free space at ({x:.2f}, {z:.2f})"
            if s.z0 + 0.05 < z < s.z1 - 0.05 and not s.in_passage(x, z, TOL_ROUTE):
                return False, f"the route runs outside the passage at ({x:.2f}, {z:.2f})"
    return True, None


def judge_walk(f, w, body):
    """("pass" | "fail" | "inconclusive", why) for one walk of the real follower."""
    s = space(f)
    trace = w.get("trace") or []
    if w.get("catchUps") or w.get("jumps"):
        return "inconclusive", f"teleported crossing (catch-ups {w.get('catchUps')}, jumps {w.get('jumps')})"
    if not trace:
        return "inconclusive", f"no trace ({w.get('outcome')})"
    worst_wall, worst_head, worst_floor = 9.0, None, 0.0
    crossed_at = None
    need = s.z1 + body.length
    for t, x, y, z in trace:
        if x is None or y is None or z is None:
            continue
        c = s.clearance(x, z)
        if c < 0:
            return "fail", f"pushed into a wall at ({x:.2f}, {z:.2f}) at {t:.1f} s"
        worst_wall = min(worst_wall, c)
        h = s.headroom(x, z)
        if h is not None and (worst_head is None or h < worst_head):
            worst_head = h
        worst_floor = max(worst_floor, abs(y - s.floor))
        if crossed_at is None and z >= need:
            crossed_at = t
    if worst_floor > TOL_FLOOR:
        return "fail", f"pushed off the floor ({worst_floor:.2f} m)"
    if worst_wall < body.radius - TOL_WALL:
        return "fail", f"clipped: {worst_wall:.2f} m from a wall with a collider radius of {body.radius:.2f} m"
    if worst_head is not None and body.height > worst_head + TOL_HEAD:
        return "fail", f"clipped through the ceiling: {body.height:.2f} m tall under {worst_head:.2f} m of headroom"
    if crossed_at is None:
        far = max((p[3] for p in trace if p[3] is not None), default=float("nan"))
        return "fail", f"{w.get('outcome')}: never {body.length:.2f} m (its length) past the exit (got {far - s.z1:+.2f} m)"
    return "pass", f"{body.length:.2f} m past the exit at {crossed_at:.1f} s, nearest wall {worst_wall:.2f} m"


# ---- the evidence ----

@dataclass
class Session:
    game: object
    fixtures: dict = field(default_factory=dict)
    bodies: dict = field(default_factory=dict)
    queries: list = field(default_factory=list)
    walks: dict = field(default_factory=dict)
    natives: dict = field(default_factory=dict)
    refused: dict = field(default_factory=dict)


def pair_of(r):
    return (num(r["horizontalSpace"]), num(r["verticalSpace"]))


def num(v):
    v = float(v)
    return int(v) if v == int(v) else v


def load_records(records):
    """Records (dicts) in file order -> sessions; a "session" line starts a new one."""
    sessions = []
    cur = None
    for r in records:
        k = r.get("kind")
        if k == "session" or cur is None:
            cur = Session(game=r.get("game") if k == "session" else None)
            sessions.append(cur)
            if k == "session":
                continue
        key = (r.get("creature"), float(r.get("scale") or 1))
        if k == "fixture":
            cur.fixtures[r["name"]] = r
        elif k == "body":
            cur.bodies[key] = r
        elif k == "creature" and r.get("refused"):
            cur.refused[key] = r["refused"]
        elif k == "query" and "horizontalSpace" in r:
            cur.queries.append(r)
        elif k == "walk":
            cur.walks.setdefault((*key, r["fixture"], pair_of(r)), []).append(r)
        elif k == "native":
            cur.natives.setdefault((*key, r.get("fixture")), []).append(r)
    return sessions


def read(paths):
    records = []
    for p in paths:
        with open(p) as fh:
            for line in fh:
                line = line.strip()
                if line:
                    records.append(json.loads(line))
    return records


def merge_verdicts(vs):
    kinds = {v for v, _ in vs}
    for k in ("fail", "inconclusive", "pass"):
        if k in kinds:
            return k, "; ".join(w for v, w in vs if v == k and w)
    return "refused", "; ".join(w for _, w in vs if w)


def decide(sessions):
    """{(creature, scale, game): {"body", "pairs": {(h, v): {...}}, "chosen", "why", "native", "fixtures"}}."""
    found = {}
    for s in sessions:
        for (creature, scale), b in s.bodies.items():
            key = (creature, scale, s.game)
            d = found.setdefault(key, {"body": Body.of(b), "verdicts": {}, "native": {}, "fixtures": {}, "control": set(), "sessions": 0})
            d["sessions"] += 1
            for name, f in s.fixtures.items():
                d["fixtures"][name] = f
                if f.get("fixtureKind") == "control":
                    d["control"].add(name)
            for q in s.queries:
                if (q.get("creature"), float(q.get("scale") or 1)) != (creature, scale) or q["fixture"] not in s.fixtures:
                    continue
                f = s.fixtures[q["fixture"]]
                pair = pair_of(q)
                if q.get("accepted"):
                    ok, why = route_crossing(f, q.get("points") or [])
                    if not ok:
                        v = ("inconclusive", f"accepted, but {why}")
                    else:
                        ws = s.walks.get((creature, scale, q["fixture"], pair))
                        if not ws:
                            v = ("inconclusive", "accepted, not walked")
                        else:
                            v = merge_verdicts([judge_walk(f, w, d["body"]) for w in ws])
                elif q.get("status") in UNKNOWN or q.get("error"):
                    v = ("inconclusive", f"not known: {q.get('status') or q.get('error')}")
                else:
                    v = ("refused", q.get("refusal") or q.get("status"))
                d["verdicts"].setdefault((q["fixture"], pair), []).append(v)
            for (c, sc, fx), ns in s.natives.items():
                if (c, sc) == (creature, scale) and fx in s.fixtures:
                    n = ns[-1]
                    f = s.fixtures[fx]
                    far = max((p[3] for p in n.get("trace") or [] if p[3] is not None), default=None)
                    d["native"][fx] = {"outcome": n.get("outcome"), "past": None if far is None else round(far - f["passageZ"][1], 2),
                                       "walker": (n.get("walker") or [[None, None]])[-1][1]}
    result = {}
    for key, d in found.items():
        cells = {k: merge_verdicts(vs) for k, vs in d["verdicts"].items()}
        pairs = {}
        for pair in sorted({p for _, p in cells}):
            row = {name: cells[(name, pair)] for name, p in cells if p == pair}
            failed = sorted(n for n, (v, _) in row.items() if v == "fail")
            unknown = sorted(n for n, (v, _) in row.items() if v == "inconclusive")
            passed = sorted(n for n, (v, _) in row.items() if v == "pass")
            refused = sorted(n for n, (v, _) in row.items() if v == "refused")
            control_ok = bool(d["control"]) and all(row.get(c, ("missing", ""))[0] == "pass" for c in d["control"])
            why = None
            if failed:
                why = "accepted a route the body cannot walk: " + ", ".join(failed)
            elif unknown:
                why = "inconclusive: " + ", ".join(unknown)
            elif not control_ok:
                c = next(iter(d["control"]), None)
                why = "the control passage is not a pass" + (f" ({row.get(c, ('missing', 'not measured'))[0]}: {row.get(c, ('', 'not measured'))[1]})" if c else " (no control fixture)")
            pairs[pair] = {"passes": why is None, "why": why, "failed": failed, "inconclusive": unknown, "passed": passed,
                           "refused": refused, "row": row}
        good = [p for p, r in pairs.items() if r["passes"]]
        for p in good:
            pairs[p]["unmeasured"] = unmeasured(d["body"], d["fixtures"].values(), pairs[p]["row"])
        vouching = [p for p in good if not pairs[p]["unmeasured"]]
        rank = lambda p: (len(pairs[p]["passed"]), p[1], p[0])  # noqa: E731
        chosen = max(vouching, key=rank) if vouching else None
        why = None
        unjudged = beyond(d["body"], d["fixtures"].values())
        if key[2] is None:
            chosen, why = None, "no game build: the session did not know the running game build"
        elif not d["body"].read:
            chosen, why = None, "Kiln.Pets did not read this body's walker and collider in the lab, so a profile could never be checked against it"
        elif not good:
            why = "no pair passes"
        elif unjudged:
            chosen, why = None, unjudged
        elif chosen is None:
            best = max(good, key=rank)
            why = f"no passing pair vouches for this body: {fmt(best)} {pairs[best]['unmeasured']}"
        result[key] = {"body": d["body"], "pairs": pairs, "chosen": chosen, "why": why, "native": d["native"],
                       "fixtures": d["fixtures"], "sessions": d["sessions"]}
    return result


def beyond(body, fixtures):
    """Why the fixtures cannot judge the body (wider than the widest measured gap, taller than the highest measured
    headroom, the control is wider and higher than both), or None."""
    fs = [f for f in fixtures if f.get("fixtureKind") != "control"]
    gaps = [float(f["gap"]) for f in fs if isinstance(f.get("gap"), (int, float))]
    heads = [float(f["headroom"]) for f in fs if isinstance(f.get("headroom"), (int, float))]
    width, height = body.width, body.height
    if not gaps or not heads:
        return "the fixtures cannot judge this body: no measured gap or headroom besides the control"
    if width > max(gaps) + 1e-6:
        return f"the fixtures cannot judge this body: {width:g} m wide, the widest measured gap is {max(gaps):g} m"
    if height > max(heads) + 1e-6:
        return f"the fixtures cannot judge this body: {height:g} m tall, the highest measured headroom is {max(heads):g} m"
    return None


def _value(v):
    return float(v) if isinstance(v, (int, float)) else math.inf      # no headroom: open to the sky


def unmeasured(body, fixtures, row):
    """Why a passing pair cannot vouch for the body, or None. On each axis (gaps: the fixtures with the highest measured
    headroom or none; headrooms: those with the widest measured gap), the passages between the widest/highest the pair
    refused and the narrowest/lowest it was walked through were never measured. A passage one 0.5 m cell narrower or
    lower than the body among them is one the pair might take and the body cannot walk."""
    fs = [f for f in fixtures if f.get("fixtureKind") != "control"]
    gaps = [float(f["gap"]) for f in fs if isinstance(f.get("gap"), (int, float))]
    heads = [float(f["headroom"]) for f in fs if isinstance(f.get("headroom"), (int, float))]
    if not gaps or not heads:
        return None   # beyond() says why
    top, wide = max(heads), max(gaps)
    eps = 1e-6
    axes = (
        ("gap", "wide", body.width, [f for f in fixtures if _value(f.get("headroom")) >= top - eps], lambda f: _value(f.get("gap"))),
        ("headroom", "tall", body.height, [f for f in fixtures if _value(f.get("gap")) >= wide - eps], lambda f: _value(f.get("headroom"))),
    )
    for axis, adj, size, family, value in axes:
        refused = [value(f) for f in family if row.get(f["name"], ("",))[0] == "refused"]
        walked = [value(f) for f in family if row.get(f["name"], ("",))[0] == "pass"]
        r = max(refused, default=0.0)
        a = min((w for w in walked if w > r + eps), default=math.inf)
        need = STEP * (math.ceil(size / STEP - eps) - 1)    # the widest/highest whole-cell passage the body cannot walk
        if r + STEP <= need + eps and r + STEP < a - eps:
            return (f"leaves a {r + STEP:g} m {axis} unmeasured (it refused {r:g} m and was walked through "
                    f"{'none narrower than the control' if a == math.inf else format(a, 'g') + ' m'}; the body is {size:g} m {adj})")
    return None


# ---- body profiles ----

def body_profiles(result):
    """{(signature, scale, game): {"body", "chosen", "creatures", "why"}}: per body (Body.signature) at a scale on a build,
    a pair when at least 1 + SPOT walking creatures with that body were calibrated and every one chose it."""
    groups = {}
    for (creature, scale, game), d in result.items():
        b = d["body"]
        if game is None or not b.read:
            continue
        groups.setdefault((b.signature, scale, game), []).append((creature, d))
    out = {}
    for key, members in sorted(groups.items()):
        creatures = sorted(c for c, _ in members)
        chosen = {d["chosen"] for _, d in members}
        why = None
        if not all(d["body"].walker for _, d in members):
            why = "a calibrated creature of this body does not walk (no path finding, or it flies): " + \
                  ", ".join(c for c, d in members if not d["body"].walker)
        elif None in chosen:
            why = "a calibrated creature of this body has no profile: " + ", ".join(c for c, d in members if d["chosen"] is None)
        elif len(chosen) > 1:
            why = "the calibrated creatures of this body chose different pairs: " + \
                  ", ".join(f"{c} {fmt(d['chosen'])}" for c, d in sorted(members))
        elif len(members) < 1 + SPOT:
            why = f"{len(members)} creature(s) of this body calibrated; a body profile needs {1 + SPOT}"
        out[key] = {"body": members[0][1]["body"], "chosen": None if why else next(iter(chosen)), "creatures": creatures,
                    "why": why}
    return out


# ---- the outputs ----

def fmt(p):
    return f"{p[0]}/{p[1]}"


def _merge_key(p):
    if p.get("match") == "body":
        b = p.get("body") or {}
        sig = tuple(round(float(b.get(n, 1 if n == "templateScale" else 0)), 3)
                    for n in ("walkerRadius", "walkerHeight", "colliderWidth", "colliderHeight", "templateScale"))
        return ("body", sig, float(p.get("scale") or 1), p.get("game"))
    return (p.get("creature"), float(p.get("scale") or 1), p.get("game"))


def clearance(result, lab="lab", merge=None, date=None):
    """clearance.json format 1 (Kiln.Pets' PetClearance.Parse): the chosen pairs, creature profiles then body profiles;
    merged profiles of other keys kept."""
    date = date or datetime.date.today().isoformat()
    profiles = []
    for (creature, scale, game), d in sorted(result.items(), key=lambda kv: (kv[0][0], kv[0][1], kv[0][2] or 0)):
        if d["chosen"] is None:
            continue
        h, v = d["chosen"]
        p = d["pairs"][d["chosen"]]
        profiles.append({
            "creature": creature, "scale": scale, "game": game, "horizontalSpace": h, "verticalSpace": v, "body": d["body"].kiln,
            "calibration": {
                "lab": lab, "date": date, "sessions": d["sessions"], "gate": "19d C6", "passed": p["passed"], "refused": p["refused"],
                "pairs": {fmt(q): ("pass" if r["passes"] else r["why"]) for q, r in sorted(d["pairs"].items())},
                "bodyLength": round(d["body"].length, 2),
            },
        })
    bodies = body_profiles(result)
    for (sig, scale, game), g in bodies.items():
        if g["chosen"] is None:
            continue
        h, v = g["chosen"]
        profiles.append({
            "match": "body", "scale": scale, "game": game, "horizontalSpace": h, "verticalSpace": v, "body": g["body"].kiln,
            "calibration": {
                "lab": lab, "date": date, "gate": "19d C6, by body", "creatures": g["creatures"],
                "rule": f"every one of the {len(g['creatures'])} walking creatures of this body calibrated at this scale on this build chose this pair",
            },
        })
    done = {_merge_key({"creature": c, "scale": sc, "game": gm}) for c, sc, gm in result}
    done |= {("body", sig, sc, gm) for sig, sc, gm in bodies}
    for old in (merge or {}).get("profiles", []):
        if _merge_key(old) not in done:
            profiles.append(old)
    profiles.sort(key=lambda p: (p.get("match") == "body", p.get("creature") or "", _merge_key(p)[1] if p.get("match") == "body" else (),
                                 float(p.get("scale") or 1), p.get("game") or 0))
    return {"format": 1, "generator": "tools/pets-clearance/profiles.py (dossier 19d, gate C6)", "profiles": profiles}


SYM = {"pass": "walks", "fail": "FAIL", "inconclusive": "?", "refused": "-"}


def report(result):
    out = ["# Pet clearance calibration", "",
           "Cells: `walks` = accepted and walked past the exit; `FAIL` = accepted but the body could not walk it; `-` = no route "
           "(refused); `?` = inconclusive. Native = the body's own walker (the game's guard walk), for comparison.", ""]
    for (creature, scale, game), d in sorted(result.items(), key=lambda kv: (kv[0][0], kv[0][1], kv[0][2] or 0)):
        b = d["body"]
        out.append(f"## {creature} at scale {scale:g}, game build {game}")
        out.append("")
        out.append(f"Body: walker radius {b.walker_radius:g} height {b.walker_height:g}; movement collider width {b.collider_width:g} "
                   f"height {b.collider_height:g}; length {b.length:.2f} m (x{b.scale:g}). Sessions: {d['sessions']}.")
        out.append("")
        if d["chosen"]:
            out.append(f"**Chosen: HorizontalSpace {d['chosen'][0]}, VerticalSpace {d['chosen'][1]}** (gate passes).")
        else:
            out.append(f"**No profile: {d['why']}.**")
        out.append("")
        pairs = sorted(d["pairs"])
        names = sorted({n for p in pairs for n in d["pairs"][p]["row"]}, key=lambda n: (d["fixtures"].get(n, {}).get("lane", 99), n))
        out.append("| fixture | " + " | ".join(fmt(p) for p in pairs) + " | native |")
        out.append("|---" * (len(pairs) + 2) + "|")
        for n in names:
            cells = []
            for p in pairs:
                v = d["pairs"][p]["row"].get(n)
                cells.append(SYM.get(v[0], v[0]) if v else "")
            nat = d["native"].get(n)
            out.append(f"| {n} | " + " | ".join(cells) + f" | {(nat['outcome'] + ' ' + format(nat['past'], '+.2f') + ' m') if nat and nat['past'] is not None else (nat or {}).get('outcome', '')} |")
        out.append("")
        for p in pairs:
            r = d["pairs"][p]
            out.append(f"- {fmt(p)}: {'passes' if r['passes'] else r['why']}" +
                       (f"; but it {r['unmeasured']}" if r.get("unmeasured") else ""))
            for n, (v, why) in sorted(r["row"].items()):
                if v in ("fail", "inconclusive") and why:
                    out.append(f"  - {n}: {v}: {why}")
        out.append("")
    bodies = body_profiles(result)
    if bodies:
        out += ["## Body profiles", "",
                f"A body profile needs {1 + SPOT} or more walking creatures of one body (walker radius/height, collider "
                "width/height, template scale) calibrated at the scale on the build, every one with the same pair.", "",
                "| body (walker r/h, collider w/h, template scale) | scale | build | creatures | profile |", "|---|---|---|---|---|"]
        for (sig, scale, game), g in bodies.items():
            out.append(f"| {' / '.join(format(x, 'g') for x in sig)} | {scale:g} | {game} | {', '.join(g['creatures'])} | "
                       f"{fmt(g['chosen']) if g['chosen'] else 'none: ' + g['why']} |")
        out.append("")
    return "\n".join(out) + "\n"


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("runs", nargs="+")
    ap.add_argument("--out", required=True)
    ap.add_argument("--report")
    ap.add_argument("--merge", help="an existing clearance.json whose profiles of other creatures, scales or builds are kept")
    ap.add_argument("--lab", default="lab")
    a = ap.parse_args(argv)
    result = decide(load_records(read(a.runs)))
    merge = json.load(open(a.merge)) if a.merge else None
    with open(a.out, "w") as fh:
        json.dump(clearance(result, a.lab, merge), fh, indent=2)
        fh.write("\n")
    if a.report:
        with open(a.report, "w") as fh:
            fh.write(report(result))
    for (creature, scale, game), d in sorted(result.items(), key=lambda kv: (kv[0][0], kv[0][1], kv[0][2] or 0)):
        print(f"{creature} x{scale:g} build {game}: " + (f"{fmt(d['chosen'])}" if d["chosen"] else f"none ({d['why']})"))
    for (sig, scale, game), g in body_profiles(result).items():
        print(f"body {'/'.join(format(x, 'g') for x in sig)} x{scale:g} build {game} ({len(g['creatures'])} creatures): " +
              (fmt(g["chosen"]) if g["chosen"] else f"none ({g['why']})"))
    return 0


if __name__ == "__main__":
    sys.exit(main())
