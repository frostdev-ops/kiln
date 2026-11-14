#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""The plan of a guided client pass, read from the sources: which checks exist, in which session each is played, which
capabilities they open, and what is still missing.

    plan.py [--repo <dir>] [--since 0.2.0-dev.28] [--strict] [--json | --held-md]

Reads four things of the repository:
  * the check pack, mods/tests/client-pass/*.cs: every [ClientCheck(...)] attribute (id, title, Area, Since, Tags,
    Needs, Order);
  * the index of the dossier's checks, mods/tests/client-pass/dev28-checks.json: id, state (open, settled), and
    optionally "deferred": "<reason>" for an id the release does not check;
  * the release map, mods/tests/client-pass/dev28-releases.json: in which release each id is played (the dossier
    ships as a release train). packages: work package -> release; owners: check id -> the package that writes it,
    for an id whose chapter has several packages; ids: check id -> release, for a package that ships in parts. An id
    the map says nothing about is planned for --since;
  * the client gate's list, native/core/gates/*.json: capability -> the check ids that open it.

Prints the sessions of the pass with their checks and a time estimate, the capabilities held by the gate (the ones
this pass can open, and the ones that wait for a later release and stay off in this one), and the problems:
  * an open id of this release (or an earlier one) with no check in the pack and no "deferred" reason;
  * a check that claims --since but whose id the index does not have;
  * a check whose Since is not the release the map plans its id for;
  * a gate that names a check id the pack does not have (unless the id is planned for a later release), or a gate
    without any check;
  * two checks with one id, or two checks of one release with one order;
  * an id of a chapter with several packages that the map gives no owner.

--held-md prints the release notes' list "Built, game test pending": the capabilities this pass can open, each with
the titles of its checks.

Sessions (a check is played in the first one that fits):
  F  frameworks profile          tag frameworks
  E  host tools, outside a game  tag host-tools or launcher
  D  two players with Kiln       tag two-players
  C  a client without Kiln       tag vanilla-client
  B  joined to a dedicated lab   Needs = Joined
  A  hosted world                everything else

Exit code 0 when the sources could be read (and, with --strict, when there is no problem), 1 for problems with
--strict, 2 when a source cannot be read. Python 3, standard library only.
"""
from __future__ import annotations

import argparse
import glob
import json
import os
import re
import sys
from typing import Any

SESSIONS = (
    ("A", "hosted world"),
    ("B", "joined to a dedicated lab server"),
    ("C", "a client without Kiln, joined to a Kiln server"),
    ("D", "two players with Kiln"),
    ("E", "host tools, outside a game"),
    ("F", "frameworks profile"),
)
MINUTES = 4  # per check, a planning figure until a pass has been timed

# The tag constants of Kiln.Testing.ClientCheckTags and of the pack (ClientPassMod), by their C# names.
TAG_NAMES = {
    "Cheats": "cheats", "Visual": "visual", "Audio": "audio", "TwoPlayers": "two-players", "GuardProof": "guard-proof",
    "Windows": "windows", "SteamDeck": "steam-deck", "Launcher": "launcher", "OtherHost": "other-host",
    "VanillaClient": "vanilla-client", "HostTools": "host-tools", "Frameworks": "frameworks",
}


class PlanError(Exception):
    """A source cannot be read; the message says why."""


def _split_args(text: str) -> list[str]:
    """The top-level, comma-separated arguments of an attribute, strings and brackets respected."""
    out, depth, cur, i, n = [], 0, [], 0, len(text)
    while i < n:
        c = text[i]
        if c == '"':
            j = i + 1
            while j < n and text[j] != '"':
                j += 2 if text[j] == "\\" else 1
            cur.append(text[i:j + 1])
            i = j + 1
            continue
        if c in "([{":
            depth += 1
        elif c in ")]}":
            depth -= 1
        if c == "," and depth == 0:
            out.append("".join(cur).strip())
            cur = []
        else:
            cur.append(c)
        i += 1
    if "".join(cur).strip():
        out.append("".join(cur).strip())
    return out


def _string(expr: str, consts: dict[str, str]) -> str | None:
    """A C# string expression made of literals and known constants joined by +; None when it is something else."""
    parts = []
    for piece in _split_plus(expr):
        piece = piece.strip()
        m = re.fullmatch(r'"((?:[^"\\]|\\.)*)"', piece, re.S)
        if m:
            parts.append(bytes(m.group(1), "utf-8").decode("unicode_escape") if "\\" in m.group(1) else m.group(1))
        elif piece in consts:
            parts.append(consts[piece])
        else:
            return None
    return "".join(parts)


def _split_plus(expr: str) -> list[str]:
    out, cur, i, n = [], [], 0, len(expr)
    while i < n:
        c = expr[i]
        if c == '"':
            j = i + 1
            while j < n and expr[j] != '"':
                j += 2 if expr[j] == "\\" else 1
            cur.append(expr[i:j + 1])
            i = j + 1
            continue
        if c == "+":
            out.append("".join(cur))
            cur = []
        else:
            cur.append(c)
        i += 1
    out.append("".join(cur))
    return out


def _attribute_bodies(source: str) -> list[tuple[int, str]]:
    """(line, text between the parentheses) of every [ClientCheck(...)] in a C# source."""
    found = []
    for m in re.finditer(r"\[ClientCheck\(", source):
        i, depth, n = m.end(), 1, len(source)
        while i < n and depth:
            c = source[i]
            if c == '"':
                i += 1
                while i < n and source[i] != '"':
                    i += 2 if source[i] == "\\" else 1
            elif c == "(":
                depth += 1
            elif c == ")":
                depth -= 1
            i += 1
        if depth == 0:
            found.append((source.count("\n", 0, m.start()) + 1, source[m.end():i - 1]))
    return found


def read_pack(folder: str) -> list[dict]:
    """Every [ClientCheck] of the pack's sources."""
    files = sorted(glob.glob(os.path.join(folder, "*.cs")))
    if not files:
        raise PlanError(f"no C# sources in {folder}")
    consts: dict[str, str] = {}
    sources = {}
    for f in files:
        try:
            with open(f, encoding="utf-8") as fh:
                sources[f] = fh.read()
        except OSError as e:
            raise PlanError(f"cannot read {f}: {e}") from e
        for m in re.finditer(r'const\s+string\s+(\w+)\s*=\s*"((?:[^"\\]|\\.)*)"\s*;', sources[f]):
            consts[m.group(1)] = m.group(2)
    checks = []
    for f, text in sources.items():
        for line, body in _attribute_bodies(text):
            args = _split_args(body)
            where = f"{os.path.basename(f)}:{line}"
            if len(args) < 2:
                raise PlanError(f"{where}: [ClientCheck] needs an id and a title")
            cid, title = _string(args[0], consts), _string(args[1], consts)
            if cid is None:
                raise PlanError(f"{where}: the check's id is not a string literal or a known constant")
            named = {}
            for a in args[2:]:
                k, _, v = a.partition("=")
                named[k.strip()] = v.strip()
            tags = []
            raw = named.get("Tags", "").strip()
            if raw:
                inner = raw[raw.find("[") + 1:raw.rfind("]")] if "[" in raw else raw
                for t in _split_args(inner):
                    t = t.split(".")[-1].strip()
                    lit = _string(t, consts)
                    tags.append(TAG_NAMES.get(t) or lit or t)
            needs = named.get("Needs", "ClientCheckNeeds.Any").split(".")[-1].strip().lower()
            since = _string(named.get("Since", '""'), consts) or named.get("Since", "")
            try:
                order = int(named.get("Order", "0"))
            except ValueError:
                order = 0
            checks.append({"id": cid, "title": title or "", "area": _string(named.get("Area", '""'), consts) or "",
                           "since": since, "tags": tags, "needs": needs, "order": order, "where": where})
    return checks


def read_index(path: str) -> list[dict]:
    try:
        with open(path, encoding="utf-8") as fh:
            doc = json.load(fh)
    except (OSError, ValueError) as e:
        raise PlanError(f"cannot read the check index {path}: {e}") from e
    rows = doc.get("checks") if isinstance(doc, dict) else None
    if not isinstance(rows, list) or not all(isinstance(r, dict) and isinstance(r.get("id"), str) for r in rows):
        raise PlanError(f"{path}: expected {{\"checks\": [{{\"id\": ...}}]}}")
    return rows


def read_releases(path: str) -> dict:
    """The release map. A missing file is an empty map (every id is planned for --since)."""
    if not os.path.exists(path):
        return {"packages": {}, "owners": {}, "ids": {}}
    try:
        with open(path, encoding="utf-8") as fh:
            doc = json.load(fh)
    except (OSError, ValueError) as e:
        raise PlanError(f"cannot read the release map {path}: {e}") from e
    out = {}
    for key in ("packages", "owners", "ids"):
        part = doc.get(key, {}) if isinstance(doc, dict) else None
        if not isinstance(part, dict) or not all(isinstance(k, str) and isinstance(v, str) for k, v in part.items()):
            raise PlanError(f"{path}: \"{key}\" must be an object of strings")
        out[key] = part
    return out


def release_key(release: str) -> tuple:
    """Orders releases: 0.2.0-dev.9 before 0.2.0-dev.28 before 0.2.0 (a release without a pre-release part is last)."""
    core, _, pre = release.partition("-")
    nums = tuple(int(n) if n.isdigit() else 0 for n in core.split("."))
    if not pre:
        return (nums, 1, ())
    return (nums, 0, tuple((0, int(x), "") if x.isdigit() else (1, 0, x) for x in pre.split(".")))


def release_of(row: dict, releases: dict, since: str) -> tuple[str, str | None]:
    """(release, problem) of an index row: ids, then the owner's (or the only package's) release, else --since."""
    i = row["id"]
    if i in releases["ids"]:
        return releases["ids"][i], None
    packages = [p for p in row.get("packages") or [] if isinstance(p, str)]
    owner = releases["owners"].get(i) or (packages[0] if len(packages) == 1 else None)
    if owner is None:
        if len(packages) > 1 and releases["packages"]:
            return since, (f"{i} belongs to a chapter with several packages ({', '.join(packages)}) and the release "
                           f"map names no owner for it: add it under \"owners\" in dev28-releases.json")
        return since, None
    if owner in releases["packages"]:
        return releases["packages"][owner], None
    if releases["packages"]:
        return since, f"{i}: the release map has no release for its package {owner} (dev28-releases.json, \"packages\")"
    return since, None


def read_gates(folder: str) -> list[dict]:
    """The gate list, read as native/core/gates/gen.py reads it (name, side, checks), without its build-stopping checks."""
    gates = []
    for f in sorted(glob.glob(os.path.join(folder, "*.json"))):
        try:
            with open(f, encoding="utf-8") as fh:
                doc = json.load(fh)
        except (OSError, ValueError) as e:
            raise PlanError(f"cannot read the gate file {f}: {e}") from e
        for g in (doc.get("gates") or []) if isinstance(doc, dict) else []:
            if isinstance(g, dict) and isinstance(g.get("name"), str):
                gates.append({"name": g["name"], "side": g.get("side") or "both",
                              "checks": [c for c in g.get("checks") or [] if isinstance(c, str)],
                              "file": os.path.basename(f)})
    return sorted(gates, key=lambda g: g["name"])


def session_of(check: dict) -> str:
    tags = set(check["tags"])
    if "frameworks" in tags:
        return "F"
    if "host-tools" in tags or "launcher" in tags:
        return "E"
    if "two-players" in tags:
        return "D"
    if "vanilla-client" in tags:
        return "C"
    return "B" if check["needs"] == "joined" else "A"


def build(pack: list[dict], index: list[dict], gates: list[dict], since: str, releases: dict | None = None) -> dict:
    """The plan as data."""
    releases = releases or {"packages": {}, "owners": {}, "ids": {}}
    problems: list[str] = []
    planned: dict[str, str] = {}          # check id -> the release it is played in
    for r in index:
        planned[r["id"]], problem = release_of(r, releases, since)
        if problem:
            problems.append(problem)
    now = release_key(since)
    by_id: dict[str, dict] = {}
    for c in pack:
        if c["id"] in by_id:
            problems.append(f"two checks have the id {c['id']} ({by_id[c['id']]['where']}, {c['where']})")
        by_id[c["id"]] = c
    mine = [c for c in pack if c["since"] == since]
    orders: dict[int, dict] = {}
    for c in mine:
        if c["order"] in orders and c["order"] != 0:
            problems.append(f"{c['id']} and {orders[c['order']]['id']} share the order {c['order']}")
        orders[c["order"]] = c
    known = {r["id"] for r in index}
    for c in mine:
        if c["id"] not in known:
            problems.append(f"{c['id']} ({c['where']}) is not in the check index: add it there, or correct the id")
        if c["id"] != c["id"].lower():
            problems.append(f"{c['id']} ({c['where']}): ids are lower-case")
    for c in pack:
        want = planned.get(c["id"])
        if want is not None and c["since"] != want and (c["since"] == since or want == since):
            problems.append(f"{c['id']} ({c['where']}) says Since = {c['since'] or '(none)'}, the release map plans it "
                            f"for {want}: use that release's constant, or move the id in dev28-releases.json")
    missing, deferred, settled_unchecked = [], [], []
    later: dict[str, int] = {}
    for r in index:
        if release_key(planned[r["id"]]) > now:
            later[planned[r["id"]]] = later.get(planned[r["id"]], 0) + 1
            continue
        if r["id"] in by_id:
            continue
        if r.get("deferred"):
            deferred.append({"id": r["id"], "reason": r["deferred"]})
        elif r.get("state") == "open":
            missing.append(r["id"])
        else:
            settled_unchecked.append(r["id"])
    for i in missing:
        problems.append(f"open check {i} has no [ClientCheck] in the pack and no \"deferred\" reason in the index")
    sessions = {k: [] for k, _ in SESSIONS}
    for c in sorted(mine, key=lambda c: (c["order"], c["id"])):
        sessions[session_of(c)].append({"id": c["id"], "title": c["title"], "area": c["area"], "order": c["order"],
                                        "tags": c["tags"], "needs": c["needs"]})
    gate_rows = []
    for g in gates:
        if not g["checks"]:
            problems.append(f"gate {g['name']} ({g['file']}) names no check")
        # A gate waits for the latest release among its checks: until then it stays off, and a check that is not
        # written yet is no problem of this pass.
        waits = max((planned[c] for c in g["checks"] if c in planned and release_key(planned[c]) > now),
                    key=release_key, default=None)
        unknown = [c for c in g["checks"] if c not in by_id]
        for c in unknown:
            if c in planned and release_key(planned[c]) > now:
                continue
            problems.append(f"gate {g['name']} ({g['file']}) names the check {c}, which the pack does not have")
        gate_rows.append({"name": g["name"], "side": g["side"], "checks": g["checks"], "file": g["file"],
                          "sessions": sorted({session_of(by_id[c]) for c in g["checks"] if c in by_id}),
                          "titles": [by_id[c]["title"] for c in g["checks"] if c in by_id],
                          "unknown": unknown, "later": waits})
    return {"since": since, "checks": len(mine), "index": len(index),
            "index_now": sum(1 for r in index if release_key(planned[r["id"]]) <= now),
            "later": [{"release": k, "ids": later[k]} for k in sorted(later, key=release_key)],
            "sessions": [{"key": k, "what": what, "checks": sessions[k], "minutes": len(sessions[k]) * MINUTES}
                         for k, what in SESSIONS],
            "gates": gate_rows, "missing": missing, "deferred": deferred, "settled_without_check": settled_unchecked,
            "problems": problems}


def render(plan: dict) -> str:
    out = [f"Guided pass for {plan['since']}: {plan['checks']} check(s) in the pack, {plan['index_now']} id(s) of "
           f"{plan['index']} in the index are this release's"]
    if plan["later"]:
        out.append("Played in a later release: " + ", ".join(f"{x['ids']} in {x['release']}" for x in plan["later"]))
    total = 0
    for s in plan["sessions"]:
        total += s["minutes"]
        out.append("")
        out.append(f"Session {s['key']}: {s['what']} ({len(s['checks'])} check(s), about {s['minutes']} min)")
        for c in s["checks"]:
            tags = f" [{', '.join(c['tags'])}]" if c["tags"] else ""
            out.append(f"  {c['order']:>5}  {c['id']:<40} {c['title']}{tags}")
    out.append("")
    out.append(f"About {total} min of checks in all (a planning figure: {MINUTES} min a check).")
    out.append("")
    opens = [g for g in plan["gates"] if not g["later"]]
    waits = [g for g in plan["gates"] if g["later"]]
    out.append(f"Held by the client gate, opened by this pass: {len(opens)} capability(ies)")
    for g in opens:
        side = "" if g["side"] == "both" else f" ({g['side']} only)"
        where = f" session {'/'.join(g['sessions'])}" if g["sessions"] else ""
        out.append(f"  {g['name']}{side}: {', '.join(g['checks']) or '(no check)'}{where}")
    if waits:
        out.append("")
        out.append(f"Held for a later release (stays off in {plan['since']}): {len(waits)} capability(ies)")
        for g in waits:
            side = "" if g["side"] == "both" else f" ({g['side']} only)"
            out.append(f"  {g['name']}{side}: {', '.join(g['checks'])}; {g['later']}")
    if plan["deferred"]:
        out.append("")
        out.append(f"Deferred ({len(plan['deferred'])}):")
        for d in plan["deferred"]:
            out.append(f"  {d['id']}: {d['reason']}")
    if plan["settled_without_check"]:
        out.append("")
        out.append(f"Settled by research, no product check ({len(plan['settled_without_check'])}): "
                   + ", ".join(plan["settled_without_check"]))
    out.append("")
    if plan["problems"]:
        out.append(f"Problems ({len(plan['problems'])}):")
        out.extend("  " + p for p in plan["problems"])
    else:
        out.append("No problems.")
    return "\n".join(out) + "\n"


def render_held_md(plan: dict) -> str:
    """The release notes' list: the capabilities this pass can open, with the titles of their checks."""
    out = []
    for g in plan["gates"]:
        if g["later"]:
            continue
        side = "" if g["side"] == "both" else f" (on a game client; a dedicated server has it)" if g["side"] == "client" \
            else f" (on a dedicated server; a game client has it)"
        checks = "; ".join(f"{t} (`{c}`)" for c, t in zip(g["checks"], g["titles"])) if len(g["titles"]) == len(g["checks"]) \
            else ", ".join(f"`{c}`" for c in g["checks"])
        out.append(f"- `{g['name']}`{side}: {checks}")
    return "\n".join(out) + ("\n" if out else "")


def main(argv: list[str] | None = None) -> int:
    p = argparse.ArgumentParser(description="The plan of a guided client pass, read from the sources.")
    p.add_argument("--repo", default=os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".."))
    p.add_argument("--since", default="0.2.0-dev.28", help="the release whose checks are planned")
    p.add_argument("--strict", action="store_true", help="exit 1 when there is a problem")
    p.add_argument("--json", action="store_true")
    p.add_argument("--held-md", action="store_true", help="print the release notes' list of held capabilities")
    args = p.parse_args(argv)
    try:
        pack = read_pack(os.path.join(args.repo, "mods", "tests", "client-pass"))
        index = read_index(os.path.join(args.repo, "mods", "tests", "client-pass", "dev28-checks.json"))
        gates = read_gates(os.path.join(args.repo, "native", "core", "gates"))
        releases = read_releases(os.path.join(args.repo, "mods", "tests", "client-pass", "dev28-releases.json"))
    except PlanError as e:
        print(f"plan: {e}", file=sys.stderr)
        return 2
    plan = build(pack, index, gates, args.since, releases)
    sys.stdout.write(json.dumps(plan, indent=1) + "\n" if args.json else
                     render_held_md(plan) if args.held_md else render(plan))
    return 1 if args.strict and plan["problems"] else 0


if __name__ == "__main__":
    sys.exit(main())
