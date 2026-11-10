#!/usr/bin/env bash
# The launcher's backend timings (docs/kiln/launcher/perf.md): builds the fake data set once, then times every command
# the UI calls on it and ranks them.
#   tools/launcher/perf.sh [--gen] [--iters N] [--out FILE]
# --gen: build the data set again (else only when missing). --iters: warm calls per command (default 10).
# --out: the JSON report (default $KILN_OUT/perf/bench.json).
# The data set lives in $KILN_OUT/perf/data (KILN_PERF_DIR overrides; ~0.5 GB). KILN_PERF_RUNTIME_MB sizes the
# installed runtime (default 200). Never opens a window; never touches the real Steam folders, games or launcher data.
set -euo pipefail
here=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
repo=$(cd "$here/../.." && pwd)
export KILN_OUT=${KILN_OUT:-$repo/.out/launcher}
export CARGO_TARGET_DIR=${CARGO_TARGET_DIR:-$KILN_OUT/cargo}
export KILN_PERF_DIR=${KILN_PERF_DIR:-$KILN_OUT/perf/data}
out=$KILN_OUT/perf/bench.json
gen=0
while (($#)); do
  case $1 in
    --gen) gen=1; shift ;;
    --iters) export KILN_PERF_ITERS=$2; shift 2 ;;
    --out) out=$2; shift 2 ;;
    *) echo "unknown argument: $1" >&2; exit 2 ;;
  esac
done
unset DISPLAY WAYLAND_DISPLAY
mkdir -p "$(dirname "$out")"
export KILN_PERF_OUT=$out
cd "$repo"
test_args=(cargo test --release -p kiln-launcher --lib)
if ((gen)) || [[ ! -d $KILN_PERF_DIR/data ]]; then
  "${test_args[@]}" perf::generate -- --ignored --exact --nocapture --test-threads=1
fi
"${test_args[@]}" perf::bench -- --ignored --exact --nocapture --test-threads=1
echo "report: $out"
