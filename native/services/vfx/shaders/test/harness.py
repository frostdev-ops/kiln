# SPDX-License-Identifier: MIT
"""The game's CPU side of a VFX particle chunk, in Python: allocation, seeding, per-tick reset, dispatch and A/B flip.

As read from client 26430625: chunk allocation 0x197a20, chunk init 0x1b1aa0, tick 0x1a6660 (docs/re/vfx.md §3).
`check()` holds the invariants a module has to keep. Modules run through spvsim, so vanilla modules can be used to
validate this harness: if the protocol as written here were wrong, they would break it.
"""
import random
import struct
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

from spvsim import Buffer, Sim, f2u, u2f  # noqa: E402

# (set, binding) of keen::VfxUpdatePipelineLayout: a buffer is made for each one a module asks for
UPDATE = (1, 0)
TRANSPARENT = {1: (1, 1), 2: (1, 2), 3: (1, 3)}
SORT = (1, 4)
LIGHTS = (1, 9)
LIGHT_SORT = (1, 10)
EMITTERS = (1, 13)
MESHES = (1, 17)
CHUNKS = (1, 39)
INSTANCES = (1, 40)
RESOURCE = (1, 41)
STATE = (1, 42)
COUNTERS = (0, 2)

# VfxUpdateBuffer
KU = dict(time_step=0x004, split=0x008, emitter_count=0x220, cap=0x224, cap_unlit=0x228, cap_lit=0x22c, cap_shaded=0x230,
          count=0x234, count_unlit=0x238, count_lit=0x23c, count_shaded=0x240, light_cap=0x25c, light_count=0x260,
          exposure_correction=0x288, camera_position=0x294, camera_world=0x2a4, camera_orientation=0x2d0, frustum=0x2e0)

# TransparentInstanceData
TI = dict(indexCount=0x00, instanceCount=0x04, indexOffset=0x08, vertexOffset=0x0c, instanceOffset=0x10, meshIndex=0x14,
          image=0x18, normals=0x1c, imageSampler=0x20, cameraRelativePosition=0x2c, depthFadeDistance=0x38, rotation=0x3c,
          scale=0x4c, color=0x58, alpha=0x64, flags=0x9c, flipbookTime=0xa8, encodedFlipbookSize=0xac)


class Entry:
    """One VfxParticleResource: module bytes, depth, parent, child, maxCount, stateSize, parameter bytes."""

    def __init__(self, code, depth=0, parent=0xffffffff, child=0, max_count=1, state_size=32, params=b"", initial=b""):
        self.code = code
        self.depth = depth
        self.parent = parent
        self.child = child
        self.max_count = max_count
        self.state_size = state_size
        self.params = params
        self.initial = initial
        self.sim = Sim(code) if isinstance(code, (bytes, bytearray)) else code


class Chunk:
    pass


def align16(n):
    return (n + 15) & ~15


class World:
    """One instance of one VFX, on its own buffers."""

    def __init__(self, entries, seed=1, cap=256, light_cap=64, split=False, dt=1 / 60, position=(10.0, 20.0, 30.0),
                 prepare=None, meshes=None, resource=None, n_instances=1):
        self.entries = entries
        self.rand = random.Random(seed)
        self.parity = 0
        self.ticks = 0
        self.buf = {}
        self.dummy_size = 1 << 16
        self.prepare = prepare
        self.split = split
        self.cap = cap

        # gpuResourceData, in the order the VFX lists them (base 0 here)
        res = b""
        for e in entries:
            if resource is None:
                e.res_start = len(res)
                res += bytes(e.params)
        if resource is not None:
            res = bytes(resource)   # a game VFX: the entries carry their own res_start
        self.buf[RESOURCE] = Buffer("resource", max(len(res), 4))
        self.buf[RESOURCE].data[:len(res)] = res

        # 0x197a20: a root always gets one state; the others get maxCount
        self.chunks = []
        stateoff = 0
        counter = 16   # handle 0 is never used
        counters_init = []
        for i, e in enumerate(entries):
            c = Chunk()
            c.index = i
            c.count = 1 if e.depth == 0 else e.max_count
            c.states = stateoff
            size = c.count * e.state_size
            a = align16(stateoff + size)
            c.list_a = a
            b = align16(a + 4 * c.count)
            c.list_b = b
            f = align16(b + 4 * c.count)
            c.free_list = f
            stateoff = align16(f + 4 * c.count)
            seeded = 1 if e.depth == 0 else 0
            c.counter_a = counter
            c.counter_b = counter + 4
            c.counter_free = counter + 8
            counters_init += [(c.counter_a, seeded), (c.counter_b, seeded), (c.counter_free, c.count - seeded)]
            counter += 12
            c.has_emitter = e.child != 0
            if c.has_emitter:
                c.emitter = len([x for x in self.chunks if x.has_emitter])
                c.particle_counter = counter
                c.spawn_counter = counter + 4
                counter += 8
            self.chunks.append(c)
        self.buf[STATE] = Buffer("chunkState", stateoff + 16)
        self.buf[COUNTERS] = Buffer("counters", counter + 64)
        for off, v in counters_init:
            self.buf[COUNTERS].put_u32(off, v)

        n_emit = len([c for c in self.chunks if c.has_emitter])
        self.buf[EMITTERS] = Buffer("emitterStates", 32 * max(n_emit, 1))
        self.chunks_tab = [Buffer("chunkUpdateA", 68 * len(entries)), Buffer("chunkUpdateB", 68 * len(entries))]
        self.buf[CHUNKS] = self.chunks_tab[0]
        self.buf[INSTANCES] = Buffer("instances", 128 * max(2, n_instances))

        # 0x1b1aa0: both chunk tables, the emitter records, the seeded root state and the lists
        for i, (e, c) in enumerate(zip(entries, self.chunks)):
            parent = self.chunks[e.parent] if e.parent != 0xffffffff else None
            child = self.chunks[e.child] if e.child != 0 else None
            for flip, tab in enumerate(self.chunks_tab):
                active, nxt = (c.counter_a, c.counter_b) if flip == 0 else (c.counter_b, c.counter_a)
                old, new = (c.list_a, c.list_b) if flip == 0 else (c.list_b, c.list_a)
                words = [0, e.res_start, 0, active, nxt, c.counter_free, c.states, old, new, c.free_list,
                         parent.states if parent else 0,
                         (parent.list_a if flip == 0 else parent.list_b) if parent else 0,
                         0,
                         child.states if child else 0,
                         child.free_list if child else 0,
                         (child.list_a if flip == 0 else child.list_b) if child else 0,
                         i]
                for k, w in enumerate(words):
                    tab.put_u32(i * 68 + k * 4, w)
            if c.has_emitter:
                em = self.buf[EMITTERS]
                base = c.emitter * 32
                em.put_u32(base + 0, c.particle_counter)
                em.put_u32(base + 4, c.spawn_counter)
                em.put_u32(base + 8, child.count)
                em.put_u32(base + 12, 0)
                em.put_u32(base + 16, e.child)
                em.put_u32(base + 20, 0)  # instance slot
            st = self.buf[STATE]
            if e.depth == 0:
                st.put_u32(c.states + 0, c.emitter if c.has_emitter else 0xffffffff)
                st.put_u32(c.states + 4, 0)
                st.put_u32(c.states + 8, 0)  # instance slot
                st.put_u32(c.states + 12, self.rand.getrandbits(32))
                st.put_u32(c.states + 16, 0)
                ini = bytes(e.initial)
                st.data[c.states + 20:c.states + 20 + len(ini)] = ini
                for k in range(c.count):
                    st.put_u32(c.list_a + 4 * k, k)
                    st.put_u32(c.list_b + 4 * k, k)
            else:
                for k in range(c.count):
                    st.put_u32(c.free_list + 4 * k, c.count - 1 - k)

        # the instance
        inst = self.buf[INSTANCES]
        self.set_instance(position=position, flags=1)

        up = Buffer("update", 1392)
        self.buf[UPDATE] = up
        up.put_f32(KU["time_step"], dt)
        up.put_u32(KU["split"], 1 if split else 0)
        up.put_u32(KU["emitter_count"], n_emit)
        up.put_f32(KU["exposure_correction"], 1.0)
        # one list and counter per kind
        self.buf[COUNTERS].data.extend(b"\0" * 64)
        base = counter + 64
        self.draw_counters = dict(count=base, count_unlit=base + 4, count_lit=base + 8, count_shaded=base + 12, light=base + 16)
        for k in ("count", "count_unlit", "count_lit", "count_shaded"):
            up.put_u32(KU[k], self.draw_counters[k])
        up.put_u32(KU["light_count"], self.draw_counters["light"])
        for k in ("cap", "cap_unlit", "cap_lit", "cap_shaded"):
            up.put_u32(KU[k], cap)
        up.put_u32(KU["light_cap"], light_cap)
        for k, b in TRANSPARENT.items():
            self.buf[b] = Buffer(f"transparent{k}", 176 * cap)
        self.buf[SORT] = Buffer("sort", 8 * cap)
        self.buf[LIGHTS] = Buffer("lights", 52 * light_cap)
        self.buf[LIGHT_SORT] = Buffer("lightSort", 8 * light_cap)
        # a far-away frustum that culls nothing: planes with w = -1e6 (inside when dot + w <= radius)
        for i in range(5):
            up.put_f32(KU["frustum"] + i * 16 + 12, -1.0e6)
        up.put_f32(KU["camera_orientation"] + 12, 1.0)   # identity

        # g_vfxMeshes: index 0 means none; index 1 is a quad
        mesh = Buffer("vfxMeshes", 32 * 8)
        self.buf[MESHES] = mesh
        for i, (vo, io, ic, vc, r) in enumerate(meshes or [(0, 0, 0, 0, 0.0), (0, 0, 6, 4, 0.7071)]):
            mesh.put_u32(i * 32 + 0, vo)
            mesh.put_u32(i * 32 + 4, io)
            mesh.put_u32(i * 32 + 8, ic)
            mesh.put_u32(i * 32 + 12, vc)
            mesh.put_f32(i * 32 + 28, r)

        self.pc = Buffer("push", 8)
        self.spawned_total = 0

    def set_instance(self, position=None, flags=None, scale=None, orientation=(0.0, 0.0, 0.0, 1.0), slot=0):
        inst = self.buf[INSTANCES]
        base = 128 * slot
        if position is not None:
            for i, v in enumerate(position):
                inst.put_f32(base + i * 4, v)
        if flags is not None:
            inst.put_u32(base + 12, flags)
        for i, v in enumerate(orientation):
            inst.put_f32(base + 16 + i * 4, v)
        if scale is None and inst.f32(base + 32) == 0.0:
            scale = (1.0, 1.0, 1.0)
        if scale is not None:
            for i, v in enumerate(scale):
                inst.put_f32(base + 32 + i * 4, v)

    def set_play(self, color=None, size=1.0, speed=1.0, brightness=1.0, count=1.0, magic=True, slot=0):
        """The per-play parameters (KX_*) of an instance, as the game copies a start block or Kiln's defaults into it."""
        inst = self.buf[INSTANCES]
        base = 128 * slot + P["KX_BASE"]
        r, g, b, a = color or (255, 255, 255, 255)
        inst.put_u32(base + P["KX_COLOR"], r | g << 8 | b << 16 | a << 24)
        inst.put_f32(base + P["KX_SIZE"], size)
        inst.put_f32(base + P["KX_SPEED"], speed)
        inst.put_f32(base + P["KX_BRIGHTNESS"], brightness)
        inst.put_f32(base + P["KX_COUNT"], count)
        inst.put_u32(base + P["KX_MAGIC"], 0x4E4C494B if magic else 0)

    def assign_instance(self, chunk_index, slot):
        """Move a root entry, and the emitter behind it, to another instance slot (what instance activation does)."""
        c = self.chunks[chunk_index]
        self.buf[STATE].put_u32(c.states + 8, slot)
        if c.has_emitter:
            self.buf[EMITTERS].put_u32(c.emitter * 32 + 20, slot)

    def bindings_for(self, sim):
        """A buffer for every binding the module names: unknown ones get a large zero buffer."""
        out = dict(self.buf)
        for sb in sim.m.bindings():
            if sb not in out:
                out[sb] = self.buf.setdefault(sb, Buffer(f"unused{sb}", self.dummy_size))
        return out

    def counter(self, off):
        return self.buf[COUNTERS].u32(off)

    def active_counter(self, c):
        return c.counter_a if self.parity == 0 else c.counter_b

    def next_counter(self, c):
        return c.counter_b if self.parity == 0 else c.counter_a

    def old_list(self, c):
        return c.list_a if self.parity == 0 else c.list_b

    def new_list(self, c):
        return c.list_b if self.parity == 0 else c.list_a

    def tick(self, phase=0, shuffle=True, reset_draws=True):
        cnt = self.buf[COUNTERS]
        self.buf[CHUNKS] = self.chunks_tab[self.parity]
        if reset_draws:
            for off in self.draw_counters.values():
                cnt.put_u32(off, 0)
        # VfxPrepare
        em = self.buf[EMITTERS]
        for c in self.chunks:
            if c.has_emitter:
                base = c.emitter * 32
                child = self.chunks[self.entries[c.index].child]
                if self.prepare is not None:
                    self.prepare.run({(0, 0): self._prepare_ubo(), (0, 1): self._prepare_list(c), (0, 2): cnt,
                                      (0, 3): em, (0, 4): self.chunks_tab[self.parity]}, self.pc, 0)
                else:
                    live = cnt.u32(c.particle_counter)
                    free = cnt.u32(child.counter_free)
                    em.put_u32(base + 12, min(free, (em.u32(base + 8) - live) & 0xffffffff))
                    cnt.put_u32(c.spawn_counter, 0)
        # the "next" counters start each tick at 0
        for c in self.chunks:
            cnt.put_u32(self.next_counter(c), 0)
        # dispatch: by depth, a barrier between depths, groups of 32
        for depth in sorted({e.depth for e in self.entries}):
            for c in self.chunks:
                e = self.entries[c.index]
                if e.depth != depth:
                    continue
                self.pc.put_u32(0, c.index * 4 | phase)
                self.pc.put_u32(4, 0)
                n = (c.count + 31) // 32 * 32
                order = list(range(n))
                if shuffle:
                    self.rand.shuffle(order)
                b = self.bindings_for(e.sim)
                for i in order:
                    e.sim.run(b, self.pc, i)
        self.parity ^= 1
        self.ticks += 1

    def _prepare_ubo(self):
        b = Buffer("prepare", 16)
        b.put_u32(0, 1)
        return b

    def _prepare_list(self, c):
        b = Buffer("activeEmitters", 4)
        b.put_u32(0, c.emitter)
        return b

    # ---- what a tick must leave behind -------------------------------------------------------------------------------
    def check(self):
        """Raises AssertionError when a chunk's bookkeeping is off. parity already flipped: the list just written is 'old'."""
        st = self.buf[STATE]
        for c in self.chunks:
            active = self.counter(self.active_counter(c))
            free = self.counter(c.counter_free)
            if active + free != c.count:
                raise AssertionError(f"chunk {c.index}: active {active} + free {free} != {c.count}")
            ids = [st.u32(self.old_list(c) + 4 * i) for i in range(active)]
            ids += [st.u32(c.free_list + 4 * i) for i in range(free)]
            if sorted(ids) != list(range(c.count)):
                raise AssertionError(f"chunk {c.index}: active and free lists are not a partition of 0..{c.count - 1}")
            if c.has_emitter:
                child = self.chunks[self.entries[c.index].child]
                live = self.counter(c.particle_counter)
                kids = self.counter(self.active_counter(child))
                if live != kids:
                    raise AssertionError(f"chunk {c.index}: emitter counts {live} particles, child has {kids}")

    def draws(self, binding=3, count_key="count"):
        n = self.counter(self.draw_counters[count_key])
        return [self.record(self.buf[(1, binding)], i) for i in range(min(n, self.cap))]

    @staticmethod
    def record(buf, i):
        base = 176 * i
        r = {k: buf.u32(base + o) for k, o in TI.items() if k in ("indexCount", "instanceCount", "indexOffset", "vertexOffset",
                                                                   "instanceOffset", "meshIndex", "image", "imageSampler", "flags", "encodedFlipbookSize")}
        for k in ("depthFadeDistance", "alpha", "flipbookTime"):
            r[k] = buf.f32(base + TI[k])
        r["position"] = tuple(buf.f32(base + TI["cameraRelativePosition"] + 4 * i2) for i2 in range(3))
        r["rotation"] = tuple(buf.f32(base + TI["rotation"] + 4 * i2) for i2 in range(4))
        r["scale"] = tuple(buf.f32(base + TI["scale"] + 4 * i2) for i2 in range(3))
        r["color"] = tuple(buf.f32(base + TI["color"] + 4 * i2) for i2 in range(3))
        return r


# ---- parameter blocks, as a VFX builder writes them --------------------------------------------------------------------
def _defs():
    """The #defines of kiln_vfx_params.h as numbers."""
    import re
    out = {}
    for line in (Path(__file__).resolve().parents[1] / "kiln_vfx_params.h").read_text().splitlines():
        m = re.match(r"#define (\w+)\s+([0-9.]+)", line)
        if m:
            out[m.group(1)] = float(m.group(2)) if "." in m.group(2) else int(m.group(2))
    return out


P = _defs()


def emitter_block(duration=0.0, delay=0.0, rate=0.0, burst=0, loop=False, child_state=None):
    b = bytearray(P["KE_BLOCK_SIZE"])
    struct.pack_into("<I", b, P["KE_VERSION"], P["KILN_VFX_ABI"])
    struct.pack_into("<I", b, P["KE_FLAGS"], P["KE_FLAG_LOOP"] if loop else 0)
    struct.pack_into("<f", b, P["KE_DURATION"], duration)
    struct.pack_into("<f", b, P["KE_DELAY"], delay)
    struct.pack_into("<f", b, P["KE_RATE"], rate)
    struct.pack_into("<I", b, P["KE_BURST"], burst)
    struct.pack_into("<I", b, P["KE_CHILD_STATE"], child_state if child_state is not None else P["KS_STATE_SIZE"])
    return bytes(b)


def sprite_block(life=(1.0, 1.0), speed=(1.0, 2.0), draw_flags=0, flags=None, image=7, sampler=2, mesh=1, spread=0, angle=0.0,
                 accel=(0.0, 0.0, 0.0), drag=0.0, size=None, alpha=None, color=None, flipbook=0, fps=0.0, emissive=0.0, radius=0.0,
                 spin=(0.0, 0.0), roll=(0.0, 0.0), depth_fade=0.0, shape=None, shape_size=(0.0, 0.0, 0.0), stretch=0.0, user=None):
    n = P["KS_CURVE_SAMPLES"]
    size = size or [0.5] * n
    alpha = alpha or [1.0] * n
    color = color or [(1.0, 0.5, 0.25)] * n
    b = bytearray(P["KS_BLOCK_SIZE"])
    pu = lambda k, v: struct.pack_into("<I", b, P[k], v)
    pf = lambda k, v: struct.pack_into("<f", b, P[k], v)
    pu("KS_VERSION", P["KILN_VFX_ABI"])
    pu("KS_DRAW_FLAGS", draw_flags)
    pu("KS_FLAGS", P["KS_FLAG_FACE_CAMERA"] if flags is None else flags)
    pu("KS_IMAGE", image)
    pu("KS_SAMPLER", sampler)
    pu("KS_MESH", mesh)
    pf("KS_LIFE_MIN", life[0])
    pf("KS_LIFE_MAX", life[1])
    pf("KS_SPEED_MIN", speed[0])
    pf("KS_SPEED_MAX", speed[1])
    pu("KS_SPREAD_KIND", spread)
    pf("KS_SPREAD_ANGLE", angle)
    pf("KS_SPAWN_RADIUS", radius)
    if shape is None and radius > 0:   # ABI 1's spawnRadius, as the builder writes it now
        shape, shape_size = P["KS_SHAPE_SPHERE"], (radius, 0.0, 0.0)
    pu("KS_SHAPE", shape or 0)
    pf("KS_SHAPE_A", shape_size[0])
    pf("KS_SHAPE_B", shape_size[1])
    pf("KS_SHAPE_C", shape_size[2])
    pf("KS_STRETCH", stretch)
    for i, v in enumerate(user or []):
        struct.pack_into("<f", b, P["KS_USER"] + 4 * i, v)
    pf("KS_ACCEL_X", accel[0])
    pf("KS_ACCEL_Y", accel[1])
    pf("KS_ACCEL_Z", accel[2])
    pf("KS_DRAG", drag)
    pf("KS_SPIN_MIN", spin[0])
    pf("KS_SPIN_MAX", spin[1])
    pf("KS_ROLL_MIN", roll[0])
    pf("KS_ROLL_MAX", roll[1])
    pf("KS_DEPTH_FADE", depth_fade)
    pu("KS_FLIPBOOK", flipbook)
    pf("KS_FLIPBOOK_FPS", fps)
    pf("KS_ROUGHNESS", 1.0)
    pf("KS_EMISSIVE", emissive)
    for i in range(n):
        struct.pack_into("<f", b, P["KS_SIZE_CURVE"] + 4 * i, size[i])
        struct.pack_into("<f", b, P["KS_ALPHA_CURVE"] + 4 * i, alpha[i])
        struct.pack_into("<3f", b, P["KS_COLOR_CURVE"] + 12 * i, *color[i])
    return bytes(b)


def light_block(lifetime=0.0, fade_in=0.0, fade_out=0.0, radius=6.0, brightness=4.0, color=(0.5, 0.8, 1.0), flicker=0.0, offset=(0.0, 0.0, 0.0)):
    b = bytearray(P["KL_BLOCK_SIZE"])
    struct.pack_into("<I", b, P["KL_VERSION"], P["KILN_VFX_ABI"])
    struct.pack_into("<f", b, P["KL_LIFETIME"], lifetime)
    struct.pack_into("<f", b, P["KL_FADE_IN"], fade_in)
    struct.pack_into("<f", b, P["KL_FADE_OUT"], fade_out)
    struct.pack_into("<f", b, P["KL_RADIUS"], radius)
    struct.pack_into("<f", b, P["KL_BRIGHTNESS"], brightness)
    struct.pack_into("<3f", b, P["KL_COLOR"], *color)
    struct.pack_into("<f", b, P["KL_FLICKER"], flicker)
    struct.pack_into("<3f", b, P["KL_OFFSET"], *offset)
    return bytes(b)
