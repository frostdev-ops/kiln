#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""sigdb definition trees with provenance, and text-preserving JSON edits (for sigport's patches).

A tree is a folder like sigdb/symbols/: <side>.json, <side>.d/*.json (client.d may overlay server.d, see sides.py).
load(defs) returns the doc the core would see plus, for each symbol, the file it comes from, so a ported pattern is
written back where a human would write it:
  - server: the file that defines the symbol;
  - client: client.json / client.d/<x>.json when the symbol is defined there; a symbol from a shared server.d file (or
    from the base of an overlay) goes into the client.d/<x>.json overlay ("extends": "server.d/<x>.json"), so a client
    port never edits the server's definitions.
Edits replace JSON values in place (hand-formatted files keep their layout); new entries are inserted as compact JSON.
"""
import copy
import json
from pathlib import Path

SECTIONS = ("symbols", "constants", "features")


def _load(p):
    return json.loads(Path(p).read_text())


class Tree:
    def __init__(self, defs):
        defs = Path(defs).resolve()
        self.root = defs.parent
        self.side = defs.stem
        self.main = defs.name
        self.texts = {}       # relpath -> original text
        self.doc = None       # merged doc (the core's view)
        self.origin = {}      # symbol -> relpath of the file that defines it in the merged view
        self.dest = {}        # symbol -> relpath where a changed definition is written
        self.cdest = {}       # constant -> relpath where a changed fixed value is written
        self.base_of = {}     # relpath of an overlay -> its base relpath
        self.problems = []
        self._compose()

    def _read(self, rel):
        if rel not in self.texts:
            self.texts[rel] = (self.root / rel).read_text()
        return json.loads(self.texts[rel])

    def _compose(self):
        side = self.side
        doc = self._read(self.main)
        for name in doc.get("symbols", {}):
            self.origin[name] = self.dest[name] = self.main
        for name in doc.get("constants", {}):
            self.cdest[name] = self.main
        own = {p.name for p in (self.root / f"{side}.d").glob("*.json")} if (self.root / f"{side}.d").is_dir() else set()
        shared = set()
        if side != "server" and (self.root / "server.d").is_dir():
            shared = {p.name for p in (self.root / "server.d").glob("*.json")}
        parts = []
        for name in sorted(own | shared):
            if side == "server" or name in own:
                rel = f"{side}.d/{name}"
                part = self._read(rel)
                ext = part.get("extends")
                if ext and side != "server":
                    base = self._read(ext)
                    self.base_of[rel] = ext
                    for s in base.get("symbols", {}):
                        self.origin[s] = ext
                        self.dest[s] = rel
                    for s in part.get("symbols", {}):
                        self.origin[s] = self.dest[s] = rel
                    for c in list(base.get("constants", {})) + list(part.get("constants", {})):
                        self.cdest[c] = rel
                    part = _overlay(base, part)
                else:
                    for s in part.get("symbols", {}):
                        self.origin[s] = self.dest[s] = rel
                    for c in part.get("constants", {}):
                        self.cdest[c] = rel
            else:
                rel = f"server.d/{name}"
                part = self._read(rel)
                for s in part.get("symbols", {}):
                    self.origin[s] = rel
                    self.dest[s] = f"{side}.d/{name}"
                    self.base_of[f"{side}.d/{name}"] = rel
                for c in part.get("constants", {}):
                    self.cdest[c] = f"{side}.d/{name}"
                    self.base_of[f"{side}.d/{name}"] = rel
            parts.append(part)
        for part in parts:
            for section in SECTIONS:
                for k, v in part.get(section, {}).items():
                    if k in doc.setdefault(section, {}):
                        self.problems.append(f"{section[:-1]} {k} is defined twice")
                        continue
                    doc[section][k] = v
        self.doc = doc

    def files(self):
        out = [self.main]
        for d in ("server.d", "client.d"):
            if (self.root / d).is_dir():
                out += [f"{d}/{p.name}" for p in sorted((self.root / d).glob("*.json"))]
        for alt in ("server.json", "client.json"):
            if alt != self.main and (self.root / alt).is_file():
                out.append(alt)
        return out


def _overlay(base, over):
    out = copy.deepcopy(base)
    for section in SECTIONS:
        dst = out.setdefault(section, {})
        for k, v in over.get(section, {}).items():
            dst[k] = copy.deepcopy(v)
        for k in over.get("remove", {}).get(section, []):
            dst.pop(k, None)
    return out


# ---- JSON value spans ---------------------------------------------------------------------------------------------
class _Spans:
    """Offsets of every value in a JSON text: path tuple -> (start, end)."""

    def __init__(self, text):
        self.t = text
        self.spans = {}
        i = self._ws(0)
        self._value(i, ())

    def _ws(self, i):
        while i < len(self.t) and self.t[i] in " \t\r\n":
            i += 1
        return i

    def _string(self, i):
        j = i + 1
        while self.t[j] != '"':
            j += 2 if self.t[j] == "\\" else 1
        return j + 1

    def _value(self, i, path):
        c = self.t[i]
        if c == "{":
            j = self._ws(i + 1)
            while self.t[j] != "}":
                ke = self._string(j)
                key = json.loads(self.t[j:ke])
                j = self._ws(ke)
                assert self.t[j] == ":"
                j = self._ws(j + 1)
                j = self._ws(self._value(j, path + (key,)))
                if self.t[j] == ",":
                    j = self._ws(j + 1)
            end = j + 1
        elif c == "[":
            j = self._ws(i + 1)
            k = 0
            while self.t[j] != "]":
                j = self._ws(self._value(j, path + (k,)))
                k += 1
                if self.t[j] == ",":
                    j = self._ws(j + 1)
            end = j + 1
        elif c == '"':
            end = self._string(i)
        else:
            j = i
            while j < len(self.t) and self.t[j] not in ",}] \t\r\n":
                j += 1
            end = j
        self.spans[path] = (i, end)
        return end


class Editor:
    """Applies value replacements/insertions to one file's text, keeping everything else byte for byte."""

    def __init__(self, text):
        self.text = text

    def set(self, path, value):
        sp = _Spans(self.text).spans
        if path in sp:
            a, b = sp[path]
            self.text = self.text[:a] + json.dumps(value) + self.text[b:]
            return
        parent, key = path[:-1], path[-1]
        a, b = sp[parent]
        body = self.text[a + 1:b - 1]
        if isinstance(key, int):
            ins = json.dumps(value)
        else:
            ins = f"{json.dumps(key)}: {json.dumps(value)}"
        if body.strip():
            # after the last member, same line style as compact entries
            k = b - 1
            while self.text[k - 1] in " \t\r\n":
                k -= 1
            self.text = self.text[:k] + ", " + ins + self.text[k:]
        else:
            self.text = self.text[:a + 1] + ins + self.text[b - 1:]

    def add_symbol(self, name, definition):
        """Adds a whole symbol entry to "symbols" (creating the section if needed), one line per strategy."""
        sp = _Spans(self.text).spans
        lines = ",\n".join("        " + json.dumps(f) for f in definition["find"])
        entry = f'    {json.dumps(name)}: {{\n      "find": [\n{lines}\n      ]\n    }}'
        if ("symbols",) not in sp:
            a, b = sp[()]
            k = b - 1
            while self.text[k - 1] in " \t\r\n":
                k -= 1
            self.text = self.text[:k] + ',\n  "symbols": {\n' + entry + "\n  }" + self.text[k:]
            return
        a, b = sp[("symbols",)]
        if not self.text[a + 1:b - 1].strip():
            self.text = self.text[:a + 1] + "\n" + entry + "\n  " + self.text[b - 1:]
            return
        k = b - 1
        while self.text[k - 1] in " \t\r\n":
            k -= 1
        self.text = self.text[:k] + ",\n" + entry + self.text[k:]


def new_overlay(side, base_rel, comment):
    return json.dumps({"format": 1, "side": side, "extends": base_rel, "comment": comment, "symbols": {}}, indent=2) + "\n"
