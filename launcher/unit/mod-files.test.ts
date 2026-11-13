// The "Add mods" file dialog offers every package the Library imports (player report of 13 Nov 2026: a .rar or .7z
// was not even shown). The archive list is kiln_manage::library::archive_kind's own, read from the crate's source, so
// a format the crate learns and the dialog lacks fails here.
import { expect, test } from "bun:test";
import { readFileSync } from "node:fs";
import { join } from "node:path";
import { MOD_FILE_EXTENSIONS, MOD_FILE_FILTERS } from "../src/shell/modFiles";

const library = readFileSync(join(import.meta.dir, "..", "..", "crates", "kiln-manage", "src", "library.rs"), "utf8");

test("the dialog offers every archive kind the Library unpacks, and DLLs", () => {
  const body = /fn archive_kind\(path: &Path\)[^{]*\{([\s\S]*?)\n\}/.exec(library)?.[1] ?? "";
  const archives = [...body.matchAll(/"([a-z0-9]+)" => Some\(ArchiveKind::/g)].map((m) => m[1]);
  expect(archives).toEqual(["zip", "7z", "rar"]);
  for (const ext of [...archives, "dll"]) expect(MOD_FILE_EXTENSIONS).toContain(ext);
  expect(MOD_FILE_FILTERS).toEqual([{ name: "Mods", extensions: MOD_FILE_EXTENSIONS }]);
});
