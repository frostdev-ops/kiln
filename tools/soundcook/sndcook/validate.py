# SPDX-License-Identifier: MIT
"""Checks every cooked sound file passes (docs/kiln/modding/sounds.md "Validation"), with tools/kfc's decoder."""
import math

from kfc import sound as S

from . import pcm as P
from . import res as R

MIN_SNR_DB = 30.0       # QOA's normal range on tonal material; below this a warning (noise-like sources score lower)
FAIL_SNR_DB = 6.0       # below this something is broken
MAX_CLIPPED = 0.001     # 0.1 % of samples


def _snr(ref, test):
    num = sum(x * x for x in ref)
    if num == 0:
        return None
    den = sum((x - y) * (x - y) for x, y in zip(ref, test))
    return float("inf") if den == 0 else 10 * math.log10(num / den)


def sound(t, data, blob, pcm16, frames, channels, stats, native=None):
    """data = SoundResource bytes, blob = streamed bytes or None, pcm16 = the interleaved int16 the encoder got."""
    checks, warnings, info = {}, [], {}
    v = R.decode(t, R.SOUND, data)
    checks["reencodes_byte_exact"] = R.encode(t, R.SOUND, v) == data
    checks["rate_48000"] = v["framesPerSecond"] == R.RATE
    checks["format_framelessqoa"] = v["format"] == "FramelessQOA"
    checks["frames"] = v["frameCount"] == frames
    checks["channels"] = S.CHANNELS.get(v["channelConfiguration"]) == channels
    checks["duration"] = v["duration"]["value"] == R.duration_ns(frames)
    inline_bytes = bytes.fromhex(v["data"]["bytes"]) if isinstance(v["data"], dict) and v["data"].get("bytes") else b""
    inline = bool(inline_bytes)
    checks["inline_iff_1s"] = inline == (frames <= R.INLINE_MAX_FRAMES)
    qoa = inline_bytes if inline else (blob or b"")
    checks["qoa_size"] = len(qoa) == S.qoa_size(frames, channels)
    if inline:
        checks["no_stream_reference"] = v["streamedData"]["hash"]["size"] == 0
    else:
        checks["content_hash"] = v["streamedData"]["hash"] == R.content_hash(qoa)
        checks["stream_category"] = v["streamedData"]["category"]["value"] == R.STREAM_CATEGORY
    clipped = stats.get("clipped", 0)
    checks["clipping_below_0.1%"] = clipped <= MAX_CLIPPED * max(1, frames * channels)
    if checks["qoa_size"]:
        if native is not None and frames * channels > 120 * R.RATE:
            dec = P.from_bytes(native.decode(qoa, frames, channels))
            info["decoder"] = "kiln-soundcook"
            inter = dec
        else:
            per = S.decode_qoa(qoa, frames, channels)
            info["decoder"] = "tools/kfc"
            inter = [per[c][i] for i in range(frames) for c in range(channels)]
        snrs = [_snr(pcm16[c::channels], inter[c::channels]) for c in range(channels)]
        snrs = [s for s in snrs if s is not None]
        snr = min(snrs) if snrs else None
        info["snr_db"] = None if snr is None else (round(snr, 2) if math.isfinite(snr) else "inf")
        if snr is None:
            warnings.append("the sound is digital silence")
        else:
            checks["snr_above_6dB"] = snr >= FAIL_SNR_DB
            if snr < MIN_SNR_DB:
                warnings.append(f"QOA SNR {snr:.1f} dB < {MIN_SNR_DB:.0f} dB (normal for noise-like sources; listen to it: "
                                "soundcook.py wav)")
    if stats.get("normalize_limited"):
        warnings.append("normalize was limited by the peak (-1 dBFS ceiling): the sound is quieter than the target")
    if clipped:
        warnings.append(f"{clipped} samples clipped (lower the gain)")
    return checks, warnings, info, v


def cue(t, data, known_sounds):
    """known_sounds: {guid: True (exists) | None (cannot check: no game)}."""
    checks, warnings = {}, []
    v = R.decode(t, R.CUE, data)
    checks["reencodes_byte_exact"] = R.encode(t, R.CUE, v) == data
    checks["has_takes"] = len(v["entries"]) > 0
    missing = [e["value"]["sound"] for e in v["entries"] if known_sounds.get(e["value"]["sound"]) is False]
    unknown = [e["value"]["sound"] for e in v["entries"] if e["value"]["sound"] not in known_sounds]
    checks["takes_exist"] = not missing and not unknown
    if any(known_sounds.get(e["value"]["sound"]) is None and e["value"]["sound"] in known_sounds for e in v["entries"]):
        warnings.append("vanilla takes not checked (no installed client)")
    checks["distances"] = 0 <= v["minDistance"] <= v["maxDistance"]
    if v["avoidRepeatingLastX"] >= max(1, len(v["entries"])) and len(v["entries"]) > 1:
        warnings.append(f"avoidRepeatingLastX {v['avoidRepeatingLastX']} >= takes {len(v['entries'])}")
    return checks, warnings, v


def voiceover(t, data, cue_guids):
    checks = {}
    v = R.decode(t, R.VO, data)
    checks["reencodes_byte_exact"] = R.encode(t, R.VO, v) == data
    checks["slots_name_cues"] = all(g == R.NULL_GUID or g in cue_guids for g in v["languages"])
    checks["durations_u16"] = all(0 <= d < 65536 for d in v["durations"])
    return checks, v
