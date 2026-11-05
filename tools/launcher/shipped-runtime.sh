# Sourced by build-linux.sh and build-windows.sh: the Kiln runtime the launcher ships.
# shipped_runtime_resources [ZIP|none]: prints the JSON for Tauri's bundle.resources.
#   ZIP: that bundle. none: no runtime (Runtime then asks for a zip). Empty: builds one from this tree.
# At first start the launcher copies it into <data>/runtime/ (commands/runtime.rs, import_shipped).
shipped_runtime_resources() {
  local zip=${1:-}
  if [[ $zip == none ]]; then
    echo '{}'
    return
  fi
  if [[ -z $zip ]]; then
    local dist=$KILN_OUT/runtime-dist
    rm -rf "$dist"
    mkdir -p "$dist"
    "$repo/tools/package/package.sh" --out "$dist" >&2
    zip=$(ls -t "$dist"/kiln-runtime-*.zip | head -1)
  fi
  [[ -f $zip ]] || { echo "no runtime bundle: $zip" >&2; return 1; }
  zip=$(realpath "$zip")
  echo "shipping runtime: $(basename "$zip") ($(du -h "$zip" | cut -f1))" >&2
  printf '{"%s":"runtime/kiln-runtime.zip"}' "$zip"
}
