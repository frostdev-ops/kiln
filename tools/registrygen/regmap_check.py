#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""regmap_check: Kiln's regmap (regmap/) checked offline against the game's types.

    regmap_check.py [--root <repo>]                          against the committed type dumps (sigdb/types/<side>/*.json)
    regmap_check.py [--root <repo>] --exe <a copy of the exe>   against that executable's own reflection (tools/kfc refl;
                                                             the file is read as data, never run)
    regmap_check.py ... --exe <copy> --record --side <server|client> --game-build <b> --steam-build <b>
                                                             write regmap/<side>/<exe sha256>.json from the proof

What it checks: every row's paths resolve from its entry's root type (an array's element, a list's element) to a member
of the kind its conversion needs; an int field's schema range fits its storage type; enum and flags names exist in the
game's enum; a static array holds the schema's maxItems; the routing rule (regmap/README.md); rows name schema fields.
A schema field with no row is listed as unmapped (not an error: it is refused per entry until a row exists). A root type
the type source does not have (the committed dumps hold the ECS closure only, not the resource types) is listed as
absent; --exe checks everything. Without --exe it also checks that every committed verified record (regmap/<side>/*.json)
was proven on this regmap/common (its regmapDigest). Exit 0: no error.
"""
import argparse
import hashlib
import json
import re
import sys
from dataclasses import dataclass, field
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
import registrygen as rg  # noqa: E402
import regmap_ids  # noqa: E402

# reflection kinds (tools/kfc/kfc/refl.py, the same numbers in sigdb/types)
K_BOOL, K_U8, K_S8, K_U16, K_S16, K_U32, K_S32, K_U64, K_S64, K_F32, K_F64 = range(1, 12)
K_ENUM, K_BM8, K_BM16, K_BM32, K_BM64, K_TYPEDEF, K_STRUCT, K_STATIC_ARRAY = range(12, 20)
K_BLOB_ARRAY, K_OBJECT_REF, K_GUID = 24, 28, 29
INT_RANGE = {K_U8: (0, 255), K_S8: (-128, 127), K_U16: (0, 65535), K_S16: (-32768, 32767), K_U32: (0, 4294967295),
             K_S32: (-2147483648, 2147483647), K_U64: (0, 2 ** 64 - 1), K_S64: (-2 ** 63, 2 ** 63 - 1)}
FLOATS = (K_F32, K_F64)
BITMASKS = (K_BM8, K_BM16, K_BM32, K_BM64)
ARRAYS = (K_STATIC_ARRAY, K_BLOB_ARRAY)
READ_RANK = {"now": 0, "worldLoad": 1, "restart": 2}
CONVS = ("int", "float", "bool", "time", "enum", "flags", "ref", "refList", "formula", "impactConfig", "list", "stackCount",
         "inventoryWalk", "unlock", "optionalEnum", "component")
TEMPLATE = "keen::ecs::TemplateResource"  # a template's paths start at one of its components, by type name (Level.dataStorage[0])


class TypeDb:
    """Types by qualified name: {name, kind, size, inner, fields[{name, type}], values[{name}], layout, count}."""

    def __init__(self, types, exe_sha256=""):
        self.by_name = {t["name"]: t for t in types}
        self.exe_sha256 = exe_sha256

    @classmethod
    def from_refl(cls, dump):
        return cls(dump["types"], dump.get("exe_sha256", ""))

    @classmethod
    def from_dump(cls, dump):
        """A committed sigdb/types dump (schema 2)."""
        types = []
        for t in dump["types"]:
            x = {"name": t["qualifiedName"], "kind": t["kind"], "size": t["size"], "inner": t.get("inner"),
                 "fields": t.get("fields", []), "values": t.get("values", []), "layout": None}
            types.append(x)
        db = cls(types, dump.get("exe_sha256", ""))
        for t in db.by_name.values():  # a static array's count from its size
            if t["kind"] == K_STATIC_ARRAY and t.get("inner") in db.by_name and db.by_name[t["inner"]]["size"]:
                t["count"] = t["size"] // db.by_name[t["inner"]]["size"]
        return db

    @classmethod
    def from_exe(cls, exe):
        sys.path.insert(0, str(HERE.parent / "kfc"))
        from kfc import refl  # noqa: E402
        return cls.from_refl(refl.extract(str(exe)))

    def get(self, name):
        return self.by_name.get(name)

    def resolve(self, t):
        for _ in range(16):
            if t is None or t["kind"] != K_TYPEDEF:
                return t
            t = self.by_name.get(t.get("inner"))
        return t

    def fields(self, t):
        out, cur, chain = [], self.resolve(t), []
        while cur is not None and cur["kind"] == K_STRUCT:
            chain.append(cur)
            cur = self.resolve(self.by_name.get(cur.get("inner"))) if cur.get("inner") else None
        for c in reversed(chain):
            out += c.get("fields", [])
        return out

    def member_type(self, type_name, member):
        for f in self.fields(self.get(type_name)):
            if f["name"] == member:
                return self.get(f["type"])
        return None

    def component(self, name):
        """A template's component type by its name without namespaces (Level -> keen::ecs::Level), or None."""
        t = self.get("keen::ecs::" + name) if name and ":" not in name else None
        return t if t is not None and self.resolve(t) is not None and self.resolve(t)["kind"] == K_STRUCT else None

    def path(self, root, dotted):
        """The type at a dotted member path from a root type; (type, error). A part "name[i]" is element i of the array member
        name (a static array's index within its count); from a template root, the first part names a component by its type."""
        t = self.get(root)
        parts = dotted.split(".")
        if root == TEMPLATE and parts and parts[0] and not any(f["name"] == parts[0] for f in self.fields(t)):
            t = self.component(parts[0])
            if t is None:
                return None, f"{dotted}: {parts[0]} is not a component type of the type source"
            parts = parts[1:]
        for part in parts:
            index = None
            if part.endswith("]") and "[" in part:
                part, _, ix = part[:-1].partition("[")
                if not ix.isdigit():
                    return None, f"{dotted}: [{ix}] is not an index"
                index = int(ix)
            r = self.resolve(t)
            if r is None or r["kind"] != K_STRUCT:
                return None, f"{dotted}: {t['name'] if t else '?'} has no members"
            nxt = None
            for f in self.fields(r):
                if f["name"] == part:
                    nxt = self.get(f["type"])
                    if nxt is None:
                        return None, f"{dotted}: the type of {part} ({f['type']}) is not in the type source"
                    break
            if nxt is None:
                return None, f"{dotted}: {r['name']} has no member {part}"
            t = nxt
            if index is not None:
                a = self.resolve(t)
                if a is None or a["kind"] not in ARRAYS:
                    return None, f"{dotted}: {part} is not an array"
                if a["kind"] == K_STATIC_ARRAY and a.get("count") is not None and index >= a["count"]:
                    return None, f"{dotted}: {part} has {a['count']} elements, not {index + 1}"
                t = self.element(a)
                if t is None:
                    return None, f"{dotted}: the element type of {part} is not in the type source"
        return t, None

    def element(self, t):
        r = self.resolve(t)
        if r is None or r["kind"] not in ARRAYS:
            return None
        return self.get(r.get("inner"))


@dataclass
class Result:
    errors: list = field(default_factory=list)
    unmapped: list = field(default_factory=list)
    absent: list = field(default_factory=list)
    resolved: int = 0
    rows: dict = field(default_factory=dict)       # qualified field -> "ok" | "unmapped"
    layouts: dict = field(default_factory=dict)    # root type -> layout hash


def common_digest(root):
    """kilnctl regproof's regmapDigest of regmap/common (regmap/README.md): sha256 over "<file name>\n<sha256 of its
    bytes>\n" for each regular *.json file, in name order; kiln.content computes the same at load."""
    files = sorted((p for p in (Path(root) / "regmap" / "common").glob("*.json") if p.is_file()), key=lambda p: p.name.encode())
    return hashlib.sha256("".join(f"{p.name}\n{hashlib.sha256(p.read_bytes()).hexdigest()}\n" for p in files).encode()).hexdigest()


def record_problems(root):
    """The committed build records that kiln.content would not enable (review3 S3): a verified record whose regmapDigest is
    missing or is not this tree's regmap/common's. Such a record turns every registry edit off on its build, so an edit of
    regmap/common needs a new proof (kilnctl regproof) and a new client mirror before it ships."""
    out = []
    digest = common_digest(root)
    for side in ("server", "client"):
        for p in sorted((Path(root) / "regmap" / side).glob("*.json")):
            name = f"regmap/{side}/{p.name}"
            try:
                rec = json.loads(p.read_text(encoding="utf-8"))
            except (OSError, ValueError) as e:
                out.append(f"{name}: not readable JSON ({e})")
                continue
            if "markedLocally" in rec:
                out.append(f"{name}: an instance's copy (markedLocally) was committed; commit kilnctl regproof's --out copy "
                           "(verified-regmap-*.json), or kiln.content tells every player to re-prove their own server")
            if rec.get("verified") is not True:
                continue
            want = {"server": ("lab",), "client": ("mirror", "lab")}[side]
            if rec.get("provenBy") not in want:
                out.append(f"{name}: a verified {side} record is proven by {' or '.join(want)} (kilnctl regproof writes provenBy), "
                           f"not {rec.get('provenBy')!r}: a reflection-only candidate proves nothing")
            have = rec.get("regmapDigest")
            if not have:
                out.append(f"{name}: a verified record names no regmapDigest (kiln.content enables none of its rows)")
            elif have != digest:
                out.append(f"{name}: the record's regmapDigest {have[:12]} is not regmap/common's {digest[:12]}: regmap/common was "
                           "edited after the proof, so every registry edit would be refused on this build; re-prove it "
                           "(kilnctl regproof, then --mirror for the client) or revert the edit")
    return out


def load_regmap(root):
    out = {}
    for p in sorted((Path(root) / "regmap" / "common").glob("*.json")):
        d = json.loads(p.read_text(encoding="utf-8"))
        out[d.get("registry", p.stem)] = d
    return out


def kind_name(t):
    names = {K_BOOL: "bool", K_U8: "u8", K_S8: "s8", K_U16: "u16", K_S16: "s16", K_U32: "u32", K_S32: "s32", K_U64: "u64",
             K_S64: "s64", K_F32: "f32", K_F64: "f64", K_ENUM: "enum", K_STRUCT: "struct", K_STATIC_ARRAY: "static array",
             K_BLOB_ARRAY: "blob array", K_OBJECT_REF: "object reference", K_GUID: "guid"}
    return f"{t['name']} ({names.get(t['kind'], 'kind ' + str(t['kind']))})" if t else "?"


def check_conv(db, q, conv, types, row, spec):
    """Errors of one row's member types against its conversion."""
    errs = []
    rs = [db.resolve(t) for t in types]

    def want(i, ok, what):
        if i >= len(rs) or rs[i] is None or not ok(rs[i]):
            errs.append(f"{q}: {row['paths'][i] if i < len(row['paths']) else '?'} is {kind_name(types[i] if i < len(types) else None)}, "
                        f"a {conv} row needs {what}")
            return False
        return True

    is_int = lambda r: r["kind"] in INT_RANGE  # noqa: E731

    def is_id(r):  # a 32-bit id: an integer, or a struct of one integer (keen::HashKey32)
        if r is None:
            return False
        if r["kind"] in INT_RANGE:
            return True
        fs = db.fields(r) if r["kind"] == K_STRUCT else []
        return len(fs) == 1 and (db.resolve(db.get(fs[0]["type"])) or {}).get("kind") in INT_RANGE
    is_num = lambda r: r["kind"] in INT_RANGE or r["kind"] in FLOATS  # noqa: E731
    if conv == "int":
        if want(0, is_int, "an integer") and spec.get("type") in ("int",):
            lo, hi = INT_RANGE[rs[0]["kind"]]
            if spec.get("min") is not None and spec["min"] < lo or spec.get("max") is not None and spec["max"] > hi:
                errs.append(f"{q}: the schema's range {spec.get('min')}..{spec.get('max')} does not fit {kind_name(types[0])} ({lo}..{hi})")
    elif conv == "float":
        want(0, is_num, "a number")
    elif conv == "bool":
        want(0, lambda r: r["kind"] == K_BOOL, "a bool")
    elif conv == "time":
        want(0, lambda r: r["name"] == "keen::Time", "a keen::Time")
    elif conv in ("enum", "optionalEnum"):
        at = 1 if conv == "optionalEnum" else 0
        if conv == "optionalEnum":
            want(0, lambda r: r["kind"] == K_BOOL, "the switch (a bool) first")
        if want(at, lambda r: r["kind"] == K_ENUM, "an enum"):
            have = {v["name"] for v in rs[at].get("values", [])}
            for k, v in row.get("values", {}).items():
                if conv == "optionalEnum" and k == "none":
                    continue
                if v not in have:
                    errs.append(f"{q}: {k} maps to {v}, which {rs[at]['name']} does not have")
            for k in spec.get("values", []):
                if k not in row.get("values", {}):
                    errs.append(f"{q}: the schema's value {k} has no game name in the row")
    elif conv == "flags":
        if want(0, lambda r: r["kind"] in BITMASKS, "a bitmask"):
            en = db.resolve(db.get(rs[0].get("inner")))
            have = {v["name"] for v in (en or {}).get("values", [])}
            for k, v in row.get("values", {}).items():
                if v not in have:
                    errs.append(f"{q}: {k} maps to {v}, which the bitmask's enum does not have")
            for k in spec.get("values", []):
                if k not in row.get("values", {}):
                    errs.append(f"{q}: the schema's value {k} has no game name in the row")
    elif conv == "ref" and len(row.get("paths", [])) == 1:
        want(0, lambda r: r["kind"] in (K_OBJECT_REF, K_GUID), "the reference (an object reference or a GUID)")
    elif conv == "ref":
        want(0, is_id, "the 32-bit id first")
        want(1, lambda r: r["kind"] in (K_OBJECT_REF, K_GUID), "the reference (an object reference or a GUID) second")
    elif conv == "component":
        for i in range(len(rs)):
            want(i, lambda r: r["kind"] == K_STRUCT, "a component type")
        values = row.get("values", {})
        if sorted(values.values()) != sorted(row.get("paths", [])):
            errs.append(f"{q}: a component row maps one Kiln name to each of its paths (\"values\")")
        for k in spec.get("values", []):
            if k not in values:
                errs.append(f"{q}: the schema's value {k} has no component in the row")
        if row.get("backend") != "none":
            errs.append(f"{q}: a component row is read-only (backend none)")
    elif conv == "refList":
        if want(0, lambda r: r["kind"] in ARRAYS, "an array of references first"):
            el = db.resolve(db.element(types[0]))
            if el is None or el["kind"] not in (K_OBJECT_REF, K_GUID):
                errs.append(f"{q}: {row['paths'][0]} does not hold references")
        if want(1, lambda r: r["kind"] in ARRAYS, "an array of ids second"):
            el = db.resolve(db.element(types[1]))
            if el is None or not is_id(el):
                errs.append(f"{q}: {row['paths'][1]} does not hold ids")
            a, b = rs[0], rs[1]
            if a and b and a["kind"] == K_STATIC_ARRAY and b["kind"] == K_STATIC_ARRAY and a.get("count") != b.get("count"):
                errs.append(f"{q}: the two arrays differ in length")
    elif conv in ("formula", "stackCount"):
        for i in range(len(rs)):
            want(i, is_num, "a number")
    elif conv == "impactConfig":
        want(0, lambda r: r["name"] == "keen::impact::Configurations", "the impact configurations")
    elif conv == "list":
        if want(0, lambda r: r["kind"] in ARRAYS, "an array"):
            if rs[0]["kind"] == K_STATIC_ARRAY and spec.get("maxItems") and rs[0].get("count") and spec["maxItems"] > rs[0]["count"]:
                errs.append(f"{q}: maxItems {spec['maxItems']} is more than the game's {rs[0]['count']} slots")
            el, of = db.resolve(db.element(types[0])), spec.get("of") or {}
            if of.get("type") == "int":
                if el is None or el["kind"] not in INT_RANGE:
                    errs.append(f"{q}: the elements are {kind_name(el)}, the schema says int")
                else:
                    lo, hi = INT_RANGE[el["kind"]]
                    if of.get("min", 0) < lo or of.get("max", 0) > hi:
                        errs.append(f"{q}: the elements' range {of.get('min')}..{of.get('max')} does not fit {kind_name(el)} ({lo}..{hi})")
    elif conv == "inventoryWalk":
        if want(0, lambda r: r["kind"] == K_STRUCT, "the root group (a struct)"):
            names = {f["name"] for f in db.fields(rs[0])}
            if not {"stacks", "groups"} <= names:
                errs.append(f"{q}: {rs[0]['name']} has no stacks and groups")
    elif conv == "unlock":
        want(0, lambda r: r["kind"] == K_STRUCT, "the knowledge requirement (a struct)")
    return errs


def list_element_root(db, root, rows, list_path):
    """The element type of a list row's array (inventoryWalk: the stacks' element)."""
    row = rows.get(list_path)
    if not row or not row.get("paths"):
        return None
    t, err = db.path(root, row["paths"][0])
    if err:
        return None
    if row.get("conv") == "inventoryWalk":
        t, err = db.path(t["name"], "stacks")
        if err:
            return None
    el = db.element(t)
    return el["name"] if el else None


def check(root, db):
    """Every row of regmap/common against the schema and a type source."""
    res = Result()
    model = rg.load(root)
    res.errors += [f"schema: {e}" for e in model.errors]
    maps = load_regmap(root)
    for name, reg in model.registries.items():
        m = maps.get(name)
        if m is None:
            res.errors.append(f"{name}: no regmap/common/{name}.json")
            continue
        rows = m.get("rows", {})
        specs = {r.path: r for r in reg.rows}
        for f in rows:
            if f not in specs:
                res.errors.append(f"{name}.{f}: the schema has no such field")
        # a refusal reason is shown to players and mod authors: Kiln words only (the game's names belong in "evidence")
        game = rg.game_names(root, model.registries)
        for f, row in rows.items():
            reason = str(row.get("reason") or "")
            bad = rg.GAME_WORDS.search(reason) or next((w for w in sorted(game) if re.search(r"\b" + re.escape(w) + r"\b", reason)), None)
            if bad:
                res.errors.append(f"{name}.{f}: the reason names the game's {bad.group(0) if hasattr(bad, 'group') else bad!r} "
                                  "(Kiln words only; game members go in evidence)")
        e = m.get("entries", {})
        rtype = e.get("resourceType", "")
        root_t = db.get(rtype)
        if root_t is None:
            res.absent.append(rtype)
        entry_root = rtype if root_t is not None else None
        if root_t is not None:
            res.layouts[rtype] = root_t.get("layout")
            if e.get("kind") == "array":
                t, err = db.path(rtype, e.get("array", ""))
                el = db.element(t) if t else None
                if err or el is None:
                    res.errors.append(f"{name}: entries.array {e.get('array')}: {err or 'not an array'}")
                    entry_root = None
                else:
                    entry_root = el["name"]
                    res.layouts[entry_root] = el.get("layout")
            for member in ("name", "guid", "id", "keyMember"):
                if e.get(member) and entry_root and db.path(entry_root, e[member])[1]:
                    res.errors.append(f"{name}: entries.{member} {e[member]}: {db.path(entry_root, e[member])[1]}")
            if "withComponent" in e:
                wc = e["withComponent"]
                if rtype != TEMPLATE or e.get("kind") != "resource" or not isinstance(wc, list) or not wc:
                    res.errors.append(f"{name}: entries.withComponent is a list of component types of template resources")
                else:
                    for c in wc:
                        if db.component(str(c)) is None:
                            res.errors.append(f"{name}: entries.withComponent {c}: not a component type of the type source")
        if e.get("keyMember") and e.get("key") != "items":
            res.errors.append(f"{name}: entries.keyMember goes with \"key\": \"items\"")
        for path, spec_row in specs.items():
            q = f"{name}.{path}"
            row = rows.get(path)
            if row is None or row.get("state") == "unmapped":
                res.unmapped.append(q)
                res.rows[q] = "unmapped"
                continue
            errs = []
            conv = row.get("conv")
            if conv not in CONVS:
                errs.append(f"{q}: conv {conv!r} is not one of regmap/README.md's")
            read, backend = row.get("read"), row.get("backend")
            if read == "unknown":
                if row.get("state") != "unproven":
                    errs.append(f"{q}: the game's read time is not traced: the row is unproven until it is")
                if spec_row.applies == "now":
                    errs.append(f"{q}: the game's read time is not traced: the schema ships the cautious applies (worldLoad or restart), not now")
            elif read not in READ_RANK:
                errs.append(f"{q}: read is now, worldLoad, restart or unknown")
            elif READ_RANK[read] > READ_RANK[spec_row.applies]:
                errs.append(f"{q}: the schema promises it applies {spec_row.applies} but the game reads it at {read} only")
            if spec_row.read_only:
                if backend != "none":
                    errs.append(f"{q}: a read-only field has no back-end (backend none)")
            elif backend == "boot" and spec_row.applies != "restart":
                errs.append(f"{q}: a boot back-end write acts only after a restart, the schema says {spec_row.applies}")
            elif backend == "live" and spec_row.applies == "restart":
                errs.append(f"{q}: a restart field goes through the boot back-end (before the game reads it)")
            elif backend not in ("live", "boot"):
                errs.append(f"{q}: a writable field needs a back-end (live or boot)")
            base = entry_root
            if spec_row.list_path:
                base = list_element_root(db, entry_root, rows, spec_row.list_path) if entry_root else None
            if root_t is not None and base is None:
                errs.append(f"{q}: its list's element type is not found")
            if base is not None:
                types = []
                for p in row.get("paths", []):
                    t, err = db.path(base, p)
                    if err:
                        errs.append(f"{q}: {err}")
                    types.append(t)
                if not any("has no member" in x or "not in the type source" in x for x in errs) and types:
                    errs += check_conv(db, q, conv, types, row, rg.scalar_spec(spec_row) if spec_row.kind == "list" and spec_row.element != "struct" and conv != "list" else spec_row.spec)
                if not errs:
                    res.resolved += 1
            res.errors += errs
            res.rows[q] = "ok" if not errs and base is not None else "unmapped"
    return res


def previous_record(root, side, game_build):
    """The newest record of this side from an earlier game build (by number), or None."""
    best = None
    for p in sorted((Path(root) / "regmap" / side).glob("*.json")):
        d = json.loads(p.read_text(encoding="utf-8"))
        b = str(d.get("gameBuild", ""))
        if b.isdigit() and str(game_build).isdigit() and int(b) < int(game_build) and (best is None or int(b) > int(best["gameBuild"])):
            best = d
    return best


def load_id_table(path):
    """A previous build's id table: as --ids-out writes it ({<registry>: {<guid>: <id>}}), or as kiln.content writes it at run
    time (<data>/content/registry/ids-<side>-<exe sha256>.json: {"format", "side", "exeSha256", ..., "registries": {...}})."""
    return id_table_of(json.loads(Path(path).read_text(encoding="utf-8")))


def id_table_of(doc):
    if isinstance(doc, dict) and isinstance(doc.get("registries"), dict) and "format" in doc:
        return doc["registries"]
    return doc


def compute_aliases(root, entries, previous, previous_table, game_build):
    """The record's aliases and this build's id table from its entries (regmap_entries.py), the previous record and the
    previous build's id table. Registries in load order: loot takes the items' ids."""
    maps = load_regmap(root)
    previous_table = id_table_of(previous_table) if previous_table is not None else None
    prev = regmap_ids.aliases_from_json((previous or {}).get("aliases", {}))
    per, table = {}, {}
    item_ids, guid_ids = {}, {}
    # templates have no regmap file (the model's builtin source names them: debug name, else template_<guid8>); their
    # clashes and names outside the grammar are pinned here like any registry's
    builtin = {"templates": {"unnamed": "template_%.8s"}}
    # keyed registries after the registry they take their ids from (loot: items by 32-bit id; creatures: templates by GUID)
    order = ["templates", "items", "recipes", "loot", "drops", "balance"]
    order += sorted(n for n in maps if n not in order and maps[n].get("entries", {}).get("key"))
    order += sorted(n for n in maps if n not in order)
    for name in order:
        if (name not in maps and name not in builtin) or name not in entries.get("registries", {}):
            continue
        src = maps[name].get("entries", {}) if name in maps else builtin[name]
        es = entries["registries"][name]
        key = src.get("key")
        al, ids, t = regmap_ids.record_aliases(name, src, prev.get(name, {}), (previous_table or {}).get(name, {}), es, game_build,
                                               item_ids if key == "items" else None,
                                               guid_ids.get(key) if key and key != "items" else None)
        if name == "items":
            item_ids = {e["id"]: i for e, i in zip(es, ids)}
        guid_ids[name] = {e["guid"].lower(): i for e, i in zip(es, ids) if e.get("guid")}
        per[name] = al
        table[name] = t
    return regmap_ids.aliases_json(per), table


def record(root, db, side, game_build, steam_build, entries=None, previous=None, previous_table=None):
    """(record, id table): the record of this build from the proof; its aliases from the entries when given, else the
    previous record's carried as they are."""
    res = check(root, db)
    proof = ("regmap_check.py --exe: every mapped row resolved and type-checked against this exe's own reflection"
             if not res.errors and not res.absent else "regmap_check.py --exe: not every row resolved (see rows)")
    if entries is not None:
        aliases, table = compute_aliases(root, entries, previous, previous_table, game_build)
    else:
        aliases, table = (previous or {}).get("aliases") or {"renames": [], "removals": [], "suffixes": []}, None
    return {"regmap": 1, "side": side, "exeSha256": db.exe_sha256, "gameBuild": game_build, "steamBuild": steam_build,
            # verified: the proof covered every root type; a row that failed it is listed "unmapped" and refused alone
            "verified": not res.absent,
            "proof": proof,
            "layouts": {k: f"0x{v:08x}" for k, v in sorted(res.layouts.items()) if v is not None},
            "rows": dict(sorted(res.rows.items())),
            "aliases": aliases}, table


def main(argv=None):
    ap = argparse.ArgumentParser(description="Kiln's regmap checked against the game's types")
    ap.add_argument("--root", default=str(HERE.parents[1]))
    ap.add_argument("--exe")
    ap.add_argument("--record", action="store_true")
    ap.add_argument("--side", choices=("server", "client"))
    ap.add_argument("--game-build", default="")
    ap.add_argument("--steam-build", default="")
    ap.add_argument("--entries", help="this build's entries (regmap_entries.py; kept outside the repository)")
    ap.add_argument("--previous", help="the previous build's record (default: the newest earlier record of this side)")
    ap.add_argument("--ids-in", help="the previous build's id table (written by --ids-out for that build)")
    ap.add_argument("--ids-out", help="write this build's id table here (GUID -> id: names, keep it outside the repository)")
    a = ap.parse_args(argv)
    root = Path(a.root)
    sources = []
    if a.exe:
        sources.append((a.exe, TypeDb.from_exe(a.exe)))
    else:
        for side in ("server", "client"):
            for p in sorted((root / "sigdb" / "types" / side).glob("*.json")):
                d = json.loads(p.read_text(encoding="utf-8"))
                if d.get("schema") == 2:
                    sources.append((f"sigdb/types/{side}/{p.name}", TypeDb.from_dump(d)))
    bad = 0
    if not a.exe:
        # the committed tree (tools/check.sh): every verified record is bound to this regmap/common
        for e in record_problems(root):
            print(f"regmap_check: {e}", file=sys.stderr)
            bad += 1
    for label, db in sources:
        res = check(root, db)
        for e in res.errors:
            print(f"regmap_check: {label}: {e}", file=sys.stderr)
        bad += len(res.errors)
        print(f"regmap_check: {label}: {res.resolved} rows resolved, {len(res.unmapped)} fields unmapped, "
              f"{len(res.absent)} root types not in this source{' (' + ', '.join(res.absent) + ')' if res.absent else ''}, "
              f"{len(res.errors)} errors")
        if a.record:
            if not a.exe or not a.side:
                print("regmap_check: --record needs --exe and --side", file=sys.stderr)
                return 2
            prev = json.loads(Path(a.previous).read_text()) if a.previous else previous_record(root, a.side, a.game_build)
            entries = json.loads(Path(a.entries).read_text()) if a.entries else None
            if entries is None:
                print("regmap_check: no --entries: the previous record's aliases are carried as they are", file=sys.stderr)
            prev_table = load_id_table(Path(a.ids_in)) if a.ids_in else None
            rec, table = record(root, db, a.side, a.game_build, a.steam_build, entries, prev, prev_table)
            if a.ids_out and table is not None:
                Path(a.ids_out).write_text(json.dumps(table), encoding="utf-8")
            out = root / "regmap" / a.side / f"{db.exe_sha256}.json"
            out.parent.mkdir(parents=True, exist_ok=True)
            out.write_text(json.dumps(rec, indent=2) + "\n", encoding="utf-8")
            print(f"regmap_check: wrote {out.relative_to(root)} (verified: {str(rec['verified']).lower()})")
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main())
