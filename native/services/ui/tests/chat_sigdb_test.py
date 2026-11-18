#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""Offline checks of chat history's sigdb definitions (sigdb/symbols/client.d/chatui.json); no game process.

1. Every sigdb name chat_input_probe.cpp asks for is in feature chat.ui.input, so the feature gates all of it.
2. With the client exe installed (KILN_CLIENT_EXE, or Steam's default folder; read as a file, never run): the feature
   resolves on it, a layout capture that stops matching turns it off, and on 26430625 (5d9ef2f1) the values are the
   ones docs/re/chat-input.md gives.
"""
import copy
import hashlib
import json
import os
import re
import sys
from pathlib import Path

root = Path(__file__).resolve().parents[4]
sys.path.insert(0, str(root / 'tools/sigport'))
from peimg import Image  # noqa: E402
from sigdb_check import Resolver  # noqa: E402

doc = json.loads((root / 'sigdb/symbols/client.d/chatui.json').read_text())
feature = doc['features']['chat.ui.input']
gated = set(feature['symbols']) | set(feature['constants'])
code = (root / 'native/services/ui/chat_input_probe.cpp').read_text()
used = set(re.findall(r'"(chatui/[A-Za-z0-9_.]+)"', code))
assert used, 'chat_input_probe.cpp names no chatui symbol'
assert used <= gated, f'read but not gated by chat.ui.input: {sorted(used - gated)}'
for name in feature['constants']:
    assert name in doc['constants'], f'{name}: listed in the feature, not declared'

exe = Path(os.environ.get('KILN_CLIENT_EXE') or
           Path.home() / '.local/share/Steam/steamapps/common/Enshrouded/enshrouded.exe')
if not exe.is_file():
    print('chat_sigdb: names gated; no client exe, resolve checks skipped')
    sys.exit(0)
image = Image(exe)


def resolve(d):
    return Resolver(image, d).run()


_, consts, feats = resolve(copy.deepcopy(doc))
assert feats['chat.ui.input'], 'chat.ui.input does not resolve on ' + str(exe)

# A changed layout: the strategy that captures state.editing no longer matches, so the feature is off (never a guess).
changed = copy.deepcopy(doc)
for f in changed['symbols']['@chatui/TextInputEdit.fields']['find']:
    if '{chatui/state.editing:' in f['pattern']:
        f['pattern'] = f['pattern'].replace('41 F6 DC', '41 F6 DD')
assert not resolve(changed)[2]['chat.ui.input'], 'a missing layout capture must turn chat.ui.input off'

sha = hashlib.sha256(exe.read_bytes()).hexdigest()
if sha.startswith('5d9ef2f1'):
    want = {'chatui/state.hash.lo': 0x968a8b89, 'chatui/state.hash.hi': 0x72615e88, 'chatui/state.edit': 0x10,
            'chatui/state.editing': 0x1988, 'chatui/widget.buffer': 0x150}
    for name, value in want.items():
        assert consts[name]['ok'] and consts[name]['value'] == value, f'{name}: {consts[name]}'
print(f'chat_sigdb: all checks passed (exe {sha[:8]})')
