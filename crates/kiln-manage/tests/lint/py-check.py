# SPDX-License-Identifier: MIT
"""Cross-check of kiln lint against the real Python readers of cook recipes and natives files: prints one JSON line
per file, {"file", "ok", "error"}. Used by tests/lint.rs (python_readers_agree).
    python3 crates/kiln-manage/tests/lint/py-check.py <repo> <file>...
"""
import json
import sys
from pathlib import Path

repo = Path(sys.argv[1])
for sub in ("tools/kfc", "tools/modelcook", "tools/impact", "tools/soundcook", "tools/animcook"):
    sys.path.insert(0, str(repo / sub))

from cook import recipe as modelcook  # noqa: E402
from impact import cook as impactcook  # noqa: E402
from impact import catalog as impactcat  # noqa: E402
from sndcook import recipe as soundcook  # noqa: E402
from anicook import recipe as animcook  # noqa: E402

for f in sys.argv[2:]:
    p = Path(f)
    err = None
    try:
        if p.name.endswith("natives.json"):
            c = impactcat.Catalog()
            c.add_natives_file(p, "mod")
            if c.problems:
                err = "; ".join(c.problems)
        else:
            text = p.read_text()
            doc = json.loads(text)
            if isinstance(doc, dict) and doc.get("tool") == "impact":
                impactcook.load(p)
            else:
                modelcook.load(p)
                soundcook.load(p)
                animcook.load(p)
    except Exception as e:  # noqa: BLE001 - any refusal counts
        err = f"{type(e).__name__}: {e}"
    print(json.dumps({"file": str(p), "ok": err is None, "error": err}))
