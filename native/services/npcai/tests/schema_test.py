#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""AI/rule schema and signature guards; no game or compiler needed."""
import json
import unittest
from pathlib import Path

from jsonschema import Draft202012Validator

ROOT = Path(__file__).resolve().parents[4]
DOC = json.loads((ROOT / 'schema/kiln-blocks.schema.json').read_text())


def validator(kind):
    return Draft202012Validator({'$ref': f'#/$defs/{kind}', '$defs': DOC['$defs']})


class SchemaTests(unittest.TestCase):
    def test_actions(self):
        v = validator('action')
        for valid in [ {'flee': True}, {'on': 'self', 'apply': 'chilled'}, {'summon': 'imp'},
                       {'summon': {'creature': 'imp', 'count': 16, 'seconds': 86400, 'side': 'own'}},
                       {'play': '11111111-1111-1111-1111-111111111111', 'wait': True}, {'type': 'PauseAction'}, {'type': 'SetImmuneAction', 'immune': True} ]:
            self.assertTrue(v.is_valid(valid), valid)
        for invalid in [ {'flee': False}, {'summon': None}, {'summon': {'creature': ' '}},
                         {'summon': {'creature': 'x', 'count': 1.5}}, {'summon': {'creature': 'x', 'count': 17}},
                         {'summon': {'creature': 'x', 'seconds': -1}}, {'heal': .1, 'summon': 'x'},
                         {'apply': None}, {'heal': 2}, {'pause': [None, 1]}, {} ]:
            self.assertFalse(v.is_valid(invalid), invalid)

    def test_conditions(self):
        v = validator('condition')
        for valid in [{'inCombat': False}, {'state': ['Combat']}, {'enemiesNear': [2, 10]},
                      {'alliesNear': {'within': 10}}, {'summonsBelow': 0}, {'healthBelow': .5}, {'not': {'inCombat': True}}, {'type': 'RandomChanceConstrain', 'chance': .5}]:
            self.assertTrue(v.is_valid(valid), valid)
        for invalid in [{'summonsBelow': 1.5}, {'enemiesNear': [-1, 10]}, {'state': '999'},
                        {'chance': .1, 'hasTarget': True}, {'state': []}, {'hitWithin': None}, {'not': None}]:
            self.assertFalse(v.is_valid(invalid), invalid)

    def test_scoped_sample_and_test_blocks(self):
        validator = Draft202012Validator(DOC)
        for directory in ('mods/tests/npcai-test/blocks', 'mods/samples/CreatureAi/blocks'):
            for path in (ROOT / directory).glob('*.json'):
                errors = list(validator.iter_errors(json.loads(path.read_text())))
                self.assertEqual([], errors, str(path))

    def test_service_patterns_describe_all_five_entry_hooks(self):
        expected = {f'npcai/Faction{x}' for x in ('Hostile', 'HostileValue', 'Friend', 'FriendOpt', 'FriendValue')}
        for side in ('server', 'client'):
            doc = json.loads((ROOT / f'sigdb/symbols/{side}.d/npcai.json').read_text())
            self.assertEqual(expected, set(doc['symbols']))
            for symbol in doc['symbols'].values():
                for strategy in symbol['find']:
                    self.assertNotIn('offset', strategy)  # callback arguments are entry registers
                    self.assertGreater(len(strategy['pattern'].split()), 24)


if __name__ == '__main__':
    unittest.main()
