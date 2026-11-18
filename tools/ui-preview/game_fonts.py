#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""The game's keen::UiFontResource and the font files kiln.ui reads from it, for a preview in a Chinese, Japanese or Korean
language: the default language group's regular and bold faces and the regular faces of LANGUAGE's own group (keen::LanguageId,
default 20 = zh-CN). Read from the local game with tools/kfc at run time into OUT/ (outside the tree; never commit them),
the files named as kiln.assets spells a content hash ("<size>-<h0>-<h1>-<h2>.bin", hex). The shim serves them to style.cpp's
real readers (kiln_preview_font_resource). Prints OUT/UiFontResource.bin, or nothing when the game is not there.

    game_fonts.py OUT [--language ID] [--game DIR]
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
    language = int(sys.argv[sys.argv.index("--language") + 1]) if "--language" in sys.argv else 20
    game = sys.argv[sys.argv.index("--game") + 1] if "--game" in sys.argv else None
    g = ["--game", game] if game else []
    out.mkdir(parents=True, exist_ok=True)
    res_file = out / "UiFontResource.bin"
    try:
        if not res_file.is_file():
            listing = run(*g, "list", "--type", "keen::UiFontResource", "--limit", "1").split()
            if not listing:
                return 1
            run(*g, "get", listing[0], "keen::UiFontResource", "-o", str(res_file))
    except (subprocess.CalledProcessError, FileNotFoundError):
        return 1
    res = res_file.read_bytes()

    def arr(at):  # a BlobArray: offset relative to the field, count
        off, n = struct.unpack_from("<iI", res, at)
        return at + off, n

    fbase, fcount = arr(0)
    gbase, gcount = arr(8)
    want = set()
    for i in range(gcount):  # client 26430625's layout (native/services/ui/font_group.h)
        at = gbase + i * 24
        mask = struct.unpack_from("<Q", res, at)[0]
        default = res[at + 0x14] != 0
        if not default and not (mask >> language) & 1:
            continue
        sbase, scount = arr(at + 8)
        for k in range(scount):
            s = sbase + k * 20
            font, variant = struct.unpack_from("<I", res, s)[0], res[s + 0x10]
            if variant == 0 or (default and variant == 1):
                want.add(font)
    for index in sorted(want):
        if index >= fcount:
            continue
        size, h0, h1, h2 = struct.unpack_from("<4I", res, fbase + index * 32)
        name = f"{size:x}-{h0:x}-{h1:x}-{h2:x}"
        path = out / (name + ".bin")
        if not path.is_file() or path.stat().st_size != size:
            run(*g, "content", name, "-o", str(path))
    print(res_file)
    return 0


if __name__ == "__main__":
    sys.exit(main())
