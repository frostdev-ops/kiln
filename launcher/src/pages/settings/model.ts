// Settings (mockup A, "Section list"): six sections down the left, one at a time on the right, each a few titled
// groups of rows. This file is the map: the sections, their groups (a group's id is its anchor, so the old deep links
// /settings#profiles, #crucible, #library, #nexus, #graphics, #upscalers and #shroudforge open the group's section), and
// the search: every row registers its section, group, name, one line of help, More text and a few keywords
// (pages/settings/searchIndex.ts builds the list in the launcher's language).
import type { AppInfo, Distribution, LauncherSettings, SchemaSnapshot } from "../../api/commands";
import { hasOwnText, t } from "../../i18n";

export const SECTIONS = ["general", "mods", "game", "graphics", "online", "advanced"] as const;
export type SectionId = (typeof SECTIONS)[number];
export const isSection = (s: string | undefined | null): s is SectionId => !!s && (SECTIONS as readonly string[]).includes(s);

export const sectionTitle = (s: SectionId) => t(`settings.sections.${s}.title`);

/** What the page knows when it decides which groups and rows exist (a row of a Linux-only switch, the Nexus card in a
 *  build without Nexus, Crucible's parts, the developer switch outside the desktop app, a server's graphics). */
export interface SettingsCtx {
  info?: AppInfo;
  settings?: LauncherSettings;
  dist: Distribution | null;
  nexus: boolean;
  crucible: boolean;
  /** The desktop app (not the browser mock): the developer switch. */
  tauri: boolean;
  /** The selected game is a client (graphics, ShroudForge). */
  client: boolean;
  /** The selected game's kiln.json schema and values, when read. */
  schema?: SchemaSnapshot | null;
  values?: unknown;
  /** The festive season's switch is offered (shell/season.ts festiveOffered: during the season). */
  festive?: boolean;
}

export interface GroupDef {
  id: string;
  section: SectionId;
  title: () => string;
  when?: (c: SettingsCtx) => boolean;
}

/** Every group, in order. The id is the group's anchor (`/settings#<id>`) and its `data-group`. */
export const GROUPS: GroupDef[] = [
  { id: "language", section: "general", title: () => t("languageSettings.title") },
  { id: "look", section: "general", title: () => t("settings.groups.look") },
  { id: "deck", section: "general", title: () => t("deck.modeCard.title") },
  { id: "computer", section: "general", title: () => t("settings.groups.computer"), when: (c) => !!c.info?.desktop },
  { id: "profiles", section: "mods", title: () => t("profilesSettings.title") },
  { id: "crucible", section: "mods", title: () => t("crucible.profiles.cardTitle"), when: (c) => c.crucible },
  { id: "adding", section: "mods", title: () => t("settings.groups.adding") },
  { id: "library", section: "mods", title: () => t("librarySettings.title") },
  { id: "games", section: "game", title: () => t("settings.games.title") },
  { id: "kiln", section: "game", title: () => t("settings.groups.kiln") },
  { id: "shroudforge", section: "game", title: () => t("sfBackend.backend.title"), when: (c) => c.client },
  { id: "graphics", section: "graphics", title: () => t("settings.groups.graphics"), when: (c) => c.client },
  { id: "upscaler", section: "graphics", title: () => t("settings.graphics.upscaler.title"), when: (c) => c.client },
  { id: "upscalers", section: "graphics", title: () => t("settings.upscalers.title"), when: (c) => c.client },
  { id: "updates", section: "online", title: () => t("settings.groups.updates") },
  { id: "nexus", section: "online", title: () => t("nexusSettings.title"), when: (c) => c.nexus },
  { id: "community", section: "online", title: () => t("settings.groups.community") },
  { id: "sends", section: "online", title: () => t("settings.groups.sends"), when: (c) => c.crucible },
  { id: "drawing", section: "advanced", title: () => t("settings.groups.drawing"), when: (c) => !!c.info?.render },
  { id: "protection", section: "advanced", title: () => t("settings.groups.protection") },
  { id: "steam", section: "advanced", title: () => t("settings.groups.steam") },
  { id: "kilnjson", section: "advanced", title: () => t("settings.groups.kilnjson") },
  { id: "art", section: "advanced", title: () => t("settings.art.title") },
  { id: "developer", section: "advanced", title: () => t("settings.groups.developer"), when: (c) => c.tauri },
];

export const groupById = (id: string) => GROUPS.find((g) => g.id === id);
export const groupsOf = (s: SectionId, c: SettingsCtx) => GROUPS.filter((g) => g.section === s && (!g.when || g.when(c)));

/** Other words for an anchor (older names, the data folder's row). */
const ALIASES: Record<string, string> = {
  "data-folder": "library", data: "library", kept: "library", "kiln-json": "kilnjson", kilnJson: "kilnjson",
  appearance: "look", fire: "look", festive: "look", launcher: "adding", sandbox: "protection", render: "drawing", gameart: "art",
  upscaler: "upscaler", dlss: "graphics", discord: "community", privacy: "sends", dev: "developer",
};

/** Where an anchor (a URL hash without "#") points: a group (its section) or a section's top. */
export function resolveAnchor(anchor: string): { section: SectionId; group: string | null } | null {
  const a = decodeURIComponent(anchor).trim();
  if (!a) return null;
  const g = groupById(ALIASES[a] ?? a);
  if (g) return { section: g.section, group: g.id };
  if (isSection(a)) return { section: a, group: null };
  return null;
}

/** The section /settings opens: the anchor's, the route's, else the last one shown in this run, else General. */
export function sectionFor(param: string | undefined, hash: string, last: string | null): SectionId {
  const fromHash = hash.length > 1 ? resolveAnchor(hash.slice(1)) : null;
  // a group's anchor wins over the route (/settings/general#profiles opens Mods & profiles)
  if (fromHash?.group) return fromHash.section;
  if (isSection(param)) return param;
  if (fromHash) return fromHash.section;
  return isSection(last) ? last : "general";
}

// ---------------------------------------------------------------- search

/** One thing search finds: a row (its `data-setting`), a kiln.json entry ("kj:<path>") or a whole card's group. */
export interface SearchItem {
  id: string;
  section: SectionId;
  group: string;
  label: string;
  hint?: string;
  more?: string;
  /** Extra words that find it (FSR, XeSS, DLSS, upscaling, the kiln.json key). */
  words?: string[];
}

/** Lower case without accents: "Ü" finds "u", "é" finds "e". */
export const fold = (s: string) => s.normalize("NFD").replace(/\p{M}+/gu, "").toLocaleLowerCase();

/** The words of a query (empty: no search). */
export const queryWords = (q: string) => fold(q).split(/\s+/).filter(Boolean);

/** Where an item matches (null: it does not): every word of the query is in its name, help, More text or keywords. */
export function matchItem(item: SearchItem, words: string[]): { in: "label" | "hint" | "more" | "words" } | null {
  if (!words.length) return null;
  const label = fold(item.label), hint = fold(item.hint ?? ""), more = fold(item.more ?? ""), extra = fold((item.words ?? []).join(" "));
  const all = `${label}\n${hint}\n${more}\n${extra}`;
  if (!words.every((w) => all.includes(w))) return null;
  if (words.some((w) => label.includes(w))) return { in: "label" };
  if (words.some((w) => hint.includes(w))) return { in: "hint" };
  if (words.some((w) => more.includes(w))) return { in: "more" };
  return { in: "words" };
}

/** The items matching a query, in the page's order (sections, then groups, then rows). */
export function search(items: SearchItem[], q: string): SearchItem[] {
  const words = queryWords(q);
  if (!words.length) return [];
  const order = (i: SearchItem) => SECTIONS.indexOf(i.section) * 1000 + Math.max(0, GROUPS.findIndex((g) => g.id === i.group));
  return items.map((i, n) => ({ i, n })).filter(({ i }) => matchItem(i, words)).sort((a, b) => order(a.i) - order(b.i) || a.n - b.n).map(({ i }) => i);
}

/** The hit Enter opens: the first whose name matches, else whose help does, else the first. */
export function bestHit(hits: SearchItem[], q: string): SearchItem | null {
  const words = queryWords(q);
  const rank = (i: SearchItem) => ({ label: 0, hint: 1, words: 2, more: 3 })[matchItem(i, words)?.in ?? "more"];
  return hits.reduce<SearchItem | null>((best, i) => (!best || rank(i) < rank(best) ? i : best), null);
}

/** Hits per section, for the counts in the list. */
export function countBySection(hits: SearchItem[]): Partial<Record<SectionId, number>> {
  const out: Partial<Record<SectionId, number>> = {};
  for (const h of hits) out[h.section] = (out[h.section] ?? 0) + 1;
  return out;
}

/** A short quote of `text` around the first word of the query it has ("…the upscaler set to AMD FSR…"). */
export function snippet(text: string, words: string[], room = 42): string | null {
  const f = fold(text);
  let at = -1;
  for (const w of words) { const i = f.indexOf(w); if (i >= 0 && (at < 0 || i < at)) at = i; }
  if (at < 0) return null;
  const from = Math.max(0, at - room), to = Math.min(text.length, at + room * 1.6);
  return `${from > 0 ? "…" : ""}${text.slice(from, to).trim()}${to < text.length ? "…" : ""}`;
}

/** `text` split into the parts that match a query word and the rest, for <mark>. */
export function markParts(text: string, words: string[]): { text: string; hit: boolean }[] {
  if (!words.length || !text) return [{ text, hit: false }];
  const f = fold(text);
  // folding keeps the text's length for nearly every text (an accented letter folds to one letter); where it does not
  // (a letter that lower-cases to two), no marks
  if (f.length !== text.length) return [{ text, hit: false }];
  const hit = new Array<boolean>(text.length).fill(false);
  for (const w of words) for (let i = f.indexOf(w); i >= 0; i = f.indexOf(w, i + 1)) hit.fill(true, i, i + w.length);
  const out: { text: string; hit: boolean }[] = [];
  for (let i = 0; i < text.length; i++) {
    const last = out[out.length - 1];
    if (last && last.hit === hit[i]) last.text += text[i];
    else out.push({ text: text[i], hit: hit[i] });
  }
  return out;
}

/** The More text without what the line above it already says: a More that opens with the line drops it (the rest, its
 *  first letter upper case); a More that says nothing more is none. */
export function moreBeyond(hint: string | undefined, more: string | undefined): string | undefined {
  if (!more) return undefined;
  const line = (hint ?? "").trim().replace(/[.。!！?？]+$/u, "");
  const text = more.trim();
  if (!line) return text;
  const fl = fold(line), ft = fold(text);
  // folding keeps the length for nearly every text; where it does not, the More stays as it is
  if (fl.length !== line.length || ft.length !== text.length || !ft.startsWith(fl)) return text;
  const rest = text.slice(line.length).replace(/^[\s.,;:。、，；：!！?？]+/u, "");
  return rest ? rest.charAt(0).toLocaleUpperCase() + rest.slice(1) : undefined;
}

/** `key`'s text where the language shown has it, else `older`'s (a newer text that replaces an older one shows the older
 *  one, translated, until a translation has the newer one). */
export const textOr = (key: string, older: string) => t(hasOwnText(key) ? key : older);

/** A kiln.json key in words, as SchemaForm names a row without a label ("chatHistory": "Chat history"). */
export function keyWords(path: string): string {
  const last = path.split(".").pop() ?? path;
  const words = last.replace(/([a-z0-9])([A-Z])/g, "$1 $2").replace(/[_-]+/g, " ").trim();
  return words.charAt(0).toUpperCase() + words.slice(1);
}
