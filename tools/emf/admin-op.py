#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""Drives the server-admin EMF mod's file protocol in a lab (requests/<id>.json -> results/<id>.json).

    tools/emf/admin-op.py <server dir> <op> [json fields...]     e.g.  admin-op.py ~/srv/enshrouded/labs/emf ping
    tools/emf/admin-op.py <dir> settings.set key=experienceCombatFactor value=1.5 expected=1.0
Prints the result JSON (waits up to 30 s). Only for labs: it writes into <server>/shroudforge/runtime/admin/engine/.
"""
import json, os, secrets, sys, time
from pathlib import Path

server, op, *fields = sys.argv[1:]
ipc = Path(server) / "shroudforge/runtime/admin/engine"
state = json.loads((ipc / "state.json").read_text())
rid = secrets.token_hex(16)
req = {"id": rid, "createdAt": time.time(), "op": op, "session": state["session"]}
for f in fields:
    k, v = f.split("=", 1)
    try:
        req[k] = json.loads(v)
    except ValueError:
        req[k] = v
tmp = ipc / "requests" / (rid + ".tmp")
tmp.write_text(json.dumps(req))
os.replace(tmp, ipc / "requests" / (rid + ".json"))
res = ipc / "results" / (rid + ".json")
for _ in range(300):
    if res.exists():
        print(res.read_text())
        sys.exit(0)
    time.sleep(0.1)
print(json.dumps({"id": rid, "ok": False, "data": {"error": "no result in 30 s"}}))
sys.exit(1)
