#!/usr/bin/env bash
# SPDX-License-Identifier: MIT
# kiln.engine host suite: generator check, C layout, the gate path lint, identity, owner/ECS resolution and the Scene
# gate over a fake engine, and the real DLL entry over a fake core; ASan/UBSan and ThreadSanitizer builds (any-thread
# members). Run with KILN_OUT set.
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
root=$(cd "$here/../../../.." && pwd)
out=${KILN_OUT:?set KILN_OUT}/engine-host-test
mkdir -p "$out"
python3 -I "$root/tools/engine/generate.py" --check
python3 -I "$root/tools/engine/test_generate.py"
# validate.py's ecsReadLayout checks (synthetic code; the real exe copies too when KILN_ENGINE_BINARIES names them)
python3 -I "$root/tools/engine/test_validate.py"
python3 -I "$here/lint_gate.py"
python3 -I "$here/stamp_fixtures.py" --self-test
cc=${CC:-gcc}
cxx=${CXX:-g++}
"$cc" -std=c11 -Wall -Wextra -Werror -fsyntax-only -I"$root/native/core/abi" "$here/abi_test.c"
inc=(-I"$here/.." -I"$here/../include" -I"$root/native/core/abi")
src=("$here/../identity.cpp")
resolve=("$here/../resolve.cpp" "$here/host/memory_host.cpp")
gate=("$here/../gate.cpp")
for kind in asan tsan; do
  if [[ $kind == asan ]]; then san=(-fsanitize=address,undefined -fno-sanitize-recover=undefined); else san=(-fsanitize=thread); fi
  "$cxx" -std=c++20 -O1 -g -Wall -Wextra -Werror -pthread "${san[@]}" "${inc[@]}" "$here/identity_test.cpp" "${src[@]}" \
    -o "$out/identity_test_$kind"
  TSAN_OPTIONS=halt_on_error=1:exitcode=66 "$out/identity_test_$kind"
  "$cxx" -std=c++20 -O1 -g -Wall -Wextra -Werror -pthread "${san[@]}" "${inc[@]}" "$here/resolve_test.cpp" \
    "${resolve[@]}" -o "$out/resolve_test_$kind"
  TSAN_OPTIONS=halt_on_error=1:exitcode=66 "$out/resolve_test_$kind"
  # gate_test replaces operator new/delete (malloc/free) to count allocations inside the critical section
  "$cxx" -std=c++20 -O1 -g -Wall -Wextra -Werror -Wno-mismatched-new-delete -pthread -DKILN_ENGINE_GATE_TEST_POINTS "${san[@]}" "${inc[@]}" "$here/gate_test.cpp" \
    "${resolve[@]}" "${gate[@]}" "$here/../engine.cpp" "${src[@]}" -o "$out/gate_test_$kind"
  TSAN_OPTIONS=halt_on_error=1:exitcode=66 "$out/gate_test_$kind"
  "$cxx" -std=c++20 -O1 -g -Wall -Wextra -Werror -pthread "${san[@]}" "${inc[@]}" "$here/plugin_host_test.cpp" \
    "$here/../engine.cpp" "${src[@]}" "${resolve[@]}" "${gate[@]}" -o "$out/plugin_host_test_$kind"
  TSAN_OPTIONS=halt_on_error=1:exitcode=66 "$out/plugin_host_test_$kind"
done
