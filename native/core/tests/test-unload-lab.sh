#!/usr/bin/env bash
# SPDX-License-Identifier: MIT
# Lab check for KilnPluginInfo.on_unload at process exit (kiln_api.h; the sequence is in core/src/api.cpp, its trigger
# in core/src/boot.cpp). Builds the test plugins (tests/unload/) with native/cmake/clang-cl-xwin.cmake, deploys Kiln
# into the lab with --policy any, and stops the server with `tools/lab/lab.sh stop` (SIGINT) in these runs:
#   baseline  only with KILN_UNLOAD_BASELINE_CORE (a kiln-core.dll without on_unload calls): no test plugin
#   plain     no test plugin
#   markers   unload-test-0-nohandler, -1-marker, -2-marker
#   faults    the same, plus -3-fault (null write), -4-block (never returns) and -5-reexit (calls ExitProcess itself)
#   restart   a start from the save the faults run left, then a plain stop
# Checks (PASS/FAIL lines; exit 1 when one fails):
#   1 marker  no marker file before the stop; after it 1-marker and 2-marker wrote theirs, 0-nohandler did not
#   2 order   unload-order.txt: 2-marker before 1-marker (faults: 5, 4, 3, 2, 1); kiln.log's "called in this order" is
#             the reverse of the load order; the handlers ran after the world stopped, not on the main thread, with
#             the loader lock free
#   3 fault   3-fault was called, kiln.log names it as faulted, a crash report names unload-test-3-fault.dll, and the
#             markers called after it still wrote theirs
#   4 block   4-block was called, kiln.log names it as given up, the markers after it still wrote theirs, and the
#             process was gone within the total limit + 2 s of kiln.log's "the process is exiting" (SIGINT to exit with
#             and without it is reported: the game's own shutdown varies by a second or two)
#   5 once    every marker file holds exactly one call, also with -5-reexit's second exit call during the sequence
#             (kiln.log names it, and the handlers after it still ran)
#   6 clean   plain run: kiln.log ends with the unload lines; no crash line, no "Unhandled" and no core dump of the
#             server's pid; the process was gone within 1 s of kiln.log's "the process is exiting" (what the change
#             adds to the stop); with a baseline: SIGINT to exit within the baseline's + 10 s (only a gross regression:
#             the game's own shutdown took 5 to 39 s on a busy machine) and no error line in the game log and the wine
#             log that the baseline's do not have
#   7 save    restart: the game finds world0.ksd, Kiln.Saves loads the same world at a newer revision than the faults
#             run started from, and the stop runs the unload sequence again
# Usage (the lab must exist: tools/lab/lab.sh new <lab> <port>; one lab server at a time, so hold the lab lock):
#   flock <lock file> native/core/tests/test-unload-lab.sh <lab name> [<KILN_OUT>]
# Env: KILN_LABSTART               the command that starts a lab, given the lab name (default: tools/lab/lab.sh start)
#      KILN_UNLOAD_NO_DEPLOY=1     test what is deployed (no Kiln build, no deploy-server.sh)
#      KILN_UNLOAD_BASELINE_CORE   kiln-core.dll without the change, for check 6's comparison
#      KILN_UNLOAD_RUN_S           seconds each run lets the world step before the stop (default 15)
#      KILN_UNLOAD_BREAK_SAVE=1    empty world0.ksd before the restart run (and put it back after): check 7 must fail
#      KILN_UNLOAD_CHECK_ONLY=<dir>  no lab run: evaluate the checks on an earlier run's folder (<KILN_OUT>/unload-lab)
#      KILN_LABS                   where the labs are (as tools/lab/lab.sh)
set -uo pipefail
here=$(cd "$(dirname "$0")" && pwd)
repo=$(cd "$here/../../.." && pwd)
lab=${1:?usage: test-unload-lab.sh <lab name> [KILN_OUT]}
export KILN_OUT=${2:-${KILN_OUT:-$repo/native/build}}
labs=${KILN_LABS:-${KILN_LAB_ROOT:-$HOME/srv/enshrouded}/labs}
dir=$labs/$lab
start_cmd=${KILN_LABSTART:-$repo/tools/lab/lab.sh start}
run_s=${KILN_UNLOAD_RUN_S:-15}
out=${KILN_UNLOAD_CHECK_ONLY:-$KILN_OUT/unload-lab}
cd "$repo"
fails=0
pass() { echo "PASS $*"; }
fail() { echo "FAIL $*"; fails=$((fails + 1)); }
stop_lab() { tools/lab/lab.sh stop "$lab" >/dev/null 2>&1 || true; }

has_line() { grep -q -- "$2" "$1" 2>/dev/null; }
calls_in() { if [[ -f $1 ]]; then awk '/ call=/ {n++} END {print n + 0}' "$1"; else echo 0; fi; }
order_of() { awk '{print $1}' "$1" 2>/dev/null | sed 's/^unload-test-//' | tr '\n' ' ' | sed 's/ $//'; }
le() { awk "BEGIN{exit !($1 <= $2)}"; }
# seconds from kiln.log's "the process is exiting" line to the process's end (run dir); empty when either is missing
exit_after_start() {
  local start end
  start=$(grep -o -E '^\[. [0-9:.]+\] \[kiln\] unload: the process is exiting' "$1/kiln.log" | head -1 | cut -d' ' -f2 | tr -d ']')
  [[ -n $start && -s $1/exit-time ]] || return 0
  end=$(date -d "@$(cat "$1/exit-time")" +%H:%M:%S.%N)
  awk -v a="$start" -v b="$end" 'BEGIN { split(a, x, ":"); split(b, y, ":"); d = (y[1] * 3600 + y[2] * 60 + y[3]) - (x[1] * 3600 + x[2] * 60 + x[3]); if (d < 0) d += 86400; printf "%.2f\n", d }'
}

if [[ -z ${KILN_UNLOAD_CHECK_ONLY:-} ]]; then
[[ -f $dir/enshrouded_server.exe ]] || { echo "no lab $lab ($dir)" >&2; exit 2; }
rm -rf "$out" && mkdir -p "$out"
trap stop_lab EXIT

# ---- build ----
plugins=$out/plugins-build
cmake -S "$here/unload" -B "$plugins" -G Ninja -DCMAKE_TOOLCHAIN_FILE="$repo/native/cmake/clang-cl-xwin.cmake" \
  -DCMAKE_BUILD_TYPE=Release -Wno-author -Wno-deprecated >/dev/null || { echo "test plugins: cmake failed" >&2; exit 2; }
cmake --build "$plugins" >/dev/null || { echo "test plugins: build failed" >&2; exit 2; }
if [[ ${KILN_UNLOAD_NO_DEPLOY:-} != 1 ]]; then
  tools/deploy-server.sh "$dir" --policy any >"$out/deploy.log" 2>&1 || { echo "deploy failed ($out/deploy.log)" >&2; exit 2; }
fi
grep -q '"policy": "any"' "$dir/kiln/kiln.json" || { echo "$dir/kiln/kiln.json: policy is not any" >&2; exit 2; }
new_core=$out/kiln-core.dll
cp "$dir/kiln/kiln-core.dll" "$new_core"

# ---- one run: start, step, stop (SIGINT), collect ----
# run <name> [plugin...]: copies the plugins in, removes them again afterwards; writes $out/<name>/
run() {
  local name=$1; shift
  local r=$out/$name
  mkdir -p "$r"
  rm -f "$dir/kiln/logs/"unload-*.txt
  rm -rf "$dir/kiln/crash-reports"
  local p
  for p in "$@"; do install -m 644 "$plugins/$p.dll" "$dir/kiln/plugins/"; done
  local since
  since=$(date '+%Y-%m-%d %H:%M:%S')
  $start_cmd "$lab" >"$r/start.txt" 2>&1 || echo "start failed" >>"$r/start.txt"
  local pid
  pid=$(tools/lab/lab.sh pid "$lab")
  echo "$pid" >"$r/pid"
  sleep "$run_s"
  command ls "$dir/kiln/logs/" >"$r/logs-before-stop.txt" 2>/dev/null
  local t0 t1
  t0=$(date +%s.%N)
  ( while [[ -n $pid && -d /proc/$pid ]]; do sleep 0.02; done; date +%s.%N >"$r/exit-time" ) &
  local watcher=$!
  stop_lab
  wait "$watcher"
  t1=$(cat "$r/exit-time")
  awk "BEGIN{printf \"%.2f\n\", $t1 - $t0}" >"$r/sigint-to-exit"
  for p in "$@"; do rm -f "$dir/kiln/plugins/$p.dll"; done
  cp "$dir/kiln/logs/kiln.log" "$r/" 2>/dev/null
  cp "$dir/kiln/logs/"unload-*.txt "$r/" 2>/dev/null
  tr -d '\r' <"$dir/logs/enshrouded_server.log" >"$r/game.log" 2>/dev/null
  tr -d '\r' <"$labs/$lab.wine.log" >"$r/wine.log" 2>/dev/null
  cp -r "$dir/kiln/crash-reports" "$r/" 2>/dev/null
  coredumpctl list --since "$since" --no-pager 2>/dev/null | awk -v p="$pid" '$5 == p' >"$r/coredumps.txt"
  echo "  run $name: pid $pid, SIGINT to exit $(cat "$r/sigint-to-exit") s"
}

# The save from an earlier start, so every measured run starts the same way (the first start of a lab makes a world).
[[ -f $dir/savegame/world0.ksd ]] || run warmup

if [[ -n ${KILN_UNLOAD_BASELINE_CORE:-} ]]; then
  install -m 644 "$KILN_UNLOAD_BASELINE_CORE" "$dir/kiln/kiln-core.dll"
  run baseline
  install -m 644 "$new_core" "$dir/kiln/kiln-core.dll"
fi
run plain
run markers unload-test-0-nohandler unload-test-1-marker unload-test-2-marker
run faults unload-test-0-nohandler unload-test-1-marker unload-test-2-marker unload-test-3-fault unload-test-4-block \
  unload-test-5-reexit
if [[ ${KILN_UNLOAD_BREAK_SAVE:-} == 1 ]]; then  # a check of the check: a broken save must fail check 7
  rm -rf "$out/save-backup" && cp -a "$dir/savegame" "$out/save-backup" && : >"$dir/savegame/world0.ksd"
fi
run restart
if [[ ${KILN_UNLOAD_BREAK_SAVE:-} == 1 ]]; then rm -rf "$dir/savegame" && cp -a "$out/save-backup" "$dir/savegame"; fi
trap - EXIT
fi  # KILN_UNLOAD_CHECK_ONLY

M=$out/markers F=$out/faults P=$out/plain R=$out/restart
first_rev=$(grep -o 'saves: hooks request.*rev [0-9]*' "$M/kiln.log" | grep -o '[0-9]*$')
faults_rev=$(grep -o 'saves: hooks request.*rev [0-9]*' "$F/kiln.log" | grep -o '[0-9]*$')
echo "== checks ($out)"
# 1 marker
if ! grep -q '^unload-' "$M/logs-before-stop.txt" && [[ -f $M/unload-test-1-marker.txt && -f $M/unload-test-2-marker.txt ]] &&
   [[ ! -f $M/unload-test-0-nohandler.txt ]]; then
  pass "1 marker: none before the stop; 1-marker and 2-marker after it; 0-nohandler (no on_unload) none"
else
  fail "1 marker: before the stop: $(grep '^unload-' "$M/logs-before-stop.txt" | tr '\n' ' '); after: $(command ls "$M" | grep '^unload-' | tr '\n' ' ')"
fi
# 2 order (and the moment and thread)
mo=$(order_of "$M/unload-order.txt") fo=$(order_of "$F/unload-order.txt")
loaded=$(grep -o -E '\] \[kiln\] (service|plugin) [^ ]+ loaded:' "$M/kiln.log" | awk '{print $4}' | tac | tr '\n' ' ')
called=$(grep -o 'unload: called in this order: .*' "$M/kiln.log" | sed 's/unload: called in this order: //' | tr ',' '\n' | awk '{print $1}' | tr '\n' ' ')
subseq=$(python3 -c "import sys; a=sys.argv[1].split(); b=iter(sys.argv[2].split()); print(int(bool(a) and all(x in b for x in a)))" "$called" "$loaded")
world_line=$(grep -n 'tick: server world .* stopping' "$M/kiln.log" | head -1 | cut -d: -f1)
unload_line=$(grep -n 'unload: the process is exiting' "$M/kiln.log" | head -1 | cut -d: -f1)
if [[ $mo == "2-marker 1-marker" && $fo == "5-reexit 4-block 3-fault 2-marker 1-marker" && $subseq == 1 && -n $world_line &&
      -n $unload_line && $unload_line -gt $world_line ]] && ! grep -q -E 'main=yes|loaderlock=held' "$M/unload-order.txt"; then
  pass "2 order: markers $mo; faults $fo; kiln.log's order is the reverse load order ($(echo "$called" | wc -w) modules); after the world stopped, off the main thread, loader lock free"
else
  fail "2 order: markers '$mo', faults '$fo', kiln.log '$called' (reverse load order: $subseq), world stop line $world_line, unload line $unload_line, threads: $(awk '{print $4, $5}' "$M/unload-order.txt" | tr '\n' ';')"
fi
# 3 fault
if [[ $fo == *"3-fault 2-marker 1-marker"* ]] && has_line "$F/kiln.log" 'unload: unload-test-3-fault.dll: on_unload faulted' &&
   grep -l -s 'unload-test-3-fault.dll' "$F"/crash-reports/*.txt >/dev/null && [[ -f $F/unload-test-1-marker.txt ]]; then
  pass "3 fault: 3-fault faulted, kiln.log names it, crash report $(basename "$(grep -l -s 'unload-test-3-fault.dll' "$F"/crash-reports/*.txt | head -1)"), the markers after it ran"
else
  fail "3 fault: order '$fo'; kiln.log: $(grep -o 'unload: unload-test-3-fault[^;]*' "$F/kiln.log" | head -1); crash reports: $(command ls "$F/crash-reports" 2>/dev/null | tr '\n' ' ')"
fi
# 4 block
limit_ms=$(grep -o 'up to [0-9]* ms each' "$F/kiln.log" | head -1 | grep -o '[0-9]*')
total_ms=$(grep -o 'ms each, [0-9]* ms in all' "$F/kiln.log" | head -1 | grep -o '[0-9]* ms in all' | grep -o '[0-9]*')
tm=$(cat "$M/sigint-to-exit") tf=$(cat "$F/sigint-to-exit") gone=$(exit_after_start "$F")
if [[ $fo == *"4-block 3-fault"* ]] && has_line "$F/kiln.log" 'unload: unload-test-4-block.dll: on_unload has not returned after' &&
   [[ -f $F/unload-test-2-marker.txt && -n $limit_ms && -n $total_ms && -n $gone ]] && le "$gone" "$(awk "BEGIN{print $total_ms / 1000 + 2}")"; then
  pass "4 block: given up after ${limit_ms} ms, named in kiln.log; the process was gone $gone s after the sequence began (limit ${total_ms} ms + 2 s); SIGINT to exit $tf s with it, $tm s without"
else
  fail "4 block: order '$fo', limits '${limit_ms}'/'${total_ms}', gone '$gone' s after the sequence began, SIGINT to exit $tf s with it, $tm s without; kiln.log: $(grep -o 'unload: unload-test-4-block[^;]*' "$F/kiln.log" | head -1)"
fi
# 5 once
once=1
for f in "$M"/unload-test-*.txt "$F"/unload-test-*.txt; do [[ $(calls_in "$f") == 1 ]] || once=0; done
if ((once)) && [[ $(grep -c . "$M/unload-order.txt") == 2 && $(grep -c . "$F/unload-order.txt") == 5 && $fo == "5-reexit "* ]] &&
   has_line "$F/kiln.log" 'unload: unload-test-5-reexit.dll: its on_unload ended the process itself' &&
   has_line "$F/kiln.log" 'unload: done in .*the process exits'; then
  pass "5 once: every handler ran exactly once; 5-reexit's own exit call did not start the sequence again, the 4 after it ran"
else
  fail "5 once: $(for f in "$M"/unload-test-*.txt "$F"/unload-test-*.txt; do echo -n "$(basename "$f") $(calls_in "$f"); "; done)"
fi
# 6 clean
tp=$(cat "$P/sigint-to-exit")
# error-like lines of a run's game log and wine log, numbers masked ("Default" is not a fault)
clean_lines() {
  cat "$1/game.log" "$1/wine.log" 2>/dev/null | grep -i -E '\b(unhandled|exception|crash|fault|segfault)\b|err:' |
    sed -E 's/^\[[^]]*\] //; s/0x[0-9a-f]+/0x?/g; s/[0-9]+/N/g' | sort -u
}
new_lines=""
[[ -d $out/baseline ]] && new_lines=$(comm -13 <(clean_lines "$out/baseline") <(clean_lines "$P"))
pg=$(exit_after_start "$P")
tail_ok=0
tail -n 3 "$P/kiln.log" | grep -q 'unload: done in .* the process exits' && tail_ok=1
if ((tail_ok)) && [[ ! -s $P/coredumps.txt && -z $new_lines && -n $pg ]] && ! grep -q -s 'Unhandled' "$P/wine.log" &&
   [[ ! -d $P/crash-reports ]] && le "$pg" 1 &&
   { [[ ! -d $out/baseline ]] || le "$tp" "$(awk "BEGIN{print $(cat "$out/baseline/sigint-to-exit") + 10}")"; }; then
  pass "6 clean: kiln.log ends with the unload lines, no crash line, no core dump; gone $pg s after the sequence began; SIGINT to exit $tp s$([[ -d $out/baseline ]] && echo ", baseline $(cat "$out/baseline/sigint-to-exit") s, no new error line" || echo " (no baseline given)")"
else
  fail "6 clean: kiln.log tail $tail_ok, core dumps '$(cat "$P/coredumps.txt")', new lines '$new_lines', gone '$pg' s after the sequence began, SIGINT to exit $tp s$([[ -d $out/baseline ]] && echo " (baseline $(cat "$out/baseline/sigint-to-exit") s)")"
fi
# 7 save
world=$(grep -o -E 'saves: hooks request.*world [0-9a-f-]+ rev' "$F/kiln.log" | grep -o -E '[0-9a-f]{8}-[0-9a-f-]+')
restart_rev=$(grep -o 'saves: hooks request.*rev [0-9]*' "$R/kiln.log" | grep -o '[0-9]*$')
if has_line "$R/start.txt" ' up after ' && has_line "$R/game.log" "Found file 'world0.ksd'" && [[ -n $world && -n $restart_rev && -n $faults_rev ]] &&
   has_line "$R/kiln.log" "saves: world loaded: .*$world, revision $restart_rev" && ((restart_rev > faults_rev)) &&
   has_line "$R/kiln.log" 'unload: done in'; then
  pass "7 save: restart loaded world $world at revision $restart_rev (the faults run started from $faults_rev, markers from $first_rev) and stopped with the unload sequence"
else
  fail "7 save: start '$(tail -1 "$R/start.txt")', world '$world', revisions faults $faults_rev restart $restart_rev, loaded: $(grep -o 'saves: world loaded: [^(]*([^)]*)[^,]*, [^,]*, revision [0-9]*' "$R/kiln.log" | head -1)"
fi
echo "== $((7 - fails)) of 7 passed"
((fails == 0))
