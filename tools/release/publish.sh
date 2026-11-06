#!/usr/bin/env bash
# Publishes a release that release.sh built and github-draft.sh drafted. This is the step that reaches users: run it
# by hand. It asks before each remote change (--yes skips the questions).
#   tools/release/publish.sh RELEASE_DIR --host SSH_HOST [--repo OWNER/NAME] [--no-dev-channel] [--yes]
# --host: the frostdev.io web server as ssh knows it (or KILN_RELEASE_HOST); the files go to $KILN_RELEASE_DIR there,
#   default /opt/frostdev-downloads/kiln (the folder the site's nginx serves as /kiln/updates/ and /kiln/downloads/).
# --nexus: afterwards upload it to the Kiln page on Nexus Mods (tools/release/nexus.py --publish; it asks first).
# --no-dev-channel: a stable release normally also becomes the dev channel's newest build; skip that for a hotfix
#   release when a newer dev build is out.
# 1. Checks the folder (SHA256SUMS, the updater signatures against the launcher's built-in key in tauri.conf.json).
# 2. frostdev.io: uploads the folder to <host>:/opt/frostdev-downloads/kiln/downloads/<version>/, then the manifests
#    (updates/frostdev.io/<channel>/latest.json) to /opt/frostdev-downloads/kiln/updates/<channel>/, each through a
#    temporary name, so a manifest never points at files that are not there yet. Then reads them back over https.
# 3. GitHub: publishes the draft v<version> (a pre-release for dev builds; "latest" for a stable one) and points the
#    dev channel at it: the `updates-dev` pre-release holds the dev channel's latest.json (created when missing).
# The nginx locations for /kiln/updates/ and /kiln/downloads/ must be live on the host first (releasing.md).
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
repo_dir=$(cd "$here/../.." && pwd)
dir="" gh_repo=frostdev-ops/kiln host=${KILN_RELEASE_HOST:-} yes=0 dev_channel=1 nexus=0
while (($#)); do
  case $1 in
    --repo) gh_repo=$2; shift 2 ;;
    --host) host=$2; shift 2 ;;
    --yes) yes=1; shift ;;
    --no-dev-channel) dev_channel=0; shift ;;
    --nexus) nexus=1; shift ;;
    -*) echo "unknown option $1" >&2; exit 2 ;;
    *) dir=$(realpath "$1"); shift ;;
  esac
done
[[ -d $dir && -n $host ]] || { echo "usage: $0 RELEASE_DIR --host SSH_HOST [--repo OWNER/NAME] [--no-dev-channel] [--yes]" >&2; exit 2; }
ask() { ((yes)) && return 0; read -r -p "$1 [y/N] " a; [[ $a == y || $a == Y ]]; }
version=$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["version"])' "$dir/updates/github/latest.json")
case $version in *-*) channel=dev ;; *) channel=stable ;; esac
tag=v$version
remote=${KILN_RELEASE_DIR:-/opt/frostdev-downloads/kiln}

echo "== checking $dir ($version, $channel)"
python3 "$here/lib/release.py" sums "$dir" --check
pub=$(mktemp)
trap 'rm -f "$pub"' EXIT
python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["plugins"]["updater"]["pubkey"])' \
  "$repo_dir/launcher/src-tauri/tauri.conf.json" >"$pub"
mapfile -t signed < <(find "$dir/linux" "$dir/windows" -name '*.sig' | sed 's/\.sig$//')
python3 "$here/lib/release.py" verify --pubkey "$pub" --version "$version" "${signed[@]}"
gh release view "$tag" --repo "$gh_repo" --json isDraft -q .isDraft | grep -qx true \
  || { echo "no draft $tag on $gh_repo (tools/release/github-draft.sh first)" >&2; exit 1; }

echo "== frostdev.io ($host:$remote)"
if ask "Upload $version to $host:$remote/downloads/$version and switch the $channel manifest?"; then
  ssh "$host" mkdir -p "$remote/downloads" "$remote/updates/stable" "$remote/updates/dev"
  rsync -a --chmod=D755,F644 --exclude updates/ "$dir/" "$host:$remote/downloads/$version/"
  for f in "$dir"/updates/frostdev.io/*/latest.json; do
    ch=$(basename "$(dirname "$f")")
    [[ $ch == dev && $channel == stable && $dev_channel == 0 ]] && continue
    rsync -a --chmod=F644 "$f" "$host:$remote/updates/$ch/.latest.json.new"
    ssh "$host" mv -f "$remote/updates/$ch/.latest.json.new" "$remote/updates/$ch/latest.json"
    got=$(curl -fsS "https://frostdev.io/kiln/updates/$ch/latest.json" | sha256sum | cut -d' ' -f1)
    [[ $got == "$(sha256sum <"$f" | cut -d' ' -f1)" ]] && echo "https://frostdev.io/kiln/updates/$ch/latest.json is $version" \
      || echo "WARNING: https://frostdev.io/kiln/updates/$ch/latest.json does not match yet (cache?)" >&2
  done
  curl -fsSI "https://frostdev.io/kiln/downloads/$version/SHA256SUMS" >/dev/null && echo "downloads reachable"
fi

echo "== GitHub ($gh_repo)"
if ask "Publish the draft $tag and point the GitHub $channel channel at it?"; then
  if [[ $channel == dev ]]; then gh release edit "$tag" --repo "$gh_repo" --draft=false --prerelease
  else gh release edit "$tag" --repo "$gh_repo" --draft=false --latest; fi
  # the dev channel's fixed pre-release (GitHub's "latest" skips pre-releases); a stable release also serves dev
  if ! gh release view updates-dev --repo "$gh_repo" >/dev/null 2>&1; then
    gh release create updates-dev --repo "$gh_repo" --prerelease --target "$(git -C "$repo_dir" rev-parse HEAD)" \
      --title "Kiln Launcher dev channel" \
      --notes "The dev channel's update manifest (latest.json), for the launcher's self-update. Not a release: the builds are in the v* releases."
  fi
  if [[ $channel == dev || $dev_channel == 1 ]]; then
    gh release upload updates-dev --repo "$gh_repo" --clobber "$dir/updates/github/latest.json"
    echo "published $tag; dev channel -> $version"
  else
    echo "published $tag; the dev channel stays where it was"
  fi
fi

if ((nexus)); then
  echo "== Nexus Mods"
  python3 -I "$here/nexus.py" "$dir"
  if ask "Upload these files to Nexus Mods and post the changelog?"; then
    python3 -I "$here/nexus.py" "$dir" --publish
  fi
fi
