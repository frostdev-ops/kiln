# SPDX-License-Identifier: MIT
# Shared path guards: run before mkdir, removal, or a Docker bind mount. Never read the secrets themselves.
safe_dir() {
  local path secret lexical
  lexical=$(realpath -ms -- "$1") || return
  path=$(realpath -m -- "$1") || return
  for secret in "$path" "$lexical"; do
    case $secret in *:*|*,*|*$'\n'*) echo "release: unsupported directory name" >&2; return 1 ;; esac
  done
  for secret in "$HOME/.config/kiln-release" "$HOME/.config/kiln-release/"{updater.key,updater.key.password,authenticode.env,nexus.env}; do
    secret=$(realpath -m -- "$secret") || return
    if [[ $path == / || $path == "$secret" || $path == "$secret/"* || $secret == "$path/"* ]]; then
      echo "release: directory overlaps the release secrets: $path" >&2
      return 1
    fi
  done
}
disjoint_dirs() {
  local a b
  a=$(realpath -m -- "$1") || return
  b=$(realpath -m -- "$2") || return
  if [[ $a == "$b" || $a == "$b/"* || $b == "$a/"* ]]; then
    echo "release: directories must not overlap: $a; $b" >&2
    return 1
  fi
}
