#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""Writes docs/kiln/status.generated.md from the state files a running Kiln writes.

  python3 -I tools/docs-site/status-tables.py --server <capabilities.json> [--client <capabilities.json>]
      --checks <client-checks.json> [-o docs/kiln/status.generated.md]

Inputs (formats fixed by schema/state-capabilities.schema.json and schema/state-client-checks.schema.json):
  --server  <game>/kiln/state/capabilities.json of a dedicated server boot (side "server")
  --client  the same file of a game client boot (side "client"); without it the client column says "no client file"
  --checks  <game>/kiln/state/client-checks.json, the guided check ledger
Server and client are separate boots, so they are separate files.

Output, per area (the part of a capability name before the first dot): capability, server state, client state, and the
reason where it is not available. Then the guided checks per area: the newest entry of each check counts, a check in
the catalogue with no entry is "never run". The header names the Kiln version, game build and side of the inputs, never
a host name, path, pid or the time this script ran: the same inputs give the same bytes.

Without -o the page goes to stdout. Exit 0 when written, 2 when an input cannot be used (the message says why).
Unknown extra keys in the inputs are ignored. Python 3, standard library only.
"""
from __future__ import annotations

import argparse
import json
import os
import sys
from typing import Any

FORMAT = 1
STATES = ("available", "unavailable", "pending")
CAPS_KEYS = ("format", "kiln", "pid", "written", "side", "exe", "hooks", "generation", "capabilities")
ROW_KEYS = ("name", "state", "reason", "reasonText", "source")
CHECK_KEYS = ("mod", "id", "title", "area")
ENTRY_KEYS = ("mod", "id", "title", "area", "kiln", "game", "session", "verdict", "time")
VERDICTS = ("pass", "fail", "skip")
NO_CLIENT = "no client file"
NO_ROW = "no row"
NEVER = "never run"


class InputError(Exception):
    """An input cannot be used; the message says which and why."""


def load_json(path: str, what: str) -> Any:
    try:
        with open(path, encoding="utf-8") as f:
            return json.load(f)
    except OSError as e:
        raise InputError(f"cannot read the {what} {path}: {e.strerror or e}") from e
    except (UnicodeDecodeError, json.JSONDecodeError) as e:
        raise InputError(f"the {what} {path} is not valid JSON: {e}") from e


def _format(doc: Any, what: str) -> None:
    if not isinstance(doc, dict):
        raise InputError(f"the {what} is not a JSON object")
    fmt = doc.get("format")
    if type(fmt) is not int or fmt != FORMAT:
        raise InputError(f"the {what} has format {fmt!r}; this script reads format {FORMAT} only")


def _need(obj: dict, keys: tuple, what: str) -> None:
    missing = [k for k in keys if k not in obj]
    if missing:
        raise InputError(f"{what} has no {', '.join(repr(k) for k in missing)}")


def check_caps(doc: Any, what: str, side: str) -> dict:
    """Validates a capabilities.json (the keys this script relies on) and returns {name: row}."""
    _format(doc, what)
    _need(doc, CAPS_KEYS, f"the {what}")
    if doc["side"] != side:
        raise InputError(f"the {what} is from a {doc['side']!r} boot; {side!r} expected")
    exe = doc["exe"]
    if not isinstance(exe, dict):
        raise InputError(f"the {what}'s 'exe' is not an object")
    _need(exe, ("sha256", "verified"), f"the {what}'s 'exe'")
    if not isinstance(doc["kiln"], str) or not isinstance(exe["sha256"], str):
        raise InputError(f"the {what}'s 'kiln' and 'exe.sha256' must be strings")
    if not isinstance(doc["capabilities"], list):
        raise InputError(f"the {what}'s 'capabilities' is not a list")
    rows: dict[str, dict] = {}
    for i, row in enumerate(doc["capabilities"]):
        if not isinstance(row, dict):
            raise InputError(f"capability {i} of the {what} is not an object")
        _need(row, ROW_KEYS, f"capability {i} of the {what}")
        for k in ROW_KEYS:
            if not isinstance(row[k], str):
                raise InputError(f"capability {i} of the {what}: '{k}' is not a string")
        if not row["name"]:
            raise InputError(f"capability {i} of the {what} has an empty name")
        if row["state"] not in STATES:
            raise InputError(f"capability {row['name']} of the {what} has state {row['state']!r} (not one of {', '.join(STATES)})")
        if row["name"] in rows:
            raise InputError(f"capability {row['name']} is in the {what} twice")
        rows[row["name"]] = row
    return rows


def check_ledger(doc: Any) -> tuple[list[dict], list[dict]]:
    what = "check ledger"
    _format(doc, what)
    _need(doc, ("checks", "entries"), f"the {what}")
    for key in ("checks", "entries"):
        if not isinstance(doc[key], list):
            raise InputError(f"the {what}'s '{key}' is not a list")
    for i, c in enumerate(doc["checks"]):
        if not isinstance(c, dict):
            raise InputError(f"check {i} of the {what} is not an object")
        _need(c, CHECK_KEYS, f"check {i} of the {what}")
    for i, e in enumerate(doc["entries"]):
        if not isinstance(e, dict):
            raise InputError(f"entry {i} of the {what} is not an object")
        _need(e, ENTRY_KEYS, f"entry {i} of the {what}")
        if not isinstance(e["game"], dict):
            raise InputError(f"entry {i} of the {what}: 'game' is not an object")
        _need(e["game"], ("side", "sha256", "verified"), f"entry {i} of the {what}'s 'game'")
        if e["verdict"] not in VERDICTS:
            raise InputError(f"entry {i} of the {what} has verdict {e['verdict']!r} (not one of {', '.join(VERDICTS)})")
        if not isinstance(e["time"], str):
            raise InputError(f"entry {i} of the {what}: 'time' is not a string")
    return doc["checks"], doc["entries"]


# --- rendering -------------------------------------------------------------------------------------------------------

_ESCAPE = str.maketrans({c: "\\" + c for c in "\\`*_[]<>|"})


def cell(text: Any) -> str:
    """Plain text for one table cell: Markdown punctuation escaped, one line."""
    return " ".join(str(text).split()).translate(_ESCAPE)


def code(text: str) -> str:
    return "`" + text.replace("`", "'") + "`"


def area_of(name: str) -> str:
    return name.split(".", 1)[0]


def sha12(sha: Any) -> str:
    return sha[:12] if isinstance(sha, str) and sha else "unknown"


def build_line(doc: dict) -> str:
    exe = doc["exe"]
    build = code(exe["sha256"]) if exe["sha256"] else "unknown (the executable could not be read)"
    verified = "a verified build" if exe["verified"] else "not in the verified-builds list"
    hooks = "hooks on" if doc["hooks"] is True else f"hooks off (safe idle: {cell(doc.get('safeIdle') or 'no reason given')})"
    return (f"Kiln {code(doc['kiln'])}, side {doc['side']}, game build {build} ({verified}), {hooks}, "
            f"file written {cell(str(doc['written'])[:10])}")


def state_text(row: dict | None, have_file: bool) -> str:
    if not have_file:
        return NO_CLIENT
    if row is None:
        return NO_ROW
    return {"available": "available", "pending": "depends on the session", "unavailable": "unavailable"}[row["state"]]


def why(row: dict | None) -> str:
    """The reason of a row that is not available: its reasonText, else its reason code."""
    if row is None or row["state"] == "available":
        return ""
    text = row["reasonText"].strip() or row["reason"]
    return cell(text)


def reason_cell(srv: dict | None, cli: dict | None, have_client: bool) -> str:
    """Unlabelled when only the server file is read or both sides give the same reason; else per side."""
    s, c = why(srv), why(cli)
    if s and c:
        return s if s == c else f"server: {s}; client: {c}"
    if s:
        return f"server: {s}" if have_client else s
    if c:
        return f"client: {c}"
    return ""


def render(server: dict, server_rows: dict, client: dict | None, client_rows: dict | None,
           checks: list[dict], entries: list[dict], command: str) -> str:
    out: list[str] = []
    w = out.append
    w("# Kiln status (generated)")
    w("")
    w("<!-- Generated by tools/docs-site/status-tables.py; do not edit by hand. Run it again on new state files. -->")
    w("")
    w("This page is generated; do not edit it by hand. It was written by")
    w("")
    w("```")
    w(command)
    w("```")
    w("")
    w("from these files:")
    w("")
    w(f"- server: {build_line(server)}")
    w(f"- client: {build_line(client) if client else 'no client file (no client boot was read; the client column says so)'}")
    kv = sorted({e["kiln"] for e in entries if isinstance(e["kiln"], str)})
    gb = sorted({(str(e["game"]["side"]), sha12(e["game"]["sha256"])) for e in entries})
    w(f"- client checks: {len(checks)} in the catalogue, {len(entries)} verdicts"
      + (f", from Kiln {', '.join(code(v) for v in kv)} on game build {', '.join(f'{side} {code(b)}' for side, b in gb)}" if entries else ""))
    w("")
    w("\"Available\" means available on the server (or client) build these files came from: the symbols resolved and the")
    w("service loaded. It does not say the feature works in the game. Only a passed client check is a real-client statement.")
    w("")

    # capabilities
    names = sorted(set(server_rows) | set(client_rows or {}), key=lambda n: n.encode("utf-8"))
    areas: dict[str, list[str]] = {}
    for n in names:
        areas.setdefault(area_of(n), []).append(n)
    w("## Capabilities")
    w("")
    w("\"Depends on the session\" is a row whose state is decided once a world is loaded (`pending` in the file).")
    w("")
    w("| Area | Server: available | Client: available |")
    w("|---|---|---|")
    for area in sorted(areas, key=lambda a: a.encode("utf-8")):
        ns = areas[area]
        s_rows = [server_rows[n] for n in ns if n in server_rows]
        s = f"{sum(r['state'] == 'available' for r in s_rows)} of {len(s_rows)}"
        if client_rows is None:
            c = NO_CLIENT
        else:
            c_rows = [client_rows[n] for n in ns if n in client_rows]
            c = f"{sum(r['state'] == 'available' for r in c_rows)} of {len(c_rows)}"
        w(f"| {cell(area)} | {s} | {c} |")
    w("")
    for area in sorted(areas, key=lambda a: a.encode("utf-8")):
        w(f"### {cell(area)}")
        w("")
        w("| Capability | Server | Client | Why not available |")
        w("|---|---|---|---|")
        for n in areas[area]:
            srv = server_rows.get(n)
            cli = client_rows.get(n) if client_rows is not None else None
            w(f"| {code(n)} | {state_text(srv, True)} | {state_text(cli, client_rows is not None)} | {reason_cell(srv, cli, client_rows is not None)} |")
        w("")

    # client checks
    latest: dict[tuple, dict] = {}
    for e in entries:  # oldest first: the last one of a check is its newest
        latest[(e["mod"], e["id"])] = e
    catalogue: dict[tuple, dict] = {}
    for c in checks:
        catalogue[(c["mod"], c["id"])] = c
    for key, e in latest.items():  # an entry whose check left the catalogue still shows
        catalogue.setdefault(key, {"mod": e["mod"], "id": e["id"], "title": e["title"], "area": e["area"]})
    by_area: dict[str, list[dict]] = {}
    for key, c in catalogue.items():
        area = c["area"]  # the catalogue holds the newest registration's fields
        by_area.setdefault(area if isinstance(area, str) and area else "(no area)", []).append(c)
    w("## Client checks")
    w("")
    w("The newest verdict of each guided check (`[ClientCheck]`, docs/kiln/client-pass.md). A verdict says what")
    w("whoever answered it saw. Session `server` was answered through a dedicated server's control folder, and on a lab")
    w("a `joined` verdict can come from a scripted client: both prove the check runner, not the game. A pass is a")
    w("real-client statement only when a player gave it in the game client.")
    w("")
    if not catalogue:
        w("The ledger holds no checks.")
        w("")
    for area in sorted(by_area, key=lambda a: a.encode("utf-8")):
        w(f"### {cell(area)}")
        w("")
        w("| Mod | Check | Title | Last verdict | Date | Session | Kiln | Game build |")
        w("|---|---|---|---|---|---|---|---|")
        rows = sorted(by_area[area], key=lambda c: (c.get("order") if type(c.get("order")) is int else 0,
                                                    str(c["mod"]), str(c["id"])))
        for c in rows:
            e = latest.get((c["mod"], c["id"]))
            title = cell(c["title"])
            if e is None:
                w(f"| {cell(c['mod'])} | {code(str(c['id']))} | {title} | {NEVER} |  |  |  |  |")
            else:
                g = e["game"]
                build = f"{cell(g['side'])} {code(sha12(g['sha256']))}"
                w(f"| {cell(c['mod'])} | {code(str(c['id']))} | {title} | {e['verdict']} | {cell(e['time'][:10])} | "
                  f"{cell(e['session'])} | {code(str(e['kiln']))} | {build} |")
        w("")
    return "\n".join(out).rstrip("\n") + "\n"


def command_line(a: argparse.Namespace) -> str:
    """The command as it is shown in the page: file names only, no folders."""
    parts = ["python3 -I tools/docs-site/status-tables.py", "--server", os.path.basename(a.server)]
    if a.client:
        parts += ["--client", os.path.basename(a.client)]
    parts += ["--checks", os.path.basename(a.checks)]
    if a.output:
        parts += ["-o", "docs/kiln/status.generated.md" if os.path.basename(a.output) == "status.generated.md"
                  else os.path.basename(a.output)]
    return " ".join(parts)


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description="Write docs/kiln/status.generated.md from Kiln's state files.")
    ap.add_argument("--server", required=True, help="capabilities.json of a server boot")
    ap.add_argument("--client", help="capabilities.json of a client boot (optional)")
    ap.add_argument("--checks", required=True, help="client-checks.json, the guided check ledger")
    ap.add_argument("-o", "--output", help="the page to write (default: stdout)")
    a = ap.parse_args(argv)
    try:
        server = load_json(a.server, "server capabilities file")
        server_rows = check_caps(server, "server capabilities file", "server")
        client = client_rows = None
        if a.client:
            client = load_json(a.client, "client capabilities file")
            client_rows = check_caps(client, "client capabilities file", "client")
        checks, entries = check_ledger(load_json(a.checks, "check ledger"))
    except InputError as e:
        print(f"status-tables: {e}", file=sys.stderr)
        return 2
    page = render(server, server_rows, client, client_rows, checks, entries, command_line(a))
    if a.output:
        tmp = a.output + ".tmp"
        with open(tmp, "w", encoding="utf-8", newline="\n") as f:
            f.write(page)
        os.replace(tmp, a.output)
    else:
        sys.stdout.write(page)
    return 0


if __name__ == "__main__":
    sys.exit(main())
