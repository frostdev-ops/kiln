#!/usr/bin/env bash
# SPDX-License-Identifier: MIT
# The priority kit's script tests (KILN_OUT=<build output> tools/priority-kit/tests/run.sh):
#   1. test_kit.py: analyze.py's numbers, collect.py, kit.sh and cpu-load.sh, on made-up data;
#   2. the PowerShell scripts with PowerShell's own parser (pwsh when it is installed, else the PowerShell SDK through
#      tests/psparse, which needs dotnet and restores the SDK from NuGet once);
#   3. the same scripts run once: cpu-load.ps1 for a 3-second schedule, then kit.ps1 install / world / raise / status /
#      uninstall and collect.ps1 against a fake game folder. Under the SDK on Linux that is PowerShell 7 with no
#      registry and no CIM: what Windows alone has is not run here.
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
kit=$(cd "$here/.." && pwd)
out=${KILN_OUT:?set KILN_OUT}/priority-kit-test
rm -rf "$out"
mkdir -p "$out"
python3 -I "$here/test_kit.py" 2>&1 | tail -3

if command -v pwsh >/dev/null; then
  ps_parse() {
    pwsh -NoProfile -Command '$bad = 0; foreach ($f in $args) { $e = $null; [void][System.Management.Automation.Language.Parser]::ParseFile($f, [ref]$null, [ref]$e); $e | ForEach-Object { "{0}:{1}: {2}" -f $f, $_.Extent.StartLineNumber, $_.Message; $bad++ }; "{0}: {1} parse errors" -f (Split-Path -Leaf $f), @($e).Count }; exit [int]($bad -gt 0)' "$@"
  }
  ps_run() {
    local script=$1; shift
    local a=() n v
    while (($#)); do n=$1 v=$2; shift 2; if [[ $v == '$true' ]]; then a+=("$n"); else a+=("$n" "$v"); fi; done
    pwsh -NoProfile -ExecutionPolicy Bypass -File "$script" "${a[@]}"
  }
  echo "powershell: pwsh $(pwsh -NoProfile -Command '$PSVersionTable.PSVersion.ToString()')"
elif command -v dotnet >/dev/null; then
  dotnet build "$here/psparse/psparse.csproj" -c Release -o "$out/psparse" --artifacts-path "$out/psparse-obj" -v q -nologo >"$out/psparse-build.log" 2>&1 ||
    { tail -20 "$out/psparse-build.log"; exit 1; }
  ps_parse() { dotnet "$out/psparse/psparse.dll" parse "$@"; }
  ps_run() { dotnet "$out/psparse/psparse.dll" run "$@"; }
  echo "powershell: no pwsh; PowerShell SDK $(sed -n 's/.*"Microsoft.PowerShell.SDK\/\([0-9.]*\)".*/\1/p' "$out/psparse-obj/obj/psparse/project.assets.json" 2>/dev/null | head -1) through tests/psparse"
else
  echo "powershell: neither pwsh nor dotnet here: the PowerShell scripts were not checked" >&2
  exit 1
fi
ps_parse "$kit/cpu-load.ps1" "$kit/kit.ps1" "$kit/collect.ps1"

fail() { printf 'FAIL %s\n' "$*"; exit 1; }
# cpu-load.ps1: three phases, the busy threads in the middle one
ps_run "$kit/cpu-load.ps1" -Threads 2 -LoadSeconds 1 -IdleSeconds 1 -Cycles 1 -Out "$out/phases.csv" > "$out/cpu-load.log" 2>&1 ||
  { cat "$out/cpu-load.log"; fail cpu-load.ps1; }
[[ $(cut -d, -f1,4 "$out/phases.csv" | tr '\n' ' ') == 'phase,threads idle,0 load,2 idle,0 ' ]] || { cat "$out/phases.csv"; fail 'cpu-load.ps1 phases'; }
echo 'PASS cpu-load.ps1: idle, load (2 threads), idle, each in the phase file'
# kit.ps1 and collect.ps1 on a fake game folder
copy=$out/kit game=$out/Enshrouded
cp -r "$kit" "$copy"
rm -rf "$copy/tests"
mkdir -p "$copy/mod/gate-priority-test" "$game/kiln/logs/gate-priority-test/run-20261118-120000" "$game/kiln/state"
printf 'MZ' > "$copy/mod/gate-priority-test/gate-priority-test.dll"
printf '{}' > "$copy/mod/gate-priority-test/native.json"
printf 'MZ' > "$game/enshrouded.exe"
printf 'MZ' > "$game/kiln/kiln-core.dll"
cat > "$game/kiln/logs/kiln.log" <<'EOF'
Kiln 0.2.0-dev starting (pid 1)
players: someone joined 76561198000000001
kiln.engine: the write gate's priority raise is off for this run
gate-priority-test: this world is 6b696c6e2d746573742d776f726c6401; put world=...
gate-priority-test: files in /home/someone/x
EOF
printf '{"time":"2026-11-18T12:00:00"}\n' > "$game/kiln/logs/tick-stats.jsonl"
printf '{"capabilities":[{"name":"engine.gate_priority_raise","state":"unavailable","reasonText":"off"}]}' > "$game/kiln/state/capabilities.json"
printf '{"raise":"off"}' > "$game/kiln/logs/gate-priority-test/run-20261118-120000/run.json"
cp "$out/phases.csv" "$copy/cpu-load-test.csv"
step() { local name=$1; shift; ps_run "$@" > "$out/$name.log" 2>&1 || { cat "$out/$name.log"; fail "$name"; }; }
step install "$copy/kit.ps1" -Command install -Game "$game"
[[ -f $game/kiln/native/gate-priority-test/gate-priority-test.dll ]] || fail 'kit.ps1 install: the mod'
step world "$copy/kit.ps1" -Command world -Game "$game"
[[ $(head -1 "$game/kiln/data/gate-priority-test/run.txt" | tr -d "\r") == world=6b696c6e2d746573742d776f726c6401 ]] || fail "kit.ps1 world"
step raise "$copy/kit.ps1" -Command raise -Value off -Game "$game"
[[ $(cat "$game/kiln/data/kiln.engine/gate-priority-raise") == off ]] || fail 'kit.ps1 raise off'
step status "$copy/kit.ps1" -Command status -Game "$game"
grep -q 'present: the raise is OFF' "$out/status.log" || fail 'kit.ps1 status'
ps_run "$copy/collect.ps1" -Game "$game" -Out "$out/collected.zip" > "$out/collect.log" 2>&1 || true  # powercfg is Windows-only
[[ -f $out/collected.zip ]] || { cat "$out/collect.log"; fail 'collect.ps1: no zip'; }
[[ ! -e $game/kiln/data/kiln.engine/gate-priority-raise ]] || fail 'collect.ps1 left the switch'
python3 -I - "$out/collected.zip" <<'EOF' || fail 'collect.ps1: the zip'
import json, sys, zipfile
z = zipfile.ZipFile(sys.argv[1])
names = sorted(z.namelist())
assert names == ['load/cpu-load-test.csv', 'logs/kiln.log', 'logs/tick-stats.jsonl', 'runs/run-20261118-120000/run.json',
                 'state/capabilities.json', 'system.json'], names
log = z.read('logs/kiln.log').decode()
assert 'players:' not in log and '<guid>' in log and '/home/<user>/x' in log and 'someone' not in log, log
assert json.loads(z.read('system.json'))['raise_switch'] == 'off'
EOF
echo 'PASS kit.ps1 install, world, raise off, status; collect.ps1: the zip, the redaction, the switch deleted'
step uninstall "$copy/kit.ps1" -Command uninstall -Game "$game" -Logs '$true'
[[ ! -e $game/kiln/native/gate-priority-test && ! -e $game/kiln/data/gate-priority-test && ! -e $game/kiln/logs/gate-priority-test ]] ||
  fail 'kit.ps1 uninstall'
echo 'PASS kit.ps1 uninstall'
