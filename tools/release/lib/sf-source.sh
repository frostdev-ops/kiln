#!/usr/bin/env bash
# SPDX-License-Identifier: MIT
# Export the pinned fork and every recursively pinned gitlink, without modifying/fetching any checkout.
# tools/release/lib/sf-source.sh <fork checkout> <commit> <out .tar.gz> [empty extract dir]
set -euo pipefail
fork=${1:?fork checkout}
commit=${2:?commit}
tarball=$(realpath -m "${3:?out tarball}")
extract=${4:-}
[[ $commit =~ ^[0-9a-fA-F]{40}$ ]] || { echo "sf-source: expected a full commit id" >&2; exit 2; }
commit=${commit,,}
tmp=$(mktemp -d)
trap 'rm -rf -- "$tmp"' EXIT
name=shroudforge-kiln-${commit:0:12}
dst=$tmp/$name
mkdir -p "$dst"
python3 - "$fork" "$commit" "$dst" <<'PY'
import json, os, pathlib, subprocess, sys
fork, commit, dest = sys.argv[1:]
submodules = []
def git(repo, *args):
    return subprocess.check_output(['git', '-C', repo, *args])
def export(repo, rev, dst, prefix=''):
    if git(repo, 'rev-parse', '--verify', rev + '^{commit}').decode().strip() != rev:
        raise RuntimeError('commit mismatch: ' + rev)
    pathlib.Path(dst).mkdir(parents=True, exist_ok=True)
    archive = subprocess.Popen(['git', '-C', repo, 'archive', '--format=tar', rev], stdout=subprocess.PIPE)
    try:
        subprocess.run(['tar', '-xf', '-', '-C', dst], stdin=archive.stdout, check=True)
    finally:
        archive.stdout.close()
        rc = archive.wait()
    if rc:
        raise RuntimeError('git archive failed: ' + rev)
    for row in git(repo, 'ls-tree', '-rz', rev).split(b'\0'):
        if not row:
            continue
        meta, raw = row.split(b'\t', 1)
        mode, kind, sha = meta.split()
        if mode != b'160000':
            continue
        path = os.fsdecode(raw)
        parts = pathlib.PurePosixPath(path).parts
        if not parts or any(p in ('..', '.') for p in parts) or path.startswith('/'):
            raise RuntimeError('unsafe gitlink path')
        sub = os.path.join(repo, path)
        if not os.path.exists(os.path.join(sub, '.git')):
            raise RuntimeError('uninitialized submodule: ' + sub)
        pin = sha.decode()
        submodules.append({'path': prefix + path, 'commit': pin})
        export(sub, pin, os.path.join(dst, path), prefix + path + '/')
export(fork, commit, dest)
epoch = git(fork, 'log', '-1', '--format=%ct', commit).decode().strip()
pathlib.Path(dest, 'KILN-SOURCE-COMMIT').write_text('frostdev-ops/shroudforge ' + commit + '\n')
pathlib.Path(dest, 'KILN-SOURCE-EPOCH').write_text(epoch + '\n')
pathlib.Path(dest, 'KILN-SOURCE-SUBMODULES.json').write_text(json.dumps(sorted(submodules, key=lambda s: s['path']), indent=2) + '\n')
PY
epoch=$(<"$dst/KILN-SOURCE-EPOCH")
mkdir -p "$(dirname "$tarball")"
# gzip -n also removes the compressor's timestamp/name. Publish only a fully written archive.
archive_tmp=$(mktemp "${tarball}.XXXXXX")
trap 'rm -rf -- "$tmp"; rm -f -- "$archive_tmp"' EXIT
tar --sort=name --owner=0 --group=0 --numeric-owner --mode='u=rwX,go=rX' --mtime="@$epoch" -C "$tmp" -cf - "$name" | gzip -n > "$archive_tmp"
mv -- "$archive_tmp" "$tarball"
if [[ -n $extract ]]; then
  mkdir -p "$extract"
  [[ -z $(ls -A -- "$extract") ]] || { echo "sf-source: extract directory must be empty" >&2; exit 1; }
  tar -xzf "$tarball" -C "$extract" --strip-components=1
fi
printf 'sf-source: %s\n' "$tarball"
