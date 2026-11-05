#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""Writes the synthetic texture of vfx-test's red burst: a 4-frame flipbook, a white digit 0 to 3 on a square, so a person can read which
frame draws. No game art.

    mods/tests/vfx-test/fixtures/make_flipbook.py mods/tests/vfx-test/vfx/flipbook_0123.png
"""
import struct
import sys
import zlib

DIGITS = {0: ["01110", "10001", "10011", "10101", "11001", "10001", "01110"],
          1: ["00100", "01100", "00100", "00100", "00100", "00100", "01110"],
          2: ["01110", "10001", "00001", "00010", "00100", "01000", "11111"],
          3: ["11110", "00001", "00001", "01110", "00001", "00001", "11110"]}
FRAME = 64
SCALE = 6


def pixel(x, y):
    frame = x // FRAME
    fx, fy = x % FRAME, y
    ox, oy = (FRAME - 5 * SCALE) // 2, (FRAME - 7 * SCALE) // 2
    gx, gy = (fx - ox) // SCALE, (fy - oy) // SCALE
    if 0 <= gx < 5 and 0 <= gy < 7 and DIGITS[frame][gy][gx] == "1":
        return (255, 255, 255, 255)  # the digit
    return (160, 160, 160, 255)      # the square


def chunk(tag, data):
    return struct.pack(">I", len(data)) + tag + data + struct.pack(">I", zlib.crc32(tag + data))


def main():
    w, h = FRAME * 4, FRAME
    raw = b"".join(b"\0" + b"".join(bytes(pixel(x, y)) for x in range(w)) for y in range(h))
    png = b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", w, h, 8, 6, 0, 0, 0)) + chunk(b"IDAT", zlib.compress(raw, 9)) + chunk(b"IEND", b"")
    with open(sys.argv[1], "wb") as f:
        f.write(png)


if __name__ == "__main__":
    main()
