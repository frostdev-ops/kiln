#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""The game's UI font for the preview, picked as native/services/ui/style.cpp picks it: keen::UiFontResource, the default
language group's regular glyph set (else font 0), its TTF content blob; and the same group's bold glyph set (variant mask
1) into OUT-bold.ttf. Read from the local game with tools/kfc at run time and written to OUT (outside the tree; never
commit it). Prints the path, or nothing when the game is not there.

    game_font.py OUT.ttf [--game DIR]
"""
import struct
import subprocess
import sys
from pathlib import Path

here = Path(__file__).resolve().parent
kfc = here.parent / "kfc" / "kfc.py"


def run(*args):
    return subprocess.run([sys.executable, "-I", str(kfc), *args], check=True, capture_output=True, text=True).stdout


def main():
    out = Path(sys.argv[1])
    game = sys.argv[sys.argv.index("--game") + 1] if "--game" in sys.argv else None
    g = ["--game", game] if game else []
    bold_out = out.with_name(out.stem + "-bold.ttf")
    if out.is_file() and out.stat().st_size > 1024 and bold_out.is_file():
        print(out)
        return 0
    out.parent.mkdir(parents=True, exist_ok=True)
    try:
        listing = run(*g, "list", "--type", "keen::UiFontResource", "--limit", "1").split()
    except (subprocess.CalledProcessError, FileNotFoundError):
        return 1
    if not listing:
        return 1
    res_file = out.with_suffix(".UiFontResource.bin")
    run(*g, "get", listing[0], "keen::UiFontResource", "-o", str(res_file))
    res = res_file.read_bytes()

    def arr(at):
        return struct.unpack_from("<iI", res, at) if at + 8 <= len(res) else (0, 0)

    f_off, f_count = arr(0)
    g_off, g_count = arr(8)
    font_index = 0
    bold_index = None
    gbase = 8 + g_off
    pick = None
    for i in range(min(g_count, 64)):
        at = gbase + i * 24
        if at + 24 > len(res):
            break
        if pick is None:
            pick = at
        if res[at + 0x14]:
            pick = at
            break
    if pick is not None:
        s_off, s_count = arr(pick + 8)
        sbase = pick + 8 + s_off
        regular = None
        for i in range(min(s_count, 64)):
            at = sbase + i * 20
            if at + 20 > len(res):
                break
            mask = res[at + 0x10]
            index = struct.unpack_from("<I", res, at)[0]
            if mask == 0 and regular is None:
                regular = index
            if mask == 1 and bold_index is None:  # keen::UiFontVariantFlag Bold = bit 0
                bold_index = index
        if regular is not None:
            font_index = regular
    if font_index >= f_count:
        font_index = 0

    def blob(index, path):
        size, h0, h1, h2 = struct.unpack_from("<4I", res, f_off + index * 32)
        run(*g, "content", f"{size:x}-{h0:x}-{h1:x}-{h2:x}", "-o", str(path))

    blob(font_index, out)
    if bold_index is not None and bold_index < f_count and bold_index != font_index:
        blob(bold_index, bold_out)
    res_file.unlink(missing_ok=True)
    print(out)
    return 0


if __name__ == "__main__":
    sys.exit(main())
