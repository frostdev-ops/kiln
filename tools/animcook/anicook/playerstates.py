# SPDX-License-Identifier: MIT
"""A mod's player-state definitions: <mod>/assets/player-states.json, format 1. Schema: schema/player-states.schema.json.

    { "format": 1,
      "states": {
        "cinder-slash-1": { "clip": "cinder_slash_1", "like": "Attack_Sword_Shield_01" },
        "cinder-guard":   { "clip": "cinder_guard", "like": "Block_FullBody", "loop": true, "blendIn": 0.12, "speed": 1.1 } } }

Strict JSON (no comments, no trailing commas, no NaN, no byte order mark); a key twice and an unknown key are errors. The mod id, and
the "side" and "clients" a mod with player states must state, come from the nearest kiln.mod.json above the file, or from arguments.
A wrong state is left out with its reason; the others stay. Whether `like` names a state that may be a neighbour, and whether the
clip is served and fits the player's skeleton, is decided by the merge (playergraph.py), which needs the game's graph.

Ids: the state's GUID is derive(mod, "state", name) (Kiln.Blocks' BlockIds rule); its id in the graph is FNV-1a-32 of the GUID's 16
bytes. A clip given by name is derive(mod, "clip", name), a clip written as a GUID is that GUID.
"""
import json
import re
import struct
from pathlib import Path

from kfc.animgraph import fnv_guid

from .graphgen import derive

FORMAT = 1
FILE_NAME = "player-states.json"
TOP_KEYS = ("format", "states")
STATE_KEYS = ("clip", "like", "blendIn", "loop", "speed")
NAME_MAX = 64  # characters (Unicode code points)
BLEND_MAX = 5.0  # seconds
NEED_SIDE, NEED_CLIENTS = "both", "mod"
GUID_RE = re.compile(r"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$")
_UNSET = object()


class DefinitionError(Exception):
    """The file as a whole cannot be read: no state of it loads."""


def f32(x):
    """`x` rounded to float32, the way the graph stores it (round to nearest, ties to even)."""
    return struct.unpack("<f", struct.pack("<f", float(x)))[0]


def state_guid(mod, name):
    return derive(mod, "state", name)


def clip_guid(mod, clip):
    """A clip of this mod's cook by name, or a GUID written out (36 characters, any case; returned lower case)."""
    return clip.lower() if GUID_RE.match(clip) else derive(mod, "clip", clip)


def manifest_reason(mod, side, clients):
    """Why a mod's player states cannot load at all, or None. The server evaluates the player's graph and every player's game draws
    it, so the mod has to load on both sides and Kiln.Network has to keep players without it out, which it does only for a
    "clients" written out in the manifest."""
    if side == NEED_SIDE and clients == NEED_CLIENTS:
        return None
    return (f"mod {mod}: player states have to be on the server and on every player's game: set \"side\": \"both\" and "
            f"\"clients\": \"mod\" in kiln.mod.json")


class State:
    """One definition. `status` is None while nothing has refused it, "refused" with `reason` after, "live" once merged."""

    def __init__(self, mod, name):
        self.mod, self.name = mod, name
        self.clip_ref = self.clip = self.like = None
        self.blend_in = self.loop = self.speed = None
        self.guid = state_guid(mod, name)
        self.id = fnv_guid(self.guid)
        self.kind = self.machine = None
        self.status = self.reason = None

    def refuse(self, why):
        """Refuse with `why` (the text after "mod <mod>, state <name>: "). A state refused once stays refused with its first reason."""
        if self.status != "refused":
            self.status, self.reason = "refused", f"mod {self.mod}, state {self.name}: {why}"

    def key(self):
        """Sort key of rule 11: mod id, then state name, as UTF-8 bytes."""
        return self.mod.encode(), self.name.encode()

    def entry(self):
        """The report's line for this state (the run-time status has the same fields)."""
        return {"mod": self.mod, "name": self.name, "guid": self.guid, "id": f"0x{self.id:08x}", "kind": self.kind, "like": self.like,
                "machine": self.machine, "status": self.status, "reason": self.reason}


class ModStates:
    def __init__(self, mod, side, clients, source, states):
        self.mod, self.side, self.clients, self.source, self.states = mod, side, clients, source, states


class _Pairs(list):
    """An object as its (key, value) pairs, so that a key given twice is seen."""


def _strict_json(text, where):
    def no_constant(name):
        raise DefinitionError(f"{where}: {name} is not JSON")
    try:
        return json.loads(text, object_pairs_hook=_Pairs, parse_constant=no_constant)
    except json.JSONDecodeError as e:
        raise DefinitionError(f"{where}: not strict JSON: {e}") from e


def _number(v):
    return isinstance(v, (int, float)) and not isinstance(v, bool)


def _check_state(st, value):
    """Fills `st` from its object; returns the problems found, in a fixed order."""
    problems = []
    name = st.name
    if not 1 <= len(name) <= NAME_MAX or "/" in name:
        problems.append(f"a state name has 1 to {NAME_MAX} characters and no '/'")
    if not isinstance(value, _Pairs):
        return problems + ["a state is an object: {\"clip\": ..., \"like\": ...}"]
    keys = [k for k, _ in value]
    twice = sorted({k for k in keys if keys.count(k) > 1})
    if twice:
        problems.append(f"key {', '.join(twice)} given twice")
    unknown = sorted(set(keys) - set(STATE_KEYS))
    if unknown:
        problems.append(f"unknown keys {', '.join(unknown)} (known: {', '.join(STATE_KEYS)})")
    d = dict(value)
    clip, like = d.get("clip"), d.get("like")
    if not isinstance(clip, str) or not clip:
        problems.append("clip: a clip name of this mod's cook or a GUID")
    else:
        st.clip_ref, st.clip = clip, clip_guid(st.mod, clip)
    if not isinstance(like, str) or not like:
        problems.append("like: a state of the player's graph, State or Machine/State")
    else:
        st.like = like
        parts = like.split("/")
        if len(parts) > 2 or not all(parts):
            problems.append(f"like {like!r}: State or Machine/State")
    if "blendIn" in d:
        v = d["blendIn"]
        if not _number(v) or not 0 <= v <= BLEND_MAX:
            problems.append(f"blendIn: seconds from 0 to {BLEND_MAX:g}")
        else:
            st.blend_in = f32(v)
    if "loop" in d:
        if not isinstance(d["loop"], bool):
            problems.append("loop: true or false")
        else:
            st.loop = d["loop"]
    if "speed" in d:
        v = d["speed"]
        ok = _number(v) and v > 0
        if ok:
            try:
                ok = 0 < f32(v) < float("inf")
            except OverflowError:
                ok = False
        if not ok:
            problems.append("speed: a number above 0 (a float32)")
        else:
            st.speed = f32(v)
    return problems


def parse(text, mod, side, clients, source=FILE_NAME):
    """`text` of a player-states.json of mod `mod`, whose manifest states `side` and `clients` (None: not written). -> ModStates.
    Raises DefinitionError when the file cannot be read at all."""
    where = f"mod {mod}: {source}"
    doc = _strict_json(text, where)
    if not isinstance(doc, _Pairs):
        raise DefinitionError(f"{where}: an object with \"format\" and \"states\"")
    keys = [k for k, _ in doc]
    twice = sorted({k for k in keys if keys.count(k) > 1})
    if twice:
        raise DefinitionError(f"{where}: key {', '.join(twice)} given twice")
    unknown = sorted(set(keys) - set(TOP_KEYS))
    if unknown:
        raise DefinitionError(f"{where}: unknown keys {', '.join(unknown)} (known: {', '.join(TOP_KEYS)})")
    d = dict(doc)
    if type(d.get("format")) is not int or d["format"] != FORMAT:
        raise DefinitionError(f"{where}: format {d.get('format')!r}, expected {FORMAT}")
    if not isinstance(d.get("states"), _Pairs):
        raise DefinitionError(f"{where}: \"states\": an object of states by name")
    pairs = d["states"]
    names = [k for k, _ in pairs]
    states, seen = [], {}
    for name, value in pairs:
        if name in seen:
            continue
        st = State(mod, name)
        seen[name] = st
        states.append(st)
        if names.count(name) > 1:
            st.refuse("defined twice in \"states\"")
            continue
        problems = _check_state(st, value)
        if problems:
            st.refuse("; ".join(problems))
    by_id = {}
    for st in states:
        by_id.setdefault(st.id, []).append(st)
    for same in by_id.values():
        if len(same) > 1:
            for st in same:
                others = ", ".join(o.name for o in same if o is not st)
                st.refuse(f"its state id 0x{st.id:08x} is the id of state {others} too: rename one")
    why = manifest_reason(mod, side, clients)
    if why:
        for st in states:
            st.status, st.reason = "refused", why + (f" (also {st.reason})" if st.reason else "")
    return ModStates(mod, side, clients, source, states)


def read_manifest(path):
    """kiln.mod.json, read as recipe.mod_id reads it: whole-line // comments and trailing commas allowed."""
    text = re.sub(r"^\s*//.*$", "", Path(path).read_text(encoding="utf-8"), flags=re.M)
    return json.loads(re.sub(r",(\s*[}\]])", r"\1", text))


def find_manifest(path):
    for d in Path(path).resolve().parents:
        m = d / "kiln.mod.json"
        if m.exists():
            return m
    return None


def load(path, mod=None, side=_UNSET, clients=_UNSET):
    """Read a player-states.json. Without `mod` the id, "side" and "clients" come from the nearest kiln.mod.json above it; with `mod`
    (tests, tools) `side` and `clients` must be given too (None: the key is not written)."""
    p = Path(path)
    if mod is None:
        m = find_manifest(p)
        if m is None:
            raise DefinitionError(f"{p}: no kiln.mod.json above it: player states need the mod's id")
        try:
            man = read_manifest(m)
        except (OSError, ValueError) as e:
            raise DefinitionError(f"{m}: {e}") from e
        mod = man.get("id")
        if not isinstance(mod, str) or not mod:
            raise DefinitionError(f"{m}: no \"id\"")
        side, clients = man.get("side"), man.get("clients")
    elif side is _UNSET or clients is _UNSET:
        raise TypeError("load(path, mod=...) needs side= and clients= as well (None when the manifest does not write the key)")
    try:
        raw = p.read_bytes()
    except OSError as e:
        raise DefinitionError(f"mod {mod}: {p}: {e}") from e
    try:
        text = raw.decode("utf-8")
    except UnicodeDecodeError as e:
        raise DefinitionError(f"mod {mod}: {p.name}: not UTF-8: {e}") from e
    if text.startswith("\ufeff"):
        raise DefinitionError(f"mod {mod}: {p.name}: starts with a byte order mark (strict JSON has none)")
    return parse(text, mod, side, clients, source=p.name)
