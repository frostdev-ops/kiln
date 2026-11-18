#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""The game's UI colours for the preview, as kiln.ui reads them in the game (native/services/ui/style.cpp,
look_palette.h): the bytes of the local game's keen::FbUiBundle resource, and the reflection layout of the fields
style.cpp reads (keen::FbUiBundle's menu and globalColors, keen::FbUiMenu, keen::FbUiGlobalColors, keen::UiColor), for
the shim's fake kiln.types. Read with tools/kfc at run time into OUT (outside the tree; never commit them). Prints the two
paths, or nothing when the game is not there.

    game_colors.py OUT [--game DIR]      -> OUT.bin, OUT.layout.txt
"""
import json
import subprocess
import sys
from pathlib import Path

here = Path(__file__).resolve().parent
kfc = here.parent / "kfc" / "kfc.py"
TYPES = ["keen::FbUiBundle", "keen::FbUiMenu", "keen::FbUiGlobalColors", "keen::UiColor"]


def run(*args):
    return subprocess.run([sys.executable, "-I", str(kfc), *args], check=True, capture_output=True, text=True).stdout


def main():
    out = Path(sys.argv[1])
    game = sys.argv[sys.argv.index("--game") + 1] if "--game" in sys.argv else None
    g = ["--game", game] if game else []
    bundle = out.with_suffix(".bin")
    layout = out.with_suffix(".layout.txt")
    if bundle.is_file() and bundle.stat().st_size > 1024 and layout.is_file() and layout.stat().st_size > 0:
        print(bundle)
        print(layout)
        return 0
    out.parent.mkdir(parents=True, exist_ok=True)
    try:
        listing = run(*g, "list", "--type", "keen::FbUiBundle", "--limit", "1").split()
        if not listing:
            return 1
        run(*g, "get", listing[0], "keen::FbUiBundle", "-o", str(bundle))
        closure = out.with_suffix(".types.json")
        run(*g, "types", "--closure", ",".join(TYPES), "--json-out", str(closure))
    except (subprocess.CalledProcessError, FileNotFoundError):
        return 1
    types = {t["name"]: t for t in json.loads(closure.read_text())["types"]}
    lines = []
    for name in TYPES:
        t = types.get(name)
        if not t:
            return 1
        lines.append(f"type {name} {t['size']} {t['hash']:x} {t['kind']}")
        for f in t.get("fields") or []:
            lines.append(f"field {name} {f['name']} {f['offset']:x} {f['type']}")
    layout.write_text("\n".join(lines) + "\n")
    print(bundle)
    print(layout)
    return 0


if __name__ == "__main__":
    sys.exit(main())
