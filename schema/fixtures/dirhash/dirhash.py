#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""kdh1: Kiln's directory hash (reference implementation; docs/kiln/launcher/contracts.md, "kdh1").

    python3 dirhash.py DIR...        print "kdh1:<hex>  DIR" for each
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
"""
import hashlib
import json
import os
import stat
import sys
import unicodedata
from pathlib import Path

EMPTY = "kdh1:" + hashlib.sha256(b"").hexdigest()


class DirHashError(Exception):
    pass


def files(root: Path):
    out = {}

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


def kdh1(root) -> str:
    h = hashlib.sha256()
    for key, path in files(Path(root)):
        data = path.read_bytes()
        h.update(key + b"\0" + str(len(data)).encode() + b"\0" + hashlib.sha256(data).hexdigest().encode() + b"\n")
    return "kdh1:" + h.hexdigest()


def main(argv):
    here = Path(__file__).resolve().parent
    if len(argv) == 2 and argv[1] in ("--check", "--write"):
        expected_file = here / "expected.json"
        got = {p.name: kdh1(p) for p in sorted((here / "cases").iterdir()) if p.is_dir()}
        if argv[1] == "--write":
            expected_file.write_text(json.dumps({"format": 1, "cases": got}, indent=2) + "\n")
            return 0
        want = json.loads(expected_file.read_text())["cases"]
        bad = [k for k in sorted(set(want) | set(got)) if want.get(k) != got.get(k)]
        for k in bad:
            print(f"FAIL {k}: expected {want.get(k)}, got {got.get(k)}")
        print("ok" if not bad else f"{len(bad)} failed")
        return 1 if bad else 0
    rc = 0
    for d in argv[1:]:
        try:
            print(f"{kdh1(d)}  {d}")
        except DirHashError as e:
            print(f"error: {e}", file=sys.stderr)
            rc = 1
    return rc


if __name__ == "__main__":
    sys.exit(main(sys.argv))
