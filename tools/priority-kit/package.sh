#!/usr/bin/env bash
# SPDX-License-Identifier: MIT
# Builds the priority kit's zip for testers (docs/kiln/priority-kit.md):
#   KILN_OUT=<build output> tools/priority-kit/package.sh [--no-build] [--out <zip>]
# It builds the test mod and the write path's fault test for Windows (native/build.sh) unless --no-build, then packs
#   kiln-priority-kit/README.txt, the scripts, mod/gate-priority-test/{gate-priority-test.dll,native.json} and
#   tests/world-ecs-fault.exe
# into <out> (default $KILN_OUT/kiln-priority-kit.zip). The Kiln test build itself is a normal Kiln bundle, made apart.
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
repo=$(cd "$here/../.." && pwd)
: "${KILN_OUT:?set KILN_OUT to the build output}"
build=1 out=$KILN_OUT/kiln-priority-kit.zip
while (($#)); do
  case $1 in
    --no-build) build=0; shift ;;
    --out) out=$2; shift 2 ;;
    *) printf 'unknown option %s\n' "$1" >&2; exit 2 ;;
  esac
done
((build)) && KILN_OUT=$KILN_OUT "$repo/native/build.sh" gate-priority-test world-ecs-fault
bin=$KILN_OUT/cmake/bin
for f in "$bin/mods/gate-priority-test/gate-priority-test.dll" "$bin/mods/gate-priority-test/native.json" "$bin/tests/world-ecs-fault.exe"; do
  [[ -s $f ]] || { printf 'missing %s (build it first)\n' "$f" >&2; exit 1; }
done
python3 -I - "$out" "$here" "$bin" "$repo" <<'EOF'
import sys, zipfile
from pathlib import Path
out, kit, bin_, repo = Path(sys.argv[1]), Path(sys.argv[2]), Path(sys.argv[3]), Path(sys.argv[4])
readme = """Kiln priority kit (GitHub issue #58: native Windows vs Proton thread-priority measurement)

The steps, what the kit changes, what to send back and how to undo it are in the Kiln repository, in
docs/kiln/priority-kit.md (also linked from issue #58):
  https://github.com/frostdev-ops/kiln/issues/58

In short, on Windows, in PowerShell in this folder:
  .\\tests\\world-ecs-fault.exe > fault-test.txt
  powershell -ExecutionPolicy Bypass -File kit.ps1 install      (needs the Kiln test build installed)
  ... start the game, load your world, wait a minute, quit ...
  powershell -ExecutionPolicy Bypass -File kit.ps1 world
  run 1: start the game, load the world, then: powershell -ExecutionPolicy Bypass -File cpu-load.ps1
         wait for "Done" and one more minute, quit, then: powershell -ExecutionPolicy Bypass -File collect.ps1
  run 2: powershell -ExecutionPolicy Bypass -File kit.ps1 raise off, then the same as run 1
  post both zips to the issue; undo with: powershell -ExecutionPolicy Bypass -File kit.ps1 uninstall -Logs
On Linux (Proton): kit.sh, cpu-load.sh and collect.sh, the same way.
"""
files = {'README.txt': readme.replace('\n', '\r\n').encode()}
for name in ('kit.ps1', 'collect.ps1', 'cpu-load.ps1', 'kit.sh', 'collect.sh', 'collect.py', 'cpu-load.sh', 'analyze.py'):
    files[name] = (kit / name).read_bytes()
files['mod/gate-priority-test/gate-priority-test.dll'] = (bin_ / 'mods/gate-priority-test/gate-priority-test.dll').read_bytes()
files['mod/gate-priority-test/native.json'] = (bin_ / 'mods/gate-priority-test/native.json').read_bytes()
files['tests/world-ecs-fault.exe'] = (bin_ / 'tests/world-ecs-fault.exe').read_bytes()
files['LICENSE.txt'] = (repo / 'LICENSE').read_bytes()
tmp = out.with_suffix('.part')
with zipfile.ZipFile(tmp, 'w', zipfile.ZIP_DEFLATED) as z:
    for name in sorted(files):
        info = zipfile.ZipInfo('kiln-priority-kit/' + name, date_time=(2026, 1, 1, 0, 0, 0))
        info.external_attr = (0o755 if name.endswith(('.sh', '.py')) else 0o644) << 16
        info.compress_type = zipfile.ZIP_DEFLATED
        z.writestr(info, files[name])
tmp.replace(out)
print('kit: %s (%d files)' % (out, len(files)))
EOF
