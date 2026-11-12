# SPDX-License-Identifier: MIT
"""kiln lint's cross-check with the player-states reader (tools/animcook/anicook/playerstates.py, whose rules
native/services/playeranim/states.cpp ports): one JSON line per file, {"file", "ok", "error"}. ok: the reader takes the
file and refuses none of its states for its shape (the merge's own checks, `like` and the clip, need the game's graph).
    python3 py-player-states.py <repo> <file>..."""
import json
import sys
from pathlib import Path

repo = Path(sys.argv[1])
sys.path[:0] = [str(repo / "tools/animcook"), str(repo / "tools/kfc")]
from anicook import playerstates as PS  # noqa: E402

for f in sys.argv[2:]:
    raw = Path(f).read_bytes()
    try:
        text = raw.decode("utf-8")
        if text.startswith("\ufeff"):
            raise PS.DefinitionError("starts with a byte order mark")
        refused = [s.reason for s in PS.parse(text, "fixture", "both", "mod").states if s.status == "refused"]
        print(json.dumps({"file": f, "ok": not refused, "error": refused[0] if refused else None}))
    except (PS.DefinitionError, UnicodeDecodeError) as e:
        print(json.dumps({"file": f, "ok": False, "error": str(e)}))
