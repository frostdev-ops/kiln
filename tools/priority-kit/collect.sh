#!/usr/bin/env bash
# SPDX-License-Identifier: MIT
# The priority kit's collection script for Linux and Proton: collect.py does the work (python3, nothing to install).
#   tools/priority-kit/collect.sh [--game <the Enshrouded folder>] [--out <zip>] [--keep-switch]
exec python3 -I "$(cd "$(dirname "$0")" && pwd)/collect.py" "$@"
