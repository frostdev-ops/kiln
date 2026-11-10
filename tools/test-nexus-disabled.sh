#!/usr/bin/env bash
# The tests of a build with Nexus Mods OFF: they prove the build never reaches Nexus (no provider, token or keyring
# access, no handler, no files) and that it still removes Kiln's own leftovers (the dev.19 personal API key, the dev.19
# nxm registration).
#
# KILN_NEXUS_ENABLED is read when the code is COMPILED (option_env!), so a plain `cargo test` builds Nexus ON and these
# tests return at once or are not the ones that matter. This script builds the two test binaries with
# KILN_NEXUS_ENABLED=0 in their own target folders (the normal build keeps its cache) and runs every test whose name
# starts with `disabled_nexus`. It fails if the tripwire tests did not run, so they cannot pass by running nothing.
#
#   tools/test-nexus-disabled.sh            # both crates
#   CARGO_TARGET_DIR=/path tools/test-nexus-disabled.sh   # the folders go under it (default: <repo>/target)
#
# The other nexus tests (`cargo test -p kiln-manage --features full nexus`) are for the build with Nexus ON; do not run
# them with the flag off. release.sh runs this script as a check when the release is built with KILN_NEXUS_ENABLED=0
# (tools/release/checks.sh).
set -euo pipefail
repo=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
base=${CARGO_TARGET_DIR:-$repo/target}
fail=0

run() { # label, crate folder, target subfolder, must-run test names (space separated), cargo test args...
  local label=$1 dir=$2 target=$3 must=$4 out name; shift 4
  echo "== $label (KILN_NEXUS_ENABLED=0)"
  if ! out=$(cd "$repo/$dir" && KILN_NEXUS_ENABLED=0 CARGO_TARGET_DIR="$base/$target" cargo test "$@" disabled_nexus 2>&1); then
    echo "$out" | tail -60
    echo "FAIL $label"
    fail=1
    return
  fi
  echo "$out" | grep -E '^test .*disabled_nexus|^test result' || true
  for name in $must; do
    if ! grep -q "^test .*::$name \.\.\. ok$" <<<"$out"; then
      echo "FAIL $label: $name did not run"
      fail=1
    fi
  done
}

run "kiln-manage" . nexus-off-manage \
  "disabled_nexus_never_reaches_provider_credentials_or_handler_and_preserves_files disabled_nexus_retires_the_dev19_personal_key_file_and_keyring_entry disabled_nexus_gives_back_kilns_own_nxm_registration_and_only_that disabled_nexus_personal_key_keyring_is_checked_until_one_lookup_succeeds disabled_nexus_never_restores_or_forwards_to_kiln_itself" \
  -p kiln-manage --features full --lib
run "launcher" launcher/src-tauri nexus-off-launcher \
  "disabled_nexus_commands_and_direct_links_do_not_initialize_auth_or_inbox disabled_nexus_link_goes_to_the_manager_before_kiln_or_shows_the_message disabled_nexus_start_removes_the_personal_key_and_gives_the_links_back_without_printing_either" \
  --lib
if ((fail)); then echo "FAIL: the disabled-Nexus tests"; exit 1; fi
echo "PASS: the disabled-Nexus tests ran and passed"
