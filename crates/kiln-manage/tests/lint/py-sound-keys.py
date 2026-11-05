# SPDX-License-Identifier: MIT
"""What tools/soundcook's recipe reader accepts, as JSON, for tests/lint.rs (sound_schema_matches_the_recipe_reader).
    python3 crates/kiln-manage/tests/lint/py-sound-keys.py <repo>
"""
import json
import sys
from pathlib import Path

repo = Path(sys.argv[1])
for sub in ("tools/kfc", "tools/soundcook"):
    sys.path.insert(0, str(repo / sub))

from sndcook import recipe, res  # noqa: E402

print(json.dumps({
    "keys": {op: sorted(k) for op, k in recipe.KEYS.items()},
    "cueKeys": sorted(recipe.CUE_KEYS),
    "scene": list(res.SCENES),
    "playScene": list(res.PLAY_SCENES),
    "stealingMode": list(res.STEALING),
}))
