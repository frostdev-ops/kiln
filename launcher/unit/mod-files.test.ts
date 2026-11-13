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

// review findings 5 and B3: Linux's dialogs (GTK, the desktop portal) match `*.<ext>` case-sensitively, so MOD.7Z and
// then NightOwl.Zip or mod.Rar stayed hidden; a class per letter takes every case (GTK 3 and 4 checked by hand)
test("on Linux each extension is one glob that takes every letter case; Windows and macOS get the plain extensions", () => {
  const linux = modFileFilters(false)[0].extensions;
  expect(linux).toEqual(["[zZ][iI][pP]", "7[zZ]", "[rR][aA][rR]", "[dD][lL][lL]"]);
  // the glob "*.<ext>" as rfd builds it, read as GTK reads it (character classes, no case folding)
  const re = linux.map((e) => new RegExp(`^.*\\.${e}$`));
  const shown = (name: string) => re.some((r) => r.test(name));
  for (const n of ["NightOwl.Zip", "a.zip", "B.ZIP", "mod.Rar", "x.rAR", "c.7Z", "d.7z", "Plug.Dll"]) expect(shown(n)).toBe(true);
  for (const n of ["notes.txt", "a.zipx", "zip"]) expect(shown(n)).toBe(false);
  expect(modFileFilters(true)[0].extensions).toEqual(["zip", "7z", "rar", "dll"]);
});
