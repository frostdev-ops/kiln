// Validates the contract examples (schema/examples) and every kiln.mod.json in mods/ against schema/*.schema.json.
//   bun run check:schemas
import Ajv2020 from "ajv/dist/2020";
import addFormats from "ajv-formats";
import { parse, type ParseError } from "jsonc-parser";
import { readdirSync, readFileSync, statSync } from "node:fs";
import { basename, join } from "node:path";

const repo = join(import.meta.dir, "..", "..");
const schemaDir = join(repo, "schema");
const ajv = new Ajv2020({ allErrors: true, strict: false });
addFormats(ajv);

function jsonc(file: string): unknown {
  const errors: ParseError[] = [];
  const value = parse(readFileSync(file, "utf8"), errors, { allowTrailingComma: true });
  if (errors.length) throw new Error(`${file}: not valid JSONC (${errors.length} errors)`);
  return value;
}

for (const f of readdirSync(schemaDir).filter((f) => f.endsWith(".schema.json"))) ajv.addSchema(jsonc(join(schemaDir, f)) as object, f);

const exampleSchema = (name: string): string => {
  if (name.startsWith("profile")) return "profile.schema.json";
  if (name.startsWith("config-schema")) return "config-schema.schema.json";
  if (name === "native-state.json") return "native-state.schema.json";
  if (name.startsWith("native")) return "native.schema.json";
  if (name === "install-receipt.json") return "install-receipt.schema.json";
  return name.replace(/\.json$/, ".schema.json");
};

let checked = 0;
let failed = 0;
function check(file: string, schemaId: string) {
  const validate = ajv.getSchema(schemaId);
  if (!validate) throw new Error(`no schema ${schemaId} for ${file}`);
  checked++;
  if (!validate(jsonc(file))) {
    failed++;
    console.error(`FAIL ${file} (${schemaId})`);
    for (const e of validate.errors ?? []) console.error(`  ${e.instancePath || "/"} ${e.message}`);
  }
}

for (const f of readdirSync(join(schemaDir, "examples")).filter((f) => f.endsWith(".json")).sort())
  check(join(schemaDir, "examples", f), exampleSchema(f));

const walk = (dir: string): string[] =>
  readdirSync(dir).flatMap((n) => {
    const p = join(dir, n);
    if (["bin", "obj", "node_modules"].includes(n)) return [];
    return statSync(p).isDirectory() ? walk(p) : basename(p) === "kiln.mod.json" ? [p] : [];
  });
for (const m of walk(join(repo, "mods")).filter((p) => !p.includes("sample-bad-manifest"))) check(m, "kiln.mod.schema.json");

console.log(failed ? `${failed} of ${checked} files failed` : `ok: ${checked} files valid`);
process.exit(failed ? 1 : 0);
