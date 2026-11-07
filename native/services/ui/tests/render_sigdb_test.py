#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""Offline checks of render.camera's fail-soft layout guard; no game process."""
import copy
import json
import re
import struct
import sys
from pathlib import Path

root = Path(__file__).resolve().parents[4]
sys.path.insert(0, str(root / 'tools/sigport'))
from sigdb_check import Resolver, parse

doc = json.loads((root / 'sigdb/symbols/client.d/render.json').read_text())
strategy = doc['symbols']['render/client.CameraWritten']['find'][0]
_, holes, length = parse(strategy['pattern'])
raw = bytearray()
for token in re.findall(r'\{[^}]+\}|\?\?|[0-9A-Fa-f]{2}', strategy['pattern']):
    if token.startswith('{'):
        name, kind = token[1:-1].split(':')
        raw.extend(struct.pack('<B' if kind == 'd8' else '<I', doc['constants'][name]['value']))
    else:
        raw.append(0 if token == '??' else int(token, 16))
assert len(raw) == length
assert raw[strategy['offset']:strategy['offset'] + 3] == bytes.fromhex('4c 8d 85')

class Image:
    text_va = 0
    def __init__(self, data):
        self.img = self.text = data

def enabled(data):
    return Resolver(Image(data), copy.deepcopy(doc)).run()[2]['render.camera']

assert enabled(raw)
assert not enabled(bytearray(length))              # unknown build
assert not enabled(raw + raw)                     # ambiguous signature
for name, kind, offset in holes:
    changed = raw.copy()
    changed[offset] ^= 1
    assert not enabled(changed), name              # each captured offset must agree with the fixed layout
print('render_sigdb: all checks passed')
