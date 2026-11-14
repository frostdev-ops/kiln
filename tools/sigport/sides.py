#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""Per-side symbol definitions: what kiln/sigdb/<side>.d/ holds on a deployed game.

The core loads kiln/sigdb/<side>.json plus every kiln/sigdb/<side>.d/*.json. In the repo:

  sigdb/symbols/server.json, server.d/<service>.json    the dedicated server
  sigdb/symbols/client.json, client.d/<service>.json    the game client (enshrouded.exe)

The client exe carries the same server-world code (it hosts single player and co-op games), so most service
definitions match it unchanged. The client's .d set is therefore composed:
  - server.d/<name>.json without a client.d twin: used as is (shared definition).
  - client.d/<name>.json with "extends": "server.d/<name>.json": an overlay. Its symbols/constants/features replace
    the base's entries of the same name, new names are added, and "remove": {"symbols": [...], "constants": [...],
    "features": [...]} drops base entries. Use it to port the few patterns that differ on the client.
  - client.d/<name>.json without "extends": a standalone client definition; it replaces server.d/<name>.json.

    python3 tools/sigport/sides.py compose client <out dir>      write the composed client.d (deploy)
    python3 tools/sigport/sides.py list client                   show where each file comes from
"""
import copy
import json
import sys
from pathlib import Path

SECTIONS = ("symbols", "constants", "features")
# Descriptors of typed links and the structs they name (dev.28): composed and merged like the sections above, but
# they are not names a build record lists (a symbol's descriptor is judged at load: the feature sig/<symbol>).
DESCRIPTOR_SECTIONS = ("sigs", "structs")
REPO_SYMBOLS = Path(__file__).resolve().parents[2] / "sigdb" / "symbols"


def _load(path):
    return json.loads(Path(path).read_text())


def overlay(base, over, base_name, over_name):
    """base doc + overlay doc -> composed doc (neither input is modified)."""
    out = copy.deepcopy(base)
    out["side"] = over.get("side", "client")
    note = over.get("comment", "")
    out["comment"] = (base.get("comment", "") + f" | {over_name} over {base_name}" + (f": {note}" if note else "")).strip()
    for section in SECTIONS + DESCRIPTOR_SECTIONS:
        if section in DESCRIPTOR_SECTIONS and section not in base and section not in over:
            continue
        dst = out.setdefault(section, {})
        for k, v in over.get(section, {}).items():
            dst[k] = copy.deepcopy(v)
        for k in over.get("remove", {}).get(section, []):
            dst.pop(k, None)
    return out


def compose(side, symbols_dir=REPO_SYMBOLS):
    """[(file name, doc, source description)] for kiln/sigdb/<side>.d/."""
    symbols_dir = Path(symbols_dir)
    own = {p.name: p for p in sorted((symbols_dir / f"{side}.d").glob("*.json"))}
    if side == "server":
        return [(n, _load(p), f"server.d/{n}") for n, p in own.items()]
    shared = {p.name: p for p in sorted((symbols_dir / "server.d").glob("*.json"))}
    out = []
    for name in sorted(set(shared) | set(own)):
        if name in own:
            doc = _load(own[name])
            ext = doc.get("extends")
            if ext:
                base_path = symbols_dir / ext
                if not base_path.is_file():
                    raise SystemExit(f"{side}.d/{name}: extends {ext}, which does not exist")
                out.append((name, overlay(_load(base_path), doc, ext, f"{side}.d/{name}"), f"{side}.d/{name} over {ext}"))
            else:
                out.append((name, doc, f"{side}.d/{name} (standalone)"))
        else:
            out.append((name, _load(shared[name]), f"server.d/{name} (shared)"))
    return out


def merged(definitions):
    """<side>.json + its composed .d files as one doc, like the core sees it. Prints duplicate-name failures."""
    definitions = Path(definitions)
    side = definitions.stem
    doc = _load(definitions)
    for name, part, _src in compose(side, definitions.parent):
        for section in SECTIONS + DESCRIPTOR_SECTIONS:
            if section in DESCRIPTOR_SECTIONS and section not in part:
                continue
            for k, v in part.get(section, {}).items():
                if k in doc.setdefault(section, {}):
                    print(f"FAIL  {side}.d/{name}: {section[:-1]} {k} is defined twice")
                    continue
                doc[section][k] = v
    return doc


def main(argv):
    if len(argv) >= 2 and argv[0] == "list":
        for name, _doc, src in compose(argv[1]):
            print(f"{name:20s} {src}")
        return 0
    if len(argv) == 3 and argv[0] == "compose":
        out = Path(argv[2])
        out.mkdir(parents=True, exist_ok=True)
        for old in out.glob("*.json"):
            old.unlink()
        for name, doc, src in compose(argv[1]):
            (out / name).write_text(json.dumps(doc, indent=2) + "\n")
            print(f"{name:20s} {src}")
        return 0
    print(__doc__)
    return 2


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
