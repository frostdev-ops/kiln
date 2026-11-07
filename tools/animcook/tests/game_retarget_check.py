#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""Checks retargeting against the clips of your installed game. Reads the game, writes nothing but its report under --out.

    python3 tools/animcook/tests/game_retarget_check.py [--skeleton GUID ...] [--clips N] [--twist DEG] [--scale S]

Each game clip is re-expressed on a rig of other joint names, every rest frame turned by a random twist, and scaled: the same motion authored on another
rig. That is retargeted back onto the game's skeleton, encoded as a cook encodes it, decoded again, and compared with the game's own clip in model space:
- with "translate" on every joint the result has to be the game's clip again, within the codec's tolerance;
- with the default (the hips only) rotations still have to match; positions differ only on joints the clip moves by translation.

Options: --game DIR, --out DIR (default $KILN_OUT/retargetcheck). Prints a summary; the details go to OUT/retarget.json.
"""
import argparse
import json
import math
import os
import sys
import time
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path[:0] = [str(HERE), str(HERE.parent), str(HERE.parent.parent / "kfc"), str(HERE.parent.parent / "modelcook")]

from anicook import clips as C, retarget as RT, skeleton as K  # noqa: E402
from anicook.gltfdoc import qinv, qmul, qnorm, qrot  # noqa: E402
from kfc import anim  # noqa: E402
from kfc.archive import Kfc, ResourceKey  # noqa: E402
from kfc.blob import Decoder  # noqa: E402
from kfc.refl import TypeDb  # noqa: E402
from test_retarget import foreign, qangle  # noqa: E402

GAME = Path.home() / ".local/share/Steam/steamapps/common/Enshrouded"
PLAYER = "87b29df6-6518-43ef-9298-e3f85a6204b9"


def reexpress(game_sk, rig, twists, scale, frame):
    """A game frame as the same motion on `rig` (model-space positions scaled, every joint's frame turned by its twist) -> rig frame."""
    ms = RT.model_space(game_sk, frame)
    n = len(rig)
    rots, poss, scls, glob = [None] * n, [None] * n, [None] * n, [None] * n
    for j in range(n):
        gp = tuple(c * scale for c in ms[j][0])
        gq = qnorm(qmul(ms[j][1], twists[j]))
        p = rig.parents[j]
        if p == 0xFFFF:
            lt, lq = gp, gq
        else:
            pp, pq = glob[p]
            lt = qrot(qinv(pq), tuple(gp[i] - pp[i] for i in range(3)))
            lq = qnorm(qmul(qinv(pq), gq))
        rots[j], poss[j], scls[j] = list(lq), list(lt), [1.0, 1.0, 1.0]
        glob[j] = (gp, gq)
    return rots, poss, scls


def compare(game_sk, a_frames, b_frames):
    """Worst joint rotation (degrees, model space) and position (metres) between two frame lists."""
    wq = wp = 0.0
    moved = set()
    for fa, fb in zip(a_frames, b_frames):
        ma, mb = RT.model_space(game_sk, fa), RT.model_space(game_sk, fb)
        for j in range(len(game_sk)):
            wq = max(wq, qangle(ma[j][1], mb[j][1]))
            d = max(abs(x - y) for x, y in zip(ma[j][0], mb[j][0]))
            if d > 0.005:
                moved.add(j)
            wp = max(wp, d)
    return wq, wp, sorted(moved)


def check_clip(game_sk, guid, raw, twist, scale, seed):
    clip = anim.parse(raw)
    dp = game_sk.default_pose()
    n = clip.frame_count - 1
    if n < 1:
        return None
    game_frames = [anim.sample_frame(clip, f, dp) for f in range(n + 1)]
    # all joints have unit scale in a re-expressed rig: keep the game's scales out of it by comparing rotations and positions only
    rig, twists = foreign(game_sk, prefix="bone_", twist_deg=twist, scale=scale, seed=seed)
    rig_frames = [reexpress(game_sk, rig, twists, scale, fr) for fr in game_frames]
    pairs = {f"bone_{nm}": nm for nm in game_sk.names}
    out = {"guid": guid, "frames": n + 1, "joints": len(game_sk)}
    for label, cfg in (("all", {"map": pairs, "translate": "all"}), ("hips", {"map": pairs})):
        plan = RT.Plan(rig, game_sk, cfg)
        back = [plan.frame(fr) for fr in rig_frames]
        # the game's scale tracks are not part of the motion carried over: put them back, as a rig with unit scales can't say them
        back = [(r, p, s) for (r, p, _), (_, _, s) in zip(back, game_frames)]
        raw2, info, _, _ = C.finish_clip(back, n, game_sk, guid, loop=clip.flags == 1)
        dec = anim.parse(raw2)
        cooked = [anim.sample_frame(dec, f, dp) for f in range(n + 1)]
        wq, wp, moved = compare(game_sk, game_frames, cooked)
        out[label] = {"scale": round(plan.scale, 6), "rotationDeg": round(wq, 4), "positionM": round(wp, 5), "jointsOff5mm": len(moved),
                      "bytes": len(raw2), "gameBytes": len(raw)}
    return out


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--game", default=str(GAME))
    ap.add_argument("--out", default=os.path.join(os.environ.get("KILN_OUT", str(HERE.parent.parent.parent / ".out" / "models")), "retargetcheck"))
    ap.add_argument("--skeleton", action="append", help=f"a ModelHierarchyResource GUID (default the player's, {PLAYER}, and the next 4 with clips)")
    ap.add_argument("--clips", type=int, default=12, help="clips per skeleton")
    ap.add_argument("--twist", type=float, default=120.0)
    ap.add_argument("--scale", type=float, default=1.3)
    a = ap.parse_args()
    game = Path(a.game)
    k, t = Kfc(game), TypeDb.from_exe(game / "enshrouded.exe")
    dec = Decoder(t, max_array=1 << 26)
    mh, ja = t.get("keen::ModelHierarchyResource"), t.get("keen::JointAnimation")
    skels = {}
    for key in k.resources_of_type(mh["hash"]):
        v = dec.decode(mh, k.read(key))
        sk = K.from_game(v, str(key.guid))
        skels[sk.game_hash] = sk
    want = a.skeleton or [PLAYER]
    by_hash = {}
    t0 = time.time()
    for key in k.resources_of_type(ja["hash"]):
        raw = k.read(key)
        hh = anim.parse(raw).hierarchy_hash
        if hh in skels:
            by_hash.setdefault(hh, []).append((str(key.guid), raw))
    chosen = [h for h, sk in skels.items() if sk.guid in want]
    if not a.skeleton:
        chosen += [h for h in sorted(by_hash, key=lambda h: -len(by_hash[h])) if h not in chosen][:4]
    results, worst = [], {"all": [0.0, 0.0], "hips": [0.0, 0.0]}
    for h in chosen:
        sk = skels[h]
        clips = by_hash.get(h, [])[:a.clips]
        for i, (guid, raw) in enumerate(clips):
            r = check_clip(sk, guid, raw, a.twist, a.scale, seed=i + 1)
            if r is None:
                continue
            r["skeleton"] = sk.guid
            results.append(r)
            for lab in worst:
                worst[lab][0] = max(worst[lab][0], r[lab]["rotationDeg"])
                worst[lab][1] = max(worst[lab][1], r[lab]["positionM"])
            print(f"{sk.guid[:8]} ({len(sk)} joints) clip {guid[:8]} {r['frames']} frames: translate all {r['all']['rotationDeg']:.3f} deg "
                  f"{r['all']['positionM'] * 1000:.2f} mm; hips only {r['hips']['rotationDeg']:.3f} deg, {r['hips']['jointsOff5mm']} joints moved by translation", flush=True)
    os.makedirs(a.out, exist_ok=True)
    summary = {"clips": len(results), "skeletons": len(chosen), "twistDeg": a.twist, "scale": a.scale, "worst": worst, "seconds": round(time.time() - t0, 1)}
    Path(a.out, "retarget.json").write_text(json.dumps({"summary": summary, "clips": results}, indent=1))
    print(json.dumps(summary))
    ok = worst["all"][0] < 1.0 and worst["all"][1] < 0.02 and worst["hips"][0] < 1.0
    print("PASS" if ok else "FAIL", "-", Path(a.out, "retarget.json"))
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
