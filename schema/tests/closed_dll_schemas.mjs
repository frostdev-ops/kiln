// SPDX-License-Identifier: MIT
// compat-rules.schema.json and hooks-state.schema.json against their examples and the shared fixtures
// (schema/fixtures/compat-rules/cases.json, schema/fixtures/hooks-state/, and the launcher's launcher-*.json beside them). Uses the launcher's ajv:
//   node schema/tests/closed_dll_schemas.mjs        (AJV_2020=<path to ajv/dist/2020.js> when launcher/node_modules is absent)
import assert from "node:assert/strict";
import { existsSync, readdirSync, readFileSync } from "node:fs";
import { pathToFileURL } from "node:url";

const here = new URL(".", import.meta.url);
const read = (file) => JSON.parse(readFileSync(new URL(file, here), "utf8"));
const candidates = [process.env.AJV_2020, new URL("../../launcher/node_modules/ajv/dist/2020.js", here).pathname].filter(Boolean);
const found = candidates.find((p) => existsSync(p));
assert(found, `ajv 2020 not found; set AJV_2020 (tried ${candidates.join(", ")})`);
const Ajv2020 = (await import(pathToFileURL(found).href)).default;
const ajv = new Ajv2020({ strict: false, validateFormats: false });
ajv.addSchema(read("../common.schema.json"));
const rulesFile = ajv.compile(read("../compat-rules.schema.json"));
const hooksFile = ajv.compile(read("../hooks-state.schema.json"));
const ruleOnly = ajv.compile({ $ref: "compat-rules.schema.json#/$defs/rule" });
const matcherOnly = ajv.compile({ $ref: "compat-rules.schema.json#/$defs/matcher" });

const must = (validate, doc, what) => assert(validate(doc), `${what}: ${JSON.stringify(validate.errors)}`);
must(rulesFile, read("../examples/compat-rules.json"), "examples/compat-rules.json");
must(hooksFile, read("../examples/hooks-state.json"), "examples/hooks-state.json");
for (const dir of ["hooks-state", "compat-rules"]) {  // every file of the two fixture folders except cases.json
  for (const name of readdirSync(new URL(`../fixtures/${dir}/`, here)).filter((n) => n.endsWith(".json") && n !== "cases.json")) {
    must(dir === "hooks-state" ? hooksFile : rulesFile, read(`../fixtures/${dir}/${name}`), `fixtures/${dir}/${name}`);
  }
}

// hooks.json: wrong shapes are refused
const good = read("../examples/hooks-state.json");
for (const [what, mutate] of [
  ["format 2", (d) => { d.format = 2; }],
  ["no targets", (d) => { delete d.targets; }],
  ["bad overlap kind", (d) => { d.overlaps[0].kind = "other"; }],
  ["overlap without owners", (d) => { delete d.overlaps[0].owners; }],
  ["bad rule action", (d) => { d.rules[0].action = "explode"; }],
  ["rva without 0x", (d) => { d.targets[0].rva = "9151f0"; }],
  ["unknown link kind", (d) => { d.targets[0].links[0].kind = "weird"; }],
]) {
  const d = structuredClone(good);
  mutate(d);
  assert.equal(hooksFile(d), false, `hooks.json: ${what} must not validate`);
}

// compat-rules.json: the parse cases. A rule the fixture keeps validates; one it drops does not (the schema also
// says "the first of a repeated id stays" and "the rest of the file survives", which the readers handle, not the schema).
const cases = read("../fixtures/compat-rules/cases.json");
for (const c of cases.parse) {
  const doc = typeof c.input === "string" ? (() => { try { return JSON.parse(c.input.replace(/^\uFEFF/, "")); } catch { return undefined; } })() : c.input;
  if (!c.ok) {
    if (doc !== undefined) assert.equal(rulesFile(doc), false, `parse "${c.name}": the file must not validate`);
    continue;
  }
  must(rulesFile, { ...doc, rules: [] }, `parse "${c.name}": the file shell`);
  let valid = 0;
  const seen = new Set();
  for (const r of doc.rules ?? []) {
    const ok = ruleOnly(r);
    if (ok && !seen.has(r.id)) { valid++; assert(c.rules.includes(r.id), `parse "${c.name}": ${r.id} validates but is dropped`); }
    if (ok) seen.add(r.id);
  }
  assert.equal(valid, c.rules.length, `parse "${c.name}": ${valid} schema-valid rules, fixture keeps ${c.rules.length}`);
  assert.equal((doc.rules ?? []).length - valid, c.dropped, `parse "${c.name}": dropped count`);
}
for (const m of [{ module: "a.dll" }, { engine: "emf" }, { mod: "emf:x" }, { symbol: "a/B" }]) must(matcherOnly, m, JSON.stringify(m));
for (const c of cases.merge) for (const r of [...(c.shipped ?? []), ...(c.user ?? [])]) must(ruleOnly, r, `merge "${c.name}"`);
for (const bad of [{ module: "" }, { module: "a.dll", engine: "emf" }, { mod: "nocolon" }, {}, { dll: "a.dll" }])
  assert.equal(matcherOnly(bad), false, `matcher ${JSON.stringify(bad)} must not validate`);
for (const c of cases.evaluate) for (const r of c.rules) must(ruleOnly, r, `evaluate "${c.name}"`);
console.log("closed-DLL schema checks passed");
