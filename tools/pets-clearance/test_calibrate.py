#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
# tools/pets-clearance/calibrate.sh run, without a game: a fake lab folder (KILN_LABS), a start command that starts nothing.
#   python3 -I tools/pets-clearance/test_calibrate.py
import os
import subprocess
import sys
import tempfile
import time
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
SCRIPT = os.path.join(HERE, "calibrate.sh")
LAB = "test-calibrate-fake"


class Run(unittest.TestCase):
    def test_a_lab_server_that_is_not_running_ends_the_wait_at_once(self):
        """A server that ended (crashed, or never came up) must not hold the shared lab lock until the timeout."""
        with tempfile.TemporaryDirectory() as d:
            labs = os.path.join(d, "labs")
            mod = os.path.join(labs, LAB, "kiln", "mods", "pets-clearance-lab")
            os.makedirs(mod)
            open(os.path.join(mod, "PetsClearanceLab.dll"), "wb").close()
            env = {**os.environ, "KILN_LABS": labs, "KILN_LABSTART": "true"}
            t0 = time.monotonic()
            p = subprocess.run(["bash", SCRIPT, "run", "--lab", LAB, "--out", os.path.join(d, "out"), "--timeout", "40"],
                               env=env, capture_output=True, text=True, timeout=120)
            took = time.monotonic() - t0
            self.assertNotEqual(p.returncode, 0, p.stdout + p.stderr)
            self.assertLess(took, 30, p.stderr)
            self.assertIn("lab server stopped", p.stderr)
            self.assertTrue(os.path.isdir(os.path.join(d, "out")))


if __name__ == "__main__":
    sys.exit(unittest.main())
