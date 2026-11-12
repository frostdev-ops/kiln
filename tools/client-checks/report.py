#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""Summarise a guided check ledger (kiln/state/client-checks.json, format 1).

    report.py <ledger> [--rows mods/tests/client-pass/rows.json] [--json]

Prints passed, failed, skipped and never run per area, then the failed checks with their notes, and with --rows each
status row with the state of its checks. The newest entry of a check counts (entries are appended oldest first); "never
run" comes from the ledger's catalogue of checks. A pass whose automatic assertions failed is printed as passed with a
mark and counted on a line of its own. --json prints the same as one JSON document.

Exit code 0 when the ledger (and the rows file, when given) could be read, 2 when not.
Python 3, standard library only. The ledger's shape: schema/state-client-checks.schema.json.
"""
from __future__ import annotations

import argparse
import json
import sys
from typing import Any

FORMAT = 1
STATES = ("passed", "failed", "skipped", "never run")
VERDICT_STATE = {"pass": "passed", "fail": "failed", "skip": "skipped"}
MARK = "*"  # a pass with a failed assertion


class LedgerError(Exception):
    """The ledger or the rows file cannot be used; the message says why."""


def load_json(path: str, what: str) -> Any:
    try:
        with open(path, encoding="utf-8") as f:
            return json.load(f)
    except OSError as e:
        raise LedgerError(f"cannot read the {what} {path}: {e.strerror or e}") from e
    except (UnicodeDecodeError, json.JSONDecodeError) as e:
        raise LedgerError(f"the {what} {path} is not valid JSON: {e}") from e


def check_ledger(ledger: Any) -> None:
    if not isinstance(ledger, dict):
        raise LedgerError("the ledger is not a JSON object")
    fmt = ledger.get("format")
    if fmt != FORMAT:
        raise LedgerError(f"the ledger has format {fmt!r}; this script reads format {FORMAT} only")
    for key in ("checks", "entries"):
        if not isinstance(ledger.get(key), list):
            raise LedgerError(f"the ledger has no list '{key}'")


def check_rows(rows: Any) -> list[dict]:
    if not isinstance(rows, dict) or not isinstance(rows.get("rows"), list):
        raise LedgerError("the rows file has no list 'rows'")
    out = []
    for i, r in enumerate(rows["rows"]):
        if not isinstance(r, dict) or not isinstance(r.get("key"), str) or not isinstance(r.get("checks"), list):
            raise LedgerError(f"row {i} of the rows file needs 'key' and 'checks'")
        out.append(r)
    return out


def _failed_assertions(entry: dict) -> list[dict]:
    return [a for a in entry.get("assertions") or [] if isinstance(a, dict) and a.get("ok") is False]


def summarize(ledger: dict, rows: list[dict] | None = None, rows_mod: str = "client-pass") -> dict:
    """The report as data: per area counts, the failed checks, and the rows (when given)."""
    check_ledger(ledger)
    checks: dict[tuple[str, str], dict] = {}
    for c in ledger["checks"]:
        if isinstance(c, dict) and isinstance(c.get("mod"), str) and isinstance(c.get("id"), str):
            checks[(c["mod"], c["id"])] = c
    newest: dict[tuple[str, str], dict] = {}
    for e in ledger["entries"]:
        if isinstance(e, dict) and isinstance(e.get("mod"), str) and isinstance(e.get("id"), str):
            newest[(e["mod"], e["id"])] = e  # oldest first: a later entry replaces an earlier one
            checks.setdefault((e["mod"], e["id"]), {"mod": e["mod"], "id": e["id"], "title": e.get("title", ""),
                                                     "area": e.get("area", "")})

    results: dict[tuple[str, str], dict] = {}
    for key, c in checks.items():
        e = newest.get(key)
        state = VERDICT_STATE.get(e.get("verdict"), "never run") if e else "never run"
        bad = _failed_assertions(e) if e else []
        results[key] = {
            "mod": key[0], "id": key[1], "title": c.get("title") or (e or {}).get("title", ""),
            "area": c.get("area") or (e or {}).get("area") or key[0],
            "state": state, "note": (e or {}).get("note", ""), "time": (e or {}).get("time"),
            "failedAssertions": [{"what": a.get("what", ""), "detail": a.get("detail")} for a in bad],
            "marked": state == "passed" and bool(bad),
        }

    areas: dict[str, dict] = {}
    for r in sorted(results.values(), key=lambda r: (r["area"], r["mod"], r["id"])):
        a = areas.setdefault(r["area"], {s: 0 for s in STATES} | {"passedWithFailedAssertions": 0})
        a[r["state"]] += 1
        if r["marked"]:
            a["passedWithFailedAssertions"] += 1
    totals = {s: sum(a[s] for a in areas.values()) for s in STATES}
    totals["passedWithFailedAssertions"] = sum(a["passedWithFailedAssertions"] for a in areas.values())

    report: dict[str, Any] = {
        "format": FORMAT,
        "areas": areas,
        "totals": totals,
        "failed": [r for r in sorted(results.values(), key=lambda r: (r["area"], r["id"])) if r["state"] == "failed"],
        "marked": [r for r in sorted(results.values(), key=lambda r: (r["area"], r["id"])) if r["marked"]],
    }
    if rows is not None:
        by_id: dict[str, list[dict]] = {}
        for r in results.values():
            by_id.setdefault(r["id"], []).append(r)
        out_rows = []
        for row in rows:
            states = []
            for ref in row["checks"]:
                mod, _, cid = ref.rpartition("/") if "/" in ref else (rows_mod, "", ref)
                found = results.get((mod, cid)) or (by_id.get(cid, [None])[0] if "/" not in ref else None)
                states.append({"check": ref, "state": found["state"] if found else "never run",
                               "marked": bool(found and found["marked"]), "known": found is not None})
            out_rows.append({"section": row.get("section", ""), "key": row["key"], "state": row_state(states),
                             "checks": states})
        report["rows"] = out_rows
    return report


def row_state(states: list[dict]) -> str:
    """failed if any check failed; passed if every check passed; else open."""
    if any(s["state"] == "failed" for s in states):
        return "failed"
    if states and all(s["state"] == "passed" for s in states):
        return "passed"
    return "open"


def render(report: dict) -> str:
    lines = []
    width = max([len(a) for a in report["areas"]] + [4])
    lines.append(f"{'area':<{width}}  passed  failed  skipped  never run")
    for name, a in report["areas"].items():
        lines.append(f"{name:<{width}}  {a['passed']:>6}  {a['failed']:>6}  {a['skipped']:>7}  {a['never run']:>9}")
    t = report["totals"]
    lines.append(f"{'all':<{width}}  {t['passed']:>6}  {t['failed']:>6}  {t['skipped']:>7}  {t['never run']:>9}")
    lines.append(f"passed with a failed assertion ({MARK}): {t['passedWithFailedAssertions']}")
    if report["marked"]:
        for r in report["marked"]:
            what = "; ".join(a["what"] + (f" ({a['detail']})" if a["detail"] else "") for a in r["failedAssertions"])
            lines.append(f"  passed{MARK} {r['mod']}/{r['id']}: {what}")
    lines.append("")
    lines.append(f"failed: {len(report['failed'])}")
    for r in report["failed"]:
        lines.append(f"  {r['mod']}/{r['id']} ({r['area']}): {r['title']}")
        if r["note"]:
            lines.append(f"    note: {r['note']}")
        for a in r["failedAssertions"]:
            lines.append(f"    assertion failed: {a['what']}" + (f" ({a['detail']})" if a["detail"] else ""))
    if "rows" in report:
        lines.append("")
        section = None
        for row in report["rows"]:
            if row["section"] != section:
                section = row["section"]
                lines.append(f"{section}:")
            parts = []
            for s in row["checks"]:
                state = s["state"] + (MARK if s["marked"] else "")
                parts.append(f"{s['check']} {state}" + ("" if s["known"] else " (not in the ledger)"))
            lines.append(f"  [{row['state']}] {row['key']}")
            lines.append(f"      {', '.join(parts)}")
    return "\n".join(lines) + "\n"


def main(argv: list[str] | None = None) -> int:
    p = argparse.ArgumentParser(description="Summarise a guided check ledger (kiln/state/client-checks.json).")
    p.add_argument("ledger", help="the ledger file")
    p.add_argument("--rows", help="a rows file (mods/tests/client-pass/rows.json) to print each status row's state")
    p.add_argument("--json", action="store_true", help="print the report as JSON")
    args = p.parse_args(argv)
    try:
        ledger = load_json(args.ledger, "ledger")
        rows = check_rows(load_json(args.rows, "rows file")) if args.rows else None
        report = summarize(ledger, rows)
    except LedgerError as e:
        print(f"report.py: {e}", file=sys.stderr)
        return 2
    if args.json:
        json.dump(report, sys.stdout, indent=2, ensure_ascii=False)
        sys.stdout.write("\n")
    else:
        sys.stdout.write(render(report))
    return 0


if __name__ == "__main__":
    sys.exit(main())
