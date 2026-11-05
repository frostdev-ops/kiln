# SPDX-License-Identifier: MIT
"""The PCM pipeline in pure Python: what kiln-soundcook's `cook` does, for 48 kHz WAV sources when the native helper
is not available (no resampling, no FLAC/Ogg/MP3 here). Written to give the same bytes as the native helper: the
same decode to doubles (integer samples / 2^31, float WAV through float32), the same order of double operations and
the same dither (splitmix64, TPDF of two uniforms), so a cook does not depend on which path ran.
"""
import math
import struct
from pathlib import Path

RATE = 48000
MASK64 = (1 << 64) - 1
GAME_CHANNELS = (1, 2, 4, 6, 8)


class PcmError(Exception):
    pass


def fnv1a64(text):
    h = 0xCBF29CE484222325
    for c in text.encode():
        h = ((h ^ c) * 0x100000001B3) & MASK64
    return h


class Prng:
    """splitmix64, as kiln-soundcook's prng."""

    def __init__(self, seed):
        self.s = seed & MASK64

    def next(self):
        self.s = (self.s + 0x9E3779B97F4A7C15) & MASK64
        z = self.s
        z = ((z ^ (z >> 30)) * 0xBF58476D1CE4E5B9) & MASK64
        z = ((z ^ (z >> 27)) * 0x94D049BB133111EB) & MASK64
        return z ^ (z >> 31)

    def unit(self):
        return (self.next() >> 11) * (1.0 / 9007199254740992.0)


_SUBFORMAT_TAIL = bytes.fromhex("000000001000800000aa00389b71")


def read_wav(path):
    """WAV (PCM 8/16/24/32-bit, IEEE float 32/64, WAVE_FORMAT_EXTENSIBLE) -> dict with interleaved doubles."""
    b = Path(path).read_bytes()
    if len(b) < 12 or b[:4] not in (b"RIFF", b"RF64") or b[8:12] != b"WAVE":
        raise PcmError(f"{path}: not a WAV file")
    pos, fmt, data = 12, None, None
    while pos + 8 <= len(b):
        cid, size = b[pos:pos + 4], struct.unpack_from("<I", b, pos + 4)[0]
        body = b[pos + 8:pos + 8 + size]
        if cid == b"fmt ":
            tag, ch, rate, _, align, bits = struct.unpack_from("<HHIIHH", body, 0)
            if tag == 0xFFFE and len(body) >= 40:
                if body[26:40] != _SUBFORMAT_TAIL:
                    raise PcmError(f"{path}: unknown WAVE_FORMAT_EXTENSIBLE subformat")
                tag = struct.unpack_from("<H", body, 24)[0]
            fmt = (tag, ch, rate, align, bits)
        elif cid == b"data":
            data = body
        pos += 8 + size + (size & 1)
    if fmt is None or data is None:
        raise PcmError(f"{path}: no fmt or data chunk")
    tag, ch, rate, align, bits = fmt
    if not 1 <= ch <= 8:
        raise PcmError(f"{path}: {ch} channels")
    frames = len(data) // align if align else 0
    if frames == 0:
        raise PcmError(f"{path}: no audio frames")
    n = frames * ch
    if tag == 1 and bits in (8, 16, 24, 32):
        if bits == 8:
            vals = [((x - 128) << 24) / 2147483648.0 for x in data[:n]]
        elif bits == 16:
            vals = [(x << 16) / 2147483648.0 for x in struct.unpack_from(f"<{n}h", data)]
        elif bits == 24:
            vals = [(int.from_bytes(data[3 * i:3 * i + 3], "little", signed=True) << 8) / 2147483648.0 for i in range(n)]
        else:
            vals = [x / 2147483648.0 for x in struct.unpack_from(f"<{n}i", data)]
        kind = "wav-pcm"
    elif tag == 3 and bits == 32:
        vals = list(struct.unpack_from(f"<{n}f", data))
        kind = "wav-float"
    elif tag == 3 and bits == 64:
        # through float32, as dr_wav's f32 reader
        vals = [struct.unpack("<f", struct.pack("<f", x))[0] for x in struct.unpack_from(f"<{n}d", data)]
        kind = "wav-float"
    else:
        raise PcmError(f"{path}: WAV format tag {tag}, {bits}-bit: needs kiln-soundcook")
    return {"rate": rate, "channels": ch, "frames": frames, "bits": bits if tag == 1 else 0,
            "exact16": tag == 1 and bits == 16, "format": kind, "data": vals}


def _measure(x, frames, ch, gain):
    block, floor_ms = 2400, math.pow(10.0, -50.0 / 10.0)
    pk, act_sum, act_n = 0.0, 0.0, 0
    for b0 in range(0, frames, block):
        b1 = min(b0 + block, frames)
        s = 0.0
        for i in range(b0 * ch, b1 * ch):
            v = x[i] * gain
            av = abs(v)
            if av > pk:
                pk = av
            s += v * v
        n = (b1 - b0) * ch
        if s / n > floor_ms:
            act_sum += s
            act_n += n
    return pk, (math.sqrt(act_sum / act_n) if act_n else 0.0)


def _dbfs(v):
    return 20.0 * math.log10(v) if v > 0 else float("-inf")


def cook(path, channels="keep", gain=None, normalize=None, start=0.0, end=0.0, seed=0):
    """Source file -> (interleaved int16 list, stats like kiln-soundcook cook's JSON). 48 kHz WAV only."""
    a = read_wav(path)
    if a["rate"] != RATE:
        raise PcmError(f"{path}: {a['rate']} Hz: resampling to 48000 Hz needs kiln-soundcook")
    src = {"format": a["format"], "rate": a["rate"], "channels": a["channels"], "frames": a["frames"], "bits": a["bits"]}
    x, ch, frames = a["data"], a["channels"], a["frames"]
    if start > 0 or end > 0:
        s0 = math.floor(start * a["rate"] + 0.5)
        s1 = min(math.floor(end * a["rate"] + 0.5), frames) if end > 0 else frames
        if s0 >= s1:
            raise PcmError("start/end leave no audio")
        x = x[s0 * ch:s1 * ch]
        frames = s1 - s0
    mixed = False
    target = {"mono": 1, "stereo": 2, "keep": ch}.get(channels)
    if target is None:
        raise PcmError("channels: mono, stereo or keep")
    if target == 1 and ch > 1:
        out = []
        for f in range(frames):
            s = 0.0
            for c in range(ch):
                s += x[f * ch + c]
            out.append(s / ch)
        x, ch, mixed = out, 1, True
    elif target == 2 and ch == 1:
        x = [v for v in x for _ in (0, 1)]
        ch = 2
    elif target == 2 and ch > 2:
        raise PcmError("stereo from more than 2 channels is not supported: use mono or keep")
    if ch not in GAME_CHANNELS:
        raise PcmError("the game plays 1, 2, 4, 6 or 8 channels")
    if gain is not None and normalize is not None:
        raise PcmError("gain and normalize exclude each other")
    g, limited = 1.0, False
    if gain is not None and gain != 0:
        g = math.pow(10.0, gain / 20.0)
    if normalize is not None:
        peak, rms = _measure(x, frames, ch, 1.0)
        if rms <= 0:
            raise PcmError("normalize: the sound is silent (no 50 ms block above -50 dBFS)")
        g = math.pow(10.0, (normalize - _dbfs(rms)) / 20.0)
        ceiling = math.pow(10.0, -1.0 / 20.0)
        if peak * g > ceiling:
            g, limited = ceiling / peak, True
    peak, rms = _measure(x, frames, ch, g)
    exact = a["exact16"] and not mixed and g == 1.0
    pr = Prng(seed)
    pcm, clipped = [], 0
    for v in x:
        if exact:
            q = int(v * 32768.0)
        else:
            u1 = pr.unit()
            u2 = pr.unit()
            q = math.floor(v * g * 32768.0 + (u1 - u2) + 0.5)
        if q > 32767:
            q, clipped = 32767, clipped + 1
        elif q < -32768:
            q, clipped = -32768, clipped + 1
        pcm.append(q)
    stats = {"ok": True, "source": src, "rate": RATE, "channels": ch, "frames": frames, "resampled": False,
             "mixed": mixed, "exact": exact, "gain_db": _dbfs(g), "normalize_limited": limited,
             "peak_dbfs": _dbfs(peak), "rms_active_dbfs": _dbfs(rms), "clipped": clipped}
    return pcm, stats


def split(pcm, channels):
    return [pcm[c::channels] for c in range(channels)]


def to_bytes(pcm):
    return struct.pack(f"<{len(pcm)}h", *pcm)


def from_bytes(b):
    return list(struct.unpack(f"<{len(b) // 2}h", b))
