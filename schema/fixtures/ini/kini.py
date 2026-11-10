#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""Reference parser for Kiln's proxy INI files (docs/kiln/launcher/contracts.md, "INI files"):
<game>/kiln/play-once.ini, <game>/kiln/chain.ini and <game>/kiln/chain/once.ini. The C proxy and
kiln_manage::{playonce,chain} must give the same results on schema/fixtures/ini/** (error texts are informative;
status, paths, details and entries must match).

  python3 kini.py check            # every fixture: <case>.ini against <case>.json "expect"
  python3 kini.py write            # rewrite every <case>.json "expect" from this parser (review the diff!)
  python3 kini.py play-once FILE [NOW] | chain FILE | once FILE
"""
import datetime
import json
import pathlib
import re
import sys

MAX_ENTRIES = 256            # per section; the rest are REFUSED (detail too-many)
MAX_AGE_S = 600              # play-once older than this: IGNORED_EXPIRED
MAX_FUTURE_S = 60            # play-once "created" more than this in the future: IGNORED_EXPIRED
MODES = ("vanilla", "no-mods", "profile")
DEFAULT_ONCE_PROFILE = "profile.once.json"   # relative to <game>/kiln/


class Invalid(Exception):
    pass


def decode(data: bytes) -> str:
    """UTF-8 (BOM optional), UTF-16LE with BOM, or ANSI (the system code page; this reference uses cp1252)."""
    if data.startswith(b"\xef\xbb\xbf"):
        return data[3:].decode("utf-8", errors="replace")
    if data.startswith(b"\xff\xfe"):
        return data[2:].decode("utf-16-le", errors="replace")
    try:
        return data.decode("utf-8")
    except UnicodeDecodeError:
        return data.decode("cp1252", errors="replace")


def lines(data: bytes):
    """Yields (line number, section or None, kind, text). kind: 'section' | 'line'. Lines are trimmed; blank lines and
    ';' '#' '//' comments are skipped (so '; ' in front of a line switches it off)."""
    section = None
    for n, raw in enumerate(decode(data).split("\n"), 1):
        line = raw.rstrip("\r").strip(" \t")
        if not line or line[0] in ";#" or line.startswith("//"):
            continue
        if line.startswith("[") and line.endswith("]"):
            section = line[1:-1].strip(" \t").lower()
            yield n, section, "section", section
            continue
        yield n, section, "line", line


def game_path(p: str):
    """'/' is read as '\\'. Absolute: drive letter + '\\' (Z:\\..., C:\\...) or UNC (\\\\server\\...); anything else is
    relative to <game>/kiln/."""
    p = p.replace("/", "\\")
    absolute = bool(re.match(r"^[A-Za-z]:\\", p)) or p.startswith("\\\\")
    return p, not absolute


def dll_entries(data: bytes, wanted: dict, default_section: str, seen: set = None) -> dict:
    """One DLL per line; a 'key = value' line counts by its value; only values ending in .dll count. `seen` (optional)
    collects the wanted sections whose header is in the file."""
    out = {name: [] for name in wanted}
    for n, section, kind, text in lines(data):
        if kind == "section":
            if seen is not None and text in out:
                seen.add(text)
            continue
        name = section if section is not None else default_section
        if name not in out:
            continue  # other sections belong to other tools
        value = text.split("=", 1)[1].strip(" \t") if "=" in text else text
        if not value.lower().endswith(".dll"):
            continue
        path, rel = game_path(value)
        entry = {"path": path, "relative": rel, "status": "ok"}
        if len(out[name]) >= MAX_ENTRIES:
            entry.update(status="refused", detail="too-many")
        out[name].append(entry)
    return out


def chain(data: bytes) -> dict:
    """kiln/chain.ini: [chain] (proxies the installer displaced) and [profile] (dllmain native mods of the active
    profile). Section names are case-insensitive; lines before any section count as [chain]. [late] (dev.21) names
    entries of the other sections that load after Kiln's plugins; reported only when it has lines."""
    out = dll_entries(data, {"chain": 1, "profile": 1, "late": 1}, "chain")
    if not out["late"]:
        del out["late"]
    return out


def once(data: bytes) -> dict:
    """kiln/chain/once.ini: read instead of chain.ini [profile] when play-once mode=profile. Its [profile] section
    (lines before any section count as [profile]). dev.21: it may also have a [chain] and a [late] (the play-once
    profile's compat rules applied): a section it has replaces chain.ini's for that start, even an empty one, so they
    are reported only when the file has the section's header; a file without them leaves chain.ini's in effect. Other
    sections are ignored."""
    seen = set()
    out = dll_entries(data, {"profile": 1, "chain": 1, "late": 1}, "profile", seen)
    for name in ("chain", "late"):
        if name not in seen:
            del out[name]
    return out


def parse_created(text: str) -> int:
    if re.fullmatch(r"[0-9]{1,12}", text):
        return int(text)
    if not re.fullmatch(r"\d{4}-\d{2}-\d{2}[Tt ]\d{2}:\d{2}:\d{2}(\.\d+)?([Zz]|[+-]\d{2}:\d{2})", text):
        raise Invalid("created must be unix seconds or RFC 3339 with a zone")
    try:
        return int(datetime.datetime.fromisoformat(text.replace("z", "Z")).timestamp())
    except ValueError:
        raise Invalid("created is not a valid time")


def play_once(data: bytes, now: int, server: bool = False, deletable: bool = True) -> dict:
    """What the proxy decides. The proxy deletes the file right after reading it, before validating it.
    status: KILN_PLAY_ONCE_* in lower case, '_' -> '-' (vanilla, no-mods, profile, ignored-*)."""
    if not deletable:
        return {"status": "ignored-undeletable"}
    if server:
        return {"status": "ignored-server"}
    keys = {}
    for n, section, kind, text in lines(data):
        if kind == "section" or "=" not in text:
            continue  # sections are ignored; so are lines that are not key = value
        key, value = (x.strip(" \t") for x in text.split("=", 1))
        keys[key.lower()] = value  # a repeated key: the last one counts
    try:
        mode = keys.get("mode", "").lower()
        if mode not in MODES:
            raise Invalid(f"mode must be one of {', '.join(MODES)}")
        created = parse_created(keys.get("created", ""))
        nonce = keys.get("nonce", "")
        if not nonce:
            raise Invalid("nonce is required")
    except Invalid as e:
        return {"status": "ignored-invalid", "error": str(e)}
    if now - created > MAX_AGE_S or created - now > MAX_FUTURE_S:
        return {"status": "ignored-expired", "nonce": nonce}
    out = {"status": mode, "created": created, "nonce": nonce}
    if mode == "profile":
        path, rel = game_path(keys.get("profile") or DEFAULT_ONCE_PROFILE)
        out["profile"] = {"path": path, "relative": rel}
    return out


def run_case(ini: pathlib.Path, spec: dict) -> dict:
    data = ini.read_bytes()
    if ini.parent.name == "play-once":
        return play_once(data, spec["now"], spec.get("server", False), spec.get("deletable", True))
    return once(data) if spec.get("file") == "once" else chain(data)


def main(argv):
    here = pathlib.Path(__file__).resolve().parent
    if len(argv) >= 2 and argv[1] in ("check", "write"):
        bad = 0
        for spec_file in sorted(here.glob("*/*.json")):
            spec = json.loads(spec_file.read_text())
            got = run_case(spec_file.with_suffix(".ini"), spec)
            if argv[1] == "write":
                spec["expect"] = got
                spec_file.write_text(json.dumps(spec, indent=2, ensure_ascii=False) + "\n")
            elif got != spec.get("expect"):
                bad += 1
                print(f"FAIL {spec_file.relative_to(here)}\n  expect {spec.get('expect')}\n  got    {got}")
        if argv[1] == "check":
            print("ok" if not bad else f"{bad} failed")
        return 1 if bad else 0
    if len(argv) >= 3 and argv[1] == "play-once":
        now = int(argv[3]) if len(argv) > 3 else int(__import__("time").time())
        print(json.dumps(play_once(pathlib.Path(argv[2]).read_bytes(), now), indent=2))
        return 0
    if len(argv) >= 3 and argv[1] in ("chain", "once"):
        f = chain if argv[1] == "chain" else once
        print(json.dumps(f(pathlib.Path(argv[2]).read_bytes()), indent=2))
        return 0
    print(__doc__)
    return 2


if __name__ == "__main__":
    sys.exit(main(sys.argv))
