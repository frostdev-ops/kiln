#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""kiln-bootsnap: the game-data snapshot Mod.OnBoot reads (contract c-08, docs/kiln/modding/boot-gamedata.md).

    bootsnap.py make --game <dir> [--force] [--exe <file>] [--root <dir>] [--json]
    bootsnap.py show --game <dir> [--root <dir>] <query>...      item:<name> template:<name> skill:0x<hex> material:<name>
    bootsnap.py key  --game <dir> [--exe <file>]

`make` reads the game's own archive (snapshot.archive_files: the backup another loader kept aside, else the live pair) and the game
executable's reflection, both as files (the executable is never run), and writes
<game>/kiln/data/boot-gamedata/<key>/ unless a snapshot of that key is there already. Reuse costs one SHA-256 of the
executable and one 64 KiB read. --json prints one JSON line with the result (kiln-manage reads it); progress goes to
stderr. Exit 0 made or reused, 1 refused (the message says why), 2 bad arguments.
"""
import argparse
import json
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
sys.path.insert(0, str(HERE.parent / "kfc"))

from bootdata import kinds, live, snapshot  # noqa: E402
from kfc import refl  # noqa: E402
from kfc.archive import Kfc  # noqa: E402

QUERY_KINDS = {"item": "items", "template": "templates", "skill": "skills", "material": "materials"}


def reflection(exe):
    """The executable's reflection (refl.extract, about 2 s). A dump tools/kfc cached for this exact executable under
    $KILN_OUT/types/<sha16>.json is read instead when there is one (developers and tests, as the cook tools do); this
    tool never writes one."""
    import hashlib
    import os
    out = os.environ.get("KILN_OUT")
    if out:
        cached = Path(out) / "types" / f"{hashlib.sha256(Path(exe).read_bytes()).hexdigest()[:16]}.json"
        if cached.is_file():
            return json.loads(cached.read_text())
    return refl.extract(str(exe))


def open_source(exe, extract=None):
    """The archive as the game shipped it (the `.bak` pair when another loader patched it), the type graph of `exe` (its
    reflection, read from the file), the config vocabulary of its impact programs. `extract`: tests give a type dump."""
    index, resources, _which = snapshot.archive_files(exe)
    kfc = Kfc(index, stem=exe.with_suffix(""), resources=resources)
    graph = live.Graph((extract or reflection)(str(exe)))
    configs = live.ConfigNames()
    for key in kfc.resources_of_type(kinds.fnv1a32("keen::ImpactProgram")):
        configs.add_program(graph, kfc.read(key))
    configs.finish()
    return kinds.Source(kfc, graph, live.Walker(graph, configs))


def cmd_make(a):
    def progress(kind, done, total):
        print(f"boot-snapshot: {kind} {done}/{total}", file=sys.stderr, flush=True)

    print("boot-snapshot: checking the game data snapshot", file=sys.stderr, flush=True)
    r = snapshot.make(a.game, open_source, exe=a.exe, force=a.force, root=a.root, progress=progress)
    for kind in r.get("emptyKinds", []):  # a snapshot without this kind: every read of it in OnBoot answers "not found"
        print(f"boot-snapshot: warning: the game's {kind} could not be read: the snapshot holds none, and an OnBoot "
              f"read of {kind[:-1]}: finds nothing", file=sys.stderr, flush=True)
    if a.json:
        print(json.dumps(r))
    elif r["status"] == "reused":
        print(f"boot-snapshot: {r['path']} is current ({r['seconds']} s)")
    else:
        c = r["counts"]
        print(f"boot-snapshot: wrote {r['path']} ({r['bytes'] / 1e6:.0f} MB, from the {r['archive']} archive) in {r['seconds']} s: "
              f"{c['items']} items, {c['templates']} templates, {c['skills']} skills, {c['materials']} materials ({r['skipped']} skipped)")
    return 0


def cmd_key(a):
    exe = snapshot.find_exe(a.game, a.exe)
    print(snapshot.snapshot_key(snapshot.file_sha256(exe), snapshot.kfc_version(exe.with_suffix(".kfc"))))
    return 0


def cmd_show(a):
    exe = snapshot.find_exe(a.game, a.exe)
    key = snapshot.snapshot_key(snapshot.file_sha256(exe), snapshot.kfc_version(exe.with_suffix(".kfc")))
    path = (Path(a.root) if a.root else snapshot.root_of(a.game)) / key
    if not snapshot.valid(path, key):
        print(f"boot-snapshot: no snapshot {path}", file=sys.stderr)
        return 1
    rc = 0
    for q in a.query:
        prefix, _, name = q.partition(":")
        kind = QUERY_KINDS.get(prefix)
        if kind is None or not name:
            print(f"boot-snapshot: {q}: not item:/template:/skill:/material:", file=sys.stderr)
            return 2
        if kind == "skills":
            name = name.lower()
        got = snapshot.read_value(path, kind, name)
        if got is None:
            print(json.dumps({"query": q, "found": False}))
            rc = 1
            continue
        e, value = got
        print(json.dumps({"query": q, "resourceId": e[2], "typeHash": e[3], "part": e[4], "value": value}, indent=1))
    return rc


def main(argv=None):
    p = argparse.ArgumentParser(prog="bootsnap", description=__doc__.split("\n\n")[0])
    sub = p.add_subparsers(dest="cmd", required=True)
    for name in ("make", "show", "key"):
        s = sub.add_parser(name)
        s.add_argument("--game", required=True, help="the game folder (the dedicated server's or the client's)")
        s.add_argument("--exe", help="the executable, when the folder holds another name")
        if name != "key":
            s.add_argument("--root", help="where the snapshots go (default <game>/kiln/data/boot-gamedata)")
        if name == "make":
            s.add_argument("--force", action="store_true", help="rebuild even when the key matches")
            s.add_argument("--json", action="store_true", help="one JSON line with the result on stdout")
        if name == "show":
            s.add_argument("query", nargs="+")
    a = p.parse_args(argv)
    try:
        return {"make": cmd_make, "show": cmd_show, "key": cmd_key}[a.cmd](a)
    except (snapshot.SnapshotError, OSError, ValueError) as e:
        if getattr(a, "json", False):
            print(json.dumps({"status": "refused", "reason": str(e)}))
        print(f"boot-snapshot: {e}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
