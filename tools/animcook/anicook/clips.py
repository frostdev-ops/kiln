# SPDX-License-Identifier: MIT
"""glTF animation -> keen::JointAnimation, and its sibling resources under the same GUID: RootMotionAnimation, LveAnimation
(root motion) and AnimationSequenceContainer with a SequenceResource (events). Formats: docs/re/npc-internals.md 2.1-2.3."""
import math
import struct

from kfc import anim

from .gltfdoc import compose, qinv, qmul, qnorm, qrot, to_game

FPS = 60
LVE_TRACK = 0xFA31A0F1  # trackName of all 578 LveAnimation resources


class ClipError(Exception):
    pass


def sample_track(doc, chans, skel, node_of_joint, f, n, t0, t1):
    """Frame f as (rots, positions, scales) per joint, game space, parent-relative."""
    t = t0 + (t1 - t0) * f / n if n else t0
    rots, poss, scls = [], [], []
    for j, node in enumerate(node_of_joint):
        if node is None:
            r = skel.rest[j]
            rots.append(list(r[1]))
            poss.append(list(r[0]))
            scls.append(list(r[2]))
            continue
        tr, q, s = doc.rest(node)
        c = chans.get(node, {})
        if "translation" in c:
            tr = c["translation"].eval(t)
        if "rotation" in c:
            q = c["rotation"].eval(t)
        if "scale" in c:
            s = c["scale"].eval(t)
        if skel.parents[j] == 0xFFFF or skel.nodes[skel.parents[j]] is None:
            above = doc.world_of_parent(node)
            if above is not None:
                tr, q, s = compose(above, (tr, q, s))
        gt, gq, gs = to_game(tr, q, s)
        rots.append(list(gq))
        poss.append(list(gt))
        scls.append(list(gs))
    return rots, poss, scls


def _globals(skel, frame):
    """Model-space (position, rotation) per joint of one frame, ignoring scale for the rotation."""
    rots, poss, scls = frame
    out = []
    for j, p in enumerate(skel.parents):
        if p == 0xFFFF:
            out.append((tuple(poss[j]), qnorm(tuple(rots[j])), tuple(scls[j])))
        else:
            out.append(compose(out[p], (tuple(poss[j]), qnorm(tuple(rots[j])), tuple(scls[j]))))
    return out


def _yaw_of(q):
    """The rotation of q about y, as a quaternion."""
    m = math.hypot(q[1], q[3]) or 1.0
    return (0.0, q[1] / m, 0.0, q[3] / m)


def extract_root_motion(skel, frames, joint, axes="xz", rotation=False):
    """Takes the joint's motion out of the frames (the engine moves the entity instead). Returns (positions, orientations):
    per frame, in the entity's space, relative to the first frame. Horizontal only by default: height stays in the pose."""
    j = skel.index[joint]
    g0 = _globals(skel, frames[0])[j]
    positions, orientations = [], []
    keep = tuple(a in axes for a in "xyz")
    for fr in frames:
        g = _globals(skel, fr)
        gj = g[j]
        delta = tuple((gj[0][c] - g0[0][c]) if keep[c] else 0.0 for c in range(3))
        positions.append(delta)
        par = skel.parents[j]
        yaw = _yaw_of(qmul(gj[1], qinv(g0[1]))) if rotation else (0.0, 0.0, 0.0, 1.0)
        orientations.append(yaw)
        target = tuple(gj[0][c] - delta[c] for c in range(3))
        if rotation:
            target = tuple(g0[0][c] + v for c, v in enumerate(qrot(qinv(yaw), tuple(target[c] - g0[0][c] for c in range(3)))))
            fr[0][j] = list(qnorm(qmul(qinv(yaw), tuple(fr[0][j]))))
        if par == 0xFFFF:
            fr[1][j] = list(target)
        else:
            pp, pq, ps = g[par]
            local = qrot(qinv(pq), tuple(target[c] - pp[c] for c in range(3)))
            fr[1][j] = [local[c] / (ps[c] or 1.0) for c in range(3)]
    return positions, orientations


def root_motion_value(positions, orientations):
    end = tuple(e - s for e, s in zip(positions[-1], positions[0]))  # the game's paths do not all start at zero
    return {"orientations": [dict(zip("xyzw", q)) for q in orientations], "positions": [dict(zip("xyz", p)) for p in positions],
            "startToEnd": {"x": -end[0], "y": -end[1], "z": -end[2]}, "fps": FPS, "frameCount": len(positions) - 1}


def lve_value(positions, orientations):
    """Keys at every frame, controls = per-frame central differences. Only sequences with usePureLveLocomotion read this; the exact
    use of `controls` was inferred from the game's data, not run."""
    n = len(positions)
    pk = []
    for i, p in enumerate(positions):
        a, b = positions[max(i - 1, 0)], positions[min(i + 1, n - 1)]
        d = (min(i + 1, n - 1) - max(i - 1, 0)) or 1
        pk.append({"time": i, "values": dict(zip("xyz", p)), "controls": {c: (b[k] - a[k]) / d for k, c in enumerate("xyz")}})
    ok = [{"time": i, "values": dict(zip("xyzw", orientations[i])), "controls": {"x": 0.0, "y": 0.0, "z": 0.0, "w": 0.0}} for i in (0, n - 1)]
    end = tuple(e - s for e, s in zip(positions[-1], positions[0]))
    return {"trackName": LVE_TRACK, "orientationKeys": ok, "positionKeys": pk, "startToEnd": {"x": -end[0], "y": -end[1], "z": -end[2]},
            "fps": FPS, "frameCount": n - 1, "flags": 0}


def qdeg(a, b):
    na = math.sqrt(sum(x * x for x in a)) or 1.0  # constant tracks are not normalised by the game's sampler
    nb = math.sqrt(sum(x * x for x in b)) or 1.0
    d = min(1.0, abs(sum(x * y for x, y in zip(a, b))) / (na * nb))
    return 2 * math.degrees(math.acos(d))


def check_clip(clip, frames, default_pose):
    """Decoded clip against the source at every frame: the largest errors."""
    out = {"rotationDeg": 0.0, "position": 0.0, "scale": 0.0}
    n = clip.frame_count
    for f, fr in enumerate(frames):
        pose = anim.sample_frame(clip, min(float(f), n - 1 + 0.999), default_pose)
        for j in range(clip.joint_count):
            out["rotationDeg"] = max(out["rotationDeg"], qdeg(pose[0][j], fr[0][j]))
            out["position"] = max(out["position"], max(abs(a - b) for a, b in zip(pose[1][j], fr[1][j])))
            out["scale"] = max(out["scale"], max(abs(a - b) for a, b in zip(pose[2][j], fr[2][j])))
    return out


def cook_clip(doc, anim_name, skel, loop=None, tolerance=None, root_motion=None):
    """-> (clip bytes, info, root motion value or None, lve value or None). `root_motion`: None, or {joint, axes, rotation}."""
    frames, n = sample_animation(doc, anim_name, skel)
    return finish_clip(frames, n, skel, anim_name, loop, tolerance, root_motion)


def sample_animation(doc, anim_name, skel):
    """An animation of the file sampled at FPS on the skeleton's joints -> (frames, n): n + 1 frames of (rots, positions, scales), game space,
    parent-relative. Joints are found by name (on a game skeleton also by the `keen_joint` extra tools/kfc writes)."""
    anims = doc.animations()
    if anim_name not in anims:
        raise ClipError(f"{doc.path.name}: no animation {anim_name!r} (has {', '.join(anims) or 'none'})")
    chans = anims[anim_name]
    node_of_joint = []
    for j, name in enumerate(skel.names):
        node = doc.by_name.get(name)
        if node is None and skel.source == "game":
            node = next((i for i in range(len(doc.nodes)) if doc.extras_joint(i) == j), None)
        node_of_joint.append(node)
    for node in chans:
        if node not in node_of_joint:
            parts = "; ".join(sorted(chans[node]))
            raise ClipError(f"{doc.path.name}, animation {anim_name!r} moves node {doc.names[node]!r} ({parts}), which is not a joint of the skeleton")
    if not chans:
        raise ClipError(f"{doc.path.name}, animation {anim_name!r} has no joint channels")
    times = [t for c in chans.values() for ch in c.values() for t in (ch.times[0], ch.times[-1])]
    t0, t1 = min(times), max(times)
    n = max(1, round((t1 - t0) * FPS))
    frames = [sample_track(doc, chans, skel, node_of_joint, f, n, t0, t1) for f in range(n + 1)]
    return frames, n


def finish_clip(frames, n, skel, anim_name, loop=None, tolerance=None, root_motion=None):
    """Sampled frames on `skel` -> (clip bytes, info, root motion value or None, lve value or None): what cook_clip returns."""
    dp = skel.default_pose()
    warnings = []
    is_loop = bool(loop)
    if is_loop:
        worst = max(max(qdeg(frames[0][0][j], frames[-1][0][j]) for j in range(len(skel))), 0.0)
        worst_p = max(max(abs(a - b) for a, b in zip(frames[0][1][j], frames[-1][1][j])) for j in range(len(skel)))
        if root_motion is None and (worst > 2.0 or worst_p > 0.02):
            warnings.append(f"loops, but its last frame differs from its first by {worst:.1f} deg / {worst_p:.3f} m: it will pop at the seam")
    rm = lv = None
    if root_motion is not None and root_motion is not False:
        joint = root_motion.get("joint") or next((nm for nm, i in skel.index.items() if skel.parents[i] == 0xFFFF and nm != "kiln_root"), None)
        if joint is None or joint not in skel.index:
            raise ClipError(f"rootMotion joint {joint!r} is not in the skeleton")
        positions, orientations = extract_root_motion(skel, frames, joint, root_motion.get("axes", "xz"), bool(root_motion.get("rotation")))
        rm, lv = root_motion_value(positions, orientations), lve_value(positions, orientations)
    tol = dict({"rotation": 0.0005, "position": 0.0002, "scale": 0.0005}, **(tolerance or {}))
    hh = getattr(skel, "game_hash", None) or skel.hierarchy_hash
    clip = anim.encode(frames, dp, hh, fps=FPS, loop=is_loop, rot_tol=tol["rotation"], pos_tol=tol["position"], scale_tol=tol["scale"])
    raw = anim.build(clip)
    back = anim.parse(raw)
    if anim.build(back) != raw:
        raise ClipError(f"{anim_name}: the clip does not rebuild byte-identical")
    errors = check_clip(back, frames, dp)
    info = {"frames": n, "duration": n / FPS, "loop": is_loop, "bytes": len(raw), "segments": len(back.segments),
            "keys": sum(len(t.keys) for s in back.segments for ch in (s.orientation, s.position, s.scale) for t in ch.animated),
            "errors": errors, "warnings": warnings}
    return raw, info, rm, lv


def event_sequence_value(types, dec, name, events, length_s):
    """A SequenceResource from an event list: {at, sound | vfx | foot | noise, slot, ...}. Times in seconds."""
    def zero(tn):
        t = types.get(tn)
        return dec.decode(t, b"\0" * t["size"])

    out = []
    for i, e in enumerate(sorted(events, key=lambda x: x.get("at", 0.0))):
        ns = {"value": int(round(float(e.get("at", 0.0)) * 1e9))}
        if "sound" in e:
            t = "keen::SfxNotifierEvent"
            v = zero(t)
            v.update(sound=e["sound"], attach=bool(e.get("attach", True)), noiseRange=float(e.get("noiseRange", 0.0)))
            v["soundVolumeHint"] = e.get("volumeHint", "GenericSfx")
            v["slot"] = e.get("slot", "Root")
        elif "vfx" in e:
            t = "keen::VfxNotifierEvent"
            v = zero(t)
            v.update(vfx=e["vfx"], attach=bool(e.get("attach", True)))
            v["slot"] = e.get("slot", "Root")
        elif "foot" in e:
            if e["foot"] not in ("left", "right"):
                raise ClipError(f"event {i}: foot must be left or right")
            t = "keen::LeftFootPlantingEvent" if e["foot"] == "left" else "keen::RightFootPlantingEvent"
            v = zero(t)
        elif "noise" in e:
            t = "keen::actor::TriggerNoiseEvent"
            v = zero(t)
            v["range"] = float(e["noise"])
        else:
            raise ClipError(f"event {i}: needs sound, vfx, foot or noise")
        v["time"] = ns
        if "notifierOffset" in v:
            v["notifierOffset"]["orientationOffset"] = {"x": 0.0, "y": 0.0, "z": 0.0, "w": 1.0}
            if "offset" in e:
                ox, oy, oz = e["offset"]
                v["notifierOffset"]["localOffset"] = {"x": ox, "y": oy, "z": oz}
        out.append({"$type": t, "value": v})
    return {"events": out, "length": {"value": int(round(length_s * 1e9))}, "loop": False, "name": name}


__all__ = ["ClipError", "FPS", "cook_clip", "event_sequence_value", "struct"]
