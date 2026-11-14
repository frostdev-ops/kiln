#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""The plan of a guided client pass, read from the sources: which checks exist, in which session each is played, which
capabilities they open, and what is still missing.

    plan.py [--repo <dir>] [--since 0.2.0-dev.28] [--strict] [--json]

Reads three things of the repository:
  * the check pack, mods/tests/client-pass/*.cs: every [ClientCheck(...)] attribute (id, title, Area, Since, Tags,
    Needs, Order);
  * the index of the dossier's checks, mods/tests/client-pass/dev28-checks.json: id, state (open, settled), and
    optionally "deferred": "<reason>" for an id the release does not check;
  * the client gate's list, native/core/gates/*.json: capability -> the check ids that open it.

Prints the sessions of the pass with their checks and a time estimate, the capabilities held by the gate with the
state of their checks, and the problems:
  * an open id of the index with no check in the pack and no "deferred" reason;
  * a check that claims --since but whose id the index does not have;
  * a gate that names a check id the pack does not have, or a gate without any check;
  * two checks with one id, or two checks of one release with one order.

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


# Mask comments and C# literals before looking for code. Keep offsets and line numbers intact.
NON_CODE = re.compile(r'//[^\n]*|/\*.*?\*/|(?P<raw>"{3,}).*?(?P=raw)|@"(?:[^"]|"")*"|'
                      r'"(?:[^"\\]|\\.)*"|\'(?:[^\'\\]|\\.)*\'', re.S)


def _masked(source: str, comments_only: bool = False) -> str:
    def mask(m):
        if comments_only and not m.group().startswith(("//", "/*")):
            return m.group()
        return re.sub(r"[^\n]", " ", m.group())
    return NON_CODE.sub(mask, source)


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


def _string(expr: str, consts: dict[str, str | None]) -> str | None:
    """A C# string expression made of literals and known constants joined by +; None when it is something else."""
    parts = []
    for piece in _split_plus(expr):
        piece = piece.strip()
        m = re.fullmatch(r'"((?:[^"\\]|\\.)*)"', piece, re.S)
        if m:
            escapes = {'0': '\0', 'a': '\a', 'b': '\b', 'f': '\f', 'n': '\n', 'r': '\r', 't': '\t', 'v': '\v',
                       '"': '"', "'": "'", '\\': '\\'}
            def decode(e):
                value = e.group()[1:]
                return escapes[value] if value in escapes else chr(int(value[1:], 16))
            parts.append(re.sub(r'\\(?:[0abfnrtv"\'\\]|u[0-9a-fA-F]{4}|U[0-9a-fA-F]{8}|x[0-9a-fA-F]{1,4})',
                                decode, m.group(1)))
        elif piece in consts:
            value = consts[piece]
            if value is None:
                raise PlanError(f"ambiguous string constant {piece}: use a literal in the attribute")
            parts.append(value)
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
    code = _masked(source)
    for m in re.finditer(r"\[\s*(?:(?:global::)?Kiln\.Testing\.)?ClientCheck(?:Attribute)?\s*\(", code):
        conditional = 0
        for directive in re.finditer(r"^[ \t]*#(if|endif)\b", code[:m.start()], re.M):
            conditional += 1 if directive.group(1) == "if" else -1
        if conditional:
            raise PlanError("a [ClientCheck] is conditionally compiled: the plan cannot decide its build symbols")
        i, depth, n = m.end(), 1, len(source)
        while i < n and depth:
            c = code[i]
            if c == "(":
                depth += 1
            elif c == ")":
                depth -= 1
            i += 1
        if depth == 0:
            found.append((source.count("\n", 0, m.start()) + 1, source[m.end():i - 1]))
        else:
            raise PlanError(f"line {source.count(chr(10), 0, m.start()) + 1}: unclosed [ClientCheck]")
    return found


def read_pack(folder: str) -> list[dict]:
    """Every [ClientCheck] of the pack's sources."""
    files = sorted(glob.glob(os.path.join(folder, "*.cs")))
    if not files:
        raise PlanError(f"no C# sources in {folder}")
    consts: dict[str, str | None] = {}
    sources = {}
    for f in files:
        try:
            with open(f, encoding="utf-8") as fh:
                sources[f] = _masked(fh.read(), comments_only=True)
        except OSError as e:
            raise PlanError(f"cannot read {f}: {e}") from e
        code = _masked(sources[f])
        for m in re.finditer(r'const\s+string\s+(\w+)\s*=\s*("(?:[^"\\]|\\.)*")\s*;', sources[f]):
            if code[m.start():m.start() + 5] == "const":
                name, value = m.group(1), _string(m.group(2), {})
                consts[name] = None if name in consts and consts[name] != value else value
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
            if title is None:
                raise PlanError(f"{where}: the check's title is not a string literal or a known constant")
            named = {}
            for a in args[2:]:
                k, sep, v = a.partition("=")
                if not sep or k.strip() not in {"Area", "Since", "Tags", "Needs", "Order", "Confirm"}:
                    raise PlanError(f"{where}: unsupported [ClientCheck] argument {a}")
                named[k.strip()] = v.strip()
            tags = []
            raw = named.get("Tags", "").strip()
            if raw:
                array = re.fullmatch(r'\[(.*)\]|new\s*(?:string\s*)?\[\s*\]\s*\{(.*)\}', raw, re.S)
                if not array:
                    raise PlanError(f"{where}: unsupported Tags expression {raw}")
                inner = array.group(1) if array.group(1) is not None else array.group(2)
                for t in _split_args(inner):
                    lit = _string(t, consts)
                    known = re.fullmatch(r'(?:(?:global::)?(?:Kiln\.Testing\.)?ClientCheckTags\.)?(\w+)', t)
                    tag = lit if lit is not None else TAG_NAMES.get(known.group(1)) if known else None
                    if tag is None:
                        raise PlanError(f"{where}: unsupported tag {t}")
                    tags.append(tag)
            need = re.fullmatch(r'(?:(?:global::)?Kiln\.Testing\.)?ClientCheckNeeds\.(Any|Hosted|Joined)',
                                named.get("Needs", "ClientCheckNeeds.Any"))
            if not need:
                raise PlanError(f"{where}: unsupported Needs expression {named['Needs']}")
            needs = need.group(1).lower()
            since = _string(named.get("Since", '""'), consts)
            area = _string(named.get("Area", '""'), consts)
            if since is None or area is None:
                raise PlanError(f"{where}: Since and Area must be string literals or known constants")
            try:
                order = int(named.get("Order", "0"))
            except ValueError:
                raise PlanError(f"{where}: unsupported Order expression {named['Order']}") from None
            checks.append({"id": cid, "title": title, "area": area,
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


def build(pack: list[dict], index: list[dict], gates: list[dict], since: str) -> dict:
    """The plan as data."""
    problems: list[str] = []
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
    missing, deferred, settled_unchecked = [], [], []
    for r in index:
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
        unknown = [c for c in g["checks"] if c not in by_id]
        for c in unknown:
            problems.append(f"gate {g['name']} ({g['file']}) names the check {c}, which the pack does not have")
        gate_rows.append({"name": g["name"], "side": g["side"], "checks": g["checks"], "file": g["file"],
                          "sessions": sorted({session_of(by_id[c]) for c in g["checks"] if c in by_id}),
                          "unknown": unknown})
    return {"since": since, "checks": len(mine), "index": len(index),
            "sessions": [{"key": k, "what": what, "checks": sessions[k], "minutes": len(sessions[k]) * MINUTES}
                         for k, what in SESSIONS],
            "gates": gate_rows, "missing": missing, "deferred": deferred, "settled_without_check": settled_unchecked,
            "problems": problems}


def render(plan: dict) -> str:
    out = [f"Guided pass for {plan['since']}: {plan['checks']} check(s) in the pack, {plan['index']} id(s) in the index"]
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
    out.append(f"Held by the client gate: {len(plan['gates'])} capability(ies)")
    for g in plan["gates"]:
        side = "" if g["side"] == "both" else f" ({g['side']} only)"
        where = f" session {'/'.join(g['sessions'])}" if g["sessions"] else ""
        out.append(f"  {g['name']}{side}: {', '.join(g['checks']) or '(no check)'}{where}")
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


def main(argv: list[str] | None = None) -> int:
    p = argparse.ArgumentParser(description="The plan of a guided client pass, read from the sources.")
    p.add_argument("--repo", default=os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".."))
    p.add_argument("--since", default="0.2.0-dev.28", help="the release whose checks are planned")
    p.add_argument("--strict", action="store_true", help="exit 1 when there is a problem")
    p.add_argument("--json", action="store_true")
    args = p.parse_args(argv)
    try:
        pack = read_pack(os.path.join(args.repo, "mods", "tests", "client-pass"))
        index = read_index(os.path.join(args.repo, "mods", "tests", "client-pass", "dev28-checks.json"))
        gates = read_gates(os.path.join(args.repo, "native", "core", "gates"))
    except PlanError as e:
        print(f"plan: {e}", file=sys.stderr)
        return 2
    plan = build(pack, index, gates, args.since)
    sys.stdout.write(json.dumps(plan, indent=1) + "\n" if args.json else render(plan))
    return 1 if args.strict and plan["problems"] else 0


if __name__ == "__main__":
    sys.exit(main())
