// The launcher's string tables, the pure half: flattening a locale file, the language lookup order, filling
// placeholders. The same format as Kiln's mod locales (managed/Kiln.Api/Locale.cs) and docs/kiln/translating.md:
// one JSON object per language, nested objects make dotted keys, `{0}` / `{name}` / `{count:N0}` placeholders, `{{` and
// `}}` for braces, plurals as `.one` / `.other` (and the other CLDR categories) key pairs.
// No React and no Vite here: unit tests and scripts/i18n-check.ts import it as it is.

/** A flattened locale file: dotted key → text. */
export type Table = Readonly<Record<string, string>>;

/** Placeholder values: positional (`{0}`, `{1}`) or one object of named values (`{name}`, `{count}`). */
export type Args = readonly unknown[];

/** The plural categories a key may end in (Intl.PluralRules / CLDR). */
export const PLURAL_FORMS = ["zero", "one", "two", "few", "many", "other"] as const;

/** The language every other falls back to. */
export const FALLBACK = "en";

/** "zh_CN", "ZH-cn" and " zh-cn " are all "zh-cn". Empty gives "en". */
export function normalizeTag(tag: string | null | undefined): string {
  const s = (tag ?? "").trim().replace(/_/g, "-").toLowerCase();
  return s.length === 0 ? FALLBACK : s;
}

/** The tags tried for `tag`, in order: the tag, its closest relatives, its main language, English.
 * "zh-CN" gives zh-cn, zh, en; "es-MX" gives es-mx, es-419, es, en (as Locale.cs's LookupOrder). Script tags a
 * browser may report map to the region files translators write: zh-Hans → zh-cn, zh-Hant / zh-HK / zh-MO → zh-tw. */
export function lookupOrder(tag: string): string[] {
  const l = normalizeTag(tag);
  const chain: string[] = [];
  const add = (t: string) => {
    if (!chain.includes(t)) chain.push(t);
  };
  add(l);
  if (/^zh-hans(-|$)/.test(l) || l === "zh-sg") add("zh-cn");
  if (/^zh-hant(-|$)/.test(l) || l === "zh-hk" || l === "zh-mo") add("zh-tw");
  const primary = l.split("-")[0];
  if (l === "es-mx") add("es-419");
  add(primary);
  if (primary === "nb") add("no");
  else if (primary === "no") add("nb");
  add(FALLBACK);
  return chain;
}

/** A locale file's object as dotted keys. Non-string values are skipped and named in `bad` (for the check script). */
export function flatten(obj: unknown, prefix = "", into: Record<string, string> = {}, bad: string[] = []): Record<string, string> {
  if (obj === null || typeof obj !== "object" || Array.isArray(obj)) return into;
  for (const [k, v] of Object.entries(obj as Record<string, unknown>)) {
    const key = prefix ? `${prefix}.${k}` : k;
    if (typeof v === "string") into[key] = v;
    else if (v !== null && typeof v === "object" && !Array.isArray(v)) flatten(v, key, into, bad);
    else bad.push(key);
  }
  return into;
}

/** The placeholders of a text as written in it, without formats: "{0} of {total:N0}" gives ["0", "total"]. */
export function placeholders(template: string): string[] {
  const out: string[] = [];
  scan(template, (token) => {
    const name = token.split(":")[0].trim();
    if (name) out.push(name);
  });
  return out;
}

/** Walks a template; calls `onToken` with each `{...}` body and returns the text with `{{`/`}}` made braces and each
 * token replaced by what `onToken` returns (undefined: the token stays as written). */
function scan(template: string, onToken: (token: string) => string | undefined): string {
  let out = "";
  for (let i = 0; i < template.length; i++) {
    const c = template[i];
    if (c === "{" && template[i + 1] === "{") {
      out += "{";
      i++;
    } else if (c === "}" && template[i + 1] === "}") {
      out += "}";
      i++;
    } else if (c === "{") {
      const end = template.indexOf("}", i + 1);
      if (end < 0) {
        out += c;
        continue;
      }
      const token = template.slice(i + 1, end);
      const filled = onToken(token);
      out += filled ?? template.slice(i, end + 1);
      i = end;
    } else out += c;
  }
  return out;
}

/** True for `t(key, { name: … })`: one plain object of named values (not an array, a Date, an Error). */
function isNamed(args: Args): args is [Record<string, unknown>] {
  if (args.length !== 1) return false;
  const a = args[0];
  return a !== null && typeof a === "object" && Object.getPrototypeOf(a) === Object.prototype;
}

/** The value of a placeholder, or `undefined` when there is none (the placeholder then stays as written). */
function valueOf(name: string, args: Args): { v: unknown } | undefined {
  if (/^\d+$/.test(name)) {
    const n = Number(name);
    if (isNamed(args) || n >= args.length) return undefined;
    return { v: args[n] };
  }
  if (!isNamed(args) || !Object.prototype.hasOwnProperty.call(args[0], name)) return undefined;
  return { v: args[0][name] };
}

const nfCache = new Map<string, Intl.NumberFormat>();
function nf(locale: string, opts: Intl.NumberFormatOptions): Intl.NumberFormat {
  const key = `${locale}|${JSON.stringify(opts)}`;
  let f = nfCache.get(key);
  if (!f) {
    try {
      f = new Intl.NumberFormat(locale, opts);
    } catch {
      f = new Intl.NumberFormat(FALLBACK, opts);
    }
    nfCache.set(key, f);
  }
  return f;
}

/** A number in a .NET standard numeric format, in `locale`: N (grouped), F (fixed), D (whole, zero-padded), P (percent
 * of a fraction), each with an optional digit count ("N0", "F1", "D3", "P0"). An unknown format gives null. */
export function formatNumber(n: number, spec: string, locale: string): string | null {
  const m = /^([NnFfDdPp])(\d{0,2})$/.exec(spec.trim());
  if (!m || !Number.isFinite(n)) return null;
  const digits = m[2] === "" ? null : Number(m[2]);
  switch (m[1].toUpperCase()) {
    case "N":
      return nf(locale, { minimumFractionDigits: digits ?? 2, maximumFractionDigits: digits ?? 2 }).format(n);
    case "F":
      return nf(locale, { minimumFractionDigits: digits ?? 2, maximumFractionDigits: digits ?? 2, useGrouping: false }).format(n);
    case "D":
      if (!Number.isInteger(n)) return null;
      return (n < 0 ? "-" : "") + String(Math.abs(n)).padStart(digits ?? 1, "0");
    case "P":
      return nf(locale, { style: "percent", minimumFractionDigits: digits ?? 2, maximumFractionDigits: digits ?? 2 }).format(n);
  }
  return null;
}

/** One value as text: null and undefined are empty, a number with a format is formatted in `locale`, anything else is
 * `String(v)` (a plain `{count}` writes the number as code would, "1234"). A bad format writes the value without it. */
function text(v: unknown, spec: string | null, locale: string): string {
  if (v === null || v === undefined) return "";
  if (spec !== null && typeof v === "number") return formatNumber(v, spec, locale) ?? String(v);
  return String(v);
}

/** Fills `{0}`, `{name}`, `{name:format}`; `{{` and `}}` are braces. A placeholder without a value stays as written. */
export function fill(template: string, args: Args, locale: string = FALLBACK): string {
  if (template.indexOf("{") < 0 && template.indexOf("}") < 0) return template;
  return scan(template, (token) => {
    const colon = token.indexOf(":");
    const name = (colon >= 0 ? token.slice(0, colon) : token).trim();
    const spec = colon >= 0 ? token.slice(colon + 1) : null;
    if (!name) return undefined;
    const got = valueOf(name, args);
    return got ? text(got.v, spec, locale) : undefined;
  });
}

/** The number that picks a plural form: the named `count`, else the first positional value when it is a number. */
export function pluralCount(args: Args): number | null {
  if (isNamed(args)) {
    const c = args[0].count;
    return typeof c === "number" ? c : null;
  }
  return typeof args[0] === "number" ? args[0] : null;
}

const prCache = new Map<string, Intl.PluralRules>();
/** The CLDR plural category of `n` in `locale` ("one", "other", ...). */
export function pluralCategory(n: number, locale: string): string {
  let r = prCache.get(locale);
  if (!r) {
    try {
      r = new Intl.PluralRules(locale);
    } catch {
      r = new Intl.PluralRules(FALLBACK);
    }
    prCache.set(locale, r);
  }
  return r.select(n);
}

/** The raw text of `key` in one table: the key itself, or for a count its plural form in that language
 * (`key.one`, then `key.other`). */
export function rawIn(table: Table | undefined, key: string, count: number | null, locale: string): string | undefined {
  if (!table) return undefined;
  const direct = table[key];
  if (direct !== undefined) return direct;
  if (count === null) return table[`${key}.other`];
  return table[`${key}.${pluralCategory(count, locale)}`] ?? table[`${key}.other`];
}

/** Keys that are plural forms of one base key ("mods.count.one" → "mods.count"), else the key. */
export function pluralBase(key: string): string {
  const i = key.lastIndexOf(".");
  const last = i >= 0 ? key.slice(i + 1) : "";
  return i > 0 && (PLURAL_FORMS as readonly string[]).includes(last) ? key.slice(0, i) : key;
}
