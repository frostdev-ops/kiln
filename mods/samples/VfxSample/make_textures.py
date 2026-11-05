#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""Draws this sample's textures, and the kiln-vfx template's dot, from nothing. Standard library only; the PNGs are committed, so nobody has to run this.

    python3 mods/samples/VfxSample/make_textures.py
"""
import math
import random
import struct
import zlib
from pathlib import Path

HERE = Path(__file__).resolve().parent
TEMPLATE = HERE.parents[2] / "templates/kiln-vfx/vfx"


def png(path, width, height, pixel):
    """pixel(x, y) -> (r, g, b, a), each 0 to 255."""
    def chunk(tag, data):
        return struct.pack(">I", len(data)) + tag + data + struct.pack(">I", zlib.crc32(tag + data))
    rows = b"".join(b"\0" + b"".join(bytes(pixel(x, y)) for x in range(width)) for y in range(height))
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 6, 0, 0, 0))
                     + chunk(b"IDAT", zlib.compress(rows, 9)) + chunk(b"IEND", b""))


def white(alpha):
    return 255, 255, 255, max(0, min(255, round(alpha * 255)))


def soft_dot(u, v):
    """A round dot that fades out, on a -1 to 1 square."""
    return max(0.0, 1.0 - math.hypot(u, v)) ** 1.5


def spark(u, v):
    """A bright centre with four thin rays: what a hot ember looks like when it is small."""
    r = math.hypot(u, v)
    rays = max(0.0, 1.0 - abs(u) * 12) * max(0.0, 1.0 - abs(v)) + max(0.0, 1.0 - abs(v) * 12) * max(0.0, 1.0 - abs(u))
    return min(1.0, max(0.0, 1.0 - r * 2.5) + rays * 0.8)


def puff(u, v, frame, bumps):
    """A soft cloud that grows and thins out, frame 0 to 3."""
    grow = 0.55 + frame * 0.13
    density = 0.0
    for x, y, r in bumps:
        density = max(density, 1.0 - math.hypot(u - x * grow, v - y * grow) / (r * grow))
    return max(0.0, density) ** 1.2 * (1.0 - frame * 0.18)


def main():
    def square(size, f):
        return lambda x, y: white(f((x + 0.5) / size * 2 - 1, (y + 0.5) / size * 2 - 1))

    png(HERE / "vfx/spark.png", 32, 32, square(32, spark))
    png(TEMPLATE / "dot.png", 16, 16, square(16, soft_dot))

    # a flipbook: 4 frames of 32x32 in a 2x2 grid. Seeded, so the file is the same each time.
    rng = random.Random(7)
    bumps = [(0.0, 0.0, 0.8)] + [(rng.uniform(-0.5, 0.5), rng.uniform(-0.5, 0.5), rng.uniform(0.25, 0.45)) for _ in range(9)]

    def grid(x, y):
        cell = 32
        frame = y // cell * 2 + x // cell
        u, v = (x % cell + 0.5) / cell * 2 - 1, (y % cell + 0.5) / cell * 2 - 1
        return white(puff(u, v, frame, bumps))

    png(HERE / "vfx/puff_2x2.png", 64, 64, grid)


if __name__ == "__main__":
    main()
