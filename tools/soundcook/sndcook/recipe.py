# SPDX-License-Identifier: MIT
"""cook.json sound steps (docs/kiln/modding/sounds.md): a mod's audio sources -> keen::SoundResource /
SoundContainerResource / VoiceoverResource files + blobs that kiln.assets serves, the assets.json entries naming them,
cooked/audio.json (the index Kiln.Audio reads) and the cook report.

    {
      "format": 1, "tool": "soundcook", "out": "cooked",
      "guidNamespace": "<uuid>",            // names -> UUIDv5(namespace, "<op>:<name>") GUIDs, fixed forever
      "voiceoverFallback": "en",            // mod-wide default for empty voice slots; null/absent = silence (as the game)
      "inputs": { "confirm": { "guid": "b096387a-…", "type": "keen::SoundContainerResource" } },
      "steps": [
        { "op": "sound", "name": "chime", "src": "../audio/chime.wav", "channels": "mono", "cue": { "scene": "Ui" } },
        { "op": "cue", "name": "boom", "sounds": ["boom_1", "boom_2"], "positional": { "min": 3, "max": 60 } },
        { "op": "cue", "name": "confirm", "base": "confirm", "guid": "@confirm", "mode": "override", "sounds": ["chime"] },
        { "op": "voiceover", "name": "hello", "languages": { "en": ["../audio/vo/en/hello.wav"] }, "fallback": "en" }
      ]
    }

A step whose "tool" (or the file's default "tool") is another cooker is left to that cooker (tools/modelcook).
"""
import concurrent.futures
import hashlib
import json
import os
import re
import uuid
from pathlib import Path

from kfc import sound as S
from kfc.archive import Kfc, ResourceKey
from kfc.refl import TypeDb
from kfc.util import guid_bytes, guid_text

from . import pcm as P
from . import res as R
from . import validate as V

TOOL = "soundcook"
DEFAULT_TOOL = "modelcook"      # cook.json's historical default (format 1 began with models)
KNOWN_TOOLS = ("modelcook", "soundcook", "animcook")
OPS = ("sound", "cue", "voiceover")
SUBDIR = "sound"

COMMON = {"op", "name", "tool", "comment", "guid", "mode"}
SOURCE_KEYS = {"src", "channels", "gain", "normalize", "start", "end"}
CUE_FIELDS = {
    "scene": ("enum", R.SCENES), "loop": "bool", "loopSameChosenEntry": "bool", "avoidRepeatingLastX": "uint",
    "volume": "float", "volumeRandomness": "float", "pitch": "float", "pitchRandomness": "float",
    "sleep": "seconds", "sleepRandomness": "seconds", "hasRandomPosition": "bool", "randomLeftRight": "float",
    "randomFrontBack": "float", "muteOnMain": "bool", "gamepadSpeaker": "bool", "gamepadSpeakerVolume": "float",
    "gamepadVibration": "bool", "gamepadVibrationIntensity": "float", "useMaxCountLimiting": "bool",
    "maxCount": "uint", "stealingMode": ("enum", R.STEALING), "priority": "u16",
}
CUE_KEYS = set(CUE_FIELDS) | {"positional", "playScene"}
KEYS = {
    "sound": COMMON | SOURCE_KEYS | {"debugName", "cue"},
    "cue": COMMON | {"sounds", "addSounds", "base"} | CUE_KEYS,
    "voiceover": COMMON | {"languages", "fallback", "narration", "subtitles", "cue", "languageCue", "channels", "gain",
                           "normalize", "base"} | CUE_KEYS,
}
NAME_RX = re.compile(r"^[A-Za-z0-9_][A-Za-z0-9_.-]{0,95}$")
GUID_RX = re.compile(r"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$")


class RecipeError(Exception):
    pass


def sha256(b):
    return hashlib.sha256(b).hexdigest()


def norm_guid(g):
    return guid_text(guid_bytes(g))


# ------------------------------------------------------------------------------------------------ the recipe file
def tool_of(doc, st):
    return st.get("tool", doc.get("tool", DEFAULT_TOOL))


def own_steps(doc):
    return [(i, st) for i, st in enumerate(doc.get("steps", [])) if tool_of(doc, st) == TOOL]


def other_tools(doc):
    return sorted({tool_of(doc, st) for st in doc.get("steps", [])} - {TOOL})


def report_name(doc):
    """The cook report of this tool: cook-report.json when soundcook is the recipe's default tool, else
    cook-report.soundcook.json (so a mixed recipe keeps modelcook's report where it always was)."""
    return "cook-report.json" if doc.get("tool", DEFAULT_TOOL) == TOOL else f"cook-report.{TOOL}.json"


def load(path):
    p = Path(path)
    try:
        doc = json.loads(p.read_text())
    except (OSError, json.JSONDecodeError) as e:
        raise RecipeError(f"{p}: {e}")
    if doc.get("format") != 1:
        raise RecipeError(f"{p}: format {doc.get('format')!r}, expected 1")
    if doc.get("tool", DEFAULT_TOOL) not in KNOWN_TOOLS:
        raise RecipeError(f"{p}: tool {doc.get('tool')!r} (known: {', '.join(KNOWN_TOOLS)})")
    names = {op: set() for op in OPS}
    for i, st in enumerate(doc.get("steps", [])):
        tl = tool_of(doc, st)
        if tl not in KNOWN_TOOLS:
            raise RecipeError(f"step {i}: tool {tl!r} (known: {', '.join(KNOWN_TOOLS)})")
        if tl != TOOL:
            continue
        op = st.get("op")
        if op not in OPS:
            raise RecipeError(f"step {i}: op {op!r} (soundcook knows: {', '.join(OPS)})")
        extra = set(st) - KEYS[op]
        if extra:
            raise RecipeError(f"step {i} ({op}): unknown key(s) {', '.join(sorted(extra))}")
        name = st.get("name")
        if not isinstance(name, str) or not NAME_RX.match(name):
            raise RecipeError(f"step {i} ({op}): name {name!r}: letters, digits, _ . - (up to 96)")
        if name in names[op]:
            raise RecipeError(f"step {i} ({op}): name {name!r} used twice")
        names[op].add(name)
    if own_steps(doc) and not doc.get("guidNamespace"):
        raise RecipeError(f"{p}: guidNamespace missing (pick one: python3 -c \"import uuid; print(uuid.uuid4())\", "
                          "then never change it)")
    if doc.get("guidNamespace"):
        try:
            uuid.UUID(doc["guidNamespace"])
        except ValueError:
            raise RecipeError(f"{p}: guidNamespace {doc['guidNamespace']!r} is not a UUID")
    return doc


def sources_of(doc, base_dir):
    """Every source file the sound steps read (relative to cook.json), in step order."""
    out = []
    for _, st in own_steps(doc):
        if st["op"] == "sound" and st.get("src"):
            out.append(st["src"])
        elif st["op"] == "voiceover":
            for takes in (st.get("languages") or {}).values():
                for tk in takes if isinstance(takes, list) else []:
                    out.append(tk if isinstance(tk, str) else tk.get("src"))
    return [s for s in out if s]


def needs_cook(path):
    """True when outputs are missing or the report does not match this recipe or its sources (no game access)."""
    p = Path(path)
    doc = load(p)
    if not own_steps(doc):
        return False
    out = p.parent / doc.get("out", "cooked")
    rep = out / report_name(doc)
    if not rep.exists():
        return True
    r = json.loads(rep.read_text())
    if r.get("recipe_sha256") != sha256(p.read_bytes()) or not r.get("ok"):
        return True
    for s in r.get("sources", []):
        f = p.parent / s["file"]
        if not f.exists() or sha256(f.read_bytes()) != s["sha256"]:
            return True
    return any(not (p.parent / o["file"]).exists() or sha256((p.parent / o["file"]).read_bytes()) != o["sha256"]
               for o in r.get("outputs", []))


# ------------------------------------------------------------------------------------------------ the game (optional)
class Game:
    """The installed game, read only: inputs for overrides, existence checks, its reflection."""

    def __init__(self, game_dir, exe=None):
        self.dir = Path(game_dir).expanduser()
        self.kfc = Kfc(self.dir)
        if exe is None:
            exe = next((self.dir / n for n in ("enshrouded.exe", "enshrouded_server.exe") if (self.dir / n).exists()), None)
        self.types = TypeDb.from_exe(exe) if exe else None
        self.exe_sha256 = self.types.dump.get("exe_sha256") if self.types else None
        self.side = "server" if "server" in self.kfc.path.name else "client"
        self.version = self.kfc.version

    @classmethod
    def open(cls, game_dir, exe=None):
        try:
            return cls(game_dir, exe)
        except (FileNotFoundError, ValueError, OSError):
            return None

    def key(self, t, guid, tname, part=0):
        return ResourceKey(guid, t.get(tname)["hash"], part)

    def has(self, t, guid, tname):
        return self.kfc.has(self.key(t, guid, tname))

    def read(self, t, guid, tname):
        return self.kfc.read(self.key(t, guid, tname))

    def summary(self):
        return {"kfc": str(self.kfc.path), "version": self.version, "exe_sha256": self.exe_sha256, "side": self.side}

    def close(self):
        self.kfc.close()


# ------------------------------------------------------------------------------------------------ helpers
def _num(v, what, lo=None, hi=None):
    if isinstance(v, bool) or not isinstance(v, (int, float)):
        raise RecipeError(f"{what}: expected a number, got {v!r}")
    if (lo is not None and v < lo) or (hi is not None and v > hi):
        raise RecipeError(f"{what}: {v} out of range")
    return v


def _apply_cue_fields(desc, fields, what):
    for k, v in fields.items():
        if k in ("positional", "playScene"):
            continue
        kind = CUE_FIELDS.get(k)
        if kind is None:
            raise RecipeError(f"{what}: unknown cue field {k!r}")
        w = f"{what}: {k}"
        if isinstance(kind, tuple):
            if v not in kind[1]:
                raise RecipeError(f"{w}: {v!r} (one of {', '.join(kind[1])})")
            desc[k] = v
        elif kind == "bool":
            if not isinstance(v, bool):
                raise RecipeError(f"{w}: expected true/false")
            desc[k] = v
        elif kind == "float":
            desc[k] = float(_num(v, w))
        elif kind == "uint":
            desc[k] = int(_num(v, w, 0, 2 ** 32 - 1))
        elif kind == "u16":
            desc[k] = int(_num(v, w, 0, 65535))
        elif kind == "seconds":
            desc[k] = {"value": int(round(_num(v, w, 0) * 1e9))}
    if "positional" in fields:
        pos = fields["positional"]
        if pos in (None, False):
            desc["hasPosition"] = False
        elif isinstance(pos, dict) and set(pos) <= {"min", "max"}:
            desc["hasPosition"] = True
            if "min" in pos:
                desc["minDistance"] = float(_num(pos["min"], f"{what}: positional.min", 0))
            if "max" in pos:
                desc["maxDistance"] = float(_num(pos["max"], f"{what}: positional.max", 0))
        else:
            raise RecipeError(f"{what}: positional: {{\"min\": m, \"max\": m}} or false")
    if "playScene" in fields and fields["playScene"] not in R.PLAY_SCENES:
        raise RecipeError(f"{what}: playScene {fields['playScene']!r} (one of {', '.join(R.PLAY_SCENES)})")


def _default_play_scene(scene):
    return {"Ui": "Ui", "Music": "Music"}.get(scene, "Game")


class _Ctx:
    def __init__(self, path, doc, game, t):
        self.p = path
        self.doc = doc
        self.base_dir = path.parent
        self.root = self.base_dir.parent          # the mod folder (cook.json lives in <mod>/assets/)
        self.out = self.base_dir / doc.get("out", "cooked")
        self.manifest = self.base_dir / doc.get("manifest", "assets.json")
        self.ns = uuid.UUID(doc["guidNamespace"]) if doc.get("guidNamespace") else None
        self.game = game
        self.t = t
        self.inputs = {}

    def guid(self, op, name):
        return str(uuid.uuid5(self.ns, f"{op}:{name}"))

    def resolve(self, ref, want_type=None, what=""):
        """'@input' -> its GUID (type-checked), a literal GUID -> itself, else None."""
        if isinstance(ref, str) and ref.startswith("@"):
            inp = self.inputs.get(ref[1:])
            if inp is None:
                raise RecipeError(f"{what}: {ref}: no such input")
            if want_type and inp["type"] != want_type:
                raise RecipeError(f"{what}: {ref} is a {inp['type']}, needs {want_type}")
            return inp["guid"]
        if isinstance(ref, str) and GUID_RX.match(ref):
            return norm_guid(ref)
        return None

    def src(self, rel, what):
        if not isinstance(rel, str) or not rel:
            raise RecipeError(f"{what}: src missing")
        if os.path.isabs(rel):
            raise RecipeError(f"{what}: src {rel}: use a path relative to cook.json")
        f = (self.base_dir / rel).resolve()
        if self.root.resolve() not in f.parents:
            raise RecipeError(f"{what}: src {rel} is outside the mod folder")
        if not f.exists():
            raise RecipeError(f"{what}: src {rel}: no such file")
        return f

    def rel_manifest(self, f):
        r = Path(os.path.relpath(f, self.manifest.parent)).as_posix()
        if r.startswith("../"):
            raise RecipeError(f"{f} is outside {self.manifest.parent} (kiln.assets reads files under assets/ only)")
        return r

    def rel_recipe(self, f):
        return Path(os.path.relpath(f, self.base_dir)).as_posix()


def _load_inputs(c):
    """Only the inputs this tool's steps use (another tool's inputs are its business)."""
    used = set()

    def scan(v):
        if isinstance(v, str) and v.startswith("@"):
            used.add(v[1:])
        elif isinstance(v, list):
            for x in v:
                scan(x)
        elif isinstance(v, dict):
            for x in v.values():
                scan(x)
    for _, st in own_steps(c.doc):
        scan(st)
        if st.get("base"):
            used.add(st["base"])
    report = {}
    for iid in sorted(used):
        spec = c.doc.get("inputs", {}).get(iid)
        if spec is None:
            raise RecipeError(f"input {iid!r} is used but not declared in \"inputs\"")
        tname = spec.get("type")
        if tname not in (R.SOUND, R.CUE, R.VO):
            raise RecipeError(f"input {iid}: type {tname!r} (sound steps take {R.SOUND}, {R.CUE}, {R.VO})")
        inp = {"guid": norm_guid(spec["guid"]), "type": tname, "part": spec.get("part", 0), "available": False}
        g = c.game
        if g is None:
            inp["why"] = "no installed game"
        elif g.has(c.t, inp["guid"], tname):
            data = g.read(c.t, inp["guid"], tname)
            bundle = g.kfc.bundles.get(c.t.get(tname)["hash"], {})
            if bundle.get("layout") != c.t.get(tname)["layout"]:
                raise RecipeError(f"input {iid}: {tname} layout {c.t.get(tname)['layout']:#x} != the data's "
                                  f"{bundle.get('layout', 0):#x}")
            inp.update(available=True, sha256=sha256(data), layout=f"0x{c.t.get(tname)['layout']:08x}")
        elif g.side == "server" and tname in R.CLIENT_ONLY:
            inp["why"] = "client-only type: a dedicated server has no sounds"
        else:
            raise RecipeError(f"input {iid}: {inp['guid']} {tname} is not in {g.kfc.path} (game update or wrong side?)")
        c.inputs[iid] = inp
        report[iid] = dict(inp)
    return report


# ------------------------------------------------------------------------------------------------ the cook
def _source_opts(spec, defaults, what):
    o = {"channels": spec.get("channels", defaults.get("channels", "keep")),
         "gain": spec.get("gain", defaults.get("gain")), "normalize": spec.get("normalize", defaults.get("normalize")),
         "start": spec.get("start", 0) or 0, "end": spec.get("end", 0) or 0}
    if o["channels"] not in ("mono", "stereo", "keep"):
        raise RecipeError(f"{what}: channels {o['channels']!r} (mono, stereo or keep)")
    if o["gain"] is not None and o["normalize"] is not None:
        raise RecipeError(f"{what}: gain and normalize exclude each other")
    for k in ("gain", "normalize"):
        if o[k] is not None:
            _num(o[k], f"{what}: {k}", -96, 48)
    _num(o["start"], f"{what}: start", 0)
    _num(o["end"], f"{what}: end", 0)
    return o


def _cook_pcm(job, native, cross_check):
    o = job["opts"]
    seed = P.fnv1a64("sound:" + job["name"])
    if native is not None:
        pcm_b, qoa, stats = native.cook(job["src"], o["channels"], o["gain"], o["normalize"], o["start"], o["end"], seed)
        pcm = P.from_bytes(pcm_b)
        encoder = "kiln-soundcook"
    else:
        pcm, stats = P.cook(job["src"], o["channels"], o["gain"], o["normalize"], o["start"], o["end"], seed)
        qoa = S.encode_qoa(P.split(pcm, stats["channels"]))
        encoder = "tools/kfc (Python)"
    cross = None
    if cross_check and native is not None:
        cross = S.encode_qoa(P.split(pcm, stats["channels"])) == qoa
    return pcm, qoa, stats, encoder, cross


def run(path, game=None, native=None, update_manifest=False, cross_check=False, log=print):
    p = Path(path).resolve()
    doc = load(p)
    t, tinfo = R.choose_types(game.types if game else None)
    c = _Ctx(p, doc, game, t)
    in_report = _load_inputs(c)
    mod_id = None
    mj = c.root / "kiln.mod.json"
    if mj.exists():
        try:
            mod_id = json.loads(mj.read_text()).get("id")
        except json.JSONDecodeError:
            pass

    # 1. collect: sounds to cook, cues, voice-overs (in step order)
    sounds, cues, vos, steps = {}, {}, {}, []
    mod_default_fb = doc.get("voiceoverFallback")

    def add_sound(step_i, name, src_rel, opts, guid=None, mode=None, debug=None):
        if name in sounds:
            raise RecipeError(f"step {step_i}: sound {name!r} defined twice")
        what = f"step {step_i} sound {name}"
        sounds[name] = {"name": name, "step": step_i, "src_rel": src_rel, "src": c.src(src_rel, what), "opts": opts,
                        "guid": guid or c.guid("sound", name), "mode": mode, "explicit": guid is not None,
                        "debugName": debug or (f"{mod_id}_{name}" if mod_id else name)}
        return sounds[name]

    def add_cue(step_i, name, spec):
        if name in cues:
            raise RecipeError(f"step {step_i}: cue {name!r} defined twice")
        cues[name] = dict(spec, name=name, step=step_i)
        return cues[name]

    for i, st in own_steps(doc):
        op, name = st["op"], st["name"]
        what = f"step {i} ({op} {name})"
        explicit = c.resolve(st.get("guid"), {"sound": R.SOUND, "cue": R.CUE, "voiceover": R.VO}[op], what) \
            if st.get("guid") else None
        if st.get("guid") and explicit is None:
            raise RecipeError(f"{what}: guid {st['guid']!r} is neither a GUID nor @input")
        if st.get("mode") not in (None, "new", "override"):
            raise RecipeError(f"{what}: mode {st['mode']!r} (new or override)")
        rec = {"step": i, "op": op, "name": name, "sounds": [], "cues": [], "voiceovers": []}
        steps.append(rec)
        if op == "sound":
            opts = _source_opts(st, {"channels": "keep"}, what)
            add_sound(i, name, st.get("src"), opts, explicit, st.get("mode"), st.get("debugName"))
            rec["sounds"].append(name)
            if st.get("cue") is not None:
                cf = {} if st["cue"] is True else st["cue"]
                if not isinstance(cf, dict) or set(cf) - CUE_KEYS:
                    raise RecipeError(f"{what}: cue: true or an object of cue fields ({', '.join(sorted(set(cf) - CUE_KEYS)) if isinstance(cf, dict) else cf})")
                add_cue(i, name, {"takes": [name], "fields": cf, "guid": c.guid("cue", name), "mode": "new", "base": None})
                rec["cues"].append(name)
        elif op == "cue":
            base = st.get("base")
            if base is not None and (base not in c.inputs or c.inputs[base]["type"] != R.CUE):
                raise RecipeError(f"{what}: base {base!r} must be an input of type {R.CUE}")
            takes = st.get("sounds")
            if takes is None and base is None:
                raise RecipeError(f"{what}: sounds missing")
            for k in ("sounds", "addSounds"):
                if st.get(k) is not None and (not isinstance(st[k], list) or not st[k]):
                    raise RecipeError(f"{what}: {k}: a non-empty list")
            fields = {k: st[k] for k in CUE_KEYS if k in st}
            guid = explicit or c.guid("cue", name)
            mode = st.get("mode") or ("override" if base is not None and guid == c.inputs[base]["guid"] else "new")
            add_cue(i, name, {"takes": takes, "add": st.get("addSounds"), "fields": fields, "guid": guid, "mode": mode,
                              "base": base, "explicit": explicit is not None})
            rec["cues"].append(name)
        else:
            narration = bool(st.get("narration", False))
            langs_in = st.get("languages") or {}
            if not isinstance(langs_in, dict):
                raise RecipeError(f"{what}: languages: {{\"en\": [files], \"de\": [...]}}")
            base = st.get("base")
            if base is not None and (base not in c.inputs or c.inputs[base]["type"] != R.VO):
                raise RecipeError(f"{what}: base {base!r} must be an input of type {R.VO}")
            if not langs_in and base is None:
                raise RecipeError(f"{what}: languages missing")
            fb_raw = st["fallback"] if "fallback" in st else mod_default_fb
            try:
                fb = R.lang_key(fb_raw) if fb_raw is not None else None
                langs = {}
                for k, v in langs_in.items():
                    lk = R.lang_key(k)
                    if lk in langs:
                        raise RecipeError(f"{what}: language {lk} given twice")
                    langs[lk] = v
            except ValueError as e:
                raise RecipeError(f"{what}: {e}")
            defaults = {"channels": st.get("channels", "mono"), "gain": st.get("gain"), "normalize": st.get("normalize")}
            cue_all = {k: st[k] for k in CUE_KEYS if k in st}   # cue fields on the step: every language's cue
            cue_all.update(st.get("cue") or {})
            per_lang = st.get("languageCue") or {}
            if set(cue_all) - CUE_KEYS:
                raise RecipeError(f"{what}: cue: unknown field(s) {', '.join(sorted(set(cue_all) - CUE_KEYS))}")
            lang_cues = {}
            for lk in R.LANGS:
                if lk not in langs:
                    continue
                takes = langs[lk]
                if not isinstance(takes, list) or not takes:
                    raise RecipeError(f"{what}: languages.{lk}: a non-empty list of files")
                tnames = []
                for ti, tk in enumerate(takes):
                    spec = {"src": tk} if isinstance(tk, str) else tk
                    if not isinstance(spec, dict) or set(spec) - SOURCE_KEYS:
                        raise RecipeError(f"{what}: languages.{lk}[{ti}]: a path or {{src, channels, gain, normalize, start, end}}")
                    tn = f"{name}.{lk}.{ti}"
                    add_sound(i, tn, spec.get("src"), _source_opts(spec, defaults, f"{what} {lk}[{ti}]"))
                    tnames.append(tn)
                    rec["sounds"].append(tn)
                lf = {"scene": "Sfx" if narration else "Voice",
                      "playScene": "LoreVoiceover" if narration else "Game"}
                lf.update(cue_all)
                lpl = per_lang.get(lk) or per_lang.get(next((k for k in per_lang if R.LANG_ALIASES.get(k.lower()) == lk), ""), {})
                if set(lpl) - CUE_KEYS:
                    raise RecipeError(f"{what}: languageCue.{lk}: unknown field(s) {', '.join(sorted(set(lpl) - CUE_KEYS))}")
                lf.update(lpl)
                cn = f"{name}.{lk}"
                add_cue(i, cn, {"takes": tnames, "fields": lf, "guid": c.guid("cue", cn), "mode": "new", "base": None,
                                "voiceover": name, "language": lk})
                lang_cues[lk] = cn
                rec["cues"].append(cn)
            if fb is not None and fb not in lang_cues and base is None:
                raise RecipeError(f"{what}: fallback {fb!r} has no takes")
            subs = st.get("subtitles") or {}
            if not isinstance(subs, dict) or not all(isinstance(v, str) for v in subs.values()):
                raise RecipeError(f"{what}: subtitles: {{\"en\": \"text\", ...}}")
            guid = explicit or c.guid("voiceover", name)
            mode = st.get("mode") or ("override" if base is not None and guid == c.inputs[base]["guid"] else "new")
            vos[name] = {"name": name, "step": i, "guid": guid, "mode": mode, "narration": narration, "fallback": fb,
                         "lang_cues": lang_cues, "subtitles": subs, "base": base}
            rec["voiceovers"].append(name)

    # 2. decode, resample, encode every source (native helper in parallel, or the Python path)
    jobs = list(sounds.values())
    workers = max(1, min(8, os.cpu_count() or 1)) if native is not None else 1
    with concurrent.futures.ThreadPoolExecutor(max_workers=workers) as ex:
        futs = {j["name"]: ex.submit(_cook_pcm, j, native, cross_check) for j in jobs}
        for j in jobs:
            try:
                j["pcm"], j["qoa"], j["stats"], j["encoder"], j["cross"] = futs[j["name"]].result()
            except Exception as e:  # noqa: BLE001  (native or Python decode error: say which step)
                raise RecipeError(f"step {j['step']} sound {j['name']} ({j['src_rel']}): {e}")

    # 3. sound resources
    sub = c.out / SUBDIR
    files, skipped_files = {}, set()
    reports = {}
    for j in jobs:
        frames, ch = j["stats"]["frames"], j["stats"]["channels"]
        if ch not in R.CHANNEL_CONFIG:
            raise RecipeError(f"step {j['step']} sound {j['name']}: {ch} channels (the game plays 1, 2, 4, 6, 8)")
        desc, blob = R.sound_desc(frames, ch, j["qoa"], j["debugName"])
        data = R.encode(t, R.SOUND, desc)
        checks, warns, info, _ = V.sound(t, data, blob, j["pcm"], frames, ch, j["stats"], native)
        if j["cross"] is not None:
            checks["native_equals_python_encoder"] = j["cross"]
        mode = j["mode"] or ("override" if j["explicit"] and game is not None and game.has(t, j["guid"], R.SOUND) else "new")
        j["mode"] = mode
        if game is not None and game.side == "client":
            exists = game.has(t, j["guid"], R.SOUND)
            checks["mode_" + mode] = exists if mode == "override" else not game.kfc.by_guid(j["guid"])
        elif mode == "override":
            warns.append("override not checked against the game (no installed client)")
        j["streamed"] = blob is not None
        fb = sub / f"{j['name']}.sound.bin"
        files[fb] = data
        j["bin"] = fb
        if blob is not None:
            fq = sub / f"{j['name']}.qoa"
            files[fq] = blob
            j["blob"] = fq
        st = j["stats"]
        reports[("sound", j["name"])] = {
            "kind": "sound", "name": j["name"], "guid": j["guid"], "mode": mode, "src": j["src_rel"],
            "source": st["source"], "frames": frames, "channels": ch, "seconds": round(frames / R.RATE, 4),
            "streamed": j["streamed"], "resampled": st["resampled"], "dithered": not st["exact"],
            "gain_db": st["gain_db"], "peak_dbfs": st["peak_dbfs"], "rms_active_dbfs": st["rms_active_dbfs"],
            "clipped": st["clipped"], "encoder": j["encoder"], "bin": c.rel_recipe(fb), "bin_size": len(data),
            "blob": c.rel_recipe(j["blob"]) if blob is not None else None, "blob_size": len(blob) if blob else 0,
            "content_hash": desc["streamedData"]["hash"] if blob is not None else None,
            **info, "checks": checks, "warnings": warns, "ok": all(checks.values())}

    # 4. cues
    by_guid = {j["guid"]: j for j in jobs}
    vanilla_cache = {}

    def take_guid(ref, what):
        if isinstance(ref, dict):
            if set(ref) - {"sound", "chance"} or "sound" not in ref:
                raise RecipeError(f"{what}: a take is a sound name, @input, GUID or {{\"sound\": …, \"chance\": c}}")
            return take_guid(ref["sound"], what)[0], float(_num(ref.get("chance", 1.0), f"{what}: chance", 0))
        if ref in sounds:
            return sounds[ref]["guid"], 1.0
        g = c.resolve(ref, R.SOUND, what)
        if g is None:
            raise RecipeError(f"{what}: take {ref!r} is not a sound of this recipe, an @input or a GUID")
        return g, 1.0

    def known(g):
        if g in by_guid:
            return True
        if game is None or game.side != "client":
            return None
        return game.has(t, g, R.SOUND)

    def take_frames(g):
        if g in by_guid:
            return by_guid[g]["stats"]["frames"]
        if game is not None and game.side == "client" and game.has(t, g, R.SOUND):
            if g not in vanilla_cache:
                vanilla_cache[g] = R.decode(t, R.SOUND, game.read(t, g, R.SOUND))["frameCount"]
            return vanilla_cache[g]
        return None

    for name, cu in cues.items():
        what = f"step {cu['step']} cue {name}"
        warns = []
        if cu["base"] is not None:
            inp = c.inputs[cu["base"]]
            if not inp["available"]:
                reports[("cue", name)] = {"kind": "cue", "name": name, "guid": cu["guid"], "mode": cu["mode"],
                                          "skipped": inp["why"], "checks": {}, "warnings": [], "ok": True}
                skipped_files.add(sub / f"{name}.cue.bin")
                cu["skipped"] = True
                continue
            desc = R.decode(t, R.CUE, game.read(t, inp["guid"], R.CUE))
            desc["entries"] = list(desc["entries"])
        else:
            desc = R.cue_defaults(len(cu["takes"] or []), cu["fields"].get("scene", "Sfx"))
        _apply_cue_fields(desc, cu["fields"], what)
        if cu["takes"] is not None:
            desc["entries"] = [R.entry(*take_guid(x, what)) for x in cu["takes"]]
            if cu["base"] is None and "avoidRepeatingLastX" not in cu["fields"]:
                desc["avoidRepeatingLastX"] = max(0, min(2, len(desc["entries"]) - 1))
        for x in cu.get("add") or []:
            desc["entries"].append(R.entry(*take_guid(x, what)))
        data = R.encode(t, R.CUE, desc)
        ks = {e["value"]["sound"]: known(e["value"]["sound"]) for e in desc["entries"]}
        checks, w2, back = V.cue(t, data, ks)
        warns += w2
        if game is not None and game.side == "client":
            exists = game.has(t, cu["guid"], R.CUE)
            checks["mode_" + cu["mode"]] = exists if cu["mode"] == "override" else not game.kfc.by_guid(cu["guid"])
        elif cu["mode"] == "override":
            warns.append("override not checked against the game (no installed client)")
        if desc["hasPosition"]:
            stereo = [by_guid[g]["name"] for g in ks if g in by_guid and by_guid[g]["stats"]["channels"] != 1]
            if stereo:
                warns.append(f"positional cue with non-mono takes ({', '.join(stereo)}): 3D sounds should be mono")
        fc = sub / f"{name}.cue.bin"
        files[fc] = data
        frames = [take_frames(e["value"]["sound"]) for e in desc["entries"]]
        cu.update(desc=back, bin=fc, durationMs=(max(frames) * 1000 // R.RATE) if frames and None not in frames else None,
                  playScene=cu["fields"].get("playScene") or _default_play_scene(back["scene"]))
        reports[("cue", name)] = {"kind": "cue", "name": name, "guid": cu["guid"], "mode": cu["mode"],
                                  "base": c.inputs[cu["base"]]["guid"] if cu["base"] else None, "scene": back["scene"],
                                  "takes": [e["value"]["sound"] for e in back["entries"]], "bin": c.rel_recipe(fc),
                                  "bin_size": len(data), "checks": checks, "warnings": warns, "ok": all(checks.values())}

    # 5. voice-overs
    cue_guids = {cu["guid"] for cu in cues.values()}
    for name, vo in vos.items():
        what = f"step {vo['step']} voiceover {name}"
        slots, durs, filled = [R.NULL_GUID] * 5, [0] * 5, {}
        if vo["base"] is not None:
            inp = c.inputs[vo["base"]]
            if not inp["available"]:
                raise RecipeError(f"{what}: base {vo['base']}: {inp['why']}")
            bv = R.decode(t, R.VO, game.read(t, inp["guid"], R.VO))
            slots, durs = list(bv["languages"]), list(bv["durations"])
            cue_guids |= {g for g in slots if g != R.NULL_GUID}
        lang_ms = {}
        for si, lk in enumerate(R.LANGS):
            cn = vo["lang_cues"].get(lk)
            if cn is None:
                continue
            slots[si] = cues[cn]["guid"]
            ms = cues[cn]["durationMs"]
            lang_ms[lk] = ms
            longest_ns = max(R.duration_ns(by_guid[g]["stats"]["frames"]) for g in
                             (e["value"]["sound"] for e in cues[cn]["desc"]["entries"]))
            durs[si] = longest_ns // 10_000_000 if vo["narration"] else 0
        if vo["fallback"] is not None:
            fi = R.LANGS.index(vo["fallback"])
            if slots[fi] == R.NULL_GUID:
                raise RecipeError(f"{what}: fallback {vo['fallback']} slot is empty")
            for si, lk in enumerate(R.LANGS):
                if slots[si] == R.NULL_GUID:
                    slots[si], durs[si] = slots[fi], durs[fi]
                    filled[lk] = vo["fallback"]
        if any(d > 65535 for d in durs):
            raise RecipeError(f"{what}: a narration take is longer than 655 s (durations are u16 in 10 ms units)")
        desc = R.vo_desc(slots, durs)
        data = R.encode(t, R.VO, desc)
        checks, back = V.voiceover(t, data, cue_guids)
        warns = []
        if game is not None:
            exists = game.has(t, vo["guid"], R.VO)
            checks["mode_" + vo["mode"]] = exists if vo["mode"] == "override" else not game.kfc.by_guid(vo["guid"])
        fv = sub / f"{name}.vo.bin"
        files[fv] = data
        vo.update(slots=slots, durations=durs, filled=filled, bin=fv, lang_ms=lang_ms)
        reports[("voiceover", name)] = {"kind": "voiceover", "name": name, "guid": vo["guid"], "mode": vo["mode"],
                                        "narration": vo["narration"], "fallback": vo["fallback"],
                                        "languages": dict(zip(R.LANGS, slots)), "durations10ms": durs,
                                        "filled": filled, "bin": c.rel_recipe(fv), "bin_size": len(data),
                                        "checks": checks, "warnings": warns, "ok": all(checks.values())}

    # 6. manifest entries, in step order
    entries = []
    for rec in steps:
        for sn in rec["sounds"]:
            j = sounds[sn]
            res_e = {"kind": "resource", "guid": j["guid"], "type": R.SOUND, "part": 0, "file": c.rel_manifest(j["bin"]),
                     "mode": j["mode"], "side": "client"}
            if j["streamed"]:
                bf = c.rel_manifest(j["blob"])
                entries.append({"kind": "blob", "file": bf, "side": "client"})
                res_e["patch"] = [{"offset": R.field_offset(t, R.SOUND, "streamedData.hash"), "blob": bf}]
            entries.append(res_e)
        for cn in rec["cues"]:
            cu = cues[cn]
            if cu.get("skipped"):
                continue
            entries.append({"kind": "resource", "guid": cu["guid"], "type": R.CUE, "part": 0,
                            "file": c.rel_manifest(cu["bin"]), "mode": cu["mode"], "side": "client"})
        for vn in rec["voiceovers"]:
            vo = vos[vn]
            entries.append({"kind": "resource", "guid": vo["guid"], "type": R.VO, "part": 0,
                            "file": c.rel_manifest(vo["bin"]), "mode": vo["mode"]})

    # 7. audio.json: the index Kiln.Audio reads (its format is this function's output)
    index = {"format": 1, "tool": TOOL, "mod": mod_id, "guidNamespace": doc.get("guidNamespace"), "sounds": {},
             "cues": {}, "voiceovers": {}}
    for j in jobs:
        index["sounds"][j["name"]] = {"guid": j["guid"], "mode": j["mode"], "frames": j["stats"]["frames"],
                                      "channels": j["stats"]["channels"], "streamed": j["streamed"],
                                      "durationMs": j["stats"]["frames"] * 1000 // R.RATE}
    for name, cu in cues.items():
        if cu.get("skipped"):
            continue
        d = cu["desc"]
        index["cues"][name] = {"guid": cu["guid"], "mode": cu["mode"], "scene": d["scene"], "playScene": cu["playScene"],
                               "loop": d["loop"],
                               "positional": {"min": d["minDistance"], "max": d["maxDistance"]} if d["hasPosition"] else None,
                               "volume": round(d["volume"], 6), "durationMs": cu["durationMs"],
                               "takes": [e["value"]["sound"] for e in d["entries"]]}
    for name, vo in vos.items():
        langs = {}
        for si, lk in enumerate(R.LANGS):
            g = vo["slots"][si]
            if g == R.NULL_GUID:
                langs[lk] = None
                continue
            src = vo["filled"].get(lk, lk)
            e = {"cue": g, "durationMs": vo["lang_ms"].get(src)}
            if lk in vo["filled"]:
                e["fallbackOf"] = vo["filled"][lk]
            langs[lk] = e
        index["voiceovers"][name] = {"guid": vo["guid"], "mode": vo["mode"], "narration": vo["narration"],
                                     "fallback": vo["fallback"], "languages": langs, "durations10ms": vo["durations"],
                                     "subtitles": vo["subtitles"]}
    index_bytes = (json.dumps(index, indent=2) + "\n").encode()
    files[c.out / "audio.json"] = index_bytes

    # 8. write
    for f, data in files.items():
        f.parent.mkdir(parents=True, exist_ok=True)
        f.write_bytes(data)

    # the committed assets.json must name every output exactly as cooked
    man = json.loads(c.manifest.read_text()) if c.manifest.exists() else {"format": 1, "entries": []}
    have = {(e.get("kind"), e.get("file")): e for e in man.get("entries", [])}
    mine = {(e["kind"], e["file"]) for e in entries}
    prefix = c.rel_manifest(sub) + "/"
    keep_skipped = {c.rel_manifest(f) for f in skipped_files}
    stale = [e for e in man.get("entries", []) if str(e.get("file", "")).startswith(prefix)
             and (e.get("kind"), e.get("file")) not in mine and e.get("file") not in keep_skipped]
    missing = [e for e in entries if (e["kind"], e["file"]) not in have]
    mismatched = [e for e in entries if (e["kind"], e["file"]) in have and have[(e["kind"], e["file"])] != e]
    if update_manifest and (missing or mismatched or stale):
        drop = mine | {(e.get("kind"), e.get("file")) for e in stale}
        man["entries"] = [x for x in man.get("entries", []) if (x.get("kind"), x.get("file")) not in drop] + entries
        man.setdefault("format", 1)
        c.manifest.write_text(json.dumps(man, indent=2) + "\n")
        missing, mismatched, stale = [], [], []

    step_reports = []
    for rec in steps:
        parts = [reports[("sound", n)] for n in rec["sounds"]] + [reports[("cue", n)] for n in rec["cues"]] + \
                [reports[("voiceover", n)] for n in rec["voiceovers"]]
        step_reports.append({"step": rec["step"], "op": rec["op"], "name": rec["name"], "ok": all(x["ok"] for x in parts),
                             "outputs": parts})
    ok = all(s["ok"] for s in step_reports) and not missing and not mismatched and not stale
    srcs = []
    for j in jobs:
        rel = c.rel_recipe(j["src"])
        if rel not in {s["file"] for s in srcs}:
            srcs.append({"file": rel, "sha256": sha256(j["src"].read_bytes())})
    report = {
        "tool": TOOL, "recipe": str(p), "recipe_sha256": sha256(p.read_bytes()), "ok": ok,
        "game": game.summary() if game else None, "types": tinfo,
        "native": {"path": str(native.path), "version": native.version} if native else None,
        "inputs": in_report, "sources": srcs,
        "outputs": [{"file": c.rel_recipe(f), "size": len(d), "sha256": sha256(d)} for f, d in files.items()],
        "index": c.rel_recipe(c.out / "audio.json"),
        "manifest": {"file": c.rel_recipe(c.manifest), "missing": missing, "mismatched": mismatched, "stale": stale},
        "steps": step_reports,
    }
    (c.out / report_name(doc)).write_text(json.dumps(report, indent=2) + "\n")
    report["report_file"] = str(c.out / report_name(doc))
    return report
