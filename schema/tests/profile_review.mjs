// SPDX-License-Identifier: MIT
// Uses the launcher's existing schema validator dependency; no browser or game is started.
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import Ajv2020 from "../../launcher/node_modules/ajv/dist/2020.js";
const read = (file) => JSON.parse(readFileSync(new URL(file, import.meta.url), "utf8"));
const ajv = new Ajv2020({ strict: false, validateFormats: false });
ajv.addSchema(read("../common.schema.json"));
const validate = ajv.compile(read("../profile.schema.json"));
const profile = (kind, entries) => ({ format: 1, id: "test", generation: 1, mods: { [kind]: entries } });
const entry = { id: "safe", version: "1.0.0", hash: `kdh1:${"a".repeat(64)}`, path: "profiles/test/eml/safe" };
const kinds = ["eml", "xhl", "shroudforge", "set", "greeneye", "flame", "shroudtopia", "enshwick", "shroudlink", "ember"];
for (const kind of kinds) {
  assert(validate(profile(kind, [entry])), JSON.stringify(validate.errors));
  assert(validate(profile(kind, [{ ...entry, version: "V1.2.3" }])), "SemVersion prefix parity");
  for (const bad of [
    { ...entry, version: "../escape" }, { ...entry, version: null },
    { ...entry, hash: "bad" }, { ...entry, hash: null },
    { ...entry, path: "mods/../outside" }, { ...entry, path: "mods/safe\0hidden" },
    { ...entry, enabled: 1 }, { ...entry, id: "../escape" },
    { ...entry, id: "safe\n" }, { ...entry, version: "1.0.0\n" }, { ...entry, hash: `${entry.hash}\n` },
  ]) assert.equal(validate(profile(kind, [bad])), false, `${kind}: ${JSON.stringify(bad)}`);
  assert.equal(validate(profile(kind, Array(4096).fill(entry))), true);
  assert.equal(validate(profile(kind, Array(4097).fill(entry))), false);
}
for (const kind of ["eml", "xhl"]) {
  for (const path of ["../mods/XHL-Vein-Mining", "..\\mods\\XHL-Vein-Mining"]) {
    assert(validate(profile(kind, [{ ...entry, path }])), JSON.stringify(validate.errors));
  }
  for (const path of ["../mods/../outside", "../../mods/safe", "../mods/safe/child", "../mods/.hidden", "../mods/safe ", "../mods/safe\u0000", "../mods/CON.txt", "../mods/Lpt0", "../mods/enshwick"]) {
    assert.equal(validate(profile(kind, [{ ...entry, path }])), false, path);
  }
}
for (const kind of ["kiln", "native", "emf", "plugin", ...kinds.filter((k) => !["eml", "xhl"].includes(k))]) {
  assert.equal(validate(profile(kind, [{ ...entry, path: "../mods/safe" }])), false, kind);
}
assert.equal(validate({ ...profile("xhl", [entry]), dropins: ["xhl"] }), false);
assert.equal(validate({ ...profile("eml", []), generation: 18446744073709551615 }), false);
console.log("engine profile schema checks passed");
