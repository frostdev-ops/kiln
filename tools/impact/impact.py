#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""Entry point: python3 tools/impact/impact.py --help (see impact/cli.py)."""
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

from impact.cli import main  # noqa: E402

if __name__ == "__main__":
    sys.exit(main())
