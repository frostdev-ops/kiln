#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""SoundSample's sources: distinct synthetic test tones (no game audio), in every input format soundcook reads.

    tones.py OUT_DIR          writes OUT_DIR/{ui,sfx,ambience,music,vo/en,vo/de}/... (FLAC/Ogg/MP3 need ffmpeg)

Each cue gets its own frequency so a recording of the desktop's audio output can tell them apart
(docs/kiln/modding/sounds.md): ui_chime 997 Hz, confirm_tone 1661 Hz, stereo_tone 440 Hz left / 660 Hz
right, blip_3d 1200/1500/1800 Hz, hum_loop 220 Hz, ambience_loop 330 + 331 Hz, music_track a C major arpeggio,
greeting en 2 x 523 Hz / de 3 x 784 Hz beeps, narration_intro 3 x 392 Hz notes. Loops hold whole cycles.
"""
import math
import shutil
import struct
import subprocess
import sys
import tempfile
from pathlib import Path

TAU = 2 * math.pi


def env(i, n, rate, attack=0.005, release=0.02):
    """Linear attack/release ramps (no clicks)."""
    a, r = max(1, int(attack * rate)), max(1, int(release * rate))
    return min(1.0, i / a, (n - 1 - i) / r) if n > 1 else 1.0


def tone(freq, seconds, rate, amp=0.5, decay=None, attack=0.005, release=0.02):
    n = int(round(seconds * rate))
    out = []
    for i in range(n):
        e = env(i, n, rate, attack, release)
        if decay:
            e *= math.exp(-i / rate / decay)
        out.append(amp * e * math.sin(TAU * freq * i / rate))
    return out


def beeps(freq, count, on, off, rate, amp=0.4, tail=0.2):
    out = []
    for k in range(count):
        out += tone(freq, on, rate, amp)
        out += [0.0] * int(round((off if k < count - 1 else tail) * rate))
    return out


def write_wav(path, rate, channels, bits=16, fmt="pcm"):
    """channels: list of per-channel float lists in [-1, 1)."""
    n = len(channels[0])
    inter = [channels[c][i] for i in range(n) for c in range(len(channels))]
    if fmt == "float":
        data, tag, bits = struct.pack(f"<{len(inter)}f", *inter), 3, 32
    elif bits == 16:
        data, tag = struct.pack(f"<{len(inter)}h", *(max(-32768, min(32767, int(round(v * 32767)))) for v in inter)), 1
    elif bits == 24:
        data = b"".join(max(-8388608, min(8388607, int(round(v * 8388607)))).to_bytes(3, "little", signed=True) for v in inter)
        tag = 1
    else:
        raise ValueError(bits)
    ch = len(channels)
    block = ch * bits // 8
    fmt_chunk = struct.pack("<HHIIHH", tag, ch, rate, rate * block, block, bits)
    path.parent.mkdir(parents=True, exist_ok=True)
    with open(path, "wb") as f:
        f.write(b"RIFF" + struct.pack("<I", 4 + 8 + len(fmt_chunk) + 8 + len(data)) + b"WAVE")
        f.write(b"fmt " + struct.pack("<I", len(fmt_chunk)) + fmt_chunk)
        f.write(b"data" + struct.pack("<I", len(data)) + data)


def convert(wav, out, codec_args):
    ff = shutil.which("ffmpeg")
    if ff is None:
        raise SystemExit(f"{out.name}: ffmpeg is needed for FLAC/Ogg/MP3 sources (the committed files are fine)")
    out.parent.mkdir(parents=True, exist_ok=True)
    subprocess.run([ff, "-hide_banner", "-loglevel", "error", "-y", "-i", str(wav), "-map_metadata", "-1",
                    "-fflags", "+bitexact", "-flags:a", "+bitexact"] + codec_args + [str(out)], check=True)


def main(out_dir):
    o = Path(out_dir)
    with tempfile.TemporaryDirectory() as d:
        tmp = Path(d)
        # UI: 0.5 s 997 Hz chime, 16-bit 48 kHz mono (inline, passes through without dither)
        write_wav(o / "ui" / "ui_chime.wav", 48000, [tone(997, 0.5, 48000, 0.5, decay=0.18)])
        # override of the vanilla dialog-confirm cue: 0.4 s 1661 Hz
        write_wav(o / "ui" / "confirm_tone.wav", 48000, [tone(1661, 0.4, 48000, 0.45, decay=0.15)])
        # 4 s stereo, 440 Hz left / 660 Hz right, FLAC at 44.1 kHz (resampled, streamed)
        write_wav(tmp / "stereo.wav", 44100, [tone(440, 4.0, 44100, 0.45), tone(660, 4.0, 44100, 0.45)])
        convert(tmp / "stereo.wav", o / "sfx" / "stereo_tone.flac", ["-c:a", "flac", "-compression_level", "8"])
        # 3 takes of a positional blip: 24-bit WAV, Ogg Vorbis, MP3
        write_wav(o / "sfx" / "blip_a.wav", 48000, [tone(1200, 0.3, 48000, 0.5, decay=0.12)], bits=24)
        write_wav(tmp / "b.wav", 44100, [tone(1500, 0.3, 44100, 0.5, decay=0.12)])
        convert(tmp / "b.wav", o / "sfx" / "blip_b.ogg", ["-c:a", "libvorbis", "-q:a", "6"])
        write_wav(tmp / "c.wav", 44100, [tone(1800, 0.3, 44100, 0.5, decay=0.12)])
        convert(tmp / "c.wav", o / "sfx" / "blip_c.mp3", ["-c:a", "libmp3lame", "-b:a", "128k"])
        # looping positional hum: exactly 2 s of 220 Hz (440 whole cycles), no ramps (seamless)
        write_wav(o / "sfx" / "hum_loop.wav", 48000, [[0.35 * math.sin(TAU * 220 * i / 48000) for i in range(96000)]])
        # looping 2D ambience: 8 s stereo, 330 Hz left / 331 Hz right with a 0.25 Hz swell (whole cycles), Ogg 48 kHz
        n = 8 * 48000
        sw = [0.6 + 0.4 * math.sin(TAU * 0.25 * i / 48000) for i in range(n)]
        write_wav(tmp / "amb.wav", 48000, [[0.3 * sw[i] * math.sin(TAU * 330 * i / 48000) for i in range(n)],
                                           [0.3 * sw[i] * math.sin(TAU * 331 * i / 48000) for i in range(n)]])
        convert(tmp / "amb.wav", o / "ambience" / "ambience_loop.ogg", ["-c:a", "libvorbis", "-q:a", "5"])
        # 12 s stereo music: C major arpeggio, notes alternate left/right, Ogg at 44.1 kHz
        notes = [261.63, 329.63, 392.0, 523.25, 392.0, 329.63]
        left, right = [], []
        for k in range(24):
            nt = tone(notes[k % len(notes)], 0.5, 44100, 0.4, decay=0.35)
            pan = 0.8 if k % 2 == 0 else 0.3
            left += [v * pan for v in nt]
            right += [v * (1.1 - pan) for v in nt]
        write_wav(tmp / "music.wav", 44100, [left, right])
        convert(tmp / "music.wav", o / "music" / "music_track.ogg", ["-c:a", "libvorbis", "-q:a", "5"])
        # voice-over stand-ins: en two 523 Hz beeps, de three 784 Hz beeps (mono 48 kHz)
        write_wav(o / "vo" / "en" / "greeting.wav", 48000, [beeps(523.25, 2, 0.3, 0.2, 48000)])
        write_wav(o / "vo" / "de" / "greeting.wav", 48000, [beeps(783.99, 3, 0.3, 0.15, 48000)])
        # narration stand-in: three slow 392 Hz notes, about 3 s (streamed)
        write_wav(tmp / "narr.wav", 48000, [beeps(392.0, 3, 0.8, 0.2, 48000, amp=0.35, tail=0.4)])
        convert(tmp / "narr.wav", o / "vo" / "en" / "narration_intro.flac", ["-c:a", "flac", "-compression_level", "8"])
    for f in sorted(o.rglob("*")):
        if f.is_file():
            print(f"{f.relative_to(o)}  {f.stat().st_size} B")


if __name__ == "__main__":
    if len(sys.argv) != 2:
        raise SystemExit(__doc__)
    main(sys.argv[1])
