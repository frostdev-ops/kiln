#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""kiln-emf lab check: installs the probe (an EMF mod) and the Kiln-side cross-check into a lab that runs kiln-emf and
the EMF mods, starts the lab, drives server-admin's file protocol and emf/commands.txt, and prints one row per EMF mod
(loads / works / evidence) from kiln.log, then stops the lab.

    KILN_OUT=<build dir> tools/emf/lab-check.py emf          # lab name (tools/lab/lab.sh)
    tools/emf/lab-check.py emf --no-start                            # the lab already runs

Needs: kiln-emf deployed (tools/deploy-server.sh), the EMF mods in <lab>/emf/mods/ (switched on in
<lab>/kiln/config/emf/<id>.ini), native targets emf-probe and the check mod built:
    native/build.sh kiln-emf emf-probe
    dotnet build mods/tests/emf-test/check -c Release -o $KILN_OUT/mods/emf-check --artifacts-path $KILN_OUT/dotnet
Player-free: things only a joined player can show are in docs/kiln/emf.md ("client checklist").
"""
import json
import os
import re
import shutil
import subprocess
import sys
import time
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
lab = sys.argv[1] if len(sys.argv) > 1 else "emf"
no_start = "--no-start" in sys.argv
L = Path.home() / "srv/enshrouded/labs" / lab
OUT = Path(os.environ.get("KILN_OUT", REPO / ".out/emf"))
LOG = L / "kiln/logs/kiln.log"


def sh(*a):
    return subprocess.run([str(x) for x in a], capture_output=True, text=True)


def install():
    probe = L / "emf/mods/emf_probe"
    probe.mkdir(parents=True, exist_ok=True)
    shutil.copy2(OUT / "cmake/bin/emf-test/emf_probe.dll", probe)
    shutil.copy2(REPO / "mods/tests/emf-test/probe/mod.json", probe)
    check = L / "kiln/mods/emf-check"
    check.mkdir(parents=True, exist_ok=True)
    for f in ("EmfCheck.dll", "kiln.mod.json"):
        shutil.copy2(OUT / "mods/emf-check" / f, check)


def wait_for(pattern, timeout):
    rx = re.compile(pattern)
    end = time.time() + timeout
    while time.time() < end:
        if LOG.exists() and rx.search(LOG.read_text(errors="replace")):
            return True
        time.sleep(1)
    return False


def admin(op, **fields):
    args = [sys.executable, REPO / "tools/emf/admin-op.py", L, op] + [f"{k}={json.dumps(v)}" for k, v in fields.items()]
    r = sh(*args)
    try:
        return json.loads(r.stdout.strip().splitlines()[-1])
    except (ValueError, IndexError):
        return {"ok": False, "data": {"error": r.stdout + r.stderr}}


def commands(lines):
    f = L / "emf/commands.txt"
    f.write_text("".join(x + "\n" for x in lines))
    end = time.time() + 30
    while f.exists() and time.time() < end:
        time.sleep(0.5)
    time.sleep(2)


def main():
    install()
    started = False
    if not no_start:
        sh(REPO / "tools/lab/lab.sh", "stop", lab)
        sh(REPO / "tools/lab/lab.sh", "start", lab)
        started = True
    ok_probe = wait_for(r"emf-probe done", 300)
    ok_check = wait_for(r"emf-check done", 120)
    results = {}
    # server-admin's own protocol
    st = json.loads((L / "shroudforge/runtime/admin/engine/state.json").read_text())
    cur = next(s["value"] for s in st["settings"] if s["key"] == "experienceCombatFactor")
    ping = admin("ping")
    items = admin("items")
    wood = [x for x in json.loads((L / "shroudforge/runtime/admin/engine/items.json").read_text()) if x["name"] == "Material_T1_Wood_Logs"]
    new = 1.5 if cur != 1.5 else 1.25
    sset = admin("settings.set", key="experienceCombatFactor", value=new, expected=cur)
    sres = admin("settings.reset", key="experienceCombatFactor", expected=new)
    bad = admin("settings.set", key="experienceCombatFactor", value=9, expected=cur)
    save = admin("save")
    results["server-admin"] = (
        ping.get("ok") and ping["data"].get("api") == 27 and items.get("ok") and items["data"]["count"] > 4000 and sset.get("ok")
        and sres.get("ok") and not bad.get("ok") and save.get("ok"),
        f"ping api {ping['data'].get('api')}; items {items['data'].get('count')} (wood max stack {wood[0]['maxStack'] if wood else '?'}); "
        f"settings.set {new} {'ok' if sset.get('ok') else 'FAIL'} (read back {sset['data'].get('value')}), reset {'ok' if sres.get('ok') else 'FAIL'}, "
        f"out of range refused {'yes' if not bad.get('ok') else 'NO'}; save {'acknowledged by on_save' if save.get('ok') else 'FAIL'}; "
        f"{len(st['settings'])} world settings published")
    mark = LOG.stat().st_size
    commands(["players", "herald.say lab-check line", "sleep.status", "tp.list", "tp.pos #1 100 200 300", "lua.status",
              "qs.list", "raids.status", "loot.status", "champion.where", "emf.mods"])
    tail = LOG.read_text(errors="replace")[mark:]
    log = LOG.read_text(errors="replace")

    def has(rx, text=log):
        return re.search(rx, text, re.M) is not None

    def grab(rx, text=log):
        m = re.search(rx, text, re.M)
        return m.group(1) if m else None

    status = json.loads((L / "kiln/state/emf.json").read_text())
    mods = {m["id"]: m for m in status["mods"]}
    cmds = {c["name"]: c for c in status.get("commands", [])}
    raised = grab(r"\[emf:big_stacks\] raised the stack size of (\d+) item")
    formula = has(r"PASS Kiln\.Content: \d+ items raised by the formula")
    results["big_stacks"] = (raised is not None and int(raised) > 2000 and has(r"PASS Kiln\.Content: Material_T1_Wood_Logs") and formula,
                             f"raised {raised} items; Kiln.Content cross-check (wood 250 -> 1000, formula on every item): {'PASS' if formula else 'FAIL'}")
    results["herald"] = (has(r"\[cmd\] nobody is online", tail) and has(r"\[emf:herald\] \[broadcast\] \[Server\] lab-check line", tail),
                         "players -> 'nobody is online'; herald.say -> broadcast line; on_chat " + ("on" if has(r"\[emf:herald\] ready:.*chat log on") else "off"))
    results["sleep"] = (has(r"\[cmd\] Sleep Through the Day: no players", tail) and not has(r"\[emf:sleep\] a game build EMF does not know"),
                        "sleep.status answers; game_build known (not idle)")
    tp = all(n in cmds for n in ("unstuck", "home", "tp", "bring", "tp.pos", "tp.back", "tp.list"))
    results["teleports"] = (tp and has(r"\[cmd\] there is no player #1", tail),
                            "7 commands registered (unstuck, home players' commands); tp.pos answers; moving players: client checklist")
    results["clock"] = (mods.get("clock", {}).get("status") == "rejected", "side client: not loaded on a dedicated server (client checklist)")
    nlua = grab(r"\[emf:lua\] ready: (\d+) Lua mod")
    results["lua"] = (nlua is not None and has(r"\[cmd\] \d+ Lua mod", tail), f"script host: {nlua} Lua mod(s) running")
    results["quick_stack"] = (has(r"\[cmd\] No marked chests", tail), "qs.list answers; chest moves need a player")
    results["raids"] = (has(r"\[cmd\] Night Raids is (on|off) in this world", tail), "raids.status answers; program registered")
    results["loot_scale"] = (has(r"\[emf:loot_scale\] hook loot_scale\.factor at exe\+0x[0-9a-f]+") and has(r"\[emf:loot_scale\] on: loot amount"),
                             "hook_pattern on the loot factor installed")
    for m in ("loot_magnet", "bulk_craft", "companion", "villager_jobs"):
        results[m] = (has(rf"\[emf:{m}\] ready:") and has(rf"\[emf:{m}\] program \S+ registered through kiln.world"),
                      "ready; its program registered (needs a player to act)")
    for m in ("beastmaster", "bounty", "champion", "grave", "handover", "kill_counter", "life_skills", "necromancer"):
        results[m] = (has(rf"\[emf:{m}\] running \(main\.lua\)"), "run by the lua mod")
    probe = grab(r"emf-probe done: (\d+ PASS, \d+ FAIL)")
    results["emf_probe"] = (probe is not None and probe.endswith(" 0 FAIL"), f"conformance probe: {probe}")
    # EMF 0.5 mods (built against EmfApi v26): the v27 table is a superset, so they load
    for m, rx in (("blessings", r"\[emf:blessings\] "), ("event_log", r"\[emf:event_log\] "), ("guid_dump", r"\[emf:guid_dump\] "),
                  ("model_probe", r"\[emf:model_probe\] "), ("model_survey", r"\[emf:model_survey\] "),
                  ("npc_explorer", r"\[emf:npc_explorer\] "), ("sample_mod", r"\[emf:sample_mod\] "),
                  ("sim_rate", r"\[emf:sim_rate\] "), ("write_test", r"\[emf:write_test\] ")):
        if m in mods:
            results[m] = (mods[m]["status"] == "loaded" and has(rx), "built for EmfApi v26, loaded on v27; " + (mods[m].get("reason") or "ready"))
    chk = grab(r"emf-check done: (\d+ PASS, \d+ FAIL)")
    results["(emf-check)"] = (chk is not None and chk.endswith(" 0 FAIL"), f"Kiln-side cross-check: {chk}")

    unsup = status.get("unsupported", {})
    print(f"{'mod':14} {'loads':9} {'works':5}  evidence")
    for mid in sorted(set(mods) | set(results)):
        st_ = mods.get(mid, {}).get("status", "-")
        ok, ev = results.get(mid, (None, mods.get(mid, {}).get("reason", "")))
        miss = sorted(fn for fn, who in unsup.items() if mid in who)
        print(f"{mid:14} {st_:9} {('PASS' if ok else 'FAIL') if ok is not None else '-':5}  {ev}{'  [not supported: ' + ', '.join(miss) + ']' if miss else ''}")
    faults = [m["id"] for m in status["mods"] if m.get("faults")]
    print(f"faulted callbacks: {faults or 'none'}; probe {'ran' if ok_probe else 'TIMED OUT'}, check {'ran' if ok_check else 'TIMED OUT'}")
    if started:
        sh(REPO / "tools/lab/lab.sh", "stop", lab)
    return 0 if all(r[0] for r in results.values()) else 1


sys.exit(main())
