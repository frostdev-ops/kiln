# SPDX-License-Identifier: MIT
"""An empty mirror object selects the same defaults as mirror: true."""
import unittest

from test_clipops import asym_clip, asym_rig, source_of, worst
from anicook import clipops as CO


class MirrorDefaults(unittest.TestCase):
    def test_empty_object_enables_default_mirror(self):
        sk = asym_rig()
        frames, rm = asym_clip(sk, n=12)
        src = source_of(sk, frames, "source", rm=rm)

        def rework(**ops):
            spec = {"from": "source", **ops}
            CO.check(spec, "test")
            return CO.rework("test", spec, src, sk, None)

        enabled = rework(mirror=True)
        empty = rework(mirror={})
        self.assertEqual(empty.frames, enabled.frames, "mirror: {} must enable default mirroring")
        self.assertEqual(empty.rm, enabled.rm, "root motion follows default mirroring too")
        plain = rework()
        disabled = rework(mirror=False)
        self.assertEqual(disabled.frames, plain.frames)
        self.assertEqual(disabled.rm, plain.rm)
        self.assertGreater(worst(sk, plain.frames, empty.frames)[0], 10.0, "default mirroring is not a no-op")
