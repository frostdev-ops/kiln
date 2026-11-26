#!/usr/bin/env bash
# SPDX-License-Identifier: MIT
# Sourced by the registry back-end suites: regmap_fixture <out dir> copies the repository's regmap/ into <out dir>/regmap-all-ok
# with every row of every per-build record set to "ok" (and its unproven reasons dropped, bound to the copy's regmap/common),
# every row regmap/common has listed too (a registry added since the record was proven), and prints that folder.
# The back-end suites test what a mapped row does on the build's real layout; which rows the lab has proven on a build
# (the committed record's unproven rows, kilnctl regproof) is the regmap suite's and kilnctl's to test, not theirs.
regmap_fixture() {
    local repo dst
    repo=$(cd "$(dirname "${BASH_SOURCE[0]}")/../../../.." && pwd)
    dst=$1/regmap-all-ok
    rm -rf "$dst" && cp -r "$repo/regmap" "$dst"
    python3 -I - "$dst" <<'EOF'
import hashlib, json, pathlib, sys
root = pathlib.Path(sys.argv[1])
# the copy's own mapping digest (kilnctl regproof's regmapDigest), so its records stay bound to the common they sit beside
text = "".join(f"{f.name}\n{hashlib.sha256(f.read_bytes()).hexdigest()}\n" for f in sorted((root / "common").glob("*.json")))
digest = hashlib.sha256(text.encode()).hexdigest()
common = sorted(f"{d['registry']}.{row}" for f in (root / "common").glob("*.json") for d in [json.loads(f.read_text(encoding="utf-8"))] for row in d.get("rows", {}))
for side in ("server", "client"):
    for f in sorted((root / side).glob("*.json")):
        doc = json.loads(f.read_text(encoding="utf-8"))
        doc["rows"] = {row: "ok" for row in sorted(set(doc.get("rows", {})) | set(common))}
        doc.pop("reasons", None)
        doc["regmapDigest"] = digest
        f.write_text(json.dumps(doc, indent=2) + "\n", encoding="utf-8")
EOF
    echo "$dst"
}
