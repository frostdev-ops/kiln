#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""The samples' check of their player clips: a state of a mod's own plays the game's body, not the skeleton's bind pose.

    python3 mods/samples/check_player_clips.py --static [MOD_DIR ...]          the recipes alone, no game
    python3 mods/samples/check_player_clips.py [--game DIR] MOD_DIR [...]       the cooked clips (MOD_DIR/assets/cooked), against the game

MOD_DIR defaults to mods/samples/PlayerStates and mods/samples/WeaponMoves. Each state of assets/player-states.json that plays a clip of the
mod's own recipe (assets/cook.json) is checked against the game clip its `like` state plays (resolved in the game's player graph, as the
cook does). The game's skeleton writes a joint a clip does not animate at its bind pose, so a clip that moves only an arm leaves the body
standing in that pose while the game's own moves pose almost every joint (`Attack_Sword_Shield_01`: 82 of 102). The samples therefore make
each clip from the game clip (`"from"`) and take the sword arm from their own motion (`"blend"` with `"bones"`).

--static (no game): every such clip must be made `"from"` a game clip (an input of type keen::JointAnimation), and what it blends in must be
named joints (`bones`), not the whole body.

With the game, per state, decoding the cooked clip:
  (a) every joint outside the blended bones equals the game clip's track, at the game clip's time this frame plays (a `trim` and `speed`
      move it; within 0.5 degrees and 2 mm, the codec's error is under 0.2 degrees and 0.3 mm);
  (b) the blended bones equal the clip they come from (`blend.with`, decoded, stretched to the clip's length as the cook does);
  (c) no joint the game clip moves off the bind pose (by more than 1 degree or 1 mm) is left at the bind pose all through the clip, apart
      from the blended bones, which are the mod's own motion.
Read-only on the game; prints one line per check and exits 1 when one fails.
"""
import argparse
import json
import math
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[1]
sys.path[:0] = [str(ROOT / "tools/animcook"), str(ROOT / "tools/kfc"), str(ROOT / "tools/modelcook")]

FPS = 60
JOINT_ANIM = "keen::JointAnimation"
PLAYER_SKELETON = "87b29df6-6518-43ef-9298-e3f85a6204b9"
TOL_DEG, TOL_M = 0.5, 0.002     # (a), (b): the codec keeps a cooked clip within 0.2 degrees and 0.3 mm of what it was given
MOVE_DEG, MOVE_M = 1.0, 0.001   # (c): a joint the game moves at least this far off the bind pose is animated
BIND_DEG, BIND_M = 0.1, 0.0001  # (c): a joint within this of the bind pose all clip long is left at it
DEFAULT_MODS = [HERE / "PlayerStates", HERE / "WeaponMoves"]


def jsonc(path):
    import re
    text = Path(path).read_text(encoding="utf-8")
    text = re.sub(r"^\s*//.*$", "", text, flags=re.M)
    return json.loads(re.sub(r",(\s*[}\]])", r"\1", text))


def recipe_clips(cook):
    """clip key -> entry, over every clips step of the recipe."""
    out = {}
    for st in cook.get("steps", []):
        if st.get("op") == "clips":
            out.update(st.get("clips", {}))
    return out


def own_states(mod):
    """(state, clip key, like) for the states that play a clip of the mod's own recipe."""
    ps = json.loads((mod / "assets/player-states.json").read_text(encoding="utf-8"))
    cook = jsonc(mod / "assets/cook.json")
    clips = recipe_clips(cook)
    return [(name, s["clip"], s["like"]) for name, s in ps["states"].items() if s["clip"] in clips], cook, clips


class Report:
    def __init__(self):
        self.fails = 0
        self.passes = 0

    def line(self, ok, text):
        print(("PASS " if ok else "FAIL ") + text)
        if ok:
            self.passes += 1
        else:
            self.fails += 1


def check_static(mod, rep):
    states, cook, clips = own_states(mod)
    inputs = cook.get("inputs", {})
    for state, key, like in states:
        c = clips[key]
        src = c.get("from")
        game_from = isinstance(src, str) and src.startswith("@") and inputs.get(src[1:], {}).get("type") == JOINT_ANIM
        rep.line(game_from, f"{mod.name} {state} (like {like}): clip {key} is made from a game clip"
                 + (f" ({src})" if game_from else ": it is sampled from glTF or another clip of the mod, so every joint its motion does not "
                    "move is written at the bind pose"))
        b = c.get("blend")
        if b is not None:
            rep.line("bones" in b, f"{mod.name} {state}: clip {key} blends in named joints only"
                     + (f" ({len(b['bones'])}: {', '.join(b['bones'])})" if "bones" in b else f": blend.weight {b.get('weight')} mixes the whole body"))


def qang(a, b):
    na = math.sqrt(sum(x * x for x in a)) or 1.0
    nb = math.sqrt(sum(x * x for x in b)) or 1.0
    return 2 * math.degrees(math.acos(min(1.0, abs(sum(x * y for x, y in zip(a, b))) / (na * nb))))


def check_cooked(mod, rep, game):
    from anicook import playergraph as PG, skeleton as K
    from kfc import anim
    from kfc.archive import Kfc, ResourceKey
    from kfc.blob import Decoder
    from kfc.refl import TypeDb

    states, cook, clips = own_states(mod)
    k = Kfc(game)
    t = TypeDb.from_exe(game / "enshrouded.exe")
    dec = Decoder(t, max_array=1 << 26)
    mh, ja, gt = t.get("keen::ModelHierarchyResource"), t.get(JOINT_ANIM), t.get(PG.GRAPH_TYPE)
    sk = K.from_game(dec.decode(mh, k.read(ResourceKey(PLAYER_SKELETON, mh["hash"], 0))), PLAYER_SKELETON)
    dp = sk.default_pose()
    graph = PG.Graph(dec.decode(gt, k.read(ResourceKey(PG.PLAYER_GRAPH, gt["hash"], 0))))
    cooked = mod / "assets/cooked"

    def load(key):
        return anim.parse((cooked / f"clip_{key}.bin").read_bytes())

    for state, key, like in states:
        (st, _m), why = PG.resolve_like(graph, like)
        if st is None:
            rep.line(False, f"{mod.name} {state}: like {like}: {why}")
            continue
        node = graph.by_id[graph.walk(st["poseResult"]["value"], stop_at_clip=True)]
        gguid = node["animation"]
        gclip = anim.parse(k.read(ResourceKey(gguid, ja["hash"], 0)))
        clip = load(key)
        c = clips[key]
        b = c.get("blend") or {}
        bones = {sk.index[x] for x in b.get("bones", [])} if isinstance(b.get("bones"), list) else set()
        label = f"{mod.name} {state} (clip {key}, like {like}: game clip {gguid}, {gclip.frame_count} frames)"
        n = clip.frame_count
        readable = not any(op in c for op in ("mirror", "reverse", "offset")) and ("blend" not in c or "bones" in b)
        if not readable:
            rep.line(False, f"{label}: (a), (b) read trim, speed and blend by bones only: {', '.join(op for op in ('mirror', 'reverse', 'offset', 'blend') if op in c)}")
        # frame f plays the game clip at start + f * speed: the span the trim keeps, over the clip's frames (as the cook samples it)
        trim = c.get("trim") if "from" in c else None
        start = trim[0] * FPS if trim else 0.0
        span = (min(trim[1] * FPS, float(gclip.frame_count)) - start) if trim else float(gclip.frame_count)
        speed = span / n if "from" in c else 1.0
        frames = [anim.sample_frame(clip, f, dp) for f in range(n + 1)]
        gsrc = [anim.sample_frame(gclip, min(start + f * speed, float(gclip.frame_count)), dp) for f in range(n + 1)]
        # (a) the body is the game clip's
        worst = (0.0, 0.0, None)
        for f in (range(n + 1) if readable else ()):
            for j in range(len(sk)):
                if j in bones:
                    continue
                d, m = qang(frames[f][0][j], gsrc[f][0][j]), math.dist(frames[f][1][j], gsrc[f][1][j])
                if d > worst[0] or m > worst[1]:
                    worst = (max(d, worst[0]), max(m, worst[1]), f"j{j} frame {f}")
        body = len(sk) - len(bones)
        if readable:
            rep.line(worst[0] <= TOL_DEG and worst[1] <= TOL_M,
                     f"{label}: (a) {body} joints outside the blended bones follow the game clip (from its frame {start:g}, {speed:.4f} of "
                     f"its frames a frame): largest difference {worst[0]:.3f} deg, {worst[1] * 1000:.2f} mm ({worst[2]})")
        # (b) the blended bones are the mod's motion
        if bones and readable:
            wclip = load(b["with"])
            wf = [anim.sample_frame(wclip, f * wclip.frame_count / n, dp) for f in range(n + 1)]
            wd = max(qang(frames[f][0][j], wf[f][0][j]) for f in range(n + 1) for j in bones)
            moved = {j: max(qang(wf[f][0][j], dp[0][j]) for f in range(n + 1)) for j in sorted(bones)}
            rep.line(wd <= TOL_DEG, f"{label}: (b) the {len(bones)} blended bones follow {b['with']}: largest difference {wd:.3f} deg; "
                     f"how far each turns off the bind pose: " + ", ".join(f"j{j} {v:.0f} deg" for j, v in moved.items()))
        # (c) nothing the game animates is left at the bind pose
        animated = [j for j in range(len(sk)) if any(qang(g[0][j], dp[0][j]) > MOVE_DEG or math.dist(g[1][j], dp[1][j]) > MOVE_M for g in gsrc)]
        left = [j for j in animated if j not in bones
                and all(qang(fr[0][j], dp[0][j]) <= BIND_DEG and math.dist(fr[1][j], dp[1][j]) <= BIND_M for fr in frames)]
        rep.line(not left, f"{label}: (c) of the {len(animated)} joints the game clip moves off the bind pose, "
                 + (f"none is left at it ({len([j for j in animated if j in bones])} are the blended bones)" if not left else
                    f"{len(left)} are left at it all clip long: " + " ".join(f"j{j}" for j in left[:40]) + (" ..." if len(left) > 40 else "")))


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("mods", nargs="*", type=Path)
    ap.add_argument("--static", action="store_true", help="the recipes only, no game")
    ap.add_argument("--game", type=Path, default=Path.home() / ".local/share/Steam/steamapps/common/Enshrouded")
    a = ap.parse_args()
    rep = Report()
    for mod in a.mods or DEFAULT_MODS:
        if a.static:
            check_static(mod, rep)
        else:
            check_cooked(mod, rep, a.game)
    print(f"check_player_clips: {rep.passes} PASS, {rep.fails} FAIL")
    return 1 if rep.fails else 0


if __name__ == "__main__":
    sys.exit(main())
