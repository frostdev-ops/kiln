#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
# tools/pets-clearance/profiles.py: the C6 judgement (dossier 19d) of lab runs, from synthetic runs.jsonl records.
#   python3 -I tools/pets-clearance/test_profiles.py
import json
import os
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import profiles as prof  # noqa: E402

FLOOR = 100.0
GAME = 1287369
WOLF = {"walkerRadius": 0, "walkerHeight": 2, "colliderWidth": 0.8, "colliderHeight": 1.4}


def lane(name, kind, gap, head, x0=0.0):
    """A lane 5 m wide (x0..x0+5), start room z 1..8, the passage 8..11 (gap centred, head m high or open), exit 11..20."""
    cx = x0 + 2.5
    passage = [[cx - gap / 2, cx + gap / 2, FLOOR, FLOOR + (head or 5), 8.0, 11.0]]
    return {
        "kind": "fixture", "name": name, "fixtureKind": kind, "gap": gap, "headroom": head, "length": 3,
        "floorY": FLOOR, "wallTop": FLOOR + 5, "start": [cx, FLOOR + 0.05, 3.5], "target": [cx, FLOOR + 0.05, 16.0],
        "passageZ": [8.0, 11.0], "startRoom": [x0, x0 + 5, FLOOR, FLOOR + 5, 1.0, 8.0], "exitRoom": [x0, x0 + 5, FLOOR, FLOOR + 5, 11.0, 20.0],
        "passage": passage, "ceiling": None if head is None else [x0, x0 + 5, FLOOR + head, FLOOR + 5, 8.0, 11.0],
    }


FIXTURES = [
    lane("corridor-w0.5-h3", "corridor", 0.5, 3, 0),
    lane("corridor-w1-h3", "corridor", 1, 3, 6),
    lane("corridor-w1-h1", "corridor", 1, 1, 12),
    lane("control-w5-open", "control", 5, None, 18),
]
BY = {f["name"]: f for f in FIXTURES}


def straight(f, x=None):
    cx = (f["startRoom"][0] + f["startRoom"][1]) / 2 if x is None else x
    return [[cx, FLOOR + 0.05, z, 0] for z in (3.5, 6.0, 8.5, 9.5, 10.5, 13.0, 16.0)]


def query(f, h, v, accepted=True, status="Found", points=None, creature="game:enemy_wildbeast_wolf", scale=1):
    return {"kind": "query", "creature": creature, "scale": scale, "fixture": f["name"], "horizontalSpace": h, "verticalSpace": v,
            "ok": status in ("Found", "Partial"), "status": status, "reaches": accepted, "accepted": accepted,
            "points": straight(f) if points is None else points}


def walk(f, h, v, outcome="Arrived", trace=None, x=None, creature="game:enemy_wildbeast_wolf", scale=1, catch_ups=0, jumps=0):
    cx = (f["startRoom"][0] + f["startRoom"][1]) / 2 if x is None else x
    if trace is None:
        trace = [[i * 0.1, cx, FLOOR + 0.02, 3.5 + i * 0.25] for i in range(int((15.0 - 3.5) / 0.25) + 1)]
    return {"kind": "walk", "creature": creature, "scale": scale, "fixture": f["name"], "horizontalSpace": h, "verticalSpace": v,
            "outcome": outcome, "catchUps": catch_ups, "jumps": jumps, "trace": trace, "end": trace[-1][1:] if trace else None}


def body(creature="game:enemy_wildbeast_wolf", scale=1, kiln=WOLF, walker=True, template_scale=None):
    return {"kind": "body", "creature": creature, "scale": scale, "control": "Ready",
            "kilnBody": None if kiln is None else {**kiln, "walker": walker, **({} if template_scale is None else {"templateScale": template_scale})},
            "colliderWidth": (kiln or WOLF)["colliderWidth"], "colliderHeight": (kiln or WOLF)["colliderHeight"],
            "walkerRadius": (kiln or WOLF)["walkerRadius"], "walkerHeight": (kiln or WOLF)["walkerHeight"],
            "usePathFinding": 1 if walker else 0, "flying": False, "flyingEnemy": False, "templateScale": template_scale,
            "boundsMin": [-0.4, 0, -0.8], "boundsMax": [0.4, 1.2, 0.8], "liveScale": scale}


def session(game=GAME):
    return [{"kind": "session", "game": game}] + FIXTURES


class Judge(unittest.TestCase):
    def test_a_route_through_the_measured_opening_crosses_it(self):
        f = BY["corridor-w1-h3"]
        self.assertEqual(prof.route_crossing(f, straight(f)), (True, None))

    def test_a_route_over_a_wall_or_beside_the_opening_bypasses_it(self):
        f = BY["corridor-w1-h3"]
        beside = [[f["startRoom"][0] + 0.3, FLOOR + 0.05, z, 0] for z in (3.5, 9.5, 16.0)]
        ok, why = prof.route_crossing(f, beside)
        self.assertFalse(ok)
        self.assertIn("outside the passage", why)
        over = [[p[0], FLOOR + 5.0 if 8 <= p[2] <= 11 else p[1], p[2], 0] for p in straight(f)]
        ok, why = prof.route_crossing(f, over)
        self.assertFalse(ok)
        self.assertIn("above", why)

    def test_a_clean_walk_past_the_exit_by_its_body_length_passes(self):
        f = BY["corridor-w1-h3"]
        v, why = prof.judge_walk(f, walk(f, 1, 2), prof.Body.of(body()))
        self.assertEqual(v, "pass", why)

    def test_a_stall_in_the_passage_fails(self):
        f = BY["corridor-w0.5-h3"]
        stuck = [[i * 0.1, 2.5, FLOOR + 0.02, min(7.6, 3.5 + i * 0.25)] for i in range(40)]
        v, why = prof.judge_walk(f, walk(f, 0.5, 1, outcome="Stuck", trace=stuck), prof.Body.of(body()))
        self.assertEqual(v, "fail")
        self.assertIn("Stuck", why)

    def test_a_body_wider_than_the_gap_that_goes_through_clipped(self):
        f = BY["corridor-w0.5-h3"]
        v, why = prof.judge_walk(f, walk(f, 0.5, 1), prof.Body.of(body()))   # 0.8 m wide through 0.5 m
        self.assertEqual(v, "fail")
        self.assertIn("clip", why)

    def test_a_body_pushed_into_a_wall_fails(self):
        f = BY["control-w5-open"]
        t = walk(f, 1, 2)["trace"]
        t[20] = [t[20][0], f["startRoom"][0] - 0.3, t[20][2], t[20][3]]   # outside the lane
        v, why = prof.judge_walk(f, walk(f, 1, 2, trace=t), prof.Body.of(body()))
        self.assertEqual(v, "fail")
        self.assertIn("wall", why)

    def test_a_body_taller_than_the_headroom_that_goes_under_it_clipped(self):
        f = BY["corridor-w1-h1"]
        v, why = prof.judge_walk(f, walk(f, 1, 1), prof.Body.of(body()))   # 1.4 m tall under 1 m
        self.assertEqual(v, "fail")
        self.assertIn("ceiling", why)

    def test_teleported_crossings_and_short_walks_are_inconclusive_or_failed(self):
        f = BY["control-w5-open"]
        self.assertEqual(prof.judge_walk(f, walk(f, 1, 2, catch_ups=1), prof.Body.of(body()))[0], "inconclusive")
        self.assertEqual(prof.judge_walk(f, walk(f, 1, 2, jumps=1), prof.Body.of(body()))[0], "inconclusive")
        short = [[i * 0.1, 20.5, FLOOR, 3.5 + i * 0.25] for i in range(32)]   # ends at z 11.25: not its length past 11
        v, why = prof.judge_walk(f, walk(f, 1, 2, outcome="Timeout", trace=short), prof.Body.of(body()))
        self.assertEqual(v, "fail")
        self.assertIn("past the exit", why)


def records(*extra, game=GAME):
    return session(game) + [body()] + list(extra)


class Gate(unittest.TestCase):
    def full(self, fail_narrow=False):
        """Pair 0.5/1 accepts every corridor; 1/2 refuses the 0.5 m one and the 1 m headroom; the control is walked."""
        out = []
        for f in FIXTURES:
            narrow = f["name"] == "corridor-w0.5-h3"
            low = f["name"] == "corridor-w1-h1"
            out.append(query(f, 0.5, 1))
            out.append(walk(f, 0.5, 1, outcome="Stuck" if (narrow or low) else "Arrived",
                            trace=[[i * 0.1, f["start"][0], FLOOR, min(7.5, 3.5 + i * 0.25)] for i in range(30)] if (narrow or low) else None))
            ok = not (narrow or low)
            out.append(query(f, 1, 2, accepted=ok, status="Found" if ok else "NoPath"))
            if ok:
                out.append(walk(f, 1, 2))
        return out

    def test_the_gate_picks_the_pair_with_no_untraversable_accepted_route(self):
        result = prof.decide(prof.load_records(records(*self.full())))
        (key, d), = result.items()
        self.assertEqual(key, ("game:enemy_wildbeast_wolf", 1.0, GAME))
        self.assertEqual(d["chosen"], (1, 2))
        self.assertFalse(d["pairs"][(0.5, 1)]["passes"])
        self.assertIn("corridor-w0.5-h3", d["pairs"][(0.5, 1)]["failed"])
        self.assertTrue(d["pairs"][(1, 2)]["passes"])

    def test_without_the_control_walked_no_pair_passes(self):
        recs = [r for r in self.full() if not (r["kind"] == "walk" and r["fixture"] == "control-w5-open")]
        (key, d), = prof.decide(prof.load_records(records(*recs))).items()
        self.assertIsNone(d["chosen"])
        self.assertIn("control", d["pairs"][(1, 2)]["why"])

    def test_an_unknown_query_answer_is_inconclusive_not_a_refusal(self):
        recs = [r for r in self.full() if not (r.get("fixture") == "corridor-w1-h3" and r.get("horizontalSpace") == 1)]
        recs.append(query(BY["corridor-w1-h3"], 1, 2, accepted=False, status="NotLoadedOrUnknown"))
        (key, d), = prof.decide(prof.load_records(records(*recs))).items()
        self.assertFalse(d["pairs"][(1, 2)]["passes"])
        self.assertIn("corridor-w1-h3", d["pairs"][(1, 2)]["inconclusive"])

    def test_a_fail_in_any_session_fails_the_pair(self):
        recs = records(*self.full()) + session() + [body(), query(BY["corridor-w1-h3"], 1, 2),
                                                    walk(BY["corridor-w1-h3"], 1, 2, outcome="Stuck",
                                                         trace=[[i * 0.1, 8.5, FLOOR, min(7.5, 3.5 + i * 0.25)] for i in range(30)])]
        (key, d), = prof.decide(prof.load_records(recs)).items()
        self.assertFalse(d["pairs"][(1, 2)]["passes"])

    def test_ties_go_to_the_larger_requirement_which_refuses_more_of_what_was_not_measured(self):
        out = []
        for f in FIXTURES:
            ok = f["name"] in ("corridor-w1-h3", "control-w5-open")
            for v in (2, 3):
                out.append(query(f, 1, v, accepted=ok, status="Found" if ok else "NoPath"))
                if ok:
                    out.append(walk(f, 1, v))
        (key, d), = prof.decide(prof.load_records(records(*out))).items()
        self.assertTrue(d["pairs"][(1, 2)]["passes"] and d["pairs"][(1, 3)]["passes"])
        self.assertEqual(d["chosen"], (1, 3))

    def test_a_body_the_fixtures_cannot_judge_gets_no_profile(self):
        """Wider than the widest measured gap (1 m here) or taller than the highest headroom (3 m): every pair that passes
        walks the control only, which says nothing about a gap between the two."""
        out = []
        for f in FIXTURES:
            ok = f["fixtureKind"] == "control"
            out.append(query(f, 2, 3, accepted=ok, status="Found" if ok else "NoPath"))
            if ok:
                out.append(walk(f, 2, 3))
        for kiln, word in (({**WOLF, "colliderWidth": 3.5}, "wide"), ({**WOLF, "colliderHeight": 3.5}, "tall")):
            recs = session() + [body(kiln=kiln)] + out
            (key, d), = prof.decide(prof.load_records(recs)).items()
            self.assertTrue(d["pairs"][(2, 3)]["passes"])
            self.assertIsNone(d["chosen"], word)
            self.assertIn("cannot judge", d["why"])
            self.assertIn(word, d["why"])
        (key, d), = prof.decide(prof.load_records(records(*out))).items()   # the wolf, 0.8 x 1.4 m, is judged
        self.assertEqual(d["chosen"], (2, 3))

    def test_no_game_build_no_profile(self):
        (key, d), = prof.decide(prof.load_records(records(*self.full(), game=None))).items()
        self.assertIsNone(d["chosen"])
        self.assertIn("game build", d["why"])


class Output(unittest.TestCase):
    def test_the_profile_file_is_what_Kiln_Pets_reads(self):
        result = prof.decide(prof.load_records(records(*Gate().full())))
        doc = prof.clearance(result, lab="test")
        self.assertEqual(doc["format"], 1)
        (p,) = doc["profiles"]
        self.assertEqual((p["creature"], p["scale"], p["game"], p["horizontalSpace"], p["verticalSpace"]),
                         ("game:enemy_wildbeast_wolf", 1.0, GAME, 1, 2))
        self.assertEqual(p["body"], {**WOLF, "templateScale": 1})
        self.assertEqual(p["calibration"]["passed"], ["control-w5-open", "corridor-w1-h3"])
        self.assertEqual(p["calibration"]["refused"], ["corridor-w0.5-h3", "corridor-w1-h1"])

    def test_a_creature_that_passes_no_pair_gets_no_profile_and_the_cli_writes_both_files(self):
        recs = [r for r in records(*Gate().full()) if not (r["kind"] == "walk" and r["fixture"] == "control-w5-open")]
        with tempfile.TemporaryDirectory() as d:
            runs = os.path.join(d, "runs.jsonl")
            with open(runs, "w") as fh:
                fh.write("\n".join(json.dumps(r) for r in recs) + "\n")
            rc = prof.main([runs, "--out", os.path.join(d, "c.json"), "--report", os.path.join(d, "r.md")])
            self.assertEqual(rc, 0)
            with open(os.path.join(d, "c.json")) as fh:
                self.assertEqual(json.load(fh)["profiles"], [])
            with open(os.path.join(d, "r.md")) as fh:
                report = fh.read()
            self.assertIn("game:enemy_wildbeast_wolf", report)
            self.assertIn("no pair passes", report)

    def test_existing_profiles_of_other_creatures_or_builds_are_kept_when_merging(self):
        old = {"format": 1, "profiles": [
            {"creature": "game:enemy_vukah_melee", "scale": 1, "game": GAME, "horizontalSpace": 0.5, "verticalSpace": 2, "body": WOLF},
            {"creature": "game:enemy_wildbeast_wolf", "scale": 1, "game": GAME, "horizontalSpace": 2, "verticalSpace": 3, "body": WOLF},
        ]}
        doc = prof.clearance(prof.decide(prof.load_records(records(*Gate().full()))), lab="t", merge=old)
        got = {(p["creature"], p["game"]): (p["horizontalSpace"], p["verticalSpace"]) for p in doc["profiles"]}
        self.assertEqual(got, {("game:enemy_vukah_melee", GAME): (0.5, 2), ("game:enemy_wildbeast_wolf", GAME): (1, 2)})


# ---- the review (dev32 review-pets-clearance) ----

class Review(unittest.TestCase):
    def test_a_body_whose_numbers_Kiln_Pets_did_not_read_gets_no_profile(self):
        """No kilnBody: Kiln.Pets could not read the walker and collider, so its check could never apply the profile, and
        the judge would take a 0 m collider (no clipping is ever seen)."""
        unread = body(kiln=None)
        unread["colliderWidth"] = unread["colliderHeight"] = None
        for b in (unread, body(kiln=None)):
            recs = session() + [b] + Gate().full()
            (key, d), = prof.decide(prof.load_records(recs)).items()
            self.assertIsNone(d["chosen"])
            self.assertIn("did not read", d["why"])
            self.assertEqual(prof.clearance({key: d}, lab="t")["profiles"], [])


if __name__ == "__main__":
    unittest.main()
