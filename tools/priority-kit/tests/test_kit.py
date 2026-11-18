#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""Tests of the priority kit's scripts on made-up data (python3 -I tools/priority-kit/tests/test_kit.py):
analyze.py's numbers, collect.py's redaction, zip and switch handling, kit.sh against a fake game folder, and
cpu-load.sh's schedule, its phase file and its stop (no busy process survives Ctrl+C)."""
import contextlib
import csv
import io
import json
import os
import shutil
import signal
import subprocess
import sys
import tempfile
import time
import unittest
import zipfile
from pathlib import Path

KIT = Path(__file__).resolve().parent.parent
sys.dont_write_bytecode = True  # no __pycache__ in the kit's folder
sys.path.insert(0, str(KIT))
import analyze  # noqa: E402
import collect  # noqa: E402

T0 = 1_800_000_000_000  # a fixed UTC time in ms


def writes_csv(raise_, rows):
    out = io.StringIO()
    out.write('# gate-priority-test 1; raise=%s; label=; mode=write; profile=p\n' % raise_)
    out.write('utc_ms,op,attempt,status,result,why,engine_reason,flags,hold_ns,call_ns,step,priority\n')
    for t, status, hold in rows:
        out.write('%d,change,0,%s,0,0,0,0x5,%d,%d,1,-1\n' % (t, status, hold, hold + 1000))
    return out.getvalue()


def steps_csv(raise_, steps):
    out = io.StringIO()
    out.write('# gate-priority-test 1; raise=%s\n' % raise_)
    out.write('step,utc_ms,pre_ns,post_ns\n')
    for step, t, pre, post in steps:
        out.write('%d,%d,%d,%d\n' % (step, t, pre, post))
    return out.getvalue()


def make_zip(path, platform='windows', raise_='on', wine=False, proton=None, header_raise=None, offset_min=60):
    """Phases: idle 120 s, load 120 s, idle 120 s from T0. Idle holds 10 us, loaded holds 2 ms; idle steps every 16 ms,
    loaded every 20 ms; tick-stats windows fully inside each phase."""
    phases = [('idle', T0, T0 + 120_000), ('load', T0 + 120_000, T0 + 240_000), ('idle', T0 + 240_000, T0 + 360_000)]
    writes = []
    for name, a, b in phases:
        for t in range(a + 5000, b - 5000, 1000):
            writes.append((t, 'ok', 2_000_000 if name == 'load' else 10_000))
            writes.append((t + 1, 'busy', 0))
    writes.append((T0 + 120_000 + 500, 'ok', 99_000_000))  # inside the margin around the change: dropped
    steps, step, pre = [], 1, 0
    for name, a, b in phases:
        t = a + 3000
        while t < b - 3000:
            gap_ms = 20 if name == 'load' else 16
            steps.append((step, t, pre, pre + 4_000_000))
            step += 1
            pre += gap_ms * 1_000_000
            t += gap_ms
    tick = []
    for name, a, b in phases:
        end_local = (b - 10_000) + offset_min * 60_000  # the line's local time: window [end-60 s, end] inside the phase
        stamp = time.strftime('%Y-%m-%dT%H:%M:%S', time.gmtime(end_local / 1000))
        tick.append(json.dumps({'time': stamp, 'steps': 3600, 'step_ms': {'p50': 5.0 if name == 'idle' else 9.0, 'p99': 7.0,
                                'max': 12.0 if name == 'idle' else 30.0}, 'interval_ms': {'p50': 16.0, 'p99': 18.0, 'max': 40.0}}))
    with zipfile.ZipFile(path, 'w') as z:
        z.writestr('system.json', json.dumps({'platform': platform, 'wine': wine, 'proton': proton, 'os': 'Test OS',
                                              'cpu': 'Test CPU', 'logical_processors': 8, 'utc_offset_minutes': offset_min}))
        z.writestr('load/cpu-load-1.csv', 'phase,start_utc_ms,end_utc_ms,threads,priority\n' +
                   ''.join('%s,%d,%d,8,Normal\n' % p for p in phases))
        z.writestr('runs/run-1/run.json', json.dumps({'raise': raise_, 'mode': 'write', 'component': 'keen::ecs::X',
                                                      'pairs': 10, 'restored': True, 'verified': True, 'end': 'done',
                                                      'writes_from_utc_ms': T0, 'ended_utc_ms': T0 + 360_000}))
        z.writestr('runs/run-1/writes.csv', writes_csv(header_raise or raise_, writes))
        z.writestr('runs/run-1/steps.csv', steps_csv(raise_, steps))
        z.writestr('logs/tick-stats.jsonl', '\n'.join(tick) + '\n')


class Analyze(unittest.TestCase):
    def setUp(self):
        self.dir = Path(tempfile.mkdtemp())

    def tearDown(self):
        shutil.rmtree(self.dir)

    def test_numbers_per_platform_raise_and_load(self):
        make_zip(self.dir / 'win.zip')
        make_zip(self.dir / 'proton.zip', platform='linux', raise_='off', proton='proton-9.0-3')
        r = analyze.analyze([self.dir / 'win.zip', self.dir / 'proton.zip'])
        g = {(x['platform'], x['raise'], x['load']): x for x in r['groups']}
        self.assertEqual(sorted(g), [('proton', 'off', 'idle'), ('proton', 'off', 'loaded'),
                                     ('windows', 'on', 'idle'), ('windows', 'on', 'loaded')])
        idle, loaded = g[('windows', 'on', 'idle')], g[('windows', 'on', 'loaded')]
        self.assertEqual(idle['hold_us']['n'], 220)  # 110 per idle phase; the outlier in the margin is dropped
        self.assertEqual(loaded['hold_us']['n'], 110)
        self.assertEqual((idle['hold_us']['p50'], idle['hold_us']['max']), (10.0, 10.0))
        self.assertEqual((loaded['hold_us']['p99'], loaded['hold_us']['over_1ms']), (2000.0, 110))
        self.assertEqual(idle['histogram']['10 us-20 us'], 220)
        self.assertEqual(loaded['histogram']['2 ms-5 ms'], 110)
        self.assertAlmostEqual(idle['busy_share'], 0.5)
        self.assertEqual(idle['outcomes'], {'busy': 220, 'ok': 220})
        self.assertAlmostEqual(idle['interval_ms']['p50'], 16.0)
        self.assertAlmostEqual(loaded['interval_ms']['p50'], 20.0)
        self.assertEqual(loaded['late_steps'], 0)  # 20 ms is not above 1.5 x the idle 16 ms
        self.assertAlmostEqual(idle['pre_to_post_ms']['p50'], 4.0)
        self.assertEqual(idle['tick_stats']['windows'], 2)
        self.assertEqual(loaded['tick_stats']['windows'], 1)
        self.assertEqual((loaded['tick_stats']['step_p50_ms'], loaded['tick_stats']['step_max_ms']), (9.0, 30.0))
        self.assertEqual(len(r['runs']), 2)
        md = analyze.markdown(r)
        self.assertIn('| windows | on | loaded | 110 | 2000.0 |', md)
        self.assertIn('| proton | off | idle |', md)

    def test_backslash_entry_names(self):
        make_zip(self.dir / 'w.zip')
        with zipfile.ZipFile(self.dir / 'w.zip') as src, zipfile.ZipFile(self.dir / 'ps51.zip', 'w') as dst:
            for n in src.namelist():  # as Windows PowerShell 5.1's Compress-Archive names them
                dst.writestr(n.replace('/', '\\'), src.read(n))
        a, b = analyze.analyze([self.dir / 'w.zip']), analyze.analyze([self.dir / 'ps51.zip'])
        self.assertEqual(a['groups'], b['groups'])
        self.assertEqual(len(b['runs']), 1)

    def test_platforms(self):
        self.assertEqual(analyze.platform_of({'platform': 'windows', 'wine': True}), 'wine')
        self.assertEqual(analyze.platform_of({'platform': 'windows', 'wine': False}), 'windows')
        self.assertEqual(analyze.platform_of({'platform': 'linux', 'proton': 'proton-9'}), 'proton')
        self.assertEqual(analyze.platform_of({}), 'unknown')

    def test_a_header_that_disagrees_is_skipped(self):
        make_zip(self.dir / 'bad.zip', header_raise='off')
        r = analyze.analyze([self.dir / 'bad.zip'])
        self.assertEqual(r['runs'], [])
        self.assertTrue(any('skipped' in n for n in r['notes']))

    def test_no_phase_file_is_all_idle(self):
        make_zip(self.dir / 'w.zip')
        unpacked = self.dir / 'unpacked'
        with zipfile.ZipFile(self.dir / 'w.zip') as z:
            z.extractall(unpacked)
        (unpacked / 'load/cpu-load-1.csv').unlink()
        r = analyze.analyze([unpacked])
        self.assertEqual([x['load'] for x in r['groups']], ['idle'])
        self.assertTrue(any('no CPU-load phase file' in n for n in r['notes']))

    def test_percentile(self):
        self.assertEqual(analyze.percentile([], 50), 0)
        self.assertEqual(analyze.percentile([1, 2, 3, 4], 50), 2)
        self.assertEqual(analyze.percentile(list(range(1, 1001)), 99.9), 999)


def fake_game(root, user='alice', home='/home/alice'):
    game = root / 'Enshrouded'
    (game / 'kiln/logs/gate-priority-test/run-20261118-120000').mkdir(parents=True)
    (game / 'kiln/state').mkdir(parents=True)
    (game / 'kiln/data/kiln.engine').mkdir(parents=True)
    (game / 'enshrouded.exe').write_bytes(b'MZ')
    (game / 'kiln/kiln-core.dll').write_bytes(b'MZ')
    (game / 'kiln/logs/kiln.log').write_text(
        'Kiln 0.2.0-dev starting (pid 1)\n'
        'build abc: verified\n'
        'players: alice joined as 76561198000000001 from %s/games\n'
        'kiln.engine: exact engine profile enshrouded-client-26430625 for enshrouded.exe\n'
        'gate-priority-test: this world is 6b696c6e2d746573742d776f726c6401; put world=...\n'
        'gate-priority-test: files in Z:%s\\Steam\\ and C:\\Users\\%s\\AppData\n' % (home, home.replace('/', '\\'), user))
    (game / 'kiln/logs/tick-stats.jsonl').write_text('{"time":"2026-11-18T12:00:00","steps":1}\n')
    (game / 'kiln/state/capabilities.json').write_text('{"capabilities":[{"name":"x","reasonText":"%s/x"}]}' % home)
    run = game / 'kiln/logs/gate-priority-test/run-20261118-120000'
    (run / 'run.json').write_text('{"raise":"off"}')
    (run / 'writes.csv').write_text('# gate-priority-test 1; raise=off\nutc_ms\n1\n')
    return game


class Collect(unittest.TestCase):
    def setUp(self):
        self.dir = Path(tempfile.mkdtemp())

    def tearDown(self):
        shutil.rmtree(self.dir)

    def zip_of(self, out):
        with zipfile.ZipFile(out) as z:
            return {n: z.read(n).decode() for n in z.namelist()}

    def test_collects_redacts_and_deletes_the_switch(self):
        game = fake_game(self.dir)
        (game / 'kiln/data/kiln.engine/gate-priority-raise').write_text('off')
        (self.dir / 'cpu-load-20261118.csv').write_text('phase,start_utc_ms,end_utc_ms,threads,priority\n')
        with contextlib.redirect_stdout(io.StringIO()) as said:
            out = collect.collect(game, '', self.dir, redact=collect.Redactor('alice', 'deskbox', '/home/alice'))
        self.assertIn('it is deleted now', said.getvalue())
        self.assertIn('raise-off', out.name)
        files = self.zip_of(out)
        self.assertEqual(sorted(files), ['load/cpu-load-20261118.csv', 'logs/kiln.log', 'logs/tick-stats.jsonl',
                                         'runs/run-20261118-120000/run.json', 'runs/run-20261118-120000/writes.csv',
                                         'state/capabilities.json', 'system.json'])
        log = files['logs/kiln.log']
        self.assertNotIn('players:', log)  # lines the analysis does not read stay out
        self.assertIn('kiln.engine: exact engine profile', log)
        self.assertIn('this world is <guid>', log)
        self.assertNotIn('alice', ''.join(files.values()))
        self.assertIn('C:\\Users\\<user>\\AppData', log)
        self.assertIn('/home/<user>', files['state/capabilities.json'])
        system = json.loads(files['system.json'])
        self.assertEqual((system['platform'], system['raise_switch']), ('linux', 'off'))
        self.assertFalse((game / 'kiln/data/kiln.engine/gate-priority-raise').exists(), 'the switch is deleted at the end')

    def test_keep_switch(self):
        game = fake_game(self.dir)
        (game / 'kiln/data/kiln.engine/gate-priority-raise').write_text('off')
        with contextlib.redirect_stdout(io.StringIO()) as said:
            collect.collect(game, str(self.dir / 'o.zip'), self.dir, keep_switch=True, redact=collect.Redactor())
        self.assertIn('the measurement switch is still there', said.getvalue())
        self.assertTrue((game / 'kiln/data/kiln.engine/gate-priority-raise').exists())

    def test_redactor(self):
        r = collect.Redactor('bob', 'host', '/home/bob')
        self.assertEqual(r('bob at host'), '<user> at <computer>')
        self.assertEqual(r('bobcat'), 'bobcat')  # whole words only
        self.assertEqual(r('id 76561198012345678'), 'id <steamid>')
        self.assertEqual(r('sha ' + 'a' * 64), 'sha ' + 'a' * 64)  # a sha256 is not a GUID
        self.assertEqual(r('/home/zed/x and D:\\Users\\Zed\\y'), '/home/<user>/x and D:\\Users\\<user>\\y')


class KitSh(unittest.TestCase):
    def setUp(self):
        self.dir = Path(tempfile.mkdtemp())
        self.kit = self.dir / 'kit'
        shutil.copytree(KIT, self.kit, ignore=shutil.ignore_patterns('tests', '__pycache__'))
        (self.kit / 'mod/gate-priority-test').mkdir(parents=True)
        (self.kit / 'mod/gate-priority-test/gate-priority-test.dll').write_bytes(b'MZ')
        (self.kit / 'mod/gate-priority-test/native.json').write_text('{}')
        self.game = fake_game(self.dir)

    def tearDown(self):
        shutil.rmtree(self.dir)

    def kit_sh(self, *args, ok=True):
        p = subprocess.run(['bash', str(self.kit / 'kit.sh'), *args, '--game', str(self.game)], capture_output=True, text=True)
        if ok:
            self.assertEqual(p.returncode, 0, p.stderr + p.stdout)
        return p

    def test_install_world_raise_status_uninstall(self):
        self.kit_sh('install')
        self.assertTrue((self.game / 'kiln/native/gate-priority-test/gate-priority-test.dll').is_file())
        run_file = self.game / 'kiln/data/gate-priority-test/run.txt'
        self.assertNotIn('world=', run_file.read_text().replace('# world=', ''))
        self.kit_sh('world')
        self.assertEqual(run_file.read_text().splitlines()[0], 'world=6b696c6e2d746573742d776f726c6401')
        self.kit_sh('world', '--guid', '00112233-4455-6677-8899-AABBCCDDEEFF')
        self.assertEqual(run_file.read_text().splitlines()[0], 'world=00112233445566778899aabbccddeeff')
        self.assertEqual(run_file.read_text().count('world='), 2)  # the new line and the template's comment
        self.kit_sh('raise', 'off')
        self.assertEqual((self.game / 'kiln/data/kiln.engine/gate-priority-raise').read_text(), 'off')
        self.assertIn('present: the raise is OFF', self.kit_sh('status').stdout)
        self.kit_sh('raise', 'on')
        self.assertFalse((self.game / 'kiln/data/kiln.engine/gate-priority-raise').exists())
        self.assertNotEqual(self.kit_sh('world', '--guid', 'xyz', ok=False).returncode, 0)
        self.kit_sh('raise', 'off')
        self.kit_sh('uninstall')
        self.assertFalse((self.game / 'kiln/native/gate-priority-test').exists())
        self.assertFalse(run_file.exists())
        self.assertFalse((self.game / 'kiln/data/kiln.engine/gate-priority-raise').exists())
        self.assertTrue((self.game / 'kiln/logs/gate-priority-test').exists(), 'measurements kept without --logs')
        self.kit_sh('uninstall', '--logs')
        self.assertFalse((self.game / 'kiln/logs/gate-priority-test').exists())

    def test_a_profile_without_native_dropins_warns(self):
        (self.game / 'kiln/profile.json').write_text('{"dropins": false}')
        self.assertIn('does not load drop-in native mods', self.kit_sh('install').stdout)
        (self.game / 'kiln/profile.json').write_text('{"dropins": ["native"]}')
        self.assertNotIn('does not load drop-in native mods', self.kit_sh('install').stdout)


class CpuLoad(unittest.TestCase):
    def setUp(self):
        self.dir = Path(tempfile.mkdtemp())

    def tearDown(self):
        shutil.rmtree(self.dir)

    def busy_in(self, sid):
        ps = subprocess.run(['ps', '-eo', 'sid=,args='], capture_output=True, text=True).stdout
        return [l for l in ps.splitlines() if l.split(None, 1)[0] == str(sid) and l.split(None, 1)[1].startswith('bash -c while')]

    def test_schedule_and_phase_file(self):
        out = self.dir / 'phases.csv'
        p = subprocess.run(['bash', str(KIT / 'cpu-load.sh'), '--threads', '2', '--load', '1', '--idle', '1', '--cycles', '1',
                            '--out', str(out)], capture_output=True, text=True, timeout=60)
        self.assertEqual(p.returncode, 0, p.stderr)
        rows = list(csv.DictReader(out.read_text().splitlines()))
        self.assertEqual([r['phase'] for r in rows], ['idle', 'load', 'idle'])
        self.assertEqual([r['threads'] for r in rows], ['0', '2', '0'])
        for r in rows:
            self.assertGreaterEqual(int(r['end_utc_ms']) - int(r['start_utc_ms']), 900)

    def test_ctrl_c_stops_every_busy_process(self):
        out = self.dir / 'phases.csv'
        p = subprocess.Popen(['bash', str(KIT / 'cpu-load.sh'), '--threads', '3', '--load', '30', '--idle', '1', '--cycles', '1',
                              '--out', str(out)], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                             start_new_session=True)
        deadline = time.time() + 20
        while time.time() < deadline and len(self.busy_in(p.pid)) < 3:
            time.sleep(0.2)
        self.assertEqual(len(self.busy_in(p.pid)), 3, 'three busy processes while loaded')
        os.killpg(p.pid, signal.SIGINT)  # what Ctrl+C in its terminal sends
        p.wait(timeout=20)
        time.sleep(0.5)
        self.assertEqual(self.busy_in(p.pid), [], 'no busy process survives')
        rows = list(csv.DictReader(out.read_text().splitlines()))
        self.assertEqual([r['phase'] for r in rows], ['idle', 'load'], 'the phase stopped part-way is recorded')

    def test_refuses_a_schedule_over_the_limit(self):
        p = subprocess.run(['bash', str(KIT / 'cpu-load.sh'), '--load', '3600', '--cycles', '2', '--max-minutes', '60'],
                           capture_output=True, text=True, timeout=30)
        self.assertNotEqual(p.returncode, 0)
        self.assertIn('more than --max-minutes', p.stderr)


if __name__ == '__main__':
    unittest.main(verbosity=2)
