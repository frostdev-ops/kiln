#!/usr/bin/env bash
# Optional Windows Authenticode signing with Azure Artifact Signing (formerly Trusted Signing) through jsign.
#   tools/release/authenticode.sh available        exit 0 when the credentials file exists
#   tools/release/authenticode.sh sign FILE...     sign and timestamp each PE file (the Tauri signCommand calls this)
#   tools/release/authenticode.sh verify FILE...   check each file's signature (osslsigncode verify)
# Credentials: $KILN_AUTHENTICODE_ENV, default ~/.config/kiln-release/authenticode.env (keys: releasing.md,
# "Authenticode"). Nothing here prints a secret: the access token reaches jsign through the environment.
# Needs: jsign (the `jsign` command, or JSIGN=<path to jsign.jar> in the env file, with java) and, to verify,
# osslsigncode. Timestamps: http://timestamp.acs.microsoft.com (RFC 3161).
set -euo pipefail
env_file=${KILN_AUTHENTICODE_ENV:-$HOME/.config/kiln-release/authenticode.env}
cmd=${1:-}
shift || true

case $cmd in
  available) [[ -f $env_file ]]; exit ;;
  sign|verify) ;;
  *) echo "usage: $0 available | sign FILE... | verify FILE..." >&2; exit 2 ;;
esac

if [[ $cmd == verify ]]; then
  command -v osslsigncode >/dev/null || { echo "authenticode: osslsigncode is not installed (needed to verify)" >&2; exit 1; }
  fail=0
  for f in "$@"; do
    if osslsigncode verify -in "$f" >/dev/null 2>&1; then echo "authenticode OK: $(basename "$f")"
    else echo "authenticode FAILED: $f" >&2; fail=1; fi
  done
  exit $fail
fi

[[ -f $env_file ]] || { echo "authenticode: no $env_file" >&2; exit 1; }
set -a
# shellcheck disable=SC1090
source "$env_file"
set +a
for k in AZURE_TENANT_ID AZURE_CLIENT_ID AZURE_CLIENT_SECRET AZURE_SIGNING_ENDPOINT AZURE_SIGNING_ACCOUNT AZURE_SIGNING_PROFILE; do
  [[ -n ${!k:-} ]] || { echo "authenticode: $k is not set in $env_file" >&2; exit 1; }
done
if [[ -n ${JSIGN:-} ]]; then jsign=(java -jar "$JSIGN"); else
  command -v jsign >/dev/null || { echo "authenticode: jsign is not installed (or set JSIGN=<jsign.jar> in $env_file)" >&2; exit 1; }
  jsign=(jsign)
fi

# an access token for the signing service (Entra client credentials), reused for an hour across calls of one release
cache=${XDG_RUNTIME_DIR:-/tmp}/kiln-authenticode-$(id -u).token
if [[ ! -s $cache || $(($(date +%s) - $(stat -c %Y "$cache"))) -gt 3000 ]]; then
  umask 077
  printf '%s' "$AZURE_CLIENT_SECRET" | curl -fsS "https://login.microsoftonline.com/$AZURE_TENANT_ID/oauth2/v2.0/token" \
    --data-urlencode "client_id=$AZURE_CLIENT_ID" --data-urlencode "client_secret@-" \
    --data-urlencode "scope=https://codesigning.azure.net/.default" --data-urlencode "grant_type=client_credentials" \
    | python3 -c 'import json,sys; print(json.load(sys.stdin)["access_token"])' >"$cache.tmp"
  mv "$cache.tmp" "$cache"
fi
KILN_SIGNING_TOKEN=$(cat "$cache")
export KILN_SIGNING_TOKEN
endpoint=${AZURE_SIGNING_ENDPOINT#https://}
endpoint=${endpoint%/}
for f in "$@"; do
  "${jsign[@]}" --storetype TRUSTEDSIGNING --keystore "$endpoint" --storepass env:KILN_SIGNING_TOKEN \
    --alias "$AZURE_SIGNING_ACCOUNT/$AZURE_SIGNING_PROFILE" --alg SHA-256 \
    --tsaurl http://timestamp.acs.microsoft.com --tsmode RFC3161 --replace "$f" >/dev/null
  echo "authenticode: signed $(basename "$f")"
done
