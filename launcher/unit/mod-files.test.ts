// The "Add mods" file dialog offers every package the Library imports (player report of 13 Nov 2026: a .rar or .7z
// was not even shown). The archive list is kiln_manage::library::archive_kind's own, read from the crate's source, so
// a format the crate learns and the dialog lacks fails here.
import { expect, test } from "bun:test";
import { readFileSync } from "node:fs";
import { join } from "node:path";
import { MOD_FILE_EXTENSIONS, modFileFilters } from "../src/shell/modFiles";

const library = readFileSync(join(import.meta.dir, "..", "..", "crates", "kiln-manage", "src", "library.rs"), "utf8");

test("the dialog offers every archive kind the Library unpacks, and DLLs", () => {
  const body = /fn archive_kind\(path: &Path\)[^{]*\{([\s\S]*?)\n\}/.exec(library)?.[1] ?? "";
  const archives = [...body.matchAll(/"([a-z0-9]+)" => Some\(ArchiveKind::/g)].map((m) => m[1]);
  expect(archives).toEqual(["zip", "7z", "rar"]);
  for (const ext of [...archives, "dll"]) expect(MOD_FILE_EXTENSIONS).toContain(ext);
  expect(modFileFilters(true)).toEqual([{ name: "Mods", extensions: MOD_FILE_EXTENSIONS }]);
});

// review finding 5: GTK and the desktop portal match `*.7z` case-sensitively, so `MOD.7Z` stayed hidden on Linux
test("on Linux the dialog also lists the upper-case patterns; Windows and macOS get each extension once", () => {
  const linux = modFileFilters(false)[0].extensions;
  for (const e of ["zip", "7z", "rar", "dll"]) expect(linux).toEqual(expect.arrayContaining([e, e.toUpperCase()]));
  expect(new Set(linux).size).toBe(linux.length);
  const win = modFileFilters(true)[0].extensions;
  expect(win.map((e) => e.toLowerCase())).toEqual(["zip", "7z", "rar", "dll"]);
});
