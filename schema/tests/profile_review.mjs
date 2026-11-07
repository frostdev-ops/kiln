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
const kinds = ["eml", "shroudforge", "set", "greeneye", "flame", "shroudtopia", "enshwick", "shroudlink", "ember"];
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
assert.equal(validate({ ...profile("eml", []), generation: 18446744073709551615 }), false);
console.log("engine profile schema checks passed");
