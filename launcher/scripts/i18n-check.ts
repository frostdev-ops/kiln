// Checks the launcher's locale files against en.json (docs/kiln/translating.md):
//   - a key en.json does not have (a typo, or a text the launcher no longer shows),
//   - a value that is not text,
//   - placeholders that differ from en.json's ({0}, {name}, {count:N0}: the names must match; a plural form may leave
//     out {count}),
// and, with --source, that every t("key") in launcher/src (the save editor aside) is in en.json.
//
//   bun scripts/i18n-check.ts                       every src/i18n/*.json, and the source
//   bun scripts/i18n-check.ts ~/zh-CN.json          one file (a translation sent by mail, a PR's file)
//
// Exit 1 when anything is wrong. Keys a translation does not have yet are counted, not wrong (they show in English).
import { readdirSync, readFileSync } from "node:fs";
import { basename, join, relative } from "node:path";
import { parse, type ParseError } from "jsonc-parser";
import { flatten, placeholders, pluralBase, PLURAL_FORMS, type Table } from "../src/i18n/format";

export interface Problem {
  key: string;
  kind: "unknown-key" | "not-text" | "placeholders" | "not-json";
  message: string;
}

/** en.json's text(s) for a key a locale has: the key itself, or all plural forms of its base. Null: en.json lacks it. */
function englishFor(en: Table, key: string): string[] | null {
  if (en[key] !== undefined) return [en[key]];
  const base = pluralBase(key);
  if (base === key) return null;
  const forms = PLURAL_FORMS.map((f) => en[`${base}.${f}`]).filter((x): x is string => x !== undefined);
  return forms.length ? forms : null;
}

/** What is wrong in one locale file's object, compared with en.json's. */
export function checkLocale(enTable: Table, file: unknown): Problem[] {
  const out: Problem[] = [];
  if (file === null || typeof file !== "object" || Array.isArray(file)) {
    return [{ key: "", kind: "not-json", message: "the file must hold one { ... } object" }];
  }
  const bad: string[] = [];
  const table = flatten(file, "", {}, bad);
  for (const key of bad) out.push({ key, kind: "not-text", message: "values are text in quotes" });
  for (const [key, text] of Object.entries(table)) {
    const english = englishFor(enTable, key);
    if (!english) {
      out.push({ key, kind: "unknown-key", message: "en.json has no such key" });
      continue;
    }
    const want = new Set(english.flatMap(placeholders));
    const have = new Set(placeholders(text));
    const plural = pluralBase(key) !== key;
    const extra = [...have].filter((p) => !want.has(p));
    const missing = [...want].filter((p) => !have.has(p) && !(plural && p === "count"));
    if (extra.length || missing.length) {
      const parts = [];
      if (missing.length) parts.push(`missing ${missing.map((p) => `{${p}}`).join(", ")}`);
      if (extra.length) parts.push(`not in English: ${extra.map((p) => `{${p}}`).join(", ")}`);
      out.push({ key, kind: "placeholders", message: parts.join("; ") });
    }
  }
  return out;
}

/** en.json keys (plural forms as their base) a locale does not have: they show in English. */
export function untranslated(enTable: Table, file: unknown): string[] {
  const table = flatten(file);
  const bases = new Set(Object.keys(table).map(pluralBase));
  const seen = new Set<string>();
  const out: string[] = [];
  for (const key of Object.keys(enTable)) {
    const base = table[key] !== undefined ? key : pluralBase(key);
    if (seen.has(base)) continue;
    seen.add(base);
    if (table[key] === undefined && !bases.has(base)) out.push(base);
  }
  return out;
}

/** True when en.json has `key`, as a text or as plural forms. */
export function hasKey(enTable: Table, key: string): boolean {
  return enTable[key] !== undefined || PLURAL_FORMS.some((f) => enTable[`${key}.${f}`] !== undefined);
}

/** en.json's own shape: a key ending in a plural word (`.one`, `.other`, ...) is a plural form, so its siblings must
 * all be plural forms and include `.other` ("x.type.other" beside "x.type.kiln" would be read as a plural of x.type). */
export function pluralShapeProblems(enTable: Table): string[] {
  const kids = new Map<string, string[]>();
  for (const k of Object.keys(enTable)) {
    const i = k.lastIndexOf(".");
    if (i > 0) kids.set(k.slice(0, i), [...(kids.get(k.slice(0, i)) ?? []), k.slice(i + 1)]);
  }
  const forms = PLURAL_FORMS as readonly string[];
  const out: string[] = [];
  for (const [base, ks] of kids) {
    if (!ks.some((k) => forms.includes(k))) continue;
    const odd = ks.filter((k) => !forms.includes(k));
    if (odd.length) out.push(`${base}: ${ks.filter((k) => forms.includes(k)).join(", ")} beside ${odd.join(", ")} (a key ending in a plural word is a plural form: rename it)`);
    else if (!ks.includes("other")) out.push(`${base}: plural forms without .other`);
    if (enTable[base] !== undefined) out.push(`${base}: both a text and plural forms`);
  }
  return out;
}

/** Every literal `t("key")`, `tr("key")` (t imported as tr) and `rich("key")` in the source files under `dir` (the save editor aside): key, file, line. */
export function sourceKeys(dir: string): { key: string; file: string; line: number }[] {
  const out: { key: string; file: string; line: number }[] = [];
  const re = /(?<![\w$.])(?:t|tr|rich)\(\s*(["'`])([A-Za-z0-9_.-]+)\1/g;
  const walk = (d: string) => {
    for (const e of readdirSync(d, { withFileTypes: true })) {
      const p = join(d, e.name);
      if (e.isDirectory()) {
        if (e.name !== "editor" && e.name !== "node_modules") walk(p);
      } else if (/\.(ts|tsx)$/.test(e.name)) {
        const lines = readFileSync(p, "utf8").split("\n");
        lines.forEach((l, i) => {
          if (/^\s*(\/\/|\/?\*)/.test(l)) return; // comments
          for (const m of l.matchAll(re)) out.push({ key: m[2], file: p, line: i + 1 });
        });
      }
    }
  };
  walk(dir);
  return out;
}

/** Reads a locale file as Locale.cs does (comments and trailing commas are fine). */
export function readLocaleFile(path: string): { value: unknown; error: string | null } {
  const errors: ParseError[] = [];
  const value = parse(readFileSync(path, "utf8"), errors, { allowTrailingComma: true });
  return { value, error: errors.length ? `not JSON (error ${errors[0].error} at offset ${errors[0].offset})` : null };
}

function main(argv: string[]): number {
  const root = join(import.meta.dir, "..");
  const i18n = join(root, "src", "i18n");
  const enTable = flatten(JSON.parse(readFileSync(join(i18n, "en.json"), "utf8")));
  const given = argv.filter((a) => !a.startsWith("--"));
  const files = given.length ? given : readdirSync(i18n).filter((f) => f.endsWith(".json") && f !== "en.json").map((f) => join(i18n, f));
  let wrong = 0;
  for (const f of files) {
    const { value, error } = readLocaleFile(f);
    const problems: Problem[] = error ? [{ key: "", kind: "not-json", message: error }] : checkLocale(enTable, value);
    const missing = error ? [] : untranslated(enTable, value);
    console.log(`${basename(f)}: ${problems.length ? `${problems.length} problem(s)` : "ok"}, ${missing.length} of ${new Set(Object.keys(enTable).map(pluralBase)).size} texts not translated yet`);
    for (const p of problems) console.log(`  ${p.key || "(file)"}: ${p.message}`);
    wrong += problems.length;
  }
  if (!given.length || argv.includes("--source")) {
    const src = join(root, "src");
    for (const p of pluralShapeProblems(enTable)) console.log(`  en.json ${p}`);
    wrong += pluralShapeProblems(enTable).length;
    const unknown = sourceKeys(src).filter((k) => !hasKey(enTable, k.key));
    for (const k of unknown) console.log(`  ${relative(root, k.file)}:${k.line}: t("${k.key}") is not in en.json`);
    console.log(`source: ${unknown.length ? `${unknown.length} key(s) not in en.json` : "ok"}`);
    wrong += unknown.length;
  }
  return wrong ? 1 : 0;
}

if (import.meta.main) process.exit(main(process.argv.slice(2)));
