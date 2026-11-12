#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""The guided check runner on a lab server (docs/kiln/modding/testing.md, "Guided checks").

Kiln and this mod must be deployed into the lab first (KILN_OUT=... tools/deploy-server.sh <lab dir> --policy any, with
the mod built into $KILN_OUT/mods/client-check-test). The script then:

  1. starts the lab with kiln/control/checks/ present and kiln.net's lab transport on --lab-port;
  2. runs the mod's checks through kiln/control/checks/ and feeds their verdicts (pass, fail with a note, skip), and
     checks that the cheats checks are refused at line 1 and the needs-hosted check for its session;
  3. joins as a scripted Kiln client on the lab transport (tools/nettest) and drives one check through the joined
     channel kiln:checks, and the cheats refusals a joined start gets;
  4. validates the ledger kiln/state/client-checks.json against schema/state-client-checks.schema.json with ajv;
  5. starts cct.wait, kills the server with kill -9 (its exact pid from lab.sh pid) while the check waits for its
     verdict, starts the lab again and shows every earlier entry is still there, the ledger is whole and valid, and the
     interrupted check has no entry;
  6. shows no cheats body ever ran (no marker file, no "CHEATS BODY RAN" line in kiln.log) and stops the lab.

    mods/tests/client-check-test/fixtures/client_checks_lab.py --lab <name> --lab-port <udp port> [--ajv <ajv package dir>]

Environment: KILN_LABS (default ~/srv/enshrouded/labs), KILN_LAB_START (the command that starts a lab, default
"tools/lab/lab.sh start"; run from the repository root). Prints PASS/FAIL lines and exits non-zero on any FAIL.
"""
import argparse
import json
import os
import re
import shlex
import shutil
import signal
import subprocess
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.abspath(os.path.join(HERE, "..", "..", "..", ".."))
sys.path.insert(0, os.path.join(REPO, "tools", "nettest"))
import nettest  # noqa: E402

MOD = "client-check-test"
CHANNEL, VERSION = "kiln:checks", "1"
M_LIST, M_CATALOG, M_START, M_STATE, M_ANSWER, M_ENTRY = range(6)
fails = 0


def check(ok, what):
    global fails
    print(("PASS " if ok else "FAIL ") + what, flush=True)
    fails += not ok
    return ok


def info(text):
    print("INFO " + text, flush=True)


class Lab:
    def __init__(self, name, start_cmd):
        self.name = name
        self.dir = os.path.join(os.path.expanduser(os.environ.get("KILN_LABS", "~/srv/enshrouded/labs")), name)
        self.kiln = os.path.join(self.dir, "kiln")
        self.control = os.path.join(self.kiln, "control", "checks")
        self.ledger = os.path.join(self.kiln, "state", "client-checks.json")
        self.log = os.path.join(self.kiln, "logs", "kiln.log")
        self.start_cmd = start_cmd
        self.n = 0

    def lab(self, *args):
        return subprocess.run([os.path.join(REPO, "tools", "lab", "lab.sh"), *args, self.name], cwd=REPO,
                              capture_output=True, text=True)

    def pid(self):
        out = self.lab("pid").stdout.strip()
        return int(out) if out.isdigit() else None

    def start(self):
        r = subprocess.run(shlex.split(self.start_cmd) + [self.name], cwd=REPO, capture_output=True, text=True)
        info(f"start: exit {r.returncode} {(r.stdout + r.stderr).strip()[-300:]}")
        return r.returncode == 0

    def stop(self):
        self.lab("stop")

    def cmd(self, command, timeout=60.0):
        """Writes <name>.cmd into kiln/control/checks/ and returns the parsed <name>.result.json."""
        self.n += 1
        name = f"t{self.n:03d}"
        tmp = os.path.join(self.control, f".{name}.tmp")
        with open(tmp, "w") as f:
            f.write(command + "\n")
        os.rename(tmp, os.path.join(self.control, name + ".cmd"))
        result = os.path.join(self.control, name + ".result.json")
        end = time.time() + timeout
        while time.time() < end:
            if os.path.exists(result):
                try:
                    with open(result) as f:
                        r = json.load(f)
                    os.remove(result)
                    return r
                except (OSError, ValueError):
                    pass
            time.sleep(0.2)
        return None

    def read_ledger(self):
        with open(self.ledger, "rb") as f:
            raw = f.read()
        return raw, json.loads(raw)

    def kiln_log(self):
        try:
            with open(self.log, encoding="utf-8", errors="replace") as f:
                return f.read()
        except OSError:
            return ""


def wait_runner(lab, timeout=240):
    end = time.time() + timeout
    while time.time() < end:
        r = lab.cmd("list", timeout=10)
        if r and r.get("ok") and any(c["mod"] == MOD for c in r["data"]["checks"]):
            return r
        time.sleep(2)
    return None


def run_and_answer(lab, cid, answer, expect_instruction=None):
    r = lab.cmd(f"run {MOD} {cid}")
    if not check(r is not None and r["ok"], f"{cid}: started ({r and r['message']})"):
        return None
    if expect_instruction:
        s = wait_status(lab, lambda d: d.get("current") and expect_instruction in d["current"]["instruction"])
        check(s is not None, f"{cid}: shows '{expect_instruction}'")
    r = lab.cmd(answer)
    ok = r is not None and r["ok"] and r["data"]["entry"] is not None
    check(ok, f"{cid}: '{answer}' recorded ({r and r['message']})")
    return r["data"]["entry"] if ok else None


def wait_status(lab, pred, timeout=30):
    end = time.time() + timeout
    while time.time() < end:
        s = lab.cmd("status", timeout=10)
        if s and pred(s["data"]):
            return s["data"]
        time.sleep(0.3)
    return None


def validate(ajv_dir, path, what):
    js = r"""
const { readFileSync } = require("node:fs");
const Ajv2020 = require(process.argv[1] + "/dist/2020.js");
const repo = process.argv[2];
const read = n => JSON.parse(readFileSync(`${repo}/schema/${n}`, "utf8"));
const ajv = new Ajv2020({ strict: true, validateFormats: false });
ajv.addSchema(read("common.schema.json"));
const v = ajv.compile(read("state-client-checks.schema.json"));
const ok = v(JSON.parse(readFileSync(process.argv[3], "utf8")));
console.log(ok ? "valid" : JSON.stringify(v.errors));
process.exit(ok ? 0 : 1);
"""
    r = subprocess.run(["node", "-e", js, ajv_dir, REPO, path], capture_output=True, text=True)
    check(r.returncode == 0, f"{what}: the ledger validates against state-client-checks.schema.json ({(r.stdout + r.stderr).strip()[:300]})")


class ChecksClient(nettest.Client):
    def set_table(self, table):
        self.table = table
        self.channels = {n: i for i, n, v, _ in table if n == CHANNEL and v == VERSION}

    def send_json(self, msg, obj):
        self.data(CHANNEL, msg, json.dumps(obj).encode())

    def take_json(self, msg, seconds=10.0):
        b = self.take(CHANNEL, msg, seconds)
        return None if b is None else json.loads(b.rstrip(b"\0"))

    def state_until(self, kinds, seconds=20.0):
        end = time.time() + seconds
        while time.time() < end:
            m = self.take_json(M_STATE, 0.5)
            if m and m["Kind"] in kinds:
                return m
        return None


def joined(lab, port):
    nettest.probe_server(port)
    c = ChecksClient(port, "client-checks")
    c.hello(nettest.FULL_MODS, [(CHANNEL, VERSION, False)])
    c.wait(lambda: c.welcome is not None or c.reject is not None, 15)
    if not check(c.welcome is not None and CHANNEL in c.channels, f"joined: the server has {CHANNEL} v{VERSION} (reject: {c.reject})"):
        return
    entry = next(t for t in c.welcome["table"] if t[1] == CHANNEL)
    check(entry[3] is False, "joined: the channel is not required (it never blocks a join)")
    c.send(nettest.LAB_READY)
    c.pump(1.0)
    c.send_json(M_LIST, {"Version": 1})
    cat = c.take_json(M_CATALOG, 10)
    check(cat is not None and cat["Enabled"], f"joined: the list arrives and the server takes checks ({cat and cat.get('Reason')})")
    if not cat:
        return
    items = {i["Id"]: i for i in cat["Checks"] if i["Mod"] == MOD}
    check("cct.assertions" in items and items["cct.assertions"]["Refusal"] is None, "joined: cct.assertions is listed with no refusal")
    check("needs a world this game hosts" in (items.get("cct.hosted-only", {}).get("Refusal") or ""),
          "joined: cct.hosted-only is listed as refused for a joined session")
    now = time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())
    old = time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime(time.time() - 120))
    guard = {"Available": True, "HookInstalled": True, "TargetVerified": True, "Active": True, "Dropped": 0, "Passed": 0, "Reason": "lab"}
    cases = [
        ("no guard status in the start request", None, "line 1"),
        ("a status read two minutes ago", {"Latched": True, "Status": guard, "Proof": "pass", "Taken": old}, "line 1"),
        ("the client's latch returned false", {"Latched": False, "Status": None, "Proof": "pass", "Taken": now}, "line 2"),
        ("the client's guard not protecting", {"Latched": True, "Status": dict(guard, HookInstalled=False), "Proof": "pass", "Taken": now}, "line 3"),
        ("no guard-proof pass in the client's ledger", {"Latched": True, "Status": guard, "Proof": "none", "Taken": now}, "line 4"),
    ]
    for what, attest, line in cases:
        c.send_json(M_START, {"Mod": MOD, "Id": "cct.cheats-flag", "Attest": attest})
        s = c.state_until({"refused", "started"})
        check(s is not None and s["Kind"] == "refused" and s["Message"].startswith(line),
              f"joined: cct.cheats-flag refused at {line} with {what} ({s and s['Message'][:140]})")
    c.send_json(M_START, {"Mod": MOD, "Id": "cct.assertions", "Attest": None})
    s = c.state_until({"refused", "started"})
    check(s is not None and s["Kind"] == "started", f"joined: cct.assertions started ({s and s['Message']})")
    c.state_until({"changed"}, 5)
    r = lab.cmd("pass")  # the control folder is not the joined client: it must not answer the client's check
    check(r is not None and not r["ok"] and "someone else" in r["message"], f"joined: only the joined client answers its check ({r and r['message']})")
    c.send_json(M_ANSWER, {"Kind": "pass", "Note": None, "Guard": None})
    s = c.state_until({"finished"})
    e = c.take_json(M_ENTRY, 10)
    check(s is not None and e is not None and e["Entry"]["verdict"] == "pass" and e["Entry"]["session"] == "joined",
          "joined: pass from the scripted client is recorded as a joined entry and sent back to it")
    c.send(nettest.frame(nettest.T_BYE).b)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--lab", required=True)
    ap.add_argument("--lab-port", type=int, required=True)
    ap.add_argument("--ajv", default=os.path.join(REPO, "launcher", "node_modules", "ajv"))
    a = ap.parse_args()
    lab = Lab(a.lab, os.environ.get("KILN_LAB_START", "tools/lab/lab.sh start"))
    if not os.path.isdir(os.path.join(lab.kiln, "mods", MOD)):
        print(f"FAIL {MOD} is not deployed in {lab.dir}/kiln/mods")
        return 1
    if lab.pid():
        print(f"FAIL the lab {a.lab} is running; stop it first")
        return 1
    # a fresh ledger, no marker, the check folder, the lab transport
    for p in [lab.ledger, os.path.join(lab.kiln, "data", MOD, "cheats-body-ran")]:
        if os.path.exists(p):
            os.remove(p)
    for f in os.listdir(os.path.join(lab.kiln, "state")) if os.path.isdir(os.path.join(lab.kiln, "state")) else []:
        if f.startswith("client-checks.unreadable-"):
            os.remove(os.path.join(lab.kiln, "state", f))
    shutil.rmtree(lab.control, ignore_errors=True)
    os.makedirs(lab.control)
    cfg = os.path.join(lab.kiln, "config", "network.json")
    os.makedirs(os.path.dirname(cfg), exist_ok=True)
    net = {}
    if os.path.exists(cfg):
        try:
            net = json.load(open(cfg))
        except ValueError:
            net = {}
    net["labPort"] = a.lab_port
    json.dump(net, open(cfg, "w"), indent=2)

    try:
        if not check(lab.start(), "the lab started"):
            return 1
        listed = wait_runner(lab)
        if not check(listed is not None, "the runner answers list with the mod's checks"):
            print(lab.kiln_log()[-3000:])
            return 1
        ids = [c["id"] for c in listed["data"]["checks"] if c["mod"] == MOD]
        info(f"{len(ids)} checks: {' '.join(ids)}; session {listed['data']['session']}; guard {listed['data']['guard']}")
        check(listed["data"]["session"] == "server", "the session of a dedicated server is 'server'")

        e = run_and_answer(lab, "cct.assertions", "pass")
        check(e is not None and e["verdict"] == "pass" and all(x["ok"] for x in e["assertions"]) and len(e["assertions"]) == 2,
              "cct.assertions: pass, both assertions ok")
        check(e is not None and any("the body ran" in l for l in e["log"]), "cct.assertions: its kiln.log line is in the entry")
        e = run_and_answer(lab, "cct.fail-note", "fail the flash was green, not blue; seen at /home/someone/x from 192.168.1.20")
        check(e is not None and e["verdict"] == "fail" and e["note"].startswith("the flash was green, not blue")
              and "192.168.1.20" not in e["note"] and "/home/someone" not in e["note"] and "<ip>" in e["note"],
              f"cct.fail-note: fail with its note, scrubbed ({e and e['note']})")
        e = run_and_answer(lab, "cct.skip", "skip not on this build")
        check(e is not None and e["verdict"] == "skip" and e["note"] == "not on this build", "cct.skip: skip with its note")
        e = run_and_answer(lab, "cct.two-steps", "pass", expect_instruction="Second instruction")
        check(e is not None and e["assertions"] and e["assertions"][0]["ok"], f"cct.two-steps: the ticks between the instructions ({e and e['assertions']})")
        e = run_and_answer(lab, "cct.private-log", "pass")
        log = "\n".join(e["log"]) if e else ""
        check(e is not None and "cct.private-log" in log and all(x in log for x in ("<ip>", "<steamid>", "<home>"))
              and not any(x in log for x in ("192.168.1.20", "fe80::1ff", "76561198012345678", "someone")),
              "cct.private-log: the log line is in the entry with every private value replaced")
        for cid, line in [("cct.cheats-flag", "line 1"), ("cct.guard-proof", "line 1")]:
            r = lab.cmd(f"run {MOD} {cid}")
            check(r is not None and not r["ok"] and r["message"].startswith("refused: " + line),
                  f"{cid}: refused from the control folder at {line} ({r and r['message'][:160]})")
        r = lab.cmd(f"run {MOD} cct.hosted-only")
        check(r is not None and not r["ok"] and "needs a world this game hosts" in r["message"], f"cct.hosted-only: refused ({r and r['message'][:120]})")
        e = run_and_answer(lab, "cct.flag-unset", "pass")
        check(e is not None and len(e["assertions"]) == 2 and all(x["ok"] for x in e["assertions"]), "cct.flag-unset: no cheats body ran")
        e = run_and_answer(lab, "cct.cleanup", "pass")
        check("cct.cleanup: the clean-up ran" in lab.kiln_log(), "cct.cleanup: the clean-up ran")

        joined(lab, a.lab_port)

        raw, doc = lab.read_ledger()
        before = [(x["id"], x["verdict"], x["time"]) for x in doc["entries"]]
        check(len(before) >= 8, f"the ledger holds {len(before)} entries")
        refused_ids = {"cct.cheats-flag", "cct.guard-proof", "cct.hosted-only"}
        check(not any(x[0] in refused_ids for x in before), "no refused check has an entry")
        validate(a.ajv, lab.ledger, "before the kill")

        # kill -9 while a check waits for its verdict
        r = lab.cmd(f"run {MOD} cct.wait")
        check(r is not None and r["ok"], "cct.wait: started")
        check(wait_status(lab, lambda d: d.get("current") and d["current"]["phase"] == "asking") is not None, "cct.wait: waits for its verdict")
        raw_before_kill, _ = lab.read_ledger()
        pid = lab.pid()
        check(pid is not None, f"the server's pid from lab.sh pid: {pid}")
        if pid:
            os.kill(pid, signal.SIGKILL)
            end = time.time() + 60
            while time.time() < end and lab.pid():
                time.sleep(0.5)
            check(lab.pid() is None, "the server is gone after kill -9")
        raw_after_kill, doc_after = lab.read_ledger()
        check(raw_after_kill == raw_before_kill, "the ledger on disk is byte for byte the one before the kill")
        check(not any(x["id"] == "cct.wait" for x in doc_after["entries"]), "the interrupted check has no entry")
        check(lab.start(), "the lab started again")
        if not check(wait_runner(lab) is not None, "the runner answers again after the restart"):
            return 1
        _, doc2 = lab.read_ledger()
        after = [(x["id"], x["verdict"], x["time"]) for x in doc2["entries"]]
        check(after[:len(before)] == before, f"every earlier verdict is still there ({len(after)} entries)")
        check(not any(x["id"] == "cct.wait" for x in doc2["entries"]), "after the restart the interrupted check still has no entry")
        state = os.path.join(lab.kiln, "state")
        check(not any(f.startswith("client-checks.unreadable-") for f in os.listdir(state)), "nothing was moved aside (the ledger read whole)")
        e = run_and_answer(lab, "cct.flag-unset", "pass")
        check(e is not None and all(x["ok"] for x in e["assertions"]), "cct.flag-unset after the restart: no cheats body ran")
        validate(a.ajv, lab.ledger, "after the restart")
        check(not os.path.exists(os.path.join(lab.kiln, "data", MOD, "cheats-body-ran")), "no cheats marker file")
        check("CHEATS BODY RAN" not in lab.kiln_log(), "no 'CHEATS BODY RAN' line in kiln.log")
        refusals = re.findall(r"checks: refused [^\n]*", lab.kiln_log())
        info(f"{len(refusals)} refusal line(s) in kiln.log, e.g. {refusals[0][:200] if refusals else '-'}")
    finally:
        lab.stop()
        info("lab stopped")
    print(f"{fails} FAIL")
    return 1 if fails else 0


if __name__ == "__main__":
    sys.exit(main())
