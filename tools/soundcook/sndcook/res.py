# SPDX-License-Identifier: MIT
"""Keen's sound resources as soundcook writes them (docs/re/audio.md section 2): keen::SoundResource (samples),
keen::SoundContainerResource (the cue), keen::VoiceoverResource (one cue per voice language).

Bytes are written with tools/kfc's reflected encoder, from the installed game's reflection when there is a game,
else from types/sound-types.json (a closure of the client's reflection, build 26430625: type layouts only, no game
data). Fields are written by name, so a game update that moves a field is followed when the game is installed; a
layout hash that differs from the bundled one is reported.
"""
import hashlib
import json
from pathlib import Path

from kfc.blob import Decoder
from kfc.blobwrite import Encoder
from kfc.refl import TypeDb

HERE = Path(__file__).resolve().parent
TYPES_FILE = HERE.parent / "types" / "sound-types.json"

SOUND, CUE, VO, ENTRY = ("keen::SoundResource", "keen::SoundContainerResource", "keen::VoiceoverResource",
                         "keen::SoundContainerResourceSoundEntry")
TYPE_NAMES = (SOUND, CUE, VO, ENTRY)
CLIENT_ONLY = (SOUND, CUE)                 # the dedicated server has neither (docs/re/audio.md section 5)
RATE = 48000
INLINE_MAX_FRAMES = 48000                  # game data: inline <= 48,000 frames (1 s), streamed above
STREAM_CATEGORY = 3016399412               # streamedData.category of every streamed game sound
CHANNEL_CONFIG = {1: "Mono", 2: "Stereo", 4: "Quadrophonic", 6: "FiveDotOne", 8: "SevenDotOne"}
LANGS = ("en", "de", "fr", "es", "zh")     # VoiceoverResource.languages slots (voice-over language setting)
LANG_ALIASES = {"en": "en", "en_us": "en", "english": "en", "de": "de", "de_de": "de", "german": "de",
                "fr": "fr", "fr_fr": "fr", "french": "fr", "es": "es", "es_es": "es", "spanish": "es",
                "zh": "zh", "zh_cn": "zh", "chinese": "zh", "simplifiedchinese": "zh"}
NULL_GUID = "00000000-0000-0000-0000-000000000000"
SCENES = ("Sfx", "Music", "Ui", "Voice")   # keen::SoundContainerScene
STEALING = ("Oldest", "Furthest", "Ignore")
PLAY_SCENES = ("Ui", "Game", "Music", "Ambience", "LoreVoiceover")   # keen::SoundSceneType used by Kiln.Audio


def lang_key(k):
    v = LANG_ALIASES.get(str(k).lower())
    if v is None:
        raise ValueError(f"voice-over language {k!r}: the game has slots for {', '.join(LANGS)}")
    return v


def load_bundled():
    return TypeDb.load(TYPES_FILE)


def layouts(t):
    return {n: (f"0x{t.get(n)['layout']:08x}" if t.get(n) else None) for n in TYPE_NAMES}


def choose_types(game_types=None):
    """(TypeDb, info): the installed game's reflection when it has every sound type, else the bundled one."""
    bundled = load_bundled()
    info = {"bundled_exe_sha256": bundled.dump.get("exe_sha256"), "bundled_layouts": layouts(bundled)}
    if game_types is not None and all(game_types.get(n) for n in TYPE_NAMES):
        gl = layouts(game_types)
        info.update(source="game", exe_sha256=game_types.dump.get("exe_sha256"), layouts=gl,
                    changed=[n for n in TYPE_NAMES if gl[n] != info["bundled_layouts"][n]])
        return game_types, info
    info.update(source="bundled", exe_sha256=bundled.dump.get("exe_sha256"), layouts=info["bundled_layouts"], changed=[])
    return bundled, info


def field_offset(t, struct_name, path):
    """Byte offset of a dotted field path, e.g. ('keen::SoundResource', 'streamedData.hash')."""
    off, cur = 0, t.get(struct_name)
    for part in path.split("."):
        f = next((f for f in t.all_fields(cur) if f["name"] == part), None)
        if f is None:
            raise KeyError(f"{struct_name}: no field {path}")
        off += f["offset"]
        cur = t.resolve(t.get(f["type"]))
    return off


def content_hash(blob):
    """kiln.assets' ContentHash for a mod blob: {size, sha256[0:4], [4:8], [8:12]} as little-endian u32."""
    d = hashlib.sha256(blob).digest()
    return {"size": len(blob), "hash0": int.from_bytes(d[0:4], "little"), "hash1": int.from_bytes(d[4:8], "little"),
            "hash2": int.from_bytes(d[8:12], "little")}


def duration_ns(frames):
    return frames * 1_000_000_000 // RATE


def sound_desc(frames, channels, qoa, debug_name):
    """A SoundResource value and its blob (None when inline)."""
    inline = frames <= INLINE_MAX_FRAMES
    blob = None if inline else qoa
    return {
        "channelConfiguration": CHANNEL_CONFIG[channels], "format": "FramelessQOA", "framesPerSecond": RATE,
        "frameCount": frames, "duration": {"value": duration_ns(frames)},
        "data": {"bytes": qoa.hex()} if inline else [],
        "streamedData": {"hash": content_hash(qoa) if not inline else {"size": 0, "hash0": 0, "hash1": 0, "hash2": 0},
                         "category": {"value": STREAM_CATEGORY if not inline else 0}, "bundleId": 0, "sortKey": 0},
        "debugName": debug_name,
    }, blob


def cue_defaults(takes, scene="Sfx"):
    """A SoundContainerResource with the game's most common values (docs/re/audio.md 2.1 survey)."""
    return {
        "mode": "Random", "scene": scene, "loop": False, "loopSameChosenEntry": False,
        "avoidRepeatingLastX": max(0, min(2, takes - 1)), "volume": 1.0, "volumeRandomness": 0.0, "pitch": 1.0,
        "pitchRandomness": 0.0, "sleep": {"value": 0}, "sleepRandomness": {"value": 0}, "hasPosition": False,
        "minDistance": 1.0, "maxDistance": 50.0, "hasRandomPosition": False, "randomLeftRight": 0.0,
        "randomFrontBack": 0.0, "muteOnMain": False, "gamepadSpeaker": False, "gamepadSpeakerVolume": 1.0,
        "gamepadVibration": False, "gamepadVibrationIntensity": 1.0, "useMaxCountLimiting": True, "maxCount": 3,
        "stealingMode": "Furthest", "priority": 0 if scene == "Ui" else 50, "entries": [], "extraRumble": None,
    }


def entry(sound_guid, chance=1.0):
    return {"$type": ENTRY, "value": {"chance": float(chance), "sound": sound_guid}}


def vo_desc(cues, durations):
    return {"languages": list(cues), "durations": list(durations)}


def encode(t, type_name, value):
    return Encoder(t).encode(type_name, value)


def decode(t, type_name, data):
    return Decoder(t).decode(type_name, data)


def roundtrip(t, type_name, data):
    """Decode then re-encode: True when the bytes come back identical (what every cooked file must pass)."""
    return encode(t, type_name, decode(t, type_name, data)) == data


def dumps(v):
    return json.dumps(v, sort_keys=True)
