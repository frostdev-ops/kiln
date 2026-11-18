#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""validate.py's ecsReadLayout checks over a synthetic .text that holds the engine functions they read (built from a
profile's own values), and what each one does when a field or a function is wrong. With KILN_ENGINE_BINARIES set to a
folder holding read-only copies (server-26430253/enshrouded_server.exe, client-26430625/enshrouded.exe) the real
copies are checked too; never a game install.

  python3 -I tools/engine/test_validate.py
"""
import copy
import json
import os
from pathlib import Path
import struct
import subprocess
import sys
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parent))
import validate as v  # noqa: E402

ROOT = Path(__file__).resolve().parents[2]
PROFILES = {p.parent.name: json.loads(p.read_text()) for p in sorted((ROOT / 'sigdb/engine').glob('*/*.json'))}
TEXT_VA = 0x1000
PAD = b'\xcc' * 64


def d32(x):
    return struct.pack('<I', x)


def hexb(s):
    return bytes.fromhex(s.replace('??', '00'))


class Text:
    """A .text with the functions validate.py looks for, laid out from `ecs` (map at 0x148, as the engine has it)."""

    def __init__(self, ecs, tracker_rva=None, hash_k=v.ENTITY_HASH, uuid_offsets=(0, 0x10), skip=(), twice=()):
        self.blob = bytearray(PAD)
        self.at = {}
        m = 0x148
        storage = ecs['executionEntityManagerOffset']
        find_rva = TEXT_VA + 0x8000  # the lookup goes at a fixed place, after everything else

        def rel(here):
            return struct.pack('<i', find_rva - (TEXT_VA + here + 4))

        def put(name, parts):
            for _ in range(2 if name in twice else 1):
                if name in skip:
                    return
                self.at[name] = TEXT_VA + len(self.blob)
                start = len(self.blob)
                for p in parts:
                    self.blob += p(start) if callable(p) else p
                self.blob += PAD

        def call_then(prefix, suffix):
            # prefix bytes, then E8 rel32 to the lookup, then suffix
            def build(start):
                pos = len(self.blob)
                return prefix + b'\xe8' + rel(pos + len(prefix) + 1) + suffix
            return build

        put('GetComponent', [hexb('48 89 5C 24 08 48 89 74 24 10 57 48 83 EC 20 48 8D BA') + d32(storage) + hexb('44 89 C3 90') +
                             hexb('48 8B 0F 41 8B D9 48 81 C1') + d32(0x88) + hexb('E8 00 00 00 00')])
        put('GetEntityTransform', [hexb('48 89 5C 24 08 57 48 83 EC 20 48 8D B9') + d32(storage) + hexb('89 54 24 38'),
                                   call_then(hexb('48 8D 8F') + d32(m) + hexb('48 8D 54 24 38'),
                                             hexb('48 3B 87') + d32(ecs['entityManagerCount']) + hexb('73 10 48 8B 97') +
                                             d32(ecs['entityManagerTable']))])
        put('GetComponentByType', [call_then(hexb('48 89 5C 24 08 48 89 74 24 10 44 89 4C 24 20 57 48 83 EC 20 48 8D BA') + d32(m) +
                                             hexb('48 8B D9 48 8B CF 48 8D 54 24 48 49 8B F0'),
                                             hexb('48 3B 47') + bytes([ecs['entityManagerCount'] - m]) + hexb('0F 83 94 00 00 00 48 8B 57') +
                                             bytes([ecs['entityManagerTable'] - m]) +
                                             hexb('4C 8D 04 C2 4D 85 C0 0F 84 83 00 00 00 4D 8B 00 4D 85 C0 74 7B 48 85 F6 74 76 44 0F B7 0E 4D 8B 50') +
                                             bytes([ecs['entityLayout']]) + hexb('41 8B C1 48 C1 E8 06 41 0F B6 C9 49 8B 14 C2 48 D3 EA') +
                                             hexb('43 0F B7 94 4A') + d32(ecs['componentStrides']) + hexb('41 8B 40') + bytes([ecs['entityRow']]) +
                                             hexb('43 0F B7 8C 4A') + d32(ecs['componentOffsets']) + hexb('48 0F AF C2 48 03 C8 48 8B C3 49 03 48') +
                                             bytes([ecs['entityStorage']]))])
        for i, off in enumerate(uuid_offsets):
            tail = hexb('0F 10 00') if off == 0 else hexb('0F 10 40') + bytes([off])
            put('TemplateReader%d' % i, [call_then(hexb('48 8D 8F') + d32(m),
                                                   hexb('48 3B 87') + d32(ecs['entityManagerCount']) + hexb('73 31 48 8B 8F') +
                                                   d32(ecs['entityManagerTable']) + hexb('48 8D 04 C1 48 85 C0 74 21 48 8B 00 48 85 C0 74 19 48 8B 40') +
                                                   bytes([ecs['entityDefinitionPointer']]) + tail)])
        put('GameServerWorldStore', [hexb('48 89 87') + d32(ecs['mainExecutionOwnerOffset']) + hexb('48 89 4C 24 30 4C 8D 87')])
        put('GameServerWorldRead', [hexb('49 8B 8E') + d32(ecs['mainExecutionOwnerOffset']) +
                                    hexb('E8 00 00 00 00 49 8B CE E8 00 00 00 00 44 38 BD 00 00 00 00 74 00 49 8B CE E8')])
        self.tracker = TEXT_VA + len(self.blob) if tracker_rva is None else tracker_rva
        put('ChangeTracker', [hexb('48 89 5C 24 08 48 89 6C 24 10 48 89 74 24 18 48 89 7C 24 20 41 56 48 83 EC 30 4D 8B 50') +
                              bytes([ecs['entityStorage']]) + hexb('49 8B F0 41 0F B7 E9 4C 8B F2 45 8B 48') + bytes([ecs['entityRow']]) +
                              b'\x90' * 32 + hexb('44 8B 46') + bytes([ecs['entityId']]) + hexb('66 89 44 24 20 E8 00 00 00 00')])
        # the lookup
        self.blob += b'\xcc' * (find_rva - TEXT_VA - len(self.blob))
        self.blob += (hexb('48 89 5C 24 08 48 89 7C 24 10 83 79 54 00 4C 8B C9 4C 8B 59') + bytes([ecs['entityManagerCount'] - m]) +
                      hexb('74 75 8B 1A 8B C3 49 8B 79') + bytes([ecs['entityManagerOccupancy'] - m]) +
                      hexb('C1 E8 10 33 C3 69 D0') + d32(hash_k) + hexb('8B C2 C1 E8 10 33 C2 69 C0') + d32(hash_k) +
                      hexb('8B C8 48 C1 E8 10 48 33 C1 49 8D 4B FF 48 23 C1 48 8B D0 0F B6 C8 48 C1 EA 06 4C 8B 04 D7 49 D3 E8 41 F6 C0 01 74 2E 4D 8B 51') +
                      bytes([ecs['entityManagerKeys'] - m]) + hexb('41 39 1C 82 74 27') + PAD)


def run(ecs, text, write=None):
    w = {'changeTrackerRva': text.tracker} if write is None else write
    return v.ecs_read_checks(ecs, w, bytes(text.blob), TEXT_VA)


def failures(checks):
    return [line for ok, line in checks if not ok]


class EcsReadChecks(unittest.TestCase):
    def test_the_ported_profiles_pass_on_their_own_code(self):
        for side, p in PROFILES.items():
            ecs = p['ecsReadLayout']
            checks, shown = run(ecs, Text(ecs))
            self.assertEqual(failures(checks), [], side)
            self.assertEqual(set(ecs) - shown - set(v.RUNTIME_ONLY), set(), side + ': every field shown or listed')
            for field in ('executionEntityManagerOffset', 'entityManagerOwnerOffset', 'entityManagerCount', 'entityManagerTable',
                          'entityManagerOccupancy', 'entityManagerKeys', 'entityId', 'entityLayout', 'entityStorage', 'entityRow',
                          'componentBits', 'componentOffsets', 'componentStrides', 'entityDefinitionPointer',
                          'mainExecutionOwnerOffset'):
                self.assertIn(field, shown, side)

    def test_a_wrong_field_fails_the_check_that_names_it(self):
        ecs = PROFILES['server']['ecsReadLayout']
        text = Text(ecs)  # the code as it is
        for field in ('executionEntityManagerOffset', 'entityManagerCount', 'entityManagerTable', 'entityManagerOccupancy',
                      'entityManagerKeys', 'entityId', 'entityLayout', 'entityStorage', 'entityRow', 'componentOffsets',
                      'componentStrides', 'entityDefinitionPointer', 'mainExecutionOwnerOffset', 'componentBits',
                      'entityManagerOwnerOffset', 'entityDefinitionUuidOffset'):
            wrong = copy.deepcopy(ecs)
            wrong[field] = ecs[field] + 8
            failed = failures(run(wrong, text)[0])
            self.assertTrue(any(field in line for line in failed), '%s: %s' % (field, failed))

    def test_a_function_missing_or_found_twice_fails(self):
        ecs = PROFILES['client']['ecsReadLayout']
        for skip in ('GetComponent', 'GetEntityTransform', 'GetComponentByType'):
            failed = failures(run(ecs, Text(ecs, skip=(skip,)))[0])
            self.assertTrue(any(skip in line and 'found once' in line for line in failed), skip)
        failed = failures(run(ecs, Text(ecs, twice=('GetComponent',)))[0])
        self.assertTrue(any('GetComponent found once' in line and '2 matches' in line for line in failed), failed)
        failed = failures(run(ecs, Text(ecs, skip=('TemplateReader0', 'TemplateReader1')))[0])
        self.assertTrue(any('template reader' in line for line in failed), failed)

    def test_the_lookup_hash_is_kilns(self):
        ecs = PROFILES['server']['ecsReadLayout']
        failed = failures(run(ecs, Text(ecs, hash_k=0x01000193))[0])
        self.assertTrue(any('id hash' in line for line in failed), failed)

    def test_the_uuid_offset_must_be_one_the_engine_copies(self):
        ecs = copy.deepcopy(PROFILES['server']['ecsReadLayout'])
        ecs['entityDefinitionUuidOffset'] = 0x10
        self.assertEqual(failures(run(ecs, Text(PROFILES['server']['ecsReadLayout']))[0]), [])
        ecs['entityDefinitionUuidOffset'] = 0x20
        self.assertTrue(failures(run(ecs, Text(PROFILES['server']['ecsReadLayout']))[0]))

    def test_the_tracker_is_looked_at_where_the_profile_says(self):
        ecs = PROFILES['server']['ecsReadLayout']
        text = Text(ecs)
        failed = failures(run(ecs, text, {'changeTrackerRva': text.tracker + 0x200})[0])
        self.assertTrue(any('entityId' in line for line in failed), failed)


@unittest.skipUnless(os.environ.get('KILN_ENGINE_BINARIES'), 'KILN_ENGINE_BINARIES not set (read-only exe copies)')
class RealCopies(unittest.TestCase):
    def test_both_exact_builds(self):
        base = Path(os.environ['KILN_ENGINE_BINARIES'])
        for profile, exe in (('server/26430253.json', 'server-26430253/enshrouded_server.exe'),
                             ('client/26430625.json', 'client-26430625/enshrouded.exe')):
            done = subprocess.run([sys.executable, '-I', str(Path(v.__file__)), str(ROOT / 'sigdb/engine' / profile), str(base / exe)],
                                  capture_output=True, text=True)
            self.assertEqual(done.returncode, 0, done.stdout[-2000:])
            self.assertIn(': 0 failure(s)', done.stdout)


if __name__ == '__main__':
    unittest.main()
