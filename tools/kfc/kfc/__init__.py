# SPDX-License-Identifier: MIT
"""Kiln's clean-room, read-only reader for Keen's KFC4 game data (Enshrouded).

Written from EMF 0.3.3's MIT description of the files (docs/engine_parts/re_content.md section 1.1) and Kiln's own
analysis of the game files and the game executable's reflection records. No kfc-parser code. See docs/re/models.md.

Modules:
    pe       minimal PE image loader (for the reflection extractor)
    refl     the game's own reflection records, read offline from enshrouded(.exe|_server.exe) -> TypeDb
    archive  the .kfc directory, .kfc_resources (zstd chunks) and the .dat content containers
    blob     decodes a reflected struct (BlobArray, BlobString, BlobOptional, BlobVariant, ...) into Python values
    mesh     keen::RenderModel -> triangles (positions, normals, uvs) and OBJ / glTF export
    anim     keen::JointAnimation: the clip codec, both ways, a sampler and the glTF export (docs/re/npc-internals.md)
    animgraph  skeleton and animation graph writers, a graph printer
"""
from .util import fnv1a32, guid_bytes, guid_text  # noqa: F401
