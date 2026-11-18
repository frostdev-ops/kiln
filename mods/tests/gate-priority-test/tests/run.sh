#!/usr/bin/env bash
# SPDX-License-Identifier: MIT
# Host tests of gate-priority-test: the mod's code against a fake Kiln whose kiln.world is the real compare-exchange
# code over the fake engine (tests/harness.h), every scenario under ASan/UBSan and ThreadSanitizer, one process each.
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
root=$(cd "$here/../../../.." && pwd)
out=${KILN_OUT:?set KILN_OUT}/gate-priority-test
mkdir -p "$out"
engine=$root/native/services/engine
world=$root/native/services/world
scenarios=(padding write raise-off noop named named-offset named-field named-gap tracked no-padding other-world no-world no-file bad-config stop stop-warmup uncertain-change uncertain-restore)
for kind in asan tsan; do
  if [[ $kind == asan ]]; then san=(-fsanitize=address,undefined -fno-sanitize-recover=undefined); else san=(-fsanitize=thread); fi
  g++ -std=c++20 -O1 -g -pthread -Wall -Wextra -Werror "${san[@]}" -I"$here/.." -I"$here" -I"$engine" -I"$engine/include" \
      -I"$engine/tests" -I"$world" -I"$world/tests" -I"$root/native/core/abi" -I"$root/native/third_party" \
      "$here/kit_test.cpp" "$here/../kit.cpp" "$world/ecs_engine.cpp" "$world/ecs_lookup.cpp" "$world/ecs_reads.cpp" \
      "$world/ecs_writes.cpp" "$world/ecs_write_hold.cpp" "$world/ecs_write_policy.cpp" "$engine/resolve.cpp" \
      "$engine/gate.cpp" "$engine/tests/host/memory_host.cpp" -o "$out/kit_test_$kind"
  for s in "${scenarios[@]}"; do
    log=$out/$kind-$s.log
    if ! TSAN_OPTIONS=halt_on_error=1:exitcode=66 "$out/kit_test_$kind" "$s" "$root/sigdb/types" > "$log" 2>&1; then
      grep -E '^FAIL|ERROR|WARNING: ThreadSanitizer|runtime error' "$log" | head -20 || true
      printf 'gate-priority-test [%s] %s: FAILED (log: %s)\n' "$kind" "$s" "$log"
      exit 1
    fi
    printf 'gate-priority-test [%s] %s\n' "$kind" "$(tail -1 "$log")"
  done
done
