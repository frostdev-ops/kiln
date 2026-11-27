#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""registrygen: Kiln's registry schemas (schema/registry/<registry>.json) -> the files made from them.

    registrygen.py [--root <repo>]               write every output (refused while a check below fails)
    registrygen.py [--root <repo>] --check       exit 1 when a schema is invalid, a name is not in ids.lock.json, or an
                                                 output differs from what the schemas give
    registrygen.py [--root <repo>] lock          add every new registry and field to schema/registry/ids.lock.json
                                                 (each gets the next id, never one used before; nothing is removed)
    registrygen.py [--root <repo>] retire <registry>.<field> <version>
                                                 move a field that left its schema to the lock's "retired" list

Outputs:
  native/services/content/registry/schema.g.h            the native descriptor: registries, field ids, types, ranges, applies
  managed/services/Kiln.Registry/Generated/<R>.gen.cs      the C# entry types of each registry (Kiln.Registry)
  managed/services/Kiln.Registry/Generated/RegistrySchema.gen.cs   the field catalog (Kiln.Registry.RegistrySchema)
  schema/registry-section.schema.json                    the data section's schema (content/*.json "registry")
  docs/kiln/modding/registries/<registry>.md             one page per registry
  managed/Kiln.Analyzers/src/Kiln.Analyzers/RegistryHints.g.cs   the analyzers' hint table (KILN124, KILN125): the old data
                                                         paths and game functions each registry field covers, from
                                                         regmap/common (Kiln's part: the analyzers read a mod's old paths)

The rules the schemas follow are schema/registry-schema.schema.json's plus what a JSON schema cannot say: every leaf has
"applies" (its own or its parent's), ops fit the type, Kiln names only (no game member, type or offset), refs name a
registry Kiln knows, ids are never reused (ids.lock.json), a major version bump ships a migration. Python 3.10+, no
dependencies (like tools/typegen).
"""
import argparse
import json
import re
import sys
from dataclasses import dataclass, field as dc_field
from pathlib import Path

SCHEMA_DIR = "schema/registry"
LOCK = "schema/registry/ids.lock.json"
NATIVE = "native/services/content/registry/schema.g.h"
CS_DIR = "managed/services/Kiln.Registry/Generated"
SECTION = "schema/registry-section.schema.json"
DOCS = "docs/kiln/modding/registries"
HINTS = "managed/Kiln.Analyzers/src/Kiln.Analyzers/RegistryHints.g.cs"
# The old paths' sections and the game type their paths start from: kiln.gamedata's (native gamedata_res.cpp builds each from
# that type; tests check the two agree) and the item data section. kiln.content's items, recipes, loot and drops sections start
# where the registry of the same name does (regmap/common entries).
GAMEDATA_SECTIONS = {"templates": "keen::ecs::TemplateResource", "arsenals": "keen::enemy::EnemyArsenalResource",
                     "sequences": "keen::actor::ActorSubSequenceResource", "buffs": "keen::BuffType", "perks": "keen::Perk",
                     "skills": "keen::SkillTreeNode", "itemdata": "keen::ItemInfo"}
CONTENT_SECTIONS = ("items", "recipes", "loot", "drops")
# conversions whose Kiln value is the game's value (a data pack's number or bool moves to the registry unchanged)
HINT_EXACT = ("int", "float", "bool", "time")

APPLIES = ("now", "worldLoad", "restart")
JOINED_CLIENT = ("now", "worldLoad", "rejoin", "restart", "serverOnly", "localOnly", "unknown")
TYPES = ("int", "float", "bool", "enum", "flags", "ref", "time", "struct", "list")
NUMBERS = ("int", "float", "time")
RESERVED_MODS = ("game", "kiln")
OPS_FOR = {"int": ("number", "set"), "float": ("number", "set"), "time": ("number", "set"), "bool": ("set",),
           "enum": ("set",), "flags": ("set",), "ref": ("set",), "list": ("list", "indexed")}
# One id grammar (native regmap.cpp is_kiln_id, C# RegistryId, the data section's schema): <namespace>:<name>, lower case,
# a game entry's clash suffix "~<8 to 32 hex digits>" at the end.
KILN_ID = re.compile(r"^[a-z0-9][a-z0-9_.-]*:[a-z0-9_.-]+(~[0-9a-f]{8,32})?$")
# Kiln.Registry's hand-written types: no generated type may take their names.
HANDWRITTEN_TYPES = ("RegistryId", "RegistryValueList", "RegistryEntryList", "RegistryFixedList", "RegistrySchema",
                     "RegistryFieldSpec", "RegistryFieldType", "RegistryOps", "RegistryFieldAttribute", "IRegistryEntry",
                     "IRegistryCells", "RegistryCells", "RegistryApplies", "RegistryListOp")
# Registries a ref may name besides the schemas here: Kiln registries that later phases add (their ids follow the same
# rules; until they exist, a ref to them is checked as an id's shape only).
LATER_REGISTRIES = ("perks", "stations", "inputCategories", "templates")
NAME = re.compile(r"^[a-z][A-Za-z0-9]*$")
TYPE_NAME = re.compile(r"^[A-Z][A-Za-z0-9]*$")
# Game words a schema must not carry (Kiln names only; the game's names and offsets live in regmap/).
GAME_WORDS = re.compile(r"(\+0x[0-9a-fA-F]+|\bkeen::[A-Za-z0-9_:<>,]*|\b0x[0-9a-fA-F]{4,}\b)")
# Not marked <auto-generated/> and not named *.g.cs (both make Roslyn treat a file as generated code): the public API gate
# (PublicAPI.*.txt) records the generated API only when the compiler treats the file as the project's own code.
CS_HEADER = "// SPDX-License-Identifier: MIT\n// Generated by tools/registrygen/registrygen.py from schema/registry/{src}. Do not edit.\n"


class LockError(Exception):
    pass


@dataclass
class Row:
    """One addressable field: a scalar leaf, a list, or a leaf inside a list's elements ("inputs[].count")."""
    registry: str
    path: str
    kind: str
    applies: str
    ops: str
    read_only: bool
    spec: dict
    element: str = ""          # a list: its element's kind
    list_path: str = ""        # a leaf inside list elements: the list's path
    enum_type: str = ""        # enum / flags: the C# type name

    @property
    def qualified(self):
        return f"{self.registry}.{self.path}"


@dataclass
class Registry:
    name: str
    src: str
    doc: dict
    rows: list = dc_field(default_factory=list)

    @property
    def leaves(self):
        return self.rows

    @property
    def type_name(self):
        return self.doc.get("typeName") or pascal(self.name)

    @property
    def version(self):
        return self.doc.get("version", "0.0.0")


@dataclass
class Model:
    registries: dict
    errors: list
    root: str = "."


def pascal(s):
    s = re.sub(r"[^A-Za-z0-9]+", " ", s).strip()
    return "".join(w[:1].upper() + w[1:] for w in s.split())


def vtuple(v):
    try:
        return tuple(int(x) for x in v.split("."))
    except (ValueError, AttributeError):
        return (0, 0, 0)


# ---------------------------------------------------------------- load and check

def load(root):
    root = Path(root)
    errors, regs, docs = [], {}, {}
    files = sorted(p for p in (root / SCHEMA_DIR).glob("*.json") if p.name != "ids.lock.json")
    for p in files:
        try:
            d = json.loads(p.read_text(encoding="utf-8"))
        except (OSError, ValueError) as e:
            errors.append(f"{p.name}: not JSON ({e})")
            continue
        name = d.get("registry")
        if name != p.stem:
            errors.append(f"{p.name}: \"registry\" is {name!r}, the file's name says {p.stem!r}")
            continue
        docs[name] = (p.name, d)
    known = set(docs) | set(LATER_REGISTRIES)
    for name, (src, d) in docs.items():
        reg = Registry(name, src, d)
        errors += check_registry(d, name)
        walk(reg, d.get("fields") or {}, "", None, None, False, errors, known)
        regs[name] = reg
    errors += check_lengths(regs)
    errors += check_type_names(regs)
    errors += check_game_words(regs, game_names(root, regs))
    return Model(regs, errors, str(root))


def check_lengths(regs):
    """A list as long as another field (lengthField) names a read-only whole number of the same registry."""
    errs = []
    for reg in regs.values():
        rows = {r.path: r for r in reg.rows}
        for r in reg.rows:
            lf = r.spec.get("lengthField") if r.kind == "list" else None
            if lf is None:
                continue
            other = rows.get(lf)
            if other is None or other.kind != "int":
                errs.append(f"{r.qualified}: lengthField {lf!r} is not a whole-number field of {reg.name}")
            elif not other.read_only:
                errs.append(f"{r.qualified}: lengthField {lf!r} must be read-only (indexed edits cannot grow the list with it)")
    return errs


def type_names(reg):
    """Every C# type the generator makes for a registry: (name, what) pairs."""
    out = [(reg.type_name + "Entry", "entry"), (reg.type_name + "Fields", "field ids")]

    def visit(fields):
        for key, spec in fields.items():
            if not isinstance(spec, dict):
                continue
            if spec.get("type") == "struct":
                out.append((spec.get("typeName") or reg.type_name + pascal(key), "struct"))
                visit(spec.get("fields") or {})
            elif spec.get("type") == "list" and isinstance(spec.get("of"), dict) and spec["of"].get("type") == "struct":
                out.append((spec["of"].get("typeName") or reg.type_name + pascal(key) + "Item", "list element"))
                visit(spec["of"].get("fields") or {})
    visit(reg.doc.get("fields") or {})
    enums = {}
    for r in reg.rows:
        if r.enum_type and r.kind != "list":
            vals = tuple(r.spec.get("values", []))
            if r.enum_type in enums and enums[r.enum_type] != vals:
                out.append((r.enum_type, "enum"))     # the same name with other values: a second type
            elif r.enum_type not in enums:
                enums[r.enum_type] = vals
                out += [(r.enum_type, "enum"), (r.enum_type + "Names", "enum names")]
    return out


def check_type_names(regs):
    errs, seen = [], {}
    for reg in regs.values():
        for n, what in type_names(reg):
            if n in HANDWRITTEN_TYPES:
                errs.append(f"{reg.name}: the C# type {n} ({what}) is one of Kiln.Registry's own types")
            elif n in seen:
                errs.append(f"{reg.name}: the C# type name {n} is used twice ({seen[n]} and {reg.name} {what})")
            else:
                seen[n] = f"{reg.name} {what}"
    return errs


def game_names(root, regs):
    """The game's member and type names regmap/ maps the fields onto (Kiln names only: a schema must not use them),
    leaving out words that are Kiln names too (a game member may share a Kiln field's name)."""
    kiln = set(regs)
    for reg in regs.values():
        for r in reg.rows:
            kiln.update(p for p in r.path.replace("[]", "").split("."))
    words = set()
    for p in sorted((Path(root) / "regmap" / "common").glob("*.json")):
        try:
            d = json.loads(p.read_text(encoding="utf-8"))
        except (OSError, ValueError):
            continue
        e = d.get("entries") or {}
        for k in ("resourceType", "nameFrom"):
            for part in re.split(r"[:.]+", str(e.get(k) or "")):
                words.add(part)
        for k in ("array", "name", "guid", "id"):
            words.update(str(e.get(k) or "").split("."))
        for row in (d.get("rows") or {}).values():
            for path in row.get("paths") or []:
                words.update(str(path).split("."))
    return {w for w in words if w and w not in kiln and w != "keen" and re.search(r"[A-Z]", w)}


def check_game_words(regs, words):
    errs = []
    pat = re.compile(r"\b(" + "|".join(sorted(map(re.escape, words), key=len, reverse=True)) + r")\b") if words else None
    for reg in regs.values():
        d = reg.doc
        texts = [("doc", d.get("doc")), ("ids.rule", (d.get("ids") or {}).get("rule")),
                 ("entry.removeDoc", (d.get("entry") or {}).get("removeDoc"))]
        texts += [(f"traits.{k}", v) for k, v in (d.get("traits") or {}).items()]

        def visit(fields, prefix):
            for key, spec in (fields or {}).items():
                if isinstance(spec, dict):
                    texts.append((f"{prefix}{key}.doc", spec.get("doc")))
                    visit(spec.get("fields"), f"{prefix}{key}.")
                    if isinstance(spec.get("of"), dict):
                        texts.append((f"{prefix}{key}[].doc", spec["of"].get("doc")))
                        visit(spec["of"].get("fields"), f"{prefix}{key}[].")
        visit(d.get("fields"), "")
        for where, text in texts:
            if not isinstance(text, str):
                continue
            m = GAME_WORDS.search(text) or (pat.search(text) if pat else None)
            if m:
                errs.append(f"{reg.name}.{where}: names the game's {m.group(0)!r} (Kiln names only; the game's names and offsets live in regmap/)")
    return errs


def check_registry(d, name):
    errs = []
    need = ["registrySchema", "registry", "version", "stability", "doc", "ids", "fields", "entry", "raw"]
    for k in need:
        if k not in d:
            errs.append(f"{name}: \"{k}\" is missing")
    allowed = set(need) | {"$schema", "experimentalId", "traits", "typeName"}
    for k in d:
        if k not in allowed:
            errs.append(f"{name}: unknown key \"{k}\"")
    if d.get("registrySchema") != 1:
        errs.append(f"{name}: registrySchema must be 1")
    if not NAME.match(name or ""):
        errs.append(f"{name}: a registry name is lower camel case (Kiln names only)")
    if not re.match(r"^[0-9]+\.[0-9]+\.[0-9]+$", str(d.get("version", ""))):
        errs.append(f"{name}: version must be <major>.<minor>.<patch>")
    if d.get("stability") not in ("experimental", "stable"):
        errs.append(f"{name}: stability is experimental or stable")
    if d.get("stability") == "experimental" and not re.match(r"^KILNX[0-9]{3}$", str(d.get("experimentalId", ""))):
        errs.append(f"{name}: an experimental registry needs its experimentalId (KILNX0nn)")
    if "typeName" in d and not TYPE_NAME.match(str(d["typeName"])):
        errs.append(f"{name}: typeName must be a C# type name")
    ids = d.get("ids") or {}
    for k in ("game", "mod", "rule"):
        if not isinstance(ids.get(k), str) or not ids.get(k):
            errs.append(f"{name}: ids.{k} is missing")
    for k in ("game", "mod"):
        if isinstance(ids.get(k), str) and re.sub(r"<[^>]*>", "", ids[k]) != re.sub(r"<[^>]*>", "", ids[k]).lower():
            errs.append(f"{name}: ids.{k} is not lower case (Kiln ids are lower case: D3)")
    for t in (d.get("traits") or {}):
        if not NAME.match(t):
            errs.append(f"{name}: trait {t!r} is not a Kiln name (lower camel case)")
    e = d.get("entry") or {}
    if not isinstance(e.get("add"), bool) or e.get("remove") not in (False, "hidden", "disabled"):
        errs.append(f"{name}: entry needs add (true/false) and remove (false, hidden, disabled)")
    if d.get("raw") not in ("allowed", "refused"):
        errs.append(f"{name}: raw is allowed or refused")
    return errs


FIELD_KEYS = {"type", "doc", "applies", "ops", "readOnly", "min", "max", "unit", "values", "registry", "fields", "of",
              "maxItems", "formula", "side", "since", "typeName", "optional", "fixedLength", "lengthField", "lengthOffset", "joinedClient"}


def walk(reg, fields, prefix, applies, list_path, read_only, errors, known):
    if not isinstance(fields, dict) or not fields:
        errors.append(f"{reg.name}.{prefix or '(root)'}: fields must be a non-empty object")
        return
    for key, spec in fields.items():
        path = f"{prefix}{key}"
        q = f"{reg.name}.{path}"
        if not NAME.match(key):
            errors.append(f"{q}: {key!r} is not a Kiln name (lower camel case, letters and digits)")
            continue
        if not isinstance(spec, dict):
            errors.append(f"{q}: a field is an object")
            continue
        for k in spec:
            if k not in FIELD_KEYS:
                errors.append(f"{q}: unknown key \"{k}\"")
        t = spec.get("type")
        if t not in TYPES:
            errors.append(f"{q}: type {t!r} is not one of {', '.join(TYPES)}")
            continue
        a = spec.get("applies", applies)
        if a is None:
            errors.append(f"{q}: no applies (now, worldLoad or restart), on the field or its parent")
            a = "restart"
        elif a not in APPLIES:
            errors.append(f"{q}: applies {a!r} is not one of now, worldLoad, restart")
        if spec.get("side", "both") not in ("both", "server", "client"):
            errors.append(f"{q}: side is one of both, server, client")
        if "joinedClient" in spec and spec["joinedClient"] not in JOINED_CLIENT:
            errors.append(f"{q}: joinedClient is one of {', '.join(JOINED_CLIENT)}")
        ro = bool(spec.get("readOnly", read_only))
        if spec.get("optional") is not None and (t != "ref" or not isinstance(spec.get("optional"), bool)):
            errors.append(f"{q}: optional (true or false) is for ref fields only")
        for k in ("fixedLength", "lengthField", "lengthOffset"):
            if k in spec and t != "list":
                errors.append(f"{q}: {k} is for lists only")
        if "typeName" in spec and not TYPE_NAME.match(str(spec["typeName"])):
            errors.append(f"{q}: typeName must be a C# type name")
        if t == "struct":
            if "ops" in spec:
                errors.append(f"{q}: a struct has no ops (its fields have)")
            walk(reg, spec.get("fields"), path + ".", a, list_path, ro, errors, known)
            continue
        if t == "list":
            if list_path:
                errors.append(f"{q}: a list inside a list's elements is not supported")
                continue
            of = spec.get("of")
            if not isinstance(of, dict) or of.get("type") not in TYPES:
                errors.append(f"{q}: a list needs \"of\" (its element)")
                continue
            if of.get("type") == "list":
                errors.append(f"{q}: a list of lists is not supported")
                continue
            ops = check_ops(q, spec, "list", ro, errors)
            fixed = spec.get("fixedLength") is not None or spec.get("lengthField") is not None
            if spec.get("fixedLength") is not None and (not isinstance(spec["fixedLength"], int) or isinstance(spec["fixedLength"], bool) or spec["fixedLength"] < 1):
                errors.append(f"{q}: fixedLength is a whole number of at least 1")
            if spec.get("lengthOffset") is not None and (not isinstance(spec["lengthOffset"], int) or spec.get("lengthField") is None):
                errors.append(f"{q}: lengthOffset is a whole number beside lengthField")
            if spec.get("fixedLength") is not None and spec.get("lengthField") is not None:
                errors.append(f"{q}: fixedLength or lengthField, not both")
            if fixed and not ro and ops != "indexed":
                errors.append(f"{q}: a list of fixed length takes only indexed edits (ops indexed: {key}[i] with a value or a number op)")
            if ops == "indexed" and not fixed:
                errors.append(f"{q}: ops indexed is for a list of fixed length (fixedLength or lengthField)")
            if ops == "indexed" and of.get("type") not in ("int", "float", "time", "bool", "enum", "ref"):
                errors.append(f"{q}: ops indexed needs plain values")
            row = Row(reg.name, path, "list", a, ops, ro, spec, element=of["type"])
            if of["type"] != "struct":
                check_scalar(q + "[]", of, errors, known)
                if of["type"] in ("enum", "flags"):
                    row.enum_type = of.get("typeName") or reg.type_name + pascal(key) + "Value"
            check_range(q, {"maxItems": spec.get("maxItems")}, errors, "maxItems")
            reg.rows.append(row)
            if of["type"] == "struct":
                walk(reg, of.get("fields"), path + "[].", a, path, ro, errors, known)
            continue
        ops = check_ops(q, spec, t, ro, errors)
        check_scalar(q, spec, errors, known)
        row = Row(reg.name, path, t, a, ops, ro, spec, list_path=list_path or "")
        if t in ("enum", "flags"):
            row.enum_type = spec.get("typeName") or reg.type_name + pascal(path.replace("[]", ""))
        reg.rows.append(row)


def check_ops(q, spec, t, ro, errors):
    ops = spec.get("ops")
    if ro:
        if ops not in (None, "none"):
            errors.append(f"{q}: a read-only field takes no ops (it has {ops!r})")
        return "none"
    if ops is None:
        errors.append(f"{q}: ops is missing ({' or '.join(OPS_FOR[t])})")
        return OPS_FOR[t][0]
    if ops not in OPS_FOR[t]:
        errors.append(f"{q}: ops {ops!r} does not fit a {t} ({' or '.join(OPS_FOR[t])})")
    return ops


def check_scalar(q, spec, errors, known):
    t = spec.get("type")
    for k in ("min", "max"):
        if spec.get(k) is not None and t in ("bool", "enum", "flags", "ref"):
            errors.append(f"{q}: {k} is for numbers")
    if t in ("enum", "flags"):
        vals = spec.get("values")
        if not isinstance(vals, list) or not vals:
            errors.append(f"{q}: an {t} needs values")
        else:
            for v in vals:
                if not isinstance(v, str) or not NAME.match(v):
                    errors.append(f"{q}: value {v!r} is not a Kiln name (lower camel case)")
            if len(set(map(str, vals))) != len(vals):
                errors.append(f"{q}: values repeat")
            if t == "flags" and len(vals) > 32:
                errors.append(f"{q}: at most 32 flags")
    if t == "ref":
        r = spec.get("registry")
        if r not in known:
            errors.append(f"{q}: ref names registry {r!r}, which Kiln does not know")
    check_range(q, spec, errors)


def check_range(q, spec, errors, *extra):
    lo, hi = spec.get("min"), spec.get("max")
    if spec.get("type") in NUMBERS and (lo is None or hi is None):
        errors.append(f"{q}: a number field needs min and max (the IR refuses values outside them)")
    for k, v in (("min", lo), ("max", hi)):
        if v is not None and (isinstance(v, bool) or not isinstance(v, (int, float))):
            errors.append(f"{q}: {k} must be a number")
    if isinstance(lo, (int, float)) and isinstance(hi, (int, float)) and lo > hi:
        errors.append(f"{q}: min {lo} is above max {hi}")
    for k in extra:
        v = spec.get(k)
        if v is not None and (not isinstance(v, int) or isinstance(v, bool) or v < 1):
            errors.append(f"{q}: {k} must be a whole number of at least 1")


# ---------------------------------------------------------------- the ids lock

LOCK_DOC = ("Every registry and field name Kiln's registry schemas have published, with its numeric id (schema.g.h's field "
            "ids), the version that published it and its contract (kind, applies, ops, read-only, range, values), and each "
            "registry's version history. Names and ids are never reused: a field that leaves its schema moves to \"retired\" "
            "(registrygen.py retire, only in a breaking step). Within one breaking line (0.<minor> while experimental, <major> "
            "from 1.0.0) a field may only be added, widen its range, append enum values or apply earlier; anything else needs "
            "the next breaking version and schema/registry/migrations/<registry>-<from>-<to>.json. Written by "
            "tools/registrygen/registrygen.py lock; never edit it by hand.")
APPLIES_RANK = {"now": 0, "worldLoad": 1, "restart": 2}


def read_lock(root):
    p = Path(root) / LOCK
    if not p.is_file():
        return {"format": 1, "registries": {}, "retiredRegistries": {}}
    return json.loads(p.read_text(encoding="utf-8"))


def write_lock(root, lock):
    p = Path(root) / LOCK
    p.parent.mkdir(parents=True, exist_ok=True)
    regs = lock.get("registries", {})
    regs = {k: regs[k] for k in sorted(regs, key=lambda k: regs[k]["id"])}
    for lr in regs.values():
        lr["fields"] = dict(sorted(lr.get("fields", {}).items(), key=lambda kv: kv[1]["id"]))
        lr["retired"] = dict(sorted(lr.get("retired", {}).items(), key=lambda kv: kv[1]["id"]))
    out = {"format": 1, "doc": LOCK_DOC, "registries": regs, "retiredRegistries": lock.get("retiredRegistries", {})}
    p.write_text(json.dumps(out, indent=2) + "\n", encoding="utf-8")


def breaking_key(v):
    """The part of a version a breaking change must raise: the minor while 0.x (experimental), the major from 1.0.0."""
    t = vtuple(v)
    return (t[0],) if t[0] >= 1 else (0, t[1])


def next_breaking(v):
    t = vtuple(v)
    return f"{t[0] + 1}.0.0" if t[0] >= 1 else f"0.{t[1] + 1}.0"


def is_breaking_step(was, ver):
    return breaking_key(ver) > breaking_key(was)


def contract(row):
    """What a field promises (kept in ids.lock.json): kind, applies, ops, read-only, range, values, list shape."""
    s, el = row.spec, scalar_spec(row)
    c = {"kind": row.kind, "applies": row.applies, "ops": row.ops, "readOnly": row.read_only}
    if row.kind == "list":
        c["element"] = row.element
    for k in ("min", "max"):
        if el.get(k) is not None:
            c[k] = el[k]
    if el.get("type") in ("enum", "flags"):
        c["values"] = list(el.get("values", []))
    if s.get("optional"):
        c["optional"] = True
    for k in ("maxItems", "fixedLength", "lengthField", "lengthOffset"):
        if s.get(k) is not None:
            c[k] = s[k]
    return c


CONTRACT_KEYS = ("kind", "applies", "ops", "readOnly", "element", "min", "max", "values", "optional", "maxItems",
                 "fixedLength", "lengthField", "lengthOffset")


def contract_change(old, new):
    """(additive changes, breaking changes) between two contracts of one field, in words."""
    add, brk = [], []
    for k in CONTRACT_KEYS:
        a, b = old.get(k), new.get(k)
        if a == b:
            continue
        if k == "min" and a is not None and b is not None and b < a:
            add.append(f"min {a} -> {b}")
        elif k == "max" and a is not None and b is not None and b > a:
            add.append(f"max {a} -> {b}")
        elif k == "maxItems" and a is not None and b is not None and b > a:
            add.append(f"maxItems {a} -> {b}")
        elif k == "values" and a is not None and b is not None and b[:len(a)] == a:
            add.append(f"values + {', '.join(b[len(a):])}")
        elif k == "applies" and a in APPLIES_RANK and b in APPLIES_RANK and APPLIES_RANK[b] < APPLIES_RANK[a]:
            add.append(f"applies {a} -> {b}")
        else:
            brk.append(f"{k} {a!r} -> {b!r}")
    return add, brk


def migration_errors(root, name, frm, to, old_names, new_names):
    rel = f"migrations/{name}-{frm}-{to}.json"
    p = Path(root) / SCHEMA_DIR / rel
    if not p.is_file():
        return [f"{name}: the breaking step {frm} -> {to} ships schema/registry/{rel} (ids and fields old -> new)"]
    try:
        m = json.loads(p.read_text(encoding="utf-8"))
    except ValueError as e:
        return [f"{name}: schema/registry/{rel} is not JSON ({e})"]
    errs = []
    if not isinstance(m, dict) or m.get("registry") != name or m.get("from") != frm or m.get("to") != to \
            or not isinstance(m.get("ids"), dict) or not isinstance(m.get("fields"), dict):
        return [f"{name}: schema/registry/{rel} is {{\"registry\": \"{name}\", \"from\": \"{frm}\", \"to\": \"{to}\", \"ids\": {{old: new}}, \"fields\": {{old: new}}}}"]
    for old, new in m["ids"].items():
        for i in (old, new):
            if not isinstance(i, str) or not KILN_ID.match(i):
                errs.append(f"{name}: schema/registry/{rel}: {i!r} is not a Kiln id (lower case <namespace>:<name>)")
    for old, new in m["fields"].items():
        if old not in old_names:
            errs.append(f"{name}: schema/registry/{rel}: field {old!r} was not published before {to}")
        if new not in new_names:
            errs.append(f"{name}: schema/registry/{rel}: field {old!r} -> {new!r}: the schema {to} has no field {new!r}")
    return errs


def transition(model, lock):
    """(refusals, pending): what the schemas may not do to the lock, and what 'lock' still has to record."""
    refuse, pending = list(model.errors), []
    regs = lock.get("registries", {})
    for name in sorted(lock.get("retiredRegistries", {})):
        if name in model.registries:
            refuse.append(f"{name}: the registry name was retired and is never reused")
    for name in sorted(regs):
        if name not in model.registries:
            refuse.append(f"{name}: in ids.lock.json but no schema file: a registry is never removed, only retired")
    for name, reg in model.registries.items():
        lr = regs.get(name)
        if lr is None:
            pending.append(f"{name}: the registry is not in ids.lock.json (run tools/registrygen/registrygen.py lock)")
            pending += [f"{r.qualified}: not in ids.lock.json (run tools/registrygen/registrygen.py lock)" for r in reg.rows]
            continue
        fields, retired = lr.get("fields", {}), lr.get("retired", {})
        was, ver = lr.get("version", "0.0.0"), reg.version
        history = lr.get("history", [{"version": was}])
        old_names = set(fields) | set(retired)
        new_names = {r.path for r in reg.rows}
        # every breaking step of the history ships its migration
        for a, b in zip(history, history[1:]):
            if b.get("breaking"):
                refuse += migration_errors(model.root, name, a["version"], b["version"], old_names, old_names | new_names)
        if vtuple(ver) < vtuple(was):
            refuse.append(f"{name}: version {ver} is below the published {was}")
            continue
        step = is_breaking_step(was, ver)
        if step:
            refuse += migration_errors(model.root, name, was, ver, old_names, new_names)
        changed = False
        for r in reg.rows:
            if r.path in retired:
                refuse.append(f"{r.qualified}: the name was retired in {retired[r.path].get('retiredIn')} and is never reused")
            elif r.path not in fields:
                changed = True
                pending.append(f"{r.qualified}: not in ids.lock.json (run tools/registrygen/registrygen.py lock)")
            else:
                old = {k: v for k, v in fields[r.path].items() if k in CONTRACT_KEYS}
                add, brk = contract_change(old, contract(r))
                if brk and not step:
                    refuse.append(f"{r.qualified}: {'; '.join(brk)} is a breaking change: it needs version "
                                  f"{next_breaking(was)} and a migration (now {ver})")
                if add or brk:
                    changed = True
                    pending.append(f"{r.qualified}: changed ({'; '.join(add + brk)}): run tools/registrygen/registrygen.py lock")
        for path in sorted(fields):
            if path not in new_names:
                refuse.append(f"{name}.{path}: in ids.lock.json but not in the schema: removed without being retired "
                              f"(only in a breaking step: registrygen.py retire {name}.{path} {next_breaking(was) if not step else ver})")
        if changed and ver == was:
            refuse.append(f"{name}: the schema changed ({', '.join(sorted(set(p.split(':')[0] for p in pending if p.startswith(name + '.'))))}) "
                          f"without a version bump (now {ver})")
        elif ver != was and not changed and not step:
            pending.append(f"{name}: version {ver} is not recorded (run tools/registrygen/registrygen.py lock)")
        elif step:
            pending.append(f"{name}: the breaking step {was} -> {ver} is not recorded (run tools/registrygen/registrygen.py lock)")
    return refuse, pending


def lock_errors(model, lock):
    refuse, pending = transition(model, lock)
    return refuse + pending


def update_lock(root):
    m = load(root)
    lock = read_lock(root)
    refuse, _ = transition(m, lock)
    if refuse:
        raise LockError("ids.lock.json is not changed:\n  " + "\n  ".join(refuse))
    regs = lock.setdefault("registries", {})
    lock.setdefault("retiredRegistries", {})
    used_reg = [r["id"] for r in regs.values()] + [r.get("id", 0) for r in lock["retiredRegistries"].values()]
    for name, reg in m.registries.items():
        lr = regs.get(name)
        if lr is None:
            nid = max(used_reg, default=0) + 1
            used_reg.append(nid)
            lr = regs[name] = {"id": nid, "version": reg.version, "since": reg.version,
                               "history": [{"version": reg.version}], "fields": {}, "retired": {}}
        fields, retired = lr.setdefault("fields", {}), lr.setdefault("retired", {})
        history = lr.setdefault("history", [{"version": lr.get("version", reg.version)}])
        used = [f["id"] for f in fields.values()] + [f["id"] for f in retired.values()]
        for r in reg.rows:
            if r.path not in fields:
                nid = max(used, default=0) + 1
                used.append(nid)
                fields[r.path] = {"id": nid, "since": reg.version}
            entry = {"id": fields[r.path]["id"], "since": fields[r.path]["since"]}
            entry.update(contract(r))
            fields[r.path] = entry
        if reg.version != lr.get("version"):
            step = is_breaking_step(lr.get("version", "0.0.0"), reg.version)
            history.append({"version": reg.version, **({"breaking": True} if step else {})})
            lr["version"] = reg.version
    write_lock(root, lock)
    return read_lock(root)


def retire(root, qualified, version):
    name, _, path = qualified.partition(".")
    lock = read_lock(root)
    lr = lock.get("registries", {}).get(name)
    if lr is None or path not in lr.get("fields", {}):
        raise LockError(f"{qualified}: not a published field")
    was = lr.get("version", "0.0.0")
    history = lr.get("history", [{"version": was}])
    same_step = version == was and history and history[-1].get("breaking") and history[-1]["version"] == version
    if not (is_breaking_step(was, version) or same_step):
        raise LockError(f"{qualified}: a field is retired only in a breaking step: version {next_breaking(was)} "
                        f"(with its migration), not {version}")
    f = lr["fields"].pop(path)
    lr.setdefault("retired", {})[path] = {"id": f["id"], "since": f["since"], "retiredIn": version}
    write_lock(root, lock)


def field_id(lock, row):
    lr = lock["registries"][row.registry]
    return (lr["id"] << 16) | lr["fields"][row.path]["id"]


def ordered(model, lock):
    return [model.registries[n] for n in sorted(model.registries, key=lambda n: lock["registries"][n]["id"])]


# ---------------------------------------------------------------- shared helpers

def num_text(v):
    return str(int(v)) if isinstance(v, (int, float)) and float(v).is_integer() else str(v)


def range_text(spec):
    lo, hi = spec.get("min"), spec.get("max")
    if lo is None and hi is None:
        return ""
    return f" {num_text(lo) if lo is not None else ''}..{num_text(hi) if hi is not None else ''}"


def scalar_spec(row):
    """The spec that holds a row's values, range and unit (a list's element, else the row's own)."""
    if row.kind == "list" and row.element != "struct":
        return row.spec["of"]
    return row.spec


def type_text(row):
    s = row.spec
    if row.kind == "list":
        of = s["of"]
        el = of["type"]
        if el == "ref":
            el = f"ref {of.get('registry')}"
        elif el in ("enum", "flags"):
            el = f"{el} ({', '.join(of.get('values', []))})"
        elif el == "struct":
            el = "entries"
        else:
            el = el + range_text(of)
        if s.get("fixedLength"):
            return f"list of {el}, exactly {s['fixedLength']}"
        if s.get("lengthField"):
            off = int(s.get("lengthOffset") or 0)
            return f"list of {el}, as long as {s['lengthField']}" + (f" + {off}" if off else "")
        return f"list of {el}" + (f", at most {s['maxItems']}" if s.get("maxItems") else "")
    if row.kind == "ref":
        return f"ref {s.get('registry')}" + (", optional" if s.get("optional") else "")
    if row.kind in ("enum", "flags"):
        return f"{row.kind} ({', '.join(s.get('values', []))})"
    unit = f" {s['unit']}" if s.get("unit") else ""
    return f"{row.kind}{unit}{range_text(s)}"


def inherited_formula(reg, row):
    """A struct's formula (items.armor: gear.armor) holds for its leaves."""
    node = reg.doc["fields"]
    found = ""
    for p in row.path.replace("[]", "").split("."):
        spec = node.get(p) if isinstance(node, dict) else None
        if not isinstance(spec, dict):
            break
        found = spec.get("formula", found)
        node = spec.get("fields") or (spec.get("of") or {}).get("fields") or {}
    return found


def cstr(s):
    return json.dumps(s or "")


def cnum(v):
    if v is None:
        return "0.0"
    r = repr(float(v))
    return r if ("e" in r or "." in r) else r + ".0"


def ident(path):
    return path.replace("[]", "").replace(".", "_")


# ---------------------------------------------------------------- native

NATIVE_KIND = {"int": "Int", "float": "Float", "bool": "Bool", "enum": "Enum", "flags": "Flags", "ref": "Ref",
               "time": "Time", "list": "List", "struct": "Struct"}


def migration_steps(model, lock):
    """The id migrations of every registry's breaking steps in ids.lock.json's history, oldest first per registry:
    (registry, from, to, {"ids": {old: new}, "fields": {old: new}}). kiln.content applies them to edits (schema.g.h kRenames)."""
    steps = []
    for reg in ordered(model, lock):
        lr = lock.get("registries", {}).get(reg.name, {})
        history = lr.get("history", [{"version": lr.get("version", reg.version)}])
        for a, b in zip(history, history[1:]):
            if not b.get("breaking"):
                continue
            p = Path(model.root) / SCHEMA_DIR / f"migrations/{reg.name}-{a['version']}-{b['version']}.json"
            if p.is_file():
                m = json.loads(p.read_text(encoding="utf-8"))
                steps.append((reg.name, a["version"], b["version"], m))
    return steps


def render_native(model, lock):
    out = ["// SPDX-License-Identifier: MIT",
           "// Generated by tools/registrygen/registrygen.py from schema/registry/*.json and ids.lock.json. Do not edit.",
           "// The registries' Kiln fields for kiln.content's registry stage: one row per field (a scalar leaf, a list, a leaf",
           "// of a list's elements \"inputs[].count\"), with its permanent id ((registry id << 16) | field id, never reused),",
           "// type, range, ops and applies. schema_ir.h turns it into the edit IR's descriptor (ir.h).",
           "#pragma once",
           "#include <cstddef>",
           "#include <cstdint>",
           "",
           "namespace kc::registry::gen {",
           "",
           "enum class Kind : uint8_t { Int, Float, Bool, Enum, Flags, Ref, Time, List, Struct };",
           "enum class Applies : uint8_t { Now = 1, WorldLoad = 2, Restart = 3 };  // KILN_REGISTRY_APPLIES_*",
           "enum class Ops : uint8_t { None, Set, Number, List, Indexed };  // Indexed: a list of fixed length, edited per element",
           "enum class Side : uint8_t { Both, Server, Client };",
           "",
           "struct Field {",
           "    uint32_t id;           // permanent: (registry id << 16) | field id (schema/registry/ids.lock.json)",
           "    const char* registry;",
           "    const char* path;      // \"maxStack\", \"damage.fire\", \"inputs\", \"inputs[].count\"",
           "    Kind kind;",
           "    Kind element;          // a list: its elements' kind (else the same as kind)",
           "    bool has_min, has_max;",
           "    double min, max;       // a number's (a list of numbers: its elements') range",
           "    uint32_t max_items;    // a list: at most this many (0: no limit)",
           "    const char* values;    // enum / flags (or a list's): Kiln names joined by '|'",
           "    const char* ref;       // ref (or a list of refs): the registry",
           "    const char* list;      // a leaf of a list's elements: the list's path",
           "    bool read_only;",
           "    Applies applies;",
           "    Ops ops;",
           "    const char* unit;",
           "    const char* formula;",
           "    bool optional;         // a ref that may be absent (cleared)",
           "    uint32_t fixed_length; // a list of exactly this many elements (0: see length_field, or not fixed)",
           "    const char* length_field;  // a list as long as this field's value + length_offset (\"\": none)",
           "    int32_t length_offset;",
           "    Side side = Side::Both;  // which game process reads the field",
           '    const char* joined_client = "unknown";  // additive schema metadata, not local applies',
           "};",
           "",
           "struct Registry {",
           "    uint16_t id;",
           "    const char* name;",
           "    const char* version;",
           "    bool experimental;",
           "    bool add;",
           "    const char* remove;    // \"\" (entries cannot be removed), \"hidden\", \"disabled\"",
           "    bool raw;",
           "    size_t first, count;   // its rows in kFields",
           "};",
           "",
           "inline constexpr uint32_t kRegistrySchema = 1;",
           ""]
    rows, regs = [], []
    for reg in ordered(model, lock):
        first = len(rows)
        rows += [(reg, r) for r in reg.rows]
        d = reg.doc
        regs.append(f"    {{{lock['registries'][reg.name]['id']}, {cstr(reg.name)}, {cstr(reg.version)}, "
                    f"{'true' if d.get('stability') == 'experimental' else 'false'}, "
                    f"{'true' if d['entry'].get('add') else 'false'}, {cstr(d['entry'].get('remove') or '')}, "
                    f"{'true' if d.get('raw') == 'allowed' else 'false'}, {first}, {len(reg.rows)}}},")
    out.append("inline constexpr Registry kRegistries[] = {")
    out += regs
    out += ["};", "", "inline constexpr Field kFields[] = {"]
    for reg, r in rows:
        s, el = r.spec, scalar_spec(r)
        values = "|".join(el.get("values", [])) if el.get("type") in ("enum", "flags") else ""
        ref = el.get("registry", "") if el.get("type") == "ref" else ""
        side = s.get("side", "both")
        side_init = f", Side::{pascal(side)}, {cstr(s.get('joinedClient', 'unknown'))}"
        out.append(f"    {{0x{field_id(lock, r):08x}u, {cstr(r.registry)}, {cstr(r.path)}, Kind::{NATIVE_KIND[r.kind]}, "
                   f"Kind::{NATIVE_KIND[r.element or r.kind]}, "
                   f"{'true' if el.get('min') is not None else 'false'}, {'true' if el.get('max') is not None else 'false'}, "
                   f"{cnum(el.get('min'))}, {cnum(el.get('max'))}, {int(s.get('maxItems') or 0)}u, {cstr(values)}, {cstr(ref)}, "
                   f"{cstr(r.list_path)}, {'true' if r.read_only else 'false'}, Applies::{pascal(r.applies)}, "
                   f"Ops::{pascal(r.ops)}, {cstr(el.get('unit'))}, {cstr(s.get('formula') or inherited_formula(reg, r))}, "
                   f"{'true' if s.get('optional') else 'false'}, {int(s.get('fixedLength') or 0)}u, {cstr(s.get('lengthField'))}, "
                   f"{int(s.get('lengthOffset') or 0)}{side_init}}},")
    out += ["};", "",
            "// The id migrations of the schemas' breaking steps (schema/registry/migrations/<registry>-<from>-<to>.json), oldest",
            "// first per registry: kiln.content reads an edit naming an old field or Kiln id as the new one before it is matched",
            "// (ir.cpp parse_batch). The last row is empty (registry nullptr).",
            "struct Rename {",
            "    const char* registry;",
            "    const char* from;      // the breaking step's versions",
            "    const char* to;",
            "    bool field;            // a field path (else a Kiln id)",
            "    const char* old_name;",
            "    const char* new_name;",
            "};",
            "",
            "inline constexpr Rename kRenames[] = {"]
    for name, frm, to, m in migration_steps(model, lock):
        for kind, renames in (("true", m.get("fields", {})), ("false", m.get("ids", {}))):
            for old in sorted(renames):
                out.append(f"    {{{cstr(name)}, {cstr(frm)}, {cstr(to)}, {kind}, {cstr(old)}, {cstr(renames[old])}}},")
    out += ["    {nullptr, nullptr, nullptr, false, nullptr, nullptr},",
            "};", "", "// Field ids by name: <registry>_<path> with '.' as '_' and '[]' left out.", "namespace field {"]
    for reg, r in rows:
        out.append(f"inline constexpr uint32_t {r.registry}_{ident(r.path)} = 0x{field_id(lock, r):08x}u;")
    out += ["}  // namespace field", "", "}  // namespace kc::registry::gen"]
    return "\n".join(out) + "\n"


# ---------------------------------------------------------------- C#

CS_SCALAR = {"int": "int", "float": "double", "bool": "bool", "time": "global::System.TimeSpan", "ref": "RegistryId"}
CS_GET = {"int": "Int", "float": "Double", "bool": "Bool", "time": "Time", "ref": "Id"}
CS_KIND = {"int": "Int", "float": "Float", "bool": "Bool", "enum": "Enum", "flags": "Flags", "ref": "Ref", "time": "Time",
           "list": "List"}


def experimental(reg):
    d = reg.doc
    if d.get("stability") != "experimental":
        return ""
    return (f'[global::System.Diagnostics.CodeAnalysis.Experimental("{d["experimentalId"]}", '
            f'UrlFormat = "https://github.com/frostdev-ops/kiln/blob/main/docs/kiln/modding/registries/{reg.name}.md")]')


def xml(s):
    return (s or "").replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;")


def summary(text, indent):
    return [f"{indent}/// <summary>{xml(text)}</summary>"] if text else []


class CsWriter:
    def __init__(self, model, lock, reg):
        self.lock, self.reg = lock, reg
        self.rows = {r.path: r for r in reg.rows}
        self.types, self.enums = [], []
        self.fields_class = f"{reg.type_name}Fields"

    def fid(self, row):
        return f"{self.fields_class}.{pascal(row.path.replace('[]', ' ').replace('.', ' '))}"

    def prop(self, row, name, indent):
        marker = f', JoinedClient = "{row.spec["joinedClient"]}"' if "joinedClient" in row.spec else ""
        attr = f'{indent}[RegistryField("{row.qualified}", RegistryApplies.{pascal(row.applies)}{marker})]'
        doc = summary(row.spec.get("doc"), indent)
        fid = self.fid(row)
        if row.kind == "list":
            of = row.spec["of"]
            if of["type"] == "struct":
                el = of.get("typeName") or self.reg.type_name + pascal(row.path) + "Item"
                leaves = self.fields_class + "." + pascal(row.path) + "Element"
                body = (f"public RegistryEntryList<{el}> {name} => new(_c, {fid}, static (c, i) => new {el}(c, i), "
                        f"static (c, i) => RegistryCells.Snapshot(c, i, {leaves}));")
                return doc + [attr, indent + body]
            if of["type"] in ("enum", "flags"):
                t, get = "string", f"RegistryCells.Text(c, {fid}, i)"
            else:
                t, get = CS_SCALAR[of["type"]], f"RegistryCells.{CS_GET[of['type']]}(c, {fid}, i)"
            if row.ops == "indexed":
                body = (f"public RegistryFixedList<{t}> {name} => new(_c, {fid}, static (c, i) => {get}, "
                        f"static (c, i, v) => RegistryCells.Set(c, {fid}, i, v));")
            elif row.read_only:
                body = f"public global::System.Collections.Generic.IReadOnlyList<{t}> {name} => new RegistryValueList<{t}>(_c, {fid}, static (c, i) => {get});"
            else:
                body = f"public RegistryValueList<{t}> {name} => new(_c, {fid}, static (c, i) => {get});"
            return doc + [attr, indent + body]
        if row.kind in ("enum", "flags"):
            t = row.enum_type
            names = f"{t}Names.All"
            if row.kind == "enum":
                getter = f"({t})RegistryCells.Enum(_c, {fid}, _i, {names})"
                setter = f"RegistryCells.SetEnum(_c, {fid}, _i, {names}, (int)value)"
            else:
                getter = f"({t})RegistryCells.Flags(_c, {fid}, _i, {names})"
                setter = f"RegistryCells.SetFlags(_c, {fid}, _i, {names}, (uint)value)"
            self.enum(t, row.spec.get("values", []), row.kind == "flags", row.spec.get("doc"))
        elif row.kind == "ref" and row.spec.get("optional"):
            t = "RegistryId?"
            getter = f"RegistryCells.OptionalId(_c, {fid}, _i)"
            setter = f"RegistryCells.SetOptional(_c, {fid}, _i, value)"
        else:
            t = CS_SCALAR[row.kind]
            getter = f"RegistryCells.{CS_GET[row.kind]}(_c, {fid}, _i)"
            setter = f"RegistryCells.Set(_c, {fid}, _i, value)"
        if row.read_only:
            return doc + [attr, f"{indent}public {t} {name} => {getter};"]
        return doc + [attr, f"{indent}public {t} {name} {{ get => {getter}; set => {setter}; }}"]

    def enum(self, name, values, flags, doc):
        if any(n == name for n, _ in self.enums):
            return
        lines = summary(doc, "")
        ex = experimental(self.reg)
        if ex:
            lines.append(ex)
        if flags:
            lines += ["[global::System.Flags]", f"public enum {name} : uint", "{",
                      "    /// <summary>None of them.</summary>", "    None = 0,"]
            for i, v in enumerate(values):
                lines += [f"    /// <summary>{v}</summary>", f"    {pascal(v)} = 1u << {i},"]
        else:
            lines += [f"public enum {name}", "{"]
            for i, v in enumerate(values):
                lines += [f"    /// <summary>{v}</summary>", f"    {pascal(v)} = {i},"]
        lines += ["}", "", f"internal static class {name}Names", "{",
                  f"    internal static readonly string[] All = {{ {', '.join(json.dumps(v) for v in values)} }};", "}"]
        self.enums.append((name, lines))

    def struct_class(self, name, children, doc, element):
        lines = summary(doc, "")
        ex = experimental(self.reg)
        if ex:
            lines.append(ex)
        lines += [f"public sealed partial class {name}", "{",
                  "    private readonly IRegistryCells _c;",
                  "    private readonly int _i;",
                  f"    internal {name}(IRegistryCells cells, int index) {{ _c = cells; _i = index; }}"]
        if element:
            lines += ["", "    /// <summary>The element's index in its list.</summary>", "    public int Index => _i;"]
        lines += self.members(children, "    ")
        lines.append("}")
        self.types.append((name, lines))

    def members(self, children, indent):
        out = []
        for key, spec, row, sub in children:
            out.append("")
            if row is not None:
                out += self.prop(row, pascal(key), indent)
            else:
                tname = spec.get("typeName") or self.reg.type_name + pascal(key)
                self.struct_class(tname, sub, spec.get("doc"), False)
                out += summary(spec.get("doc"), indent)
                out.append(f"{indent}public {tname} {pascal(key)} => new(_c, _i);")
        return out

    def tree(self, fields, prefix):
        out = []
        for key, spec in fields.items():
            path = prefix + key
            if spec.get("type") == "struct":
                out.append((key, spec, None, self.tree(spec.get("fields", {}), path + ".")))
                continue
            out.append((key, spec, self.rows.get(path), None))
            if spec.get("type") == "list" and spec["of"].get("type") == "struct":
                el = spec["of"].get("typeName") or self.reg.type_name + pascal(key) + "Item"
                self.struct_class(el, self.tree(spec["of"].get("fields", {}), path + "[]."), spec.get("doc"), True)
        return out

    def render(self):
        reg = self.reg
        entry = f"{reg.type_name}Entry"
        body = self.members(self.tree(reg.doc["fields"], ""), "    ")
        lines = [CS_HEADER.format(src=reg.src).rstrip("\n"), "#nullable enable", "namespace Kiln.Registry;", ""]
        lines += summary(f"An entry of the {reg.name} registry. {reg.doc.get('doc')}", "")
        ex = experimental(reg)
        if ex:
            lines.append(ex)
        lines += [f"public sealed partial class {entry} : IRegistryEntry", "{",
                  "    private readonly IRegistryCells _c;",
                  "    private const int _i = -1;",
                  f"    internal {entry}(RegistryId id, IRegistryCells cells) {{ Id = id; _c = cells; }}",
                  "",
                  "    /// <summary>The entry's Kiln id (game:&lt;stable name&gt; or &lt;mod id&gt;:&lt;name&gt;).</summary>",
                  "    public RegistryId Id { get; }",
                  "",
                  "    /// <summary>The registry's name.</summary>",
                  f"    public string Registry => \"{reg.name}\";"]
        lines += body
        lines.append("}")
        for _, t in self.types:
            lines += [""] + t
        for _, t in self.enums:
            lines += [""] + t
        lines += ["", f"internal static class {self.fields_class}", "{"]
        for r in reg.rows:
            lines.append(f"    internal const int {pascal(r.path.replace('[]', ' ').replace('.', ' '))} = 0x{field_id(self.lock, r):08x};")
        for r in reg.rows:
            if r.kind == "list" and r.element == "struct":
                leaves = [x for x in reg.rows if x.list_path == r.path]
                items = ", ".join(f'("{x.path[len(r.path) + 3:]}", {pascal(x.path.replace("[]", " ").replace(".", " "))})' for x in leaves)
                lines.append(f"    internal static readonly (string Field, int Id)[] {pascal(r.path)}Element = {{ {items} }};")
        lines.append("}")
        return "\n".join(lines) + "\n"


def render_cs_catalog(model, lock):
    lines = [CS_HEADER.format(src="*.json").rstrip("\n"), "#nullable enable", "namespace Kiln.Registry;", "",
             "public static partial class RegistrySchema", "{",
             "    /// <summary>The registry schema format (registrySchema).</summary>",
             "    public const int Format = 1;", "",
             "    private static readonly RegistryFieldSpec[] s_fields =", "    {"]
    vers = []

    def dn(v):
        return "null" if v is None else cnum(v)

    for reg in ordered(model, lock):
        vers.append(f'        ["{reg.name}"] = "{reg.version}",')
        for r in reg.rows:
            s, el = r.spec, scalar_spec(r)
            values = el.get("values", []) if el.get("type") in ("enum", "flags") else []
            vals = "null" if not values else "new[] { " + ", ".join(json.dumps(v) for v in values) + " }"
            ref = json.dumps(el.get("registry")) if el.get("type") == "ref" else "null"
            unit = json.dumps(el.get("unit")) if el.get("unit") else "null"
            formula = s.get("formula") or inherited_formula(reg, r)
            elk = f"RegistryFieldType.{CS_KIND[r.element]}" if r.element and r.element != "struct" else "null"
            if r.element == "struct":
                elk = "RegistryFieldType.Struct"
            lines.append(f"        new(0x{field_id(lock, r):08x}, \"{r.qualified}\", RegistryFieldType.{CS_KIND[r.kind]}, {elk}, "
                         f"RegistryApplies.{pascal(r.applies)}, RegistryOps.{pascal(r.ops)}, {dn(el.get('min'))}, {dn(el.get('max'))}, "
                         f"{int(s.get('maxItems') or 0)}, {vals}, {ref}, {unit}, {json.dumps(formula) if formula else 'null'}, "
                         f"{'true' if r.read_only else 'false'}, {'true' if s.get('optional') else 'false'}, {int(s.get('fixedLength') or 0)}, "
                         f"{json.dumps(s['lengthField']) if s.get('lengthField') else 'null'}, {int(s.get('lengthOffset') or 0)}, "
                         f"\"{lock['registries'][reg.name]['fields'][r.path]['since']}\") {{ JoinedClient = \"{s.get('joinedClient', 'unknown')}\" }},")
    lines += ["    };", "",
              "    private static readonly global::System.Collections.Generic.Dictionary<string, string> s_versions = new()",
              "    {"] + vers + ["    };", "}"]
    return "\n".join(lines) + "\n"


# ---------------------------------------------------------------- the data section's schema

def render_section(model, lock):
    defs = {
        "select": {
            "type": "object", "additionalProperties": False, "required": ["id"],
            "description": "Which entries: an id, a case-insensitive glob over ids (game:material_*), or an old debug-name selector; where: conditions on the entry's fields; has / hasNot: traits.",
            "properties": {"id": {"type": "string", "minLength": 1}, "where": {"type": "object"},
                           "has": {"type": "array", "items": {"type": "string"}},
                           "hasNot": {"type": "array", "items": {"type": "string"}}},
        },
        "op": {
            "description": "A value (set), or relative operations evaluated as data packs evaluate them: r = clamp(v * mul + add, min, max), then raiseOnly / lowerOnly against v.",
            "anyOf": [
                {"type": ["number", "boolean", "string", "array"]},
                {"type": "object", "additionalProperties": False, "minProperties": 1,
                 "properties": {"set": {}, "mul": {"type": "number"}, "add": {"type": "number"}, "min": {"type": "number"},
                                "max": {"type": "number"}, "raiseOnly": {"type": "boolean"}, "lowerOnly": {"type": "boolean"}}},
            ],
        },
        "list": {
            "description": "A list operation: clear, append, remove (values to take out), replace (the whole list).",
            "anyOf": [
                {"const": "clear"},
                {"type": "object", "additionalProperties": False, "minProperties": 1, "maxProperties": 1,
                 "properties": {"clear": {"const": True}, "append": {"type": "array", "minItems": 1},
                                "remove": {"type": "array", "minItems": 1}, "replace": {"type": "array"}}},
            ],
        },
    }
    edits = []
    for reg in ordered(model, lock):
        name = reg.name
        writable = [r.path for r in reg.rows if not r.read_only and r.ops != "indexed"]
        indexed = [re.escape(r.path).replace(r"\[\]", r"\[([0-9]+|\*)\]") for r in reg.rows if not r.read_only and "[]" in r.path]
        indexed += [re.escape(r.path) + r"\[([0-9]+|\*)\]" for r in reg.rows if r.kind == "list" and not r.read_only]
        # a field of fixed length takes only indexed edits: "xpPerLevel[2]"
        rd = {"$defs": {"field": {"enum": writable}}}
        if indexed:
            rd["$defs"]["fieldIndexed"] = {"type": "string", "pattern": "^(" + "|".join(indexed) + ")$"}
            field_ref = {"anyOf": [{"$ref": f"#/$defs/{name}/$defs/field"}, {"$ref": f"#/$defs/{name}/$defs/fieldIndexed"}]}
        else:
            field_ref = {"$ref": f"#/$defs/{name}/$defs/field"}
        e = reg.doc["entry"]
        props = {"registry": {"const": name}, "select": {"$ref": "#/$defs/select"}, "field": field_ref,
                 "op": {"$ref": "#/$defs/op"}, "list": {"$ref": "#/$defs/list"}, "exclusive": {"type": "boolean"},
                 "yield": {"type": "boolean"}}
        if reg.doc.get("raw") == "allowed":
            props["raw"] = {"type": "object", "description": "Advanced tier: raw game paths; refused per entry where the game's names changed."}
        kinds = (["add"] if e.get("add") else []) + (["remove"] if e.get("remove") else [])
        if kinds:
            props["entry"] = {"enum": kinds}
        if e.get("add"):
            # the one id grammar (KILN_ID) for a mod's own entry: not the reserved game: and kiln:, no clash suffix
            props["id"] = {"type": "string", "pattern": "^(?!(game|kiln):)[a-z0-9][a-z0-9_.-]*:[a-z0-9_.-]+$"}
            props["like"] = {"type": "string", "minLength": 1}
            props["fields"] = {"type": "object", "propertyNames": {"$ref": f"#/$defs/{name}/$defs/field"}}
        rd["$defs"]["edit"] = {"type": "object", "additionalProperties": False, "required": ["registry"],
                               "description": f"An edit of the {name} registry (docs/kiln/modding/registries/{name}.md).",
                               "properties": props}
        defs[name] = rd
        edits.append({"$ref": f"#/$defs/{name}/$defs/edit"})
    defs["edit"] = {"oneOf": edits}
    doc = {
        "$schema": "https://json-schema.org/draft/2020-12/schema",
        "$id": "registry-section.schema.json",
        "title": "Kiln data pack \"registry\" section (generated)",
        "description": ("Generated from schema/registry/*.json by tools/registrygen/registrygen.py. Do not edit. Edits of Kiln "
                        "registries by stage: every mod's add, then every mod's alter, then every mod's final (later in load "
                        "order wins and is reported; relative operations stack; exclusive keeps the strict rule; yield applies "
                        "only where no earlier writer set the field). Experimental (KILNX031): read by the registry stage from "
                        "the release that wires it (docs/kiln/modding/registries/README.md)."),
        "type": "object", "additionalProperties": False,
        "properties": {s: {"type": "array", "items": {"$ref": "#/$defs/edit"}} for s in ("add", "alter", "final")},
        "$defs": defs,
    }
    return json.dumps(doc, indent=2) + "\n"


# ---------------------------------------------------------------- docs

APPLIES_TEXT = {"now": "takes effect at the next use", "worldLoad": "takes effect when a world loads",
                "restart": "takes effect after a game restart"}
Q = "\x60"


def code(s):
    return f"{Q}{s}{Q}"


def struct_docs(fields, prefix):
    """(path, doc) of every group of fields (a struct or a list's elements) that has a doc."""
    out = []
    for key, spec in fields.items():
        if not isinstance(spec, dict):
            continue
        if spec.get("type") == "struct":
            if spec.get("doc"):
                out.append((prefix + key, spec["doc"]))
            out += struct_docs(spec.get("fields") or {}, prefix + key + ".")
        elif spec.get("type") == "list" and isinstance(spec.get("of"), dict) and spec["of"].get("type") == "struct":
            out += struct_docs(spec["of"].get("fields") or {}, prefix + key + "[].")
    return out


def regmap_rows(root, registry):
    p = Path(root) / "regmap" / "common" / f"{registry}.json"
    if not p.is_file():
        return None
    try:
        return json.loads(p.read_text(encoding="utf-8")).get("rows") or {}
    except ValueError:
        return None


def mapping_text(rows, path):
    if rows is None:
        return "no regmap yet"
    row = rows.get(path)
    if row is None:
        return "not mapped yet"
    state = row.get("state", "mapped")
    return state if state == "mapped" else f"{state}: {str(row.get('reason', '')).replace('|', '/')}"


def render_doc(model, lock, reg):
    d = reg.doc
    lr = lock["registries"][reg.name]
    stab = (f"experimental ({d['experimentalId']}: may change in any release until it is marked stable)"
            if d.get("stability") == "experimental" else "stable (frozen by the API 1.0.0 contract: additive only)")
    out = [f"# The {code(reg.name)} registry",
           "",
           f"<!-- Generated from schema/registry/{reg.src} by tools/registrygen/registrygen.py. Do not edit. -->",
           "",
           d.get("doc", ""),
           "",
           f"Schema version {reg.version}, {stab}. C# entry type {code('Kiln.Registry.' + reg.type_name + 'Entry')}. "
           "What registries are, the stages, the conflict rules and how ids are made: [README](README.md).",
           "",
           "## Ids",
           "",
           f"- Game entries: {code(d['ids']['game'])}. Mod entries: {code(d['ids']['mod'])}. All ids are lower case. The mod ids "
           f"{' and '.join(code(m) for m in RESERVED_MODS)} are reserved: no mod's entries use them.",
           f"- {d['ids']['rule']}",
           "",
           "## Fields",
           "",
           f"Applies: {code('now')} = {APPLIES_TEXT['now']}; {code('worldLoad')} = {APPLIES_TEXT['worldLoad']}; "
           f"{code('restart')} = {APPLIES_TEXT['restart']}. Ops: {code('number')} = a value or the relative operations "
           f"(mul, add, min, max, raiseOnly, lowerOnly); {code('set')} = a value; {code('list')} = clear, append, remove, "
           f"replace; {code('indexed')} = a list of fixed length, edited one element at a time ({code('xpPerLevel[2]')}) with a "
           "value or a number operation. A field of a list's elements is named with [] in this table and with the element's "
           f"index in an edit ({code('entries[3].chance')}, or [*] for every element). Mapping: whether this Kiln maps the "
           "field onto the game; an unproven or unmapped field is refused per entry, with the reason, until a Kiln update "
           "maps it. Joined client: when a joined Kiln client consumes a server edit; see [README](README.md#what-a-joined-client-sees).",
           ""]
    groups = struct_docs(d.get("fields") or {}, "")
    if groups:
        out += ["Groups of fields:", ""] + [f"- {code(p)}: {doc}" for p, doc in groups] + [""]
    out += ["| Field | Type | Applies | Ops | Since | Mapping | Joined client | About |",
            "|---|---|---|---|---|---|---|---|"]
    rows_map = regmap_rows(model.root, reg.name)
    for r in reg.rows:
        about = r.spec.get("doc") or ""
        f = r.spec.get("formula") or inherited_formula(reg, r)
        if f:
            about = (about + " " if about else "") + f"Formula: {code(f)}."
        if r.spec.get("side") == "client":
            about = (about + " " if about else "") + "Read by game clients only."
        ops = "read-only" if r.read_only else r.ops
        about = about.replace("|", "/")
        out.append(f"| {code(r.path)} | {type_text(r)} | {r.applies} | {ops} | {lr['fields'][r.path]['since']} | "
                   f"{mapping_text(rows_map, r.path)} | {r.spec.get('joinedClient', 'unknown')} | {about} |")
    out.append("")
    traits = d.get("traits") or {}
    if traits:
        out += ["## Traits", "", f"Selectors ask for these with {code('has')} / {code('hasNot')}.", "",
                "| Trait | Means |", "|---|---|"]
        out += [f"| {code(k)} | {v} |" for k, v in traits.items()]
        out.append("")
    e = d["entry"]
    out += ["## Entries", ""]
    out.append(f"- Add (stage add, id {code('<your mod id>:<name>')}): {'yes' if e.get('add') else 'no'}.")
    if e.get("add"):
        out.append("- Add requires GameStart and an explicit present Kiln `like` id; clone chains end at a game entry. "
                   "Kiln retains the stable identity and pins the structural base install-wide. Use a new id for an incompatible base.")
    out.append(f"- Remove ({e['remove']}): {e.get('removeDoc') or 'yes'}" if e.get("remove") else "- Remove: no.")
    out.append(f"- Raw game paths in edits (the Advanced tier): {d.get('raw')}.")
    retired = lr.get("retired") or {}
    if retired:
        out += ["", "## Retired names", "", "Never used again.", ""]
        out += [f"- {code(k)} (since {v.get('since')}, retired in {v.get('retiredIn')})" for k, v in retired.items()]
    return "\n".join(out) + "\n"


# ---------------------------------------------------------------- the analyzers' hint table

def regmap_docs(root):
    out = {}
    folder = Path(root) / "regmap" / "common"
    for p in sorted(folder.glob("*.json")) if folder.is_dir() else []:
        try:
            d = json.loads(p.read_text(encoding="utf-8"))
        except ValueError:
            continue
        if isinstance(d, dict) and d.get("registry") == p.stem:
            out[p.stem] = d
    return out


def entry_root(entries):
    """The root an entry's row paths start at: the resource type, or "<type>/<array>[]" for the elements of an array."""
    kind, rtype = entries.get("kind", "resource"), entries.get("resourceType", "")
    if not rtype or kind == "hooks":
        return None
    if kind == "array":
        return f"{rtype}/{entries.get('array', '')}[]"
    return rtype


def section_drift(root):
    """GAMEDATA_SECTIONS against native gamedata_res.cpp's section table: one line per section that names another type."""
    p = Path(root) / "native" / "services" / "content" / "gamedata_res.cpp"
    if not p.is_file():
        return []
    src = p.read_text(encoding="utf-8")
    found = dict(re.findall(r'\{KILN_GD_[A-Z]+, "([a-z]+)", "([^"]+)"', src))
    out = []
    for section, rtype in GAMEDATA_SECTIONS.items():
        if section in found and found[section] != rtype:
            out.append(f"gamedata section {section} starts at {found[section]} in gamedata_res.cpp, not {rtype}")
    return out


def render_hints(model, root):
    """RegistryHints.g.cs: every old data path a registry field covers, for KILN124 and its fix, and every game function a
    hooks registry's field covers, for KILN125. Made from the schemas and regmap/common, so a new registry is in it once its
    files are; nothing here is kept by hand."""
    docs = regmap_docs(root)
    paths, hooks, sections = [], [], dict(GAMEDATA_SECTIONS)
    for name in sorted(model.registries):
        reg = model.registries[name]
        m = docs.get(name)
        if not m:
            continue
        entries = m.get("entries") or {}
        base = entry_root(entries)
        if base and name in CONTENT_SECTIONS:
            sections[name] = base
        fields = {r.path for r in reg.rows}
        for field, row in sorted((m.get("rows") or {}).items()):
            if field not in fields or "[]" in field or not isinstance(row, dict) or row.get("state") == "unmapped":
                continue
            ps = [p for p in row.get("paths") or [] if isinstance(p, str)]
            if entries.get("kind") == "hooks":
                for fn in re.findall(r"\bsigdb [a-z][a-z0-9_.-]*/([A-Za-z_][A-Za-z0-9_]*)", str(row.get("evidence", ""))):
                    hooks.append((fn.lower(), f"{name}.{field}"))
                continue
            if not base:
                continue
            exact = len(ps) == 1 and row.get("conv") in HINT_EXACT
            for p in ps:
                paths.append((base, p.lower(), f"{name}.{field}", exact))
                if entries.get("kind") == "array":
                    # a resource patch on the registry's resource names the element through the array
                    rtype, arr = base.split("/", 1)
                    paths.append((rtype, f"{arr}.{p}".lower(), f"{name}.{field}", exact))
    paths = sorted(set(paths))
    hooks = sorted(set(hooks))

    def lit(v):
        return "true" if v is True else "false" if v is False else json.dumps(v)

    def table(rows):
        return "".join(f"        ({', '.join(lit(x) for x in r)}),\n" for r in rows)

    regs = [(n, model.registries[n].version) for n in sorted(model.registries)]
    return ("// SPDX-License-Identifier: MIT\n"
            "// Generated by tools/registrygen/registrygen.py from schema/registry/*.json and regmap/common/*.json. Do not edit.\n"
            "// The registry fields that cover a mod's old paths (docs/kiln/modding/analyzers.md, KILN124 and KILN125): Kiln's\n"
            "// part, like regmap/; the analyzers compare what a mod already names (its data pack paths, resource patches, hooked\n"
            "// functions) with it and point at the Kiln field. Paths are lower case, as the game's walker matches them.\n"
            "namespace Kiln.Analyzers;\n\n"
            "internal static class RegistryHints\n{\n"
            "    /// <summary>Every registry of the schema: name and schema version.</summary>\n"
            "    internal static readonly (string Name, string Version)[] Registries = {\n"
            + table(regs) +
            "    };\n\n"
            "    /// <summary>Where an old section's paths start (a game type, or \"&lt;type&gt;/&lt;array&gt;[]\" for an array's elements).</summary>\n"
            "    internal static readonly (string Section, string Root)[] Sections = {\n"
            + table(sorted(sections.items())) +
            "    };\n\n"
            "    /// <summary>(root, member path, Kiln field, exact): exact = the same value one to one (a number or a bool).</summary>\n"
            "    internal static readonly (string Root, string Path, string Field, bool Exact)[] Paths = {\n"
            + table(paths) +
            "    };\n\n"
            "    /// <summary>(game function, Kiln field): a hooks registry's field over the function a mod hooks.</summary>\n"
            "    internal static readonly (string Function, string Field)[] Hooks = {\n"
            + table(hooks) +
            "    };\n}\n")


# ---------------------------------------------------------------- driver

def render(model, lock):
    out = {NATIVE: render_native(model, lock),
           f"{CS_DIR}/RegistrySchema.gen.cs": render_cs_catalog(model, lock),
           SECTION: render_section(model, lock),
           HINTS: render_hints(model, model.root)}
    for reg in ordered(model, lock):
        out[f"{CS_DIR}/{pascal(reg.name)}.gen.cs"] = CsWriter(model, lock, reg).render()
        out[f"{DOCS}/{reg.name}.md"] = render_doc(model, lock, reg)
    return out


def write_outputs(root, outputs):
    for rel, text in outputs.items():
        p = Path(root) / rel
        p.parent.mkdir(parents=True, exist_ok=True)
        if not p.is_file() or p.read_text(encoding="utf-8") != text:
            p.write_text(text, encoding="utf-8")


def stale(root, outputs):
    bad = [rel for rel, text in sorted(outputs.items())
           if not (Path(root) / rel).is_file() or (Path(root) / rel).read_text(encoding="utf-8") != text]
    for d, pat in ((CS_DIR, "*.cs"), (DOCS, "*.md")):
        folder = Path(root) / d
        for p in sorted(folder.glob(pat)) if folder.is_dir() else []:
            rel = f"{d}/{p.name}"
            if rel not in outputs and p.name != "README.md":
                bad.append(rel + " (no schema makes it)")
    return bad


def main(argv=None):
    ap = argparse.ArgumentParser(description="Kiln registry schemas -> generated files")
    ap.add_argument("--root", default=str(Path(__file__).resolve().parents[2]))
    ap.add_argument("--check", action="store_true")
    ap.add_argument("cmd", nargs="*")
    a = ap.parse_args(argv)
    root = Path(a.root)
    try:
        if a.cmd[:1] == ["lock"]:
            update_lock(root)
            print("registrygen: ids.lock.json up to date")
            return 0
        if a.cmd[:1] == ["retire"]:
            if len(a.cmd) != 3:
                print("usage: registrygen.py retire <registry>.<field> <version>", file=sys.stderr)
                return 2
            retire(root, a.cmd[1], a.cmd[2])
            return 0
        if a.cmd:
            print(f"registrygen: unknown command {a.cmd[0]}", file=sys.stderr)
            return 2
    except LockError as e:
        print(f"registrygen: {e}", file=sys.stderr)
        return 1
    model = load(root)
    lock = read_lock(root)
    errs = lock_errors(model, lock)
    if errs:
        for e in errs:
            print(f"registrygen: {e}", file=sys.stderr)
        return 1
    outputs = render(model, lock)
    if a.check:
        bad = stale(root, outputs)
        for b in bad:
            print(f"registrygen: stale: {b} (run tools/registrygen/registrygen.py)", file=sys.stderr)
        if not bad:
            print(f"registrygen: {len(model.registries)} registries, "
                  f"{sum(len(r.rows) for r in model.registries.values())} fields, outputs current")
        return 1 if bad else 0
    write_outputs(root, outputs)
    print(f"registrygen: wrote {len(outputs)} files")
    return 0


if __name__ == "__main__":
    sys.exit(main())
