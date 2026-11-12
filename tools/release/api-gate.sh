#!/usr/bin/env bash
# The API gate of a release (the public API record managed/**/PublicAPI.{Shipped,Unshipped}.txt and the mod API version
# in managed/Kiln.Api/Mods.cs). release.sh calls it; a worker can run it by hand.
#   tools/release/api-gate.sh removed OLD [NEW|--worktree]       shipped API lines removed since OLD (exit 1: named)
#   tools/release/api-gate.sh pending [REV|--worktree]           Unshipped.txt files that hold lines, with counts
#   tools/release/api-gate.sh version-rule OLD [NEW|--worktree]  new API pending needs a higher API version than OLD's
#   tools/release/api-gate.sh promote [--if-same-as REV]         Unshipped.txt lines into Shipped.txt (working tree)
#   tools/release/api-gate.sh api-version [REV|--worktree]       the API version in Mods.cs
#   tools/release/api-gate.sh bump-api [VERSION] [--major]       raise it (default the next minor) in Mods.cs,
#                                                                Kiln.Sdk's Kiln.Version.props and kiln-manage's profile.rs
# NEW and REV default to HEAD. Details: tools/release/lib/apigate.py. Tests: python3 -I tools/release/test_apigate.py
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
exec python3 -I "$here/lib/apigate.py" --repo "${KILN_API_GATE_REPO:-$(cd "$here/../.." && pwd)}" "$@"
