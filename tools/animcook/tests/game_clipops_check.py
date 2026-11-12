#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""Checks the clip operations (`"from"`, anicook/clipops.py) against the clips of your installed game. Reads the game, writes nothing but its
report under --out.

    python3 tools/animcook/tests/game_clipops_check.py [--clips N] [--clip GUID ...] [--out DIR]

On the player's skeleton: what the mirror pairing finds (pairs, centre joints, joints it could not place), then for each clip four round trips,
each step through the encoder as a cook runs it, compared with the game's own clip in model space:
- mirror twice, reverse twice, a trim of the whole span: the game's clip again within the codec's tolerance;
- speed 2 then speed 0.5: the same, apart from what playing at 30 samples a second in between loses on fast motion;
- speed 2 and speed 0.5 alone, against the game's clip at twice (half) the time.
Root motion is compared the same way (mirror twice, reverse twice), and the events must come back in number and time.

Options: --game DIR, --out DIR (default $KILN_OUT/clipopscheck). Prints a summary; the details go to OUT/clipops.json.
"""
import argparse
import json
import os
import statistics
import sys
import time
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path[:0] = [str(HERE), str(HERE.parent), str(HERE.parent.parent / "kfc"), str(HERE.parent.parent / "modelcook")]

from anicook import clipops as CO, clips as C, retarget as RT, skeleton as K  # noqa: E402
from anicook.gltfdoc import compose, qnorm  # noqa: E402
from kfc import anim  # noqa: E402
from kfc.archive import Kfc, ResourceKey  # noqa: E402
from kfc.blob import Decoder  # noqa: E402
from kfc.refl import TypeDb  # noqa: E402

GAME = Path.home() / ".local/share/Steam/steamapps/common/Enshrouded"
PLAYER = "87b29df6-6518-43ef-9298-e3f85a6204b9"
BLOCK_IDLE = "263941ec-3361-42b0-96e2-35a29f22224b"
LABELS = ("mirror2", "reverse2", "speed2x0.5", "trimAll", "speed2", "speed0.5")


def model_space(sk, frame):
    """As retarget.model_space, but a zero scale (the game hides joints so) is allowed."""
    out = [None] * len(sk)
    for j in RT._order(sk):
        local = (tuple(frame[1][j]), qnorm(tuple(frame[0][j])), tuple(frame[2][j]))
        p = sk.parents[j]
        out[j] = local if p == 0xFFFF else compose(out[p], local)
    return out


def compare(sk, a, b):
    """Worst joint rotation (degrees) and position (metres) in model space between two clips, at the same fraction of their length."""
    wq = wp = 0.0
    for k in range(b.n + 1):
        ma = model_space(sk, a.pose(k / b.n * a.n))
        mb = model_space(sk, b.pose(k))
        for j in range(len(sk)):
            wq = max(wq, CO.qangle(ma[j][1], mb[j][1]))
            wp = max(wp, max(abs(x - y) for x, y in zip(ma[j][0], mb[j][0])))
    return wq, wp


def path_error(a, b):
    """Worst root motion position difference (metres) between two clips, at the same fraction of their length."""
    if a.rm is None or b.rm is None:
        return None if a.rm is b.rm else float("inf")
    return max(max(abs(x - y) for x, y in zip(a.rm_at(k / b.n * a.n)[0], b.rm_at(k)[0])) for k in range(b.n + 1))


def events_of(seqs):
    return sorted((e["$type"], e["value"]["time"]["value"]) for s in seqs for e in s["events"] if "value" in e)


def step(sk, src, spec, slots):
    """One operation through the encoder: -> the decoded result as a Source, and the rework's report."""
    res = CO.rework("check", dict(spec, **{"from": src.label}), src, sk, None, slots=slots)
    raw, info, _, _ = C.finish_clip(res.frames, res.n, sk, "check", loop=src.loop)
    return CO.Source(src.label, anim.parse(raw), sk, rm=res.rm, sequences=res.sequences), res.report, info


def check_clip(sk, src, slots):
    out = {"guid": src.label, "frames": src.n, "loop": src.loop, "rootMotion": src.rm is not None,
           "events": sum(len(s["events"]) for s in src.sequences)}
    trips = {"mirror2": [{"mirror": True}, {"mirror": True}], "reverse2": [{"reverse": True}, {"reverse": True}],
             "speed2x0.5": [{"speed": 2.0}, {"speed": 0.5}], "trimAll": [{"trim": [0.0, src.duration]}],
             "speed2": [{"speed": 2.0}], "speed0.5": [{"speed": 0.5}]}
    for label, specs in trips.items():
        cur = src
        warnings = []
        enc = 0.0
        for spec in specs:
            cur, rep, info = step(sk, cur, spec, slots)
            warnings += [w for w in rep["warnings"] if "no mirror image" not in w]
            enc = max(enc, info["errors"]["rotationDeg"])
        wq, wp = compare(sk, src, cur)
        r = {"rotationDeg": round(wq, 4), "positionM": round(wp, 5), "frames": cur.n, "encoderDeg": round(enc, 4)}
        if src.rm is not None:
            pe = path_error(src, cur)
            r["rootMotionM"] = None if pe is None else round(pe, 6)
        if src.sequences:
            a, b = events_of(src.sequences), events_of(cur.sequences)
            r["eventsBack"] = len(b) == len(a) and [t for t, _ in a] == [t for t, _ in b]
            r["eventTimeErrS"] = round(max((abs(x[1] - y[1]) / 1e9 for x, y in zip(a, b)), default=0.0), 6)
        if warnings:
            r["warnings"] = warnings
        out[label] = r
    return out


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--game", default=str(GAME))
    ap.add_argument("--out", default=os.path.join(os.environ.get("KILN_OUT", str(HERE.parent.parent.parent / ".out" / "models")), "clipopscheck"))
    ap.add_argument("--clips", type=int, default=40, help="clips to check (the block idle, then clips with root motion, then with events, then others)")
    ap.add_argument("--clip", action="append", default=[], help="a clip GUID to check as well")
    a = ap.parse_args()
    game = Path(a.game)
    k, t = Kfc(game), TypeDb.from_exe(game / "enshrouded.exe")
    dec = Decoder(t, max_array=1 << 26)
    mh, ja = t.get("keen::ModelHierarchyResource"), t.get("keen::JointAnimation")
    rmt, asc, srt = t.get("keen::RootMotionAnimation"), t.get("keen::AnimationSequenceContainer"), t.get("keen::SequenceResource")
    slots = [v["name"] for v in t.get("keen::AttachmentSlot")["values"]]
    sk = K.from_game(dec.decode(mh, k.read(ResourceKey(PLAYER, mh["hash"], 0))), PLAYER)
    t0 = time.time()
    mirror = CO.Mirror(sk).report()
    print(f"player skeleton {len(sk)} joints: {len(mirror['pairs'])} pairs, {mirror['centre']} centre, unplaced {mirror['unplaced']} "
          f"(kept on their parent: {mirror['keptOnParent']}), tolerance {mirror['tolerance'] * 1000:.1f} mm", flush=True)
    player = []
    for key in k.resources_of_type(ja["hash"]):
        raw = k.read(key)
        if anim.parse(raw).hierarchy_hash == sk.game_hash:
            player.append(str(key.guid))
    player.sort()

    def has(g, tt):
        return k.has(ResourceKey(g, tt["hash"], 0))

    want = [BLOCK_IDLE] + a.clip
    with_rm = [g for g in player if has(g, rmt) and g not in want]
    with_ev = [g for g in player if has(g, asc) and not has(g, rmt) and g not in want]
    rest = [g for g in player if g not in want and g not in with_rm and g not in with_ev]
    n_rm = max(0, (a.clips - len(want)) * 3 // 8)
    n_ev = max(0, (a.clips - len(want)) * 3 // 8)
    chosen = want + with_rm[:n_rm] + with_ev[:n_ev]
    chosen += rest[:max(0, a.clips - len(chosen))]
    results = []
    worst = {}
    for g in chosen:
        clip = anim.parse(k.read(ResourceKey(g, ja["hash"], 0)))
        rm = CO.path_of(dec.decode(rmt, k.read(ResourceKey(g, rmt["hash"], 0)))) if has(g, rmt) else None
        seqs = []
        if has(g, asc):
            for sg in dec.decode(asc, k.read(ResourceKey(g, asc["hash"], 0))).get("sequences", []):
                if k.has(ResourceKey(sg, srt["hash"], 0)):
                    seqs.append(dec.decode(srt, k.read(ResourceKey(sg, srt["hash"], 0))))
        if clip.frame_count < 2:
            continue
        r = check_clip(sk, CO.Source(g, clip, sk, rm=rm, sequences=seqs), slots)
        results.append(r)
        for lab in LABELS:
            w = worst.setdefault(lab, {"rotationDeg": 0.0, "positionM": 0.0, "rootMotionM": 0.0, "eventsBack": True})
            w["rotationDeg"] = max(w["rotationDeg"], r[lab]["rotationDeg"])
            w["positionM"] = max(w["positionM"], r[lab]["positionM"])
            if r[lab].get("rootMotionM") is not None:
                w["rootMotionM"] = max(w["rootMotionM"], r[lab]["rootMotionM"])
            if "eventsBack" in r[lab]:
                w["eventsBack"] = w["eventsBack"] and r[lab]["eventsBack"]
        print(f"{g[:8]} {r['frames']:4d} frames rm={int(r['rootMotion'])} events={r['events']:2d}: " +
              "; ".join(f"{lab} {r[lab]['rotationDeg']:.3f} deg {r[lab]['positionM'] * 1000:.2f} mm" for lab in LABELS),
              flush=True)
    os.makedirs(a.out, exist_ok=True)
    summary = {"skeleton": PLAYER, "playerClips": len(player), "clips": len(results), "withRootMotion": sum(r["rootMotion"] for r in results),
               "withEvents": sum(1 for r in results if r["events"]), "blockIdle": BLOCK_IDLE in [r["guid"] for r in results],
               "mirror": {k_: (v if k_ != "pairs" else len(v)) for k_, v in mirror.items()}, "worst": worst,
               "median": {lab: {x: round(statistics.median(r[lab][x] for r in results), 5) for x in ("rotationDeg", "positionM")} for lab in LABELS},
               "seconds": round(time.time() - t0, 1)}
    Path(a.out, "clipops.json").write_text(json.dumps({"summary": summary, "mirrorPairs": mirror["pairs"], "clips": results}, indent=1))
    print(json.dumps(summary))
    exact = ("mirror2", "reverse2", "trimAll")
    ok = all(worst[x]["rotationDeg"] < 1.0 and worst[x]["positionM"] < 0.02 and worst[x]["rootMotionM"] < 0.001 and worst[x]["eventsBack"] for x in exact)
    print("PASS" if ok else "FAIL", "-", Path(a.out, "clipops.json"))
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
