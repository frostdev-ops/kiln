#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""Chapter 08's forced-drop proof: compares two starts' folders written by run.sh (compare.py <a> <b>): capabilities rows, every
kiln/state/*.json, the files of kiln/data (by hash), the observer's lines and the test mods' done and FAIL lines. Times, pids and
durations are left out. Prints one line per section, "equal" or what differs."""
import difflib
import json
import re
import sys
from pathlib import Path

VOLATILE = re.compile(r"^(written|pid|started|startedAt|atMs|at|time|timestamp|elapsed\w*|\w*Ms|ms|seconds|duration\w*|updated\w*|generated\w*|took\w*|serverSteps|savedAt|start|parts|FirstSeen|LastSeen|createdUnixMs)$")  # parts: progress.json, the labels of a step still running when it was written
# the test mods' own run records (a pid and start time) and the world's per-start save record files (named by time; compared by content below)
RUN_RECORDS = re.compile(r"^\./(content-test/phase\.json|skills-test/first-boot-done|worlds/[^/]+/(blobs|manifests)/.*)$")
NUMBERS = re.compile(r"\b\d+(\.\d+)? ?(ms|s)\b|0x[0-9a-f]{8,}|\"service\":\"[0-9A-F]{16}\"")  # the last: an address in assets' status
# --known (a drop start against one without the mod): what differs by design and is named, not compared. A drop start is a boot profile
# and the absent start is not (mods.boot.phase, mods.boot.resource_patches, boot.json); mods.json lists the dropped mod as refused; the
# dropped mod's OnBoot compiled an item block in the boot phase, and Kiln.Blocks reports its two rows at the first item compile.
KNOWN_ROWS = {"mods.boot.phase", "mods.boot.resource_patches", "blocks.animated_held", "blocks.weapon_moves"}
KNOWN_FILES = {"boot.json", "mods.json"}
STAMP = re.compile(r"^\[\w [\d:.]+\] ")


def strip(v):
    if isinstance(v, dict):
        return {k: strip(x) for k, x in v.items() if not VOLATILE.match(k)}
    if isinstance(v, list):
        return [strip(x) for x in v]
    return v


def flat(v, path=""):
    if isinstance(v, dict):
        for k, x in v.items():
            yield from flat(x, f"{path}.{k}" if path else k)
    elif isinstance(v, list):
        for i, x in enumerate(v):
            yield from flat(x, f"{path}[{i}]")
    else:
        yield path, v


def json_diff(a, b, limit=12):
    fa, fb = dict(flat(strip(a))), dict(flat(strip(b)))
    out = []
    for k in sorted(set(fa) | set(fb)):
        if fa.get(k, "<none>") != fb.get(k, "<none>"):
            out.append(f"{k}: {json.dumps(fa.get(k, '<none>'))[:160]} | {json.dumps(fb.get(k, '<none>'))[:160]}")
    return out[:limit] + ([f"... {len(out) - limit} more"] if len(out) > limit else []), len(out)


def caps(d):
    p = d / "state" / "capabilities.json"
    j = json.loads(p.read_text()) if p.exists() else {}
    return {r["name"]: strip(r) for r in j.get("capabilities", [])}


def log_lines(d, pred):
    lines = (d / "kiln.log").read_text(errors="replace").splitlines()
    return [NUMBERS.sub("#", STAMP.sub("", x)) for x in lines if pred(x)]


def tests(d):
    res = {}
    for line in (d / "kiln.log").read_text(errors="replace").splitlines():
        m = re.match(r"\[\w [\d:.]+\] \[mod:([^\]]+)\] (.*)", line)
        if not m or m.group(1) in ("boot-drop-observer", "boot-drop-test"):
            continue
        mod, text = m.groups()
        r = res.setdefault(mod, {"done": [], "fails": []})
        if re.search(r"done:? \d+ PASS", text) or text.startswith("wt: mode"):
            r["done"].append(NUMBERS.sub("#", text))
        if re.search(r"\bFAIL\b", text) and "PASS," not in text and not re.search(r"done:? \d+ PASS", text):
            r["fails"].append(NUMBERS.sub("#", text))
    return res


def world_records(a, b):
    """The world's save record of each start: the newest manifest's bundles (scope -> blob), compared by content per scope."""
    def newest(d):
        ms = sorted((d / "data-worlds").glob("*/manifests/*.json"))
        if not ms:
            return None
        out = {}
        for scope, ref in json.loads(ms[-1].read_text()).get("bundles", {}).items():
            blob = next((d / "data-worlds").glob(f"*/blobs/{ref['sha256'][:2]}/{ref['sha256']}.json"), None)
            out[scope] = strip(json.loads(blob.read_text())) if blob else "<blob missing>"
        return out
    ra, rb = newest(a), newest(b)
    if ra is None or rb is None:
        print("world record: not copied in " + ("a" if ra is None else "b"))
        return
    lines, count = json_diff(ra, rb)
    print(f"world record (newest manifest, {len(ra)} | {len(rb)} bundles, by content): " + ("equal" if not count else f"{count} value(s) differ"))
    for x in lines:
        print("  " + x)


def main():
    known = "--known" in sys.argv
    a, b = (Path(x) for x in sys.argv[1:] if x != "--known")
    print(f"== {a.name} | {b.name}")
    ca, cb = caps(a), caps(b)
    diff = [n for n in sorted(set(ca) | set(cb)) if ca.get(n) != cb.get(n)]
    if known:
        print("known (named, not compared): rows " + ", ".join(n for n in diff if n in KNOWN_ROWS) + "; state/" + ", state/".join(sorted(KNOWN_FILES)))
        diff = [n for n in diff if n not in KNOWN_ROWS]
    print(f"capabilities: {len(ca)} | {len(cb)} rows, " + ("equal" if not diff else f"{len(diff)} differ"))
    for n in diff:
        print(f"  {n}: {json.dumps(ca.get(n))[:220]}\n  {' ' * len(n)}  {json.dumps(cb.get(n))[:220]}")
    names = sorted({p.name for p in (a / "state").glob("*.json")} | {p.name for p in (b / "state").glob("*.json")})
    for n in names:
        if n == "capabilities.json" or (known and n in KNOWN_FILES):
            continue
        pa, pb = a / "state" / n, b / "state" / n
        if not pa.exists() or not pb.exists():
            print(f"state/{n}: only in {'a' if pa.exists() else 'b'}")
            continue
        try:
            ja, jb = json.loads(pa.read_text()), json.loads(pb.read_text())
        except ValueError:
            print(f"state/{n}: " + ("equal (bytes)" if pa.read_bytes() == pb.read_bytes() else "differs (not JSON)"))
            continue
        lines, count = json_diff(ja, jb)
        print(f"state/{n}: " + ("equal" if not count else f"{count} value(s) differ"))
        for x in lines:
            print("  " + x)
    ha = dict(reversed(x.split(None, 1)) for x in (a / "data.sha256").read_text().splitlines() if x.strip())
    hb = dict(reversed(x.split(None, 1)) for x in (b / "data.sha256").read_text().splitlines() if x.strip())
    dd = [f for f in sorted(set(ha) | set(hb)) if ha.get(f) != hb.get(f) and not RUN_RECORDS.match(f)]
    print(f"kiln/data: {len(ha)} | {len(hb)} files, " + ("equal" if not dd else f"{len(dd)} differ") + " (run records left out)")
    for f in dd[:20]:
        print(f"  {f}: {'missing' if f not in ha else ha[f][:12]} | {'missing' if f not in hb else hb[f][:12]}")
    world_records(a, b)
    oa = log_lines(a, lambda x: "boot-drop-observer:" in x)
    ob = log_lines(b, lambda x: "boot-drop-observer:" in x)
    od = [x for x in difflib.unified_diff(oa, ob, lineterm="", n=0) if not x.startswith(("---", "+++", "@@"))]
    print(f"observer: {len(oa)} | {len(ob)} lines, " + ("equal" if not od else f"{len(od)} line(s) differ"))
    for x in od[:30]:
        print("  " + x[:240])
    ta, tb = tests(a), tests(b)
    for m in sorted(set(ta) | set(tb)):
        ra, rb = ta.get(m, {}), tb.get(m, {})
        if not (ra.get("done") or rb.get("done") or ra.get("fails") or rb.get("fails")):
            continue
        same = ra == rb
        print(f"test {m}: {'equal' if same else 'DIFFERENT'}: {ra.get('done')} | {rb.get('done')}")
        if not same:
            for x in sorted(set(ra.get("fails", [])) ^ set(rb.get("fails", []))):
                print(f"  {'a' if x in ra.get('fails', []) else 'b'} only: {x[:220]}")


if __name__ == "__main__":
    main()
