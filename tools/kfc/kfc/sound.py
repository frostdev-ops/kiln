# SPDX-License-Identifier: MIT
"""Keen's sound data: `keen::SoundResource` (FramelessQOA / PCM), containers and voice-overs. Read-only helpers for
docs/re/audio.md, plus a slow reference encoder for tests and for checking a future cooker.

The codec is QOA ("Quite OK Audio", Dominic Szablewski, qoaformat.org; spec and reference code are MIT) without its
file header, frame headers and stored LMS state. Written from the QOA specification and Kiln's own disassembly of
the client's decoder (client 26430625: slice decoder 0x753590, stream decoder 0x10552f0, LMS init 0x753570); no
other code was used.

FramelessQOA, as the game reads it:
- 48,000 frames per second (the loader rejects any other rate);
- per channel one LMS predictor, started at history {0,0,0,0} and weights {0, 0, -(1<<13), 1<<14} (QOA's encoder
  start state), never reset or stored: the predictor runs continuously over the whole sound;
- a slice is one little-endian u64 for 20 samples of one channel: bits 63..60 the scale factor index, then twenty
  3-bit residual indices from bit 59 down; a short last slice is left-aligned (its unused low bits are padding);
- slices are interleaved by channel: slice i of channel c is u64 number i * channels + c;
- size = ceil(frameCount / 20) * 8 * channels bytes.
`Uncompressed` sounds are interleaved little-endian int16 PCM.
"""
import math
import struct
import wave

FRAMES_PER_SECOND = 48000
SLICE_LEN = 20
CHANNELS = {"Mono": 1, "Stereo": 2, "Quadrophonic": 4, "FiveDotOne": 6, "SevenDotOne": 8}

SCALEFACTORS = [round((s + 1) ** 2.75) for s in range(16)]          # 1, 7, 21, 45, ... 2048
_DQ_BASE = (0.75, -0.75, 2.5, -2.5, 4.5, -4.5, 7.0, -7.0)


def _round_away(x):
    return int(math.copysign(math.floor(abs(x) + 0.5), x))


DEQUANT = [[_round_away(sf * q) for q in _DQ_BASE] for sf in SCALEFACTORS]   # the exe's int32 table (16 x 8)
RECIPROCAL = [((1 << 16) + sf - 1) // sf for sf in SCALEFACTORS]
QUANT = [7, 7, 7, 5, 5, 3, 3, 1, 0, 0, 2, 2, 4, 4, 6, 6, 6]                   # residual -8..8 -> index


def qoa_size(frames, channels):
    return math.ceil(frames / SLICE_LEN) * 8 * channels


class Lms:
    __slots__ = ("h", "w")

    def __init__(self):
        self.h = [0, 0, 0, 0]
        self.w = [0, 0, -(1 << 13), 1 << 14]

    def copy(self):
        n = Lms()
        n.h = self.h[:]
        n.w = self.w[:]
        return n

    def predict(self):
        h, w = self.h, self.w
        v = h[0] * w[0] + h[1] * w[1] + h[2] * w[2] + h[3] * w[3]
        return (((v + 0x80000000) & 0xFFFFFFFF) - 0x80000000) >> 13

    def update(self, sample, residual):
        d = residual >> 4
        h, w = self.h, self.w
        for i in range(4):
            w[i] += -d if h[i] < 0 else d
        h[0], h[1], h[2], h[3] = h[1], h[2], h[3], sample


def _clamp16(v):
    return -32768 if v < -32768 else 32767 if v > 32767 else v


def decode_qoa(data, frames, channels):
    """FramelessQOA bytes -> list of per-channel lists of int16 sample values (length `frames` each)."""
    n_slices = math.ceil(frames / SLICE_LEN)
    need = n_slices * 8 * channels
    if len(data) < need:
        raise ValueError(f"FramelessQOA: {len(data)} bytes, {need} needed for {frames} frames x {channels} channels")
    words = struct.unpack_from(f"<{n_slices * channels}Q", data)
    out = []
    for c in range(channels):
        lms = Lms()
        h, w = lms.h, lms.w
        pcm = []
        append = pcm.append
        for s in words[c::channels]:
            dq = DEQUANT[s >> 60]
            for i in range(SLICE_LEN):
                r = dq[(s >> (57 - 3 * i)) & 7]
                v = h[0] * w[0] + h[1] * w[1] + h[2] * w[2] + h[3] * w[3]
                v = ((((v + 0x80000000) & 0xFFFFFFFF) - 0x80000000) >> 13) + r   # 32-bit wrap, as the exe
                v = -32768 if v < -32768 else 32767 if v > 32767 else v
                d = r >> 4
                w[0] += -d if h[0] < 0 else d
                w[1] += -d if h[1] < 0 else d
                w[2] += -d if h[2] < 0 else d
                w[3] += -d if h[3] < 0 else d
                h[0], h[1], h[2], h[3] = h[1], h[2], h[3], v
                append(v)
        out.append(pcm[:frames])
    return out


def decode_pcm16(data, frames, channels):
    vals = struct.unpack_from(f"<{frames * channels}h", data)
    return [list(vals[c::channels]) for c in range(channels)]


def _div(v, sf):
    """QOA's rounding division of a residual by a scale factor (reciprocal multiply, away from zero)."""
    n = (v * RECIPROCAL[sf] + (1 << 15)) >> 16
    return n + ((v > 0) - (v < 0)) - ((n > 0) - (n < 0))


def encode_qoa(channels_pcm):
    """Reference encoder (slow, pure Python; for tests and for checking a native cooker): per-channel int16 lists ->
    FramelessQOA bytes. Scale factor search as in the QOA specification, ranking candidates by squared error plus
    the squared LMS weights penalty. Any valid choice of scale factors decodes; only quality depends on the search."""
    channels = len(channels_pcm)
    frames = len(channels_pcm[0]) if channels else 0
    if any(len(p) != frames for p in channels_pcm):
        raise ValueError("channels differ in length")
    n_slices = math.ceil(frames / SLICE_LEN)
    lms = [Lms() for _ in range(channels)]
    words = [0] * (n_slices * channels)
    for si in range(n_slices):
        start = si * SLICE_LEN
        end = min(start + SLICE_LEN, frames)
        for c in range(channels):
            samples = channels_pcm[c][start:end]
            best = None
            for sf in range(16):
                t = lms[c].copy()
                err = 0
                slice_ = sf
                for x in samples:
                    p = t.predict()
                    q = QUANT[max(-8, min(8, _div(x - p, sf))) + 8]
                    r = DEQUANT[sf][q]
                    v = _clamp16(p + r)
                    e = x - v
                    err += e * e
                    w = t.w
                    penalty = ((w[0] * w[0] + w[1] * w[1] + w[2] * w[2] + w[3] * w[3]) >> 18) - 0x8FF
                    if penalty > 0:
                        err += penalty * penalty
                    if best is not None and err >= best[0]:
                        break
                    t.update(v, r)
                    slice_ = (slice_ << 3) | q
                else:
                    slice_ <<= 3 * (SLICE_LEN - len(samples))
                    if best is None or err < best[0]:
                        best = (err, slice_, t)
            words[si * channels + c] = best[1]
            lms[c] = best[2]
    return struct.pack(f"<{len(words)}Q", *words)


# ---------------------------------------------------------------- game resources
def sound_bytes(kfc, desc):
    """The sample data of a decoded keen::SoundResource: inline `data`, else the `streamedData` content blob."""
    from .archive import ContentHash
    d = desc.get("data")
    if isinstance(d, dict) and d.get("bytes"):
        return bytes.fromhex(d["bytes"])
    if isinstance(d, (bytes, bytearray)) and d:
        return bytes(d)
    h = desc["streamedData"]["hash"]
    if not h["size"]:
        return b""
    return kfc.read_content(ContentHash(h["size"], h["hash0"], h["hash1"], h["hash2"]))


def decode_sound(kfc, desc):
    """keen::SoundResource (decoded dict) -> (rate, per-channel int16 lists)."""
    ch = CHANNELS[desc["channelConfiguration"]]
    raw = sound_bytes(kfc, desc)
    if desc["format"] == "FramelessQOA":
        return desc["framesPerSecond"], decode_qoa(raw, desc["frameCount"], ch)
    if desc["format"] == "Uncompressed":
        return desc["framesPerSecond"], decode_pcm16(raw, desc["frameCount"], ch)
    raise ValueError(f"unknown SoundDataFormat {desc['format']}")


def write_wav(path, rate, channels_pcm):
    n = len(channels_pcm[0]) if channels_pcm else 0
    inter = [channels_pcm[c][i] for i in range(n) for c in range(len(channels_pcm))]
    with wave.open(str(path), "wb") as f:
        f.setnchannels(len(channels_pcm))
        f.setsampwidth(2)
        f.setframerate(rate)
        f.writeframes(struct.pack(f"<{len(inter)}h", *inter))


def read_wav(path):
    """16-bit PCM WAV -> (rate, per-channel int16 lists). (A cooker would also take 24/32-bit and float.)"""
    with wave.open(str(path), "rb") as f:
        if f.getsampwidth() != 2:
            raise ValueError(f"{path}: {8 * f.getsampwidth()}-bit WAV; this reader takes 16-bit PCM")
        ch, rate, n = f.getnchannels(), f.getframerate(), f.getnframes()
        vals = struct.unpack(f"<{n * ch}h", f.readframes(n))
    return rate, [list(vals[c::ch]) for c in range(ch)]


def snr_db(ref, test):
    num = sum(x * x for x in ref)
    den = sum((x - y) * (x - y) for x, y in zip(ref, test))
    return float("inf") if den == 0 else 10 * math.log10(max(num, 1) / den)
