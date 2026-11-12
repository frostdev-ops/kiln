#!/usr/bin/env bash
# SPDX-License-Identifier: MIT
# Run the actual exit sequence and trigger with deterministic Win32 stubs; no Wine or game.
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
out=${KILN_OUT:-$here/../../build}/unload-sequence-test
mkdir -p "$out"
python3 -I - "$here/../src/api.cpp" "$here/../src/boot.cpp" "$out" <<'PY'
import pathlib, sys
api, boot = (pathlib.Path(p).read_text() for p in sys.argv[1:3])
out = pathlib.Path(sys.argv[3])
start = api.index('namespace {', api.index('// ---- on_unload at process exit'))
end = api.rindex('}  // namespace kiln')
(out / 'unload_sequence.inc').write_text(api[start:end])
start = boot.index('void* g_exit_next =')
end = boot.index('// ---- on_unload: is the exit link armed?', start)
(out / 'unload_trigger.inc').write_text(boot[start:end])
PY
"${CXX:-clang++}" -std=c++20 -O1 -g -Wall -Wextra -Werror -Wno-format -Wno-unused-function -Wno-unused-parameter \
  -fsanitize=address,undefined -I "$out" -I "$here/../abi" "$here/unload_sequence_test.cpp" -o "$out/unload_sequence_test"
"$out/unload_sequence_test"
