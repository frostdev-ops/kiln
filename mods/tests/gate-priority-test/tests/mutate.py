#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""Mutation run of gate-priority-test (python3 -I tests/mutate.py <scratch dir>): each check of kit.cpp broken alone
must make one of the host scenarios (tests/kit_test.cpp) fail. The harness and kiln.world's code are compiled once; each
mutant recompiles kit.cpp and runs the scenarios that should catch it."""
import shutil
import subprocess
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
MOD = HERE.parent
ROOT = MOD.parent.parent.parent
ENGINE = ROOT / 'native/services/engine'
WORLD = ROOT / 'native/services/world'
INC = ['-I' + str(p) for p in (MOD, HERE, ENGINE, ENGINE / 'include', ENGINE / 'tests', WORLD, WORLD / 'tests',
                               ROOT / 'native/core/abi', ROOT / 'native/third_party')]
FLAGS = ['g++', '-std=c++20', '-O1', '-g0', '-pthread', '-w']
OTHERS = [HERE / 'kit_test.cpp', WORLD / 'ecs_engine.cpp', WORLD / 'ecs_lookup.cpp', WORLD / 'ecs_reads.cpp',
          WORLD / 'ecs_writes.cpp', WORLD / 'ecs_write_hold.cpp', WORLD / 'ecs_write_policy.cpp', ENGINE / 'resolve.cpp',
          ENGINE / 'gate.cpp', ENGINE / 'tests/host/memory_host.cpp']

# (name, text in kit.cpp, its replacement, scenarios of which at least one must fail)
MUTANTS = [
    ('padding: overlapping members accepted', 'if (m.offset < end) return "members overlap (a union?)";', 'if (false) {}',
     ['padding']),
    ('padding: a gap alignment does not explain accepted',
     'if (!a || round_up(end, a) != m.offset) return "a gap the alignment does not explain";', 'if (false) {}',
     ['padding', 'write']),
    ('padding: a fieldless base of more than one byte taken for an empty one', ' || f.info.type_size > 1', '',
     ['padding']),
    ('padding: a tail the alignment does not explain accepted',
     'if (!in.alignment || round_up(end, in.alignment) != in.type_size) return "a tail the alignment does not explain";',
     'if (false) {}', ['padding']),
    ('the restore skipped', 'if (flipped) {\n            if (!attempt("restore"', 'if (false) {\n            if (!attempt("restore"',
     ['write']),
    ('the write policy not asked', 'p.status != KILN_ECS_OK) {\n            if (wanted) why_named', 'false) {\n            if (wanted) why_named',
     ['tracked']),
    ('another world accepted', 'if (g->config.world.empty() || guid != g->config.world) {',
     'if (g->config.world.empty()) {', ['other-world']),
    ('the raise not recorded', '"; raise=" + raise +', '"; raise=on" +', ['raise-off']),
    ('the world stopping ignored', 'if (g->world_stopping.load()) return finish("the server world stopped", run);', '',
     ['stop-warmup']),
    ('a conflict\'s current bytes not taken up', '(r.status == KILN_ECS_OK || r.status == KILN_ECS_CONFLICT)) cur = confirmed;',
     '(r.status == KILN_ECS_OK)) cur = confirmed;', ['write']),
    ('a named byte not checked for padding', 'if (!std::binary_search(pad.bytes.begin(), pad.bytes.end(), at))', 'if (false)',
     ['named-field']),
    ('restored judged by the replies, not by a read', 'restored = verified = snap[offset] == original;',
     'restored = !flipped; verified = restored && snap[offset] == original;', ['uncertain-change', 'uncertain-restore']),
]


def run(cmd, **kw):
    return subprocess.run(cmd, capture_output=True, text=True, **kw)


def main():
    if len(sys.argv) != 2:
        print(__doc__)
        return 2
    scratch = Path(sys.argv[1])
    shutil.rmtree(scratch, ignore_errors=True)
    scratch.mkdir(parents=True)
    objs = []
    for src in OTHERS:
        o = scratch / (src.stem + '.o')
        p = run(FLAGS + INC + ['-c', str(src), '-o', str(o)])
        if p.returncode:
            print(p.stderr[-2000:])
            return 1
        objs.append(str(o))
    text = (MOD / 'kit.cpp').read_text()
    caught = 0
    for name, old, new, scenarios in MUTANTS:
        if text.count(old) != 1:
            print('MUTANT NOT APPLIED (text not found once): %s' % name)
            return 1
        mutant = scratch / 'kit.cpp'
        mutant.write_text(text.replace(old, new))
        exe = scratch / 'kit_test'
        p = run(FLAGS + INC + [str(mutant)] + objs + ['-o', str(exe)])
        if p.returncode:
            print('MUTANT DOES NOT COMPILE: %s\n%s' % (name, p.stderr[-1500:]))
            return 1
        failed = [s for s in scenarios if run([str(exe), s, str(ROOT / 'sigdb/types')], timeout=120).returncode]
        if failed:
            caught += 1
            print('caught   %s (by %s)' % (name, ', '.join(failed)))
        else:
            print('SURVIVED %s' % name)
    print('gate-priority-test mutants: %d of %d caught' % (caught, len(MUTANTS)))
    return 0 if caught == len(MUTANTS) else 1


if __name__ == '__main__':
    sys.exit(main())
