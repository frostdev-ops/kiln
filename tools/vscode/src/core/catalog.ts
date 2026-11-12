// SPDX-License-Identifier: MIT
// The game-data catalog `kiln catalog` generates from the user's installed game (`kiln catalog build`):
// <dir>/client.json (preferred) or <dir>/server.json; <dir> = kiln.catalog.path | $KILN_CATALOG | ~/.local/share/kiln/catalog.
// {"format":1, "kind":"kiln.catalog", "generated", "game":{side, steamBuild, svn, ...}, "icons": "<dir>"|null,
//  "languages":[...], "sections":{"items":[{name, id:"0x…", guid, displayName, description, kind, tier, maxStack, icon}], ...}}
// Game data: read locally, never copied into the repo or the .vsix.
import * as fs from "node:fs";
import * as os from "node:os";
import * as path from "node:path";
import { kilnDataDir } from "./dataDir";

export interface CatalogEntry {
  name: string;
  id?: string;
  guid?: string;
  displayName?: string;
  description?: string;
  icon?: string;
  [k: string]: unknown;
}

export interface CatalogFile {
  format: number;
  kind: string;
  generated?: string;
  game?: { side?: string; steamBuild?: string; svn?: string; dir?: string; [k: string]: unknown };
  icons?: string | null;
  languages?: unknown;
  sections: Record<string, CatalogEntry[]>;
}

export function defaultCatalogDir(env: NodeJS.ProcessEnv = process.env): string {
  return path.join(kilnDataDir(env), "catalog");  // $KILN_DATA_DIR, a moved data folder's pointer, or the default root
}

/** The catalog file to read: a file named directly, or <dir>/client.json, else <dir>/server.json. */
export function locateCatalog(setting: string | undefined): string | undefined {
  const cands = [setting, process.env.KILN_CATALOG, defaultCatalogDir()].filter((x): x is string => !!x);
  for (const c of cands) {
    const p = c.startsWith("~") ? path.join(os.homedir(), c.slice(1)) : c;
    try {
      const st = fs.statSync(p);
      if (st.isFile()) return p;
      if (st.isDirectory()) {
        for (const f of ["client.json", "server.json"]) {
          if (fs.existsSync(path.join(p, f))) return path.join(p, f);
        }
      }
    } catch {
      /* next */
    }
  }
  return undefined;
}

export class Catalog {
  readonly sections = new Map<string, CatalogEntry[]>();
  private byName = new Map<string, Map<string, CatalogEntry>>();
  private byNameLower = new Map<string, Map<string, CatalogEntry>>();
  private byGuid = new Map<string, { section: string; entry: CatalogEntry }>();
  private byId = new Map<string, Map<string, CatalogEntry>>();

  constructor(
    readonly file: string,
    readonly data: CatalogFile,
  ) {
    for (const [sec, list] of Object.entries(data.sections ?? {})) {
      if (!Array.isArray(list)) continue;
      // every entry gets a key: its name, else its id (loca, itemCategories), else its GUID (drops, resources)
      const entries = list
        .filter((e) => e && typeof e === "object")
        .map((e) => {
          const name = typeof e.name === "string" ? e.name : typeof e.id === "string" ? e.id : typeof e.guid === "string" ? e.guid : undefined;
          if (name === undefined) return undefined;
          const disp = e.displayName ?? (typeof e.text === "string" ? (e.text as string) : undefined);
          return name === e.name && disp === e.displayName ? e : { ...e, name, displayName: disp };
        })
        .filter((e): e is CatalogEntry => !!e);
      this.sections.set(sec, entries);
      const names = new Map<string, CatalogEntry>();
      const lower = new Map<string, CatalogEntry>();
      const ids = new Map<string, CatalogEntry>();
      for (const e of entries) {
        names.set(e.name, e);
        lower.set(e.name.toLowerCase(), e);
        if (e.guid) this.byGuid.set(e.guid.toLowerCase(), { section: sec, entry: e });
        if (e.id) ids.set(String(e.id).toLowerCase(), e);
      }
      this.byName.set(sec, names);
      this.byNameLower.set(sec, lower);
      this.byId.set(sec, ids);
    }
  }

  static load(file: string): Catalog {
    const data = JSON.parse(fs.readFileSync(file, "utf8")) as CatalogFile;
    if (data.kind !== "kiln.catalog") throw new Error(`${file}: kind is ${data.kind}, not kiln.catalog`);
    if (data.format !== 1) throw new Error(`${file}: catalog format ${data.format} (this extension reads 1)`);
    return new Catalog(file, data);
  }

  get iconsDir(): string | undefined {
    const d = this.data.icons;
    if (!d) return undefined;
    return path.isAbsolute(d) ? d : path.resolve(path.dirname(this.file), d);
  }

  iconPath(e: CatalogEntry): string | undefined {
    const dir = this.iconsDir;
    if (!dir || !e.icon) return undefined;
    const p = path.join(dir, e.icon);
    return fs.existsSync(p) ? p : undefined;
  }

  get label(): string {
    const g = this.data.game ?? {};
    return [g.side, g.steamBuild && `build ${g.steamBuild}`, g.svn && `svn ${g.svn}`].filter(Boolean).join(", ");
  }

  sectionNames(): string[] {
    return [...this.sections.keys()];
  }

  entries(section: string): CatalogEntry[] {
    return this.sections.get(section) ?? [];
  }

  /**
   * One value as a mod file writes it: a name (exact, then case-insensitive), a GUID, an id (0x…), or the
   * prefixed forms `item:<name>[.<path>]`, `template:<name>`, `like:<item>`.
   */
  resolve(value: string, sections?: string[]): { section: string; entry: CatalogEntry } | undefined {
    const v = value.trim();
    const pref = /^(item|template|like):(.+)$/.exec(v);
    if (pref) {
      const sec = pref[1] === "template" ? "templates" : "items";
      const name = pref[1] === "item" ? pref[2].split(".")[0] : pref[2];
      return this.resolve(name, [sec]);
    }
    const secs = sections?.length ? sections : this.sectionNames();
    if (/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(v)) {
      const hit = this.byGuid.get(v.toLowerCase());
      return hit && secs.includes(hit.section) ? hit : undefined;
    }
    for (const s of secs) {
      const e = this.byName.get(s)?.get(v);
      if (e) return { section: s, entry: e };
    }
    if (/^0x[0-9a-f]{1,8}$/i.test(v)) {
      const id = "0x" + v.slice(2).toLowerCase().padStart(8, "0");
      for (const s of secs) {
        const e = this.byId.get(s)?.get(id);
        if (e) return { section: s, entry: e };
      }
    }
    for (const s of secs) {
      const e = this.byNameLower.get(s)?.get(v.toLowerCase());
      if (e) return { section: s, entry: e };
    }
    return undefined;
  }

  /** Substring search over name and displayName (names first), at most `limit` hits. */
  search(q: string, sections?: string[], limit = 200): { section: string; entry: CatalogEntry }[] {
    const needle = q.toLowerCase();
    const out: { section: string; entry: CatalogEntry; score: number }[] = [];
    for (const s of sections?.length ? sections : this.sectionNames()) {
      for (const e of this.entries(s)) {
        const n = e.name.toLowerCase();
        const d = (e.displayName ?? "").toLowerCase();
        let score = -1;
        if (n === needle || d === needle) score = 0;
        else if (n.startsWith(needle) || d.startsWith(needle)) score = 1;
        else if (n.includes(needle) || d.includes(needle) || e.guid === needle || e.id === needle) score = 2;
        // a VFX has no name of its own: what else uses it finds it too
        else if ([e.names, e.usedBy].some((l) => Array.isArray(l) && (l as string[]).some((x) => x.toLowerCase().includes(needle)))) score = 2;
        if (score >= 0) out.push({ section: s, entry: e, score });
      }
    }
    out.sort((a, b) => a.score - b.score || a.entry.name.localeCompare(b.entry.name));
    return out.slice(0, limit);
  }
}

/** A short markdown description of an entry (hover, tree tooltip). */
export function describeEntry(section: string, e: CatalogEntry): string[] {
  const lines: string[] = [];
  const title = e.displayName ? `**${e.displayName}** \`${e.name}\`` : `\`${e.name}\``;
  lines.push(`${title}  _${section}_`);
  const facts: string[] = [];
  for (const k of ["kind", "rarity", "tier", "maxStack", "level", "family", "workshopName", "slot", "type", "scene", "maxLevel", "length", "particles"]) {
    if (e[k] !== undefined && e[k] !== null && e[k] !== "") facts.push(`${k} ${String(e[k])}`);
  }
  if (e.id) facts.push(`id \`${e.id}\``);
  if (e.guid) facts.push(`guid \`${e.guid}\``);
  if (facts.length) lines.push(facts.join(" · "));
  if (e.description) lines.push(String(e.description));
  return lines;
}

/**
 * The string literal around `col` in one line (JSON or C# regular strings): the content's [start, end) columns,
 * scanning quote pairs from the line start (an unclosed string runs to the end of the line).
 */
export function stringAt(line: string, col: number): { start: number; end: number } | undefined {
  let i = 0;
  while (i < line.length) {
    if (line[i] !== '"') {
      i++;
      continue;
    }
    const start = i + 1;
    let j = start;
    while (j < line.length && line[j] !== '"') j += line[j] === "\\" ? 2 : 1;
    const end = Math.min(j, line.length);
    if (col >= start && col <= end) return { start, end };
    i = end + 1;
  }
  return undefined;
}
