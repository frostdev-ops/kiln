#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""Release/export regressions. All repositories, payloads and tool doubles live in temporary directories."""
import importlib.util
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest

sys.dont_write_bytecode = True

REPO = Path(__file__).resolve().parents[4]
FORK_LAYER = Path(os.environ.get('KILN_SF_REVIEW_LAYER', str(REPO.parent / 'shroudforge-kiln')))

def run(*args, **kw):
    return subprocess.run(args, stdout=subprocess.PIPE, stderr=subprocess.PIPE, **kw)

def git(root, *args, data=None):
    env = dict(os.environ, GIT_AUTHOR_NAME='Fixture', GIT_AUTHOR_EMAIL='fixture@invalid',
               GIT_COMMITTER_NAME='Fixture', GIT_COMMITTER_EMAIL='fixture@invalid',
               GIT_AUTHOR_DATE='1234567890 +0000', GIT_COMMITTER_DATE='1234567890 +0000')
    result = run('git', '-C', str(root), *args, input=data, env=env)
    if result.returncode:
        raise AssertionError(result.stderr.decode())
    return result.stdout.strip().decode()

def fixture(root, files, links=()):
    root.mkdir(parents=True, exist_ok=True)
    git(root, 'init', '-q')
    rows = []
    for name, content in files.items():
        (root / name).write_text(content)
        sha = git(root, 'hash-object', '-w', '--stdin', data=content.encode())
        rows.append(f'100644 blob {sha}\t{name}\0')
    for name, sha in links:
        rows.append(f'160000 commit {sha}\t{name}\0')
    tree = git(root, 'mktree', '-z', '--missing', data=''.join(rows).encode())
    sha = git(root, 'commit-tree', tree, data=b'fixture\n')
    git(root, 'update-ref', 'HEAD', sha)
    return sha

def write(path, text, executable=False):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(text)
    if executable:
        path.chmod(0o755)

class ExportTests(unittest.TestCase):
    def test_recursive_gitlinks_spaces_reproducible_and_missing_source_fails(self):
        with tempfile.TemporaryDirectory(prefix='kiln-source-test-') as tmp:
            root = Path(tmp)
            fork = root / 'fork with spaces'
            inner = fork / 'sub module' / 'nested module'
            inner_sha = fixture(inner, {'COPYING': 'nested GPL source'})
            sub_sha = fixture(inner.parent, {'LICENSE': 'outer source'}, [('nested module', inner_sha)])
            sha = fixture(fork, {'LICENSE': 'root source'}, [('sub module', sub_sha)])
            exporter = REPO / 'tools/release/lib/sf-source.sh'
            one, two, dst = root / 'one.tar.gz', root / 'two.tar.gz', root / 'extract'
            for archive in (one, two):
                result = run(str(exporter), str(fork), sha, str(archive), *([str(dst)] if archive == one else []))
                self.assertEqual(result.returncode, 0, result.stderr.decode())
            self.assertEqual(one.read_bytes(), two.read_bytes())
            self.assertEqual((dst / 'sub module/nested module/COPYING').read_text(), 'nested GPL source')
            pins = json.loads((dst / 'KILN-SOURCE-SUBMODULES.json').read_text())
            self.assertEqual({p['commit'] for p in pins}, {inner_sha, sub_sha})
            shutil.rmtree(inner / '.git')
            three = root / 'must-not-exist.tar.gz'
            self.assertNotEqual(run(str(exporter), str(fork), sha, str(three)).returncode, 0)
            self.assertFalse(three.exists())

    def test_gpl_notices_fail_closed_if_metadata_is_missing(self):
        import unittest.mock as mock
        spec = importlib.util.spec_from_file_location('notices', REPO / 'tools/release/lib/notices.py')
        module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(module)
        with mock.patch.object(module.subprocess, 'check_output', side_effect=subprocess.CalledProcessError(1, 'cargo')):
            with self.assertRaises(RuntimeError):
                module.gpl_plugins(str(REPO))

class StageTests(unittest.TestCase):
    def test_eml_and_sf_build_failures_are_fatal_despite_stale_outputs(self):
        for failure in ('eml', 'sf', 'adapter', 'managed'):
            with self.subTest(failure=failure), tempfile.TemporaryDirectory(prefix='kiln-stage-test-') as tmp:
                root = Path(tmp)
                repo, out, bins = root / 'repo', root / 'build', root / 'bin'
                target = repo / 'tools/package/stage.sh'
                target.parent.mkdir(parents=True)
                shutil.copyfile(REPO / 'tools/package/stage.sh', target)
                write(repo / 'tools/package/fetch-dotnet.sh', '#!/bin/sh\nexit 0\n', True)
                write(repo / 'native/plugins/shroudforge/CMakeLists.txt', '# fixture')
                write(repo / 'managed/services/Kiln.Eml/Kiln.Eml.csproj', '<Project/>')
                write(repo / 'native/build.sh', '#!/bin/sh\nif [ "$TEST_FAIL" = adapter ] && [ "$1" = kiln-shroudforge ]; then exit 11; fi\nexit 0\n', True)
                write(repo / 'gpl/kiln-eml/build.sh', '#!/bin/sh\n[ "$TEST_FAIL" != eml ]\n', True)
                write(repo / 'third_party/shroudforge/kiln/build.sh', '#!/bin/sh\nexit 12\n', True)
                for path in ('cmake/bin/dinput8.dll', 'cmake/bin/kiln-core.dll', 'cmake/bin/kiln-core.pdb',
                             'host/Kiln.Host.dll', 'shroudforge-build/shroudforge/shroudforge-runtime.dll'):
                    write(out / path, 'stale binary')
                (repo / '.cache/dotnet-win-x64/root').mkdir(parents=True)
                write(bins / 'dotnet', '#!/bin/sh\nif [ "$TEST_FAIL" = managed ]; then case "$*" in *Kiln.Eml.csproj*) exit 13;; esac; fi\nexit 0\n', True)
                env = dict(os.environ, PATH=str(bins) + ':' + os.environ['PATH'], KILN_OUT=str(out), TEST_FAIL=failure)
                result = run('bash', str(target), str(root / 'stage'), env=env)
                self.assertNotEqual(result.returncode, 0, result.stdout.decode())
                self.assertIn(b'failed', result.stderr)
                self.assertFalse((root / 'stage/kiln-runtime.json').exists())
                deploy = repo / 'tools/deploy-server.sh'
                shutil.copyfile(REPO / 'tools/deploy-server.sh', deploy)
                server = root / 'fake-server'
                write(server / 'enshrouded_server.exe', 'fixture only - never executed')
                write(bins / 'pgrep', '#!/bin/sh\nexit 1\n', True)
                args = ['--no-build'] if failure == 'sf' else []
                deployed = run('bash', str(deploy), str(server), *args, env=env)
                self.assertNotEqual(deployed.returncode, 0, deployed.stdout.decode())
                self.assertIn(b'incomplete' if failure == 'sf' else b'failed', deployed.stderr)
                self.assertFalse((server / 'kiln').exists())

@unittest.skipUnless((FORK_LAYER / 'kiln/check-contract.py').exists(), 'set KILN_SF_REVIEW_LAYER to the reviewed fork source')
class ForkBuildTests(unittest.TestCase):
    def test_sync_refuses_dirty_or_wrong_branch_before_fetch(self):
        with tempfile.TemporaryDirectory(prefix='kiln-sync-test-') as tmp:
            root = Path(tmp)
            sha = fixture(root, {'README': 'fixture'})
            script = root / 'kiln/sync-upstream.sh'
            script.parent.mkdir()
            shutil.copyfile(FORK_LAYER / 'kiln/sync-upstream.sh', script)
            result = run('bash', str(script), '1.8.0')
            self.assertNotEqual(result.returncode, 0)
            self.assertIn(b'branch kiln', result.stderr)
            git(root, 'update-ref', 'refs/heads/kiln', sha)
            git(root, 'symbolic-ref', 'HEAD', 'refs/heads/kiln')
            result = run('bash', str(script), '1.8.0')
            self.assertNotEqual(result.returncode, 0)
            self.assertIn(b'must be clean', result.stderr)

    def test_contract_rejects_signature_and_export_changes(self):
        with tempfile.TemporaryDirectory(prefix='kiln-contract-test-') as tmp:
            root = Path(tmp)
            script = root / 'kiln/check-contract.py'
            script.parent.mkdir()
            shutil.copyfile(FORK_LAYER / 'kiln/check-contract.py', script)
            declarations = [
                'shroudforge_prepare_startup(game: *const u16) -> bool',
                'shroudforge_create(game: *const u16) -> *mut RuntimeHandle',
                'shroudforge_update(handle: *mut RuntimeHandle, delta_seconds: f64,) -> bool',
                'shroudforge_destroy(handle: *mut RuntimeHandle)',
            ]
            source = '\n'.join('#[unsafe(no_mangle)]\npub unsafe extern "C" fn ' + d + ' {}' for d in declarations)
            target = root / 'modules/entrypoints/loader/embedded.rs'
            write(target, source)
            self.assertEqual(run('python3', str(script)).returncode, 0)
            write(target, source.replace('delta_seconds: f64', 'delta_seconds: f32'))
            self.assertNotEqual(run('python3', str(script)).returncode, 0)
            write(target, source.replace('#[unsafe(no_mangle)]', '', 1))
            self.assertNotEqual(run('python3', str(script)).returncode, 0)

    def test_export_build_stale_ui_last_nonmod_and_failed_build_preserves_output(self):
        with tempfile.TemporaryDirectory(prefix='kiln-fork-build-test-') as tmp:
            root = Path(tmp)
            fork, out, bins = root / 'source with spaces', root / 'out with spaces', root / 'bin'
            shutil.copytree(FORK_LAYER / 'kiln', fork / 'kiln', ignore=shutil.ignore_patterns('out'))
            files = {
                'KILN-SOURCE-COMMIT': 'frostdev-ops/shroudforge ' + 'a' * 40 + '\n',
                'KILN-SOURCE-EPOCH': '1234567890\n', 'KILN-SOURCE-SUBMODULES.json': '[]',
                'LICENSE': 'MIT', 'kfc-parser/Cargo.toml': '# fixture', 'kfc-parser/LICENSE': 'GPL',
                'modules/game-runtime/native/json/LICENSE.MIT': 'MIT', 'modules/hotkeys/SDL/LICENSE.txt': 'zlib',
                'modules/game-runtime/profiles/server.json': '{}', 'mods/a/mod.json': '{}', 'mods/z/readme': 'not a mod',
                'build/ui/modloader/index.html': 'stale', 'modules/modloader/ui/package.json': '{}',
            }
            for path, content in files.items(): write(fork / path, content)
            write(bins / 'npm', '#!/bin/sh\nif [ "$1" = run ]; then printf fresh > ../../../build/ui/modloader/index.html; fi\n', True)
            write(bins / 'cargo', '''#!/bin/sh
[ "${SF_TEST_FAIL:-}" != cargo ] || exit 13
mkdir -p "$CARGO_TARGET_DIR/x86_64-pc-windows-msvc/release"
printf runtime > "$CARGO_TARGET_DIR/x86_64-pc-windows-msvc/release/shroudforge_modloader.dll"
''', True)
            write(bins / 'cmake', '''#!/bin/sh
[ "$1" = --build ] || exit 0
mkdir -p "$2/bin" "$2/_deps/zydis-src/dependencies/zycore"
printf native > "$2/bin/kfc-runtime.dll"
printf MIT > "$2/_deps/zydis-src/LICENSE"
printf MIT > "$2/_deps/zydis-src/dependencies/zycore/LICENSE"
''', True)
            env = dict(os.environ, PATH=str(bins) + ':' + os.environ['PATH'], CARGO_TARGET_DIR=str(out / 'cargo'))
            result = run('bash', str(fork / 'kiln/build.sh'), str(out), env=env)
            self.assertEqual(result.returncode, 0, result.stderr.decode())
            self.assertEqual((fork / 'build/ui/modloader/index.html').read_text(), 'fresh')
            self.assertEqual((out / 'shroudforge/version.json').stat().st_mtime, 1234567890)
            self.assertIn('a' * 40, (out / 'shroudforge/SOURCE.txt').read_text())
            before = (out / 'shroudforge/shroudforge-runtime.dll').read_bytes()
            env['SF_TEST_FAIL'] = 'cargo'
            self.assertNotEqual(run('bash', str(fork / 'kiln/build.sh'), str(out), env=env).returncode, 0)
            self.assertEqual((out / 'shroudforge/shroudforge-runtime.dll').read_bytes(), before)

if __name__ == '__main__':
    unittest.main()
