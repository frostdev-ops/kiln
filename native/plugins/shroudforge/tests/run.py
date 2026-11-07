#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""Integration checks with fake DLLs under Wine. No game binary or installation is used."""
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile

build, adapter = map(lambda p: Path(p).resolve(), sys.argv[1:3])
with tempfile.TemporaryDirectory(prefix='kiln-sf-fixtures-', dir=build.parent) as tmp:
    root = Path(tmp)
    env = dict(os.environ, WINEPREFIX=str(root / 'wine'), WINEDEBUG='-all', KILN_SHROUDFORGE='1')
    def case(name, installed='good', bundled='good', early=False, fault=False, linked_layout=False):
        game = root / name
        if linked_layout:
            layout = root / (name + '-steam-library')
            layout.symlink_to(root, target_is_directory=True)
            game = layout / name
        for kind, mode in [('shroudforge', installed), ('kiln/plugins/shroudforge', bundled)]:
            dest = game / kind
            dest.mkdir(parents=True)
            (dest / 'version.json').write_text(json.dumps({'version': '1.7.9'}))
            (dest / 'profiles').mkdir()
            shutil.copyfile(build / ('bad_kfc.dll' if mode == 'bad-abi' else 'good_kfc.dll'), dest / 'kfc-runtime.dll')
            if mode == 'corrupt':
                (dest / 'shroudforge-runtime.dll').write_bytes(b'not a DLL')
            else:
                shutil.copyfile(build / 'runtime.dll', dest / 'shroudforge-runtime.dll')
        (game / 'kiln/kiln.json').write_text(json.dumps({'shroudforge': {'useInstalled': name != 'bundled-corrupt'}}))
        if early:
            shutil.copyfile(build / 'eml.dll', game / 'kiln/plugins/kiln-eml.dll')
        log = game / 'calls.txt'
        call_env = dict(env, KILN_SF_TEST_LOG='Z:' + str(log).replace('/', '\\'))
        if fault:
            call_env['KILN_SF_FAULT_UPDATE'] = '1'
        cmd = ['wine', str(build / 'sfhost.exe'), str(game), str(adapter)] + (['early'] if early else [])
        result = subprocess.run(cmd, env=call_env, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, timeout=40)
        if result.returncode:
            raise RuntimeError(f'{name}: host exit {result.returncode}\n{result.stdout.decode(errors="replace")}')
        status = json.loads((game / 'kiln/state/shroudforge.json').read_text())
        calls = log.read_text().splitlines()
        assert status['source'] == ('installed' if name == 'bundled-corrupt' else 'bundled'), (name, status)
        assert calls.count('prepare') == calls.count('create') == calls.count('destroy') == calls.count('shutdown') == 1, (name, calls)
        assert calls.index('prepare') < calls.index('create') < calls.index('update') < calls.index('destroy') < calls.index('shutdown'), calls
        if early:
            assert calls.index('reserve') < calls.index('prepare'), calls
        assert status['runtime'] == 'stopped', status
        if fault:
            assert status['mode'] == 'failed', status
        print('PASS', name)
    case('installed-corrupt', installed='corrupt')
    case('installed-bad-abi', installed='bad-abi')
    case('bundled-corrupt', bundled='corrupt')
    case('early-reservation', installed='corrupt', early=True)
    case('faulted-update', installed='corrupt', fault=True)
    case('linked-layout', installed='corrupt', linked_layout=True)
