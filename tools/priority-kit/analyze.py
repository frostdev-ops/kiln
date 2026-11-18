#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""The priority kit's analysis (docs/kiln/priority-kit.md): turns the zips collect.ps1 and collect.sh made into the numbers
GitHub #58 asks for, per platform (native Windows, Proton), priority raise (on, off) and CPU load (idle, loaded):

  1. the hold time of every call that took the engine's gate (the test mod's writes.csv): a histogram and percentiles;
  2. the server step time: the test mod's own Pre-to-Pre intervals and Pre-to-Post times (steps.csv), and the minute
     windows of Kiln's tick-stats.jsonl that lie inside one load phase;
  3. the outcomes (ok, busy, conflict, ...).

    python3 analyze.py kiln-priority-kit-*.zip [--json] [--margin 2]

A row's load phase comes from the CPU-load helper's phase file in the same zip (cpu-load-*.csv, UTC times); without one,
everything counts as idle. --margin drops the seconds around each phase change (default 2). Markdown goes to stdout,
ready to paste into the issue; --json prints the same numbers as one JSON document."""
import argparse
import csv
import io
import json
import math
import sys
import zipfile
from datetime import datetime, timedelta, timezone
from pathlib import Path

BUCKETS_US = [10, 20, 50, 100, 200, 500, 1000, 2000, 5000, 10000, 20000]


def bucket_names():
    names, lo = [], 0
    for hi in BUCKETS_US:
        names.append(label(lo) + '-' + label(hi))
        lo = hi
    names.append('>=' + label(lo))
    return names


def label(us):
    return '%g ms' % (us / 1000) if us >= 1000 else '%g us' % us


def percentile(values, p):
    """Nearest-rank percentile of sorted values (0 when empty)."""
    if not values:
        return 0
    rank = max(1, math.ceil(p * len(values) / 100 - 1e-9))  # the epsilon: 99.9 * 1000 / 100 is not quite 999
    return values[min(rank, len(values)) - 1]


class Source:
    """One zip or folder from a collection script."""

    def __init__(self, path):
        self.path = Path(path)
        self._zip = zipfile.ZipFile(self.path) if self.path.is_file() else None
        # Windows PowerShell 5.1's Compress-Archive writes entry names with backslashes: use '/' throughout
        if self._zip:
            self._entries = {n.replace('\\', '/'): n for n in self._zip.namelist() if not n.endswith(('/', '\\'))}
        else:
            self._entries = {str(p.relative_to(self.path)).replace('\\', '/'): p for p in self.path.rglob('*') if p.is_file()}

    def names(self):
        return list(self._entries)

    def text(self, name):
        entry = self._entries.get(name)
        if entry is None:
            return None
        try:
            if self._zip:
                return self._zip.read(entry).decode('utf-8', errors='replace')
            return Path(entry).read_text(encoding='utf-8', errors='replace')
        except (KeyError, OSError):
            return None


def rows(text):
    """CSV rows as dicts; '#' lines (the header comment) are skipped and returned apart."""
    if not text:
        return [], ''
    comment = ''
    body = []
    for line in text.splitlines():
        if line.startswith('#'):
            comment = line
        elif line.strip():
            body.append(line)
    return list(csv.DictReader(io.StringIO('\n'.join(body)))), comment


class Phases:
    def __init__(self, phases, margin_ms):
        self.phases = sorted(phases, key=lambda p: p[1])
        self.margin = margin_ms

    def of(self, t_ms):
        """'loaded', 'idle', or None inside a margin around a change."""
        for name, start, end in self.phases:
            if start <= t_ms <= end:
                if t_ms - start < self.margin or end - t_ms < self.margin:
                    return None
                return 'loaded' if name == 'load' else 'idle'
        for name, start, end in self.phases:  # within a margin outside a phase
            if start - self.margin <= t_ms <= end + self.margin:
                return None
        return 'idle'

    def window(self, start_ms, end_ms):
        """The phase a whole window lies in (90% of it), else None."""
        for name, start, end in self.phases:
            overlap = min(end, end_ms) - max(start, start_ms)
            if overlap >= 0.9 * (end_ms - start_ms):
                return 'loaded' if name == 'load' else 'idle'
        if not any(min(end, end_ms) > max(start, start_ms) for _, start, end in self.phases):
            return 'idle'
        return None


def platform_of(system):
    if not system:
        return 'unknown'
    if system.get('platform') == 'linux':
        return 'proton' if system.get('proton') is not None else 'linux'
    return 'wine' if system.get('wine') else 'windows'


def analyze(paths, margin_s=2.0):
    groups = {}   # (platform, raise, load) -> {"holds": [], "status": {}, "intervals": [], "pre_post": [], "windows": []}
    runs = []
    notes = []

    def group(platform, raise_, load):
        return groups.setdefault((platform, raise_, load), {'holds': [], 'status': {}, 'intervals': [], 'pre_post': [],
                                                             'windows': []})

    for path in paths:
        src = Source(path)
        names = src.names()
        system = json.loads(src.text('system.json') or '{}')
        platform = platform_of(system)
        phases = []
        for n in names:
            if n.startswith('load/') and n.endswith('.csv'):
                for r in rows(src.text(n))[0]:
                    try:
                        phases.append((r['phase'], int(r['start_utc_ms']), int(r['end_utc_ms'])))
                    except (KeyError, ValueError):
                        pass
        if not phases:
            notes.append('%s: no CPU-load phase file; everything counts as idle' % src.path.name)
        ph = Phases(phases, margin_s * 1000)
        run_dirs = sorted({n.split('/')[1] for n in names if n.startswith('runs/') and n.count('/') >= 2})
        offset = timedelta(minutes=system.get('utc_offset_minutes', 0) or 0)
        for rd in run_dirs:
            run = json.loads(src.text('runs/%s/run.json' % rd) or '{}')
            raise_ = run.get('raise', 'unknown')
            writes, header = rows(src.text('runs/%s/writes.csv' % rd))
            if header and ('raise=%s' % raise_) not in header:
                notes.append('%s/%s: writes.csv says %s but run.json says raise=%s; skipped' % (src.path.name, rd, header, raise_))
                continue
            runs.append({'source': src.path.name, 'run': rd, 'platform': platform, 'os': system.get('os'),
                         'cpu': system.get('cpu'), 'logical_processors': system.get('logical_processors'),
                         'raise': raise_, 'mode': run.get('mode'), 'component': run.get('component'),
                         'pairs': run.get('pairs'), 'restored': run.get('restored'), 'verified': run.get('verified'),
                         'end': run.get('end'), 'label': run.get('label'), 'profile': run.get('profile')})
            for w in writes:
                try:
                    t = int(w['utc_ms'])
                except (KeyError, ValueError):
                    continue
                load = ph.of(t)
                if load is None:
                    continue
                g = group(platform, raise_, load)
                g['status'][w['status']] = g['status'].get(w['status'], 0) + 1
                if w['status'] == 'ok':
                    g['holds'].append(int(w['hold_ns']))
            steps = rows(src.text('runs/%s/steps.csv' % rd))[0]
            prev = None
            for s in steps:
                try:
                    step, t, pre, post = int(s['step']), int(s['utc_ms']), int(s['pre_ns']), int(s['post_ns'])
                except (KeyError, ValueError):
                    continue
                load = ph.of(t)
                if load is not None:
                    g = group(platform, raise_, load)
                    if prev and step == prev[0] + 1 and pre > prev[1]:
                        g['intervals'].append((pre - prev[1]) / 1e6)
                    if post > pre:
                        g['pre_post'].append((post - pre) / 1e6)
                prev = (step, pre)
            start = run.get('writes_from_utc_ms') or run.get('started_utc_ms') or 0
            end = run.get('ended_utc_ms') or 2 ** 62
            for line in (src.text('logs/tick-stats.jsonl') or '').splitlines():
                try:
                    d = json.loads(line)
                    if d.get('world', 'server') != 'server':
                        continue
                    local = datetime.strptime(d['time'], '%Y-%m-%dT%H:%M:%S')
                    w_end = int(((local - offset).replace(tzinfo=timezone.utc)).timestamp() * 1000)
                except (ValueError, KeyError, TypeError):
                    continue
                w_start = w_end - 60_000
                if w_start < start or w_end > end:
                    continue
                load = ph.window(w_start, w_end)
                if load is None:
                    continue
                group(platform, raise_, load)['windows'].append({k: d.get(k, {}) for k in ('step_ms', 'interval_ms', 'pre_ms', 'post_ms')} |
                                                                {'steps': d.get('steps', 0)})
    return summarize(groups, runs, notes)


def summarize(groups, runs, notes):
    out = {'runs': runs, 'notes': notes, 'groups': []}
    for (platform, raise_, load), g in sorted(groups.items()):
        holds = sorted(g['holds'])
        us = [h / 1000 for h in holds]
        hist = [0] * (len(BUCKETS_US) + 1)
        for v in us:
            i = 0
            while i < len(BUCKETS_US) and v >= BUCKETS_US[i]:
                i += 1
            hist[i] += 1
        calls = sum(g['status'].values())
        iv = sorted(g['intervals'])
        pp = sorted(g['pre_post'])
        win = g['windows']
        out['groups'].append({
            'platform': platform, 'raise': raise_, 'load': load,
            'hold_us': {'n': len(us), 'p50': percentile(us, 50), 'p90': percentile(us, 90), 'p99': percentile(us, 99),
                        'p99.9': percentile(us, 99.9), 'max': us[-1] if us else 0,
                        'over_1ms': sum(1 for v in us if v >= 1000)},
            'histogram': dict(zip(bucket_names(), hist)),
            'outcomes': dict(sorted(g['status'].items())), 'calls': calls,
            'busy_share': (g['status'].get('busy', 0) / calls) if calls else 0,
            'interval_ms': {'n': len(iv), 'p50': percentile(iv, 50), 'p99': percentile(iv, 99), 'max': iv[-1] if iv else 0},
            'pre_to_post_ms': {'n': len(pp), 'p50': percentile(pp, 50), 'p99': percentile(pp, 99), 'max': pp[-1] if pp else 0},
            'tick_stats': {
                'windows': len(win),
                'step_p50_ms': percentile(sorted(w['step_ms'].get('p50', 0) for w in win), 50),
                'step_p99_ms': max((w['step_ms'].get('p99', 0) for w in win), default=0),
                'step_max_ms': max((w['step_ms'].get('max', 0) for w in win), default=0),
                'interval_p99_ms': max((w['interval_ms'].get('p99', 0) for w in win), default=0),
                'interval_max_ms': max((w['interval_ms'].get('max', 0) for w in win), default=0),
            },
        })
    # late steps: Pre-to-Pre over 1.5 times the idle median of the same platform and raise
    idle = {(g['platform'], g['raise']): g['interval_ms']['p50'] for g in out['groups'] if g['load'] == 'idle'}
    for g in out['groups']:
        base = idle.get((g['platform'], g['raise']))
        src = groups[(g['platform'], g['raise'], g['load'])]['intervals']
        g['late_steps'] = sum(1 for v in src if base and v > 1.5 * base)
    return out


def fmt(v, digits=1):
    return ('%.' + str(digits) + 'f') % v if isinstance(v, float) else str(v)


def markdown(r):
    lines = []
    lines.append('### Hold time of the engine gate (µs, every call that took it)')
    lines.append('')
    lines.append('| Platform | Raise | Load | Calls held | p50 | p90 | p99 | p99.9 | Max | ≥ 1 ms | Busy share |')
    lines.append('|---|---|---|---:|---:|---:|---:|---:|---:|---:|---:|')
    for g in r['groups']:
        h = g['hold_us']
        lines.append('| %s | %s | %s | %d | %s | %s | %s | %s | %s | %d | %.1f%% |' % (
            g['platform'], g['raise'], g['load'], h['n'], fmt(h['p50']), fmt(h['p90']), fmt(h['p99']), fmt(h['p99.9']),
            fmt(h['max']), h['over_1ms'], 100 * g['busy_share']))
    lines.append('')
    lines.append('### Hold-time histogram (calls per bucket)')
    lines.append('')
    heads = ['%s/%s/%s' % (g['platform'], g['raise'], g['load']) for g in r['groups']]
    lines.append('| Bucket | ' + ' | '.join(heads) + ' |')
    lines.append('|---|' + '---:|' * len(heads))
    for b in bucket_names():
        lines.append('| %s | ' % b + ' | '.join(str(g['histogram'][b]) for g in r['groups']) + ' |')
    lines.append('')
    lines.append('### Server step (ms): the test mod\'s own rows')
    lines.append('')
    lines.append('| Platform | Raise | Load | Steps | Pre to Pre p50 | p99 | Max | Late (> 1.5 × idle p50) | Pre to Post p50 | p99 | Max |')
    lines.append('|---|---|---|---:|---:|---:|---:|---:|---:|---:|---:|')
    for g in r['groups']:
        i, p = g['interval_ms'], g['pre_to_post_ms']
        lines.append('| %s | %s | %s | %d | %s | %s | %s | %d | %s | %s | %s |' % (
            g['platform'], g['raise'], g['load'], i['n'], fmt(i['p50'], 2), fmt(i['p99'], 2), fmt(i['max'], 2),
            g['late_steps'], fmt(p['p50'], 2), fmt(p['p99'], 2), fmt(p['max'], 2)))
    lines.append('')
    lines.append('### Server step (ms): tick-stats.jsonl minute windows inside one phase')
    lines.append('')
    lines.append('| Platform | Raise | Load | Windows | Step p50 | Step p99 | Step max | Interval p99 | Interval max |')
    lines.append('|---|---|---|---:|---:|---:|---:|---:|---:|')
    for g in r['groups']:
        t = g['tick_stats']
        lines.append('| %s | %s | %s | %d | %s | %s | %s | %s | %s |' % (
            g['platform'], g['raise'], g['load'], t['windows'], fmt(float(t['step_p50_ms']), 2), fmt(float(t['step_p99_ms']), 2),
            fmt(float(t['step_max_ms']), 2), fmt(float(t['interval_p99_ms']), 2), fmt(float(t['interval_max_ms']), 2)))
    lines.append('')
    lines.append('### Outcomes')
    lines.append('')
    lines.append('| Platform | Raise | Load | Calls | Outcomes |')
    lines.append('|---|---|---|---:|---|')
    for g in r['groups']:
        lines.append('| %s | %s | %s | %d | %s |' % (g['platform'], g['raise'], g['load'], g['calls'],
                                                   ', '.join('%s %d' % kv for kv in g['outcomes'].items())))
    lines.append('')
    lines.append('### Runs')
    lines.append('')
    lines.append('| Zip | Run | Platform | OS | CPU (logical) | Raise | Mode | Component | Pairs | Restored | End |')
    lines.append('|---|---|---|---|---|---|---|---|---:|---|---|')
    for x in r['runs']:
        lines.append('| %s | %s | %s | %s | %s (%s) | %s | %s | %s | %s | %s | %s |' % (
            x['source'], x['run'], x['platform'], x['os'], x['cpu'], x['logical_processors'], x['raise'], x['mode'],
            x['component'], x['pairs'], 'yes' if x['restored'] and x['verified'] else 'no', x['end']))
    for n in r['notes']:
        lines.append('')
        lines.append('Note: ' + n)
    return '\n'.join(lines) + '\n'


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__.split('\n\n')[0])
    ap.add_argument('zips', nargs='+', help='zips (or unpacked folders) from collect.ps1 / collect.sh')
    ap.add_argument('--json', action='store_true', help='print JSON instead of Markdown')
    ap.add_argument('--margin', type=float, default=2.0, help='seconds dropped around each load phase change')
    args = ap.parse_args(argv)
    result = analyze(args.zips, args.margin)
    sys.stdout.write(json.dumps(result, indent=2) + '\n' if args.json else markdown(result))
    return 0


if __name__ == '__main__':
    sys.exit(main())
