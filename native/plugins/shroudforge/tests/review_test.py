#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""Release/export regressions. All repositories, payloads and tool doubles live in temporary directories."""
import importlib.util
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import tempfile
import unittest

sys.dont_write_bytecode = True

REPO = Path(__file__).resolve().parents[4]
# The host key feed of shroudforge-runtime.dll (the fork's kiln/abi/input_host.h): name, Rust arguments, Rust result.
FEED_EXPORTS = [
    ('shroudforge_input_host_version', '', 'u32'),
    ('shroudforge_input_host_begin', '', 'u32'),
    ('shroudforge_input_host_raw_key', 'make_code: u32, flags: u32, vkey: u32', None),
    ('shroudforge_input_host_window_key', 'wparam: usize, lparam: isize', None),
    ('shroudforge_input_host_mouse_button', 'button: u32, down: u32', None),
    ('shroudforge_input_host_reset', '', None),
    ('shroudforge_input_host_end', '', None),
]
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
    def test_main_source_export_is_independent_of_umask(self):
        # Exercise the actual archive step without running the release's builds, signers or publishers.
        script = (REPO / 'tools/release/release.sh').read_text()
        start = script.index('tar --sort=name --owner=0')
        command = script[start:script.index('\n[[ $(sed', start)]
        with tempfile.TemporaryDirectory(prefix='kiln-main-source-') as tmp:
            archives = []
            for index, mask in enumerate((0o022, 0o077)):
                work = Path(tmp) / f'work {index}'
                (work / 'source').mkdir(parents=True)
                (work / 'src').mkdir(mode=0o755 & ~mask)
                write(work / 'src/LICENSE', 'source')
                (work / 'src/LICENSE').chmod(0o644 & ~mask)
                env = dict(os.environ, work=str(work), version='1.0.0', SOURCE_DATE_EPOCH='1234567890')
                result = run('bash', '-euc', command, env=env, umask=mask)
                self.assertEqual(result.returncode, 0, result.stderr.decode())
                archives.append((work / 'source/kiln-1.0.0-src.tar.gz').read_bytes())
            self.assertEqual(*archives)

    def test_export_is_independent_of_umask(self):
        with tempfile.TemporaryDirectory(prefix='kiln-source-umask-') as tmp:
            root = Path(tmp)
            fork = root / 'fork'
            sha = fixture(fork, {'LICENSE': 'source'})
            exporter = REPO / 'tools/release/lib/sf-source.sh'
            archives = [root / 'one.tar.gz', root / 'two.tar.gz']
            for archive, mask in zip(archives, (0o022, 0o077)):
                result = run(str(exporter), str(fork), sha, str(archive), umask=mask)
                self.assertEqual(result.returncode, 0, result.stderr.decode())
            self.assertEqual(archives[0].read_bytes(), archives[1].read_bytes())

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

def stage_fixture(root):
    """Complete inert prerequisites, so each test fails at its intended component, not an earlier missing helper."""
    repo, out, bins = root / 'repo', root / 'build', root / 'bin'
    target = repo / 'tools/package/stage.sh'
    target.parent.mkdir(parents=True)
    shutil.copyfile(REPO / 'tools/package/stage.sh', target)
    for script in ('tools/package/fetch-dotnet.sh', 'native/build.sh', 'gpl/kiln-eml/build.sh',
                   'third_party/shroudforge/kiln/build.sh', 'native/plugins/enshwick/tools/build-runtime.sh'):
        write(repo / script, '#!/bin/sh\nexit 0\n', True)
    write(repo / 'native/plugins/shroudforge/CMakeLists.txt', '# fixture')
    write(repo / 'managed/services/Kiln.Eml/Kiln.Eml.csproj', '<Project/>')
    write(bins / 'dotnet', '#!/bin/sh\nexit 0\n', True)
    for path in ('cmake/bin/dinput8.dll', 'cmake/bin/kiln-core.dll', 'cmake/bin/kiln-core.pdb',
                 'cmake/bin/plugins/kiln-eml.dll', 'cmake/bin/plugins/kiln-shroudforge.dll',
                 'host/Kiln.Host.dll', 'host/Kiln.Eml.dll', 'host/Kiln.Api.dll', 'host/Kiln.Game.dll',
                 'mods/kiln-admin/Kiln.Admin.dll', 'mods/kiln-admin/kiln.mod.json',
                 'mods/hello/Hello.dll', 'mods/hello/kiln.mod.json', 'cmake/bin/kiln-native-hello.dll',
                 'enshwick-runtime/enshwick.dll', 'enshwick-runtime/LICENSE', 'enshwick-runtime/enshwick-version.txt'):
        write(out / path, 'fixture only - never executed')
    for path in ('shroudforge-runtime.dll', 'kfc-runtime.dll', 'NOTICE.txt', 'SOURCE.txt', 'version.json',
                 'LICENSES/kfc-parser-GPL-3.0.txt', 'LICENSES/ShroudForge-MIT.txt'):
        write(out / 'shroudforge-build/shroudforge' / path, 'fixture only - never executed')
    for path in ('LICENSE', 'NOTICE.txt'):
        write(repo / 'gpl/kiln-eml' / path, 'fixture')
    (out / 'cmake/bin/services').mkdir()
    (repo / '.cache/dotnet-win-x64/root').mkdir(parents=True)
    return repo, out, bins, target


class StageTests(unittest.TestCase):
    def test_deploy_refuses_a_component_missing_its_mit_license(self):
        with tempfile.TemporaryDirectory(prefix='kiln-deploy-license-') as tmp:
            root = Path(tmp)
            target = root / 'repo/tools/deploy-server.sh'
            target.parent.mkdir(parents=True)
            shutil.copyfile(REPO / 'tools/deploy-server.sh', target)
            server, out, bins = root / 'fake-server', root / 'build', root / 'bin'
            write(server / 'enshrouded_server.exe', 'fixture only - never executed')
            for path in ('shroudforge-runtime.dll', 'kfc-runtime.dll', 'NOTICE.txt', 'SOURCE.txt', 'version.json',
                         'LICENSES/kfc-parser-GPL-3.0.txt'):
                write(out / 'shroudforge-build/shroudforge' / path, 'fixture')
            write(bins / 'pgrep', '#!/bin/sh\nexit 1\n', True)
            env = dict(os.environ, PATH=str(bins) + ':' + os.environ['PATH'], KILN_OUT=str(out))
            result = run('bash', str(target), str(server), '--no-build', env=env)
            self.assertNotEqual(result.returncode, 0)
            self.assertIn(b'ShroudForge-MIT.txt', result.stderr)
            self.assertFalse((server / 'kiln').exists())

    def test_successful_build_commands_must_produce_required_components(self):
        for path in ('cmake/bin/plugins/kiln-eml.dll', 'cmake/bin/plugins/kiln-shroudforge.dll',
                     'host/Kiln.Eml.dll', 'shroudforge-build/shroudforge/shroudforge-runtime.dll',
                     'enshwick-runtime/enshwick.dll'):
            with self.subTest(missing=Path(path).name), tempfile.TemporaryDirectory(prefix='kiln-stage-missing-') as tmp:
                root = Path(tmp)
                repo, out, bins, target = stage_fixture(root)
                (out / path).unlink()
                env = dict(os.environ, PATH=str(bins) + ':' + os.environ['PATH'], KILN_OUT=str(out))
                result = run('bash', str(target), str(root / 'stage'), env=env)
                self.assertNotEqual(result.returncode, 0, result.stdout.decode())
                self.assertIn(Path(path).name.encode(), result.stderr)
                self.assertFalse((root / 'stage/kiln-runtime.json').exists())

    def test_component_build_failures_are_fatal_despite_stale_outputs(self):
        expected = {'eml': b'plugin kiln-eml failed', 'sf': b'ShroudForge runtime build failed',
                    'adapter': b'native plugin kiln-shroudforge failed', 'managed': b'managed service Kiln.Eml failed',
                    'enshwick': b'Enshwick loader (enshwick.dll for kiln-enshwick) failed'}
        for failure, reason in expected.items():
            with self.subTest(failure=failure), tempfile.TemporaryDirectory(prefix='kiln-stage-test-') as tmp:
                root = Path(tmp)
                repo, out, bins, target = stage_fixture(root)
                write(repo / 'native/build.sh', '#!/bin/sh\nif [ "$TEST_FAIL" = adapter ] && [ "$1" = kiln-shroudforge ]; then exit 11; fi\nexit 0\n', True)
                write(repo / 'gpl/kiln-eml/build.sh', '#!/bin/sh\n[ "$TEST_FAIL" != eml ]\n', True)
                write(repo / 'third_party/shroudforge/kiln/build.sh', '#!/bin/sh\n[ "$TEST_FAIL" != sf ]\n', True)
                write(repo / 'native/plugins/enshwick/tools/build-runtime.sh', '#!/bin/sh\n[ "$TEST_FAIL" != enshwick ]\n', True)
                write(bins / 'dotnet', '#!/bin/sh\nif [ "$TEST_FAIL" = managed ]; then case "$*" in *Kiln.Eml.csproj*) exit 13;; esac; fi\nexit 0\n', True)
                env = dict(os.environ, PATH=str(bins) + ':' + os.environ['PATH'], KILN_OUT=str(out), TEST_FAIL=failure)
                result = run('bash', str(target), str(root / 'stage'), env=env)
                self.assertNotEqual(result.returncode, 0, result.stdout.decode())
                self.assertIn(reason, result.stderr)
                self.assertFalse((root / 'stage/kiln-runtime.json').exists())
                if failure == 'enshwick':
                    continue  # deploy-server consumes a prebuilt Enshwick loader; stage owns its build gate.
                deploy = repo / 'tools/deploy-server.sh'
                shutil.copyfile(REPO / 'tools/deploy-server.sh', deploy)
                server = root / 'fake-server'
                write(server / 'enshrouded_server.exe', 'fixture only - never executed')
                write(bins / 'pgrep', '#!/bin/sh\nexit 1\n', True)
                if failure == 'sf':
                    (out / 'shroudforge-build/shroudforge/kfc-runtime.dll').unlink()
                args = ['--no-build'] if failure == 'sf' else []
                deployed = run('bash', str(deploy), str(server), *args, env=env)
                self.assertNotEqual(deployed.returncode, 0, deployed.stdout.decode())
                deploy_reason = {'sf': b'incomplete ShroudForge component: kfc-runtime.dll',
                                 'eml': b'kiln-eml build failed', 'adapter': b'ShroudForge adapter build failed',
                                 'managed': b'Kiln.Eml build failed'}[failure]
                self.assertIn(deploy_reason, deployed.stderr)
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
            # The host key feed (since the 112d939 pin): its Rust definitions must match the header kiln-shroudforge
            # reads. A fork layer from before the feed checks the bootstrap only.
            header_file = FORK_LAYER / 'kiln/abi/input_host.h'
            host_input = root / 'modules/hotkeys/host_input.rs'
            if header_file.exists():
                header = header_file.read_text()
                write(root / 'kiln/abi/input_host.h', header)
                version = re.search(r'#define SHROUDFORGE_INPUT_HOST_VERSION (\d+)u', header).group(1)
                rust = f'const INPUT_HOST_VERSION: u32 = {version};\n' + '\n'.join(
                    '#[unsafe(no_mangle)]\npub extern "C" fn ' + name + '(' + args + ')' + (' -> ' + result if result else '') + ' {}'
                    for name, args, result in FEED_EXPORTS)
                write(host_input, rust)
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
            write(target, source)
            if not header_file.exists():
                return
            write(host_input, rust.replace('lparam: isize', 'lparam: i32'))
            self.assertNotEqual(run('python3', str(script)).returncode, 0)
            write(host_input, rust.replace(f'u32 = {version};', f'u32 = {int(version) + 1};'))
            self.assertNotEqual(run('python3', str(script)).returncode, 0)
            write(host_input, rust)
            self.assertEqual(run('python3', str(script)).returncode, 0)
            write(root / 'kiln/abi/input_host.h', header.replace('void shroudforge_input_host_reset(void);', ''))
            self.assertNotEqual(run('python3', str(script)).returncode, 0)

    @unittest.skipUnless((FORK_LAYER / 'kiln/abi/input_host.h').exists(), 'a fork layer without the host key feed')
    def test_adapter_looks_up_the_feed_the_fork_declares(self):
        # kiln-shroudforge finds the seven exports by name and knows one version (src/key_feed.cpp): both must be what
        # the pinned fork's kiln/abi/input_host.h declares, or the feed silently never starts.
        header = (FORK_LAYER / 'kiln/abi/input_host.h').read_text()
        adapter = (REPO / 'native/plugins/shroudforge/src/key_feed.cpp').read_text()
        declared = re.findall(r'\b(shroudforge_input_host_\w+)\(', header)
        looked_up = re.findall(r'GetProcAddress\(runtime, "(shroudforge_input_host_\w+)"\)', adapter)
        self.assertEqual(sorted(declared), sorted(looked_up))
        self.assertEqual(sorted(declared), sorted(name for name, _, _ in FEED_EXPORTS))
        version = re.search(r'#define SHROUDFORGE_INPUT_HOST_VERSION (\d+)u', header).group(1)
        self.assertIn(f'constexpr uint32_t kFeedVersion = {version};', adapter)

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
            # the export table check of build.sh (since the 112d939 pin); SF_TEST_FAIL=export drops one export
            exports = ['shroudforge_prepare_startup', 'shroudforge_create', 'shroudforge_update', 'shroudforge_destroy'] + \
                [name for name, _, _ in FEED_EXPORTS]
            write(bins / 'llvm-readobj', '#!/bin/sh\n' + ''.join(
                f'[ "${{SF_TEST_FAIL:-}}" = export ] && [ {name} = shroudforge_input_host_end ] || printf "  Name: {name}\\n"\n'
                for name in exports), True)
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
            if 'llvm-readobj' in (fork / 'kiln/build.sh').read_text():
                env['SF_TEST_FAIL'] = 'export'
                failed = run('bash', str(fork / 'kiln/build.sh'), str(out), env=env)
                self.assertNotEqual(failed.returncode, 0)
                self.assertIn(b'does not export shroudforge_input_host_end', failed.stderr)
                self.assertEqual((out / 'shroudforge/shroudforge-runtime.dll').read_bytes(), before)

if __name__ == '__main__':
    unittest.main()
