// The launcher's languages (docs/kiln/translating.md). English is built in (en.json); other locale files register
// themselves (src/i18n/locales.ts, from Vite's glob of src/i18n/*.json). Usage:
//
//   import { t, useT } from "../i18n";
//   function Card() { const t = useT(); return <h2>{t("settings.title")}</h2>; }    // re-renders on a language change
//   toast.ok(t("games.found", { count: n }));                                        // outside render: the current language
//
// A key the active language lacks shows the English text; one English lacks shows the key itself (both logged once in
// development). The language preference is "system" (the first of the system's languages that has a file) or a tag.
import { Fragment, createElement, isValidElement, useMemo, useSyncExternalStore, type ReactNode } from "react";
import en from "./en.json" with { type: "json" };
import { FALLBACK, fill, flatten, lookupOrder, normalizeTag, pluralCount, rawIn, type Table } from "./format";

export { fill, formatNumber, lookupOrder, placeholders, type Table } from "./format";

/** The preference: "system" or a language tag ("en", "zh-CN"). */
export type LanguagePref = "system" | (string & {});

/** The translation function: `t("key")`, `t("key", { name: "x", count: 3 })`, `t("key", a, b)` for `{0}`, `{1}`. */
export type TFunction = (key: string, ...args: unknown[]) => string;

interface Locale {
  /** As the file is named ("zh-CN"). */
  tag: string;
  table: Table;
}

/** Built in (en.json and the files beside it), by normalized tag. */
const builtIn = new Map<string, Locale>([[FALLBACK, { tag: "en", table: flatten(en) }]]);
/** From the data folder's locales/ (setUserLocales): a translator's file wins over a built-in one of its language. */
let user = new Map<string, Locale>();
/** What answers: user over built in. */
const tables = new Map<string, Locale>(builtIn);
let pref: LanguagePref = "system";
let active = FALLBACK;
let chain: string[] = [FALLBACK];
/** Bumped at every change of the language or of a table: what `useT` re-renders on. */
let version = 0;
const listeners = new Set<() => void>();
const reported = new Set<string>();

const dev = (() => {
  try {
    return !!(import.meta as { env?: { DEV?: boolean } }).env?.DEV;
  } catch {
    return false;
  }
})();

function rebuild() {
  tables.clear();
  for (const [k, v] of builtIn) tables.set(k, v);
  for (const [k, v] of user) if (k !== FALLBACK) tables.set(k, v);
  reported.clear();
  apply(true);
}

/** Adds (or replaces) a built-in language: its tag as the file is named and the file's object. */
export function registerLocale(tag: string, file: unknown): void {
  if (normalizeTag(tag) === FALLBACK) return; // English is en.json
  builtIn.set(normalizeTag(tag), { tag, table: flatten(file) });
  rebuild();
}

/** Replaces the languages read from the data folder (a translator's files; English stays en.json's). */
export function setUserLocales(files: readonly { tag: string; file: unknown }[]): void {
  user = new Map(files.filter((f) => normalizeTag(f.tag) !== FALLBACK).map((f) => [normalizeTag(f.tag), { tag: f.tag, table: flatten(f.file) }]));
  rebuild();
}

/** The languages with a file, English first, then by tag: `{ tag, name }` with the name in its own language. */
export function languages(): { tag: string; name: string }[] {
  const all = [...tables.values()].sort((a, b) => (a.tag === "en" ? -1 : b.tag === "en" ? 1 : a.tag.localeCompare(b.tag)));
  return all.map((l) => ({ tag: l.tag, name: languageName(l.tag) }));
}

/** Names Intl does not give the way players know them ("中文（中国）" for zh-CN). */
const KNOWN_NAMES: Record<string, string> = { "zh-cn": "简体中文", "zh-tw": "繁體中文", "zh-hans": "简体中文", "zh-hant": "繁體中文" };

/** A language's name in that language: its file's own `language.name`, else a known name, else the system's name for
 * it, else the tag. */
export function languageName(tag: string): string {
  const own = tables.get(normalizeTag(tag))?.table["language.name"];
  if (own) return own;
  if (KNOWN_NAMES[normalizeTag(tag)]) return KNOWN_NAMES[normalizeTag(tag)];
  try {
    return new Intl.DisplayNames([tag], { type: "language" }).of(tag) ?? tag;
  } catch {
    return tag;
  }
}

/** The system's languages, most wanted first (navigator.languages). */
function systemLanguages(): string[] {
  const nav = typeof navigator === "undefined" ? undefined : navigator;
  const list = nav?.languages?.length ? nav.languages : nav?.language ? [nav.language] : [];
  return [...list];
}

/** The language a preference shows: a tag, or for "system" the first system language with a file (else English). */
export function resolveLanguage(p: LanguagePref, system: readonly string[] = systemLanguages()): string {
  if (p !== "system") return normalizeTag(p);
  for (const s of system) {
    const hit = lookupOrder(s).find((x) => x !== FALLBACK && tables.has(x));
    if (hit) return normalizeTag(s);
  }
  return FALLBACK;
}

function apply(force = false) {
  const next = resolveLanguage(pref);
  const nextChain = lookupOrder(next).filter((x) => tables.has(x));
  if (!force && next === active && nextChain.join() === chain.join()) return;
  active = next;
  chain = nextChain;
  version++;
  if (typeof document !== "undefined") document.documentElement.lang = tables.get(chain[0])?.tag ?? next;
  for (const l of listeners) l();
}

/** Sets the preference ("system" or a tag); every `useT` re-renders. */
export function setLanguage(p: LanguagePref | null | undefined): void {
  pref = !p || p === "system" ? "system" : p;
  apply();
}

/** The preference as set. */
export const languagePref = (): LanguagePref => pref;

/** The tag of the language shown, normalized ("zh-cn"; "en" when none of the wanted ones has a file). */
export const currentLanguage = (): string => chain[0] ?? FALLBACK;

/** The tag of the language shown, as its file is named ("zh-CN"), for Intl formatters. */
export const currentLocale = (): string => tables.get(currentLanguage())?.tag ?? FALLBACK;

function report(key: string, where: string) {
  if (!dev || reported.has(`${where}|${key}`)) return;
  reported.add(`${where}|${key}`);
  if (where === FALLBACK) console.error(`[i18n] no text for "${key}" in en.json`);
  else console.warn(`[i18n] "${key}" is not in ${where}: shown in English`);
}

/** The text of `key` in the current language with its placeholders filled. */
export function t(key: string, ...args: unknown[]): string {
  const count = pluralCount(args);
  for (const tag of chain) {
    const loc = tables.get(tag)!;
    const raw = rawIn(loc.table, key, count, loc.tag);
    if (raw !== undefined) {
      if (tag === FALLBACK && chain[0] !== FALLBACK) report(key, tables.get(chain[0])!.tag);
      return fill(raw, args, currentLocale());
    }
  }
  // the chain always ends in English (built in): English lacks it too
  report(key, FALLBACK);
  return key;
}

/** Like `t`, for a text with elements in it: `rich("play.hint", { button: <b>{t("play.play")}</b> })` for
 * "Press {button} to start". Element values go in as they are; any other value is filled as `t` fills it. One named
 * object, or positional values. */
export function rich(key: string, ...args: unknown[]): ReactNode {
  const elements: ReactNode[] = [];
  const mark = (v: unknown) => {
    if (!isValidElement(v)) return v;
    elements.push(v);
    return `\u0000${elements.length - 1}\u0000`;
  };
  const named = args.length === 1 && args[0] !== null && typeof args[0] === "object" && Object.getPrototypeOf(args[0]) === Object.prototype;
  const marked = named
    ? [Object.fromEntries(Object.entries(args[0] as Record<string, unknown>).map(([k, v]) => [k, mark(v)]))]
    : args.map(mark);
  const text = t(key, ...marked);
  if (elements.length === 0) return text;
  const parts = text.split(/\u0000(\d+)\u0000/);
  return createElement(Fragment, null, ...parts.map((p, i) => (i % 2 === 1 ? createElement(Fragment, { key: i }, elements[Number(p)]) : p)));
}

/** True when English (the fallback) has `key` (or its plural forms). */
export function hasKey(key: string): boolean {
  return rawIn(tables.get(FALLBACK)!.table, key, null, "en") !== undefined;
}

function subscribe(l: () => void) {
  listeners.add(l);
  return () => {
    listeners.delete(l);
  };
}

const getVersion = () => version;

/** The current language's tag; re-renders the component when the language or a table changes. */
export function useLanguage(): string {
  useSyncExternalStore(subscribe, getVersion, getVersion);
  return currentLanguage();
}

/** `t` for a component: the same function, a new identity per change, and the component re-renders when the language
 * or a table changes (also inside React.memo). */
export function useT(): TFunction {
  const v = useSyncExternalStore(subscribe, getVersion, getVersion);
  return useMemo<TFunction>(() => {
    void v;
    return (key, ...args) => t(key, ...args);
  }, [v]);
}
