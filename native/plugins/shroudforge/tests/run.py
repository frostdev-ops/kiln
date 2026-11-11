#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""Integration checks with fake DLLs under Wine. No game binary or installation is used."""
import json
import os
from pathlib import Path
import re
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
    MISSING = object()  # menu_key: no toggleKey in ShroudForge's config (None writes a JSON null)
    only = os.environ.get('KILN_SF_ONLY', '')  # run only the cases whose name starts with this
    def case(name, installed='good', bundled='good', early=False, fault=False, linked_layout=False, busy=False, reservation=None, extra_env=None, verify=None, helper=False, menu_off=False, self_hosted=False, menu_key=MISSING, runtime='runtime'):
        if only and not name.startswith(only):
            return
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
                shutil.copyfile(build / (runtime + '.dll'), dest / 'shroudforge-runtime.dll')
        (game / 'kiln/kiln.json').write_text(json.dumps({'shroudforge': {'useInstalled': name != 'bundled-corrupt'}}))
        if early:
            shutil.copyfile(build / 'eml.dll', game / 'kiln/plugins/kiln-eml.dll')
        if helper:
            shutil.copyfile(build / 'sfhost.exe', game / 'shroudforge/shroudforge.exe')
        if menu_off or menu_key is not MISSING:
            (game / 'shroudforge/config').mkdir()
            menu_config = {'enabled': not menu_off}
            if menu_key is not MISSING:
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
            if who == 'helper':
                assert 'menu: three auto-reset toggles received by helper' in out, out
            if who == 'kiln':  # the event only: Kiln.Ui, which waits on it in game, is not part of this test
                assert "menu: Kiln's auto-reset event is open; three toggles set and taken back by this test" in out, out
        return verify
    client = {'KILN_SF_CLIENT': '1'}
    case('menu-bundled-no-exe', installed='corrupt', extra_env=client, verify=menu_is('kiln'))
    case('menu-custom-key', extra_env=client, menu_key='Ctrl+SDL:14', verify=menu_is('kiln', 'Ctrl+SDL:14'))
    case('menu-empty-key', extra_env=client, menu_key='', verify=menu_is('kiln', ''))
    # ShroudForge refuses a toggleKey that is not text ("Hotkey must be an SDL binding string"): no key, never F9
    case('menu-null-key', extra_env=client, menu_key=None, verify=menu_is('kiln', ''))
    case('menu-number-key', extra_env=client, menu_key=66, verify=menu_is('kiln', ''))
    case('menu-installed-helper', extra_env=client, helper=True, verify=menu_is('helper'))
    case('menu-helper-exited', extra_env=dict(client, KILN_SF_HELPER_EXIT='1'), helper=True, verify=menu_is('kiln'))
    case('menu-disabled', extra_env=dict(client, KILN_SF_MENU_OFF='1'), menu_off=True, verify=menu_is('off'))
    case('menu-self-hosted', extra_env=dict(client, KILN_SF_SELF_HOSTED='1'), self_hosted=True)

    # ---- dev.24: mod shortcuts from Kiln's host key feed (the fork's kiln/abi/input_host.h, kiln.ui v5) ----
    # host.cpp's fake kiln.ui plays play_script(): window keys before the first raw keyboard record, raw keys with their
    # window messages, button messages before the first raw button record, raw buttons with theirs, a focus loss, an
    # unknown kind and a short record. Each key goes through one of the two key functions: raw records once the window
    # gives them (window messages after that are the same keys again), and so for buttons.
    FED = ['feed-window 41 1e0001', 'feed-window 41 c01e0001', 'feed-raw 43 0 78', 'feed-raw 43 1 78',
           'feed-mouse 4 1', 'feed-mouse 4 0', 'feed-mouse 1 1', 'feed-mouse 1 0', 'feed-reset']
    def feed_calls(calls):
        return [c for c in calls if c.startswith('feed-')]
    # Two boot orders. 'real' (the default below): what a real start does (host.cpp KILN_SF_REAL_ORDER). The early pass
    # and its on_ready start the worker before Kiln loads any service; kiln.hooks is served to early plugins (a verified
    # build); kiln.ui appears only at Kiln's plugin step, whose kiln_plugin_main and on_ready follow; the window's records
    # come after that. 'old': kiln.ui answers from the first call (kiln_plugin_main alone, or the early pass with kiln.ui
    # already there), as every key case had it before a real game start showed that it never is.
    REAL = {'KILN_SF_REAL_ORDER': '1', 'KILN_SF_FAKE_HOOKS': '1'}
    def keys_case(name, order='real', extra_env=None, **kw):
        if order == 'real':
            extra_env = dict(extra_env or {}, **REAL)
            kw['early'] = True
        case(name, extra_env=extra_env, **kw)
    def moment(out, when):  # shroudforge.json "keys" as host.cpp read it at that moment
        line = next(l for l in out.splitlines() if l.startswith(f'[host] status {when}: '))
        return json.loads(line.split(': ', 1)[1])['keys']
    def keys_from_kiln(order='real'):
        def verify(status, calls, out):
            assert feed_calls(calls) == ['feed-version', 'feed-begin'] + FED + ['feed-end'], feed_calls(calls)
            if order == 'real':
                # begin at the early on_ready, before create; the subscription at the plugin step; records after it
                assert calls.index('feed-begin') < calls.index('create'), calls
                assert calls.index('feed-begin') < calls.index('ui-subscribe') < calls.index(FED[0]), calls
                early, step = moment(out, 'after the early phase'), moment(out, 'after the plugin step')
                assert early['source'] == 'waiting' and "Kiln's plugin step" in early['reason'], early
                assert step['source'] == 'kiln' and 'not a check' in step['reason'], step
                assert out.index('key state from Kiln, waiting for kiln.ui: ') < out.index('key state from Kiln: '), out
            else:
                assert calls.index('ui-subscribe') < calls.index('feed-begin') < calls.index('create'), calls
            assert calls.index('ui-unsubscribe') < calls.index('feed-end') < calls.index('destroy'), calls
            assert status['keys']['source'] == 'kiln', status
            assert 'ShroudForge mod shortcuts: key state from Kiln: ' in out, out
            assert 'not a check' in status['keys']['reason'], status
            assert '[fake kiln.ui] subscriptions left: 0' in out, out
        return verify
    def no_feed(source, why, calls_wanted=()):
        def verify(status, calls, out):
            assert feed_calls(calls) == list(calls_wanted), (feed_calls(calls), calls)
            assert status['keys']['source'] == source and why in status['keys']['reason'], status
            assert 'ShroudForge mod shortcuts: ' in out, out
        return verify
    ui5 = dict(client, KILN_SF_FAKE_UI='5')
    keys_case('keys-fed-bundled', order='old', installed='corrupt', runtime='runtime_feed', extra_env=ui5,
              verify=keys_from_kiln('old'))
    keys_case('keys-fed-installed', runtime='runtime_feed', extra_env=ui5, verify=keys_from_kiln())
    keys_case('keys-fed-early', order='old', installed='corrupt', early=True, runtime='runtime_feed', extra_env=ui5,
              verify=keys_from_kiln('old'))
    def raw_unregistered(status, calls, out):
        want = ['feed-raw 43 0 78', 'feed-mouse 4 1', 'feed-window 78 c0430001', 'feed-mouse 4 0',
                'feed-window 78 430001', 'feed-window 78 c0430001', 'feed-mouse 4 1', 'feed-mouse 4 0',
                'feed-raw 43 0 78', 'feed-raw 43 1 78', 'feed-raw 37 2 2c', 'feed-raw 37 3 2c',
                'feed-mouse 4 1', 'feed-mouse 4 0']
        assert feed_calls(calls) == ['feed-version', 'feed-begin'] + FED + want + ['feed-end'], feed_calls(calls)
    keys_case('keys-raw-unregistered', installed='corrupt', runtime='runtime_feed',
              extra_env=dict(ui5, KILN_SF_FEED_UNREGISTER='1'), verify=raw_unregistered)
    # one export missing (an upstream runtime has none; the fork before 9d586f1 neither): no call to any of them
    keys_case('keys-export-missing', installed='corrupt', runtime='runtime_feed_partial', extra_env=ui5,
              verify=no_feed('none', 'has no host key feed'))
    keys_case('keys-old-runtime', installed='corrupt', extra_env=ui5, verify=no_feed('none', 'has no host key feed'))
    keys_case('keys-old-runtime-helper', runtime='runtime', extra_env=ui5, helper=True,
              verify=no_feed('helper', "ShroudForge's own shroudforge.exe"))
    keys_case('keys-unknown-version', installed='corrupt', runtime='runtime_feed',
              extra_env=dict(ui5, KILN_SF_FEED_VERSION='2'), verify=no_feed('none', 'version 2', ['feed-version']))
    def begin_refused(order):
        def verify(status, calls, out):
            no_feed('helper', 'already runs', ['feed-version', 'feed-begin'])(status, calls, out)
            if order == 'old':
                assert calls.index('feed-begin') < calls.index('ui-unsubscribe') < calls.index('create'), calls
            else:  # nothing began, so the plugin step attaches nothing
                assert 'ui-subscribe' not in calls and moment(out, 'after the early phase') == status['keys'], (calls, out)
        return verify
    for order in ('old', 'real'):
        keys_case('keys-begin-refused' + ('-real' if order == 'real' else ''), order=order, installed='corrupt',
                  runtime='runtime_feed', extra_env=dict(ui5, KILN_SF_FEED_BEGIN='0'), verify=begin_refused(order))
    keys_case('keys-server', installed='corrupt', runtime='runtime_feed', extra_env={'KILN_SF_FAKE_UI': '5'},
              verify=no_feed('none', 'dedicated server'))
    # no kiln.ui, or one without the subscription, in a start where that is the final answer (no early pass)
    keys_case('keys-no-ui', order='old', installed='corrupt', runtime='runtime_feed', extra_env=client,
              verify=no_feed('none', 'kiln.ui v5'))
    keys_case('keys-old-ui', order='old', installed='corrupt', runtime='runtime_feed',
              extra_env=dict(client, KILN_SF_FAKE_UI='4'), verify=no_feed('none', 'kiln.ui v5'))
    keys_case('keys-short-ui', order='old', installed='corrupt', runtime='runtime_feed',
              extra_env=dict(client, KILN_SF_FAKE_UI='short'), verify=no_feed('none', 'kiln.ui v5'))
    keys_case('keys-switched-off', installed='corrupt', runtime='runtime_feed', extra_env=dict(ui5, KILN_SF_KEY_FEED='off'),
              verify=no_feed('none', 'KILN_SF_KEY_FEED=off'))
    def feed_fault(export):
        def verify(status, calls, out):
            fed = feed_calls(calls)
            if export == 'version':
                assert fed == ['feed-version'] and status['keys']['source'] == 'none', (fed, status)
            elif export == 'begin':
                assert fed == ['feed-version', 'feed-begin'] and 'faulted in begin' in status['keys']['reason'], (fed, status)
                assert 'ui-subscribe' not in calls, calls  # nothing began: the plugin step attaches nothing
            elif export == 'end':
                keys_from_kiln()(status, calls, out)
                assert 'faulted when it was ended' in out, out
            else:
                last = next(i for i, c in enumerate(FED) if c.split()[0] == 'feed-' + export)
                assert fed == ['feed-version', 'feed-begin'] + FED[:last + 1], fed
                assert 'host key feed faulted on the window thread' in out, out
        return verify
    for export in ('version', 'begin', 'raw', 'window', 'mouse', 'reset', 'end'):
        keys_case('keys-fault-' + export, installed='corrupt', runtime='runtime_feed',
                  extra_env=dict(ui5, KILN_SF_FEED_FAULT=export), verify=feed_fault(export))
    # Teardown while the window thread sends key records without a pause (each feed call takes 2 ms): no export after
    # end or destroy. STUCK: kiln.ui's unsubscribe fails and records keep coming after the teardown.
    def raced(stuck):
        def verify(status, calls, out):
            late = [c for c in calls if c.startswith('feed-out-of-turn')]
            assert not late, late[:5]
            assert calls.count('feed-end') == 1 and calls.index('feed-end') < calls.index('destroy'), calls[-20:]
            records, after = map(int, re.search(r'delivered (\d+) records, (\d+) after', out).groups())
            assert calls.count('feed-raw 43 0 78') >= 10 and records >= 20, (records, calls.count('feed-raw 43 0 78'))
            if stuck:
                assert 'ui-unsubscribe-refused' in calls and after >= 10, (after, calls[-10:])
            else:
                assert calls.index('ui-unsubscribe') < calls.index('feed-end'), calls[-20:]
                assert '[fake kiln.ui] subscriptions left: 0' in out, out
            print(f'teardown race: {records} records sent, {calls.count("feed-raw 43 0 78")} key presses fed, {after} after the teardown')
        return verify
    race = dict(ui5, KILN_SF_FEED_RACE='1', KILN_SF_FEED_SLOW='1')
    keys_case('keys-teardown-race', installed='corrupt', runtime='runtime_feed', extra_env=race, verify=raced(False))
    keys_case('keys-teardown-race-stuck-ui', installed='corrupt', runtime='runtime_feed',
              extra_env=dict(race, KILN_SF_FAKE_UI_STUCK='1'), verify=raced(True))

    # ---- the real boot order: the feed begins at the early on_ready and attaches at Kiln's plugin step ----
    # (a) no shroudforge.exe: begun before create ("waiting"), attached at the plugin step ("kiln"), records seen
    keys_case('keys-real-order', installed='corrupt', runtime='runtime_feed', extra_env=ui5, verify=keys_from_kiln())
    # (b) kiln.ui does not come at the plugin step (kiln-ui.dll did not load, or an older one): begun, never fed, ended
    def ui_never(status, calls, out):
        assert feed_calls(calls) == ['feed-version', 'feed-begin', 'feed-end'], feed_calls(calls)
        assert 'ui-subscribe' not in calls and calls.index('feed-begin') < calls.index('create'), calls
        assert moment(out, 'after the early phase')['source'] == 'waiting', out
        step = moment(out, 'after the plugin step')
        assert step['source'] == 'none' and "not there at Kiln's plugin step" in step['reason'], step
        assert 'no shroudforge.exe is installed' in step['reason'] and status['keys'] == step, (step, status)
        assert 'key state from Kiln, waiting for kiln.ui: ' in out and 'key state from nowhere: ' in out, out
    keys_case('keys-real-no-ui', installed='corrupt', runtime='runtime_feed', extra_env=client, verify=ui_never)
    keys_case('keys-real-old-ui', installed='corrupt', runtime='runtime_feed', extra_env=dict(client, KILN_SF_FAKE_UI='4'),
              verify=ui_never)
    # (b) with shroudforge.exe installed: begin would give up the helper for good, and kiln.ui may not come: the helper
    # keeps the key state, decided at the early on_ready and unchanged by the plugin step
    def helper_kept(why):
        def verify(status, calls, out):
            assert feed_calls(calls) == [] and 'ui-subscribe' not in calls, calls
            assert status['keys']['source'] == 'helper' and why in status['keys']['reason'], status
            assert "ShroudForge's own shroudforge.exe --keyboard-input" in status['keys']['reason'], status
            assert moment(out, 'after the early phase') == moment(out, 'after the plugin step') == status['keys'], out
        return verify
    keys_case('keys-real-no-ui-helper', installed='corrupt', runtime='runtime_feed', extra_env=client, helper=True,
              verify=helper_kept("comes only at Kiln's plugin step"))
    keys_case('keys-real-ui-helper', installed='corrupt', runtime='runtime_feed', extra_env=ui5, helper=True,
              verify=helper_kept("comes only at Kiln's plugin step"))
    # (b) safe idle: kiln.hooks is not served to the early plugins and no plugin step comes: the final answer at once
    safe_idle = dict(ui5, KILN_SF_REAL_ORDER='1', KILN_SF_NO_PLUGIN_STEP='1')
    def idle(source):
        def verify(status, calls, out):
            assert feed_calls(calls) == [] and 'ui-subscribe' not in calls, calls
            assert status['keys']['source'] == source and 'no Kiln plugin step (safe idle' in status['keys']['reason'], status
            assert moment(out, 'after the early phase') == status['keys'], out
        return verify
    case('keys-real-safe-idle', installed='corrupt', early=True, runtime='runtime_feed', extra_env=safe_idle,
         verify=idle('none'))
    case('keys-real-safe-idle-helper', installed='corrupt', early=True, runtime='runtime_feed', extra_env=safe_idle,
         helper=True, verify=idle('helper'))
    # kiln.hooks served, but no plugin step comes after all (symbol definitions missing): "waiting" until the runtime
    # stops, then the final word
    def stopped_waiting(early_moment=True):
        def verify(status, calls, out):
            assert feed_calls(calls) == ['feed-version', 'feed-begin', 'feed-end'], feed_calls(calls)
            assert not [c for c in calls if c.startswith('feed-out-of-turn')], calls
            if early_moment:
                assert moment(out, 'after the early phase')['source'] == 'waiting', out
            assert status['keys']['source'] == 'none' and 'stopped before Kiln\'s plugin step' in status['keys']['reason'], status
            assert '[fake kiln.ui] subscriptions left: 0' in out, out
        return verify
    def no_step(status, calls, out):
        stopped_waiting()(status, calls, out)
        assert 'ui-subscribe' not in calls and moment(out, 'after the plugin step')['source'] == 'waiting', out
    keys_case('keys-real-no-plugin-step', installed='corrupt', runtime='runtime_feed',
              extra_env=dict(ui5, KILN_SF_NO_PLUGIN_STEP='1'), verify=no_step)
    # (d) the worker stops (its first update faults) right before the plugin step: the attach subscribes nothing. A
    # stopped worker also closes ShroudForge's menu event, so these two cases switch that menu off (host.cpp's toggles).
    stopped = dict(ui5, KILN_SF_MENU_OFF='1')
    def stop_before(status, calls, out):
        stopped_waiting(early_moment=False)(status, calls, out)  # the fault may come before the host reads the file
        assert 'ui-subscribe' not in calls and status['mode'] == 'failed', (calls, status)
        assert moment(out, 'after the plugin step') == status['keys'], out
    keys_case('keys-real-stop-before-attach', installed='corrupt', runtime='runtime_feed', fault=True, menu_off=True,
              extra_env=dict(stopped, KILN_SF_PLUGIN_STEP_AFTER_STOP='1'), verify=stop_before)
    # (d) the worker stops while the attach is inside kiln.ui's subscribe, and the window hands that subscriber a record
    # at once: no record after end, the subscription removed again, the teardown's word stands, no hang
    def stop_during(status, calls, out):
        stopped_waiting()(status, calls, out)
        assert 'update-saw-subscribe' in calls and 'subscribe returns after the worker\'s teardown: yes' in out, (calls, out)
        assert calls.index('feed-end') < calls.index('ui-subscribe') < calls.index('ui-unsubscribe'), calls
        assert status['mode'] == 'failed' and moment(out, 'after the plugin step') == status['keys'], (status, out)
    keys_case('keys-real-stop-during-attach', installed='corrupt', runtime='runtime_feed', menu_off=True,
              extra_env=dict(stopped, KILN_SF_STOP_IN_SUBSCRIBE='1', KILN_SF_FAULT_AFTER_SUBSCRIBE='1'), verify=stop_during)

    # ---- dev.22: drop-ins for ShroudForge with an active profile that lists no ShroudForge mods ----
    def profile_case(name, dropins, package, state=None):
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
        if state:  # ShroudForge's own state file: <game>/shroudforge/state.json (its default paths.state folder)
            (game / 'shroudforge/state.json').write_text(json.dumps(state))
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
    # no package left, but ShroudForge's last startup patch is applied: it must run to keep (or undo) that patch
    status, log, out = profile_case('profile-dropins-applied-state', True, package=False, state={'assets': {'status': 'applied'}})
    assert status['mode'] != 'off' and log.exists() and 'prepare' in log.read_text().splitlines(), (status, out)
    print('PASS profile-dropins-applied-state')
    for name, dropins, package in [('profile-no-dropins', None, True), ('profile-dropins-other', ['emf'], True),
                                   ('profile-dropins-empty-folder', True, False)]:
        status, log, out = profile_case(name, dropins, package)
        assert status['mode'] == 'off' and not log.exists(), (name, status, out)
        print('PASS', name)
