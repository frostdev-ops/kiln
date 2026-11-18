#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""What kiln.world runs while it holds the engine's write gate (a compare-exchange's hold: ecs_write_hold.cpp, and the
entity lookups it calls in ecs_lookup.cpp/.h) must not reach a wait, a lock, an allocation, a log or the KilnApi: the
GameServer thread spins behind a held Scene word, so any of these inside the hold can stall the server. Every loop over
engine data there has a fixed bound (the id probe's, or the component's size, checked before the gate); a `while` or
`for (;;)` is reported for review. Checks the code with comments and string literals stripped. Run by tests/run.sh."""
import re
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
FILES = ['ecs_write_hold.cpp', 'ecs_lookup.cpp', 'ecs_lookup.h']
FORBIDDEN = {
    r'\bSleep(Ex)?\b|\bSwitchToThread\b|\bYieldProcessor\b|\bWaitFor\w*\b|\bWaitOnAddress\b|\bthis_thread\b': 'a wait',
    r'\bEnterCriticalSection\b|\bAcquireSRWLock\w*\b|\bstd::(recursive_|timed_|shared_)?mutex\b|\block_guard\b|'
    r'\bscoped_lock\b|\bunique_lock\b|\bshared_lock\b|\bcondition_variable\b|\bpthread_\w+|\bmu_\b': 'a lock',
    r'\bnew\b|\bmalloc\b|\bcalloc\b|\brealloc\b|\bstd::(string|vector|map|unordered_map|function|deque|list|shared_ptr)\b|'
    r'\bmake_unique\b|\bmake_shared\b|\bHeapAlloc\b|\bVirtualAlloc\b|\bcatalog\s*\(|\bregistration\s*\(': 'an allocation',
    r'\bKW_[EWI]\b|\bKLOG_\w+\b|\bprintf\b|\bfprintf\b|\bsnprintf\b|\bOutputDebugString\w*\b|->\s*log\s*\(|\blogf\b': 'a log',
    r'\bKilnApi\b|\bg_api\b|\bapi\s*->': 'a KilnApi call',
    r'\bthrow\b': 'an exception',
    r'\bwhile\s*\((?!\s*0\s*\))|\bfor\s*\(\s*;\s*;': 'an unbounded loop',
}


def code_of(text):
    text = re.sub(r'/\*.*?\*/', ' ', text, flags=re.S)
    text = re.sub(r'//[^\n]*', ' ', text)
    text = re.sub(r'"(\\.|[^"\\])*"', '""', text)
    return re.sub(r"'(\\.|[^'\\])*'", "''", text)


def main():
    problems = []
    for name in FILES:
        code = code_of((HERE.parent / name).read_text(encoding='utf-8'))
        for number, line in enumerate(code.splitlines(), 1):
            for pattern, what in FORBIDDEN.items():
                if re.search(pattern, line):
                    problems.append('%s:%d: %s: %s' % (name, number, what, line.strip()))
    for problem in problems:
        print(problem, file=sys.stderr)
    print('write hold lint: %d files, %d problems' % (len(FILES), len(problems)))
    return 1 if problems else 0


if __name__ == '__main__':
    sys.exit(main())
