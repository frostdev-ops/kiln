// SPDX-License-Identifier: MIT
// Cross-check of kiln lint's schema validator: validates files with ajv (draft 2020-12) against schema/*.schema.json
// and prints one JSON line per file: {"file", "schema", "valid", "errors": [instancePath...]}.
//   node crates/kiln-manage/tests/lint/ajv-check.mjs <schema dir> <launcher node_modules> <schema file> <json file>...
// Used by tests/lint.rs (ajv_agrees) when node and launcher/node_modules exist.
import { createRequire } from "node:module";
import { readFileSync, readdirSync } from "node:fs";
import { join, resolve } from "node:path";

const [schemaDir, modules, schemaName, ...files] = process.argv.slice(2);
const require = createRequire(join(resolve(modules), "x.js"));
const Ajv2020 = require("ajv/dist/2020").default;
const addFormats = require("ajv-formats").default;
const { parse } = require("jsonc-parser");
const ajv = new Ajv2020({ allErrors: true, strict: false });
addFormats(ajv);
for (const f of readdirSync(schemaDir).filter((f) => f.endsWith(".schema.json")))
  ajv.addSchema(parse(readFileSync(join(schemaDir, f), "utf8"), [], { allowTrailingComma: true }), f);
const validate = ajv.getSchema(schemaName);
for (const file of files) {
  const errors = [];
  const doc = parse(readFileSync(file, "utf8"), errors, { allowTrailingComma: true });
  if (errors.length) { console.log(JSON.stringify({ file, schema: schemaName, syntax: true })); continue; }
  const valid = validate(doc);
  console.log(JSON.stringify({ file, schema: schemaName, valid, errors: (validate.errors ?? []).map((e) => `${e.instancePath} ${e.keyword} ${e.message}`) }));
}
