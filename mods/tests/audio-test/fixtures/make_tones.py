#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""audio-test's own fixture sounds: synthetic test tones encoded into the game's sound resources, so the test mod
does not depend on the sample mods' cook. Writes, under <mod build folder>/assets/:

  cook.json                  only {format, tool, guidNamespace} (the GUID namespace Kiln.Audio derives names from)
  cooked/*.sound.bin / *.cue.bin / *.vo.bin / *.qoa   SoundResource, SoundContainerResource, VoiceoverResource, blobs
  cooked/audio.json          the cooked index in soundcook's format
  assets.json                kiln.assets entries (new resources; streamed sounds get their blob hash patched in)

    python3 make_tones.py --exe <enshrouded.exe> --out $KILN_OUT/mods/audio-test

The game's reflection (type layouts) is read from the exe at run time, like any cook; templates for the field values
come from the reflection's defaults below, not from game files. Nothing here is game data, and the output stays in the
build folder (never committed). Tones (distinct from SoundSample's; the manual test names them):

  at_tone      2.0 s mono 880 Hz, streamed, positional 3-60 m            Sound.PlayAt
  loop_pulse   1.0 s mono 330 Hz pulsing 4x/s, loop, positional 2-40 m  Sound.PlayOn (follows), loop + Stop
  ui_beep      0.3 s mono 1320 Hz, inline, Ui                           Sound.Play2D
  music_chord  3.0 s stereo C4+E4 (262/330 Hz), loop, streamed, Music   Sound.PlayMusic
  vo_line      en: 2 beeps 600 Hz (0.8 s), de: 3 beeps 700 Hz (1.1 s), fr/es/zh empty      Voiceover.Play
  vo_narration en: 3 rising notes 450/500/550 Hz (2.4 s), durations set, de..zh empty     Voiceover.Narrate
"""
import argparse
import json
import math
import random
import sys
import uuid
from pathlib import Path

KFC = Path(__file__).resolve().parents[4] / "tools" / "kfc"
sys.path.insert(0, str(KFC))

from kfc import sound as S  # noqa: E402
from kfc.blobwrite import Encoder  # noqa: E402
from kfc.refl import TypeDb  # noqa: E402

NAMESPACE = uuid.UUID("a7d1c0de-5e70-4c3b-9e1a-0a0d10fe57a1")  # audio-test's own GUID namespace
RATE = 48000
NULL = "00000000-0000-0000-0000-000000000000"
CATEGORY = 3016399412  # the game's category of streamed sound blobs (docs/re/audio.md 1.3)


def gid(op, name):
    return str(uuid.uuid5(NAMESPACE, f"{op}:{name}"))


def tone(freqs, seconds, amp=0.35, pulse_hz=0.0, gaps=None):
    """int16 samples: a sum of sines with 5 ms fades; pulse_hz amplitude-modulates; gaps = [(start, end)] beeps."""
    n = int(seconds * RATE)
    rng = random.Random(1)
    out = []
    fade = int(0.005 * RATE)
    for i in range(n):
        t = i / RATE
        v = sum(math.sin(2 * math.pi * f * t) for f in freqs) / max(1, len(freqs))
        g = 1.0
        if pulse_hz:
            g *= 0.5 + 0.5 * math.sin(2 * math.pi * pulse_hz * t) ** 2
        if gaps is not None:
            on = [(a, b) for a, b in gaps if a <= t < b]
            if not on:
                g = 0.0
            else:
                a, b = on[0]
                g *= min(1.0, (t - a) * RATE / fade, (b - t) * RATE / fade)
        g *= min(1.0, i / fade, (n - 1 - i) / fade) if n > 2 * fade else 1.0
        s = v * amp * g + (rng.random() - rng.random()) / 32768.0  # TPDF dither
        out.append(max(-32768, min(32767, int(round(s * 32767)))))
    return out


def notes(seq, note_s, gap_s, amp=0.35):
    """A melody: [freq...] each note_s long with gap_s silence."""
    pcm = []
    for f in seq:
        pcm += tone([f], note_s, amp)
        pcm += [0] * int(gap_s * RATE)
    return pcm


def beeps(freq, count, beep_s=0.2, gap_s=0.15, amp=0.35):
    return notes([freq] * count, beep_s, gap_s, amp)


class Cook:
    def __init__(self, exe, out):
        self.types = TypeDb.from_exe(exe)
        self.enc = Encoder(self.types)
        self.out = Path(out) / "assets"
        (self.out / "cooked").mkdir(parents=True, exist_ok=True)
        self.entries = []
        self.index = {"format": 1, "mod": "audio-test", "guidNamespace": str(NAMESPACE), "cues": {}, "sounds": {},
                      "voiceovers": {}}

    def sound(self, name, channels_pcm):
        frames = len(channels_pcm[0])
        ch = len(channels_pcm)
        qoa = S.encode_qoa(channels_pcm)
        assert len(qoa) == math.ceil(frames / 20) * 8 * ch
        inline = frames <= RATE
        desc = {
            "channelConfiguration": "Mono" if ch == 1 else "Stereo",
            "format": "FramelessQOA",
            "framesPerSecond": RATE,
            "frameCount": frames,
            "duration": {"value": frames * 1_000_000_000 // RATE},
            "data": {"bytes": qoa.hex()} if inline else [],
            "streamedData": {"hash": {"size": 0 if inline else len(qoa), "hash0": 0, "hash1": 0, "hash2": 0},
                             "category": {"value": 0 if inline else CATEGORY}, "bundleId": 0, "sortKey": 0},
            "debugName": f"kiln_audiotest_{name}",
        }
        if inline:
            desc["streamedData"] = {"hash": {"size": 0, "hash0": 0, "hash1": 0, "hash2": 0}, "category": {"value": 0},
                                    "bundleId": 0, "sortKey": 0}
        g = gid("sound", name)
        rb = self.enc.encode("keen::SoundResource", desc)
        f = f"cooked/{name}.sound.bin"
        (self.out / f).write_bytes(rb)
        e = {"kind": "resource", "guid": g, "type": "keen::SoundResource", "part": 0, "mode": "new", "file": f}
        if not inline:
            bf = f"cooked/{name}.qoa"
            (self.out / bf).write_bytes(qoa)
            self.entries.append({"kind": "blob", "file": bf})
            e["patch"] = [{"offset": 24, "blob": bf}]
        self.entries.append(e)
        self.index["sounds"][name] = {"guid": g, "frames": frames, "channels": ch, "streamed": not inline, "mode": "new"}
        return g, frames

    def cue(self, name, sounds, scene="Sfx", volume=0.8, loop=False, positional=None, play_scene=None, guid=None):
        g = guid or gid("cue", name)
        desc = {
            "mode": "Random", "scene": scene, "loop": loop, "loopSameChosenEntry": False,
            "avoidRepeatingLastX": min(2, max(0, len(sounds) - 1)), "volume": volume, "volumeRandomness": 0.0,
            "pitch": 1.0, "pitchRandomness": 0.0, "sleep": {"value": 0}, "sleepRandomness": {"value": 0},
            "hasPosition": positional is not None,
            "minDistance": float(positional[0]) if positional else 1.0,
            "maxDistance": float(positional[1]) if positional else 50.0,
            "hasRandomPosition": False, "randomLeftRight": 0.0, "randomFrontBack": 0.0, "muteOnMain": False,
            "gamepadSpeaker": False, "gamepadSpeakerVolume": 1.0, "gamepadVibration": False,
            "gamepadVibrationIntensity": 1.0, "useMaxCountLimiting": True, "maxCount": 3, "stealingMode": "Furthest",
            "priority": 50,
            "entries": [{"$type": "keen::SoundContainerResourceSoundEntry", "value": {"chance": 1.0, "sound": s}}
                        for s, _ in sounds],
            "extraRumble": None,
        }
        rb = self.enc.encode("keen::SoundContainerResource", desc)
        f = f"cooked/{name}.cue.bin"
        (self.out / f).write_bytes(rb)
        self.entries.append({"kind": "resource", "guid": g, "type": "keen::SoundContainerResource", "part": 0,
                             "mode": "new", "file": f})
        dur = max(fr for _, fr in sounds) * 1000 // RATE
        ps = play_scene or {"Ui": "Ui", "Music": "Music"}.get(scene, "Game")
        self.index["cues"][name] = {"guid": g, "scene": scene, "loop": loop,
                                    "positional": {"min": positional[0], "max": positional[1]} if positional else None,
                                    "volume": volume, "durationMs": dur, "mode": "new", "takes": [s for s, _ in sounds],
                                    "playScene": ps}
        return g, dur

    def voiceover(self, name, langs, narration=False, subtitles=None, volume=0.8):
        slots = ["en", "de", "fr", "es", "zh"]
        guids, durs10, index_langs = [], [], {}
        for i, lang in enumerate(slots):
            pcm = langs.get(lang)
            if pcm is None:
                guids.append(NULL)
                durs10.append(0)
                index_langs[lang] = None
                continue
            sg = self.sound(f"{name}.{lang}.0", [pcm])
            cg, dur = self.cue(f"{name}.{lang}", [sg], scene="Voice", volume=volume, guid=gid("cue", f"{name}.{lang}"))
            del self.index["cues"][f"{name}.{lang}"]  # per-language cues are not public cue names
            guids.append(cg)
            ns = sg[1] * 1_000_000_000 // RATE
            durs10.append(ns // 10_000_000 if narration else 0)
            index_langs[lang] = {"cue": cg, "durationMs": dur}
        g = gid("voiceover", name)
        rb = self.enc.encode("keen::VoiceoverResource", {"languages": guids, "durations": durs10})
        f = f"cooked/{name}.vo.bin"
        (self.out / f).write_bytes(rb)
        self.entries.append({"kind": "resource", "guid": g, "type": "keen::VoiceoverResource", "part": 0, "mode": "new",
                             "file": f})
        self.index["voiceovers"][name] = {"guid": g, "languages": index_langs, "narration": narration, "fallback": None,
                                          "subtitles": subtitles or {}, "durations10ms": durs10}

    def finish(self):
        (self.out / "assets.json").write_text(json.dumps({"format": 1, "entries": self.entries}, indent=2) + "\n")
        (self.out / "cooked" / "audio.json").write_text(json.dumps(self.index, indent=2) + "\n")
        (self.out / "cook.json").write_text(json.dumps(
            {"format": 1, "tool": "soundcook", "out": "cooked", "guidNamespace": str(NAMESPACE),
             "comment": "audio-test fixture: generated by mods/tests/audio-test/fixtures/make_tones.py", "steps": []},
            indent=2) + "\n")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--exe", required=True, help="enshrouded.exe (reflection only)")
    ap.add_argument("--out", required=True, help="the built mod folder")
    a = ap.parse_args()
    c = Cook(a.exe, a.out)
    at = c.sound("at_tone", [tone([880], 2.0)])
    c.cue("at_tone", [at], positional=(3, 60))
    lp = c.sound("loop_pulse", [tone([330], 1.0, pulse_hz=2.0)])
    c.cue("loop_pulse", [lp], loop=True, positional=(2, 40))
    ui = c.sound("ui_beep", [tone([1320], 0.3)])
    c.cue("ui_beep", [ui], scene="Ui", volume=0.6)
    mc = [tone([262, 330], 3.0, amp=0.3), tone([262, 330], 3.0, amp=0.3)]
    m = c.sound("music_chord", mc)
    c.cue("music_chord", [m], scene="Music", volume=0.7, loop=True)
    c.voiceover("vo_line", {"en": beeps(600, 2), "de": beeps(700, 3)},
                subtitles={"en": "[audio-test] two beeps (English)", "de": "[audio-test] drei Pieptöne (Deutsch)"})
    c.voiceover("vo_narration", {"en": notes([450, 500, 550], 0.6, 0.2)}, narration=True,
                subtitles={"en": "[audio-test] narration: three rising notes"})
    c.finish()
    print(f"audio-test fixtures: {len(c.entries)} assets.json entries in {c.out}")
    for k in ("cues", "voiceovers"):
        for n, v in c.index[k].items():
            print(f"  {k[:-1]:9s} {n:13s} {v['guid']}")


if __name__ == "__main__":
    main()
