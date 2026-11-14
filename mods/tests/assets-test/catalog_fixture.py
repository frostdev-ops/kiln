#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""The independent side of assets-test's full-catalog check (PASS assets.full-catalog, chapter 13 §13b.6): reads the
game's content index with Kiln's own KFC reader (tools/kfc, not kiln.assets) and writes what the in-game check compares
against: how many blobs the index lists, one game blob's hash and the SHA-256 of its bytes. Nothing of the game is kept
but those three facts, in the lab's data folder (never in the repository).

    python3 mods/tests/assets-test/catalog_fixture.py <game folder or .kfc> <out .json>
    e.g. <lab>/kiln/data/assets-test/catalog-fixture.json
"""
import hashlib
import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[3] / "tools" / "kfc"))
from kfc.archive import Kfc  # noqa: E402


def main(argv):
    if len(argv) != 3:
        print(__doc__, file=sys.stderr)
        return 2
    kfc = Kfc(argv[1])
    hashes = sorted(kfc.contents)  # ContentHash tuples: (size, h0, h1, h2), the catalog's order
    pick = next((h for h in hashes if 4096 <= h[0] <= 65536), hashes[0])
    data = kfc.read_content(pick)
    out = {
        "total": len(hashes),
        "hash": f"{pick[0]}-{pick[1]:08x}-{pick[2]:08x}-{pick[3]:08x}",  # decimal size, hex words: ContentHash.ToString()
        "sha256": hashlib.sha256(data).hexdigest(),
        "first": f"{hashes[0][0]}-{hashes[0][1]:08x}-{hashes[0][2]:08x}-{hashes[0][3]:08x}",
        "index": Path(kfc.path).name,
    }
    Path(argv[2]).parent.mkdir(parents=True, exist_ok=True)
    Path(argv[2]).write_text(json.dumps(out, indent=1) + "\n")
    print(f"catalog fixture: {out['total']} blobs in {out['index']}; {out['hash']} sha256 {out['sha256'][:16]}... -> {argv[2]}")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
