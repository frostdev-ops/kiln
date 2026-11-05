// SPDX-License-Identifier: MIT
// Copies what the extension bundles from the Kiln tree into tools/vscode and regenerates the schema contributions:
//   schema/*.schema.json (+ schemas.json, idrefs.json) -> data/schemas/   (allowComments/allowTrailingCommas from the dialect)
//   tools/impact/data/*                                 -> data/impact/index.json (natives, types, configs, errors)
//   branding/logo                                       -> media/icon.png, media/kiln-mono.svg
//   package.json contributes.jsonValidation and the jsonc filenamePatterns <- schema/schemas.json
//     (schemas.fallback.json when schema/schemas.json is absent)
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const here = path.dirname(fileURLToPath(import.meta.url));
const ext = path.resolve(here, "..");
const repo = path.resolve(ext, "../..");
const rd = (p) => JSON.parse(fs.readFileSync(p, "utf8"));
const wr = (p, v) => {
  fs.mkdirSync(path.dirname(p), { recursive: true });
  fs.writeFileSync(p, JSON.stringify(v, null, 2) + "\n");
};

// ---- schemas
const schemaDir = path.join(repo, "schema");
const outSchemas = path.join(ext, "data/schemas");
fs.rmSync(outSchemas, { recursive: true, force: true });
fs.mkdirSync(outSchemas, { recursive: true });
// schema/schemas.json is the mapping; schemas.fallback.json fills in schemas it does not map (the
// launcher/runtime files under kiln/: state, profile, receipts, endpoint), and is the whole mapping while it is absent
const lintMap = path.join(schemaDir, "schemas.json");
const fallback = rd(path.join(ext, "schemas.fallback.json"));
const mapFile = fs.existsSync(lintMap) ? lintMap : path.join(ext, "schemas.fallback.json");
const map = rd(mapFile);
if (mapFile === lintMap) {
  const have = new Set(map.schemas.map((s) => s.schema));
  for (const s of fallback.schemas) if (!have.has(s.schema)) map.schemas.push({ ...s, extra: "tools/vscode/schemas.fallback.json" });
}
const dialect = new Map(map.schemas.map((s) => [s.schema, s.dialect ?? {}]));
// VS Code's JSON service (seen on 1.145) reads the local references inside another file's schema against the wrong root when the reference
// to that file has sibling keywords: "$ref '/$defs/block' ... can not be resolved", and the whole document goes unchecked. `allOf` means the same.
const ALONE = new Set(["$ref", "$schema", "$id", "id", "$comment"]);
const wrapExternalRefs = (n) => {
  if (Array.isArray(n)) return n.map(wrapExternalRefs);
  if (!n || typeof n !== "object") return n;
  const out = Object.fromEntries(Object.entries(n).map(([k, v]) => [k, wrapExternalRefs(v)]));
  if (typeof out.$ref !== "string" || out.$ref.startsWith("#") || Object.keys(out).every((k) => ALONE.has(k))) return out;
  const { $ref, ...rest } = out;
  return { ...rest, allOf: [{ $ref }, ...(rest.allOf ?? [])] };
};
for (const f of fs.readdirSync(schemaDir)) {
  if (!f.endsWith(".schema.json")) continue;
  const s = wrapExternalRefs(rd(path.join(schemaDir, f)));
  const d = dialect.get(f);
  if (d) {
    // vscode-json-languageservice reads these two root keywords
    s.allowComments = !!d.comments;
    s.allowTrailingCommas = !!d.trailingCommas;
  }
  wr(path.join(outSchemas, f), s);
}
wr(path.join(outSchemas, "schemas.json"), { ...map, source: path.relative(repo, mapFile) });
if (fs.existsSync(path.join(schemaDir, "idrefs.json"))) {
  fs.copyFileSync(path.join(schemaDir, "idrefs.json"), path.join(outSchemas, "idrefs.json"));
}

// ---- package.json contributions
const pkgPath = path.join(ext, "package.json");
const pkg = rd(pkgPath);
pkg.contributes.jsonValidation = map.schemas
  .filter((s) => fs.existsSync(path.join(outSchemas, s.schema)))
  .map((s) => ({ fileMatch: s.fileMatch, url: `./data/schemas/${s.schema}` }));
const jsonc = [...new Set(map.schemas.filter((s) => s.dialect?.comments).flatMap((s) => s.fileMatch))]
  .filter((g) => !g.startsWith("!"));
const langs = pkg.contributes.languages.filter((l) => !(l.id === "jsonc" && !l.extensions));
langs.push({ id: "jsonc", filenamePatterns: jsonc });
pkg.contributes.languages = langs;
fs.writeFileSync(pkgPath, JSON.stringify(pkg, null, 2) + "\n");

// ---- Impact data: one slim index (the extension's hover/completion/signature data)
const idata = path.join(repo, "tools/impact/data");
const natives = rd(path.join(idata, "natives.json"));
const kilnNatives = rd(path.join(idata, "kiln-natives.json"));
const notes = rd(path.join(idata, "natives-notes.json"));
const types = rd(path.join(idata, "types.json"));
const configs = rd(path.join(idata, "configs.json"));
const slimArg = (a) => ({ name: a.name, types: a.types ?? [], dir: a.dir ?? "in", ...(a.doc ? { doc: a.doc } : {}), ...(a.raw ? { raw: true } : {}) });
const slim = (n, origin) => ({
  name: n.name,
  node: n.node ?? n.name,
  hash: n.hash,
  argc: n.argc,
  origin,
  args: (n.args ?? []).map(slimArg),
  ...(n.returns ? { returns: n.returns } : {}),
  ...(n.suspends ? { suspends: n.suspends } : {}),
  ...(n.doc ? { doc: n.doc } : {}),
  ...(n.query ? { query: true } : {}),
  ...(n.uses?.programs != null ? { programs: n.uses.programs } : {}),
});
wr(path.join(ext, "data/impact/index.json"), {
  format: 1,
  source: "tools/impact/data (scripts/sync.mjs)",
  build: natives.build ?? null,
  errors: notes.errors ?? {},
  natives: [...natives.natives.map((n) => slim(n, "vanilla")), ...kilnNatives.natives.map((n) => slim(n, "kiln"))],
  types: types.impact.map((t) => ({ name: t.name, hash: t.hash, size: t.size, align: t.align, qualified: t.qualified, uses: t.vanillaVariables })),
  configs: configs.configs.map((c) => ({ id: c.id, aliases: c.aliases, types: c.types, programs: c.programs })),
});

// ---- brand
const logo = path.join(repo, "branding/logo");
fs.copyFileSync(path.join(logo, "png/app-icon-128.png"), path.join(ext, "media/icon.png"));
fs.copyFileSync(path.join(logo, "mark-mono.svg"), path.join(ext, "media/kiln-mono.svg"));
fs.copyFileSync(path.join(logo, "mark.svg"), path.join(ext, "media/kimp.svg"));

console.log(`sync: ${pkg.contributes.jsonValidation.length} schema mapping(s) from ${path.relative(repo, mapFile)}, ` +
  `${natives.natives.length + kilnNatives.natives.length} natives, ${types.impact.length} types, ${configs.configs.length} configs`);
