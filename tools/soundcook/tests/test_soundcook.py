# SPDX-License-Identifier: MIT
"""soundcook tests.

Offline (no game files): the native FramelessQOA encoder/decoder against tools/kfc's Python reference (byte for
byte), the native PCM pipeline against the pure-Python one, the decoders and the resampler on synthetic tones and on
SoundSample's committed sources, recipes (sounds, cues, voice-overs, fallback, narration, manifest, index, re-cook
rules, errors), overrides against a synthetic KFC (tools/kfc's test builder) for a client and a dedicated server.
With the installed client (skipped when absent; read only): the bundled reflection matches the game's, vanilla sound
resources re-encode byte for byte with it, the native encoder reproduces the game's own QOA slices, and SoundSample's
recipe cooks with its override.

    python3 -m unittest discover -s tools/soundcook/tests -v
The native helper is built on first use (tools/soundcook/native/build.sh, needs cc); its tests skip without it.
"""
import json
import math
import random
import shutil
import struct
import subprocess
import sys
import tempfile
import unittest
import uuid
from pathlib import Path

HERE = Path(__file__).resolve().parent
TOOL = HERE.parent
REPO = TOOL.parents[1]
sys.path.insert(0, str(TOOL))
sys.path.insert(0, str(TOOL / "samples"))
sys.path.insert(0, str(TOOL.parent / "kfc"))
sys.path.insert(0, str(TOOL.parent / "kfc" / "tests"))

import tones  # noqa: E402
from kfc import sound as S  # noqa: E402
from kfc.archive import Kfc  # noqa: E402
from sndcook import pcm as P, recipe, res as R  # noqa: E402
from sndcook.native import Native, NativeError  # noqa: E402

NATIVE = Native.find(log=lambda m: None)
GAME = Path.home() / ".local/share/Steam/steamapps/common/Enshrouded"
HAVE_CLIENT = (GAME / "enshrouded.kfc").exists() and (GAME / "enshrouded.exe").exists()
SAMPLE = REPO / "mods" / "samples" / "SoundSample"
NS = "11111111-2222-4333-8444-555555555555"


def sine(n, freq, amp=8000, rate=48000, phase=0.0):
    return [int(round(amp * math.sin(2 * math.pi * freq * i / rate + phase))) for i in range(n)]


def inter(chans):
    return [chans[c][i] for i in range(len(chans[0])) for c in range(len(chans))]


def goertzel(x, freq, rate=48000):
    w = 2 * math.pi * freq / rate
    c = 2 * math.cos(w)
    s1 = s2 = 0.0
    for v in x:
        s1, s2 = v + c * s1 - s2, s1
    return s1 * s1 + s2 * s2 - c * s1 * s2


def dominant(x, freq, rate=48000):
    """Energy at freq vs at 0.8 x and 1.25 x freq: >> 1 for a clean tone."""
    e = goertzel(x, freq, rate)
    return e / max(1e-9, max(goertzel(x, freq * 0.8, rate), goertzel(x, freq * 1.25, rate)))


@unittest.skipUnless(NATIVE, "kiln-soundcook not built and no C compiler")
class NativeQoaTests(unittest.TestCase):
    def test_encoder_matches_python_reference(self):
        rnd = random.Random(5)
        cases = {
            "mono tone": [sine(4800, 440)],
            "stereo": [sine(3001, 300), sine(3001, 500, phase=1.0)],
            "noise": [[max(-32768, min(32767, int(rnd.gauss(0, 12000)))) for _ in range(2500)]],
            "full-scale square": [[32767 if (i // 37) % 2 else -32768 for i in range(1000)]],
            "silence": [[0] * 100],
            "1 frame": [[1234]], "19 frames": [sine(19, 1000)], "20 frames": [sine(20, 1000)],
            "21 frames": [sine(21, 1000)],
            "8 channels": [sine(500, 100 * (c + 1)) for c in range(8)],
            "6 channels": [sine(777, 150 * (c + 1)) for c in range(6)],
        }
        for name, chans in cases.items():
            with self.subTest(name):
                ref = S.encode_qoa(chans)
                got = NATIVE.encode(P.to_bytes(inter(chans)), len(chans))
                self.assertEqual(got, ref)

    def test_decoder_matches_python_on_any_bytes(self):
        # random slices push the LMS weights far out: checks the 32-bit prediction wrap and the int16 clamp
        rnd = random.Random(9)
        for ch, frames in ((1, 997), (2, 400), (4, 60)):
            data = bytes(rnd.randrange(256) for _ in range(S.qoa_size(frames, ch)))
            py = inter(S.decode_qoa(data, frames, ch))
            self.assertEqual(P.from_bytes(NATIVE.decode(data, frames, ch)), py)

    def test_selftest(self):
        r = subprocess.run([str(NATIVE.path), "selftest"], capture_output=True, text=True)
        self.assertEqual(r.returncode, 0, r.stdout)
        self.assertGreater(json.loads(r.stdout)["snr_db"], 30)


@unittest.skipUnless(NATIVE, "kiln-soundcook not built and no C compiler")
class PipelineTests(unittest.TestCase):
    """The native pipeline and the pure-Python one give the same int16 PCM and QOA for 48 kHz WAV."""

    @classmethod
    def setUpClass(cls):
        cls.tmp = tempfile.TemporaryDirectory()
        d = Path(cls.tmp.name)
        f = [x / 32768 for x in sine(9000, 440, 20000)]
        g = [x / 32768 for x in sine(9000, 700, 12000)]
        cls.files = {
            "s16 stereo": (tones.write_wav(d / "a.wav", 48000, [f, g]), d / "a.wav"),
            "s24 mono": (tones.write_wav(d / "b.wav", 48000, [f], bits=24), d / "b.wav"),
            "f32 mono": (tones.write_wav(d / "c.wav", 48000, [f], fmt="float"), d / "c.wav"),
        }
        # 8-bit and float64 WAVs by hand
        cls.files["u8 mono"] = (None, cls._wav(d / "d.wav", 1, 8, bytes(int(128 + 100 * math.sin(i / 7)) for i in range(5000))))
        cls.files["f64 mono"] = (None, cls._wav(d / "e.wav", 3, 64, struct.pack("<3000d", *(0.3 * math.sin(i / 9) for i in range(3000)))))

    @staticmethod
    def _wav(p, tag, bits, data):
        fmt = struct.pack("<HHIIHH", tag, 1, 48000, 48000 * bits // 8, bits // 8, bits)
        p.write_bytes(b"RIFF" + struct.pack("<I", 4 + 8 + len(fmt) + 8 + len(data)) + b"WAVE" + b"fmt " +
                      struct.pack("<I", len(fmt)) + fmt + b"data" + struct.pack("<I", len(data)) + data)
        return p

    @classmethod
    def tearDownClass(cls):
        cls.tmp.cleanup()

    def test_same_bytes(self):
        variants = [{}, {"channels": "mono"}, {"gain": -6.0}, {"normalize": -20.0}, {"start": 0.01, "end": 0.15},
                    {"channels": "stereo"}, {"gain": 30.0}]
        for name, (_, path) in self.files.items():
            for v in variants:
                with self.subTest(f"{name} {v}"):
                    nb, nq, ns = NATIVE.cook(path, seed=77, **v)
                    pp, ps = P.cook(path, seed=77, **v)
                    self.assertEqual(P.from_bytes(nb), pp)
                    self.assertEqual(nq, S.encode_qoa(P.split(pp, ps["channels"])))
                    for k in ("frames", "channels", "exact", "mixed", "clipped", "normalize_limited"):
                        self.assertEqual(ns[k], ps[k], k)

    def test_exact_passthrough_and_dither(self):
        _, path = self.files["s16 stereo"]
        pcm, st = P.cook(path)
        self.assertTrue(st["exact"])
        src = P.read_wav(path)
        self.assertEqual(pcm, [int(v * 32768) for v in src["data"]])
        _, st2 = P.cook(path, channels="mono")
        self.assertFalse(st2["exact"])                     # a mixdown is requantized with dither
        _, st3 = P.cook(path, gain=30.0)
        self.assertGreater(st3["clipped"], 0)

    def test_python_refuses_what_needs_the_helper(self):
        with tempfile.TemporaryDirectory() as d:
            p = Path(d) / "x.wav"
            tones.write_wav(p, 44100, [[0.1] * 100])
            with self.assertRaises(P.PcmError):
                P.cook(p)


@unittest.skipUnless(NATIVE, "kiln-soundcook not built and no C compiler")
class DecodeResampleTests(unittest.TestCase):
    def test_resampler_quality(self):
        with tempfile.TemporaryDirectory() as d:
            p = Path(d) / "t.wav"
            n = 88200
            tones.write_wav(p, 44100, [[0.5 * math.sin(2 * math.pi * 1000 * i / 44100) for i in range(n)]])
            pcm_b, _, st = NATIVE.cook(p, seed=1)
        self.assertTrue(st["resampled"])
        self.assertEqual(st["frames"], 96000)
        got = P.from_bytes(pcm_b)
        ideal = [0.5 * 32767 * math.sin(2 * math.pi * 1000 * i / 48000) for i in range(96000)]
        mid = slice(4800, 91200)
        err = sum((a - b) ** 2 for a, b in zip(got[mid], ideal[mid]))
        sig = sum(b * b for b in ideal[mid])
        self.assertGreater(10 * math.log10(sig / err), 50)    # no delay, no aliasing, only the dither's noise

    def test_sample_sources(self):
        cases = [("sfx/blip_a.wav", 48000, 1, 1200), ("sfx/blip_b.ogg", 44100, 1, 1500),
                 ("sfx/blip_c.mp3", 44100, 1, 1800), ("sfx/stereo_tone.flac", 44100, 2, 440),
                 ("sfx/hum_loop.wav", 48000, 1, 220), ("ambience/ambience_loop.ogg", 48000, 2, 330),
                 ("vo/en/narration_intro.flac", 48000, 1, 392)]
        for rel, rate, ch, freq in cases:
            with self.subTest(rel):
                src = SAMPLE / "audio" / rel
                pr = NATIVE.probe(src)
                self.assertEqual((pr["rate"], pr["channels"]), (rate, ch))
                pcm_b, qoa, st = NATIVE.cook(src, channels="keep", seed=3)
                self.assertEqual(st["frames"], (pr["frames"] * 48000 + rate // 2) // rate)
                self.assertEqual(len(qoa), S.qoa_size(st["frames"], ch))
                left = P.from_bytes(pcm_b)[0::ch]
                self.assertGreater(dominant(left[:48000], freq), 50)
        pcm_b, _, _ = NATIVE.cook(SAMPLE / "audio" / "sfx" / "stereo_tone.flac", seed=3)
        right = P.from_bytes(pcm_b)[1::2]
        self.assertGreater(dominant(right[:48000], 660), 50)

    def test_errors(self):
        with tempfile.TemporaryDirectory() as d:
            p = Path(d) / "x.wav"
            p.write_text("not audio")
            with self.assertRaises(NativeError):
                NATIVE.cook(p)
            q = Path(d) / "three.wav"
            tones.write_wav(q, 48000, [[0.1] * 100] * 3)
            with self.assertRaises(NativeError):          # 3 channels: the game plays 1, 2, 4, 6, 8
                NATIVE.cook(q)
            self.assertEqual(NATIVE.cook(q, channels="mono")[2]["channels"], 1)


def _mod(root, steps, extra=None, sources=True):
    """A synthetic mod: audio/ with 48 kHz WAVs, assets/cook.json."""
    a = root / "assets"
    a.mkdir(parents=True, exist_ok=True)
    (root / "kiln.mod.json").write_text(json.dumps({"id": "testmod"}))
    if sources:
        au = root / "audio"
        tones.write_wav(au / "chime.wav", 48000, [tones.tone(997, 0.5, 48000)])
        tones.write_wav(au / "long.wav", 48000, [tones.tone(440, 1.5, 48000), tones.tone(660, 1.5, 48000)])
        tones.write_wav(au / "t1.wav", 48000, [tones.tone(1200, 0.2, 48000)])
        tones.write_wav(au / "t2.wav", 48000, [tones.tone(1500, 0.2, 48000)])
        tones.write_wav(au / "en.wav", 48000, [tones.beeps(523.25, 2, 0.3, 0.2, 48000)])
        tones.write_wav(au / "de.wav", 48000, [tones.beeps(783.99, 3, 0.3, 0.15, 48000)])
        tones.write_wav(au / "narr.wav", 48000, [tones.beeps(392.0, 3, 0.5, 0.2, 48000)])
    doc = {"format": 1, "tool": "soundcook", "out": "cooked", "guidNamespace": NS, "voiceoverFallback": "en",
           "steps": steps}
    doc.update(extra or {})
    (a / "cook.json").write_text(json.dumps(doc, indent=1))
    return a / "cook.json"


BASIC_STEPS = [
    {"op": "sound", "name": "chime", "src": "../audio/chime.wav", "channels": "mono", "cue": {"scene": "Ui", "volume": 0.8}},
    {"op": "sound", "name": "long", "src": "../audio/long.wav", "channels": "stereo"},
    {"op": "sound", "name": "t1", "src": "../audio/t1.wav"},
    {"op": "sound", "name": "t2", "src": "../audio/t2.wav"},
    {"op": "cue", "name": "blips", "sounds": ["t1", {"sound": "t2", "chance": 0.5}], "positional": {"min": 2, "max": 40},
     "sleep": 0.25, "stealingMode": "Oldest"},
    {"op": "cue", "name": "music", "sounds": ["long"], "scene": "Music", "loop": True},
    {"op": "voiceover", "name": "hello", "volume": 0.7, "languages": {"en": ["../audio/en.wav"], "De_De": ["../audio/de.wav"]},
     "subtitles": {"en": "Hello.", "de": "Hallo."}},
    {"op": "voiceover", "name": "narr", "narration": True, "fallback": None, "languages": {"en": ["../audio/narr.wav"]}},
]


class RecipeTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.root = Path(self.tmp.name) / "mod"
        self.path = _mod(self.root, BASIC_STEPS)

    def tearDown(self):
        self.tmp.cleanup()

    def run_recipe(self, native=NATIVE, **kw):
        return recipe.run(self.path, None, native, log=lambda m: None, **kw)

    def test_out_is_the_mods_cooked_folder(self):
        """A cook writes into the mod's assets/cooked folder only: another "out" is refused with what to change, and nothing is written."""
        for out in ("built", "../cooked", ".", "cooked/../../audio"):
            path = _mod(self.root, BASIC_STEPS, {"out": out}, sources=False)
            before = sorted(str(f) for f in self.root.rglob("*"))
            with self.assertRaises(recipe.RecipeError, msg=out) as e:
                recipe.run(path, None, NATIVE, log=lambda m: None, update_manifest=True)
            self.assertIn("outside the mod's assets/cooked folder", str(e.exception))
            self.assertIn('"out": "cooked"', str(e.exception))
            with self.assertRaises(recipe.RecipeError):
                recipe.needs_cook(path)
            self.assertEqual(sorted(str(f) for f in self.root.rglob("*")), before)
        # a step's name that would lead out of the folder, and another manifest than the mod's: refused, nothing written
        for extra, steps in (({}, [dict(BASIC_STEPS[0], name="../../../escaped")]), ({"manifest": "../elsewhere.json"}, BASIC_STEPS)):
            path = _mod(self.root, steps, extra)
            before = sorted(str(f) for f in self.root.rglob("*"))
            with self.assertRaises(recipe.RecipeError):
                recipe.run(path, None, NATIVE, log=lambda m: None, update_manifest=True)
            self.assertEqual(sorted(str(f) for f in self.root.rglob("*")), before)
        self.assertEqual(recipe.out_dir(_mod(self.root, BASIC_STEPS, {"out": "cooked/snd"}, sources=False), {"out": "cooked/snd"}),
                         (self.root / "assets/cooked/snd").resolve())
        # the report is a file of the cook like its outputs: a link in its place is refused before anything is written, and the file it
        # points to is left alone
        path = _mod(self.root, BASIC_STEPS)
        victim = self.root.parent / "victim.json"
        victim.write_text("theirs")
        report = self.root / "assets" / "cooked" / recipe.report_name(json.loads(path.read_text()))
        report.parent.mkdir(parents=True, exist_ok=True)
        if report.exists() or report.is_symlink():
            report.unlink()
        report.symlink_to(victim)
        before = sorted(str(f) for f in self.root.rglob("*"))
        with self.assertRaises(recipe.RecipeError) as e:
            recipe.run(path, None, NATIVE, log=lambda m: None, update_manifest=True)
        self.assertIn("is a link", str(e.exception))
        self.assertEqual((victim.read_text(), sorted(str(f) for f in self.root.rglob("*"))), ("theirs", before))

    def test_cook_outputs_manifest_index(self):
        self.assertTrue(recipe.needs_cook(self.path))
        r = self.run_recipe(update_manifest=True, cross_check=True)
        self.assertTrue(r["ok"], json.dumps(r["steps"], indent=1)[:3000])
        self.assertEqual(r["types"]["source"], "bundled")
        t = R.load_bundled()
        cooked = self.root / "assets" / "cooked"
        g = lambda op, n: str(uuid.uuid5(uuid.UUID(NS), f"{op}:{n}"))  # noqa: E731
        idx = json.loads((cooked / "audio.json").read_text())
        self.assertEqual(idx["mod"], "testmod")
        self.assertEqual(idx["cues"]["chime"]["guid"], g("cue", "chime"))
        self.assertEqual(idx["sounds"]["chime"]["guid"], g("sound", "chime"))
        self.assertEqual(idx["cues"]["chime"]["playScene"], "Ui")
        self.assertEqual(idx["cues"]["music"]["playScene"], "Music")
        self.assertFalse(idx["sounds"]["chime"]["streamed"])
        self.assertTrue(idx["sounds"]["long"]["streamed"])
        # the voiced line: en + de cooked, fr/es/zh filled from the mod-wide fallback en
        hello = idx["voiceovers"]["hello"]
        self.assertEqual(hello["guid"], g("voiceover", "hello"))
        self.assertEqual(hello["languages"]["en"]["cue"], g("cue", "hello.en"))
        self.assertEqual(hello["languages"]["de"]["cue"], g("cue", "hello.de"))
        for lk in ("fr", "es", "zh"):
            self.assertEqual(hello["languages"][lk], {"cue": g("cue", "hello.en"), "durationMs": 1000, "fallbackOf": "en"})
        self.assertEqual(hello["durations10ms"], [0, 0, 0, 0, 0])
        self.assertEqual(hello["subtitles"]["de"], "Hallo.")
        # narration: fallback null (silence) and durations for the lore queue
        narr = idx["voiceovers"]["narr"]
        self.assertTrue(narr["narration"])
        self.assertEqual([narr["languages"][k] is None for k in R.LANGS], [False, True, True, True, True])
        self.assertEqual(narr["durations10ms"][0], 210)                         # 3 x 0.5 s + 2 x 0.2 s + 0.2 s tail
        self.assertEqual(idx["cues"]["narr.en"]["playScene"], "LoreVoiceover")
        vo = R.decode(t, R.VO, (cooked / "sound" / "narr.vo.bin").read_bytes())
        self.assertEqual(vo["languages"][0], g("cue", "narr.en"))
        self.assertEqual(vo["languages"][1], R.NULL_GUID)
        self.assertEqual(vo["durations"], [210, 0, 0, 0, 0])
        # cues
        cue = R.decode(t, R.CUE, (cooked / "sound" / "blips.cue.bin").read_bytes())
        self.assertTrue(cue["hasPosition"])
        self.assertEqual((cue["minDistance"], cue["maxDistance"]), (2.0, 40.0))
        self.assertEqual(cue["avoidRepeatingLastX"], 1)
        self.assertEqual(cue["sleep"]["value"], 250_000_000)
        self.assertEqual(cue["stealingMode"], "Oldest")
        self.assertEqual([e["value"]["chance"] for e in cue["entries"]], [1.0, 0.5])
        hcue = R.decode(t, R.CUE, (cooked / "sound" / "hello.en.cue.bin").read_bytes())
        self.assertEqual((hcue["scene"], hcue["hasPosition"]), ("Voice", False))
        self.assertAlmostEqual(hcue["volume"], 0.7, places=6)
        # sounds
        s = R.decode(t, R.SOUND, (cooked / "sound" / "long.sound.bin").read_bytes())
        self.assertEqual((s["channelConfiguration"], s["frameCount"], s["framesPerSecond"]), ("Stereo", 72000, 48000))
        blob = (cooked / "sound" / "long.qoa").read_bytes()
        self.assertEqual(s["streamedData"]["hash"], R.content_hash(blob))
        self.assertEqual(s["debugName"], "testmod_long")
        back = S.decode_qoa(blob, 72000, 2)
        self.assertGreater(dominant(back[1][:24000], 660), 50)
        # manifest
        man = json.loads((self.root / "assets" / "assets.json").read_text())
        by_file = {e["file"]: e for e in man["entries"]}
        e = by_file["cooked/sound/long.sound.bin"]
        self.assertEqual(e["patch"], [{"offset": 24, "blob": "cooked/sound/long.qoa"}])
        self.assertEqual(by_file["cooked/sound/long.qoa"], {"kind": "blob", "file": "cooked/sound/long.qoa", "side": "client"})
        self.assertNotIn("side", by_file["cooked/sound/hello.vo.bin"])        # voice-overs go to both sides
        self.assertEqual(by_file["cooked/sound/hello.vo.bin"]["type"], R.VO)
        self.assertTrue(all(o["checks"].get("native_equals_python_encoder", True) for s in r["steps"] for o in s["outputs"]))
        # cook again when a source or an output changes
        self.assertFalse(recipe.needs_cook(self.path))
        (self.root / "audio" / "t1.wav").write_bytes((self.root / "audio" / "t2.wav").read_bytes())
        self.assertTrue(recipe.needs_cook(self.path))
        self.run_recipe(update_manifest=True)
        self.assertFalse(recipe.needs_cook(self.path))
        (cooked / "sound" / "chime.sound.bin").write_bytes(b"x")
        self.assertTrue(recipe.needs_cook(self.path))

    def test_manifest_mismatch_and_stale(self):
        r = self.run_recipe()
        self.assertFalse(r["ok"])                      # no assets.json yet: every entry is missing
        self.assertTrue(r["manifest"]["missing"])
        self.assertTrue(self.run_recipe(update_manifest=True)["ok"])
        doc = json.loads(self.path.read_text())
        doc["steps"] = [s for s in doc["steps"] if s["name"] != "music"]
        self.path.write_text(json.dumps(doc))
        r = self.run_recipe()
        self.assertEqual([e["file"] for e in r["manifest"]["stale"]], ["cooked/sound/music.cue.bin"])
        self.assertTrue(self.run_recipe(update_manifest=True)["ok"])
        files = {e["file"] for e in json.loads((self.root / "assets" / "assets.json").read_text())["entries"]}
        self.assertNotIn("cooked/sound/music.cue.bin", files)

    @unittest.skipUnless(NATIVE, "kiln-soundcook not built")
    def test_python_path_gives_the_same_files(self):
        a = self.run_recipe(update_manifest=True)
        b_root = Path(self.tmp.name) / "mod2"
        shutil.copytree(self.root, b_root, ignore=shutil.ignore_patterns("cooked"))
        b = recipe.run(b_root / "assets" / "cook.json", None, None, update_manifest=True, log=lambda m: None)
        self.assertTrue(a["ok"] and b["ok"])
        sa = {o["file"]: o["sha256"] for o in a["outputs"] if not o["file"].endswith("audio.json")}
        sb = {o["file"]: o["sha256"] for o in b["outputs"] if not o["file"].endswith("audio.json")}
        self.assertEqual(sa, sb)

    def test_errors(self):
        bad = [
            ({"steps": [{"op": "sound", "name": "x", "src": "../audio/chime.wav", "volume": 1}]}, "unknown key"),
            ({"guidNamespace": None}, "guidNamespace"),
            ({"steps": [{"op": "voiceover", "name": "v", "fallback": "fr", "languages": {"en": ["../audio/en.wav"]}}]}, "fallback"),
            ({"steps": [{"op": "voiceover", "name": "v", "languages": {"jp": ["../audio/en.wav"]}}]}, "slots"),
            ({"steps": [{"op": "sound", "name": "x", "src": "../../outside.wav"}]}, "outside"),
            ({"steps": [{"op": "cue", "name": "c", "sounds": ["nope"]}]}, "not a sound"),
            ({"steps": [{"op": "cue", "name": "c", "sounds": ["@missing"]}]}, "not declared"),
            ({"steps": [{"op": "sound", "name": "a b", "src": "../audio/chime.wav"}]}, "name"),
            ({"steps": [{"op": "explode", "name": "x"}]}, "op"),
            ({"steps": [{"tool": "texcook", "op": "x"}]}, "tool"),
            ({"steps": [{"op": "cue", "name": "c", "sounds": ["chime"], "positional": [1, 2]},
                        {"op": "sound", "name": "chime", "src": "../audio/chime.wav"}]}, "positional"),
        ]
        for extra, frag in bad:
            with self.subTest(frag):
                p = _mod(self.root, extra.pop("steps", BASIC_STEPS), extra, sources=False)
                with self.assertRaises(recipe.RecipeError) as cm:
                    recipe.run(p, None, NATIVE, log=lambda m: None)
                self.assertIn(frag, str(cm.exception))

    def test_mixed_recipe_runs_only_sound_steps(self):
        steps = [{"tool": "modelcook", "op": "model", "gltf": "src/x.gltf", "base": "bronze"}] + \
                [dict(s, tool="soundcook") for s in BASIC_STEPS[:2]]
        p = _mod(self.root, steps, {"tool": "modelcook", "inputs": {"bronze": {"guid": NS, "type": "keen::RenderModel"}}},
                 sources=False)
        doc = recipe.load(p)
        self.assertEqual([i for i, _ in recipe.own_steps(doc)], [1, 2])
        self.assertEqual(recipe.other_tools(doc), ["modelcook"])
        self.assertEqual(recipe.report_name(doc), "cook-report.soundcook.json")
        r = recipe.run(p, None, NATIVE, update_manifest=True, log=lambda m: None)
        self.assertTrue(r["ok"])
        self.assertEqual(r["inputs"], {})                          # the model input is modelcook's business
        self.assertTrue((self.root / "assets" / "cooked" / "cook-report.soundcook.json").exists())
        self.assertFalse((self.root / "assets" / "cooked" / "cook-report.json").exists())
        self.assertEqual([s["step"] for s in r["steps"]], [1, 2])


def _synthetic_game(d, server=False):
    """A KFC with one vanilla Ui cue (+ its sound) and one vanilla voice-over, layouts from the bundled reflection."""
    from test_kfc import build_kfc
    t = R.load_bundled()
    snd_guid, cue_guid, vo_guid = ("aaaaaaaa-0000-4000-8000-000000000001", "aaaaaaaa-0000-4000-8000-000000000002",
                                   "aaaaaaaa-0000-4000-8000-000000000003")
    q = S.encode_qoa([sine(2000, 800)])
    sd, _ = R.sound_desc(2000, 1, q, "vanilla_click")
    cue = R.cue_defaults(1, "Ui")
    cue.update(volume=0.375, pitchRandomness=0.055, maxCount=5, entries=[R.entry(snd_guid)])
    vo = R.vo_desc([cue_guid, cue_guid, R.NULL_GUID, R.NULL_GUID, cue_guid], [0, 0, 0, 0, 0])
    res = [(vo_guid, t.get(R.VO)["hash"], 0, R.encode(t, R.VO, vo))]
    if not server:
        res = [(snd_guid, t.get(R.SOUND)["hash"], 0, R.encode(t, R.SOUND, sd)),
               (cue_guid, t.get(R.CUE)["hash"], 0, R.encode(t, R.CUE, cue))] + res
    build_kfc(d, res, [b"unused"], chunk_size=4096)
    k = Kfc(d)
    kb = bytearray((d / "test.kfc").read_bytes())
    vo_ = struct.unpack_from("<I", kb, 0x80)[0] + 0x80
    for i, (h, _) in enumerate(sorted(k.bundles.items())):
        struct.pack_into("<I", kb, vo_ + 12 * i, t.get(h)["layout"])
    k.close()
    name = "test_server" if server else "test"
    if server:
        for f in list(d.glob("test*")):
            f.rename(d / f.name.replace("test", "test_server", 1))
    (d / f"{name}.kfc").write_bytes(bytes(kb))
    return snd_guid, cue_guid, vo_guid


class OverrideTests(unittest.TestCase):
    """Overrides against a synthetic game (nothing from the real one)."""

    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        base = Path(self.tmp.name)
        self.game_dir = base / "game"
        self.game_dir.mkdir()
        self.snd, self.cue, self.vo = _synthetic_game(self.game_dir)
        self.root = base / "mod"
        self.steps = [
            {"op": "sound", "name": "chime", "src": "../audio/chime.wav", "channels": "mono"},
            {"op": "cue", "name": "click", "base": "click", "guid": "@click", "mode": "override", "sounds": ["chime"],
             "pitchRandomness": 0},
            {"op": "cue", "name": "click_plus", "base": "click", "addSounds": ["chime"], "volume": 0.9},
            {"op": "cue", "name": "vanilla_mix", "sounds": ["@click_sound", "chime"]},
            {"op": "voiceover", "name": "line", "base": "line", "guid": "@line", "fallback": None,
             "languages": {"de": ["../audio/de.wav"]}},
        ]
        self.inputs = {"click": {"guid": self.cue, "type": R.CUE}, "click_sound": {"guid": self.snd, "type": R.SOUND},
                       "line": {"guid": self.vo, "type": R.VO}}
        self.path = _mod(self.root, self.steps, {"inputs": self.inputs})

    def tearDown(self):
        self.tmp.cleanup()

    def test_client(self):
        g = recipe.Game(self.game_dir)
        self.addCleanup(g.close)
        self.assertEqual(g.side, "client")
        r = recipe.run(self.path, g, NATIVE, update_manifest=True, log=lambda m: None)
        self.assertTrue(r["ok"], json.dumps(r["steps"], indent=1)[:3000])
        t = R.load_bundled()
        sub = self.root / "assets" / "cooked" / "sound"
        chime = str(uuid.uuid5(uuid.UUID(NS), "sound:chime"))
        c = R.decode(t, R.CUE, (sub / "click.cue.bin").read_bytes())
        self.assertEqual((c["scene"], c["volume"], c["maxCount"], c["pitchRandomness"]), ("Ui", 0.375, 5, 0.0))
        self.assertEqual([e["value"]["sound"] for e in c["entries"]], [chime])
        c2 = R.decode(t, R.CUE, (sub / "click_plus.cue.bin").read_bytes())
        self.assertEqual([e["value"]["sound"] for e in c2["entries"]], [self.snd, chime])
        self.assertAlmostEqual(c2["volume"], 0.9, places=6)
        modes = {o["name"]: (o["mode"], o["checks"]) for s in r["steps"] for o in s["outputs"]}
        self.assertEqual(modes["click"][0], "override")
        self.assertTrue(modes["click"][1]["mode_override"])
        self.assertEqual(modes["click_plus"][0], "new")
        self.assertTrue(modes["vanilla_mix"][1]["takes_exist"])
        # a vanilla voice-over with its German slot replaced; en and zh keep the vanilla cue, fr/es stay empty
        v = R.decode(t, R.VO, (sub / "line.vo.bin").read_bytes())
        self.assertEqual(v["languages"], [self.cue, str(uuid.uuid5(uuid.UUID(NS), "cue:line.de")), R.NULL_GUID,
                                          R.NULL_GUID, self.cue])
        self.assertEqual(modes["line"][0], "override")

    def test_dedicated_server_skips_client_only_bases(self):
        sdir = Path(self.tmp.name) / "server"
        sdir.mkdir()
        _synthetic_game(sdir, server=True)
        g = recipe.Game(sdir)
        self.addCleanup(g.close)
        self.assertEqual(g.side, "server")
        r = recipe.run(self.path, g, NATIVE, update_manifest=True, log=lambda m: None)
        self.assertTrue(r["ok"], json.dumps(r["steps"], indent=1)[:3000])
        out = {o["name"]: o for s in r["steps"] for o in s["outputs"]}
        self.assertIn("client-only", out["click"]["skipped"])
        self.assertIn("client-only", r["inputs"]["click"]["why"])
        self.assertTrue(out["line"]["ok"])                     # voice-overs exist on servers too

    def test_missing_input_on_client_is_an_error(self):
        doc = json.loads(self.path.read_text())
        doc["inputs"]["click"]["guid"] = "bbbbbbbb-0000-4000-8000-000000000009"
        self.path.write_text(json.dumps(doc))
        g = recipe.Game(self.game_dir)
        self.addCleanup(g.close)
        with self.assertRaises(recipe.RecipeError):
            recipe.run(self.path, g, NATIVE, log=lambda m: None)


@unittest.skipUnless(HAVE_CLIENT, "Enshrouded client not installed")
class GameTests(unittest.TestCase):
    """Against the installed client, read only. Nothing from the game is written outside the temp dir."""

    @classmethod
    def setUpClass(cls):
        cls.game = recipe.Game(GAME)
        cls.t = cls.game.types

    @classmethod
    def tearDownClass(cls):
        cls.game.close()

    def test_bundled_reflection_matches_the_game(self):
        bundled = R.load_bundled()
        self.assertEqual(R.layouts(bundled), R.layouts(self.t),
                         "the client's sound types changed: refresh types/sound-types.json (kfc.py types --closure)")

    def test_vanilla_resources_reencode_with_the_bundled_types(self):
        bt = R.load_bundled()
        k = self.game.kfc
        for tn in (R.SOUND, R.CUE, R.VO):
            keys = k.resources_of_type(self.t.get(tn)["hash"])
            self.assertGreater(len(keys), 1000, tn)
            for key in keys[::25]:
                data = k.read(key)
                self.assertTrue(R.roundtrip(bt, tn, data), f"{tn} {key.guid}")

    @unittest.skipUnless(NATIVE, "kiln-soundcook not built")
    def test_native_encoder_reproduces_game_slices(self):
        k = self.game.kfc
        keys = k.resources_of_type(self.t.get(R.SOUND)["hash"])
        picked, seen = [], set()
        for key in keys:
            s = R.decode(self.t, R.SOUND, k.read(key))
            if s["format"] != "FramelessQOA" or not 20000 < s["frameCount"] < 120000:
                continue
            kind = (s["channelConfiguration"], isinstance(s["data"], dict) and bool(s["data"].get("bytes")))
            if kind not in seen:
                seen.add(kind)
                picked.append(s)
            if len(picked) == 4:
                break
        self.assertGreaterEqual(len(picked), 3)
        # Re-encoding decoded audio can tie: two scale factors reconstruct a slice perfectly (rank 0) and the game's
        # encoder, which saw the original audio, took the larger one; the reference keeps the first. Over a 613-sound
        # sample 418 match exactly and every mismatch looked at was such a tie, so most picks must match.
        exact = 0
        for s in picked:
            ch, n = S.CHANNELS[s["channelConfiguration"]], s["frameCount"]
            data = S.sound_bytes(k, s)
            pcm = S.decode_qoa(data, n, ch)
            ours = NATIVE.encode(P.to_bytes(inter(pcm)), ch)
            body = (math.ceil(n / 20) - 1) * 8 * ch               # all but the padded last slice of each channel
            if ours[:body] == data[:body]:
                exact += 1
                # the last slices decode to the same samples (only their unused padding residuals may differ)
                self.assertEqual(S.decode_qoa(ours, n, ch), pcm, s["debugName"])
        self.assertGreaterEqual(exact, len(picked) - 1, [s["debugName"] for s in picked])

    def test_sample_recipe_cooks_with_its_override(self):
        with tempfile.TemporaryDirectory() as d:
            mod = Path(d) / "SoundSample"
            shutil.copytree(SAMPLE, mod, ignore=shutil.ignore_patterns("cooked", "bin", "obj"))
            r = recipe.run(mod / "assets" / "cook.json", self.game, NATIVE, log=lambda m: None)
            self.assertTrue(all(s["ok"] for s in r["steps"]), json.dumps(r["steps"], indent=1)[:3000])
            self.assertFalse(r["manifest"]["missing"] or r["manifest"]["mismatched"] or r["manifest"]["stale"],
                             "mods/samples/SoundSample/assets/assets.json is out of date: run build-assets.sh")
            out = {o["name"]: o for s in r["steps"] for o in s["outputs"]}
            self.assertEqual(out["dialog_confirm"]["mode"], "override")
            self.assertTrue(out["dialog_confirm"]["checks"]["mode_override"])
            c = R.decode(self.t, R.CUE, (mod / "assets" / "cooked" / "sound" / "dialog_confirm.cue.bin").read_bytes())
            v = R.decode(self.t, R.CUE, self.game.read(self.t, "b096387a-cc73-475a-bacd-69ce8cad9fc9", R.CUE))
            for f in ("scene", "volume", "maxCount", "stealingMode", "priority"):
                self.assertEqual(c[f], v[f], f)
            self.assertEqual(c["pitchRandomness"], 0.0)


if __name__ == "__main__":
    unittest.main()
