#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""sound.py: read-only views of the game's sounds (docs/re/audio.md).

    sound.py [--game DIR] [--exe EXE] <command> ...

    list [--grep RX] [--limit N]         keen::SoundResource: guid, format, channels, seconds, inline/stream, name
    info GUID                            a SoundResource, SoundContainerResource or VoiceoverResource, followed down
                                         to its sounds (containers: entries; voice-overs: each language)
    wav GUID [-o DIR]                    decode to 16-bit WAV: a sound, every entry of a container, or every
                                         language of a voice-over (default DIR $KILN_OUT/sound)
    roundtrip IN.wav [-o OUT.wav]        encode a 16-bit WAV with the reference FramelessQOA encoder, decode it
                                         again, report size and SNR (checks the codec; writes no game data)

Defaults as kfc.py: --game ~/.local/share/Steam/steamapps/common/Enshrouded, read only; outputs go to $KILN_OUT.
"""
import argparse
import json
import re
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

from kfc import sound as S  # noqa: E402
from kfc.archive import Kfc, ResourceKey  # noqa: E402
from kfc.blob import Decoder  # noqa: E402
from kfc.refl import TypeDb  # noqa: E402
from kfc.util import out_dir  # noqa: E402

DEFAULT_GAME = Path.home() / ".local/share/Steam/steamapps/common/Enshrouded"
NULL = "00000000-0000-0000-0000-000000000000"
LANGS = ("english", "german", "french", "spanish", "simplifiedChinese")


class Ctx:
    def __init__(self, a):
        self.game = Path(a.game).expanduser()
        exe = Path(a.exe).expanduser() if a.exe else next(
            (self.game / n for n in ("enshrouded.exe", "enshrouded_server.exe") if (self.game / n).exists()), None)
        if exe is None:
            raise SystemExit(f"no exe in {self.game}; pass --exe")
        self.kfc = Kfc(self.game)
        self.types = TypeDb.from_exe(exe)
        self.dec = Decoder(self.types)

    def th(self, name):
        return self.types.get(name)["hash"]

    def get(self, guid, tname):
        key = ResourceKey(guid, self.th(tname), 0)
        if not self.kfc.has(key):
            return None
        return self.dec.decode(key[1], self.kfc.read(key))

    def kind(self, guid):
        for t in ("keen::SoundResource", "keen::SoundContainerResource", "keen::VoiceoverResource"):
            if self.kfc.has(ResourceKey(guid, self.th(t), 0)):
                return t
        return None


def _sound_line(guid, s):
    inline = isinstance(s["data"], dict) and bool(s["data"].get("bytes"))
    return (f"{guid} {s['format']:<12} {s['channelConfiguration']:<10} {s['frameCount'] / s['framesPerSecond']:7.2f} s "
            f"{'inline' if inline else 'stream'} {s['debugName']}")


def _entries(c):
    for e in c["entries"]:
        v = e.get("value", e)
        yield e.get("$type", "?"), v.get("chance"), v.get("sound")


def cmd_list(c, a):
    rx = re.compile(a.grep, re.I) if a.grep else None
    n = 0
    for key in c.kfc.resources_of_type(c.th("keen::SoundResource")):
        s = c.dec.decode(key[1], c.kfc.read(key))
        if rx and not rx.search(s["debugName"]):
            continue
        print(_sound_line(key.guid, s))
        n += 1
        if a.limit and n >= a.limit:
            break


def _show_container(c, guid, indent=""):
    cont = c.get(guid, "keen::SoundContainerResource")
    if cont is None:
        print(f"{indent}{guid}: no SoundContainerResource")
        return
    head = {k: v for k, v in cont.items() if k != "entries"}
    print(f"{indent}container {guid}: {json.dumps(head)}")
    for typ, chance, sg in _entries(cont):
        s = c.get(sg, "keen::SoundResource") if sg else None
        print(f"{indent}  {typ.split('::')[-1]} chance {chance}: " + (_sound_line(sg, s) if s else f"{sg} (no sound)"))


def cmd_info(c, a):
    t = c.kind(a.guid)
    if t is None:
        raise SystemExit(f"{a.guid}: not a sound, container or voice-over resource")
    if t == "keen::SoundResource":
        print(_sound_line(a.guid, c.get(a.guid, t)))
    elif t == "keen::SoundContainerResource":
        _show_container(c, a.guid)
    else:
        vo = c.get(a.guid, t)
        print(f"voiceover {a.guid}: durations (10 ms units) {vo['durations']}")
        for lang, g in zip(LANGS, vo["languages"]):
            if g == NULL:
                print(f"  {lang}: none")
            else:
                print(f"  {lang}:")
                _show_container(c, g, "    ")


def _wav_sound(c, guid, outdir, prefix=""):
    s = c.get(guid, "keen::SoundResource")
    rate, pcm = S.decode_sound(c.kfc, s)
    p = outdir / f"{prefix}{s['debugName']}.wav"
    S.write_wav(p, rate, pcm)
    print(f"{p} ({len(pcm)} ch, {len(pcm[0]) / rate:.2f} s)")


def cmd_wav(c, a):
    outdir = Path(a.o) if a.o else out_dir("sound")
    outdir.mkdir(parents=True, exist_ok=True)
    t = c.kind(a.guid)
    if t == "keen::SoundResource":
        _wav_sound(c, a.guid, outdir)
    elif t == "keen::SoundContainerResource":
        for _, _, sg in _entries(c.get(a.guid, t)):
            _wav_sound(c, sg, outdir)
    elif t == "keen::VoiceoverResource":
        vo = c.get(a.guid, t)
        for lang, g in zip(LANGS, vo["languages"]):
            if g != NULL:
                for _, _, sg in _entries(c.get(g, "keen::SoundContainerResource")):
                    _wav_sound(c, sg, outdir, f"{lang}_")
    else:
        raise SystemExit(f"{a.guid}: not a sound, container or voice-over resource")


def cmd_roundtrip(a):
    rate, pcm = S.read_wav(a.input)
    if rate != S.FRAMES_PER_SECOND:
        print(f"note: {a.input} is {rate} Hz; the game only plays 48000 Hz sounds (a cooker must resample)")
    data = S.encode_qoa(pcm)
    back = S.decode_qoa(data, len(pcm[0]), len(pcm))
    snr = min(S.snr_db(x, y) for x, y in zip(pcm, back))
    out = Path(a.o) if a.o else out_dir("sound") / (Path(a.input).stem + ".roundtrip.wav")
    S.write_wav(out, rate, back)
    print(f"{a.input}: {len(pcm)} ch, {len(pcm[0])} frames -> {len(data)} bytes FramelessQOA "
          f"({8 * len(data) / max(1, len(pcm[0]) * len(pcm)):.2f} bits/sample), SNR {snr:.1f} dB; decoded: {out}")


def main(argv=None):
    ap = argparse.ArgumentParser(prog="sound.py", description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--game", default=str(DEFAULT_GAME))
    ap.add_argument("--exe")
    sp = ap.add_subparsers(dest="cmd", required=True)
    p = sp.add_parser("list")
    p.add_argument("--grep")
    p.add_argument("--limit", type=int, default=0)
    p = sp.add_parser("info")
    p.add_argument("guid")
    p = sp.add_parser("wav")
    p.add_argument("guid")
    p.add_argument("-o")
    p = sp.add_parser("roundtrip")
    p.add_argument("input")
    p.add_argument("-o")
    a = ap.parse_args(argv)
    if a.cmd == "roundtrip":
        return cmd_roundtrip(a)
    c = Ctx(a)
    return {"list": cmd_list, "info": cmd_info, "wav": cmd_wav}[a.cmd](c, a)


if __name__ == "__main__":
    sys.exit(main())
