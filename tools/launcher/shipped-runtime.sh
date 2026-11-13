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

# shipped_python_resources [DIR|none] RESOURCES_JSON: prints RESOURCES_JSON with the Python the Windows launcher ships
# for the cook tools added as python/ (the launcher runs python/python.exe from its resources). Empty: the pinned
# embeddable package (tools/package/fetch-python.sh). none: unchanged. The package is flat (python.org's embeddable
# zip has no folders); one with a folder in it is refused rather than shipped in part.
shipped_python_resources() {
  local dir=${1:-} resources=$2
  if [[ $dir == none ]]; then
    echo "$resources"
    return
  fi
  if [[ -z $dir ]]; then
    dir=$("$repo/tools/package/fetch-python.sh" --print-root) || return 1
  fi
  [[ -f $dir/python.exe ]] || { echo "no python.exe in $dir" >&2; return 1; }
  dir=$(realpath "$dir")
  if [[ -n $(find "$dir" -mindepth 1 -type d -print -quit) ]]; then
    echo "the Python in $dir has folders: only a flat embeddable package is shipped" >&2
    return 1
  fi
  echo "shipping Python: $dir ($(du -sh "$dir" | cut -f1))" >&2
  python3 -c 'import json, sys; d = json.loads(sys.argv[1]); d[sys.argv[2] + "/*"] = "python/"; print(json.dumps(d))' "$resources" "$dir"
}
