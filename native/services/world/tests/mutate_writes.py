#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""Mutation run of the compare-exchange writes: each check of the protocol is broken alone in a copy of the sources and
ecs_writes_test (ASan build) must fail. Usage: mutate_writes.py <scratch dir> [text in a mutant's description]
(needs g++; the whole run takes about half an hour).
Prints one line per mutant and exits 1 if any survived."""
import shutil
import subprocess
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
WORLD = HERE.parent
ENGINE = WORLD.parent / 'engine'
ROOT = WORLD.parent.parent  # native/

MUTANTS = [
    ('ecs_write_hold.cpp', 'if (!scope.validate()) {', 'if (false) {', 'the gate is not validated first'),
    ('ecs_write_hold.cpp', 'if (!scope.component_supported(in.index)) {', 'if (false) {', 'the live tracking bits are not read'),
    ('ecs_write_hold.cpp', 'if (std::memcmp(in.before, in.expected, n) != 0) {', 'if (false) {', 'no compare'),
    ('ecs_write_hold.cpp', '(!in.mask || in.mask[i]) ? in.desired[i]', '(true) ? in.desired[i]', 'the mask is ignored'),
    ('ecs_write_hold.cpp', 's != KILN_ECS_OK || !same_place(at, again)) {', 'false) {', 'no lookup before the write'),
    ('ecs_write_hold.cpp', 'in.retirements->load(std::memory_order_acquire) != in.retirements_seen', 'false',
     'no retirement check in the hold'),
    ('ecs_write_hold.cpp', 'const bool proven = written && in.reader->copy(at.address, in.verified, n) && std::memcmp(in.verified, in.merged, n) == 0;',
     'const bool proven = written;', 'no readback'),
    ('ecs_write_hold.cpp', 'if (in.reader->copy(at.address, in.verified, n) && std::memcmp(in.verified, in.before, n) == 0) {',
     'if (true) {', 'the rollback is not proven'),
    ('ecs_write_hold.cpp', 'if (scope.validate() && find_again(in, again, &why) == KILN_ECS_OK && same_place(at, again)) {',
     'if (true) {', 'no identity check after the write'),
    ('ecs_writes.cpp', 'if (latch_.latched(target))', 'if (false)', 'the latch is not consulted'),
    ('ecs_writes.cpp', 'if (ref->reload_epoch != owner) return', 'if (false) return', "another owner's ref is accepted"),
    ('ecs_writes.cpp', 'if (retired(owner)) return', 'if (false) return', 'a retired owner is accepted'),
    ('ecs_writes.cpp', 'if (!ring_.reserve()) return', 'if ((ring_.reserve(), false)) return', 'a full event queue is ignored'),
    ('ecs_writes.cpp', '''    } else {
        ring_.unreserve();
    }''', '''    } else {
    }''', 'an unused reservation is kept'),
    ('ecs_writes.cpp', 'if (bytes != pol.component_size) return', 'if (false) return', 'the size is not checked'),
    ('ecs_writes.cpp', 'if (!s->alive.load(std::memory_order_seq_cst)) {', 'if (false) {', 'unsubscribed callbacks still run'),
    ('ecs_writes.cpp', 'while (gone->calls.load(std::memory_order_seq_cst) != 0) std::this_thread::yield();', '',
     'unsubscribe does not wait for a running callback'),
    ('ecs_writes.cpp', 'const bool ok = src_.guarded ? src_.guarded(&deliver, &d, &code) : (deliver(&d), true);',
     'const bool ok = (deliver(&d), true);', 'subscribers run without containment'),
    ('ecs_writes.cpp', 'out.status == KILN_ECS_OK && (out.flags & KILN_MUTATION_WRITTEN) ? merged : before',
     'merged', 'confirmed holds the merged bytes on a conflict'),
    ('ecs_writes.cpp', 'if (info.status != KILN_ECS_OK) return finish(info.status, KILN_ECS_WRITE_WHY_REF);', '',
     'the ref is not validated before the gate'),
    ('ecs_writes.cpp', 'if (!latch_.latch(target) && !widened_logged_.exchange(true)) {',
     'if (false) {', 'an uncertain write is not latched'),
    ('ecs_write_policy.cpp', 'case KILN_TYPE_F64: case KILN_TYPE_ENUM:', 'case KILN_TYPE_F64: case KILN_TYPE_ENUM: case KILN_TYPE_DS_ARRAY:',
     'a dynamic array counts as plain'),
    ('ecs_write_policy.cpp', 'if (f.tracked_a || f.tracked_b)', 'if (false)', 'the policy ignores the tracking bits'),
    ('ecs_write_policy.cpp', 'if (f.registered_size > f.budget)\n', 'if (false)\n', 'the budget is not applied'),
    ('ecs_lookup.cpp', 'std::min<uint64_t>(t.count, probe_bound)', 't.count', 'the hold\'s probe is not bounded'),
]


FLAGS = ['g++', '-std=c++20', '-O1', '-g', '-pthread', '-w', '-fsanitize=address,undefined',
         '-fno-sanitize-recover=undefined', '-DKILN_ENGINE_GATE_TEST_POINTS', '-DKILN_WORLD_HOLD_TEST_POINTS',
         '-I%s' % ENGINE, '-I%s' % (ENGINE / 'include'), '-I%s' % (ENGINE / 'tests'), '-I%s' % (ROOT / 'core/abi'),
         '-I%s' % (ROOT / 'third_party')]
WORLD_SOURCES = ['tests/ecs_writes_test.cpp', 'ecs_writes.cpp', 'ecs_write_hold.cpp', 'ecs_write_policy.cpp',
                 'ecs_engine.cpp', 'ecs_lookup.cpp', 'ecs_reads.cpp']
ENGINE_SOURCES = ['engine.cpp', 'identity.cpp', 'resolve.cpp', 'gate.cpp', 'tests/host/memory_host.cpp']


def compile_one(source, obj):
    return subprocess.run(FLAGS + ['-c', str(source), '-o', str(obj)], capture_output=True).returncode == 0


def main():
    if len(sys.argv) not in (2, 3):
        print(__doc__)
        return 2
    scratch = Path(sys.argv[1]).resolve()
    objects = scratch / 'obj'
    objects.mkdir(parents=True, exist_ok=True)
    # Everything compiled once from the real sources; a mutant recompiles only its own file.
    base = {}
    for rel in WORLD_SOURCES:
        obj = objects / (rel.replace('/', '_') + '.o')
        if not compile_one(WORLD / rel, obj):
            print('the unmutated sources do not build: %s' % rel)
            return 2
        base[rel] = obj
    engine_objects = []
    for rel in ENGINE_SOURCES:
        obj = objects / ('engine_' + rel.replace('/', '_') + '.o')
        if not compile_one(ENGINE / rel, obj):
            print('the engine sources do not build: %s' % rel)
            return 2
        engine_objects.append(obj)
    survived = 0
    ran = 0
    only = sys.argv[2] if len(sys.argv) == 3 else None
    for file, old, new, what in MUTANTS:
        if only and only not in what:
            continue
        work = scratch / 'world'
        if work.exists():
            shutil.rmtree(work)
        shutil.copytree(WORLD, work, ignore=shutil.ignore_patterns('*.o', 'build'))
        path = work / file
        text = path.read_text(encoding='utf-8')
        if text.count(old) != 1:
            print('MISSING %s: %s' % (file, what))
            survived += 1
            continue
        path.write_text(text.replace(old, new), encoding='utf-8')
        mutated = objects / 'mutant.o'
        exe = scratch / 'mutant'
        objs = [mutated if rel == file else base[rel] for rel in WORLD_SOURCES] + engine_objects
        if not compile_one(path, mutated) or subprocess.run(FLAGS + [str(o) for o in objs] + ['-o', str(exe)],
                                                            capture_output=True).returncode != 0:
            print('NO BUILD %s: %s' % (file, what))
            survived += 1
            continue
        ran += 1
        try:
            r = subprocess.run([str(exe), str(ROOT.parent / 'sigdb/types')], capture_output=True, timeout=600)
            killed = r.returncode != 0
        except subprocess.TimeoutExpired:
            killed = True
        print('%s %s: %s' % ('killed  ' if killed else 'SURVIVED', file, what), flush=True)
        survived += 0 if killed else 1
    shutil.rmtree(scratch / 'world', ignore_errors=True)
    shutil.rmtree(objects, ignore_errors=True)
    (scratch / 'mutant').unlink(missing_ok=True)
    print('mutation run: %d mutants, %d survived' % (ran, survived))
    return 1 if survived else 0


if __name__ == '__main__':
    sys.exit(main())
