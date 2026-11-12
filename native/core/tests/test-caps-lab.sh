#!/usr/bin/env bash
# SPDX-License-Identifier: MIT
# Lab check of the capability registry (kiln.caps) on a dedicated server:
#   1. deploy Kiln, start, stop: kiln/state/capabilities.json validates against schema/state-capabilities.schema.json,
#      and its rows agree with logs/sigdb-report.json and state/boot.json;
#   2. the same lab with the pattern of gameplay/CraftRecipe broken in the lab's own copy of the definitions
#      (kiln/sigdb/server.d/gameplay.json, never the repository's): gameplay.craft is unavailable, symbol-missing,
#      naming that symbol, and every other feature is as in step 1;
#   3. (--with-mods, needs the managed half of the registry) with the pattern still broken, a test mod whose manifest
#      requires gameplay.craft is refused with that reason in kiln/state/mods.json and in kiln.log.
# gameplay.craft is optional for the server: kiln.gameplay only leaves crafting interception off without it.
#
#   KILN_OUT=<build dir> native/core/tests/test-caps-lab.sh <lab> [--with-mods] [--no-deploy]
#
# The lab must exist (tools/lab/lab.sh new <lab> <port>); this script starts and stops it and leaves it stopped, with
# the lab's definitions put back. Environment:
#   KILN_LAB_START  the command that starts a lab, given the lab's name (default: tools/lab/lab.sh start)
#   KILN_SLOT       a prefix for the heavy deploy step (a build), e.g. a slot wrapper; default none
#   KILN_LABS       where labs live (as tools/lab/lab.sh)
#   AJV_2020        path to ajv/dist/2020.js; else this tree's launcher/node_modules, else the main checkout's
# Results: $KILN_OUT/caps-lab/<step>/ (the state files and kiln.log of each run). Exit 0 when every step passed.
set -euo pipefail
repo=$(cd "$(dirname "$0")/../../.." && pwd)
lab=${1:?usage: KILN_OUT=<dir> $0 <lab> [--with-mods] [--no-deploy]}
shift
with_mods=0 deploy=1
while (($#)); do
  case $1 in
    --with-mods) with_mods=1 ;;
    --no-deploy) deploy=0 ;;
    *) echo "unknown option $1" >&2; exit 2 ;;
  esac
  shift
done
: "${KILN_OUT:?set KILN_OUT to the build dir}"
[[ $lab =~ ^[a-z0-9-]+$ ]] || { echo "a lab name is [a-z0-9-]+" >&2; exit 2; }
labs=${KILN_LABS:-${KILN_LAB_ROOT:-$HOME/srv/enshrouded}/labs}
server=$labs/$lab
[[ -f $server/enshrouded_server.exe ]] || { echo "no lab $lab (tools/lab/lab.sh new $lab <port>)" >&2; exit 1; }
read -r -a start <<< "${KILN_LAB_START:-tools/lab/lab.sh start}"
read -r -a slot <<< "${KILN_SLOT:-}"
work=$KILN_OUT/caps-lab
rm -rf "$work"
mkdir -p "$work"
cd "$repo"

defs=$server/kiln/sigdb/server.d/gameplay.json
mod_dir=$server/kiln/mods/caps-requires-test
cleanup() {
  tools/lab/lab.sh stop "$lab" > /dev/null 2>&1 || true
  [[ -f $work/gameplay.json.orig ]] && cp "$work/gameplay.json.orig" "$defs"
  rm -rf "$mod_dir"
}
trap cleanup EXIT

failures=0
pass() { echo "PASS $*"; }
fail() { echo "FAIL $*"; failures=$((failures + 1)); }

# ajv (draft 2020-12), as schema/tests/closed_dll_schemas.mjs finds it
ajv=${AJV_2020:-}
if [[ -z $ajv ]]; then
  for c in "$repo/launcher/node_modules/ajv/dist/2020.js" \
           "$(dirname "$(git -C "$repo" rev-parse --path-format=absolute --git-common-dir 2>/dev/null || echo /nonexistent/.git)")/launcher/node_modules/ajv/dist/2020.js"; do
    [[ -f $c ]] && { ajv=$c; break; }
  done
fi
[[ -n $ajv && -f $ajv ]] || { echo "ajv 2020 not found: set AJV_2020 or run bun install in launcher/" >&2; exit 1; }
cat > "$work/validate.mjs" <<'EOF'
import { readFileSync } from "node:fs";
import { pathToFileURL } from "node:url";
const [ajvPath, schemaDir, file] = process.argv.slice(2);
const Ajv2020 = (await import(pathToFileURL(ajvPath).href)).default;
const ajv = new Ajv2020({ strict: false, validateFormats: false });
const read = (f) => JSON.parse(readFileSync(f, "utf8"));
ajv.addSchema(read(`${schemaDir}/common.schema.json`));
const validate = ajv.compile(read(`${schemaDir}/state-capabilities.schema.json`));
if (!validate(read(file))) { console.log(JSON.stringify(validate.errors, null, 1)); process.exit(1); }
EOF

# One start and stop of the lab; its state files and kiln.log go to $work/<tag>/.
run() {
  local tag=$1 k=$server/kiln
  rm -f "$k/state/capabilities.json" "$k/state/mods.json" "$k/state/boot.json"
  "${start[@]}" "$lab" || { tools/lab/lab.sh stop "$lab" > /dev/null 2>&1 || true; echo "lab $lab did not start" >&2; return 1; }
  sleep 5  # the managed host writes mods.json after the world is up
  tools/lab/lab.sh stop "$lab"
  mkdir -p "$work/$tag"
  cp "$k/state/capabilities.json" "$k/state/boot.json" "$k/logs/kiln.log" "$work/$tag/" 2>/dev/null || true
  cp "$k/logs/sigdb-report.json" "$k/state/mods.json" "$work/$tag/" 2>/dev/null || true
  [[ -f $work/$tag/capabilities.json ]] || { echo "no kiln/state/capabilities.json after the $tag run" >&2; return 1; }
}

validate() {
  if node "$work/validate.mjs" "$ajv" "$repo/schema" "$work/$1/capabilities.json"; then
    pass "$1: capabilities.json validates against schema/state-capabilities.schema.json"
  else
    fail "$1: capabilities.json does not validate"
  fi
}

# check <step> <python, reading caps/boot/report/mods/log/base; prints PASS/FAIL lines>
check() {
  local out
  out=$(python3 - "$work" "$1" <<EOF
import json, os, sys
work, tag = sys.argv[1], sys.argv[2]
d = os.path.join(work, tag)
load = lambda name, folder=d: json.load(open(os.path.join(folder, name))) if os.path.exists(os.path.join(folder, name)) else None
caps = load("capabilities.json"); boot = load("boot.json"); report = load("sigdb-report.json"); mods = load("mods.json")
log = open(os.path.join(d, "kiln.log"), encoding="utf-8", errors="replace").read() if os.path.exists(os.path.join(d, "kiln.log")) else ""
base = load("capabilities.json", os.path.join(work, "1")) if tag != "1" else caps
rows = {r["name"]: r for r in caps["capabilities"]}
def ok(cond, what): print(("PASS " if cond else "FAIL ") + tag + ": " + what)
$2
EOF
)
  echo "$out"
  failures=$((failures + $(grep -c '^FAIL' <<< "$out" || true)))
}

if ((deploy)); then
  "${slot[@]}" env KILN_OUT="$KILN_OUT" tools/deploy-server.sh "$server" --policy any > "$work/deploy.log" 2>&1 ||
    { tail -20 "$work/deploy.log"; echo "deploy failed ($work/deploy.log)" >&2; exit 1; }
fi
[[ -f $defs ]] || { echo "$defs is missing: deploy Kiln first" >&2; exit 1; }
cp "$defs" "$work/gameplay.json.orig"
rm -rf "$mod_dir"

# ---- step 1 ----
run 1
validate 1
check 1 '
names = [r["name"] for r in caps["capabilities"]]
ok(names == sorted(names, key=lambda s: s.encode()), "rows sorted by name (%d rows)" % len(names))
ok(caps["hooks"] is True and caps["safeIdle"] is None and caps["generation"] >= 1, "hooks on, generation %s" % caps["generation"])
ok(rows.get("kiln.caps", {}).get("state") == "available" and rows["kiln.caps"]["reasonText"] == "v1", "kiln.caps is available, v1")
feats = report["features"]
bad = []
for f, resolved in feats.items():
    r = rows.get(f)
    if r is None: bad.append(f + ": no row"); continue
    if r["source"] != "sigdb": bad.append(f + ": source " + r["source"])
    if resolved and r["state"] != "available" and r["reason"] != "faulted": bad.append(f + ": " + r["state"] + " " + r["reason"])
    if not resolved and not (r["reason"] == "symbol-missing" and r["reasonText"].split(" ")[0] in ("symbol", "constant")):
        bad.append(f + ": " + r["reason"] + " " + r["reasonText"])
ok(not bad, "one row per sigdb feature, agreeing with sigdb-report.json (%d features) %s" % (len(feats), "; ".join(bad[:5])))
svc = [s.split("@") for s in boot["services"]]
bad = [n for n, v in svc if rows.get(n, {}).get("state") != "available" or rows[n]["reasonText"] != "v" + v]
ok(not bad, "every service in boot.json has an available row with its version (%d) %s" % (len(svc), ", ".join(bad)))
ok(rows.get("gameplay.craft", {}).get("state") == "available", "gameplay.craft is available on this build (step 2 breaks it)")
'

# ---- step 2 ----
python3 - "$defs" <<'EOF'
import json, sys
f = sys.argv[1]
d = json.load(open(f))
for s in d["symbols"]["gameplay/CraftRecipe"]["find"]:
    s["pattern"] = "0F 0B 0F 0B 0F 0B 0F 0B 0F 0B 0F 0B"  # ud2 x6: matches nothing
json.dump(d, open(f, "w"), indent=2)
EOF
run 2
validate 2
check 2 '
r = rows.get("gameplay.craft", {})
ok(r.get("state") == "unavailable" and r.get("reason") == "symbol-missing" and
   r.get("reasonText", "").startswith("symbol gameplay/CraftRecipe: ") and "0 matches" in r.get("reasonText", ""),
   "gameplay.craft unavailable, symbol-missing: " + r.get("reasonText", "(no row)"))
before = {x["name"]: x for x in base["capabilities"] if x["source"] == "sigdb"}
moved = [n for n, x in before.items() if n != "gameplay.craft" and rows.get(n, {}).get("state") != x["state"]]
ok(not moved, "every other feature as in step 1 %s" % ", ".join(moved))
'

# ---- step 3 ----
if ((with_mods)); then
  mkdir -p "$mod_dir"
  cat > "$mod_dir/kiln.mod.json" <<'EOF'
{
  "id": "caps-requires-test",
  "name": "kiln.caps lab test: requires gameplay.craft",
  "version": "0.1.0",
  "apiVersion": "0.2",
  "side": "server",
  "features": ["gameplay.craft"],
  "description": "Written by native/core/tests/test-caps-lab.sh while the lab's gameplay/CraftRecipe pattern is broken: the loader must refuse it with the registry's reason."
}
EOF
  run 3
  check 3 '
m = next((x for x in (mods or {}).get("mods", []) if x["id"] == "caps-requires-test"), None)
reason = (m or {}).get("reason") or ""
ok(m is not None and m["state"] == "failed" and "gameplay.craft" in reason and "symbol gameplay/CraftRecipe" in reason,
   "mods.json: the mod is refused with the reason: " + (reason or "(no entry)"))
ok(any("caps-requires-test" in l and "symbol gameplay/CraftRecipe" in l for l in log.splitlines()),
   "kiln.log: the refusal names the symbol")
'
fi

echo "test-caps-lab: $failures failure(s); files in $work"
((failures == 0))
