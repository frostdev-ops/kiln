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
    def case(name, installed='good', bundled='good', early=False, fault=False, linked_layout=False, busy=False, reservation=None, extra_env=None, verify=None, helper=False, menu_off=False, self_hosted=False, menu_key=None):
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
        if helper:
            shutil.copyfile(build / 'sfhost.exe', game / 'shroudforge/shroudforge.exe')
        if menu_off or menu_key is not None:
            (game / 'shroudforge/config').mkdir()
            menu_config = {'enabled': not menu_off}
            if menu_key is not None:
                menu_config['toggleKey'] = menu_key
            (game / 'shroudforge/config/modloader-config.json').write_text(json.dumps({'modules': {'modloaderUi': menu_config}}))
        if self_hosted:
            shutil.copyfile(build / 'bootstrap.dll', game / 'bootstrap.dll')
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
        if self_hosted:
            assert status['mode'] == 'self-hosted' and not log.exists(), (name, status)
            assert status.get('menu') != 'kiln', status
            print('PASS', name)
            return
        if reservation:
            calls = log.read_text().splitlines() if log.exists() else []
            assert status['mode'] == 'failed', (name, status)
            assert calls == (['reserve'] if reservation == 'DENY_RESERVATION' else []), (name, calls)
            print('PASS', name)
            return
        calls = log.read_text().splitlines()
        assert status['source'] == ('installed' if installed == 'good' or name == 'bundled-corrupt' else 'bundled'), (name, status)
        assert calls.count('prepare') == calls.count('create') == calls.count('destroy') == calls.count('shutdown') == 1, (name, calls)
        assert calls.index('prepare') < calls.index('create') < calls.index('update') < calls.index('destroy') < calls.index('shutdown'), calls
        if early:
            assert calls.index('reserve') < calls.index('prepare'), calls
        assert status['runtime'] == 'stopped', status
        assert not (game / 'shroudforge/runtime/heartbeat.json').exists(), 'stopped worker left a live heartbeat'
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

    # The real adapter loop must keep ticking while its filesystem I/O is slow. The fake host delays every rename the
    # adapter makes (heartbeat.json, shroudforge.json) and each open of ShroudForge's log, with no production seam.
    def slow_io(out):
        rows = [line.split(maxsplit=4)[1:] for line in out.splitlines() if line.startswith('slow-io ')]
        return [(int(b), int(e), int(t), name.strip()) for b, e, t, name in rows]
    def heartbeat_timing(stopping=False, fault=False):
        def verify(status, calls, out):
            io = slow_io(out)
            intervals = [entry for entry in io if entry[3] == 'heartbeat.json']
            assert len(intervals) == 1, (intervals, out)
            begin, end, writer, _ = intervals[0]
            assert end - begin >= 300, intervals
            timed = []
            for line in calls[calls.index('create') + 1:]:
                if line.startswith('timing '):
                    _, operation, at, thread, dt = line.split()
                    timed.append((operation, int(at), int(thread), float(dt)))
            updates = [entry for entry in timed if entry[0] == 'update']
            assert updates and all(entry[3] > 0 for entry in updates), timed
            runtime_thread = updates[0][2]
            assert writer != runtime_thread, 'heartbeat I/O still runs on the native/Lua update thread'
            # No slow file operation of the adapter on the native/Lua update thread while that loop runs (its own
            # last status write after the loop ended is fine).
            in_loop = [entry for entry in io if entry[2] == runtime_thread and entry[1] >= updates[0][1] and entry[0] < updates[-1][1]]
            assert not in_loop, ('file I/O on the native/Lua update thread during the loop', in_loop, updates[0][1], updates[-1][1])
            for name in ('shroudforge.json', 'shroudforge-server.log'):
                assert any(entry[3] == name and entry[2] == writer for entry in io), (name, 'not written/read by the writer', io)
            assert all(entry[2] == runtime_thread for entry in timed), 'runtime ABI calls moved off their serial worker'
            for index, entry in enumerate(timed):
                if entry[0] == 'update':
                    assert index and timed[index - 1][0] == 'tick', timed
            destroy = next(entry for entry in timed if entry[0] == 'destroy')
            shutdown = next(entry for entry in timed if entry[0] == 'shutdown')
            assert destroy[1] >= end and shutdown[1] >= destroy[1], 'runtime destroyed before heartbeat writer joined'
            if not stopping:
                for b, e, thread, name in io:
                    if thread == writer and updates[0][1] < b and e < updates[-1][1]:
                        progressed = sum(b < entry[1] < e for entry in updates)
                        assert progressed >= 10, ('updates blocked by slow I/O', name, progressed, timed)
                progressed = sum(begin < entry[1] < end for entry in updates)
                assert progressed >= 10, ('updates blocked by slow heartbeat I/O', progressed, timed)
                line = next(line for line in out.splitlines() if line.startswith('heartbeat '))
                heartbeat = json.loads(line.removeprefix('heartbeat '))
                assert heartbeat['schemaVersion'] == 1 and heartbeat['host'] == 'kiln' and heartbeat['mode'] == 'SERVER'
                observed = int(next(line.split()[1] for line in out.splitlines() if line.startswith('heartbeat-observed ')))
                assert heartbeat['pid'] > 0 and 0 <= observed - heartbeat['updatedAt'] <= 2, heartbeat
                print('heartbeat progress:', progressed, 'updates during slow I/O; max dt', max(entry[3] for entry in updates))
            assert (status['mode'] == 'failed') == fault, status
        return verify
    slow_env = {'KILN_SF_SLOW_HEARTBEAT': '1'}
    case('slow-heartbeat', installed='corrupt', early=True, extra_env=slow_env, verify=heartbeat_timing())
    stop_env = dict(slow_env, KILN_SF_STOP_DURING_HEARTBEAT='1')
    case('heartbeat-stop-during-write', installed='corrupt', extra_env=stop_env, verify=heartbeat_timing(stopping=True))
    case('heartbeat-fault-during-write', installed='corrupt', fault=True, extra_env=stop_env,
         verify=heartbeat_timing(stopping=True, fault=True))
    def stalled_heartbeat(status, calls, out):
        # The writer keeps running, but must repeat the loop's last timestamp,
        # not manufacture a fresh heartbeat for a stuck native/Lua update.
        lines = out.splitlines()
        assert sum(entry[3] == 'heartbeat.json' for entry in slow_io(out)) == 2, out
        started = int(next(line.split()[1] for line in lines if line.startswith('heartbeat-start ')))
        observed = int(next(line.split()[1] for line in lines if line.startswith('heartbeat-observed ')))
        heartbeat = json.loads(next(line.removeprefix('heartbeat ') for line in lines if line.startswith('heartbeat ')))
        assert heartbeat['updatedAt'] <= started + 1 and observed - heartbeat['updatedAt'] >= 2, (started, observed, heartbeat)
        assert calls.count('update') == 1 and status['mode'] == 'hosted', (status, calls)
    case('heartbeat-keeps-stalled-loop-timestamp', installed='corrupt',
         extra_env=dict(slow_env, KILN_SF_STALL_UPDATE='1'), verify=stalled_heartbeat)

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

    # ---- dev.24: no separately installed UI is needed; do not race the installed/self-hosted helper ----
    def menu_is(who, key='SDL:66'):
        def verify(status, calls, out):
            assert status['menu'] == who and status['menuKey'] == key, (status, out)
            if who in ('helper', 'kiln'):
                assert 'menu: three auto-reset toggles received by ' in out, out
        return verify
    client = {'KILN_SF_CLIENT': '1'}
    case('menu-bundled-no-exe', installed='corrupt', extra_env=client, verify=menu_is('kiln'))
    case('menu-custom-key', extra_env=client, menu_key='Ctrl+SDL:14', verify=menu_is('kiln', 'Ctrl+SDL:14'))
    case('menu-empty-key', extra_env=client, menu_key='', verify=menu_is('kiln', ''))
    case('menu-installed-helper', extra_env=client, helper=True, verify=menu_is('helper'))
    case('menu-helper-exited', extra_env=dict(client, KILN_SF_HELPER_EXIT='1'), helper=True, verify=menu_is('kiln'))
    case('menu-disabled', extra_env=dict(client, KILN_SF_MENU_OFF='1'), menu_off=True, verify=menu_is('off'))
    case('menu-self-hosted', extra_env=dict(client, KILN_SF_SELF_HOSTED='1'), self_hosted=True)

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
