# SPDX-License-Identifier: MIT
"""Small shared helpers: FNV-1a, GUID text <-> bytes, output paths."""
import os
import struct
import uuid
from pathlib import Path

FNV_OFFSET = 0x811C9DC5
FNV_PRIME = 0x01000193


def fnv1a32(data, seed=FNV_OFFSET):
    """32-bit FNV-1a, the hash the game uses for type names ("keen::RenderModel") and ids (FNV-1a of GUID bytes)."""
    if isinstance(data, str):
        data = data.encode()
    h = seed
    for c in data:
        h = ((h ^ c) * FNV_PRIME) & 0xFFFFFFFF
    return h


def guid_text(b):
    """16 bytes in keen::Guid memory order ({u32, u16, u16, u8[8]}, Windows layout) -> canonical lower-case text.

    The same text Kiln's services print (native/services/content/game.cpp guid_text)."""
    return str(uuid.UUID(bytes_le=bytes(b)))


def guid_bytes(text):
    """Inverse of guid_text."""
    return uuid.UUID(text).bytes_le


def is_null_guid(b):
    return not any(b)


def out_dir(sub=""):
    """Default output folder: $KILN_OUT, else <repo>/.out/models."""
    base = os.environ.get("KILN_OUT") or str(Path(__file__).resolve().parents[3] / ".out" / "models")
    p = Path(base) / sub if sub else Path(base)
    p.mkdir(parents=True, exist_ok=True)
    return p


def u32(b, o):
    return struct.unpack_from("<I", b, o)[0]


def i32(b, o):
    return struct.unpack_from("<i", b, o)[0]


def u64(b, o):
    return struct.unpack_from("<Q", b, o)[0]
