#!/usr/bin/env bash
# SPDX-License-Identifier: MIT
# kiln.tasks host suite: the production model, pool and binding (src/tasks_model.cpp, src/tasks.cpp) under
# their overlap tests, built twice: ASan/UBSan and ThreadSanitizer, each halting on the first report. No game, no Wine.
#   native/core/tests/test-tasks.sh [test-name]
# Output: $KILN_OUT/tasks-tests when KILN_OUT is set, else a private temporary directory removed on exit.
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
if [[ -n ${KILN_OUT:-} ]]; then
  out=$KILN_OUT/tasks-tests
  mkdir -p "$out"
else
  out=$(mktemp -d "${TMPDIR:-/tmp}/kiln-tasks-tests.XXXXXX")
  trap 'rm -rf "$out"' EXIT
fi
cxx=${CXX:-$(command -v clang++ || command -v g++)}
common=(-std=c++20 -O1 -g -Wall -Wextra -Werror -pthread -I"$here/../src" -I"$here/../abi" -I"$here")
model=("$here/../src/tasks_model.cpp")
pool=("$here/../src/tasks_model.cpp" "$here/../src/tasks.cpp" "$here/../src/tasks_stats.cpp")
for kind in asan tsan; do
  if [[ $kind == asan ]]; then
    san=(-fsanitize=address,undefined -fno-sanitize-recover=undefined)
  else
    san=(-fsanitize=thread)
  fi
  "$cxx" "${common[@]}" "${san[@]}" "$here/tasks_model_test.cpp" "${model[@]}" -o "$out/tasks_model_test_$kind"
  "$cxx" "${common[@]}" "${san[@]}" "$here/tasks_pool_test.cpp" "${pool[@]}" -o "$out/tasks_pool_test_$kind"
  "$cxx" "${common[@]}" "${san[@]}" "$here/tasks_api_test.cpp" "${pool[@]}" -o "$out/tasks_api_test_$kind"
  "$cxx" "${common[@]}" "${san[@]}" "$here/tasks_edges_test.cpp" "${pool[@]}" -o "$out/tasks_edges_test_$kind"
  "$cxx" "${common[@]}" "${san[@]}" "$here/tasks_host_test.cpp" "${pool[@]}" -o "$out/tasks_host_test_$kind"
  "$cxx" "${common[@]}" "${san[@]}" "$here/tasks_stats_test.cpp" "${pool[@]}" -o "$out/tasks_stats_test_$kind"
  # the binding against the real owner registry (src/registry.cpp, included by the test with host stubs)
  "$cxx" "${common[@]}" "${san[@]}" -I"$here/stubs" -I"$here/../../third_party" "$here/tasks_registry_test.cpp" "${pool[@]}" \
    -o "$out/tasks_registry_test_$kind"
  for bin in tasks_model_test tasks_pool_test tasks_api_test tasks_edges_test tasks_host_test tasks_registry_test tasks_stats_test; do
    printf '[%s] ' "$kind"
    rc=0
    TSAN_OPTIONS=halt_on_error=1:exitcode=66 ASAN_OPTIONS=halt_on_error=1 "$out/${bin}_$kind" "$@" || rc=$?
    # exit 2 = this binary has no test by that name (a filter meant for the other binary); skip, do not fail the run
    if [[ $rc != 0 && $rc != 2 ]]; then exit "$rc"; fi
  done
done
# the core's calls into kiln.tasks at the server world's start, Post and STOPPING (tick.cpp, checked as source)
"$cxx" -std=c++20 -O1 -Wall -Wextra -Werror "$here/tasks_wiring_test.cpp" -o "$out/tasks_wiring_test"
"$out/tasks_wiring_test" "$here/../src/tick.cpp" | grep -v '^ok'
