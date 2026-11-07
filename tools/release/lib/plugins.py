#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""The plugins a Kiln runtime must hold, and a check that a runtime holds them all (no silent omissions).

Kiln is the universal loader: every compatibility plugin ships in every release, installed by default on both sides.
The expected list comes from the source tree, so a new plugin is expected as soon as it exists:

  - kiln/plugins/kiln-<p>.dll for each native/plugins/<p>/CMakeLists.txt (component core; kiln-emf: emf), unless
    that CMakeLists.txt says "Builds no plugin" (native/plugins/compat: the plugins' shared code and host tests);
  - kiln/plugins/<name>.dll for each GPL plugin gpl/<name>/Cargo.toml (component eml for kiln-eml);
  - ShroudForge's runtime kiln/plugins/shroudforge/{shroudforge-runtime,kfc-runtime}.dll (component shroudforge),
    when third_party/shroudforge/kiln/build.sh is in the tree;
  - Enshwick's loader kiln/plugins/enshwick/enshwick.dll and its LICENSE.txt (component enshwick), which
    kiln-enshwick runs when the game has no copy of its own (needs native/plugins/enshwick).

Each must be in the runtime's manifest (kiln-runtime.json files[], or a staged component folder), non-empty, in the
expected component, and that component must be default and for both sides.

  plugins.py check --src <tree> <runtime.zip | stage dir>
  plugins.py list --src <tree>
"""
import argparse
import glob
import json
import os
import sys
import zipfile

MANIFEST = "kiln-runtime.json"
SIDES = ("server", "client")
# the opt-out a native/plugins/<p>/CMakeLists.txt carries when the folder is no plugin (compat: shared code + tests)
NO_PLUGIN = "Builds no plugin"


def expected(src):
    """[(path, component)] the runtime of the tree at src must hold."""
    out = []
    for cm in sorted(glob.glob(os.path.join(src, "native", "plugins", "*", "CMakeLists.txt"))):
        p = os.path.basename(os.path.dirname(cm))
        if NO_PLUGIN in open(cm, encoding="utf-8").read():
            continue  # shared code and host tests (native/plugins/compat)
        out.append((f"kiln/plugins/kiln-{p}.dll", "emf" if p == "emf" else "core"))
    for ws in sorted(glob.glob(os.path.join(src, "gpl", "*", "Cargo.toml"))):
        name = os.path.basename(os.path.dirname(ws))
        out.append((f"kiln/plugins/{name}.dll", "eml" if name == "kiln-eml" else name))
    if os.path.isfile(os.path.join(src, "third_party", "shroudforge", "kiln", "build.sh")):
        out += [("kiln/plugins/shroudforge/shroudforge-runtime.dll", "shroudforge"),
                ("kiln/plugins/shroudforge/kfc-runtime.dll", "shroudforge")]
    if os.path.isdir(os.path.join(src, "native", "plugins", "enshwick")):
        out += [("kiln/plugins/enshwick/enshwick.dll", "enshwick"), ("kiln/plugins/enshwick/LICENSE.txt", "enshwick")]
    return out


def from_zip(path):
    """(components, {path: (component, size)}) of a runtime zip's manifest."""
    with zipfile.ZipFile(path) as z:
        m = json.loads(z.read(MANIFEST))
    return m.get("components") or {}, {f["path"]: (f.get("component"), f.get("size", 0)) for f in m.get("files") or []}


def from_stage(stage):
    """(components, {path: (component, size)}) of a stage folder (tools/package/stage.sh)."""
    with open(os.path.join(stage, MANIFEST), encoding="utf-8") as f:
        m = json.load(f)
    files = {}
    for comp in sorted(os.listdir(stage)):
        root = os.path.join(stage, comp)
        if not os.path.isdir(root):
            continue
        for d, _, names in os.walk(root):
            for n in names:
                full = os.path.join(d, n)
                files[os.path.relpath(full, root).replace(os.sep, "/")] = (comp, os.path.getsize(full))
    return m.get("components") or {}, files


def problems(exp, components, files):
    out = []
    for path, comp in exp:
        got = files.get(path)
        if got is None:
            out.append(f"{path}: missing (expected in component {comp})")
            continue
        if got[0] != comp:
            out.append(f"{path}: in component {got[0]}, expected {comp}")
        if not got[1]:
            out.append(f"{path}: empty")
        c = components.get(got[0])
        if c is None:
            out.append(f"{path}: component {got[0]} is not in the manifest")
            continue
        if not c.get("default", True):
            out.append(f"{path}: component {got[0]} is not installed by default")
        missing_sides = [s for s in SIDES if s not in (c.get("sides") or [])]
        if missing_sides:
            out.append(f"{path}: component {got[0]} is not for {', '.join(missing_sides)}")
    return out


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = ap.add_subparsers(dest="cmd", required=True)
    c = sub.add_parser("check")
    c.add_argument("--src", required=True)
    c.add_argument("runtime", help="a runtime zip or a stage folder")
    ls = sub.add_parser("list")
    ls.add_argument("--src", required=True)
    a = ap.parse_args(argv)
    exp = expected(a.src)
    if a.cmd == "list":
        for path, comp in exp:
            print(f"{comp:<12} {path}")
        return 0
    if not exp:
        print(f"plugins: no plugins expected from {a.src} (not a Kiln tree?)", file=sys.stderr)
        return 1
    components, files = from_stage(a.runtime) if os.path.isdir(a.runtime) else from_zip(a.runtime)
    bad = problems(exp, components, files)
    for p in bad:
        print(f"plugins: {p}", file=sys.stderr)
    if bad:
        print(f"plugins: FAIL: {len(bad)} problem(s) in {a.runtime}", file=sys.stderr)
        return 1
    for path, comp in exp:
        print(f"  {comp:<12} {path:<52} {files[path][1]:>10}")
    print(f"plugins: OK: all {len(exp)} expected plugin files in {a.runtime}, in default components for both sides")
    return 0


if __name__ == "__main__":
    sys.exit(main())
