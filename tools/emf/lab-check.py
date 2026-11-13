#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""kiln-emf lab check: installs the two probes (EMF mods) and the Kiln-side cross-check into a lab that runs kiln-emf
(with or without EMF's own mods), starts the lab, drives server-admin's file protocol and emf/commands.txt, and prints one row per EMF mod
(loads / works / evidence) from kiln.log, then stops the lab.

    KILN_OUT=<build dir> tools/emf/lab-check.py emf          # lab name (tools/lab/lab.sh)
    tools/emf/lab-check.py emf --install-only                        # only put the probes and the check mod in place
    tools/emf/lab-check.py emf --no-start                            # the lab already runs (started by a wave's labstart)

Needs: kiln-emf deployed (tools/deploy-server.sh), the EMF mods in <lab>/emf/mods/ (switched on in
<lab>/kiln/config/emf/<id>.ini), native targets emf-probe and the check mod built:
    native/build.sh kiln-emf emf-probe emf-net-probe
    dotnet build mods/tests/emf-test/check -c Release -o $KILN_OUT/mods/emf-check --artifacts-path $KILN_OUT/dotnet
Player-free: things only a joined player can show are in docs/kiln/emf.md ("client checklist").
"""
import filecmp
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
install_only = "--install-only" in sys.argv
L = Path.home() / "srv/enshrouded/labs" / lab
OUT = Path(os.environ.get("KILN_OUT", REPO / ".out/emf"))
LOG = L / "kiln/logs/kiln.log"


def sh(*a):
    return subprocess.run([str(x) for x in a], capture_output=True, text=True)


def put(src, folder):
    """Copies src into folder unless the same file is there: a running server has its DLLs mapped."""
    dst = folder / Path(src).name
    if not (dst.exists() and filecmp.cmp(src, dst, shallow=False)):
        shutil.copy2(src, dst)


def install():
    probe = L / "emf/mods/emf_probe"
    probe.mkdir(parents=True, exist_ok=True)
    put(OUT / "cmake/bin/emf-test/emf_probe.dll", probe)
    put(REPO / "mods/tests/emf-test/probe/mod.json", probe)
    net = L / "emf/mods/emf_net_probe"
    net.mkdir(parents=True, exist_ok=True)
    put(OUT / "cmake/bin/emf-test/emf_net_probe.dll", net)
    put(REPO / "mods/tests/emf-net-test/mod.json", net)
    check = L / "kiln/mods/emf-check"
    check.mkdir(parents=True, exist_ok=True)
    for f in ("EmfCheck.dll", "kiln.mod.json"):
        put(OUT / "mods/emf-check" / f, check)


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
    if install_only:
        return 0
    if no_start:
        return check()
    try:  # armed before the launch: whatever happens from here on, the lab this tool started is stopped again
        sh(REPO / "tools/lab/lab.sh", "stop", lab)
        LOG.unlink(missing_ok=True)  # only this run's lines count (with --no-start the caller answers for the log)
        r = sh(REPO / "tools/lab/lab.sh", "start", lab)
        if r.returncode != 0:
            print(f"the lab did not start (lab.sh start {lab}: exit {r.returncode}): {(r.stdout + r.stderr).strip()}")
            return 1
        return check()
    finally:
        sh(REPO / "tools/lab/lab.sh", "stop", lab)


def check():
    ok_probe = wait_for(r"emf-probe done", 300)
    ok_check = wait_for(r"emf-check done", 120)
    results = {}
    status = json.loads((L / "kiln/state/emf.json").read_text())
    mods = {m["id"]: m for m in status["mods"]}
    admin_status = mods.get("server-admin", {}).get("status")
    if admin_status == "loaded":
        server_admin(results)
    elif admin_status is not None:
        results["server-admin"] = (False, f"installed but {admin_status}: {mods['server-admin'].get('reason', '')}")
    mark = LOG.stat().st_size
    commands(["players", "herald.say lab-check line", "sleep.status", "tp.list", "tp.pos #1 100 200 300", "lua.status",
              "qs.list", "raids.status", "loot.status", "champion.where", "emf.mods"])
    tail = LOG.read_text(errors="replace")[mark:]
    log = LOG.read_text(errors="replace")
    faults = report(results, mods, tail, log, ok_probe, ok_check)
    return 0 if ok_probe and ok_check and not faults and all(r[0] for r in results.values()) else 1


def server_admin(results):
    """server-admin's own file protocol (a lab that has that mod)."""
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
        ping.get("ok") and ping["data"].get("api") == 40 and items.get("ok") and items["data"]["count"] > 4000 and sset.get("ok")
        and sres.get("ok") and not bad.get("ok") and save.get("ok"),
        f"ping api {ping['data'].get('api')}; items {items['data'].get('count')} (wood max stack {wood[0]['maxStack'] if wood else '?'}); "
        f"settings.set {new} {'ok' if sset.get('ok') else 'FAIL'} (read back {sset['data'].get('value')}), reset {'ok' if sres.get('ok') else 'FAIL'}, "
        f"out of range refused {'yes' if not bad.get('ok') else 'NO'}; save {'acknowledged by on_save' if save.get('ok') else 'FAIL'}; "
        f"{len(st['settings'])} world settings published")


def report(results, mods, tail, log, ok_probe, ok_check):
    """One row per EMF mod of the lab; returns the mods with a faulted callback. A check of a mod the lab does not
    have is left out: a lab with the probes alone (or with a few mods built from EMF's source) is a lab too."""

    def has(rx, text=log):
        return re.search(rx, text, re.M) is not None

    def grab(rx, text=log):
        m = re.search(rx, text, re.M)
        return m.group(1) if m else None

    status = json.loads((L / "kiln/state/emf.json").read_text())
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
    results["minimap"] = (mods.get("minimap", {}).get("status") == "rejected", "side client: not loaded on a dedicated server (client checklist)")
    for m in [m for m in results if m not in mods and m != "server-admin"]:
        del results[m]  # not in this lab
    probe = grab(r"emf-probe done: (\d+ PASS, \d+ FAIL)")
    results["emf_probe"] = (probe is not None and probe.endswith(" 0 FAIL"), f"conformance probe: {probe}")
    net = grab(r"EMF v40 lab probe done: (\d+ PASS, \d+ FAIL)")
    if "emf_net_probe" in mods:
        results["emf_net_probe"] = (net is not None and net.endswith(" 0 FAIL"), f"mod messages probe: {net}")
    # EMF's other shipped mods: the v40 table is a superset of every earlier one, so mods built for an older EMF load too
    for m, rx in (("blessings", r"\[emf:blessings\] "), ("event_log", r"\[emf:event_log\] "), ("guid_dump", r"\[emf:guid_dump\] "),
                  ("model_probe", r"\[emf:model_probe\] "), ("model_survey", r"\[emf:model_survey\] "),
                  ("npc_explorer", r"\[emf:npc_explorer\] "), ("sample_mod", r"\[emf:sample_mod\] "),
                  ("sim_rate", r"\[emf:sim_rate\] "), ("write_test", r"\[emf:write_test\] ")):
        if m in mods:
            results[m] = (mods[m]["status"] == "loaded" and has(rx), "loaded on the v40 table; " + (mods[m].get("reason") or "ready"))
    chk = grab(r"emf-check done: (\d+ PASS, \d+ FAIL)")
    results["(emf-check)"] = (chk is not None and chk.endswith(" 0 FAIL"), f"Kiln-side cross-check: {chk}")

    unsup = status.get("unsupported", {})
    print(f"{'mod':14} {'loads':9} {'works':5}  evidence")
    for mid in sorted(set(mods) | set(results)):
        st_ = mods.get(mid, {}).get("status", "-")
        ok, ev = results.get(mid, (None, mods.get(mid, {}).get("reason", "")))
        miss = sorted(fn for fn, who in unsup.items() if mid in who)
        print(f"{mid:14} {st_:9} {('PASS' if ok else 'FAIL') if ok is not None else '-':5}  {ev}{'  [not supported: ' + ', '.join(miss) + ']' if miss else ''}")
    # the status file is written every few seconds: a fault after its last write is in the log already (seh.cpp guarded)
    faults = sorted({m["id"] for m in status["mods"] if m.get("faults")} |
                    set(re.findall(r"\[emf:([^\]]+)\] .* faulted \(exception 0x", log)))
    print(f"faulted callbacks: {faults or 'none'}; probe {'ran' if ok_probe else 'TIMED OUT'}, check {'ran' if ok_check else 'TIMED OUT'}")
    return faults


sys.exit(main())
