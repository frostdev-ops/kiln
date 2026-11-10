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
    # No window may reach the desktop: no X display, a Wayland socket that does not exist, winedbg off.
    env = dict(os.environ, WINEPREFIX=str(root / 'wine'), WINEDEBUG='-all', KILN_SHROUDFORGE='1',
               WAYLAND_DISPLAY='kiln-no-display', WINEDLLOVERRIDES='winedbg.exe=d')
    env.pop('DISPLAY', None)
    def case(name, installed='good', bundled='good', early=False, fault=False, linked_layout=False, busy=False, reservation=None, extra_env=None, verify=None):
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
            shutil.copyfile(build / ({'bad-abi': 'bad_kfc.dll', 'old': 'old_kfc.dll'}.get(mode, 'good_kfc.dll')), dest / 'kfc-runtime.dll')
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
        if busy:
            call_env['KILN_SF_BUSY_LOCK'] = '1'
        if reservation:
            call_env['KILN_SF_' + reservation] = '1'
        call_env.update(extra_env or {})
        cmd = ['wine', str(build / 'sfhost.exe'), str(game), str(adapter)] + (['early'] if early else [])
        result = subprocess.run(cmd, env=call_env, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, timeout=40)
        if busy:
            assert result.returncode == 32, (name, result.returncode, result.stdout)
            assert not log.exists(), 'a busy install must never reach prepare or the game entry point'
            assert json.loads((game / 'kiln/state/shroudforge.json').read_text())['mode'] == 'failed'
            print('PASS', name)
            return
        if result.returncode:
            raise RuntimeError(f'{name}: host exit {result.returncode}\n{result.stdout.decode(errors="replace")}')
        status = json.loads((game / 'kiln/state/shroudforge.json').read_text())
        if reservation:
            calls = log.read_text().splitlines() if log.exists() else []
            assert status['mode'] == 'failed', (name, status)
            assert calls == (['reserve'] if reservation == 'DENY_RESERVATION' else []), (name, calls)
            print('PASS', name)
            return
        calls = log.read_text().splitlines()
        assert status['source'] == ('installed' if name == 'bundled-corrupt' else 'bundled'), (name, status)
        assert calls.count('prepare') == calls.count('create') == calls.count('destroy') == calls.count('shutdown') == 1, (name, calls)
        assert calls.index('prepare') < calls.index('create') < calls.index('update') < calls.index('destroy') < calls.index('shutdown'), calls
        if early:
            assert calls.index('reserve') < calls.index('prepare'), calls
        assert status['runtime'] == 'stopped', status
        if fault:
            assert status['mode'] == 'failed', status
        if verify:
            verify(status, calls, result.stdout.decode(errors='replace'))
        print('PASS', name)
    case('installed-corrupt', installed='corrupt')
    case('installed-bad-abi', installed='bad-abi')
    case('bundled-corrupt', bundled='corrupt')
    case('early-reservation', installed='corrupt', early=True)
    case('faulted-update', installed='corrupt', fault=True)
    case('linked-layout', installed='corrupt', linked_layout=True)
    case('standalone-busy-install', busy=True)
    case('closed-reservation', installed='corrupt', early=True, reservation='DENY_RESERVATION')
    case('short-eml-table', installed='corrupt', early=True, reservation='SHORT_EML_TABLE')

    # ---- dev.21 (WP-F4): the engine hooks of the bundled native runtime through kiln.hooks ----
    def offered(status, calls, out):
        # the table is set before KfcRuntimeInitialize, which then finds the place and hooks it three ways
        assert status['engineHooks'] == 'kiln.hooks', status
        assert 'takes its engine hooks from kiln.hooks' in out, out
        assert calls.index('set-host') < calls.index('init'), calls
        assert 'find 1 1000 0' in calls and 'find-mismatch 3 0 0' in calls and 'find-ambiguous 2 0 0' in calls and 'find-none 0 0 0' in calls, calls
        assert 'installed 1 1 1' in calls, calls
        for label in ('shroudforge/game_thread', 'shroudforge/world_building_dispatch', 'shroudforge/world_cursor'):
            assert f'label={label} target=0x1000' in out, (label, out)
        # the host fired each hook with r15=0x15 rbp=0xbb rcx=0xc7 r14=0x14 r13=0x13
        assert 'cb-plain 15 bb 0' in calls, calls                 # plain: (r15, rbp)
        assert 'cb-context c7 0 0' in calls, calls                # context: (rcx, 0)
        assert 'cb-cursor 284 13 bb' in calls, calls             # cursor: (r14 + 0x270, r13, rbp)
        # KfcRuntimeShutdown takes the three hooks away again (newest first), through the host
        assert calls.count('removed') == 3 and out.count('[fake kiln.hooks] revoke') == 3, (calls, out)
        assert calls.index('shutdown') < calls.index('removed'), calls
    def self_hooking(reason, absent=True):
        def verify(status, calls, out):
            assert status['engineHooks'] == 'self', status
            assert 'hooks the game itself: ' in out and reason in out, out
            assert 'set-host' not in calls and 'find' not in ' '.join(calls), calls
        return verify
    hook_env = {'KILN_SF_FAKE_HOOKS': '1'}
    case('engine-hooks-offered', installed='corrupt', extra_env=hook_env, verify=offered)
    case('engine-hooks-offered-early', installed='corrupt', early=True, extra_env=hook_env, verify=offered)
    case('engine-hooks-no-kiln-hooks', installed='corrupt', verify=self_hooking('kiln.hooks v2 is not available'))
    case('engine-hooks-old-runtime', installed='corrupt', bundled='old', extra_env=hook_env, verify=self_hooking('no host interface'))
    case('engine-hooks-switched-off', installed='corrupt', extra_env=dict(hook_env, KILN_SF_ENGINE_HOOKS='self'), verify=self_hooking('KILN_SF_ENGINE_HOOKS=self'))
    def refused(status, calls, out):
        assert status['engineHooks'] == 'self' and 'set-host-refused' in calls and 'find' not in ' '.join(calls), (status, calls)
        assert 'hooks the game itself: kfc-runtime.dll refused the host interface' in out, out
    case('engine-hooks-refused', installed='corrupt', extra_env=dict(hook_env, KILN_SF_REFUSE_HOST='1'), verify=refused)

    # ---- dev.22: drop-ins for ShroudForge with an active profile that lists no ShroudForge mods ----
    def profile_case(name, dropins, package):
        game = root / name
        (game / 'kiln').mkdir(parents=True)
        for kind in ('shroudforge', 'kiln/plugins/shroudforge'):
            dest = game / kind
            dest.mkdir(parents=True)
            (dest / 'version.json').write_text(json.dumps({'version': '1.7.9'}))
            (dest / 'profiles').mkdir()
            shutil.copyfile(build / 'good_kfc.dll', dest / 'kfc-runtime.dll')
            shutil.copyfile(build / 'runtime.dll', dest / 'shroudforge-runtime.dll')
        if package:
            (game / 'mods/pkg').mkdir(parents=True)
            (game / 'mods/pkg/extended.mod.json').write_text(json.dumps({'enabled': True}))
        profile = {'format': 1, 'id': 'p', 'generation': 1, 'mods': {}}
        if dropins is not None:
            profile['dropins'] = dropins
        (game / 'kiln/profile.json').write_text(json.dumps(profile))
        log = game / 'calls.txt'
        call_env = dict(env, KILN_SF_TEST_LOG='Z:' + str(log).replace('/', '\\'),
                        KILN_SF_PROFILE='Z:' + str(game / 'kiln/profile.json').replace('/', '\\'))
        call_env.pop('KILN_SHROUDFORGE', None)
        result = subprocess.run(['wine', str(build / 'sfhost.exe'), str(game), str(adapter)], env=call_env,
                                stdout=subprocess.PIPE, stderr=subprocess.STDOUT, timeout=40)
        if result.returncode:
            raise RuntimeError(f'{name}: host exit {result.returncode}\n{result.stdout.decode(errors="replace")}')
        return json.loads((game / 'kiln/state/shroudforge.json').read_text()), log, result.stdout.decode(errors='replace')
    for name, dropins in [('profile-dropins-true', True), ('profile-dropins-list', ['emf', 'shroudforge'])]:
        status, log, out = profile_case(name, dropins, package=True)
        assert status['mode'] != 'off' and log.exists() and 'prepare' in log.read_text().splitlines(), (name, status, out)
        print('PASS', name)
    for name, dropins, package in [('profile-no-dropins', None, True), ('profile-dropins-other', ['emf'], True),
                                   ('profile-dropins-empty-folder', True, False)]:
        status, log, out = profile_case(name, dropins, package)
        assert status['mode'] == 'off' and not log.exists(), (name, status, out)
        print('PASS', name)
