#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
# Offline recipe guards. python -I test_prepare.py PRISTINE_SOURCE SCRATCH_ROOT
import importlib.util
import pathlib
import shutil
import sys
import tempfile
import unittest

# Keep recipe-test bytecode out of the source tree even with python -I.
sys.dont_write_bytecode = True
HERE = pathlib.Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location('bridge_prepare', HERE / 'prepare.py')
recipe = importlib.util.module_from_spec(spec)
spec.loader.exec_module(recipe)
SOURCE, SCRATCH = map(pathlib.Path, sys.argv[1:3])

class PrepareTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix='recipe-', dir=SCRATCH)
        self.root = pathlib.Path(self.temp.name)
        self.src = self.root / 'source'
        target = self.src / 'mod/client/src'
        target.mkdir(parents=True)
        shutil.copy2(SOURCE / 'LICENSE', self.src / 'LICENSE')
        for name in recipe.HASHES:
            shutil.copy2(SOURCE / 'mod/client/src' / name, target / name)
        self.dst = self.root / 'private'
    def tearDown(self):
        self.temp.cleanup()
    def test_private_copy_retains_license_and_stock_native_tab_path(self):
        recipe.prepare(self.src, self.dst)
        self.assertEqual((self.src/'LICENSE').read_bytes(), (self.dst/'LICENSE').read_bytes())
        self.assertEqual((self.src/'mod/client/src/native.rs').read_bytes(), (self.dst/'mod/client/src/native.rs').read_bytes())
        self.assertIn('mod bridge_adapter;', (self.dst/'mod/client/src/lib.rs').read_text())
        self.assertIn('&& !crate::bridge_adapter::presenting()', (self.dst/'mod/client/src/ui.rs').read_text())
        self.assertIn('if crate::bridge_adapter::presenting() {', (self.dst/'mod/client/src/input.rs').read_text())
        self.assertIn('shroudlink_client_bridge_v1', (self.dst/'mod/client/src/bridge_adapter.rs').read_text())
    def test_existing_destination_refused_without_touching_it(self):
        self.dst.mkdir(); sentinel = self.dst/'keep'; sentinel.write_text('keep')
        with self.assertRaises(ValueError): recipe.prepare(self.src,self.dst)
        self.assertEqual(sentinel.read_text(),'keep')
    def test_nested_destination_refused(self):
        with self.assertRaises(ValueError): recipe.prepare(self.src,self.src/'output')
        self.assertFalse((self.src/'output').exists())
    def test_unknown_input_rejected_before_any_copy(self):
        (self.src/'mod/client/src/native.rs').write_bytes(b'unsupported count-independent change')
        with self.assertRaises(ValueError): recipe.prepare(self.src,self.dst)
        self.assertFalse(self.dst.exists())
    def test_changed_patch_anchor_rejected_before_any_copy(self):
        import hashlib
        path = self.src/'mod/client/src/lib.rs'
        path.write_bytes(path.read_bytes().replace(b'mod native;',b'mod renamed;'))
        old = recipe.HASHES['lib.rs']
        try:
            recipe.HASHES['lib.rs']=hashlib.sha256(path.read_bytes()).hexdigest()
            with self.assertRaises(AssertionError): recipe.prepare(self.src,self.dst)
            self.assertFalse(self.dst.exists())
        finally: recipe.HASHES['lib.rs']=old
    def test_mit_notice_required(self):
        (self.src/'LICENSE').write_text('Unknown license')
        with self.assertRaises(ValueError): recipe.prepare(self.src,self.dst)
        self.assertFalse(self.dst.exists())

if __name__ == '__main__':
    unittest.main(argv=[sys.argv[0]])
