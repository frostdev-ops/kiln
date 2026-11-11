#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""ShroudForge's real runtime (the bundled component, as tools/package/stage.sh stages it) behind kiln-shroudforge.dll,
under Wine with no display and no game: does a mod shortcut follow the key records kiln.ui hands the adapter?

    real_keys.py <tests bin> <kiln-shroudforge.dll> <staged component: .../shroudforge-build/shroudforge> <game client folder>

ShroudForge's API needs the game's type registry (from <game>/enshrouded.exe and the version tag of enshrouded.kfc) and a
reader of its archive (enshrouded.kfc, enshrouded.kfc_resources): the three are copied, not linked, from the client folder
into the scratch game folder, so nothing is ever written back there (read in the copy, never run; the scratch folder
goes at the end). The test host runs as enshrouded.exe from a folder of its own (ShroudForge's runtime
decides client or server from the running exe's name). The scratch game folder holds the component in
kiln/plugins/shroudforge/, and a probe mod (written here) with three shortcut settings that logs
each change of shroudforge.input.is_keybind_down / was_keybind_pressed to ShroudForge's own log. No ShroudForge is
installed (no shroudforge.exe), which is the case the feed is for. host.cpp (KILN_SF_REAL_KEYS) plays key records
through its fake kiln.ui. ShroudForge answers shortcut queries only while a window of its process is in front; Wine
without a display has none, so the runtime's GetForegroundWindow import answers with a message-only window of the host
(an OS stand-in; nothing else of the runtime is replaced). Twice: with the feed, and with KILN_SF_KEY_FEED=off (the
runtime's own helper path, which has no shroudforge.exe here)."""
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile

tests, adapter, component, client = map(lambda p: Path(p).resolve(), sys.argv[1:5])
version = json.loads((component / 'version.json').read_text())
print('component:', version, (component / 'SOURCE.txt').read_text().splitlines()[1])

PROBE_MOD = {'id': 'kiln-keyfeed-probe', 'name': 'Kiln key feed probe', 'version': '1.0.0', 'authors': ['Kiln tests'],
             'license': 'MIT', 'dependencies': [], 'capabilities': ['runtime']}
PROBE_EXTENDED = {'schemaVersion': 1, 'enabled': True, 'targets': ['client'], 'settings': {
    'probeKey': {'value': 'SDL:66', 'control': 'keybind', 'label': 'F9', 'applyAt': 'live'},
    'probeChord': {'value': 'Ctrl+SDL:66', 'control': 'keybind', 'label': 'Ctrl+F9', 'applyAt': 'live'},
    'probeMouse': {'value': 'Mouse:4', 'control': 'keybind', 'label': 'Mouse 4', 'applyAt': 'live'}}}
PROBE_LUA = '''
local keys = {"probeKey", "probeChord", "probeMouse"}
local last = {}
local function look(key)
    local down = shroudforge.input.is_keybind_down(key)
    local pressed = shroudforge.input.was_keybind_pressed(key)
    if down ~= last[key] or pressed then
        shroudforge.log.info("probe " .. key .. " down=" .. tostring(down) .. " pressed=" .. tostring(pressed))
        last[key] = down
    end
end
local watching = false
return {
    update_interval_ms = 10,
    on_load = function()
        for _, key in ipairs(keys) do last[key] = false end
    end,
    on_update = function()
        for _, key in ipairs(keys) do look(key) end
        -- was_keybind_pressed counts from its first call: say so once that call has been made
        if not watching then
            watching = true
            shroudforge.log.info("probe ready")
        end
    end,
}
'''
# What the probe must log, in order, for host.cpp's steps (F9; F9 as window messages after raw input: not forwarded;
# Ctrl+F9, where F9 alone is another binding; XBUTTON1; F9 held across a focus loss, then its late release).
FED = [
    'probe probeKey down=true pressed=true',
    'probe probeKey down=false pressed=false',
    'probe probeChord down=true pressed=true',
    'probe probeChord down=false pressed=false',
    'probe probeMouse down=true pressed=true',
    'probe probeMouse down=false pressed=false',
    'probe probeKey down=true pressed=true',
    'probe probeKey down=false pressed=false',
]

with tempfile.TemporaryDirectory(prefix='kiln-sf-real-', dir=tests.parent) as tmp:
    root = Path(tmp)
    env = dict(os.environ, WINEPREFIX=str(root / 'wine'), WINEDEBUG='-all', KILN_SHROUDFORGE='1', KILN_SF_CLIENT='1',
               KILN_SF_FAKE_UI='5', KILN_SF_REAL_KEYS='1', WAYLAND_DISPLAY='kiln-no-display',
               WINEDLLOVERRIDES='mscoree=;mshtml=;winedbg.exe=d')
    env.pop('DISPLAY', None)
    def run(name, extra):
        game = root / name
        shutil.copytree(component, game / 'kiln/plugins/shroudforge')
        (game / 'kiln/kiln.json').write_text(json.dumps({'shroudforge': {'useInstalled': False}}))
        mod = game / 'mods/kiln-keyfeed-probe'
        (mod / 'src').mkdir(parents=True)
        (mod / 'mod.json').write_text(json.dumps(PROBE_MOD))
        (mod / 'extended.mod.json').write_text(json.dumps(PROBE_EXTENDED))
        (mod / 'src/mod.lua').write_text(PROBE_LUA)
        for file in ('enshrouded.exe', 'enshrouded.kfc', 'enshrouded.kfc_resources'):
            shutil.copyfile(client / file, game / file)
        host = root / (name + '-host')
        host.mkdir()
        shutil.copyfile(tests / 'sfhost.exe', host / 'enshrouded.exe')
        result = subprocess.run(['wine', str(host / 'enshrouded.exe'), str(game), str(adapter)], env=dict(env, **extra),
                                stdout=subprocess.PIPE, stderr=subprocess.STDOUT, timeout=180)
        out = result.stdout.decode(errors='replace')
        sf_log = (game / 'shroudforge/logs/shroudforge-client.log')
        sf = sf_log.read_text(errors='replace') if sf_log.exists() else ''
        status = json.loads((game / 'kiln/state/shroudforge.json').read_text())
        probe = [line[line.index('probe '):].strip() for line in sf.splitlines() if 'probe probe' in line]
        print(f'--- {name}: host exit {result.returncode}, mode {status.get("mode")}, keys {status.get("keys")}')
        for line in out.splitlines():
            if line.startswith('[real]') or 'mod shortcuts' in line or 'Keyboard' in line or 'ShroudForge runtime' in line:
                print('  ', line.strip())
        print('   probe:', *probe, sep='\n     ')
        return result, out, sf, status, probe
    result, out, sf, status, probe = run('fed', {})
    assert result.returncode == 0, out
    assert 'of this process (yes); the runtime\'s GetForegroundWindow answers it: yes' in out, \
        'no window in front: ShroudForge answers no shortcut query'
    assert status['mode'] == 'hosted' and status['keys']['source'] == 'kiln', status
    assert 'Keyboard and mouse input is fed by the host' in sf, sf[-3000:]
    assert 'Keyboard shortcuts unavailable' not in sf, sf[-3000:]
    assert probe == FED, probe
    result, out, sf, status, probe = run('helper-path', {'KILN_SF_KEY_FEED': 'off'})
    assert result.returncode == 0, out
    assert status['keys']['source'] == 'none', status
    assert 'Keyboard shortcuts unavailable' in sf, sf[-3000:]
    assert not [p for p in probe if 'down=true' in p or 'pressed=true' in p], probe
    print('PASS real runtime: the probe mod\'s shortcuts follow the fed records; without the feed they never fire')
