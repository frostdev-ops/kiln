#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""Writes the sample's textures into textures/: a mossy brick and its maps, a mossy top, a snowy bark, glowing runes. Procedural, so the sample
carries no art of anyone else's. Python's standard library only.

    python3 mods/samples/SurfaceSample/make_textures.py
"""
import math
import struct
import zlib
from pathlib import Path

OUT = Path(__file__).resolve().parent / "textures"
N = 256


def write_png(path, width, height, pixel, channels):
    def chunk(t, d):
        return struct.pack(">I", len(d)) + t + d + struct.pack(">I", zlib.crc32(t + d))
    rows = bytearray()
    for y in range(height):
        rows.append(0)
        for x in range(width):
            rows += bytes(pixel(x, y))
    kind = {1: 0, 3: 2}[channels]
    path.write_bytes(b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, kind, 0, 0, 0))
                     + chunk(b"IDAT", zlib.compress(bytes(rows), 9)) + chunk(b"IEND", b""))


def noise(x, y, s):
    """Cheap tileable value noise, 0..1, period N."""
    a = 2 * math.pi / N
    return 0.5 + 0.25 * math.sin(x * a * s + 1.3 * math.sin(y * a * 3)) * math.cos(y * a * s + 0.7 * math.sin(x * a * 2)) + 0.25 * math.sin((x + 2 * y) * a * (s + 1))


def brick(x, y):
    """(mortar 0..1, brick shade 0..1): bricks 64 x 32, every other row shifted by half."""
    row = y // 32
    bx, by = (x + (32 if row % 2 else 0)) % 64, y % 32
    edge = min(bx, 63 - bx, by, 31 - by)
    mortar = max(0.0, 1 - edge / 3) if edge < 3 else 0.0
    return mortar, noise(x + row * 17, y, 4)


def moss_amount(x, y):
    m, _ = brick(x, y)
    return min(1.0, max(0.0, noise(x, y, 2) * 1.6 - 0.55) + 0.6 * m)


def albedo(x, y):
    m, shade = brick(x, y)
    stone = (96 + 40 * shade, 92 + 36 * shade, 84 + 30 * shade)
    if m > 0:
        stone = (70, 72, 66)
    k = moss_amount(x, y)
    moss = (58 + 30 * noise(x, y, 7), 92 + 50 * noise(y, x, 6), 40)
    return tuple(int(s * (1 - k) + g * k) for s, g in zip(stone, moss))


def height(x, y):
    m, shade = brick(x, y)
    return 0.15 if m > 0 else 0.7 + 0.25 * shade


def normal(x, y):
    dx = (height((x + 1) % N, y) - height((x - 1) % N, y)) * 2
    dy = (height(x, (y + 1) % N) - height(x, (y - 1) % N)) * 2
    length = math.sqrt(dx * dx + dy * dy + 1)
    # green up, as in glTF and Blender
    return (int((-dx / length * 0.5 + 0.5) * 255), int((dy / length * 0.5 + 0.5) * 255), int((1 / length * 0.5 + 0.5) * 255))


def main():
    OUT.mkdir(exist_ok=True)
    write_png(OUT / "moss-brick.png", N, N, albedo, 3)
    write_png(OUT / "moss-brick_n.png", N, N, normal, 3)
    write_png(OUT / "moss-brick_r.png", N, N, lambda x, y: (int(255 * (0.95 if brick(x, y)[0] > 0 else 0.7 + 0.2 * moss_amount(x, y))),), 1)
    write_png(OUT / "moss-brick_h.png", N, N, lambda x, y: (int(255 * height(x, y)),), 1)
    write_png(OUT / "moss-top.png", N, N, lambda x, y: (int(50 + 40 * noise(x, y, 5)), int(90 + 60 * noise(y, x, 4)), 38), 3)
    write_png(OUT / "snowy-logs.png", N, N, lambda x, y: (int(205 + 40 * noise(x, y * 4, 3)),) * 2 + (int(225 + 30 * noise(x, y * 4, 3)),), 3)
    write_png(OUT / "runes.png", N, N, lambda x, y: (40, 190, 255) if (x % 64 in range(28, 36) or y % 64 in range(28, 36)) and noise(x, y, 3) > 0.45 else (0, 0, 0), 3)


if __name__ == "__main__":
    main()
