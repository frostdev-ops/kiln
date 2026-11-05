# SPDX-License-Identifier: MIT
"""tools/kfc: keen::ContentHash and the SPIR-V reflector, with no game files.

    python3 -m unittest discover -s tools/kfc/tests -v

The vectors below were checked against the game: the same code reproduces the ContentHash of every blob tried of client
26430625 (2,921 VFX shader modules, 57 GPC code blobs, 1,495 random content blobs; docs/re/vfx.md §6.2).
"""
import struct
import sys
import unittest
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent))

from kfc.contenthash import content_hash, keen_hash128  # noqa: E402
from kfc.spirv import MAGIC, Module  # noqa: E402


class ContentHashTest(unittest.TestCase):
    def test_empty_is_the_games_empty_hash(self):
        # The game compares against these constants in its pipeline request (client 0x1a0010): this is the empty hash.
        self.assertEqual(content_hash(b""), (0, 0xE93D8778, 0x6D71D111, 0xFD535C96))

    def test_vectors(self):
        vectors = {
            b"a": "6a2924cb720b42a7dfa8c9f7d2961b9e",
            bytes(range(16)): "525399fa7c32a7d3ce7ce572cf12109f",
            bytes(range(64)): "65894db736bd54ce7e0ef8d8d5e4a08c",
            bytes(range(100)): "c0a73764cb9aa382a53bb0f732f86207",
            bytes(range(256)) * 3: "fc6484a4598e2f95104fb940ebf4e865",
        }
        for data, want in vectors.items():
            self.assertEqual(keen_hash128(data).hex(), want, f"{len(data)} bytes")

    def test_shape(self):
        data = bytes(range(100))
        h = content_hash(data)
        self.assertEqual(h[0], 100)
        self.assertEqual(struct.pack("<3I", *h[1:]), keen_hash128(data)[4:])


class SpirvTest(unittest.TestCase):
    @staticmethod
    def _module():
        def ins(op, *words):
            return [(len(words) + 1) << 16 | op, *words]

        name = struct.unpack("<2I", b"cs_main\0")
        w = [MAGIC, 0x00010500, 0, 16, 0]
        w += ins(17, 1)                       # OpCapability Shader
        w += ins(14, 0, 1)                    # OpMemoryModel
        w += ins(15, 5, 1, *name)             # OpEntryPoint GLCompute %1 "cs_main"
        w += ins(16, 1, 17, 32, 1, 1)         # OpExecutionMode %1 LocalSize 32 1 1
        w += ins(71, 5, 34, 1)                # OpDecorate %5 DescriptorSet 1
        w += ins(71, 5, 33, 42)               # OpDecorate %5 Binding 42
        w += ins(72, 4, 0, 35, 0)             # OpMemberDecorate %4 0 Offset 0
        w += ins(21, 2, 32, 0)                # %2 = OpTypeInt 32 0
        w += ins(29, 3, 2)                    # %3 = OpTypeRuntimeArray %2
        w += ins(30, 4, 3)                    # %4 = OpTypeStruct %3
        w += ins(32, 6, 12, 4)                # %6 = OpTypePointer StorageBuffer %4
        w += ins(59, 6, 5, 12)                # %5 = OpVariable %6 StorageBuffer
        return struct.pack(f"<{len(w)}I", *w)

    def test_reflects_the_interface(self):
        m = Module(self._module())
        self.assertEqual(m.version, (1, 5))
        self.assertEqual(m.capabilities, {1})
        self.assertEqual(m.entry_points, [(5, "cs_main")])
        self.assertEqual(m.local_size, (32, 1, 1))
        self.assertEqual(list(m.bindings()), [(1, 42)])
        self.assertFalse(m.has_push_constants())
        self.assertIn("+0: u32[]", m.describe_struct(m.resolve_pointee(5)))

    def test_rejects_other_bytes(self):
        with self.assertRaises(ValueError):
            Module(b"\0" * 32)


if __name__ == "__main__":
    unittest.main()
