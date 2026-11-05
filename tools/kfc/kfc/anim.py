# SPDX-License-Identifier: MIT
"""keen::JointAnimation: the game's skeletal clip codec, decoded and re-encoded (see docs/re/npc-internals.md).

Read from the game's own sampler (server 0x775300 and three siblings, client 0x93c420; decompiled and checked against
every clip):
a clip is split into segments of at most 63 frames. In a segment each track is either
    animated   a u64 key mask (bit i = a key at frame startTime + i), the joint index, then per key value + tangent,
               interpolated as a cubic Hermite spline between the two keys around the sample time;
    constant   the joint index and one value;
    default    just the joint index: the value comes from the skeleton's default pose (ModelHierarchyResource.defaultPose).
Orientation keys are 4 x i16 / 32767 (normalised after interpolation), tangents 4 x i16 x ORIENT_TANGENT_SCALE per frame;
position keys are 3 x i16 x positionFactor, tangents 3 x i16 x positionTangent per frame; scale keys are 3 x i16 x scaleFactor,
their tangents 3 x i16 x the same 2^-17 as orientation tangents.

All functions are pure; `parse` and `build` are exact inverses over the game's data (5,461 of 5,461 clips byte-identical,
tools/kfc/tests/test_anim.py).
"""
import base64
import json
import math
import struct
from dataclasses import dataclass, field
from pathlib import Path

HEADER_SIZE = 20
ROOT_SIZE = 48
ORIENT_VALUE_SCALE = 1.0 / 32767.0
SEGMENT_FRAMES = 63  # a segment's mask has 64 bits: start..start+63


@dataclass
class Track:
    joint: int
    mask: int = 0            # u64 for animated tracks
    keys: list = field(default_factory=list)  # [(value tuple, tangent tuple), ...] raw i16


@dataclass
class Channel:
    animated: list = field(default_factory=list)   # [Track]
    constant: list = field(default_factory=list)   # [(joint, value tuple)]
    default: list = field(default_factory=list)    # [joint]


@dataclass
class Segment:
    start: int
    end: int
    orientation: Channel
    position: Channel
    scale: Channel


@dataclass
class Clip:
    hierarchy_hash: int
    position_factor: float
    position_tangent: float
    scale_factor: float
    fps: int
    frame_count: int
    joint_count: int
    flags: int
    segments: list


class _R:
    def __init__(self, b, o):
        self.b = b
        self.o = o

    def u16(self):
        v = struct.unpack_from("<H", self.b, self.o)[0]
        self.o += 2
        return v

    def i16s(self, n):
        v = struct.unpack_from(f"<{n}h", self.b, self.o)
        self.o += 2 * n
        return v

    def u64s(self, n):
        v = struct.unpack_from(f"<{n}Q", self.b, self.o)
        self.o += 8 * n
        return v

    def align8(self, base):
        self.o = base + ((self.o - base + 7) & ~7)


def _read_channel(r, base, used, nonconst, comps):
    masks = r.u64s(nonconst)
    c = Channel()
    for m in masks:
        joint = r.u16()
        n = bin(m).count("1")
        keys = []
        for _ in range(n):
            v = r.i16s(comps)
            t = r.i16s(comps)
            keys.append((v, t))
        c.animated.append(Track(joint, m, keys))
    for _ in range(used - nonconst):
        joint = r.u16()
        c.constant.append((joint, r.i16s(comps)))
    n = r.u16()
    c.default = [r.u16() for _ in range(n)]
    r.align8(base)
    return c


def parse(raw):
    """Parse a raw keen::JointAnimation resource into a Clip."""
    hh, pf, pt, sf = struct.unpack_from("<Qfff", raw, 0)
    hrel, hn = struct.unpack_from("<iI", raw, 0x14)
    drel, dn = struct.unpack_from("<iI", raw, 0x1c)
    fps, frames, joints, flags = struct.unpack_from("<4H", raw, 0x24)
    hbase = 0x14 + hrel
    dbase = 0x1c + drel if dn else 0
    segs = []
    for i in range(hn):
        off, st, en, uo, no, up, np, us, ns = struct.unpack_from("<I8H", raw, hbase + i * HEADER_SIZE)
        r = _R(raw, dbase + off)
        o = _read_channel(r, dbase, uo, no, 4)
        p = _read_channel(r, dbase, up, np, 3)
        s = _read_channel(r, dbase, us, ns, 3)
        segs.append(Segment(st, en, o, p, s))
    return Clip(hh, pf, pt, sf, fps, frames, joints, flags, segs)


def _write_channel(w, ch, comps, base):
    for t in ch.animated:
        w.extend(struct.pack("<Q", t.mask))
    for t in ch.animated:
        w.extend(struct.pack("<H", t.joint))
        for v, tg in t.keys:
            w.extend(struct.pack(f"<{comps}h", *v))
            w.extend(struct.pack(f"<{comps}h", *tg))
    for joint, v in ch.constant:
        w.extend(struct.pack("<H", joint))
        w.extend(struct.pack(f"<{comps}h", *v))
    w.extend(struct.pack("<H", len(ch.default)))
    for j in ch.default:
        w.extend(struct.pack("<H", j))
    while (len(w) - base) % 8:
        w.append(0)


def build(clip):
    """Clip -> raw keen::JointAnimation resource, in the layout of the game's converter."""
    hdr = bytearray()
    data = bytearray()
    for s in clip.segments:
        off = len(data)  # 8 aligned: every channel ends on an 8 byte boundary
        _write_channel(data, s.orientation, 4, 0)
        _write_channel(data, s.position, 3, 0)
        _write_channel(data, s.scale, 3, 0)
        hdr += struct.pack("<I8H", off, s.start, s.end,
                           len(s.orientation.animated) + len(s.orientation.constant), len(s.orientation.animated),
                           len(s.position.animated) + len(s.position.constant), len(s.position.animated),
                           len(s.scale.animated) + len(s.scale.constant), len(s.scale.animated))
    # the sampler reads 16 bytes at a time at unaligned positions, so the array ends at least 16 bytes after the data
    data.extend(b"\0" * (((len(data) + 31) & ~15) - len(data)))
    root = bytearray(ROOT_SIZE)
    struct.pack_into("<Qfff", root, 0, clip.hierarchy_hash, clip.position_factor, clip.position_tangent, clip.scale_factor)
    # BlobArray headers: self relative; headers follow the root, data follows on a 16 byte boundary
    hat = ROOT_SIZE
    struct.pack_into("<iI", root, 0x14, hat - 0x14, len(clip.segments))
    dat = (hat + len(hdr) + 15) & ~15
    struct.pack_into("<iI", root, 0x1c, dat - 0x1c, len(data) // 16)
    struct.pack_into("<4H", root, 0x24, clip.fps, clip.frame_count, clip.joint_count, clip.flags)
    return bytes(root) + bytes(hdr) + b"\0" * (dat - hat - len(hdr)) + bytes(data)


# ---- sampling: the game's arithmetic (Hermite between the two keys around the time, per component) ----

ORIENT_TANGENT_SCALE = 1.0 / 131072.0  # server 0xbdc7b0: 2^-17 per frame


def _key_frames(mask, start):
    return [start + i for i in range(64) if (mask >> i) & 1]


def _hermite(v0, t0, v1, t1, u, dt):
    u2 = u * u
    u3 = u2 * u
    return (v0 * (2 * u3 - 3 * u2 + 1) + t0 * dt * (u3 - 2 * u2 + u) + v1 * (-2 * u3 + 3 * u2) + t1 * dt * (u3 - u2))


def _seg_index(clip, frame_int):
    for i, s in enumerate(clip.segments):
        if frame_int < s.end:
            return i
    return len(clip.segments) - 1


def _sample_track(t, seg_start, f, vscale, tscale, comps):
    frames = _key_frames(t.mask, seg_start)
    # the two keys around f; the game takes bits below min(f - start + 1, 63) as "at or before"
    k = min(int(f) - seg_start + 1, 63)
    lo = None
    for i, fr in enumerate(frames):
        if fr - seg_start < k:
            lo = i
    if lo is None:
        lo = 0
    hi = min(lo + 1, len(frames) - 1)
    f0, f1 = frames[lo], frames[hi]
    dt = float(f1 - f0)
    u = (f - f0) / dt if dt else 0.0
    (v0, t0), (v1, t1) = t.keys[lo], t.keys[hi]
    return [_hermite(v0[c] * vscale, t0[c] * tscale, v1[c] * vscale, t1[c] * tscale, u, dt) for c in range(comps)]


def sample(clip, time_s, default_pose, wrap=None):
    """Pose at a time in seconds: {joint: (quat xyzw, pos xyz, scale xyz)}. `default_pose` as in parse_default_pose."""
    f = time_s * clip.fps
    fi = int(f)
    frac = f - fi
    if clip.flags == 1:
        fi %= clip.frame_count
    elif clip.flags == 0 and fi >= clip.frame_count:
        fi = clip.frame_count - 1
        frac = 0.999
    return sample_frame(clip, fi + frac, default_pose)


def sample_frame(clip, f, default_pose):
    s = clip.segments[_seg_index(clip, int(f))]
    n = clip.joint_count
    rot = [None] * n
    pos = [None] * n
    scl = [None] * n
    for t in s.orientation.animated:
        q = _sample_track(t, s.start, f, ORIENT_VALUE_SCALE, ORIENT_TANGENT_SCALE, 4)
        m = math.sqrt(sum(x * x for x in q)) or 1.0
        rot[t.joint] = [x / m for x in q]
    for j, v in s.orientation.constant:
        rot[j] = [x * ORIENT_VALUE_SCALE for x in v]
    for j in s.orientation.default:
        rot[j] = list(default_pose[0][j])
    for t in s.position.animated:
        pos[t.joint] = _sample_track(t, s.start, f, clip.position_factor, clip.position_tangent, 3)
    for j, v in s.position.constant:
        pos[j] = [x * clip.position_factor for x in v]
    for j in s.position.default:
        pos[j] = list(default_pose[1][j])
    for t in s.scale.animated:
        scl[t.joint] = _sample_track(t, s.start, f, clip.scale_factor, ORIENT_TANGENT_SCALE, 3)
    for j, v in s.scale.constant:
        scl[j] = [x * clip.scale_factor for x in v]
    for j in s.scale.default:
        scl[j] = list(default_pose[2][j])
    return rot, pos, scl


def parse_default_pose(raw_hierarchy):
    """ModelHierarchyResource bytes -> (quats, positions, scales) of its packed defaultPose (orientation[], position[],
    scale[], each `alignedJointCount` float4)."""
    joints = struct.unpack_from("<I", raw_hierarchy, 0x30)[0]
    rel, blocks = struct.unpack_from("<iI", raw_hierarchy, 0x34)
    aligned = struct.unpack_from("<I", raw_hierarchy, 0x3c)[0]
    base = 0x34 + rel
    rot = [struct.unpack_from("<4f", raw_hierarchy, base + i * 16) for i in range(joints)]
    pos = [struct.unpack_from("<3f", raw_hierarchy, base + (aligned + i) * 16) for i in range(joints)]
    scl = [struct.unpack_from("<3f", raw_hierarchy, base + (2 * aligned + i) * 16) for i in range(joints)]
    return rot, pos, scl


# ---- encoding: dense per-frame tracks -> keys. Any key choice the format can hold plays; this one is error bounded ----

def _clamp16(x):
    return max(-32767, min(32767, int(round(x))))


def _fit_track(values, first, last, eval_err, tol):
    """Pick key frames in [first, last] for one track: start with the ends, add the worst frame until every frame is
    within tol. `eval_err(keys, f)` returns the error at frame f with those keys."""
    keys = [first, last]
    while True:
        worst, worst_e = None, tol
        for a, b in zip(keys, keys[1:]):
            for f in range(a + 1, b):
                e = eval_err(a, b, f)
                if e > worst_e:
                    worst, worst_e = f, e
        if worst is None:
            return keys
        keys.append(worst)
        keys.sort()


def _tangents(frames_v):
    """Central differences per frame, one sided at the ends: C1 across segments because all share one dense curve."""
    n = len(frames_v)
    c = len(frames_v[0])
    out = []
    for i in range(n):
        a = frames_v[max(i - 1, 0)]
        b = frames_v[min(i + 1, n - 1)]
        d = (min(i + 1, n - 1) - max(i - 1, 0)) or 1
        out.append([(b[k] - a[k]) / d for k in range(c)])
    return out


def encode(frames, default_pose, hierarchy_hash, fps=60, loop=False, rot_tol=0.0005, pos_tol=0.0002, scale_tol=0.0005):
    """Dense samples -> Clip. `frames` = list over frame 0..N (N+1 samples, duration N/fps) of (rots, positions, scales),
    each a list per joint in the game's left-handed, parent-relative space. Returns a Clip for `build`.
    Tolerances: quaternion components, metres, scale units (per frame, before quantization)."""
    n_frames = len(frames) - 1
    joints = len(frames[0][0])

    # orientation hemisphere continuity per joint
    rots = [[list(fr[0][j]) for fr in frames] for j in range(joints)]
    for track in rots:
        for i in range(1, len(track)):
            if sum(a * b for a, b in zip(track[i - 1], track[i])) < 0:
                track[i] = [-x for x in track[i]]
    poss = [[list(fr[1][j]) for fr in frames] for j in range(joints)]
    scls = [[list(fr[2][j]) for fr in frames] for j in range(joints)]

    rot_tan = [_tangents(t) for t in rots]
    pos_tan = [_tangents(t) for t in poss]
    scl_tan = [_tangents(t) for t in scls]

    # the clip wide factors: one i16 range for all position / scale values and tangents
    def maxabs(tracks):
        return max((abs(x) for t in tracks for fr in t for x in fr), default=0.0)

    pf = (maxabs(poss) or 1.0) / 32767.0
    pt = (maxabs(pos_tan) or 1.0) / 32767.0
    sf = (maxabs(scls) or 1.0) / 32767.0

    def channel(tracks, tans, dflt, tol, vscale, tscale, comps, normalise):
        def segment(start, end):
            ch = Channel()
            for j in range(joints):
                vals = tracks[j][start:end + 1]
                dv = dflt[j]
                # sign-insensitive for quaternions
                sign = 1.0
                if normalise and sum(a * b for a, b in zip(vals[0], dv)) < 0:
                    sign = -1.0
                if all(max(abs(v[k] - sign * dv[k]) for k in range(comps)) <= tol for v in vals):
                    ch.default.append(j)
                    continue
                first = vals[0]
                if all(max(abs(v[k] - first[k]) for k in range(comps)) <= tol for v in vals):
                    ch.constant.append((j, tuple(_clamp16(first[k] / vscale) for k in range(comps))))
                    continue

                def err(a, b, f):
                    dt = float(b - a)
                    u = (f - a) / dt
                    t0, t1 = tans[j][a], tans[j][b]
                    v0, v1 = tracks[j][a], tracks[j][b]
                    worst = 0.0
                    for c in range(comps):
                        h = _hermite(v0[c], t0[c], v1[c], t1[c], u, dt)
                        worst = max(worst, abs(h - tracks[j][f][c]))
                    return worst

                keys = _fit_track(vals, start, end, err, tol)
                t = Track(j)
                for f in keys:
                    t.mask |= 1 << (f - start)
                    t.keys.append((tuple(_clamp16(tracks[j][f][k] / vscale) for k in range(comps)),
                                   tuple(_clamp16(tans[j][f][k] / tscale) for k in range(comps))))
                ch.animated.append(t)
            return ch
        return segment

    seg_rot = channel(rots, rot_tan, default_pose[0], rot_tol, ORIENT_VALUE_SCALE, ORIENT_TANGENT_SCALE, 4, True)
    seg_pos = channel(poss, pos_tan, default_pose[1], pos_tol, pf, pt, 3, False)
    seg_scl = channel(scls, scl_tan, default_pose[2], scale_tol, sf, ORIENT_TANGENT_SCALE, 3, False)
    segments = []
    start = 0
    while start < n_frames or not segments:
        end = min(start + SEGMENT_FRAMES, n_frames)
        segments.append(Segment(start, start + SEGMENT_FRAMES, seg_rot(start, end), seg_pos(start, end), seg_scl(start, end)))
        start += SEGMENT_FRAMES
    return Clip(hierarchy_hash, pf, pt, sf, fps, n_frames, joints, 1 if loop else 0, segments)


def fnv1a64(data):
    v = 0xcbf29ce484222325
    for x in data:
        v = ((v ^ x) * 0x100000001b3) & 0xFFFFFFFFFFFFFFFF
    return v


def hierarchy_hash(joint_names):
    """JointAnimation.hierarchyHash: FNV-1a 64 over the skeleton's u32 joint name hashes (little endian)."""
    return fnv1a64(struct.pack(f"<{len(joint_names)}I", *joint_names))


def write_gltf(clip, default_pose, hierarchy, path, tri=None):
    """Inspection export: the skeleton (nodes j<index>, right handed like tools/kfc's model export) with the clip as a
    glTF animation, sampled at every frame. `tri`: a right-handed mesh.Tris of a model on this skeleton, to skin."""
    from . import mesh as kmesh
    skeleton = kmesh.skeleton_rh(hierarchy)
    kmesh.write_gltf(tri if tri is not None else kmesh.Tris(), path, name="clip", skeleton=skeleton)
    doc = json.loads(Path(path).read_text())
    blob = bytearray(base64.b64decode(doc["buffers"][0]["uri"].split(",", 1)[1]))
    first = next(i for i, n in enumerate(doc["nodes"]) if n.get("name") == "j0")

    def add(data, comp_type, count, mn=None, mx=None):
        while len(blob) % 4:
            blob.append(0)
        doc["bufferViews"].append({"buffer": 0, "byteOffset": len(blob), "byteLength": len(data)})
        blob.extend(data)
        a = {"bufferView": len(doc["bufferViews"]) - 1, "componentType": 5126, "count": count, "type": comp_type}
        if mn is not None:
            a["min"], a["max"] = mn, mx
        doc["accessors"].append(a)
        return len(doc["accessors"]) - 1

    n = clip.frame_count + 1
    times = [f / clip.fps for f in range(n)]
    tacc = add(struct.pack(f"<{n}f", *times), "SCALAR", n, [times[0]], [times[-1]])
    frames = [sample_frame(clip, min(float(f), clip.frame_count - 1 + 0.999), default_pose) for f in range(n)]
    samplers, channels = [], []
    for j in range(clip.joint_count):
        # mirror x: position (-x, y, z), rotation (x, -y, -z, w), as tools/kfc's skeleton export
        rots = b"".join(struct.pack("<4f", fr[0][j][0], -fr[0][j][1], -fr[0][j][2], fr[0][j][3]) for fr in frames)
        poss = b"".join(struct.pack("<3f", -fr[1][j][0], fr[1][j][1], fr[1][j][2]) for fr in frames)
        scls = b"".join(struct.pack("<3f", *fr[2][j]) for fr in frames)
        for data, kind, path_name in ((rots, "VEC4", "rotation"), (poss, "VEC3", "translation"), (scls, "VEC3", "scale")):
            samplers.append({"input": tacc, "output": add(data, kind, n), "interpolation": "LINEAR"})
            channels.append({"sampler": len(samplers) - 1, "target": {"node": first + j, "path": path_name}})
    doc["animations"] = [{"name": "clip", "samplers": samplers, "channels": channels}]
    doc["buffers"][0] = {"byteLength": len(blob),
                         "uri": "data:application/octet-stream;base64," + base64.b64encode(bytes(blob)).decode()}
    Path(path).write_text(json.dumps(doc))
