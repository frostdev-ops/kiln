#!/usr/bin/env bash
# Dev fallback. The launcher makes this art itself from the user's installed game: Settings → Game art
# → "Extract from game" (or the editor's banner), kiln_manage::gameart, docs/kiln/launcher/gameart.md. This script copies
# the Save Editor's own copy (fonts, item icons, UI textures, made by the editor's author) into
# launcher/assets-local/editor/ (gitignored), where the vite dev server (`bun run dev`, `tauri dev`) finds them.
# These are game files: never commit them, never bundle them.
#
#   tools/launcher/fetch-editor-assets.sh [--from <Save-Editor checkout>] [--install]
#
#   --from     a Save Editor checkout, default $KILN_SAVE_EDITOR (reads <from>/build-assets/game/{fonts,icons,ui})
#   --install  also copy them to the launcher data dir (<data>/editor-assets/) for installed builds
#              (<data> = $KILN_DATA_DIR, else $XDG_DATA_HOME/kiln or ~/.local/share/kiln)
# Without the assets the editor still works and shows placeholders.
set -euo pipefail
here=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
repo=$(cd "$here/../.." && pwd)
from=${KILN_SAVE_EDITOR:-}
install=0
while (($#)); do
  case $1 in
    --from) from=$2; shift 2 ;;
    --install) install=1; shift ;;
    -h|--help) sed -n 2,13p "$0"; exit 0 ;;
    *) echo "unknown argument: $1" >&2; exit 2 ;;
  esac
done
[[ -n $from ]] || { echo "usage: $0 --from <Save-Editor checkout> [--install]" >&2; exit 2; }
src=$from/build-assets/game
for d in fonts icons ui; do
  [[ -d $src/$d ]] || { echo "missing $src/$d (is --from a Save Editor checkout?)" >&2; exit 1; }
done
copy() { # <dest>
  mkdir -p "$1"
  for d in fonts icons ui; do
    rm -rf "${1:?}/$d.tmp"
    cp -r --reflink=auto "$src/$d" "$1/$d.tmp"
    rm -rf "${1:?}/$d"
    mv "$1/$d.tmp" "$1/$d"
  done
  printf '{"format": 1, "kind": "editor", "source": "copy", "from": "%s", "extracted": "%s"}\n' "$src" "$(date -u +%FT%TZ)" > "$1/.source.json"
  echo "$(find "$1" -type f | wc -l) files -> $1"
}
copy "$repo/launcher/assets-local/editor"
if ((install)); then
  data=${KILN_DATA_DIR:-${XDG_DATA_HOME:-$HOME/.local/share}/kiln}
  copy "$data/editor-assets"
fi
