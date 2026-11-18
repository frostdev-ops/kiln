#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""The priority kit's collection script for Linux and Proton (docs/kiln/priority-kit.md); collect.ps1 is the Windows one
and both follow the same rules. Run it after each game run, before the next start (Kiln starts tick-stats.jsonl afresh
every time the game starts):

    tools/priority-kit/collect.sh [--game <the Enshrouded folder>] [--out <zip>] [--keep-switch]

It puts into one zip, next to this script unless --out says otherwise: the lines of kiln/logs/kiln.log and kiln.prev.log
the analysis reads (not the whole log), kiln/logs/tick-stats.jsonl, kiln/state/capabilities.json, every run of the test
mod (kiln/logs/gate-priority-test/run-*), the CPU-load helper's phase files (cpu-load-*.csv next to this script), the
output of the check without the game (fault-test*.txt, ditto), and
system.json (distribution, kernel, Proton version, CPU, cores, memory, the CPU governor, the nice limit, the time zone
offset, and whether the measurement switch was set). The user name, the machine's name, home folders, Steam ids and
world GUIDs are replaced before anything is written; look into the zip before you post it. At the end it deletes the
measurement switch (kiln/data/kiln.engine/gate-priority-raise) if it is still there; --keep-switch keeps it."""
import argparse
import datetime
import getpass
import json
import os
import platform
import re
import resource
import socket
import sys
import tempfile
import time
import zipfile
from pathlib import Path

KIT_VERSION = '1'
APP_ID = '1203620'
KEEP = re.compile(r'Kiln |build |config:|kiln\.engine|engine\.|gate-priority-test|world\.ecs|ecs-write|kiln\.tasks|tasks[:.]|'
                  r'watchdog|tick:|crash|fault|quarantin|safe idle')


def libraries():
    """Steam library folders, from the usual Steam roots' libraryfolders.vdf."""
    home = Path.home()
    roots = [home / '.local/share/Steam', home / '.steam/steam', home / '.var/app/com.valvesoftware.Steam/.local/share/Steam']
    seen = []
    for root in roots:
        vdf = root / 'steamapps/libraryfolders.vdf'
        candidates = [root]
        if vdf.is_file():
            candidates += [Path(m) for m in re.findall(r'"path"\s+"([^"]+)"', vdf.read_text(errors='replace'))]
        for c in candidates:
            if c not in seen:
                seen.append(c)
    return seen


def find_game(given):
    if given:
        game = Path(given).expanduser()
        if not (game / 'enshrouded.exe').is_file():
            sys.exit('No enshrouded.exe in %s' % game)
        return game.resolve()
    for lib in libraries():
        game = lib / 'steamapps/common/Enshrouded'
        if (game / 'enshrouded.exe').is_file():
            return game.resolve()
    sys.exit('The Enshrouded folder was not found in the Steam libraries; pass --game "<the folder with enshrouded.exe>".')


class Redactor:
    """Replaces what names the person or the machine: the user name, the host name, home folders, Steam ids, GUIDs."""

    def __init__(self, user=None, host=None, home=None):
        self.literal = []
        for value, token in ((home, '/home/<user>'), (user, '<user>'), (host, '<computer>')):
            if value and len(value) >= 3:
                self.literal.append((re.compile(r'(?<![A-Za-z0-9])' + re.escape(value) + r'(?![A-Za-z0-9])', re.I), token))

    def __call__(self, text):
        for pattern, token in self.literal:
            text = pattern.sub(token.replace('\\', '\\\\'), text)
        text = re.sub(r'(?i)([a-z]:[\\/]+users[\\/]+)[^\\/"\s]+', r'\1<user>', text)
        text = re.sub(r'(/home/|/Users/)[^/"\s]+', r'\1<user>', text)
        text = re.sub(r'7656119\d{10}', '<steamid>', text)
        text = re.sub(r'(?<![0-9A-Fa-f])[0-9A-Fa-f]{32}(?![0-9A-Fa-f])', '<guid>', text)
        return text


def read(path):
    return Path(path).read_text(encoding='utf-8', errors='replace')


def proton_version(game):
    """The Proton build Steam ran the game with (compatdata/<app>/version), when it is there."""
    library = game.parent.parent.parent  # <library>/steamapps/common/Enshrouded
    for f in (library / 'steamapps/compatdata' / APP_ID / 'version', library / 'steamapps/compatdata' / APP_ID / 'config_info'):
        if f.is_file():
            text = read(f).strip()
            return text.splitlines()[0].strip() if text else ''
    return None


def first_line(path, prefix):
    try:
        for line in read(path).splitlines():
            if line.startswith(prefix):
                return line.split(':', 1)[1].strip()
    except OSError:
        pass
    return None


def system_info(game, switch_text, files):
    os_release = {}
    try:
        for line in read('/etc/os-release').splitlines():
            if '=' in line:
                k, v = line.split('=', 1)
                os_release[k] = v.strip().strip('"')
    except OSError:
        pass
    mem_kb = 0
    try:
        mem_kb = int(re.search(r'MemTotal:\s+(\d+)', read('/proc/meminfo')).group(1))
    except (OSError, AttributeError):
        pass
    governor = None
    try:
        governor = read('/sys/devices/system/cpu/cpu0/cpufreq/scaling_governor').strip()
    except OSError:
        pass
    nice_soft, nice_hard = resource.getrlimit(resource.RLIMIT_NICE)
    cores = None
    try:
        cores = len({(m.group(1), m.group(2)) for m in re.finditer(r'physical id\s*:\s*(\d+)[\s\S]*?core id\s*:\s*(\d+)', read('/proc/cpuinfo'))}) or None
    except OSError:
        pass
    return {
        'kit': KIT_VERSION,
        'collector': 'collect.py',
        'platform': 'linux',
        'wine': True,
        'os': os_release.get('PRETTY_NAME', platform.system()),
        'kernel': platform.release(),
        'proton': proton_version(game),
        'cpu': first_line('/proc/cpuinfo', 'model name'),
        'cores': cores,
        'logical_processors': os.cpu_count(),
        'memory_gb': round(mem_kb / 1024 / 1024, 1),
        'cpu_governor': governor,
        'rlimit_nice': [nice_soft, nice_hard],
        'utc_offset_minutes': int(datetime.datetime.now().astimezone().utcoffset().total_seconds() // 60),
        'collected_utc_ms': int(time.time() * 1000),
        'raise_switch': switch_text,
        'files': files,
    }


def collect(game, out, here, keep_switch=False, redact=None):
    kiln = game / 'kiln'
    logs = kiln / 'logs'
    switch = kiln / 'data/kiln.engine/gate-priority-raise'
    redact = redact or Redactor(getpass.getuser(), socket.gethostname(), str(Path.home()))
    entries = {}
    for name in ('kiln.log', 'kiln.prev.log'):
        p = logs / name
        if p.is_file():
            lines = [redact(line) for line in read(p).splitlines() if KEEP.search(line)]
            if lines:
                entries['logs/' + name] = '\n'.join(lines) + '\n'
    if (logs / 'tick-stats.jsonl').is_file():
        entries['logs/tick-stats.jsonl'] = redact(read(logs / 'tick-stats.jsonl'))
    if (kiln / 'state/capabilities.json').is_file():
        entries['state/capabilities.json'] = redact(read(kiln / 'state/capabilities.json'))
    runs = logs / 'gate-priority-test'
    if runs.is_dir():
        for run in sorted(p for p in runs.iterdir() if p.is_dir() and p.name.startswith('run-')):
            for f in sorted(run.iterdir()):
                if f.is_file():
                    entries['runs/%s/%s' % (run.name, f.name)] = redact(read(f))
    for f in sorted(Path(here).glob('cpu-load-*.csv')):
        entries['load/' + f.name] = read(f)
    for f in sorted(Path(here).glob('fault-test*.txt')):
        entries['checks/' + f.name] = redact(read(f))
    switch_text = read(switch).strip() if switch.is_file() else None
    entries['system.json'] = redact(json.dumps(system_info(game, switch_text, sorted(entries)), indent=2)) + '\n'
    mode = 'raise-off' if switch_text == 'off' else 'raise-on'
    stamp = datetime.datetime.now(datetime.timezone.utc).strftime('%Y%m%d-%H%M%S')
    out = Path(out) if out else Path(here) / ('kiln-priority-kit-linux-%s-%s.zip' % (mode, stamp))
    with tempfile.NamedTemporaryFile(dir=out.parent, delete=False, suffix='.part') as tmp:
        with zipfile.ZipFile(tmp, 'w', zipfile.ZIP_DEFLATED) as z:
            for name, text in sorted(entries.items()):
                z.writestr(name, text)
    os.replace(tmp.name, out)
    print('Collected %d file(s) into %s:' % (len(entries), out))
    for name in sorted(entries):
        print('  ' + name)
    if not any(n.startswith('runs/') for n in entries):
        print('WARNING: no run of the test mod was found (kiln/logs/gate-priority-test/run-*). '
              'Did the run file name the world you played?')
    if switch.exists():
        if keep_switch:
            print('WARNING: the measurement switch is still there (%s): the next start runs with the priority raise OFF. '
                  'Delete it when you are done (kit.sh raise on).' % switch)
        else:
            switch.unlink()
            print('WARNING: the measurement switch was still there; it is deleted now, so the next start runs with the '
                  'priority raise on (the normal state).')
    return out


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__.split('\n\n')[0])
    ap.add_argument('--game', default='')
    ap.add_argument('--out', default='')
    ap.add_argument('--keep-switch', action='store_true')
    args = ap.parse_args(argv)
    collect(find_game(args.game), args.out, Path(__file__).resolve().parent, args.keep_switch)
    return 0


if __name__ == '__main__':
    sys.exit(main())
