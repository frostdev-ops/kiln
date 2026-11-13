#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""kdh1: Kiln's directory hash (reference implementation; docs/kiln/launcher/contracts.md, "kdh1").

    python3 dirhash.py DIR...        print "kdh1:<hex>  DIR" for each
    python3 dirhash.py --mod DIR...  the same for each folder as a mod (its identity, below)
    python3 dirhash.py --check       hash every case in cases/ and compare with expected.json
    python3 dirhash.py --write       rewrite expected.json from cases/ (review the diff!)

Rules:
  1. Walk DIR recursively. Skip every entry (file or folder, at any depth) whose name starts with "." — with
     everything below it. The root itself may be a symlink; nothing below it may be one: a symlink (or a Windows
     reparse point) is an error, as is any entry that is neither a regular file nor a folder.
  2. Each regular file's path relative to DIR: components joined with "/", each component NFC-normalized and UTF-8
     encoded. A name that is not valid Unicode is an error; two paths equal after NFC are an error.
  3. Sort the paths by their UTF-8 bytes.
  4. For each file, in that order, the record  path "\\0" size "\\0" sha256hex(content) "\\n"  (size in decimal ASCII
     without leading zeros, sha256 in lower-case hex).
  5. kdh1 = "kdh1:" + lower-case hex sha256 of all records concatenated. An empty folder hashes the empty string:
     kdh1:e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855.
  hash8 (library folder names <version>+<hash8>) = the first 8 hex characters after "kdh1:".

A mod's identity (what a server advertises for a mod, what a modpack and the library record) is the kdh1 of the files
that are the mod. For a Kiln mod, a folder with a regular file named kiln.mod.json directly in it, the folder
assets/cooked directly in it is not walked: what is in it is made on each machine from that machine's game (the cook)
and is no part of the mod. Names are compared as kdh1 writes them (NFC, no case folding): "Assets/Cooked", a file
named assets/cooked, and a cooked folder anywhere else all count. Every other folder's identity is its plain kdh1.
expected.json holds both: "cases" (kdh1) and "identity".
"""
import hashlib
import json
import os
import stat
import sys
import unicodedata
from pathlib import Path

EMPTY = "kdh1:" + hashlib.sha256(b"").hexdigest()
MOD_MANIFEST = "kiln.mod.json"
COOKED = ["assets", "cooked"]


class DirHashError(Exception):
    pass


def is_kiln_mod(root: Path) -> bool:
    """A regular file named kiln.mod.json directly in the folder."""
    with os.scandir(root) as it:
        for e in it:
            if unicodedata.normalize("NFC", e.name) == MOD_MANIFEST and stat.S_ISREG(os.lstat(e.path).st_mode):
                return True
    return False


def files(root: Path, mod: bool = False):
    """The files kdh1 counts, sorted by key. With `mod`, the files that are the mod: a Kiln mod's assets/cooked is not walked."""
    out = {}
    skip_cooked = mod and root.is_dir() and is_kiln_mod(root)

    def walk(dir_path: Path, rel: list[str]):
        with os.scandir(dir_path) as it:
            for e in it:
                name = e.name
                if name.startswith("."):
                    continue
                try:
                    nfc = unicodedata.normalize("NFC", name)
                    nfc.encode("utf-8")
                except UnicodeError:
                    raise DirHashError(f"{dir_path / name}: name is not valid Unicode")
                st = os.lstat(e.path)
                if stat.S_ISLNK(st.st_mode):
                    raise DirHashError(f"{e.path}: symlinks are not allowed")
                if stat.S_ISDIR(st.st_mode):
                    if skip_cooked and rel + [nfc] == COOKED:
                        continue
                    walk(Path(e.path), rel + [nfc])
                elif stat.S_ISREG(st.st_mode):
                    key = "/".join(rel + [nfc]).encode("utf-8")
                    if key in out:
                        raise DirHashError(f"{e.path}: two names are equal after NFC normalization")
                    out[key] = Path(e.path)
                else:
                    raise DirHashError(f"{e.path}: not a regular file or folder")

    if not root.is_dir():
        raise DirHashError(f"{root}: not a folder")
    walk(root, [])
    return sorted(out.items())


def kdh1(root, mod: bool = False) -> str:
    h = hashlib.sha256()
    for key, path in files(Path(root), mod):
        data = path.read_bytes()
        h.update(key + b"\0" + str(len(data)).encode() + b"\0" + hashlib.sha256(data).hexdigest().encode() + b"\n")
    return "kdh1:" + h.hexdigest()


def mod_identity(root) -> str:
    """A mod's identity: kdh1 without a Kiln mod's cook outputs (assets/cooked)."""
    return kdh1(root, mod=True)


# What the cases must show whatever expected.json says: (case, the identity it must have)
SAME_IDENTITY = [("mod-cooked", lambda got: got["identity"]["mod"]), ("mod", lambda got: got["cases"]["mod"])]
COOKED_COUNTS = ["mod-cooked-case", "mod-cooked-file", "cooked-not-a-mod", "cooked-manifest-folder"]


def relations(got):
    """Failures of the rules the mod cases exist for."""
    bad = []
    for name, want in SAME_IDENTITY:
        if got["identity"].get(name) != want(got):
            bad.append(f"{name}: identity {got['identity'].get(name)}, expected {want(got)}")
    if got["cases"].get("mod-cooked") == got["identity"].get("mod-cooked"):
        bad.append("mod-cooked: its kdh1 must differ from its identity")
    for name in COOKED_COUNTS:
        if got["identity"].get(name) != got["cases"].get(name):
            bad.append(f"{name}: identity must be its kdh1")
    if got["identity"].get("mod-cooked-names") == got["cases"].get("mod-cooked-names"):
        bad.append("mod-cooked-names: assets/cooked/left-out.bin must be left out")
    return bad


def main(argv):
    here = Path(__file__).resolve().parent
    if len(argv) == 2 and argv[1] in ("--check", "--write"):
        expected_file = here / "expected.json"
        dirs = [p for p in sorted((here / "cases").iterdir()) if p.is_dir()]
        got = {"cases": {p.name: kdh1(p) for p in dirs}, "identity": {p.name: mod_identity(p) for p in dirs}}
        broken = relations(got)
        for b in broken:
            print(f"FAIL {b}")
        if argv[1] == "--write":
            if broken:
                return 1
            expected_file.write_text(json.dumps({"format": 1, **got}, indent=2) + "\n")
            return 0
        want = json.loads(expected_file.read_text())
        bad = [(m, k) for m in ("cases", "identity") for k in sorted(set(want.get(m, {})) | set(got[m])) if want.get(m, {}).get(k) != got[m].get(k)]
        for m, k in bad:
            print(f"FAIL {m} {k}: expected {want.get(m, {}).get(k)}, got {got[m].get(k)}")
        print("ok" if not bad and not broken else f"{len(bad) + len(broken)} failed")
        return 1 if bad or broken else 0
    rc = 0
    mod = len(argv) > 1 and argv[1] == "--mod"
    for d in argv[2:] if mod else argv[1:]:
        try:
            print(f"{kdh1(d, mod)}  {d}")
        except DirHashError as e:
            print(f"error: {e}", file=sys.stderr)
            rc = 1
    return rc


if __name__ == "__main__":
    sys.exit(main(sys.argv))
