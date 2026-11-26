#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""regmap_ids: the Kiln ids of a registry's game entries (D3) and the alias deltas a per-build regmap record keeps.

derive() makes the ids exactly as kiln.content does at start (native/services/content/registry/regmap.cpp assign_ids; both
are tested on native/services/content/tests/registry_ids_cases.json):
  - an id the record pins (a rename or a clash suffix kept from an earlier build) holds first, whatever the order;
  - a removed entry's id is never given again;
  - else game:<the debug name in lower case>, every character outside [a-z0-9_.-] as '_' (pinned by the record);
    an unnamed entry: the registry's "unnamed" pattern; a drop table: the first, in ordinal order, of its templates' names;
    a loot entry: its item's id; an entry of a registry keyed by another (src "key": a creature by its template) that registry's
    id for its GUID (or, without a GUID, its 32-bit id);
  - the entry that holds a name first keeps it; a later one gets "~<first 8 hex digits of its GUID>", more while taken.

record_aliases() is what regmap_check.py --record does for each registry: from the previous build's record (its pins and
removals) and the previous build's id table (GUID -> id, made by the same tool for that build and kept outside the
repository: it holds names), it carries every pin forward, pins an entry whose id would move (a rename, an order change,
a drop table whose templates changed), pins both sides of every clash and every name outside the grammar, and retires the
id of every entry the new build no longer has. The record keeps only GUID -> id deltas and removed ids.
"""
import re

KILN_ID = re.compile(r"^[a-z0-9][a-z0-9_.-]*:[a-z0-9_.-]+(~[0-9a-f]{8,32})?$")
ID_CHARS = set(b"abcdefghijklmnopqrstuvwxyz0123456789_.-")


def _lower_bytes(s):
    """ASCII lower case over the UTF-8 bytes (what the native side does), every byte outside the grammar as '_'."""
    raw = bytes(c + 32 if 65 <= c <= 90 else c for c in s.encode("utf-8"))
    name = bytes(c if c in ID_CHARS else ord("_") for c in raw)
    return raw, name.decode("ascii")


def _hex(entry):
    h = entry.get("guid", "").lower().replace("-", "")
    return h or "%08x" % (entry.get("id", 0) & 0xFFFFFFFF)


def _unnamed(src, entry, hexs):
    pat = src.get("unnamed", "")
    if "%08x" in pat:
        return pat.replace("%08x", "%08x" % (entry.get("id", 0) & 0xFFFFFFFF))
    if "%.8s" in pat:
        return pat.replace("%.8s", hexs[:8])
    if pat and "%" not in pat:
        return pat
    return "entry_%08x" % (entry.get("id", 0) & 0xFFFFFFFF)


def derive(registry, src, aliases, entries, by_item_id=None, by_key_guid=None):
    """(ids, notes) for entries in the archive's order. aliases: {"pinned": {guid: id}, "removed": {id: build}}. A registry with
    src "key" takes the keyed registry's id: by_key_guid ({guid: id}) by the entry's GUID first, else by_item_id ({32-bit id: id})."""
    pinned = {g.lower(): i for g, i in (aliases.get("pinned") or {}).items()}
    taken = set((aliases.get("removed") or {}).keys())
    ids, notes = [None] * len(entries), []
    for i, e in enumerate(entries):
        p = pinned.get(e.get("guid", "").lower())
        if p:
            ids[i] = p
            taken.add(p)

    def suffixed(base, hexs):
        hexs = hexs or "00000000"
        cur, n = base, 8
        while cur in taken and n <= max(len(hexs), 8) + 4:
            cur = f"{base}~{hexs[:min(n, len(hexs))]}"
            n += 4
        return cur

    for i, e in enumerate(entries):
        if ids[i] is not None:
            continue
        hexs = _hex(e)
        keyed = None
        if src.get("key"):
            g = e.get("guid", "").lower()
            if by_key_guid is not None and g and g in by_key_guid:
                keyed = by_key_guid[g]
            elif by_item_id is not None and e.get("id") in by_item_id:
                keyed = by_item_id[e["id"]]
        if keyed is not None:
            cur = keyed
            if cur in taken:
                notes.append(f"{registry}: a second entry for {cur} ({hexs}) gets a suffix")
                cur = suffixed(keyed, hexs)
            taken.add(cur)
            ids[i] = cur
            continue
        if src.get("nameFrom"):
            by = sorted(_lower_bytes(t)[0] for t in e.get("namedBy") or [])
            raw = by[0] if by else b""
            name = bytes(c if c in ID_CHARS else ord("_") for c in raw).decode("ascii")
        else:
            raw, name = _lower_bytes(e.get("name", ""))
        if name.encode("ascii") != raw:
            notes.append(f"{registry}: {raw.decode('utf-8', 'replace')!r} is outside the id grammar: {name} ({hexs}, pinned by the record)")
        if not name:
            name = _unnamed(src, e, hexs)
        cur = suffixed("game:" + name, hexs)
        if not KILN_ID.match(cur):
            notes.append(f"{registry}: {cur!r} is not a Kiln id: game:entry_{hexs[:8]} instead")
            cur = suffixed(f"game:entry_{hexs[:8]}", hexs)
        taken.add(cur)
        ids[i] = cur
    return ids, notes


def _sanitized(src, entry):
    if src.get("nameFrom"):
        by = sorted(_lower_bytes(t)[0] for t in entry.get("namedBy") or [])
        raw = by[0] if by else b""
        return bytes(c if c in ID_CHARS else ord("_") for c in raw) != raw
    raw, name = _lower_bytes(entry.get("name", ""))
    return name.encode("ascii") != raw


def _key(e):
    """An entry's key in an id table: its GUID (lower case), else "#<32-bit id>" (kiln.content's key of a loot entry)."""
    g = e.get("guid", "")
    if g:
        return g.lower()
    return f"#{e['id']}" if isinstance(e.get("id"), int) and e.get("id") else ""


def record_aliases(registry, src, previous, previous_table, entries, build, by_item_id=None, by_key_guid=None):
    """(aliases, ids, table) of one registry on a new build. previous: the registry's aliases in the previous record;
    previous_table: GUID -> id of the previous build (may be empty: then the pins alone hold the ids)."""
    pinned = {g.lower(): i for g, i in (previous.get("pinned") or {}).items()}
    removed = dict(previous.get("removed") or {})
    table = {g.lower(): i for g, i in (previous_table or {}).items()}
    # an entry without a GUID (a loot entry) is keyed by its 32-bit id, "#<id>", as kiln.content's id table keys it
    now = {k for k in map(_key, entries) if k}
    for g, i in list(table.items()) + list(pinned.items()):
        if g not in now:
            removed.setdefault(i, build)
            pinned.pop(g, None)
    for _ in range(8):
        ids, _notes = derive(registry, src, {"pinned": pinned, "removed": removed}, entries, by_item_id, by_key_guid)
        moved = False
        for e, i in zip(entries, ids):
            g = e.get("guid", "").lower()
            if g and g in table and table[g] != i and pinned.get(g) != table[g]:
                pinned[g] = table[g]
                moved = True
        if not moved:
            break
    groups = {}
    for e, i in zip(entries, ids):
        groups.setdefault(i.split("~")[0], []).append((e.get("guid", "").lower(), i))
    for members in groups.values():
        if len(members) > 1 or any("~" in i for _, i in members):
            for g, i in members:
                if g:
                    pinned[g] = i
    for e, i in zip(entries, ids):
        g = e.get("guid", "").lower()
        if g and _sanitized(src, e):
            pinned[g] = i
    new_table = {_key(e): i for e, i in zip(entries, ids) if _key(e)}
    return {"pinned": dict(sorted(pinned.items())), "removed": dict(sorted(removed.items()))}, ids, new_table


def aliases_json(per_registry):
    """The record's "aliases": renames (an entry keeps this id), suffixes (a clash suffix kept), removals."""
    out = {"renames": [], "removals": [], "suffixes": []}
    for reg in sorted(per_registry):
        a = per_registry[reg]
        for g, i in sorted((a.get("pinned") or {}).items()):
            out["suffixes" if "~" in i else "renames"].append({"registry": reg, "guid": g, "id": i})
        for i, b in sorted((a.get("removed") or {}).items()):
            out["removals"].append({"registry": reg, "id": i, "removedIn": b})
    return out


def aliases_from_json(rec):
    out = {}
    for k in ("renames", "suffixes"):
        for r in rec.get(k, []):
            out.setdefault(r["registry"], {"pinned": {}, "removed": {}})["pinned"][r["guid"].lower()] = r["id"]
    for r in rec.get("removals", []):
        out.setdefault(r["registry"], {"pinned": {}, "removed": {}})["removed"][r["id"]] = r["removedIn"]
    return out
