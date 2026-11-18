#!/usr/bin/env bash
# SPDX-License-Identifier: MIT
# The priority kit's CPU-load helper for Linux (Proton) (docs/kiln/priority-kit.md): the same schedule as cpu-load.ps1.
# Busy processes at one nice level, in loaded and idle phases, with a hard time limit. Run it in its own terminal while
# the game runs.
#
#   tools/priority-kit/cpu-load.sh                                   # every logical CPU, normal, the default schedule
#   tools/priority-kit/cpu-load.sh --threads 8 --priority above --load 120 --cycles 3
#
# --priority below|normal|above is nice 5, 0 or -5 (above needs a nice limit that allows it: `ulimit -e`; without one
# it says so and runs at 0). The schedule: idle for --idle seconds, then --cycles times (loaded --load s, idle --idle s);
# the default is 2 + 3 x (2 + 2) = 14 minutes. Each phase's start and end (UTC) go to a CSV the analysis reads
# (cpu-load-<UTC time>.csv next to this script, or --out).
#
# Stopping: Ctrl+C, closing the terminal or a kill of this script stops every busy process (each also runs under
# `timeout` for its phase), and nothing runs past --max-minutes (default 60).
set -euo pipefail
threads=$(nproc)
priority=normal
load=120 idle=120 cycles=3 max_minutes=60 out=
usage() { sed -n '3,17p' "$0" | sed 's/^# \{0,1\}//'; exit 2; }
while (($#)); do
  case $1 in
    --threads) threads=$2; shift 2 ;;
    --priority) priority=$2; shift 2 ;;
    --load) load=$2; shift 2 ;;
    --idle) idle=$2; shift 2 ;;
    --cycles) cycles=$2; shift 2 ;;
    --max-minutes) max_minutes=$2; shift 2 ;;
    --out) out=$2; shift 2 ;;
    -h|--help) usage ;;
    *) printf 'unknown option %s\n' "$1" >&2; usage ;;
  esac
done
int_in() { [[ $2 =~ ^[0-9]+$ ]] && (($2 >= $3 && $2 <= $4)) || { printf '%s must be %s..%s\n' "$1" "$3" "$4" >&2; exit 2; }; }
int_in --threads "$threads" 1 256
int_in --load "$load" 1 3600
int_in --idle "$idle" 1 3600
int_in --cycles "$cycles" 1 20
int_in --max-minutes "$max_minutes" 1 240
case $priority in
  below) nice_level=5 ;;
  normal) nice_level=0 ;;
  above) nice_level=-5 ;;
  *) printf -- '--priority must be below, normal or above\n' >&2; exit 2 ;;
esac
total=$((idle + cycles * (load + idle)))
((total <= max_minutes * 60)) || { printf 'The schedule takes %s s, more than --max-minutes %s.\n' "$total" "$max_minutes" >&2; exit 2; }
if ((nice_level < 0)); then
  if ! nice -n "$nice_level" true 2>/dev/null || [[ $(nice -n "$nice_level" nice) != "$nice_level" ]]; then
    printf 'nice %s is not allowed here (ulimit -e); the load runs at nice 0 and the CSV says so\n' "$nice_level" >&2
    nice_level=0
  fi
fi
here=$(cd "$(dirname "$0")" && pwd)
[[ -n $out ]] || out=$here/cpu-load-$(date -u +%Y%m%d-%H%M%S).csv

pids=()
stop_load() {
  local p
  for p in "${pids[@]}"; do kill "$p" 2>/dev/null || true; done
  for p in "${pids[@]}"; do wait "$p" 2>/dev/null || true; done
  pids=()
}
utc_ms() { date -u +%s%3N; }
phase= phase_start=0
finish() {
  stop_load
  if [[ -n $phase ]]; then  # stopped part-way: the phase as far as it ran
    printf '%s,%s,%s,%s,%s\n' "$phase" "$phase_start" "$(utc_ms)" "$([[ $phase == load ]] && echo "$threads" || echo 0)" "nice$nice_level" >> "$out"
    phase=
  fi
}
trap 'finish; printf "Stopped. Phases written to %s\n" "$out"; exit 130' INT TERM HUP
trap finish EXIT

printf 'phase,start_utc_ms,end_utc_ms,threads,priority\n' > "$out"
schedule=(idle:"$idle")
for ((c = 0; c < cycles; c++)); do schedule+=(load:"$load" idle:"$idle"); done
printf 'CPU load: %s busy process(es) at nice %s, %s phase(s), %s minutes; Ctrl+C stops it. Phases go to %s\n' \
  "$threads" "$nice_level" "${#schedule[@]}" "$((total / 60))" "$out"
hard_stop=$(( $(date -u +%s) + max_minutes * 60 ))
for item in "${schedule[@]}"; do
  phase=${item%%:*} seconds=${item#*:}
  now=$(date -u +%s)
  ((now + seconds <= hard_stop)) || seconds=$((hard_stop - now))
  ((seconds > 0)) || break
  phase_start=$(utc_ms)
  printf '%s %s for %s s\n' "$(date +%T)" "$phase" "$seconds"
  if [[ $phase == load ]]; then
    for ((i = 0; i < threads; i++)); do
      timeout --signal=KILL "$((seconds + 2))" nice -n "$nice_level" bash -c 'while :; do :; done' &
      pids+=($!)
    done
  fi
  sleep "$seconds"
  stop_load
  printf '%s,%s,%s,%s,%s\n' "$phase" "$phase_start" "$(utc_ms)" "$([[ $phase == load ]] && echo "$threads" || echo 0)" "nice$nice_level" >> "$out"
  phase=
done
printf 'Done. Phases written to %s\n' "$out"
